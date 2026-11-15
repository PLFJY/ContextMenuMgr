using System.Windows;
using System.Windows.Media.Animation;

namespace ContextMenuMgr.Frontend.Controls.Modern.Frame;

public sealed class FadeNavigationTransitionInfo : ModernNavigationTransitionInfo
{
    internal override Storyboard CreateEnterStoryboard(FrameworkElement element, bool movingBackwards, TimeSpan duration)
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(EntranceNavigationTransitionInfo.StagedSpline(OpacityPath, 0, 1, TimeSpan.FromTicks(duration.Ticks / 3), duration, DecelerateKeySpline));
        return storyboard;
    }

    internal override Storyboard CreateExitStoryboard(FrameworkElement element, bool movingBackwards, TimeSpan duration)
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(EntranceNavigationTransitionInfo.Opacity(
            1, 0, TimeSpan.FromTicks(duration.Ticks / 3), AccelerateKeySpline));
        return storyboard;
    }
}
