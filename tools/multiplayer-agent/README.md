# PCL-MOON multiplayer premain agent

无第三方依赖，不修改/重转换游戏字节码。Java 8 classfile（major 52）；构建需要支持 `javac --release 8` 的 JDK 9+，游戏 JVM 仍须满足游戏自身版本要求。**这是保守的反射适配器，不是所有 Minecraft/Forge/Fabric 版本的兼容保证；尚未进行真实游戏验证。**

## 构建与装载

```powershell
# 在仓库根目录执行；编译、打包、测试、字节码检查全部通过后才替换输出 jar
.\tools\multiplayer-agent\build.ps1

# 自定义输出；本仓库启动器 MSBuild 已自动构建并内嵌默认路径的 JAR
.\tools\multiplayer-agent\build.ps1 -OutputJar 'D:\path\to\launcher\Resources\pcl-moon-multiplayer-agent.jar'
# 可选 -JavaHome 'C:\path\to\jdk-21'
```

默认输出 `tools/multiplayer-agent/build/pcl-moon-multiplayer-agent.jar`。相对 `-OutputJar` 路径相对于调用者当前目录。编译产物/测试映射临时文件仅使用 `$env:PI_SCRATCH_DIR` 或系统 temp；构建临时目录在 finally 中删除。jar 仅包含 agent，不包含测试夹具。

游戏 JVM 参数：`-javaagent:<jar绝对路径>`。不使用 agentmain/动态 attach，不依赖游戏命令行地址参数。

launcher 在启动游戏前监听 **IPv4 127.0.0.1，端口 0（ephemeral）**，读取实际端口后给游戏进程设置：

- `PCL_MOON_LINK_PORT`：1..65535 十进制，无正负号/空格。
- `PCL_MOON_LINK_TOKEN`：launcher 每次启动生成的随机 32 字节，编码为 64 个 hex 字符。认证时原样、区分大小写比较。
- `PCL_MOON_LINK_MAPPINGS`（可选）：该游戏版本的官方 Mojang **client mappings txt** 路径。路径不存在或读取失败时仍尝试 named/SRG/intermediary，不下载映射。
- `PCL_MOON_LINK_RUNTIME_MAPPINGS`（可选）：launcher 已下载并解压的 UTF-8 **文本文件**路径：Fabric `mappings/mappings.tiny` 或 Forge MCPConfig `config/joined.tsrg`。agent 不下载、不打开 jar/zip；用于把同版本 Mojang named→obf 映射链到实际 intermediary/SRG 名字。缺失/损坏时保留 known-alias fallback，不阻止游戏启动。

缺少两个必需变量时安静禁用；配置错误或 agent 异常不会从 premain 抛出、阻止游戏启动。映射读取和网络工作在 daemon thread 内。所有 agent 自建线程均为 daemon；不注册阻止退出的 shutdown hook。

## 固定协议 v1

仅一次 outbound TCP `127.0.0.1:<port>`，无监听、无重连。UTF-8，每行 LF（接收兼容 CRLF）；单行最多 **16384 字节，不计 LF、计入 CR**。严格拒绝非法 UTF-8、超长行、半行超过 5 秒；认证/连接/阻塞写超时 5 秒，超时关闭会话。连接中断不自动退出当前游戏服务器。

```text
agent    -> HELLO\t1\tTOKEN
launcher -> OK
launcher -> CONNECT\t<requestId>\t<base64(UTF-8 address)>
launcher -> DISCONNECT\t<requestId>
agent    -> STATE\t<requestId或空>\t<state>\t<base64(address)>\t<base64(message)>
```

上面的 `\t` 表示真正的 TAB。Base64 使用标准 alphabet，不换行。launcher **必须先验证 HELLO 的版本和 token，再返回精确的 `OK`**；`OK` 前 agent 不发现/调用客户端，不发送 STATE。token 是本机 bearer credential，不是加密或双向身份认证，不能防御已控制本机用户进程的攻击者。

`requestId` 为 1..128 个 `[A-Za-z0-9_.:-]` 字符，建议全局唯一。命令完成/拒绝带请求 id；主动轮询带空 id。最近 256 个合法请求 id 去重；更旧的 id 不保证去重。格式错误的 id 使用空 id 报错。未知命令、坏 base64/地址以 error 回复，不关闭正常会话。

状态：

