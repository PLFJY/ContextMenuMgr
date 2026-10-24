using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
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
    private readonly FrontendSettingsService _settingsService;
    private readonly Func<Task> _backAsync;
    private bool _disposed;
    private bool _deferRefresh;

    public FileTypeBatchManagementViewModel(
        ContextMenuItemViewModel sourceItem,
        FileTypeBatchQuery query,
        IBackendClient backendClient,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        ContextMenuItemActionsService actionsService,
        FrontendSettingsService settingsService,
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
        _settingsService = settingsService;
        _backAsync = backAsync;

        ItemsView = new ListCollectionView(Items) { IsLiveSorting = true };
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ContextMenuItemViewModel.RegistryPath), ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ContextMenuItemViewModel.DisplayName), ListSortDirection.Ascending));

        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ContextMenuItemViewModel SourceItem { get; }

    public FileTypeBatchQuery Query { get; }

    public ObservableCollection<ContextMenuItemViewModel> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    public string Title => _localization.Format("FileTypeBatchTitle", SourceItem.DisplayName);

    public string IdentityText => !string.IsNullOrWhiteSpace(Query.CommandExecutablePath)
        ? _localization.Format("FileTypeBatchCommandIdentity", Query.CommandExecutablePath)
        : _localization.Format("FileTypeBatchClsidIdentity", Query.HandlerClsid ?? string.Empty);

    public string CountText => _localization.Format("FileTypeBatchCountFormat", Items.Count);

    public string SourceRegistryPathLabel => _localization.Translate("FileTypeBatchSourceRegistryPath");

    public string SourceRegistryPath => Query.SourceRegistryPath ?? SourceItem.RegistryPath;

    public string RefreshText => _localization.Translate("Refresh");

    public string BackText => _localization.Translate("Back");

    public string DisableAllText => _localization.Translate("ApplicationGroupDisableAll");

    public string EnableAllText => _localization.Translate("FileTypeBatchEnableAll");

    public string DeleteAllText => _localization.Translate("FileTypeBatchDeleteAll");

    public string UndoAllText => _localization.Translate("FileTypeBatchUndoAll");

    public string DeleteText => _localization.Translate("Delete");

    public string ProtectedDeleteText => _localization.Translate("FileTypeBatchProtectedDelete");

    public string EmptyText => IsBusy
        ? _localization.Translate("LoadingStatus")
        : _localization.Translate("FileTypeBatchNoItems");

    public bool ShowPlaceholder => Items.Count == 0;

    public bool CanDisableAll => !IsBusy && Items.Any(static item => item.IsEnabled && item.CanToggle);

    public bool CanEnableAll => !IsBusy && Items.Any(static item => !item.IsEnabled && item.CanToggle);

    public bool CanDeleteAll => !IsBusy && Items.Any(static item => CanDeleteBatchItem(item));

    public bool CanUndoAll => !IsBusy && Items.Any(static item => item.IsDeleted && item.HasBackup);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanDisableAll))]
    [NotifyPropertyChangedFor(nameof(CanEnableAll))]
    [NotifyPropertyChangedFor(nameof(CanDeleteAll))]
    [NotifyPropertyChangedFor(nameof(CanUndoAll))]
    public partial bool IsBusy { get; private set; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        NotifyBulkCommands();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (Items.Count == 0)
            {
                var cached = await _backendClient.ReadCachedListAsync<ContextMenuEntry>(new PipeRequest {
                    Command = PipeCommand.FindRelatedFileTypeMenuItems, FileTypeBatchQuery = Query }, cts.Token);
                if (cached is not null && !_disposed) ApplySnapshot(cached);
            }
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
            IsBusy = false;
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
        var deleteCandidates = Items.Where(static item => CanDeleteBatchItem(item)).ToArray();
        if (deleteCandidates.Length == 0)
        {
            return;
        }

        var shouldDelete = await FrontendMessageBox.ShowConfirmAsync(
            _localization.Format(
                "FileTypeBatchDeleteAllConfirmation",
                deleteCandidates.Length,
                Items.Count(static item => IsProtectedBatchDeleteItem(item))),
            _localization.Translate("FileTypeBatchDeleteAll"),
            _localization.Translate("FileTypeBatchDeleteAll"),
            _localization.Translate("DialogCancel"));
        if (!shouldDelete)
        {
            return;
        }

        IsBusy = true;
        NotifyBulkCommands();
        try
        {
            _deferRefresh = true;
            foreach (var item in deleteCandidates)
            {
                await _workspace.DeleteOrUndoAsync(item);
            }

            _deferRefresh = false;
            await RefreshAsync();
        }
        finally
        {
            _deferRefresh = false;
            IsBusy = false;
            NotifyBulkCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndoAll))]
    private async Task UndoAllAsync()
    {
        IsBusy = true;
        NotifyBulkCommands();
        try
        {
            _deferRefresh = true;
            foreach (var item in Items.Where(static item => item.IsDeleted && item.HasBackup).ToArray())
            {
                await _workspace.DeleteOrUndoAsync(item);
            }

            _deferRefresh = false;
            await RefreshAsync();
        }
        finally
        {
            _deferRefresh = false;
            IsBusy = false;
            NotifyBulkCommands();
        }
    }

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

        await _workspace.DeleteOrUndoAsync(item);
        await RefreshAsync();
    }

    private async Task SetAllEnabledAsync(bool enable)
    {
        IsBusy = true;
        NotifyBulkCommands();
        try
        {
            _deferRefresh = true;
            foreach (var item in Items.Where(item => item.CanToggle && item.IsEnabled != enable).ToArray())
            {
                await _workspace.SetEnabledAsync(item, enable);
            }

            _deferRefresh = false;
            await RefreshAsync();
        }
        finally
        {
            _deferRefresh = false;
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

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowPlaceholder));
        NotifyBulkCommands();
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
        OnPropertyChanged(nameof(SourceRegistryPathLabel));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(DisableAllText));
        OnPropertyChanged(nameof(EnableAllText));
        OnPropertyChanged(nameof(DeleteAllText));
        OnPropertyChanged(nameof(UndoAllText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(ProtectedDeleteText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    public static bool CanDeleteBatchItem(ContextMenuItemViewModel item)
        => !item.IsDeleted
           && item.IsPresentInRegistry
           && !IsProtectedBatchDeleteItem(item);

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
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            item.Dispose();
        }
    }
}
