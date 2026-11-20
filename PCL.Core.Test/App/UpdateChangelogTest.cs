using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Essentials;

namespace PCL.Core.Test.App;

[TestClass]
public sealed class UpdateChangelogTest
{
    private const string Hash = "2afd93757b1fe7875d81c75a967148e5119919b46bcebc980f1d3c4696133bfa";
    private const string Changelog = "## 2.15.5\n\n### 优化\n\n- browse: 浏览页首次打开时显示骨架加载动画\n\n### 其他\n\n- 自定义版本信息默认值改为 PCLMOON";

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void RemovesReleasePackagingNotes(string newline)
    {
        var markdown = (Changelog + "\n\n下载 `pcl-moon-win-x64.exe`。这是框架依赖单文件，本机需要 .NET 10 Desktop Runtime；缺少时启动器会先询问再下载。\n未内置 WebView2 Fixed Runtime。本包默认开启 ReadyToRun。\n\nSHA-256: `" + Hash + "`\n")
            .Replace("\n", newline);

        var result = UpdateHelper.CleanChangelog(markdown);

        Assert.AreEqual(Changelog, result.Replace("\r\n", "\n"));
        Assert.AreEqual(result, UpdateHelper.CleanChangelog(result));
    }

    [TestMethod]
    public void PreservesRegularChangelogAndLaterSections()
    {
        var notes = "### 问题修复\n\n- 修复下载 `pcl-moon-win-x64.exe` 时的进度显示\n- 修复 SHA-256 校验异常\n\n.NET 10 和 ReadyToRun 启动性能优化。";
        Assert.AreEqual(notes, UpdateHelper.CleanChangelog(notes));
        Assert.AreEqual(Changelog + "\n\n" + notes,
            UpdateHelper.CleanChangelog(Changelog + "\n\n下载 `pcl-moon-win-arm64.exe`。安装包说明。\n\nSHA-256: `" + Hash + "`\n\n" + notes));
    }

    [TestMethod]
    public void PreservesUnrelatedDownloadInstructions()
    {
        const string markdown = "下载 `example.exe`。使用说明。\n\nSHA-256: 校验失败时请重试。";
        Assert.AreEqual(markdown, UpdateHelper.CleanChangelog(markdown));
    }

    [TestMethod]
    public void AcceptsMissingChangelog()
    {
        Assert.AreEqual(string.Empty, UpdateHelper.CleanChangelog(null));
        Assert.AreEqual(string.Empty, UpdateHelper.CleanChangelog(string.Empty));
    }

    [TestMethod]
    public void ExtractsExplicitSummaryTag()
    {
        const string commented = "<!-- <summary>重绘无边框星轨启动窗口。\n支持快捷下载检测前置。</summary> -->\n\n## 2.17.0\n\n### 新增与优化\n\n- **启动窗口**：升级视觉效果。";
        const string bare = "<summary>一行摘要内容</summary>\n\n## 2.17.0\n\n- 优化。";

        Assert.AreEqual("重绘无边框星轨启动窗口。 支持快捷下载检测前置。", UpdateHelper.ExtractSummary(commented));
        Assert.AreEqual("一行摘要内容", UpdateHelper.ExtractSummary(bare));
    }

    [TestMethod]
    public void ExtractsFallbackSummaryFromMarkdownWithoutTag()
    {
        var markdown = "## 2.17.0\n\n### 新增与优化\n\n- **独立游戏启动窗口重绘**：改为无边框星轨主题设计。\n- **启动闪屏**：新增 [独立加载动画](https://example.com) 体验。\n- **原生性能加速**：内置 `pcl_native` 提升速度。\n- **第 4 条内容**：不应继续包含。\n\n下载 `pcl-moon-win-x64.exe`。安装包说明。\n\nSHA-256: `" + Hash + "`";

        var summary = UpdateHelper.ExtractSummary(markdown);

        Assert.AreEqual(
            "• 独立游戏启动窗口重绘：改为无边框星轨主题设计。\n• 启动闪屏：新增 独立加载动画 体验。\n• 原生性能加速：内置 pcl_native 提升速度。",
            summary);
    }

    [TestMethod]
    public void StripsSummaryMetadataForMarkdownDisplay()
    {
        var raw = "<!-- <summary>简短摘要</summary> -->\n\n\n## 2.17.0\n\n<summary>残留标签</summary>\n\n### 新增与优化\n\n- 改进内容\n\n下载 `pcl-moon-win-x64.exe`。说明。\n\nSHA-256: `" + Hash + "`";

        Assert.AreEqual("## 2.17.0\n\n### 新增与优化\n\n- 改进内容", UpdateHelper.StripSummaryMetadata(raw));
    }

    [TestMethod]
    public void MatchesVersionRecognizesMatchingAndMismatchedChangelogs()
    {
        Assert.IsTrue(UpdateHelper.MatchesVersion("## 2.17.0\n\n- 内容", "2.17.0"));
        Assert.IsTrue(UpdateHelper.MatchesVersion("<!-- <summary>摘要</summary> -->\n\n## 2.17.0\n\n- 内容", "2.17.0"));
        Assert.IsTrue(UpdateHelper.MatchesVersion("## v2.17.0 更新一览(2026-11-20)\n\n- 内容", "2.17.0"));
        Assert.IsFalse(UpdateHelper.MatchesVersion("## 2.16.1\n\n- 旧内容", "2.17.0"));
        Assert.IsFalse(UpdateHelper.MatchesVersion("没有标题的文本 2.17.0", "2.17.0"));
        Assert.IsFalse(UpdateHelper.MatchesVersion(null, "2.17.0"));
    }
}
