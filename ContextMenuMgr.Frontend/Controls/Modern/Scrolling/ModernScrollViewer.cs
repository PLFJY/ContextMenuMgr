using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace ContextMenuMgr.Frontend.Controls.Modern.Scrolling;

/// <summary>
/// Pixel scrolling surface for the shared frame and Self pages. Native WPF touch
/// panning is independent of bounded wheel animation and nested wheel ownership.
/// </summary>
public sealed class ModernScrollViewer : ScrollViewer
{
    public static readonly DependencyProperty IsSmoothScrollingEnabledProperty = DependencyProperty.Register(
        nameof(IsSmoothScrollingEnabled), typeof(bool), typeof(ModernScrollViewer), new PropertyMetadata(true));

    public static readonly DependencyProperty WheelScrollMultiplierProperty = DependencyProperty.Register(
        nameof(WheelScrollMultiplier), typeof(double), typeof(ModernScrollViewer), new PropertyMetadata(1D));

    public static readonly DependencyProperty ScrollAnimationDurationProperty = DependencyProperty.Register(
        nameof(ScrollAnimationDuration), typeof(int), typeof(ModernScrollViewer),
        new PropertyMetadata((int)ScrollAnimationHelper.DefaultDuration.TotalMilliseconds));

    public static readonly DependencyProperty ScrollEasingFunctionProperty = DependencyProperty.Register(
        nameof(ScrollEasingFunction), typeof(IEasingFunction), typeof(ModernScrollViewer), new PropertyMetadata(null));

    private readonly WheelScrollController _wheel;

    static ModernScrollViewer()
    {
        PanningModeProperty.OverrideMetadata(typeof(ModernScrollViewer),
            new FrameworkPropertyMetadata(PanningMode.VerticalOnly));
    }

    public ModernScrollViewer()
    {
        _wheel = new WheelScrollController(this);
        PreviewMouseWheel += OnPreviewMouseWheel;
        // Observe native manipulation without claiming it or changing WPF's tap threshold/inertia.
        AddHandler(ManipulationStartingEvent, new EventHandler<ManipulationStartingEventArgs>(
            (_, _) => _wheel.Reset()), handledEventsToo: true);
    }

    public bool IsSmoothScrollingEnabled
    {
        get => (bool)GetValue(IsSmoothScrollingEnabledProperty);
        set => SetValue(IsSmoothScrollingEnabledProperty, value);
    }

    public double WheelScrollMultiplier
    {
        get => (double)GetValue(WheelScrollMultiplierProperty);
        set => SetValue(WheelScrollMultiplierProperty, value);
    }

    public int ScrollAnimationDuration
    {
        get => (int)GetValue(ScrollAnimationDurationProperty);
        set => SetValue(ScrollAnimationDurationProperty, value);
    }

    public IEasingFunction? ScrollEasingFunction
    {
        get => (IEasingFunction?)GetValue(ScrollEasingFunctionProperty);
        set => SetValue(ScrollEasingFunctionProperty, value);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        _wheel.Handle(e, IsSmoothScrollingEnabled, WheelScrollMultiplier, ScrollAnimationDuration, ScrollEasingFunction);

    internal bool CanScrollInDirection(int wheelDelta) =>
        WheelScrollEventGuard.CanNestedViewerScroll(this, wheelDelta);
}
