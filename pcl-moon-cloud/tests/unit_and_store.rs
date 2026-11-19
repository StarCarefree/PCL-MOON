use std::fs;
use std::path::Path;
use std::sync::Arc;
use std::thread;

use axum::http::StatusCode;
use pcl_moon_cloud::blob::{Blob, accepts_gzip, matches_etag};
use pcl_moon_cloud::config::Config;
use pcl_moon_cloud::pack::Pack;
use pcl_moon_cloud::store::{MAX_FILE_BYTES, PackStore};
use tempfile::TempDir;

fn test_config(dir: &Path) -> Config {
    Config {
        public_port: 6664,
        public_bind: "0.0.0.0".to_string(),
        admin_port: 8081,
        admin_bind: "127.0.0.1".to_string(),
        admin_token: "test_token_012345678901234567890123456789".to_string(),
        data_dir: dir.to_path_buf(),
    }
}

fn sample_pack(id: &str) -> Pack {
    Pack {
        id: id.to_string(),
        name: "测试整合包".to_string(),
        description: "说明".to_string(),
        version: "1.0".to_string(),
        minecraft: "1.21.1".to_string(),
        icon: String::new(),
        cover_url: String::new(),
        background: String::new(),
        download_url: "https://cdn.example.com/a.zip".to_string(),
        backup_urls: Vec::new(),
        sha256: String::new(),
        file_name: "a.zip".to_string(),
        share_url: String::new(),
        share_path: String::new(),
        enabled: true,
        sort_order: 0,
    }
}

#[test]
fn blob_respects_encoding_quality_and_wildcard_overrides() {
    assert!(!accepts_gzip(None));
    assert!(accepts_gzip(Some("gzip")));
    assert!(accepts_gzip(Some("br, GZIP;q=0.5")));
    assert!(accepts_gzip(Some("*;q=1")));
    assert!(!accepts_gzip(Some("gzip;q=0, *;q=1")));
    assert!(!accepts_gzip(Some("gzip;q=NaN")));
    assert!(!accepts_gzip(Some("gzip;q=wrong")));
}

