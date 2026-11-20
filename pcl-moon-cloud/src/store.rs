use std::collections::{HashMap, HashSet};
use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, RwLock};

use axum::http::StatusCode;
use serde::{Deserialize, Serialize};

use crate::blob::Blob;
use crate::config::Config;
use crate::error::ApiError;
use crate::pack::Pack;

pub const MAX_PACKS: usize = 100;
pub const MAX_FILE_BYTES: usize = 8 * 1024 * 1024;

static TMP_SEQ: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AdminData {
    pub revision: i64,
    pub packs: Vec<Pack>,
}

#[derive(Debug)]
pub struct Snapshot {
    pub admin: AdminData,
    pub catalog: Blob,
    pub details: HashMap<String, Blob>,
    pub published: HashMap<String, Pack>,
}

#[derive(Serialize)]
struct CatalogResponse<'a> {
    packs: &'a [Pack],
}

pub struct PackStore {
    directory: PathBuf,
    file: PathBuf,
    lock_file: Option<File>,
    write_mutex: Mutex<()>,
    snapshot: RwLock<Arc<Snapshot>>,
}

struct TempFileGuard {
    path: PathBuf,
    active: bool,
}

impl TempFileGuard {
    fn new(path: PathBuf) -> Self {
        Self { path, active: true }
    }

    fn disarm(&mut self) {
        self.active = false;
    }
}

impl Drop for TempFileGuard {
    fn drop(&mut self) {
        if self.active {
            let _ = std::fs::remove_file(&self.path);
        }
    }
}

impl Drop for PackStore {
    fn drop(&mut self) {
        if let Some(lock_file) = self.lock_file.take() {
            let _ = lock_file.unlock();
        }
    }
}

impl PackStore {
    pub fn open(config: &Config) -> io::Result<Self> {
        let directory = std::path::absolute(&config.data_dir)?;
        std::fs::create_dir_all(&directory)?;
        let file = directory.join("catalog.json");
        let lock_path = directory.join("catalog.lock");

        let lock_file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&lock_path)?;

        if let Err(err) = lock_file.try_lock() {
            return Err(match err {
                std::fs::TryLockError::WouldBlock => io::Error::new(
                    io::ErrorKind::WouldBlock,
                    "数据目录已被另一服务占用；本服务仅支持单实例写入",
                ),
                std::fs::TryLockError::Error(inner) => inner,
            });
        }

        let initial = match Self::load_initial(&file) {
            Ok(data) => data,
            Err(err) => {
                let _ = lock_file.unlock();
                drop(lock_file);
                return Err(err);
            }
        };

        let snapshot = match build_snapshot(initial.revision, initial.packs) {
            Ok(s) => s,
            Err(err) => {
                let _ = lock_file.unlock();
                drop(lock_file);
                return Err(err);
            }
        };

