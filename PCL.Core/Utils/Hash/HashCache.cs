using Microsoft.Data.Sqlite;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Native;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Utils.Hash;

public class HashCache
{
    private readonly string _connectionString;
    private readonly bool _batchWrites;
    private readonly object _flushLock = new();
    private readonly ConcurrentDictionary<(string Path, string Algorithm), PendingHash> _pendingWrites = new();
    private sealed record PendingHash(long Size, string LastWrite, string Hash);

    public HashCache(string dbPath, bool batchWrites = false, int databaseTimeoutSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        if (databaseTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(databaseTimeoutSeconds));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Pooling = true,
            DefaultTimeout = databaseTimeoutSeconds
        }.ToString();
        _batchWrites = batchWrites;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
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
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
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

    /// <summary>
    /// 批量查询仍有效的 SHA1 缓存（含待刷新写队列与分块 SQL <c>IN</c> 查询），
    /// 仅返回指纹匹配且非空的记录，键为 <see cref="Path.GetFullPath(string)"/>。
    /// </summary>
    public Dictionary<string, string> GetCachedSha1Batch(IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var metas = new (string FullPath, long Size, DateTime LastWriteUtc)?[filePaths.Count];
        void Probe(int i)
        {
            var path = filePaths[i];
            if (string.IsNullOrWhiteSpace(path)) return;
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (info.Exists)
                metas[i] = (fullPath, info.Length, info.LastWriteTimeUtc);
        }

        if (filePaths.Count > 8)
        {
            Parallel.For(0, filePaths.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) }, Probe);
        }
        else
        {
            for (var i = 0; i < filePaths.Count; i++) Probe(i);
        }

        var list = new List<(string FullPath, long Size, DateTime LastWriteUtc)>(filePaths.Count);
        foreach (var m in metas)
        {
            if (m is { } v) list.Add(v);
        }
        return GetCachedSha1Batch(list);
    }

    /// <summary>
    /// 使用调用方已采集的文件元数据批量查询 SHA1 缓存，避免重复文件系统 stat，
    /// 并将数据库查询合并为最多 500 项一批的 <c>WHERE FilePath IN (...)</c> 单次查询。
    /// 要求每项 <c>FullPath</c> 已通过 <see cref="Path.GetFullPath(string)"/> 规范化。
    /// </summary>
    public Dictionary<string, string> GetCachedSha1Batch(
        IReadOnlyList<(string FullPath, long Size, DateTime LastWriteUtc)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0) return results;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dbCandidates = new List<(string FullPath, long Size, string LastWrite)>(files.Count);
        foreach (var item in files)
        {
            if (string.IsNullOrWhiteSpace(item.FullPath)) continue;
            if (!seen.Add(item.FullPath)) continue;
            var lastWrite = item.LastWriteUtc.ToString("O");

            if (_pendingWrites.TryGetValue((item.FullPath, "SHA1"), out var pending) &&
                pending.Size == item.Size && pending.LastWrite == lastWrite &&
                !string.IsNullOrEmpty(pending.Hash))
            {
                results[item.FullPath] = pending.Hash;
                continue;
            }

            dbCandidates.Add((item.FullPath, item.Size, lastWrite));
        }

        if (dbCandidates.Count == 0) return results;

        const int chunkSize = 500;
        using var conn = _CreateConnection();
        for (var offset = 0; offset < dbCandidates.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, dbCandidates.Count - offset);
            using var cmd = conn.CreateCommand();
            var sql = new System.Text.StringBuilder("SELECT FilePath, FileSize, LastWriteTime, SHA1 FROM HashCache WHERE FilePath IN (");
            var expected = new Dictionary<string, (long Size, string LastWrite)>(count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var candidate = dbCandidates[offset + i];
                if (i > 0) sql.Append(',');
                var paramName = $"@p{i}";
                sql.Append(paramName);
                cmd.Parameters.AddWithValue(paramName, candidate.FullPath);
                expected[candidate.FullPath] = (candidate.Size, candidate.LastWrite);
            }
            sql.Append(')');
            cmd.CommandText = sql.ToString();

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var dbPath = reader.GetString(0);
                if (!expected.TryGetValue(dbPath, out var exp)) continue;
                if (reader.GetInt64(1) == exp.Size &&
                    reader.GetString(2) == exp.LastWrite &&
                    !reader.IsDBNull(3))
                {
                    var sha1 = reader.GetString(3);
                    if (!string.IsNullOrEmpty(sha1))
                        results[dbPath] = sha1;
                }
            }
        }

        return results;
    }

    public Task<string> GetSHA256Async(string filePath) =>
        _GetHashWithPending(filePath, SHA256Provider.Instance, "SHA256");

    public Task<string> GetSHA512Async(string filePath) =>
        _GetHashWithPending(filePath, SHA512Provider.Instance, "SHA512");

    public Task<string> GetMurmurHash2Async(string filePath) =>
        _GetHashWithPending(filePath, MurmurHash2Provider.Instance, "MurmurHash2");

    private readonly ConcurrentDictionary<(string Path, string Algorithm), Lazy<Task<string>>> _FileHashComputePending = new();

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
        var fullPath = Path.GetFullPath(filePath);
        var computeKey = (fullPath, algoName);
        // GetOrAdd 的工厂可能并发执行多次，用 Lazy 避免同一文件被重复计算。
        var pending = _FileHashComputePending.GetOrAdd(computeKey,
            _ => new Lazy<Task<string>>(() => _GetHashAsync(fullPath, provider, algoName)));
        try
        {
            return await pending.Value.ConfigureAwait(false);
        }
        finally
        {
            // 同步命中也在入表后清理；只移除本次任务，避免误删后续计算。
            _FileHashComputePending.TryRemove(new KeyValuePair<(string Path, string Algorithm), Lazy<Task<string>>>(computeKey, pending));
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
                else if (!_batchWrites)
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
            if (!_batchWrites) await _DeleteCacheEntryAsync(fullPath).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> _ComputeHashAsync(string fullPath, IHashProvider provider)
    {
        if (provider.GetType() == typeof(MurmurHash2Provider) &&
            PclNativeInterop.TryMurmurHash2File(fullPath, out var nativeHash))
        {
            return BitConverter.GetBytes(nativeHash).ToHexString();
        }
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
            try
            {
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
            }
            finally
            {
                // 缓存保存失败也丢弃本次快照，保留并发加入的新结果。
                foreach (var entry in snapshot) _pendingWrites.TryRemove(entry);
            }
        }
    }

    /// <summary>
    /// 批量获取文件列表的 SHA1 与 MurmurHash2；优先复用缓存，未命中项在原生可用时通过单遍并行盘扫描计算并写回缓存。
    /// 返回值以 <see cref="Path.GetFullPath(string)"/> 为键。
    /// </summary>
    public async Task<Dictionary<string, (string Sha1, uint Murmur2)>> GetDualHashesBatchAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var results = new Dictionary<string, (string Sha1, uint Murmur2)>(StringComparer.OrdinalIgnoreCase);
        if (filePaths.Count == 0) return results;

        var candidates = new List<(string FullPath, long Size, string LastWrite)>(filePaths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) continue;
            var fullPath = Path.GetFullPath(path);
            if (!seen.Add(fullPath)) continue;
            var info = new FileInfo(fullPath);
            if (!info.Exists) continue;
            candidates.Add((fullPath, info.Length, info.LastWriteTimeUtc.ToString("O")));
        }

        if (candidates.Count == 0) return results;

        var missing = new List<(string FullPath, long Size, string LastWrite)>();
        using (var conn = _CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT FileSize, LastWriteTime, SHA1, MurmurHash2 FROM HashCache WHERE FilePath = @FilePath";
            var pathParam = cmd.CreateParameter();
            pathParam.ParameterName = "@FilePath";
            cmd.Parameters.Add(pathParam);

            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? sha1 = null;
                string? murmurHex = null;

                if (_pendingWrites.TryGetValue((item.FullPath, "SHA1"), out var pendingSha1) &&
                    pendingSha1.Size == item.Size && pendingSha1.LastWrite == item.LastWrite)
                    sha1 = pendingSha1.Hash;
                if (_pendingWrites.TryGetValue((item.FullPath, "MurmurHash2"), out var pendingM2) &&
                    pendingM2.Size == item.Size && pendingM2.LastWrite == item.LastWrite)
                    murmurHex = pendingM2.Hash;

                if (sha1 is null || murmurHex is null)
                {
                    pathParam.Value = item.FullPath;
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var cachedSize = reader.GetInt64(0);
                        var cachedTime = reader.GetString(1);
                        if (cachedSize == item.Size && cachedTime == item.LastWrite)
                        {
                            sha1 ??= reader.IsDBNull(2) ? null : reader.GetString(2);
                            murmurHex ??= reader.IsDBNull(3) ? null : reader.GetString(3);
                        }
                    }
                }

                if (sha1 is not null && murmurHex is not null)
                {
                    try
                    {
                        var bytes = murmurHex.HexToBytes();
                        if (bytes.Length == 4)
                        {
                            results[item.FullPath] = (sha1, BitConverter.ToUInt32(bytes));
                            continue;
                        }
                    }
                    catch
                    {
                        // Corrupt cached hex; fall through to recompute.
                    }
                }

                missing.Add(item);
            }
        }

        if (missing.Count == 0) return results;

        var fallbackList = new List<(string FullPath, long Size, string LastWrite)>();
        if (PclNativeInterop.IsAvailable)
        {
            var missingPaths = new string[missing.Count];
            for (var i = 0; i < missing.Count; i++) missingPaths[i] = missing[i].FullPath;

            var nativeOut = new PclFileHashResult[missing.Count];
            var maxThreads = (uint)Math.Clamp(Environment.ProcessorCount, 1, 6);
            if (PclNativeInterop.TryBatchHashFiles(missingPaths, nativeOut, maxThreads))
            {
                var toPersist = new List<(string FullPath, long Size, string LastWrite, string Sha1, string MurmurHex)>(missing.Count);
                for (var i = 0; i < missing.Count; i++)
                {
                    var item = missing[i];
                    var res = nativeOut[i];
                    if (res.Status != PclNativeInterop.PclOk)
                    {
                        fallbackList.Add(item);
                        continue;
                    }

                    var sha1Hex = PclNativeInterop.FormatSha1Hex(in res);
                    var murmurHex = BitConverter.GetBytes(res.Murmur2).ToHexString();
                    results[item.FullPath] = (sha1Hex, res.Murmur2);

                    var postInfo = new FileInfo(item.FullPath);
                    if (postInfo.Exists && postInfo.Length == item.Size &&
                        postInfo.LastWriteTimeUtc.ToString("O") == item.LastWrite)
                    {
                        toPersist.Add((item.FullPath, item.Size, item.LastWrite, sha1Hex, murmurHex));
                    }
                }

                if (toPersist.Count > 0)
                {
                    if (_batchWrites)
                    {
                        foreach (var row in toPersist)
                        {
                            _pendingWrites[(row.FullPath, "SHA1")] = new PendingHash(row.Size, row.LastWrite, row.Sha1);
                            _pendingWrites[(row.FullPath, "MurmurHash2")] = new PendingHash(row.Size, row.LastWrite, row.MurmurHex);
                        }
                    }
                    else
                    {
                        using var writeConn = _CreateConnection();
                        using var tx = writeConn.BeginTransaction();
                        foreach (var row in toPersist)
                        {
                            await _InsertOrUpdateHashAsync(row.FullPath, row.Size, row.LastWrite, "SHA1", row.Sha1, writeConn, tx).ConfigureAwait(false);
                            await _InsertOrUpdateHashAsync(row.FullPath, row.Size, row.LastWrite, "MurmurHash2", row.MurmurHex, writeConn, tx).ConfigureAwait(false);
                        }
                        tx.Commit();
                    }
                }
            }
            else
            {
                fallbackList.AddRange(missing);
            }
        }
        else
        {
            fallbackList.AddRange(missing);
        }

        foreach (var item in fallbackList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sha1 = await GetSHA1Async(item.FullPath).ConfigureAwait(false);
                var murmurHex = await GetMurmurHash2Async(item.FullPath).ConfigureAwait(false);
                var murmur2 = BitConverter.ToUInt32(murmurHex.HexToBytes());
                results[item.FullPath] = (sha1, murmur2);
            }
            catch
            {
                // Keep partial batch tolerant of individual vanished/locked files.
            }
        }

        return results;
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
