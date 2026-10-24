using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the shell View Model.
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly Windows11ContextMenuService _windows11Service;
    private readonly ContextMenuGlobalSearchService _globalSearchService;
    private readonly GlobalSearchNavigationFilterService _globalSearchFilterService;
    private readonly INavigationService _navigationService;
    private readonly ExplorerRestartStateService _explorerRestartState;
    private readonly IBackendClient _backendClient;
    private readonly InfoBadge _approvalsBadge = new() { Visibility = Visibility.Collapsed };

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellViewModel"/> class.
    /// </summary>
    public ShellViewModel(
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        Windows11ContextMenuService windows11Service,
        ContextMenuGlobalSearchService globalSearchService,
        GlobalSearchNavigationFilterService globalSearchFilterService,
        INavigationService navigationService,
        ExplorerRestartStateService explorerRestartState,
        IBackendClient backendClient,
        MenuListActivityService? listActivity = null)
    {
        ListActivity = listActivity ?? new MenuListActivityService();
        _workspace = workspace;
        _localization = localization;
        _windows11Service = windows11Service;
        _globalSearchService = globalSearchService;
        _globalSearchFilterService = globalSearchFilterService;
        _navigationService = navigationService;
        _explorerRestartState = explorerRestartState;
        _backendClient = backendClient;

        _localization.LanguageChanged += OnLanguageChanged;
        _workspace.PendingApprovalDetected += OnPendingApprovalDetected;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _workspace.Items.CollectionChanged += OnWorkspaceItemsCollectionChanged;
        _workspace.WpsOfficeApprovalItems.CollectionChanged += OnWorkspaceItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnWorkspaceItemPropertyChanged;
        }
        foreach (var item in _workspace.WpsOfficeApprovalItems)
        {
            item.PropertyChanged += OnWorkspaceItemPropertyChanged;
        }
        _globalSearchService.CandidatesChanged += OnGlobalSearchCandidatesChanged;
        _explorerRestartState.PropertyChanged += OnExplorerRestartStatePropertyChanged;

        UpdateApprovalBadge();
    }

    public ObservableCollection<ToastNotificationViewModel> Notifications => _workspace.Notifications;

    public string WindowTitle => _localization.Translate("WindowTitle");

    public string AppTitle => _localization.Translate("AppTitle");

    public string RefreshText => _localization.Translate("Refresh");

    public string ConnectionStatus => _workspace.ConnectionStatus;

    public string ServiceAttentionText => _workspace.ServiceAttentionText;

    public bool HasServiceAttention => !string.IsNullOrWhiteSpace(ServiceAttentionText);

    public string FileCategoryName => _localization.Translate("FileCategoryName");

    public string AllObjectsCategoryName => _localization.Translate("AllObjectsCategoryName");

    public string FolderCategoryName => _localization.Translate("FolderCategoryName");

    public string DirectoryCategoryName => _localization.Translate("DirectoryCategoryName");

    public string BackgroundCategoryName => _localization.Translate("BackgroundCategoryName");

    public string DesktopCategoryName => _localization.Translate("DesktopCategoryName");

    public string DriveCategoryName => _localization.Translate("DriveCategoryName");

    public string LibraryCategoryName => _localization.Translate("LibraryCategoryName");

    public string ComputerCategoryName => _localization.Translate("ComputerCategoryName");

    public string RecycleBinCategoryName => _localization.Translate("RecycleBinCategoryName");

    public string ApplicationGroupsPageTitle => _localization.Translate("ApplicationGroupsPageTitle");

    public string FileTypesPageTitle => _localization.Translate("FileTypesPageTitle");

    public string MenuAnalysisTitle => _localization.Translate("MenuAnalysisTitle");

    public string ShellNewPageTitle => _localization.Translate("ShellNewPageTitle");

    public string SendToPageTitle => _localization.Translate("SendToPageTitle");

    public string WinXPageTitle => _localization.Translate("WinXPageTitle");

    public string OpenWithPageTitle => _localization.Translate("OpenWithPageTitle");

    public string Windows11PageTitle => _localization.Translate("Windows11PageTitle");

    public bool IsWindows11ContextMenuSupported => _windows11Service.IsSupported;

    public string OtherRulesPageTitle => _localization.Translate("OtherRulesPageTitle");

    public string ApprovalsTitle => _localization.Translate("PendingApprovalTitle");

    public string SettingsTitle => _localization.Translate("SettingsTitle");

    public string LegacyContextMenuItemsName => _localization.Translate("LegacyContextMenuItems");

    public InfoBadge ApprovalsBadge => _approvalsBadge;

    public bool NeedsExplorerRestart => _explorerRestartState.NeedsRestart;

    public bool NeedsIconCacheRefresh => _explorerRestartState.NeedsIconCacheRefresh;

    public string RestartExplorerText => _localization.Translate("RestartExplorer");

    public string RefreshIconCacheText => _localization.Translate("RefreshIconCache");

    public string GlobalSearchPlaceholder => _localization.Translate("GlobalSearchPlaceholder");

    public string GlobalSearchNoResults => _localization.Translate("GlobalSearchNoResults");

    public ObservableCollection<GlobalSearchResultViewModel> GlobalSearchResults { get; } = [];

    [ObservableProperty]
    public partial string GlobalSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsGlobalSearchSuggestionListOpen { get; set; }

    public event EventHandler<ContextMenuEntry>? PendingApprovalDetected;

    /// <summary>
    /// Initializes async.
    /// </summary>
    public async Task InitializeAsync(bool suppressBootstrapPrompt = false)
    {
        await _workspace.InitializeAsync(suppressBootstrapPrompt);
        UpdateApprovalBadge();

        if (_windows11Service.IsSupported)
        {
            _ = _windows11Service.EnsureLoadedAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Stops monitoring Async.
    /// </summary>
    public Task<BackendServiceBootstrapResult> StopMonitoringAsync()
    {
        return _workspace.StopMonitoringAsync();
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _workspace.PendingApprovalDetected -= OnPendingApprovalDetected;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _workspace.Items.CollectionChanged -= OnWorkspaceItemsCollectionChanged;
        _workspace.WpsOfficeApprovalItems.CollectionChanged -= OnWorkspaceItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
        }
        foreach (var item in _workspace.WpsOfficeApprovalItems)
        {
            item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
        }
        _globalSearchService.CandidatesChanged -= OnGlobalSearchCandidatesChanged;
        _explorerRestartState.PropertyChanged -= OnExplorerRestartStatePropertyChanged;
    }

    public MenuListActivityService ListActivity { get; }
    public Func<Task?>? RefreshCurrentPage { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var current = RefreshCurrentPage?.Invoke();
        if (current is not null) await current;
        else await _workspace.RefreshAsync();
    }

    [RelayCommand]
    private Task AllowNotificationAsync(ToastNotificationViewModel? notification)
    {
        return ResolveNotificationAsync(notification, ContextMenuDecision.Allow);
    }

    [RelayCommand]
    private Task DenyNotificationAsync(ToastNotificationViewModel? notification)
    {
        return ResolveNotificationAsync(notification, ContextMenuDecision.Deny);
    }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _backendClient.RestartExplorerAsync(cts.Token);
            _explorerRestartState.Clear();
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("RestartExplorerFailed"));
        }
    }

    [RelayCommand]
    private async Task RefreshIconCacheAsync()
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c " + BuildIconCacheRefreshCommand(),
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
            {
                throw new InvalidOperationException("Failed to start icon-cache refresh script.");
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Icon-cache refresh script exited with code {process.ExitCode}.");
            }

            _explorerRestartState.ClearIconCacheRefresh();
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("RefreshIconCacheFailed"));
        }
    }

    [RelayCommand]
    private void OpenGlobalSearchResult(GlobalSearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        FrontendDebugLog.Info(
            nameof(ShellViewModel),
            "GlobalSearchOpenResult: "
            + $"ItemId={result.Id}, "
            + $"DisplayName='{SanitizeLogText(result.DisplayName)}', "
            + $"TargetPageType={result.TargetPageType.Name}, "
            + $"Category={result.Category?.ToString() ?? "<null>"}, "
            + $"IsWindows11={result.IsWindows11}, "
            + $"TargetFilterText='{SanitizeLogText(result.TargetFilterText)}'.");

        _globalSearchFilterService.RequestFilter(
            result.TargetPageType,
            result.Category,
            result.IsWindows11,
            result.TargetFilterText,
            result.Id);
        _navigationService.Navigate(result.TargetPageType);
        IsGlobalSearchSuggestionListOpen = false;
    }

    public void OpenFirstGlobalSearchResult()
    {
        OpenGlobalSearchResult(GlobalSearchResults.FirstOrDefault());
    }

    public void UpdateGlobalSearchText(string text)
    {
        if (string.Equals(GlobalSearchText, text, StringComparison.Ordinal))
        {
            RefreshGlobalSearchResults();
            return;
        }

        GlobalSearchText = text;
    }

    partial void OnGlobalSearchTextChanged(string value)
    {
        RefreshGlobalSearchResults();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(RefreshText));
        _workspace.RefreshServiceAttentionText();
        OnPropertyChanged(nameof(ServiceAttentionText));
        OnPropertyChanged(nameof(HasServiceAttention));
        OnPropertyChanged(nameof(FileCategoryName));
        OnPropertyChanged(nameof(AllObjectsCategoryName));
        OnPropertyChanged(nameof(FolderCategoryName));
        OnPropertyChanged(nameof(DirectoryCategoryName));
        OnPropertyChanged(nameof(BackgroundCategoryName));
        OnPropertyChanged(nameof(DesktopCategoryName));
        OnPropertyChanged(nameof(DriveCategoryName));
        OnPropertyChanged(nameof(LibraryCategoryName));
        OnPropertyChanged(nameof(ComputerCategoryName));
        OnPropertyChanged(nameof(RecycleBinCategoryName));
        OnPropertyChanged(nameof(ApplicationGroupsPageTitle));
        OnPropertyChanged(nameof(FileTypesPageTitle));
        OnPropertyChanged(nameof(MenuAnalysisTitle));
        OnPropertyChanged(nameof(ShellNewPageTitle));
        OnPropertyChanged(nameof(SendToPageTitle));
        OnPropertyChanged(nameof(WinXPageTitle));
        OnPropertyChanged(nameof(OpenWithPageTitle));
        OnPropertyChanged(nameof(Windows11PageTitle));
        OnPropertyChanged(nameof(OtherRulesPageTitle));
        OnPropertyChanged(nameof(ApprovalsTitle));
        OnPropertyChanged(nameof(SettingsTitle));
        OnPropertyChanged(nameof(RestartExplorerText));
        OnPropertyChanged(nameof(RefreshIconCacheText));
        OnPropertyChanged(nameof(LegacyContextMenuItemsName));
        OnPropertyChanged(nameof(GlobalSearchPlaceholder));
        OnPropertyChanged(nameof(GlobalSearchNoResults));
        RefreshGlobalSearchResults();
        UpdateApprovalBadge();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuWorkspaceService.ConnectionStatus))
        {
            OnPropertyChanged(nameof(ConnectionStatus));
        }

        if (e.PropertyName == nameof(ContextMenuWorkspaceService.ServiceAttentionText))
        {
            OnPropertyChanged(nameof(ServiceAttentionText));
            OnPropertyChanged(nameof(HasServiceAttention));
        }
    }

    private void OnExplorerRestartStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerRestartStateService.NeedsRestart))
        {
            OnPropertyChanged(nameof(NeedsExplorerRestart));
        }

        if (e.PropertyName == nameof(ExplorerRestartStateService.NeedsIconCacheRefresh))
        {
            OnPropertyChanged(nameof(NeedsIconCacheRefresh));
        }
    }

    private void UpdateApprovalBadge()
    {
        var count = _workspace.Items.Count(static item => item.IsPendingApproval)
                    + _workspace.WpsOfficeApprovalItems.Count(static item => item.IsPendingApproval);
        _approvalsBadge.Value = count > 0 ? count.ToString() : string.Empty;
        _approvalsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWorkspaceItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ContextMenuItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnWorkspaceItemPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ContextMenuItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
            }
        }

        UpdateApprovalBadge();
    }

    private void OnWorkspaceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuItemViewModel.IsPendingApproval))
        {
            UpdateApprovalBadge();
        }
    }

    private void OnGlobalSearchCandidatesChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(RefreshGlobalSearchResults);
    }

    private void RefreshGlobalSearchResults()
    {
        GlobalSearchResults.Clear();
        foreach (var result in _globalSearchService.Search(GlobalSearchText))
        {
            GlobalSearchResults.Add(result);
        }

        IsGlobalSearchSuggestionListOpen = !string.IsNullOrWhiteSpace(GlobalSearchText)
                                           && GlobalSearchResults.Count > 0;

        FrontendDebugLog.Info(
            nameof(ShellViewModel),
            "GlobalSearchUpdated: "
            + $"Text='{SanitizeLogText(GlobalSearchText)}', "
            + $"ResultCount={GlobalSearchResults.Count}, "
            + $"SuggestionListOpen={IsGlobalSearchSuggestionListOpen}.");
    }

    private static string SanitizeLogText(string? text)
    {
        return (text ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string BuildIconCacheRefreshCommand()
    {
        return string.Join(
            " & ",
            "taskkill /f /im explorer.exe",
            "attrib -h -s -r \"%userprofile%\\AppData\\Local\\IconCache.db\"",
            "del /f \"%userprofile%\\AppData\\Local\\IconCache.db\"",
            "attrib /s /d -h -s -r \"%userprofile%\\AppData\\Local\\Microsoft\\Windows\\Explorer\\*\"",
            "del /f \"%userprofile%\\AppData\\Local\\Microsoft\\Windows\\Explorer\\thumbcache_32.db\"",
            "del /f \"%userprofile%\\AppData\\Local\\Microsoft\\Windows\\Explorer\\thumbcache_96.db\"",
            "del /f \"%userprofile%\\AppData\\Local\\Microsoft\\Windows\\Explorer\\thumbcache_102.db\"",
            "del /f \"%userprofile%\\AppData\\Local\\Microsoft\\Windows\\Explorer\\thumbcache_256.db\"",
            "del /f \"%userprofile%\\AppData\\Local\\Microsoft\\Windows\\Explorer\\thumbcache_1024.db\"",
            "del /f \"%userprofile%\\AppData\\Local\\Microsoft\\Windows\\Explorer\\thumbcache_idx.db\"",
            "del /f \"%userprofile%\\AppData\\Local\\Microsoft\\Windows\\Explorer\\thumbcache_sr.db\"",
            "reg delete \"HKEY_CLASSES_ROOT\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\TrayNotify\" /v IconStreams /f",
            "reg delete \"HKEY_CLASSES_ROOT\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\TrayNotify\" /v PastIconsStream /f",
            "start explorer");
    }

    private void OnPendingApprovalDetected(object? sender, ContextMenuEntry e)
    {
        PendingApprovalDetected?.Invoke(this, e);
    }

    private Task ResolveNotificationAsync(ToastNotificationViewModel? notification, ContextMenuDecision decision)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.ItemId))
        {
            return Task.CompletedTask;
        }

        return _workspace.ApplyDecisionAsync(notification.ItemId, decision);
    }
}
