using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PCL.Core.Utils.Native;

[StructLayout(LayoutKind.Sequential)]
public struct PclSearchEntryDesc
{
    public uint SourceStart;
    public uint SourceCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct PclSearchSourceDesc
{
    public uint AliasStart;
    public uint AliasCount;
    public double Weight;
}

[StructLayout(LayoutKind.Sequential)]
public struct PclSearchAliasSpan
{
    public uint TextOffset;
    public uint TextLen;
}

[StructLayout(LayoutKind.Sequential)]
public struct PclSearchResult
{
    public uint EntryIndex;
    public double Similarity;
    public byte AbsoluteRight;
}

[StructLayout(LayoutKind.Sequential)]
public struct PclUtf16Span
{
    public uint Offset;
    public uint Len;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PclFileHashResult
{
    public int Status;
    public uint Murmur2;
    public fixed byte Sha1[20];
    public ulong FileSize;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PclSha1FileResult
{
    public int Status;
    public uint Reserved;
    public ulong FileSize;
    public fixed byte Sha1[20];
    public fixed byte Pad[4];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PclJarScanItem
{
    public int Status;
    public byte DetectedLoader;
    public byte MetadataKind;
    public fixed byte Reserved[2];
    public uint Murmur2;
    public fixed byte Sha1[20];
    public ulong FileSize;
    public uint MetadataOffset;
    public uint MetadataLen;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PclJarScanBatch
{
    public PclJarScanItem* ItemsPtr;
    public nuint ItemCount;
    public byte* DataPtr;
    public nuint DataLen;
}

public sealed record BatchJarScanResult(PclJarScanItem[] Items, byte[] MetadataArena);

/// <summary>
/// Safe P/Invoke bridge and fallback control for <c>pcl_native.dll</c>.
/// </summary>
public static unsafe partial class PclNativeInterop
{
    public const string LibraryName = "pcl_native";
    public const string LibraryFileName = "pcl_native.dll";
    public const uint ExpectedAbiVersion = 2;
    public const string DisableEnvVarName = "PCL_DISABLE_NATIVE";

    public const int PclOk = 0;
    public const int PclErrNullPtr = -1;
    public const int PclErrInvalidArg = -2;
    public const int PclErrInvalidFormat = -3;
    public const int PclErrIo = -4;
    public const int PclErrBufferTooSmall = -5;
    public const int PclErrPanic = -99;

    public const int RenderingBiasPerformance = 0;
    public const int RenderingBiasQuality = 1;
    public const int KernelGaussian = 0;
    public const int KernelBox = 1;

    public const int SearchModeSimilarity = 0;
    public const int SearchModeModBase = 1;

    public const byte LoaderUnknown = 0;
    public const byte LoaderFabric = 1;
    public const byte LoaderQuilt = 2;
    public const byte LoaderForge = 3;
    public const byte LoaderNeoForge = 4;

    public const byte MetaNone = 0;
    public const byte MetaFabricJson = 1;
    public const byte MetaQuiltJson = 2;
    public const byte MetaModsToml = 3;
    public const byte MetaNeoForgeModsToml = 4;
    public const byte MetaMcmodInfo = 5;

    private static readonly Lazy<bool> _NativeReady = new(_ProbeNativeReady, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string? LoadedLibraryPath { get; private set; }

    public static bool IsSupportedPlatform =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Checks whether native acceleration is disabled via <c>PCL_DISABLE_NATIVE</c>.
    /// Read dynamically so tests and runtime diagnostics can toggle it immediately.
    /// </summary>
    public static bool IsDisabledByEnvironment
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DisableEnvVarName);
            if (string.IsNullOrWhiteSpace(value)) return false;
            var trimmed = value.Trim();
            return string.Equals(trimmed, "1", StringComparison.Ordinal) ||
                   string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsAvailable => !IsDisabledByEnvironment && _NativeReady.Value;

    static PclNativeInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(PclNativeInterop).Assembly, _ResolveNativeLibrary);
    }

    private static nint _ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, LibraryFileName, StringComparison.OrdinalIgnoreCase))
        {
            return nint.Zero;
        }

        if (!IsSupportedPlatform)
        {
            return nint.Zero;
        }

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirs)
        {
            foreach (var rawDir in searchDirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (_TryLoadCandidate(Path.Combine(rawDir, LibraryFileName), out var handle))
                    return handle;
                if (_TryLoadCandidate(Path.Combine(rawDir, "runtimes", "win-x64", "native", LibraryFileName), out handle))
                    return handle;
            }
        }

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            if (_TryLoadCandidate(Path.Combine(baseDir, LibraryFileName), out var handle))
                return handle;
            if (_TryLoadCandidate(Path.Combine(baseDir, "runtimes", "win-x64", "native", LibraryFileName), out handle))
                return handle;

