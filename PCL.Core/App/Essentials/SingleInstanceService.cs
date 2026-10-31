using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using PCL.Core.App.IoC;
using PCL.Core.IO;
using PCL.Core.Utils;

namespace PCL.Core.App.Essentials;

[LifecycleService(LifecycleState.BeforeLoading, Priority = -2134567890)]
[LifecycleScope("single-instance", "单例", false)]
public sealed partial class SingleInstanceService
{
    private static FileStream? _lockStream;
    private static readonly string _LockFilePath = Path.Combine(Paths.SharedLocalData, "instance.lock");

    private static void _TryRpc(string processId, string content)
    {
        var pipeName = $"{RpcService.PipePrefix}@{processId}";
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(1000);
        using var sw = new StreamWriter(pipe, PipeComm.PipeEncoding);
        sw.WriteLine(content);
        sw.Write(PipeComm.PipeEndingChar);
        sw.Flush();
    }

    [LifecycleStart]
    private static void _Start()
    {
        if (_TryLock()) return;
        try
        {
            string[] info;
            using (var stream = File.Open(_LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                info = reader.ReadToEnd().Split('\n', 2);
            var pid = info[0].Trim();
            var dir = info.Length > 1 ? info[1].Trim() : null;
            // 从其他目录打开时关闭已有实例，改在当前目录启动
            if (!string.IsNullOrEmpty(dir) && !string.Equals(dir, Basics.ExecutableDirectory, StringComparison.OrdinalIgnoreCase)
                && _CloseOther(pid) && _TryLock())
                return;
            Context.Info($"发现重复实例 {pid}，尝试传递参数并拉起主窗口");
            try
            {
                _TryRpc(pid, "REQ cli\n" + JsonSerializer.Serialize(StartupService.UnhandledCommands, JsonCompat.SerializerOptions));
                _TryRpc(pid, "REQ activate");
            }
            catch (Exception ex) { Context.Warn("RPC 通信失败", ex); }
        }
        catch (Exception ex) { Context.Error("读取单例锁出错", ex); }
        Context.RequestExit(1);
    }

    private static bool _TryLock()
    {
        try
        {
            var stream = File.Open(_LockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            Context.Debug("未发现重复实例，正在向单例锁写入信息");
            using var sw = new StreamWriter(stream, Encoding.UTF8, 256, true);
            sw.Write($"{Basics.CurrentProcessId}\n{Basics.ExecutableDirectory}");
            sw.Flush();
            _lockStream = stream;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool _CloseOther(string pid)
    {
        try
        {
            using var process = Process.GetProcessById(int.Parse(pid));
            Context.Info($"发现其他目录的实例 {pid}，正在关闭");
            _TryRpc(pid, "REQ exit");
            if (!process.WaitForExit(15000)) return false;
            Thread.Sleep(200);
            return true;
        }
        catch (Exception ex)
        {
            Context.Warn("关闭其他目录的实例失败", ex);
            return false;
        }
    }

    [LifecycleStop]
    private static void _Stop()
    {
        if (_lockStream is null) return;
        Context.Debug("正在删除单例锁");
        _lockStream.Dispose();
        File.Delete(_LockFilePath);
    }
}
