using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.UI;

namespace PCL;

public partial class PageLaunchRight
{
    private CancellationTokenSource? _dashboardFilesCancellation;
    private GameDashboardFileSnapshot _dashboardFiles = new([], [], null);
    private string _dashboardFilesRoot = "";
    private bool _dashboardFilesLoading;
    private DateTime _dashboardFilesChecked;

    private string DashboardGameRoot()
    {
        try { return _dashboardSelected?.PathIndie ?? ""; }
        catch (Exception ex) { ModBase.Log(ex, "读取组件游戏目录失败", ModBase.LogLevel.Debug); return ""; }
    }

    private async void RefreshDashboardFiles(bool force = false)
    {
        if (_dashboard is null || !_dashboard.Widgets.Any(widget => widget.Kind is "worlds" or "servers")) return;
        var root = DashboardGameRoot();
        var same = string.Equals(root, _dashboardFilesRoot, StringComparison.OrdinalIgnoreCase);
        if (same && (_dashboardFilesLoading || !force && DateTime.UtcNow - _dashboardFilesChecked < TimeSpan.FromSeconds(30))) return;
        _dashboardFilesCancellation?.Cancel();
        _dashboardFilesCancellation?.Dispose();
        var cancellation = _dashboardFilesCancellation = new CancellationTokenSource();
        _dashboardFilesRoot = root;
        _dashboardFilesChecked = DateTime.UtcNow;
        if (!same) _dashboardFiles = new([], [], null);
        _dashboardFilesLoading = root.Length > 0;
        try
        {
            var snapshot = root.Length == 0 ? new GameDashboardFileSnapshot([], [], null) : await GameDashboardFiles.LoadAsync(root, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, _dashboardFilesCancellation)) return;
            _dashboardFiles = snapshot;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (!ReferenceEquals(cancellation, _dashboardFilesCancellation)) return;
            _dashboardFiles = new([], [], "读取失败：" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(cancellation, _dashboardFilesCancellation)) _dashboardFilesLoading = false;
        }
        if (_dashboardEditing) return;
        RefreshDashboardFileCards();
    }

    private void RefreshDashboardFileCards()
    {
        if (_dashboard is null) return;
        foreach (var widget in _dashboard.Widgets.Where(widget => widget.Kind is "worlds" or "servers"))
            if (_dashboardContents.TryGetValue(widget.Id, out var content)) content.Content = CreateDashboardTools(widget);
    }

    private FrameworkElement CreateDashboardTools(GameDashboardWidget widget)
    {
        var panel = new StackPanel { MinHeight = 100 };
        switch (widget.Kind)
        {
            case "favorites":
                var favorites = DashboardInstances().Where(instance => PCL.Core.App.States.Instance.Starred[instance.PathInstance]).OrderBy(instance => instance.Name).ToArray();
                foreach (var instance in favorites.Take(6)) panel.Children.Add(DashboardInstanceButton(instance, InstanceActivity.PlayDetail(instance.PathInstance)));
                if (favorites.Length == 0) panel.Children.Add(DashboardText("还没有收藏版本。在版本列表中收藏后，即可在这里快速选择。", 12, true));
                panel.Children.Add(DashboardToolButton($"管理收藏 · {favorites.Length} 个", () => DashboardNavigate("versions")));
                break;
            case "actions":
                var actions = new WrapPanel();
                foreach (var (name, route) in new[] { ("＋ 安装版本", "download"), ("▦ 游戏管理", "versions"), ("↓ 下载任务", "tasks"), ("⌁ 多人游戏", "multiplayer"), ("⚙ 启动设置", "settings") })
                {
                    var button = DashboardToolButton(name, () => DashboardNavigate(route));
                    button.Margin = new Thickness(0, 0, 8, 8);
                    actions.Children.Add(button);
                }
                panel.Children.Add(actions);
                break;
            case "folders":
                var root = DashboardGameRoot();
                panel.Children.Add(DashboardText(_dashboardSelected is null ? "请先选择一个游戏版本。" : "当前版本 · " + _dashboardSelected.Name, 12, true));
                var folders = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var (name, relative) in new[] { ("游戏目录", ""), ("模组", "mods"), ("存档", "saves"), ("截图", "screenshots"), ("日志", "logs"), ("资源包", "resourcepacks") })
                {
                    var path = root.Length == 0 ? "" : Path.Combine(root, relative);
                    var button = DashboardToolButton(name + " ↗", () => DashboardOpenFolder(path));
                    button.IsEnabled = !_dashboardEditing && path.Length > 0 && Directory.Exists(path);
                    button.ToolTip = path.Length == 0 ? "先选择版本" : Directory.Exists(path) ? path : "目录尚未创建；进入游戏使用该功能后会出现";
                    button.Margin = new Thickness(0, 0, 6, 6);
                    folders.Children.Add(button);
                }
                panel.Children.Add(folders);
                break;
            case "worlds":
            case "servers":
                var gameRoot = DashboardGameRoot();
                if (!string.Equals(gameRoot, _dashboardFilesRoot, StringComparison.OrdinalIgnoreCase))
                {
                    // Never show the previous instance's cached data while a new request is pending.
                    panel.Children.Add(DashboardText("请选择版本或等待数据刷新。", 12, true));
                    break;
                }
                panel.Children.Add(DashboardText("当前版本 · " + (_dashboardSelected?.Name ?? "未选择"), 11, true));
                if (_dashboardSelected is null) panel.Children.Add(DashboardText("请先选择一个已安装版本。", 12, true));
                else if (_dashboardFilesLoading) panel.Children.Add(DashboardText("正在读取本地游戏数据…", 12, true));
                else if (widget.Kind == "worlds")
                {
                    foreach (var world in _dashboardFiles.Worlds)
                    {
                        var row = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
                        row.Children.Add(DashboardText(world.Name, 13));
                        row.Children.Add(DashboardText(world.Version + " · " + (world.LastPlayedUtc == DateTime.MinValue ? "未知保存时间" : world.LastPlayedUtc.ToLocalTime().ToString("MM.dd HH:mm")), 11, true));
                        var open = DashboardToolButton(row, () => DashboardOpenFolder(world.Folder));
                        open.Height = double.NaN;
                        open.HorizontalContentAlignment = HorizontalAlignment.Left;
                        open.ToolTip = "打开世界文件夹，不会修改或启动世界";
                        panel.Children.Add(open);
                    }
                    if (_dashboardFiles.Worlds.Count == 0) panel.Children.Add(DashboardText("暂无可读取的世界。", 12, true));
                    panel.Children.Add(DashboardText("预览最近目录中的最多6个世界（扫描上限100个）；不含游玩中的未保存进度。", 10, true));
                }
                else
                {
                    foreach (var server in _dashboardFiles.Servers)
                    {
                        var row = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
                        row.Children.Add(DashboardText(server.Name, 13));
                        row.Children.Add(DashboardText(server.Address + "  ·  点击复制", 11, true));
                        var copy = DashboardToolButton(row, () => DashboardCopyAddress(server.Address));
                        copy.Height = double.NaN;
                        copy.HorizontalContentAlignment = HorizontalAlignment.Left;
                        copy.ToolTip = "复制地址，不会自动连接";
                        panel.Children.Add(copy);
                    }
                    if (_dashboardFiles.Servers.Count == 0) panel.Children.Add(DashboardText("游戏中保存的服务器会显示在这里。", 12, true));
                    panel.Children.Add(DashboardText("只读 servers.dat · 最多预览6个服务器", 10, true));
                }
                if (_dashboardFiles.Error is { } error) panel.Children.Add(DashboardText(error, 11, true));
                var refresh = DashboardToolButton("刷新本地数据", () => { RefreshDashboardFiles(true); RefreshDashboardFileCards(); });
                refresh.IsEnabled = !_dashboardEditing && !_dashboardFilesLoading && _dashboardSelected is not null;
                refresh.HorizontalAlignment = HorizontalAlignment.Left;
                panel.Children.Add(refresh);
                break;
        }
        return panel;
    }

    private Button DashboardToolButton(object content, Action action)
    {
        var button = DashboardButton(content, action);
        button.IsEnabled = !_dashboardEditing;
        return button;
    }

    private void DashboardOpenFolder(string path)
    {
        try
        {
            if (_dashboardEditing) return;
            if (!Directory.Exists(path)) { HintService.Hint("该目录不存在，可能已被移动或尚未生成。"); return; }
            ModBase.OpenExplorer(path);
        }
        catch (Exception ex) { ModMain.MyMsgBox(ex.Message, "无法打开游戏目录"); }
    }

    private static void DashboardCopyAddress(string address)
    {
        try { Clipboard.SetText(address); HintService.Hint("服务器地址已复制"); }
        catch (Exception ex) { ModMain.MyMsgBox(ex.Message, "复制失败"); }
    }
}
