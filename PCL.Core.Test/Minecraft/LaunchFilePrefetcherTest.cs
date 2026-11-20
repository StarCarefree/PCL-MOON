using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class LaunchFilePrefetcherTest
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcl-prefetch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    [DataRow(0UL, 0L)]
    [DataRow(2UL * 1024 * 1024 * 1024 - 1, 0L)]
    [DataRow(2UL * 1024 * 1024 * 1024, 512L * 1024 * 1024)]
    [DataRow(8UL * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024)]
    [DataRow(32UL * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024)]
    [DataRow(64UL * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024)]
    public void ComputeBudgetBounds(ulong available, long expected)
    {
        Assert.AreEqual(expected, LaunchFilePrefetcher.ComputeBudget(available));
    }

    [TestMethod]
    public async Task ReadsOnlyDistinctExistingJars()
    {
        var dir = NewDir();
        try
        {
            var jarA = Path.Combine(dir, "a.jar");
            var jarB = Path.Combine(dir, "B.JAR");
            var notJar = Path.Combine(dir, "c.txt");
            File.WriteAllBytes(jarA, new byte[3 * 1024 * 1024 + 7]);
            File.WriteAllBytes(jarB, new byte[1024]);
            File.WriteAllBytes(notJar, new byte[999]);
            using var prefetcher = new LaunchFilePrefetcher(64L * 1024 * 1024, TimeSpan.FromSeconds(2), null);
            prefetcher.Enqueue(new[]
            {
                jarA, jarA.ToUpperInvariant(), jarB, notJar,
                Path.Combine(dir, "missing.jar"), jarA,
            });
            await prefetcher.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(3 * 1024 * 1024 + 7 + 1024, prefetcher.BytesRead);
            Assert.AreEqual(2, prefetcher.FilesRead);
            Assert.AreEqual(0, prefetcher.FilesSkipped);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task BudgetTruncatesReading()
    {
        var dir = NewDir();
        try
        {
            var jar = Path.Combine(dir, "big.jar");
            File.WriteAllBytes(jar, new byte[3 * 1024 * 1024]);
            const long budget = 1024 * 1024;
            using var prefetcher = new LaunchFilePrefetcher(budget, TimeSpan.FromSeconds(2), null);
            prefetcher.Enqueue(new[] { jar });
            await prefetcher.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(budget, prefetcher.BytesRead);
            Assert.AreEqual(0, prefetcher.FilesRead); // 未读到 EOF 不计入完成数
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task DisposeStopsWorker()
    {
        var dir = NewDir();
        try
        {
            var jar = Path.Combine(dir, "a.jar");
            File.WriteAllBytes(jar, new byte[256 * 1024]);
            var prefetcher = new LaunchFilePrefetcher(64L * 1024 * 1024, TimeSpan.FromMinutes(5), null);
            prefetcher.Enqueue(new[] { jar });
            prefetcher.Dispose();
            await prefetcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            prefetcher.Enqueue(new[] { jar }); // Dispose 后不再接收
            Assert.IsTrue(prefetcher.Completion.IsCompleted);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task DisposeAfterDrainLogsDone()
    {
        var dir = NewDir();
        try
        {
            var jar = Path.Combine(dir, "a.jar");
            File.WriteAllBytes(jar, new byte[64 * 1024]);
            string? message = null;
            var prefetcher = new LaunchFilePrefetcher(64L * 1024 * 1024, TimeSpan.FromMinutes(5),
                m => message = m);
            prefetcher.Enqueue(new[] { jar });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (prefetcher.FilesRead < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.AreEqual(1, prefetcher.FilesRead);
            prefetcher.Dispose();
            await prefetcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(message);
            StringAssert.Contains(message, "完成");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task DisposeBeforeAnyEnqueueCompletes()
    {
        using var prefetcher = new LaunchFilePrefetcher(1024, TimeSpan.FromMinutes(5), null);
        prefetcher.Dispose();
        await prefetcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task EnqueueDirectoryReadsDistinctJarsHandlesMissingAndRespectsBudget()
    {
        var dir = NewDir();
        try
        {
            var jar1 = Path.Combine(dir, "mod1.jar");
            var jar2 = Path.Combine(dir, "mod2.jar");
            var txt = Path.Combine(dir, "ignore.txt");
            File.WriteAllBytes(jar1, new byte[512 * 1024]);
            File.WriteAllBytes(jar2, new byte[512 * 1024 + 9]);
            File.WriteAllBytes(txt, new byte[4096]);

            using var prefetcher = new LaunchFilePrefetcher(64L * 1024 * 1024, TimeSpan.FromSeconds(5), null);
            prefetcher.EnqueueDirectory(Path.Combine(dir, "does-not-exist"));
            prefetcher.Enqueue(new[] { jar1 });
            prefetcher.EnqueueDirectory(dir, "*.jar");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (prefetcher.FilesRead < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.AreEqual(2, prefetcher.FilesRead);
            Assert.AreEqual(512 * 1024 + 512 * 1024 + 9, prefetcher.BytesRead);

            prefetcher.Dispose();
            await prefetcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { Directory.Delete(dir, true); }
    }
}
