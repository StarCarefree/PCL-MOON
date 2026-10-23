using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal static class FrameGenerationLaunch
{
    private static readonly object ExtractLock = new();

    internal static void Prepare(ProcessStartInfo process, int javaMajor, bool is64Bit)
    {
        if (!FrameGenerationPolicy.ShouldInject(Config.Launch.FrameGeneration, Config.Launch.FrameDiagnostics,
                javaMajor, is64Bit, process.Arguments)) return;
        try
        {
            using var resource = typeof(FrameGenerationLaunch).Assembly.GetManifestResourceStream("PCL.FrameGeneration.agent.jar")
                ?? throw new IOException("Missing optional frame-generation agent");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var digest = SHA256.HashData(bytes);
            var directory = Path.Combine(ModBase.pathPure, "framegen-agent");
            var path = Path.Combine(directory, "pcl-moon-framegen-agent-" + Convert.ToHexStringLower(digest) + ".jar");
            lock (ExtractLock)
            {
                Directory.CreateDirectory(directory);
                if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(digest))
                {
                    var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllBytes(pending, bytes); File.Move(pending, path, overwrite: true); }
                    finally { if (File.Exists(pending)) File.Delete(pending); }
                }
            }
            process.Arguments = "-javaagent:\"" + path + "\" "
                + "-Dpclmoon.framegen.enabled=" + (Config.Launch.FrameGeneration ? "true" : "false") + " "
                + "-Dpclmoon.framegen.diagnostics=" + (Config.Launch.FrameDiagnostics ? "true" : "false") + " "
                + process.Arguments;
            ModBase.Log($"[FrameGen] 已启用可选 Agent：插帧={Config.Launch.FrameGeneration}，长帧诊断={Config.Launch.FrameDiagnostics}；OpenGL 兼容性由游戏内探测，非真实渲染帧率提升");
        }
        catch (Exception error)
        {
            ModBase.Log("[FrameGen] 可选 Agent 不可用，保留原始启动流程：" + error.GetType().Name);
        }
    }
}
