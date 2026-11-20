using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PCL.Core.Utils.Native;

internal sealed class SafeSearchIndexHandle : SafeHandle
{
    public SafeSearchIndexHandle() : base(nint.Zero, ownsHandle: true)
    {
    }

    internal SafeSearchIndexHandle(nint handle) : base(nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        if (handle == nint.Zero) return true;
        try
        {
            _ = PclNativeInterop.pcl_native_free_search_index(handle);
        }
        catch
        {
            // Never throw from a SafeHandle finalizer.
        }
        SetHandle(nint.Zero);
        return true;
    }
}

/// <summary>
/// Safe managed wrapper around a native Rust <c>PclSearchIndex</c> handle.
/// </summary>
public sealed unsafe class NativeSearchIndex : IDisposable
{
    private readonly SafeSearchIndexHandle _handle;

    public int EntryCount { get; }

    private NativeSearchIndex(SafeSearchIndexHandle handle, int entryCount)
    {
        _handle = handle;
        EntryCount = entryCount;
    }

    public static NativeSearchIndex? TryBuild(
        ReadOnlySpan<PclSearchEntryDesc> entries,
        ReadOnlySpan<PclSearchSourceDesc> sources,
        ReadOnlySpan<PclSearchAliasSpan> aliases,
        ReadOnlySpan<char> textBuf)
    {
        if (!PclNativeInterop.IsAvailable) return null;

        try
        {
            nint rawPtr = nint.Zero;
            fixed (PclSearchEntryDesc* ePtr = entries)
            fixed (PclSearchSourceDesc* sPtr = sources)
            fixed (PclSearchAliasSpan* aPtr = aliases)
            fixed (char* tPtr = textBuf)
            {
                var rc = PclNativeInterop.pcl_native_search_index_create(
                    ePtr,
                    (nuint)entries.Length,
                    sPtr,
                    (nuint)sources.Length,
                    aPtr,
                    (nuint)aliases.Length,
                    (ushort*)tPtr,
                    (nuint)textBuf.Length,
                    &rawPtr);
                if (rc != PclNativeInterop.PclOk || rawPtr == nint.Zero)
                {
                    if (rawPtr != nint.Zero)
                    {
                        try { _ = PclNativeInterop.pcl_native_free_search_index(rawPtr); } catch { }
                    }
                    return null;
                }
            }

            return new NativeSearchIndex(new SafeSearchIndexHandle(rawPtr), entries.Length);
        }
        catch
        {
            return null;
        }
    }

    public bool TryQuery(
        int mode,
        ReadOnlySpan<char> query,
        int maxBlurCount,
        double minBlurSimilarity,
        Span<PclSearchResult> outResults,
        out int totalCount)
    {
        totalCount = 0;
        if (!PclNativeInterop.IsAvailable || _handle.IsClosed || _handle.IsInvalid)
            return false;

        var addedRef = false;
        try
        {
            _handle.DangerousAddRef(ref addedRef);
            var ptr = _handle.DangerousGetHandle();
            if (ptr == nint.Zero) return false;

            nuint total = 0;
            var blurLimit = maxBlurCount <= 0 ? (nuint)0 : (nuint)maxBlurCount;
            fixed (char* qPtr = query)
            fixed (PclSearchResult* rPtr = outResults)
            {
                var rc = PclNativeInterop.pcl_native_search_index_query(
                    ptr,
                    mode,
                    (ushort*)qPtr,
                    (nuint)query.Length,
                    blurLimit,
                    minBlurSimilarity,
                    rPtr,
                    (nuint)outResults.Length,
                    &total);
                if (rc != PclNativeInterop.PclOk) return false;
            }

            totalCount = checked((int)total);
            return true;
        }
        catch
        {
            totalCount = 0;
            return false;
        }
        finally
        {
            if (addedRef) _handle.DangerousRelease();
        }
    }

    public bool TryQueryPooled(
        int mode,
        ReadOnlySpan<char> query,
        int maxBlurCount,
        double minBlurSimilarity,
        out PclSearchResult[]? rentedBuffer,
        out int resultCount)
    {
        rentedBuffer = null;
        resultCount = 0;
        if (EntryCount == 0)
        {
            rentedBuffer = Array.Empty<PclSearchResult>();
            return true;
        }

        var buffer = ArrayPool<PclSearchResult>.Shared.Rent(EntryCount);
        try
        {
            if (!TryQuery(mode, query, maxBlurCount, minBlurSimilarity, buffer.AsSpan(0, EntryCount), out var total))
            {
                ArrayPool<PclSearchResult>.Shared.Return(buffer);
                return false;
            }

            if (total > EntryCount)
            {
                // Defensive guard; total_count cannot exceed EntryCount in valid index queries.
                ArrayPool<PclSearchResult>.Shared.Return(buffer);
                return false;
            }

            rentedBuffer = buffer;
            resultCount = total;
            return true;
        }
        catch
        {
            ArrayPool<PclSearchResult>.Shared.Return(buffer);
            rentedBuffer = null;
            resultCount = 0;
            return false;
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
    }
}

/// <summary>
/// Shared CSR packing and bounded LRU corpus cache for native search acceleration.
/// </summary>
public static class NativeSearchHelper
{
    public const int DefaultOneShotMinEntries = 128;
    public const int DefaultCacheMinEntries = 64;
    private const int MaxCacheSlots = 8;