| state | 含义 |
| --- | --- |
| waiting | 尚未找到已初始化的已知 Minecraft singleton |
| ready | 基本状态反射可用、无世界/玩家/网络 handler，尚未观察到连接；不保证该运行时具有已支持的连接签名 |
| connecting | 已启动连接界面，或世界/玩家/handler 尚不完整；**不是 connected** |
| connected | 非单人，实际 world、player、play network handler 全部存在 |
| disconnected | 曾观察/发起连接后，上述对象及连接界面消失 |
| unsupported | 映射/签名缺失或歧义、单人世界、安全前置条件不满足；不会试探调用未知方法 |
| error | 协议/反射调用失败、busy、主线程超时等 |

正常状态每 1 秒通过 Minecraft Executor 采样，包括玩家手动进服/退服。请求 CONNECT 的响应只是 `connecting`；后续真实状态用空 id 发送，不永久绑定请求 id。地址从 **游戏当前 ServerData/ServerInfo 的 ip/address 字段**读取，不缓存请求地址冒充实际地址；它是游戏当前服务器条目的地址（可能为域名/SRV 名称），不是底层 socket 解析后的远端数值 IP。没有当前条目时返回空地址及说明，地址字段映射未知时明确 unsupported。由于不是 Netty hook，不能识别尚未反映到游戏对象的短暂网络变化。

地址允许域名（含 IDN）、IPv4、裸 IPv6、`[IPv6]:port`；不接受 URL、userinfo、路径、控制/空白字符、端口 0/越界及 IPv6 zone id。验证不做域名 DNS 查询；实际 DNS/SRV/服务器认证由游戏执行。

### 排队、重入、超时

- 命令操作和状态采样均经 singleton 的 **`java.util.concurrent.Executor.execute`**；没有 Executor 的版本返回 unsupported，不猜任意调度方法。
- 最多一个已提交游戏任务，轮询任务执行期间最多保留一个命令；其它命令立即 busy/error，不建无限队列。
- 游戏任务或保留命令最长等待 15 秒。尚未开始的超时任务取消后即使恢复执行也不调用游戏；取消 runnable 排空前不再提交新任务，避免卡死的游戏队列无限增长。
- 已运行的游戏方法不能安全抢占；15 秒后报 error，但仍保留互斥锁，直到该任务实际返回才恢复。每秒反馈 stalled，不反复执行、不等待主线程 future、不挂住 launcher 链路线程。
- 客户端尚未就绪的命令立即 error、不保留到未来执行；启动发现超过 60 秒仍失败则主动状态为 unsupported，继续每秒只读重试发现。
- Minecraft 已在连接界面时 CONNECT/DISCONNECT 都拒绝，要求玩家先手动取消。加载/卸载过渡中的网络/世界状态不重入切服。

## 反射兼容范围与安全边界

发现仅用 `Instrumentation.getAllLoadedClasses()`，匹配已知 client 类名/映射后的 runtime 名。使用反射 `sun.misc.Unsafe.staticFieldBase/staticFieldOffset/getObjectVolatile` 读取静态同类型非空单例存储，避免 `Field.get` 或 getInstance 触发 Minecraft 类初始化；不依赖 Java 22+ 已移除的 `shouldBeInitialized`。读到非空后，所有游戏状态采样仍在 Minecraft Executor 执行。发现线程在反射读取字段前会把 TCCL 设为客户端类的加载器，保证 Forge 事件变换器等按 TCCL 解析父类的机制能工作。VM 禁止这些内部 API 时返回 unsupported；Java 25 可能产生 Unsafe 弃用警告，此 API 是明确的未来兼容限制。

支持的角色名包括：

| 角色 | Mojmap / MCP、Yarn、intermediary |
| --- | --- |
| Client | `net.minecraft.client.Minecraft`、`MinecraftClient`、`class_310` |
| ConnectScreen | `gui.screens.ConnectScreen`、旧 MCP `gui.screen.ConnectingScreen`、Yarn `gui.screen.multiplayer.ConnectScreen`、`class_412` |
| Screen | `gui.screens.Screen` / `gui.screen.Screen`、`class_437` |
| ServerData / ServerInfo | `client.multiplayer.ServerData` / `client.network.ServerInfo`、**`class_642`** |
| ServerAddress | `client.multiplayer[.resolver].ServerAddress` / `client.network.ServerAddress`、**`class_639`** |

intermediary 名带 `net.minecraft.` 前缀。注意 `class_639` 是地址解析类，`class_642` 是服务器条目类，不能反过来。

