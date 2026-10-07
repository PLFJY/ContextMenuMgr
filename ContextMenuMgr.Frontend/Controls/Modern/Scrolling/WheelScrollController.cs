using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace ContextMenuMgr.Frontend.Controls.Modern.Scrolling;

internal sealed class WheelScrollController : IDisposable
{
    private readonly ScrollViewer _viewer;
    private readonly WheelInputSequence _sequence = new();

    internal WheelScrollController(ScrollViewer viewer)
    {
        _viewer = viewer;
        viewer.Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Reset();

    public void Dispose()
    {
        Reset();
        _viewer.Unloaded -= OnUnloaded;
    }

    internal void Reset()
    {
        _sequence.Reset();
        ScrollAnimationHelper.CancelVerticalAnimation(_viewer);
    }

    internal void Handle(MouseWheelEventArgs e, bool smooth = true, double multiplier = 1,
        int duration = 220, IEasingFunction? easing = null)
    {
        if (!smooth)
        {
            Reset();
            return;
        }

        if (WheelScrollEventGuard.ShouldSkipSmoothScroll(_viewer, e, e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.Delta == 0)
        {
            e.Handled = true; // WPF's native sign-only handler treats zero as WheelUp.
            return;
        }

        if (!WheelScrollEventGuard.CanNestedViewerScroll(_viewer, e.Delta))
        {
            // Cancel pending movement when reversal meets an actual viewport boundary.
            Reset();
            return;
        }

        var continuous = _sequence.Observe(e.Delta, e.Timestamp);
        var target = WheelScrollPolicy.CalculateTarget(_viewer.VerticalOffset,
            ScrollAnimationHelper.GetCurrentVerticalAnimationTarget(_viewer), e.Delta,
            _viewer.ScrollableHeight, _viewer.ViewportHeight, SystemParameters.WheelScrollLines, multiplier, continuous);
        var effectiveDuration = continuous ? Math.Min(duration, WheelScrollPolicy.ContinuousDurationMilliseconds) : duration;
        ScrollAnimationHelper.SmoothScrollToVerticalOffset(_viewer, target,
            TimeSpan.FromMilliseconds(Math.Max(0, effectiveDuration)), effectiveDuration > 0, easing);
        e.Handled = true;
    }
}
