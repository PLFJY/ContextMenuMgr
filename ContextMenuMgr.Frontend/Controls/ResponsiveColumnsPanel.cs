using System.Windows;
using System.Windows.Controls;

namespace ContextMenuMgr.Frontend.Controls;

/// <summary>Arranges a pair of related regions side by side, stacking them when space is limited.</summary>
public sealed class ResponsiveColumnsPanel : Panel
{
    public static readonly DependencyProperty BreakpointProperty = DependencyProperty.Register(
        nameof(Breakpoint), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(640d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FirstColumnWidthProperty = DependencyProperty.Register(
        nameof(FirstColumnWidth), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(220d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty StackedFirstMaxHeightProperty = DependencyProperty.Register(
        nameof(StackedFirstMaxHeight), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SizeFirstToRemainingProperty = DependencyProperty.Register(
        nameof(SizeFirstToRemaining), typeof(bool), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Breakpoint { get => (double)GetValue(BreakpointProperty); set => SetValue(BreakpointProperty, value); }
    public double FirstColumnWidth { get => (double)GetValue(FirstColumnWidthProperty); set => SetValue(FirstColumnWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double StackedFirstMaxHeight { get => (double)GetValue(StackedFirstMaxHeightProperty); set => SetValue(StackedFirstMaxHeightProperty, value); }
    public bool SizeFirstToRemaining { get => (bool)GetValue(SizeFirstToRemainingProperty); set => SetValue(SizeFirstToRemainingProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2) return base.MeasureOverride(availableSize);
        var first = InternalChildren[0];
        var second = InternalChildren[1];
        var width = availableSize.Width;
        if (width < Breakpoint)
        {
            first.Measure(new Size(width, Math.Min(StackedFirstMaxHeight, availableSize.Height)));
            second.Measure(new Size(width, Math.Max(0, availableSize.Height - first.DesiredSize.Height - Gap)));
            return new Size(Math.Max(first.DesiredSize.Width, second.DesiredSize.Width),
                first.DesiredSize.Height + Gap + second.DesiredSize.Height);
        }

        if (SizeFirstToRemaining)
        {
            second.Measure(new Size(Math.Min(width, 360), availableSize.Height));
            first.Measure(new Size(Math.Max(0, width - second.DesiredSize.Width - Gap), availableSize.Height));
        }
        else
        {
            first.Measure(new Size(FirstColumnWidth, availableSize.Height));
            second.Measure(new Size(Math.Max(0, width - FirstColumnWidth - Gap), availableSize.Height));
        }
        return new Size(first.DesiredSize.Width + Gap + second.DesiredSize.Width,
            Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return base.ArrangeOverride(finalSize);
        if (finalSize.Width < Breakpoint)
        {
            var height = Math.Min(InternalChildren[0].DesiredSize.Height, finalSize.Height);
            InternalChildren[0].Arrange(new Rect(0, 0, finalSize.Width, height));
            InternalChildren[1].Arrange(new Rect(0, height + Gap, finalSize.Width,
                Math.Max(0, finalSize.Height - height - Gap)));
        }
        else
        {
            var firstWidth = SizeFirstToRemaining
                ? Math.Max(0, finalSize.Width - InternalChildren[1].DesiredSize.Width - Gap)
                : FirstColumnWidth;
            InternalChildren[0].Arrange(new Rect(0, 0, firstWidth, finalSize.Height));
            InternalChildren[1].Arrange(new Rect(firstWidth + Gap, 0,
                Math.Max(0, finalSize.Width - firstWidth - Gap), finalSize.Height));
        }
        return finalSize;
    }
}