官方 ProGuard 格式解析包含类、字段、方法、源行号区间、数组类型及重载；方法别名按声明类、返回值和全部参数类型筛选。查找只接受确定名称/官方别名或少量明确允许的**唯一类型签名**（如 startConnecting、地址 parse、getConnection/currentServer、setScreen），不按模糊名称、不枚举调用任意 void 方法。歧义直接 unsupported。

连接形状：

1. `static void startConnecting(Screen, Minecraft, ServerAddress, ServerData, boolean)`。
2. 同上加末尾 `TransferState`（官方名；也识别 Yarn CookieStorage 角色）：传 `false, null`，不是服务器 transfer 恢复。
3. 1.16.5 形状 `new ConnectScreen(Screen, Minecraft, ServerData)`，随后 `setScreen(Screen)`。
4. `ServerData(String,String,boolean)` 或 `ServerData(String,String,enum)` 且能确定枚举 `OTHER`，以及静态 `ServerAddress parse(String)`。不猜枚举 ordinal；intermediary 的 OTHER 可通过两份映射按声明类+字段类型链到 `field_xxx`，缺映射时仍可能 unsupported。

反射根据已加载 Minecraft 的实际类名选择 official obf、正常 named、intermediary 或 SRG。Forge 的 named 类 + SRG `m_xxx_/f_xxx_` 成员形状会检查客户端已有成员的完整类型签名后选择 SRG 成员，但保留实际 named 类名，不把 MCPConfig 中的 `C_xxx_` 当成实际游戏类。此选择只反射检查，不调用游戏方法；每个 bridge 使用独立映射视图，不修改源映射。旧 Forge 支持的是上述连接/Executor/字段形状，并非 1.7/1.12 全系列支持。Minecraft 新增参数、改变返回值、未知 mod 包装层、反射受限时仍可能 unsupported。

### Runtime mappings 文本格式限制

- Fabric Tiny v1：首行为 `v1<TAB>official<TAB>intermediary`；读取 CLASS/FIELD/METHOD。
- Fabric Tiny v2.0：首行为 `tiny<TAB>2<TAB>0<TAB>official<TAB>intermediary`；读取 class 及一级缩进的 f/m。参数、局部变量、注释等更深 metadata 跳过；`escaped-names` 明确不支持。
- Tiny 第一命名空间必须是 official；可有额外命名空间，按 header 找 intermediary 列。descriptor 始终属于第一命名空间，不反向猜命名空间。
- Forge TSRG v1：无 header，obf→SRG/MCP 两列类名；一级 TAB 缩进字段 `obfName srgName` 或方法 `obfName (descriptor)return srgName`。
- Forge TSRG v2：header `tsrg2 obf srg [id...]`；支持额外 id 列；二级及更深的 static/参数 metadata 全部跳过。字段使用 MCPConfig 实际的无 descriptor 形状；其他扩展的 typed-field 方言不支持。
- 方法链固定以 **official obf 声明类 + 名称 + 完整 JVM descriptor** 查找；Tiny 字段同样使用 descriptor。TSRG 无字段 descriptor，因此先按 official 声明类/字段名定位，再用 Mojang 映射的字段类型验证实际反射字段。不同类型或重载不会靠名字混用；已映射的类中不再让原始同名 decoy 绕过字段/方法别名检查。
- 文件必须对应同一游戏版本、同一 official-obf 命名空间；不是通用 remapper。不支持逆向 Tiny、SRG 文本/CSV、压缩容器、无 Mojang 映射时仅凭 loader 文件反推出官方语义。格式错误/冲突不安装半份映射。
- 对于没有 `client_mappings` 的新 named 发行版本（包括父端检测到的 26.3），**仍保留不需要任何映射的 known-named 路径**，不是强制下载映射；这不等于承诺其连接签名已实测兼容。

切换现有多人世界时，先完整解析连接方案，再解析清理方法。旧形状调用确定别名的 `ClientLevel.disconnect()` 和 `Minecraft.clearLevel/disconnect([Screen])`；26.x 形状由 `Minecraft.disconnect(Screen,false)` 完成整个网络/世界清理，不再要求旧世界无参断开方法。清理后确认 world/player/handler 消失，才启动新连接；中途失败不继续连接，已断开的服务器不自动恢复。

