using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class ModLoadingProgressDriverTest
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcl-moon-mod-driver-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteModJar(string path, string manifestName, string jsonContent)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(manifestName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(jsonContent);
    }

    [TestMethod]
    public void ScanEntrypointsParsesFabricQuiltDeduplicatesAndSorts()
    {
        var root = CreateTempDir();
        try
        {
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(mods);

            // Fabric 模组：含字符串、对象(value/adapter)、嵌套数组与自定义阶段
            WriteModJar(Path.Combine(mods, "01-alpha.jar"), "fabric.mod.json", """
                {
                  "id": "alpha",
                  "name": "Alpha Mod",
                  "version": "1.2.3",
                  "entrypoints": {
                    "client": [
                      "com.example.AlphaClient",
                      { "value": "com.example.AlphaExtra", "adapter": "kotlin" }
                    ],
                    "main": [
                      [{ "adapter": "groovy-adapter" }, {}]
                    ],
                    "custom_stage": [ "com.example.CustomEntry" ]
                  }
                }
                """);

            // 同 ID 重复模组（按排序只保留首个 01-alpha.jar）
            WriteModJar(Path.Combine(mods, "02-alpha-dup.jar"), "fabric.mod.json", """
                {
                  "id": "alpha",
                  "name": "Duplicate Alpha",
                  "version": "9.9.9",
                  "entrypoints": { "client": [ "dup.Client" ] }
                }
                """);

            // Quilt 模组（quilt_loader 结构，name 缺省回退 id）
            WriteModJar(Path.Combine(mods, "03-quilt.jar"), "quilt.mod.json", """
                {
                  "quilt_loader": {
                    "id": "quilt-demo",
                    "version": "0.5.0",
                    "metadata": { "name": "Quilt 模组" },
                    "entrypoints": {
                      "init": [ "org.example.QuiltInit" ],
                      "pre_launch": [ "org.example.QuiltPre" ]
                    }
                  }
                }
                """);

            // 损坏的 JAR 文件应安全跳过，不中断其余扫描
            File.WriteAllText(Path.Combine(mods, "04-broken.jar"), "not-a-zip-archive");

            var phases = ModLoadingProgressDriver.ScanEntrypoints(mods, out var modCount, out var fallbackTarget);

            Assert.AreEqual(2, modCount);
            Assert.AreEqual("Alpha Mod 1.2.3", fallbackTarget);

            // 验证阶段顺序：pre_launch -> main -> init -> client -> custom_stage
            Assert.AreEqual(5, phases.Count);
            Assert.AreEqual("pre_launch", phases[0].Type);
            Assert.AreEqual("PreLaunchEntrypoint", phases[0].DisplayType);
            Assert.AreEqual("main", phases[1].Type);
            Assert.AreEqual("ModInitializer", phases[1].DisplayType);
            Assert.AreEqual("init", phases[2].Type);
            Assert.AreEqual("ModInitializer", phases[2].DisplayType);
            Assert.AreEqual("client", phases[3].Type);
            Assert.AreEqual("ClientModInitializer", phases[3].DisplayType);
            Assert.AreEqual("custom_stage", phases[4].Type);
            Assert.AreEqual("CustomStageEntrypoint", phases[4].DisplayType);

            Assert.AreEqual("Quilt 模组", phases[0].Entries[0].ModName);
            Assert.AreEqual(2, phases[1].Entries.Count);
            Assert.AreEqual("groovy-adapter", phases[1].Entries[0].Entrypoint);
            Assert.AreEqual("入口点", phases[1].Entries[1].Entrypoint);
            Assert.AreEqual("Quilt 模组", phases[2].Entries[0].ModName);
            Assert.AreEqual("org.example.QuiltInit", phases[2].Entries[0].Entrypoint);
            Assert.AreEqual(2, phases[3].Entries.Count);
            Assert.AreEqual("com.example.AlphaExtra", phases[3].Entries[1].Entrypoint);
            Assert.AreEqual("com.example.CustomEntry", phases[4].Entries[0].Entrypoint);

            Assert.AreEqual(
                "Loading 'client' (ClientModInitializer) — 1/2 — Alpha Mod",
                ModLoadingProgressDriver.FormatLoadingStage("client", "ClientModInitializer", 1, 2, "Alpha Mod"));
            Assert.AreEqual(
                "Loading 'client' (ClientModInitializer) — 0/2",
                ModLoadingProgressDriver.FormatLoadingStage("client", "ClientModInitializer", 0, 2, null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DriverPushesInProcessProgressAndCompletesWithWindow()
    {
        var root = CreateTempDir();
        try
        {
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(mods);
            WriteModJar(Path.Combine(mods, "pack.jar"), "fabric.mod.json", """
                {
                  "id": "demo-pack",
                  "name": "示例整合包",
                  "version": "1.0.0",
                  "entrypoints": {
                    "main": [ "a.Main" ],
                    "client": [ "a.Client1", "a.Client2" ]
                  }
                }
                """);

            var state = new LaunchProgressState();
            state.GameStarted();

            using var driver = new ModLoadingProgressDriver(state, root, "测试实例 1.21.1", stepDelayMsOverride: 5);
            await driver.Start();

            Assert.IsTrue(state.TelemetryConnected);
            Assert.IsTrue(state.ModLoadingComplete);
            Assert.IsFalse(state.IsTerminal);
            Assert.AreEqual("测试实例 1.21.1", state.Snapshot.Target);

            state.WindowAppeared();
            Assert.IsTrue(state.IsTerminal);
            Assert.AreEqual(LaunchProgressPhase.Ready, state.Snapshot.Phase);
            Assert.AreEqual(1d, state.Snapshot.Progress);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DriverHandlesMissingModsDirAndDisposeCancellation()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "pcl-moon-missing-" + Guid.NewGuid().ToString("N"));
        var emptyState = new LaunchProgressState();
        emptyState.GameStarted();

        using (var emptyDriver = new ModLoadingProgressDriver(emptyState, missingRoot, stepDelayMsOverride: 0))
            await emptyDriver.Start();

        Assert.IsTrue(emptyState.TelemetryConnected);
        Assert.IsTrue(emptyState.ModLoadingComplete);
        emptyState.WindowAppeared();
        Assert.AreEqual(LaunchProgressPhase.Ready, emptyState.Snapshot.Phase);

        var slowRoot = CreateTempDir();
        try
        {
            var mods = Path.Combine(slowRoot, "mods");
            Directory.CreateDirectory(mods);
            var listJson = new List<string>();
            for (var i = 0; i < 20; i++) listJson.Add($"\"c.M{i}\"");
            WriteModJar(Path.Combine(mods, "slow.jar"), "fabric.mod.json",
                "{\"id\":\"slow\",\"name\":\"Slow\",\"entrypoints\":{\"client\":[" + string.Join(",", listJson) + "]}}");

            var cancelState = new LaunchProgressState();
            cancelState.GameStarted();
            var driver = new ModLoadingProgressDriver(cancelState, slowRoot, stepDelayMsOverride: 100);
            var task = driver.Start();
            await Task.Delay(30);
            driver.Dispose();
            await task;

            Assert.IsFalse(cancelState.ModLoadingComplete);
            Assert.AreNotEqual(LaunchProgressPhase.Failed, cancelState.Snapshot.Phase);
        }
        finally { Directory.Delete(slowRoot, recursive: true); }
    }
}