    private sealed class CachedCorpus
    {
        public ulong Fingerprint;
        public int EntryCount;
        public int SourceCount;
        public int AliasCount;
        public int[] EntrySourceCounts = [];
        public int[] SourceAliasCounts = [];
        public double[] Weights = [];
        public string[] Aliases = [];
        public NativeSearchIndex Index = null!;
        public long LastUsedSeq;
    }

    private static readonly object _CacheLock = new();
    private static readonly List<CachedCorpus> _Cache = new(MaxCacheSlots);
    private static long _AccessSeq;

    public static void ClearCache()
    {
        lock (_CacheLock)
        {
            foreach (var item in _Cache)
            {
                item.Index.Dispose();
            }
            _Cache.Clear();
        }
    }

    public static NativeSearchIndex? TryBuildFromFlat(
        int entryCount,
        int[] entrySourceCounts,
        int sourceCount,
        int[] sourceAliasCounts,
        double[] weights,
        int aliasCount,
        string[] aliases)
    {
        if (!PclNativeInterop.IsAvailable) return null;
        if (entryCount == 0)
        {
            return NativeSearchIndex.TryBuild([], [], [], []);
        }

        var totalChars = 0;
        for (var i = 0; i < aliasCount; i++)
        {
            var a = aliases[i];
            if (a is null) return null;
            totalChars += a.Length;
        }

        var entryDescs = ArrayPool<PclSearchEntryDesc>.Shared.Rent(entryCount);
        var sourceDescs = ArrayPool<PclSearchSourceDesc>.Shared.Rent(Math.Max(1, sourceCount));
        var aliasSpans = ArrayPool<PclSearchAliasSpan>.Shared.Rent(Math.Max(1, aliasCount));
        var textBuf = ArrayPool<char>.Shared.Rent(Math.Max(1, totalChars));

        try
        {
            uint srcIdx = 0;
            for (var e = 0; e < entryCount; e++)
            {
                var cnt = (uint)entrySourceCounts[e];
                entryDescs[e] = new PclSearchEntryDesc
                {
                    SourceStart = srcIdx,
                    SourceCount = cnt
                };
                srcIdx += cnt;
            }

            uint aliasIdx = 0;
            for (var s = 0; s < sourceCount; s++)
            {
                var w = weights[s];
                if (!double.IsFinite(w) || w < 0.0) return null;
                var cnt = (uint)sourceAliasCounts[s];
                sourceDescs[s] = new PclSearchSourceDesc
                {
                    AliasStart = aliasIdx,
                    AliasCount = cnt,
                    Weight = w
                };
                aliasIdx += cnt;
            }

            var textOffset = 0;
            for (var a = 0; a < aliasCount; a++)
            {
                var str = aliases[a];
                str.AsSpan().CopyTo(textBuf.AsSpan(textOffset));
                aliasSpans[a] = new PclSearchAliasSpan
                {
                    TextOffset = (uint)textOffset,
                    TextLen = (uint)str.Length
                };
                textOffset += str.Length;
            }

            return NativeSearchIndex.TryBuild(
                entryDescs.AsSpan(0, entryCount),
                sourceDescs.AsSpan(0, sourceCount),
                aliasSpans.AsSpan(0, aliasCount),
                textBuf.AsSpan(0, totalChars));
        }
        finally
        {
            ArrayPool<PclSearchEntryDesc>.Shared.Return(entryDescs);
            ArrayPool<PclSearchSourceDesc>.Shared.Return(sourceDescs);
            ArrayPool<PclSearchAliasSpan>.Shared.Return(aliasSpans);
            ArrayPool<char>.Shared.Return(textBuf);
        }
    }

