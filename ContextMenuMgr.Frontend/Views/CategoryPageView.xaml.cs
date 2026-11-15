using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ContextMenuMgr.Frontend.Controls.Modern.Frame;
using ContextMenuMgr.Frontend.Controls.Modern.Scrolling;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views.Pages;
using Wpf.Ui;

namespace ContextMenuMgr.Frontend.Views;

/// <summary>
/// Represents the category Page View.
/// </summary>
public partial class CategoryPageView : Page, INavigationScrollOwner
{
    private ScrollViewer? _scrollViewer;
    private double _savedOffset;
    /// <summary>
    /// Initializes a new instance of the <see cref="CategoryPageView"/> class.
    /// </summary>
    public CategoryPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ComposeContent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        PreviewKeyDown += (_, e) =>
        {
            if (_scrollViewer is not null && e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
                ScrollAnimationHelper.CancelVerticalAnimation(_scrollViewer);
        };
    }

    private void ComposeContent()
    {
        if (DataContext is not CategoryPageViewModel viewModel) { MenuList.ItemsSource = null; return; }
        MenuList.ItemsSource = new CompositeCollection
        {
            viewModel,
            new CollectionContainer { Collection = viewModel.ItemsView }
        };
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        MenuList.ApplyTemplate();
        _scrollViewer = MenuList.Template.FindName("PART_ScrollViewer", MenuList) as ScrollViewer;
        if (_scrollViewer is null) return;
        _scrollViewer.ScrollChanged += OnScrollChanged;
        UpdateScrollButtons();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CategoryPageViewModel viewModel) viewModel.CancelPendingSearch();
        if (_scrollViewer is null) return;
        _scrollViewer.ScrollChanged -= OnScrollChanged;
        ScrollAnimationHelper.CancelVerticalAnimation(_scrollViewer);
        _scrollViewer = null;
    }

    public void OnNavigatingAway()
    {
        if (_scrollViewer is not null) _savedOffset = _scrollViewer.VerticalOffset;
    }

    public void OnNavigated(ModernFrameNavigationMode mode)
    {
        if (DataContext is CategoryPageViewModel viewModel) viewModel.RefreshSearchImmediately();
        MenuList.UpdateLayout();
        if (_scrollViewer is not null)
        {
            ScrollAnimationHelper.CancelVerticalAnimation(_scrollViewer);
            _scrollViewer.ScrollToVerticalOffset(mode == ModernFrameNavigationMode.Back ? _savedOffset : 0);
        }
        UpdateScrollButtons();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateScrollButtons();

    private void UpdateScrollButtons()
    {
        var scrollable = _scrollViewer is { ScrollableHeight: > 1 };
        ScrollTopButton.Visibility = scrollable && _scrollViewer!.VerticalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        ScrollBottomButton.Visibility = scrollable && _scrollViewer!.ScrollableHeight - _scrollViewer.VerticalOffset > 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollTop_Click(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer is not null)
            ScrollAnimationHelper.SmoothScrollToVerticalOffset(_scrollViewer, 0, TimeSpan.FromMilliseconds(300));
    }

    private void ScrollBottom_Click(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer is not null)
            ScrollAnimationHelper.SmoothScrollToVerticalOffset(_scrollViewer, double.PositiveInfinity, TimeSpan.FromMilliseconds(300));
    }

    private void OpenApplicationGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ContextMenuItemViewModel item }
            || Application.Current is not App app
            || app.TryGetService<GlobalSearchNavigationFilterService>() is not { } filterService
            || app.TryGetService<INavigationService>() is not { } navigationService)
        {
            return;
        }

        filterService.RequestFilter(
            typeof(ApplicationGroupsPage),
            item.Category,
            isWindows11: false,
            filterText: item.DisplayName,
            item.Id);
        navigationService.Navigate(typeof(ApplicationGroupsPage));
    }
}
