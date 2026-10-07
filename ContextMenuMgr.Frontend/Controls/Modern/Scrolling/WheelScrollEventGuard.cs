using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ContextMenuMgr.Frontend.Controls.Modern.Scrolling;

internal static class WheelScrollEventGuard
{
    public static bool ShouldSkipSmoothScroll(
        ScrollViewer owner,
        MouseWheelEventArgs e,
        DependencyObject? source,
        ModifierKeys? modifiers = null)
    {
        if (e.Handled
            || ((modifiers ?? Keyboard.Modifiers) & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            return true;
        }

        foreach (var current in EnumerateAncestors(source))
        {
            if (ReferenceEquals(current, owner))
            {
                return false;
            }

            if (current is Popup or ContextMenu
                || current.GetType().Name.Contains("PopupRoot", StringComparison.Ordinal)
                || current is ComboBox { IsDropDownOpen: true })
            {
                return true;
            }

            // Inherited ownership selects the Frame host, but is not a boundary at every
            // descendant. Self pages must still use their own ModernScrollViewer.
            var valueSource = DependencyPropertyHelper.GetValueSource(current, ModernScroll.OwnershipProperty);
            var ownership = valueSource.BaseValueSource == BaseValueSource.Inherited
                ? ModernScrollOwnership.Auto : ModernScroll.GetOwnership(current);
            if (ownership == ModernScrollOwnership.Self)
            {
                return true;
            }

            if (ownership == ModernScrollOwnership.Frame)
            {
                return false;
            }

            if (current is ScrollViewer nested)
            {
                if (CanNestedViewerScroll(nested, e.Delta))
                {
                    return true;
                }

                // A reversal may hand off at the visible boundary before the inner
                // preview handler runs. Stop its old opposite-direction animation.
                var pending = ScrollAnimationHelper.GetCurrentVerticalAnimationTarget(nested);
                if (pending.HasValue && (pending.Value - nested.VerticalOffset) * e.Delta > 0)
                {
                    ScrollAnimationHelper.CancelVerticalAnimation(nested);
                }
            }
        }

        return false;
    }

    internal static bool CanNestedViewerScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0)
        {
            return false;
        }

        return delta switch
        {
            < 0 => viewer.VerticalOffset < viewer.ScrollableHeight,
            > 0 => viewer.VerticalOffset > 0,
            _ => false
        };
    }

    internal static IEnumerable<DependencyObject> EnumerateAncestors(DependencyObject? source)
    {
        var current = source;
        var visited = new HashSet<DependencyObject>();
        while (current is not null && visited.Add(current))
        {
            yield return current;
            current = GetParent(current);
        }
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is Popup popup)
        {
            return popup.PlacementTarget;
        }

        if (current is ContextMenu contextMenu)
        {
            return contextMenu.PlacementTarget;
        }

        if (current is Visual or Visual3D)
        {
            var visualParent = VisualTreeHelper.GetParent(current);
            if (visualParent is not null)
            {
                return visualParent;
            }
        }

        return current switch
        {
            FrameworkElement element => element.Parent ?? element.TemplatedParent,
            FrameworkContentElement contentElement => contentElement.Parent ?? contentElement.TemplatedParent,
            _ => LogicalTreeHelper.GetParent(current)
        };
    }
}
