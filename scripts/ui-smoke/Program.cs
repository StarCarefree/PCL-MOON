using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Input;

// Run against a built launcher DLL; never invoke Program.Main or lifecycle startup.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dotnet run --project scripts/ui-smoke/VersionListEvents.csproj -- <launcher.dll> [--page]");
            return 2;
        }
        try
        {
            var path = Path.GetFullPath(args[0]);
            var directory = Path.GetDirectoryName(path)!;
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var candidate = Path.Combine(directory, name.Name + ".dll");
                return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
            };
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            if (args.Contains("--plugin-files")) { Assembly.SetEntryAssembly(assembly); ServerPluginFileChecks.Run(assembly, directory); return 0; }
            if (args.Contains("--mod-toggle-ui"))
            {
                Assembly.SetEntryAssembly(assembly);
                InitializeDefaultConfig(directory);
                ModToggleUiChecks.Run(assembly, args);
                return 0;
            }
            if (args.Contains("--file-completion"))
            {
                Assembly.SetEntryAssembly(assembly);
                InitializeDefaultConfig(directory);
                FileCompletionChecks.Run(assembly, args);
                return 0;
            }
            // WPF absolute resource URIs must resolve against the launcher, not this host.
            if (args.Contains("--page")) Assembly.SetEntryAssembly(assembly);
            var pageType = assembly.GetType("PCL.PageSelectRight", throwOnError: true)!;
            var buttonType = assembly.GetType("PCL.MyButton", throwOnError: true)!;
            var eventType = buttonType.GetEvent("Click")!.EventHandlerType!;
            var bind = pageType.GetMethod("_CreateDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // Exercise exactly the name-based delegate creation used by the BAML reader,
            // without executing handlers on an uninitialized page.
            var bindingTarget = RuntimeHelpers.GetUninitializedObject(pageType);
            foreach (var handler in new[] { "ClientList_Click", "ServerList_Click" })
            {
                _ = bind.Invoke(bindingTarget, new object[] { eventType, handler });
                Console.WriteLine("PASS runtime Click binding: " + handler);
            }
            if (args.Contains("--page"))
            {
                InitializeDefaultConfig(directory);
                var applicationType = assembly.GetType("PCL.Application", throwOnError: true)!;
                var application = (Application)Activator.CreateInstance(applicationType)!;
                applicationType.GetMethod("InitializeComponent")!.Invoke(application, null);
                if (args.Contains("--launch-dock"))
                {
                    LaunchDockChecks.Run(assembly, args);
                    application.Shutdown();
                    return 0;
                }
                if (args.Contains("--accounts"))
                {
                    AccountChecks.Run(assembly);
                    application.Shutdown();
                    return 0;
                }
                if (args.Contains("--multiplayer"))
                {
                    MultiplayerChecks.Run(assembly, args);
                    application.Shutdown();
                    return 0;
                }
                var page = (FrameworkElement)Activator.CreateInstance(pageType)!;
                Console.WriteLine("PASS PageSelectRight constructor / BAML load");
                var client = (FrameworkElement)page.FindName("PanClientContent");
                var server = (FrameworkElement)page.FindName("PanServers");
                foreach (var showServer in new[] { true, false, true, false })
                {
                    var handler = showServer ? "ServerList_Click" : "ClientList_Click";
                    var sender = page.FindName(showServer ? "BtnServerList" : "BtnClientList");
                    var click = (Delegate)bind.Invoke(page, new object[] { eventType, handler })!;
                    click.DynamicInvoke(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
                    if (server.Visibility != (showServer ? Visibility.Visible : Visibility.Collapsed)
                        || client.Visibility != (showServer ? Visibility.Collapsed : Visibility.Visible))
                        throw new InvalidOperationException("Version-list switch did not update the expected containers.");
                }
                Console.WriteLine("PASS client/server list round trips");
                if (args.Contains("--server-theme")) ServerThemeChecks.Run(assembly, application, args);
                application.Shutdown();
            }
            return 0;
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException { InnerException: not null }) ex = ex.InnerException;
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
    private static void InitializeDefaultConfig(string directory)
    {
        // Populate config item caches from declared defaults only: no providers/files/lifecycle.
        var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "PCL.Core.dll"));
        var service = core.GetType("PCL.Core.App.Configuration.ConfigService", throwOnError: true)!;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        service.GetMethod("_InitializeConfigItems", statics)!.Invoke(null, null);
        service.GetField("_isConfigItemsInitialized", statics)!.SetValue(null, true);
        var items = (System.Collections.IDictionary)service.GetField("_Items", statics)!.GetValue(null)!;
        foreach (var item in items.Values)
        {
            var type = item!.GetType();
            type.GetProperty("EnableCache")!.SetValue(item, true);
            var value = type.GetProperty("DefaultValueNoType")!.GetValue(item);
            var cache = type.GetField("_valueCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!;
            cache.GetType().GetMethod("Write")!.Invoke(cache, new[] { value, null });
            type.GetField("_valueCache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, cache);
        }
    }

}
