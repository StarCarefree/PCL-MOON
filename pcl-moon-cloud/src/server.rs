use std::io;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Duration;

use axum::{
    Router, middleware,
    routing::{get, put},
};
use hyper::server::conn::http1;
use hyper_util::rt::{TokioIo, TokioTimer};
use hyper_util::service::TowerToHyperService;
use tokio::net::TcpListener;
use tokio::sync::{Semaphore, watch};

use crate::assets::AdminAssets;
use crate::config::Config;
use crate::guard::{admin_guard, public_guard};
use crate::routes;
use crate::store::PackStore;

const PUBLIC_MAX_CONNECTIONS: usize = 2048;
const ADMIN_MAX_CONNECTIONS: usize = 128;

#[derive(Clone)]
pub struct AppState {
    pub store: Arc<PackStore>,
    pub assets: Arc<AdminAssets>,
    pub expected_authorization: Arc<[u8]>,
    pub public_port: u16,
    pub admin_port: u16,
}

#[derive(Clone, Copy, Debug)]
pub struct BoundPorts {
    pub public_port: u16,
    pub admin_port: u16,
}

pub struct ServerHandle {
    pub ports: BoundPorts,
    shutdown_tx: watch::Sender<bool>,
    public_task: tokio::task::JoinHandle<()>,
    admin_task: tokio::task::JoinHandle<()>,
    _store: Arc<PackStore>,
}

impl ServerHandle {
    pub async fn shutdown(self) {
        let _ = self.shutdown_tx.send(true);
        let _ = tokio::time::timeout(Duration::from_secs(20), async {
            let _ = self.public_task.await;
            let _ = self.admin_task.await;
        })
        .await;
    }

    pub async fn wait(self) {
        let _ = tokio::join!(self.public_task, self.admin_task);
    }
}

pub fn build_public_router(state: AppState) -> Router {
    Router::new()
        .route("/healthz", get(routes::healthz))
        .route("/index.json", get(routes::public_catalog))
        .route("/catalog.json", get(routes::public_catalog))
        .route("/api/v1/packs", get(routes::public_catalog))
        .route("/api/v1/packs/{id}", get(routes::public_detail))
        .route("/api/v1/packs/{id}/download", get(routes::public_download))
        .layer(middleware::from_fn(public_guard))
        .with_state(state)
}

pub fn build_admin_router(state: AppState) -> Router {
    Router::new()
        .route("/", get(routes::admin_asset))
        .route("/index.html", get(routes::admin_asset))
        .route("/app.js", get(routes::admin_asset))
        .route("/app.css", get(routes::admin_asset))
        .route("/healthz", get(routes::healthz))
        .route(
            "/api/admin/packs",
            get(routes::admin_catalog).post(routes::admin_create),
        )
        .route(
            "/api/admin/packs/{id}",
            put(routes::admin_update).delete(routes::admin_delete),
        )
        .route(
            "/api/admin/status",
            get(routes::admin_status)
                .post(routes::admin_method_not_allowed)
                .put(routes::admin_method_not_allowed)
                .delete(routes::admin_method_not_allowed),
        )
        .fallback(routes::admin_fallback)
        .method_not_allowed_fallback(routes::admin_method_not_allowed)
        .layer(middleware::from_fn_with_state(state.clone(), admin_guard))
        .with_state(state)
}

async fn bind_listener(
    bind: &str,
    port: u16,
    label: &str,
) -> io::Result<(TcpListener, SocketAddr)> {
    let listener = TcpListener::bind((bind, port))
        .await
        .map_err(|e| io::Error::new(e.kind(), format!("{label}绑定失败 ({bind}:{port}): {e}")))?;
    let addr = listener.local_addr()?;
    Ok((listener, addr))
}

pub async fn start_server(
    config: &Config,
    shutdown_rx: watch::Receiver<bool>,
) -> io::Result<(ServerHandle, watch::Sender<bool>)> {
    let store = Arc::new(PackStore::open(config)?);
    let assets = Arc::new(AdminAssets::load()?);

    let (public_listener, public_addr) =
        bind_listener(&config.public_bind, config.public_port, "公开端口").await?;
    let (admin_listener, admin_addr) =
        bind_listener(&config.admin_bind, config.admin_port, "管理端口").await?;

    if public_addr.port() == admin_addr.port() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "获取端口和管理端口不能相同",
        ));
    }

    let expected_authorization: Arc<[u8]> =
        format!("Bearer {}", config.admin_token).into_bytes().into();

    let state = AppState {
        store: store.clone(),
        assets,
        expected_authorization,
        public_port: public_addr.port(),
        admin_port: admin_addr.port(),
    };

    let (tx, rx) = watch::channel(false);
    // Forward external shutdown signal into internal channel if triggered.
    let tx_clone = tx.clone();
    let mut ext_rx = shutdown_rx;
    tokio::spawn(async move {
        if ext_rx.wait_for(|v| *v).await.is_ok() {
            let _ = tx_clone.send(true);
        }
    });

    let public_router = build_public_router(state.clone());
    let admin_router = build_admin_router(state);

    let public_task = tokio::spawn(serve_listener(
        public_listener,
        public_router,
        PUBLIC_MAX_CONNECTIONS,
        rx.clone(),
    ));
    let admin_task = tokio::spawn(serve_listener(
        admin_listener,
        admin_router,
        ADMIN_MAX_CONNECTIONS,
        rx,
    ));

    let handle = ServerHandle {
        ports: BoundPorts {
            public_port: public_addr.port(),
            admin_port: admin_addr.port(),
        },
        shutdown_tx: tx.clone(),
        public_task,
        admin_task,
        _store: store,
    };
    Ok((handle, tx))
}

async fn serve_listener(
    listener: TcpListener,
    router: Router,
    max_connections: usize,
    mut shutdown: watch::Receiver<bool>,
) {
    let semaphore = Arc::new(Semaphore::new(max_connections));
    let graceful = hyper_util::server::graceful::GracefulShutdown::new();

    loop {
        let accepted = tokio::select! {
            biased;
            res = shutdown.wait_for(|v| *v) => {
                if res.is_ok() {
                    break;
                }
                continue;
            }
            res = listener.accept() => res,
        };

        let (stream, _) = match accepted {
            Ok(pair) => pair,
            Err(_) => continue,
        };

        let Ok(permit) = semaphore.clone().try_acquire_owned() else {
            drop(stream);
            continue;
        };

        let _ = stream.set_nodelay(true);
        let io = TokioIo::new(stream);
        let svc = TowerToHyperService::new(router.clone());

        let conn = http1::Builder::new()
            .timer(TokioTimer::new())
            .header_read_timeout(Duration::from_secs(10))
            .max_buf_size(16 * 1024)
            .keep_alive(true)
            .serve_connection(io, svc);
        let fut = graceful.watch(conn);

        tokio::spawn(async move {
            let _permit = permit;
            let _ = fut.await;
        });
    }

    let _ = tokio::time::timeout(Duration::from_secs(20), graceful.shutdown()).await;
}
