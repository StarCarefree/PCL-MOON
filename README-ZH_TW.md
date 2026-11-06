[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="PCL-MOON" width="80" height="80">

# PCL-MOON

[![Stars](https://img.shields.io/github/stars/StarCarefree/PCL-MOON?style=for-the-badge&label=stars&labelColor=444444&color=eac54f)](https://github.com/StarCarefree/PCL-MOON)
[![Release](https://img.shields.io/github/v/release/StarCarefree/PCL-MOON?label=release&logo=github&style=for-the-badge)](https://github.com/StarCarefree/PCL-MOON/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/StarCarefree/PCL-MOON/build-test.yml?branch=dev&label=build&style=for-the-badge)](https://github.com/StarCarefree/PCL-MOON/actions/workflows/build-test.yml)

[![Issues](https://img.shields.io/github/issues/StarCarefree/PCL-MOON?style=for-the-badge&label=issues&labelColor=444444&color=1F883D&logo=github)](https://github.com/StarCarefree/PCL-MOON/issues)
[![Pull requests](https://img.shields.io/github/issues-pr/StarCarefree/PCL-MOON?style=for-the-badge&label=pull%20requests&labelColor=444444&color=1F883D&logo=github)](https://github.com/StarCarefree/PCL-MOON/pulls)

[下載](https://github.com/StarCarefree/PCL-MOON/releases/latest) |
[問題回報](https://github.com/StarCarefree/PCL-MOON/issues) |
[貢獻說明](CONTRIBUTING.md)

[PCL 上游](https://github.com/Meloong-Git/PCL) |
[PCL CE](https://github.com/PCL-Community/PCL-CE)

</div>

PCL-MOON 是 Windows 上的 Minecraft 啟動器。它基於 [PCL](https://github.com/Meloong-Git/PCL) 與 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的開放原始碼繼續開發，但程式名稱、介面品牌和更新來源都是本儲存庫。

版本號不與 PCL 主線或 PCL CE 對應。PCL-MOON 的問題請提交到 [本儲存庫 Issues](https://github.com/StarCarefree/PCL-MOON/issues)，不要發到上游或 PCL CE。也不要安裝 PCL CE 的 `PCL2_CE_*.exe` 或鏡像站套件來更新本啟動器。

## 這版包含什麼

- **原生主介面**：帳號、啟動、版本清單和版本設定由 .NET 10 / WPF 執行。WebView2 只補充「瀏覽」，不覆蓋標題列和側欄。
- **資源瀏覽**：資料包、地圖、整合包、光影包、材質包。來源可多選 MCL、CurseForge、Modrinth 和 MC 百科。下載進入原有檔案清單，由玩家選擇版本。
- **整合包目錄**：可設定 `index.json` 主來源和最多 8 個備用來源，成功一個即停止，不合併多個目錄。夸克分享只有在同意授權後，才會把選中的單個 `.mrpack` / `.zip` 轉存到網盤目錄 `PCL-MOON`。
- **伺服器管理**：伺服器放在啟動器目錄 `servers/<id>/`，與客戶端版本、模組和帳號隔離。可安裝 Vanilla、Paper、Purpur 或整合包作者提供的伺服器包，並管理主控台與 `server.properties`。不會自動開放防火牆或連接埠對應。
- **啟動動畫**：預設開啟。設定 → 個性化 → 基礎選項可關閉；關閉後仍使用原來的啟動進度面板，不影響啟動遊戲。

更細的邊界和操作見下方文件。文件裡的驗證紀錄不等於所有連網流程都已在你的機器上跑通。

## 下載

只從 [本儲存庫 Releases](https://github.com/StarCarefree/PCL-MOON/releases/latest) 取得發布檔案。啟動器檢查更新時也只請求 `https://api.github.com/repos/StarCarefree/PCL-MOON/releases`，並選擇目前架構的 `pcl-moon-win-*.exe`。

| 檔案 | 用途 |
| --- | --- |
| `pcl-moon-win-x64.exe` | 64 位元 Windows 程式，也是啟動器更新會下載的檔案 |
| `pcl-moon-win-ARM64.exe` | ARM64 Windows 程式 |
| `pcl-moon-win-x64-portable.zip` | 64 位元便攜包。解壓整個目錄後執行 `Start-PCL-CY.cmd` |
| `pcl-moon-win-ARM64-portable.zip` | ARM64 便攜包，用法相同 |

發布包不附帶 .NET 10 Desktop Runtime。便攜包缺少執行階段時會先彈出確認框，只有點「是」才從微軟官方網址下載安裝。單個 exe 會先列出缺少的元件；點「同意並下載」才安裝。只缺瀏覽頁元件時可以跳過並啟動。

x64 與 ARM64 不能混用。不要把便攜包裡的 exe 單獨拷走：瀏覽頁依賴同層 `WebView2Runtime`。

## 支援平台

| 作業系統 | 支援情況 | 環境要求 |
| --- | --- | --- |
| Windows 10 1809 (17763) 或更高 | 完整支援 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 8 到 Windows 10 1809 之前 | 可能能開啟，不保證功能完整 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 7 或更低 | 不支援 | — |
| macOS / Linux | 只能交叉編譯，不能直接執行 | [.NET 10 SDK](https://get.dot.net/10) |

本儲存庫只維護最新發布。WPF 介面不能當作 macOS 或 Linux 客戶端。

## 自行建置

在 Windows 上安裝 [.NET 10 SDK](https://get.dot.net/10)，於儲存庫根目錄執行：

```powershell
.\scripts\Publish-SingleExe.ps1 -Architecture x64
```

或按兩下 `scripts/Publish-SingleExe.bat`。本機單檔產物名稱是 `PCL-MOON.exe`，不要把它和 Release 上的 `pcl-moon-win-x64.exe` 當成同一個檔名。

便攜包：

```powershell
.\scripts\Publish-Portable.ps1 -Architecture x64 -Configuration Release -FixedRuntimeDirectory <FixedVersion根目錄>
```

`x64` 與 `arm64` 必須分別建置。目錄服務在 `pcl-moon-cloud/`，需要 Java 17 或更高版本，見 [pcl-moon-cloud/README.md](pcl-moon-cloud/README.md)。

## 文件

- [原生介面與瀏覽頁](docs/hybrid-ui.md)
- [五類資源瀏覽](docs/resource-browse.md)
- [整合包目錄來源](docs/catalog-sources.md)
- [伺服器管理](docs/server-management.md)
- [伺服器核心來源](docs/server-core-sources.md)
- [啟動動畫](docs/game-launch-animation.md)
- [便攜包與單個 exe](docs/webview2-portable.md)

## 授權條款

- `Plain Craft Launcher 2/` 使用上游 [《PCL 散布有限許可》](Plain%20Craft%20Launcher%202/LICENCE)。除該許可明確授予的權利外，權利仍歸許可方。
- 其餘目錄使用 [Apache License 2.0](LICENSE)。

## 貢獻者

[![](https://contrib.rocks/image?repo=StarCarefree/PCL-MOON)](https://github.com/StarCarefree/PCL-MOON/graphs/contributors)
