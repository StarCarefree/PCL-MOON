using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.Minecraft.Profile;
using PCL.Core.UI;

namespace PCL;

public partial class PageLaunchRight
{
    private (McInstance Instance, DateTime Time)[] _dashboardRecent = [];
    private bool _dashboardRecentLoading;
    private DateTime _dashboardRecentChecked;
    private string _dashboardRecentFolder = "";

    private McInstance[] DashboardInstances()
    {
        var folder = ModFolder.mcFolderSelected;
        if (string.IsNullOrEmpty(folder)) return [];
        try
        {
            var prefix = Path.GetFullPath(Path.Combine(folder, "versions")) + Path.DirectorySeparatorChar;
            return ModInstanceList.mcInstanceList.ToArray().SelectMany(pair => pair.Value.ToArray())
                .Where(instance => instance.PathInstance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(instance => instance.PathInstance, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取游戏组件版本列表失败", ModBase.LogLevel.Debug);
            return [];
        }
    }

    private void RefreshDashboardData(McInstance? selected)
    {
        _dashboardSelected = selected;
        if (_dashboard is null || !IsVisible) return;
        var path = selected?.PathInstance ?? "";
        if (path == _dashboardVersion && DateTime.UtcNow - _dashboardRefreshed < TimeSpan.FromSeconds(5)) return;
        _dashboardVersion = path;
        _dashboardRefreshed = DateTime.UtcNow;
        RefreshDashboardRecent();
        if (_dashboardEditing) return;
        RefreshDashboardFiles();
        foreach (var widget in _dashboard.Widgets)
        {
            if (widget.Kind is "hero" or "note" || !_dashboardContents.TryGetValue(widget.Id, out var content)) continue;
            if (content.Content is FrameworkElement { Tag: Action refresh } interactive)
            {
                if (!interactive.IsKeyboardFocusWithin && !interactive.IsMouseOver) refresh();
            }
            else content.Content = CreateDashboardContent(widget);
        }
    }

    private async void RefreshDashboardRecent()
    {
        if (_dashboardRecentLoading || _dashboard is null || !_dashboard.Widgets.Any(widget => widget.Kind == "recent")) return;
        var folder = ModFolder.mcFolderSelected ?? "";
        if (folder == _dashboardRecentFolder && DateTime.UtcNow - _dashboardRecentChecked < TimeSpan.FromSeconds(30)) return;
        _dashboardRecentFolder = folder;
        _dashboardRecentChecked = DateTime.UtcNow;
        _dashboardRecentLoading = true;
        var instances = DashboardInstances();
        // Resolve paths on the UI thread; metadata I/O runs off-thread and never parses entire logs.
        var roots = instances.Select(instance => (Instance: instance, Log: DashboardLogPath(instance))).ToArray();
        try
        {
            var recent = await Task.Run(() => roots.Select(item => (item.Instance, item.Log, Time: DashboardLogTime(item.Log)))
                .Where(item => item.Time > DateTime.MinValue).OrderByDescending(item => item.Time)
                .GroupBy(item => item.Log, StringComparer.OrdinalIgnoreCase)
                .Select(group => (group.First().Instance, group.First().Time)).Take(4).ToArray());
            if (folder != (ModFolder.mcFolderSelected ?? "")) return;
            _dashboardRecent = recent;
            if (_dashboardEditing || _dashboard is null) return;
            foreach (var widget in _dashboard.Widgets.Where(widget => widget.Kind == "recent"))
                if (_dashboardContents.TryGetValue(widget.Id, out var content)) content.Content = CreateDashboardContent(widget);
        }
        catch (Exception ex) { ModBase.Log(ex, "读取最近游玩组件失败", ModBase.LogLevel.Debug); }
        finally { _dashboardRecentLoading = false; }
    }

    private static string DashboardLogPath(McInstance instance)
    {
        try { return Path.Combine(instance.PathIndie, "logs", "latest.log"); }
        catch { return ""; }
    }

    private static DateTime DashboardLogTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue; }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private FrameworkElement CreateDashboardContent(GameDashboardWidget widget)
    {
        var panel = new StackPanel { MinHeight = 100 };
        switch (widget.Kind)
        {
            case "greeting":
                var hour = DateTime.Now.Hour;
                var greeting = hour < 6 ? "夜深了" : hour < 11 ? "早上好" : hour < 14 ? "中午好" : hour < 18 ? "下午好" : "晚上好";
                var name = ProfileService.Current?.UserName;
                var heading = DashboardText(greeting + (string.IsNullOrWhiteSpace(name) ? ", 冒险家" : ", " + name), 23);
                heading.FontWeight = FontWeights.SemiBold;
                heading.Margin = new Thickness(0, 6, 0, 8);
                panel.Children.Add(heading);
                panel.Children.Add(DashboardText("愿今天的方块世界，为你带来美好心情！", 12, true));
                var date = DashboardText(DateTime.Now.ToString("yyyy.MM.dd  ·  dddd"), 11, true);
                date.Margin = new Thickness(0, 18, 0, 0);
                panel.Children.Add(date);
                break;
            case "calendar":
                return CreateDashboardCalendar(widget);
            case "note":
                var note = DashboardText(string.IsNullOrWhiteSpace(widget.Text) ? "还没有内容。进入编辑布局，打开组件设置写下你的计划。" : widget.Text, 14, string.IsNullOrWhiteSpace(widget.Text));
                note.Margin = new Thickness(0, 6, 0, 0);
                panel.Children.Add(note);
                break;
            case "stats":
                return CreateDashboardStats(widget);
            case "favorites":
            case "worlds":
            case "servers":
            case "actions":
            case "folders":
                return CreateDashboardTools(widget);
            case "shortcut":
                var target = DashboardInstances().FirstOrDefault(instance => string.Equals(instance.PathInstance, widget.Target, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    panel.Children.Add(DashboardText("找不到绑定的版本", 16));
                    panel.Children.Add(DashboardText("版本可能已移动、删除，或位于其他游戏文件夹。进入组件设置可重新绑定。", 12, true));
                }
                else panel.Children.Add(DashboardInstanceButton(target, "选择此版本  ↗"));
                break;
            case "recent":
                var current = DashboardInstances().Select(instance => instance.PathInstance).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var recent = _dashboardRecent.Where(item => current.Contains(item.Instance.PathInstance)).ToArray();
                foreach (var item in recent) panel.Children.Add(DashboardInstanceButton(item.Instance, item.Time.ToString("MM.dd HH:mm") + " · 日志更新"));
                if (recent.Length == 0) panel.Children.Add(DashboardText(_dashboardRecentLoading ? "正在读取游戏记录…" : "还没有找到游戏日志。启动并游玩后，最近版本会出现在这里。", 12, true));
                panel.Children.Add(DashboardText("依据 latest.log 修改时间；共享游戏目录的版本合并显示。", 10, true));
                break;
            case "library":
                var versions = DashboardInstances().OrderByDescending(instance => States.Instance.Starred[instance.PathInstance]).ThenBy(instance => instance.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                foreach (var instance in versions.Take(6))
                {
                    var starred = States.Instance.Starred[instance.PathInstance];
                    var versionLine = HybridSidebar.VersionLine(instance);
                    panel.Children.Add(DashboardInstanceButton(instance, (starred ? "★ 收藏  ·  " : "") + (string.IsNullOrEmpty(versionLine) ? "选择版本" : versionLine)));
                }
                if (versions.Length == 0) panel.Children.Add(DashboardText("这里还没有游戏。安装一个版本，开始你的第一次冒险。", 13, true));
                var browse = DashboardButton(versions.Length == 0 ? "安装游戏  ↗" : $"管理全部 {versions.Length} 个版本  ↗", () => DashboardNavigate(versions.Length == 0 ? "download" : "versions"));
                browse.Foreground = DashboardBrush("Hybrid.Accent");
                browse.HorizontalAlignment = HorizontalAlignment.Left;
                browse.Margin = new Thickness(0, 4, 0, 0);
                browse.Height = 32;
                browse.Padding = new Thickness(8, 0, 8, 0);
                browse.Background = Brushes.Transparent;
                browse.BorderBrush = Brushes.Transparent;
                panel.Children.Add(browse);
                break;
        }
        return panel;
    }

    private Button DashboardInstanceButton(McInstance instance, string detail)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = HybridSidebar.IconFor(instance);
        if (icon is not null)
        {
            var image = new Image { Source = icon, Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            row.Children.Add(image);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = DashboardText(instance.Name, 13);
        title.FontWeight = FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var line = DashboardText(detail, 11, true);
            line.TextWrapping = TextWrapping.NoWrap;
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(line);
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var button = DashboardButton(row, () => SelectDashboardInstance(instance));
        button.Height = double.NaN;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(10, 0, 10, 0);
        button.Margin = new Thickness(0, 0, 0, 6);
        button.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x26));
        button.IsEnabled = !_dashboardEditing;
        button.ToolTip = "选择版本；使用右侧启动按钮开始游戏";
        return button;
    }

    private async void SelectDashboardInstance(McInstance instance)
    {
        try
        {
            if (_dashboardEditing) return;
            LauncherLibrary.SelectInstance(instance);
            if (ModMain.frmMain is { } form) await form.NavigateHybridAsync("games");
            ShowSelectedVersion(instance);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "选择游戏组件版本失败", ModBase.LogLevel.Debug);
            ModMain.MyMsgBox(ex.Message, "无法选择版本");
        }
    }

    private async void DashboardNavigate(string route)
    {
        try { if (ModMain.frmMain is { } form) await form.NavigateHybridAsync(route); }
        catch (Exception ex) { ModMain.MyMsgBox(ex.Message, "无法打开页面"); }
    }

}
