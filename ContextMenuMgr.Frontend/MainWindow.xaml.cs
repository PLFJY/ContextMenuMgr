using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views;
using ContextMenuMgr.Frontend.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;

namespace ContextMenuMgr.Frontend;

/// <summary>
/// Represents the main Window.
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateCheckService _updateCheckService;
    private readonly MainWindowPlacementService _windowPlacementService;
#if DEBUG
    private readonly IServiceProvider _serviceProvider;
    private DebugToolsWindow? _debugToolsWindow;
#endif
    private Type? _pendingPageType;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow(
        ViewModels.ShellViewModel viewModel,
        IServiceProvider serviceProvider,
        INavigationService navigationService,
        IInfoBarService infoBarService,
        UpdateCheckService updateCheckService,
        FrontendThemeService themeService,
        MainWindowPlacementService windowPlacementService)
    {
        _updateCheckService = updateCheckService;
        _windowPlacementService = windowPlacementService;
#if DEBUG
        _serviceProvider = serviceProvider;
#endif
        themeService.Initialize(this);
        InitializeComponent();
        WindowChromeTitleBarFactory.Apply(this, 44);
        infoBarService.SetInfoBarControl(RootInfoBar);
        RootNavigation.SetServiceProvider(serviceProvider);
        navigationService.SetNavigationControl(RootNavigation);
        DataContext = viewModel;
        viewModel.RefreshCurrentPage = () => RootNavigation.CurrentContent?.DataContext switch
        {
            SpecialMenuPageViewModel special => special.RefreshAsync(),
            Windows11ContextMenuPageViewModel windows11 => windows11.RefreshCommand.ExecuteAsync(null),
            FileTypesPageViewModel fileTypes => fileTypes.RefreshAsync(),
            OtherRulesPageViewModel rules => rules.RefreshAsync(),
            SceneContextMenuTabViewModel scene => scene.RefreshAsync(),
            FileTypeBatchManagementViewModel batch => batch.RefreshAsync(),
            _ => null
        };
        Closed += (_, _) => viewModel.RefreshCurrentPage = null;

        ApplyWindowIcon();
#if DEBUG
        PreviewKeyDown += OnPreviewKeyDown;
#endif
        StateChanged += (_, _) => UpdateMaximizeButtonIcon();
        Closing += OnClosing;
        _windowPlacementService.ApplySavedPlacement(this);
        UpdateMaximizeButtonIcon();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Navigates to to.
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        _pendingPageType = pageType;
        if (IsLoaded)
        {
            RootNavigation.Navigate(pageType);
        }
    }

    /// <summary>
    /// Executes bring To Foreground.
    /// </summary>
    public void BringToForeground()
    {
        var previousTopmost = Topmost;
        Topmost = true;
        Activate();
        Topmost = previousTopmost;
        Focus();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void GlobalSearchBox_QuerySubmitted(
        Wpf.Ui.Controls.AutoSuggestBox sender,
        Wpf.Ui.Controls.AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        args.Handled = true;

        if (DataContext is not ShellViewModel viewModel)
        {
            return;
        }

        var result = viewModel.GlobalSearchResults.FirstOrDefault();
        FrontendDebugLog.Info(
            nameof(MainWindow),
            "GlobalSearchQuerySubmitted: "
            + $"Query='{SanitizeLogText(args.QueryText)}', "
            + $"ResultCount={viewModel.GlobalSearchResults.Count}, "
            + $"OpeningItemId={result?.Id ?? "<null>"}.");

        if (result is not null)
        {
            OpenGlobalSearchResult(sender, result);
        }
    }

    private void GlobalSearchBox_SuggestionChosen(
        Wpf.Ui.Controls.AutoSuggestBox sender,
        Wpf.Ui.Controls.AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        args.Handled = true;

        if (args.SelectedItem is not GlobalSearchResultViewModel result)
        {
            return;
        }

        FrontendDebugLog.Info(
            nameof(MainWindow),
            "GlobalSearchSuggestionChosen: "
            + $"ItemId={result.Id}, "
            + $"TargetPage={result.TargetPageType.Name}.");
        OpenGlobalSearchResult(sender, result);
    }

    private void GlobalSearchBox_TextChanged(
        Wpf.Ui.Controls.AutoSuggestBox sender,
        Wpf.Ui.Controls.AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != Wpf.Ui.Controls.AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        args.Handled = true;

        if (DataContext is not ShellViewModel viewModel)
        {
            sender.IsSuggestionListOpen = false;
            return;
        }

        viewModel.UpdateGlobalSearchText(args.Text);
        sender.ItemsSource = viewModel.GlobalSearchResults;
        sender.IsSuggestionListOpen = !string.IsNullOrWhiteSpace(args.Text)
                                      && viewModel.GlobalSearchResults.Count > 0;

        FrontendDebugLog.Info(
            nameof(MainWindow),
            "GlobalSearchAutoSuggestTextChanged: "
            + $"Reason={args.Reason}, "
            + $"Text='{SanitizeLogText(args.Text)}', "
            + "Handled=True, "
            + $"ResultCount={viewModel.GlobalSearchResults.Count}, "
            + $"PopupOpen={sender.IsSuggestionListOpen}.");
    }

    private void OpenGlobalSearchResult(
        Wpf.Ui.Controls.AutoSuggestBox sender,
        GlobalSearchResultViewModel result)
    {
        if (DataContext is not ShellViewModel viewModel)
        {
            return;
        }

        if (viewModel.OpenGlobalSearchResultCommand.CanExecute(result))
        {
            viewModel.OpenGlobalSearchResultCommand.Execute(result);
        }

        sender.IsSuggestionListOpen = false;
    }

    private static string SanitizeLogText(string? text)
    {
        return (text ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private void UpdateMaximizeButtonIcon()
    {
        MaximizeButton.Icon = WindowState == WindowState.Maximized
            ? new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.SquareMultiple24 }
            : new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Maximize24 };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var targetPageType = _pendingPageType ?? typeof(FileContextMenuPage);
        RootNavigation.Navigate(targetPageType);
        _updateCheckService.StartInitialCheck();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _windowPlacementService.SavePlacement(this);
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
        }
    }

#if DEBUG
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12)
        {
            return;
        }

        e.Handled = true;
        ShowDebugToolsWindow();
    }

    private void ShowDebugToolsWindow()
    {
        if (_debugToolsWindow is { IsVisible: true })
        {
            _debugToolsWindow.Activate();
            return;
        }

        _debugToolsWindow = ActivatorUtilities.CreateInstance<DebugToolsWindow>(_serviceProvider);
        _debugToolsWindow.Owner = this;
        _debugToolsWindow.Closed += (_, _) => _debugToolsWindow = null;
        _debugToolsWindow.Show();
    }
#endif
}
