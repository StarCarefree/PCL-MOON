using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.WebHost;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class WebHostCompatibilityTest
{
    [TestMethod]
    public void WindowsEmbedsInstalledWebView2AndDoesNotShipIt()
    {
        var capability = WebHostCapability.Evaluate(
            WebHostOs.Windows,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            systemWebView2Installed: true);

        Assert.AreEqual("SystemWebView2", capability.EmbeddedEngine);
        Assert.IsTrue(capability.CanEmbedWebView2);
        Assert.IsFalse(capability.CanEmbedSystemChrome);
        Assert.IsTrue(capability.CanOpenSystemChrome);
        Assert.IsTrue(capability.UsesSystemBrowser);
        Assert.IsFalse(capability.ShipsBrowserRuntime);
        Assert.IsTrue(capability.NativeWpfShellPortable);
    }

    [TestMethod]
    public void MissingRuntimeDoesNotFallBackToChrome()
    {
        var capability = WebHostCapability.Evaluate(
            WebHostOs.Windows,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            systemWebView2Installed: false);

        Assert.AreEqual("None", capability.EmbeddedEngine);
        Assert.IsFalse(capability.CanEmbedWebView2);
        Assert.IsFalse(capability.ShipsBrowserRuntime);
    }

    [TestMethod]
    public void MacHasNoSystemWebView2()
    {
        var capability = WebHostCapability.Evaluate(
            WebHostOs.MacOS,
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            systemWebView2Installed: true);

        Assert.AreEqual("None", capability.EmbeddedEngine);
        Assert.IsFalse(capability.CanEmbedWebView2);
        Assert.IsFalse(capability.ShipsBrowserRuntime);
        Assert.IsTrue(capability.PageLayerPortable);
        Assert.IsFalse(capability.NativeWpfShellPortable);
    }

    [TestMethod]
    public void CustomBrowserFolderIsNeverPassedToTheLoader()
    {
        Assert.IsNull(SystemWebView2Plan.BrowserExecutableFolder(null));
        Assert.IsNull(SystemWebView2Plan.BrowserExecutableFolder(@"C:\Program Files\Google\Chrome\Application"));
        Assert.IsNull(SystemWebView2Plan.BrowserExecutableFolder(@"C:\FixedRuntime"));
        Assert.IsTrue(SystemWebView2Plan.IgnoresCustomFolder(@"C:\FixedRuntime"));
        Assert.IsFalse(SystemWebView2Plan.PlatformCanHost(WebHostOs.MacOS));
        Assert.IsTrue(SystemWebView2Plan.PlatformCanHost(WebHostOs.Windows));
    }

    [TestMethod]
    public void PageCatalogKeepsNativeIdsAndDoesNotPretendEveryPageIsPorted()
    {
        Assert.IsTrue(LauncherPageCatalog.Routes.Count > 20);
        Assert.IsTrue(LauncherPageCatalog.Find("setup.about")!.WebImplemented);
        Assert.IsFalse(LauncherPageCatalog.Find("download.forge")!.WebImplemented);
        Assert.AreEqual(LauncherPageCatalog.Download, LauncherPageCatalog.Find("download.forge")!.NativePage);
        Assert.AreEqual(11, LauncherPageCatalog.Find("download.forge")!.NativeSubType);
        Assert.IsNull(LauncherPageCatalog.Find("tools.lobby"));
        Assert.AreEqual(3, LauncherPageCatalog.Find("tools.test")!.NativeSubType);
        Assert.IsNull(LauncherPageCatalog.Find("missing"));
    }

    [TestMethod]
    public void PlatformNamesMapToHostOs()
    {
        Assert.AreEqual(WebHostOs.MacOS, WebHostPlatform.Detect("Darwin 24.0.0"));
        Assert.AreEqual(WebHostOs.Windows, WebHostPlatform.Detect("Microsoft Windows 10.0.26100"));
        Assert.AreEqual(WebHostOs.Linux, WebHostPlatform.Detect("Linux 6.8"));
    }
}
