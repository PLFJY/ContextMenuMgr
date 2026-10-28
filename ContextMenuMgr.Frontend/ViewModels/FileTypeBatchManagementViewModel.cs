using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class FileTypeBatchManagementViewModel : ObservableObject, IDisposable
{
    private readonly IBackendClient _backendClient;
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly IconPreviewService _iconPreviewService;
    private readonly ContextMenuItemActionsService _actionsService;
    private readonly Func<Task> _backAsync;
    private bool _disposed;
    private bool _deferRefresh;
    private bool _hasInitializedSelection;
    private CancellationTokenSource? _batchCancellation;

    public FileTypeBatchManagementViewModel(
        ContextMenuItemViewModel sourceItem,
        FileTypeBatchQuery query,
        IBackendClient backendClient,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        ContextMenuItemActionsService actionsService,
        Func<Task> backAsync)
    {
        SourceItem = sourceItem;
        Query = query;
        _backendClient = backendClient;
        _backendClient.ItemsRemoved += OnItemsRemoved;
        _workspace = workspace;
        _workspace.PropertyChanged += OnBackendAvailabilityChanged;
        _localization = localization;
        _iconPreviewService = iconPreviewService;
        _actionsService = actionsService;
        _backAsync = backAsync;

        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ContextMenuItemViewModel SourceItem { get; }

    public FileTypeBatchQuery Query { get; }

    public ObservableCollection<ContextMenuItemViewModel> Items { get; } = [];

    public ObservableCollection<FileTypeBatchActionGroup> ActionGroups { get; } = [];

    public string Title => _localization.Format("FileTypeBatchTitle", SourceItem.DisplayName);

    public string IdentityText => Query.EntryKind == ContextMenuEntryKind.ShellExtension
        && !string.IsNullOrWhiteSpace(Query.HandlerClsid)
            ? _localization.Format("FileTypeBatchClsidIdentity", Query.HandlerClsid)
            : !string.IsNullOrWhiteSpace(Query.CommandExecutablePath)
                ? _localization.Format("FileTypeBatchCommandIdentity", Query.CommandExecutablePath)
                : _localization.Translate("FileTypeBatchIdentityUnknown");

    public string CountText => _localization.Format("FileTypeBatchGroupedCountFormat", ActionGroups.Count, Items.Count);

    public string? ProgressText { get; private set; }

    public string? ResultText { get; private set; }

    public string CancelText => _localization.Translate("DialogCancel");

    public string SourceRegistryPathLabel => _localization.Translate("FileTypeBatchSourceRegistryPath");

    public string SourceRegistryPath => Query.SourceRegistryPath ?? SourceItem.RegistryPath;

    public string RefreshText => _localization.Translate("Refresh");

    public string BackText => _localization.Translate("Back");

    public string DisableAllText => _localization.Translate("ApplicationGroupDisableAll");

    public string EnableAllText => _localization.Translate("FileTypeBatchEnableAll");

    public string DeleteAllText => _localization.Translate("FileTypeBatchDeleteAll");

    public string UndoAllText => _localization.Translate("FileTypeBatchUndoAll");

    public string ProtectedDeleteText => _localization.Translate("FileTypeBatchProtectedDelete");

    public string EmptyText => IsBusy
        ? _localization.Translate("LoadingStatus")
        : _localization.Translate("FileTypeBatchNoItems");

    public bool ShowPlaceholder => Items.Count == 0;

    public bool CanEditBatchRows => !IsBusy;

    public bool CanDisableAll => !IsBusy && SelectedItems().Any(static item => item.IsEnabled && item.CanToggle);

    public bool CanEnableAll => !IsBusy && SelectedItems().Any(static item => !item.IsEnabled && item.CanToggle);

    public bool CanDeleteAll => !IsBusy && SelectedItems().Any(CanDeleteBatchItem);

    public bool CanUndoAll => !IsBusy && SelectedItems().Any(static item => item.IsDeleted && item.HasBackup);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanDisableAll))]
    [NotifyPropertyChangedFor(nameof(CanEnableAll))]
    [NotifyPropertyChangedFor(nameof(CanDeleteAll))]
    [NotifyPropertyChangedFor(nameof(CanUndoAll))]
    [NotifyPropertyChangedFor(nameof(CanEditBatchRows))]
    public partial bool IsBusy { get; private set; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        var preserveBusy = _batchCancellation is not null;
        IsBusy = true;
        NotifyBulkCommands();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var entries = await _backendClient.FindRelatedFileTypeMenuItemsAsync(Query, cts.Token);
            if (!_disposed) ApplySnapshot(entries);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                nameof(FileTypeBatchManagementViewModel),
                $"RefreshAsync failed. SourceItemId={SourceItem.Id}, Message={ex.Message}");
            await FrontendMessageBox.ShowErrorAsync(ex.Message, Title);
        }
        finally
        {
            if (!preserveBusy) IsBusy = false;
            NotifyBulkCommands();
        }
    }

    [RelayCommand]
    private Task BackAsync() => _backAsync();

    [RelayCommand(CanExecute = nameof(CanDisableAll))]
    private Task DisableAllAsync() => SetAllEnabledAsync(enable: false);

    [RelayCommand(CanExecute = nameof(CanEnableAll))]
    private Task EnableAllAsync() => SetAllEnabledAsync(enable: true);

    [RelayCommand(CanExecute = nameof(CanDeleteAll))]
    private async Task DeleteAllAsync()
    {
        var deleteCandidates = SelectedItems().Where(CanDeleteBatchItem).ToArray();
        if (deleteCandidates.Length == 0)
        {
            return;
        }

        var userCount = deleteCandidates.Count(item => item.Entry.BackendRegistryPath.StartsWith(
            "HKEY_USERS\\", StringComparison.OrdinalIgnoreCase));
        var actionScope = string.Join("; ", ActionGroups.Where(group => group.IsSelected)
            .Select(group => $"{group.Name} ({group.Registrations.Count(registration =>
                registration.IsIncluded && CanDeleteBatchItem(registration.Item))})")
            .Take(12));

        var shouldDelete = await FrontendMessageBox.ShowConfirmAsync(
            _localization.Format(
                "FileTypeBatchDeleteSelectedConfirmation",
                deleteCandidates.Length,
                ActionGroups.Count(group => group.IsSelected),
                userCount,
                deleteCandidates.Length - userCount,
                actionScope),
            _localization.Translate("FileTypeBatchDeleteAll"),
            _localization.Translate("FileTypeBatchDeleteAll"),
            _localization.Translate("DialogCancel"));
        if (!shouldDelete)
        {
            return;
        }

        IsBusy = true;
        NotifyBulkCommands();
        using var batchCancellation = new CancellationTokenSource();
        _batchCancellation = batchCancellation;
        var operationId = Guid.NewGuid();
        var successes = 0;
        var failures = 0;
        var skipped = Items.Count - deleteCandidates.Length;
        var uncertain = 0;
        var completed = 0;
        try
        {
            _deferRefresh = true;
            foreach (var item in deleteCandidates)
            {
                if (batchCancellation.IsCancellationRequested) break;
                ProgressText = _localization.Format("FileTypeBatchProgress", completed, deleteCandidates.Length, item.KeyName);
                OnPropertyChanged(nameof(ProgressText));
                var outcome = await _workspace.DeleteOrUndoFileTypeBatchItemAsync(item, batchCancellation.Token, operationId);
                switch (outcome.Status)
                {
                    case FileTypeBatchOperationStatus.ConfirmedSuccess: successes++; break;
                    case FileTypeBatchOperationStatus.ConfirmedFailure: failures++; break;
                    case FileTypeBatchOperationStatus.Skipped: skipped++; break;
                    case FileTypeBatchOperationStatus.Uncertain: uncertain++; break;
                    case FileTypeBatchOperationStatus.Cancelled: break;
                }
                if (outcome.Status is FileTypeBatchOperationStatus.ConfirmedFailure or FileTypeBatchOperationStatus.Uncertain)
                    FrontendDebugLog.Warning(nameof(FileTypeBatchManagementViewModel),
                        $"BatchItemResult: OperationId={operationId}, Source={item.Entry.BackendRegistryPath}, Status={outcome.Status}, ErrorCode={outcome.ErrorCode}.");
                completed++;
            }
            var cancelled = deleteCandidates.Length - completed;
            ResultText = _localization.Format("FileTypeBatchResult", successes, failures, skipped, cancelled, uncertain);
            OnPropertyChanged(nameof(ResultText));
            FrontendDebugLog.Info(nameof(FileTypeBatchManagementViewModel),
                $"BatchCompleted: OperationId={operationId}, Requested={Items.Count}, Selected={deleteCandidates.Length}, Criteria=SelectedActionAndPhysicalPath, Success={successes}, Failed={failures}, Skipped={skipped}, Cancelled={cancelled}, Uncertain={uncertain}.");
            _deferRefresh = false;
            await RefreshAsync();
        }
        finally
        {
            _deferRefresh = false;
            _batchCancellation = null;
            ProgressText = null;
            OnPropertyChanged(nameof(ProgressText));
            IsBusy = false;
            NotifyBulkCommands();
        }
    }

    [RelayCommand]
    private void CancelBatch() => _batchCancellation?.Cancel();

    private IEnumerable<ContextMenuItemViewModel> SelectedItems()
        => ActionGroups.Where(static group => group.IsSelected)
            .SelectMany(static group => group.Registrations)
            .Where(static registration => registration.IsIncluded)
            .Select(static registration => registration.Item);

    [RelayCommand(CanExecute = nameof(CanUndoAll))]
    private Task UndoAllAsync() => RunSelectedBatchAsync(
        SelectedItems().Where(static item => item.IsDeleted && item.HasBackup).ToArray(),
        (item, token) => _workspace.DeleteOrUndoFileTypeBatchItemAsync(item, token), "Undo");

    [RelayCommand]
    private async Task DeleteItemAsync(ContextMenuItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.IsDeleted && !CanDeleteBatchItem(item))
        {
            await FrontendMessageBox.ShowErrorAsync(ProtectedDeleteText, Title);
            return;
        }

        var outcome = await _workspace.DeleteOrUndoFileTypeBatchItemAsync(item, CancellationToken.None);
        ResultText = _localization.Format("FileTypeBatchResult",
            outcome.Status == FileTypeBatchOperationStatus.ConfirmedSuccess ? 1 : 0,
            outcome.Status == FileTypeBatchOperationStatus.ConfirmedFailure ? 1 : 0,
            outcome.Status == FileTypeBatchOperationStatus.Skipped ? 1 : 0,
            outcome.Status == FileTypeBatchOperationStatus.Cancelled ? 1 : 0,
            outcome.Status == FileTypeBatchOperationStatus.Uncertain ? 1 : 0);
        OnPropertyChanged(nameof(ResultText));
        await RefreshAsync();
    }

    private Task SetAllEnabledAsync(bool enable) => RunSelectedBatchAsync(
        SelectedItems().Where(item => item.CanToggle && !item.IsToggleOutcomeUncertain
            && item.IsEnabled != enable).ToArray(),
        async (item, _) =>
        {
            var applied = await _workspace.SetEnabledAsync(item, enable);
            return new(item.IsToggleOutcomeUncertain ? FileTypeBatchOperationStatus.Uncertain
                : applied ? FileTypeBatchOperationStatus.ConfirmedSuccess
                : FileTypeBatchOperationStatus.ConfirmedFailure);
        },
        enable ? "Enable" : "Disable");

    private async Task RunSelectedBatchAsync(IReadOnlyList<ContextMenuItemViewModel> targets,
        Func<ContextMenuItemViewModel, CancellationToken, Task<FileTypeBatchOperationOutcome>> operation,
        string operationName)
    {
        if (targets.Count == 0) return;
        IsBusy = true;
        NotifyBulkCommands();
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        var operationId = Guid.NewGuid();
        var success = 0;
        var failed = 0;
        var uncertain = 0;
        var skipped = Items.Count - targets.Count;
        var completed = 0;
        try
        {
            _deferRefresh = true;
            foreach (var item in targets)
            {
                if (cancellation.IsCancellationRequested) break;
                ProgressText = _localization.Format("FileTypeBatchProgress", completed, targets.Count, item.KeyName);
                OnPropertyChanged(nameof(ProgressText));
                var outcome = await operation(item, cancellation.Token);
                switch (outcome.Status)
                {
                    case FileTypeBatchOperationStatus.ConfirmedSuccess: success++; break;
                    case FileTypeBatchOperationStatus.ConfirmedFailure: failed++; break;
                    case FileTypeBatchOperationStatus.Uncertain: uncertain++; break;
                    case FileTypeBatchOperationStatus.Skipped: skipped++; break;
                }
                completed++;
                if (outcome.Status is FileTypeBatchOperationStatus.ConfirmedFailure or FileTypeBatchOperationStatus.Uncertain)
                    FrontendDebugLog.Warning(nameof(FileTypeBatchManagementViewModel),
                        $"BatchItemResult: OperationId={operationId}, Operation={operationName}, Source={item.Entry.BackendRegistryPath}, Status={outcome.Status}, ErrorCode={outcome.ErrorCode}.");
            }
            ResultText = _localization.Format("FileTypeBatchResult", success, failed, skipped,
                targets.Count - completed, uncertain);
            OnPropertyChanged(nameof(ResultText));
            FrontendDebugLog.Info(nameof(FileTypeBatchManagementViewModel),
                $"BatchCompleted: OperationId={operationId}, Operation={operationName}, Requested={Items.Count}, Selected={targets.Count}, Success={success}, Failed={failed}, Skipped={skipped}, Cancelled={targets.Count - completed}, Uncertain={uncertain}.");
            _deferRefresh = false;
            await RefreshAsync();
        }
        finally
        {
            _deferRefresh = false;
            _batchCancellation = null;
            ProgressText = null;
            OnPropertyChanged(nameof(ProgressText));
            IsBusy = false;
            NotifyBulkCommands();
        }
    }

    private async Task<bool> SetEnabledAsync(ContextMenuItemViewModel item, bool enable)
    {
        var success = await _workspace.SetEnabledAsync(item, enable);
        if (success && !_deferRefresh)
        {
            await RefreshAsync();
        }

        return success;
    }

    private void OnBackendAvailabilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuWorkspaceService.IsBackendReady))
            foreach (var item in Items) item.IsBackendAvailable = _workspace.IsBackendReady;
    }

    private void OnItemsRemoved(object? sender, MenuItemsRemovedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Apply()
        {
            if (_disposed || e.Request.SpecialItem is not null) return;
            foreach (var item in Items.Where(item => e.ItemIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray())
            { item.PropertyChanged -= OnItemPropertyChanged; Items.Remove(item); item.Dispose(); }
            RebuildGroups();
            NotifyBulkCommands();
        }
        if (dispatcher is not null && !dispatcher.CheckAccess()) _ = dispatcher.BeginInvoke((Action)Apply); else Apply();
    }

    private void ApplySnapshot(IReadOnlyList<ContextMenuEntry> entries)
    {
        entries = MenuListData.Current(entries);
        var existing = Items.ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (existing.Remove(entry.Id, out var current))
            {
                if (!current.IsToggleBusy && !current.IsAttributesBusy) current.Update(entry);
                continue;
            }

            var item = new ContextMenuItemViewModel(
                entry,
                _localization,
                _iconPreviewService,
                _actionsService,
                SetEnabledAsync) { IsBackendAvailable = _workspace.IsBackendReady };
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        foreach (var stale in existing.Values.Where(item => !item.IsToggleBusy && !item.IsAttributesBusy && MenuListData.AllowsRemoval(entries, item.Entry)))
        {
            stale.PropertyChanged -= OnItemPropertyChanged;
            stale.Dispose();
            Items.Remove(stale);
        }

        RebuildGroups();

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowPlaceholder));
        NotifyBulkCommands();
    }

    private void RebuildGroups()
    {
        var selectedGroups = ActionGroups.Where(static group => group.IsSelected)
            .Select(static group => group.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedPaths = ActionGroups.SelectMany(static group => group.Registrations)
            .Where(static item => !item.IsIncluded)
            .Select(static item => item.Item.Entry.BackendRegistryPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ActionGroups)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
            foreach (var registration in group.Registrations)
                registration.PropertyChanged -= OnRegistrationPropertyChanged;
        }
        ActionGroups.Clear();
        foreach (var items in Items.GroupBy(item => item.Entry.EntryKind == ContextMenuEntryKind.ShellExtension
                     ? item.Entry.HandlerClsid ?? item.KeyName : item.KeyName, StringComparer.OrdinalIgnoreCase))
        {
            var first = items.First();
            var match = items.Min(static item => item.Entry.FileTypeBatchMatchKind);
            var evidence = match switch
            {
                FileTypeBatchMatchKind.ExactAction => _localization.Translate("FileTypeBatchExactEvidence"),
                FileTypeBatchMatchKind.SameModule => _localization.Translate("FileTypeBatchModuleEvidence"),
                _ => _localization.Translate("FileTypeBatchCandidateEvidence")
            };
            var registrations = items.Select(item => new FileTypeBatchRegistration(item,
                (item.Entry.FileTypeBatchMatchKind != FileTypeBatchMatchKind.RegistrationFamilyCandidate
                 || selectedGroups.Contains(items.Key))
                && !excludedPaths.Contains(item.Entry.BackendRegistryPath),
                item.Entry.FileTypeBatchMatchKind switch
                {
                    FileTypeBatchMatchKind.ExactAction => _localization.Translate("FileTypeBatchExactEvidence"),
                    FileTypeBatchMatchKind.SameModule => _localization.Translate("FileTypeBatchModuleEvidence"),
                    _ => _localization.Translate("FileTypeBatchCandidateEvidence")
                })).ToArray();
            foreach (var registration in registrations)
                registration.PropertyChanged += OnRegistrationPropertyChanged;
            var label = string.Equals(first.DisplayName, first.KeyName, StringComparison.Ordinal)
                ? first.KeyName : $"{first.DisplayName} ({first.KeyName})";
            var group = new FileTypeBatchActionGroup(items.Key, label, match, registrations, evidence)
            {
                IsSelected = selectedGroups.Contains(items.Key)
                    || !_hasInitializedSelection
                       && string.Equals(items.Key, Query.EntryKind == ContextMenuEntryKind.ShellExtension
                           ? Query.HandlerClsid : Query.KeyName, StringComparison.OrdinalIgnoreCase)
                       && match == FileTypeBatchMatchKind.ExactAction
            };
            group.PropertyChanged += OnGroupPropertyChanged;
            ActionGroups.Add(group);
        }
        _hasInitializedSelection = true;
        OnPropertyChanged(nameof(CountText));
        NotifyBulkCommands();
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileTypeBatchActionGroup.IsSelected)) return;
        if (sender is FileTypeBatchActionGroup
            { IsSelected: true, MatchKind: FileTypeBatchMatchKind.RegistrationFamilyCandidate } candidate)
            foreach (var registration in candidate.Registrations)
                registration.IsIncluded = true;
        NotifyBulkCommands();
    }

    private void OnRegistrationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileTypeBatchRegistration.IsIncluded)) NotifyBulkCommands();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContextMenuItemViewModel.IsEnabled)
            or nameof(ContextMenuItemViewModel.IsDeleted)
            or nameof(ContextMenuItemViewModel.IsPresentInRegistry))
        {
            OnPropertyChanged(nameof(CanDisableAll));
            OnPropertyChanged(nameof(CanEnableAll));
            OnPropertyChanged(nameof(CanDeleteAll));
            OnPropertyChanged(nameof(CanUndoAll));
            NotifyBulkCommands();
        }
    }

    private void NotifyBulkCommands()
    {
        DisableAllCommand.NotifyCanExecuteChanged();
        EnableAllCommand.NotifyCanExecuteChanged();
        DeleteAllCommand.NotifyCanExecuteChanged();
        UndoAllCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IdentityText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(SourceRegistryPathLabel));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(DisableAllText));
        OnPropertyChanged(nameof(EnableAllText));
        OnPropertyChanged(nameof(DeleteAllText));
        OnPropertyChanged(nameof(UndoAllText));
        OnPropertyChanged(nameof(ProtectedDeleteText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    public static bool CanDeleteBatchItem(ContextMenuItemViewModel item)
        => !item.IsDeleted
           && item.IsPresentInRegistry
           && !IsProtectedBatchDeleteItem(item)
           && !string.IsNullOrWhiteSpace(item.Entry.FileTypeBatchFingerprint);

    public static bool IsProtectedBatchDeleteItem(ContextMenuItemViewModel item)
        => ProtectedMenuItemGuard.IsProtectedFileTypeBatchDeleteItem(item);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backendClient.ItemsRemoved -= OnItemsRemoved;
        _workspace.PropertyChanged -= OnBackendAvailabilityChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        _batchCancellation?.Cancel();
        foreach (var group in ActionGroups)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
            foreach (var registration in group.Registrations)
                registration.PropertyChanged -= OnRegistrationPropertyChanged;
        }
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            item.Dispose();
        }
    }
}
