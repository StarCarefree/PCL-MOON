using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Core.IO;

public static class ArchiveIconCache
{
    // 分段锁限制同一图标的并发写入，且不随模组数量持续增长。
    private static readonly object[] WriteLocks = Enumerable.Range(0, 32).Select(_ => new object()).ToArray();

    public static string GetOrExtract(ZipArchiveEntry entry, string sourcePath, string cacheDirectory)
    {
        // CRC 区分同路径、同大小的图标更新，也适用于没有物理文件的内嵌 jar。
        var key = $"{sourcePath}\n{entry.FullName}\n{entry.Length}\n{entry.Crc32:X8}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var cachePath = Path.Combine(cacheDirectory, $"{Convert.ToHexString(hash)}.png");
        bool IsCached()
        {
            var cached = new FileInfo(cachePath);
            return cached.Exists && cached.Length == entry.Length;
        }
        if (IsCached()) return cachePath;

        lock (WriteLocks[hash[0] % WriteLocks.Length])
        {
            if (IsCached()) return cachePath;
            Directory.CreateDirectory(cacheDirectory);
            var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = entry.Open())
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                // 完整写入后才发布，避免并发列表扫描或 UI 读到半张图标。
                File.Move(temporaryPath, cachePath, true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        return cachePath;
    }
}