            var dir = new DirectoryInfo(baseDir);
            for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                var repoDll = Path.Combine(dir.FullName, "native", "pcl-native", "target", "release", LibraryFileName);
                if (_TryLoadCandidate(repoDll, out handle))
                    return handle;
            }
        }

        return nint.Zero;
    }

    private static bool _TryLoadCandidate(string path, out nint handle)
    {
        handle = nint.Zero;
        try
        {
            if (!File.Exists(path)) return false;
            if (NativeLibrary.TryLoad(path, out handle))
            {
                LoadedLibraryPath ??= path;
                return true;
            }
        }
        catch
        {
            handle = nint.Zero;
        }
        return false;
    }

    private static bool _ProbeNativeReady()
    {
        if (!IsSupportedPlatform) return false;
        try
        {
            return pcl_native_abi_version() == ExpectedAbiVersion;
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport(LibraryName)]
    internal static partial uint pcl_native_abi_version();

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_blur_bgra32(
        byte* srcPtr,
        int srcStride,
        byte* dstPtr,
        int dstStride,
        int width,
        int height,
        double radius,
        double samplingRate,
        int renderingBias,
        int kernelType);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_bsdiff_inspect(
        byte* diffPtr,
        nuint diffLen,
        ulong* outNewLen);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_bsdiff_apply(
        byte* originPtr,
        nuint originLen,
        byte* diffPtr,
        nuint diffLen,
        byte* outPtr,
        nuint outCap,
        ulong* outWrittenLen);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_search_index_create(
        PclSearchEntryDesc* entriesPtr,
        nuint entryCount,
        PclSearchSourceDesc* sourcesPtr,
        nuint sourceCount,
        PclSearchAliasSpan* aliasesPtr,
        nuint aliasCount,
        ushort* textBufPtr,
        nuint textBufLen,
        nint* outIndex);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_search_index_query(
        nint index,
        int mode,
        ushort* queryPtr,
        nuint queryLen,
        nuint maxBlurCount,
        double minBlurSimilarity,
        PclSearchResult* outResultsPtr,
        nuint outResultsCap,
        nuint* outTotalCount);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_search_index_len(
        nint index,
        nuint* outLen);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_free_search_index(nint index);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_murmur2_slice(
        byte* dataPtr,
        nuint dataLen,
        uint* outHash);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_murmur2_file(
        ushort* pathPtr,
        nuint pathLen,
        uint* outHash);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_batch_hash_files(
        ushort* textBufPtr,
        nuint textBufLen,
        PclUtf16Span* spansPtr,
        nuint fileCount,
        uint maxThreads,
        PclFileHashResult* outResultsPtr);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_sha1_file(
        ushort* pathPtr,
        nuint pathLen,
        byte* outSha1Ptr,
        ulong* outFileSizePtr);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_batch_sha1_files(
        ushort* pathsUtf16Ptr,
        nuint pathsTotalLen,
        PclUtf16Span* spansPtr,
        nuint fileCount,
        PclSha1FileResult* outResultsPtr,
        uint maxThreads);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_batch_scan_jars(
        ushort* textBufPtr,
        nuint textBufLen,
        PclUtf16Span* spansPtr,
        nuint fileCount,
        byte preferredLoader,
        byte includeHashes,
        uint maxThreads,
        PclJarScanBatch** outBatch);

    [LibraryImport(LibraryName)]
    internal static partial int pcl_native_free_jar_scan_results(PclJarScanBatch* batch);

    public static bool TryBlurBgra32(
        ReadOnlySpan<byte> src,
        int srcStride,
        nint dstPtr,
        int dstStride,
        int width,
        int height,
        double radius,
        double samplingRate,
        int renderingBias,
        int kernelType)
    {
        if (!IsAvailable || dstPtr == nint.Zero) return false;
        if (width <= 0 || height <= 0 || srcStride < width * 4 || dstStride < width * 4) return false;
        if ((long)srcStride * height > src.Length) return false;

        try
        {
            fixed (byte* sPtr = src)
            {
                return pcl_native_blur_bgra32(
                    sPtr,
                    srcStride,
                    (byte*)dstPtr,
                    dstStride,
                    width,
                    height,
                    radius,
                    samplingRate,
                    renderingBias,
                    kernelType) == PclOk;
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool TryBsDiffInspect(ReadOnlySpan<byte> diffData, out ulong newLen, out int statusCode)
    {
        newLen = 0;
        statusCode = PclErrInvalidArg;
        if (!IsAvailable) return false;

        try
        {
            fixed (byte* diffPtr = diffData)
            {
                ulong len = 0;
                statusCode = pcl_native_bsdiff_inspect(diffPtr, (nuint)diffData.Length, &len);
                newLen = len;
                return statusCode == PclOk;
            }
        }
        catch
        {
            statusCode = PclErrPanic;
            return false;
        }
    }

    public static bool TryBsDiffApply(
        ReadOnlySpan<byte> originData,
        ReadOnlySpan<byte> diffData,
        Span<byte> output,
        out ulong writtenLen,
        out int statusCode)
    {
        writtenLen = 0;
        statusCode = PclErrInvalidArg;
        if (!IsAvailable) return false;

        try
        {
            fixed (byte* origPtr = originData)
            fixed (byte* diffPtr = diffData)
            fixed (byte* outPtr = output)
            {
                ulong written = 0;
                statusCode = pcl_native_bsdiff_apply(
                    origPtr,
                    (nuint)originData.Length,
                    diffPtr,
                    (nuint)diffData.Length,
                    outPtr,
                    (nuint)output.Length,
                    &written);
                writtenLen = written;
                return statusCode == PclOk;
            }
        }
        catch
        {
            statusCode = PclErrPanic;
            return false;
        }
    }

    public static bool TryMurmurHash2Slice(ReadOnlySpan<byte> data, out uint hash)
    {
        hash = 0;
        if (!IsAvailable) return false;

        try
        {
            fixed (byte* ptr = data)
            {
                uint h = 0;
                if (pcl_native_murmur2_slice(ptr, (nuint)data.Length, &h) == PclOk)
                {
                    hash = h;
                    return true;
                }
            }
        }
        catch
        {
            // Seamless fallback to managed path
        }
        return false;
    }

    public static bool TryMurmurHash2File(string filePath, out uint hash)
    {
        hash = 0;
        if (!IsAvailable || string.IsNullOrEmpty(filePath)) return false;

        try
        {
            fixed (char* p = filePath)
            {
                uint h = 0;
                if (pcl_native_murmur2_file((ushort*)p, (nuint)filePath.Length, &h) == PclOk)
                {
                    hash = h;
                    return true;
                }
            }
        }
        catch
        {
            // Seamless fallback to managed path
        }
        return false;
    }

    private static bool _RentAndPackPaths(
        IReadOnlyList<string> filePaths,
        out char[] textBuf,
        out PclUtf16Span[] spans,
        out int totalChars)
    {
        textBuf = [];
        spans = [];
        totalChars = 0;

        var count = filePaths.Count;
        for (var i = 0; i < count; i++)
        {
            var p = filePaths[i];
            if (string.IsNullOrEmpty(p)) return false;
            totalChars += p.Length;
        }

        textBuf = ArrayPool<char>.Shared.Rent(Math.Max(1, totalChars));
        spans = ArrayPool<PclUtf16Span>.Shared.Rent(count);
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            var p = filePaths[i];
            p.AsSpan().CopyTo(textBuf.AsSpan(offset));
            spans[i] = new PclUtf16Span { Offset = (uint)offset, Len = (uint)p.Length };
            offset += p.Length;
        }
        return true;
    }

    public static bool TrySha1File(string filePath, out string sha1Hex, out long fileSize)
    {
        sha1Hex = string.Empty;
        fileSize = 0;
        if (!IsAvailable || string.IsNullOrEmpty(filePath)) return false;

        try
        {
            var digest = stackalloc byte[20];
            ulong nativeSize = 0;
            fixed (char* p = filePath)
            {
                if (pcl_native_sha1_file((ushort*)p, (nuint)filePath.Length, digest, &nativeSize) == PclOk)
                {
                    sha1Hex = Convert.ToHexStringLower(new ReadOnlySpan<byte>(digest, 20));
                    fileSize = checked((long)nativeSize);
                    return true;
                }
            }
        }
        catch
        {
            // Seamless fallback to managed path
        }
        return false;
    }

    public static bool TryBatchSha1Files(
        IReadOnlyList<string> filePaths,
        Span<PclSha1FileResult> results,
        uint maxThreads = 0)
    {
        if (!IsAvailable || filePaths is null) return false;
        var count = filePaths.Count;
        if (count == 0) return true;
        if (results.Length < count) return false;

        if (!_RentAndPackPaths(filePaths, out var textBuf, out var spans, out var totalChars))
            return false;

        try
        {
            fixed (char* tPtr = textBuf)
            fixed (PclUtf16Span* sPtr = spans)
            fixed (PclSha1FileResult* rPtr = results)
            {
                return pcl_native_batch_sha1_files(
                    (ushort*)tPtr,
                    (nuint)totalChars,
                    sPtr,
                    (nuint)count,
                    rPtr,
                    maxThreads) == PclOk;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(textBuf);
            ArrayPool<PclUtf16Span>.Shared.Return(spans);
        }
    }

    public static bool TryBatchHashFiles(
        IReadOnlyList<string> filePaths,
        Span<PclFileHashResult> outResults,
        uint maxThreads = 0)
    {
        if (!IsAvailable || filePaths is null) return false;
        var count = filePaths.Count;
        if (count == 0) return true;
        if (outResults.Length < count) return false;

        if (!_RentAndPackPaths(filePaths, out var textBuf, out var spans, out var totalChars))
            return false;

        try
        {
            fixed (char* tPtr = textBuf)
            fixed (PclUtf16Span* sPtr = spans)
            fixed (PclFileHashResult* rPtr = outResults)
            {
                return pcl_native_batch_hash_files(
                    (ushort*)tPtr,
                    (nuint)totalChars,
                    sPtr,
                    (nuint)count,
                    maxThreads,
                    rPtr) == PclOk;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(textBuf);
            ArrayPool<PclUtf16Span>.Shared.Return(spans);
        }
    }

    public static bool TryBatchScanJars(
        IReadOnlyList<string> filePaths,
        byte preferredLoader,
        bool includeHashes,
        uint maxThreads,
        out BatchJarScanResult? result)
    {
        result = null;
        if (!IsAvailable || filePaths is null) return false;
        var count = filePaths.Count;
        if (count == 0)
        {
            result = new BatchJarScanResult([], []);
            return true;
        }

        if (!_RentAndPackPaths(filePaths, out var textBuf, out var spans, out var totalChars))
            return false;

        PclJarScanBatch* batchPtr = null;
        try
        {
            fixed (char* tPtr = textBuf)
            fixed (PclUtf16Span* sPtr = spans)
            {
                var rc = pcl_native_batch_scan_jars(
                    (ushort*)tPtr,
                    (nuint)totalChars,
                    sPtr,
                    (nuint)count,
                    preferredLoader,
                    includeHashes ? (byte)1 : (byte)0,
                    maxThreads,
                    &batchPtr);
                if (rc != PclOk || batchPtr is null) return false;
            }

            var itemCount = checked((int)batchPtr->ItemCount);
            var dataLen = checked((int)batchPtr->DataLen);
            var items = itemCount == 0 ? [] : new PclJarScanItem[itemCount];
            var arena = dataLen == 0 ? [] : new byte[dataLen];

            if (itemCount > 0 && batchPtr->ItemsPtr is not null)
            {
                fixed (PclJarScanItem* dstItems = items)
                {
                    var byteLen = (long)itemCount * sizeof(PclJarScanItem);
                    Buffer.MemoryCopy(batchPtr->ItemsPtr, dstItems, byteLen, byteLen);
                }
            }
            if (dataLen > 0 && batchPtr->DataPtr is not null)
            {
                fixed (byte* dstData = arena)
                {
                    Buffer.MemoryCopy(batchPtr->DataPtr, dstData, dataLen, dataLen);
                }
            }

            result = new BatchJarScanResult(items, arena);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
        finally
        {
            if (batchPtr is not null)
            {
                try { _ = pcl_native_free_jar_scan_results(batchPtr); } catch { }
            }
            ArrayPool<char>.Shared.Return(textBuf);
            ArrayPool<PclUtf16Span>.Shared.Return(spans);
        }
    }

    public static string FormatSha1Hex(in PclSha1FileResult result)
    {
        fixed (byte* p = result.Sha1)
        {
            return Convert.ToHexStringLower(new ReadOnlySpan<byte>(p, 20));
        }
    }

    public static string FormatSha1Hex(in PclFileHashResult result)
    {
        fixed (byte* p = result.Sha1)
        {
            return Convert.ToHexStringLower(new ReadOnlySpan<byte>(p, 20));
        }
    }

    public static string FormatSha1Hex(in PclJarScanItem item)
    {
        fixed (byte* p = item.Sha1)
        {
            return Convert.ToHexStringLower(new ReadOnlySpan<byte>(p, 20));
        }
    }
}
