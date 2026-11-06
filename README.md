**简体中文** | [English](README-EN.md) | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="PCL-MOON" width="80" height="80">

# PCL-MOON

[![Stars](https://img.shields.io/github/stars/StarCarefree/PCL-MOON?style=for-the-badge&label=stars&labelColor=444444&color=eac54f)](https://github.com/StarCarefree/PCL-MOON)
[![Release](https://img.shields.io/github/v/release/StarCarefree/PCL-MOON?label=release&logo=github&style=for-the-badge)](https://github.com/StarCarefree/PCL-MOON/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/StarCarefree/PCL-MOON/build-test.yml?branch=dev&label=build&style=for-the-badge)](https://github.com/StarCarefree/PCL-MOON/actions/workflows/build-test.yml)

[![Issues](https://img.shields.io/github/issues/StarCarefree/PCL-MOON?style=for-the-badge&label=issues&labelColor=444444&color=1F883D&logo=github)](https://github.com/StarCarefree/PCL-MOON/issues)
[![Pull requests](https://img.shields.io/github/issues-pr/StarCarefree/PCL-MOON?style=for-the-badge&label=pull%20requests&labelColor=444444&color=1F883D&logo=github)](https://github.com/StarCarefree/PCL-MOON/pulls)

[下载](https://github.com/StarCarefree/PCL-MOON/releases/latest) |
[问题反馈](https://github.com/StarCarefree/PCL-MOON/issues) |
[贡献说明](CONTRIBUTING.md)

[PCL 上游](https://github.com/Meloong-Git/PCL) |
[PCL CE](https://github.com/PCL-Community/PCL-CE)

</div>

PCL-MOON 是 Windows 上的 Minecraft 启动器。它基于 [PCL](https://github.com/Meloong-Git/PCL) 与 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的开源代码继续开发，但程序名、界面品牌和更新源都是本仓库。

版本号不与 PCL 主线或 PCL CE 对应。PCL-MOON 的问题请提交到 [本仓库 Issues](https://github.com/StarCarefree/PCL-MOON/issues)，不要发到上游或 PCL CE。也不要安装 PCL CE 的 `PCL2_CE_*.exe` 或镜像站包来更新本启动器。

## 这版包含什么

- **原生主界面**：账号、启动、版本列表和版本设置由 .NET 10 / WPF 执行。WebView2 只补充“浏览”，不覆盖标题栏和侧栏。
- **资源浏览**：数据包、地图、整合包、光影包、材质包。来源可多选 MCL、CurseForge、Modrinth 和 MC 百科。下载进入原有文件列表，由玩家选择版本。
- **整合包目录**：可配置 `index.json` 主源和最多 8 个备用源，成功一个即停止，不合并多个目录。夸克分享只有在同意授权后，才会把选中的单个 `.mrpack` / `.zip` 转存到网盘目录 `PCL-MOON`。
- **服务端管理**：服务端放在启动器目录 `servers/<id>/`，与客户端版本、模组和账号隔离。可安装 Vanilla、Paper、Purpur 或整合包作者提供的服务端包，并管理控制台与 `server.properties`。不会自动开放防火墙或端口映射。
- **启动动画**：默认开启。设置 → 个性化 → 基础选项可关闭；关闭后仍使用原来的启动进度面板，不影响启动游戏。

更细的边界和操作见下方文档。文档里的验证记录不等于所有联网流程都已在你的机器上跑通。

## 下载

只从 [本仓库 Releases](https://github.com/StarCarefree/PCL-MOON/releases/latest) 获取发布文件。启动器检查更新时也只请求 `https://api.github.com/repos/StarCarefree/PCL-MOON/releases`，并选择当前架构的 `pcl-moon-win-*.exe`。

| 文件 | 用途 |
| --- | --- |
| `pcl-moon-win-x64.exe` | 64 位 Windows 程序，也是启动器更新会下载的文件 |
| `pcl-moon-win-ARM64.exe` | ARM64 Windows 程序 |
| `pcl-moon-win-x64-portable.zip` | 64 位便携包。解压整个目录后运行 `Start-PCL-CY.cmd` |
| `pcl-moon-win-ARM64-portable.zip` | ARM64 便携包，用法相同 |

发布包不附带 .NET 10 Desktop Runtime。便携包缺少运行时时会先弹出确认框，只有点“是”才从微软官方地址下载安装。单个 exe 会先列出缺少的组件；点「同意并下载」才安装。只缺浏览页组件时可以跳过并启动。

x64 与 ARM64 不能混用。不要把便携包里的 exe 单独拷走：浏览页依赖同级 `WebView2Runtime`。

## 支持平台

| 操作系统 | 支持情况 | 环境要求 |
| --- | --- | --- |
| Windows 10 1809 (17763) 或更高 | 完整支持 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 8 到 Windows 10 1809 之前 | 可能能打开，不保证功能完整 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 7 或更低 | 不支持 | — |
| macOS / Linux | 只能交叉编译，不能直接运行 | [.NET 10 SDK](https://get.dot.net/10) |

本仓库只维护最新发布。WPF 界面不能当作 macOS 或 Linux 客户端。

## 自行构建

在 Windows 上安装 [.NET 10 SDK](https://get.dot.net/10)，于仓库根目录执行：

```powershell
.\scripts\Publish-SingleExe.ps1 -Architecture x64
```

或双击 `scripts/Publish-SingleExe.bat`。本地单文件产物名是 `PCL-MOON.exe`，不要把它和 Release 上的 `pcl-moon-win-x64.exe` 当成同一个文件名。

便携包：

```powershell
.\scripts\Publish-Portable.ps1 -Architecture x64 -Configuration Release -FixedRuntimeDirectory <FixedVersion根目录>
```

`x64` 与 `arm64` 必须分别构建。目录服务在 `pcl-moon-cloud/`，需要 Java 17 或更高版本，见 [pcl-moon-cloud/README.md](pcl-moon-cloud/README.md)。

## 文档

- [原生界面与浏览页](docs/hybrid-ui.md)
- [五类资源浏览](docs/resource-browse.md)
- [整合包目录源](docs/catalog-sources.md)
- [服务端管理](docs/server-management.md)
- [服务端核心来源](docs/server-core-sources.md)
- [启动动画](docs/game-launch-animation.md)
- [便携包与单个 exe](docs/webview2-portable.md)

## 许可证

- `Plain Craft Launcher 2/` 使用上游 [《PCL 分发有限许可》](Plain%20Craft%20Launcher%202/LICENCE)。除该许可明确授予的权利外，权利仍归许可方。
- 其余目录使用 [Apache License 2.0](LICENSE)。

## 贡献者

[![](https://contrib.rocks/image?repo=StarCarefree/PCL-MOON)](https://github.com/StarCarefree/PCL-MOON/graphs/contributors)