#[test]
fn blob_supports_weak_validators_and_lists() {
    assert!(matches_etag(Some(r#"W/"abc""#), r#""abc""#));
    assert!(matches_etag(Some(r#""other", "abc""#), r#""abc""#));
    assert!(matches_etag(Some("*"), r#""abc""#));
    assert!(!matches_etag(Some(r#""different""#), r#""abc""#));
}

#[test]
fn blob_generates_distinct_plain_and_gzip_etags() {
    let payload = "星遥云端测试数据 ".repeat(50).into_bytes();
    let blob = Blob::new(payload.clone(), "application/json").expect("blob");
    assert!(blob.gzip_bytes().len() < blob.plain_bytes().len());
    assert_ne!(blob.plain_etag(), blob.gzip_etag());
}

#[test]
fn persistence_restart_and_exclusive_directory_lock() {
    let dir = TempDir::new().expect("tempdir");
    let cfg = test_config(dir.path());
    {
        let first = PackStore::open(&cfg).expect("open first");
        first
            .create(sample_pack("persistent"), Some("\"0\""))
            .expect("create persistent");
        let second = PackStore::open(&cfg);
        assert!(second.is_err(), "second concurrent store must fail lock");
    }

    let restarted = PackStore::open(&cfg).expect("reopen after drop");
    let snap = restarted.current();
    assert_eq!(snap.admin.revision, 1);
    assert!(snap.published.contains_key("persistent"));
    assert_eq!(snap.admin.packs.len(), 1);
}

#[test]
fn simultaneous_writers_cannot_overwrite_each_other() {
    let dir = TempDir::new().expect("tempdir");
    let store = Arc::new(PackStore::open(&test_config(dir.path())).expect("open"));

    let s1 = store.clone();
    let t1 = thread::spawn(move || match s1.create(sample_pack("one"), Some("\"0\"")) {
        Ok(_) => StatusCode::OK.as_u16(),
        Err(e) => e.status.as_u16(),
    });

    let s2 = store.clone();
    let t2 = thread::spawn(move || match s2.create(sample_pack("two"), Some("\"0\"")) {
        Ok(_) => StatusCode::OK.as_u16(),
        Err(e) => e.status.as_u16(),
    });

    let mut codes = vec![t1.join().unwrap(), t2.join().unwrap()];
    codes.sort_unstable();
    assert_eq!(codes, vec![200, 412]);
    assert_eq!(store.current().admin.packs.len(), 1);
}

#[test]
fn failed_atomic_write_keeps_existing_snapshot() {
    let dir = TempDir::new().expect("tempdir");
    let store = PackStore::open(&test_config(dir.path())).expect("open");
    store
        .create(sample_pack("old"), Some("\"0\""))
        .expect("create old");

    let target = dir.path().join("catalog.json");
    fs::remove_file(&target).expect("remove catalog.json");
    fs::create_dir(&target).expect("create blocking dir");
    fs::write(target.join("blocker"), "x").expect("write blocker");

    let err = store
        .create(sample_pack("new"), Some("\"1\""))
        .expect_err("replace over non-empty dir must fail");
    assert_eq!(err.status, StatusCode::SERVICE_UNAVAILABLE);

    let snap = store.current();
    assert_eq!(snap.published.len(), 1);
    assert!(snap.published.contains_key("old"));
    assert_eq!(snap.admin.revision, 1);

    let tmp_count = fs::read_dir(dir.path())
        .unwrap()
        .filter_map(Result::ok)
        .filter(|e| e.path().extension().and_then(|s| s.to_str()) == Some("tmp"))
        .count();
    assert_eq!(tmp_count, 0);
}

#[test]
fn corrupted_file_does_not_reset_data_and_releases_lock() {
    let dir = TempDir::new().expect("tempdir");
    let catalog_path = dir.path().join("catalog.json");
    fs::write(&catalog_path, "{not-json").expect("write corrupt");

    let cfg = test_config(dir.path());
    assert!(PackStore::open(&cfg).is_err());
    assert_eq!(fs::read_to_string(&catalog_path).unwrap(), "{not-json");

    fs::remove_file(&catalog_path).expect("remove corrupt");
    let _recovered = PackStore::open(&cfg).expect("lock released and recovered");
}

#[test]
fn rejects_catalog_that_would_exceed_restart_read_limit() {
    let dir = TempDir::new().expect("tempdir");
    let cfg = test_config(dir.path());
    let long_url = format!("https://cdn.example.com/{}", "月".repeat(2000));
    let long_desc = "月".repeat(4000);
    let mut accepted = 0usize;

    {
        let store = PackStore::open(&cfg).expect("open");
        for index in 0..100 {
            let mut large = sample_pack(&format!("large-{index}"));
            large.name = "名称".to_string();
            large.description = long_desc.clone();
            large.icon = long_url.clone();
            large.cover_url = long_url.clone();
            large.background = long_url.clone();
            large.download_url = long_url.clone();
            large.backup_urls = vec![long_url.clone(); 8];
            large.file_name = "pack.zip".to_string();
            large.share_url = long_url.clone();
            large.enabled = false;

            match store.create(large, Some(&format!("\"{accepted}\""))) {
                Ok(_) => accepted += 1,
                Err(err) => {
                    assert_eq!(err.status, StatusCode::CONFLICT);
                    assert!(err.message.contains("8 MiB"));
                    break;
                }
            }
        }
        assert!((1..=99).contains(&accepted), "accepted={accepted}");
        let disk_size = fs::metadata(dir.path().join("catalog.json")).unwrap().len();
        assert!(disk_size <= MAX_FILE_BYTES as u64);
        assert_eq!(store.current().admin.revision, accepted as i64);
    }

    let restarted = PackStore::open(&cfg).expect("reopen after 8 MiB boundary");
    assert_eq!(restarted.current().admin.packs.len(), accepted);
}

#[test]
fn refuses_weak_startup_token_and_matching_ports() {
    let dir = TempDir::new().expect("tempdir");
    let mut cfg = test_config(dir.path());
    cfg.admin_token = "password".to_string();
    assert!(cfg.validate().is_err());

    let res = Config::from_sources(["pcl-moon-cloud"], |k, _| match k {
        "PCL_MOON_PUBLIC_PORT" => Some("7777".to_string()),
        "PCL_MOON_ADMIN_PORT" => Some("7777".to_string()),
        "PCL_MOON_ADMIN_TOKEN" => Some("test_token_012345678901234567890123456789".to_string()),
        _ => None,
    });
    assert!(res.is_err());

    let defaults = Config::from_sources(["pcl-moon-cloud"], |k, _| match k {
        "PCL_MOON_ADMIN_TOKEN" => Some("test_token_012345678901234567890123456789".to_string()),
        _ => None,
    })
    .expect("default config");
    assert_eq!(defaults.public_port, 6664);
    assert_eq!(defaults.admin_port, 8081);

    let overridden = Config::from_sources(["pcl-moon-cloud"], |k, _| match k {
        "PCL_MOON_PUBLIC_PORT" => Some("7777".to_string()),
        "PCL_MOON_ADMIN_TOKEN" => Some("test_token_012345678901234567890123456789".to_string()),
        _ => None,
    })
    .expect("overridden config");
    assert_eq!(overridden.public_port, 7777);
}

#[test]
fn validates_utf16_lengths_and_url_edge_cases() {
    // 40 emoji = 80 UTF-16 code units, exceeds minecraft max 40 even though 40 chars
    let mut p = sample_pack("emoji-len");
    p.minecraft = "👍".repeat(21); // 42 UTF-16 code units, only 21 chars
    assert!(p.validate().is_err());

    let mut p2 = sample_pack("emoji-ok");
    p2.minecraft = "👍".repeat(20); // 40 UTF-16 code units
    assert!(p2.validate().is_ok());

    let mut p3 = sample_pack("bad-empty-frag");
    p3.download_url = "https://example.com/a.zip#".to_string();
    assert!(p3.validate().is_err());

    let mut p4 = sample_pack("bad-empty-userinfo");
    p4.download_url = "https://@example.com/a.zip".to_string();
    assert!(p4.validate().is_err());

    let mut p5 = sample_pack("bad-port-zero");
    p5.download_url = "https://example.com:0/a.zip".to_string();
    assert!(p5.validate().is_err());
}