**单人世界（含 LAN 主机）一律拒绝切服和 DISCONNECT**，即使 world 暂时为空但 integrated server 存在也保护。检测 integrated-server 类型字段及已知 isLocalServer/hasSingleplayerServer/isInSingleplayer，任一为真即拒绝；无法证明非单人则 unsupported。agent 不代替玩家保存存档、不调用未知保存方法；请玩家先正常“保存并退出”。不能保证 mod 自己有问题的退出方法不会报错。

## 验证事实

`build.ps1` 自动执行：

- `javac --release 8`、JDK `jar`，检查 agent 和 harness 全部 class major 为 52。
- 合成客户端夹具：官方 mapping 语法/别名、重载筛选、地址/UTF-8/base64/行限制、认证拒绝、STATE、CONNECT 确实在夹具主线程执行、5/6 参数及旧构造连接、真实条目地址而非请求地址、手动进入/退出、多人切服清理、单人保护/过渡保护、busy/排队超时/过期取消。
- 子 JVM 通过真实 `-javaagent` premain + Instrumentation + 环境变量 + loopback socket 发现**合成** singleton 并执行 CONNECT；验证游戏主程序结束后 daemon agent 不阻止 JVM 退出。

开发机实际用 JDK 21 编译运行通过：**235 条合成断言，67 个 agent/harness class 全部 major 52**（状态帧数量可能因时序略有变化）。Java 8 / Java 25 运行时未实机运行；Java 8 字节码/API 由 `--release 8` 和 major 检查保证。

另只读核对 Mojang 官方 1.16.5 / 1.21.1 client mappings，确认旧构造、clearLevel、ServerData/ServerAddress 类，以及新版六参数 startConnecting/TransferState 的官方签名。映射文件未打包进工程：

- 1.16.5: `https://piston-data.mojang.com/v1/objects/374c6b789574afbdc901371207155661e0509e17/client.txt`
- 1.21.1: `https://piston-data.mojang.com/v1/objects/2244b6f072256667bcd9a73df124d6c58de77992/client.txt`

本次 runtime 扩展另只读下载并核对以下真实文本格式，临时下载均置于 scratch，未将映射文件打包：
- Fabric Tiny v1：`https://maven.fabricmc.net/net/fabricmc/intermediary/1.16.5/intermediary-1.16.5.jar` 的 `mappings/mappings.tiny`。
- Fabric Tiny v2：`https://maven.fabricmc.net/net/fabricmc/intermediary/1.21.1/intermediary-1.21.1-v2.jar` 的同名 entry。
- Forge TSRG v1/v2：`https://maven.minecraftforge.net/de/oceanlabs/mcp/mcp_config/1.16.5/mcp_config-1.16.5.zip`、`https://maven.minecraftforge.net/de/oceanlabs/mcp/mcp_config/1.21.1/mcp_config-1.21.1.zip` 的 `config/joined.tsrg`。

这四份真实文件也已完整通过 parser/链查找 smoke test：Tiny v1 5445 类/48547 成员，Tiny v2 8254 类/78922 成员，TSRG v1 5447 类/59840 成员，TSRG v2 8269 类/111338 成员。1.21.1 中 `ServerData.Type.OTHER` 链到 Fabric `field_45611`；`Minecraft.disconnect(Screen)` 链到 Fabric `method_56134` / Forge `m_91320_`。这些只是只读映射验证，不是真游戏验证。

新增合成回归覆盖四种格式、v2 extra id/参数 metadata 跳过、跨命名空间 enum OTHER（含伪同名 OTHER 陷阱）、SRG disconnect/clearLevel 后实际切服、named-class + SRG-member 的 Forge 形状、完整 descriptor/重载/字段防混淆、named/official fallback，以及子 JVM 读取 `PCL_MOON_LINK_RUNTIME_MAPPINGS`。真实游戏依然未测。

另只读检查当前 Mojang 正式版 26.3 客户端 JAR（SHA1 `e877b6a07acd633fb3bb475002175cec036e7b87`）：要求 Java 25，确认六参 startConnecting、enum ServerData、Minecraft.disconnect(Screen,boolean) 与已移除的 ClientLevel 无参 disconnect。新增合成回归覆盖新版完整断开以及无副作用单例存储读取。这不是 Java 25 / 26.3 实际游戏运行验证。

启动器集成及剩余全量测试限制见 [`docs/multiplayer-agent.md`](../../docs/multiplayer-agent.md)。**没有启动真实 Minecraft、没有验证真实 Forge/Fabric、多版本服务器或存档保存；合成测试和静态签名核对不能冒充游戏实测。**
