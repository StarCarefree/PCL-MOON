using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class GameDashboardLayoutTest
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(),
            "game-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string FilePath(string name = "layout.json") => Path.Combine(_root, name);

    [TestMethod]
    public void CreateDefault_HasHeroGreetingLibrary()
    {
        var layout = GameDashboardLayout.CreateDefault();
        Assert.AreEqual(2, layout.Columns);
        Assert.AreEqual(3, layout.Widgets.Count);
        Assert.AreEqual("hero", layout.Widgets[0].Kind);
        Assert.IsTrue(layout.Widgets[0].Wide);
        Assert.AreEqual("greeting", layout.Widgets[1].Kind);
        Assert.IsFalse(layout.Widgets[1].Wide);
        Assert.AreEqual("library", layout.Widgets[2].Kind);
        Assert.IsFalse(layout.Widgets[2].Wide);
        Assert.AreEqual(3, layout.Widgets.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var widget in layout.Widgets)
            Assert.AreEqual(32, widget.Id.Length);
    }

    [TestMethod]
    public void SaveAndLoad_RoundtripPreservesLayout()
    {
        var original = GameDashboardLayout.CreateDefault();
        original.Columns = 1;
        original.Widgets[1].Title = "你好";
        original.Widgets.Add(new GameDashboardWidget
        {
            Kind = "calendar",
            Title = "日历",
            Wide = true,
            Target = "https://example.test/cal",
            Text = "note-body"
        });
        original.Widgets.Add(new GameDashboardWidget { Kind = "stats" });
        original.Widgets.Add(new GameDashboardWidget { Kind = "shortcut", Target = "page://launch" });
        original.Widgets.Add(new GameDashboardWidget { Kind = "note", Text = "hello" });

        var path = FilePath();
        original.Save(path);
        var loaded = GameDashboardLayout.Load(path);

        Assert.AreEqual(1, loaded.Columns);
        Assert.AreEqual(original.Widgets.Count, loaded.Widgets.Count);
        for (var i = 0; i < original.Widgets.Count; i++)
        {
            Assert.AreEqual(original.Widgets[i].Id, loaded.Widgets[i].Id);
            Assert.AreEqual(original.Widgets[i].Kind, loaded.Widgets[i].Kind);
            Assert.AreEqual(original.Widgets[i].Title, loaded.Widgets[i].Title);
            Assert.AreEqual(original.Widgets[i].Wide, loaded.Widgets[i].Wide);
            Assert.AreEqual(original.Widgets[i].Target, loaded.Widgets[i].Target);
            Assert.AreEqual(original.Widgets[i].Text, loaded.Widgets[i].Text);
        }
    }

    [TestMethod]
    public void Clone_IsDeepCopy()
    {
        var original = GameDashboardLayout.CreateDefault();
        original.Widgets[0].Title = "keep";
        var clone = original.Clone();
        Assert.AreNotSame(original, clone);
        Assert.AreNotSame(original.Widgets, clone.Widgets);
        Assert.AreNotSame(original.Widgets[0], clone.Widgets[0]);
        Assert.AreEqual("keep", clone.Widgets[0].Title);
        Assert.AreEqual(original.Widgets[0].Id, clone.Widgets[0].Id);

        clone.Columns = 1;
        clone.Widgets[0].Title = "changed";
        clone.Widgets.Add(new GameDashboardWidget { Kind = "note" });
        original.Widgets[1].Title = "original-only";

        Assert.AreEqual(2, original.Columns);
        Assert.AreEqual("keep", original.Widgets[0].Title);
        Assert.AreEqual(3, original.Widgets.Count);
        Assert.AreEqual("", clone.Widgets[1].Title);
        Assert.AreEqual(4, clone.Widgets.Count);
    }

    [TestMethod]
    public void Load_MissingFileReturnsDefault()
    {
        var loaded = GameDashboardLayout.Load(FilePath("missing.json"));
        AssertDefault(loaded);
    }

    [TestMethod]
    public void Load_EmptyWidgetListIsPreserved()
    {
        var path = FilePath();
        new GameDashboardLayout { Columns = 1, Widgets = [] }.Save(path);
        var loaded = GameDashboardLayout.Load(path);
        Assert.AreEqual(1, loaded.Columns);
        Assert.AreEqual(0, loaded.Widgets.Count);
    }

    [TestMethod]
    public void Load_DamagedJsonReturnsDefault()
    {
        var path = FilePath();
        File.WriteAllText(path, "{not-json");
        AssertDefault(GameDashboardLayout.Load(path));
    }

    [TestMethod]
    public void Load_OversizedFileReturnsDefault()
    {
        var path = FilePath();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1)));
        AssertDefault(GameDashboardLayout.Load(path));
    }

    [TestMethod]
    public void Load_AbnormalDataIsNormalized()
    {
        var path = FilePath();
        var title = new string('T', 120);
        var text = new string('B', 5000);
        var target = new string('P', 3000);
        var widgets = new StringBuilder();
        for (var i = 0; i < 45; i++)
        {
            if (i > 0) widgets.Append(',');
            widgets.Append($"{{\"id\":\"w{i}\",\"kind\":\"note\",\"title\":\"{title}\",\"text\":\"{text}\",\"target\":\"{target}\"}}");
        }
        File.WriteAllText(path, $"{{\"columns\":99,\"widgets\":[{widgets}]}}");

        var loaded = GameDashboardLayout.Load(path);
        Assert.AreEqual(2, loaded.Columns);
        Assert.AreEqual(40, loaded.Widgets.Count);
        Assert.AreEqual(new string('T', 80), loaded.Widgets[0].Title);
        Assert.AreEqual(new string('B', 4000), loaded.Widgets[0].Text);
        Assert.AreEqual(new string('P', 2048), loaded.Widgets[0].Target);
    }

    [TestMethod]
    public void Load_UnknownKindDroppedAndDuplicateOrEmptyIdsRegenerated()
    {
        var path = FilePath();
        File.WriteAllText(path, """
            {
              "columns": 2,
              "widgets": [
                { "id": "same", "kind": "unknown" },
                { "id": "same", "kind": "note", "title": "first" },
                { "id": "same", "kind": "stats", "title": "second" },
                { "id": "", "kind": "shortcut", "title": "empty" },
                { "kind": "calendar", "title": "missing" }
              ]
            }
            """);

        var loaded = GameDashboardLayout.Load(path);
        Assert.AreEqual(4, loaded.Widgets.Count);
        Assert.AreEqual("note", loaded.Widgets[0].Kind);
        Assert.AreEqual("same", loaded.Widgets[0].Id);
        Assert.AreEqual("stats", loaded.Widgets[1].Kind);
        Assert.AreNotEqual("same", loaded.Widgets[1].Id);
        Assert.AreEqual(32, loaded.Widgets[1].Id.Length);
        Assert.AreEqual(32, loaded.Widgets[2].Id.Length);
        Assert.AreEqual(32, loaded.Widgets[3].Id.Length);
        Assert.AreEqual(4, loaded.Widgets.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.IsFalse(loaded.Widgets.Any(w => w.Kind == "unknown"));
    }

    [TestMethod]
    public void Load_ColumnsAreClampedToOneOrTwo()
    {
        var path = FilePath();
        File.WriteAllText(path, """{"columns":1,"widgets":[{"kind":"note"}]}""");
        Assert.AreEqual(1, GameDashboardLayout.Load(path).Columns);

        File.WriteAllText(path, """{"columns":0,"widgets":[{"kind":"note"}]}""");
        Assert.AreEqual(1, GameDashboardLayout.Load(path).Columns);

        File.WriteAllText(path, """{"columns":8,"widgets":[{"kind":"note"}]}""");
        Assert.AreEqual(2, GameDashboardLayout.Load(path).Columns);
    }

    [TestMethod]
    public void Save_FailureDoesNotOverwriteExistingFile()
    {
        var path = FilePath();
        var original = GameDashboardLayout.CreateDefault();
        original.Widgets[0].Title = "original";
        original.Save(path);
        var expected = File.ReadAllText(path);

        var next = GameDashboardLayout.CreateDefault();
        next.Widgets[0].Title = "should-not-write";
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var threw = false;
            try { next.Save(path); }
            catch (Exception) { threw = true; }
            Assert.IsTrue(threw, "Save must surface replace/move failures");
        }

        Assert.AreEqual(expected, File.ReadAllText(path));
        Assert.AreEqual("original", GameDashboardLayout.Load(path).Widgets[0].Title);
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length);
    }

    [TestMethod]
    public void Load_LegacyRecentAndLibraryMergedIntoLibrary()
    {
        var path = FilePath();
        File.WriteAllText(path, """
            {
              "columns": 2,
              "widgets": [
                { "id": "h1", "kind": "hero", "wide": true },
                { "id": "g1", "kind": "greeting", "wide": false },
                { "id": "r1", "kind": "recent", "wide": false },
                { "id": "l1", "kind": "library", "wide": true, "title": "常用游戏库" }
              ]
            }
            """);

        var loaded = GameDashboardLayout.Load(path);
        CollectionAssert.AreEqual(
            new[] { "hero", "greeting", "library" },
            loaded.Widgets.Select(w => w.Kind).ToArray());
        Assert.IsFalse(loaded.Widgets[2].Wide);
        Assert.AreEqual("常用游戏库", loaded.Widgets[2].Title);

        var recentOnlyPath = FilePath("recent-only.json");
        File.WriteAllText(recentOnlyPath, """
            {
              "columns": 2,
              "widgets": [
                { "id": "g1", "kind": "greeting" },
                { "id": "r1", "kind": "recent", "title": "常玩" }
              ]
            }
            """);
        var recentOnly = GameDashboardLayout.Load(recentOnlyPath);
        CollectionAssert.AreEqual(new[] { "greeting", "library" }, recentOnly.Widgets.Select(w => w.Kind).ToArray());
        Assert.AreEqual("常玩", recentOnly.Widgets[1].Title);

        var libraryOnlyPath = FilePath("library-only.json");
        File.WriteAllText(libraryOnlyPath, """
            {
              "columns": 2,
              "widgets": [
                { "id": "l1", "kind": "library", "wide": true }
              ]
            }
            """);
        var libraryOnly = GameDashboardLayout.Load(libraryOnlyPath);
        Assert.AreEqual(1, libraryOnly.Widgets.Count);
        Assert.AreEqual("library", libraryOnly.Widgets[0].Kind);
        Assert.IsTrue(libraryOnly.Widgets[0].Wide);
    }

    private static void AssertDefault(GameDashboardLayout layout)
    {
        Assert.AreEqual(2, layout.Columns);
        Assert.AreEqual(3, layout.Widgets.Count);
        CollectionAssert.AreEqual(
            new[] { "hero", "greeting", "library" },
            layout.Widgets.Select(w => w.Kind).ToArray());
        Assert.IsTrue(layout.Widgets[0].Wide);
        Assert.IsFalse(layout.Widgets[1].Wide);
        Assert.IsFalse(layout.Widgets[2].Wide);
    }
}
