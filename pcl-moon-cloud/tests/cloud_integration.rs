use std::collections::HashMap;
use std::fs;
use std::io::Read;

use flate2::read::GzDecoder;
use pcl_moon_cloud::config::Config;
use pcl_moon_cloud::server::{BoundPorts, ServerHandle, start_server};
use reqwest::{Client, StatusCode, redirect::Policy};
use serde_json::{Value, json};
use tempfile::TempDir;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::sync::watch;

const TOKEN: &str = "integration_test_token_0123456789abcdef";
const BEARER: &str = "Bearer integration_test_token_0123456789abcdef";

struct TestServer {
    _dir: TempDir,
    data_dir: std::path::PathBuf,
    ports: BoundPorts,
    client: Client,
    handle: Option<ServerHandle>,
}

impl TestServer {
    async fn start() -> Self {
        let dir = TempDir::new().expect("tempdir");
        let data_dir = dir.path().to_path_buf();
        let cfg = Config {
            public_port: 0,
            public_bind: "127.0.0.1".to_string(),
            admin_port: 0,
            admin_bind: "127.0.0.1".to_string(),
            admin_token: TOKEN.to_string(),
            data_dir: data_dir.clone(),
        };
        let (_tx, rx) = watch::channel(false);
        let (handle, _) = start_server(&cfg, rx).await.expect("start_server");
        let ports = handle.ports;
        let client = Client::builder()
            .no_proxy()
            .redirect(Policy::none())
            .build()
            .expect("client");
        Self {
            _dir: dir,
            data_dir,
            ports,
            client,
            handle: Some(handle),
        }
    }

    async fn stop(mut self) {
        if let Some(handle) = self.handle.take() {
            handle.shutdown().await;
        }
    }
}

fn payload(id: &str, enabled: bool) -> String {
    json!({
        "id": id,
        "name": "月光整合包",
        "version": "1.0.0",
        "description": "网络测试简介".repeat(100),
        "minecraft": "1.21.1",
        "coverUrl": "https://cdn.example.com/cover.webp",
        "downloadUrl": "https://cdn.example.com/pack.mrpack",
        "backupUrls": ["https://backup.example.com/pack.mrpack"],
        "fileName": "月光.mrpack",
        "enabled": enabled,
        "sortOrder": 0
    })
    .to_string()
}

async fn send(
    client: &Client,
    port: u16,
    path: &str,
    method: reqwest::Method,
    body: Option<String>,
    headers: &[(&str, &str)],
) -> reqwest::Response {
    let mut req = client.request(method, format!("http://127.0.0.1:{port}{path}"));
    for (k, v) in headers {
        req = req.header(*k, *v);
    }
    if let Some(b) = body {
        req = req.body(b);
    }
    req.send().await.expect("http send")
}

#[tokio::test]
async fn port_isolation_and_authentication() {
    let srv = TestServer::start().await;
    let pub_port = srv.ports.public_port;
    let adm_port = srv.ports.admin_port;
    assert_ne!(pub_port, adm_port);

    let port_hdr = adm_port.to_string();
    for path in ["/", "/app.js", "/api/admin/packs", "/api/admin/status"] {
        let res = send(
            &srv.client,
            pub_port,
            path,
            reqwest::Method::GET,
            None,
            &[("Authorization", BEARER), ("X-Forwarded-Port", &port_hdr)],
        )
        .await;
        assert_eq!(res.status(), StatusCode::NOT_FOUND, "path={path}");
        assert_eq!(
            res.headers()
                .get("x-content-type-options")
                .and_then(|v| v.to_str().ok()),
            Some("nosniff")
        );
    }

    let post_pub = send(
        &srv.client,
        pub_port,
        "/catalog.json",
        reqwest::Method::POST,
        Some("{}".to_string()),
        &[],
    )
    .await;
    assert_eq!(post_pub.status(), StatusCode::METHOD_NOT_ALLOWED);
    assert_eq!(
        post_pub
            .headers()
            .get("allow")
            .and_then(|v| v.to_str().ok()),
        Some("GET, HEAD")
    );

    assert_eq!(
        send(
            &srv.client,
            adm_port,
            "/api/admin/packs",
            reqwest::Method::GET,
            None,
            &[]
        )
        .await
        .status(),
        StatusCode::UNAUTHORIZED
    );
    assert_eq!(
        send(
            &srv.client,
            adm_port,
            "/api/admin/packs",
            reqwest::Method::GET,
            None,
            &[("Authorization", "Bearer wrong")]
        )
        .await
        .status(),
        StatusCode::UNAUTHORIZED
    );
    assert_eq!(
        send(
            &srv.client,
            adm_port,
            "/api/admin/packs",
            reqwest::Method::GET,
            None,
            &[("Authorization", BEARER)]
        )
        .await
        .status(),
        StatusCode::OK
    );
    assert_eq!(
        send(
            &srv.client,
            adm_port,
            "/catalog.json",
            reqwest::Method::GET,
            None,
            &[]
        )
        .await
        .status(),
        StatusCode::NOT_FOUND
    );

    for path in ["/", "/app.js", "/app.css"] {
        let asset = send(
            &srv.client,
            adm_port,
            path,
            reqwest::Method::GET,
            None,
            &[("Accept-Encoding", "gzip")],
        )
        .await;
        assert_eq!(asset.status(), StatusCode::OK, "asset={path}");
        assert!(asset.headers().get("content-security-policy").is_some());
        assert_eq!(
            asset
                .headers()
                .get("content-encoding")
                .and_then(|v| v.to_str().ok()),
            Some("gzip")
        );
    }

    // Check /api/admin/status returns exact bound ports
    let status_res = send(
        &srv.client,
        adm_port,
        "/api/admin/status",
        reqwest::Method::GET,
        None,
        &[("Authorization", BEARER)],
    )
    .await;
    assert_eq!(status_res.status(), StatusCode::OK);
    let status_bytes = status_res.bytes().await.unwrap();
    let status_json: Value = serde_json::from_slice(&status_bytes).unwrap();
    assert_eq!(status_json["publicPort"], pub_port);
    assert_eq!(status_json["adminPort"], adm_port);

    srv.stop().await;
}

