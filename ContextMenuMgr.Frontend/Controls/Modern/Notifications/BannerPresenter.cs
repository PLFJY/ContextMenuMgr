using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ContextMenuMgr.Frontend.Controls.Modern.Notifications;

/// <summary>Reveals a floating banner from its top edge and retains it until exit completes.</summary>
public sealed class BannerPresenter : Decorator
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(BannerPresenter), new PropertyMetadata(false, OnIsOpenChanged));

    public static readonly DependencyProperty MinimumDisplayDurationProperty = DependencyProperty.Register(
        nameof(MinimumDisplayDuration), typeof(TimeSpan), typeof(BannerPresenter), new PropertyMetadata(TimeSpan.Zero),
        value => (TimeSpan)value >= TimeSpan.Zero);

    public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.Register(
        nameof(DisplayText), typeof(string), typeof(BannerPresenter), new PropertyMetadata(null, OnDisplayTextChanged));

    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        "Reveal", typeof(double), typeof(BannerPresenter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    private readonly RectangleGeometry _clip = new();
    private readonly TranslateTransform _translation = new();
    private double _naturalHeight;
    private int _transitionVersion;
    private readonly DispatcherTimer _closeTimer;
    private long _displayStartedAt;
    private TimeSpan _entryAllowance;

    public BannerPresenter()
    {
        _closeTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _closeTimer.Tick += (_, _) => RequestTransition();
        Clip = _clip;
        Visibility = Visibility.Collapsed;
        Opacity = 0;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    public TimeSpan MinimumDisplayDuration { get => (TimeSpan)GetValue(MinimumDisplayDurationProperty); set => SetValue(MinimumDisplayDurationProperty, value); }

    public string? DisplayText { get => (string?)GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }

    private double Reveal => Math.Clamp((double)GetValue(RevealProperty), 0, 1);

    private static void OnIsOpenChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var presenter = (BannerPresenter)sender;
        if (e.NewValue is true) presenter.StartDisplayClock();
        if (presenter.IsLoaded) presenter.RequestTransition();
        else if (e.NewValue is true) presenter.Visibility = Visibility.Visible;
    }

    private static void OnDisplayTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var presenter = (BannerPresenter)sender;
        if (presenter.IsOpen && e.NewValue is string text && !string.IsNullOrWhiteSpace(text))
            presenter.StartDisplayClock();
    }

    private void StartDisplayClock()
    {
        _closeTimer.Stop();
        _displayStartedAt = Stopwatch.GetTimestamp();
        _entryAllowance = SystemParameters.ClientAreaAnimation && Reveal < 1
            ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero;
    }

    private void RequestTransition()
    {
        _closeTimer.Stop();
        if (!IsOpen && _displayStartedAt != 0 && MinimumDisplayDuration > TimeSpan.Zero)
        {
            var remaining = MinimumDisplayDuration + _entryAllowance - Stopwatch.GetElapsedTime(_displayStartedAt);
            if (remaining > TimeSpan.Zero)
            {
                _closeTimer.Interval = remaining;
                _closeTimer.Start();
                return;
            }
        }
        TransitionTo(IsOpen);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        _naturalHeight = Child.DesiredSize.Height;
        return new Size(Child.DesiredSize.Width, _naturalHeight * Reveal);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is not null)
        {
            Child.RenderTransform = _translation;
            Child.Arrange(new Rect(0, 0, arrangeSize.Width, _naturalHeight));
            _translation.Y = -_naturalHeight * (1 - Reveal);
        }
        // Release clipping once fully open so the surface shadow is not cut off.
        Clip = Reveal >= 1 ? null : _clip;
        _clip.Rect = new Rect(0, 0, arrangeSize.Width, _naturalHeight * Reveal);
        return arrangeSize;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        if (IsOpen) StartDisplayClock();
        RequestTransition();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _closeTimer.Stop();
        _displayStartedAt = 0;
        StopAnimations();
        SetCurrentValue(RevealProperty, 0d);
        Opacity = 0;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _translation.Y = 0;
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (Dispatcher.CheckAccess()) { if (IsLoaded) RequestTransition(); }
        else _ = Dispatcher.BeginInvoke((Action)(() => { if (IsLoaded) RequestTransition(); }));
    }

    private void StopAnimations()
    {
        _transitionVersion++;
        BeginAnimation(RevealProperty, null);
        BeginAnimation(OpacityProperty, null);
    }

    private void TransitionTo(bool open)
    {
        var from = Reveal;
        var fromOpacity = Opacity;
        StopAnimations();
        var version = _transitionVersion;
        var target = open ? 1d : 0d;
        IsHitTestVisible = open;
        if (!SystemParameters.ClientAreaAnimation || Math.Abs(from - target) < 0.001)
        {
            SetCurrentValue(RevealProperty, target);
            Opacity = target;
            Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        // Windows Fluent control timings and entrance/exit curves:
        // https://learn.microsoft.com/windows/apps/design/motion/timing-and-easing
        var duration = TimeSpan.FromMilliseconds(open ? 250 : 167);
        var motion = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.Stop };
        motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        motion.KeyFrames.Add(new SplineDoubleKeyFrame(target, KeyTime.FromTimeSpan(duration),
            open ? new KeySpline(0, 0, 0, 1) : new KeySpline(1, 0, 1, 1)));
        var fade = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromOpacity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (!open) fade.KeyFrames.Add(new LinearDoubleKeyFrame(fromOpacity, KeyTime.FromTimeSpan(duration - TimeSpan.FromMilliseconds(83))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(target,
            KeyTime.FromTimeSpan(open ? TimeSpan.FromMilliseconds(83) : duration)));
        motion.Completed += (_, _) =>
        {
            if (version != _transitionVersion || !IsLoaded) return;
            StopAnimations();
            SetCurrentValue(RevealProperty, target);
            Opacity = target;
            Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        };
        SetCurrentValue(RevealProperty, target);
        SetCurrentValue(OpacityProperty, target);
        BeginAnimation(RevealProperty, motion, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
}
