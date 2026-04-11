using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Services;

public partial class ContextMenuWorkspaceService : ObservableObject, IAsyncDisposable
{
    private readonly IBackendClient _backendClient;
    private readonly IBackendServiceManager _backendServiceManager;
    private readonly ContextMenuItemActionsService _itemActionsService;
    private readonly IconPreviewService _iconPreviewService;
    private readonly LocalizationService _localization;
    private readonly HashSet<string> _seenPendingApprovalIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenChangedItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _pendingApprovalBaselineInitialized;
    private bool _initialized;
    private ServiceAttentionState _serviceAttentionState = ServiceAttentionState.None;

    public ContextMenuWorkspaceService(
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
        _backendClient.NotificationReceived += OnBackendNotificationReceived;
        ConnectionStatus = _localization.Translate("ConnectingStatus");
    }

    public event EventHandler<ContextMenuEntry>? PendingApprovalDetected;

    public ObservableCollection<ContextMenuItemViewModel> Items { get; } = [];

    public ObservableCollection<ToastNotificationViewModel> Notifications { get; } = [];

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; }

    [ObservableProperty]
    public partial string ServiceAttentionText { get; private set; } = string.Empty;

    public async Task InitializeAsync(bool suppressBootstrapPrompt = false)
    {
        await _initializeLock.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

        FrontendDebugLog.Info(
            "ContextMenuWorkspaceService",
            $"InitializeAsync started. SuppressBootstrapPrompt={suppressBootstrapPrompt}");

        if (await EnsureBackendReadyAsync(suppressBootstrapPrompt))
        {
            await _backendClient.ConnectAsync(CancellationToken.None);
            await RefreshAsync();
            _initialized = true;
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

    public async Task RefreshAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var snapshot = await _backendClient.GetSnapshotAsync(cts.Token);
            ApplySnapshot(snapshot);
            UpdateServiceAttention(ServiceAttentionState.None);
            ConnectionStatus = _localization.Translate("ConnectedStatus");
        }
        catch (Exception ex)
        {
            UpdateServiceAttention(
                _backendServiceManager.IsServiceInstalled()
                    ? ServiceAttentionState.Unavailable
                    : ServiceAttentionState.Missing);
            ConnectionStatus = _localization.Format("BackendUnavailableStatus", ex.Message);
        }
    }

    public async Task<bool> SetEnabledAsync(ContextMenuItemViewModel item, bool enable)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.SetEnabledAsync(item.Id, enable, cts.Token);
            if (updated is not null)
            {
                UpsertItem(updated);
                return true;
            }
        }

        catch (Exception ex)
        {
            FrontendDebugLog.Error("ContextMenuWorkspaceService", ex, $"SetEnabledAsync failed for {item.Id}.");
            ConnectionStatus = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
        }

        return false;
    }

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

    public async Task DeleteOrUndoAsync(ContextMenuItemViewModel item)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updated = item.IsDeleted
                ? await _backendClient.UndoDeleteAsync(item.Id, cts.Token)
                : await _backendClient.DeleteItemAsync(item.Id, cts.Token);

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

    public async Task ApplyDecisionAsync(ContextMenuItemViewModel item, ContextMenuDecision decision)
        => await ApplyDecisionAsync(item.Id, decision);

    public async Task ApplyDecisionAsync(string itemId, ContextMenuDecision decision)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var updated = await _backendClient.ApplyDecisionAsync(itemId, decision, cts.Token);
            if (updated is not null)
            {
                UpsertItem(updated);
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

    public async Task<BackendServiceBootstrapResult> InstallOrRepairServiceAsync()
    {
        var result = await _backendServiceManager.InstallOrRepairServiceAsync(CancellationToken.None);
        if (result.Success)
        {
            UpdateServiceAttention(ServiceAttentionState.None);
            await RefreshAsync();
            return result;
        }

        return result;
    }

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

    public Task<BackendServiceBootstrapResult> UninstallServiceAsync()
    {
        return _backendServiceManager.UninstallServiceAsync(CancellationToken.None);
    }

    public bool IsServiceInstalled() => _backendServiceManager.IsServiceInstalled();

    public ContextMenuItemActionsService ItemActions => _itemActionsService;

    public async Task<bool> SetShellAttributeAsync(ContextMenuItemViewModel item, ContextMenuShellAttribute attribute, bool enable)
    {
        try
        {
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
            ConnectionStatus = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
        }

        return false;
    }

    public async Task<bool> SetDisplayTextAsync(ContextMenuItemViewModel item, string textValue)
    {
        try
        {
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
            ConnectionStatus = _localization.Format("ItemUpdateFailedStatus", item.DisplayName, ex.Message);
        }

        return false;
    }

    public async Task<bool> GetRegistryProtectionSettingAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _backendClient.GetRegistryProtectionSettingAsync(cts.Token);
    }

    public async Task<bool> SetRegistryProtectionSettingAsync(bool enable)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _backendClient.SetRegistryProtectionSettingAsync(enable, cts.Token);
    }

    public string GetServiceStatusText()
    {
        return (_backendServiceManager.GetServiceStatus()?.ToString() ?? "Missing");
    }

    public async ValueTask DisposeAsync()
    {
        _backendClient.NotificationReceived -= OnBackendNotificationReceived;
        await _backendClient.DisposeAsync();
    }

    private async Task<bool> EnsureBackendReadyAsync(bool suppressBootstrapPrompt)
    {
        if (await CanReachBackendAsync())
        {
            UpdateServiceAttention(ServiceAttentionState.None);
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
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Format("ServiceInstallFailedDetailed", ex.Message),
                _localization.Translate("WindowTitle"));
            return false;
        }

        if (!result.Success)
        {
            UpdateServiceAttention(
                _backendServiceManager.IsServiceInstalled()
                    ? ServiceAttentionState.Unavailable
                    : ServiceAttentionState.Missing);
            ConnectionStatus = result.Cancelled
                ? _localization.Translate("ServiceOperationCancelled")
                : _localization.Format("ServiceInstallFailedDetailed", result.Detail);

            if (!result.Cancelled)
            {
                await FrontendMessageBox.ShowErrorAsync(
                    _localization.Format("ServiceInstallFailedDetailed", result.Detail),
                    _localization.Translate("WindowTitle"));
            }

            return false;
        }

        var ready = await WaitForBackendAsync(TimeSpan.FromSeconds(8));
        UpdateServiceAttention(ready ? ServiceAttentionState.None : ServiceAttentionState.Unavailable);
        ConnectionStatus = ready
            ? _localization.Translate("ServiceInstallSucceeded")
            : _localization.Translate("ServicePipeUnavailableSimple");
        return ready;
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
                Items.Add(new ContextMenuItemViewModel(entry, _localization, _iconPreviewService, _itemActionsService, SetEnabledAsync, SetShellAttributeAsync, SetDisplayTextAsync, AcknowledgeItemStateAsync));
            }
        }

        foreach (var removed in existing.Values.ToList())
        {
            Items.Remove(removed);
        }

        UpdateNotifications(snapshot);
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

        Items.Add(new ContextMenuItemViewModel(entry, _localization, _iconPreviewService, _itemActionsService, SetEnabledAsync, SetShellAttributeAsync, SetDisplayTextAsync, AcknowledgeItemStateAsync));
    }

    private void RemoveItem(string itemId)
    {
        var existing = Items.FirstOrDefault(item => string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Items.Remove(existing);
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

    private void OnBackendNotificationReceived(object? sender, BackendNotification notification)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (notification.Item is not null)
            {
                if (notification.Kind == PipeNotificationKind.ItemDetected
                    && notification.Item.IsPendingApproval
                    && _seenPendingApprovalIds.Add(notification.Item.Id))
                {
                    PendingApprovalDetected?.Invoke(this, notification.Item);
                }

                UpsertItem(notification.Item);
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
}
