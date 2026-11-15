using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ContextMenuMgr.Frontend.Controls.Modern.Frame;
using ContextMenuMgr.Frontend.Controls.Modern.Scrolling;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Animations;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Controls.Modern.Navigation;

public partial class ModernNavigationView : UserControl, INavigationView
{
    public static readonly DependencyProperty HeaderProperty = Register(nameof(Header), typeof(object));
    public static readonly DependencyProperty HeaderVisibilityProperty = Register(nameof(HeaderVisibility), typeof(Visibility), Visibility.Collapsed);
    public static readonly DependencyProperty AlwaysShowHeaderProperty = Register(nameof(AlwaysShowHeader), typeof(bool), false);
    public static readonly DependencyProperty MenuItemsSourceProperty = Register(nameof(MenuItemsSource), typeof(object), null, OnMenuItemsSourceChanged);
    public static readonly DependencyProperty FooterMenuItemsSourceProperty = Register(nameof(FooterMenuItemsSource), typeof(object), null, OnFooterMenuItemsSourceChanged);
    public static readonly DependencyProperty IsTopSeparatorVisibleProperty = Register(nameof(IsTopSeparatorVisible), typeof(bool), true);
    public static readonly DependencyProperty IsFooterSeparatorVisibleProperty = Register(nameof(IsFooterSeparatorVisible), typeof(bool), true);
    public static readonly DependencyProperty ContentOverlayProperty = Register(nameof(ContentOverlay), typeof(object));
    private static readonly DependencyPropertyKey IsBackEnabledPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsBackEnabled), typeof(bool), typeof(ModernNavigationView), new PropertyMetadata(false));
    public static readonly DependencyProperty IsBackEnabledProperty = IsBackEnabledPropertyKey.DependencyProperty;
    public static readonly DependencyProperty IsBackButtonVisibleProperty = Register(
        nameof(IsBackButtonVisible), typeof(NavigationViewBackButtonVisible), NavigationViewBackButtonVisible.Auto);
    public static readonly DependencyProperty IsPaneToggleVisibleProperty = Register(nameof(IsPaneToggleVisible), typeof(bool), true);
    public static readonly DependencyProperty IsPaneOpenProperty = Register(nameof(IsPaneOpen), typeof(bool), true, OnIsPaneOpenChanged);
    public static readonly DependencyProperty IsPaneVisibleProperty = Register(nameof(IsPaneVisible), typeof(bool), true);
    public static readonly DependencyProperty OpenPaneLengthProperty = Register(nameof(OpenPaneLength), typeof(double), 200D, OnPaneLengthChanged);
    public static readonly DependencyProperty CompactPaneLengthProperty = Register(nameof(CompactPaneLength), typeof(double), 48D, OnPaneLengthChanged);
    public static readonly DependencyProperty ActualPaneLengthProperty = Register(
        nameof(ActualPaneLength), typeof(double), 200D);
    public static readonly DependencyProperty PaneHeaderProperty = Register(nameof(PaneHeader), typeof(object));
    public static readonly DependencyProperty PaneTitleProperty = Register(nameof(PaneTitle), typeof(string));
    public static readonly DependencyProperty PaneFooterProperty = Register(nameof(PaneFooter), typeof(object));
    public static readonly DependencyProperty PaneDisplayModeProperty = Register(
        nameof(PaneDisplayMode), typeof(NavigationViewPaneDisplayMode), NavigationViewPaneDisplayMode.Left);
    public static readonly DependencyProperty AutoSuggestBoxProperty = Register(nameof(AutoSuggestBox), typeof(AutoSuggestBox));
    public static readonly DependencyProperty TitleBarProperty = Register(nameof(TitleBar), typeof(TitleBar));
    public static readonly DependencyProperty BreadcrumbBarProperty = Register(nameof(BreadcrumbBar), typeof(BreadcrumbBar));
    public static readonly DependencyProperty ItemTemplateProperty = Register(nameof(ItemTemplate), typeof(ControlTemplate));
    public static readonly DependencyProperty TransitionDurationProperty = Register(
        nameof(TransitionDuration), typeof(int), 450, OnTransitionDurationChanged);
    public static readonly DependencyProperty TransitionProperty = Register(
        nameof(Transition), typeof(Transition), Transition.FadeInWithSlide, OnTransitionChanged);
    public static readonly DependencyProperty FrameMarginProperty = Register(nameof(FrameMargin), typeof(Thickness), default(Thickness));

    private readonly ObservableCollection<object> _menuItems = [];
    private readonly ObservableCollection<object> _footerMenuItems = [];
    private readonly Stack<ModernNavigationEntry?> _selectedEntryBackStack = [];
    private INotifyCollectionChanged? _menuItemsSourceCollection;
    private INotifyCollectionChanged? _footerItemsSourceCollection;
    private ModernNavigationEntry? _selectedEntry;
    private bool _isGoingBack;
    private bool _rebuildPending;

    public ModernNavigationView()
    {
        InitializeComponent();
        NavigateEntryCommand = new ModernNavigationCommand(parameter =>
        {
            if (parameter is ModernNavigationEntry entry)
            {
                InvokeEntry(entry);
            }
        });

        _menuItems.CollectionChanged += OnMenuItemsChanged;
        _footerMenuItems.CollectionChanged += OnFooterMenuItemsChanged;
        DataContextChanged += OnNavigationDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SetCurrentValue(ActualPaneLengthProperty, GetTargetPaneLength());
        PART_Frame.TransitionDuration = TimeSpan.FromMilliseconds(TransitionDuration);
        PART_Frame.DefaultTransitionInfo = CreateTransitionInfo(Transition);
        PART_Frame.NavigationCompleted += OnFrameNavigationCompleted;
    }

    public ObservableCollection<ModernNavigationEntry> MenuEntries { get; } = [];

    public ObservableCollection<ModernNavigationEntry> FooterMenuEntries { get; } = [];

    public System.Windows.Input.ICommand NavigateEntryCommand { get; }

    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public Visibility HeaderVisibility { get => (Visibility)GetValue(HeaderVisibilityProperty); set => SetValue(HeaderVisibilityProperty, value); }
    public bool AlwaysShowHeader { get => (bool)GetValue(AlwaysShowHeaderProperty); set => SetValue(AlwaysShowHeaderProperty, value); }
    public IList MenuItems => _menuItems;
    public object? MenuItemsSource { get => GetValue(MenuItemsSourceProperty); set => SetValue(MenuItemsSourceProperty, value); }
    public IList FooterMenuItems => _footerMenuItems;
    public object? FooterMenuItemsSource { get => GetValue(FooterMenuItemsSourceProperty); set => SetValue(FooterMenuItemsSourceProperty, value); }
    public bool IsTopSeparatorVisible { get => (bool)GetValue(IsTopSeparatorVisibleProperty); set => SetValue(IsTopSeparatorVisibleProperty, value); }
    public bool IsFooterSeparatorVisible { get => (bool)GetValue(IsFooterSeparatorVisibleProperty); set => SetValue(IsFooterSeparatorVisibleProperty, value); }
    public INavigationViewItem? SelectedItem => _selectedEntry?.SourceNavigationViewItem;
    public object? ContentOverlay { get => GetValue(ContentOverlayProperty); set => SetValue(ContentOverlayProperty, value); }
    public bool IsBackEnabled => (bool)GetValue(IsBackEnabledProperty);
    public NavigationViewBackButtonVisible IsBackButtonVisible { get => (NavigationViewBackButtonVisible)GetValue(IsBackButtonVisibleProperty); set => SetValue(IsBackButtonVisibleProperty, value); }
    public bool IsPaneToggleVisible { get => (bool)GetValue(IsPaneToggleVisibleProperty); set => SetValue(IsPaneToggleVisibleProperty, value); }
    public bool IsPaneOpen { get => (bool)GetValue(IsPaneOpenProperty); set => SetValue(IsPaneOpenProperty, value); }
    public bool IsPaneVisible { get => (bool)GetValue(IsPaneVisibleProperty); set => SetValue(IsPaneVisibleProperty, value); }
    public double OpenPaneLength { get => (double)GetValue(OpenPaneLengthProperty); set => SetValue(OpenPaneLengthProperty, value); }
    public double CompactPaneLength { get => (double)GetValue(CompactPaneLengthProperty); set => SetValue(CompactPaneLengthProperty, value); }
    public double ActualPaneLength
    {
        get => (double)GetValue(ActualPaneLengthProperty);
        private set => SetValue(ActualPaneLengthProperty, value);
    }
    public object? PaneHeader { get => GetValue(PaneHeaderProperty); set => SetValue(PaneHeaderProperty, value); }
    public string? PaneTitle { get => (string?)GetValue(PaneTitleProperty); set => SetValue(PaneTitleProperty, value); }
    public object? PaneFooter { get => GetValue(PaneFooterProperty); set => SetValue(PaneFooterProperty, value); }
    public NavigationViewPaneDisplayMode PaneDisplayMode { get => (NavigationViewPaneDisplayMode)GetValue(PaneDisplayModeProperty); set => SetValue(PaneDisplayModeProperty, value); }
    public AutoSuggestBox? AutoSuggestBox { get => (AutoSuggestBox?)GetValue(AutoSuggestBoxProperty); set => SetValue(AutoSuggestBoxProperty, value); }
    public TitleBar? TitleBar { get => (TitleBar?)GetValue(TitleBarProperty); set => SetValue(TitleBarProperty, value); }
    public BreadcrumbBar? BreadcrumbBar { get => (BreadcrumbBar?)GetValue(BreadcrumbBarProperty); set => SetValue(BreadcrumbBarProperty, value); }
    public ControlTemplate? ItemTemplate { get => (ControlTemplate?)GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    public int TransitionDuration { get => (int)GetValue(TransitionDurationProperty); set => SetValue(TransitionDurationProperty, value); }
    public Transition Transition { get => (Transition)GetValue(TransitionProperty); set => SetValue(TransitionProperty, value); }
    public Thickness FrameMargin { get => (Thickness)GetValue(FrameMarginProperty); set => SetValue(FrameMarginProperty, value); }
    public bool CanGoBack => PART_Frame.CanGoBack;
    public FrameworkElement? CurrentContent => PART_Frame.CurrentContent;
    public ModernScrollViewer ContentScrollHost => PART_Frame.ContentScrollHost;

    public event TypedEventHandler<NavigationView, RoutedEventArgs>? PaneOpened;
    public event TypedEventHandler<NavigationView, RoutedEventArgs>? PaneClosed;
    public event TypedEventHandler<NavigationView, RoutedEventArgs>? SelectionChanged;
    public event TypedEventHandler<NavigationView, RoutedEventArgs>? ItemInvoked;
    public event TypedEventHandler<NavigationView, RoutedEventArgs>? BackRequested;
    public event TypedEventHandler<NavigationView, NavigatingCancelEventArgs>? Navigating;
    public event TypedEventHandler<NavigationView, NavigatedEventArgs>? Navigated;
    public event EventHandler<ModernFrameNavigationEventArgs>? NavigationCompleted;

    public bool Navigate(Type pageType, object? dataContext = null)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        var entry = AllEntries()
            .Where(candidate => candidate.TargetPageType == pageType)
            .OrderByDescending(candidate => candidate.Depth)
            .FirstOrDefault();
        return NavigatePage(pageType, dataContext, entry);
    }

    public bool Navigate(string pageIdOrTargetTag, object? dataContext = null)
    {
        if (string.IsNullOrWhiteSpace(pageIdOrTargetTag))
        {
            return false;
        }

        var entry = AllEntries().FirstOrDefault(candidate =>
            string.Equals(candidate.TargetPageTag, pageIdOrTargetTag, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.SourceNavigationViewItem?.Id, pageIdOrTargetTag, StringComparison.OrdinalIgnoreCase)
            || string.Equals((candidate.SourceItem as FrameworkElement)?.Tag?.ToString(), pageIdOrTargetTag, StringComparison.OrdinalIgnoreCase));
        return entry?.TargetPageType is { } pageType && NavigatePage(pageType, dataContext, entry);
    }

    public bool NavigateWithHierarchy(Type pageType, object? dataContext = null) => Navigate(pageType, dataContext);

    public bool ReplaceContent(Type pageTypeToEmbed) => PART_Frame.Navigate(pageTypeToEmbed);

    public bool ReplaceContent(UIElement pageInstanceToEmbed, object? dataContext = null)
    {
        if (pageInstanceToEmbed is not FrameworkElement element)
        {
            return false;
        }

        if (dataContext is not null)
        {
            element.DataContext = dataContext;
        }

        return PART_Frame.Navigate(element);
    }

    public bool GoForward() => PART_Frame.GoForward();

    public bool GoBack()
    {
        _isGoingBack = true;
        try
        {
            if (!PART_Frame.GoBack())
            {
                return false;
            }

            SetSelectedEntry(_selectedEntryBackStack.Count > 0 ? _selectedEntryBackStack.Pop() : null, false);
            SetValue(IsBackEnabledPropertyKey, PART_Frame.CanGoBack);
            BackRequested?.Invoke(null!, new RoutedEventArgs());
            if (CurrentContent is { } content)
            {
                Navigated?.Invoke(null!, new NavigatedEventArgs(System.Windows.Controls.Button.ClickEvent, this) { Page = content });
            }
            return true;
        }
        finally
        {
            _isGoingBack = false;
        }
    }

    public void ClearJournal()
    {
        PART_Frame.ClearJournal();
        _selectedEntryBackStack.Clear();
        SetValue(IsBackEnabledPropertyKey, false);
    }

    public void SetPageProviderService(INavigationViewPageProvider navigationViewPageProvider)
    {
        ArgumentNullException.ThrowIfNull(navigationViewPageProvider);
        PART_Frame.SetPageProvider(navigationViewPageProvider);
    }

    public void SetServiceProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        PART_Frame.ServiceProvider = serviceProvider;
    }

    private void InvokeEntry(ModernNavigationEntry entry)
    {
        if (!entry.IsEnabled)
        {
            return;
        }

        if (entry.HasChildren)
        {
            entry.SetExpanded(!entry.IsExpanded);

            // A hierarchical item is a group first. In ContextMenuMgr the legacy group also
            // carries FileContextMenuPage for WPF-UI compatibility; its child owns navigation.
            if (HasNavigableDescendant(entry))
            {
                ItemInvoked?.Invoke(null!, new RoutedEventArgs());
                return;
            }
        }

        if (entry.TargetPageType is not null)
        {
            if (IsCurrentEntry(entry))
            {
                return;
            }

            ItemInvoked?.Invoke(null!, new RoutedEventArgs());
            NavigatePage(entry.TargetPageType, null, entry);
        }
    }

    private bool NavigatePage(Type pageType, object? dataContext, ModernNavigationEntry? entry)
    {
        if (IsCurrentPage(pageType) || entry is not null && IsCurrentEntry(entry))
        {
            return false;
        }

        var creationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var page = PART_Frame.CreatePage(pageType, dataContext);
        ContextMenuMgr.Frontend.Services.FrontendDebugLog.Info(nameof(ModernNavigationView),
            $"NavigationPageCreated: Page={pageType.Name}, ElapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(creationStarted).TotalMilliseconds:F1}.");

        var navigatingArgs = new NavigatingCancelEventArgs(System.Windows.Controls.Button.ClickEvent, this) { Page = page };
        Navigating?.Invoke(null!, navigatingArgs);
        if (navigatingArgs.Cancel)
        {
            return false;
        }

        ExpandAncestors(entry);
        if (!PART_Frame.Navigate(page))
        {
            return false;
        }

        SetSelectedEntry(entry, !_isGoingBack);
        SetValue(IsBackEnabledPropertyKey, PART_Frame.CanGoBack);
        Navigated?.Invoke(null!, new NavigatedEventArgs(System.Windows.Controls.Button.ClickEvent, this) { Page = page });
        return true;
    }

    private bool IsCurrentEntry(ModernNavigationEntry entry)
    {
        if (!IsCurrentPage(entry.TargetPageType))
        {
            return false;
        }

        return ReferenceEquals(_selectedEntry, entry)
            || ReferenceEquals(_selectedEntry?.SourceItem, entry.SourceItem)
            || (_selectedEntry?.TargetPageType is { } selectedType
                && selectedType == entry.TargetPageType);
    }

    private bool IsCurrentPage(Type? pageType) =>
        pageType is not null
        && PART_Frame.CurrentContent is { } content
        && (content.GetType() == pageType || pageType.IsInstanceOfType(content));

    private static bool HasNavigableDescendant(ModernNavigationEntry entry) =>
        entry.Children.Any(child => child.TargetPageType is not null || HasNavigableDescendant(child));

    private void OnFrameNavigationCompleted(object? sender, ModernFrameNavigationEventArgs e) =>
        NavigationCompleted?.Invoke(this, e);

    private void SetSelectedEntry(ModernNavigationEntry? entry, bool addCurrentToBackStack)
    {
        if (ReferenceEquals(_selectedEntry, entry))
        {
            return;
        }

        if (addCurrentToBackStack && _selectedEntry is not null)
        {
            _selectedEntryBackStack.Push(_selectedEntry);
        }

        foreach (var candidate in AllEntries())
        {
            candidate.IsSelected = ReferenceEquals(candidate, entry);
            candidate.IsActiveBranch = entry is not null && IsAncestorOf(candidate, entry);
            candidate.SourceNavigationViewItem?.SetCurrentValue(
                NavigationViewItem.IsActiveProperty,
                candidate.IsSelected || candidate.IsActiveBranch);
        }

        _selectedEntry = entry;
        SelectionChanged?.Invoke(null!, new RoutedEventArgs());
    }

    private static bool IsAncestorOf(ModernNavigationEntry candidate, ModernNavigationEntry entry)
    {
        for (var parent = entry.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static void ExpandAncestors(ModernNavigationEntry? entry)
    {
        for (var parent = entry?.Parent; parent is not null; parent = parent.Parent)
        {
            parent.SetExpanded(true);
        }
    }

    private IEnumerable<ModernNavigationEntry> AllEntries() => MenuEntries.Concat(FooterMenuEntries);

    private void RebuildEntries()
    {
        _rebuildPending = false;
        var selectedSource = _selectedEntry?.SourceItem;
        DisposeRootEntries(MenuEntries);
        DisposeRootEntries(FooterMenuEntries);
        MenuEntries.Clear();
        FooterMenuEntries.Clear();

        foreach (var entry in ModernNavigationEntryCollectionBuilder.Build(
                     EnumerateSource(MenuItemsSource, _menuItems), false, DataContext, OnEntryRebuildRequested))
        {
            MenuEntries.Add(entry);
        }

        foreach (var entry in ModernNavigationEntryCollectionBuilder.Build(
                     EnumerateSource(FooterMenuItemsSource, _footerMenuItems), true, DataContext, OnEntryRebuildRequested))
        {
            FooterMenuEntries.Add(entry);
        }

        var selected = selectedSource is null
            ? null
            : AllEntries().FirstOrDefault(entry => ReferenceEquals(entry.SourceItem, selectedSource));
        _selectedEntry = null;
        SetSelectedEntry(selected, false);
    }

    private void OnEntryRebuildRequested(object? sender, EventArgs e)
    {
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        Dispatcher.BeginInvoke(RebuildEntries, DispatcherPriority.DataBind);
    }

    private static void DisposeRootEntries(IEnumerable<ModernNavigationEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.Parent is null))
        {
            entry.Dispose();
        }
    }

    private static IEnumerable EnumerateSource(object? itemsSource, ObservableCollection<object> fallback) =>
        itemsSource is IEnumerable enumerable and not string ? enumerable : fallback;

    private void OnMenuItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildEntries();
    private void OnFooterMenuItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildEntries();

    private static void OnMenuItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ModernNavigationView)d).AttachSourceCollection(e.NewValue, false);

    private static void OnFooterMenuItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ModernNavigationView)d).AttachSourceCollection(e.NewValue, true);

    private void AttachSourceCollection(object? source, bool footer)
    {
        var previous = footer ? _footerItemsSourceCollection : _menuItemsSourceCollection;
        if (previous is not null)
        {
            previous.CollectionChanged -= OnItemsSourceCollectionChanged;
        }

        var current = source as INotifyCollectionChanged;
        if (footer)
        {
            _footerItemsSourceCollection = current;
        }
        else
        {
            _menuItemsSourceCollection = current;
        }

        if (current is not null)
        {
            current.CollectionChanged += OnItemsSourceCollectionChanged;
        }

        RebuildEntries();
    }

    private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildEntries();

    private void OnNavigationDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        foreach (var entry in AllEntries())
        {
            if (entry.SourceItem is FrameworkElement element
                && (ReferenceEquals(element.DataContext, e.OldValue) || element.DataContext is null))
            {
                element.SetCurrentValue(DataContextProperty, e.NewValue);
            }
        }

        RebuildEntries();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RebuildEntries();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DisposeRootEntries(MenuEntries);
        DisposeRootEntries(FooterMenuEntries);
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e) => GoBack();

    private static void OnIsPaneOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ModernNavigationView)d;
        view.AnimatePaneLength();
        if ((bool)e.NewValue)
        {
            view.PaneOpened?.Invoke(null!, new RoutedEventArgs());
        }
        else
        {
            view.PaneClosed?.Invoke(null!, new RoutedEventArgs());
        }
    }

    private static void OnPaneLengthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ModernNavigationView)d;
        view.AnimatePaneLength();
    }

    private static void OnTransitionDurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ModernNavigationView)d).PART_Frame.TransitionDuration = TimeSpan.FromMilliseconds(Math.Max(0, (int)e.NewValue));

    private static void OnTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ModernNavigationView)d).PART_Frame.DefaultTransitionInfo = CreateTransitionInfo((Transition)e.NewValue);

    private void AnimatePaneLength()
    {
        var from = ActualPaneLength;
        var target = GetTargetPaneLength();
        SetCurrentValue(ActualPaneLengthProperty, target);
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(ActualPaneLengthProperty, null);
            return;
        }

        BeginAnimation(
            ActualPaneLengthProperty,
            new System.Windows.Media.Animation.DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            });
    }

    private static ContextMenuMgr.Frontend.Controls.Modern.Frame.ModernNavigationTransitionInfo CreateTransitionInfo(Transition transition) =>
        transition switch
        {
            Transition.None => new ContextMenuMgr.Frontend.Controls.Modern.Frame.SuppressNavigationTransitionInfo(),
            Transition.FadeIn => new ContextMenuMgr.Frontend.Controls.Modern.Frame.FadeNavigationTransitionInfo(),
            Transition.SlideBottom => new ContextMenuMgr.Frontend.Controls.Modern.Frame.SlideNavigationTransitionInfo
            {
                Effect = ContextMenuMgr.Frontend.Controls.Modern.Frame.SlideNavigationTransitionEffect.FromBottom
            },
            Transition.SlideRight => new ContextMenuMgr.Frontend.Controls.Modern.Frame.SlideNavigationTransitionInfo
            {
                Effect = ContextMenuMgr.Frontend.Controls.Modern.Frame.SlideNavigationTransitionEffect.FromRight
            },
            Transition.SlideLeft => new ContextMenuMgr.Frontend.Controls.Modern.Frame.SlideNavigationTransitionInfo
            {
                Effect = ContextMenuMgr.Frontend.Controls.Modern.Frame.SlideNavigationTransitionEffect.FromLeft
            },
            _ => new ContextMenuMgr.Frontend.Controls.Modern.Frame.EntranceNavigationTransitionInfo()
        };

    private double GetTargetPaneLength() => IsPaneOpen
        ? Math.Max(CompactPaneLength, OpenPaneLength)
        : Math.Max(40, CompactPaneLength);

    private static DependencyProperty Register(
        string name,
        Type propertyType,
        object? defaultValue = null,
        PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(name, propertyType, typeof(ModernNavigationView), new PropertyMetadata(defaultValue, callback));
}
