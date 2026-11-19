use std::process::ExitCode;

use pcl_moon_cloud::config::Config;
use pcl_moon_cloud::server::start_server;
use tokio::sync::watch;

async fn shutdown_signal() {
    #[cfg(unix)]
    {
        use tokio::signal::unix::{SignalKind, signal};
        if let Ok(mut term) = signal(SignalKind::terminate()) {
            tokio::select! {
                _ = tokio::signal::ctrl_c() => {}
                _ = term.recv() => {}
            }
            return;
        }
    }
    let _ = tokio::signal::ctrl_c().await;
}

#[tokio::main]
async fn main() -> ExitCode {
    let config = match Config::from_env_and_args() {
        Ok(cfg) => cfg,
        Err(err) => {
            eprintln!("配置错误: {err}");
            return ExitCode::FAILURE;
        }
    };

    let (shutdown_tx, shutdown_rx) = watch::channel(false);
    let (handle, _) = match start_server(&config, shutdown_rx).await {
        Ok(pair) => pair,
        Err(err) => {
            eprintln!("服务启动失败: {err}");
            return ExitCode::FAILURE;
        }
    };

    eprintln!(
        "PCL-MOON Cloud (Rust) started: public={}:{}, admin={}:{}",
        config.public_bind, handle.ports.public_port, config.admin_bind, handle.ports.admin_port
    );

    tokio::spawn(async move {
        shutdown_signal().await;
        let _ = shutdown_tx.send(true);
    });

    handle.wait().await;
    ExitCode::SUCCESS
}
