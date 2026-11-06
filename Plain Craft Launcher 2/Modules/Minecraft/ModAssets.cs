using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Globalization;
using System.Text.Json.Nodes;
using PCL;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;

namespace PCL
{
    public static class ModAssets
    {
        // 获取索引
        /// <summary>
        ///     获取某实例资源文件索引的对应 Json 项，详见实例 Json 中的 assetIndex 项。失败会抛出异常。
        /// </summary>
        public static JsonNode McAssetsGetIndex(McInstance mcInstance, bool returnLegacyOnError = false,
            bool checkURLEmpty = false)
        {
            string assetsName;
            try
            {
                while (true)
                {
                    var index = mcInstance.JsonObject["assetIndex"];
                    if (index is not null && index["id"] is not null)
                        return index;
                    if (mcInstance.JsonObject["assets"] is not null)
                        assetsName = mcInstance.JsonObject["assets"].ToString();
                    if (checkURLEmpty && index["url"] is not null)
                        return index;
                    // 下一个实例
                    if (string.IsNullOrEmpty(mcInstance.InheritInstanceName))
                        break;
                    mcInstance = new McInstance(Path.Combine(ModFolder.mcFolderSelected, "versions", mcInstance.InheritInstanceName));
                }
            }
            catch
            {
            }

            // 无法获取到下载地址
            if (returnLegacyOnError)
            {
                // 返回 assets 文件名会由于没有下载地址导致全局失败
                // If AssetsName IsNot Nothing AndAlso AssetsName <> "legacy" Then
                // Log("[Minecraft] 无法获取资源文件索引下载地址，使用 assets 项提供的资源文件名：" & AssetsName)
                // Return GetJson("{""id"": """ & AssetsName & """}")
                // Else
                ModBase.Log("[Minecraft] 无法获取资源文件索引下载地址，使用默认的 legacy 下载地址");
                return (JsonNode)ModBase.GetJson(@"{
                    ""id"": ""legacy"",
                    ""sha1"": ""c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729"",
                    ""size"": 134284,
                    ""url"": ""https://launchermeta.mojang.com/mc-staging/assets/legacy/c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729/legacy.json"",
                    ""totalSize"": 111220701
                }");
            }
            // End If

            throw new Exception(Lang.Text("Minecraft.Error.NoAssetIndexInfo"));
        }

        /// <summary>
        ///     获取某实例资源文件索引名，优先使用 assetIndex，其次使用 assets。失败会返回 legacy。
        /// </summary>
        public static string McAssetsGetIndexName(McInstance mcInstance)
        {
            try
            {
                while (true)
                {
                    if (mcInstance.JsonObject["assetIndex"] is not null &&
                        mcInstance.JsonObject["assetIndex"]["id"] is not null)
                        return mcInstance.JsonObject["assetIndex"]["id"].ToString();
                    if (mcInstance.JsonObject["assets"] is not null) return mcInstance.JsonObject["assets"].ToString();
                    if (string.IsNullOrEmpty(mcInstance.InheritInstanceName))
                        break;
                    mcInstance = new McInstance(Path.Combine(ModFolder.mcFolderSelected, "versions", mcInstance.InheritInstanceName));
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "获取资源文件索引名失败");
            }

            return "legacy";
        }

        // 获取列表
        public struct McAssetsToken
        {
            /// <summary>
            ///     文件的完整本地路径。
            /// </summary>
            public string localPath;

            /// <summary>
            ///     Json 中书写的源路径。例如 minecraft/sounds/mob/stray/death2.ogg 。
            /// </summary>
            public string sourcePath;

            /// <summary>
            ///     文件大小。若无有效数据即为 0。
            /// </summary>
            public long size;

            /// <summary>
            ///     文件的 Hash 校验码。
            /// </summary>
            public string hash;

            public override string ToString()
            {
                return ModBase.GetString(size) + " | " + localPath;
            }
        }

