using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class AppCdsSettingsLayoutTest
{
    private static string RepositoryRoot([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [TestMethod]
    public void AppCdsHasOneVisibleEntryUsingExistingConfigurationAndHandler()
    {
        var root = RepositoryRoot();
        var page = XDocument.Load(Path.Combine(root, "Plain Craft Launcher 2", "Pages", "PageSetup", "PageSetupLaunch.xaml"));
        XNamespace local = "clr-namespace:PCL";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var entries = page.Descendants(local + "MyCheckBox")
            .Where(e => (string?)e.Attribute("Tag") == "LaunchAppCds").ToArray();
        Assert.HasCount(1, entries);
        var checkbox = entries[0];
        Assert.AreEqual("CheckAppCds", (string?)checkbox.Attribute(x + "Name"));
        Assert.AreEqual("CheckBoxChange", (string?)checkbox.Attribute("Change"));
        var card = checkbox.Ancestors(local + "MyCard").Single();
        Assert.AreEqual("CardStartupAcceleration", (string?)card.Attribute(x + "Name"));
        Assert.AreEqual("{DynamicResource Setup.Launch.Acceleration.Title}", (string?)card.Attribute("Title"));
        Assert.IsFalse(checkbox.AncestorsAndSelf().Any(e =>
            (string?)e.Attribute("Visibility") is "Collapsed" or "Hidden" ||
            (string?)e.Attribute("IsSwapped") == "True"));
        Assert.IsTrue(card.Descendants().Any(e =>
            (string?)e.Attribute("Text") == "{DynamicResource Setup.Launch.Advanced.AppCds.ToolTip}" &&
            (string?)e.Attribute("TextWrapping") == "Wrap"));
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "Plain Craft Launcher 2", "Pages", "PageSetup", "PageSetupLaunch.xaml.cs")),
            "CheckAppCds.Checked = Config.Launch.AppCds;");
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "PCL.Core", "App", "Config.cs")),
            "[ConfigItem<bool>(\"LaunchAppCds\", false, ConfigSource.Local)]");
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("zh-TW")]
    [DataRow("zh-HK")]
    [DataRow("en-US")]
    [DataRow("en-GB")]
    [DataRow("es-ES")]
    [DataRow("fr-FR")]
    [DataRow("ja-JP")]
    [DataRow("lzh")]
    public void AccelerationTitleIsLocalizedExactlyOnce(string language)
    {
        var dictionary = XDocument.Load(Path.Combine(RepositoryRoot(), "PCL.Core", "App", "Localization", "Languages", language + ".xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var matches = dictionary.Root!.Elements()
            .Where(e => (string?)e.Attribute(x + "Key") == "Setup.Launch.Acceleration.Title").ToArray();
        Assert.HasCount(1, matches);
        Assert.IsFalse(string.IsNullOrWhiteSpace(matches[0].Value));
    }
}
