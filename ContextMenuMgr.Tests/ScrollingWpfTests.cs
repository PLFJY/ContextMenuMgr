using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ContextMenuMgr.Frontend.Controls.Modern.Navigation;
using ContextMenuMgr.Frontend.Controls.Modern.Scrolling;
using Xunit;

namespace ContextMenuMgr.Tests;

[CollectionDefinition("WPF scrolling", DisableParallelization = true)]
public sealed class WpfScrollingCollection;

[Collection("WPF scrolling")]
public sealed class ScrollingWpfTests
{
    // One STA owns the application resources and all WPF objects. No frontend startup,
    // backend connection, real input device, or interactive window is required.
    [Fact]
    public void NativeTouchConfigurationWheelRoutingAndNavigationTemplate()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TestApp? app = null;
            try
            {
                app = new TestApp();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/ContextMenuManagerPlus;component/Controls/Modern/Scrolling/ModernScrollingStyles.xaml",
                        UriKind.Relative)
                });
                CheckTouchDefaults();
                CheckNestedRouting();
                CheckOwnershipBoundaries();
                CheckPopupAndModifiers();
                CheckNavigationTemplate(app);
                CheckAnimationCleanup();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (app is not null)
                {
                    app.Shutdown();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                        new Action(() => Dispatcher.CurrentDispatcher.InvokeShutdown()));
                    Dispatcher.Run(); // completes application shutdown and clears Application.Current
                }
                else
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF scrolling checks timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void CheckTouchDefaults()
    {
        var viewer = new ModernScrollViewer();
        Assert.Equal(PanningMode.VerticalOnly, viewer.PanningMode);
        Assert.Equal(DependencyProperty.UnsetValue, viewer.ReadLocalValue(ScrollViewer.PanningModeProperty));
        viewer.ApplyTemplate();
        Assert.True(viewer.IsManipulationEnabled);
        viewer.Style = new Style(typeof(ModernScrollViewer))
        {
            Setters = { new Setter(ScrollViewer.PanningModeProperty, PanningMode.None) }
        };
        Assert.Equal(PanningMode.None, viewer.PanningMode);
        Assert.False(viewer.IsManipulationEnabled);
        viewer.PanningMode = PanningMode.VerticalFirst;
        Assert.Equal(PanningMode.VerticalFirst, viewer.PanningMode);
        Assert.True(viewer.IsManipulationEnabled);
        Assert.Equal(new ScrollViewer().PanningDeceleration, new ModernScrollViewer().PanningDeceleration);
    }

    private static void CheckNestedRouting()
    {
        var source = new Border { Height = 2000 };
        var inner = new ModernScrollViewer { Content = source, Height = 100 };
        var panel = new StackPanel();
        panel.Children.Add(inner);
        panel.Children.Add(new Border { Height = 2000 });
        var outer = new ModernScrollViewer { Content = panel };
        Layout(outer);
        Assert.True(inner.ScrollableHeight > 0);
        Assert.True(Skip(outer, source, -120));
        Assert.False(Skip(inner, source, -120));

        inner.ScrollToBottom();
        Layout(outer);
        Assert.Equal(inner.ScrollableHeight, inner.VerticalOffset);
        Assert.False(Skip(outer, source, -120));
        Assert.True(Skip(outer, source, 120));

        ScrollAnimationHelper.SmoothScrollToVerticalOffset(inner, inner.VerticalOffset - 48);
        Assert.False(Skip(outer, source, -120));
        Assert.Null(ScrollAnimationHelper.GetCurrentVerticalAnimationTarget(inner));

        // The same routed event must be claimed once, by the outer owner at the boundary.
        var wheel = Wheel(source, -30);
        source.RaiseEvent(wheel);
        Assert.True(wheel.Handled);
        Assert.Null(ScrollAnimationHelper.GetCurrentVerticalAnimationTarget(inner));
        ScrollAnimationHelper.CancelVerticalAnimation(outer);
    }

    private static void CheckOwnershipBoundaries()
    {
        var source = new Border { Height = 2000 };
        var inner = new ModernScrollViewer { Content = source, Height = 100 };
        var boundary = new Border { Child = inner };
        ModernScroll.SetOwnership(boundary, ModernScrollOwnership.Self);
        var outer = new ModernScrollViewer { Content = boundary };
        Layout(outer);
        Assert.Equal(ModernScrollOwnership.Self, ModernScroll.GetOwnership(source));
        Assert.False(Skip(inner, source, -120)); // inherited Self cannot bypass the inner policy
        inner.ScrollToBottom();
        Layout(outer);
        Assert.True(Skip(outer, source, -120)); // explicit Self still fences off the Frame

        ModernScroll.SetOwnership(boundary, ModernScrollOwnership.Frame);
        Assert.False(Skip(outer, source, -120));
        inner.ScrollToTop();
        Layout(outer);
        Assert.True(Skip(outer, source, -120)); // inherited Frame cannot steal a scrollable child
    }

    private static void CheckPopupAndModifiers()
    {
        var owner = new ModernScrollViewer();
        var combo = new OpenComboBox();
        Assert.True(Skip(owner, combo, -120));
        Assert.True(Skip(owner, new Popup { PlacementTarget = owner }, -120));
        Assert.True(Skip(owner, new ContextMenu { PlacementTarget = owner }, -120));
        Assert.True(Skip(owner, owner, -120, ModifierKeys.Control));
        Assert.True(Skip(owner, owner, -120, ModifierKeys.Shift));
        Assert.False(Skip(owner, owner, -120, ModifierKeys.Alt));
        var handled = Wheel(owner, -120);
        handled.Handled = true;
        Assert.True(WheelScrollEventGuard.ShouldSkipSmoothScroll(owner, handled, owner, ModifierKeys.None));
    }

    private static void CheckNavigationTemplate(Application app)
    {
        var navigation = new ModernNavigationView();
        for (var i = 0; i < 80; i++)
        {
            navigation.MenuItems.Add(new Wpf.Ui.Controls.NavigationViewItem
            {
                Content = $"Item {i}", TargetPageType = typeof(ScrollTestPage)
            });
        }
        navigation.Measure(new Size(800, 300));
        navigation.Arrange(new Rect(0, 0, 800, 300));
        navigation.UpdateLayout();
        var list = (ListBox)navigation.FindName("PART_MenuList");
        var viewer = Descendant<ScrollViewer>(list)!;
        Assert.NotNull(viewer);
        Assert.True(ModernListScroll.GetIsEnabled(list));
        Assert.Equal(PanningMode.VerticalOnly, viewer.PanningMode);
        Assert.True(viewer.IsManipulationEnabled);
        Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
        Assert.True(viewer.CanContentScroll);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
        Assert.True(viewer.ScrollableHeight > 0);
        var presenter = Descendant<ScrollContentPresenter>(viewer)!;
        var wheel = Wheel(presenter, -1);
        presenter.RaiseEvent(wheel);
        Assert.True(wheel.Handled);
        var target = ScrollAnimationHelper.GetCurrentVerticalAnimationTarget(viewer);
        if (target.HasValue)
        {
            Assert.Equal(SystemParameters.WheelScrollLines < 0
                ? viewer.ViewportHeight / 120 : SystemParameters.WheelScrollLines * 16 / 120.0, target.Value, 6);
        }
        // Template-only unload must cancel too, even while the list remains alive.
        viewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, viewer));
        Assert.Null(ScrollAnimationHelper.GetCurrentVerticalAnimationTarget(viewer));
        list.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, list));
        navigation.NavigateEntryCommand.Execute(navigation.MenuEntries[3]);
        Assert.True(navigation.MenuEntries[3].IsSelected);
        Assert.False(navigation.MenuEntries[2].IsSelected);

        // Equivalent category/approval lists use the same production resource, not a copy.
        var category = new ListBox { Style = (Style)app.Resources["ModernScrollableListBoxStyle"] };
        Assert.Equal(PanningMode.VerticalOnly, ScrollViewer.GetPanningMode(category));
        Assert.True(ModernListScroll.GetIsEnabled(category));
    }

    private static void CheckAnimationCleanup()
    {
        var source = new Border { Height = 2000 };
        var viewer = new ModernScrollViewer { Content = source };
        Layout(viewer);
        source.RaiseEvent(Wheel(source, -120));
        viewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, viewer));
        Assert.Null(ScrollAnimationHelper.GetCurrentVerticalAnimationTarget(viewer));
        viewer.IsSmoothScrollingEnabled = false;
        source.RaiseEvent(Wheel(source, -120));
        Assert.Null(ScrollAnimationHelper.GetCurrentVerticalAnimationTarget(viewer));
    }

    private static bool Skip(ScrollViewer owner, DependencyObject source, int delta,
        ModifierKeys modifiers = ModifierKeys.None) =>
        WheelScrollEventGuard.ShouldSkipSmoothScroll(owner, Wheel(source, delta), source, modifiers);

    // A disconnected ComboBox normally coerces IsDropDownOpen to false. Simulate its
    // open state for routing tests without opening a real popup on the test desktop.
    private sealed class OpenComboBox : ComboBox
    {
        static OpenComboBox() => IsDropDownOpenProperty.OverrideMetadata(typeof(OpenComboBox),
            new FrameworkPropertyMetadata(true, null, (_, _) => true));
    }

    public sealed class ScrollTestPage : Page;

    private sealed class TestApp : Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    private static MouseWheelEventArgs Wheel(DependencyObject source, int delta) => new(Mouse.PrimaryDevice, 100, delta)
    {
        RoutedEvent = UIElement.PreviewMouseWheelEvent,
        Source = source
    };

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(800, 300));
        root.Arrange(new Rect(0, 0, 800, 300));
        root.UpdateLayout();
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is T nested) return nested;
        }
        return null;
    }
}
