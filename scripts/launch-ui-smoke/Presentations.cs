using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using PCL;
using PCL.Core.Minecraft.Launcher;

internal static partial class Program
{
    private static async Task CheckPresentations()
    {
        var type = typeof(HybridLaunchWindow).Assembly.GetType("PCL.LaunchProgressPresentation")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var cancelled = new int[2];
        object New(int index) => Activator.CreateInstance(type, flags, null,
            new object[] { "隔离启动 " + index, (System.Action)(() => cancelled[index]++) }, null)!;
        object Field(object instance, string name) => type.GetField(name, flags)!.GetValue(instance)!;
        void Call(object instance, string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(instance, args);

        var gameRoot = Path.Combine(Path.GetTempPath(), "pcl-moon-ui-smoke-game-" + Guid.NewGuid().ToString("N"));
        var modsDir = Path.Combine(gameRoot, "mods");
        Directory.CreateDirectory(modsDir);
        using (var fs = File.Create(Path.Combine(modsDir, "smoke-fabric.jar")))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
        {
            writer.Write("""
                {
                  "id": "smoke-mod",
                  "name": "冒烟测试模组",
                  "version": "1.0.0",
                  "entrypoints": {
                    "client": [
                      "smoke.Client1",
                      "smoke.Client2",
                      "smoke.Client3",
                      "smoke.Client4",
                      "smoke.Client5"
                    ]
                  }
                }
                """);
        }

        var first = New(0); var second = New(1);
        try
        {
            var one = (HybridLaunchWindow)Field(first, "_window"); var two = (HybridLaunchWindow)Field(second, "_window");
            for (var i = 0; i < 20 && (!one.IsVisible || !two.IsVisible); i++)
                await Task.Delay(25);
            Check(one.IsVisible && two.IsVisible && one.Owner is null && two.Owner is null, "Each launch needs an independent window");

            var info = new ProcessStartInfo("java") { Arguments = "-Xmx1G Main", WorkingDirectory = gameRoot, UseShellExecute = false };
            await Task.Run(() => Call(second, "PrepareProcess", info, true));
            Check(info.Arguments == "-Xmx1G Main", "In-process progress must not inject -javaagent into game JVM");
            Check(!info.Environment.ContainsKey("PCL_MOON_MLS_PROGRESS_FILE"), "In-process progress must not set legacy MLS temp env vars");

            var unsupported = new ProcessStartInfo("java") { Arguments = "Main", WorkingDirectory = gameRoot, UseShellExecute = false };
            Call(first, "PrepareProcess", unsupported, false);
            Check(unsupported.Arguments == "Main" && !unsupported.Environment.ContainsKey("PCL_MOON_MLS_PROGRESS_FILE"), "Unsupported loaders must not be instrumented");

            Call(second, "GameStarted");
            var state = (LaunchProgressState)type.GetProperty("State", flags)!.GetValue(second)!;
            var view = (HybridLaunchScreen)two.FindName("Screen");

            // 等待启动器进程内驱动推送出至少一个有效百分比并刷新到独立窗口上
            var sawPercentage = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(120);
                var pct = Control<System.Windows.Controls.TextBlock>(view, "Percentage").Text;
                if (pct is not ("--" or "" or "0%"))
                {
                    sawPercentage = true;
                    break;
                }
            }
            Check(sawPercentage, "In-process mod progress driver did not reach actual independent window");

            Action((HybridLaunchScreen)one.FindName("Screen"), "LaunchScreen.Cancel").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(cancelled[0] == 1 && cancelled[1] == 0, "Cancel crossed launch sessions");

            two.Close();
            Check(cancelled[1] == 0 && !state.IsTerminal, "Closing display must not cancel or complete a game");

            deadline = DateTime.UtcNow.AddSeconds(5);
            while (!state.ModLoadingComplete && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Check(state.ModLoadingComplete, "In-process mod progress driver did not reach completion after display was closed");

            await Task.Run(() => Call(second, "WindowAppeared"));
            Check(state.IsTerminal && state.Snapshot.Phase == LaunchProgressPhase.Ready, "Progress must continue after display was dismissed");
        }
        finally
        {
            ((IDisposable)first).Dispose(); ((IDisposable)second).Dispose();
            try { if (Directory.Exists(gameRoot)) Directory.Delete(gameRoot, true); } catch { }
        }
    }
}
