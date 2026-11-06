using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PCL.Core.Test.Hash;

[TestClass]
public class HashCacheLookupTest
{
    private string _directory = null!;
    private string _file = null!;
    private HashCache _cache = null!;

    [TestInitialize]
    public void Initialize()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md")))
            repository = repository.Parent;
        var root = Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ??
                   Path.Combine(repository?.FullName ?? throw new DirectoryNotFoundException("Repository not found"),
                       "artifacts", "test", "mod-first-load");
        _directory = Path.Combine(root, nameof(HashCacheLookupTest), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "example.jar");
        File.WriteAllBytes(_file, new byte[8192]);
        _cache = new HashCache(Path.Combine(_directory, "hashes.db"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "hashes.db")};Pooling=True");
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_directory, true);
    }

    [TestMethod]
    public async Task CacheMissDoesNotOpenFileOrComputeHash()
    {
        using var exclusive = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
    }

    [TestMethod]
    public async Task CacheHitDoesNotOpenFileAndSurvivesNewCacheInstance()
    {
        var expected = await _cache.GetSHA1Async(_file);
        var other = new HashCache(Path.Combine(_directory, "hashes.db"));
        using var exclusive = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.AreEqual(expected, await other.GetCachedSHA1Async(_file));
    }

    [TestMethod]
    public async Task ChangedSizeInvalidatesCacheEvenWithSameTimestamp()
    {
        await _cache.GetSHA1Async(_file);
        await _cache.GetSHA1Async(_file);
        var timestamp = File.GetLastWriteTimeUtc(_file);
        var replacement = new byte[4096];
        File.WriteAllBytes(_file, replacement);
        File.SetLastWriteTimeUtc(_file, timestamp);
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
        Assert.AreEqual(SHA1Provider.Instance.ComputeHash(replacement).ToHexString(), await _cache.GetSHA1Async(_file));
    }

    [TestMethod]
    public async Task ChangedTimestampInvalidatesSameSizeFile()
    {
        await _cache.GetSHA1Async(_file);
        File.SetLastWriteTimeUtc(_file, File.GetLastWriteTimeUtc(_file).AddMinutes(1));
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
    }

    [TestMethod]
    public async Task MissingFileDoesNotReturnOldHash()
    {
        await _cache.GetSHA1Async(_file);
        File.Delete(_file);
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
    }

    [TestMethod]
    public async Task ConcurrentReadsCanRecomputeAfterCachedHits()
    {
        var expected = SHA1Provider.Instance.ComputeHash(new byte[8192]).ToHexString();
        var initial = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => _cache.GetSHA1Async(_file))));
        Assert.IsTrue(initial.All(hash => hash == expected));
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => _cache.GetSHA1Async(_file))));
        var replacement = new byte[4096];
        File.WriteAllBytes(_file, replacement);
        expected = SHA1Provider.Instance.ComputeHash(replacement).ToHexString();
        var updated = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => _cache.GetSHA1Async(_file))));
        Assert.IsTrue(updated.All(hash => hash == expected));
    }

    [TestMethod]
    public async Task OtherAlgorithmDoesNotTriggerSHA1Computation()
    {
        await _cache.GetSHA256Async(_file);
        using var exclusive = new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
    }

    [TestMethod]
    public async Task BatchWritesPersistAllFilesInOneFlush()
    {
        var batch = new HashCache(Path.Combine(_directory, "hashes.db"), batchWrites: true);
        var files = Enumerable.Range(0, 24).Select(i => Path.Combine(_directory, $"library-{i}.jar")).ToArray();
        foreach (var path in files) File.WriteAllBytes(path, new byte[4096]);
        var hashes = await Task.WhenAll(files.Select(batch.GetSHA1Async));
        Assert.IsNull(await _cache.GetCachedSHA1Async(files[0]));
        using (new FileStream(files[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(hashes[0], await batch.GetSHA1Async(files[0]));
        batch.FlushPendingWrites();
        var reopened = new HashCache(Path.Combine(_directory, "hashes.db"));
        for (var i = 0; i < files.Length; i++)
            Assert.AreEqual(hashes[i], await reopened.GetCachedSHA1Async(files[i]));
    }

    [TestMethod]
    public async Task BatchFlushDiscardsModifiedOrDeletedFiles()
    {
        var batch = new HashCache(Path.Combine(_directory, "hashes.db"), batchWrites: true);
        await batch.GetSHA1Async(_file);
        File.SetLastWriteTimeUtc(_file, File.GetLastWriteTimeUtc(_file).AddMinutes(1));
        batch.FlushPendingWrites();
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
        await batch.GetSHA1Async(_file);
        File.Delete(_file);
        batch.FlushPendingWrites();
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
    }

    [TestMethod]
    public async Task BatchReplacementDoesNotRestoreOldAlgorithmHashes()
    {
        var batch = new HashCache(Path.Combine(_directory, "hashes.db"), batchWrites: true);
        await batch.GetSHA1Async(_file);
        await batch.GetSHA256Async(_file);
        batch.FlushPendingWrites();
        File.WriteAllBytes(_file, new byte[4096]);
        var expected = await batch.GetSHA256Async(_file);
        batch.FlushPendingWrites();
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
        Assert.AreEqual(expected, await _cache.GetSHA256Async(_file));
    }

    [TestMethod]
    public async Task QueueComputedHashReusesResultAndRejectsChangedFingerprint()
    {
        var batch = new HashCache(Path.Combine(_directory, "hashes.db"), batchWrites: true);
        var hash = SHA1Provider.Instance.ComputeHash(new byte[8192]).ToHexString();
        var modified = File.GetLastWriteTimeUtc(_file);
        batch.QueueComputedHash(_file, SHA1Provider.Instance, hash, 8192, modified);
        using (new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(hash, await batch.GetSHA1Async(_file));
        batch.FlushPendingWrites();
        Assert.AreEqual(hash, await _cache.GetCachedSHA1Async(_file));
        File.WriteAllBytes(_file, new byte[4096]);
        batch.QueueComputedHash(_file, SHA1Provider.Instance, hash, 8192, modified);
        batch.FlushPendingWrites();
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
    }
}
