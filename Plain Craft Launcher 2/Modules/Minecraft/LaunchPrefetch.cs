using System.IO;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.Utils.OS;

namespace PCL;

internal static class LaunchPrefetch
{
    /// <summary>
    /// 后台预读实例 Mod 等文件到系统缓存。任何失败只记日志并返回 null，绝不影响启动。
    /// </summary>
    internal static LaunchFilePrefetcher? Start(McInstance instance)
    {
        try
        {
            if (!Config.Launch.PrefetchFiles || instance is null) return null;
            var budget = LaunchFilePrefetcher.ComputeBudget(KernelInterop.GetAvailablePhysicalMemoryBytes());
            if (budget <= 0)
            {
                ModBase.Log("[Prefetch] 可用内存不足，跳过启动前文件预读");
                return null;
            }
            var prefetcher = new LaunchFilePrefetcher(budget, TimeSpan.FromSeconds(120), message => ModBase.Log(message));
            var mods = Path.Combine(instance.PathIndie, "mods");
            prefetcher.EnqueueDirectory(mods, "*.jar");
            return prefetcher;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Prefetch] 启动前文件预读不可用，使用原始启动流程");
            return null;
        }
    }
}
