using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PCL;

/// <summary>
/// 运行在独立 STA 线程上的星遥主题启动加载窗口，在主线程初始化期间保持流畅星月动画并与主窗口无缝接力。
/// </summary>
public partial class HybridSplashWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private bool _dismissing;
    private DispatcherTimer? _dismissTimer;

    public HybridSplashWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    private void CardSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 15, 15);
        clip.Freeze();
        CardSurface.Clip = clip;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            CardRoot.Opacity = 1;
            CardScale.ScaleX = 1;
            CardScale.ScaleY = 1;
            CardOffset.Y = 0;
            return;
        }

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeSine = new SineEase { EasingMode = EasingMode.EaseInOut };

        // 卡片入场浮现
        CardRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });
        var scaleIn = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn.Clone());
        CardOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });

        // 独立线程持续循环动效：轨道金星公转、月晕呼吸、顶缘流金扫掠
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(-24, 336, TimeSpan.FromMilliseconds(3200))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        MoonGlow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(1400))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = easeSine
            });
        TopBeamShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1500))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = easeSine
            });
    }

    /// <summary>
    /// 播放星月绽放淡出退场，并在结束后关闭自身及独立 Dispatcher。
    /// </summary>
    internal void DismissAnimated(double speedScale)
    {
        if (_dismissing) return;
        _dismissing = true;

        if (!SystemParameters.ClientAreaAnimation)
        {
            CloseAndShutdown();
            return;
        }

        var scale = double.IsFinite(speedScale) && speedScale > 0 ? Math.Clamp(speedScale, 0.25, 4.0) : 1.0;
        var durationMs = Math.Max(80, (int)Math.Round(220 / scale));
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        var currentOpacity = Math.Clamp(CardRoot.Opacity, 0, 1);
        var currentScale = double.IsFinite(CardScale.ScaleX) && CardScale.ScaleX > 0.5 ? CardScale.ScaleX : 1.0;
        var currentOffsetY = double.IsFinite(CardOffset.Y) ? CardOffset.Y : 0.0;

        CardRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(currentOpacity, 0, duration)
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });
        var scaleOut = new DoubleAnimation(currentScale, 1.07, duration)
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut.Clone());
        CardOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(currentOffsetY, -6, duration)
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });

        _dismissTimer?.Stop();
        _dismissTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(durationMs + 24)
        };
        _dismissTimer.Tick += (_, _) => CloseAndShutdown();
        _dismissTimer.Start();
    }

    internal void CloseAndShutdown()
    {
        _dismissTimer?.Stop();
        _dismissTimer = null;
        try
        {
            if (IsVisible) Hide();
            Close();
        }
        catch
        {
            // 忽略退出竞态异常
        }
        finally
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

/// <summary>
/// 管理独立 STA 线程上的星遥启动加载闪屏，防止与主 UI 线程相互阻塞。
/// </summary>
internal static class HybridSplashHost
{
    private static readonly object Sync = new();
    private static bool _started;
    private static bool _dismissed;
    private static double _pendingSpeed = 1.0;
    private static bool _closeImmediately;
    private static Dispatcher? _dispatcher;
    private static HybridSplashWindow? _window;

    /// <summary>
    /// 当前是否由星月加载闪屏接管了启动阶段。
    /// </summary>
    public static bool WasShown
    {
        get
        {
            lock (Sync) return _started;
        }
    }

    /// <summary>
    /// 在独立 STA 后台线程上立即显示星遥启动加载卡片。
    /// </summary>
    public static void Show()
    {
        lock (Sync)
        {
            if (_started || _dismissed) return;
            _started = true;
        }

        var thread = new Thread(() =>
        {
            try
            {
                lock (Sync)
                {
                    if (_dismissed) return;
                }

                var dispatcher = Dispatcher.CurrentDispatcher;
                var window = new HybridSplashWindow();
                var immediateClose = false;
                var shouldDismiss = false;
                var dismissSpeed = 1.0;

                lock (Sync)
                {
                    _dispatcher = dispatcher;
                    _window = window;
                    if (_dismissed)
                    {
                        shouldDismiss = true;
                        immediateClose = _closeImmediately;
                        dismissSpeed = _pendingSpeed;
                    }
                }

                if (immediateClose)
                {
                    window.CloseAndShutdown();
                    return;
                }

                window.Show();
                if (shouldDismiss)
                    window.DismissAnimated(dismissSpeed);

                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "星遥启动加载窗口运行异常", ModBase.LogLevel.Debug);
            }
            finally
            {
                lock (Sync)
                {
                    _window = null;
                    _dispatcher = null;
                }
            }
        })
        {
            Name = "PCL-MOON Splash",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// 平滑退场关闭加载闪屏，返回此次启动前是否展示过该闪屏。
    /// </summary>
    public static bool Dismiss(double speedScale = 1.0)
    {
        Dispatcher? dispatcher;
        HybridSplashWindow? window;
        bool wasShown;

        lock (Sync)
        {
            wasShown = _started;
            if (_dismissed) return wasShown;
            _dismissed = true;
            _pendingSpeed = speedScale;
            dispatcher = _dispatcher;
            window = _window;
        }

        if (dispatcher is not null && window is not null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(() => window.DismissAnimated(speedScale));

        return wasShown;
    }

    /// <summary>
    /// 异常或强制退出时立刻关闭闪屏，避免遮挡错误弹窗。
    /// </summary>
    public static void CloseImmediately()
    {
        Dispatcher? dispatcher;
        HybridSplashWindow? window;

        lock (Sync)
        {
            _dismissed = true;
            _closeImmediately = true;
            dispatcher = _dispatcher;
            window = _window;
        }

        if (dispatcher is not null && window is not null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(() => window.CloseAndShutdown());
    }
}
