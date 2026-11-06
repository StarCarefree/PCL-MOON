using System.IO;
using System.Security.Cryptography;

namespace PCL;

internal static class WebUiAssets
{
    public const string HostName = "pcl.webui";
    private static readonly string[] Files = ["index.html", "app.css", "explorer-identity.js", "app.js"];

    public static string Extract()
    {
        var assembly = typeof(WebUiAssets).Assembly;
        var assets = new Dictionary<string, byte[]>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files)
        {
            using var stream = assembly.GetManifestResourceStream("PCL.WebUi.www." + file)
                ?? throw new InvalidOperationException("缺少页面资源：" + file);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            assets[file] = memory.ToArray();
            hash.AppendData(assets[file]);
        }
        // Different launcher builds must not overwrite the assets of a running instance.
        var version = Convert.ToHexString(hash.GetHashAndReset())[..16];
        var dir = Path.Combine(UserDataFolder(), "www", version);
        Directory.CreateDirectory(dir);
        foreach (var (file, bytes) in assets)
        {
            var destination = Path.Combine(dir, file);
            if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes)) continue;
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return dir;
    }

    public static string UserDataFolder()
    {
        // Honor WebView2's standard process-level profile override (also used by isolated UI tests).
        var requested = Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
        var dir = string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL-MOON", "WebUi", "ebwebview")
            : Path.GetFullPath(requested);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
