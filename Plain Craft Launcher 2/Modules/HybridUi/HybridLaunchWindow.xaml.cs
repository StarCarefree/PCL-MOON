using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

/// <summary>
/// Non-modal, ownerless launch presentation. Closing it never cancels a launch or shuts down the application.
/// Create a new instance to reopen after Closed; BeginLaunch shows the window without ShowDialog.
/// </summary>
public partial class HybridLaunchWindow : Window
{
    private const double EntranceScale = 0.972;
    private const double EntranceOffset = 16;
    private bool _closed;
    private int _entranceGeneration;
    private DispatcherTimer? _entranceGuard;
    public event EventHandler? CancelRequested;

    public HybridLaunchWindow()
    {
        InitializeComponent();
        Screen.HomeRequested += ScreenHomeRequested;
        Screen.CancelRequested += ScreenCancelRequested;
    }

    public void BeginLaunch(string instanceName)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => BeginLaunch(instanceName));
            return;
        }
        if (_closed) throw new InvalidOperationException("启动窗口已关闭，请创建新的 HybridLaunchWindow 实例。");
        Screen.BeginLaunch(instanceName);
        // Do not assign Owner: minimizing the launcher must not minimize this window.
        if (!IsVisible)
        {
            var generation = PrepareEntrance();
            Show();
            // 未准备入场动画，或 Show 期间已被停止时不再播放
            if (generation > 0 && generation == _entranceGeneration && Shell.RenderTransform is TransformGroup) PlayEntrance();
        }
        Screen.SetPresentationSuspended(WindowState == WindowState.Minimized);
    }

    private int PrepareEntrance()
    {
        if (!SystemParameters.ClientAreaAnimation || WindowState == WindowState.Minimized)
        {
            StopEntrance();
            return 0;
        }
        Opacity = 0;
        Shell.RenderTransformOrigin = new Point(0.5, 0.62);
        Shell.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(EntranceScale, EntranceScale),
                new TranslateTransform(0, EntranceOffset)
            }
        };
        SystemParameters.StaticPropertyChanged += AnimationSettingChanged;
        return ++_entranceGeneration;
    }

    private void PlayEntrance()
    {
        var generation = _entranceGeneration;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        HoldThenSettle(this, OpacityProperty, 0, 1, 320, ease, generation, isElement: true);
        HoldThenSettle(ShellScale, ScaleTransform.ScaleXProperty, EntranceScale, 1, 460, ease, generation);
        HoldThenSettle(ShellScale, ScaleTransform.ScaleYProperty, EntranceScale, 1, 460, ease, generation);
        HoldThenSettle(ShellOffset, TranslateTransform.YProperty, EntranceOffset, 0, 400, ease, generation);
        _entranceGuard?.Stop();
        _entranceGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(560) };
        _entranceGuard.Tick += (_, _) => { if (generation == _entranceGeneration) StopEntrance(); };
        _entranceGuard.Start();
    }

    private ScaleTransform ShellScale => (ScaleTransform)((TransformGroup)Shell.RenderTransform).Children[0];
    private TranslateTransform ShellOffset => (TranslateTransform)((TransformGroup)Shell.RenderTransform).Children[1];

    private void HoldThenSettle(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds, IEasingFunction ease, int generation, bool isElement = false)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (generation != _entranceGeneration) return;
            target.SetValue(property, to);
            if (isElement) ((UIElement)target).BeginAnimation(property, null);
            else ((Animatable)target).BeginAnimation(property, null);
        };
        if (isElement) ((UIElement)target).BeginAnimation(property, animation);
        else ((Animatable)target).BeginAnimation(property, animation);
    }
    internal void StopEntrance()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(StopEntrance);
            return;
        }
        _entranceGeneration++;
        _entranceGuard?.Stop();
        _entranceGuard = null;
        SystemParameters.StaticPropertyChanged -= AnimationSettingChanged;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (Shell?.RenderTransform is TransformGroup group && group.Children.Count == 2)
        {
            group.Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, null);
            group.Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            group.Children[1].BeginAnimation(TranslateTransform.YProperty, null);
        }
        if (Shell is not null)
        {
            Shell.RenderTransform = Transform.Identity;
            Shell.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }

    private void AnimationSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation) && !SystemParameters.ClientAreaAnimation)
            Dispatcher.BeginInvoke(StopEntrance);
    }

    public void ApplyProgress(LaunchProgressSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyProgress(snapshot)));
            return;
        }
        if (!_closed) Screen.ApplyProgress(snapshot);
    }

    private void ScreenHomeRequested(object? sender, EventArgs e) => Close();
    private void ScreenCancelRequested(object? sender, EventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowStateChanged(object? sender, EventArgs e)
    {
        Screen?.SetPresentationSuspended(WindowState == WindowState.Minimized);
        if (WindowState == WindowState.Minimized) StopEntrance();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        StopEntrance();
        Screen.HomeRequested -= ScreenHomeRequested;
        Screen.CancelRequested -= ScreenCancelRequested;
        Screen.EndLaunch();
    }
}
