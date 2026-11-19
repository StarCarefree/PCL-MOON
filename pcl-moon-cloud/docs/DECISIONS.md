# Architecture Decisions: pcl-moon-cloud -> Rust

## ADR-001: Target Shape and Runtime Stack
- Status: accepted (2026-11-19)
- Context: `pcl-moon-cloud` is a standalone dual-port HTTP catalog server (`PclMoonApplication`, 11 Java files, 561 LOC) with embedded static admin web assets (`src/main/resources/admin/*`).
- Decision: Port as a standalone pure Rust async HTTP server in `pcl-moon-cloud/rust/app` using `tokio`, `axum` (0.8) / `hyper` (HTTP/1.1), `serde` + `serde_json`, `flate2` (gzip), `sha2` (SHA-256 ETag), `url` + `regex` (URL/fileName/id/token validation), `subtle` (constant-time Bearer token compare), and `std::fs::File::try_lock` (available in Rust 1.99) for exclusive directory locking.
- Consequences: Zero JVM dependency at runtime, sub-second cold start, minimal idle working set, single native executable.

## ADR-002: Dual-Port Socket Isolation and Listener Limits
- Status: accepted (2026-11-19)
- Context: Original `PortConfiguration` + `PortGuard` isolates public catalog traffic (`PCL_MOON_PUBLIC_BIND`:`PCL_MOON_PUBLIC_PORT`, default `0.0.0.0:6664`) from admin UI/API traffic (`PCL_MOON_ADMIN_BIND`:`PCL_MOON_ADMIN_PORT`, default `127.0.0.1:8081`) strictly by accepted socket port, never trusting `Host` or `X-Forwarded-Port`.
- Decision: Bind two distinct `tokio::net::TcpListener` sockets and serve two completely separate `axum::Router` instances (one public router, one admin router). Enforce `public_port > 0 && public_port == admin_port` startup rejection; allow `0` for OS-assigned ephemeral ports and report the actual bound `local_addr().port()` in `/api/admin/status`. Apply connection concurrency limits (2048 public / 128 admin), 8 KiB header buffer limit, and 10s/15s request/keep-alive timeouts.
- Consequences: Cross-port access to admin routes from the public socket or public catalog routes from the admin socket is structurally impossible and yields identical `404` JSON errors.

## ADR-003: Configuration, Environment Compatibility, and Startup Guards
- Status: accepted (2026-11-19)
- Context: `application.yml` maps `PCL_MOON_*` with `MOONCL_*` legacy fallback. `CloudProperties` rejects missing/invalid `PCL_MOON_ADMIN_TOKEN` (must match `^[A-Za-z0-9_-]{32,256}$`) and out-of-range ports (`0..=65535`).
- Decision: Read `PCL_MOON_PUBLIC_PORT` -> `MOONCL_PUBLIC_PORT` -> `PORT` (only when `PCL_MOON_*` and `MOONCL_*` are unset, to integrate cleanly with `rust-anything app record/replay` and PaaS runners) -> `6664`; likewise `PCL_MOON_PUBLIC_BIND`/`MOONCL_PUBLIC_BIND` -> `0.0.0.0`, `PCL_MOON_ADMIN_PORT`/`MOONCL_ADMIN_PORT` -> `8081`, `PCL_MOON_ADMIN_BIND`/`MOONCL_ADMIN_BIND` -> `127.0.0.1`, `PCL_MOON_ADMIN_TOKEN`/`MOONCL_ADMIN_TOKEN`, `PCL_MOON_DATA_DIR`/`MOONCL_DATA_DIR` -> `./data`. Also support optional `--server.port=<N>` / `--port <N>` CLI override if supplied.
- Consequences: Full env compatibility with existing `deploy/pcl-moon.env.example` and tests, while working seamlessly under `rust-anything` port injection.