#[tokio::test]
async fn full_lifecycle_caching_and_download_mirrors() {
    let srv = TestServer::start().await;
    let pub_port = srv.ports.public_port;
    let adm_port = srv.ports.admin_port;

    let initial = send(
        &srv.client,
        adm_port,
        "/api/admin/packs",
        reqwest::Method::GET,
        None,
        &[("Authorization", BEARER)],
    )
    .await;
    let version = initial
        .headers()
        .get("etag")
        .unwrap()
        .to_str()
        .unwrap()
        .to_string();

    let created = send(
        &srv.client,
        adm_port,
        "/api/admin/packs",
        reqwest::Method::POST,
        Some(payload("moon-test", true)),
        &[
            ("Authorization", BEARER),
            ("If-Match", &version),
            ("Content-Type", "application/json"),
        ],
    )
    .await;
    assert_eq!(created.status(), StatusCode::OK);
    let next = created
        .headers()
        .get("etag")
        .unwrap()
        .to_str()
        .unwrap()
        .to_string();
    assert_ne!(next, version);

    // Stale revision -> 412
    assert_eq!(
        send(
            &srv.client,
            adm_port,
            "/api/admin/packs",
            reqwest::Method::POST,
            Some(payload("draft-test", false)),
            &[
                ("Authorization", BEARER),
                ("If-Match", &version),
                ("Content-Type", "application/json"),
            ],
        )
        .await
        .status(),
        StatusCode::PRECONDITION_FAILED
    );

    // Missing If-Match -> 428
    assert_eq!(
        send(
            &srv.client,
            adm_port,
            "/api/admin/packs",
            reqwest::Method::POST,
            Some(payload("draft-test", false)),
            &[
                ("Authorization", BEARER),
                ("Content-Type", "application/json"),
            ],
        )
        .await
        .status(),
        StatusCode::PRECONDITION_REQUIRED
    );

    // Create draft -> 200
    let draft = send(
        &srv.client,
        adm_port,
        "/api/admin/packs",
        reqwest::Method::POST,
        Some(payload("draft-test", false)),
        &[
            ("Authorization", BEARER),
            ("If-Match", &next),
            ("Content-Type", "application/json"),
        ],
    )
    .await;
    assert_eq!(draft.status(), StatusCode::OK);
    let next = draft
        .headers()
        .get("etag")
        .unwrap()
        .to_str()
        .unwrap()
        .to_string();

    let catalog_res = send(
        &srv.client,
        pub_port,
        "/catalog.json",
        reqwest::Method::GET,
        None,
        &[],
    )
    .await;
    let catalog_etag = catalog_res
        .headers()
        .get("etag")
        .unwrap()
        .to_str()
        .unwrap()
        .to_string();
    let catalog_bytes = catalog_res.bytes().await.unwrap();

    let index_res = send(
        &srv.client,
        pub_port,
        "/index.json",
        reqwest::Method::GET,
        None,
        &[],
    )
    .await;
    assert_eq!(index_res.status(), StatusCode::OK);
    assert_eq!(
        index_res.headers().get("etag").unwrap().to_str().unwrap(),
        catalog_etag
    );
    assert_eq!(index_res.bytes().await.unwrap(), catalog_bytes);

    assert_eq!(
        send(
            &srv.client,
            adm_port,
            "/index.json",
            reqwest::Method::GET,
            None,
            &[]
        )
        .await
        .status(),
        StatusCode::NOT_FOUND
    );

    let catalog_json: Value = serde_json::from_slice(&catalog_bytes).unwrap();
    assert_eq!(catalog_json["packs"].as_array().unwrap().len(), 1);
    assert_eq!(
        catalog_json["packs"][0]["icon"].as_str().unwrap(),
        "https://cdn.example.com/cover.webp"
    );

    // Gzip response check
    let compressed_res = send(
        &srv.client,
        pub_port,
        "/catalog.json",
        reqwest::Method::GET,
        None,
        &[("Accept-Encoding", "gzip")],
    )
    .await;
    assert_eq!(
        compressed_res
            .headers()
            .get("content-encoding")
            .and_then(|v| v.to_str().ok()),
        Some("gzip")
    );
    let comp_etag = compressed_res
        .headers()
        .get("etag")
        .unwrap()
        .to_str()
        .unwrap()
        .to_string();
    assert_ne!(comp_etag, catalog_etag);
    let comp_bytes = compressed_res.bytes().await.unwrap();
    assert!(comp_bytes.len() < catalog_bytes.len() / 2);
    let mut decoder = GzDecoder::new(&comp_bytes[..]);
    let mut decompressed = Vec::new();
    decoder.read_to_end(&mut decompressed).unwrap();
    assert_eq!(decompressed, catalog_bytes.as_ref());

    // Weak ETag 304
    let weak_tag = format!("W/{catalog_etag}");
    let unchanged = send(
        &srv.client,
        pub_port,
        "/catalog.json",
        reqwest::Method::GET,
        None,
        &[("If-None-Match", &weak_tag)],
    )
    .await;
    assert_eq!(unchanged.status(), StatusCode::NOT_MODIFIED);
    assert!(unchanged.bytes().await.unwrap().is_empty());

    // HEAD Content-Length check
    let head = send(
        &srv.client,
        pub_port,
        "/catalog.json",
        reqwest::Method::HEAD,
        None,
        &[],
    )
    .await;
    assert_eq!(head.status(), StatusCode::OK);
    let cl: usize = head
        .headers()
        .get("content-length")
        .unwrap()
        .to_str()
        .unwrap()
        .parse()
        .unwrap();
    assert_eq!(cl, catalog_bytes.len());
    assert!(head.bytes().await.unwrap().is_empty());

    // gzip;q=0, *;q=1 disables gzip
    let no_gzip = send(
        &srv.client,
        pub_port,
        "/catalog.json",
        reqwest::Method::GET,
        None,
        &[("Accept-Encoding", "gzip;q=0, *;q=1")],
    )
    .await;
    assert!(no_gzip.headers().get("content-encoding").is_none());

    // Draft hidden on public
    assert_eq!(
        send(
            &srv.client,
            pub_port,
            "/api/v1/packs/draft-test",
            reqwest::Method::GET,
            None,
            &[]
        )
        .await
        .status(),
        StatusCode::NOT_FOUND
    );

    // Mirrors 0, 1, and out-of-range
    let dl0 = send(
        &srv.client,
        pub_port,
        "/api/v1/packs/moon-test/download",
        reqwest::Method::GET,
        None,
        &[],
    )
    .await;
    assert_eq!(dl0.status(), StatusCode::FOUND);
    assert_eq!(
        dl0.headers().get("location").and_then(|v| v.to_str().ok()),
        Some("https://cdn.example.com/pack.mrpack")
    );

    let dl1 = send(
        &srv.client,
        pub_port,
        "/api/v1/packs/moon-test/download?mirror=1",
        reqwest::Method::GET,
        None,
        &[],
    )
    .await;
    assert_eq!(dl1.status(), StatusCode::FOUND);
    assert_eq!(
        dl1.headers().get("location").and_then(|v| v.to_str().ok()),
        Some("https://backup.example.com/pack.mrpack")
    );

    assert_eq!(
        send(
            &srv.client,
            pub_port,
            "/api/v1/packs/moon-test/download?mirror=2",
            reqwest::Method::GET,
            None,
            &[]
        )
        .await
        .status(),
        StatusCode::BAD_REQUEST
    );

    // Update to draft -> disappears from public
    let updated = send(
        &srv.client,
        adm_port,
        "/api/admin/packs/moon-test",
        reqwest::Method::PUT,
        Some(payload("moon-test", false)),
        &[
            ("Authorization", BEARER),
            ("If-Match", &next),
            ("Content-Type", "application/json"),
        ],
    )
    .await;
    assert_eq!(updated.status(), StatusCode::OK);
    let updated_tag = updated
        .headers()
        .get("etag")
        .unwrap()
        .to_str()
        .unwrap()
        .to_string();

    let empty_cat_bytes = send(
        &srv.client,
        pub_port,
        "/catalog.json",
        reqwest::Method::GET,
        None,
        &[],
    )
    .await
    .bytes()
    .await
    .unwrap();
    let empty_cat: Value = serde_json::from_slice(&empty_cat_bytes).unwrap();
    assert!(empty_cat["packs"].as_array().unwrap().is_empty());
    assert_eq!(
        send(
            &srv.client,
            pub_port,
            "/api/v1/packs/moon-test/download",
            reqwest::Method::GET,
            None,
            &[]
        )
        .await
        .status(),
        StatusCode::NOT_FOUND
    );

    // Delete packs -> persisted to disk
    let deleted = send(
        &srv.client,
        adm_port,
        "/api/admin/packs/moon-test",
        reqwest::Method::DELETE,
        None,
        &[("Authorization", BEARER), ("If-Match", &updated_tag)],
    )
    .await;
    assert_eq!(deleted.status(), StatusCode::OK);
    let del_tag = deleted
        .headers()
        .get("etag")
        .unwrap()
        .to_str()
        .unwrap()
        .to_string();
    let on_disk = fs::read_to_string(srv.data_dir.join("catalog.json")).unwrap();
    assert!(!on_disk.contains("moon-test"));

    assert_eq!(
        send(
            &srv.client,
            adm_port,
            "/api/admin/packs/draft-test",
            reqwest::Method::DELETE,
            None,
            &[("Authorization", BEARER), ("If-Match", &del_tag)],
        )
        .await
        .status(),
        StatusCode::OK
    );

    srv.stop().await;
}