        Ok(Self {
            directory,
            file,
            lock_file: Some(lock_file),
            write_mutex: Mutex::new(()),
            snapshot: RwLock::new(Arc::new(snapshot)),
        })
    }

    fn load_initial(file: &Path) -> io::Result<AdminData> {
        if !file.exists() {
            return Ok(AdminData {
                revision: 0,
                packs: Vec::new(),
            });
        }
        let meta = std::fs::metadata(file)?;
        if meta.len() > MAX_FILE_BYTES as u64 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "目录文件超过 8 MiB",
            ));
        }
        let raw = std::fs::read(file)?;
        let data: AdminData = serde_json::from_slice(&raw)
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
        if data.revision < 0 || data.packs.len() > MAX_PACKS {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "目录文件格式无效",
            ));
        }
        let mut ids = HashSet::with_capacity(data.packs.len());
        for pack in &data.packs {
            pack.validate().map_err(|e| {
                io::Error::new(
                    io::ErrorKind::InvalidData,
                    format!("目录校验失败: {}", e.message),
                )
            })?;
            if !ids.insert(pack.id.as_str()) {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "目录文件存在重复 ID",
                ));
            }
        }
        Ok(data)
    }

    pub fn current(&self) -> Arc<Snapshot> {
        // INFALLIBLE: RwLock is never poisoned because read/write guards never panic.
        self.snapshot.read().expect("snapshot RwLock").clone()
    }

    pub fn create(
        &self,
        pack: Pack,
        expected_revision: Option<&str>,
    ) -> Result<AdminData, ApiError> {
        let _guard = self.write_mutex.lock().expect("write mutex");
        let current = self.current();
        check_revision(expected_revision, current.admin.revision)?;
        pack.validate()?;
        if current.admin.packs.len() >= MAX_PACKS {
            return Err(ApiError::new(StatusCode::CONFLICT, "最多支持 100 个整合包"));
        }
        if current.admin.packs.iter().any(|p| p.id == pack.id) {
            return Err(ApiError::new(StatusCode::CONFLICT, "整合包 ID 已存在"));
        }
        let mut packs = current.admin.packs.clone();
        packs.push(pack);
        self.persist(&current, packs)
    }

    pub fn update(
        &self,
        id: &str,
        pack: Pack,
        expected_revision: Option<&str>,
    ) -> Result<AdminData, ApiError> {
        let _guard = self.write_mutex.lock().expect("write mutex");
        let current = self.current();
        check_revision(expected_revision, current.admin.revision)?;
        pack.validate()?;
        if id != pack.id {
            return Err(ApiError::new(
                StatusCode::BAD_REQUEST,
                "不允许修改整合包 ID",
            ));
        }
        let mut packs = current.admin.packs.clone();
        let index = index_of(&packs, id)?;
        packs[index] = pack;
        self.persist(&current, packs)
    }

    pub fn delete(&self, id: &str, expected_revision: Option<&str>) -> Result<AdminData, ApiError> {
        let _guard = self.write_mutex.lock().expect("write mutex");
        let current = self.current();
        check_revision(expected_revision, current.admin.revision)?;
        let mut packs = current.admin.packs.clone();
        let index = index_of(&packs, id)?;
        packs.remove(index);
        self.persist(&current, packs)
    }

    fn persist(&self, current: &Snapshot, packs: Vec<Pack>) -> Result<AdminData, ApiError> {
        let next_revision = current
            .admin
            .revision
            .checked_add(1)
            .ok_or_else(|| ApiError::new(StatusCode::CONFLICT, "目录版本号已达到上限"))?;

        let next = build_snapshot(next_revision, packs).map_err(|err| {
            eprintln!("目录快照构建失败: {err}");
            ApiError::new(
                StatusCode::SERVICE_UNAVAILABLE,
                "保存失败，旧目录保持不变；请检查磁盘权限与空间",
            )
        })?;

        let bytes = serde_json::to_vec_pretty(&next.admin).map_err(|err| {
            eprintln!("目录序列化失败: {err}");
            ApiError::new(
                StatusCode::SERVICE_UNAVAILABLE,
                "保存失败，旧目录保持不变；请检查磁盘权限与空间",
            )
        })?;

        if bytes.len() > MAX_FILE_BYTES {
            return Err(ApiError::new(
                StatusCode::CONFLICT,
                "目录总大小超过 8 MiB，请缩短说明或地址",
            ));
        }

        if let Err(err) = self.write_atomic(&bytes) {
            eprintln!("目录持久化失败: {err}");
            return Err(ApiError::new(
                StatusCode::SERVICE_UNAVAILABLE,
                "保存失败，旧目录保持不变；请检查磁盘权限与空间",
            ));
        }

        let admin = next.admin.clone();
        let next_arc = Arc::new(next);
        *self.snapshot.write().expect("snapshot RwLock") = next_arc;
        Ok(admin)
    }

    fn write_atomic(&self, bytes: &[u8]) -> io::Result<()> {
        let seq = TMP_SEQ.fetch_add(1, Ordering::Relaxed);
        let pid = std::process::id();
        let temp_path = self.directory.join(format!("catalog-{pid}-{seq}.tmp"));
        let mut guard = TempFileGuard::new(temp_path.clone());

        {
            let mut file = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&temp_path)?;
            file.write_all(bytes)?;
            file.sync_all()?;
        }

        // No non-atomic fallback: on unsupported filesystems a failed write leaves the old snapshot intact.
        std::fs::rename(&temp_path, &self.file)?;
        guard.disarm();
        Ok(())
    }
}

fn index_of(packs: &[Pack], id: &str) -> Result<usize, ApiError> {
    packs
        .iter()
        .position(|p| p.id == id)
        .ok_or_else(|| ApiError::new(StatusCode::NOT_FOUND, "整合包不存在"))
}

fn check_revision(expected: Option<&str>, current_revision: i64) -> Result<(), ApiError> {
    let Some(expected) = expected else {
        return Err(ApiError::new(
            StatusCode::PRECONDITION_REQUIRED,
            "写入必须提供 If-Match 版本，请先刷新目录",
        ));
    };
    if expected != format!("\"{current_revision}\"") {
        return Err(ApiError::new(
            StatusCode::PRECONDITION_FAILED,
            "目录已被修改，请刷新后重新确认编辑",
        ));
    }
    Ok(())
}

fn build_snapshot(revision: i64, mut packs: Vec<Pack>) -> io::Result<Snapshot> {
    packs.sort_by(|a, b| {
        a.sort_order
            .cmp(&b.sort_order)
            .then_with(|| a.id.cmp(&b.id))
    });

    let mut public_packs = Vec::new();
    let mut details = HashMap::new();
    let mut published = HashMap::new();

    for pack in &packs {
        if !pack.enabled {
            continue;
        }
        let view = pack.public_view();
        let detail_bytes =
            serde_json::to_vec(&view).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
        details.insert(
            pack.id.clone(),
            Blob::new(detail_bytes, "application/json")?,
        );
        published.insert(pack.id.clone(), pack.clone());
        public_packs.push(view);
    }

    let catalog_bytes = serde_json::to_vec(&CatalogResponse {
        packs: &public_packs,
    })
    .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
    let catalog = Blob::new(catalog_bytes, "application/json")?;

    Ok(Snapshot {
        admin: AdminData { revision, packs },
        catalog,
        details,
        published,
    })
}
