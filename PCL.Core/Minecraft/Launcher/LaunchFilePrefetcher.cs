using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>
/// 启动前在后台以低 I/O 优先级预读游戏文件到系统文件缓存，减少冷启动时的磁盘等待。
/// 只读不缓存：数据读完即丢弃，驻留与否由 Windows 决定。无跨启动状态。
/// </summary>
public sealed partial class LaunchFilePrefetcher : IDisposable
{
    private const int BufferSize = 1024 * 1024;

    private readonly long _byteBudget;
    private readonly TimeSpan _maxDuration;
    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly ConcurrentQueue<(string Directory, string SearchPattern)> _pendingDirs = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _worker;
    private bool _disposed;
    private long _bytesRead;
    private int _filesRead;
    private int _filesSkipped;
    private volatile bool _inFlight;

    /// <summary>
    /// 预读字节预算：可用物理内存不足 2 GiB 时返回 0（不预读），否则为可用内存的 1/4、至多 4 GiB。
    /// </summary>
    public static long ComputeBudget(ulong availablePhysicalBytes)
    {
        const ulong gib = 1UL << 30;
        if (availablePhysicalBytes < 2 * gib) return 0;
        return (long)Math.Min(4 * gib, availablePhysicalBytes / 4);
    }

    public LaunchFilePrefetcher(long byteBudget, TimeSpan maxDuration, Action<string>? log)
    {
        _byteBudget = Math.Max(0, byteBudget);
        _maxDuration = maxDuration;
        _log = log;
    }

    /// <summary>预读线程退出（含取消/超时/预算用尽）后完成；供观察与测试，不要求调用方等待。</summary>
    public Task Completion => _completion.Task;
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public int FilesRead => _filesRead;
    public int FilesSkipped => _filesSkipped;

