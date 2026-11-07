using System.Text.Json;
using PCL.Core.App.Essentials;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
/// PCL-MOON 只从自己的 GitHub Release 检查和下载更新，不再走 PCL CE 的更新源。
/// </summary>
public static class PclMoonRelease
{
    public const string Owner = "StarCarefree";
    public const string Repo = "PCL-MOON";
    public const string Page = "https://github.com/StarCarefree/PCL-MOON/releases";
    public const string LatestPage = Page + "/latest";

    public static string TagPage(string versionBaseName) => Page + "/v" + versionBaseName;
}

public class UpdatesGitHubReleaseModel : IUpdateSource
{
    private const string ApiUrl =
        "https://api.github.com/repos/" + PclMoonRelease.Owner + "/" + PclMoonRelease.Repo + "/releases?per_page=30";

    private List<GitHubRelease> _cache;

    public string SourceName { get; set; } = "GitHub";

    public bool IsAvailable() => true;

    public bool RefreshCache()
    {
        _cache = FetchReleases();
        return _cache.Count > 0;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var picked = Pick(EnsureCache(), channel, arch);
        return ToVersion(picked.Release, picked.Asset);
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var latest = GetLatestVersion(channel, arch);
        return currentVersion >= SemVer.Parse(latest.VersionName);
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        return new VersionAnnouncementDataModel { Content = [] };
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var loaders = new List<ModLoader.LoaderBase>();
        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(Lang.Text("Update.Task.GetDownloadInfo"), load =>
        {
            var picked = Pick(EnsureCache(), channel, arch);
            if (string.IsNullOrWhiteSpace(picked.Asset.Url))
                throw new Exception("PCL-MOON Release 没有可下载的程序");
            load.output =
            [
                new DownloadFile([picked.Asset.Url], output, useBrowserUserAgent: true)
            ];
        }));
        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"), []));
        return loaders;
    }

    private List<GitHubRelease> EnsureCache()
    {
        if (_cache is null)
            RefreshCache();
        return _cache ?? [];
    }

    private VersionDataModel ToVersion(GitHubRelease release, GitHubAsset asset)
    {
        return new VersionDataModel
        {
            Source = SourceName,
            VersionName = VersionFromTag(release.Tag),
            VersionCode = 0,
            Sha256 = ReadSha256(release, asset),
            Changelog = UpdateHelper.CleanChangelog(release.Body)
        };
    }

    private static string ReadSha256(GitHubRelease release, GitHubAsset asset)
    {
        var fromDigest = NormalizeSha256(asset.Digest);
        if (fromDigest.Length == 64)
            return fromDigest;
        var side = release.Assets.FirstOrDefault(item =>
            string.Equals(item.Name, asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (side is null || string.IsNullOrWhiteSpace(side.Url))
            return "";
        var text = HttpRequest.GetStringAsync(side.Url).GetAwaiter().GetResult();
        var token = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return NormalizeSha256(token);
    }

    internal static List<GitHubRelease> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new Exception("PCL-MOON Release 响应不是列表");
        var list = new List<GitHubRelease>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            var assets = new List<GitHubAsset>();
            if (item.TryGetProperty("assets", out var assetEl) && assetEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetEl.EnumerateArray())
                {
                    assets.Add(new GitHubAsset(
                        ReadString(asset, "name"),
                        ReadString(asset, "browser_download_url"),
                        ReadString(asset, "digest")));
                }
            }

            list.Add(new GitHubRelease(
                ReadString(item, "tag_name"),
                ReadString(item, "body"),
                item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                assets));
        }

        return list;
    }

    internal static (GitHubRelease Release, GitHubAsset Asset) Pick(
        IReadOnlyList<GitHubRelease> releases, UpdateChannel channel, UpdateArch arch)
    {
        foreach (var release in releases)
        {
            if (channel == UpdateChannel.stable && release.Prerelease)
                continue;
            var asset = release.Assets.FirstOrDefault(item => Matches(item.Name, arch));
            if (asset is not null)
                return (release, asset);
        }

        throw new Exception($"PCL-MOON 没有适合 {channel}/{arch} 的 Release");
    }

    internal static bool Matches(string name, UpdateArch arch)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var normalized = name.ToLowerInvariant();
        if (normalized.EndsWith(".sha256") || normalized.EndsWith(".asc") ||
            normalized.EndsWith(".sigstore.json") || normalized.EndsWith(".zip"))
            return false;
        if (!normalized.EndsWith(".exe"))
            return false;
        var arm = normalized.Contains("arm64") || normalized.Contains("aarch64");
        var x64 = normalized.Contains("x64") || normalized.Contains("amd64") || normalized.Contains("win64");
        return arch == UpdateArch.arm64 ? arm : x64 && !arm;
    }

    internal static string VersionFromTag(string tag)
    {
        var name = (tag ?? "").Trim();
        if (name.Length > 1 && (name[0] == 'v' || name[0] == 'V') && char.IsDigit(name[1]))
            name = name[1..];
        return name;
    }

    internal static string NormalizeSha256(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return "";
        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];
        value = value.Trim().ToLowerInvariant();
        foreach (var ch in value)
        {
            if (ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return "";
        }

        return value.Length == 64 ? value : "";
    }

    private static List<GitHubRelease> FetchReleases()
    {
        using var response = HttpRequest.Create(ApiUrl)
            .WithHeader("Accept", "application/vnd.github+json")
            .WithHeader("X-GitHub-Api-Version", "2022-11-28")
            .SendAsync()
            .GetAwaiter()
            .GetResult();
        return ParseReleases(response.AsString());
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        return value.GetString() ?? "";
    }

    internal sealed record GitHubAsset(string Name, string Url, string Digest);

    internal sealed record GitHubRelease(string Tag, string Body, bool Prerelease, List<GitHubAsset> Assets);
}
