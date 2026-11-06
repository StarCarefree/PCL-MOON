using System.Windows;
using System.Windows.Media.Animation;

namespace PCL;

public partial class PageInstanceCompResource
{
    private void SetListPreparing(bool preparing)
    {
        Load.ShowProgress = false;
        Load.SetResourceReference(MyLoading.TextProperty,
            preparing ? "Instance.Resource.List.Preparing" : "Instance.Resource.Loading");
        PanPreparingDots.Visibility = preparing ? Visibility.Visible : Visibility.Hidden;
        for (var i = 0; i < PanPreparingDots.Children.Count; i++)
        {
            var dot = PanPreparingDots.Children[i];
            dot.BeginAnimation(OpacityProperty, null);
            dot.Opacity = 0.3;
            if (!preparing || !SystemParameters.ClientAreaAnimation || ModAnimation.aniSpeed > 10) continue;
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(450))
            {
                BeginTime = TimeSpan.FromMilliseconds(i * 140),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
    }
}
