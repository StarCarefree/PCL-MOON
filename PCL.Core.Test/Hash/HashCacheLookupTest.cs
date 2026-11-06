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
}
