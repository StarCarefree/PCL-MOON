using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Java;
using PCL.Core.Minecraft.Java.Parser;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public static partial class ModJava
{
    public static ModLoader.LoaderTask<JavaDownloadRequest, JavaEntry> GetJavaDownloadLoader() =>
        new(Lang.Text("Minecraft.Launch.Java.Task.Download"), DownloadJava);

    private static void DownloadJava(ModLoader.LoaderTask<JavaDownloadRequest, JavaEntry> task)
    {
        var request = task.input;
        if (request.MajorVersion <= 0) throw new ArgumentOutOfRangeException(nameof(request.MajorVersion));
        var token = task.AbortedToken;
        var errors = new List<Exception>();
        foreach (var useMirror in new[] { false, true })
        {
            token.ThrowIfCancellationRequested();
            var source = Lang.Text(useMirror
                ? "Minecraft.Launch.Java.Download.SourceMirror"
                : "Minecraft.Launch.Java.Download.SourceOfficial");
            try
            {
                task.name = Lang.Text("Minecraft.Launch.Java.Task.ResolveJdk", source);
                task.Progress = 0.02;
                var package = useMirror
                    ? InjdkJavaCatalog.SelectPackage(request) ?? throw new InvalidDataException(
                        Lang.Text("Minecraft.Launch.Java.Download.NoPackage", source, request.MajorVersion))
                    : ResolveZulu(request, token);
                token.ThrowIfCancellationRequested();
                ModBase.Log($"[Java] 从 {source} 下载 JDK：{package.Name}，{package.DownloadUrl}");
                task.output = InstallJdk(task, package, token, source);
                return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                errors.Add(ex);
                ModBase.Log(ex, $"[Java] {source} JDK 下载或安装失败", ModBase.LogLevel.Debug);
                if (!useMirror)
                    HintService.Hint(Lang.Text("Minecraft.Launch.Java.Download.FallbackMirror"));
            }
        }
        throw new AggregateException(Lang.Text("Minecraft.Launch.Java.Download.AllFailed", request.MajorVersion), errors);
    }

    private static string FetchJdkMetadata(string url, CancellationToken token) =>
        Requester.FetchString(url, new RequestParam { Timeout = 20000, Retries = 2 }, token);

    private static JavaDownloadPackage ResolveZulu(JavaDownloadRequest request, CancellationToken token)
    {
        var list = JsonNode.Parse(FetchJdkMetadata(JavaDownloadCatalog.ZuluListUrl(request), token)) as JsonArray
                   ?? throw new InvalidDataException("Invalid Zulu package list.");
        var candidates = JavaDownloadCatalog.ZuluCandidates(list, request);
        if (candidates.Length == 0)
            throw new InvalidDataException(Lang.Text("Minecraft.Launch.Java.Download.NoPackage", "Zulu", request.MajorVersion));
        // 列表与详情分别校验，防止接口忽略架构或特性过滤条件。
        var detail = JsonNode.Parse(FetchJdkMetadata(
            "https://api.azul.com/metadata/v1/zulu/packages/" + candidates[0], token)) as JsonObject
                     ?? throw new InvalidDataException("Invalid Zulu package details.");
        return JavaDownloadCatalog.ReadZuluPackage(detail, request);
    }

    private static JavaEntry InstallJdk(ModLoader.LoaderTask<JavaDownloadRequest, JavaEntry> task,
        JavaDownloadPackage package, CancellationToken token, string source)
    {
        var runtimeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft", "runtime");
        // 暂存与正式目录位于同一卷，校验完成后通过重命名一次发布。
        var workDir = Path.Combine(runtimeDir, ".pcl-jdk-downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var archivePath = Path.Combine(workDir, "jdk.zip");
            // Azul 元数据中的 size 为取整值；使用官方 SHA-256 校验完整性。
            var checker = new ModBase.FileChecker(hash: package.Sha256);
            var download = new LoaderDownload(Lang.Text("Minecraft.Launch.Java.Task.DownloadJdk", source),
                [new DownloadFile([package.DownloadUrl], archivePath, checker)]);
            task.name = download.name;
            try
            {
                download.Start();
                while (download.State == ModBase.LoadState.Loading)
                {
                    token.ThrowIfCancellationRequested();
                    task.Progress = 0.1 + download.Progress * 0.65;
                    Thread.Sleep(30);
                }
                token.ThrowIfCancellationRequested();
                if (download.State != ModBase.LoadState.Finished)
                    throw download.Error ?? new IOException("JDK download did not finish.");
            }
            finally
            {
                download.Abort();
                // 等待文件句柄释放后才能清理，取消不会触发备用源下载。
                download.Completion.GetAwaiter().GetResult();
            }

            token.ThrowIfCancellationRequested();
            var checkError = checker.Check(archivePath);
            if (checkError is not null) throw new InvalidDataException(checkError);
            task.name = Lang.Text("Minecraft.Launch.Java.Task.InstallJdk", source);
            var extractDir = Path.Combine(workDir, "extracted");
            ExtractJdk(archivePath, extractDir, token, progress => task.Progress = 0.75 + progress * 0.2);
            var roots = Directory.GetDirectories(extractDir);
            if (roots.Length != 1) throw new InvalidDataException("Unexpected JDK archive layout.");
            var root = roots[0];
            var installation = ValidateJdk(root, task.input);
            token.ThrowIfCancellationRequested();

            var folderName = $"{package.Vendor.ToLowerInvariant()}-{JavaManager.NormalizeVersion(installation.Version)}-" +
                             $"win-{(task.input.Is64Bit ? "x64" : "x86")}-{package.Sha256[..12]}";
            var target = Path.Combine(runtimeDir, folderName);
            if (Directory.Exists(target))
                ValidateJdk(target, task.input);
            else
                Directory.Move(root, target);

            var entry = Javas.AddOrGet(Path.Combine(target, "bin", "java.exe"));
            if (entry is null || !entry.IsEnabled)
                throw new InvalidDataException(Lang.Text("Minecraft.Launch.Java.Download.RegistrationFailed"));
            Javas.SaveConfig();
            task.Progress = 1;
            ModBase.Log($"[Java] JDK 安装完成：{entry}");
            return entry;
        }
        finally
        {
            try { ModBase.DeleteDirectory(workDir); }
            catch (Exception ex) { ModBase.Log(ex, "[Java] 清理 JDK 下载暂存目录失败", ModBase.LogLevel.Debug); }
        }
    }

    private static JavaInstallation ValidateJdk(string root, JavaDownloadRequest request)
    {
        var bin = Path.Combine(root, "bin");
        if (!File.Exists(Path.Combine(bin, "javac.exe")) || !File.Exists(Path.Combine(bin, "javaw.exe")) ||
            !File.Exists(Path.Combine(root, "release")) || !File.Exists(Path.Combine(root, "lib", "jvm.lib")) ||
            (!File.Exists(Path.Combine(bin, "server", "jvm.dll")) &&
             !File.Exists(Path.Combine(root, "jre", "bin", "server", "jvm.dll")) &&
             !File.Exists(Path.Combine(root, "jre", "bin", "client", "jvm.dll"))))
            throw new InvalidDataException("Incomplete JDK archive.");
        var installation = new PeHeaderParser().Parse(Path.Combine(bin, "java.exe"));
        if (installation is null || installation.IsJre || !request.Accepts(installation.Version) ||
            installation.Architecture != (request.Is64Bit ? MachineType.AMD64 : MachineType.I386) ||
            installation.Brand != JavaBrandType.Zulu)
            throw new InvalidDataException(Lang.Text("Minecraft.Launch.Java.Download.IncompatiblePackage"));
        return installation;
    }

    private static void ExtractJdk(string archivePath, string destination, CancellationToken token, Action<double> progress)
    {
        Directory.CreateDirectory(destination);
        var prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("JDK archive entry escapes the installation directory.");
            if (string.IsNullOrEmpty(entry.Name))
                Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyToAsync(output, token).GetAwaiter().GetResult();
            }
            progress((double)++count / archive.Entries.Count);
        }
    }
}
