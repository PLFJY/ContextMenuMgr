using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using ContextMenuMgr.Frontend.Controls.Modern.Scrolling;
using ContextMenuMgr.Frontend.Views;
using Wpf.Ui.Abstractions;

namespace ContextMenuMgr.Frontend.Controls.Modern.Frame;

[TemplatePart(Name = "PART_OldContent", Type = typeof(ContentPresenter))]
[TemplatePart(Name = "PART_NewContent", Type = typeof(ContentPresenter))]
[TemplatePart(Name = "PART_DirectContent", Type = typeof(ContentPresenter))]
[TemplatePart(Name = "PART_ContentScrollHost", Type = typeof(ModernScrollViewer))]
public sealed class ModernFrame : Control
{
    public static readonly DependencyProperty DefaultTransitionInfoProperty = DependencyProperty.Register(
        nameof(DefaultTransitionInfo), typeof(ModernNavigationTransitionInfo), typeof(ModernFrame), new PropertyMetadata(null));
    public static readonly DependencyProperty TransitionDurationProperty = DependencyProperty.Register(
        nameof(TransitionDuration), typeof(TimeSpan), typeof(ModernFrame), new PropertyMetadata(TimeSpan.FromMilliseconds(450)));
    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(ModernFrame), new PropertyMetadata(true));
    public static readonly DependencyProperty ContentScrollHostModeProperty = DependencyProperty.Register(
        nameof(ContentScrollHostMode), typeof(ModernFrameContentScrollHostMode), typeof(ModernFrame),
        new PropertyMetadata(ModernFrameContentScrollHostMode.Enabled));
    public static readonly DependencyProperty ResetScrollOnNavigationProperty = DependencyProperty.Register(
        nameof(ResetScrollOnNavigation), typeof(bool), typeof(ModernFrame), new PropertyMetadata(true));

    private readonly List<ModernFrameJournalEntry> _backStack = [];
    private ContentPresenter? _oldPresenter;
    private ContentPresenter? _newPresenter;
    private ContentPresenter? _directPresenter;
    private ModernScrollViewer? _scrollHost;
    private FrameworkElement? _hostedContent;
    private Storyboard? _exitStoryboard;
    private Storyboard? _enterStoryboard;
    private DispatcherOperation? _pendingAnimation;
    private FrameworkElement? _waitingContent;
    private RoutedEventHandler? _contentLoaded;
    private INavigationViewPageProvider? _pageProvider;
    private bool _usingScrollHost = true;
    private int _navigationVersion;

    public ModernFrame()
    {
        Focusable = false;
        IsTabStop = false;
        ClipToBounds = true;
        DefaultTransitionInfo = new EntranceNavigationTransitionInfo();
        Template = CreateDefaultTemplate();
    }

    public ModernNavigationTransitionInfo? DefaultTransitionInfo
    {
        get => (ModernNavigationTransitionInfo?)GetValue(DefaultTransitionInfoProperty);
        set => SetValue(DefaultTransitionInfoProperty, value);
    }

    public TimeSpan TransitionDuration
    {
        get => (TimeSpan)GetValue(TransitionDurationProperty);
        set => SetValue(TransitionDurationProperty, value);
    }

    public bool IsAnimationEnabled
    {
        get => (bool)GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public ModernFrameContentScrollHostMode ContentScrollHostMode
    {
        get => (ModernFrameContentScrollHostMode)GetValue(ContentScrollHostModeProperty);
        set => SetValue(ContentScrollHostModeProperty, value);
    }

    public bool ResetScrollOnNavigation
    {
        get => (bool)GetValue(ResetScrollOnNavigationProperty);
        set => SetValue(ResetScrollOnNavigationProperty, value);
    }

    public IServiceProvider? ServiceProvider { get; set; }
    public FrameworkElement? CurrentContent { get; private set; }
    public bool CanGoBack => _backStack.Count > 0;

    public ModernScrollViewer ContentScrollHost
    {
        get
        {
            EnsureTemplateParts();
            return _scrollHost!;
        }
    }

    public event EventHandler<ModernFrameNavigatingEventArgs>? Navigating;
    public event EventHandler<ModernFrameNavigationEventArgs>? Navigated;
    public event EventHandler<ModernFrameNavigationEventArgs>? NavigationCompleted;

    public void SetPageProvider(INavigationViewPageProvider pageProvider) => _pageProvider = pageProvider;

    public FrameworkElement CreatePage(Type pageType, object? dataContext)
    {
        object? page = ServiceProvider?.GetService(pageType);
        page ??= _pageProvider?.GetPage(pageType);
        page ??= Activator.CreateInstance(pageType);
        if (page is not FrameworkElement element)
        {
            throw new InvalidOperationException($"Navigation page type '{pageType.FullName}' must create a FrameworkElement.");
        }

        if (dataContext is not null)
        {
            element.DataContext = dataContext;
        }

        return element;
    }

    public bool Navigate(Type pageType, object? dataContext = null)
    {
        if (CurrentContent is { } current
            && (current.GetType() == pageType || pageType.IsInstanceOfType(current)))
        {
            return false;
        }

        return Navigate(CreatePage(pageType, dataContext), null);
    }

    public bool Navigate(FrameworkElement content, ModernNavigationTransitionInfo? transitionInfo = null, bool addToJournal = true) =>
        NavigateCore(content, null, transitionInfo, addToJournal, ModernFrameNavigationMode.New);

    public bool GoBack()
    {
        if (_backStack.Count == 0)
        {
            return false;
        }

        var entry = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        return NavigateCore(entry.Content, entry.Parameter, entry.TransitionInfo, false, ModernFrameNavigationMode.Back);
    }

    public bool GoForward() => false;
    public void ClearJournal() => _backStack.Clear();

    public override void OnApplyTemplate()
    {
        StopTransition();
        base.OnApplyTemplate();
        _oldPresenter = GetTemplateChild("PART_OldContent") as ContentPresenter;
        _newPresenter = GetTemplateChild("PART_NewContent") as ContentPresenter;
        _directPresenter = GetTemplateChild("PART_DirectContent") as ContentPresenter;
        _scrollHost = GetTemplateChild("PART_ContentScrollHost") as ModernScrollViewer;
        if (_oldPresenter is null || _newPresenter is null || _directPresenter is null || _scrollHost is null)
        {
            throw new InvalidOperationException("ModernFrame template parts are unavailable.");
        }

        _scrollHost.Content = _newPresenter;
        if (_hostedContent is not null)
        {
            AttachCurrentHost(_hostedContent);
        }
    }

    private bool NavigateCore(
        FrameworkElement content,
        object? parameter,
        ModernNavigationTransitionInfo? transitionInfo,
        bool addToJournal,
        ModernFrameNavigationMode mode)
    {
        EnsureTemplateParts();
        if (ReferenceEquals(CurrentContent, content))
        {
            return false;
        }

        var effectiveTransition = transitionInfo ?? DefaultTransitionInfo;
        var navigatingArgs = new ModernFrameNavigatingEventArgs(content, parameter, mode, effectiveTransition);
        Navigating?.Invoke(this, navigatingArgs);
        if (navigatingArgs.Cancel)
        {
            return false;
        }

        StopTransition();
        var navigationVersion = ++_navigationVersion;
        var oldContent = CurrentContent;
        var oldHost = _hostedContent;
        (oldContent as INavigationScrollOwner)?.OnNavigatingAway();
        (oldContent?.DataContext as INavigationPresentation)?.OnNavigatingAway();
        if (addToJournal && oldContent is not null && !ReferenceEquals(oldContent, content))
        {
            _backStack.Add(new ModernFrameJournalEntry(oldContent, null, effectiveTransition));
        }

        DetachCurrentHost();
        CurrentContent = content;
        (content.DataContext as INavigationPresentation)?.OnNavigationStarted();
        _hostedContent = CreateHostedContent(content);
        _usingScrollHost = ShouldUseScrollHost(content);
        var navigationArgs = new ModernFrameNavigationEventArgs(content, parameter, mode, effectiveTransition);

        if (oldHost is null || !ShouldAnimate(effectiveTransition))
        {
            ReleaseHostedContent(oldHost);
            ClearOldPresenter();
            AttachCurrentHost(_hostedContent);
            ResetScrollIfNeeded(mode);
            Normalize(_usingScrollHost ? _scrollHost! : _directPresenter!);
            QueueNavigationCompleted(navigationVersion, navigationArgs);
        }
        else
        {
            _oldPresenter!.Content = oldHost;
            _oldPresenter.Visibility = Visibility.Visible;
            _oldPresenter.IsHitTestVisible = false;
            _oldPresenter.Opacity = 1;
            AttachCurrentHost(_hostedContent);
            ResetScrollIfNeeded(mode);
            var activeHost = _usingScrollHost ? (FrameworkElement)_scrollHost! : _directPresenter!;
            activeHost.IsHitTestVisible = false;
            activeHost.Opacity = 0;
            _exitStoryboard = effectiveTransition?.CreateExitStoryboard(_oldPresenter, mode == ModernFrameNavigationMode.Back, TransitionDuration);
            _enterStoryboard = effectiveTransition?.CreateEnterStoryboard(activeHost, mode == ModernFrameNavigationMode.Back, TransitionDuration);
            BeginTransition(activeHost, navigationVersion, navigationArgs);
        }

        Navigated?.Invoke(this, navigationArgs);
        return true;
    }

    private void BeginTransition(FrameworkElement activeHost, int navigationVersion,
        ModernFrameNavigationEventArgs navigationArgs)
    {
        // Start both clocks when content is ready; WinUI keyframes stage exit and entrance.
        Panel.SetZIndex(activeHost, 2);
        WhenContentReady(navigationVersion, () =>
        {
            ApplyNavigationPosition(navigationArgs.NavigationMode);
            ContextMenuMgr.Frontend.Services.FrontendDebugLog.Info(nameof(ModernFrame),
                $"NavigationTransitionStarted: Page={CurrentContent?.GetType().Name}, Mode={navigationArgs.NavigationMode}, Transition={navigationArgs.TransitionInfo?.GetType().Name}.");
            var remaining = (_exitStoryboard is null ? 0 : 1) + (_enterStoryboard is null ? 0 : 1);
            if (remaining == 0) { CompleteTransition(activeHost, navigationVersion, navigationArgs); return; }
            void Finished()
            {
                if (navigationVersion == _navigationVersion && --remaining == 0)
                    CompleteTransition(activeHost, navigationVersion, navigationArgs);
            }
            if (_exitStoryboard is { } exit) exit.Completed += (_, _) => Finished();
            if (_enterStoryboard is { } enter) enter.Completed += (_, _) => Finished();
            try
            {
                _exitStoryboard?.Begin(_oldPresenter, true);
                _enterStoryboard?.Begin(activeHost, true);
            }
            catch (Exception)
            {
                CompleteTransition(activeHost, navigationVersion, navigationArgs);
            }
        });
    }

    private void WhenContentReady(int navigationVersion, Action action)
    {
        void Queue() => _pendingAnimation = Dispatcher.BeginInvoke(() =>
        {
            _pendingAnimation = null;
            if (navigationVersion == _navigationVersion) action();
        }, DispatcherPriority.Loaded);
        if (CurrentContent is not { IsLoaded: false } content) { Queue(); return; }
        _waitingContent = content;
        _contentLoaded = (_, _) =>
        {
            content.Loaded -= _contentLoaded;
            _contentLoaded = null;
            _waitingContent = null;
            if (navigationVersion == _navigationVersion) Queue();
        };
        content.Loaded += _contentLoaded;
    }

    private void CompleteTransition(
        FrameworkElement activeHost,
        int navigationVersion,
        ModernFrameNavigationEventArgs navigationArgs)
    {
        if (navigationVersion != _navigationVersion)
        {
            return;
        }

        _exitStoryboard?.Remove(_oldPresenter);
        _exitStoryboard = null;
        _enterStoryboard?.Remove(activeHost);
        _enterStoryboard = null;
        ClearOldPresenter();
        activeHost.IsHitTestVisible = true;
        Normalize(activeHost);
        (CurrentContent?.DataContext as INavigationPresentation)?.OnNavigationCompleted();
        NavigationCompleted?.Invoke(this, navigationArgs);
    }

    private void StopTransition()
    {
        if (_waitingContent is not null && _contentLoaded is not null)
            _waitingContent.Loaded -= _contentLoaded;
        _waitingContent = null;
        _contentLoaded = null;
        _pendingAnimation?.Abort();
        _pendingAnimation = null;
        if (_oldPresenter is not null)
        {
            _exitStoryboard?.Remove(_oldPresenter);
        }

        var activeHost = _usingScrollHost ? (FrameworkElement?)_scrollHost : _directPresenter;
        if (activeHost is not null)
        {
            _enterStoryboard?.Remove(activeHost);
            activeHost.IsHitTestVisible = true;
            Normalize(activeHost);
        }

        _exitStoryboard = null;
        _enterStoryboard = null;
        ClearOldPresenter();
    }

    private void AttachCurrentHost(FrameworkElement hostedContent)
    {
        if (_usingScrollHost)
        {
            _directPresenter!.Content = null;
            _directPresenter.Visibility = Visibility.Collapsed;
            _newPresenter!.Content = hostedContent;
            _scrollHost!.Content = _newPresenter;
            _scrollHost.Visibility = Visibility.Visible;
        }
        else
        {
            _newPresenter!.Content = null;
            _scrollHost!.Visibility = Visibility.Collapsed;
            _directPresenter!.Content = hostedContent;
            _directPresenter.Visibility = Visibility.Visible;
        }
    }

    private void DetachCurrentHost()
    {
        if (_newPresenter is not null)
        {
            _newPresenter.Content = null;
        }
        if (_directPresenter is not null)
        {
            _directPresenter.Content = null;
        }
    }

    private void ClearOldPresenter()
    {
        if (_oldPresenter is null)
        {
            return;
        }

        ReleaseHostedContent(_oldPresenter.Content as FrameworkElement);
        _oldPresenter.Content = null;
        _oldPresenter.Visibility = Visibility.Collapsed;
        _oldPresenter.ClearValue(OpacityProperty);
        _oldPresenter.ClearValue(RenderTransformProperty);
    }

    private void ResetScrollIfNeeded(ModernFrameNavigationMode mode)
    {
        if (!_usingScrollHost || !ResetScrollOnNavigation || mode != ModernFrameNavigationMode.New)
        {
            return;
        }

        ScrollAnimationHelper.CancelVerticalAnimation(_scrollHost!);
        _scrollHost!.ScrollToHome();
        _scrollHost.ScrollToLeftEnd();
    }

    private void ApplyNavigationPosition(ModernFrameNavigationMode mode)
    {
        if (_usingScrollHost)
        {
            _scrollHost!.UpdateLayout();
            var target = CurrentContent as INavigationScrollTarget ?? FindDescendant<INavigationScrollTarget>(_hostedContent);
            target?.ApplyNavigationScrollPosition(_scrollHost);
        }
        else if (CurrentContent is INavigationScrollOwner owner)
            owner.OnNavigated(mode);
    }

    private void QueueNavigationCompleted(int navigationVersion, ModernFrameNavigationEventArgs navigationArgs) =>
        WhenContentReady(navigationVersion, () =>
        {
            ApplyNavigationPosition(navigationArgs.NavigationMode);
            (CurrentContent?.DataContext as INavigationPresentation)?.OnNavigationCompleted();
            NavigationCompleted?.Invoke(this, navigationArgs);
        });

    private bool ShouldUseScrollHost(FrameworkElement content) => ContentScrollHostMode switch
    {
        ModernFrameContentScrollHostMode.Enabled => true,
        ModernFrameContentScrollHostMode.Disabled => false,
        ModernFrameContentScrollHostMode.Auto => ModernScroll.GetOwnership(content) != ModernScrollOwnership.Self,
        _ => true
    };

    private bool ShouldAnimate(ModernNavigationTransitionInfo? transition) =>
        IsAnimationEnabled
        && SystemParameters.ClientAreaAnimation
        && TransitionDuration > TimeSpan.Zero
        && transition is not null
        && transition is not SuppressNavigationTransitionInfo;

    private static void Normalize(FrameworkElement element)
    {
        element.Opacity = 1;
        element.ClearValue(Panel.ZIndexProperty);
        if (element.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
            transform.Y = 0;
        }
    }

    private static FrameworkElement CreateHostedContent(FrameworkElement content) =>
        content is Page page ? new PageFrameHost(page) : content;

    private static void ReleaseHostedContent(FrameworkElement? hostedContent)
    {
        if (hostedContent is PageFrameHost host)
        {
            host.ClearPage();
        }
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : class
    {
        if (root is T match)
        {
            return match;
        }
        if (root is null)
        {
            return null;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var result = FindDescendant<T>(VisualTreeHelper.GetChild(root, index));
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    private static ControlTemplate CreateDefaultTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        var oldPresenter = new FrameworkElementFactory(typeof(ContentPresenter), "PART_OldContent");
        oldPresenter.SetValue(VisibilityProperty, Visibility.Collapsed);
        oldPresenter.SetValue(Panel.ZIndexProperty, 1);
        root.AppendChild(oldPresenter);

        var scrollHost = new FrameworkElementFactory(typeof(ModernScrollViewer), "PART_ContentScrollHost");
        scrollHost.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scrollHost.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scrollHost.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter), "PART_NewContent"));
        root.AppendChild(scrollHost);

        var directPresenter = new FrameworkElementFactory(typeof(ContentPresenter), "PART_DirectContent");
        directPresenter.SetValue(VisibilityProperty, Visibility.Collapsed);
        root.AppendChild(directPresenter);
        return new ControlTemplate(typeof(ModernFrame)) { VisualTree = root };
    }

    private void EnsureTemplateParts()
    {
        if (_oldPresenter is not null && _newPresenter is not null && _directPresenter is not null && _scrollHost is not null)
        {
            return;
        }
        ApplyTemplate();
        if (_oldPresenter is null || _newPresenter is null || _directPresenter is null || _scrollHost is null)
        {
            throw new InvalidOperationException("ModernFrame template parts are unavailable.");
        }
    }

    private sealed class PageFrameHost : System.Windows.Controls.Frame
    {
        private readonly Page _page;
        private bool _navigated;

        public PageFrameHost(Page page)
        {
            _page = page;
            Focusable = false;
            NavigationUIVisibility = NavigationUIVisibility.Hidden;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_navigated)
            {
                return;
            }
            _navigated = true;
            Navigate(_page);
        }

        public void ClearPage()
        {
            Loaded -= OnLoaded;
            Content = null;
            while (CanGoBack)
            {
                RemoveBackEntry();
            }
        }
    }
}
