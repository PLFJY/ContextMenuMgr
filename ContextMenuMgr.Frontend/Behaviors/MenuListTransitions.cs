using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Behaviors;

/// <summary>Animates realized rows only; removed rows become noninteractive visual ghosts.</summary>
public static class MenuListTransitions
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MenuListTransitions), new PropertyMetadata(false, OnEnabled));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(MenuListTransitions));
    public static bool GetIsEnabled(DependencyObject owner) => (bool)owner.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject owner, bool value) => owner.SetValue(IsEnabledProperty, value);

    private static void OnEnabled(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not ItemsControl list) return;
        if (list.GetValue(StateProperty) is State old) old.Dispose();
        list.ClearValue(StateProperty);
        if (e.NewValue is true) list.SetValue(StateProperty, new State(list));
    }

    private sealed class State : IDisposable
    {
        private readonly ItemsControl _list;
        private readonly Dictionary<object, Row> _rows = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<FrameworkElement> _animated = [];
        private readonly List<(AdornerLayer Layer, Ghost Ghost, string? Key)> _ghosts = [];
        private readonly HashSet<object> _added = new(ReferenceEqualityComparer.Instance);
        private bool _active;
        private bool _changed;
        private bool _warm;
        private DispatcherOperation? _layout;
        private readonly List<ScrollViewer> _scrollHosts = [];
        private bool _viewportChanged = true;
        private Size _lastSize;

        public State(ItemsControl list)
        {
            _list = list;
            list.Loaded += Loaded;
            list.Unloaded += Unloaded;
            if (list.IsLoaded) Attach();
        }
        private bool Animate => SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;
        private void Loaded(object sender, RoutedEventArgs e) => Attach();
        private void Attach()
        {
            if (_active) return;
            _active = true;
            ((INotifyCollectionChanged)_list.Items).CollectionChanged += Changed;
            _list.LayoutUpdated += LayoutUpdated;
            _list.ItemContainerGenerator.StatusChanged += ContainersChanged;
            for (var ancestor = VisualTreeHelper.GetParent(_list); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ScrollViewer scroll) { _scrollHosts.Add(scroll); scroll.ScrollChanged += Scrolled; }
            QueueLayout();
        }
        private void Unloaded(object sender, RoutedEventArgs e) => Detach();
        private void Detach()
        {
            if (!_active) return;
            _active = false;
            ((INotifyCollectionChanged)_list.Items).CollectionChanged -= Changed;
            _list.LayoutUpdated -= LayoutUpdated;
            _list.ItemContainerGenerator.StatusChanged -= ContainersChanged;
            foreach (var scroll in _scrollHosts) scroll.ScrollChanged -= Scrolled;
            _scrollHosts.Clear();
            _layout?.Abort(); _layout = null;
            foreach (var element in _animated.ToArray()) Reset(element);
            foreach (var ghost in _ghosts.ToArray())
            { ghost.Ghost.BeginAnimation(UIElement.OpacityProperty, null); ghost.Layer.Remove(ghost.Ghost); }
            _ghosts.Clear(); _rows.Clear(); _added.Clear(); _warm = false; _changed = false;
        }
        private void Changed(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _changed = true;
            if (_warm && Animate && e.Action != NotifyCollectionChangedAction.Reset)
            {
                if (e.OldItems is not null && e.Action != NotifyCollectionChangedAction.Move)
                    foreach (var item in e.OldItems)
                        if (item is not null && _rows.TryGetValue(item, out var row)) AddGhost(row, Key(item));
                if (e.NewItems is not null && e.Action != NotifyCollectionChangedAction.Move)
                    foreach (var item in e.NewItems)
                    {
                        if (item is null) continue;
                        _added.Add(item);
                        var key = Key(item);
                        foreach (var ghost in _ghosts.Where(ghost => key is not null && ghost.Key == key).ToArray())
                        { ghost.Ghost.BeginAnimation(UIElement.OpacityProperty, null); ghost.Layer.Remove(ghost.Ghost); _ghosts.Remove(ghost); }
                    }
            }
            QueueLayout();
        }
        private void QueueLayout()
        {
            if (_layout is { Status: DispatcherOperationStatus.Pending }) return;
            _layout = _list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            { _layout = null; if (_active) Capture(); }));
        }
        private void LayoutUpdated(object? sender, EventArgs e) => Capture();
        private void Scrolled(object sender, ScrollChangedEventArgs e)
        { _viewportChanged = true; QueueLayout(); }
        private void ContainersChanged(object? sender, EventArgs e)
        { _viewportChanged = true; QueueLayout(); }
        private void Capture()
        {
            if (!_list.IsVisible || _warm && !_changed && !_viewportChanged && _lastSize == _list.RenderSize) return;
            _viewportChanged = false; _lastSize = _list.RenderSize;
            var current = new Dictionary<object, Row>(ReferenceEqualityComparer.Instance);
            // Walk realized presenters rather than every logical item in a virtualized list.
            foreach (var container in Containers(_list))
            {
                var item = _list.ItemContainerGenerator.ItemFromContainer(container);
                if (item == DependencyProperty.UnsetValue || item is null || container.ActualWidth <= 0 || container.ActualHeight <= 0 || !InViewport(container)) continue;
                var position = container.TranslatePoint(default, _list);
                var bounds = new Rect(position, container.RenderSize);
                var row = new Row(container, bounds);
                current[item] = row;
                // A recycled container must never carry the old row's animation.
                if (_rows.Any(pair => ReferenceEquals(pair.Value.Element, container) && !ReferenceEquals(pair.Key, item))) Reset(container);
                if (!_changed || !_warm || !Animate) continue;
                if (_added.Count <= 32 && _added.Contains(item)) AnimateRow(container, 12, entrance: true);
                else if (_rows.TryGetValue(item, out var previous) && ReferenceEquals(previous.Element, container))
                {
                    var offset = previous.Bounds.Y - bounds.Y;
                    if (Math.Abs(offset) > 0.5 && Math.Abs(offset) < Math.Max(_list.ActualHeight, 1)) AnimateRow(container, offset, entrance: false);
                }
            }
            _rows.Clear(); foreach (var row in current) _rows.Add(row.Key, row.Value);
            _added.Clear(); _changed = false; _warm = true;
        }
        private static IEnumerable<FrameworkElement> Containers(DependencyObject node)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            {
                var child = VisualTreeHelper.GetChild(node, index);
                if (child is FrameworkElement element && ItemsControl.ItemsControlFromItemContainer(element) is not null)
                { yield return element; continue; }
                foreach (var descendant in Containers(child)) yield return descendant;
            }
        }
        private static bool InViewport(FrameworkElement element)
        {
            DependencyObject? ancestor = VisualTreeHelper.GetParent(element);
            while (ancestor is not null)
            {
                if (ancestor is ScrollContentPresenter viewport)
                {
                    var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
                    if (!bounds.IntersectsWith(new Rect(viewport.RenderSize))) return false;
                }
                ancestor = VisualTreeHelper.GetParent(ancestor);
            }
            return true;
        }
        private static string? Key(object item) => item switch
        {
            ContextMenuItemViewModel row => row.Id,
            SpecialMenuItemViewModel row => row.Id,
            Windows11ContextMenuItemViewModel row => row.IsSystemCommand ? row.Id : row.PackageFamilyName,
            ApplicationGroupViewModel row => row.Identity,
            ApprovalQueueItemViewModel row => ContextMenuApprovalIdentity.CreateLogicalItemKey(row.PrimaryItem.Entry),
            OpenWithApprovalItemViewModel row => row.Id,
            ShellSubMenuItemViewModel row => row.Item.Id,
            _ => null
        };

        private void AnimateRow(FrameworkElement element, double offset, bool entrance)
        {
            // Do not replace transforms owned by templates or other behaviors.
            if (element.RenderTransform is not MatrixTransform { Matrix.IsIdentity: true }
                && !_animated.Contains(element)) return;
            Reset(element);
            var transform = new TranslateTransform();
            element.SetCurrentValue(UIElement.RenderTransformProperty, transform);
            _animated.Add(element);
            var animation = new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            animation.Completed += (_, _) => { if (_animated.Contains(element) && ReferenceEquals(element.RenderTransform, transform)) Reset(element); };
            transform.BeginAnimation(TranslateTransform.YProperty, animation);
            if (entrance) element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        }
        private void Reset(FrameworkElement element)
        {
            if (!_animated.Remove(element)) return;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (element.RenderTransform is TranslateTransform transform) transform.BeginAnimation(TranslateTransform.YProperty, null);
            element.SetCurrentValue(UIElement.RenderTransformProperty, Transform.Identity);
        }
        private void AddGhost(Row row, string? key)
        {
            if (_ghosts.Count >= 32 || row.Bounds.Width > 4096 || row.Bounds.Height > 4096) return;
            var layer = AdornerLayer.GetAdornerLayer(_list);
            if (layer is null || !row.Element.IsVisible) return;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(row.Element);
                var pixelWidth = Math.Max(1, (int)Math.Ceiling(row.Bounds.Width * dpi.DpiScaleX));
                var pixelHeight = Math.Max(1, (int)Math.Ceiling(row.Bounds.Height * dpi.DpiScaleY));
                if ((long)pixelWidth * pixelHeight > 4 * 1024 * 1024) return;
                var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(row.Element); bitmap.Freeze();
                var clip = new Rect(_list.RenderSize);
                for (var ancestor = VisualTreeHelper.GetParent(_list); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                    if (ancestor is ScrollContentPresenter viewport)
                        clip.Intersect(viewport.TransformToDescendant(_list).TransformBounds(new Rect(viewport.RenderSize)));
                if (clip.IsEmpty) return;
                var ghost = new Ghost(_list, bitmap, row.Bounds) { Clip = new RectangleGeometry(clip), IsHitTestVisible = false };
                layer.Add(ghost); _ghosts.Add((layer, ghost, key));
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
                fade.Completed += (_, _) => { layer.Remove(ghost); _ghosts.Remove((layer, ghost, key)); };
                ghost.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { Services.FrontendDebugLog.Warning(nameof(MenuListTransitions), $"List transition skipped: {ex.Message}"); }
        }
        public void Dispose() { Detach(); _list.Loaded -= Loaded; _list.Unloaded -= Unloaded; }
        private sealed record Row(FrameworkElement Element, Rect Bounds);
    }
    private sealed class Ghost(UIElement owner, ImageSource image, Rect bounds) : Adorner(owner)
    {
        protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawImage(image, bounds);
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }
}