    public static bool TryQueryWithLruCache(
        int mode,
        ReadOnlySpan<char> query,
        int maxBlurCount,
        double minBlurSimilarity,
        int entryCount,
        int[] entrySourceCounts,
        int sourceCount,
        int[] sourceAliasCounts,
        double[] weights,
        int aliasCount,
        string[] aliases,
        ulong fingerprint,
        bool allowCache,
        out PclSearchResult[]? rentedResults,
        out int resultCount)
    {
        rentedResults = null;
        resultCount = 0;
        if (!PclNativeInterop.IsAvailable) return false;

        NativeSearchIndex? index = null;
        var disposeAfterQuery = false;

        if (allowCache)
        {
            lock (_CacheLock)
            {
                for (var i = 0; i < _Cache.Count; i++)
                {
                    var cand = _Cache[i];
                    if (cand.Fingerprint == fingerprint &&
                        _MatchesCorpus(cand, entryCount, sourceCount, aliasCount, entrySourceCounts, sourceAliasCounts, weights, aliases))
                    {
                        cand.LastUsedSeq = ++_AccessSeq;
                        index = cand.Index;
                        break;
                    }
                }
            }
        }

        if (index is null)
        {
            index = TryBuildFromFlat(entryCount, entrySourceCounts, sourceCount, sourceAliasCounts, weights, aliasCount, aliases);
            if (index is null) return false;

            if (allowCache)
            {
                var esCopy = new int[entryCount];
                Array.Copy(entrySourceCounts, esCopy, entryCount);
                var saCopy = new int[sourceCount];
                Array.Copy(sourceAliasCounts, saCopy, sourceCount);
                var wCopy = new double[sourceCount];
                Array.Copy(weights, wCopy, sourceCount);
                var aCopy = new string[aliasCount];
                Array.Copy(aliases, aCopy, aliasCount);

                var entry = new CachedCorpus
                {
                    Fingerprint = fingerprint,
                    EntryCount = entryCount,
                    SourceCount = sourceCount,
                    AliasCount = aliasCount,
                    EntrySourceCounts = esCopy,
                    SourceAliasCounts = saCopy,
                    Weights = wCopy,
                    Aliases = aCopy,
                    Index = index,
                    LastUsedSeq = System.Threading.Interlocked.Increment(ref _AccessSeq)
                };

                CachedCorpus? evicted = null;
                lock (_CacheLock)
                {
                    if (_Cache.Count >= MaxCacheSlots)
                    {
                        var minIdx = 0;
                        var minSeq = _Cache[0].LastUsedSeq;
                        for (var i = 1; i < _Cache.Count; i++)
                        {
                            if (_Cache[i].LastUsedSeq < minSeq)
                            {
                                minSeq = _Cache[i].LastUsedSeq;
                                minIdx = i;
                            }
                        }
                        evicted = _Cache[minIdx];
                        _Cache.RemoveAt(minIdx);
                    }
                    _Cache.Add(entry);
                }
                // Note: SafeHandle refcount protects any concurrent query still reading evicted.Index if we only Dispose when idle;
                // better yet, keep SafeHandle alive until GC if a concurrent reader might be inside TryQuery, or wait:
                // Actually let's NOT call Dispose() synchronously on evicted while another thread could be calling TryQuery on it!
                // SafeHandle finalizer will free native memory cleanly once all references go out of scope.
                _ = evicted;
            }
            else
            {
                disposeAfterQuery = true;
            }
        }

        try
        {
            return index.TryQueryPooled(mode, query, maxBlurCount, minBlurSimilarity, out rentedResults, out resultCount);
        }
        finally
        {
            if (disposeAfterQuery)
            {
                index.Dispose();
            }
        }
    }

    private static bool _MatchesCorpus(
        CachedCorpus c,
        int entryCount,
        int sourceCount,
        int aliasCount,
        int[] entrySourceCounts,
        int[] sourceAliasCounts,
        double[] weights,
        string[] aliases)
    {
        if (c.EntryCount != entryCount || c.SourceCount != sourceCount || c.AliasCount != aliasCount)
            return false;

        for (var i = 0; i < entryCount; i++)
        {
            if (c.EntrySourceCounts[i] != entrySourceCounts[i]) return false;
        }
        for (var i = 0; i < sourceCount; i++)
        {
            if (c.SourceAliasCounts[i] != sourceAliasCounts[i] ||
                BitConverter.DoubleToInt64Bits(c.Weights[i]) != BitConverter.DoubleToInt64Bits(weights[i]))
                return false;
        }
        for (var i = 0; i < aliasCount; i++)
        {
            var a = c.Aliases[i];
            var b = aliases[i];
            if (!ReferenceEquals(a, b) && !string.Equals(a, b, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    public static ulong MixFingerprint(ulong h, ulong value)
    {
        h ^= value;
        h *= 0x100000001B3UL;
        return h;
    }
}
