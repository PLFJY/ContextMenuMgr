using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the context Menu Workspace Service.
/// </summary>
public partial class ContextMenuWorkspaceService : ObservableObject, IAsyncDisposable
{
    private readonly IBackendClient _backendClient;
    private readonly IBackendServiceManager _backendServiceManager;
    private readonly ContextMenuItemActionsService _itemActionsService;
    private readonly ContextMenuDeepAnalysisService _deepAnalysisService;
    private readonly IconPreviewService _iconPreviewService;
    private readonly LocalizationService _localization;
    private readonly FrontendSettingsService _settingsService;
    private readonly TrayHostProcessService _trayHostProcessService;
    private readonly PortablePackageTrustService _portablePackageTrustService;
    private readonly HashSet<string> _seenPendingApprovalIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenChangedItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenWpsOfficeApprovalIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _wpsOfficeApprovalRefreshLock = new(1, 1);
    private CancellationTokenSource? _wpsOfficeApprovalRefreshCts;
    private Task? _wpsOfficeApprovalRefreshTask;
    private bool _pendingApprovalBaselineInitialized;
    private bool _wpsOfficeApprovalBaselineInitialized;
    private bool _notificationsInitialized;
    private bool _fullyInitialized;
    private bool _uiStateActive;
    private bool _trayHostEnsured;
    private ServiceAttentionState _serviceAttentionState = ServiceAttentionState.None;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextMenuWorkspaceService"/> class.
    /// </summary>
    public ContextMenuWorkspaceService(
        IBackendClient backendClient,
        IBackendServiceManager backendServiceManager,
        ContextMenuItemActionsService itemActionsService,
        ContextMenuDeepAnalysisService deepAnalysisService,
        IconPreviewService iconPreviewService,
        LocalizationService localization,
        FrontendSettingsService settingsService,
        TrayHostProcessService trayHostProcessService,
        PortablePackageTrustService portablePackageTrustService)
    {
        _backendClient = backendClient;
        _backendServiceManager = backendServiceManager;
        _itemActionsService = itemActionsService;
        _deepAnalysisService = deepAnalysisService;
        _iconPreviewService = iconPreviewService;
        _localization = localization;
        _settingsService = settingsService;
        _trayHostProcessService = trayHostProcessService;
        _portablePackageTrustService = portablePackageTrustService;
        _backendClient.NotificationReceived += OnBackendNotificationReceived;
        ConnectionStatus = _localization.Translate("ConnectingStatus");
    }

    public event EventHandler<ContextMenuEntry>? PendingApprovalDetected;

    /// <summary>
    /// Gets the items.
    /// </summary>
    public ObservableCollection<ContextMenuItemViewModel> Items { get; } = [];

    public ObservableCollection<ContextMenuItemViewModel> WpsOfficeApprovalItems { get; } = [];

    /// <summary>
    /// Gets the notifications.
    /// </summary>
    public ObservableCollection<ToastNotificationViewModel> Notifications { get; } = [];

