using System.IO;
using PCL.Network;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;

using PCL.Core.Utils.Validate;
namespace PCL;

/// <summary>UI-thread owned coordinator; native launcher remains the installer/login/launch authority.</summary>
internal sealed class LauncherLibrary : IDisposable
{
    private sealed record Settings(string CatalogUrl = "", string QuarkServiceUrl = "", string[]? CatalogUrls = null);
    private sealed record Installed(string Id, string Version);
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "PCL", "launcher-library.json");
    private readonly QuarkOpenSessionStore _quarkSessions = new(AppContext.BaseDirectory);
    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(30) };
    private readonly PackTransfer _transfer;
    private readonly HttpClient _catalogHttp = new(new HttpClientHandler
    {
        UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All, MaxAutomaticRedirections = 5
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly PackCatalogClient _catalogClient;
    private string _catalogSource = "";
    private Settings _settings = new(CatalogUrls: []);
    private List<PackDefinition> _packs = [PackCatalog.BuiltIn];
    internal IReadOnlyList<PackDefinition> CatalogPacks => _packs;
    internal async Task InstallSharedAsync(PackDefinition pack)
    {
        pack = pack with { LocalArchive = "" };
        if (string.IsNullOrWhiteSpace(pack.DownloadUrl) && string.IsNullOrWhiteSpace(pack.ShareUrl))
            throw new InvalidDataException("这个分享没有公开下载地址。");
        if (_work is not null) throw new InvalidOperationException("已有整合包任务在进行。");
        await RunAsync(token => InstallAsync(pack, string.IsNullOrWhiteSpace(pack.DownloadUrl), token));
        if (_error.Length > 0) throw new InvalidOperationException(_error);
    }
    private List<ServerDefinition> _servers = [];
    private readonly Dictionary<string, (McInstance Instance, string Version)> _installed = new();
    private readonly Dictionary<string, ExternalUpdatePlan> _updates = new();
    private readonly Dictionary<string, PackDefinition> _managed = new();
    private readonly HashSet<string> _publishedManaged = new(StringComparer.Ordinal);
    private readonly ExternalPackCheckOnce _once = new();
    private string _checkNotice = "";
    private HintType _checkNoticeType = HintType.Info;
    private bool _autoFinished;
    private string _autoFolder = "";
    public static bool PackWorkInProgress { get; private set; }
    public static bool VersionUpdating { get; private set; }
    public static double VersionUpdateProgress { get; private set; }

    internal static bool IsVersionUpdatePhase(string? phase) =>
        !string.IsNullOrEmpty(phase) && phase.Contains("更新器", StringComparison.Ordinal);

    private void PublishWork()
    {
        var updating = PackWorkInProgress && IsVersionUpdatePhase(_phase);
        var progress = updating ? Math.Clamp(_progress, 0, 1) : 0;
        if (updating == VersionUpdating && Math.Abs(progress - VersionUpdateProgress) < 0.0001) return;
        VersionUpdating = updating;
        VersionUpdateProgress = progress;
        ModBase.RunInUi(static () => ModMain.frmLaunchLeft?.ApplyVersionUpdateChrome());
    }
    public bool TryTakeCheckNotice(out string text, out HintType type)
    {
        text = _checkNotice;
        type = _checkNoticeType;
        _checkNotice = "";
        return text.Length > 0;
    }
    private CancellationTokenSource? _work;
    private ModLoader.LoaderCombo<string>? _installer;
    private ModLoader.LoaderCombo<string>? _downloadTask;
    private string _selected = PackCatalog.StarRailId;
    private string _phase = "就绪";
    private string _error = "";
    private JsonObject? _presetQuarkReply;
    private string _taskScope = "library";
    private bool _selectCreatedInstance;
    private double _progress;
    private DateTime _nextScan;
    private string _lastDirectory = "";
    private BitmapSource? _avatarSource;
    private DateTime _nextRefresh = DateTime.UtcNow.AddSeconds(4);
    private string _avatar = "";
    private bool _disposed;
    public bool IsBusy => _work is not null;

    public LauncherLibrary()
    {
        _transfer = new PackTransfer(_http);
        _catalogClient = new PackCatalogClient(_catalogHttp);
        try
        {
            if (File.Exists(_settingsPath)) _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath)) ?? new();
            var urls = PackCatalogSources.Normalize(_settings.CatalogUrls, _settings.CatalogUrl);
            _settings = new Settings(urls.FirstOrDefault() ?? "", _settings.QuarkServiceUrl ?? "", urls);
        }
        catch (Exception ex)
        {
            _settings = new(CatalogUrls: []);
            ModBase.Log(ex, "读取整合包服务配置失败", ModBase.LogLevel.Debug);
        }
    }

    public JsonObject Capture(bool scan = true)
    {
        if (scan) ScanInstalled();
        var account = ProfileService.Current;
        var source = ModMain.frmLoginProfileSkin?.Skin?.ImgBack?.Source as BitmapSource;
        if (!ReferenceEquals(source, _avatarSource))
        {
            _avatarSource = source;
            _avatar = "";
            if (source is not null)
            {
                using var stream = new MemoryStream();
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(source)); png.Save(stream);
                _avatar = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            }
        }
        var sessions = GameLinkSession.Snapshot();
        var instances = new JsonArray();
        foreach (var instance in Instances())
        {
            var session = sessions.FirstOrDefault(item => string.Equals(item.Instance, instance.Name, StringComparison.Ordinal));
            instances.Add(new JsonObject { ["id"] = "instance:" + instance.Name, ["name"] = instance.Name,
                ["version"] = instance.Desc, ["minecraft"] = instance.Info.VanillaName ?? "",
                ["vanilla"] = IsVanilla(instance),
                ["running"] = session is not null,
                ["session"] = session?.SessionId ?? "",
                ["linkState"] = session?.State ?? "",
                ["linkAddress"] = session?.Address ?? "",
                ["selected"] = _selected == "instance:" + instance.Name });
        }
        var presets = new JsonArray();
        foreach (var pack in _packs)
        {
            _installed.TryGetValue(pack.Id, out var installed);
            presets.Add(new JsonObject
            {
                ["id"] = pack.Id, ["name"] = pack.Name, ["description"] = pack.Description,
                ["version"] = pack.Version,
                ["author"] = pack.Author, ["loader"] = pack.Loader,
                ["updateVersion"] = _updates.TryGetValue(pack.Id, out var update) ? update.Version : "",
                ["installedVersion"] = installed.Version ?? "", ["minecraft"] = pack.Minecraft,
                ["icon"] = pack.Icon, ["background"] = pack.Background,
                ["downloadUrl"] = pack.DownloadUrl, ["shareUrl"] = pack.ShareUrl,
                ["installed"] = installed.Instance is not null,
                ["updateAvailable"] = _updates.ContainsKey(pack.Id)
            });
        }
        var servers = new JsonArray();
        foreach (var server in BrowseServerStore.Load().Concat(_servers.Where(item => !item.Directory && !item.Local))) servers.Add(ServerJson(server));
        var type = account?.ProfileType switch
        {
            ProfileType.Microsoft => "Microsoft / Mojang",
            ProfileType.Authlib or ProfileType.YggdrasilConnect => account.ServerName ?? "LittleSkin / Authlib",
            ProfileType.Offline => "离线账号",
            _ => "点击登录"
        };
        return new JsonObject
        {
            ["account"] = new JsonObject { ["name"] = account?.UserName ?? "未登录", ["type"] = type, ["avatar"] = account is null ? "" : _avatar },
            ["directory"] = ModFolder.mcFolderSelected ?? "", ["instances"] = instances, ["presets"] = presets, ["servers"] = servers, ["selected"] = _selected,
            ["task"] = new JsonObject { ["busy"] = _work is not null || ModLaunch.isLaunching,
                ["gameRunning"] = ModWatcher.hasRunningMinecraft,
                ["phase"] = ModLaunch.isLaunching ? "正在启动游戏" : ModWatcher.hasRunningMinecraft ? "游戏运行中" : _phase,
                ["indeterminate"] = _work is not null && _installer is null && _downloadTask is null && !_phase.StartsWith("下载整合包", StringComparison.Ordinal),
                ["progress"] = ModLaunch.isLaunching ? ModLaunch.mcLaunchLoader.Progress : _installer is not null ? .25 + .75 * _installer.Progress
                    : _downloadTask is not null ? .25 * _downloadTask.Progress : _progress,
                ["scope"] = _taskScope, ["error"] = _error },
            ["config"] = new JsonObject { ["catalogUrl"] = _settings.CatalogUrl,
                ["catalogUrls"] = new JsonArray(_settings.CatalogUrls!.Select(url => (JsonNode?)JsonValue.Create(url)).ToArray()),
                ["catalogSource"] = _catalogSource, ["quarkServiceUrl"] = _settings.QuarkServiceUrl,
                ["catalogDefaultActive"] = _settings.CatalogUrls!.Length == 0,
                ["catalogDefaultUrls"] = new JsonArray(PackCatalogSources.DefaultUrls.Select(url => (JsonNode?)JsonValue.Create(url)).ToArray()),
                ["quarkAuthorized"] = HasQuarkSession() }
        };
    }

    private static McInstance[] Instances()
    {
        if (string.IsNullOrEmpty(ModFolder.mcFolderSelected)) return [];
        var versions = Path.GetFullPath(Path.Combine(ModFolder.mcFolderSelected, "versions")) + Path.DirectorySeparatorChar;
        return ModInstanceList.mcInstanceList.ToArray().SelectMany(pair => pair.Value.ToArray())
            .Where(i => i.PathInstance.StartsWith(versions, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.Name).Select(g => g.First()).ToArray();
    }

    private void ScanInstalled(bool force = false)
    {
        var folder = FolderKey(ModFolder.mcFolderSelected);
        if (!force && folder == _lastDirectory && DateTime.UtcNow < _nextScan) return;
        if (folder != _lastDirectory)
        {
            _updates.Clear();
            _once.Clear();
            _autoFinished = false;
            _nextRefresh = DateTime.UtcNow.AddSeconds(2);
        }
        _lastDirectory = folder; _nextScan = DateTime.UtcNow.AddSeconds(3);
        _installed.Clear();
        _managed.Clear();
        foreach (var instance in Instances().OrderBy(i => File.GetLastWriteTimeUtc(Path.Combine(i.PathInstance, "PCL", "launcher-pack.json"))))
        {
            if (!File.Exists(Path.Combine(instance.PathInstance, instance.Name + ".json")) || File.Exists(Path.Combine(instance.PathInstance, ".pclignore")) || ExternalPackCheckOnce.IsExcludedLocalVersion(instance.Name, instance.PathInstance)) continue;
            try
            {
                var marker = Path.Combine(instance.PathInstance, "PCL", "launcher-pack.json");
                var saved = File.Exists(marker) ? JsonSerializer.Deserialize<Installed>(File.ReadAllText(marker)) : null;
                if (saved is not null) _installed[saved.Id] = (instance, saved.Version);
                else if (!_installed.ContainsKey(PackCatalog.StarRailId) && instance.Name.Contains("StarRailHarpyExpress", StringComparison.OrdinalIgnoreCase))
                    _installed[PackCatalog.StarRailId] = (instance, ExternalPackUpdater.ReadLocalVersion(instance.PathIndie));
            }
            catch (Exception ex) { ModBase.Log(ex, "读取整合包实例标记失败", ModBase.LogLevel.Debug); }
            TryRegisterManaged(instance);
        }
        foreach (var id in _updates.Keys.Where(id => !_installed.ContainsKey(id)).ToArray()) _updates.Remove(id);
        PublishManaged();
        _selected = PackCatalog.ResolveSelection(_selected, _packs.Select(p => p.Id), Instances().Select(i => "instance:" + i.Name));
    }

    private void TryRegisterManaged(McInstance instance)
    {
        string root;
        try { root = ExternalPackCheckOnce.Normalize(instance.PathIndie); }
        catch (Exception ex) { ModBase.Log(ex, "读取实例隔离目录失败", ModBase.LogLevel.Debug); return; }
        ExternalUpdateSource? source;
        try { source = SourceFor(root, instance); }
        catch (Exception ex) { ModBase.Log(ex, "读取更新配置失败", ModBase.LogLevel.Debug); return; }
        if (source is not { Enabled: true }) return;
        if (_installed.Values.Any(item => SameRoot(item.Instance, root))) return;
        var id = ExternalPackCheckOnce.ManagedPackId(instance.Name, root);
        var version = "";
        try { version = ExternalPackUpdater.ReadLocalVersion(root); }
        catch (Exception ex) { ModBase.Log(ex, "读取整合包内部版本失败", ModBase.LogLevel.Debug); }
        if (string.IsNullOrWhiteSpace(version)) version = instance.Desc;
        _installed[id] = (instance, version);
        _managed[id] = new PackDefinition
        {
            Id = id, Name = instance.Name, Version = string.IsNullOrWhiteSpace(version) ? "Packwiz" : version,
            Description = "已启用 mod-loading-screen 更新。启动器只比较 Packwiz 清单，不把它当成整包版本。"
        };
    }

    private static bool SameRoot(McInstance instance, string root)
    {
        try { return string.Equals(ExternalPackCheckOnce.Normalize(instance.PathIndie), root, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    private static ExternalUpdateSource? SourceFor(string root, McInstance instance)
    {
        var local = ExternalPackUpdater.ReadSource(root);
        if (local is not null) return local;
        var marker = Path.Combine(instance.PathInstance, "PCL", "launcher-pack.json");
        var pack = instance.Name.Contains("StarRailHarpyExpress", StringComparison.OrdinalIgnoreCase) || File.Exists(marker);
        return pack ? ExternalPackUpdater.ReadArchiveConfig(PackCatalog.TestArchive) : null;
    }

    private void PublishManaged()
    {
        foreach (var id in _publishedManaged.Where(id => !_managed.ContainsKey(id)).ToArray())
        {
            _packs.RemoveAll(pack => pack.Id == id);
            _publishedManaged.Remove(id);
        }
        foreach (var pack in _managed.Values)
        {
            if (_packs.All(item => item.Id != pack.Id)) _packs.Add(pack);
            _publishedManaged.Add(pack.Id);
        }
    }

    public async Task CheckManagedOnceAsync()
    {
        var folder = FolderKey(ModFolder.mcFolderSelected);
        if (_autoFinished && string.Equals(folder, _autoFolder, StringComparison.OrdinalIgnoreCase)) return;
        if (_disposed || _work is not null || folder.Length == 0 || ModLaunch.isLaunching) return;
        if (ModInstanceList.mcInstanceListLoader.State != ModBase.LoadState.Finished) return;
        await RunAsync(token => CheckInstalledAsync(token, false));
        // A failed, timed-out or cancelled attempt still counts. HybridTick must not restart it every 750ms.
        if (string.Equals(FolderKey(ModFolder.mcFolderSelected), folder, StringComparison.OrdinalIgnoreCase))
        {
            _autoFinished = true;
            _autoFolder = folder;
        }
    }

    private async Task CheckInstalledAsync(CancellationToken token, bool manual)
    {
        ScanInstalled(true);
        _phase = "检查整合包更新";
        _checkNotice = "";
        var found = 0;
        var checkedCount = 0;
        var errors = new List<string>();
        foreach (var (id, installed) in _installed.ToArray())
        {
            token.ThrowIfCancellationRequested();
            string root;
            try { root = ExternalPackCheckOnce.Normalize(installed.Instance.PathIndie); }
            catch (Exception ex) { ModBase.Log(ex, "读取实例目录失败", ModBase.LogLevel.Debug); continue; }
            if (!manual && _once.IsDone(root)) continue;
            try
            {
                var source = SourceFor(root, installed.Instance);
                if (source is not { Enabled: true })
                {
                    _updates.Remove(id);
                    if (!manual) _once.MarkDone(root);
                    continue;
                }
                checkedCount++;
                var plan = await new ExternalPackUpdater(_http).CheckAsync(root, token, source);
                if (!manual) _once.MarkDone(root);
                if (plan is null) _updates.Remove(id);
                else { _updates[id] = plan; found++; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                ModBase.Log(ex, "检查整合包更新失败", ModBase.LogLevel.Debug);
                if (!manual) _once.MarkDone(root);
                errors.Add(installed.Instance.Name + "：" + ex.Message);
            }
        }
        if (found > 0) _phase = "发现 Packwiz 更新";
        else if (errors.Count > 0) _phase = "检查失败";
        else if (checkedCount > 0) _phase = "已检查，没有 Packwiz 更新";
        else _phase = "就绪";
        _error = errors.Count == 0 ? "" : errors.Count == 1 ? errors[0] : errors.Count + " 个实例检查失败：" + errors[0];
        if (manual || (found == 0 && errors.Count == 0 && checkedCount == 0)) return;
        if (found > 0)
        {
            _checkNotice = "发现整合包的 Packwiz 更新。打开浏览页后可更新游戏；这不是整包版本号升级。";
            _checkNoticeType = HintType.Info;
            if (errors.Count > 0) _checkNotice += " 另有实例检查失败：" + errors[0];
        }
        else if (errors.Count > 0)
        {
            _checkNotice = "整合包更新检查失败：" + errors[0];
            _checkNoticeType = HintType.Error;
        }
        else
        {
            _checkNotice = "已检查，没有 Packwiz 更新。";
            _checkNoticeType = HintType.Success;
        }
    }

    private static string FolderKey(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        try { return ExternalPackCheckOnce.Normalize(folder); }
        catch (Exception) { return folder.Trim(); }
    }

    public async Task RefreshIfNeededAsync(FormMain form)
    {
        if (_disposed || _work is not null || DateTime.UtcNow < _nextRefresh || string.IsNullOrEmpty(ModFolder.mcFolderSelected) ||
            ModInstanceList.mcInstanceListLoader.State != ModBase.LoadState.Finished || form.PanMsgBackground.Visibility == System.Windows.Visibility.Visible) return;
        _nextRefresh = DateTime.UtcNow.AddMinutes(5);
        await HandleAsync(new JsonObject { ["operation"] = "refresh" }, form);
    }

    public async Task HandleAsync(JsonObject message, FormMain form)
    {
        var operation = message["operation"]?.GetValue<string>() ?? "";
        if (operation == "cancel")
        {
            _work?.Cancel(); _downloadTask?.Abort(); _installer?.Abort();
            if (_work is null && ModLaunch.isLaunching) ModLaunch.AbortActiveLaunches();
            return;
        }
        if (_disposed || _work is not null || (ModLaunch.isLaunching && operation is not ("primary" or "select"))) return;
        _error = "";
        if (operation == "select")
        {
            var id = message["id"]?.GetValue<string>() ?? "";
            if (_packs.Any(p => p.Id == id)) _selected = id;
            else if (Instances().FirstOrDefault(i => "instance:" + i.Name == id) is { } instance)
            { SelectInstance(instance); _selected = id; }
            return;
        }
        if (operation == "login") { form.ShowHybridAccount(); return; }
        if (operation == "directory") { form.PageChange(FormMain.PageType.InstanceSelect); return; }
        if (operation == "import") { ModModpack.ModpackInstall(); return; }
        if (operation is "quarkList" or "quarkDownload") { await HandlePresetQuarkAsync(message, operation); return; }
        if (operation == "configure")
        {
            string[]? sources = null;
            if (message.TryGetPropertyValue("catalogUrls", out var sourceNode))
            {
                if (sourceNode is not JsonArray sourceArray) throw new InvalidDataException("目录源必须是地址数组。");
                sources = sourceArray.Select(item => item?.GetValue<string>() ?? "").ToArray();
            }
            var urls = PackCatalogSources.Normalize(sources, message["catalogUrl"]?.GetValue<string>() ?? "");
            var quark = message["quarkServiceUrl"]?.GetValue<string>()?.Trim() ?? "";
            if (quark.Length > 0) PackCatalog.HttpUri(quark);
            var settings = new Settings(urls.FirstOrDefault() ?? "", quark, urls);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath + ".tmp", JsonSerializer.Serialize(settings));
            File.Move(_settingsPath + ".tmp", _settingsPath, true); _settings = settings;
            if (urls.Length == 0) { _packs = [PackCatalog.BuiltIn]; _servers = []; _catalogSource = ""; }
        }
        if (operation is "refresh" or "configure" or "checkUpdates")
        {
            await RunAsync(async token =>
            {
                if (operation != "checkUpdates")
                {
                     // No configured source means the shipped Gitee default, never "no catalog."
                    var sources = PackCatalogSources.Effective(_settings.CatalogUrls);
                    _phase = "获取预设目录";
                    var result = await _catalogClient.FetchAsync(sources, token,
                        (index, count, _) => _phase = $"获取预设目录源 {index}/{count}");
                    var packs = result.Packs.ToList();
                    if (packs.All(p => p.Id != PackCatalog.StarRailId)) packs.Insert(0, PackCatalog.BuiltIn);
                    _packs = packs;
                    _servers = result.Servers.ToList();
                    _catalogSource = result.SourceUrl;
                    _phase = result.NotModified ? "预设目录未改变（304）" : "预设目录已刷新";
                    if (!_packs.Any(p => p.Id == _selected) && !_selected.StartsWith("instance:")) _selected = _packs[0].Id;
                }
                await CheckInstalledAsync(token, operation == "checkUpdates");
            }, operation == "checkUpdates" ? "library" : "catalog");
            return;
        }
        if (operation == "authorizeQuark")
        {
            if (ModMain.MyMsgBox("将打开夸克官方授权页。完成后，通过夸克下载只会把目标整合包转存到你的网盘目录 PCL-MOON，不会转存整个分享，也不会读取浏览器 Cookie。",
                "授权夸克网盘", "打开授权页", "取消") != 1) return;
            await RunAsync(AuthorizeQuarkAsync);
            return;
        }
        if (operation == "joinServer")
        {
            await JoinServerAsync(message, form);
            return;
        }
        if (operation is "addLocalServer" or "removeLocalServer")
        {
            if (operation == "removeLocalServer") BrowseServerStore.Remove(message["id"]?.GetValue<string>() ?? "");
            else await BrowseServerStore.AddAsync(message["name"]?.GetValue<string>() ?? "", message["address"]?.GetValue<string>() ?? "", message["minecraft"]?.GetValue<string>() ?? "", message["description"]?.GetValue<string>() ?? "", "");
            return;
        }
        if (operation is not ("primary" or "installQuark")) return;
        if (!(operation == "primary" && _selected.StartsWith("instance:")) && (ModWatcher.hasRunningMinecraft || ModLaunch.isLaunching)) throw new InvalidOperationException("请先关闭游戏，再执行下载、更新或切换启动。");
        ScanInstalled(true);
        if (operation == "primary" && _selected.StartsWith("instance:"))
        {
            if (Instances().FirstOrDefault(i => "instance:" + i.Name == _selected) is { } instance)
            { SelectInstance(instance); Launch(form); return; }
            _selected = _packs[0].Id;
        }
        var pack = _packs.FirstOrDefault(p => p.Id == _selected) ?? throw new InvalidOperationException("请先选择整合包。");
        if (operation == "primary" && _installed.TryGetValue(pack.Id, out var existing))
        {
            if (_updates.TryGetValue(pack.Id, out var plan))
            {
                var prompt = "将按配置文件中的 Packwiz 地址更新。目标没有 mod-loading-screen 或本地配置文件时，仍使用整合包配置，不因文件不存在而跳过。\n来源：" + plan.Url +
                    "\n此更新不等同于整包版本升级。将备份 mods、config、resourcepacks 等受管状态，不处理存档。";
                if (ModMain.MyMsgBox(prompt + (plan.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ? "\n注意：当前更新源使用未加密 HTTP，请确认信任该来源。" : ""),
                    "更新整合包", "更新游戏", "取消", isWarn: true) != 1) return;
                await RunAsync(async token =>
                {
                    _phase = "备份并运行整合包更新器";
                    _progress = 0;
                    PublishWork();
                    var java = await Task.Run(() =>
                    {
                        lock (ModJava.javaLock) return ModJava.JavaSelect("更新需要 Java 21 或更高版本。", new Version(21, 0), relatedInstance: existing.Instance, enforceVersionRange: true);
                    }, token);
                    if (java is null) throw new InvalidOperationException("未找到 Java 21，请在设置中添加 Java。");
                    await new ExternalPackUpdater(_http, java.Installation.JavaExePath).ApplyAsync(existing.Instance.PathIndie, plan, new Progress<double>(p => { _progress = p; PublishWork(); }), token, PackCatalog.TestArchive);
                    _updates.Remove(pack.Id); _phase = "更新完成";
                });
            }
            else { SelectInstance(existing.Instance); Launch(form); }
            return;
        }
        var useQuark = operation == "installQuark";
        if (!string.IsNullOrEmpty(pack.ShareUrl) && !HasQuarkSession())
        {
            var ordinary = !useQuark && !string.IsNullOrEmpty(pack.DownloadUrl);
            if (ModMain.MyMsgBox("尚未授权夸克网盘。是否现在授权，以便只转存这一个整合包后再下载？不会转存整个分享，也不会读取浏览器 Cookie。",
                "使用夸克网盘下载", "授权并下载", ordinary ? "继续普通下载" : "取消") != 1)
            {
                if (!ordinary) { _phase = "已取消"; return; }
            }
            else
            {
                await RunAsync(AuthorizeQuarkAsync);
                if (!HasQuarkSession()) return;
                useQuark = true;
            }
        }
        await RunAsync(token => InstallAsync(pack, useQuark, token));
    }

    internal static void SelectInstance(McInstance instance)
    {
        if (!instance.Check()) throw new InvalidDataException("所选实例不可启动，请在实例管理中修复。");
        ModInstanceList.McMcInstanceSelected = instance; States.Game.SelectedInstance = instance.Name;
        ModMain.frmLaunchLeft?.RefreshButtonsUI();
    }

    private static void Launch(FormMain form)
    {
        form.PageChange(FormMain.PageType.Launch);
        var launch = ModMain.frmLaunchLeft ?? throw new InvalidOperationException("启动页面尚未就绪，请稍后重试。");
        launch.RefreshButtonsUI();
        if (ProfileService.Current is null) throw new InvalidOperationException("请点击左上角头像，先登录或创建离线账号。");
        launch.LaunchButtonClick();
    }

    private static JsonObject ServerJson(ServerDefinition server)
    {
        var packs = new JsonArray();
        foreach (var offer in server.Packs)
            packs.Add(new JsonObject
            {
                ["id"] = offer.Id, ["name"] = offer.Name, ["minecraft"] = offer.Minecraft, ["loader"] = offer.Loader,
                ["downloadUrl"] = offer.DownloadUrl, ["fileName"] = offer.FileName, ["modrinth"] = offer.Modrinth
            });
        return new JsonObject
        {
            ["id"] = server.Id, ["name"] = server.Name, ["description"] = server.Description, ["address"] = server.Address,
            ["minecraft"] = server.Minecraft, ["vanilla"] = server.Vanilla, ["icon"] = server.Icon,
            ["website"] = server.Website, ["source"] = server.Source, ["directory"] = server.Directory, ["local"] = server.Local, ["packs"] = packs
        };
    }

    private async Task JoinServerAsync(JsonObject message, FormMain form)
    {
        var id = message["id"]?.GetValue<string>() ?? "";
        var address = (message["address"]?.GetValue<string>() ?? "").Trim();
        var minecraft = (message["minecraft"]?.GetValue<string>() ?? "").Trim();
        var choice = message["choice"]?.GetValue<string>() ?? "";
        var server = _servers.FirstOrDefault(item => item.Id == id) ?? BrowseServerStore.Load().FirstOrDefault(item => item.Id == id);
        if (server is not null)
        {
            address = server.Address;
            minecraft = server.Minecraft;
        }
        else if (!PackCatalog.IsAddress(address))
            throw new InvalidOperationException("服务器信息不完整。");
        if (!PackCatalog.IsAddress(address)) throw new InvalidDataException("服务器地址无效。");
        var sessionId = message["session"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await JoinRunningAsync(sessionId, address);
            return;
        }
        if (ModWatcher.hasRunningMinecraft || ModLaunch.isLaunching) throw new InvalidOperationException("请先关闭正在运行或启动中的游戏，或选择已启动且未进服的游戏。");
        if (string.IsNullOrWhiteSpace(minecraft) && server is null)
            throw new InvalidOperationException("服务器信息不完整。");
        var instanceName = message["instance"]?.GetValue<string>() ?? "";
        var instance = string.IsNullOrWhiteSpace(instanceName) ? null : Instances().FirstOrDefault(item => item.Name == instanceName);
        if (choice == "vanilla")
        {
            instance = VanillaInstance(minecraft) ?? await DownloadVanillaAsync(minecraft);
        }
        else if (choice.StartsWith("pack:", StringComparison.Ordinal))
        {
            var offerId = choice["pack:".Length..];
            var offer = server?.Packs.FirstOrDefault(item => item.Id == offerId)
                ?? (offerId == PackCatalog.FabulouslyOptimizedId ? PackCatalog.RecommendedFabric(minecraft) : null)
                ?? throw new InvalidOperationException("没有这个推荐整合包。");
            OpenPackDownload(form, offer, message["packName"]?.GetValue<string>() ?? "", message["loader"]?.GetValue<string>() ?? "");
            return;
        }
        if (instance is null)
            throw new InvalidOperationException("请选择一个已有游戏实例。");
        if (!string.IsNullOrWhiteSpace(minecraft) && !VersionMatches(instance, minecraft) && message["mismatch"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("所选游戏不是服务器版本 " + minecraft + "。确认后可仍使用该实例进入。");
        SelectInstance(instance);
        Launch(form, address);
    }

    private static async Task JoinRunningAsync(string sessionId, string address)
    {
        var session = GameLinkSession.Snapshot().FirstOrDefault(item => item.SessionId == sessionId)
            ?? throw new InvalidOperationException("这个游戏已经退出，请重新选择。");
        if (session.State is "connected" or "connecting")
            throw new InvalidOperationException("这个游戏已经在服务器里，请先退出当前服务器。");
        if (session.State is not ("ready" or "disconnected" or "error"))
            throw new InvalidOperationException(session.State == "unsupported"
                ? "这个游戏的 PCL-MOON javaagent 没有适配成功，游戏会继续运行，但不能自动进入。"
                : "游戏还在加载，请等主菜单出现后再进入。");
        await GameLinkSession.SendAsync(sessionId, address);
    }


    private static bool VersionMatches(McInstance instance, string minecraft) =>
        string.Equals(instance.Info.VanillaName, minecraft, StringComparison.OrdinalIgnoreCase);

    private static bool IsVanilla(McInstance instance) =>
        !instance.Info.HasForgelike && !instance.Info.HasFabriclike && !instance.Info.HasLiteLoader
        && !instance.Info.HasLabyMod && !instance.Info.HasOptiFine;

    private static McInstance? VanillaInstance(string minecraft) =>
        Instances().FirstOrDefault(item => IsVanilla(item) && VersionMatches(item, minecraft) && item.Check());

    private async Task<McInstance> DownloadVanillaAsync(string minecraft)
    {
        if (string.IsNullOrEmpty(ModFolder.mcFolderSelected)) throw new InvalidOperationException("请先选择 Minecraft 文件夹。");
        _phase = "下载原版 " + minecraft;
        PublishWork();
        var loader = await Task.Run(() =>
        {
            var url = ModDownload.DlClientListGet(minecraft) as string;
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("版本列表里没有原版 " + minecraft + "。");
            return ModDownloadLib.McDownloadClient(NetPreDownloadBehaviour.ExitWhileExistsOrDownloading, minecraft, url);
        });
        if (loader is not null)
        {
            _installer = loader;
            while (loader.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting)
            {
                _progress = loader.Progress;
                PublishWork();
                await Task.Delay(400);
            }
            if (loader.State != ModBase.LoadState.Finished && VanillaInstance(minecraft) is null)
                throw new InvalidOperationException(loader.Error?.Message ?? "原版下载没有完成。");
        }
        ModInstanceList.mcInstanceListForceRefresh = true;
        await Task.Run(() => ModInstanceList.mcInstanceListLoader.WaitForExit(ModFolder.mcFolderSelected, isForceRestart: true));
        return VanillaInstance(minecraft) ?? throw new InvalidOperationException("原版已下载，但没有找到可启动实例。");
    }

    private static void OpenPackDownload(FormMain form, CatalogPackOffer offer, string requestedName, string requestedLoader)
    {
        var name = string.IsNullOrWhiteSpace(offer.Name) ? requestedName : offer.Name;
        var loader = string.IsNullOrWhiteSpace(offer.Loader) ? requestedLoader : offer.Loader;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("没有这个推荐整合包。");
        form.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadPack);
        var page = ModMain.frmDownloadPack?.Content ?? throw new InvalidOperationException("整合包下载页尚未就绪。");
        page.SearchFor(name, offer.Minecraft, PackLoader(loader));
    }

    private static ModComp.CompLoaderType PackLoader(string loader) => loader.Trim().ToLowerInvariant() switch
    {
        "fabric" => ModComp.CompLoaderType.Fabric,
        "quilt" => ModComp.CompLoaderType.Quilt,
        "forge" => ModComp.CompLoaderType.Forge,
        "neoforge" => ModComp.CompLoaderType.NeoForge,
        _ => ModComp.CompLoaderType.Any
    };

    private async Task<McInstance> InstallOfferAsync(CatalogPackOffer offer, string minecraft)
    {
        if (!string.IsNullOrWhiteSpace(offer.DownloadUrl))
        {
            var pack = new PackDefinition
            {
                Id = "server-" + offer.Id, Name = offer.Name, Description = offer.Name, Version = minecraft,
                Minecraft = minecraft, Loader = offer.Loader, DownloadUrl = offer.DownloadUrl,
                FileName = string.IsNullOrWhiteSpace(offer.FileName) ? offer.Id + ".mrpack" : offer.FileName,
                Sha256 = offer.Sha256, ShareUrl = offer.ShareUrl, SharePath = offer.SharePath
            };
            await InstallAsync(pack, !string.IsNullOrWhiteSpace(offer.ShareUrl) && string.IsNullOrWhiteSpace(offer.DownloadUrl), CancellationToken.None);
            return Instances().FirstOrDefault(item => VersionMatches(item, minecraft) && item.Name.Contains(offer.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("整合包已交给启动器安装，但还没有找到对应实例。");
        }
        if (!string.IsNullOrWhiteSpace(offer.Modrinth))
        {
            _phase = "请在版本列表选择 " + minecraft;
            ModMain.MyMsgBox("将打开 " + offer.Name + " 的 Modrinth 页面。请选择 Minecraft " + minecraft + " 的文件，下载 mrpack 后用启动器导入。导入完成后再回到服务器页一键进入。",
                "下载推荐整合包", "打开 Modrinth", "取消");
            ModBase.OpenWebsite(offer.Modrinth);
        }
        throw new InvalidOperationException("这个推荐整合包没有可直接安装的文件。");
    }

    private static void Launch(FormMain form, string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) { Launch(form); return; }
        form.PageChange(FormMain.PageType.Launch);
        if (ProfileService.Current is null) throw new InvalidOperationException("请点击左上角头像，先登录或创建离线账号。");
        if (!ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { ServerIp = server, instance = ModInstanceList.McMcInstanceSelected }))
            throw new InvalidOperationException("启动器没有开始进入服务器。");
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, string scope = "library")
    {
        using var cts = new CancellationTokenSource(); _work = cts; PackWorkInProgress = true; _progress = 0; _error = "";
        _taskScope = scope;
        try { await work(cts.Token); }
        catch (OperationCanceledException) { _phase = "已取消"; }
        catch (ModBase.CancelledException) { _phase = "已取消"; }
        catch (ThreadInterruptedException) when (cts.IsCancellationRequested) { _phase = "已取消"; }
        catch (Exception ex) { _phase = "操作失败"; _error = ex.Message; ModBase.Log(ex, "整合包服务操作失败", ModBase.LogLevel.Debug); }
        finally { _downloadTask = null; _installer = null; _work = null; PackWorkInProgress = false; _nextScan = DateTime.MinValue; PublishWork(); }
    }

    private bool HasQuarkSession() =>
        _quarkSessions.Load()?.IsReusable == true;

    private async Task AuthorizeQuarkAsync(CancellationToken token)
    {
        _phase = "正在请求夸克授权页";
        var client = new QuarkOpenClient(_http);
        var start = await client.BeginAuthorizeAsync(token).ConfigureAwait(false);
        if (!QuarkClientLaunch.TryOpenAuthorizePage(start.PageUrl))
            throw new InvalidOperationException("无法打开夸克授权页。未保存令牌。");
        _phase = "请在浏览器中完成夸克授权";
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var session = await client.PollAsync(start, token).ConfigureAwait(false);
            if (session is not null)
            {
                _quarkSessions.Save(session);
                _phase = "夸克网盘已授权。下载时只会转存目标整合包。";
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }
        throw new TimeoutException("夸克授权未完成。未保存令牌，也未下载文件。");
    }

    private async Task<bool> TryOwnedDownloadAsync(PackDefinition pack, string temp, CancellationToken token, Action<string> setArchive)
    {
        var session = _quarkSessions.Load();
        if (session?.IsReusable != true) return false;
        _phase = "转存单个整合包到已授权网盘";
        var client = new QuarkOpenClient(_http);
        client.SessionRefreshed += _quarkSessions.Save;
        var owned = await client.ResolveOwnedAsync(session, pack.ShareUrl, pack.SharePath, token).ConfigureAwait(false);
        _quarkSessions.Save(owned.Session);
        var archive = PackCatalog.SafePath(temp, owned.Download.FileName);
        _phase = "正在使用启动器原生下载任务";
        var task = QuarkNativeDownload.Create(pack.Name, archive, owned);
        _downloadTask = task;
        try
        {
            ModBase.RunInUiWait(() =>
            {
                token.ThrowIfCancellationRequested();
                task.Start(archive);
                ModLoader.LoaderTaskbarAdd(task);
                if (ModMain.frmMain is { } form)
                {
                    form.BtnExtraDownload.ShowRefresh();
                    form.BtnExtraDownload.Ribble();
                    _ = form.NavigateHybridAsync("tasks");
                }
            });
            await QuarkNativeDownload.WaitAsync(task, token).ConfigureAwait(false);
            _progress = .25;
        }
        finally { _downloadTask = null; }
        setArchive(archive);
        return true;
    }

    private static string OpenShareFallback(string shareUrl, bool authorized)
    {
        var openedDrive = QuarkClientLaunch.TryOpenDriveShare(shareUrl);
        var openedBrowser = !openedDrive && QuarkClientLaunch.TryOpenShare(shareUrl);
        if (authorized)
            return openedDrive || openedBrowser
                ? "已授权网盘，但转存或下载目标文件失败，没有转存整个分享。已打开分享窗口，请手动下载后再导入。"
                : "已授权网盘，但转存或下载目标文件失败，没有转存整个分享。";
        if (openedDrive)
            return "夸克 CDN 拒绝了匿名直链。尚未授权夸克开放平台，因此没有转存或下载。已唤起分享窗口，但该窗口没有下载接口。请在服务配置中授权后重试，或手动下载后再导入。";
        if (openedBrowser)
            return "夸克 CDN 拒绝了匿名直链。尚未授权夸克开放平台，因此没有转存或下载。已打开分享窗口，请手动下载后再导入。也可先在服务配置中授权夸克网盘。";
        return "夸克 CDN 拒绝了匿名直链（HTTP 412）。该文件超过访客通道大小限制，不能直接下载。";
    }

    internal const string DownloadCacheName = PCL.Core.IO.PackDownloadCache.FolderName;

    internal static string PackDownloadRoot()
        => PCL.Core.IO.PackDownloadCache.Resolve(Config.Launch.PackDownloadRoot, Path.GetTempPath());

    internal static string QuarkDriveFolder => "夸克网盘根目录 / PCL-MOON";

    internal static bool TryClearDownloadCache(out string message)
    {
        if (PackWorkInProgress)
        {
            message = "正在下载或安装，不能清空缓存。";
            return false;
        }
        var removed = 0;
        var blocked = 0;
        foreach (var cache in PCL.Core.IO.PackDownloadCache.ClearTargets(Config.Launch.PackDownloadRoot, Path.GetTempPath()))
        {
            if (!Directory.Exists(cache)) continue;
            try
            {
                var count = Directory.EnumerateFileSystemEntries(cache).Count();
                Directory.Delete(cache, true);
                removed += count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                blocked++;
            }
        }
        message = blocked > 0
            ? "部分下载缓存正在使用，已清空 " + removed + " 项。"
            : removed == 0 ? "下载缓存已经是空的。" : "已清空 " + removed + " 项下载缓存。";
        return blocked == 0;
    }

    private static HashAlgorithmName PackHash(string hash) => (hash ?? "").Length switch
    {
        32 => HashAlgorithmName.MD5,
        40 => HashAlgorithmName.SHA1,
        128 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256
    };

    private async Task InstallAsync(PackDefinition pack, bool quark, CancellationToken token)
    {
        var folder = ModFolder.mcFolderSelected;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) throw new InvalidOperationException("请先在底部选择游戏目录。");
        var versions = Path.Combine(folder, "versions");
        var archive = pack.LocalArchive;
        var version = pack.Version;
        var temp = Path.Combine(PackDownloadRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string instanceName = "";
        try
        {
            if (quark || string.IsNullOrEmpty(archive) || !File.Exists(archive))
            {
                if (quark && HasQuarkSession())
                {
                    string? ownedArchive = null;
                    if (!await TryOwnedDownloadAsync(pack, temp, token, path => ownedArchive = path).ConfigureAwait(false))
                        throw new QuarkOpenException("夸克授权不可用，请重新授权。");
                    archive = ownedArchive ?? throw new InvalidOperationException("夸克下载没有产生目标文件。");
                }
                else
                {
                    _phase = quark ? "解析夸克分享文件" : "下载整合包";
                    var url = pack.DownloadUrl;
                    IReadOnlyDictionary<string, string>? headers = null;
                    var fileName = pack.FileName;
                    if (quark || string.IsNullOrEmpty(url))
                    {
                        if (string.IsNullOrEmpty(pack.ShareUrl)) throw new InvalidOperationException("此预设未配置下载地址或夸克链接。");
                        var resolved = await new QuarkPackClient(_http).ResolveAsync(_settings.QuarkServiceUrl, pack.ShareUrl, pack.SharePath, token);
                        url = resolved.Url; headers = resolved.Headers; fileName = resolved.FileName;
                    }
                    archive = PackCatalog.SafePath(temp, fileName);
                    _phase = "下载整合包";
                    try
                    {
                        await _transfer.DownloadAsync(url, archive, quark ? "" : pack.Sha256, PackHash(pack.Sha256),
                            new Progress<double>(p => _progress = p * .25), token, headers);
                    }
                    catch (HttpRequestException ex) when (quark && ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                    {
                        string? ownedArchive = null;
                        try
                        {
                            if (!await TryOwnedDownloadAsync(pack, temp, token, path => ownedArchive = path).ConfigureAwait(false))
                                throw new InvalidOperationException(OpenShareFallback(pack.ShareUrl, false));
                        }
                        catch (QuarkOpenException owned)
                        {
                            // Keep the precise API failure in this operation; don't open another window.
                            throw new InvalidOperationException(owned.Message);
                        }
                        archive = ownedArchive ?? throw new InvalidOperationException("夸克下载没有产生目标文件。");
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (ModFolder.mcFolderSelected != folder) throw new InvalidOperationException("游戏目录已改变，请重新安装。");
            var suggested = PackCatalog.InstanceFolderName(string.IsNullOrWhiteSpace(archive) ? pack.FileName : Path.GetFileName(archive), versions);
            instanceName = ModMain.MyMsgBoxInput("整合包实例名称", "默认使用下载的压缩包名称，可自行修改。", suggested,
                [new FolderNameValidator(versions)]) ?? "";
            if (string.IsNullOrWhiteSpace(instanceName)) throw new ModBase.CancelledException();
            var target = Path.Combine(versions, instanceName);
            var metadata = await Task.Run(() => PackArchive.ReadMetadata(archive), token);
            if (!string.IsNullOrWhiteSpace(metadata?.Version)) version = metadata.Version;
            _phase = "调用启动器导入整合包";
             var mcmodId = pack.Id.StartsWith("mcmod-", StringComparison.Ordinal) && int.TryParse(pack.Id.AsSpan(6), out var wikiId) && wikiId > 0 ? wikiId.ToString() : null;
             _installer = await Task.Run(() => ModModpack.ModpackInstall(archive, instanceName, mcmodId: mcmodId), token);
            if (_installer is null) throw new InvalidOperationException("此包的原生安装器未返回任务，请使用手动导入。");
            using var registration = token.Register(() => _installer?.Abort());
            await Task.Run(() => _installer.WaitForExit());
            token.ThrowIfCancellationRequested();
            if (_installer.State != ModBase.LoadState.Finished) throw _installer.Error ?? new IOException("整合包安装未成功。");
            // Native installers may choose a different name for legacy formats; never mark a guessed path installed.
            if (!File.Exists(Path.Combine(target, instanceName + ".json"))) throw new IOException("原生导入未生成预期实例；请从实例管理检查导入结果。");
            var instance = new McInstance(target);
            WriteMarker(instance, pack.Id, version);
            ModInstanceList.mcInstanceListForceRefresh = true;
            await Task.Run(() => ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, folder, ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\", true));
            SelectInstance(instance); _selected = _selectCreatedInstance ? "instance:" + instance.Name : pack.Id; _progress = 1; _phase = "安装完成";
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
    }
    internal JsonObject? TakePresetQuarkReply() { var reply = _presetQuarkReply; _presetQuarkReply = null; return reply; }
    private async Task HandlePresetQuarkAsync(JsonObject message, string operation)
    {
        var id = message["id"]?.GetValue<string>() ?? "";
        var shareId = message["shareId"]?.GetValue<string>() ?? "";
        var path = message["path"]?.GetValue<string>() ?? "";
        var requestId = message["requestId"]?.GetValue<string>() ?? "";
        var pack = _packs.FirstOrDefault(item => item.Id == id);
        var shareUrl = pack is not null ? QuarkClientLaunch.MatchShare([pack.ShareUrl], shareId) : null;
        if (pack is null || shareUrl is null) throw new InvalidDataException("这个预设没有对应的夸克分享，没有读取网盘。");
        if (path.Length > 4096) throw new InvalidDataException("夸克目录无效，没有读取网盘。");
        if (operation == "quarkList")
        {
            var listing = await new QuarkPackClient(_http).ListAsync(shareUrl, path, CancellationToken.None);
            _presetQuarkReply = new JsonObject
            {
                ["type"] = "quarkVersions", ["id"] = 1, ["shareId"] = shareId, ["requestId"] = requestId,
                ["path"] = listing.Path, ["truncated"] = listing.Truncated,
                ["entries"] = new JsonArray(listing.Entries.Select(entry => new JsonObject { ["name"] = entry.Name, ["path"] = entry.Path, ["directory"] = entry.Directory, ["size"] = entry.Size }).ToArray())
            };
            return;
        }
        var started = await InstallBrowseQuarkAsync(1, pack.Name, shareUrl, path);
        _presetQuarkReply = new JsonObject { ["type"] = "quarkDownload", ["id"] = id, ["shareId"] = shareId, ["requestId"] = requestId, ["accepted"] = started };
    }

    internal async Task<bool> InstallBrowseQuarkAsync(int wikiId, string name, string shareUrl, string sharePath)
    {
        if (_disposed || _work is not null) throw new InvalidOperationException("已有任务在进行，请稍候。");
        if (ModWatcher.hasRunningMinecraft || ModLaunch.isLaunching) throw new InvalidOperationException("请先关闭游戏，再下载整合包。");
        if (wikiId is < 1 or > 10000000 || !QuarkClientLaunch.IsShareUrl(shareUrl)) throw new InvalidDataException("夸克分享无效，没有转存。");
        var file = sharePath.Split('/')[^1];
        if (file.Length == 0 || !(file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("请选择一个 .mrpack 或 .zip。没有转存。");
        var pack = new PackDefinition
        {
            Id = "mcmod-" + wikiId, Name = BrowsePackName(name), ShareUrl = shareUrl, SharePath = sharePath, FileName = file
        };
        if (!HasQuarkSession())
        {
            if (ModMain.MyMsgBox("尚未授权夸克网盘。是否现在授权，以便只转存你选中的这一个整合包后再下载？不会转存整个分享，也不会读取浏览器 Cookie。",
                "使用夸克网盘下载", "授权并下载", "取消") != 1)
            {
                _phase = "已取消";
                return false;
            }
            await RunAsync(AuthorizeQuarkAsync, "browseQuark");
            if (_error.Length > 0) throw new InvalidOperationException(_error);
            if (!HasQuarkSession()) return false;
        }
        _selectCreatedInstance = true;
        try { await RunAsync(token => InstallAsync(pack, true, token), "browseQuark"); }
        finally { _selectCreatedInstance = false; }
        if (_error.Length > 0) throw new InvalidOperationException(_error);
        return _phase != "已取消";
    }

    private static string BrowsePackName(string name)
    {
        var cleaned = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length == 0) return "整合包";
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private static void WriteMarker(McInstance instance, string id, string version)
    {
        var path = Path.Combine(instance.PathInstance, "PCL", "launcher-pack.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Installed(id, version)));
        File.Move(path + ".tmp", path, true);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _work?.Cancel(); _downloadTask?.Abort(); _installer?.Abort(); _http.Dispose(); _catalogHttp.Dispose();
    }
}
