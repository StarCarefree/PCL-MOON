using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Effects;
using PCL.Core.Utils;
using PCL.Core.Utils.Diff;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;
using PCL.Core.Utils.Native;

namespace PCL.Core.Test.Native;

[TestClass]
[DoNotParallelize]
public sealed class PclNativeEquivalenceTest
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        NativeSearchHelper.ClearCache();
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "PCLTest",
            nameof(PclNativeEquivalenceTest),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        NativeSearchHelper.ClearCache();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void NativeLibrary_LoadsAndChecksAbiVersion_AndEnvSwitchTogglesFallback()
    {
        if (!PclNativeInterop.IsSupportedPlatform)
        {
            Assert.IsFalse(PclNativeInterop.IsAvailable);
            return;
        }

        Assert.IsTrue(PclNativeInterop.IsAvailable, "pcl_native.dll should load on Windows x64");
        Assert.AreEqual(PclNativeInterop.ExpectedAbiVersion, PclNativeInterop.pcl_native_abi_version());
        Assert.IsFalse(string.IsNullOrEmpty(PclNativeInterop.LoadedLibraryPath));
        Assert.IsTrue(File.Exists(PclNativeInterop.LoadedLibraryPath!));

        try
        {
            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
            Assert.IsTrue(PclNativeInterop.IsDisabledByEnvironment);
            Assert.IsFalse(PclNativeInterop.IsAvailable);

            // Safety checks on error paths when disabled
            Assert.IsFalse(PclNativeInterop.TryMurmurHash2Slice(new byte[8192], out _));
            Assert.IsFalse(PclNativeInterop.TryMurmurHash2File("nonexistent.bin", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        }

        Assert.IsTrue(PclNativeInterop.IsAvailable);
        Assert.IsFalse(PclNativeInterop.TryMurmurHash2File(Path.Combine(_tempDir, "missing.jar"), out _));
        Assert.IsFalse(PclNativeInterop.TryBlurBgra32(new byte[16], 4, nint.Zero, 4, 1, 1, 2.0, 1.0, 0, 0));
    }

    [TestMethod]
    public unsafe void InteropStructLayouts_MatchRustAbi()
    {
        Assert.AreEqual(8, sizeof(PclSearchEntryDesc));
        Assert.AreEqual(16, sizeof(PclSearchSourceDesc));
        Assert.AreEqual(8, sizeof(PclSearchAliasSpan));
        Assert.AreEqual(24, sizeof(PclSearchResult));
        Assert.AreEqual(8, sizeof(PclUtf16Span));
        Assert.AreEqual(40, sizeof(PclFileHashResult));
        Assert.AreEqual(40, sizeof(PclSha1FileResult));
        Assert.AreEqual(48, sizeof(PclJarScanItem));
        Assert.AreEqual(32, sizeof(PclJarScanBatch));
    }

    [TestMethod]
    public void Sha1FileAndBatch_MatchManagedAndRespectDisableSwitch()
    {
        if (!PclNativeInterop.IsSupportedPlatform)
            return;

        var emptyFile = Path.Combine(_tempDir, "empty.bin");
        var largeFile = Path.Combine(_tempDir, "超过256KB_示例.jar");
        var smallFile = Path.Combine(_tempDir, "small_😀.jar");
        var missingFile = Path.Combine(_tempDir, "missing.jar");

        File.WriteAllBytes(emptyFile, []);
        var largeBytes = BuildMixedPayload(320 * 1024 + 19);
        var smallBytes = BuildMixedPayload(1537);
        File.WriteAllBytes(largeFile, largeBytes);
        File.WriteAllBytes(smallFile, smallBytes);

        var expEmpty = Convert.ToHexStringLower(SHA1.HashData([]));
        var expLarge = Convert.ToHexStringLower(SHA1.HashData(largeBytes));
        var expSmall = Convert.ToHexStringLower(SHA1.HashData(smallBytes));

        Assert.IsTrue(PclNativeInterop.TrySha1File(emptyFile, out var gotEmpty, out var sizeEmpty));
        Assert.AreEqual(expEmpty, gotEmpty);
        Assert.AreEqual(0L, sizeEmpty);

        Assert.IsTrue(PclNativeInterop.TrySha1File(largeFile, out var gotLarge, out var sizeLarge));
        Assert.AreEqual(expLarge, gotLarge);
        Assert.AreEqual((long)largeBytes.Length, sizeLarge);

        Assert.IsFalse(PclNativeInterop.TrySha1File(missingFile, out _, out _));

        string[] paths = [emptyFile, largeFile, smallFile, missingFile];
        var batchOut = new PclSha1FileResult[paths.Length];
        Assert.IsTrue(PclNativeInterop.TryBatchSha1Files(paths, batchOut, maxThreads: 4));

        Assert.AreEqual(PclNativeInterop.PclOk, batchOut[0].Status);
        Assert.AreEqual(0UL, batchOut[0].FileSize);
        Assert.AreEqual(expEmpty, PclNativeInterop.FormatSha1Hex(in batchOut[0]));

        Assert.AreEqual(PclNativeInterop.PclOk, batchOut[1].Status);
        Assert.AreEqual((ulong)largeBytes.Length, batchOut[1].FileSize);
        Assert.AreEqual(expLarge, PclNativeInterop.FormatSha1Hex(in batchOut[1]));

        Assert.AreEqual(PclNativeInterop.PclOk, batchOut[2].Status);
        Assert.AreEqual((ulong)smallBytes.Length, batchOut[2].FileSize);
        Assert.AreEqual(expSmall, PclNativeInterop.FormatSha1Hex(in batchOut[2]));

        Assert.AreEqual(PclNativeInterop.PclErrIo, batchOut[3].Status);

        try
        {
            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
            Assert.IsFalse(PclNativeInterop.TrySha1File(largeFile, out _, out _));
            Assert.IsFalse(PclNativeInterop.TryBatchSha1Files(paths, batchOut));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        }
    }

    [TestMethod]
    public async Task MurmurHash2_MatchesManagedAcrossEmptyWhitespaceSlicesAndLargeStreamingFiles()
    {
        byte[][] samples =
        [
            [],
            " \t\r\n "u8.ToArray(),
            "a"u8.ToArray(),
            "ab"u8.ToArray(),
            "abc"u8.ToArray(),
            "abcd"u8.ToArray(),
            "abcde"u8.ToArray(),
            " a\tb\rc\nd\t e "u8.ToArray(),
            BuildMixedPayload(4095),
            BuildMixedPayload(4096),
            BuildMixedPayload(4097),
            BuildMixedPayload(65536),
            BuildMixedPayload(65539),
        ];

        foreach (var sample in samples)
        {
            uint expected = ReferenceMurmur2Managed(sample);

            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
            var nativeSlice = MurmurHash2Provider.Instance.ComputeHash(sample.AsSpan());
            Assert.AreEqual(expected, BitConverter.ToUInt32(nativeSlice), $"native slice len={sample.Length}");

            using (var ms = new MemoryStream(sample))
            {
                var nativeMs = MurmurHash2Provider.Instance.ComputeHash(ms);
                Assert.AreEqual(expected, BitConverter.ToUInt32(nativeMs));
                Assert.AreEqual(ms.Length, ms.Position);
            }

            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
            var fallbackSlice = MurmurHash2Provider.Instance.ComputeHash(sample.AsSpan());
            Assert.AreEqual(expected, BitConverter.ToUInt32(fallbackSlice), $"fallback slice len={sample.Length}");

            using (var ms = new MemoryStream(sample))
            {
                var fallbackMs = MurmurHash2Provider.Instance.ComputeHash(ms);
                Assert.AreEqual(expected, BitConverter.ToUInt32(fallbackMs));
            }
        }

        // > 16 MiB large file streaming path + HashCache verification
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        var largeFile = Path.Combine(_tempDir, "large_模组_17mib.bin");
        var largeData = BuildMixedPayload(17 * 1024 * 1024 + 13);
        await File.WriteAllBytesAsync(largeFile, largeData);
        uint expectedLarge = ReferenceMurmur2Managed(largeData);
        string expectedLargeHex = BitConverter.GetBytes(expectedLarge).ToHexString();

        using (var fs = new FileStream(largeFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var h1 = MurmurHash2Provider.Instance.ComputeHash(fs);
            Assert.AreEqual(expectedLarge, BitConverter.ToUInt32(h1));
            Assert.AreEqual(fs.Length, fs.Position);

            fs.Position = 0;
            var h2 = await MurmurHash2Provider.Instance.ComputeHashAsync(fs);
            Assert.AreEqual(expectedLarge, BitConverter.ToUInt32(h2));
        }

        // Also test non-zero Position on FileStream falls back seamlessly to stream path
        using (var fs = new FileStream(largeFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Position = 123;
            var expectedTail = ReferenceMurmur2Managed(largeData.AsSpan(123));
            var tailHash = MurmurHash2Provider.Instance.ComputeHash(fs);
            Assert.AreEqual(expectedTail, BitConverter.ToUInt32(tailHash));
        }

        var dbPath = Path.Combine(_tempDir, "hashcache.db");
        var cache = new HashCache(dbPath);
        Assert.AreEqual(expectedLargeHex, await cache.GetMurmurHash2Async(largeFile));

        var batchMap = await cache.GetDualHashesBatchAsync([largeFile]);
        Assert.IsTrue(batchMap.ContainsKey(Path.GetFullPath(largeFile)));
        Assert.AreEqual(expectedLarge, batchMap[Path.GetFullPath(largeFile)].Murmur2);
        Assert.AreEqual(SHA1Provider.Instance.ComputeHash(largeData).ToHexString(), batchMap[Path.GetFullPath(largeFile)].Sha1);

        // Forced fallback via PCL_DISABLE_NATIVE=1 on FileStream & GetDualHashesBatchAsync & non-seekable DeflateStream
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
        using (var fs = new FileStream(largeFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var fallbackFileHash = await MurmurHash2Provider.Instance.ComputeHashAsync(fs);
            Assert.AreEqual(expectedLarge, BitConverter.ToUInt32(fallbackFileHash));
        }
        var fallbackBatchMap = await cache.GetDualHashesBatchAsync([largeFile]);
        Assert.AreEqual(expectedLarge, fallbackBatchMap[Path.GetFullPath(largeFile)].Murmur2);

        // Non-seekable stream (GZipStream / DeflateStream)
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        var rawBytes = BuildMixedPayload(9000);
        using var compressedMs = new MemoryStream();
        using (var gz = new GZipStream(compressedMs, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(rawBytes);
        compressedMs.Position = 0;
        using (var gzRead = new GZipStream(compressedMs, CompressionMode.Decompress))
        {
            var streamHash = MurmurHash2Provider.Instance.ComputeHash(gzRead);
            Assert.AreEqual(ReferenceMurmur2Managed(rawBytes), BitConverter.ToUInt32(streamHash));
        }
    }

    [TestMethod]
    public async Task BsDiff_MatchesManagedIncludingNegativeSeekTransientNegativeOldPosAndCorruptExceptions()
    {
        var diff = new BsDiff();
        var origin = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"u8.ToArray();

        // Multi-triple patch with positive/negative seekPos and tail extending past origin.Length
        var diff1 = origin[0..6].Select(b => unchecked((byte)((byte)'A' - b))).ToArray();
        var diff2 = origin[16..20].Select((b, idx) => unchecked((byte)((byte)('g' + idx) - b))).ToArray();
        var diff3 = origin[5..10].Select(b => unchecked((byte)((b ^ 0x01) - b))).ToArray();
        byte[] diff4 = [unchecked((byte)((byte)'W' - origin[34])), unchecked((byte)((byte)'X' - origin[35])), (byte)'Y', (byte)'Z'];
        var diffRaw = diff1.Concat(diff2).Concat(diff3).Concat(diff4).ToArray();
        var extraRaw = "!!?##"u8.ToArray();
        (long Add, long Copy, long Seek)[] triples = [(6, 2, 10), (4, 1, -15), (5, 0, 24), (4, 2, -3)];
        var expected = "AAAAAA!!"u8.ToArray()
            .Concat("ghij?"u8.ToArray())
            .Concat(origin[5..10].Select(b => (byte)(b ^ 0x01)))
            .Concat("WXYZ##"u8.ToArray())
            .ToArray();
        var patch = BuildSyntheticBsDiffPatch(triples, diffRaw, extraRaw, expected.Length);

        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        var nativeOut = await diff.ApplyAsync(origin, patch);
        CollectionAssert.AreEqual(expected, nativeOut);

        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
        var managedOut = await diff.ApplyAsync(origin, patch);
        CollectionAssert.AreEqual(expected, managedOut);

        // Transient negative old_pos when addRange == 0 followed by positive seek
        var transientPatch = BuildSyntheticBsDiffPatch([(2, 0, -10), (0, 1, 10), (2, 0, 0)], new byte[4], "!"u8.ToArray(), 5);
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        CollectionAssert.AreEqual("AB!CD"u8.ToArray(), await diff.ApplyAsync("ABCDEF"u8.ToArray(), transientPatch));
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
        CollectionAssert.AreEqual("AB!CD"u8.ToArray(), await diff.ApplyAsync("ABCDEF"u8.ToArray(), transientPatch));

        // Empty output patch
        var emptyPatch = BuildSyntheticBsDiffPatch([], [], [], 0);
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
        Assert.AreEqual(0, (await diff.ApplyAsync([], emptyPatch)).Length);

        // Corrupt headers, magic, BZip2, overflows, and negative old_pos during add > 0 must throw in BOTH modes
        var badMagic = new byte[32];
        "BADMAGIC"u8.CopyTo(badMagic);
        var negHeader = new byte[32];
        "BSDIFF40"u8.CopyTo(negHeader);
        BinaryPrimitives.WriteInt64LittleEndian(negHeader.AsSpan(8, 8), -1);
        var corruptBz = (byte[])patch.Clone();
        corruptBz[34] ^= 0xFF;
        corruptBz[^1] ^= 0xFF;
        var addOverflow = BuildSyntheticBsDiffPatch([(5, 0, 0)], "12345"u8.ToArray(), [], 3);
        var oldOverflow = BuildSyntheticBsDiffPatch([(2, 0, 20), (1, 0, 0)], "123"u8.ToArray(), [], 3);
        var negOldAdd = BuildSyntheticBsDiffPatch([(2, 0, -10), (2, 0, 0)], "1234"u8.ToArray(), [], 4);

        byte[][] badPatches = ["short"u8.ToArray(), badMagic, negHeader, corruptBz, addOverflow, oldOverflow, negOldAdd];
        foreach (var bad in badPatches)
        {
            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
            await AssertThrowsAnyAsync<Exception>(() => diff.ApplyAsync("abcdefgh"u8.ToArray(), bad));

            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
            await AssertThrowsAnyAsync<Exception>(() => diff.ApplyAsync("abcdefgh"u8.ToArray(), bad));
        }
    }

    [TestMethod]
    public void SimilaritySearch_And_ModBaseMode_MatchManagedAcrossUnicodeEmojiSurrogatesAndTies()
    {
        var corpus = new List<(string Name, string Alias, string Desc)>
        {
            ("机械动力", "Create", "工业齿轮与传送带 automation"),
            ("机械动力 拓展", "Create Addon", "齿轮拓展"),
            ("应用能源2", "Applied Energistics 2", "ME网络与自动合成"),
            ("表情模组 😀", "EmojiMod 😀", "测试代理对 😀 表情"),
            ("\uD800A孤立代理", "\uD800a", "孤立高代理项测试"),
            ("aaab", "aaab", "ModBase vs SimilaritySearch divergence"),
            ("xabcy", "xabcy", "splice vs gap divergence"),
            ("abc", "abc", "short query raw vs nospace"),
        };

        // Expand past 256 items so Rust's parallel par_chunks_mut(256) path is also exercised
        for (int i = 0; i < 292; i++)
        {
            corpus.Add(($"Synthetic Mod {i}", $"syn_mod_{i:D3}", $"合成测试模组 #{i} create"));
        }

        string[] queries =
        [
            "",
            "   ",
            "a",
            " a",
            "aab",
            "abcxy",
            "机械动力 create",
            "机械 动力",
            "😀",
            "应用能源 😀",
            "\uD800a",
            "nonexistent_xyz_123"
        ];

        var simEntries = corpus.Select((c, idx) => new SearchEntry<int>(idx,
        [
            new(c.Name, 1.0),
            new(c.Alias, 0.8),
            new(c.Desc, 0.4)
        ])).ToList();

        using var compiled = SimilaritySearch.CreateIndex(simEntries);
        Assert.IsTrue(compiled.IsNativeBacked);

        foreach (var q in queries)
        {
            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
            var managedSim = SimilaritySearch.Search(CloneSimEntries(simEntries), q, 25, 0.25);

            Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
            NativeSearchHelper.ClearCache();
            var nativeOneShot = SimilaritySearch.SearchNativeOneShotForTest(CloneSimEntries(simEntries), q, 25, 0.25);
            var nativeCached = SimilaritySearch.Search(CloneSimEntries(simEntries), q, 25, 0.25);
            var nativeCompiled = compiled.Search(q, 25, 0.25);

            AssertSearchEntriesEqual(managedSim, nativeOneShot, $"Similarity one-shot q='{q}'");
            AssertSearchEntriesEqual(managedSim, nativeCached, $"Similarity cached q='{q}'");
            AssertSearchEntriesEqual(managedSim, nativeCompiled, $"Similarity compiled q='{q}'");
        }

        // Also test ModBase mode (PCL_SEARCH_MODE_MODBASE) via NativeSearchIndex vs exact C# ModBase reference
        var mbEntries = corpus.Select((c, idx) => (
            Id: idx,
            Sources: new List<(string[] Aliases, double Weight)>
            {
                ([c.Name, c.Alias], 1.0),
                ([c.Desc], 0.4)
            }
        )).ToList();

        using var mbIndex = BuildNativeModBaseIndex(mbEntries);
        Assert.IsNotNull(mbIndex);

        foreach (var q in queries.Where(q => q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 0))
        {
            var expectedMb = ReferenceModBaseSearch(mbEntries, q, 25, 0.25);
            Assert.IsTrue(mbIndex.TryQueryPooled(PclNativeInterop.SearchModeModBase, q.AsSpan(), 25, 0.25, out var rented, out var count));
            Assert.IsNotNull(rented);
            try
            {
                Assert.AreEqual(expectedMb.Count, count, $"ModBase count mismatch for q='{q}'");
                for (int i = 0; i < count; i++)
                {
                    Assert.AreEqual(expectedMb[i].Id, (int)rented[i].EntryIndex, $"ModBase index mismatch at {i} for q='{q}'");
                    Assert.AreEqual(expectedMb[i].AbsoluteRight, rented[i].AbsoluteRight != 0, $"ModBase abs mismatch at {i} for q='{q}'");
                    Assert.AreEqual(expectedMb[i].Similarity, rented[i].Similarity, 1e-12, $"ModBase sim mismatch at {i} for q='{q}'");
                }
            }
            finally
            {
                if (rented.Length > 0) System.Buffers.ArrayPool<PclSearchResult>.Shared.Return(rented);
            }
        }

        // Forced fallback on CompiledSimilarityIndex when PCL_DISABLE_NATIVE=1
        Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
        Assert.IsFalse(compiled.IsNativeBacked);
        var fallbackCompiled = compiled.Search("机械动力 create", 25, 0.25);
        AssertSearchEntriesEqual(SimilaritySearch.Search(CloneSimEntries(simEntries), "机械动力 create", 25, 0.25), fallbackCompiled, "Compiled fallback");
    }

    [TestMethod]
    public void SamplingBlurProcessor_MatchesManagedWithinRoundingToleranceAndFallsBackCleanly()
    {
        Exception? staEx = null;
        var t = new Thread(() =>
        {
            try
            {
                RunSamplingBlurParityOnStaThread();
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        if (staEx is not null)
        {
            throw new AssertFailedException("SamplingBlur STA parity failed", staEx);
        }
    }

    private static void RunSamplingBlurParityOnStaThread()
    {
        foreach (var fmt in new[] { PixelFormats.Bgra32, PixelFormats.Pbgra32 })
        {
            var src = CreatePatternBitmap(129, 73, fmt);
            var configs = new[]
            {
                (Bias: RenderingBias.Quality, Kernel: KernelType.Gaussian, Radius: 12.0, Rate: 1.0),
                (Bias: RenderingBias.Quality, Kernel: KernelType.Box, Radius: 8.5, Rate: 0.5),
                (Bias: RenderingBias.Performance, Kernel: KernelType.Gaussian, Radius: 12.0, Rate: 1.0),
                (Bias: RenderingBias.Performance, Kernel: KernelType.Gaussian, Radius: 8.0, Rate: 0.5),
            };

            using var processor = new SamplingBlurProcessor();
            foreach (var cfg in configs)
            {
                Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, null);
                processor.InvalidateCache();
                var nativeBmp = processor.ApplySamplingBlur(src, cfg.Radius, cfg.Rate, cfg.Bias, cfg.Kernel);
                Assert.IsNotNull(nativeBmp);

                Environment.SetEnvironmentVariable(PclNativeInterop.DisableEnvVarName, "1");
                processor.InvalidateCache();
                var managedBmp = processor.ApplySamplingBlur(src, cfg.Radius, cfg.Rate, cfg.Bias, cfg.Kernel);
                Assert.IsNotNull(managedBmp);

                Assert.AreEqual(managedBmp.PixelWidth, nativeBmp.PixelWidth);
                Assert.AreEqual(managedBmp.PixelHeight, nativeBmp.PixelHeight);

                var nativePixels = new byte[nativeBmp.PixelWidth * nativeBmp.PixelHeight * 4];
                var managedPixels = new byte[managedBmp.PixelWidth * managedBmp.PixelHeight * 4];
                nativeBmp.CopyPixels(nativePixels, nativeBmp.PixelWidth * 4, 0);
                managedBmp.CopyPixels(managedPixels, managedBmp.PixelWidth * 4, 0);

                int maxDiff = 0;
                for (int i = 0; i < nativePixels.Length; i++)
                {
                    int diff = Math.Abs(nativePixels[i] - managedPixels[i]);
                    if (diff > maxDiff) maxDiff = diff;
                }

                Assert.IsLessThanOrEqualTo(
                    2,
                    maxDiff,
                    $"Pixel delta too large ({maxDiff}) for {fmt} {cfg.Bias} {cfg.Kernel} r={cfg.Radius} rate={cfg.Rate}");
            }
        }
    }

    [TestMethod]
    public void BatchHashAndJarScan_MatchManagedAcrossLoadersAndChineseEmojiPaths()
    {
        var subDir = Path.Combine(_tempDir, "模组目录_😀");
        Directory.CreateDirectory(subDir);

        var fabricJar = Path.Combine(subDir, "fabric-示例.jar");
        var dualJar = Path.Combine(subDir, "dual-fabric-neoforge-😀.jar");
        var forgeTomlJar = Path.Combine(subDir, "forge-mods.jar");
        var legacyJar = Path.Combine(subDir, "legacy-mcmod.jar");
        var corruptJar = Path.Combine(subDir, "corrupt.jar");
        var missingJar = Path.Combine(subDir, "missing.jar");

        var fabricJson = """{"schemaVersion":1,"id":"create_test","name":"Create"}""";
        var neoforgeToml = "modLoader=\"javafml\"\n[[mods]]\nmodId=\"neo_test\"\n";
        var forgeToml = "modLoader=\"javafml\"\n[[mods]]\nmodId=\"forge_test\"\n";
        var mcmodJson = """[{"modid":"legacy_mod","name":"Legacy"}]""";

        CreateZip(fabricJar, ("fabric.mod.json", fabricJson));
        CreateZip(dualJar, ("fabric.mod.json", fabricJson), ("META-INF/neoforge.mods.toml", neoforgeToml));
        CreateZip(forgeTomlJar, ("META-INF/mods.toml", forgeToml), ("mcmod.info", mcmodJson));
        CreateZip(legacyJar, ("mcmod.info", mcmodJson));
        File.WriteAllText(corruptJar, "not a zip \t\r\n ");

        string[] paths = [fabricJar, dualJar, forgeTomlJar, legacyJar, corruptJar, missingJar];
        var hashResults = new PclFileHashResult[paths.Length];
        Assert.IsTrue(PclNativeInterop.TryBatchHashFiles(paths, hashResults, maxThreads: 2));

        for (int i = 0; i < 5; i++)
        {
            Assert.AreEqual(PclNativeInterop.PclOk, hashResults[i].Status);
            var bytes = File.ReadAllBytes(paths[i]);
            Assert.AreEqual(ReferenceMurmur2Managed(bytes), hashResults[i].Murmur2);
            Assert.AreEqual(SHA1Provider.Instance.ComputeHash(bytes).ToHexString(), PclNativeInterop.FormatSha1Hex(in hashResults[i]));
        }
        Assert.AreEqual(PclNativeInterop.PclErrIo, hashResults[5].Status);

        Assert.IsTrue(PclNativeInterop.TryBatchScanJars(
            paths,
            PclNativeInterop.LoaderNeoForge,
            includeHashes: true,
            maxThreads: 2,
            out var scan));
        Assert.IsNotNull(scan);
        Assert.AreEqual(6, scan.Items.Length);

        Assert.AreEqual(PclNativeInterop.LoaderFabric, scan.Items[0].DetectedLoader);
        Assert.AreEqual(PclNativeInterop.MetaFabricJson, scan.Items[0].MetadataKind);
        Assert.AreEqual(fabricJson, Encoding.UTF8.GetString(scan.MetadataArena, (int)scan.Items[0].MetadataOffset, (int)scan.Items[0].MetadataLen));

        // Preferred NeoForge wins on dual fabric+neoforge jar
        Assert.AreEqual(PclNativeInterop.LoaderNeoForge, scan.Items[1].DetectedLoader);
        Assert.AreEqual(PclNativeInterop.MetaNeoForgeModsToml, scan.Items[1].MetadataKind);

        Assert.AreEqual(PclNativeInterop.LoaderForge, scan.Items[2].DetectedLoader);
        Assert.AreEqual(PclNativeInterop.MetaModsToml, scan.Items[2].MetadataKind);

        Assert.AreEqual(PclNativeInterop.LoaderForge, scan.Items[3].DetectedLoader);
        Assert.AreEqual(PclNativeInterop.MetaMcmodInfo, scan.Items[3].MetadataKind);

        Assert.AreEqual(PclNativeInterop.PclErrInvalidFormat, scan.Items[4].Status);
        Assert.AreEqual(hashResults[4].Murmur2, scan.Items[4].Murmur2);
        Assert.AreEqual(PclNativeInterop.PclErrIo, scan.Items[5].Status);
    }

    private static byte[] BuildMixedPayload(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (i % 19) switch
            {
                0 => (byte)'\t',
                1 => (byte)'\n',
                2 => (byte)'\r',
                3 => (byte)' ',
                _ => (byte)((i * 37 + 11) & 0xFF)
            };
        }
        return data;
    }

    private static uint ReferenceMurmur2Managed(ReadOnlySpan<byte> data)
    {
        var filtered = new List<byte>(data.Length);
        foreach (var b in data)
            if (b != 9 && b != 10 && b != 13 && b != 32)
                filtered.Add(b);
        var span = CollectionsMarshal.AsSpan(filtered);
        var h = (uint)(1 ^ span.Length);
        var i = 0;
        for (; i <= span.Length - 4; i += 4)
        {
            var k = span[i] | ((uint)span[i + 1] << 8) | ((uint)span[i + 2] << 16) | ((uint)span[i + 3] << 24);
            k *= 0x5BD1E995u;
            k ^= k >> 24;
            k *= 0x5BD1E995u;
            h *= 0x5BD1E995u;
            h ^= k;
        }
        switch (span.Length - i)
        {
            case 3:
                h ^= (uint)(span[i] | ((uint)span[i + 1] << 8));
                h ^= (uint)span[i + 2] << 16;
                h *= 0x5BD1E995u;
                break;
            case 2:
                h ^= (uint)(span[i] | ((uint)span[i + 1] << 8));
                h *= 0x5BD1E995u;
                break;
            case 1:
                h ^= span[i];
                h *= 0x5BD1E995u;
                break;
        }
        h ^= h >> 13;
        h *= 0x5BD1E995u;
        h ^= h >> 15;
        return h;
    }

    private static byte[] BuildSyntheticBsDiffPatch((long Add, long Copy, long Seek)[] triples, byte[] diffRaw, byte[] extraRaw, long newLen)
    {
        using var ctrlMs = new MemoryStream();
        Span<byte> buf = stackalloc byte[8];
        foreach (var (add, copy, seek) in triples)
        {
            WriteBsDiffInt64(buf, add);
            ctrlMs.Write(buf);
            WriteBsDiffInt64(buf, copy);
            ctrlMs.Write(buf);
            WriteBsDiffInt64(buf, seek);
            ctrlMs.Write(buf);
        }

        var ctrlBz = CompressBz2(ctrlMs.ToArray());
        var diffBz = CompressBz2(diffRaw);
        var extraBz = CompressBz2(extraRaw);

        using var ms = new MemoryStream();
        ms.Write("BSDIFF40"u8);
        BinaryPrimitives.WriteInt64LittleEndian(buf, ctrlBz.Length);
        ms.Write(buf);
        BinaryPrimitives.WriteInt64LittleEndian(buf, diffBz.Length);
        ms.Write(buf);
        BinaryPrimitives.WriteInt64LittleEndian(buf, newLen);
        ms.Write(buf);
        ms.Write(ctrlBz);
        ms.Write(diffBz);
        ms.Write(extraBz);
        return ms.ToArray();
    }

    private static void WriteBsDiffInt64(Span<byte> buf, long value)
    {
        ulong encoded = value < 0 ? (ulong)(-value) | 0x8000_0000_0000_0000UL : (ulong)value;
        BinaryPrimitives.WriteUInt64LittleEndian(buf, encoded);
    }

    private static byte[] CompressBz2(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var bz = new BZip2OutputStream(ms, 9) { IsStreamOwner = false })
        {
            bz.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    private static async Task AssertThrowsAnyAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            Assert.Fail($"Expected exception of type {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static List<SearchEntry<int>> CloneSimEntries(List<SearchEntry<int>> src) =>
        src.Select(e => new SearchEntry<int>(e.Item, [.. e.SearchSource])).ToList();

    private static void AssertSearchEntriesEqual(List<SearchEntry<int>> expected, List<SearchEntry<int>> actual, string context)
    {
        Assert.AreEqual(expected.Count, actual.Count, $"Count mismatch ({context})");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].Item, actual[i].Item, $"Item mismatch at {i} ({context})");
            Assert.AreEqual(expected[i].AbsoluteRight, actual[i].AbsoluteRight, $"AbsoluteRight mismatch at {i} ({context})");
            Assert.AreEqual(expected[i].Similarity, actual[i].Similarity, 1e-12, $"Similarity mismatch at {i} ({context})");
        }
    }

    private static NativeSearchIndex BuildNativeModBaseIndex(List<(int Id, List<(string[] Aliases, double Weight)> Sources)> entries)
    {
        var entryDescs = new PclSearchEntryDesc[entries.Count];
        var sourceDescs = new List<PclSearchSourceDesc>();
        var aliasSpans = new List<PclSearchAliasSpan>();
        var sb = new StringBuilder();

        for (int e = 0; e < entries.Count; e++)
        {
            uint srcStart = (uint)sourceDescs.Count;
            var srcs = entries[e].Sources;
            foreach (var (aliases, weight) in srcs)
            {
                uint aliasStart = (uint)aliasSpans.Count;
                foreach (var a in aliases)
                {
                    uint off = (uint)sb.Length;
                    sb.Append(a);
                    aliasSpans.Add(new PclSearchAliasSpan { TextOffset = off, TextLen = (uint)a.Length });
                }
                sourceDescs.Add(new PclSearchSourceDesc { AliasStart = aliasStart, AliasCount = (uint)aliases.Length, Weight = weight });
            }
            entryDescs[e] = new PclSearchEntryDesc { SourceStart = srcStart, SourceCount = (uint)srcs.Count };
        }

        return NativeSearchIndex.TryBuild(
            entryDescs,
            CollectionsMarshal.AsSpan(sourceDescs),
            CollectionsMarshal.AsSpan(aliasSpans),
            sb.ToString().AsSpan())!;
    }

    private static List<(int Id, double Similarity, bool AbsoluteRight)> ReferenceModBaseSearch(
        List<(int Id, List<(string[] Aliases, double Weight)> Sources)> entries,
        string query,
        int maxBlurCount,
        double minBlurSimilarity)
    {
        var queryParts = query.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        var queryPartsLower = queryParts.Select(q => q.ToLower()).ToArray();

        static double Sim(string source, string q)
        {
            source = source.ToLower().Replace(" ", "");
            q = q.ToLower().Replace(" ", "");
            int sourceLength = source.Length;
            int queryLength = q.Length;
            int qp = 0;
            double lenSum = 0;
            while (qp < queryLength)
            {
                int sp = 0, lenMax = 0, spMax = 0;
                while (sp < source.Length)
                {
                    int len = 0;
                    while (qp + len < queryLength && sp + len < source.Length && source[sp + len] == q[qp + len])
                        len++;
                    if (len > lenMax) { lenMax = len; spMax = sp; }
                    sp += Math.Max(1, len);
                }
                if (lenMax > 0)
                {
                    source = source.Substring(0, spMax) + (source.Length > spMax + lenMax ? source.Substring(spMax + lenMax) : "");
                    double inc = Math.Pow(1.4d, 3 + lenMax) - 3.6d;
                    inc *= 1d + 0.3d * Math.Max(0, 3 - Math.Abs(qp - spMax));
                    lenSum += inc;
                }
                qp += Math.Max(1, lenMax);
            }
            return lenSum / queryLength * (3d / Math.Pow(sourceLength + 15, 0.5d)) * (queryLength <= 2 ? 3 - queryLength : 1);
        }

        var scored = new List<(int Id, double Similarity, bool AbsoluteRight)>(entries.Count);
        foreach (var entry in entries)
        {
            double totalWeight = 0, sum = 0;
            foreach (var src in entry.Sources)
            {
                if (src.Aliases.Length > 0) sum += src.Aliases.Max(a => Sim(a, query)) * src.Weight;
                totalWeight += src.Weight;
            }
            double sim = sum / totalWeight;
            var processed = entry.Sources.Select(s => s.Aliases.Select(a => a.Replace(" ", "").ToLower()).ToArray()).ToList();
            bool abs = queryPartsLower.All(qp => processed.Any(ps => ps.Any(p => p.Contains(qp))));
            scored.Add((entry.Id, sim, abs));
        }

        var sorted = scored.OrderByDescending(e => e.AbsoluteRight).ThenByDescending(e => e.Similarity).ToList();
        var result = new List<(int Id, double Similarity, bool AbsoluteRight)>();
        int blurCount = 0;
        foreach (var item in sorted)
        {
            if (item.AbsoluteRight) result.Add(item);
            else
            {
                if (item.Similarity < minBlurSimilarity || blurCount >= maxBlurCount) break;
                result.Add(item);
                blurCount++;
            }
        }
        return result;
    }

    private static WriteableBitmap CreatePatternBitmap(int width, int height, PixelFormat format)
    {
        var bmp = new WriteableBitmap(width, height, 96, 96, format, null);
        bmp.Lock();
        try
        {
            unsafe
            {
                byte* ptr = (byte*)bmp.BackBuffer;
                int stride = bmp.BackBufferStride;
                for (int y = 0; y < height; y++)
                {
                    byte* row = ptr + y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int off = x * 4;
                        byte a = (byte)(200 + (x & 0x37));
                        byte r = (byte)((x + y * 19) & 0xFF);
                        byte g = (byte)(((x * 5) ^ (y * 11)) & 0xFF);
                        byte b = (byte)((x * 13 + y * 7) & 0xFF);
                        if (format == PixelFormats.Pbgra32)
                        {
                            r = (byte)(r * a / 255);
                            g = (byte)(g * a / 255);
                            b = (byte)(b * a / 255);
                        }
                        row[off] = b;
                        row[off + 1] = g;
                        row[off + 2] = r;
                        row[off + 3] = a;
                    }
                }
            }
            bmp.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            bmp.Unlock();
        }
        bmp.Freeze();
        return bmp;
    }

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var s = e.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
    }
}