    /// <summary>
    /// Gets or sets the connection Status.
    /// </summary>
    [ObservableProperty]
    public partial string ConnectionStatus { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsServiceBootstrapInProgress { get; private set; }

    [ObservableProperty]
    public partial string MenuLoadFailureText { get; private set; } = string.Empty;

    public bool HasMenuLoadFailure => !string.IsNullOrWhiteSpace(MenuLoadFailureText);

    /// <summary>
    /// Gets or sets the service Attention Text.
    /// </summary>
    [ObservableProperty]
    public partial string ServiceAttentionText { get; private set; } = string.Empty;

    /// <summary>
    /// Initializes async.
    /// </summary>
    public async Task InitializeAsync(bool suppressBootstrapPrompt = false)
    {
        await _initializeLock.WaitAsync();
        try
        {
            if (_fullyInitialized)
            {
                return;
            }

            FrontendDebugLog.Info(
                "ContextMenuWorkspaceService",
                $"InitializeAsync started. SuppressBootstrapPrompt={suppressBootstrapPrompt}");

            if (await EnsureBackendReadyAsync(suppressBootstrapPrompt))
            {
                await SyncBackendLogLevelAsync();
                await SyncTrayHostLogLevelAsync();
                await EnsureNotificationConnectionAsync();
                StartWpsOfficeApprovalRefreshLoop();
                _uiStateActive = true;
                await RefreshAsync();
                _fullyInitialized = true;
                return;
            }

            UpdateServiceAttention(
                _backendServiceManager.IsServiceInstalled()
                    ? ServiceAttentionState.Unavailable
                    : ServiceAttentionState.Missing);
            ConnectionStatus = _localization.Translate("BackendUnavailableStatusStandalone");
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    /// <summary>
    /// Initializes notifications Only Async.
    /// </summary>
    public async Task InitializeNotificationsOnlyAsync(bool suppressBootstrapPrompt = true)
    {
        await _initializeLock.WaitAsync();
        try
        {
            if (_notificationsInitialized)
            {
                return;
            }

            FrontendDebugLog.Info(
                "ContextMenuWorkspaceService",
                $"InitializeNotificationsOnlyAsync started. SuppressBootstrapPrompt={suppressBootstrapPrompt}");

            if (await EnsureBackendReadyAsync(suppressBootstrapPrompt))
            {
                _uiStateActive = false;
                await SyncBackendLogLevelAsync();
                await SyncTrayHostLogLevelAsync();
                await EnsureNotificationConnectionAsync();
                StartWpsOfficeApprovalRefreshLoop();
                return;
            }

            UpdateServiceAttention(
                _backendServiceManager.IsServiceInstalled()
                    ? ServiceAttentionState.Unavailable
                    : ServiceAttentionState.Missing);
            ConnectionStatus = _localization.Translate("BackendUnavailableStatusStandalone");
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    /// <summary>
    /// Refreshes async.
    /// </summary>
    public async Task RefreshAsync()
    {
        _uiStateActive = true;
        IsLoading = true;
        ClearMenuLoadFailure();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var snapshot = await _backendClient.GetSnapshotAsync(cts.Token);
            // The frontend works from a backend-authored snapshot so every page
            // stays consistent after a single refresh pass.
            ApplySnapshot(snapshot);
            await RefreshWpsOfficeApprovalsAsync();
            UpdateServiceAttention(ServiceAttentionState.None);
            ConnectionStatus = _localization.Translate("ConnectedStatus");
        }
        catch (Exception ex)
        {
            if (BackendOperationHealthClassifier.FromPingResult(await CanReachBackendAsync())
                == BackendOperationHealth.OperationFailed)
            {
                // A successful ping is positive evidence that the service and pipe
                // are alive. Do not send the user to service repair for one failed
                // backend operation such as a snapshot/state-store error.
                UpdateServiceAttention(ServiceAttentionState.None);
                ConnectionStatus = _localization.Format("BackendDataLoadFailedStatus", ex.Message);
                SetMenuLoadFailure(MenuLoadFailureState.DataUnavailable);
            }
            else
            {
                UpdateServiceAttention(
                    _backendServiceManager.IsServiceInstalled()
                        ? ServiceAttentionState.Unavailable
                        : ServiceAttentionState.Missing);
                ConnectionStatus = _localization.Format("BackendUnavailableStatus", ex.Message);
                SetMenuLoadFailure(MenuLoadFailureState.ServiceUnavailable);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SyncBackendLogLevelAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _backendClient.SetLogLevelAsync(_settingsService.Current.LogLevel.ToRuntimeLogLevel(), cts.Token);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning("ContextMenuWorkspaceService", $"Failed to sync backend log level: {ex.Message}");
        }
    }

    public async Task SyncTrayHostLogLevelAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _trayHostProcessService.SetLogLevelAsync(_settingsService.Current.LogLevel.ToRuntimeLogLevel(), cts.Token);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning("ContextMenuWorkspaceService", $"Failed to sync tray-host log level: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets enabled Async.
    /// </summary>
    public async Task<bool> SetEnabledAsync(ContextMenuItemViewModel item, bool enable)
    {
        var isWorkspaceItem = Items.Any(existing => string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase));

        try
        {
            if (!enable
                && ProtectedMenuItemGuard.IsProtectedOpenItem(item)
                && !await ProtectedMenuItemGuard.ConfirmAsync(_localization))
            {
                return false;
            }

            if (RegistryProtectionDialog.ShouldBlockNormalContextMenuRegistryMutation(_settingsService, item.Entry))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.SetEnabledAsync(item.Id, enable, cts.Token, item.Entry);
            if (updated is not null)
            {
                if (isWorkspaceItem)
                {
                    UpsertItem(updated);
                }

                return true;
            }

            throw new InvalidOperationException("The backend completed the request without returning the updated menu item.");
        }

        catch (Exception ex)
        {
            FrontendDebugLog.Error(
                "ContextMenuWorkspaceService",
                ex,
                $"SetEnabledAsync failed for {item.Id}. BackendRegistryPath={item.Entry?.BackendRegistryPath ?? "<none>"}, HandlerClsid={item.Entry?.HandlerClsid ?? "<none>"}.");
            if (RegistryProtectionDialog.IsRegistryProtectionError(ex))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return false;
            }

            var message = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
            ConnectionStatus = message;
            await FrontendMessageBox.ShowErrorAsync(
                message,
                _localization.Translate("WindowTitle"));
        }

        return false;
    }

    /// <summary>
    /// Executes acknowledge Item State Async.
    /// </summary>
    public async Task<bool> AcknowledgeItemStateAsync(ContextMenuItemViewModel item)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.AcknowledgeItemStateAsync(item.Id, cts.Token);
            if (updated is not null)
            {
                UpsertItem(updated);
            }
            else
            {
                RemoveItem(item.Id);
            }

            return true;
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("ContextMenuWorkspaceService", ex, $"AcknowledgeItemStateAsync failed for {item.Id}.");
            ConnectionStatus = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Deletes or Undo Async.
    /// </summary>
    public async Task DeleteOrUndoAsync(ContextMenuItemViewModel item)
    {
        try
        {
            if (!item.IsDeleted
                && ProtectedMenuItemGuard.IsProtectedOpenItem(item)
                && !await ProtectedMenuItemGuard.ConfirmAsync(_localization))
            {
                return;
            }

            if (!item.IsDeleted
                && RegistryProtectionDialog.ShouldBlockNormalContextMenuRegistryMutation(_settingsService, item.Entry))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updated = item.IsDeleted
                ? await _backendClient.UndoDeleteAsync(item.Id, cts.Token)
                : await _backendClient.DeleteItemAsync(item.Id, cts.Token, item.Entry);

            if (updated is not null)
            {
                UpsertItem(updated);
            }
            else
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            await HandleItemOperationFailureAsync(
                item,
                item.IsDeleted ? "UndoDeleteFailedStatus" : "DeleteFailedStatus",
                ex,
                item.IsDeleted ? "UndoDeleteAsync" : "DeleteItemAsync");
        }
    }

    /// <summary>
    /// Executes permanently Delete Async.
    /// </summary>
    public async Task PermanentlyDeleteAsync(ContextMenuItemViewModel item)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _backendClient.PurgeDeletedItemAsync(item.Id, cts.Token);
            RemoveItem(item.Id);
        }
        catch (Exception ex)
        {
            await HandleItemOperationFailureAsync(item, "PermanentDeleteFailedStatus", ex, "PurgeDeletedItemAsync");
        }
    }

    /// <summary>
    /// Applies decision Async.
    /// </summary>
    public async Task ApplyDecisionAsync(ContextMenuItemViewModel item, ContextMenuDecision decision)
    {
        if (decision is ContextMenuDecision.Deny or ContextMenuDecision.Remove
            && ProtectedMenuItemGuard.IsProtectedOpenItem(item)
            && !await ProtectedMenuItemGuard.ConfirmAsync(_localization))
        {
            return;
        }

        await ApplyDecisionAsync(item.Id, decision);
    }

    /// <summary>
    /// Applies decision Async.
    /// </summary>
    public async Task ApplyDecisionAsync(string itemId, ContextMenuDecision decision)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.ApplyDecisionAsync(itemId, decision, cts.Token);
            var isWpsOfficeApproval = IsWpsOfficeApprovalItemId(itemId);
            if (updated is not null)
            {
                if (isWpsOfficeApproval)
                {
                    UpsertWpsOfficeApprovalItem(updated);
                }
                else
                {
                    UpsertItem(updated);
                }

                RemoveApprovalNotifications(itemId);
            }
            else
            {
                if (decision == ContextMenuDecision.Remove)
                {
                    RemoveItem(itemId);
                    RemoveApprovalNotifications(itemId);
                }

                await RefreshAsync();
            }

            if (isWpsOfficeApproval)
            {
                await RefreshWpsOfficeApprovalsAsync();
            }
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("ContextMenuWorkspaceService", ex, $"ApplyDecisionAsync failed for {itemId}.");
            ConnectionStatus = _localization.Format("DecisionApplyFailedStatus", ex.Message);
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Format("DecisionApplyFailedStatus", ex.Message),
                _localization.Translate("WindowTitle"));
        }
    }

    public async Task<bool> RefreshWpsOfficeApprovalsAsync()
    {
        if (!await _wpsOfficeApprovalRefreshLock.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var snapshot = await _backendClient.GetWpsOfficePendingApprovalsAsync(cts.Token);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => ApplyWpsOfficeApprovalSnapshot(snapshot));
            return true;
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                "ContextMenuWorkspaceService",
                $"RefreshWpsOfficeApprovalsAsync failed: {ex.Message}");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                static () => { });
            return false;
        }
        finally
        {
            _wpsOfficeApprovalRefreshLock.Release();
        }
    }

    /// <summary>
    /// Executes install Or Repair Service Async.
    /// </summary>
    public async Task<BackendServiceBootstrapResult> InstallOrRepairServiceAsync()
    {
        var result = await _backendServiceManager.InstallOrRepairServiceAsync(CancellationToken.None);
        if (result.Success)
        {
            _trayHostEnsured = false;
            UpdateServiceAttention(ServiceAttentionState.None);
            await EnsureTrayHostAsync();
            await RefreshAsync();
            return result;
        }

        return result;
    }

    /// <summary>
    /// Stops monitoring Async.
    /// </summary>
    public async Task<BackendServiceBootstrapResult> StopMonitoringAsync()
    {
        var result = await _backendServiceManager.StopServiceAsync(CancellationToken.None);
        if (result.Success)
        {
            UpdateServiceAttention(ServiceAttentionState.Unavailable);
            ConnectionStatus = _localization.Translate("ServiceStoppedStatus");
        }

        return result;
    }

    /// <summary>
    /// Executes uninstall Service Async.
    /// </summary>
    public Task<BackendServiceBootstrapResult> UninstallServiceAsync()
    {
        return _backendServiceManager.UninstallServiceAsync(CancellationToken.None);
    }

    public Task<BackendServiceBootstrapResult> ForceRemoveServiceAsync()
    {
        return _backendServiceManager.ForceRemoveServiceAsync(CancellationToken.None);
    }

    /// <summary>
    /// Sets service Auto Start Enabled Async.
    /// </summary>
    public Task<BackendServiceBootstrapResult> SetServiceAutoStartEnabledAsync(bool enabled)
    {
        return _backendServiceManager.SetServiceAutoStartEnabledAsync(enabled, CancellationToken.None);
    }

    public Task RepairRuntimeDataAclAsync(CancellationToken cancellationToken)
    {
        return _backendClient.RepairRuntimeDataAclAsync(cancellationToken);
    }

    /// <summary>
    /// Executes is Service Installed.
    /// </summary>
    public bool IsServiceInstalled() => _backendServiceManager.IsServiceInstalled();

    public ContextMenuItemActionsService ItemActions => _itemActionsService;

    /// <summary>
    /// Sets shell Attribute Async.
    /// </summary>
    public async Task<bool> SetShellAttributeAsync(ContextMenuItemViewModel item, ContextMenuShellAttribute attribute, bool enable)
    {
        try
        {
            if (ProtectedMenuItemGuard.IsProtectedOpenItem(item)
                && !await ProtectedMenuItemGuard.ConfirmAsync(_localization))
            {
                return false;
            }

            if (RegistryProtectionDialog.ShouldBlockNormalContextMenuRegistryMutation(_settingsService, item.Entry))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.SetShellAttributeAsync(item.Id, attribute, enable, cts.Token);
            if (updated is not null)
            {
                UpsertItem(updated);
                return true;
            }
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("ContextMenuWorkspaceService", ex, $"SetShellAttributeAsync failed for {item.Id}/{attribute}.");
            if (RegistryProtectionDialog.IsRegistryProtectionError(ex))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return false;
            }

            ConnectionStatus = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Sets display Text Async.
    /// </summary>
    public async Task<bool> SetDisplayTextAsync(ContextMenuItemViewModel item, string textValue)
    {
        try
        {
            if (RegistryProtectionDialog.ShouldBlockNormalContextMenuRegistryMutation(_settingsService, item.Entry))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.SetDisplayTextAsync(item.Id, textValue, cts.Token);
            if (updated is not null)
            {
                UpsertItem(updated);
                return true;
            }
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("ContextMenuWorkspaceService", ex, $"SetDisplayTextAsync failed for {item.Id}.");
            if (RegistryProtectionDialog.IsRegistryProtectionError(ex))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return false;
            }

            ConnectionStatus = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
        }

        return false;
    }

    public async Task<bool> SetCommandTextAsync(ContextMenuItemViewModel item, string commandText)
    {
        try
        {
            if (ProtectedMenuItemGuard.IsProtectedOpenItem(item)
                && !await ProtectedMenuItemGuard.ConfirmAsync(_localization))
            {
                return false;
            }

            if (RegistryProtectionDialog.ShouldBlockNormalContextMenuRegistryMutation(_settingsService, item.Entry))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.SetCommandTextAsync(item.Id, commandText, cts.Token);
            if (updated is not null)
            {
                UpsertItem(updated);
                return true;
            }
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("ContextMenuWorkspaceService", ex, $"SetCommandTextAsync failed for {item.Id}.");
            if (RegistryProtectionDialog.IsRegistryProtectionError(ex))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return false;
            }

            ConnectionStatus = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Gets registry Protection Setting Async.
    /// </summary>
    public async Task<bool> GetRegistryProtectionSettingAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _backendClient.GetRegistryProtectionSettingAsync(cts.Token);
    }

    /// <summary>
    /// Sets registry Protection Setting Async.
    /// </summary>
    public async Task<bool> SetRegistryProtectionSettingAsync(bool enable)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _backendClient.SetRegistryProtectionSettingAsync(enable, cts.Token);
    }

    public async Task<bool> GetWin11ModernContextMenuDisabledAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _backendClient.GetWin11ModernContextMenuDisabledAsync(cts.Token);
    }

    public async Task SetWin11ModernContextMenuDisabledAsync(bool disabled)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _backendClient.SetWin11ModernContextMenuDisabledAsync(disabled, cts.Token);
    }

    /// <summary>
    /// Gets service Status Text.
    /// </summary>
    public string GetServiceStatusText()
    {
        return (_backendServiceManager.GetServiceStatus()?.ToString() ?? "Missing");
    }

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _wpsOfficeApprovalRefreshCts?.Cancel();
        if (_wpsOfficeApprovalRefreshTask is not null)
        {
            try
            {
                await _wpsOfficeApprovalRefreshTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _wpsOfficeApprovalRefreshCts?.Dispose();
        _wpsOfficeApprovalRefreshLock.Dispose();
        _backendClient.NotificationReceived -= OnBackendNotificationReceived;
        await _backendClient.DisposeAsync();
    }

    private void StartWpsOfficeApprovalRefreshLoop()
    {
        if (_wpsOfficeApprovalRefreshTask is { IsCompleted: false })
        {
            return;
        }

        _wpsOfficeApprovalRefreshCts?.Cancel();
        _wpsOfficeApprovalRefreshCts?.Dispose();
        _wpsOfficeApprovalRefreshCts = new CancellationTokenSource();
        var token = _wpsOfficeApprovalRefreshCts.Token;
        _wpsOfficeApprovalRefreshTask = Task.Run(() => WpsOfficeApprovalRefreshLoopAsync(token), token);
    }

    private async Task WpsOfficeApprovalRefreshLoopAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(retryDelay, cancellationToken);
                var success = await RefreshWpsOfficeApprovalsAsync();
                retryDelay = success
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                FrontendDebugLog.Warning(
                    "ContextMenuWorkspaceService",
                    $"WpsOfficeApprovalRefreshLoopAsync failed: {ex.Message}");
            }
        }
    }

    private async Task<bool> EnsureBackendReadyAsync(bool suppressBootstrapPrompt)
    {
        if (await CanReachBackendAsync())
        {
            ClearMenuLoadFailure();
            UpdateServiceAttention(ServiceAttentionState.None);
            await EnsureTrayHostAsync();
            return true;
        }

        if (suppressBootstrapPrompt)
        {
            FrontendDebugLog.Info(
                "ContextMenuWorkspaceService",
                "Backend unreachable during silent startup. Skipping elevation/UI prompts.");

            if (_backendServiceManager.IsServiceInstalled())
            {
                var backendReady = await WaitForBackendAsync(TimeSpan.FromSeconds(12));
                FrontendDebugLog.Info(
                    "ContextMenuWorkspaceService",
                    $"Silent startup wait finished. BackendReady={backendReady}");
                UpdateServiceAttention(backendReady ? ServiceAttentionState.None : ServiceAttentionState.Unavailable);
                return backendReady;
            }

            UpdateServiceAttention(ServiceAttentionState.Missing);
            return false;
        }

        UpdateServiceAttention(ServiceAttentionState.Installing);
        ConnectionStatus = _localization.Translate("ServiceBootstrapInProgress");
        BackendServiceBootstrapResult result;
        IsServiceBootstrapInProgress = true;
        ClearMenuLoadFailure();
        try
        {
            result = await _backendServiceManager.InstallOrRepairServiceAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            UpdateServiceAttention(
                _backendServiceManager.IsServiceInstalled()
                    ? ServiceAttentionState.Unavailable
                    : ServiceAttentionState.Missing);
            ConnectionStatus = _localization.Format("ServiceInstallFailedDetailed", ex.Message);
            SetMenuLoadFailure(MenuLoadFailureState.ServiceUnavailable);
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Format("ServiceInstallFailedDetailed", ex.Message),
                _localization.Translate("WindowTitle"));
            return false;
        }
        finally
        {
            IsServiceBootstrapInProgress = false;
        }

        if (!result.Success)
        {
            UpdateServiceAttention(
                _backendServiceManager.IsServiceInstalled()
                    ? ServiceAttentionState.Unavailable
                    : ServiceAttentionState.Missing);
            ConnectionStatus = result.Cancelled
                ? _localization.Translate("ServiceOperationCancelled")
                : (string.Equals(result.Code, "PORTABLE_RUNTIME_FILES_BLOCKED", StringComparison.OrdinalIgnoreCase)
                    ? _localization.Translate("PortableRuntimeBlockedWarningText")
                    : _localization.Format("ServiceInstallFailedDetailed", result.Detail));
            SetMenuLoadFailure(result.Cancelled
                ? MenuLoadFailureState.Cancelled
                : MenuLoadFailureState.ServiceUnavailable);

            if (!result.Cancelled)
            {
                if (string.Equals(result.Code, "PORTABLE_RUNTIME_FILES_BLOCKED", StringComparison.OrdinalIgnoreCase))
                {
                    await PromptUnblockPortableRuntimeFilesAndRetryAsync();
                }
                else
                {
                    await FrontendMessageBox.ShowErrorAsync(
                        _localization.Format("ServiceInstallFailedDetailed", result.Detail),
                        _localization.Translate("WindowTitle"));
                }
            }

            return false;
        }

        var ready = await WaitForBackendAsync(TimeSpan.FromSeconds(8));
        UpdateServiceAttention(ready ? ServiceAttentionState.None : ServiceAttentionState.Unavailable);
        ConnectionStatus = ready
            ? _localization.Translate("ServiceInstallSucceeded")
            : _localization.Translate("ServicePipeUnavailableSimple");

        if (ready)
        {
            ClearMenuLoadFailure();
            _trayHostEnsured = false;
            await EnsureTrayHostAsync();
        }
        else
        {
            SetMenuLoadFailure(MenuLoadFailureState.ServiceUnavailable);
        }

        return ready;
    }

    private async Task PromptUnblockPortableRuntimeFilesAndRetryAsync()
    {
        var shouldUnblock = await FrontendMessageBox.ShowConfirmAsync(
            _localization.Translate("PortableRuntimeBlockedWarningText"),
            _localization.Translate("PortableRuntimeBlockedWarningTitle"),
            _localization.Translate("UnblockPortableFilesAndRetry"),
            _localization.Translate("DialogCancel"));
        if (!shouldUnblock)
        {
            return;
        }

        var unblockReport = await _portablePackageTrustService.UnblockPortableRuntimeFilesAsync(CancellationToken.None);
        if (unblockReport.FailedFiles.Count > 0)
        {
            var failedFiles = string.Join(
                Environment.NewLine,
                unblockReport.FailedFiles.Select(static file => $"{Path.GetFileName(file.FilePath)}: {file.Error}"));
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Format("PortableRuntimeUnblockFailed", failedFiles),
                _localization.Translate("PortableRuntimeBlockedWarningTitle"));
            return;
        }

        await EnsureBackendReadyAsync(suppressBootstrapPrompt: false);
    }

    private async Task<bool> CanReachBackendAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _backendClient.PingAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureTrayHostAsync()
    {
        if (_trayHostEnsured)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _backendClient.EnsureTrayHostAsync(cts.Token);
            _trayHostEnsured = true;
            await SyncTrayHostLogLevelAsync();
            FrontendDebugLog.Info("ContextMenuWorkspaceService", "EnsureTrayHostAsync succeeded.");
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("ContextMenuWorkspaceService", ex, "EnsureTrayHostAsync failed.");
        }
    }

    /// <summary>
    /// Executes release Ui State.
    /// </summary>
    public void ReleaseUiState()
    {
        _uiStateActive = false;
        _fullyInitialized = false;
        foreach (var item in Items.ToArray())
        {
            item.Dispose();
        }

        Items.Clear();
        Notifications.Clear();
    }

    private async Task EnsureNotificationConnectionAsync()
    {
        if (_notificationsInitialized)
        {
            return;
        }

        await _backendClient.ConnectAsync(CancellationToken.None);
        _notificationsInitialized = true;
    }

    private async Task<bool> WaitForBackendAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CanReachBackendAsync())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        return false;
    }

    private void ApplySnapshot(IReadOnlyList<ContextMenuEntry> snapshot)
    {
        var existing = Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in snapshot)
        {
            if (existing.Remove(entry.Id, out var item))
            {
                item.Update(entry);
            }
            else
            {
                Items.Add(CreateItemViewModel(entry));
            }
        }

        foreach (var removed in existing.Values.ToList())
        {
            removed.Dispose();
            Items.Remove(removed);
        }

        UpdateNotifications(snapshot);
    }

    private void ApplyWpsOfficeApprovalSnapshot(IReadOnlyList<ContextMenuEntry> snapshot)
    {
        var existing = WpsOfficeApprovalItems.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var pendingItems = snapshot.Where(static entry => entry.IsPendingApproval).ToArray();
        var currentPendingIds = pendingItems
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pendingItems)
        {
            if (existing.Remove(entry.Id, out var item))
            {
                item.Update(entry);
            }
            else
            {
                WpsOfficeApprovalItems.Add(CreateItemViewModel(entry));
            }
        }

        foreach (var removed in existing.Values.ToList())
        {
            removed.Dispose();
            WpsOfficeApprovalItems.Remove(removed);
        }

        if (!_wpsOfficeApprovalBaselineInitialized)
        {
            foreach (var itemId in currentPendingIds)
            {
                _seenWpsOfficeApprovalIds.Add(itemId);
            }

            _wpsOfficeApprovalBaselineInitialized = true;
        }
        else
        {
            foreach (var item in pendingItems)
            {
                if (_seenWpsOfficeApprovalIds.Add(item.Id))
                {
                    PendingApprovalDetected?.Invoke(this, item);
                }
            }
        }

        foreach (var staleId in _seenWpsOfficeApprovalIds.Where(id => !currentPendingIds.Contains(id)).ToList())
        {
            _seenWpsOfficeApprovalIds.Remove(staleId);
        }
    }

    private void UpdateNotifications(IEnumerable<ContextMenuEntry> snapshot)
    {
        var currentPendingIds = snapshot
            .Where(static item => item.IsPendingApproval)
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!_pendingApprovalBaselineInitialized)
        {
            foreach (var itemId in currentPendingIds)
            {
                _seenPendingApprovalIds.Add(itemId);
            }

            _pendingApprovalBaselineInitialized = true;
        }
        else
        {
        foreach (var item in snapshot.Where(static item => item.IsPendingApproval))
        {
            if (_seenPendingApprovalIds.Add(item.Id))
            {
                PendingApprovalDetected?.Invoke(this, item);
            }
        }
        }

        foreach (var item in snapshot.Where(static item => item.DetectedChangeKind != ContextMenuChangeKind.None))
        {
            if (_seenChangedItemIds.Add(item.Id))
            {
                Notifications.Insert(0, new ToastNotificationViewModel(
                    new BackendNotification
                    {
                        Kind = PipeNotificationKind.ItemStateChanged,
                        Item = item,
                        Message = _localization.Format(
                            "StartupChangeNotificationFormat",
                            ContextMenuCategoryText.GetLocalizedName(item.Category, _localization),
                            item.DisplayName)
                    },
                    _localization));
            }
        }

        foreach (var staleId in _seenPendingApprovalIds.Where(id => !currentPendingIds.Contains(id)).ToList())
        {
            _seenPendingApprovalIds.Remove(staleId);
        }

        var currentChangedIds = snapshot
            .Where(static item => item.DetectedChangeKind != ContextMenuChangeKind.None)
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleId in _seenChangedItemIds.Where(id => !currentChangedIds.Contains(id)).ToList())
        {
            _seenChangedItemIds.Remove(staleId);
        }

        foreach (var staleNotification in Notifications
                     .Where(notification =>
                         !currentPendingIds.Contains(notification.ItemId)
                         && !currentChangedIds.Contains(notification.ItemId))
                     .ToList())
        {
            Notifications.Remove(staleNotification);
        }
    }

    private void UpsertItem(ContextMenuEntry entry)
    {
        var existing = Items.FirstOrDefault(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Update(entry);
            if (!entry.IsPendingApproval)
            {
                RemoveApprovalNotifications(entry.Id);
            }

            return;
        }

        Items.Add(CreateItemViewModel(entry));
    }

    private void UpsertWpsOfficeApprovalItem(ContextMenuEntry entry)
    {
        var existing = WpsOfficeApprovalItems.FirstOrDefault(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Update(entry);
            return;
        }

        WpsOfficeApprovalItems.Add(CreateItemViewModel(entry));
    }

    private ContextMenuItemViewModel CreateItemViewModel(ContextMenuEntry entry)
    {
        return new ContextMenuItemViewModel(entry, _localization, _iconPreviewService, _itemActionsService, SetEnabledAsync, SetShellAttributeAsync, SetDisplayTextAsync, AcknowledgeItemStateAsync, SetCommandTextAsync, _deepAnalysisService, _settingsService);
    }

    private void RemoveItem(string itemId)
    {
        var existing = Items.FirstOrDefault(item => string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Dispose();
            Items.Remove(existing);
        }

        var existingWpsApproval = WpsOfficeApprovalItems.FirstOrDefault(item => string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (existingWpsApproval is not null)
        {
            existingWpsApproval.Dispose();
            WpsOfficeApprovalItems.Remove(existingWpsApproval);
        }
    }

    private ToastNotificationViewModel CreateApprovalNotification(ContextMenuEntry item)
    {
        return new ToastNotificationViewModel(
            new BackendNotification
            {
                Kind = PipeNotificationKind.ItemDetected,
                Item = item,
                Message = _localization.Format("ApprovalNeededMessage", item.DisplayName)
            },
            _localization);
    }

    private void RemoveApprovalNotifications(string itemId)
    {
        _seenPendingApprovalIds.Remove(itemId);

        foreach (var toast in Notifications
                     .Where(notification =>
                         notification.IsApprovalRequest
                         && string.Equals(notification.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            Notifications.Remove(toast);
        }
    }

    /// <summary>
    /// Refreshes service Attention Text.
    /// </summary>
    public void RefreshServiceAttentionText()
    {
        ServiceAttentionText = GetServiceAttentionText(_serviceAttentionState);
    }

    private void UpdateServiceAttention(ServiceAttentionState state)
    {
        _serviceAttentionState = state;
        ServiceAttentionText = GetServiceAttentionText(state);
    }

    private string GetServiceAttentionText(ServiceAttentionState state) => state switch
    {
        ServiceAttentionState.None => string.Empty,
        ServiceAttentionState.Missing => _localization.Translate("SystemServiceRequiredBanner"),
        ServiceAttentionState.Unavailable => _localization.Translate("SystemServiceUnavailableBanner"),
        ServiceAttentionState.Installing => _localization.Translate("SystemServiceInstallingBanner"),
        _ => string.Empty
    };

    private static bool IsWpsOfficeApprovalItemId(string? itemId)
        => !string.IsNullOrWhiteSpace(itemId)
           && itemId.StartsWith("special:wps-", StringComparison.OrdinalIgnoreCase);

    private void ClearMenuLoadFailure()
    {
        MenuLoadFailureText = string.Empty;
    }

    private void SetMenuLoadFailure(MenuLoadFailureState state)
    {
        MenuLoadFailureText = state switch
        {
            MenuLoadFailureState.Cancelled => _localization.Translate("MenuLoadCancelledText"),
            MenuLoadFailureState.ServiceUnavailable => _localization.Translate("MenuLoadServiceUnavailableText"),
            MenuLoadFailureState.DataUnavailable => _localization.Translate("MenuLoadDataUnavailableText"),
            _ => string.Empty
        };
    }

    partial void OnMenuLoadFailureTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasMenuLoadFailure));
    }

    private void OnBackendNotificationReceived(object? sender, BackendNotification notification)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (notification.Kind == PipeNotificationKind.ServiceMessage
                && !string.IsNullOrWhiteSpace(notification.Message))
            {
                ServiceAttentionText = string.IsNullOrWhiteSpace(notification.MessageResourceKey)
                    ? notification.Message
                    : _localization.Translate(notification.MessageResourceKey);
            }

            if (notification.Item is not null)
            {
                if (notification.Kind == PipeNotificationKind.ItemDetected
                    && notification.Item.IsPendingApproval
                    && _seenPendingApprovalIds.Add(notification.Item.Id))
                {
                    PendingApprovalDetected?.Invoke(this, notification.Item);
                }

                if (_uiStateActive)
                {
                    UpsertItem(notification.Item);
                }
            }
        });
    }

    private async Task HandleItemOperationFailureAsync(
        ContextMenuItemViewModel item,
        string resourceKey,
        Exception ex,
        string operationName)
    {
        FrontendDebugLog.Error(
            "ContextMenuWorkspaceService",
            ex,
            $"{operationName} failed for {item.Id}.");

        var detail = ex is TimeoutException
            ? _localization.Translate("DeleteOperationTimeoutDetail")
            : ex.Message;

        if (RegistryProtectionDialog.IsRegistryProtectionError(ex))
        {
            await RegistryProtectionDialog.ShowAsync(_localization);
            return;
        }

        var message = _localization.Format(resourceKey, item.DisplayName, detail);
        ConnectionStatus = message;

        await FrontendMessageBox.ShowErrorAsync(
            message,
            _localization.Translate("WindowTitle"));
    }

    private enum ServiceAttentionState
    {
        None,
        Missing,
        Unavailable,
        Installing
    }

    private enum MenuLoadFailureState
    {
        None,
        Cancelled,
        ServiceUnavailable,
        DataUnavailable
    }
}
