using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal static class StartupAgentLaunch
{
    private static readonly object ExtractLock = new();

    internal static void Prepare(ProcessStartInfo process, int javaMajor, bool is64Bit, bool hasForge)
    {
        if (!StartupAccelerationPolicy.ShouldInject(javaMajor, is64Bit, process.Arguments, hasForge)) return;
        try
        {
            using var resource = ModBase.GetResourceStream("Resources/pcl-moon-startup-agent.jar");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var digest = SHA256.HashData(bytes);
            var hash = Convert.ToHexStringLower(digest);
            var directory = Path.Combine(ModBase.pathPure, "startup-agent");
            var path = Path.Combine(directory, "pcl-moon-startup-agent-" + hash + ".jar");
            lock (ExtractLock)
            {
                Directory.CreateDirectory(directory);
                if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(digest))
                {
                    var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(pending, bytes);
                        File.Move(pending, path, overwrite: true);
                    }
                    finally { if (File.Exists(pending)) File.Delete(pending); }
                }
            }
            process.Arguments = "-javaagent:\"" + path + "\" " + process.Arguments;
            ModBase.Log("[Startup] 已按显式选择启用实验性启动索引/Forge 类读取优化；不缓存转换结果，保留 Mod 转换与校验，实际收益以实例测试为准");
        }
        catch (Exception error)
        {
            // Optional extraction failures must not turn a working instance into a failed launch.
            ModBase.Log("[Startup] 实验 Agent 不可用，使用原始启动流程：" + error.GetType().Name);
        }
    }
}
