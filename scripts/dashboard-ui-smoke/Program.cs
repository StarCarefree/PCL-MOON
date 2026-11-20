using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL;
using PCL.Core.App;
using PCL.Core.UI;

internal static partial class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    private static Action<Window>? _dialogCheck;
    private static Exception? _dialogError;
    private static string _screenshots = "";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: Dashboard.UiSmoke <launcher output directory> <scratch directory>");
        var output = Path.GetFullPath(args[0]);
        var root = Path.Combine(Path.GetFullPath(args[1]), "dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var key in new[] { "PCL_PATH", "PCL_PATH_SHARED", "PCL_PATH_LOCAL", "PCL_PATH_TEMP" })
            Environment.SetEnvironmentVariable(key, Path.Combine(root, key));
        _screenshots = Path.Combine(root, "screenshots");
        Directory.CreateDirectory(_screenshots);
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(output, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(output, name.Name + ".dll")) : null;
        try { Run(); Console.WriteLine($"DASHBOARD_UI_PASS: {_checks} checks\nScreenshots: {_screenshots}"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new Exception(message); }
    private static object? Call(PageLaunchRight page, string name, params object?[] args) => typeof(PageLaunchRight).GetMethod(name, Private)!.Invoke(page, args);
    private static T Field<T>(PageLaunchRight page, string name) => (T)typeof(PageLaunchRight).GetField(name, Private)!.GetValue(page)!;
    private static void Set(PageLaunchRight page, string name, object value) => typeof(PageLaunchRight).GetField(name, Private)!.SetValue(page, value);
    private static T Control<T>(PageLaunchRight page, string name) => (T)page.FindName(name);
    private static void Click(PageLaunchRight page, string name) => Call(page, name, null, new RoutedEventArgs());
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Tree(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static Button Button(Window dialog, string content) => Tree(dialog).OfType<Button>().Single(button => button.Content as string == content);
    private static void Render(FrameworkElement element, double width, double height, string? filename = null)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        if (filename is null) return;
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 19, 21)), null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(backdrop);
        bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_screenshots, filename)); png.Save(file);
    }
    private static void InitializeDefaults()
    {
        var service = typeof(PCL.Core.App.Configuration.ConfigService);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        service.GetMethod("_InitializeConfigItems", flags)!.Invoke(null, null);
        service.GetField("_isConfigItemsInitialized", flags)!.SetValue(null, true);
        var items = (System.Collections.IDictionary)service.GetField("_Items", flags)!.GetValue(null)!;
        foreach (var item in items.Values)
        {
            var type = item!.GetType();
            type.GetProperty("EnableCache")!.SetValue(item, true);
            var value = type.GetProperty("DefaultValueNoType")!.GetValue(item);
            var cache = type.GetField("_valueCache", Private)!.GetValue(item)!;
            cache.GetType().GetMethod("Write")!.Invoke(cache, [value, null]);
            type.GetField("_valueCache", Private)!.SetValue(item, cache);
        }
    }

    private static void DialogTest(PageLaunchRight page, string method, Action<Window> check, params object?[] args)
    {
        _dialogError = null;
        _dialogCheck = check;
        Call(page, method, args);
        _dialogCheck = null;
        if (_dialogError is not null) throw _dialogError;
    }

    private static void Run()
    {
        InitializeDefaults();
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["ColorBrush1"] = new SolidColorBrush(Color.FromRgb(236, 237, 239));
        app.Resources["ColorBrush3"] = new SolidColorBrush(Color.FromRgb(255, 185, 66));
        app.Resources["LaunchFontFamily"] = new FontFamily("Microsoft YaHei UI");
        app.Resources["ColorBrushSemiTransparent"] = Brushes.Transparent;
        app.Resources["Hybrid.Games"] = "我的游戏";
        app.Resources["Hybrid.VersionList"] = "版本列表";
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not Window dialog || _dialogCheck is null) return;
            dialog.Left = -10000; dialog.Top = -10000;
            var check = _dialogCheck;
            _dialogCheck = null;
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                try { dialog.UpdateLayout(); check(dialog); }
                catch (Exception ex) { _dialogError = ex; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }));
        }));
        var legacy = GameDashboardLayout.CreateDefault();
        legacy.Widgets[0].Title = "旧版本布局";
        legacy.Save(Path.Combine(Paths.Data, "game-dashboard.json"));
        var page = new PageLaunchRight();
        Control<FrameworkElement>(page, "PanHint").Visibility = Visibility.Collapsed;
        Control<FrameworkElement>(page, "PanLog").Visibility = Visibility.Collapsed;
        var layout = Field<GameDashboardLayout>(page, "_dashboard");
        Check(layout.Widgets[0].Title == "旧版本布局", "First load must import the existing local layout");
        Check(File.Exists(Path.Combine(Paths.SharedData, "game-dashboard.json")), "Legacy layout must migrate to stable user data");
        var grid = Control<Grid>(page, "DashboardGrid");
        Render(page, 900, 820, "default.png");
        Check(grid.Children.Count == 3, "Default must contain three widgets");
        Check(grid.ColumnDefinitions.Count == 2, "Wide viewport must use two columns");
        Check(layout.Widgets[2].Kind == "recent" && !layout.Widgets[2].Wide, "Recent widget must sit beside greeting by default");
        Check(InsightText(grid).Contains("依据 latest.log 修改时间"), "Default layout must render recent widget beside greeting");
        Check(Control<Border>(page, "HeroCard").Parent is ContentControl, "Original hero must be hosted in a component");
        Click(page, "DashboardEdit_Click");
        Check(Field<bool>(page, "_dashboardEditing"), "Edit must create draft");
        var first = layout.Widgets[0];
        Call(page, "MoveDashboardWidget", first, 1);
        Check(layout.Widgets[1].Id == first.Id, "Move must reorder widgets");
        Click(page, "DashboardColumns_Click");
        Check(layout.Columns == 1 && grid.ColumnDefinitions.Count == 1, "Single-column preference must take effect");
        Click(page, "DashboardCancel_Click");
        layout = Field<GameDashboardLayout>(page, "_dashboard");
        Check(layout.Columns == 2 && layout.Widgets[0].Id == first.Id, "Cancel must restore order and columns");
        Check(!Field<bool>(page, "_dashboardEditing"), "Cancel must exit edit mode");
        Render(page, 480, 820, "narrow.png");
        Check(grid.ColumnDefinitions.Count == 1, "Narrow viewport must collapse to one column");
        Check(layout.Columns == 2, "Responsive layout must not overwrite column preference");
        Click(page, "DashboardEdit_Click");
        DialogTest(page, "ShowDashboardCatalog", dialog =>
        {
            var search = Tree(dialog).OfType<TextBox>().Single();
            search.Text = "日历"; dialog.UpdateLayout();
            var add = Tree(dialog).OfType<Button>().Single(button => button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("日历")));
            Check(add.IsEnabled, "Calendar must be addable");
            Render((FrameworkElement)dialog.Content, 530, 620, "catalog.png");
            add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        });
        layout = Field<GameDashboardLayout>(page, "_dashboard");
        Check(layout.Widgets.Any(widget => widget.Kind == "calendar"), "Catalog must add calendar to draft");
        var note = new GameDashboardWidget { Kind = "note" };
        DialogTest(page, "EditDashboardWidget", dialog =>
        {
            var inputs = Tree(dialog).OfType<TextBox>().ToArray();
            inputs[0].Text = "冒险计划";
            inputs[1].Text = "基地坐标 120 / 64 / -250\n下一站：末地";
            Tree(dialog).OfType<CheckBox>().Single().IsChecked = true;
            Render((FrameworkElement)dialog.Content, 530, 620, "settings.png");
            Button(dialog, "应用到草稿").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }, note);
        Check(note.Title == "冒险计划" && note.Text.Contains("末地") && note.Wide, "Settings must apply title, content and width");
        layout.Widgets.Add(note);
        layout.Widgets.Add(new GameDashboardWidget { Kind = "stats" });
        layout.Widgets.Add(new GameDashboardWidget { Kind = "shortcut", Target = "missing" });
        Call(page, "RenderDashboard");
        Render(page, 900, 1100, "editing.png");
        Check(grid.Children.Count == layout.Widgets.Count, "Every component must have a card");
        // Avoid notification lifecycle: persist with the exact production model, then rebuild the page.
        layout.Save(Path.Combine(Paths.SharedData, "game-dashboard.json"));
        var restored = GameDashboardLayout.Load(Path.Combine(Paths.SharedData, "game-dashboard.json"));
        Check(restored.Widgets.Any(widget => widget.Kind == "note" && widget.Text == note.Text), "Saved note must round-trip");
        var restoredPage = new PageLaunchRight();
        Check(Field<GameDashboardLayout>(restoredPage, "_dashboard").Widgets.Count == layout.Widgets.Count, "New page must restore saved layout");
        Click(page, "DashboardCancel_Click");
        Check(Field<GameDashboardLayout>(page, "_dashboard").Widgets.Count == 3, "Cancel must roll back added widgets");
        Click(page, "DashboardEdit_Click");
        layout = Field<GameDashboardLayout>(page, "_dashboard");
        layout.Widgets.Clear();
        Call(page, "RenderDashboard");
        Check(Control<Border>(page, "DashboardEmpty").Visibility == Visibility.Visible && grid.Children.Count == 0, "Empty layout must show add affordance");
        Render(page, 480, 700, "empty.png");
        Click(page, "DashboardCancel_Click");
        Check(Control<Border>(page, "HeroCard").Parent is ContentControl, "Hero must survive repeated detach/rebuild");
        // Exercise the real Save handler, not only the model serialization API.
        Click(page, "DashboardEdit_Click");
        layout = Field<GameDashboardLayout>(page, "_dashboard");
        Call(page, "MoveDashboardWidget", layout.Widgets[0], 1);
        Click(page, "DashboardColumns_Click");
        layout.Widgets[0].Title = "本地持久化验证";
        var expected = layout.Clone();
        Click(page, "DashboardSave_Click");
        Check(File.Exists(Path.Combine(Paths.SharedData, "game-dashboard.json")), "Save button must create stable user layout file");
        Check(!Field<bool>(page, "_dashboardEditing"), "Save button must exit edit mode");
        var reopened = Field<GameDashboardLayout>(new PageLaunchRight(), "_dashboard");
        Check(reopened.Columns == expected.Columns, "Save button must persist column preference");
        Check(reopened.Widgets.Select(widget => widget.Id).SequenceEqual(expected.Widgets.Select(widget => widget.Id)), "Save button must persist widget order");
        Check(reopened.Widgets[0].Title == "本地持久化验证", "Save button must persist component settings");
        Console.WriteLine("PASS real save button / local file / fresh page reload");
        // An update can put the launcher in a new directory; user layout must survive it.
        Paths.Data = Path.Combine(Paths.Temp, "updated-launcher", "PCL");
        Directory.CreateDirectory(Paths.Data);
        GameDashboardLayout.CreateDefault().Save(Path.Combine(Paths.Data, "game-dashboard.json"));
        var updated = Field<GameDashboardLayout>(new PageLaunchRight(), "_dashboard");
        Check(updated.Columns == expected.Columns, "Updating launcher directory must preserve column preference");
        Check(updated.Widgets.Select(widget => widget.Id).SequenceEqual(expected.Widgets.Select(widget => widget.Id)), "Updating launcher directory must preserve widget order");
        Check(updated.Widgets[0].Title == expected.Widgets[0].Title, "Updating launcher directory must preserve component settings");
        Console.WriteLine("PASS launcher update preserves user layout");
        CheckDashboardInsights(page);
        app.Shutdown();
    }
}
