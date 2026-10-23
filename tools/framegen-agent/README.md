# 实验性游戏画面插帧 Agent

## 使用

在 **设置 → 启动 → 高级设置** 中启用，下次启动游戏生效：

- **实验性游戏画面插帧**：通用 OpenGL 双向块匹配，2× 呈现，默认目标 60 FPS，面向核显/NVIDIA 的 OpenGL 路径，无 DLSS、CUDA 或厂商扩展依赖；核显尚未实测。
- **记录游戏长帧诊断**：可以单独启用；发现前台窗口超过 250ms 无呈现时，在启动器捕获的游戏输出中记录 `[PCL FrameTime]`、GC 时间变化和渲染线程栈。

两个开关均默认关闭，不写入或替换实例的 mod 文件，也不自动修改 JVM/游戏设置。关闭后重新启动即可移除注入；还可用 JVM 参数 `-Dpclmoon.framegen.disabled=true` 强制禁用。手动指定同名 Agent 时不会重复注入。

## 效果与限制

这是**实验功能，不是已完成的 1.16.5–26.3 兼容矩阵**。

- 以两张真帧估计双向运动、在 GPU 上生成半时刻画面，再呈现当前真帧；不是重复帧或简单 crossfade。低分辨率运动场宽度最多 160，再做逐级块匹配细化。捕获、匹配、合成都不读回整帧到 CPU。
- 默认目标是 **60 呈现 FPS / 约 30 真渲染 FPS**。Minecraft 自身的 FPS 计数仍可能显示约 30；它不统计额外呈现。插值增加等待和输入延迟，并消耗 GPU 带宽，不能承诺比原生渲染更快。建议游戏帧率上限不低于 60。
- 不分离 HUD。遮挡、均匀色块、细小物体、快速独立运动等可能出现匹配歧义/伪影；低置信度区域使用当前真帧，不保证所有画面都能正确插值。
- 仅支持有当前上下文的 **GLFW + OpenGL 3.2+ 双缓冲**。Vulkan、新渲染后端、尚未加载 GLFW 的情形不受此方案覆盖；不能据此声称 26.3 可用。未修改或取消世界渲染，不依赖 Minecraft 混淆名称。
- 不支持 32 位 JVM。Agent 自有代码为 Java 8 字节码；构建用 JDK 9+。尚未进行 Java 8 运行时、核显、全部 Minecraft 版本及光影/录像 mod 的实机矩阵测试。

### 防止插帧加重卡顿

- 绝对时间调度；错过时限直接跳过，不突发补播积压帧，不自旋等待，不吞掉中断。
- 间隔超过 100ms、失焦、最小化或窗口大小变化后丢弃历史，恢复时重新预热。
- OpenGL 3.3+ 使用异步 GPU 查询；仅查询结果已就绪时读取。匹配开销超过 4ms，冷却 120 张真帧后重新预热。3.2 无 GPU timer 时仍按呈现时限跳过。
- 不接管其他渲染器已有的时间查询。保存/恢复游戏 FBO、多目标输出、采样器、纹理、VAO、程序、视口、颜色掩码等状态。异常时熔断，尽可能恢复当前真帧并退回原生呈现。

**插帧无法消除 GC STW、区块加载、磁盘或主线程阻塞引起的 0 FPS。** 当前没有该问题的实机复现/日志，不能宣称根因已修复。请先单独打开长帧诊断，复现后查看 `[PCL FrameTime]`。GC 时间变化只是采样信息，不是因果判定；加载/切换焦点也可能产生长间隔。

## 构建与验证

```bash
python -X utf8 tools/framegen-agent/build.py
python -X utf8 tools/framegen-agent/test.py --skip-build --gpu
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Release --filter FullyQualifiedName~FrameGenerationPolicyTest
```

需要 PATH 中可用的 Python、JDK javac/java、.NET 10 SDK。构建用 SHA256 固定版本校验下载 ASM/LWJGL，临时文件优先放 `PI_SCRATCH_DIR`。MSBuild 自动构建并嵌入 Agent；运行游戏不联网下载。

- 输出：`tools/framegen-agent/build/pcl-moon-framegen-agent.jar`，已 gitignore。
- `test.py` 的 11 个场景验证真正的 `-javaagent` / bootstrap 桥接 / 隔离类加载路径，并使用可控 GLFW 替身检查调度、回退、禁用、失焦、超预算、窗口释放、长间隔不补播，以及受控阻塞期间实际输出长帧诊断。替身通过不等于游戏兼容。
- `--gpu` 使用测试专用 Windows x64 LWJGL natives，创建隐藏 3.2 和 3.3 核心上下文，检查真实中间位移、排除简单混帧/重复帧、GL 状态/MRT/已有 timer query、场景切换、缩放和重复释放。仅测试代码读回像素；natives 不打包进 Agent。
- 已验证：RTX 2080 SUPER / NVIDIA 595.79，3.2、3.3 核心上下文；半位移图像 MAE 约 `0.00108`，简单混帧对照约 `0.09571`。这是合成输入的正确性测试，不是游戏 FPS 提升或核显性能基准。
- 已验证：13 项 C# 启动策略测试，以及启动器 Release 构建（0 错误，现有仓库有大量警告）。
- 另用临时下载的 LWJGL 3.2.2 重跑了上述 3.2/3.3 GPU 图像与状态测试，均通过；其在当前 Java 21 下出现旧 LWJGL 的 JNI 版本警告。本结果不是 Minecraft 1.16.5 + Java 8 的实机验证。

高级参数：`-Dpclmoon.framegen.outputFps=60`（范围 30–240）；手动使用 Agent 时可传 `-Dpclmoon.framegen.enabled=false -Dpclmoon.framegen.diagnostics=true` 只采样不插帧。

## 实现与许可

`GlfwTransformer` 只包装 LWJGL 的非 native `glfwSwapBuffers` / `glfwDestroyWindow`，不重转换已加载的类，不改 JDK。bootstrap 桥接本身不依赖 LWJGL；渲染器通过以游戏 GLFW 类加载器为父的隔离加载器，使用游戏自带 LWJGL。ASM 被私有重定位，避免干扰 mod 加载器。

参考研究：<https://github.com/Screamix1/FrameGen>。该项目源码采用深度重投影外推，当前源码明确适配 NeoForge 1.21.1；本模块独立实现图像空间块匹配，不复制其 LGPL 源码或着色器。依赖声明见 `THIRD-PARTY-NOTICES.md`。
