using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ContextMenuMgr.Frontend.Controls.Modern.Frame;

public sealed class EntranceNavigationTransitionInfo : ModernNavigationTransitionInfo
{
    // WinUI ThemeTransitions.cpp, EntranceNavigationTransitionInfo::CreateStoryboards:
    // https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/phone/lib/ThemeTransitions.cpp#L2861
    // 140 DIP, 150 ms exit followed by 300 ms entrance, with the original cubic curves.
    internal override Storyboard CreateEnterStoryboard(FrameworkElement element, bool movingBackwards, TimeSpan duration)
    {
        var exit = TimeSpan.FromTicks(duration.Ticks / 3);
        var storyboard = new Storyboard();
        if (movingBackwards)
            storyboard.Children.Add(StagedSpline(OpacityPath, 0, 1, exit, duration, DecelerateKeySpline));
        else
        {
            element.RenderTransform = new TranslateTransform();
            storyboard.Children.Add(StagedSpline(TranslateYPath, 140, 0, exit, duration, DecelerateKeySpline));
            storyboard.Children.Add(StepOpacity(0, 1, exit));
        }
        return storyboard;
    }

    internal override Storyboard CreateExitStoryboard(FrameworkElement element, bool movingBackwards, TimeSpan duration)
    {
        var exit = TimeSpan.FromTicks(duration.Ticks / 3);
        var storyboard = new Storyboard();
        if (movingBackwards)
        {
            element.RenderTransform = new TranslateTransform();
            storyboard.Children.Add(Translate(TranslateYPath, 0, 140, exit, AccelerateKeySpline));
            storyboard.Children.Add(StepOpacity(1, 0, exit));
        }
        else storyboard.Children.Add(Opacity(1, 0, exit, AccelerateKeySpline));
        return storyboard;
    }

    internal static DoubleAnimationUsingKeyFrames StagedSpline(PropertyPath path, double from, double to,
        TimeSpan start, TimeSpan end, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new DiscreteDoubleKeyFrame(from, TimeSpan.Zero), new DiscreteDoubleKeyFrame(from, start),
                new SplineDoubleKeyFrame(to, end, spline) }
        };
        Storyboard.SetTargetProperty(animation, path);
        return animation;
    }

    internal static DoubleAnimationUsingKeyFrames StepOpacity(double from, double to, TimeSpan at)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        { KeyFrames = { new DiscreteDoubleKeyFrame(from, TimeSpan.Zero), new DiscreteDoubleKeyFrame(to, at) } };
        Storyboard.SetTargetProperty(animation, OpacityPath);
        return animation;
    }

    internal static DoubleAnimationUsingKeyFrames Translate(PropertyPath path, double from, double to, TimeSpan duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new DiscreteDoubleKeyFrame(from, TimeSpan.Zero), new SplineDoubleKeyFrame(to, duration, spline) }
        };
        Storyboard.SetTargetProperty(animation, path);
        return animation;
    }

    internal static DoubleAnimationUsingKeyFrames Opacity(double from, double to, TimeSpan duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new DiscreteDoubleKeyFrame(from, TimeSpan.Zero), new SplineDoubleKeyFrame(to, duration, spline) }
        };
        Storyboard.SetTargetProperty(animation, OpacityPath);
        return animation;
    }

}
