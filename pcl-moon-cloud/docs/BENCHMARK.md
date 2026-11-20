# Java vs Pure Rust Performance & Footprint Benchmark (`pcl-moon-cloud`)

- **Date**: 2026-11-19
- **Environment**: Windows x86_64, Java (`C:/Program Files/Zulu/zulu-21/bin/java.exe -Xms64m -Xmx256m -jar build/libs/pcl-moon-cloud-1.0.0.jar`) vs Pure Rust (`pcl-moon-cloud/rust/app/target/release/pcl-moon-cloud.exe`, `rustc 1.99.0`, `lto = true`, `codegen-units = 1`, `strip = true`).
- **Script**: [`artifacts/test/pcl-moon-cloud/bench.py`](../../artifacts/test/pcl-moon-cloud/bench.py)
- **Workload & Method**: 5 cold-start runs each with isolated data directories seeded from `pcl-moon-cloud/rust/seed-data/catalog.json` (3 packs, 2 published, 1 draft) and ephemeral free TCP ports. Cold startup measures wall-clock time from `subprocess.Popen` until `GET /healthz` returns `HTTP 200 {"status":"ok"}` (5 ms polling interval). After hitting `GET /catalog.json` once, processes settle idle for 1.5 s before querying Windows `GetProcessMemoryInfo` (`WorkingSetSize` and `PrivateUsage`).

## Summary Comparison

| Metric | Java (Zulu 21 + Spring Boot 3.5) | Pure Rust (`pcl-moon-cloud.exe`) | Improvement (Java / Rust) |
| --- | ---: | ---: | ---: |
| Binary Size | 21.60 MiB (22,646,602 B, excl. JRE) | 3.17 MiB (3,319,296 B, standalone) | **6.82× smaller** |
| Cold Startup Median (`/healthz` 200) | 3232.79 ms | 96.50 ms | **33.50× faster** |
| Cold Startup Mean (`/healthz` 200) | 3236.78 ms | 97.13 ms | **33.32× faster** |
| Cold Startup Range | 3230.16 – 3252.42 ms | 90.18 – 106.70 ms | — |
| Idle Working Set Median | 227.63 MiB | 9.04 MiB | **25.18× lower** |
| Idle Private Bytes Median | 290.42 MiB | 3.09 MiB | **93.99× lower** |

## Raw Measurements (5 Runs)

### Java (`pcl-moon-cloud-1.0.0.jar`)
- **Startup (ms)**: `3232.79`, `3230.16`, `3237.10`, `3231.42`, `3252.42`
- **Working Set (MiB)**: `227.63`, `240.60`, `219.30`, `227.19`, `248.52`
- **Private Bytes (MiB)**: `290.42`, `302.47`, `281.39`, `288.84`, `311.26`

### Pure Rust (`pcl-moon-cloud.exe`)
- **Startup (ms)**: `106.70`, `90.18`, `91.52`, `96.50`, `100.76`
- **Working Set (MiB)**: `9.04`, `9.06`, `9.04`, `9.02`, `9.05`
- **Private Bytes (MiB)**: `3.10`, `3.08`, `3.09`, `3.12`, `3.04`