    /// <summary>
    /// 登记待预读文件。按完整路径 OrdinalIgnoreCase 去重，只收存在的普通 .jar 文件。
    /// 首次调用懒启动后台线程；队列清空后线程继续等待后续 Enqueue，直到 Dispose 或超时。
    /// </summary>
    public void Enqueue(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            if (_disposed) return;
            var added = false;
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path) || !_seen.Add(path)) continue;
                if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) continue;
                }
                catch (Exception)
                {
                    continue;
                }
                _pending.Enqueue(path);
                added = true;
            }
            if (!added) return;
            EnsureWorkerStartedLocked();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// 登记待后台枚举并预读的目录（不阻塞调用线程）；仅收匹配到的已存在 .jar 文件。
    /// </summary>
    public void EnqueueDirectory(string directoryPath, string searchPattern = "*.jar")
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return;
        var pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*.jar" : searchPattern;
        lock (_gate)
        {
            if (_disposed) return;
            _pendingDirs.Enqueue((directoryPath, pattern));
            EnsureWorkerStartedLocked();
            Monitor.PulseAll(_gate);
        }
    }

    private void EnsureWorkerStartedLocked()
    {
        if (_worker is not null) return;
        _worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PCL-MOON launch file prefetch" };
        _worker.Start();
    }

    private enum StopReason { Done, Cancelled, TimedOut, BudgetExhausted }

    private void Run()
    {
        var started = Environment.TickCount64;
        var deadline = started + (long)Math.Min(_maxDuration.TotalMilliseconds, int.MaxValue);
        var reason = StopReason.Done;
        try
        {
            while (true)
            {
                string? path = null;
                (string Directory, string SearchPattern)? pendingDir = null;
                lock (_gate)
                {
                    while (_pending.IsEmpty && _pendingDirs.IsEmpty && !_cancel.IsCancellationRequested)
                    {
                        var remaining = deadline - Environment.TickCount64;
                        if (remaining <= 0) { reason = StopReason.TimedOut; return; }
                        Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
                    }
                    if (_cancel.IsCancellationRequested) { reason = StopReason.Cancelled; return; }
                    if (!_pending.TryDequeue(out path) && _pendingDirs.TryDequeue(out var dirItem))
                        pendingDir = dirItem;
                }
                if (_cancel.IsCancellationRequested) { reason = StopReason.Cancelled; return; }
                if (Environment.TickCount64 >= deadline) { reason = StopReason.TimedOut; return; }
                if (Interlocked.Read(ref _bytesRead) >= _byteBudget) { reason = StopReason.BudgetExhausted; return; }

                if (pendingDir is { } dirReq)
                {
                    ExpandPendingDirectory(dirReq.Directory, dirReq.SearchPattern);
                    continue;
                }
                if (path is null) continue;
                _inFlight = true;
                try
                {
                    PrefetchOne(path, deadline);
                }
                finally
                {
                    _inFlight = false;
                }
            }
        }
        finally
        {
            // 队列已空且无读到一半的文件时，Dispose 只是收尾，不算中断工作
            if (reason == StopReason.Cancelled && !_inFlight && _pending.IsEmpty && _pendingDirs.IsEmpty) reason = StopReason.Done;
            var reasonText = reason switch
            {
                StopReason.Cancelled => "取消",
                StopReason.TimedOut => "超时",
                StopReason.BudgetExhausted => "预算用尽",
                _ => "完成",
            };
            _log?.Invoke($"[Prefetch] 预读 {_filesRead} 个文件，{BytesRead / 1048576.0:F1} MB，跳过 {_filesSkipped} 个，用时 {Environment.TickCount64 - started} ms（{reasonText}）");
            _completion.TrySetResult();
        }
    }

    private void ExpandPendingDirectory(string directoryPath, string searchPattern)
    {
        try
        {
            if (!Directory.Exists(directoryPath)) return;
            var discovered = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directoryPath, searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (_cancel.IsCancellationRequested) return;
                if (!file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                discovered.Add(file);
            }
            if (discovered.Count == 0) return;

            lock (_gate)
            {
                if (_disposed || _cancel.IsCancellationRequested) return;
                foreach (var file in discovered)
                {
                    if (_seen.Add(file))
                        _pending.Enqueue(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 目录枚举异常不影响后续队列或游戏启动
        }
    }

    private void PrefetchOne(string path, long deadline)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            TrySetLowIoPriority(stream.SafeFileHandle);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    if (_cancel.IsCancellationRequested || Environment.TickCount64 >= deadline
                            || Interlocked.Read(ref _bytesRead) >= _byteBudget) return;
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    Interlocked.Add(ref _bytesRead, read);
                }
                Interlocked.Increment(ref _filesRead);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _filesSkipped);
        }
    }

    // 只用低 I/O 优先级提示，不用 THREAD_MODE_BACKGROUND_BEGIN：后者会降低内存优先级，
    // 预读进缓存的页反而会被最先回收，达不到预热目的。失败（非 NTFS/旧系统）直接忽略。
    private const int FileIoPriorityHintInfo = 12;
    private const int IoPriorityHintLow = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_IO_PRIORITY_HINT_INFO
    {
        public int PriorityHint;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        ref FILE_IO_PRIORITY_HINT_INFO info, uint bufferSize);

    private static void TrySetLowIoPriority(SafeFileHandle handle)
    {
        try
        {
            var info = new FILE_IO_PRIORITY_HINT_INFO { PriorityHint = IoPriorityHintLow };
            SetFileInformationByHandle(handle, FileIoPriorityHintInfo, ref info, (uint)Marshal.SizeOf<FILE_IO_PRIORITY_HINT_INFO>());
        }
        catch (Exception) { /* best-effort */ }
    }

    /// <summary>取消预读，不阻塞等待后台线程。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancel.Cancel();
            Monitor.PulseAll(_gate);
            if (_worker is null) _completion.TrySetResult();
        }
        _cancel.Dispose();
    }
}
