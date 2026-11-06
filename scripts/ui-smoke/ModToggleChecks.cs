using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

internal static class ModToggleChecks
{
    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instances = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run(Assembly assembly, string[] args)
    {
        var local = assembly.GetType("PCL.ModLocalComp", true)!;
        var fileType = assembly.GetType("PCL.ModLocalComp+LocalCompFile", true)!;
        var defer = local.GetField("DeferJijOnMiss", Statics)!;
        var resolve = fileType.GetMethod("ResolveJijNow", Instances)!;
        var pending = fileType.GetProperty("JijPending", Instances)!;
        var root = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(),
            "pcl-mod-toggle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var option = Array.IndexOf(args, "--mods-directory");
            if (option >= 0)
            {
                foreach (var source in Directory.EnumerateFiles(args[option + 1], "*.jar"))
                {
                    // Only copies are parsed or renamed. Never mutate the user's instance.
                    var copy = Path.Combine(root, Path.GetFileName(source));
                    File.Copy(source, copy);
                    var mod = Load(fileType, defer, copy);
                    var complete = (bool)resolve.Invoke(mod, null)!;
                    if (fileType.GetProperty("State")!.GetValue(mod)!.ToString() == "Fine")
                    {
                        Toggle(assembly, fileType, mod, copy, false);
                        var disabled = Activator.CreateInstance(fileType, copy + ".disabled")!;
                        fileType.GetMethod("CopyLoadedStateFrom", Instances)!.Invoke(disabled, new[] { mod });
                        Toggle(assembly, fileType, disabled, copy + ".disabled", true);
                        Console.WriteLine("PASS disable/enable copy: " + Path.GetFileName(source));
                    }
                    Console.WriteLine($"SCAN {Path.GetFileName(source)}: complete={complete}, pending={pending.GetValue(mod)}");
                }
            }

            // Valid outer JAR, unreadable optional nested metadata: parsing remains incomplete,
            // but the user's explicit disable operation must still be able to rename the file.
            var path = Path.Combine(root, "incomplete.jar");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), Encoding.UTF8))
                    writer.Write("{\"schemaVersion\":1,\"id\":\"toggle_test\",\"version\":\"1.0\",\"name\":\"Toggle test\",\"jars\":[{\"file\":\"nested.jar\"}]}");
                using var broken = new StreamWriter(archive.CreateEntry("nested.jar").Open());
                broken.Write("not a zip");
            }
            var entity = Load(fileType, defer, path);
            if ((bool)resolve.Invoke(entity, null)! || !(bool)pending.GetValue(entity)!)
                throw new Exception("Fixture did not exercise incomplete Jar-in-Jar parsing.");
            Toggle(assembly, fileType, entity, path, false);
            Console.WriteLine("PASS actual EDModsCore: incomplete nested parsing does not block disabling");
            var replacement = Activator.CreateInstance(fileType, path + ".disabled")!;
            fileType.GetMethod("CopyLoadedStateFrom", Instances)!.Invoke(replacement, new[] { entity });
            if (!(bool)pending.GetValue(replacement)!)
                throw new Exception("Renaming lost the incomplete dependency state.");
            Toggle(assembly, fileType, replacement, path + ".disabled", true);
            Console.WriteLine("PASS enable and preserve incomplete dependency state");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Toggle(Assembly assembly, Type fileType, object entity, string path, bool enable)
    {
        var pageType = assembly.GetType("PCL.PageInstanceCompResource", true)!;
        var page = RuntimeHelpers.GetUninitializedObject(pageType);
        var compType = assembly.GetType("PCL.ModComp+CompType", true)!;
        pageType.GetField("currentCompType", Instances)!.SetValue(page, Enum.Parse(compType, "Mod"));
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(fileType))!;
        list.Add(entity);
        Exception? failure = null;
        try { pageType.GetMethod("EDModsCore", Instances)!.Invoke(page, new object[] { list, enable }); }
        catch (TargetInvocationException ex) { failure = ex.InnerException; }
        // Deliberately headless: EDModsCore may reach uninitialized UI controls after the
        // disk operation. Assert the real rename, not that unrelated UI refresh.
        var destination = enable ? path[..^".disabled".Length] : path + ".disabled";
        if (!File.Exists(destination) || File.Exists(path))
            throw new Exception("Mod state switch was blocked instead of renaming: " + Path.GetFileName(path), failure);
        if (failure is not null && failure is not NullReferenceException) throw failure;
    }
    private static object Load(Type fileType, FieldInfo defer, string path)
    {
        defer.SetValue(null, true);
        try
        {
            var file = Activator.CreateInstance(fileType, path)!;
            fileType.GetMethod("Load", Instances)!.Invoke(file, new object[] { false });
            return file;
        }
        finally { defer.SetValue(null, false); }
    }
}
