using ContextMenuMgr.Frontend.Controls.Modern.Scrolling;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class WheelScrollPolicyTests
{
    [Theory]
    [InlineData(-120, 248)]
    [InlineData(120, 152)]
    [InlineData(-30, 212)]
    [InlineData(1, 199.6)]
    [InlineData(0, 200)]
    public void DeltaRetainsDirectionAndFraction(int delta, double expected) =>
        Assert.Equal(expected, Target(200, null, delta), 8);

    [Theory]
    [InlineData(true, -1, 48)]
    [InlineData(true, -120, 48)]
    [InlineData(false, -120, 144)]
    public void LongStreamWithLaggingViewportHasBoundedLead(bool continuous, int delta, double limit)
    {
        double? target = null;
        double actual = 200;
        for (var i = 0; i < 10000; i++)
        {
            target = Target(actual, target, delta, continuous: continuous);
            Assert.InRange(target.Value - actual, 0, limit);
            // Simulate a slow renderer; the target must stay bounded as the viewport moves.
            actual = Math.Min(9000, actual + 0.01);
        }
    }

    [Theory]
    [InlineData(300, 120, 152)]
    [InlineData(100, -120, 248)]
    public void ReversalDiscardsPendingMovement(double oldTarget, int delta, double expected) =>
        Assert.Equal(expected, Target(200, oldTarget, delta));

    [Fact]
    public void ClampsToBothContentBoundaries()
    {
        Assert.Equal(0, Target(10, null, 120));
        Assert.Equal(10000, Target(9990, null, -120));
    }

    [Fact]
    public void DiscreteNotchesAccumulateUntilBound()
    {
        Assert.Equal(296, Target(200, 248, -120));
        Assert.Equal(344, Target(200, null, -360));
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(0.5, 224)]
    [InlineData(2, 296)]
    public void MultiplierIsRespected(double multiplier, double expected) =>
        Assert.Equal(expected, Target(200, null, -120, multiplier: multiplier));

    [Fact]
    public void WindowsSettingsIncludeNoScrollAndPageScroll()
    {
        Assert.Equal(200, Target(200, 300, -120, lines: 0));
        Assert.Equal(216, Target(200, null, -120, lines: 1));
        Assert.Equal(800, Target(200, null, -120, lines: -1));
        Assert.Equal(350, Target(200, null, -30, lines: -1));
        Assert.Equal(0, Target(200, null, 120, lines: -1));
        Assert.Equal(1400, Target(200, null, -120, lines: -1, multiplier: 2));
        Assert.Equal(1800, Target(200, null, -120, lines: 100));
    }

    [Fact]
    public void ZeroDeltaAndInvalidMultiplierDoNothing()
    {
        Assert.Equal(200, Target(200, 300, 0));
        Assert.Equal(200, Target(200, null, -120, multiplier: double.NaN));
        Assert.Equal(200, Target(200, null, -120, multiplier: double.PositiveInfinity));
    }

    [Fact]
    public void ClassificationUsesDeltasAndTimingWithPauseAndReset()
    {
        var sequence = new WheelInputSequence();
        Assert.False(sequence.Observe(-120, 100));
        Assert.False(sequence.Observe(-120, 300));
        Assert.True(sequence.Observe(-120, 330));
        Assert.True(sequence.Observe(120, 420));
        Assert.False(sequence.Observe(120, 600));
        Assert.True(sequence.Observe(-1, 800));
        Assert.True(sequence.Observe(-120, 890));
        sequence.Reset();
        Assert.False(sequence.Observe(-120, 900));
    }

    [Fact]
    public void TimestampWrapAndZeroDeltaDoNotBreakClassification()
    {
        var sequence = new WheelInputSequence();
        Assert.False(sequence.Observe(-120, int.MaxValue - 5));
        Assert.True(sequence.Observe(-120, int.MinValue + 5));
        sequence.Reset();
        Assert.False(sequence.Observe(0, 20));
        Assert.False(sequence.Observe(-120, 25));
    }

    private static double Target(double actual, double? target, int delta, int lines = 3,
        double multiplier = 1, bool continuous = false) =>
        WheelScrollPolicy.CalculateTarget(actual, target, delta, 10000, 600, lines, multiplier, continuous);
}
