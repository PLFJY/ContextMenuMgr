using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ContextMenuMgr.Frontend.Controls.Modern.Scrolling;

public static class ScrollAnimationHelper
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(220);
    private static readonly ConditionalWeakTable<ScrollViewer, VerticalAnimation> Animations = new();
    private static readonly IEasingFunction DefaultEasing = CreateDefaultEasing();

    private static IEasingFunction CreateDefaultEasing()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }

    public static double? GetCurrentVerticalAnimationTarget(ScrollViewer viewer) =>
        Animations.TryGetValue(viewer, out var animation) && animation.IsActive ? animation.TargetOffset : null;

    public static void CancelVerticalAnimation(ScrollViewer viewer)
    {
        if (!viewer.Dispatcher.CheckAccess())
        {
            viewer.Dispatcher.Invoke(() => CancelVerticalAnimation(viewer));
            return;
        }

        if (Animations.TryGetValue(viewer, out var animation))
        {
            animation.Stop();
            Animations.Remove(viewer);
        }
    }

    public static void SmoothScrollToVerticalOffset(
        ScrollViewer viewer,
        double targetOffset,
        TimeSpan? duration = null,
        bool animated = true,
        IEasingFunction? easingFunction = null)
    {
        if (!viewer.Dispatcher.CheckAccess())
        {
            viewer.Dispatcher.Invoke(() =>
                SmoothScrollToVerticalOffset(viewer, targetOffset, duration, animated, easingFunction));
            return;
        }

        var target = double.IsNaN(targetOffset)
            ? viewer.VerticalOffset
            : Math.Clamp(targetOffset, 0, Math.Max(0, viewer.ScrollableHeight));
        var effectiveDuration = duration ?? DefaultDuration;
        if (!animated
            || effectiveDuration <= TimeSpan.Zero
            || !SystemParameters.ClientAreaAnimation
            || RenderCapability.Tier <= 0)
        {
            CancelVerticalAnimation(viewer);
            viewer.ScrollToVerticalOffset(target);
            return;
        }

        var easing = easingFunction ?? DefaultEasing;
        if (Animations.TryGetValue(viewer, out var existing))
        {
            // A saturated lead must not restart the same animation on every event.
            if (!existing.Matches(target, effectiveDuration, easing))
            {
                existing.Retarget(target, effectiveDuration, easing);
            }
            return;
        }

        var animation = new VerticalAnimation(viewer, target, effectiveDuration, easing, RemoveAnimation);
        Animations.Add(viewer, animation);
        animation.Start();
    }

    private static void RemoveAnimation(ScrollViewer viewer) => Animations.Remove(viewer);

    private sealed class VerticalAnimation(
        ScrollViewer viewer,
        double targetOffset,
        TimeSpan duration,
        IEasingFunction easing,
        Action<ScrollViewer> remove)
    {
        private readonly WeakReference<ScrollViewer> _viewer = new(viewer);
        private long _startedAt;
        private double _startOffset;
        private TimeSpan _duration = duration;
        private IEasingFunction _easing = easing;

        public bool IsActive { get; private set; }
        public double TargetOffset { get; private set; } = targetOffset;

        public bool Matches(double offset, TimeSpan requestedDuration, IEasingFunction requestedEasing) =>
            TargetOffset == offset && _duration == requestedDuration && ReferenceEquals(_easing, requestedEasing);

        public void Start()
        {
            if (!_viewer.TryGetTarget(out var target))
            {
                return;
            }

            _startOffset = target.VerticalOffset;
            _startedAt = Stopwatch.GetTimestamp();
            IsActive = true;
            CompositionTarget.Rendering += OnRendering;
        }

        public void Retarget(double targetOffset, TimeSpan newDuration, IEasingFunction newEasing)
        {
            if (!_viewer.TryGetTarget(out var target))
            {
                Stop();
                return;
            }

            TargetOffset = targetOffset;
            _duration = newDuration;
            _easing = newEasing;
            _startOffset = target.VerticalOffset;
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Stop()
        {
            if (!IsActive)
            {
                return;
            }

            IsActive = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_viewer.TryGetTarget(out var target))
            {
                Stop();
                return;
            }

            var progress = Math.Clamp(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
            var offset = _startOffset + ((TargetOffset - _startOffset) * _easing.Ease(progress));
            target.ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, target.ScrollableHeight)));
            if (progress < 1)
            {
                return;
            }

            target.ScrollToVerticalOffset(Math.Clamp(TargetOffset, 0, Math.Max(0, target.ScrollableHeight)));
            Stop();
            remove(target);
        }
    }
}
