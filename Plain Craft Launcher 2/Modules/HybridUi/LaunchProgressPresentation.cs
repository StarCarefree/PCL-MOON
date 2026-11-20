using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

/// <summary>Per-session independent window. Closing it does not own or abort the game process.</summary>
internal sealed class LaunchProgressPresentation : IDisposable
{
    internal LaunchProgressState State { get; } = new();
    private readonly string _name;
    private readonly HybridLaunchWindow _window;
    private readonly DispatcherTimer _timer;
    private readonly Action _cancel;
    private LaunchProgressSnapshot? _displayed;
    private bool _closed;
    private int _disposed;
    private DateTime? _gameStartedUtc;
    private string? _gameDirectory;
    private bool _inProcessProgressEnabled;
    private ModLoadingProgressDriver? _progressDriver;

    internal LaunchProgressPresentation(string name, Action cancel)
    {
        _cancel = cancel;
        _name = name;
        _window = new HybridLaunchWindow(); // Intentionally not owned by the launcher window.
        _timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += Tick;
        _window.CancelRequested += CancelRequested;
        _window.Closed += (_, _) => { _closed = true; _timer.Stop(); };
        _window.Dispatcher.BeginInvoke(() => _window.BeginLaunch(name));
        _timer.Start();
    }

    private void CancelRequested(object? sender, EventArgs e) => _cancel();
    private void Tick(object? sender, EventArgs e) => Paint();

    // 保留空方法以兼容 ModLaunch 会话轮询调用；实际进度由启动器进程内驱动直接推送。
    internal void Poll() { }

    private void Paint()
    {
        if (_closed) return;
        var snapshot = State.Snapshot;
        if (_displayed == snapshot) return;
        _displayed = snapshot;
        _window.ApplyProgress(snapshot);
    }

    internal void PrepareProcess(ProcessStartInfo info, bool supportsMls)
    {
        if (!supportsMls)
        {
            State.GameStarted("此实例没有 Fabric/Quilt 模组清单，将按游戏窗口状态交接");
            return;
        }
        _gameDirectory = info.WorkingDirectory;
        _inProcessProgressEnabled = true;
    }

    internal void GameStarted()
    {
        _gameStartedUtc = DateTime.UtcNow;
        State.GameStarted();
        if (!_inProcessProgressEnabled || Volatile.Read(ref _disposed) != 0) return;

        var driver = new ModLoadingProgressDriver(State, _gameDirectory ?? string.Empty, _name);
        if (Interlocked.CompareExchange(ref _progressDriver, driver, null) is null)
        {
            if (Volatile.Read(ref _disposed) != 0)
                Interlocked.Exchange(ref _progressDriver, null)?.Dispose();
            else
                _ = driver.Start();
        }
        else
        {
            driver.Dispose();
        }
    }

    internal bool WaitForTelemetryHandshake => _inProcessProgressEnabled && _gameStartedUtc is { } start && !State.TelemetryConnected && DateTime.UtcNow - start < TimeSpan.FromSeconds(4);
    internal void WindowAppeared() => State.WindowAppeared();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _progressDriver, null)?.Dispose();
        if (_window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            _timer.Stop(); _timer.Tick -= Tick;
            _window.CancelRequested -= CancelRequested;
            if (_closed) return;
            Paint();
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); if (!_closed) _window.Close(); };
            closeTimer.Start();
        });
    }
}
