using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PCL;

/// <summary>
/// 星遥主题窗口启闭过渡光幕（星月启幕 / 星月归寂），平时隐藏且不拦截鼠标事件。
/// </summary>
public partial class HybridWindowTransition : UserControl
{
    private int _generation;
    private DispatcherTimer? _finishTimer;

    public HybridWindowTransition()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 播放启动器启动时的星月启幕或闪屏接力流光。
    /// </summary>
    /// <param name="speedScale">动画速度倍率。</param>
    /// <param name="hadSplash">此前是否已由独立星月闪屏承接加载阶段；若为 true 则以轻盈流金扫光接力，避免重复叠显第二个星月徽记。</param>
    /// <param name="onCompleted">动画结束回调。</param>
    public void PlayStartup(double speedScale, bool hadSplash = false, Action? onCompleted = null)
    {
        var generation = ResetForPlay();
        var scale = ClampSpeed(speedScale);
        Visibility = Visibility.Visible;

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeQuintOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var easeInOut = new SineEase { EasingMode = EasingMode.EaseInOut };

        if (hadSplash)
        {
            // 闪屏接力模式：星月加载卡片刚在中央散开，主窗体顺势泛起一缕暖金微光并掠过全宽顶缘流金光束
            Emblem.Opacity = 0;

            Veil.Opacity = 0.28;
            Veil.BeginAnimation(OpacityProperty, new DoubleAnimation(0.28, 0, Duration(360, scale))
            {
                EasingFunction = easeOut,
                FillBehavior = FillBehavior.HoldEnd
            });

            TopBeamScale.ScaleX = 1;
            TopBeamShift.X = -1;
            TopBeam.Opacity = 0;
            TopBeamShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, Duration(540, scale))
            {
                EasingFunction = easeInOut
            });
            var beamOpacity = new DoubleAnimationUsingKeyFrames();
            beamOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            beamOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(Delay(120, scale)), easeOut));
            beamOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Delay(540, scale)), easeInOut));
            TopBeam.BeginAnimation(OpacityProperty, beamOpacity);

            ScheduleFinish(generation, Delay(560, scale), () =>
            {
                Stop();
                onCompleted?.Invoke();
            });
            return;
        }

        // 无闪屏直启模式：在主窗体内呈现完整的星月启幕与顶缘流金扫光
        Veil.Opacity = 0.88;
        Veil.BeginAnimation(OpacityProperty, new DoubleAnimation(0.88, 0, Duration(320, scale))
        {
            BeginTime = Delay(170, scale),
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });

        TopBeamScale.ScaleX = 1;
        TopBeamShift.X = -1;
        TopBeam.Opacity = 0;
        TopBeamShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, Duration(560, scale))
        {
            EasingFunction = easeInOut
        });
        var fullBeamOpacity = new DoubleAnimationUsingKeyFrames();
        fullBeamOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fullBeamOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(Delay(130, scale)), easeOut));
        fullBeamOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Delay(560, scale)), easeInOut));
        TopBeam.BeginAnimation(OpacityProperty, fullBeamOpacity);

        Emblem.Opacity = 0;
        EmblemScale.ScaleX = 0.90;
        EmblemScale.ScaleY = 0.90;
        EmblemOffset.Y = 6;
        OrbitRotation.Angle = -28;

        var emblemOpacity = new DoubleAnimationUsingKeyFrames();
        emblemOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        emblemOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.98, KeyTime.FromTimeSpan(Delay(140, scale)), easeOut));
        emblemOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.98, KeyTime.FromTimeSpan(Delay(240, scale))));
        emblemOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Delay(470, scale)), easeInOut));
        Emblem.BeginAnimation(OpacityProperty, emblemOpacity);

        var scaleAnim = new DoubleAnimation(0.90, 1.07, Duration(470, scale))
        {
            EasingFunction = easeQuintOut
        };
        EmblemScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        EmblemScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim.Clone());
        EmblemOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, -5, Duration(470, scale))
        {
            EasingFunction = easeOut
        });
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-28, 28, Duration(500, scale))
        {
            EasingFunction = easeQuintOut
        });

        ScheduleFinish(generation, Delay(580, scale), () =>
        {
            Stop();
            onCompleted?.Invoke();
        });
    }

    /// <summary>
    /// 播放启动器关闭时的星月归寂与顶缘流金收敛。
    /// </summary>
    public void PlayShutdown(double speedScale)
    {
        ResetForPlay();
        var scale = ClampSpeed(speedScale);
        Visibility = Visibility.Visible;

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        // 夜幕：柔和合拢覆上深炭微光
        Veil.Opacity = 0;
        Veil.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.72, Duration(190, scale))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });

        // 顶缘流金光轨：居中点亮并向中心收束消散
        TopBeamShift.X = 0;
        TopBeamScale.ScaleX = 1;
        TopBeam.Opacity = 0.85;
        TopBeamScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.14, Duration(240, scale))
        {
            EasingFunction = easeIn,
            FillBehavior = FillBehavior.HoldEnd
        });
        TopBeam.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, 0, Duration(240, scale))
        {
            EasingFunction = easeIn,
            FillBehavior = FillBehavior.HoldEnd
        });

        // 星月徽记：浮现并缓缓回旋微缩归寂
        Emblem.Opacity = 0;
        EmblemScale.ScaleX = 1.05;
        EmblemScale.ScaleY = 1.05;
        EmblemOffset.Y = -4;
        OrbitRotation.Angle = 18;

        var emblemOpacity = new DoubleAnimationUsingKeyFrames();
        emblemOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        emblemOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(Delay(110, scale)), easeOut));
        emblemOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Delay(275, scale)), easeIn));
        Emblem.BeginAnimation(OpacityProperty, emblemOpacity);

        var scaleAnim = new DoubleAnimation(1.05, 0.88, Duration(275, scale))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        EmblemScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        EmblemScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim.Clone());
        EmblemOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 6, Duration(275, scale))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(18, -16, Duration(275, scale))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    /// <summary>
    /// 立即停止所有过渡动画并隐藏光幕。
    /// </summary>
    public void Stop()
    {
        _generation++;
        _finishTimer?.Stop();
        _finishTimer = null;
        ClearAnimations();
        Veil.Opacity = 0;
        TopBeam.Opacity = 0;
        TopBeamScale.ScaleX = 1;
        TopBeamShift.X = -1;
        Emblem.Opacity = 0;
        EmblemScale.ScaleX = 1;
        EmblemScale.ScaleY = 1;
        EmblemOffset.Y = 0;
        OrbitRotation.Angle = 0;
        Visibility = Visibility.Collapsed;
    }

    private int ResetForPlay()
    {
        _generation++;
        _finishTimer?.Stop();
        _finishTimer = null;
        ClearAnimations();
        return _generation;
    }

    private void ClearAnimations()
    {
        Veil.BeginAnimation(OpacityProperty, null);
        TopBeam.BeginAnimation(OpacityProperty, null);
        TopBeamScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TopBeamShift.BeginAnimation(TranslateTransform.XProperty, null);
        Emblem.BeginAnimation(OpacityProperty, null);
        EmblemScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        EmblemScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        EmblemOffset.BeginAnimation(TranslateTransform.YProperty, null);
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void ScheduleFinish(int generation, TimeSpan delay, Action callback)
    {
        _finishTimer?.Stop();
        _finishTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = delay };
        _finishTimer.Tick += (_, _) =>
        {
            _finishTimer?.Stop();
            _finishTimer = null;
            if (generation == _generation) callback();
        };
        _finishTimer.Start();
    }

    private static double ClampSpeed(double speedScale)
        => double.IsFinite(speedScale) && speedScale > 0 ? Math.Clamp(speedScale, 0.25, 4.0) : 1.0;

    private static TimeSpan Delay(int baseMs, double speedScale)
        => TimeSpan.FromMilliseconds(Math.Max(16, Math.Round(baseMs / speedScale)));

    private static Duration Duration(int baseMs, double speedScale)
        => new(Delay(baseMs, speedScale));
}
