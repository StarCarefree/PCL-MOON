# Migration Map: pcl-moon-cloud -> Pure Rust

- **Goal**: Replace the Java/Spring Boot `pcl-moon-cloud` service with a standalone, behavior-equivalent pure Rust binary in `pcl-moon-cloud/rust/app`, keeping the original Java codebase untouched as reference.
- **Decisions**: See [DECISIONS.md](DECISIONS.md) (ADR-001 through ADR-010).
- **Glossary**: See [GLOSSARY.md](GLOSSARY.md).
- **Benchmark**: See [BENCHMARK.md](BENCHMARK.md).

## Inventory & Plan

| Wave | Module / Unit | Java Source (LOC) | Rust Target | Status | Verification |
| --- | --- | --- | --- | --- | --- |
| 0 | App Recording (`app record`) | `pcl-moon-cloud-1.0.0.jar` | `rust/app-cases/` (285 cases) + `artifacts/test/pcl-moon-cloud/admin-record/` (32 cases) | done | `app replay` 285/285 on Java |
| 1 | Front Door Scaffold | `PclMoonApplication` | `rust/app` (scaffolded via `app front-door`, then lifted to standalone pure Rust with fallback removed) | done | `Cargo.toml`, `src/main.rs` |
| 2 | Core Data & Encoding (`Blob`, `Pack`, `ApiError`) | `Blob.java` (82), `Pack.java` (61), `ApiException.java` (6), `ApiErrors.java` (31) | `app/src/blob.rs`, `pack.rs`, `error.rs` | done | `unit_and_store` (10/10 pass) |
| 2 | Config & Persistence (`CloudProperties`, `PackStore`) | `CloudProperties.java` (15), `PackStore.java` (156) | `app/src/config.rs`, `store.rs` | done | `unit_and_store` (10/10 pass) |
| 3 | HTTP Guard, Assets & Routes (`PortGuard`, `PortConfiguration`, `AdminAssets`, `CloudController`) | `PortGuard.java` (91), `PortConfiguration.java` (55), `AdminAssets.java` (39), `CloudController.java` (74) | `app/src/guard.rs`, `assets.rs`, `routes.rs`, `server.rs`, `main.rs` | done | `cloud_integration` (3/3 pass), `app replay` 273/285 exact + 12/12 gunzip match (285/285), `verify_parity.py` 285/285 + 32/32, `test-admin-ui-rust.mjs` 7/7 |
| 4 | Pure Binary, Static Checks & Benchmark | Full App | `rust/app/target/release/pcl-moon-cloud.exe` (3.17 MiB), `BENCHMARK.md` | done | `cargo test` 13/13, `clippy -D warnings` 0 warnings, `cargo fmt --check` pass, 33.5× faster startup, 25.2× lower Working Set |

## Out of Scope / Untouched
- Original Java sources (`pcl-moon-cloud/src/`, `build.gradle`, `README.md`, `deploy/`, `scripts/`) remain completely unmodified.
- Main launcher files (`PCL.Core/`, `Plain Craft Launcher 2/`, `scripts/`) remain completely untouched.
