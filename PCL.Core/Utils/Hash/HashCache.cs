using Microsoft.Data.Sqlite;
using PCL.Core.Utils.Exts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PCL.Core.Utils.Hash;

public class HashCache
{
    private readonly string _dbPath;
    private readonly bool _batchWrites;
    private readonly object _flushLock = new();
    private readonly ConcurrentDictionary<(string Path, string Algorithm), PendingHash> _pendingWrites = new();
    private sealed record PendingHash(long Size, string LastWrite, string Hash);

    public HashCache(string dbPath, bool batchWrites = false)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _batchWrites = batchWrites;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        _Initialize();
    }

    private void _Initialize()
    {
        using var connection = _CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS HashCache (
                FilePath TEXT NOT NULL PRIMARY KEY,
                FileSize INTEGER NOT NULL,
                LastWriteTime TEXT NOT NULL,
                MD5 TEXT NULL,
                SHA1 TEXT NULL,
                SHA256 TEXT NULL,
                SHA512 TEXT NULL,
                MurmurHash2 TEXT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        using var setCmd = connection.CreateCommand();
        setCmd.CommandText = "PRAGMA journal_mode=WAL";
        setCmd.ExecuteNonQuery();
    }

    private SqliteConnection _CreateConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=True");
        connection.Open();
        return connection;
    }

    public Task<string> GetMD5Async(string filePath) =>
        _GetHashWithPending(filePath, MD5Provider.Instance, "MD5");

    public Task<string> GetSHA1Async(string filePath) =>
        _GetHashWithPending(filePath, SHA1Provider.Instance, "SHA1");

    /// <summary>只查询仍有效的 SHA1 缓存，未命中返回 null，不读取文件内容或计算哈希。</summary>
    public async Task<string?> GetCachedSHA1Async(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists) return null;
        var fileSize = fileInfo.Length;
        var lastWrite = fileInfo.LastWriteTimeUtc.ToString("O");
        var cached = await _FindCacheEntryAsync(fullPath).ConfigureAwait(false);
        return cached is not null && cached.FileSize == fileSize && cached.LastWriteTime == lastWrite
            ? cached.SHA1
            : null;
    }

    public Task<string> GetSHA256Async(string filePath) =>
        _GetHashWithPending(filePath, SHA256Provider.Instance, "SHA256");

    public Task<string> GetSHA512Async(string filePath) =>
        _GetHashWithPending(filePath, SHA512Provider.Instance, "SHA512");

    public Task<string> GetMurmurHash2Async(string filePath) =>
        _GetHashWithPending(filePath, MurmurHash2Provider.Instance, "MurmurHash2");

    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> _FileHashComputePending = new();

    public Task<string> GetHashAsync(string filePath, IHashProvider provider) =>
        _GetHashWithPending(filePath, provider, _AlgorithmName(provider));

    private static string _AlgorithmName(IHashProvider provider) => provider switch
        {
            MD5Provider => "MD5",
            SHA1Provider => "SHA1",
            SHA256Provider => "SHA256",
            SHA512Provider => "SHA512",
            MurmurHash2Provider => "MurmurHash2",
            _ => throw new ArgumentException($"不支持的哈希算法: {provider.GetType().Name}")
        };

    /// <summary>批量模式接收已计算的哈希；指纹不再匹配时丢弃，避免后台重读文件。</summary>
    public void QueueComputedHash(string filePath, IHashProvider provider, string hash, long size, DateTime modifiedUtc)
    {
        if (!_batchWrites) throw new InvalidOperationException("Only batch caches can queue computed hashes.");
        if (string.IsNullOrEmpty(hash)) return;
        var fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length != size || info.LastWriteTimeUtc != modifiedUtc) return;
        _pendingWrites[(fullPath, _AlgorithmName(provider))] = new PendingHash(size, modifiedUtc.ToString("O"), hash);
    }

    private async Task<string> _GetHashWithPending(string filePath, IHashProvider provider, string algoName)
    {
        var computeKey = $"{Path.GetFullPath(_dbPath)}:{filePath}:{algoName}:{_batchWrites}";
        // GetOrAdd 的工厂可能并发执行多次，用 Lazy 避免同一文件被重复计算。
        var pending = _FileHashComputePending.GetOrAdd(computeKey,
            _ => new Lazy<Task<string>>(() => _GetHashAsync(filePath, provider, algoName)));
        try
        {
            return await pending.Value.ConfigureAwait(false);
        }
        finally
        {
            // 同步命中也在入表后清理；只移除本次任务，避免误删后续计算。
            _FileHashComputePending.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(computeKey, pending));
        }
    }

    private async Task<string> _GetHashAsync(string filePath, IHashProvider provider, string algoName)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);

        try
        {
            var fileInfo = new FileInfo(fullPath);
            var fileSize = fileInfo.Length;
            var lastWrite = fileInfo.LastWriteTimeUtc.ToString("O");

            if (_pendingWrites.TryGetValue((fullPath, algoName), out var pending) &&
                pending.Size == fileSize && pending.LastWrite == lastWrite)
                return pending.Hash;
            var cached = await _FindCacheEntryAsync(fullPath).ConfigureAwait(false);

            if (cached != null)
            {
                if (cached.FileSize == fileSize && cached.LastWriteTime == lastWrite)
                {
                    var hash = _GetHashFromEntry(cached, algoName);
                    if (hash != null)
                        return hash;

                    var computedHash = await _ComputeHashAsync(fullPath, provider).ConfigureAwait(false);
                    await _InsertOrUpdateHashAsync(fullPath, fileSize, lastWrite, algoName, computedHash).ConfigureAwait(false);
                    return computedHash;
                }
                else
                {
                    await _DeleteCacheEntryAsync(fullPath).ConfigureAwait(false);
                }
            }

            var computed = await _ComputeHashAsync(fullPath, provider).ConfigureAwait(false);
            await _InsertOrUpdateHashAsync(fullPath, fileSize, lastWrite, algoName, computed).ConfigureAwait(false);
            return computed;
        }
        catch (FileNotFoundException)
        {
            await _DeleteCacheEntryAsync(fullPath).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> _ComputeHashAsync(string fullPath, IHashProvider provider)
    {
        using FileStream fs = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (await provider.ComputeHashAsync(fs).ConfigureAwait(false)).ToHexString();
    }

    private async Task<CacheEntry?> _FindCacheEntryAsync(string fullPath)
    {
        using var conn = _CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FilePath, FileSize, LastWriteTime, MD5, SHA1, SHA256, SHA512, MurmurHash2 FROM HashCache WHERE FilePath = @FilePath";
        cmd.Parameters.AddWithValue("@FilePath", fullPath);

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            return null;

        return new CacheEntry
        {
            FilePath = reader.GetString(0),
            FileSize = reader.GetInt64(1),
            LastWriteTime = reader.GetString(2),
            MD5 = reader.IsDBNull(3) ? null : reader.GetString(3),
            SHA1 = reader.IsDBNull(4) ? null : reader.GetString(4),
            SHA256 = reader.IsDBNull(5) ? null : reader.GetString(5),
            SHA512 = reader.IsDBNull(6) ? null : reader.GetString(6),
            MurmurHash2 = reader.IsDBNull(7) ? null : reader.GetString(7)
        };
    }

    private static string? _GetHashFromEntry(CacheEntry entry, string algoName) => algoName switch
    {
        "MD5" => entry.MD5,
        "SHA1" => entry.SHA1,
        "SHA256" => entry.SHA256,
        "SHA512" => entry.SHA512,
        "MurmurHash2" => entry.MurmurHash2,
        _ => null
    };

    private async Task _InsertOrUpdateHashAsync(string fullPath, long fileSize, string lastWrite, string algoName, string hash,
        SqliteConnection? batchConnection = null, SqliteTransaction? transaction = null)
    {
        if (hash.IsNullOrWhiteSpace()) return;
        if (_batchWrites && batchConnection is null)
        {
            _pendingWrites[(fullPath, algoName)] = new PendingHash(fileSize, lastWrite, hash);
            return;
        }
        using var ownedConnection = batchConnection is null ? _CreateConnection() : null;
        using var cmd = (batchConnection ?? ownedConnection!).CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO HashCache (FilePath, FileSize, LastWriteTime, MD5, SHA1, SHA256, SHA512, MurmurHash2)
            VALUES (@FilePath, @FileSize, @LastWriteTime, @MD5, @SHA1, @SHA256, @SHA512, @MurmurHash2)
            ON CONFLICT(FilePath) DO UPDATE SET
                FileSize = excluded.FileSize,
                LastWriteTime = excluded.LastWriteTime,
                MD5 = CASE WHEN HashCache.FileSize = excluded.FileSize AND HashCache.LastWriteTime = excluded.LastWriteTime THEN COALESCE(excluded.MD5, HashCache.MD5) ELSE excluded.MD5 END,
                SHA1 = CASE WHEN HashCache.FileSize = excluded.FileSize AND HashCache.LastWriteTime = excluded.LastWriteTime THEN COALESCE(excluded.SHA1, HashCache.SHA1) ELSE excluded.SHA1 END,
                SHA256 = CASE WHEN HashCache.FileSize = excluded.FileSize AND HashCache.LastWriteTime = excluded.LastWriteTime THEN COALESCE(excluded.SHA256, HashCache.SHA256) ELSE excluded.SHA256 END,
                SHA512 = CASE WHEN HashCache.FileSize = excluded.FileSize AND HashCache.LastWriteTime = excluded.LastWriteTime THEN COALESCE(excluded.SHA512, HashCache.SHA512) ELSE excluded.SHA512 END,
                MurmurHash2 = CASE WHEN HashCache.FileSize = excluded.FileSize AND HashCache.LastWriteTime = excluded.LastWriteTime THEN COALESCE(excluded.MurmurHash2, HashCache.MurmurHash2) ELSE excluded.MurmurHash2 END
            """;
        cmd.Parameters.AddWithValue("@FilePath", fullPath);
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        cmd.Parameters.AddWithValue("@LastWriteTime", lastWrite);
        cmd.Parameters.AddWithValue("@MD5", algoName == "MD5" ? hash : DBNull.Value);
        cmd.Parameters.AddWithValue("@SHA1", algoName == "SHA1" ? hash : DBNull.Value);
        cmd.Parameters.AddWithValue("@SHA256", algoName == "SHA256" ? hash : DBNull.Value);
        cmd.Parameters.AddWithValue("@SHA512", algoName == "SHA512" ? hash : DBNull.Value);
        cmd.Parameters.AddWithValue("@MurmurHash2", algoName == "MurmurHash2" ? hash : DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>将批量模式计算的哈希放在一次事务中落盘，避免每个支持库单独同步磁盘。</summary>
    public void FlushPendingWrites()
    {
        lock (_flushLock)
        {
            if (_pendingWrites.IsEmpty) return;
            var snapshot = _pendingWrites.ToArray();
            using var connection = _CreateConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var entry in snapshot)
            {
                // 文件在校验后被覆盖或删除时，不写入已过时的结果。
                var info = new FileInfo(entry.Key.Path);
                if (!info.Exists || info.Length != entry.Value.Size ||
                    info.LastWriteTimeUtc.ToString("O") != entry.Value.LastWrite) continue;
                _InsertOrUpdateHashAsync(entry.Key.Path, entry.Value.Size, entry.Value.LastWrite,
                    entry.Key.Algorithm, entry.Value.Hash, connection, transaction).GetAwaiter().GetResult();
            }
            transaction.Commit();
            foreach (var entry in snapshot) _pendingWrites.TryRemove(entry);
        }
    }

    private async Task _DeleteCacheEntryAsync(string fullPath)
    {
        using var conn = _CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM HashCache WHERE FilePath = @FilePath";
        cmd.Parameters.AddWithValue("@FilePath", fullPath);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed class CacheEntry
    {
        public string FilePath { get; init; } = "";
        public long FileSize { get; init; }
        public string LastWriteTime { get; init; } = "";
        public string? MD5 { get; init; }
        public string? SHA1 { get; init; }
        public string? SHA256 { get; init; }
        public string? SHA512 { get; init; }
        public string? MurmurHash2 { get; init; }
    }
}
