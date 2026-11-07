using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;
using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
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
        foreach (var timeout in new[] { 1, 30 })
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(Path.Combine(_directory, "hashes.db")), Pooling = true, DefaultTimeout = timeout
            }.ToString());
            SqliteConnection.ClearPool(connection);
        }
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

    // 显式重实现接口，保留 SHA1 算法标识并控制两个实例同时处于计算中。
    private sealed class PausedSHA1Provider : SHA1Provider, IHashProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<byte[]> IHashProvider.ComputeHashAsync(Stream input, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Resume.Task;
            return await SHA1Provider.Instance.ComputeHashAsync(input, cancellationToken);
        }
    }

    [TestMethod]
    public async Task ConcurrentBatchInstancesEachQueueTheirOwnResult()
    {
        var database = Path.Combine(_directory, "hashes.db");
        var first = new HashCache(database, batchWrites: true);
        var second = new HashCache(database, batchWrites: true);
        var provider1 = new PausedSHA1Provider();
        var provider2 = new PausedSHA1Provider();
        var task1 = first.GetHashAsync(_file, provider1);
        Task<string>? task2 = null;
        try
        {
            await provider1.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            task2 = second.GetHashAsync(_file, provider2);
            await provider2.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            provider1.Resume.TrySetResult();
            provider2.Resume.TrySetResult();
            await task1;
            if (task2 is not null) await task2;
        }
        second.FlushPendingWrites();
        Assert.AreEqual(await task1, await new HashCache(database).GetCachedSHA1Async(_file));
    }

    [TestMethod]
    public async Task EquivalentPathsShareOneInstanceTask()
    {
        var provider = new PausedSHA1Provider();
        var first = _cache.GetHashAsync(_file, provider);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondProvider = new PausedSHA1Provider();
            var second = _cache.GetHashAsync(Path.Combine(_directory, ".", "example.jar"), secondProvider);
            secondProvider.Resume.TrySetResult();
            Assert.IsFalse(secondProvider.Entered.Task.IsCompleted);
            provider.Resume.TrySetResult();
            Assert.AreEqual(await first, await second);
        }
        finally
        {
            provider.Resume.TrySetResult();
            await first;
        }
    }

    [TestMethod]
    public async Task ExclusiveDatabaseLockUsesConfiguredShortTimeout()
    {
        var database = Path.Combine(_directory, "hashes.db");
        var cache = new HashCache(database, databaseTimeoutSeconds: 1);
        using var blocker = new SqliteConnection($"Data Source={database};Pooling=False");
        blocker.Open();
        // WAL 的闲置连接也会阻止切换日志模式；独占读锁测试先释放连接池。
        SqliteConnection.ClearAllPools();
        using var command = blocker.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE;";
        command.ExecuteNonQuery();
        try
        {
            var elapsed = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<SqliteException>(() => cache.GetCachedSHA1Async(_file));
            Assert.AreEqual(5, error.SqliteErrorCode);
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Lock wait: {elapsed.Elapsed}");
            elapsed.Restart();
            Assert.Throws<SqliteException>(() => new HashCache(database, databaseTimeoutSeconds: 1));
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Initialization wait: {elapsed.Elapsed}");
        }
        finally
        {
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }
    }

    [TestMethod]
    public async Task BatchInvalidationDoesNotDeleteWhileAnotherWriterHoldsLock()
    {
        await _cache.GetSHA1Async(_file);
        var batch = new HashCache(Path.Combine(_directory, "hashes.db"), batchWrites: true, databaseTimeoutSeconds: 1);
        File.WriteAllBytes(_file, new byte[4096]);
        using var blocker = new SqliteConnection($"Data Source={Path.Combine(_directory, "hashes.db")};Pooling=False");
        blocker.Open();
        using (var transaction = blocker.BeginTransaction())
        {
            Assert.AreEqual(SHA1Provider.Instance.ComputeHash(new byte[4096]).ToHexString(), await batch.GetSHA1Async(_file));
            File.Delete(_file);
            await Assert.ThrowsAsync<FileNotFoundException>(() => batch.GetSHA1Async(_file));
        }
        batch.FlushPendingWrites();
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
    }

    [TestMethod]
    public async Task FailedBatchFlushDiscardsSnapshot()
    {
        var batch = new HashCache(Path.Combine(_directory, "hashes.db"), batchWrites: true, databaseTimeoutSeconds: 1);
        await batch.GetSHA1Async(_file);
        using var blocker = new SqliteConnection($"Data Source={Path.Combine(_directory, "hashes.db")};Pooling=False");
        blocker.Open();
        using var transaction = blocker.BeginTransaction();
        Assert.Throws<SqliteException>(() => batch.FlushPendingWrites());
        batch.FlushPendingWrites();
        Assert.IsNull(await _cache.GetCachedSHA1Async(_file));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void DatabaseTimeoutMustBePositive(int timeout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HashCache(Path.Combine(_directory, "hashes.db"), databaseTimeoutSeconds: timeout));
    }

}
