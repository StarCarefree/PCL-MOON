using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

/// <summary>Presentation only. Each percentage belongs to the current phase, never to a blended total.</summary>
public partial class HybridLaunchScreen : UserControl
{
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _active;
    private bool _terminal;
    private bool _cancelPending;
    private bool _suspended;
    private bool _motionRunning;
    private bool _unknownRunning;
    private bool _observingSettings;
    private double? _progress;
    private LaunchProgressPhase _phase = LaunchProgressPhase.Launcher;
    private LaunchProgressPhase _lastWorkingPhase = LaunchProgressPhase.Launcher;

    public event EventHandler? HomeRequested;
    public event EventHandler? CancelRequested;

    public HybridLaunchScreen()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => PaintElapsed();
    }

    public void BeginLaunch(string instanceName)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => BeginLaunch(instanceName));
            return;
        }
        StopMotion();
        _active = true;
        _terminal = false;
        _cancelPending = false;
        _progress = null;
        _phase = _lastWorkingPhase = LaunchProgressPhase.Launcher;
        VersionName.Text = string.IsNullOrWhiteSpace(instanceName) ? "未命名游戏版本" : instanceName;
        VersionName.ToolTip = VersionName.Text;
        ProgressFill.Width = 0;
        _elapsed.Restart();
        PaintElapsed();
        Visibility = Visibility.Visible;
        ObserveSettings();
        ApplyProgress(new LaunchProgressSnapshot(LaunchProgressPhase.Launcher,
            "准备启动环境", "正在检查运行环境与启动所需文件", null));
    }

    public void ApplyProgress(LaunchProgressSnapshot snapshot)
    {
        if (snapshot is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyProgress(snapshot)));
            return;
        }
        if (!_active) return;
        var phaseChanged = _phase != snapshot.Phase;
        var oldProgress = _progress;
        _phase = snapshot.Phase;
        if (_phase is LaunchProgressPhase.Launcher or LaunchProgressPhase.Game)
            _lastWorkingPhase = _phase;
        _terminal = _phase is LaunchProgressPhase.Ready or LaunchProgressPhase.Failed;
        _progress = snapshot.Progress is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : null;
        if (_terminal) _elapsed.Stop();

        LaunchTitle.Text = _phase switch
        {
            LaunchProgressPhase.Game => "正在加载游戏",
            LaunchProgressPhase.Ready => "你的世界，已就绪",
            LaunchProgressPhase.Failed => "启动遇到问题",
            _ => "正在准备启动"
        };
        PhaseCaption.Text = _phase switch
        {
            LaunchProgressPhase.Game => "02 / 游戏加载",
            LaunchProgressPhase.Ready => "启动完成",
            LaunchProgressPhase.Failed => "启动未完成",
            _ => "01 / 启动器准备"
        };
        StageName.Text = string.IsNullOrWhiteSpace(snapshot.Stage) ? _phase switch
        {
            LaunchProgressPhase.Game => "等待游戏加载进度",
            LaunchProgressPhase.Ready => "游戏已就绪",
            LaunchProgressPhase.Failed => "启动失败",
            _ => "准备启动环境"
        } : snapshot.Stage;
        if (!string.IsNullOrWhiteSpace(snapshot.Target))
        {
            VersionName.Text = snapshot.Target;
            VersionName.ToolTip = snapshot.Target;
        }
        DetailText.Text = string.IsNullOrWhiteSpace(snapshot.Detail) ? _phase switch
        {
            LaunchProgressPhase.Game => "游戏进度由整合包入口点声明提供；未收到进度时不估算百分比",
            LaunchProgressPhase.Ready => "启动流程已完成，可以切换至游戏窗口",
            LaunchProgressPhase.Failed => "请返回启动器查看错误详情与日志",
            _ => "正在准备 Java、依赖与启动参数"
        } : snapshot.Detail;
        NoticeText.Text = !string.IsNullOrWhiteSpace(snapshot.Warning) ? snapshot.Warning : _phase switch
        {
            LaunchProgressPhase.Game when snapshot.TelemetryConnected => "入口点进度 · 仅显示游戏加载阶段",
            LaunchProgressPhase.Game => "等待加载模组清单 · 不使用计时估算进度",
            LaunchProgressPhase.Ready => "此窗口可关闭，不会退出游戏",
            LaunchProgressPhase.Failed => "此窗口可关闭，不会退出启动器",
            _ => "启动器准备与游戏加载分别计量"
        };
        StageName.ToolTip = StageName.Text;
        DetailText.ToolTip = DetailText.Text;
        NoticeText.ToolTip = NoticeText.Text;
        Percentage.Text = _progress.HasValue ? $"{_progress.Value * 100:0}%" : "--";
        CountText.Text = snapshot.Current is { } done && snapshot.Total is { } count && count > 0 ? $"{done}/{count}" : "";
        var failed = _phase == LaunchProgressPhase.Failed;
        var accent = (Brush)FindResource(failed ? "Launch.Error" : "Hybrid.Accent");
        Percentage.Foreground = VersionDot.Fill = PhaseDot.Fill = accent;
        ProgressFill.Background = (Brush)FindResource(failed ? "Launch.ProgressError" : "Launch.ProgressGold");
        UnknownFill.Background = (Brush)FindResource(failed ? "Launch.UnknownError" : "Launch.UnknownGold");
        AmbientGlow.Fill = (Brush)FindResource(failed ? "Launch.AmbientError" : "Launch.AmbientGold");
        TopBeam.Visibility = _terminal ? Visibility.Collapsed : Visibility.Visible;
        ProgressShimmer.Visibility = _terminal ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Foreground = (Brush)FindResource(failed ? "Launch.Error"
            : !string.IsNullOrWhiteSpace(snapshot.Warning) ? "Hybrid.Accent" : "Hybrid.Muted");
        CancelButton.IsEnabled = !_terminal && !_cancelPending;
        CancelButton.Content = _cancelPending && !_terminal ? "正在取消…" : "取消启动";
        PaintSteps();
        if (phaseChanged)
        {
            ProgressFill.BeginAnimation(WidthProperty, null);
            ProgressFill.Width = 0;
            AnimatePhase();
        }
        if (phaseChanged || oldProgress != _progress) PaintProgress(!phaseChanged);
        else PaintProgress(false, preserveAnimation: true);
        PaintElapsed();
        UpdateMotion();
    }

    /// <summary>Compatibility adapter for launcher-only callers; does not infer game loading.</summary>
    public void UpdateLaunch(string title, string stage, double progress, string? speed) =>
        ApplyProgress(new LaunchProgressSnapshot(LaunchProgressPhase.Launcher, stage,
            string.IsNullOrWhiteSpace(speed) ? title : title + " · " + speed,
            double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : null));

    public void EndLaunch()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(EndLaunch); return; }
        if (Window.GetWindow(this) is HybridLaunchWindow host) host.StopEntrance();
        _active = false;
        _elapsed.Stop();
        StopMotion();
        ForgetSettings();
        Visibility = Visibility.Collapsed;
    }

    // A minimized Window may leave its child IsVisible true. The host explicitly suspends rendering.
    internal void SetPresentationSuspended(bool suspended)
    {
        _suspended = suspended;
        UpdateMotion();
    }

    private void PaintSteps()
    {
        var gameStarted = _lastWorkingPhase == LaunchProgressPhase.Game || _phase == LaunchProgressPhase.Ready;
        var failed = _phase == LaunchProgressPhase.Failed;
        PaintStep(LauncherStep, LauncherBar, LauncherNumber, LauncherLabel, LauncherState,
            gameStarted ? "已完成" : failed ? "失败" : "进行中", !gameStarted, failed && !gameStarted);
        PaintStep(GameStep, GameBar, GameNumber, GameLabel, GameState,
            _phase == LaunchProgressPhase.Ready ? "已完成" : gameStarted ? failed ? "失败" : "进行中" : "待开始",
            gameStarted && _phase != LaunchProgressPhase.Ready, failed && gameStarted);
        StepBridge.Background = (Brush)FindResource(gameStarted ? "Launch.BridgeActive" : "Launch.BridgeIdle");
    }

    private void PaintStep(Border plate, Border bar, TextBlock number, TextBlock label, TextBlock state, string text, bool current, bool failed)
    {
        var done = text == "已完成";
        var accent = (Brush)FindResource(failed ? "Launch.Error" : "Hybrid.Accent");
        var goldSoft = (Brush)FindResource("Launch.GoldSoft");
        var muted = (Brush)FindResource("Hybrid.Muted");

        plate.Background = (Brush)FindResource(
            current ? (failed ? "Launch.StepErrorBg" : "Launch.StepActiveBg")
            : done ? "Launch.StepDoneBg" : "Launch.StepIdleBg");
        plate.BorderBrush = current
            ? new SolidColorBrush(failed ? Color.FromRgb(138, 69, 64) : Color.FromRgb(118, 91, 45))
            : done ? new SolidColorBrush(Color.FromRgb(47, 49, 56))
            : (Brush)FindResource("Launch.LineMuted");
        bar.Background = current ? accent : done ? new SolidColorBrush(Color.FromRgb(94, 78, 49)) : new SolidColorBrush(Color.FromRgb(42, 46, 54));
        number.Foreground = state.Foreground = current ? accent : done ? goldSoft : muted;
        label.Foreground = (Brush)FindResource(current || done ? "Hybrid.Text" : "Hybrid.Muted");
        state.Text = text;
    }

    private void PaintElapsed() => Elapsed.Text = $"已用时 {(int)_elapsed.Elapsed.TotalMinutes:00}:{_elapsed.Elapsed.Seconds:00}";

    private void PaintProgress(bool animate, bool preserveAnimation = false)
    {
        UnknownFill.Visibility = _progress.HasValue ? Visibility.Collapsed : Visibility.Visible;
        ProgressFill.Visibility = _progress.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (preserveAnimation) return;
        var target = Math.Max(0, ProgressTrack.ActualWidth * (_progress ?? 0));
        var from = ProgressFill.ActualWidth;
        ProgressFill.BeginAnimation(WidthProperty, null);
        ProgressFill.Width = target;
        if (animate && CanMove && !_terminal && Math.Abs(from - target) > 0.5)
            ProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            });
    }

    private bool CanPresent => _active && IsLoaded && IsVisible && !_suspended;
    private bool CanMove => CanPresent && SystemParameters.ClientAreaAnimation;

    private void UpdateMotion()
    {
        if (!CanPresent || _terminal)
        {
            StopMotion();
            return;
        }
        if (!_clock.IsEnabled) _clock.Start();
        if (!CanMove)
        {
            StopVisualMotion();
            return;
        }
        if (!_motionRunning)
        {
            _motionRunning = true;
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };

            // 外轨星月金星匀速顺转
            OrbitRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(-24, 336, TimeSpan.FromSeconds(8.5))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
            // 中轨星环缓慢逆转
            InnerOrbitRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(18, -342, TimeSpan.FromSeconds(19))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
            // 双层月晕柔和呼吸
            MoonGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(2.2))
            {
                EasingFunction = sine, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            });
            MoonHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0.95, TimeSpan.FromSeconds(3.0))
            {
                EasingFunction = sine, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            });
            var haloBreath = new DoubleAnimation(0.95, 1.05, TimeSpan.FromSeconds(3.0))
            {
                EasingFunction = sine, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            };
            HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, haloBreath);
            HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, haloBreath.Clone());
            // 弯月微光与四芒星错峰闪烁
            Moon.BeginAnimation(OpacityProperty, new DoubleAnimation(0.84, 1, TimeSpan.FromSeconds(2.8))
            {
                EasingFunction = sine, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            });
            StarSparkle.BeginAnimation(OpacityProperty, new DoubleAnimation(0.42, 1, TimeSpan.FromSeconds(1.8))
            {
                EasingFunction = sine, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            });
            var sparklePulse = new DoubleAnimation(0.86, 1.12, TimeSpan.FromSeconds(1.8))
            {
                EasingFunction = sine, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            };
            SparkleScale.BeginAnimation(ScaleTransform.ScaleXProperty, sparklePulse);
            SparkleScale.BeginAnimation(ScaleTransform.ScaleYProperty, sparklePulse.Clone());
            // 顶缘流金光轨与确定进度表面微光扫掠
            TopBeamShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(2.6))
            {
                EasingFunction = sine, RepeatBehavior = RepeatBehavior.Forever
            });
            ProgressShimmerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1.2, TimeSpan.FromSeconds(2.0))
            {
                EasingFunction = sine, RepeatBehavior = RepeatBehavior.Forever
            });
            // 状态呼吸点
            PhaseDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromSeconds(1.5))
            {
                EasingFunction = sine, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            });
        }
        UpdateUnknownMotion();
    }

    private void UpdateUnknownMotion(bool resized = false)
    {
        var shouldMove = CanMove && !_terminal && !_progress.HasValue;
        if (!shouldMove || resized)
        {
            UnknownTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            _unknownRunning = false;
        }
        if (!shouldMove || _unknownRunning) return;
        var trackWidth = ProgressTrack.ActualWidth;
        if (trackWidth <= 1) return;
        _unknownRunning = true;
        // 流星光束自左向右平滑划过轨道
        UnknownTranslation.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-UnknownFill.Width, trackWidth, TimeSpan.FromSeconds(1.65))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void AnimatePhase()
    {
        if (!CanMove) return;
        StatusTranslation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        });
        StatusPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.76, 1, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        });
    }

    private void StopVisualMotion()
    {
        _motionRunning = _unknownRunning = false;
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        InnerOrbitRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        MoonGlow.BeginAnimation(OpacityProperty, null);
        MoonHalo.BeginAnimation(OpacityProperty, null);
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Moon.BeginAnimation(OpacityProperty, null);
        StarSparkle.BeginAnimation(OpacityProperty, null);
        SparkleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SparkleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        TopBeamShift.BeginAnimation(TranslateTransform.XProperty, null);
        ProgressShimmerShift.BeginAnimation(TranslateTransform.XProperty, null);
        PhaseDot.BeginAnimation(OpacityProperty, null);
        UnknownTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        ProgressFill.BeginAnimation(WidthProperty, null);
        StatusTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        StatusPanel.BeginAnimation(OpacityProperty, null);
    }

    private void StopMotion() { _clock.Stop(); StopVisualMotion(); }

    private void ObserveSettings()
    {
        if (_observingSettings || !IsLoaded) return;
        SystemParameters.StaticPropertyChanged += AnimationSettingChanged;
        _observingSettings = true;
    }

    private void ForgetSettings()
    {
        if (!_observingSettings) return;
        SystemParameters.StaticPropertyChanged -= AnimationSettingChanged;
        _observingSettings = false;
    }

    private void AnimationSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.BeginInvoke(new Action(UpdateMotion));
    }

    private void ViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_active) ObserveSettings();
        PaintProgress(false);
        UpdateMotion();
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateMotion();
    private void ViewUnloaded(object sender, RoutedEventArgs e) { StopMotion(); ForgetSettings(); }
    private void ProgressSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            var radius = e.NewSize.Height / 2;
            var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
            clip.Freeze();
            ProgressTrack.Clip = clip;
        }
        PaintProgress(false);
        UpdateUnknownMotion(resized: true);
    }
    private void HomeClick(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (!_active || _terminal || _cancelPending) return;
        _cancelPending = true;
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消…";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
