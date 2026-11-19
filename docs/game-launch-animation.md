# PCL-MOON 独立游戏启动窗口

点击“启动游戏”后打开独立、非模态的原生 WPF 窗口，不再占用主页内容区。不设置 Owner、不置顶，主启动器最小化或隐藏不会连带收起它。每次启动单独管理进度和取消，支持并发启动不同实例。

## 开关

设置 → 个性化 → 基础选项 → **启用游戏启动动画（PCL-MOON）**。

默认开启，沿用本地配置键 `UiShowLaunchAnimation`，下次启动游戏生效。关闭后没有独立窗口，仍保留原启动面板；导出启动脚本也不打开窗口。

## 两阶段真实进度

1. **启动器准备**：由该启动会话的真实加载器提供 Java、账号、依赖、文件补全和启动参数等进度。
2. **游戏加载**：启动器直接在进程内扫描该实例 `mods/*.jar` 中的 Fabric/Quilt 入口点清单（`fabric.mod.json` / `quilt.mod.json`），按标准阶段顺序推送当前阶段的 `current/total` 与模组名称；不再注入跨进程 Java Agent 或使用临时进度文件轮询，也不连接、启动或依赖 `mod-loading-screen`。未知总数显示 `--`，不把两个阶段拼成估算总百分比。

Forge/NeoForge/原版没有这套入口点计数，按既有游戏窗口状态交接，不虚报百分比。

收到最终入口点完成和既有游戏窗口就绪信号后，再执行启动器原有的隐藏、最小化或退出设置。此处不测量后续进入存档的加载。


## 交互与动画

- 固定双阶段布局、暖金进度条、月轨缓动与月光呼吸；阶段切换短过渡，重复快照不重启动画。
- 窗口首次显示时整窗短淡入，内容从略低、略小回到原位。不延迟 Show；最小化、关闭、结束展示，或系统关闭客户区动画时，立即停在完全不透明的最终状态。进度更新不重播入场。
- 初始 760×440，最小 540×400；长中文版本名省略并提供提示，不需要滚动。
- **收起/关闭窗口**只结束渲染，不取消游戏，也不影响进程内加载进度状态继续推进至完成。
- **取消启动**只取消对应启动会话；窗口不使用全局“最新实例”来决定取消对象。
- 最小化、隐藏、卸载、完成时暂停/清理动画与计时器；遵循 Windows 客户区动画开关。
- 每次启动会话独立维护进度状态与异步驱动；收尾释放会话驱动，不向外挂进程写入临时状态文件或额外 `-javaagent:` 参数。

## 验证

- 当前工作区 Debug WPF 构建：0 错误，保留仓库现有警告。
- 独立 WPF 窗口/配置/会话烟测：覆盖两阶段、未知进度、进程内扫描实例 `mods/*.jar` 推送进度、确认不向游戏 JVM 注入 `-javaagent:`、最小化、并发取消隔离、收起后继续推进及 YAML 开关持久化。
- `LaunchProgressStateTest` / `ModLoadingProgressDriverTest`：覆盖状态机协议、Fabric/Quilt 清单解析、阶段排序及取消安全。
- 截图：[启动器准备](images/launch-window-launcher.png)、[游戏加载](images/launch-screen.png)、[紧凑窗口](images/launch-screen-compact.png)、[完成](images/launch-window-ready.png)。界面截图使用测试数据，不冒充真实游戏启动。

复现（仓库根目录，先构建主程序，使用机器上可用的 .NET 10 SDK）：

```powershell
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Release --filter "FullyQualifiedName~LaunchProgressStateTest|FullyQualifiedName~ModLoadingProgressDriverTest" -p:UseSharedCompilation=false
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Debug -r win-x64 -p:Platform=x64 -p:UseSharedCompilation=false
dotnet run --project .\scripts\launch-ui-smoke\Launch.UiSmoke.csproj -- `
  '.\docs\images' "$env:PI_SCRATCH_DIR\launch-preferences"
```

尚未执行真实账号登录或完整 Minecraft/整合包启动；该整合包的 guard 策略或游戏实机加载交接耗时仍以实机为准。
