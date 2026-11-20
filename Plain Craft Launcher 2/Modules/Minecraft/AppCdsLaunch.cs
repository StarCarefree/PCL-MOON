using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal static class AppCdsLaunch
{
    internal static void Prepare(ProcessStartInfo process, int javaMajor, bool is64Bit,
        string? javaExePath, string? instancePath)
    {
        if (!Config.Launch.AppCds) return;
        try
        {
            var javaExe = string.IsNullOrWhiteSpace(javaExePath) ? process.FileName : javaExePath;
            var hasBaseArchive = HasBaseArchive(javaExe);
            var archiveDir = Path.Combine(ModBase.pathPure, "appcds");
            var targetInstance = string.IsNullOrWhiteSpace(instancePath) ? process.WorkingDirectory : instancePath;
            var archivePath = Path.Combine(archiveDir, AppCdsPolicy.ArchiveFileName(javaExe, targetInstance));

            var decision = AppCdsPolicy.Evaluate(
                Config.Launch.AppCds, javaMajor, is64Bit, hasBaseArchive, archivePath, process.Arguments);
            if (!decision.Enabled)
            {
                ModBase.Log($"[AppCDS] 已跳过自动归档（保持兼容）：{decision.Reason}");
                return;
            }

            Directory.CreateDirectory(archiveDir);
            process.Arguments = AppCdsPolicy.BuildArguments(archivePath) + process.Arguments;
            ModBase.Log($"[AppCDS] 已启用应用类数据共享归档：{archivePath}");
        }
        catch (Exception error)
        {
            // 可选启动加速失败绝不能阻断正常启动。
            ModBase.Log("[AppCDS] 准备归档失败，已回退原启动流程：" + error.GetType().Name);
        }
    }

    private static bool HasBaseArchive(string? javaExePath)
    {
        if (string.IsNullOrWhiteSpace(javaExePath)) return false;
        var binDir = Path.GetDirectoryName(javaExePath);
        if (string.IsNullOrWhiteSpace(binDir)) return false;
        var javaHome = Path.GetDirectoryName(binDir);
        if (string.IsNullOrWhiteSpace(javaHome)) return false;
        return AppCdsPolicy.BaseArchiveCandidates
            .Select(rel => Path.Combine(javaHome, rel))
            .Any(File.Exists);
    }
}
