using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PCL;

/// <summary>Skeleton shown while the browse WebView2 initializes; WPF cannot draw over the WebView HWND, so it sits behind a hidden view.</summary>
public partial class HybridBrowseLoading : UserControl
{
    private static readonly Brush Bone = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)));
    private bool _dismissed;

    public HybridBrowseLoading()
    {
        InitializeComponent();
        for (var i = 0; i < 4; i++) Recommend.Children.Add(RecommendRow(i == 1));
        Loaded += (_, _) => Play();
    }

    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }

    private static FrameworkElement RecommendRow(bool active)
    {
        var row = new Grid { Height = 60, Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (active)
        {
            var plate = new Border { Background = new SolidColorBrush(Color.FromRgb(0x24, 0x20, 0x1B)), CornerRadius = new CornerRadius(6), Margin = new Thickness(-4, -4, 0, -4) };
            Grid.SetColumnSpan(plate, 2);
            row.Children.Add(plate);
        }
        row.Children.Add(new Border { Width = 44, Height = 44, Background = Bone, CornerRadius = new CornerRadius(4) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(new Border { Width = 120, Height = 12, Background = Bone, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left });
        text.Children.Add(new Border { Width = 80, Height = 10, Background = Bone, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private void Play()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        // 进入：与 HybridMotion.Enter 一致的错峰下落
        var delay = 0;
        foreach (FrameworkElement element in new FrameworkElement[] { RowHeader, RowDiscover, RowTitle, RowStage })
        {
            var shift = new TranslateTransform(0, -16);
            element.RenderTransform = shift;
            element.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(delay);
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = begin });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-16, 0, TimeSpan.FromMilliseconds(380))
                { BeginTime = begin, EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
            delay += 25;
        }
        // 循环：顶部进度扫光、骨架呼吸、月牙轻微脉动
        ProgressShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1300))
            { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        var breathe = new DoubleAnimation(1, 0.7, TimeSpan.FromMilliseconds(900))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(400), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Skeleton.BeginAnimation(OpacityProperty, breathe);
        var pulse = new DoubleAnimation(1, 1.12, TimeSpan.FromMilliseconds(900))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        MarkScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        MarkScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    /// <summary>Fade out, then run <paramref name="completed"/> (reveals the WebView and removes this control).</summary>
    public void Dismiss(Action completed)
    {
        if (_dismissed) return;
        _dismissed = true;
        if (!SystemParameters.ClientAreaAnimation || !IsVisible) { completed(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => completed();
        BeginAnimation(OpacityProperty, fade);
    }
}
