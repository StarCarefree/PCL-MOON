using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.UI;

namespace PCL;

public partial class PageLaunchRight
{
    private GameDashboardLayout? _dashboard;
    private GameDashboardLayout? _dashboardSaved;
    private bool _dashboardEditing;
    private int _dashboardColumnCount;
    private DateTime _dashboardRefreshed;
    private string _dashboardVersion = "";
    private McInstance? _dashboardSelected;
    private readonly Dictionary<string, ContentControl> _dashboardContents = new();
    private static string DashboardPath => Path.Combine(Paths.SharedData, "game-dashboard.json");
    private const string DashboardDragFormat = "PCL-MOON.GameDashboard.Widget";
    private static readonly (string Kind, string Title, string Icon, string Group, string Description)[] DashboardCatalog =
    [
        ("hero", "当前游戏", "◈", "游戏与收藏", "展示当前版本、加载器和游戏状态。"),
        ("recent", "最近游玩", "↶", "游戏与收藏", "按游戏日志更新时间列出最近玩过的版本。"),
        ("library", "我的游戏库", "▦", "游戏与收藏", "查看已安装版本，收藏的版本优先显示。"),
        ("shortcut", "版本快捷入口", "↗", "游戏与收藏", "固定一个版本，点击选择后从右侧启动。"),
        ("favorites", "收藏游戏", "★", "游戏与收藏", "集中查看已收藏的本地版本，快速切换游戏。"),
        ("worlds", "单人世界", "◇", "游戏与收藏", "查看当前版本的世界与最近保存时间，打开存档目录。"),
        ("servers", "已保存服务器", "☷", "游戏与收藏", "读取当前版本的服务器列表，一键复制地址。"),
        ("actions", "常用操作", "⌘", "日常与工具", "安装版本、管理游戏、下载任务和多人游戏入口。"),
        ("folders", "游戏文件夹", "↗", "日常与工具", "直达当前版本的模组、存档、截图和日志目录。"),
        ("greeting", "今日问候", "☀", "日常与工具", "随时间变化的问候和当前账号。"),
        ("calendar", "日历", "▦", "日常与工具", "翻月查看游玩热力、每日明细与日期便签。"),
        ("stats", "游玩统计", "◷", "日常与工具", "按时间与版本筛选，查看趋势、排行并导出 CSV。"),
        ("note", "便签", "✎", "日常与工具", "记下建造计划、坐标或下一次冒险。")
    ];

