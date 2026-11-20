using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// Uses a real WPF page, real EDMods confirmation/cascade/rename/update path, and no user files.
internal static class ModToggleUiChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run(Assembly assembly, string[] args)
    {
        var option = Array.IndexOf(args, "--mods-directory");
        if (option < 0) throw new ArgumentException("--mods-directory is required");
        var root = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(),
            "mod-toggle-ui-" + Guid.NewGuid().ToString("N"));
        var instancePath = Path.Combine(root, "versions", "ToggleTest");
        var modsPath = Path.Combine(instancePath, "mods");
        Directory.CreateDirectory(modsPath);
        var applicationType = assembly.GetType("PCL.Application", true)!;
        var app = (Application)Activator.CreateInstance(applicationType)!;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        applicationType.GetMethod("InitializeComponent")!.Invoke(app, null);
        try
        {
            assembly.GetType("PCL.ModBase", true)!.GetField("pathTemp")!.SetValue(null, root + Path.DirectorySeparatorChar);
            assembly.GetType("PCL.ModFolder", true)!.GetField("mcFolderSelected")!.SetValue(null, root + Path.DirectorySeparatorChar);
            var core = assembly.GetReferencedAssemblies().Single(n => n.Name == "PCL.Core");
            var coreAssembly = Assembly.Load(core);
            var providerType = coreAssembly.GetType("PCL.Core.App.Configuration.Storage.JsonFileProvider", true)!;
            var provider = Activator.CreateInstance(providerType, Path.Combine(root, "test-config.json"))!;
            var storageType = coreAssembly.GetType("PCL.Core.App.Configuration.Storage.FileConfigStorage", true)!;
            var storage = Activator.CreateInstance(storageType, provider)!;
            var service = coreAssembly.GetType("PCL.Core.App.Configuration.ConfigService", true)!;
            foreach (var field in new[] { "_sharedConfigProvider", "_sharedEncryptedConfigProvider", "_localConfigProvider", "_instanceConfigProvider" })
                service.GetField(field, Static)!.SetValue(null, storage);
            service.GetField("_isProvidersInitialized", Static)!.SetValue(null, true);
            // Fixed minimal instance: metadata and every operation are isolated from the user's config.
            var json = JsonNode.Parse("{\"id\":\"ToggleTest\",\"type\":\"release\",\"mainClass\":\"net.fabricmc.loader.impl.launch.knot.KnotClient\",\"clientVersion\":\"1.21.1\",\"libraries\":[],\"arguments\":{\"game\":[],\"jvm\":[]}}")!.AsObject();
            File.WriteAllText(Path.Combine(instancePath, "ToggleTest.json"), json.ToJsonString());
            var mcType = assembly.GetType("PCL.McInstance", true)!;
            var mc = Activator.CreateInstance(mcType, instancePath)!;
            mcType.GetProperty("JsonObject")!.SetValue(mc, json);
            assembly.GetType("PCL.PageInstanceLeft", true)!.GetField("McInstance", Static)!.SetValue(null, mc);
            var local = assembly.GetType("PCL.ModLocalComp", true)!;
            var fileType = assembly.GetType("PCL.ModLocalComp+LocalCompFile", true)!;
            var defer = local.GetField("DeferJijOnMiss", Static)!;
            var pending = fileType.GetProperty("JijPending", Instance)!;
            var output = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(fileType))!;
            foreach (var source in Directory.EnumerateFiles(args[option + 1], "*.jar"))
                File.Copy(source, Path.Combine(modsPath, Path.GetFileName(source)));
            // A known-good independent mod proves another broken mod cannot block the whole list.
            var healthyPath = Path.Combine(modsPath, "ui-independent.jar");
            using (var zip = ZipFile.Open(healthyPath, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
                writer.Write("{\"schemaVersion\":1,\"id\":\"ui_independent\",\"version\":\"1.0\",\"name\":\"UI independent\"}");
            defer.SetValue(null, true);
            try
            {
                foreach (var path in Directory.EnumerateFiles(modsPath, "*.jar"))
                {
                    var mod = Activator.CreateInstance(fileType, path)!;
                    fileType.GetMethod("Load")!.Invoke(mod, new object[] { false });
                    output.Add(mod);
                }
            }
            finally { defer.SetValue(null, false); }
            foreach (var mod in output)
                fileType.GetMethod("ResolveJijNow", Instance)!.Invoke(mod, null);
            var unresolved = output.Cast<object>().Where(m => (bool)pending.GetValue(m)!).ToArray();
            if (unresolved.Length == 0) throw new Exception("No incomplete mod found; this is not the reported reproduction.");
            foreach (var mod in unresolved) Console.WriteLine("INCOMPLETE " + Path.GetFileName(PathOf(mod)));
            var loader = local.GetField("compResourceListLoader", Static)!.GetValue(null)!;
            loader.GetType().GetField("output")!.SetValue(loader, output);
            local.GetField("CompJijResolved", Static)!.SetValue(null, false);
            var pageType = assembly.GetType("PCL.PageInstanceCompResource", true)!;
            // Constructor initializes real BAML controls, dictionaries and dispatcher; never fake an uninitialized page.
            var page = (FrameworkElement)Activator.CreateInstance(pageType)!;
            var panel = (Panel)pageType.GetField("PanList", Instance)!.GetValue(page)!;
            var items = (IDictionary)pageType.GetField("modItems")!.GetValue(page)!;
            var selected = (HashSet<string>)pageType.GetField("selectedMods")!.GetValue(page)!;
            var targets = output.Cast<object>().Where(m => fileType.GetProperty("State")!.GetValue(m)!.ToString() == "Fine").ToArray();
            foreach (var mod in targets)
            {
                var row = (UIElement)pageType.GetMethod("BuildLocalCompItem", Instance)!.Invoke(page, new[] { mod })!;
                items.Add(fileType.GetProperty("RawPath")!.GetValue(mod)!, row);
                panel.Children.Add(row);
            }
            void Operate(object[] mods, bool enable, int? answer)
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(fileType))!;
                foreach (var mod in mods) list.Add(mod);
                selected.Clear();
                foreach (var mod in mods) selected.Add((string)fileType.GetProperty("RawPath")!.GetValue(mod)!);
                void Invoke() => pageType.GetMethod("EDMods", Instance)!.Invoke(page, new object[] { list, enable });
                if (answer is int choice) AnswerOwnDialog(choice, Invoke); else Invoke();
                Drain();
            }
            object Find(string path) => output.Cast<object>().Single(m => PathOf(m) == path);
            void VerifyRows(object[] mods, string suffix)
            {
                foreach (var previous in mods)
                {
                    var original = (string)fileType.GetProperty("RawPath")!.GetValue(previous)!;
                    var path = original + suffix;
                    if (!File.Exists(path) || File.Exists(original + (suffix.Length == 0 ? ".disabled" : "")))
                        throw new Exception("File state did not change: " + Path.GetFileName(original));
                    var replacement = Find(path);
                    var row = panel.Children.Cast<object>().Single(r => ReferenceEquals(r.GetType().GetProperty("Entry")!.GetValue(r), replacement));
                    if (!ReferenceEquals(items[original], row)) throw new Exception("UI dictionary and list diverged");
                    if (!selected.Contains(original)) throw new Exception("Selection lost its stable raw path");
                    if ((bool)pending.GetValue(previous)! && !(bool)pending.GetValue(replacement)!)
                        throw new Exception("Rename discarded incomplete dependency metadata");
                }
            }
            var healthy = Find(healthyPath);
            Operate(new[] { healthy }, false, 7); // Native MsgBox No = the Cancel branch.
            if (!File.Exists(healthyPath) || File.Exists(healthyPath + ".disabled")) throw new Exception("Cancel mutated mod");
            Console.WriteLine("PASS actual UI: cancel preserves file/list");
            Operate(new[] { healthy }, false, 6); // Native MsgBox Yes = Continue.
            VerifyRows(new[] { healthy }, ".disabled");
            Console.WriteLine("PASS actual UI: continue disables a healthy mod despite another incomplete mod");
            Operate(new[] { Find(healthyPath + ".disabled") }, true, null);
            VerifyRows(new[] { healthy }, "");
            var originals = targets.Select(m => Find((string)fileType.GetProperty("RawPath")!.GetValue(m)!)).ToArray();
            Operate(originals, false, 6);
            VerifyRows(originals, ".disabled");
            var disabled = originals.Select(m => Find((string)fileType.GetProperty("RawPath")!.GetValue(m)! + ".disabled")).ToArray();
            Operate(disabled, true, null);
            VerifyRows(disabled, "");
            Console.WriteLine($"PASS actual UI: {originals.Length - 1} real mod copies + 1 fixture disabled/enabled, list/UI/selection/pending state verified");
        }
        finally
        {
            app.Shutdown();
            Directory.Delete(root, true);
        }
    }

    private static string PathOf(object mod) => (string)mod.GetType().GetField("path")!.GetValue(mod)!;
    private static void Drain()
    {
        var until = DateTime.UtcNow.AddMilliseconds(150);
        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    // Only this isolated test's UI thread dialogs are eligible. No keyboard/global window automation.
    private static void AnswerOwnDialog(int answer, Action action)
    {
        uint thread = GetCurrentThreadId();
        using var done = new CancellationTokenSource();
        var answered = 0;
        var watcher = Task.Run(() =>
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!done.IsCancellationRequested && DateTime.UtcNow < until)
            {
                EnumThreadWindows(thread, (window, _) =>
                {
                    var name = new StringBuilder(64);
                    GetClassName(window, name, name.Capacity);
                    if (name.ToString() == "#32770" && GetDlgItem(window, 6) != IntPtr.Zero && IsWindowVisible(window))
                    {
                        if (Interlocked.CompareExchange(ref answered, 1, 0) == 0)
                            PostMessage(window, 0x111, (IntPtr)answer, IntPtr.Zero); // WM_COMMAND IDYES/IDNO
                    }
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(10);
            }
        });
        try { action(); }
        finally { done.Cancel(); watcher.GetAwaiter().GetResult(); }
        if (answered != 1) throw new Exception("Expected the real parsing confirmation dialog");
    }
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
