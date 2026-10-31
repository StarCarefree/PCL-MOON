[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="PCL-MOON" width="80" height="80">

# PCL-MOON

[![Stars](https://img.shields.io/github/stars/StarCarefree/PCL-MOON?style=for-the-badge&label=stars&labelColor=444444&color=eac54f)](https://github.com/StarCarefree/PCL-MOON)
[![Release](https://img.shields.io/github/v/release/StarCarefree/PCL-MOON?label=release&logo=github&style=for-the-badge)](https://github.com/StarCarefree/PCL-MOON/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/StarCarefree/PCL-MOON/build-test.yml?branch=dev&label=build&style=for-the-badge)](https://github.com/StarCarefree/PCL-MOON/actions/workflows/build-test.yml)

[![Issues](https://img.shields.io/github/issues/StarCarefree/PCL-MOON?style=for-the-badge&label=issues&labelColor=444444&color=1F883D&logo=github)](https://github.com/StarCarefree/PCL-MOON/issues)
[![Pull requests](https://img.shields.io/github/issues-pr/StarCarefree/PCL-MOON?style=for-the-badge&label=pull%20requests&labelColor=444444&color=1F883D&logo=github)](https://github.com/StarCarefree/PCL-MOON/pulls)

[Download](https://github.com/StarCarefree/PCL-MOON/releases/latest) |
[Issues](https://github.com/StarCarefree/PCL-MOON/issues) |
[Contributing](CONTRIBUTING.md)

[PCL upstream](https://github.com/Meloong-Git/PCL) |
[PCL CE](https://github.com/PCL-Community/PCL-CE)

</div>

PCL-MOON is a Windows Minecraft launcher. It continues from the open-source code of [PCL](https://github.com/Meloong-Git/PCL) and [PCL CE](https://github.com/PCL-Community/PCL-CE), but the product name, UI brand, and update source belong to this repository.

Version numbers do not track PCL or PCL CE. Report PCL-MOON issues to [this repository](https://github.com/StarCarefree/PCL-MOON/issues), not upstream. Do not install a PCL CE `PCL2_CE_*.exe` or a mirror build as an update for this launcher.

## What this tree adds

- **Native shell**: accounts, launch, the instance list, and instance settings stay on .NET 10 / WPF. WebView2 only supplements Browse. It does not cover the title bar or sidebar.
- **Resource browse**: datapacks, maps, modpacks, shaders, and resource packs. MCL, CurseForge, Modrinth, and MCMod can be selected together. Downloads open the existing file list; the player chooses the version.
- **Modpack catalog**: configure one `index.json` primary source and up to eight fallbacks. The first valid source wins; sources are not merged. A Quark share saves only the selected `.mrpack` / `.zip` into the drive folder `PCL-MOON`, and only after the user agrees to authorize.
- **Server management**: servers live under `servers/<id>/` in the launcher directory, separate from client instances, mods, and accounts. Vanilla, Paper, Purpur, or an author-provided server pack can be installed, with a console and `server.properties`. The launcher does not open firewall ports or port mappings.
- **Launch animation**: on by default. Turn it off under Settings → Personalization → Basic. The original progress panel remains, and launching the game is unchanged.

See the docs below for limits. A verification note is not a claim that every online flow has been run on your machine.

## Download

Use [this repository's Releases](https://github.com/StarCarefree/PCL-MOON/releases/latest) only. The launcher checks `https://api.github.com/repos/StarCarefree/PCL-MOON/releases` and picks the `pcl-moon-win-*.exe` for the current architecture.

| File | Use |
| --- | --- |
| `pcl-moon-win-x64.exe` | 64-bit Windows program. This is the file the updater downloads. |
| `pcl-moon-win-ARM64.exe` | ARM64 Windows program. |
| `pcl-moon-win-x64-portable.zip` | 64-bit portable package. Extract the whole directory, then run `Start-PCL-CY.cmd`. |
| `pcl-moon-win-ARM64-portable.zip` | ARM64 portable package. Same entry point. |

Releases do not bundle the .NET 10 Desktop Runtime. The portable package asks before downloading it from Microsoft. The single executable lists missing components first and downloads only after **Agree and download**. If only the browse component is missing, you can skip it and still start.

Do not mix x64 and ARM64. Do not copy the executable out of the portable package: Browse depends on the sibling `WebView2Runtime` directory.

## Supported platforms

| Operating system | Support | Requirement |
| --- | --- | --- |
| Windows 10 1809 (17763) or later | Supported | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 8 through Windows 10 before 1809 | May open; features are not guaranteed | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 7 or earlier | Not supported | — |
| macOS / Linux | Cross-compile only; cannot run the WPF app | [.NET 10 SDK](https://get.dot.net/10) |

Only the latest release from this repository is maintained. The WPF UI is not a macOS or Linux client.

## Build

Install the [.NET 10 SDK](https://get.dot.net/10) on Windows, then from the repository root:

```powershell
.\scripts\Publish-SingleExe.ps1 -Architecture x64
```

Or double-click `scripts/Publish-SingleExe.bat`. The local single-file output is named `PCL-MOON.exe`. That is not the same filename as `pcl-moon-win-x64.exe` on Releases.

Portable package:

```powershell
.\scripts\Publish-Portable.ps1 -Architecture x64 -Configuration Release -FixedRuntimeDirectory <FixedVersion-root>
```

Build `x64` and `arm64` separately. The catalog service lives in `pcl-moon-cloud/` and need Java 17 or later. See [pcl-moon-cloud/README.md](pcl-moon-cloud/README.md).

## Docs

- [Native shell and browse page](docs/hybrid-ui.md)
- [Five resource types](docs/resource-browse.md)
- [Modpack catalog sources](docs/catalog-sources.md)
- [Server management](docs/server-management.md)
- [Server core sources](docs/server-core-sources.md)
- [Launch animation](docs/game-launch-animation.md)
- [Portable package and single executable](docs/webview2-portable.md)

## License

- `Plain Craft Launcher 2/` uses the upstream [PCL limited distribution license](Plain%20Craft%20Launcher%202/LICENCE). Rights not expressly granted remain with the licensor.
- Every other directory uses the [Apache License 2.0](LICENSE).

## Contributors

[![](https://contrib.rocks/image?repo=StarCarefree/PCL-MOON)](https://github.com/StarCarefree/PCL-MOON/graphs/contributors)
