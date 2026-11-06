using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PCL.Core.Minecraft.Server;

public sealed class ServerManager
{
    private const string MetadataFile = ".pcl-moon-server.json";
    private const string LegacyMetadataFile = ".mooncl-server.json";
    private static readonly ConcurrentDictionary<string, ServerSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly HttpClient _http;
     public string RootDirectory { get; private set; }
     /// <summary>Java temp directory for installer and server processes. Null keeps the process default.</summary>
     public string? JavaTempDirectory { get; set; }
     /// <summary>Same ordering as launcher FileSource: 0 mirror first, 1 official first, 2 official only.</summary>
     public int DownloadSource { get; set; } = 1;

    public ServerManager(string rootDirectory, HttpClient? http = null)
    {
         RootDirectory = Path.GetFullPath(rootDirectory);
         _http = http ?? SharedHttp;
     }
 
     public void UseRoot(string rootDirectory)
     {
         var next = Path.GetFullPath(rootDirectory);
         if (next.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase)) return;
         if (Sessions.Values.Any(session => session.IsRunning || session.IsInstalling))
             throw new InvalidOperationException("服务端正在运行或安装，不能切换保存目录。");
         RootDirectory = next;
     }

     public Task<IReadOnlyList<string>> ListCoreVersionsAsync(ServerLoader loader, CancellationToken cancellationToken = default)
         => ServerCoreCatalog.ListVersionsAsync(_http, loader, DownloadSource, cancellationToken);
 
     public Task<IReadOnlyList<string>> ListLoaderVersionsAsync(ServerLoader loader, string minecraftVersion, CancellationToken cancellationToken = default)
         => ServerCoreCatalog.ListLoaderVersionsAsync(_http, loader, minecraftVersion, DownloadSource, cancellationToken);

    public Task<ServerCoreArtifact> ResolveCoreAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        => ServerCoreCatalog.ResolveAsync(_http, profile, DownloadSource, cancellationToken);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ServerCoreCatalog.UserAgent);
        return http;
    }

     public string GetDirectory(ServerProfile profile)
     {
         if (!ServerCommands.IsFolderId(profile.Id)) throw new ArgumentException("服务端 ID 无效。");
         var directory = ServerPaths.Inside(RootDirectory, profile.Id);
         MigrateLegacyMetadata(directory);
         return directory;
     }

    private static void MigrateLegacyMetadata(string directory)
    {
        try
        {
            var legacy = Path.Combine(directory, LegacyMetadataFile);
            var current = Path.Combine(directory, MetadataFile);
            if (!File.Exists(current) && File.Exists(legacy)) File.Move(legacy, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
 
     public ServerSession GetSession(ServerProfile profile) => Sessions.GetOrAdd(GetDirectory(profile), _ => new ServerSession(this, profile.Id));
 
     public IReadOnlyList<ServerProfile> List()
     {
         if (!Directory.Exists(RootDirectory)) return [];
         return Directory.EnumerateDirectories(RootDirectory).Where(path => ServerCommands.IsFolderId(Path.GetFileName(path)))
             .Where(path => { MigrateLegacyMetadata(path); return File.Exists(Path.Combine(path, MetadataFile)); }).Select(path => Load(Path.GetFileName(path)))
             .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
     }
 
     internal ServerProfile Load(string id)
     {
         var path = ServerPaths.Inside(GetDirectory(new ServerProfile { Id = id }), MetadataFile);
         var profile = JsonSerializer.Deserialize<ServerProfile>(File.ReadAllText(path)) ?? throw new IOException("服务端配置为空：" + id);
         if (profile.Id != id) throw new IOException("服务端配置 ID 与目录不匹配：" + id);
         ServerCommands.Validate(profile); return profile;
     }
 
    private void AssignFolder(ServerProfile profile)
    {
        var folder = ServerCommands.FolderName(profile.Name);
        if (ServerCommands.IsFolderId(profile.Id) && Directory.Exists(Path.Combine(RootDirectory, profile.Id)))
        {
            if (profile.Id.Equals(folder, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(Path.Combine(RootDirectory, profile.Id, MetadataFile)) && !string.IsNullOrWhiteSpace(Load(profile.Id).EntryPoint)) return;
        }
        var candidate = folder;
        for (var index = 2; Directory.Exists(Path.Combine(RootDirectory, candidate)); index++) candidate = folder + "-" + index;
        profile.Id = candidate;
    }

    public void Save(ServerProfile profile)
    {
        if (!ServerCommands.IsFolderId(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
        var previousId = profile.Id;
        var previousDirectory = GetDirectory(profile);
        AssignFolder(profile);
        ServerCommands.Validate(profile);
        var directory = GetDirectory(profile);
        var session = Sessions.GetOrAdd(directory, _ => Sessions.TryRemove(previousDirectory, out var moved) ? moved : new ServerSession(this, profile.Id));
        session.Bind(profile.Id);
        lock (session.SyncRoot)
        {
            session.RequireIdle();
            if (File.Exists(Path.Combine(GetDirectory(profile), MetadataFile)) && !string.IsNullOrEmpty(Load(profile.Id).EntryPoint) && string.IsNullOrWhiteSpace(profile.EntryPoint))
                throw new InvalidOperationException("已安装服务端的启动入口不能清空；更换版本请新建服务端。");
            SaveCore(profile);
            if (!previousDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase)) RemoveAbandonedFolder(previousDirectory, previousId);
        }
    }

    private static void RemoveAbandonedFolder(string directory, string id)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var metadata = Path.Combine(directory, MetadataFile);
            if (File.Exists(metadata))
            {
                var stored = JsonSerializer.Deserialize<ServerProfile>(File.ReadAllText(metadata));
                if (stored is null || stored.Id != id || !string.IsNullOrWhiteSpace(stored.EntryPoint)) return;
            }
            Directory.Delete(directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveCore(ServerProfile profile)
    {
        var directory = GetDirectory(profile);
        if (!string.IsNullOrWhiteSpace(profile.EntryPoint)) _ = ServerPaths.Inside(directory, profile.EntryPoint);
        Directory.CreateDirectory(directory);
        AtomicWrite(ServerPaths.Inside(directory, MetadataFile), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
    }

    public Dictionary<string, string> ReadProperties(ServerProfile profile)
    {
        var file = ServerPaths.Inside(GetDirectory(profile), "server.properties");
        return ServerProperties.Parse(File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "");
    }

    public void WriteProperties(ServerProfile profile, IReadOnlyDictionary<string, string> values)
    {
        var session = GetSession(profile);
        lock (session.SyncRoot)
        {
            session.RequireIdle(); Directory.CreateDirectory(GetDirectory(profile));
            var file = ServerPaths.Inside(GetDirectory(profile), "server.properties");
            AtomicWrite(file, ServerProperties.Merge(File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "", values));
        }
    }

    public bool IsEulaAccepted(ServerProfile profile)
    {
        var path = ServerPaths.Inside(GetDirectory(profile), "eula.txt");
        return File.Exists(path) && ServerProperties.Parse(File.ReadAllText(path)).TryGetValue("eula", out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public void AcceptEula(ServerProfile profile)
    {
        var session = GetSession(profile);
        lock (session.SyncRoot)
        {
            session.RequireIdle(); Directory.CreateDirectory(GetDirectory(profile));
            AtomicWrite(ServerPaths.Inside(GetDirectory(profile), "eula.txt"), "# User explicitly accepted https://aka.ms/MinecraftEULA\neula=true\n");
        }
    }

    public async Task InstallAsync(ServerProfile profile, string? packZip, string? installerJar, string? installScript, bool trustScript, CancellationToken cancellationToken = default)
    {
        var originalProfile = profile;
        profile = JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(profile))!;
        ServerCommands.Validate(profile);
        var session = GetSession(profile);
        lock (session.SyncRoot)
        {
            session.RequireIdle();
            var metadata = Path.Combine(GetDirectory(profile), MetadataFile);
            if (File.Exists(metadata) && !string.IsNullOrWhiteSpace(Load(profile.Id).EntryPoint)) throw new InvalidOperationException("此服务端已经安装。更换版本请新建服务端，避免覆盖世界和配置。");
            profile.EntryPoint = ""; SaveCore(profile); session.BeginInstall();
        }
        var status = "安装失败，可重试";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(45));
        try
        {
            await Task.Run(async () =>
            {
                var token = timeout.Token; token.ThrowIfCancellationRequested();
                var directory = GetDirectory(profile);
                if (packZip is not null) { session.Log("正在导入服务端 ZIP…"); ExtractPack(packZip, directory, token); }
                string? script = null;
                if (installScript is not null)
                {
                    if (!trustScript) throw new InvalidOperationException("必须明确确认信任，才能执行安装脚本。");
                    var extension = Path.GetExtension(installScript).ToLowerInvariant();
                    if (extension is not ".bat" and not ".cmd" and not ".ps1") throw new ArgumentException("只支持 bat/cmd/ps1 安装脚本。");
                    script = ServerPaths.Inside(directory, "pcl-moon-install-script" + extension);
                    if (!Path.GetFullPath(installScript).Equals(script, StringComparison.OrdinalIgnoreCase)) File.Copy(installScript, script, true);
                }
                else if (installerJar is null)
                {
                    script = new[] { "install.bat", "install.cmd", "install.ps1" }.Select(name => ServerPaths.Inside(directory, name)).FirstOrDefault(File.Exists);
                    if (script is not null && !trustScript) throw new InvalidOperationException("包内存在 install 脚本，请检查来源并勾选信任后重试；或手动选择加载器安装器。");
                }
                var direct = ServerCommands.IsDirectCore(profile.Loader);
                if (direct && script is null && installerJar is not null)
                    ImportDirectJar(directory, installerJar, trustScript, session, token);
                else if (script is not null)
                     await session.RunInstallerAsync(ServerCommands.Script(profile, directory, script, JavaTempDirectory), token).ConfigureAwait(false);
                else if (direct && DetectLoaderEntry(profile, directory) is not null)
                {
                    ServerCoreTransfer.ValidateJar(ServerPaths.Inside(directory, "server.jar"));
                    session.Log("使用已有 server.jar（只校验归档，不执行，不对照官方校验和）。");
                }
                else if (direct)
                    await DownloadDirectCoreAsync(profile, directory, session, token).ConfigureAwait(false);
                else if (installerJar is not null || packZip is null || DetectLoaderEntry(profile, directory) is null)
                {
                    var jar = ServerPaths.Inside(directory, "pcl-moon-loader-installer.jar");
                    if (installerJar is not null)
                    {
                        if (!trustScript) throw new InvalidOperationException("必须明确确认信任，才能执行本地安装器。");
                        if (!Path.GetExtension(installerJar).Equals(".jar", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择加载器 installer JAR。");
                        if (!Path.GetFullPath(installerJar).Equals(jar, StringComparison.OrdinalIgnoreCase)) File.Copy(installerJar, jar, true);
                    }
                    else await DownloadInstallerAsync(profile, jar, session, token).ConfigureAwait(false);
                     await session.RunInstallerAsync(ServerCommands.Installer(profile, directory, jar, JavaTempDirectory), token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                var entry = (script is not null ? DetectEntry(profile, directory, trustScript) : DetectLoaderEntry(profile, directory))
                    ?? throw new IOException(direct ? "未生成 server.jar。直接核心不会执行安装器，请检查整合包或下载日志。" : "安装程序未生成所选加载器的服务端入口，请检查日志及服务端包。");
                _ = ServerPaths.Inside(directory, entry);
                // Neither imported ZIPs nor scripts may silently accept the user's EULA.
                AtomicWrite(ServerPaths.Inside(directory, "eula.txt"), "eula=false\n");
                profile.EntryPoint = entry;
                lock (session.SyncRoot) SaveCore(profile);
                originalProfile.EntryPoint = entry;
                originalProfile.LoaderVersion = profile.LoaderVersion;
                originalProfile.RequiredJavaMajor = profile.RequiredJavaMajor;
                session.Log("服务端入口：" + entry);
            }, timeout.Token).ConfigureAwait(false);
            status = "安装完成，等待启动";
        }
         catch (OperationCanceledException) { status = cancellationToken.IsCancellationRequested ? "安装已取消" : "安装超时"; throw; }
         catch (Exception ex) { session.Log(ex.Message); throw; }
         finally
         {
             session.EndInstall(status);
             if (cancellationToken.IsCancellationRequested) DeleteIncomplete(profile, session);
         }
     }
 
     private void DeleteIncomplete(ServerProfile profile, ServerSession session)
     {
         var directory = GetDirectory(profile);
         try
         {
             if (File.Exists(Path.Combine(directory, MetadataFile)) && !string.IsNullOrWhiteSpace(Load(profile.Id).EntryPoint)) return;
             if (Directory.Exists(directory)) Directory.Delete(directory, true);
             profile.EntryPoint = "";
             session.Log("已删除未完成的服务端目录。");
         }
         catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Log("取消后删除安装文件失败：" + ex.Message); }
     }

    public static string? DetectLoaderEntry(ServerProfile profile, string directory)
    {
        switch (profile.Loader)
        {
            case ServerLoader.NeoForge:
                var args = ServerPaths.Inside(directory, $"libraries/net/neoforged/neoforge/{profile.LoaderVersion}/win_args.txt");
                return File.Exists(args) ? Path.GetRelativePath(directory, args) : null;
            case ServerLoader.Fabric:
                return File.Exists(ServerPaths.Inside(directory, "fabric-server-launch.jar")) ? "fabric-server-launch.jar" : null;
             case ServerLoader.Vanilla:
             case ServerLoader.Paper:
             case ServerLoader.Purpur:
             case ServerLoader.Spigot:
             case ServerLoader.Folia:
                 return File.Exists(ServerPaths.Inside(directory, "server.jar")) ? "server.jar" : null;
             default:
                 return null;
        }
    }

    public static string? DetectEntry(ServerProfile profile, string directory, bool allowScripts)
    {
        var loaderEntry = DetectLoaderEntry(profile, directory);
        if (loaderEntry is not null) return loaderEntry;
        if (File.Exists(ServerPaths.Inside(directory, "server.jar"))) return "server.jar";
        if (allowScripts)
            foreach (var name in new[] { "run.bat", "start.bat", "start.cmd", "start.ps1" })
                if (File.Exists(ServerPaths.Inside(directory, name))) return name;
        return null;
    }

    private static void ExtractPack(string archive, string directory, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 100000) throw new IOException("服务端包文件数超过上限。");
        var files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')).ToArray();
        if (files.Length == 0) throw new IOException("服务端包为空。");
        var names = files.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
        foreach (var name in names)
            if (name.Split('/').Any(part => part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || Regex.IsMatch(part, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)", RegexOptions.IgnoreCase)))
                throw new IOException("服务端包包含不安全的路径。");
        var directories = names.Where(name => name.Contains('/')).Select(name => name.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var contentDirectories = new[] { "mods", "config", "defaultconfigs", "libraries", "kubejs", "scripts", "world", "resourcepacks", "datapacks", "logs", "plugins" };
        var prefix = directories.Length == 1 && !contentDirectories.Contains(directories[0], StringComparer.OrdinalIgnoreCase) ? directories[0] + "/" : "";
        names = names.Select(name => prefix.Length > 0 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name).ToArray();
        if (names.Any(name => name.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase) || name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("这是客户端整合包清单，请下载整合包作者提供的服务端 ZIP。");
        long total = 0;
        var targets = new List<(ZipArchiveEntry Entry, string Path)>();
        for (var i = 0; i < files.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var entry = files[i]; var relative = names[i];
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new IOException("服务端包不能包含符号链接。");
            var target = ServerPaths.Inside(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Path.GetFileName(target).Equals(MetadataFile, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(target).Equals(LegacyMetadataFile, StringComparison.OrdinalIgnoreCase)) throw new IOException("服务端包不能覆盖启动器元数据。");
            if (relative.Equals("eula.txt", StringComparison.OrdinalIgnoreCase)) continue;
            total = checked(total + entry.Length);
            if (total > 8L * 1024 * 1024 * 1024 || entry.Length > 2L * 1024 * 1024 * 1024) throw new IOException("服务端包解压大小超过安全上限。");
            targets.Add((entry, target));
        }
        foreach (var (entry, target) in targets)
        {
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var output = File.Create(target);
            var buffer = new byte[81920]; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
        }
    }

    public static string[] InstallerUrls(ServerProfile profile, int source, string? fabricInstallerVersion = null)
    {
        ServerCommands.Validate(profile);
        if (ServerCommands.IsDirectCore(profile.Loader)) throw new ArgumentException("该核心不使用加载器安装器。");
        if (profile.Loader == ServerLoader.Fabric)
        {
            if (string.IsNullOrWhiteSpace(fabricInstallerVersion) || !Regex.IsMatch(fabricInstallerVersion, @"\A[0-9.]+\z")) throw new ArgumentException("Fabric 安装器版本无效。");
            return [$"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{fabricInstallerVersion}/fabric-installer-{fabricInstallerVersion}.jar"];
        }
        if (profile.Loader != ServerLoader.NeoForge) throw new ArgumentException("不支持的服务端加载器。");
        if (string.IsNullOrWhiteSpace(profile.LoaderVersion)) throw new ArgumentException("请填写 NeoForge 版本。");
        var path = $"net/neoforged/neoforge/{profile.LoaderVersion}/neoforge-{profile.LoaderVersion}-installer.jar";
        var official = "https://maven.neoforged.net/releases/" + path;
        var mirror = "https://bmclapi2.bangbang93.com/maven/" + path;
        return source switch { 0 => [mirror, official], 2 => [official], _ => [official, mirror] };
    }

    private async Task DownloadInstallerAsync(ServerProfile profile, string destination, ServerSession session, CancellationToken token)
    {
        string? fabricVersion = null;
        if (profile.Loader == ServerLoader.Fabric)
        {
            session.Log("读取 Fabric 官方安装器版本…");
            var xml = XDocument.Parse(await _http.GetStringAsync("https://maven.fabricmc.net/net/fabricmc/fabric-installer/maven-metadata.xml", token).ConfigureAwait(false));
            fabricVersion = xml.Root?.Element("versioning")?.Element("release")?.Value;
        }
        Exception? last = null;
        foreach (var url in InstallerUrls(profile, DownloadSource, fabricVersion))
        {
            token.ThrowIfCancellationRequested(); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                session.Log("下载安装器：" + new Uri(url).Host);
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 512L * 1024 * 1024) throw new IOException("安装器文件过大。");
                await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = File.Create(temporary))
                {
                    var buffer = new byte[81920]; long total = 0; int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        total += count; if (total > 512L * 1024 * 1024) throw new IOException("安装器文件过大。");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                }
                using (var jar = ZipFile.OpenRead(temporary)) if (jar.Entries.Count == 0) throw new IOException("下载内容不是有效 JAR。");
                File.Move(temporary, destination, true); return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            { last = ex; session.Log("此下载源失败，尝试下一个来源：" + ex.Message); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        throw new IOException("无法下载服务端加载器安装器。", last);
    }

    private static void ImportDirectJar(string directory, string installerJar, bool trustScript, ServerSession session, CancellationToken token)
    {
        if (!trustScript) throw new InvalidOperationException("必须明确确认信任，才能把本地 JAR 复制为服务端核心。该文件不会被执行。");
        if (!Path.GetExtension(installerJar).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("直接核心请选择服务端 JAR。该文件不会被当作安装器执行。");
        var destination = ServerPaths.Inside(directory, "server.jar");
        session.Log("导入本地服务端 JAR（仅复制并校验归档，不执行）：" + Path.GetFileName(installerJar));
        ServerCoreTransfer.CopyValidatedJar(installerJar, destination, token);
        session.Log("本地 JAR 未对照官方校验和，只检查了 ZIP/JAR 文件头。");
    }

    private async Task DownloadDirectCoreAsync(ServerProfile profile, string directory, ServerSession session, CancellationToken token)
    {
         if (string.IsNullOrWhiteSpace(profile.MinecraftVersion)) throw new ArgumentException("请填写精确的 Minecraft 版本。");
         if (profile.Loader == ServerLoader.Spigot && string.IsNullOrWhiteSpace(profile.LoaderVersion))
             throw new ArgumentException("请选择 Spigot 版本。");
         var notice = ServerCoreCatalog.MirrorNotice(profile.Loader, DownloadSource);
         if (notice.Length > 0) session.Log(notice);
         if (profile.Loader != ServerLoader.Spigot && ServerCoreCatalog.IsStableDefault(profile.LoaderVersion))
             session.Log(profile.Loader == ServerLoader.Vanilla ? "原版未填写构建，将安装所填的精确 Minecraft 版本。" : "构建留空，将选择稳定版或最新构建，并在完成后写入实际构建号。");
        session.Log("正在读取服务端元数据…");
        var artifact = await ServerCoreCatalog.ResolveAsync(_http, profile, DownloadSource, token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(artifact.MirrorNotice)) session.Log(artifact.MirrorNotice);
        var jar = ServerPaths.Inside(directory, "server.jar");
        session.Log($"下载 {artifact.Loader} {artifact.MinecraftVersion}{(string.IsNullOrEmpty(artifact.Build) ? "" : " 构建 " + artifact.Build)} 到 server.jar（不执行）");
        await ServerCoreTransfer.DownloadVerifiedAsync(_http, artifact.Urls, jar, artifact.ChecksumAlgorithm, artifact.Checksum, artifact.Size, session.Log, token).ConfigureAwait(false);
        if (profile.Loader != ServerLoader.Vanilla && !string.IsNullOrEmpty(artifact.Build)) profile.LoaderVersion = artifact.Build;
        if (artifact.JavaMajor is int major)
        {
            profile.RequiredJavaMajor = major;
            session.Log($"已记录所需 Java 主版本 {major}。启动时会检查 java.exe。");
        }
        else session.Log("元数据未提供 Java 主版本，启动时不会强制检查。");
    }

    private static void AtomicWrite(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
