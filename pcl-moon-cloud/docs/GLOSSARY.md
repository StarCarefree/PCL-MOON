# Glossary: pcl-moon-cloud

| Original Java Term / Symbol | Meaning | Rust Symbol / Module |
| --- | --- | --- |
| `PclMoonApplication` | Process entry point, config loading, dual-server lifecycle & graceful shutdown | `app/src/main.rs` |
| `CloudProperties` | Env/CLI configuration (`public_bind`, `public_port`, `admin_bind`, `admin_port`, `admin_token`, `data_dir`) & startup validation | `app/src/config.rs` |
| `PortConfiguration` / `Ports` | Dual bound socket listeners (`public`, `admin`) and bound port reporting | `app/src/server.rs` (`BoundPorts`) |
| `PortGuard` | Socket-based route isolation, security headers, Bearer auth, 128 KiB streaming body guard | `app/src/guard.rs` |
| `ApiException` / `ApiErrors` | Domain HTTP error (`status`, Chinese `message`) serialized as `{"error": "..."}` with `Cache-Control: no-store` | `app/src/error.rs` (`ApiError`) |
| `Pack` | Pack catalog record with UTF-16 `@Size`, regex, HTTP(S) URI, and `.zip`/`.mrpack` filename validation | `app/src/pack.rs` (`Pack`) |
| `PackStore` / `AdminData` / `Snapshot` | Exclusive `catalog.lock`, atomic `catalog.json` persistence (8 MiB limit, 100 pack limit), `If-Match` revision lock, immutable precomputed snapshot | `app/src/store.rs` (`PackStore`, `AdminData`, `Snapshot`) |
| `Blob` | Precomputed immutable payload (`plain`, `gzip`, `plain_tag`, `gzip_tag`, `content_type`), `Accept-Encoding` q-value & `If-None-Match` conditional responder | `app/src/blob.rs` (`Blob`) |
| `CloudController` | Public catalog/detail/download/healthz routes and Admin CRUD/status/healthz routes | `app/src/routes.rs` |
| `AdminAssets` | Embedded `admin/{index.html,app.js,app.css}` served as cached `Blob`s on the admin socket | `app/src/assets.rs` |
