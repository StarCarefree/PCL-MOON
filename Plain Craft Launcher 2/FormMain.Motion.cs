using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PCL.Core.App;

namespace PCL;

public partial class FormMain
{
    private const double StartupInitialScale = 0.968;
    private const double StartupInitialOffsetY = 14d;
    private const double ShutdownTargetScale = 0.952;
    private const double ShutdownTargetOffsetY = 14d;

    private int _windowMotionGeneration;
    private bool _isClosing;
    private bool _startupCompleted;
    private bool _aniControlReleased;
    private DispatcherTimer? _startupGuardTimer;
    private DispatcherTimer? _startupSidebarTimer;
    private DispatcherTimer? _shutdownTimer;
    private DispatcherTimer? _shutdownGuardTimer;

    /// <summary>
    /// 在窗口显示前初始化透明度与起始几何变换，确保启动淡入不被用户透明度设置覆盖。
    /// </summary>
    private void PrepareWindowStartup()
    {
        Opacity = Config.Preference.Theme.WindowOpacity / 1000d + 0.4d;
        GridRoot.Opacity = 0d;
        TransformScale.ScaleX = StartupInitialScale;
        TransformScale.ScaleY = StartupInitialScale;
        TransformPos.X = 0d;
        TransformPos.Y = StartupInitialOffsetY;
    }

    /// <summary>
    /// 在首帧布局就绪后接力退场加载闪屏并播放星遥主题启动动画：窗口轻盈浮现 + 星月流光启幕 + 侧栏错峰滑入。
    /// </summary>
    private void PlayWindowStartupAnimation()
    {
        var generation = ++_windowMotionGeneration;
        StopStartupTimers();
        ClearRootMotionAnimations();

        if (!SystemParameters.ClientAreaAnimation)
        {
            HybridSplashHost.CloseImmediately();
            ReleaseStartupAniControl();
            SettleRootTransform(opacity: 1d);
            WindowTransitionOverlay.Stop();
            HybridSidebarView.PlayEntrance();
            CompleteWindowStartup();
            return;
        }

        // 等首帧布局与绘制完成再起表，避免启动期首帧渲染吃掉动画前半段
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != _windowMotionGeneration || _isClosing) return;

            var speed = GetWindowMotionSpeed();
            var hadSplash = HybridSplashHost.Dismiss(speed);

            GridRoot.Opacity = 0d;
            TransformScale.ScaleX = StartupInitialScale;
            TransformScale.ScaleY = StartupInitialScale;
            TransformPos.X = 0d;
            TransformPos.Y = StartupInitialOffsetY;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeQuintOut = new QuinticEase { EasingMode = EasingMode.EaseOut };

            AnimateWindowProperty(GridRoot, OpacityProperty, 0d, 1d, ScaleMotionMs(280, speed), 0, easeOut, generation, isElement: true);
            AnimateWindowProperty(TransformScale, ScaleTransform.ScaleXProperty, StartupInitialScale, 1d, ScaleMotionMs(380, speed), 0, easeQuintOut, generation);
            AnimateWindowProperty(TransformScale, ScaleTransform.ScaleYProperty, StartupInitialScale, 1d, ScaleMotionMs(380, speed), 0, easeQuintOut, generation);
            AnimateWindowProperty(TransformPos, TranslateTransform.YProperty, StartupInitialOffsetY, 0d, ScaleMotionMs(360, speed), 0, easeOut, generation);

            WindowTransitionOverlay.PlayStartup(speed, hadSplash);

            // 保持与既有行为一致：约 50ms 后恢复控件动画开关
            ModAnimation.AniStart(new[]
            {
                ModAnimation.AaCode(ReleaseStartupAniControl, 50)
            }, "Form Show Control");