#[tokio::test]
async fn rejects_unsafe_and_oversized_input_including_chunked() {
    let srv = TestServer::start().await;
    let adm_port = srv.ports.admin_port;

    let rev = send(
        &srv.client,
        adm_port,
        "/api/admin/packs",
        reqwest::Method::GET,
        None,
        &[("Authorization", BEARER)],
    )
    .await
    .headers()
    .get("etag")
    .unwrap()
    .to_str()
    .unwrap()
    .to_string();

    let valid = payload("bad-test", true);
    let invalids = [
        valid.replace("https://cdn.example.com/pack.mrpack", "file:///etc/passwd"),
        valid.replace("月光.mrpack", "../pack.zip"),
        valid.replace(
            "https://cdn.example.com/pack.mrpack",
            "https://user:pass@example.com/a",
        ),
        valid.replace("bad-test", "../bad"),
        valid.replace("1.0.0", ""),
        valid.replace("\"enabled\":true", "\"enabled\":true,\"command\":\"bad\""),
    ];

    for bad in invalids {
        let res = send(
            &srv.client,
            adm_port,
            "/api/admin/packs",
            reqwest::Method::POST,
            Some(bad),
            &[
                ("Authorization", BEARER),
                ("If-Match", &rev),
                ("Content-Type", "application/json"),
            ],
        )
        .await;
        assert_eq!(res.status(), StatusCode::BAD_REQUEST);
    }

    // Content-Length > 128 KiB -> 413
    let oversized = send(
        &srv.client,
        adm_port,
        "/api/admin/packs",
        reqwest::Method::POST,
        Some("x".repeat(131_073)),
        &[
            ("Authorization", BEARER),
            ("If-Match", &rev),
            ("Content-Type", "application/json"),
        ],
    )
    .await;
    assert_eq!(oversized.status(), StatusCode::PAYLOAD_TOO_LARGE);

    // Raw HTTP/1.1 Transfer-Encoding: chunked exceeding 128 KiB without Content-Length -> 413
    let mut stream = TcpStream::connect(("127.0.0.1", adm_port))
        .await
        .expect("connect");
    let req_head = format!(
        "POST /api/admin/packs HTTP/1.1\r\nHost: 127.0.0.1:{adm_port}\r\nAuthorization: {BEARER}\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
    );
    stream.write_all(req_head.as_bytes()).await.unwrap();

    // Send 131073 bytes in two chunks
    let chunk1 = vec![b'a'; 65_536];
    stream
        .write_all(format!("{:x}\r\n", chunk1.len()).as_bytes())
        .await
        .unwrap();
    stream.write_all(&chunk1).await.unwrap();
    stream.write_all(b"\r\n").await.unwrap();

    let chunk2 = vec![b'b'; 65_537];
    stream
        .write_all(format!("{:x}\r\n", chunk2.len()).as_bytes())
        .await
        .unwrap();
    let _ = stream.write_all(&chunk2).await;
    let _ = stream.write_all(b"\r\n0\r\n\r\n").await;

    let mut resp = Vec::new();
    let _ = stream.read_to_end(&mut resp).await;
    let resp_str = String::from_utf8_lossy(&resp);
    assert!(
        resp_str.starts_with("HTTP/1.1 413 "),
        "unexpected chunked response: {resp_str}"
    );
    let _ = HashMap::<String, String>::new();

    srv.stop().await;
}
