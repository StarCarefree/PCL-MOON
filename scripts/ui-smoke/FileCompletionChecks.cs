using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

// Read-only benchmark of the actual launcher's completion analysis. No launch/download lifecycle.
internal static class FileCompletionChecks
{
    internal static void Run(Assembly assembly, string[] args)
    {
        var option = Array.IndexOf(args, "--instance");
        if (option < 0) throw new ArgumentException("--instance <directory> is required");
        var path = Path.GetFullPath(args[option + 1]).TrimEnd('\\', '/');
        var root = Directory.GetParent(path)!.Parent!.FullName + Path.DirectorySeparatorChar;
        var instanceType = assembly.GetType("PCL.McInstance", true)!;
        var instance = Activator.CreateInstance(instanceType, path)!;
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(path, Path.GetFileName(path) + ".json")))!.AsObject();
        instanceType.GetProperty("JsonObject")!.SetValue(instance, json);
        assembly.GetType("PCL.ModFolder", true)!.GetField("mcFolderSelected")!.SetValue(null, root);
        var cacheOption = Array.IndexOf(args, "--cache-directory");
        if (cacheOption >= 0)
            assembly.GetType("PCL.ModBase", true)!.GetField("pathTemp")!.SetValue(null,
                Path.GetFullPath(args[cacheOption + 1]) + Path.DirectorySeparatorChar);
        var assets = assembly.GetType("PCL.ModAssets", true)!;
        var library = assembly.GetType("PCL.ModLibrary", true)!;
        var getLibraries = library.GetMethod("McLibListGetWithJson")!;
        var tokens = getLibraries.Invoke(null, getLibraries.GetParameters()
            .Select(p => p.Name == "targetMcInstance" ? instance : p.HasDefaultValue ? p.DefaultValue : json).ToArray())!;
        var tokenList = (IList)tokens;
        Console.WriteLine($"INSTANCE {Path.GetFileName(path)}: libraries={tokenList.Count}");
        var fixAssets = assets.GetMethod("McAssetsFixList")!;
        var fixLibraries = library.GetMethod("McLibNetFilesFromTokens")!;
        Measure("ASSETS", () => (ICollection)fixAssets.Invoke(null, new object?[] { instance, false, null })!);
        var useCache = args.Contains("--use-cache");
        var libArguments = fixLibraries.GetParameters().Length == 3
            ? new object[] { tokens, root, useCache } : new object[] { tokens, root };
        Measure("LIBRARIES", () => (ICollection)fixLibraries.Invoke(null, libArguments)!);
        var client = json["downloads"]?["client"];
        if (client is not null)
        {
            var checkerType = assembly.GetType("PCL.ModBase+FileChecker", true)!;
            var checker = Activator.CreateInstance(checkerType, 1024L, (long?)client["size"] ?? -1L,
                (string?)client["sha1"], true, false)!;
            var check = checkerType.GetMethod(useCache ? "CheckForLaunch" : "Check",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            Measure("CLIENT", () => check.Invoke(checker, new object[] { Path.Combine(path, Path.GetFileName(path) + ".jar") })
                is null ? Array.Empty<object>() : new object[1]);
        }
        if (useCache) Flush(assembly);
        if (useCache) CheckCacheInvalidation(assembly);
    }

    private static void Measure(string name, Func<ICollection> action)
    {
        var first = Stopwatch.StartNew();
        var expected = action().Count;
        first.Stop();
        var times = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var watch = Stopwatch.StartNew();
            if (action().Count != expected) throw new Exception(name + ": inconsistent missing-file results");
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Console.WriteLine($"{name}: first={first.Elapsed.TotalMilliseconds:F2} ms median={times[3]:F2} ms missing={expected}");
    }

    private static void Flush(Assembly assembly) =>
        ((Task)assembly.GetType("PCL.ModBase+FileChecker", true)!.GetMethod("FlushLaunchHashes",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).GetAwaiter().GetResult();

    private static void CheckCacheInvalidation(Assembly assembly)
    {
        var type = assembly.GetType("PCL.ModBase+FileChecker", true)!;
        var path = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(),
            "completion-check-" + Guid.NewGuid().ToString("N"));
        var bytes = new byte[8192];
        File.WriteAllBytes(path, bytes);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes));
        var checker = Activator.CreateInstance(type, -1L, 8192L, hash, true, false)!;
        var launch = type.GetMethod("CheckForLaunch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var full = type.GetMethod("Check")!;
        bool Valid() => launch.Invoke(checker, new object[] { path }) is null;
        try
        {
            if (!Valid()) throw new Exception("Initial launch verification failed");
            Flush(assembly);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                if (!Valid()) throw new Exception("Unchanged launch file was rehashed instead of using cache");
            // Different expected hashes and min-size requirements must not be hidden by a hit.
            type.GetField("hash")!.SetValue(checker, new string('0', 40));
            if (Valid()) throw new Exception("Cache ignored a changed expected hash");
            type.GetField("hash")!.SetValue(checker, hash);
            type.GetField("minSize")!.SetValue(checker, 8193L);
            if (Valid()) throw new Exception("Cache bypassed minimum size verification");
            type.GetField("minSize")!.SetValue(checker, -1L);
            var timestamp = File.GetLastWriteTimeUtc(path);
            bytes[0] = 1;
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
            if (Valid()) throw new Exception("Changed same-size file used stale launch hash");
            File.WriteAllBytes(path, new byte[8192]);
            File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(2));
            if (!Valid()) throw new Exception("Repaired file did not recover");
            timestamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, timestamp);
            if (full.Invoke(checker, new object[] { path }) is null)
                throw new Exception("Manual full verification incorrectly trusted the launch cache");
            File.WriteAllBytes(path, new byte[4000]);
            if (Valid()) throw new Exception("Changed file length used stale launch hash");
            File.Delete(path);
            if (Valid()) throw new Exception("Deleted file used stale launch hash");
            Console.WriteLine("PASS launch hash cache: hit, expected hash, min-size, timestamp, size, deletion, forced full repair");
        }
        finally { File.Delete(path); }
    }
}
