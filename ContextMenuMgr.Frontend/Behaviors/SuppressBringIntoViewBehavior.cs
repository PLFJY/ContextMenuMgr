using System.Windows;

namespace ContextMenuMgr.Frontend.Behaviors;

/// <summary>
/// 拦截附加元素上的 <see cref="FrameworkElement.RequestBringIntoViewEvent"/> 路由事件，
/// 阻止其继续冒泡到外层导航滚动宿主。
/// </summary>
/// <remarks>
/// 适用于绑定到 <see cref="System.Windows.Data.CollectionView"/> 的 ListBox、ItemsControl 等列表控件：
/// 列表刷新（<c>ItemsView.Refresh()</c>）会重新生成 item 容器，WPF 框架可能在此期间引发
/// <see cref="FrameworkElement.RequestBringIntoViewEvent"/>（焦点恢复 / 选中项恢复）。
/// 列表内部的 ScrollViewer 在容器尚未完成布局时无法正确处理该事件，事件会冒泡到外层
/// <c>ModernScrollViewer</c>，导致外层滚动位置跳动（例如把页头筛选框滚出视野）。
/// 在列表控件上启用本行为后，事件在该层被标记为已处理，不再冒泡到外层；
/// 若列表内部有 ScrollViewer，它位于控件之下，其自身滚动行为不受影响。
/// </remarks>
public static class SuppressBringIntoViewBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SuppressBringIntoViewBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly RequestBringIntoViewEventHandler Handler = OnRequestBringIntoView;

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.OldValue)
        {
            element.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, Handler);
        }

        if ((bool)e.NewValue)
        {
            // handledEventsToo=true：无论内部 ScrollViewer 是否已处理，都阻止事件冒泡到外层滚动宿主。
            element.AddHandler(FrameworkElement.RequestBringIntoViewEvent, Handler, handledEventsToo: true);
        }
    }

    private static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }
}
