using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ServiceProcess;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IBackendClient _backendClient;
    private readonly IBackendServiceManager _backendServiceManager;
    private readonly ContextMenuItemActionsService _itemActionsService;
    private readonly IconPreviewService _iconPreviewService;
    private readonly LocalizationService _localization;

    public MainViewModel(
        IBackendClient backendClient,
        IBackendServiceManager backendServiceManager,
        ContextMenuItemActionsService itemActionsService,
        IconPreviewService iconPreviewService,
        LocalizationService localization)
    {
        _backendClient = backendClient;
        _backendServiceManager = backendServiceManager;
        _itemActionsService = itemActionsService;
        _iconPreviewService = iconPreviewService;
        _localization = localization;

        ConnectionStatus = _localization.Translate("ConnectingStatus");

        _backendClient.NotificationReceived += OnNotificationReceived;
        _localization.LanguageChanged += OnLanguageChanged;

        AvailableLanguages =
        [
            new LanguageOptionViewModel(AppLanguageOption.System, _localization),
            new LanguageOptionViewModel(AppLanguageOption.ChineseSimplified, _localization),
            new LanguageOptionViewModel(AppLanguageOption.EnglishUnitedStates, _localization)
        ];

        Categories =
        [
            new CategoryViewModel(ContextMenuCategory.File, _localization),
            new CategoryViewModel(ContextMenuCategory.Folder, _localization),
            new CategoryViewModel(ContextMenuCategory.DesktopBackground, _localization),
            new CategoryViewModel(ContextMenuCategory.DirectoryBackground, _localization)
        ];

        SelectedLanguage = AvailableLanguages[0];
    }

    public ObservableCollection<CategoryViewModel> Categories { get; }

    public ObservableCollection<ToastNotificationViewModel> Notifications { get; } = [];

    public ObservableCollection<LanguageOptionViewModel> AvailableLanguages { get; }

    [ObservableProperty]
    public partial LanguageOptionViewModel? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; }

    public string WindowTitle => _localization.Translate("WindowTitle");

    public string AppTitle => _localization.Translate("AppTitle");

    public string AppDescription => _localization.Translate("AppDescription");

    public string BackendStatusTitle => _localization.Translate("BackendStatusTitle");

    public string LanguageLabel => _localization.Translate("LanguageLabel");

    public string RefreshText => _localization.Translate("Refresh");

    public string MonitoredCategoriesTitle => _localization.Translate("MonitoredCategoriesTitle");

    public string MonitoredCategoriesDescription => _localization.Translate("MonitoredCategoriesDescription");

    public string PendingApprovalTitle => _localization.Translate("PendingApprovalTitle");

    public string PendingApprovalDescription => _localization.Translate("PendingApprovalDescription");

    public string ServiceNotesTitle => _localization.Translate("ServiceNotesTitle");

    public string ServiceNotesDescription => _localization.Translate("ServiceNotesDescription");

    public string AllowText => _localization.Translate("Allow");

    public string DenyText => _localization.Translate("Deny");

    partial void OnSelectedLanguageChanged(LanguageOptionViewModel? value)
    {
        if (value is not null)
        {
            _localization.SelectedLanguage = value.Option;
        }
    }

    public async Task InitializeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("MainViewModel", "InitializeAsync started.");
        if (await EnsureBackendReadyAsync())
        {
            FrontendDebugLog.Info("MainViewModel", "Backend ready. Refreshing snapshot.");
            await RefreshAsync();
            FrontendDebugLog.Info("MainViewModel", $"InitializeAsync finished successfully in {stopwatch.ElapsedMilliseconds} ms.");
            return;
        }

        FrontendDebugLog.Info("MainViewModel", $"Backend not ready. Falling back to placeholder data after {stopwatch.ElapsedMilliseconds} ms.");
        SeedDesignTimeData();
    }

    public async Task DisposeAsync()
    {
        _backendClient.NotificationReceived -= OnNotificationReceived;
        _localization.LanguageChanged -= OnLanguageChanged;
        await _backendClient.DisposeAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("MainViewModel", "RefreshAsync started.");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var snapshot = await _backendClient.GetSnapshotAsync(cts.Token);

            PopulateCategories(snapshot);
            ConnectionStatus = _localization.Format("ConnectedStatus", DateTime.Now);
            FrontendDebugLog.Info("MainViewModel", $"RefreshAsync loaded {snapshot.Count} items in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("MainViewModel", ex, $"RefreshAsync failed after {stopwatch.ElapsedMilliseconds} ms.");
            SeedDesignTimeData();
            ConnectionStatus = _localization.Format("BackendUnavailableStatus", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ToggleItemAsync(ContextMenuItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.SetEnabledAsync(item.Id, !item.IsEnabled, cts.Token);
            if (updated is not null)
            {
                UpdateItem(updated);
                ConnectionStatus = _localization.Format("ItemUpdatedStatus", updated.DisplayName, DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
        }
    }

    [RelayCommand]
    private Task ApproveNotificationAsync(ToastNotificationViewModel? notification)
    {
        return ResolveNotificationAsync(notification, ContextMenuDecision.Allow);
    }

    [RelayCommand]
    private Task DenyNotificationAsync(ToastNotificationViewModel? notification)
    {
        return ResolveNotificationAsync(notification, ContextMenuDecision.Deny);
    }

    private async Task<bool> EnsureBackendReadyAsync()
    {
        FrontendDebugLog.Info("MainViewModel", "EnsureBackendReadyAsync started.");
        if (await CanReachBackendAsync())
        {
            FrontendDebugLog.Info("MainViewModel", "Backend reachable without bootstrap.");
            return true;
        }

        var isInstalled = _backendServiceManager.IsServiceInstalled();
        var serviceStatus = _backendServiceManager.GetServiceStatus();
        FrontendDebugLog.Info("MainViewModel", $"Backend unreachable. Installed={isInstalled}, Status={serviceStatus?.ToString() ?? "Missing"}");

        ConnectionStatus = _localization.Translate("ServiceBootstrapInProgress");
        FrontendDebugLog.Info("MainViewModel", "Starting InstallOrRepairServiceAsync.");
        var result = await _backendServiceManager.InstallOrRepairServiceAsync(CancellationToken.None);
        FrontendDebugLog.Info("MainViewModel", $"InstallOrRepairServiceAsync returned Success={result.Success}, Cancelled={result.Cancelled}, Code={result.Code}, Detail={result.Detail}");
        if (!result.Success)
        {
            ConnectionStatus = result.Cancelled
                ? _localization.Translate("ServiceOperationCancelled")
                : ResolveBootstrapFailureMessage(result.Code, result.Detail);
            return false;
        }

        FrontendDebugLog.Info("MainViewModel", "Waiting for backend pipe after successful bootstrap.");
        if (await WaitForBackendAsync(TimeSpan.FromSeconds(8)))
        {
            ConnectionStatus = _localization.Translate("ServiceInstallSucceeded");
            FrontendDebugLog.Info("MainViewModel", "Backend pipe became reachable after bootstrap.");
            return true;
        }

        ConnectionStatus = ResolveBootstrapFailureMessage("PIPE_UNREACHABLE", DescribeServiceStatus(_backendServiceManager.GetServiceStatus()));
        FrontendDebugLog.Info("MainViewModel", $"Backend pipe remained unreachable. ServiceStatus={_backendServiceManager.GetServiceStatus()?.ToString() ?? "Missing"}");
        return false;
    }

    private async Task<bool> CanReachBackendAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _backendClient.PingAsync(cts.Token);
            FrontendDebugLog.Info("MainViewModel", $"CanReachBackendAsync succeeded in {stopwatch.ElapsedMilliseconds} ms.");
            return true;
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("MainViewModel", ex, $"CanReachBackendAsync failed after {stopwatch.ElapsedMilliseconds} ms.");
            return false;
        }
    }

    private async Task<bool> WaitForBackendAsync(TimeSpan timeout)
    {
        FrontendDebugLog.Info("MainViewModel", $"WaitForBackendAsync started. Timeout={timeout.TotalSeconds:0.##}s");
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            if (await CanReachBackendAsync())
            {
                FrontendDebugLog.Info("MainViewModel", $"WaitForBackendAsync succeeded on attempt {attempt}.");
                return true;
            }

            var serviceStatus = _backendServiceManager.GetServiceStatus();
            FrontendDebugLog.Info("MainViewModel", $"WaitForBackendAsync attempt {attempt} service status={serviceStatus?.ToString() ?? "Missing"}");
            if (serviceStatus is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                FrontendDebugLog.Info("MainViewModel", "WaitForBackendAsync stopping early because service is stopped.");
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        FrontendDebugLog.Info("MainViewModel", "WaitForBackendAsync timed out.");
        return false;
    }

    private async Task ResolveNotificationAsync(ToastNotificationViewModel? notification, ContextMenuDecision decision)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.ItemId))
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.ApplyDecisionAsync(notification.ItemId, decision, cts.Token);

            if (updated is not null)
            {
                UpdateItem(updated);
                var decisionText = _localization.Translate(
                    decision == ContextMenuDecision.Allow ? "DecisionAllow" : "DecisionDeny");
                ConnectionStatus = _localization.Format("DecisionAppliedStatus", decisionText, updated.DisplayName);
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = _localization.Format("DecisionApplyFailedStatus", ex.Message);
        }
        finally
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => Notifications.Remove(notification));
        }
    }

    private void PopulateCategories(IReadOnlyList<ContextMenuEntry> snapshot)
    {
        foreach (var category in Categories)
        {
            category.Items.Clear();
        }

        foreach (var entry in snapshot)
        {
            GetCategory(entry.Category).Items.Add(new ContextMenuItemViewModel(entry, _localization, _iconPreviewService, _itemActionsService));
        }
    }

    private void SeedDesignTimeData()
    {
        var sampleData = new[]
        {
            new ContextMenuEntry
            {
                Id = @"*\shell|OpenWithMyApp",
                Category = ContextMenuCategory.File,
                EntryKind = ContextMenuEntryKind.ShellVerb,
                KeyName = "OpenWithMyApp",
                DisplayName = "Open With My App",
                RegistryPath = @"*\shell\OpenWithMyApp",
                SourceRootPath = @"*\shell",
                CommandText = "\"C:\\Program Files\\MyApp\\myapp.exe\" \"%1\"",
                IsEnabled = true,
                Notes = "\"C:\\Program Files\\MyApp\\myapp.exe\" \"%1\""
            },
            new ContextMenuEntry
            {
                Id = @"Directory\shellex\ContextMenuHandlers|ArchiveTool",
                Category = ContextMenuCategory.Folder,
                EntryKind = ContextMenuEntryKind.ShellExtension,
                KeyName = "ArchiveTool",
                DisplayName = "Archive Tool",
                RegistryPath = @"Directory\shellex\ContextMenuHandlers\ArchiveTool",
                SourceRootPath = @"Directory\shellex\ContextMenuHandlers",
                HandlerClsid = "{01234567-89AB-CDEF-0123-456789ABCDEF}",
                IsEnabled = false,
                Notes = "Handler CLSID: {01234567-89AB-CDEF-0123-456789ABCDEF}"
            }
        };

        PopulateCategories(sampleData);
    }

    private void UpdateItem(ContextMenuEntry updatedEntry)
    {
        var category = GetCategory(updatedEntry.Category);
        var existing = category.Items.FirstOrDefault(item => item.Id == updatedEntry.Id);

        if (existing is not null)
        {
            existing.Update(updatedEntry);
            return;
        }

        category.Items.Add(new ContextMenuItemViewModel(updatedEntry, _localization, _iconPreviewService, _itemActionsService));
    }

    private void OnNotificationReceived(object? sender, BackendNotification notification)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (notification.Item is not null)
            {
                UpdateItem(notification.Item);
            }

            Notifications.Insert(0, new ToastNotificationViewModel(notification, _localization));

            while (Notifications.Count > 4)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }

            ConnectionStatus = _localization.Format("NotificationReceivedStatus", DateTime.Now);
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(AppDescription));
        OnPropertyChanged(nameof(BackendStatusTitle));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(MonitoredCategoriesTitle));
        OnPropertyChanged(nameof(MonitoredCategoriesDescription));
        OnPropertyChanged(nameof(PendingApprovalTitle));
        OnPropertyChanged(nameof(PendingApprovalDescription));
        OnPropertyChanged(nameof(ServiceNotesTitle));
        OnPropertyChanged(nameof(ServiceNotesDescription));
        OnPropertyChanged(nameof(AllowText));
        OnPropertyChanged(nameof(DenyText));
    }

    private string ResolveBootstrapFailureMessage(string errorCode, string detail)
    {
        return errorCode switch
        {
            "BACKEND_EXE_MISSING" => _localization.Translate("BackendExecutableMissing"),
            "ELEVATION_CANCELLED" => _localization.Translate("ServiceOperationCancelled"),
            "SERVICE_NOT_RUNNING" => _localization.Format("ServiceNotRunningStatus", detail),
            "PIPE_UNREACHABLE" => _localization.Format("ServicePipeUnavailableStatus", detail),
            "SERVICE_BOOTSTRAP_ERROR" => _localization.Format("ServiceInstallFailedDetailed", detail),
            "BOOTSTRAP_EXCEPTION" => _localization.Format("ServiceInstallFailedDetailed", detail),
            _ => _localization.Format("ServiceInstallFailed", errorCode)
        };
    }

    private string DescribeServiceStatus(ServiceControllerStatus? status)
    {
        return status switch
        {
            ServiceControllerStatus.Running => _localization.Translate("ServiceStatusRunning"),
            ServiceControllerStatus.Stopped => _localization.Translate("ServiceStatusStopped"),
            ServiceControllerStatus.StartPending => _localization.Translate("ServiceStatusStartPending"),
            ServiceControllerStatus.StopPending => _localization.Translate("ServiceStatusStopPending"),
            ServiceControllerStatus.Paused => _localization.Translate("ServiceStatusPaused"),
            ServiceControllerStatus.PausePending => _localization.Translate("ServiceStatusPausePending"),
            ServiceControllerStatus.ContinuePending => _localization.Translate("ServiceStatusContinuePending"),
            null => _localization.Translate("ServiceStatusMissing"),
            _ => status.ToString() ?? _localization.Translate("ServiceStatusMissing")
        };
    }

    private CategoryViewModel GetCategory(ContextMenuCategory category) => category switch
    {
        ContextMenuCategory.File => Categories[0],
        ContextMenuCategory.Folder => Categories[1],
        ContextMenuCategory.DesktopBackground => Categories[2],
        ContextMenuCategory.DirectoryBackground => Categories[3],
        _ => Categories[0]
    };
}
