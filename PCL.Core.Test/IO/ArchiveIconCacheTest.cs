using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PCL.Core.Test.IO;

[TestClass]
public class ArchiveIconCacheTest
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md")))
            repository = repository.Parent;
        var root = Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ??
                   Path.Combine(repository?.FullName ?? throw new DirectoryNotFoundException("Repository not found"),
                       "artifacts", "test", "mod-list");
        _directory = Path.Combine(root, nameof(ArchiveIconCacheTest), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    private string Extract(string content, string entryName = "icon.png", string sourcePath = "example.jar")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var output = archive.CreateEntry(entryName, CompressionLevel.NoCompression).Open())
            output.Write(Encoding.UTF8.GetBytes(content));
        stream.Position = 0;
        using var input = new ZipArchive(stream, ZipArchiveMode.Read);
        return ArchiveIconCache.GetOrExtract(input.GetEntry(entryName)!, sourcePath, _directory);
    }

    [TestMethod]
    public void UnchangedIconDoesNotRewriteCache()
    {
        var path = Extract("original icon");
        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.AreEqual(path, Extract("original icon"));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.AreEqual("original icon", File.ReadAllText(path));
    }

    [TestMethod]
    public void SameSizeReplacementUsesNewCacheEntry()
    {
        var oldPath = Extract("old icon");
        var newPath = Extract("new icon");
        Assert.AreNotEqual(oldPath, newPath);
        Assert.AreEqual("new icon", File.ReadAllText(newPath));
    }

    [TestMethod]
    public void MissingOrTruncatedCacheIsRecreated()
    {
        var path = Extract("complete icon");
        File.WriteAllText(path, "partial");
        Assert.AreEqual(path, Extract("complete icon"));
        Assert.AreEqual("complete icon", File.ReadAllText(path));
        File.Delete(path);
        Assert.AreEqual(path, Extract("complete icon"));
        Assert.AreEqual("complete icon", File.ReadAllText(path));
    }

    [TestMethod]
    public void DifferentArchiveAndEntryPathsHaveSeparateCacheEntries()
    {
        var path = Extract("icon");
        Assert.AreNotEqual(path, Extract("icon", "pack.png"));
        Assert.AreNotEqual(path, Extract("icon", sourcePath: "example.jar!/nested.jar"));
    }

    [TestMethod]
    public async Task ConcurrentExtractionPublishesCompleteIcon()
    {
        var content = new string('x', 128 * 1024);
        var paths = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Extract(content))));
        Assert.AreEqual(1, paths.Distinct().Count());
        Assert.AreEqual(content, File.ReadAllText(paths[0]));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
    }
}