        internal static string McAssetsHashPrefix(string hash)
        {
            return hash[..2];
        }

        internal static string McAssetsUrl(string hash)
        {
            return $"https://resources.download.minecraft.net/{McAssetsHashPrefix(hash)}/{hash}";
        }

        /// <summary>
        ///     获取 Minecraft 的资源文件列表。失败会抛出异常。
        /// </summary>
        internal static List<McAssetsToken> McAssetsListGet(McInstance mcInstance)
        {
            // 正常索引走精确查找；非标准大小写仍与通用 JsonNode 解析器兼容。
            static JsonElement Property(JsonElement value, string name)
            {
                if (value.TryGetProperty(name, out var property)) return property;
                foreach (var item in value.EnumerateObject())
                    if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return item.Value;
                return default;
            }
            var indexName = McAssetsGetIndexName(mcInstance);
            try
            {
                // 初始化
                if (!File.Exists($@"{ModFolder.mcFolderSelected}assets\indexes\{indexName}.json"))
                    throw new FileNotFoundException(Lang.Text("Minecraft.Error.AssetIndexNotFound"),
                        Path.Combine(ModFolder.mcFolderSelected, "assets", "indexes", indexName + ".json"));
                // 索引只读，无需为每个资源构建可变 JsonNode 树或将数字转成字符串再解析。
                using var document = JsonDocument.Parse(
                    ModBase.ReadFile($@"{ModFolder.mcFolderSelected}assets\indexes\{indexName}.json"),
                    JsonCompat.DocumentOptions);
                var json = document.RootElement;
                var remapValue = Property(json, "map_to_resources");
                var remap = remapValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
                            remapValue.Deserialize<bool>(JsonCompat.SerializerOptions);
                var virtualValue = Property(json, "virtual");
                var virtualFiles = virtualValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
                                   virtualValue.Deserialize<bool>(JsonCompat.SerializerOptions);
                var objects = Property(json, "objects");
                var result = new List<McAssetsToken>(objects.GetPropertyCount());
                // 同一对象可能对应多个逻辑资源名，只检查/下载一次。旧版按逻辑路径保存，不能按哈希去重。
                // 大小有冲突的条目仍分别校验，不能因为去重隐藏损坏的索引。
                var seen = remap || virtualFiles ? null : new HashSet<(string Hash, long Size)>();
                var assetsRoot = Path.Combine(ModFolder.mcFolderSelected, "assets");
                var targetRoot = remap ? Path.Combine(mcInstance.PathIndie, "resources") :
                    virtualFiles ? Path.Combine(assetsRoot, "virtual", "legacy") : Path.Combine(assetsRoot, "objects");

                foreach (var file in objects.EnumerateObject())
                {
                    var hash = Property(file.Value, "hash").GetString();
                    var sizeValue = Property(file.Value, "size");
                    var size = sizeValue.ValueKind == JsonValueKind.String
                        ? long.Parse(sizeValue.GetString(), CultureInfo.InvariantCulture) : sizeValue.GetInt64();
                    if (seen is not null && !seen.Add((hash, size))) continue;
                    var localPath = remap || virtualFiles
                        ? Path.Combine(targetRoot, file.Name.Replace("/", @"\"))
                        : Path.Combine(targetRoot, McAssetsHashPrefix(hash), hash);
                    result.Add(new McAssetsToken
                    {
                        localPath = localPath,
                        sourcePath = file.Name,
                        hash = hash,
                        size = size
                    });
                }

                return result;
            }

            catch (Exception ex)
            {
                ModBase.Log(ex, "获取资源文件列表失败：" + indexName);
                throw;
            }
        }

        /// <summary>
        ///     同一次扫描内批量读取较大的对象目录，避免为每个已有资源单独查询文件系统。
        ///     不跨启动缓存；旧版逻辑路径、小目录与读取失败的条目仍逐文件检查。
        /// </summary>
        private static ConcurrentDictionary<string, long> McAssetsFileSizes(List<McAssetsToken> assets)
        {
            var sizes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var objectsRoot = Path.Combine(ModFolder.mcFolderSelected, "assets", "objects");
            Parallel.ForEach(assets.GroupBy(token => Path.GetDirectoryName(token.localPath), StringComparer.OrdinalIgnoreCase),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) }, group =>
            {
                if (group.Count() < 8 ||
                    !string.Equals(Path.GetDirectoryName(group.Key), objectsRoot, StringComparison.OrdinalIgnoreCase))
                    return;
                var remaining = group.Select(token => Path.GetFileName(token.localPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var enumerationLimit = remaining.Count * 4L;
                try
                {
                    foreach (var file in new DirectoryInfo(group.Key).EnumerateFiles())
                    {
                        // 多版本共用目录时限制枚举量；未收集的条目照常逐文件检查。
                        if (enumerationLimit-- <= 0) break;
                        if (!remaining.Contains(file.Name)) continue;
                        // 链接的枚举大小不一定代表目标文件，保留原来的 FileInfo 检查语义。
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        sizes[file.FullName] = file.Length;
                        remaining.Remove(file.Name);
                        if (remaining.Count == 0) break;
                    }
                }
                catch (IOException) { } // 缺失目录、枚举期间删除等情况回退到逐文件检查。
                catch (UnauthorizedAccessException) { }
            });
            return sizes;
        }

        // 获取缺失列表
        /// <summary>
        ///     获取实例缺失的资源文件所对应的 NetTaskFile。
        /// </summary>
        public static List<DownloadFile> McAssetsFixList(McInstance mcInstance, bool checkHash,
            [Optional] ref ModLoader.LoaderBase progressFeed)
        {
            // 如果需要检查 Hash，则留到下载时处理，以借助多线程加快检查速度
            if (checkHash)
                return McAssetsListGet(mcInstance).Select(token =>
                {
                    var hash = token.hash;
                    return new DownloadFile(
                        ModDownload.DlSourceAssetsGet(McAssetsUrl(hash)),
                        token.localPath,
                        new ModBase.FileChecker(actualSize: token.size == 0L ? -1 : token.size, hash: hash));
                }).ToList();
            // 如果不检查 Hash，则立即处理
            var result = new List<DownloadFile>();

            List<McAssetsToken> assetsList;
            try
            {
                assetsList = McAssetsListGet(mcInstance);
                McAssetsToken token;
                if (progressFeed is not null)
                    progressFeed.Progress = 0.04d;
                var fileSizes = McAssetsFileSizes(assetsList);
                var progressInterval = Math.Max(1, (assetsList.Count + 99) / 100);
                for (int i = 0, loopTo = assetsList.Count - 1; i <= loopTo; i++)
                {
                    // 初始化
                    token = assetsList[i];
                    // 最多约 100 次通知，避免数万个 ProgressChanged 事件挤占启动线程。
                    if (progressFeed is not null && i % progressInterval == 0)
                        progressFeed.Progress = 0.05d + 0.94d * i / assetsList.Count;
                    // 检查文件是否存在
                    if (fileSizes.TryGetValue(token.localPath, out var size))
                    {
                        if (token.size == 0L || token.size == size) continue;
                    }
                    else
                    {
                        var file = new FileInfo(token.localPath);
                        if (file.Exists && (token.size == 0L || token.size == file.Length)) continue;
                    }
                    // 文件不存在，添加下载
                    var hash = token.hash;
                    result.Add(new DownloadFile(
                        ModDownload.DlSourceAssetsGet(McAssetsUrl(hash)),
                        token.localPath,
                        new ModBase.FileChecker(actualSize: token.size == 0L ? -1 : token.size, hash: hash)));
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "获取实例缺失的资源文件下载列表失败");
            }

            if (progressFeed is not null)
                progressFeed.Progress = 0.99d;
            return result;
        }
    }
}
