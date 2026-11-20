using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL;
using PCL.Core.Minecraft.Launcher;

internal static partial class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static T Control<T>(HybridLaunchScreen view, string name) => (T)view.FindName(name);
    private static DispatcherTimer Clock(HybridLaunchScreen view) => (DispatcherTimer)typeof(HybridLaunchScreen).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static object? ClockOf(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Button Action(HybridLaunchScreen view, string id) => Tree(view).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id);
    private static void Capture(FrameworkElement view, string file)
    {
        view.UpdateLayout();
        var image = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(view); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(file); png.Save(stream);
    }
    private static void AssertLayout(HybridLaunchScreen view)
    {
        view.UpdateLayout();
        Check(!Tree(view).OfType<ScrollViewer>().Any(), "Loading window must not need scrolling");
        foreach (var name in new[] { "LauncherStep", "GameStep", "StatusPanel", "CancelButton", "VersionName" })
        {
            var element = Control<FrameworkElement>(view, name);
            var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= view.ActualWidth + 1 && bounds.Bottom <= view.ActualHeight + 1, name + " clipped");
        }
    }
    [STAThread]
    public static void Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new HybridLaunchWindow { Left = -10000, Top = -10000 };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await CheckPreferences(args[1]);
                if (args.Length > 2) CheckBridgeRecording(args[2]);
                if (SystemParameters.ClientAreaAnimation)
                    Check(window.Opacity < 1 && ClockOf(window, "_entranceGuard") is DispatcherTimer { IsEnabled: true }, "Launch window must ease in instead of popping");
                else
                    Check(Math.Abs(window.Opacity - 1) < 0.001, "Disabled client animation must show the launch window immediately");
                var view = (HybridLaunchScreen)window.FindName("Screen");
                Check(window.Owner is null && window.ShowInTaskbar && !window.Topmost, "Must be an ownerless non-topmost independent window");
                Check(window.IsVisible && Clock(view).IsEnabled, "Independent window must start its clock");
                window.ApplyProgress(new(LaunchProgressPhase.Launcher, "补全游戏文件", "下载资源  ·  12 MiB/s", .42));
                await Task.Delay(650); view.UpdateLayout();
                Check(window.Opacity > 0.98, "Entrance must finish fully opaque");
                Check(Control<TextBlock>(view, "Percentage").Text == "42%", "Launcher progress missing");
                Check(Control<TextBlock>(view, "LauncherState").Text == "进行中", "Launcher step missing");
                AssertLayout(view); Capture((FrameworkElement)window.Content, Path.Combine(output, "launch-window-launcher.png"));
                window.ApplyProgress(new(LaunchProgressPhase.Game, "等待模组进度", "正在等待 mod-loading-screen", null));
                Check(Control<TextBlock>(view, "Percentage").Text == "--", "Unknown game progress must not inherit launcher percentage");
                window.ApplyProgress(new(LaunchProgressPhase.Game, "初始化 client", "Example Mod  ·  example", .25, true, null, "StarRailExpress 开拓星穹谋杀案 1.21.1", 1, 4));
                await Task.Delay(300);
                Check(Control<TextBlock>(view, "Percentage").Text == "25%", "Game percentage missing");
                Check(Control<TextBlock>(view, "CountText").Text == "1/4", "MLS target count missing");
                Check(Control<TextBlock>(view, "VersionName").Text == "StarRailExpress 开拓星穹谋杀案 1.21.1", "MLS target title missing");
                Check(Control<TextBlock>(view, "LauncherState").Text == "已完成" && Control<TextBlock>(view, "GameState").Text == "进行中", "Two phase hierarchy wrong");
                Capture((FrameworkElement)window.Content, Path.Combine(output, "launch-screen.png"));
                var cancel = 0; window.CancelRequested += (_, _) => cancel++;
                var button = Action(view, "LaunchScreen.Cancel"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(cancel == 1, "Cancel must dispatch only once");
                window.WindowState = WindowState.Minimized; await Task.Delay(30);
                Check(!Clock(view).IsEnabled, "Minimizing must suspend animations/clock");
                window.WindowState = WindowState.Normal; await Task.Delay(30);
                Check(Clock(view).IsEnabled, "Restoring active window must resume clock");
                view.EndLaunch(); view.BeginLaunch(new string('长', 120));
                Check(Control<TextBlock>(view, "Percentage").Text == "--", "New launch must reset unknown progress");
                window.ApplyProgress(new(LaunchProgressPhase.Game, "阶段很长的模组初始化名称", "游戏正在加载资源", double.NaN));
                Check(Control<TextBlock>(view, "Percentage").Text == "--", "NaN must not become fake progress");
                window.ApplyProgress(new(LaunchProgressPhase.Game, "初始化 client", "中文模组  ·  example", .76, true));
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await Task.Delay(300); AssertLayout(view);
                Capture((FrameworkElement)window.Content, Path.Combine(output, "launch-screen-compact.png"));
                window.ApplyProgress(new(LaunchProgressPhase.Ready, "游戏就绪", "已收到模组完成事件，游戏窗口已打开", 1, true));
                Check(!Clock(view).IsEnabled && !Action(view, "LaunchScreen.Cancel").IsEnabled, "Terminal state must stop motion/cancel");
                Capture((FrameworkElement)window.Content, Path.Combine(output, "launch-window-ready.png"));
                view.EndLaunch(); view.BeginLaunch("测试关闭行为");
                Action(view, "LaunchScreen.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!window.IsVisible && cancel == 1 && !Clock(view).IsEnabled, "Closing must not cancel and must stop rendering");
                await CheckPresentations();
                Console.WriteLine($"PASS: {_checks} independent launch window/config checks; screenshots: {output}");
                app.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        window.BeginLaunch("PCL-MOON · 星月之旅 1.21.1 / Fabric");
        app.Run();
    }
}
