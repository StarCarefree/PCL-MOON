using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Profile;

namespace PCL;

public partial class FormMain
{
    private DispatcherTimer? _hybridTimer;
    private bool? _hybridGameLayout;
    private string? _hybridVersionPath;
    private string? _hybridVersionsFolder;
    private bool _hybridShellStale;

    private void InitializeHybridShell()
    {
        var scrollbars = new ResourceDictionary { Source = new Uri("/Plain Craft Launcher 2;component/Modules/HybridUi/HybridScrollbars.xaml", UriKind.Relative) };
        System.Windows.Application.Current.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = scrollbars[typeof(System.Windows.Controls.Primitives.ScrollBar)];
        System.Windows.Application.Current.Resources[typeof(MyScrollBar)] = scrollbars[typeof(MyScrollBar)];
        HybridSidebarView.Runtime.Text = Basics.VersionName + "  ·  .NET 10";
        _hybridTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(750) };
        _hybridTimer.Tick += HybridTick;
        _hybridTimer.Start();
        Closed += (_, _) =>
        {
            InstanceActivity.Flush();
            _hybridTimer.Stop();
            _hybridTimer.Tick -= HybridTick;
            _webUi.Dispose();
        };
        HybridAccountView.Closed += (_, _) => UpdateHybridShell();
        // 最小化/隐藏期间跳过的侧栏刷新在恢复时补一次
        EventHandler refreshIfStale = (_, _) =>
        {
            if (!_hybridShellStale || WindowState == WindowState.Minimized || Hidden) return;
            _hybridShellStale = false;
            try { UpdateHybridShell(); }
            catch (Exception ex) { ModBase.Log(ex, "恢复原生侧栏刷新失败", ModBase.LogLevel.Debug); }
        };
        StateChanged += refreshIfStale;
        IsVisibleChanged += (_, _) => refreshIfStale(this, EventArgs.Empty);
        HybridSidebarView.PlayEntrance();
        UpdateHybridShell();
        var launch = ModMain.frmLaunchLeft;
        if (launch is not null)
        {
            // Only the original launch button gets the warm CTA treatment; user page themes remain intact.
            foreach (var key in new[] { "ColorBrush2", "ColorBrush3", "ColorBrushHalfWhite" })
                launch.BtnLaunch.Resources[key] = new SolidColorBrush(Color.FromRgb(255, 185, 66));
            launch.BtnLaunch.Resources["ColorBrush1"] = new SolidColorBrush(Color.FromRgb(22, 24, 27));
            launch.BtnLaunch.Resources["ColorBrush7"] = new SolidColorBrush(Color.FromRgb(255, 206, 112));
            launch.BtnLaunch.LabText.FontSize = 18;
            launch.BtnLaunch.LabText.FontWeight = FontWeights.SemiBold;
            launch.BtnLaunch.LabText.Foreground = new SolidColorBrush(Color.FromRgb(22, 24, 27));
            launch.BtnLaunch.Opacity = launch.BtnLaunch.IsEnabled ? 1 : 0.48;
            launch.BtnLaunch.IsEnabledChanged += (_, _) => launch.BtnLaunch.Opacity = launch.BtnLaunch.IsEnabled ? 1 : 0.48;
        }
    }

    private bool _packCheckRunning;

    private async void HybridTick(object? sender, EventArgs e)
    {
        var dormant = WindowState == WindowState.Minimized || Hidden;
        try
        {
            InstanceActivity.Tick();
            if (dormant) _hybridShellStale = true;
            else UpdateHybridShell();
        }
        catch (Exception ex) { ModBase.Log(ex, "更新原生侧栏失败", ModBase.LogLevel.Debug); }
        if (dormant || _packCheckRunning || PanMsgBackground.Visibility == Visibility.Visible || ModLaunch.isLaunching) return;
        _packCheckRunning = true;
        try
        {
            await _webUi.CheckPackUpdatesOnceAsync();
            if (_webUi.TryTakeCheckNotice(out var notice, out var noticeType))
                HintService.Hint(notice, noticeType);
        }
        catch (Exception ex) { ModBase.Log(ex, "检查整合包更新失败", ModBase.LogLevel.Debug); }
        finally { _packCheckRunning = false; }
    }

    internal void SetBrowseVisible(bool visible)
    {
        if (PanBrowse is null) return;
        if (visible)
        {
            HybridMultiplayerView.Visibility = Visibility.Collapsed;
            BtnExtraShutdown.ShowRefresh();
        }
        PanBrowse.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var covered = visible || HybridMultiplayerView.Visibility == Visibility.Visible;
        PanMain.Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        PanMain.IsHitTestVisible = !covered;
        ModAnimation.AniStop("Hybrid Browse");
        PanBrowse.Opacity = 1;
        PanBrowse.RenderTransform = Transform.Identity;
        UpdateHybridShell();
    }

    internal void SetMultiplayerVisible(bool visible)
    {
        HybridMultiplayerView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BtnExtraShutdown.ShowRefresh();
        var covered = visible || _webUi.IsPresented;
        PanMain.Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        PanMain.IsHitTestVisible = !covered;
        UpdateHybridShell();
    }

    internal void ShowHybridAccount()
    {
        if (PanMsgBackground.Visibility == Visibility.Visible) return;
        if (HybridAccountView.Visibility == Visibility.Visible)
        {
            HybridAccountView.Close();
            return;
        }
        if (_webUi.IsPresented) _webUi.Hide();
        HybridAccountView.Open();
    }

    internal void ShowHybridLogin(ModLaunch.McLoginType type)
    {
        if (_webUi.IsPresented) _webUi.Hide();
        HybridAccountView.OpenLogin(type);
    }

    internal void RefreshHybridVersions()
    {
        try { UpdateHybridShell(); }
        catch (Exception ex) { ModBase.Log(ex, "更新启动中的版本列表失败", ModBase.LogLevel.Debug); }
    }

    internal void UpdateHybridShell()
    {
        if (HybridSidebarView is null || PanMain is null) return;
        var browse = _webUi.IsPresented;
        var page = (PageType)pageCurrent;
        var route = HybridMultiplayerView.Visibility == Visibility.Visible ? "multiplayer" : browse ? "browse" : page switch
        {
            PageType.Download or PageType.CompDetail => "download",
            PageType.Setup => "settings",
            PageType.Tools => "tools",
            _ => "games"
        };
        HybridSidebarView.SetActive(route);
        HybridSidebarView.Nav.IsEnabled = PanMsgBackground.Visibility != Visibility.Visible;
        var nextSection = route switch
        {
            "browse" => Lang.Text("Hybrid.Browse"),
            "multiplayer" => "多人游戏",
            "settings" => Lang.Text("Main.Tab.Settings"),
            "download" => Lang.Text("Main.Tab.Download"),
            "tools" => Lang.Text("Main.Tab.Tools"),
            _ => Lang.Text("Hybrid.Games")
        };
        if (LabHybridSection.Text != nextSection)
        {
            LabHybridSection.Text = nextSection;
            LabHybridSection.Opacity = 0;
            LabHybridSection.RenderTransform = new TranslateTransform(12, 0);
            ModAnimation.AniStart(new[]
            {
                ModAnimation.AaOpacity(LabHybridSection, 1, 150),
                ModAnimation.AaTranslateX(LabHybridSection, -12, 320, 0, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak))
            }, "Hybrid Section");
        }
        // Swap before the page-change animation so the native launch button never flashes on the left.
        ApplyHybridGameLayout(page == PageType.Launch);
        PanLeft.Visibility = browse || page == PageType.Launch ? Visibility.Collapsed : Visibility.Visible;
        var account = ProfileService.Current;
        var skin = account is null ? null : ModMain.frmLoginProfileSkin?.Skin;
        HybridSidebarView.UpdateAccount(account?.UserName ?? Lang.Text("Hybrid.NotLoggedIn"),
            skin?.ImgBack?.Source, skin?.ImgFore?.Source);
        var selected = ModInstanceList.McMcInstanceSelected;
        var versionPath = selected?.PathInstance ?? "";
        var versionChanged = _hybridVersionPath is not null && !string.Equals(_hybridVersionPath, versionPath, StringComparison.OrdinalIgnoreCase);
        _hybridVersionPath = versionPath;
        if (ModMain.frmLaunchRight is { } home)
        {
            home.ShowSelectedVersion(selected);
            ModMain.frmLaunchLeft?.ApplyVersionUpdateChrome();
            home.LabNativeGameStatus.Text = selected is not null && ModLaunch.IsInstanceLaunching(selected)
                ? Lang.Text("Launch.Status.Title.Launching")
                : selected is null ? Lang.Text("Hybrid.NoVersions") : Lang.Text("Hybrid.Ready");
            if (versionChanged) home.PlayVersionSwitch();
        }
        var folder = ModFolder.mcFolderSelected;
        if (!string.IsNullOrEmpty(folder) && ModInstanceList.mcInstanceListLoader.State != ModBase.LoadState.Loading)
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, folder,
                ModLoader.LoaderFolderRunType.RunOnUpdated, 1, @"versions\");
        var versionsLoaded = ModInstanceList.mcInstanceListLoader.State == ModBase.LoadState.Finished;
        if (versionsLoaded) _hybridVersionsFolder = folder;
        // 同一文件夹后台重载（如下载写入 versions）时保留上次列表，只在首次加载或切换文件夹时隐藏
        HybridSidebarView.SetVersionsLoaded(versionsLoaded || !string.IsNullOrEmpty(folder) && string.Equals(_hybridVersionsFolder, folder, StringComparison.OrdinalIgnoreCase));
        if (versionsLoaded)
        {
            var prefix = string.IsNullOrEmpty(folder) ? null : Path.GetFullPath(Path.Combine(folder, "versions")) + Path.DirectorySeparatorChar;
            var instances = prefix is null ? [] : ModInstanceList.mcInstanceList.ToArray().SelectMany(pair => pair.Value.ToArray())
                .Where(instance => instance.PathInstance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(instance => instance.PathInstance, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            HybridSidebarView.UpdateVersions(instances, selected, ModLaunch.LaunchingPaths(), Lang.Text("Hybrid.LaunchingPrefix"));
            if (versionChanged) HybridSidebarView.AnimateSelected(versionPath);
        }
    }
    internal void ApplyHybridGameLayout(bool gameLayout)
    {
        if (gameLayout) PanForm.Background = (Brush)FindResource("Hybrid.Background");
        if (_hybridGameLayout == gameLayout) return;
        _hybridGameLayout = gameLayout;
        // A dedicated bottom row keeps the primary action visible without covering cards.
        Grid.SetColumn(PanLaunchLayer, 0);
        Grid.SetColumnSpan(PanLaunchLayer, gameLayout ? 2 : 1);
        Grid.SetRow(PanLaunchLayer, gameLayout ? 2 : 0);
        Grid.SetRowSpan(PanLaunchLayer, gameLayout ? 1 : 3);
        Panel.SetZIndex(PanLaunchLayer, 0);
        PanLeftSlot.HorizontalAlignment = HorizontalAlignment.Stretch;
        PanLeftSlot.VerticalAlignment = gameLayout ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        Grid.SetColumn(PanRightSlot, gameLayout ? 0 : 1);
        Grid.SetRow(PanRightSlot, gameLayout ? 1 : 0);
        Grid.SetRowSpan(PanRightSlot, gameLayout ? 1 : 3);
        Grid.SetColumnSpan(PanRightSlot, gameLayout ? 2 : 1);
        PanMain.ColumnDefinitions[0].Width = gameLayout ? new GridLength(0) : GridLength.Auto;
        PanMain.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
    }

    private async void HybridNavigation_Requested(object sender, HybridNavigationEventArgs e)
    {
        try
        {
            if (e.Route == "instance" && e.Instance is not null)
            {
                // 点击正在启动的实例时保留启动进度，只在切到其他实例时退出进度页
                if (ModMain.frmLaunchLeft?.PanLaunching.Visibility == Visibility.Visible && !ModLaunch.IsInstanceLaunching(e.Instance))
                    ModMain.frmLaunchLeft.PageChangeToLogin();
                LauncherLibrary.SelectInstance(e.Instance);
                await NavigateHybridAsync("games");
                return;
            }
            if (e.Route == "instanceSettings" && e.Instance is not null)
            {
                if (PanMsgBackground.Visibility == Visibility.Visible) return;
                LauncherLibrary.SelectInstance(e.Instance);
                OpenVersionSettings(e.Instance);
                return;
            }
            await NavigateHybridAsync(e.Route);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "原生导航操作失败", ModBase.LogLevel.Debug);
            ModMain.MyMsgBox(ex.Message, Lang.Text("Hybrid.Games"));
        }
    }

    internal async Task NavigateHybridAsync(string? route)
    {
        if (PanMsgBackground.Visibility == Visibility.Visible) return;
        if (route == "browse") { await ShowWebUiAsync(); return; }
        switch (route)
        {
            case "multiplayer": _webUi.Hide(); SetMultiplayerVisible(true); break;
            case "games":
                _webUi.Hide();
                var replayGames = (PageType)pageCurrent == PageType.Launch;
                PageChange(PageType.Launch);
                if (replayGames) ModMain.frmLaunchRight?.PlayPageSwitch();
                break;
            case "settings": _webUi.Hide(); PageChange(PageType.Setup, PageSubType.SetupLaunch); break;
            case "download": _webUi.Hide(); PageChange(PageType.Download, PageSubType.DownloadInstall); break;
            case "tools": _webUi.Hide(); PageChange(PageType.Tools, PageSubType.ToolsTest); break;
            case "versions":
                _webUi.Hide(); PageChange(PageType.InstanceSelect);
                break;
            case "versionSettings":
                if (ModInstanceList.McMcInstanceSelected is { } selected) OpenVersionSettings(selected);
                break;
            case "account":
                ShowHybridAccount();
                break;
            case "tasks": _webUi.Hide(); PageChange(PageType.TaskManager); break;
        }
        UpdateHybridShell();
    }

    private void OpenVersionSettings(McInstance instance)
    {
        if (ModLaunch.IsInstanceLaunching(instance)) return;
        _webUi.Hide();
        instance.Load();
        PageInstanceLeft.McInstance = instance;
        if (File.Exists(Path.Combine(instance.PathInstance, ".pclignore"))) return;
        var left = ModMain.frmInstanceLeft ??= new PageInstanceLeft();
        left.pageID = PageSubType.Default;
        PageChange(PageType.InstanceSetup, PageSubType.VersionSetup);
        ModBase.RunInUi(() => ModMain.frmInstanceSetup?.Reload(), true);
    }

    internal JsonObject HybridStatus()
    {
        var launch = ModMain.frmLaunchLeft;
        var below = false;
        if (launch is not null && launch.IsLoaded)
        {
            var start = launch.BtnLaunch.TranslatePoint(new Point(0, launch.BtnLaunch.ActualHeight), launch);
            below = launch.BtnInstance.TranslatePoint(new Point(), launch).Y >= start.Y && launch.BtnMore.TranslatePoint(new Point(), launch).Y >= start.Y;
        }
        return new JsonObject
        {
            ["type"] = "status", ["primaryUi"] = "native", ["view"] = _webUi.IsPresented ? "browse" : "native",
            ["windowActive"] = IsActive,
            ["browserCreated"] = _webUi.IsAttached, ["browserVisible"] = PanBrowse.IsVisible && PanMsgBackground.Visibility != Visibility.Visible,
            ["browserKeyboardFocus"] = _webUi.HasKeyboardFocus,
            ["page"] = (int)(PageType)pageCurrent, ["sub"] = (int)PageCurrentSub,
            ["contentState"] = (PanMainRight.Child as MyPageRight)?.PageState.ToString(),
            ["nativeVisible"] = PanMain.IsVisible, ["modalVisible"] = PanMsgBackground.Visibility == Visibility.Visible,
            ["nativeLeft"] = PanMainLeft.Child?.GetType().Name, ["nativeRight"] = PanMainRight.Child?.GetType().Name,
            ["originalLaunchControls"] = launch?.BtnLaunch is MyButton && launch.BtnInstance is MyButton && launch.BtnMore is MyButton,
            ["versionsBelowLaunch"] = below, ["versionSettingsEnabled"] = launch?.BtnMore.IsEnabled ?? false,
            ["width"] = ActualWidth, ["height"] = ActualHeight
        };
    }
}
