using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ContextMenuMgr.Frontend.Controls.Modern.Scrolling;

/// <summary>
/// Opt-in wheel behavior for lists whose template supplies a pixel-scrolling viewer.
/// Uses the input ancestor path, so template replacements need no cached visual-tree search.
/// </summary>
public static class ModernListScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ModernListScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ListScrollState), typeof(ModernListScroll));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            list.PreviewMouseWheel += OnPreviewMouseWheel;
            list.Unloaded += OnUnloaded;
            list.AddHandler(UIElement.ManipulationStartingEvent,
                new EventHandler<ManipulationStartingEventArgs>(OnManipulationStarting), true);
        }
        else
        {
            list.PreviewMouseWheel -= OnPreviewMouseWheel;
            list.Unloaded -= OnUnloaded;
            list.RemoveHandler(UIElement.ManipulationStartingEvent,
                new EventHandler<ManipulationStartingEventArgs>(OnManipulationStarting));
            ClearState(list);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var list = (ItemsControl)sender;
        ScrollViewer? viewer = null;
        foreach (var ancestor in WheelScrollEventGuard.EnumerateAncestors(e.OriginalSource as DependencyObject))
        {
            if (ReferenceEquals(ancestor, list))
            {
                break;
            }

            if (ancestor is ScrollViewer candidate && ReferenceEquals(candidate.TemplatedParent, list))
            {
                viewer = candidate;
                break;
            }
        }

        if (viewer is null or ModernScrollViewer)
        {
            return;
        }

        var state = (ListScrollState?)list.GetValue(StateProperty);
        if (state is null || !ReferenceEquals(state.Viewer, viewer))
        {
            ClearState(list);
            state = new ListScrollState(viewer, new WheelScrollController(viewer));
            list.SetValue(StateProperty, state);
        }

        state.Controller.Handle(e);
    }

    private static void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e) =>
        ((ListScrollState?)((ItemsControl)sender!).GetValue(StateProperty))?.Controller.Reset();

    private static void OnUnloaded(object sender, RoutedEventArgs e) => ClearState((ItemsControl)sender);

    private static void ClearState(ItemsControl list)
    {
        ((ListScrollState?)list.GetValue(StateProperty))?.Controller.Dispose();
        list.ClearValue(StateProperty);
    }

    private sealed record ListScrollState(ScrollViewer Viewer, WheelScrollController Controller);
}
