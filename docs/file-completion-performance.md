# 补全文件本地检查优化

## 范围

正常启动的主 JAR 与支持库预检查复用持久化哈希缓存。文件大小或 UTC 修改时间变化时重新计算；比较的预期哈希、最小大小、JSON 等原有检查仍然执行。手动“补全文件”、安装器、下载后的校验继续完整读取文件，不使用启动缓存。与已有哈希缓存一样，这不是对保留文件大小和修改时间的恶意篡改的检测机制；手动修复可以强制检查这种情况。

首次数据库初始化及缓存批量保存放到后台。缓存尚未就绪时直接完整校验，把本次算出的结果按指纹排队保存，不额外读取文件。保存前再次检查指纹；缓存失效或不可用不阻止原有校验。资源对象目录最多 4 路并行读取元数据，小目录从 8 项起批量读取，超出枚举上限、链接、旧版资源和读取失败仍回退到逐文件检查。资源存在性不跨启动缓存。

启动缓存的 SQLite 锁等待配置为 1 秒（通用 `HashCache` 默认 30 秒，拒绝非正数，避免 0 表示无限等待）。连接打开失败会释放连接。启动校验遇到数据库异常或缓存初始化失败后，本次启动器进程的后续检查直接完整校验；文件共享等非数据库错误只回退当前文件。已进入缓存调用的并发检查仍可能各自完成一次短等待；该配置不是整个校验的硬性总时限。手动 `Check` 路径不变。

哈希计算任务按缓存实例、规范化完整路径和算法合并；同库的两个 batch 实例分别维护自己的待写结果。批量模式遇到指纹变化或文件删除时不逐文件执行 DELETE，由保存时的指纹核对和 UPSERT 版本比较清理旧算法值。保存失败也会丢弃当次队列快照，后续可重新计算；初始化失败和启动缓存熔断会清空外层待保存队列。

## 真实实例基准

使用 `SRE4.3`，78 个平台适用支持库，所有所需文件齐全。测试只读取实例，不启动游戏、不访问网络；缓存和测试文件放在 scratch。每阶段首次执行后取 7 次运行的中位数，最终使用独立进程验证持久化缓存。

| 阶段 | 完整读取 / 旧路径 | 优化后缓存命中 |
| --- | ---: | ---: |
| 资源扫描 | 69.53 ms（同版本完整检查基准） | 84.94 ms |
| 支持库检查 | 143.10 ms | 4.15 ms |
| 主 JAR 校验 | 126.08 ms | 0.23 ms |
| 三阶段中位数之和 | 338.71 ms | 89.32 ms |

本地检查指标约 3.8 倍；资源扫描单项在本机有抖动，不能声称该单项达到几倍。数据不是端到端启动时间，也不包括索引网络更新和实际缺失文件的下载。首次空缓存完整读取不能承诺同等倍数；首次支持库检查为 211.58 ms，缓存初始化和落盘不再同步阻塞启动。

## 验证

- 本次审计修复的 Release x64 单文件 EXE 构建成功，原生库与 PE 检查通过（仍有项目警告）。
- `HashCacheLookupTest`：18 项通过，新增可控异步 provider 验证双 batch 实例各自排队、路径规范化任务合并、独占锁短超时（查询与初始化）、写事务占锁时批量失效/删除文件不执行 DELETE、失败 flush 丢弃快照、拒绝非正超时。与启动策略及 JVM tuning 合计 93 项定向回归通过。
- 资源生产代码隔离测试：此前同步审计中 45 项通过；本轮未修改资源扫描代码，未重复执行。
- 本轮真实启动器 DLL 测试：预期哈希、最小大小、修改时间、文件长度、文件删除、强制完整修复全部通过；独占锁下首次回退约 1,158 ms，随后四次完整检查合计约 1 ms，同长度同时间戳内容变更也被重新计算并检出。
- 同轮 Agent 验证：Java 17/21 各 21 组通过，原生 FLS 成功释放计数断言及真实 GLFW 跨线程解绑/销毁检查通过。

日志位于 `artifacts/test/audit-fixes/`。未重跑完整 C# 测试集；早期过宽过滤还触发旧 `HashCacheTest` 的连接池文件占用清理失败，最终结果仅指上述定向测试。未重新测量端到端游戏性能，ARM64 仅交叉编译和架构检查，原生运行测试在 Windows x64 执行。

命令（先设置 `DOTNET_ROOT` 指向可用 SDK）：

```bash
PI_SCRATCH_DIR="$PWD/artifacts/test/audit-fixes/hash" dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Release --filter FullyQualifiedName~HashCacheLookupTest --artifacts-path "$PWD/artifacts/test/audit-fixes/hash/build" -p:OutputPath="$PWD/artifacts/test/audit-fixes/hash/bin/" -p:UseSharedCompilation=false --results-directory "$PWD/artifacts/test/audit-fixes/hash/results"
dotnet run --project scripts/launch-performance/Launch.Performance.csproj -c Release
dotnet run --project scripts/ui-smoke/VersionListEvents.csproj -- '<launcher.dll>' --file-completion --instance '<instance-directory>' --use-cache --cache-directory '<scratch-cache-directory>'
```

最后一条不加 `--use-cache` 可对比完整读取路径。对同一缓存目录以新进程再次执行，可以验证跨进程命中。

独立验证真实 DLL 的失效与锁争用回退（无需实例、网络或游戏）：

```bash
PI_SCRATCH_DIR="$PWD/artifacts/test/audit-fixes" dotnet run --project scripts/ui-smoke/VersionListEvents.csproj --artifacts-path artifacts/test/audit-fixes/ui-smoke -- '<launcher.dll>' --file-completion --cache-checks
```