    private void InitializeDashboard()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/Plain Craft Launcher 2;component/Modules/HybridUi/GameDashboardTheme.xaml", UriKind.Relative) });
        // Layout is a user preference: keep it across launcher directory/version changes.
        var legacyPath = Path.Combine(Paths.Data, "game-dashboard.json");
        var migrate = !File.Exists(DashboardPath) && File.Exists(legacyPath);
        _dashboard = GameDashboardLayout.Load(migrate ? legacyPath : DashboardPath);
        if (migrate)
        {
            try { _dashboard.Save(DashboardPath); }
            catch (Exception ex) { ModBase.Log(ex, "迁移游戏组件布局失败，保留旧布局", ModBase.LogLevel.Debug); }
        }
        // Keep the original named hero and its launch/version hooks; only reparent its presentation.
        PanMain.Children.Remove(HeroCard);
        Unloaded += (_, _) =>
        {
            _dashboardFilesCancellation?.Cancel();
            _dashboardFilesCancellation?.Dispose();
            _dashboardFilesCancellation = null;
            _dashboardFilesLoading = false;
            _dashboardFilesChecked = default;
        };
        RenderDashboard();
    }

    private void BeginDashboardEdit()
    {
        if (_dashboardEditing || _dashboard is null) return;
        _dashboardSaved = _dashboard.Clone();
        _dashboardEditing = true;
        RenderDashboard();
    }

    private void DashboardEdit_Click(object sender, RoutedEventArgs e) => BeginDashboardEdit();
    private void DashboardAdd_Click(object sender, RoutedEventArgs e)
    {
        BeginDashboardEdit();
        ShowDashboardCatalog();
    }

    private void DashboardColumns_Click(object sender, RoutedEventArgs e)
    {
        if (_dashboard is null) return;
        _dashboard.Columns = _dashboard.Columns == 2 ? 1 : 2;
        RenderDashboard();
    }

    private void DashboardReset_Click(object sender, RoutedEventArgs e)
    {
        if (ModMain.MyMsgBox("恢复默认组件与布局？保存后才会覆盖当前配置。", "恢复默认", "恢复", "取消") != 1) return;
        _dashboard = GameDashboardLayout.CreateDefault();
        RenderDashboard();
    }

    private void DashboardCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_dashboardSaved is not null) _dashboard = _dashboardSaved;
        _dashboardSaved = null;
        _dashboardEditing = false;
        RenderDashboard();
    }

    private void DashboardSave_Click(object sender, RoutedEventArgs e)
    {
        if (_dashboard is null) return;
        try
        {
            _dashboard.Save(DashboardPath);
            _dashboardSaved = null;
            _dashboardEditing = false;
            RenderDashboard();
            HintService.Hint("组件布局已保存");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "保存游戏组件布局失败", ModBase.LogLevel.Debug);
            ModMain.MyMsgBox("无法保存布局，编辑内容已保留。\n" + ex.Message, "保存失败");
        }
    }

    private void DashboardGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_dashboard is not null && DashboardEffectiveColumns() != _dashboardColumnCount)
            ArrangeDashboard();
    }

    private int DashboardEffectiveColumns() => _dashboard?.Columns == 2 && DashboardGrid.ActualWidth >= 620 ? 2 : 1;

    private void ArrangeDashboard()
    {
        if (_dashboard is null) return;
        _dashboardColumnCount = DashboardEffectiveColumns();
        DashboardGrid.ColumnDefinitions.Clear();
        DashboardGrid.RowDefinitions.Clear();
        for (var c = 0; c < _dashboardColumnCount; c++)
            DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var row = 0;
        var column = 0;
        for (var i = 0; i < _dashboard.Widgets.Count; i++)
        {
            var widget = _dashboard.Widgets[i];
            var span = widget.Wide ? _dashboardColumnCount : 1;
            if (span == 2 && column != 0) { row++; column = 0; }
            while (DashboardGrid.RowDefinitions.Count <= row)
                DashboardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = (FrameworkElement)DashboardGrid.Children[i];
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            Grid.SetColumnSpan(card, span);
            card.Margin = new Thickness(0, 0, column == 0 && span == 1 && _dashboardColumnCount == 2 ? 12 : 0, 12);
            column += span;
            if (column >= _dashboardColumnCount) { row++; column = 0; }
        }
    }

    private void RenderDashboard()
    {
        if (_dashboard is null) return;
        // Detach the shared hero before rebuilding its wrapper.
        if (HeroCard.Parent is ContentControl parent) parent.Content = null;
        HeroCard.Visibility = Visibility.Collapsed;
        DashboardGrid.Children.Clear();
        _dashboardContents.Clear();
        DashboardEditBar.Visibility = _dashboardEditing ? Visibility.Visible : Visibility.Collapsed;
        DashboardEdit.Visibility = _dashboardEditing ? Visibility.Collapsed : Visibility.Visible;
        DashboardColumns.Content = _dashboard.Columns == 2 ? "布局：双列" : "布局：单列";
        DashboardEmpty.Visibility = _dashboard.Widgets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var widget in _dashboard.Widgets)
        {
            var catalog = DashboardCatalog.FirstOrDefault(entry => entry.Kind == widget.Kind);
            var panel = new StackPanel();
            var header = new Grid { Margin = new Thickness(16, 12, 12, 8), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = DashboardText(catalog.Icon + "  " + (string.IsNullOrWhiteSpace(widget.Title) ? catalog.Title : widget.Title), 13);
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.TextWrapping = TextWrapping.NoWrap;
            header.Children.Add(title);
            if (_dashboardEditing)
            {
                header.Cursor = Cursors.SizeAll;
                header.ToolTip = "拖动标题排序";
                Point start = default;
                header.PreviewMouseLeftButtonDown += (_, args) => start = args.GetPosition(header);
                header.MouseMove += (_, args) =>
                {
                    if (args.LeftButton != MouseButtonState.Pressed || IsDashboardButton(args.OriginalSource as DependencyObject)) return;
                    var delta = args.GetPosition(header) - start;
                    if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    DragDrop.DoDragDrop(header, new DataObject(DashboardDragFormat, widget.Id), DragDropEffects.Move);
                };
                var tools = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
                var index = _dashboard.Widgets.IndexOf(widget);
                var up = DashboardButton("↑", () => MoveDashboardWidget(widget, -1), "上移");
                up.IsEnabled = index > 0;
                tools.Children.Add(up);
                var down = DashboardButton("↓", () => MoveDashboardWidget(widget, 1), "下移");
                down.IsEnabled = index < _dashboard.Widgets.Count - 1;
                tools.Children.Add(down);
                tools.Children.Add(DashboardButton("设置", () => EditDashboardWidget(widget), "编辑标题、尺寸与内容"));
                tools.Children.Add(DashboardButton("×", () => { _dashboard.Widgets.Remove(widget); RenderDashboard(); }, "移除组件，不删除游戏"));
                Grid.SetColumn(tools, 1);
                header.Children.Add(tools);
            }
            panel.Children.Add(header);
            var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(16, 0, 16, 16) };
            _dashboardContents[widget.Id] = content;
            if (widget.Kind == "hero")
            {
                HeroCard.Visibility = Visibility.Visible;
                content.Margin = new Thickness(0);
                if (HeroCard.Parent is null) content.Content = HeroCard;
                if (PanRunning.Parent is Panel previous && previous != panel) previous.Children.Remove(PanRunning);
                PanRunning.Margin = new Thickness(16, 12, 16, 16);
                RunningInstanceCards.Arrange(PanRunning);
            }
            else content.Content = CreateDashboardContent(widget);
            panel.Children.Add(content);
            if (widget.Kind == "hero" && PanRunning.Parent is null)
            {
                var heroIndex = panel.Children.IndexOf(content);
                panel.Children.Insert(heroIndex + 1, PanRunning);
            }
            var card = new Border
            {
                Background = DashboardBrush("Hybrid.Surface"), BorderBrush = DashboardBrush(_dashboardEditing ? "Hybrid.Accent" : "Hybrid.Line"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = panel, ClipToBounds = true,
                AllowDrop = _dashboardEditing
            };
            card.DragOver += (_, args) =>
            {
                args.Effects = ValidDashboardDrag(args, widget) ? DragDropEffects.Move : DragDropEffects.None;
                args.Handled = true;
            };
            card.Drop += (_, args) =>
            {
                if (!ValidDashboardDrag(args, widget)) return;
                var id = (string)args.Data.GetData(DashboardDragFormat);
                var source = _dashboard.Widgets.First(item => item.Id == id);
                var target = _dashboard.Widgets.IndexOf(widget);
                _dashboard.Widgets.Remove(source);
                _dashboard.Widgets.Insert(target, source);
                args.Handled = true;
                RenderDashboard();
            };
            DashboardGrid.Children.Add(card);
        }
        ArrangeDashboard();
        if (!_dashboardEditing) RefreshDashboardFiles();
    }

    private bool ValidDashboardDrag(DragEventArgs args, GameDashboardWidget target) => _dashboardEditing &&
        args.Data.GetDataPresent(DashboardDragFormat) && args.Data.GetData(DashboardDragFormat) is string id &&
        id != target.Id && _dashboard!.Widgets.Any(item => item.Id == id);

    private static bool IsDashboardButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void MoveDashboardWidget(GameDashboardWidget widget, int offset)
    {
        if (_dashboard is null) return;
        var index = _dashboard.Widgets.IndexOf(widget);
        var next = index + offset;
        if (index < 0 || next < 0 || next >= _dashboard.Widgets.Count) return;
        _dashboard.Widgets.RemoveAt(index);
        _dashboard.Widgets.Insert(next, widget);
        RenderDashboard();
    }

    private void ShowDashboardCatalog()
    {
        var dialog = CreateDashboardDialog("添加组件", "WIDGET GALLERY / 为你的游戏空间添一块拼图");
        var list = new StackPanel();
        var search = DashboardInput("", 80);
        search.ToolTip = "搜索组件名称或说明";
        list.Children.Add(DashboardText("搜索组件", 12, true));
        list.Children.Add(search);
        var cards = new StackPanel();
        list.Children.Add(cards);
        void Filter()
        {
            cards.Children.Clear();
            var matches = DashboardCatalog.Where(item => (item.Title + item.Description).Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
            foreach (var group in matches.GroupBy(item => item.Group))
            {
                var groupTitle = DashboardText(group.Key, 11, true);
                groupTitle.Margin = new Thickness(0, 18, 0, 8);
                cards.Children.Add(groupTitle);
                foreach (var entry in group)
                {
                    var body = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };
                    body.Children.Add(DashboardText(entry.Icon + "  " + entry.Title, 14));
                    var description = DashboardText(entry.Description, 12, true);
                    description.Margin = new Thickness(23, 6, 0, 0);
                    body.Children.Add(description);
                    var add = DashboardButton(body, () =>
                    {
                        if (_dashboard!.Widgets.Count >= 40) return;
                        var singleton = entry.Kind is not ("shortcut" or "note");
                        if (singleton && _dashboard.Widgets.Any(item => item.Kind == entry.Kind)) return;
                        var widget = new GameDashboardWidget { Kind = entry.Kind, Wide = entry.Kind is "hero" or "stats" };
                        if (entry.Kind == "shortcut")
                        {
                            widget.Target = _dashboardSelected?.PathInstance ?? "";
                            if (!EditDashboardWidget(widget)) return;
                        }
                        else if (entry.Kind == "note" && !EditDashboardWidget(widget)) return;
                        _dashboard.Widgets.Add(widget);
                        RenderDashboard();
                        dialog.Close();
                    });
                    add.HorizontalContentAlignment = HorizontalAlignment.Left;
                    if (_dashboard!.Widgets.Count >= 40) { add.IsEnabled = false; add.ToolTip = "最多添加 40 个组件"; }
                    add.Height = double.NaN;
                    add.MinHeight = 70;
                    add.Background = DashboardBrush("Hybrid.Surface");
                    add.BorderBrush = DashboardBrush("Hybrid.Line");
                    add.Margin = new Thickness(0, 0, 0, 6);
                    if (entry.Kind is not ("shortcut" or "note") && _dashboard!.Widgets.Any(item => item.Kind == entry.Kind))
                    {
                        body.Children.Add(DashboardText("已添加", 11, true));
                        add.IsEnabled = false;
                    }
                    cards.Children.Add(add);
                }
            }
            if (matches.Length == 0) cards.Children.Add(DashboardText("没有匹配的组件，试试其他关键词。", 13, true));
        }
        search.TextChanged += (_, _) => Filter();
        Filter();
        SetDashboardDialogBody(dialog, list);
        dialog.ShowDialog();
    }

    private bool EditDashboardWidget(GameDashboardWidget widget)
    {
        var dialog = CreateDashboardDialog("编辑组件", "调整标题、占用空间与组件内容");
        var panel = new StackPanel();
        panel.Children.Add(DashboardText("组件标题 · 留空使用默认标题", 12, true));
        var title = DashboardInput(widget.Title, 80);
        panel.Children.Add(title);
        var wide = new CheckBox { Content = "占满一行", IsChecked = widget.Wide, Foreground = DashboardBrush("Hybrid.Text"), Margin = new Thickness(0, 10, 0, 16) };
        panel.Children.Add(wide);
        ComboBox? versions = null;
        TextBox? text = null;
        if (widget.Kind == "shortcut")
        {
            panel.Children.Add(DashboardText("绑定已安装版本", 12, true));
            versions = new ComboBox { Margin = new Thickness(0, 8, 0, 14), MinHeight = 36, MaxDropDownHeight = 260 };
            foreach (var instance in DashboardInstances())
                versions.Items.Add(new ComboBoxItem { Content = instance.Name, Tag = instance.PathInstance });
            versions.SelectedItem = versions.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals((string)item.Tag, widget.Target, StringComparison.OrdinalIgnoreCase));
            panel.Children.Add(versions);
            panel.Children.Add(DashboardText("点击快捷入口会选择版本，启动仍使用右侧按钮。", 11, true));
        }
        if (widget.Kind == "note")
        {
            panel.Children.Add(DashboardText("便签内容", 12, true));
            text = DashboardInput(widget.Text, 4000);
            text.AcceptsReturn = true;
            text.TextWrapping = TextWrapping.Wrap;
            text.Height = 180;
            text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            panel.Children.Add(text);
        }
        var validation = DashboardText("", 12, true);
        panel.Children.Add(validation);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(DashboardButton("取消", () => dialog.Close()));
        var apply = DashboardButton("应用到草稿", () =>
        {
            if (widget.Kind == "shortcut" && versions?.SelectedItem is not ComboBoxItem)
            { validation.Text = "请选择一个已安装版本；还没有版本时，请先安装游戏。"; return; }
            widget.Title = title.Text.Trim();
            widget.Wide = wide.IsChecked == true;
            widget.Text = text?.Text ?? widget.Text;
            widget.Target = (versions?.SelectedItem as ComboBoxItem)?.Tag as string ?? widget.Target;
            dialog.DialogResult = true;
        });
        apply.Foreground = DashboardBrush("Hybrid.Accent");
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        SetDashboardDialogBody(dialog, panel);
        if (dialog.ShowDialog() != true) return false;
        RenderDashboard();
        return true;
    }

    private Brush DashboardBrush(string key) => (Brush)FindResource(key);
    private TextBlock DashboardText(string text, double size = 13, bool muted = false) => new()
    {
        Text = text, FontSize = size, Foreground = DashboardBrush(muted ? "Hybrid.Muted" : "Hybrid.Text"),
        TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.55
    };

    private Button DashboardButton(object content, Action action, string? tooltip = null)
    {
        var button = new Button { Content = content, ToolTip = tooltip, Style = (Style)FindResource("Dashboard.Button"), MinWidth = 28 };
        button.Click += (_, _) => action();
        return button;
    }

    private TextBox DashboardInput(string text, int limit) => new()
    {
        Text = text, MaxLength = limit, Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 12),
        Background = DashboardBrush("Hybrid.Surface"), Foreground = DashboardBrush("Hybrid.Text"),
        BorderBrush = DashboardBrush("Hybrid.Line"), CaretBrush = DashboardBrush("Hybrid.Accent"), MinHeight = 38
    };

    private Window CreateDashboardDialog(string title, string subtitle)
    {
        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = title, Owner = owner, Width = Math.Min(580, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(680, SystemParameters.WorkArea.Height - 60), MinWidth = 360, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = DashboardBrush("Hybrid.Background"), Foreground = DashboardBrush("Hybrid.Text"),
            FontFamily = new FontFamily("Microsoft YaHei UI"), ResizeMode = ResizeMode.CanResizeWithGrip,
            WindowStyle = WindowStyle.None, BorderBrush = DashboardBrush("Hybrid.Line"), BorderThickness = new Thickness(1)
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog, new System.Windows.Shell.WindowChrome
        { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8) });
        var shell = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(DashboardText(subtitle, 10, true));
        var heading = DashboardText(title, 26);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 6, 0, 0);
        header.Children.Add(heading);
        var titlebar = new DockPanel { Background = Brushes.Transparent };
        var close = DashboardButton("×", () => dialog.Close(), "关闭 · Esc");
        close.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(close, Dock.Right);
        titlebar.Children.Add(close);
        titlebar.Children.Add(header);
        titlebar.MouseLeftButtonDown += (_, args) => { if (!IsDashboardButton(args.OriginalSource as DependencyObject)) dialog.DragMove(); };
        DockPanel.SetDock(titlebar, Dock.Top);
        shell.Children.Add(titlebar);
        dialog.Content = shell;
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { dialog.Close(); args.Handled = true; } };
        return dialog;
    }

    private static void SetDashboardDialogBody(Window dialog, FrameworkElement body) =>
        ((DockPanel)dialog.Content).Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) });
}
