using System;
using System.Text.Json;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Per-launch presentation state. A window appearing is not a mod-loading completion event.</summary>
public sealed class LaunchProgressState
{
    private readonly object _gate = new();
    private bool _gameStarted, _terminal, _connected, _modComplete, _windowAppeared;
    private LaunchProgressSnapshot _snapshot = new(LaunchProgressPhase.Launcher, "检查启动环境", "正在准备 Java、账号和游戏文件", 0);
    public LaunchProgressSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool TelemetryConnected { get { lock (_gate) return _connected; } }
    public bool IsTerminal { get { lock (_gate) return _terminal; } }
    public bool ModLoadingComplete { get { lock (_gate) return _modComplete; } }

    public void Launcher(string stage, double progress, string detail)
    {
        lock (_gate)
        {
            if (_gameStarted || _terminal) return;
            _snapshot = new(LaunchProgressPhase.Launcher, stage, detail,
                double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : null, Target: _snapshot.Target);
        }
    }

    public void GameStarted(string? warning = null)
    {
        lock (_gate)
        {
            if (_gameStarted || _terminal) return;
            _gameStarted = true;
            _snapshot = new(LaunchProgressPhase.Game, "等待游戏加载信号", "Java 进程已启动，正在读取整合包模组清单", null, false, warning, _snapshot.Target);
        }
    }

    /// <summary>由启动器进程内直接推送模组加载阶段与计数进度。</summary>
    public bool ReportProgress(string stage, string detail, string? target = null, long current = 0, long total = 0)
        => ApplyTelemetry("loading", stage, detail, target, current, total);

    /// <summary>由启动器进程内通知模组入口点声明读取完成，等待游戏窗口就绪。</summary>
    public bool ReportModComplete(string? target = null)
        => ApplyTelemetry("ready", "模组初始化完成", "正在等待游戏窗口就绪", target, 1, 1);

    /// <summary>由启动器进程内报告模组清单不可用，回退按游戏窗口状态交接。</summary>
    public bool ReportUnavailable(string stage, string detail, string? target = null)
        => ApplyTelemetry("unavailable", stage, detail, target, 0, 0);

    /// <summary>Consumes only the bounded, session-authenticated bridge snapshot, not arbitrary log lines.</summary>
    public bool AcceptTelemetry(string json, string token)
    {
        if (json.Length > 16384 || string.IsNullOrEmpty(token)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("token", out var auth) || auth.GetString() != token ||
                !root.TryGetProperty("protocol", out var protocol) || protocol.GetInt32() != 1 ||
                !root.TryGetProperty("status", out var statusElement)) return false;
            var status = statusElement.GetString();
            if (status is not ("loading" or "ready" or "unavailable")) return false;
            var stage = Text(root, "stage", "游戏正在加载");
            var detail = Text(root, "detail", "整合包模组加载进度");
            var incoming = Text(root, "target", "");
            string? target = incoming.Length == 0 ? null : incoming;
            long? currentCount = null, totalCount = null;
            if (root.TryGetProperty("current", out var current) && current.TryGetInt64(out var done) &&
                root.TryGetProperty("total", out var total) && total.TryGetInt64(out var count))
            {
                currentCount = done;
                totalCount = count;
            }
            return ApplyTelemetry(status, stage, detail, target, currentCount, totalCount);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { return false; }
    }

    private bool ApplyTelemetry(string status, string stage, string detail, string? target, long? currentCount, long? totalCount)
    {
        if (status is not ("loading" or "ready" or "unavailable")) return false;
        var stageText = Truncate(stage, "游戏正在加载");
        var detailText = Truncate(detail, "整合包模组加载进度");
        var resolvedTarget = string.IsNullOrEmpty(target) ? null : Truncate(target, "");
        long? boundedCurrent = null, boundedTotal = null;
        double? progress = null;
        if (currentCount is { } done && totalCount is { } count && done >= 0 && count > 0)
        {
            boundedCurrent = done;
            boundedTotal = count;
            progress = Math.Clamp(done / (double)count, 0, 1);
        }

        lock (_gate)
        {
            if (_terminal) return false;
            _gameStarted = true;
            resolvedTarget ??= _snapshot.Target;
            if (status == "unavailable")
            {
                if (_connected) SignalLost();
                else _snapshot = new(LaunchProgressPhase.Game, "等待游戏窗口", detailText, null, false, stageText, resolvedTarget);
                return true;
            }
            _connected = true;
            if (status == "ready")
            {
                _modComplete = true;
                _terminal = _windowAppeared;
                _snapshot = _windowAppeared
                    ? Ready()
                    : new(LaunchProgressPhase.Game, "模组初始化完成", "正在等待游戏窗口就绪", null, true, null, resolvedTarget);
            }
            else if (!_modComplete)
                _snapshot = new(LaunchProgressPhase.Game, stageText, detailText, progress, true, null, resolvedTarget, boundedCurrent, boundedTotal);
            return true;
        }
    }

    public void WindowAppeared()
    {
        lock (_gate)
        {
            _windowAppeared = true;
            if (_terminal || (_connected && !_modComplete)) return;
            _terminal = true;
            _snapshot = _connected ? Ready()
                : new(LaunchProgressPhase.Ready, "游戏窗口已打开", "未收到模组进度信号；已按游戏窗口状态完成交接", null, false,
                    "此实例未提供游戏内部加载百分比", _snapshot.Target);
        }
    }

    public void SignalLost()
    {
        lock (_gate)
        {
            if (_terminal) return;
            _terminal = true;
            _snapshot = new(LaunchProgressPhase.Failed, "游戏加载信号已中断", "游戏可以继续运行；请查看游戏窗口或启动日志", null, _connected, null, _snapshot.Target);
        }
    }

    public void Fail(string detail)
    {
        lock (_gate)
        {
            _terminal = true;
            _snapshot = new(LaunchProgressPhase.Failed, "启动未完成", detail, null, _connected, null, _snapshot.Target);
        }
    }

    private LaunchProgressSnapshot Ready()
        => new(LaunchProgressPhase.Ready, "你的世界，准备就绪", "模组初始化完成，游戏窗口已就绪", 1, true, null, _snapshot.Target);

    private static string Text(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return fallback;
        return Truncate(value.GetString(), fallback);
    }

    private static string Truncate(string? text, string fallback)
    {
        if (text is null) return fallback;
        return text[..Math.Min(text.Length, 2048)];
    }
}