            // 窗口开始浮现后再触发侧边栏错峰滑入，避免在窗口可见前就播完
            _startupSidebarTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ScaleMotionMs(120, speed))
            };
            _startupSidebarTimer.Tick += (_, _) =>
            {
                _startupSidebarTimer?.Stop();
                _startupSidebarTimer = null;
                if (generation == _windowMotionGeneration && !_isClosing)
                    HybridSidebarView.PlayEntrance();
            };
            _startupSidebarTimer.Start();

            // 主窗体浮现到位后收尾并释放变换动画锁
            _startupGuardTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ScaleMotionMs(400, speed))
            };
            _startupGuardTimer.Tick += (_, _) =>
            {
                _startupGuardTimer?.Stop();
                _startupGuardTimer = null;
                if (generation != _windowMotionGeneration || _isClosing) return;
                ReleaseStartupAniControl();
                SettleRootTransform(opacity: 1d);
                CompleteWindowStartup();
            };
            _startupGuardTimer.Start();
        });
    }

    /// <summary>
    /// 播放星遥主题关闭动画（星月归寂 + 窗体轻沉收拢淡出），结束后可靠退出程序。
    /// </summary>
    private void PlayWindowShutdownAnimation(bool isUpdating)
    {
        var generation = ++_windowMotionGeneration;
        StopStartupTimers();
        ModAnimation.AniStop("Form Show");
        ModAnimation.AniStop("Form Show Control");
        ModAnimation.AniStop("Form Close");
        ReleaseStartupAniControl();

        // 若当前展示原生 WebView2 浏览页，先隐藏以避免其浮在缩放淡出的 WPF 窗口上方穿帮
        if (_webUi.IsPresented)
            _webUi.Hide();

        var shutdownFinished = false;
        void FinishShutdown()
        {
            if (shutdownFinished) return;
            shutdownFinished = true;
            _shutdownTimer?.Stop();
            _shutdownTimer = null;
            _shutdownGuardTimer?.Stop();
            _shutdownGuardTimer = null;
            WindowTransitionOverlay.Stop();
            IsHitTestVisible = false;
            Visibility = Visibility.Collapsed;
            ShowInTaskbar = false;
            EndProgramForce(force: false, isUpdating: isUpdating);
        }

        // 无动效环境或窗口当前不可见/最小化时直接快速退出
        if (!SystemParameters.ClientAreaAnimation ||
            WindowState == WindowState.Minimized ||
            Visibility != Visibility.Visible ||
            Hidden)
        {
            FinishShutdown();
            return;
        }

        // 从当前实时视觉状态平滑接续关闭（即便启动动画尚未播完）
        var currentOpacity = Math.Clamp(GridRoot.Opacity, 0d, 1d);
        var currentScale = double.IsFinite(TransformScale.ScaleX) && TransformScale.ScaleX > 0.5 ? TransformScale.ScaleX : 1d;
        var currentOffsetY = double.IsFinite(TransformPos.Y) ? TransformPos.Y : 0d;
        ClearRootMotionAnimations();
        GridRoot.Opacity = currentOpacity;
        TransformScale.ScaleX = currentScale;
        TransformScale.ScaleY = currentScale;
        TransformPos.X = 0d;
        TransformPos.Y = currentOffsetY;

        var speed = GetWindowMotionSpeed();
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var easeInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

        WindowTransitionOverlay.PlayShutdown(speed);

        AnimateWindowProperty(TransformScale, ScaleTransform.ScaleXProperty, currentScale, ShutdownTargetScale,
            ScaleMotionMs(260, speed), ScaleMotionMs(20, speed), easeIn, generation, holdEnd: true);
        AnimateWindowProperty(TransformScale, ScaleTransform.ScaleYProperty, currentScale, ShutdownTargetScale,
            ScaleMotionMs(260, speed), ScaleMotionMs(20, speed), easeIn, generation, holdEnd: true);
        AnimateWindowProperty(TransformPos, TranslateTransform.YProperty, currentOffsetY, ShutdownTargetOffsetY,
            ScaleMotionMs(260, speed), ScaleMotionMs(20, speed), easeIn, generation, holdEnd: true);
        AnimateWindowProperty(GridRoot, OpacityProperty, currentOpacity, 0d,
            ScaleMotionMs(220, speed), ScaleMotionMs(60, speed), easeInOut, generation, isElement: true, holdEnd: true);

        var exitDelayMs = ScaleMotionMs(295, speed);
        _shutdownTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(exitDelayMs)
        };
        _shutdownTimer.Tick += (_, _) => FinishShutdown();
        _shutdownTimer.Start();

        // 兜底保护，防止极端情况下动画未按时触发退出
        _shutdownGuardTimer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(exitDelayMs + 180)
        };
        _shutdownGuardTimer.Tick += (_, _) => FinishShutdown();
        _shutdownGuardTimer.Start();
    }

    private void CompleteWindowStartup()
    {
        if (_startupCompleted) return;
        _startupCompleted = true;
        isWindowLoadFinished = true;
        ModBase.Log(
            $"[System] DPI：{ModBase.dpi}，系统版本：{Environment.OSVersion.VersionString}，PCL 位置：{Basics.ExecutablePath}");
        if (WebUiController.RequestedByArguments(Basics.CommandLineArguments))
            _ = ShowWebUiAsync();
#if DEBUG
        if (Basics.CommandLineArguments.Contains("--hybrid-smoke")) _ = StartHybridSmokeAsync();
#endif
    }

    private void ReleaseStartupAniControl()
    {
        if (_aniControlReleased) return;
        _aniControlReleased = true;
        ModAnimation.AniControlEnabled -= 1;
    }

    private void StopStartupTimers()
    {
        _startupSidebarTimer?.Stop();
        _startupSidebarTimer = null;
        _startupGuardTimer?.Stop();
        _startupGuardTimer = null;
    }

    private void ClearRootMotionAnimations()
    {
        GridRoot.BeginAnimation(OpacityProperty, null);
        TransformScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TransformScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        TransformPos.BeginAnimation(TranslateTransform.XProperty, null);
        TransformPos.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void SettleRootTransform(double opacity)
    {
        ClearRootMotionAnimations();
        GridRoot.Opacity = opacity;
        TransformScale.ScaleX = 1d;
        TransformScale.ScaleY = 1d;
        TransformPos.X = 0d;
        TransformPos.Y = 0d;
    }

    private void AnimateWindowProperty(
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        int durationMs,
        int delayMs,
        IEasingFunction ease,
        int generation,
        bool isElement = false,
        bool holdEnd = false)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Math.Max(16, durationMs)))
        {
            BeginTime = delayMs > 0 ? TimeSpan.FromMilliseconds(delayMs) : TimeSpan.Zero,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        if (!holdEnd)
        {
            animation.Completed += (_, _) =>
            {
                if (generation != _windowMotionGeneration) return;
                target.SetValue(property, to);
                if (isElement) ((UIElement)target).BeginAnimation(property, null);
                else ((Animatable)target).BeginAnimation(property, null);
            };
        }
        if (isElement) ((UIElement)target).BeginAnimation(property, animation);
        else ((Animatable)target).BeginAnimation(property, animation);
    }

    private static double GetWindowMotionSpeed()
        => double.IsFinite(ModAnimation.aniSpeed) && ModAnimation.aniSpeed > 0
            ? Math.Clamp(ModAnimation.aniSpeed, 0.25, 4.0)
            : 1.0;

    private static int ScaleMotionMs(int baseMs, double speed)
        => Math.Max(16, (int)Math.Round(baseMs / speed));
}
