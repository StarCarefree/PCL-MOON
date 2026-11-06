using System.Collections;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class MultiplayerChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    internal static void Run(Assembly assembly, string[] args)
    {
        var serverType = assembly.GetType("PCL.VisitedServerListView", true)!;
        var server = (FrameworkElement)Activator.CreateInstance(serverType, nonPublic: true)!;
        var serversCreated = 0;
        var type = assembly.GetType("PCL.HybridMultiplayer", true)!;
        Func<FrameworkElement> factory = () => { serversCreated++; return server; };
        var page = (FrameworkElement)Activator.CreateInstance(type, Hidden, null, new object[] { factory }, null)!;
        var servers = (ContentControl)page.FindName("ServerHost");
        Check(servers.Content is null && serversCreated == 0, "lazy servers");
        type.GetMethod("EnsureServers", Hidden)!.Invoke(page, null);
        type.GetMethod("EnsureServers", Hidden)!.Invoke(page, null);
        Check(serversCreated == 1 && ReferenceEquals(servers.Content, server), "server list instance reused");

        // No game, user data or real network is needed for these UI checks.
        var list = (IList)serverType.GetField("_servers", Hidden)!.GetValue(server)!;
        var itemType = list.GetType().GenericTypeArguments[0];
        for (var i = 0; i < 23; i++)
            list.Add(Activator.CreateInstance(itemType, new object[]
            {
                i == 0 ? "长名称服务器 / 生存建筑与好友一起玩测试测试测试" : "多人服务器 " + (i + 1),
                i == 0 ? "a-very-long-server-address.example.com:25565" : "play.example.com:" + (25565 + i),
                "", "1.21.1 Fabric 生存实例（长实例名称测试）", "", "1.21.1"
            })!);
        void Render() => serverType.GetMethod("Render", Hidden)!.Invoke(server, new object[] { false });
        void ChangePage(int delta) => serverType.GetMethod("ChangePage", Hidden)!.Invoke(server, new object[] { delta });
        T Field<T>(string name) => (T)serverType.GetField(name, Hidden)!.GetValue(server)!;
        Render();
        var cards = Field<StackPanel>("_list");
        var previous = Field<Button>("_previous");
        var next = Field<Button>("_next");
        Check(cards.Children.Count == 10 && !previous.IsEnabled && next.IsEnabled, "first page has 10 servers and correct buttons");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(cards.Children.Count == 10 && ReferenceEquals(((Border)cards.Children[0]).Tag, list[10]), "next button selects second page");
        ChangePage(1);
        Check(cards.Children.Count == 3 && previous.IsEnabled && !next.IsEnabled, "last page is partial");
        ChangePage(99);
        Check(cards.Children.Count == 3 && Field<int>("_page") == 2, "page clamps at last page");
        var search = Field<TextBox>("_search");
        search.Text = "长名称服务器";
        Check(cards.Children.Count == 1 && Field<int>("_page") == 0 && !previous.IsEnabled && !next.IsEnabled, "search resets and repaginates");
        search.Text = "does-not-exist";
        Check(cards.Children.Count == 1 && ((Border)cards.Children[0]).Tag is null && !next.IsEnabled, "search empty state");
        search.Text = "";
        Check(cards.Children.Count == 10 && Field<int>("_page") == 0, "clear search restores first page");

        // Complete a controlled query to verify busy state and caching without a public server request.
        var statusType = serverType.GetNestedType("ServerStatus", BindingFlags.NonPublic)!;
        var completionType = typeof(TaskCompletionSource<>).MakeGenericType(statusType);
        var completion = Activator.CreateInstance(completionType)!;
        var queryTask = (Task)completionType.GetProperty("Task")!.GetValue(completion)!;
        SetPing(serverType, server, itemType, queryTask);
        var first = (Border)cards.Children[0];
        var ping = Descendants(first).OfType<Button>().Single(button => Equals(button.Tag, "ping"));
        ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!ping.IsEnabled && Equals(ping.Content, "查询中"), "Ping click sets busy state");
        var duplicate = (Task)serverType.GetMethod("QueryServerAsync", Hidden)!.Invoke(server, new[] { list[0] })!;
        Check(duplicate.IsCompleted, "duplicate Ping does not queue another request");
        var online = Activator.CreateInstance(statusType, true, "3/20", 39, "欢迎来到服务器")!;
        completionType.GetMethod("SetResult")!.Invoke(completion, new[] { online });
        PumpUntil(() => ping.IsEnabled);
        Check(Status(first).Text.Contains("39 ms"), "query displays latency and player count");
        Check(Status((Border)cards.Children[1]).Text == "尚未查询", "single Ping leaves other servers unchanged");
        ChangePage(1); previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Status((Border)cards.Children[0]).Text.Contains("39 ms"), "query cache survives pagination");
        search.Text = "长名称服务器"; search.Text = "";
        Check(Status((Border)cards.Children[0]).Text.Contains("39 ms"), "query cache survives search");

        foreach (var size in new[] { new Size(520, 420), new Size(900, 650) })
        {
            page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
            Check(servers.ActualWidth > 0 && servers.ActualHeight > 0, "content stretches");
            foreach (Border card in cards.Children)
            {
                var body = (Grid)card.Child;
                var details = body.Children.OfType<StackPanel>().Single(panel => Grid.GetColumn(panel) == 1);
                var actions = body.Children.OfType<StackPanel>().Single(panel => Grid.GetColumn(panel) == 2);
                var buttons = Descendants(actions).OfType<Button>().ToArray();
                Check(buttons.Length == 2, "each row has adjacent Ping and open buttons");
                Check(details.ActualWidth > 40, "text retains usable width");
                Check(details.TransformToAncestor(body).Transform(new Point(details.ActualWidth, 0)).X <= actions.TransformToAncestor(body).Transform(new Point()).X + 1, "text and action buttons do not overlap");
                Check(card.ActualHeight <= 96, "server rows stay compact even with MOTD");
                Check(body.ActualWidth <= servers.ActualWidth, "cards fit viewport");
            }
            var scroll = Field<ScrollViewer>("_scroll");
            Check(scroll.TransformToAncestor(page).Transform(new Point(0, scroll.ActualHeight)).Y <= next.TransformToAncestor(page).Transform(new Point()).Y, "pagination stays outside the scrolling list");
            var imageDir = args.FirstOrDefault(arg => arg.StartsWith("--screenshots="))?[14..];
            if (imageDir is not null)
            {
                Directory.CreateDirectory(imageDir);
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(page);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(imageDir, $"multiplayer-{size.Width}.png")); encoder.Save(output);
            }
            Console.WriteLine($"PASS multiplayer compact layout {size.Width}x{size.Height}");
        }

        var fault = (Task)typeof(Task).GetMethods().Single(method => method.Name == "FromException" && method.IsGenericMethod)
            .MakeGenericMethod(statusType).Invoke(null, new object[] { new IOException("test query failure") })!;
        SetPing(serverType, server, itemType, fault);
        var failed = (Task)serverType.GetMethod("QueryServerAsync", Hidden)!.Invoke(server, new[] { list[0] })!;
        PumpUntil(() => failed.IsCompleted); failed.GetAwaiter().GetResult();
        first = (Border)cards.Children[0];
        Check(Status(first).Text == "无法连接" && Descendants(first).OfType<Button>().Single(button => Equals(button.Tag, "ping")).IsEnabled, "failure clears stale online state and permits retry");
        Check(Descendants(first).OfType<TextBlock>().Single(block => Equals(block.Tag, "motd")).Visibility == Visibility.Collapsed, "failure hides stale MOTD");
        list.Clear(); Render();
        Check(cards.Children.Count == 1 && !previous.IsEnabled && !next.IsEnabled && Field<int>("_page") == 0, "empty data clamps pagination");
        Console.WriteLine("PASS multiplayer lazy host / pagination / search / single Ping busy-success-failure / cached status");
    }

    private static TextBlock Status(Border card) => Descendants(card).OfType<TextBlock>().Single(block => Equals(block.Tag, "status"));

    private static void SetPing(Type type, object view, Type itemType, Task task)
    {
        var field = type.GetField("_ping", Hidden)!;
        var parameter = Expression.Parameter(itemType, "server");
        field.SetValue(view, Expression.Lambda(field.FieldType, Expression.Constant(task, task.GetType()), parameter).Compile());
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        Check(done(), "async query completes within test deadline");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Multiplayer check failed: " + message);
    }
}
