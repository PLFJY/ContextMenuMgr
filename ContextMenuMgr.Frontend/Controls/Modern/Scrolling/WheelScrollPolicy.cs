namespace ContextMenuMgr.Frontend.Controls.Modern.Scrolling;

/// <summary>Pixel-offset policy; independent of WPF events and hardware vendors.</summary>
internal static class WheelScrollPolicy
{
    internal const int WheelDeltaPerNotch = 120;
    internal const double PixelsPerLine = 16;
    internal const int ContinuousDurationMilliseconds = 60;

    internal static double CalculateTarget(double actualOffset, double? existingTarget, int delta,
        double scrollableHeight, double viewportHeight, int wheelLines, double multiplier, bool continuous)
    {
        var actual = Math.Clamp(actualOffset, 0, Math.Max(0, scrollableHeight));
        if (delta == 0 || wheelLines == 0 || !double.IsFinite(multiplier) || multiplier <= 0)
        {
            return actual;
        }

        // SPI_GETWHEELSCROLLLINES exposes WHEEL_PAGESCROLL (UINT_MAX) as -1.
        var pageScroll = wheelLines < 0;
        var notchDistance = (pageScroll ? Math.Max(0, viewportHeight) : wheelLines * PixelsPerLine) * multiplier;
        var change = -delta / (double)WheelDeltaPerNotch * notchDistance;
        var leadLimit = notchDistance * (continuous || pageScroll ? 1 : 3);
        if (!pageScroll && viewportHeight > 0)
        {
            leadLimit = Math.Max(notchDistance, Math.Min(leadLimit, viewportHeight));
        }

        // Keep pending movement only in the new direction. Reversal starts at the visible offset.
        var pending = existingTarget.GetValueOrDefault(actual) - actual;
        var start = pending * change > 0 ? actual + pending : actual;
        var target = Math.Clamp(start + change, actual - leadLimit, actual + leadLimit);
        return Math.Clamp(target, 0, Math.Max(0, scrollableHeight));
    }
}

/// <summary>Small deltas or events <=40ms apart form a continuous stream until a 120ms pause.</summary>
internal sealed class WheelInputSequence
{
    private int? _lastTimestamp;
    private bool _continuous;

    internal bool Observe(int delta, int timestamp)
    {
        if (delta == 0)
        {
            return _continuous;
        }

        // Unsigned subtraction also handles WPF's signed tick-count wraparound.
        var elapsed = _lastTimestamp is int previous ? unchecked((uint)(timestamp - previous)) : uint.MaxValue;
        _continuous = delta % WheelScrollPolicy.WheelDeltaPerNotch != 0
            || elapsed <= 40
            || (_continuous && elapsed <= 120);
        _lastTimestamp = timestamp;
        return _continuous;
    }

    internal void Reset()
    {
        _lastTimestamp = null;
        _continuous = false;
    }
}