## ADR-004: HTTP Equivalence Bar, Headers, Compression, and ETags
- Status: accepted (2026-11-19)
- Context: Public clients and the admin UI depend on exact status codes (`200`, `302`, `304`, `400`, `401`, `404`, `405`, `409`, `412`, `413`, `428`, `503`), `{"error": "..."}` JSON payloads, security headers (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Content-Security-Policy`), precomputed immutable `Blob` (compact JSON + Gzip + SHA-256 quoted strong ETag), `Vary: Accept-Encoding`, `Accept-Encoding` q-value parsing (`gzip;q=0`, `*;q=1`, `NaN`), `If-None-Match` weak/list matching (`W/"..."`, `*`), `HEAD` with `Content-Length`, and `Cache-Control` policies.
- Decision: Replicate `Blob`, `PortGuard`, `CloudController`, and `ApiErrors` byte-for-byte in semantics and Chinese error messages. Note one documented normalization in `app record`: `/api/admin/status` uses `Map.of(...)` in Java which randomizes JSON key order across JVM runs, so `--normalize key-order` is used when comparing JSON responses in `app-cases`.
- Consequences: Drop-in protocol equivalence for both `Plain Craft Launcher 2` and `admin/` web UI.

## ADR-005: Validation Semantics (UTF-16 Lengths, Unknown Fields, URLs, FileName)
- Status: accepted (2026-11-19)
- Context: `Pack` uses Jakarta Bean Validation (`@NotBlank`, `@Size`, `@Pattern`, `@Min`, `@Max`), Jackson `fail-on-unknown-properties: true`, and `Pack.validateLinks()` (`java.net.URI` + regex).
- Decision: Validate `@Size` lengths using UTF-16 code unit counts (`s.encode_utf16().count()`) to match Java `String.length()`, enforce `#[serde(deny_unknown_fields)]` on `Pack` and `AdminData`, treat `null` on optional string/list/bool/int fields consistently with `Pack`'s compact constructor, and verify HTTP(S) URLs reject userinfo, fragments (`#`), missing host, and port `0` or `> 65535`.
- Consequences: Rejects the exact same malicious/oversized/malformed inputs with `400` and preserves valid Unicode/emoji behavior.

## ADR-006: Persistence, Exclusive File Lock, 8 MiB Limit, and Atomic Replace
- Status: accepted (2026-11-19)
- Context: `PackStore` holds an exclusive lock on `<dataDir>/catalog.lock`, validates `<dataDir>/catalog.json` (max 8 MiB, max 100 packs, non-negative revision, unique valid IDs) on startup, rejects startup if corrupted without modifying the file, and persists writes via pretty-printed JSON -> 8 MiB limit check (`409`) -> temp file in same directory -> `sync_all()` -> atomic rename over `catalog.json` (returning `503` without mutating in-memory snapshot if atomic move fails).
- Decision: Mirror the exact lock, startup validation, `catalog-<id>.tmp` write + `sync_all()` + `std::fs::rename` replacement, and cleanup guard. Never fall back to non-atomic copy/truncate.
- Consequences: Single-writer safety and crash/failure isolation match the Java service.

## ADR-007: Admin Request Auth, 128 KiB Streaming Body Limit, and Optimistic Locking
- Status: accepted (2026-11-19)
- Context: Admin `/api/admin/*` routes require constant-time `Authorization: Bearer <token>` matching (`401` + `WWW-Authenticate: Bearer`), reject bodies (both `Content-Length` and chunked/unknown-length) exceeding `131072` bytes with `413` before JSON parsing/If-Match checking, and enforce `If-Match: "<revision>"` (`428` missing, `412` stale).
- Decision: Implement auth/method/body-limit middleware before body extraction in the admin router, reading at most `131073` bytes incrementally from the body stream so oversized chunked payloads abort early with `413`.
- Consequences: Protects memory on admin writes and reproduces exact precedence (`404 -> 401 -> 405 -> 413 -> 400 JSON -> 428/412 -> 400 validation -> 409 -> 503 -> 200`).

## ADR-008: Embedded Admin Web Assets
- Status: accepted (2026-11-19)
- Context: `AdminAssets` serves `/`, `/index.html`, `/app.js`, `/app.css` from `src/main/resources/admin/*` without external dependencies.
- Decision: Embed `../../src/main/resources/admin/{index.html,app.js,app.css}` directly into the Rust binary via `include_bytes!` and wrap them in precomputed immutable `Blob`s (`private, no-cache`, Gzip + SHA-256 ETag).
- Consequences: Single self-contained Rust binary with zero runtime asset file lookups (`app lift` asset-embedding check satisfied).

## ADR-009: Recording Strategy for Dual-Port HTTP Server (`app record` + Integration Tests)
- Status: accepted (2026-11-19)
- Context: `rust-anything app record --http` allocates a single `{port}` / `PORT` per recording session and sends HTTP cases to that port. `pcl-moon-cloud` exposes two ports (`public` and `admin`).
- Decision: Record the primary public port HTTP suite via `rust-anything app record --http` (`pcl-moon-cloud/rust/app-cases/http/`, covering `/healthz`, `/index.json`, `/catalog.json`, `/api/v1/packs`, `/api/v1/packs/{id}`, `/api/v1/packs/{id}/download` with `mirror=0/1/invalid`, Gzip `Accept-Encoding` q-values/wildcards, `If-None-Match` strong/weak/list/wildcard `304`, `HEAD` `Content-Length`, port isolation `404` on admin paths, and `405` on `POST`/`PUT`/`DELETE`). Additionally record a secondary admin-port validation suite in `artifacts/test/pcl-moon-cloud/` (or test both original Java jar and Rust binary against identical admin HTTP scenarios) AND port 100% of `CloudIntegrationTest`, `PackStoreTest`, and `BlobTest` (plus `RuntimeCompatibilityTest` env/port checks) as Rust integration tests in `pcl-moon-cloud/rust/app/tests/`.
- Consequences: 100% `rust-anything app replay` verification on the public contract plus 100% automated regression verification of admin API, auth, optimistic locking, chunked 413, file locking, atomic write failure, 8 MiB limit, and dual-port socket isolation.

## ADR-010: Observed Equivalence Verification Notes (Gzip Framing & Cache-Control Header)
- Status: accepted (2026-11-19)
- Context: During `rust-anything app replay` against unmodified Java recordings:
  1. `rust-anything app replay` compares `binary = true` bodies byte-for-byte without gunzipping. OpenJDK's zlib and Rust `flate2` (`miniz_oxide`, with `GzBuilder::operating_system(255)` matching Java's `1f 8b 08 00 00 00 00 00 00 ff` header) both emit 445-byte valid Gzip payloads that decompress to identical 2094-byte JSON, differing only in deflate Huffman framing bytes (`273/285` exact byte match on raw `app replay`; `285/285` when gunzipped via `verify_parity.py`).
  2. In Java, `PortGuard` sets `Cache-Control: no-store` and `CloudController` / `ApiErrors` appends `Cache-Control: no-store` on `/api/admin/*`, resulting in a duplicate `no-store, no-store` header on Spring MVC responses; the Rust port emits a clean single `Cache-Control: no-store` header (`32/32` semantic admin parity via `verify_parity.py`).
- Decision: Keep `rust/app-cases/` unmodified as ground truth from Java, keep pure-Rust `flate2` (`miniz_oxide`) without C-sys dependencies, emit single `Cache-Control: no-store`, and enforce full gunzipped + header parity in `verify_parity.py` and `cargo test`.
- Consequences: Zero-C-dependency standalone build, full protocol compatibility verified by `cargo test` (13/13), `verify_parity.py` (285/285 public + 32/32 admin), and headless browser UI smoke test (`test-admin-ui-rust.mjs`, 7/7).

