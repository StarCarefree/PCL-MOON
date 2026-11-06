using System;
using System.Management;
using System.Text;
using System.Threading;
using PCL.Core.Logging;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;

namespace PCL.Core.Utils.Secret;

public class Identify
{
    private static readonly Lazy<byte[]> _rawId = new(_GetRawId, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> _launcherId = new(_GetLauncherId, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 设备原始识别数据。首次访问会触发 WMI 查询，代价较高，请优先使用 <see cref="LauncherId"/>。
    /// </summary>
    public static byte[] RawId => _rawId.Value;

    /// <summary>
    /// 启动器识别码。计算过程含 WMI 查询，首次访问可能明显耗时。
    /// </summary>
    public static string LauncherId => _launcherId.Value;

    /// <summary>
    /// 在后台线程预热识别码。WMI 首次查询往往比后续访问慢得多，
    /// 提前在后台触发可以让它与其它加载流程并行，而不是阻塞启动线程。
    /// </summary>
    public static void Prewarm()
    {
        ThreadPool.UnsafeQueueUserWorkItem(static _ => { _ = LauncherId; }, null);
    }

    private static byte[] _GetRawId()
    {
        var code = new StringBuilder();
        try
        {
            code.Append("UUID:").Append(_GetWmiProperty("Win32_ComputerSystemProduct", "UUID"))
                .Append("|MB_Prod:").Append(_GetWmiProperty("Win32_BaseBoard", "Product"))
                .Append("|MB_SN:").Append(_GetWmiProperty("Win32_BaseBoard", "SerialNumber"))
                .Append("|CPU:").Append(_GetWmiProperty("Win32_Processor", "ProcessorId"));
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Identify", "获取设备基础信息失败");
        }

        return Encoding.UTF8.GetBytes(SHA512Provider.Instance.ComputeHash(code.ToString()).ToHexString());
    }

    private static string _GetWmiProperty(string className, string propertyName)
    {
        try
        {
            using var searcher =
                new ManagementObjectSearcher($"SELECT {propertyName} FROM {className}");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                if (obj[propertyName] is not null)
                    return (obj[propertyName].ToString() ?? string.Empty).Trim();
            }
        }
        catch { /* Ignore */ }
        return string.Empty;
    }

    private static string _GetLauncherId()
    {
        try
        {
            var prefix = "PCL-MOON|"u8.ToArray();
            // 取副本后再清零，避免把 RawId 缓存本身抹掉
            var ctx = (byte[])RawId.Clone();
            var suffix = "|LauncherId"u8.ToArray();

            var buffer = new byte[prefix.Length + ctx.Length + suffix.Length];
            var bufferSpan = buffer.AsSpan();
            prefix.CopyTo(bufferSpan[..prefix.Length]);
            ctx.CopyTo(bufferSpan.Slice(prefix.Length, ctx.Length));
            suffix.CopyTo(bufferSpan.Slice(prefix.Length + ctx.Length, suffix.Length));

            Array.Clear(ctx);
            var sample = SHA512Provider.Instance.ComputeHash(bufferSpan).ToHexString();
            bufferSpan.Clear();

            // 16 in length, 8 bytes, 64 bits, enough for us
            return sample.Substring(64, 16)
                .ToUpper()
                .Insert(4, "-")
                .Insert(9, "-")
                .Insert(14, "-");
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Identify", "无法获取识别码");
            return "PCL2-MOON-GOOD-2025";
        }
    }
}
