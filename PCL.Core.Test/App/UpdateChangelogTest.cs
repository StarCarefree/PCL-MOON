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
}
