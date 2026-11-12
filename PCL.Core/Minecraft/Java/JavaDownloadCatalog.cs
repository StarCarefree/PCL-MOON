using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Java;

public sealed record JavaDownloadRequest(
    int MajorVersion,
    bool Is64Bit,
    Version? MinimumVersion = null,
    Version? MaximumVersion = null,
    IReadOnlySet<int>? CompatibleMajors = null)
{
    public bool Accepts(Version version)
    {
        var normalized = JavaManager.NormalizeVersion(version);
        return normalized.Major == MajorVersion &&
               (CompatibleMajors is null || CompatibleMajors.Contains(normalized.Major)) &&
               JavaManager.IsVersionSuitable(version, MinimumVersion ?? new Version(0, 0),
                   MaximumVersion ?? new Version(999, 999, 999));
    }
}

public sealed record JavaDownloadPackage(string Vendor, string Name, string DownloadUrl, string Sha256, long Size);

public static class JavaDownloadCatalog
{
    public static string ZuluListUrl(JavaDownloadRequest request) =>
        $"https://api.azul.com/metadata/v1/zulu/packages/?java_version={request.MajorVersion}" +
        $"&os=windows&arch=x86&hw_bitness={(request.Is64Bit ? 64 : 32)}&archive_type=zip" +
        "&java_package_type=jdk&javafx_bundled=false&crac_supported=false" +
        "&release_status=ga&availability_types=CA&latest=true&page_size=100";

    public static string[] ZuluCandidates(JsonArray packages, JavaDownloadRequest request)
    {
        // 接口可能同时返回不同位数和特性包，必须再次按文件名与版本过滤。
        var suffix = request.Is64Bit ? "x64" : "i686";
        return packages.OfType<JsonObject>()
            .Where(item => Regex.IsMatch(item["name"]?.GetValue<string>() ?? "",
                @"^zulu[\d.]+-ca-jdk[\d.]+-win_" + suffix + @"\.zip$"))
            .Select(item => (Item: item, Version: ReadZuluVersion(item["java_version"])))
            .Where(item => item.Version is not null && request.Accepts(item.Version))
            .OrderByDescending(item => item.Version)
            .Select(item => item.Item["package_uuid"]?.GetValue<string>())
            .Where(id => Guid.TryParse(id, out _))
            .Cast<string>()
            .ToArray();
    }

    public static JavaDownloadPackage ReadZuluPackage(JsonObject item, JavaDownloadRequest request)
    {
        var version = ReadZuluVersion(item["java_version"]);
        var name = item["name"]?.GetValue<string>() ?? "";
        var suffix = request.Is64Bit ? "x64" : "i686";
        if (version is null || !request.Accepts(version) ||
            item["os"]?.GetValue<string>() != "windows" || item["arch"]?.GetValue<string>() != "x86" ||
            item["hw_bitness"]?.GetValue<int>() != (request.Is64Bit ? 64 : 32) ||
            item["archive_type"]?.GetValue<string>() != "zip" ||
            item["java_package_type"]?.GetValue<string>() != "jdk" ||
            item["availability_type"]?.GetValue<string>() != "CA" ||
            item["release_status"]?.GetValue<string>() != "ga" ||
            item["javafx_bundled"]?.GetValue<bool>() != false ||
            item["crac_supported"]?.GetValue<bool>() != false ||
            !Regex.IsMatch(name, @"^zulu[\d.]+-ca-jdk[\d.]+-win_" + suffix + @"\.zip$"))
            throw new InvalidDataException("Zulu package does not match the requested standard JDK.");

        var url = item["download_url"]?.GetValue<string>() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "cdn.azul.com" || uri.AbsolutePath != "/zulu/bin/" + name ||
            !string.IsNullOrEmpty(uri.Query))
            throw new InvalidDataException("Unexpected Zulu download URL.");
        var hash = ReadSha256(item["sha256_hash"]?.GetValue<string>() ?? "");
        var size = item["size"]?.GetValue<long>() ?? 0;
        if (size <= 0) throw new InvalidDataException("Missing Zulu package size.");
        return new JavaDownloadPackage("Zulu", name, url, hash, size);
    }

    public static string ReadSha256(string text)
    {
        var match = Regex.Match(text.Trim(), @"\A([a-fA-F0-9]{64})(?:\s|\z)");
        if (!match.Success) throw new InvalidDataException("Missing or invalid JDK SHA-256 checksum.");
        return match.Groups[1].Value.ToLowerInvariant();
    }

    private static Version? ReadZuluVersion(JsonNode? node)
    {
        if (node is not JsonArray { Count: >= 3 } parts) return null;
        // 启动器版本模型最多使用四段；额外的发行商修订号不参与游戏版本范围判断。
        var values = parts.Take(4).Select(value => value?.GetValue<int>() ?? -1).ToArray();
        if (values.Any(value => value < 0)) return null;
        return values.Length > 3
            ? new Version(values[0], values[1], values[2], values[3])
            : new Version(values[0], values[1], values[2]);
    }
}
