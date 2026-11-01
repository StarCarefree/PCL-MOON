using System;
using System.Net.Http;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.WebHost;

namespace PCL;

/// <summary>Optional browse/pack-services surface. The window, game launch and all original pages remain native.</summary>
internal sealed partial class WebUiController : IDisposable
{
    private WebView2? _view;
    private HybridBrowseLoading? _loading;
    private LauncherLibrary? _library;
    private FormMain? _form;
    private DispatcherTimer? _timer;
    private Task? _initializing;
    private DependencyPropertyDescriptor? _modalVisibility;
    private int _generation;
    private bool _ready;
    private bool _revealed;
    private bool _disposed;
    private string _runtimeVersion = "";
    private bool _bundledRuntime;
    private string _lastSnapshot = "";
    private bool? _lastPresentation;

    public bool IsAttached => _view is not null;
    public bool IsPresented { get; private set; }
    internal LauncherLibrary Library { get { _library ??= new LauncherLibrary(); return _library; } }
    public bool HasKeyboardFocus => IsPresented && _view?.Visibility == Visibility.Visible &&
        ((_view?.IsKeyboardFocusWithin ?? false) || (_form is { } form && HybridBrowserFocus.HasNativeChildFocus(form)));
    public static bool RequestedByArguments(System.Collections.Generic.IEnumerable<string> args) => WebView2RuntimePlan.UseWebUi(args.ToArray());
    private static Task SwitchToUiAsync(Dispatcher dispatcher)
    {
        if (dispatcher.CheckAccess()) return Task.CompletedTask;
        return dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Normal).Task;
    }

    private void PostUi(Action action)
    {
        var dispatcher = _form?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public async Task AttachAsync(FormMain form)
    {
        if (_disposed) return;
        var replay = _ready;
        _form = form;
        IsPresented = true;
        form.SetBrowseVisible(true);
        if (_view is null && _initializing is { IsCompleted: true }) _initializing = null;
        _initializing ??= InitializeAsync(form);
        await _initializing;
        if (_disposed) return;
        await SwitchToUiAsync(form.Dispatcher);
        if (_disposed) return;
        if (replay) _lastPresentation = null;
        UpdatePresentation();
        if (_ready && IsPresented && form.PanMsgBackground.Visibility != Visibility.Visible && _view is { } view)
        {
            view.UpdateLayout();
            await form.Dispatcher.InvokeAsync(() =>
            {
                if (IsPresented && !_disposed)
                {
                    view.Focus();
                    System.Windows.Input.Keyboard.Focus(view);
                }
            }, DispatcherPriority.Input);
        }
        Refresh(force: true);
    }

    private async Task InitializeAsync(FormMain form)
    {
        var generation = ++_generation;
        try
        {
            var folder = WebView2RuntimePlan.FindBundledFolder(AppContext.BaseDirectory, File.Exists);
            _bundledRuntime = folder is not null;
            Environment.SetEnvironmentVariable(SystemWebView2Plan.LoaderOverride, folder);
            _runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(folder);
            _library ??= new LauncherLibrary();
            var view = new WebView2 { Focusable = true, DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 20, 20, 20) };
            _view = view;
            _loading = new HybridBrowseLoading();
            form.PanBrowse.Children.Add(_loading);
            form.PanBrowse.Children.Add(view);
            _modalVisibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));
            _modalVisibility.AddValueChanged(form.PanMsgBackground, OnModalChanged);
            form.Closed += OnClosed;
            UpdatePresentation();
            var environment = await CoreWebView2Environment.CreateAsync(folder, WebUiAssets.UserDataFolder());
            if (_disposed || generation != _generation) return;
            await SwitchToUiAsync(form.Dispatcher);
            if (_disposed || generation != _generation) return;
            await view.EnsureCoreWebView2Async(environment);
            await SwitchToUiAsync(form.Dispatcher);
            if (_disposed || generation != _generation || view.CoreWebView2 is null) return;
            var core = view.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = ModBase.modeDebug;
            core.Settings.AreDevToolsEnabled = ModBase.modeDebug;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.SetVirtualHostNameToFolderMapping(WebUiAssets.HostName, WebUiAssets.Extract(), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) =>
            {
                if (WebView2RuntimePlan.IsTrustedSource(e.Uri, WebUiAssets.HostName)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += (_, e) =>
            {
                if (_disposed || generation != _generation || !WebView2RuntimePlan.IsTrustedSource(e.Source, WebUiAssets.HostName)) return;
                var json = e.WebMessageAsJson;
                form.Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && generation == _generation) OnMessage(core, json, form);
                });
            };
            core.NavigationCompleted += (_, e) => PostUi(() =>
            {
                if (_disposed || generation != _generation) return;
                if (e.IsSuccess)
                {
                    _ready = true;
                    SendBootstrap(core);
                    try { _library?.Capture(); }
                    catch (Exception ex) { ModBase.Log(ex, "浏览页首次扫描整合包失败", ModBase.LogLevel.Debug); }
                    Refresh(force: true);
                    SendPendingPluginBrowse();
                }
                Reveal(view, form, generation);
            });
            _timer = new DispatcherTimer(DispatcherPriority.Background, form.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += OnTick;
            _timer.Start();
            core.Navigate("https://" + WebUiAssets.HostName + "/index.html");
            ModBase.Log($"[WebUi] 浏览补充页已启动：{(_bundledRuntime ? "随包固定运行时" : "系统运行时")} {_runtimeVersion}");
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _generation) return;
            try { await SwitchToUiAsync(form.Dispatcher); }
            catch (Exception switchEx)
            {
                ModBase.Log(switchEx, "浏览补充页回到界面线程失败", ModBase.LogLevel.Debug);
                ModBase.Log(ex, "浏览补充页启动失败，原生界面保持可用");
                return;
            }
            if (_disposed || generation != _generation) return;
            Hide();
            ReleaseView();
            _initializing = null;
            ModBase.Log(ex, "浏览补充页启动失败，原生界面保持可用");
            ModMain.MyMsgBox(Lang.Text("Hybrid.BrowserFailed") + "\n\n" + ex.Message, Lang.Text("Hybrid.Browse"));
        }
    }

    // 页面首帧就绪后淡出骨架再显示 WebView，网页随后播放自身的进入动画
    private void Reveal(WebView2 view, FormMain form, int generation)
    {
        if (_revealed) return;
        void Show()
        {
            if (_disposed || generation != _generation || _revealed) return;
            _revealed = true;
            if (_loading is not null) { form.PanBrowse.Children.Remove(_loading); _loading = null; }
            _lastPresentation = null;
            UpdatePresentation();
            if (IsPresented && form.PanMsgBackground.Visibility != Visibility.Visible) view.Focus();
        }
        if (_loading is null) Show();
        else _loading.Dismiss(Show);
    }

    public void Hide()
    {
        IsPresented = false;
        _form?.SetBrowseVisible(false);
        UpdatePresentation();
        if (_form is { } form && form.PanMsgBackground.Visibility != Visibility.Visible)
            System.Windows.Input.Keyboard.Focus(form.PanMain);
    }

    private void OnModalChanged(object? sender, EventArgs e) => UpdatePresentation();
    private void UpdatePresentation()
    {
        if (_view is null || _form is null) return;
        // A WPF overlay cannot cover WebView2's child HWND. Hide the browser during native modal dialogs.
        var visible = IsPresented && _form.PanMsgBackground.Visibility != Visibility.Visible;
        // 加载期间保持 Hidden：WebView 照常初始化和布局，但不遮住下方的骨架
        _view.Visibility = !visible ? Visibility.Collapsed : _revealed ? Visibility.Visible : Visibility.Hidden;
        if (!_ready || !_revealed || _lastPresentation == visible || _view.CoreWebView2 is not { } core) return;
        try
        {
            core.PostWebMessageAsJson(new JsonObject { ["type"] = "presentation", ["visible"] = visible }.ToJsonString());
            _lastPresentation = visible;
        }
        catch (Exception ex) { ModBase.Log(ex, "同步浏览页可见状态失败", ModBase.LogLevel.Debug); }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || !IsPresented || _form is not { } form || form.WindowState == WindowState.Minimized || form.Hidden || form.PanMsgBackground.Visibility == Visibility.Visible) return;
        try
        {
            try { _library?.Capture(); }
            catch (Exception ex) { ModBase.Log(ex, "浏览页扫描整合包失败", ModBase.LogLevel.Debug); }
            Refresh();
            if (_library is not null) await _library.RefreshIfNeededAsync(form);
        }
        catch (Exception ex) { ModBase.Log(ex, "刷新浏览补充页失败", ModBase.LogLevel.Debug); }
    }

    public async Task CheckPackUpdatesOnceAsync()
    {
        if (_disposed) return;
        _library ??= new LauncherLibrary();
        await _library.CheckManagedOnceAsync();
    }

    public bool TryTakeCheckNotice(out string text, out HintType type)
    {
        text = "";
        type = HintType.Info;
        return _library?.TryTakeCheckNotice(out text, out type) == true;
    }

    private void Refresh(bool force = false)
    {
        if (!_ready || !IsPresented || _form is null || _library is null || _view?.CoreWebView2 is not { } core || _form.WindowState == WindowState.Minimized) return;
        try
        {
            var snapshot = new JsonObject { ["type"] = "browse", ["launcher"] = _library.Capture(scan: false) };
            var json = snapshot.ToJsonString();
            if (!force && json == _lastSnapshot) return;
            _lastSnapshot = json;
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { ModBase.Log(ex, "浏览数据同步失败", ModBase.LogLevel.Debug); }
    }

    private async void OnMessage(CoreWebView2 core, string json, FormMain form)
    {
        if (_disposed || json.Length > 131072) return;
        JsonObject? message = null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed) return;
            message = parsed;
            var type = ReadString(message, "type");
            if (type == "status") { core.PostWebMessageAsJson(form.HybridStatus().ToJsonString()); return; }
#if DEBUG
            if (Basics.CommandLineArguments.Contains("--hybrid-smoke"))
            {
                if (type == "testNavigate") { await form.NavigateHybridForTestAsync(ReadString(message, "page"), ReadString(message, "id")); return; }
                if (type == "testCaptureNative") { form.CaptureHybridNativeForTest(); core.PostWebMessageAsJson(form.HybridStatus().ToJsonString()); return; }
            }
#endif
            if (!IsPresented || form.PanMsgBackground.Visibility == Visibility.Visible) return;
            switch (type)
            {
                case "bootstrap": SendBootstrap(core); Refresh(force: true); break;
                case "mcmod": await HandleMcmodAsync(core, message, form); break;
                case "platform": await HandlePlatformAsync(core, message, form); break;
                case "launcher" when _library is not null:
                    await _library.HandleAsync(message, form);
                    if (ReadString(message, "operation") is "quarkList" or "quarkDownload") PostLibraryReply(core, message);
                    Refresh(force: true);
                    break;
                case "native": await form.NavigateHybridAsync(ReadString(message, "page")); break;
                case "servers": await HandleServersAsync(core); break;
                case "openExternal": OpenExternal(ReadString(message, "url")); break;
            }
        }
        catch (Exception ex)
        {
            if (message is not null && ReadString(message, "operation") is "quarkList" or "quarkDownload" && _library is not null)
                PostJson(core, new JsonObject { ["type"] = "error", ["operation"] = ReadString(message, "operation"), ["id"] = ReadString(message, "id") ?? "", ["requestId"] = ReadString(message, "requestId") ?? "", ["message"] = ex.Message });
            ModBase.Log(ex, "浏览补充页消息处理失败", ModBase.LogLevel.Debug);
            var text = ex.Message;
            PostUi(() =>
            {
                if (_disposed || _view?.CoreWebView2 is null) return;
                core.PostWebMessageAsJson(new JsonObject { ["type"] = "error", ["message"] = text }.ToJsonString());
            });
        }
    }

    private void PostLibraryReply(CoreWebView2 core, JsonObject message)
    {
        var reply = _library?.TakePresetQuarkReply();
        if (reply is null) return;
        reply["operation"] = ReadString(message, "operation");
        PostJson(core, reply);
    }
    private static void PostJson(CoreWebView2 core, JsonObject reply) => core.PostWebMessageAsJson(reply.ToJsonString());

    private void SendBootstrap(CoreWebView2 core) => core.PostWebMessageAsJson(new JsonObject
    {
        ["type"] = "bootstrap", ["primaryUi"] = "native", ["view"] = "browse",
        ["language"] = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
        ["version"] = Basics.VersionName, ["branch"] = Basics.VersionBranch,
        ["runtimeVersion"] = _runtimeVersion, ["bundledRuntime"] = _bundledRuntime
    }.ToJsonString());

    private async Task HandleServersAsync(CoreWebView2 core)
    {
        JsonArray items = [];
        var message = "";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.mclists.cn/");
            using var response = await http.GetAsync("https://www.mclists.cn/api/v2/servers?page=1&limit=50&sort=recommended");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("MCLists 没有返回服务器数组。");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = Text(item, "name");
                var address = Text(item, "endpoint");
                if (address.Length == 0) address = Text(item, "address");
                var website = Text(item, "url");
                if (name.Length is < 1 or > 120 || address.Length is < 1 or > 253 || !seen.Add(address)) continue;
                if (!website.StartsWith("https://www.mclists.cn/", StringComparison.OrdinalIgnoreCase)) website = "https://www.mclists.cn/";
                var version = Text(item, "version");
                var category = item.TryGetProperty("category", out var categoryNode) && categoryNode.ValueKind == JsonValueKind.Object ? Text(categoryNode, "name") : "";
                var description = Text(item, "description");
                if (description.Length == 0) description = string.Join(" · ", new[] { category, version }.Where(part => part.Length > 0));
                items.Add(new JsonObject
                {
                    ["id"] = "mclists-" + Text(item, "sid"),
                    ["name"] = name,
                    ["description"] = description,
                    ["website"] = website,
                    ["address"] = address,
                    ["minecraft"] = version,
                    ["vanilla"] = false,
                    ["source"] = "mclists"
                });
            }
            var total = document.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("total", out var totalNode) && totalNode.TryGetInt32(out var count) ? count : items.Count;
            message = items.Count == 0 ? "MCLists 没有返回可进入的服务器。" : $"MCLists 推荐服务器 {items.Count} 个，目录共 {total} 个。";
        }
        catch (Exception ex) { message = "MCLists 暂时不可用：" + ex.Message; }
        core.PostWebMessageAsJson(new JsonObject { ["type"] = "serverList", ["items"] = items, ["message"] = message }.ToJsonString());
    }
    private static string Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString() ?? "").Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }
    private static string? ReadString(JsonObject message, string name) => message[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) return;
        ModBase.OpenWebsite(url);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();
    private void ReleaseView()
    {
        ++_generation;
        _ready = false;
        _revealed = false;
        _lastSnapshot = "";
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
        if (_form is not null)
        {
            _form.Closed -= OnClosed;
            _modalVisibility?.RemoveValueChanged(_form.PanMsgBackground, OnModalChanged);
            if (_view is not null) _form.PanBrowse.Children.Remove(_view);
            if (_loading is not null) _form.PanBrowse.Children.Remove(_loading);
        }
        _loading = null;
        _modalVisibility = null;
        _view?.Dispose();
        _view = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mcmodLifetime.Cancel();
        _mcmodHttp.Dispose();
        _quarkListHttp.Dispose();
        _xyeHttp.Dispose();
        ReleaseView();
        _library?.Dispose();
        _library = null;
        _form = null;
    }
}
