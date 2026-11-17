using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.Views;

namespace ContextMenuMgr.Frontend.ViewModels;

internal enum ApplicationGroupOperation { Disable, Delete, Restore }

public partial class ApplicationGroupsPageViewModel : ObservableObject, IDisposable, INavigationPresentation
{
    private const int RebuildDebounceMilliseconds = 120;

    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly GlobalSearchNavigationFilterService _navigationFilterService;
    private readonly ContextMenuApplicationIdentityService _identityService;
    private readonly DelayedLoadingPresentation _loadingPresentation = new();
    private bool _applyingNavigationFilter;
    private CancellationTokenSource? _rebuildCts;
    private bool _presentationActive;
    private bool _rebuildPending;

    public ApplicationGroupsPageViewModel(
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        GlobalSearchNavigationFilterService navigationFilterService,
        ContextMenuApplicationIdentityService identityService)
    {
        _workspace = workspace;
        _localization = localization;
        _navigationFilterService = navigationFilterService;
        _identityService = identityService;
        _loadingPresentation.PropertyChanged += OnLoadingPresentationChanged;
        _workspace.Items.CollectionChanged += OnItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }
        _localization.LanguageChanged += OnLanguageChanged;
        _navigationFilterService.FilterRequested += OnNavigationFilterRequested;
        IsLoading = true;
        if (!ApplyPendingNavigationRequest())
        {
            // The frame activates presentation after its complete Fluent transition.
            // Group metadata is computed off-thread and rows remain virtualized.
            ScheduleRebuildGroups();
        }
    }

    public ObservableCollection<ApplicationGroupViewModel> Groups { get; } = [];

    public string Title => _localization.Translate("ApplicationGroupsPageTitle");
    public string Description => _localization.Translate("ApplicationGroupsPageDescription");
    public string NoItemsText => _localization.Translate("ApplicationGroupsNoItems");
    public string SearchLabel => _localization.Translate("SearchLabel");
    public string NavigationFilterBannerText => _localization.Translate("ApplicationGroupFilterActive");
    public string ClearFilterText => _localization.Translate("ClearApplicationGroupFilter");
    public string LoadingText => _localization.Translate("LoadingItemsText");
    public string DeleteText => _localization.Translate("Delete");
    public string RestoreText => _localization.Translate("ApplicationGroupRestore");
    public string PermanentDeleteText => _localization.Translate("PermanentDelete");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBulkIdle))]
    public partial bool IsBulkBusy { get; private set; }

    public bool IsBulkIdle => !IsBulkBusy;

    private string? _bulkGroupIdentity;

    internal bool IsGroupExecuting(string identity) => IsBulkBusy
        && string.Equals(_bulkGroupIdentity, identity, StringComparison.OrdinalIgnoreCase);

    partial void OnIsBulkBusyChanged(bool value)
    {
        foreach (var group in Groups) group.RefreshSelectionState();
        DeleteOrUndoItemCommand.NotifyCanExecuteChanged();
        PermanentlyDeleteItemCommand.NotifyCanExecuteChanged();
    }

    internal Task ApplyGroupSelectionAsync(ApplicationGroupViewModel group, ApplicationGroupOperation operation)
        => ApplySelectionAsync(group.Identity, group.AllItems.Where(static item => item.IsApplicationGroupChecked).ToArray(), operation, requireChecked: true);

    private bool CanDeleteOrUndoItem(ContextMenuItemViewModel? item) => IsBulkIdle && item is not null
        && (item.IsDeleted ? ApplicationGroupViewModel.CanRestoreItem(item) : ApplicationGroupViewModel.CanDeleteItem(item));

    private bool CanPermanentlyDeleteItem(ContextMenuItemViewModel? item) => IsBulkIdle && item is not null
        && ApplicationGroupViewModel.CanPurgeItem(item);

    [RelayCommand(CanExecute = nameof(CanDeleteOrUndoItem))]
    private Task DeleteOrUndoItemAsync(ContextMenuItemViewModel? item) => item is null ? Task.CompletedTask
        : ApplySelectionAsync(GetGroupIdentity(item), [item], item.IsDeleted ? ApplicationGroupOperation.Restore : ApplicationGroupOperation.Delete, requireChecked: false);

    [RelayCommand(CanExecute = nameof(CanPermanentlyDeleteItem))]
    private Task PermanentlyDeleteItemAsync(ContextMenuItemViewModel? item) => item is null ? Task.CompletedTask
        : PurgeSelectionAsync(GetGroupIdentity(item), [item], requireChecked: false);

    internal Task PurgeGroupSelectionAsync(ApplicationGroupViewModel group)
        => PurgeSelectionAsync(group.Identity, group.AllItems.Where(static item => item.IsApplicationGroupChecked).ToArray(), requireChecked: true);

    private async Task PurgeSelectionAsync(string identity, IReadOnlyList<ContextMenuItemViewModel> items, bool requireChecked)
    {
        if (IsBulkBusy) return;
        var selected = items.Where(ApplicationGroupViewModel.CanPurgeItem)
            .Select(item => (Item: item, Category: item.Category, Reference: new ContextMenuMgr.Contracts.DeletedItemReference(item.Id, item.DeletedAtUtc))).ToArray();
        if (selected.Length == 0) return;
        _bulkGroupIdentity = identity;
        IsBulkBusy = true;
        try
        {
            if (!await FrontendMessageBox.ShowConfirmAsync(_localization.Format("PurgeDeletedItemsPrompt", identity, selected.Length),
                PermanentDeleteText, PermanentDeleteText, _localization.Translate("DialogCancel"))) return;
            foreach (var category in selected.GroupBy(item => item.Category))
            {
                var references = category.Where(row => _workspace.Items.Contains(row.Item)
                    && (!requireChecked || row.Item.IsApplicationGroupChecked)
                    && ApplicationGroupViewModel.CanPurgeItem(row.Item)
                    && row.Item.DeletedAtUtc == row.Reference.DeletedAtUtc
                    && string.Equals(GetGroupIdentity(row.Item), identity, StringComparison.OrdinalIgnoreCase))
                    .Select(row => row.Reference).ToArray();
                foreach (var chunk in references.Chunk(4096))
                    await _workspace.PurgeDeletedItemsAsync(new() { Category = category.Key, Items = chunk });
            }
        }
        finally
        {
            IsBulkBusy = false;
            _bulkGroupIdentity = null;
        }
    }

    private async Task ApplySelectionAsync(string identity, IReadOnlyList<ContextMenuItemViewModel> items,
        ApplicationGroupOperation operation, bool requireChecked)
    {
        if (IsBulkBusy) return;
        var selected = items.Select(item => (Item: item, WasDeleted: item.IsDeleted, DeletedAtUtc: item.DeletedAtUtc)).ToArray();
        _bulkGroupIdentity = identity;
        IsBulkBusy = true;
        try
        {
            await ToggleBusyPresentation.WaitForFirstFrameAsync();
            foreach (var row in selected)
            {
                var item = row.Item;
                if (requireChecked && !item.IsApplicationGroupChecked || !_workspace.Items.Contains(item)
                    || !string.Equals(GetGroupIdentity(item), identity, StringComparison.OrdinalIgnoreCase)) continue;
                if (operation == ApplicationGroupOperation.Delete)
                {
                    if (row.WasDeleted || !ApplicationGroupViewModel.CanDeleteItem(item)) continue;
                    await _workspace.DeleteOrUndoAsync(item);
                    if (item.IsDeleted && !item.IsDeleteOutcomeUncertain) item.IsApplicationGroupChecked = false;
                }
                else if (operation == ApplicationGroupOperation.Restore)
                {
                    if (!row.WasDeleted || !ApplicationGroupViewModel.CanRestoreItem(item) || item.DeletedAtUtc != row.DeletedAtUtc) continue;
                    await _workspace.DeleteOrUndoAsync(item);
                    if (!item.IsDeleted && !item.IsDeleteOutcomeUncertain) item.IsApplicationGroupChecked = false;
                }
                else
                {
                    if (!ApplicationGroupViewModel.CanDisableItem(item)) continue;
                    await _workspace.SetEnabledAsync(item, false);
                    if (!item.IsEnabled && !item.IsToggleOutcomeUncertain) item.IsApplicationGroupChecked = false;
                }
            }
        }
        finally
        {
            IsBulkBusy = false;
            _bulkGroupIdentity = null;
        }
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NavigationFocusItemId { get; private set; }

    [ObservableProperty]
    public partial string? NavigationFocusGroupIdentity { get; private set; }

    [ObservableProperty]
    public partial bool IsNavigationFilterActive { get; private set; }

    public bool IsEmpty => Groups.Count == 0 && !IsLoading && !IsListLoading;

    public bool IsListLoading => !_workspace.HasSnapshot && _loadingPresentation.IsVisible;

    public bool ShowListContent => !IsListLoading && !IsEmpty;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    partial void OnIsLoadingChanged(bool value)
    {
        _loadingPresentation.Update(value);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowListContent));
    }

    private void OnLoadingPresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsListLoading));
        OnPropertyChanged(nameof(ShowListContent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_applyingNavigationFilter)
        {
            return;
        }

        ClearNavigationFilterState();
        ScheduleRebuildGroups();
    }

    public void OnNavigationStarted() => _presentationActive = false;

    public void OnNavigationCompleted()
    {
        _presentationActive = true;
        if (_rebuildPending) ScheduleRebuildGroups(debounce: false);
    }

    public void OnNavigatingAway()
    {
        _presentationActive = false;
        if (_rebuildCts is { IsCancellationRequested: false }) _rebuildCts.Cancel();
    }

    private void ScheduleRebuildGroups(bool debounce = true)
    {
        _rebuildPending = true;
        if (!_presentationActive) return;
        _rebuildCts?.Cancel();
        _rebuildCts?.Dispose();
        var cts = _rebuildCts = new CancellationTokenSource();
        _ = RebuildAfterDebounceAsync(cts, debounce);
    }

    private async Task RebuildAfterDebounceAsync(CancellationTokenSource cts, bool debounce)
    {
        try
        {
            if (debounce) await Task.Delay(RebuildDebounceMilliseconds, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_rebuildCts, cts))
            {
                return;
            }

            await RebuildGroupsAsync(cts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(nameof(ApplicationGroupsPageViewModel), $"RebuildGroups failed: {ex}");
            if (ReferenceEquals(_rebuildCts, cts)) IsLoading = false;
        }
    }

    private async Task RebuildGroupsAsync(CancellationTokenSource cts)
    {
        // Capture immutable search inputs on the dispatcher; identity resolution and matching
        // must not run on the input/animation thread or read mutable ViewModels in Task.Run.
        var query = SearchText;
        var focusedIdentity = IsNavigationFilterActive ? NavigationFocusGroupIdentity : null;
        var focusedItemId = NavigationFocusItemId;
        var candidates = _workspace.Items.Where(item => !item.IsWindows11ContextMenu && IsClassicCategory(item.Category))
            .Select(item => (Item: item, Entry: item.Entry, Fields: new[] { item.DisplayName, item.CategoryName,
                item.StateLabel, item.RegistryPath, item.UserNote, item.Entry.FilePath, item.Entry.HandlerClsid, item.Entry.CommandText })).ToArray();
        var specs = await Task.Run(() => candidates
            .Select(candidate => (candidate.Item, Identity: _identityService.GetIdentity(candidate.Entry).Identity, candidate.Fields))
            .GroupBy(candidate => candidate.Identity, StringComparer.OrdinalIgnoreCase)
            .Where(group => focusedIdentity is null || string.Equals(group.Key, focusedIdentity, StringComparison.OrdinalIgnoreCase))
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => (Identity: group.Key,
                AllItems: group.Select(candidate => candidate.Item).ToArray(),
                Items: group.Where(candidate => focusedIdentity is not null || string.IsNullOrWhiteSpace(query)
                    || ContextMenuSearchMatcher.MatchesFields(query, [candidate.Identity, .. candidate.Fields]))
                    .Select(candidate => candidate.Item).ToArray()))
            .Where(group => group.Items.Length > 0).ToArray(), cts.Token);
        if (cts.IsCancellationRequested || !ReferenceEquals(_rebuildCts, cts)) return;
        if (focusedIdentity is not null && !specs.SelectMany(spec => spec.Items).Any(item => item.Id.Equals(focusedItemId, StringComparison.OrdinalIgnoreCase)))
        {
            ClearNavigationFilterState();
            ScheduleRebuildGroups(debounce: false);
            return;
        }
        var existing = Groups.ToDictionary(group => group.Identity, StringComparer.OrdinalIgnoreCase);
        var groups = specs.Select(spec =>
        {
            if (!existing.TryGetValue(spec.Identity, out var group))
                group = new ApplicationGroupViewModel(spec.Identity, spec.Items, spec.AllItems, this, _localization);
            else group.UpdateItems(spec.Items, spec.AllItems);
            return group;
        }).ToArray();
        IncrementalCollection.Apply(Groups, groups);
        _rebuildPending = false;
        IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowListContent));
    }

    private string GetGroupIdentity(ContextMenuItemViewModel item) => _identityService.GetIdentity(item).Identity;

    private static bool IsClassicCategory(ContextMenuMgr.Contracts.ContextMenuCategory category) => category is
        ContextMenuMgr.Contracts.ContextMenuCategory.File
        or ContextMenuMgr.Contracts.ContextMenuCategory.AllFileSystemObjects
        or ContextMenuMgr.Contracts.ContextMenuCategory.Folder
        or ContextMenuMgr.Contracts.ContextMenuCategory.Directory
        or ContextMenuMgr.Contracts.ContextMenuCategory.DirectoryBackground
        or ContextMenuMgr.Contracts.ContextMenuCategory.DesktopBackground
        or ContextMenuMgr.Contracts.ContextMenuCategory.Drive
        or ContextMenuMgr.Contracts.ContextMenuCategory.Library
        or ContextMenuMgr.Contracts.ContextMenuCategory.Computer
        or ContextMenuMgr.Contracts.ContextMenuCategory.RecycleBin;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ContextMenuItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ContextMenuItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }

        ScheduleRebuildGroups();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ContextMenuItemViewModel selectedItem
            && e.PropertyName is nameof(ContextMenuItemViewModel.IsApplicationGroupChecked)
                or nameof(ContextMenuItemViewModel.CanPrimaryAction) or nameof(ContextMenuItemViewModel.CanToggle)
                or nameof(ContextMenuItemViewModel.IsAttributesBusy) or nameof(ContextMenuItemViewModel.IsAcknowledgementBusy)
                or nameof(ContextMenuItemViewModel.IsAcknowledgementUncertain) or nameof(ContextMenuItemViewModel.IsToggleOutcomeUncertain)
                or nameof(ContextMenuItemViewModel.HasBackup) or nameof(ContextMenuItemViewModel.IsDeleted)
                or nameof(ContextMenuItemViewModel.DeletedAtUtc))
        {
            foreach (var group in Groups.Where(group => group.AllItems.Contains(selectedItem))) group.RefreshSelectionState();
            DeleteOrUndoItemCommand.NotifyCanExecuteChanged();
            PermanentlyDeleteItemCommand.NotifyCanExecuteChanged();
            return;
        }
        if (sender is ContextMenuItemViewModel toggleItem
            && e.PropertyName is (nameof(ContextMenuItemViewModel.IsEnabled)
                or nameof(ContextMenuItemViewModel.IsToggleBusy)))
        {
            // Keep the click path free of group-wide scans. The busy item is
            // already excluded by CanToggle from a concurrent batch action.
            if (toggleItem.IsToggleBusy)
            {
                return;
            }

            foreach (var group in Groups.Where(group => group.Items.Contains(toggleItem)))
            {
                group.RefreshSelectionState();
            }

            // State labels can participate in the local search. Defer that
            // regroup until a pending mutation finishes; unfiltered groups stay put.
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                ScheduleRebuildGroups();
            }

            return;
        }

        if (e.PropertyName is nameof(ContextMenuItemViewModel.UserNote)
            or nameof(ContextMenuItemViewModel.DisplayName))
        {
            if (sender is ContextMenuItemViewModel item
                && e.PropertyName == nameof(ContextMenuItemViewModel.DisplayName)
                && IsNavigationFilterActive
                && string.Equals(item.Id, NavigationFocusItemId, StringComparison.OrdinalIgnoreCase))
            {
                SetSearchTextFromNavigationFilter(item.DisplayName);
            }

            ScheduleRebuildGroups();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(NoItemsText));
        OnPropertyChanged(nameof(SearchLabel));
        OnPropertyChanged(nameof(NavigationFilterBannerText));
        OnPropertyChanged(nameof(ClearFilterText));
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(RestoreText));
        OnPropertyChanged(nameof(PermanentDeleteText));
        foreach (var group in Groups) group.RefreshSelectionState();
        ScheduleRebuildGroups();
    }

    private void OnNavigationFilterRequested(object? sender, GlobalSearchFilterRequestedEventArgs e)
    {
        if (e.TargetPageType != typeof(ContextMenuMgr.Frontend.Views.Pages.ApplicationGroupsPage))
        {
            return;
        }

        if (ApplyNavigationRequest(e.ItemId, e.FilterText))
        {
            _navigationFilterService.ConsumePendingRequest(e.TargetPageType);
        }
    }

    private bool ApplyPendingNavigationRequest()
    {
        var pending = _navigationFilterService.ConsumePendingRequest(
            typeof(ContextMenuMgr.Frontend.Views.Pages.ApplicationGroupsPage));
        if (pending is null)
        {
            return false;
        }

        return ApplyNavigationRequest(pending.ItemId, pending.FilterText);
    }

    private bool ApplyNavigationRequest(string? itemId, string? fallbackFilterText)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        var targetItem = _workspace.Items.FirstOrDefault(item =>
            !item.IsWindows11ContextMenu
            && IsClassicCategory(item.Category)
            && string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase));

        if (targetItem is null)
        {
            FrontendDebugLog.Warning(
                nameof(ApplicationGroupsPageViewModel),
                $"ApplicationGroupNavigationFilterItemNotFound: ItemId={itemId}.");

            ClearNavigationFilterState();
            SetSearchTextFromNavigationFilter(string.IsNullOrWhiteSpace(fallbackFilterText)
                ? itemId
                : fallbackFilterText);

            ScheduleRebuildGroups();
            return true;
        }

        var groupIdentity = GetGroupIdentity(targetItem);
        NavigationFocusItemId = targetItem.Id;
        NavigationFocusGroupIdentity = groupIdentity;
        IsNavigationFilterActive = true;

        SetSearchTextFromNavigationFilter(targetItem.DisplayName);

        ScheduleRebuildGroups(debounce: false);

        FrontendDebugLog.Info(
            nameof(ApplicationGroupsPageViewModel),
            "ApplicationGroupNavigationFilterApplied: "
            + $"ItemId={targetItem.Id}, "
            + $"DisplayName='{SanitizeLogText(targetItem.DisplayName)}', "
            + $"GroupIdentity='{SanitizeLogText(groupIdentity)}'.");
        return true;
    }

    [RelayCommand]
    private void ClearNavigationFilter()
    {
        ClearNavigationFilterState();
        SearchText = string.Empty;
        ScheduleRebuildGroups();
    }

    private void SetSearchTextFromNavigationFilter(string text)
    {
        _applyingNavigationFilter = true;
        try
        {
            SearchText = text;
        }
        finally
        {
            _applyingNavigationFilter = false;
        }
    }

    private void ClearNavigationFilterState()
    {
        NavigationFocusItemId = null;
        NavigationFocusGroupIdentity = null;
        IsNavigationFilterActive = false;
    }

    private static string SanitizeLogText(string? text)
    {
        return (text ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _loadingPresentation.PropertyChanged -= OnLoadingPresentationChanged;
        _loadingPresentation.Dispose();
        _rebuildCts?.Cancel();
        _rebuildCts?.Dispose();
        _rebuildCts = null;
        _workspace.Items.CollectionChanged -= OnItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _localization.LanguageChanged -= OnLanguageChanged;
        _navigationFilterService.FilterRequested -= OnNavigationFilterRequested;
    }
}

public partial class ApplicationGroupViewModel : ObservableObject
{
    private readonly ApplicationGroupsPageViewModel _owner;
    private readonly LocalizationService _localization;
    private bool _updatingSelection;

    public ApplicationGroupViewModel(
        string identity,
        IReadOnlyList<ContextMenuItemViewModel> items,
        IReadOnlyList<ContextMenuItemViewModel> allItems,
        ApplicationGroupsPageViewModel owner,
        LocalizationService localization)
    {
        Identity = identity;
        Items = new ObservableCollection<ContextMenuItemViewModel>(items);
        AllItems = allItems;
        _owner = owner;
        _localization = localization;
    }

    public string Identity { get; }
    public ObservableCollection<ContextMenuItemViewModel> Items { get; }
    public IReadOnlyList<ContextMenuItemViewModel> AllItems { get; private set; }
    public void UpdateItems(IReadOnlyList<ContextMenuItemViewModel> items, IReadOnlyList<ContextMenuItemViewModel> allItems)
    {
        AllItems = allItems;
        if (!Items.SequenceEqual(items)) IncrementalCollection.Apply(Items, items);
        OnPropertyChanged(nameof(CountText));
        RefreshSelectionState();
    }
    public string CountText => _localization.Format("ApplicationGroupCountFormat", Items.Count);
    public string DeleteText => _localization.Translate("Delete");
    public string DisableText => _localization.Translate("ApplicationGroupDisableSelected");
    public string RestoreText => _localization.Translate("ApplicationGroupRestore");
    public string PermanentDeleteText => _localization.Translate("PermanentDelete");
    public string SelectAllText => _localization.Translate("ApplicationGroupSelectAll");
    public int SelectedCount => AllItems.Count(static item => item.IsApplicationGroupChecked);
    public bool HasSelection => SelectedCount > 0;
    public bool HasLiveSelection => AllItems.Any(static item => item.IsApplicationGroupChecked && !item.IsDeleted);
    public bool HasDeletedSelection => AllItems.Any(static item => item.IsApplicationGroupChecked && item.IsDeleted);
    public string SelectedCountText => HasSelection ? _localization.Format("SelectedItemsCount", SelectedCount) : string.Empty;
    public bool IsExecuting => _owner.IsGroupExecuting(Identity);
    public bool? IsAllVisibleChecked
    {
        get
        {
            var selected = Items.Count(static item => item.IsApplicationGroupChecked);
            return selected == 0 ? false : selected == Items.Count ? true : null;
        }
    }
    private bool CanToggleSelection => _owner.IsBulkIdle && Items.Count > 0;
    private bool CanDeleteSelected => _owner.IsBulkIdle && AllItems.Any(static item => item.IsApplicationGroupChecked && CanDeleteItem(item));
    private bool CanDisableSelected => _owner.IsBulkIdle && AllItems.Any(static item => item.IsApplicationGroupChecked && CanDisableItem(item));
    private bool CanRestoreSelected => _owner.IsBulkIdle && AllItems.Any(static item => item.IsApplicationGroupChecked && CanRestoreItem(item));
    private bool CanPurgeSelected => _owner.IsBulkIdle && AllItems.Any(static item => item.IsApplicationGroupChecked && CanPurgeItem(item));

    private static bool IsItemReady(ContextMenuItemViewModel item) => !item.IsDeleteBusy && !item.IsDeleteOutcomeUncertain
        && !item.IsToggleBusy && !item.IsToggleOutcomeUncertain && !item.IsAttributesBusy
        && !item.IsAcknowledgementBusy && !item.IsAcknowledgementUncertain;
    internal static bool CanDeleteItem(ContextMenuItemViewModel item) => IsItemReady(item) && item.CanDelete && item.CanPrimaryAction;
    internal static bool CanDisableItem(ContextMenuItemViewModel item) => IsItemReady(item) && item.IsEnabled && item.CanToggle;
    internal static bool CanRestoreItem(ContextMenuItemViewModel item) => IsItemReady(item) && item.IsDeleted && item.CanPrimaryAction && item.HasBackup;
    internal static bool CanPurgeItem(ContextMenuItemViewModel item) => IsItemReady(item) && item.IsDeleted && item.CanPrimaryAction;

    public void RefreshSelectionState()
    {
        if (_updatingSelection) return;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(DisableText));
        OnPropertyChanged(nameof(RestoreText));
        OnPropertyChanged(nameof(PermanentDeleteText));
        OnPropertyChanged(nameof(SelectAllText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasLiveSelection));
        OnPropertyChanged(nameof(HasDeletedSelection));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(IsAllVisibleChecked));
        OnPropertyChanged(nameof(IsExecuting));
        ToggleSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DisableSelectedCommand.NotifyCanExecuteChanged();
        RestoreSelectedCommand.NotifyCanExecuteChanged();
        PermanentlyDeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelection))]
    private void ToggleSelection()
    {
        var check = IsAllVisibleChecked != true;
        _updatingSelection = true;
        try
        {
            foreach (var item in Items) item.IsApplicationGroupChecked = check;
        }
        finally
        {
            _updatingSelection = false;
            RefreshSelectionState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedAsync() => _owner.ApplyGroupSelectionAsync(this, ApplicationGroupOperation.Delete);

    [RelayCommand(CanExecute = nameof(CanDisableSelected))]
    private Task DisableSelectedAsync() => _owner.ApplyGroupSelectionAsync(this, ApplicationGroupOperation.Disable);

    [RelayCommand(CanExecute = nameof(CanRestoreSelected))]
    private Task RestoreSelectedAsync() => _owner.ApplyGroupSelectionAsync(this, ApplicationGroupOperation.Restore);

    [RelayCommand(CanExecute = nameof(CanPurgeSelected))]
    private Task PermanentlyDeleteSelectedAsync() => _owner.PurgeGroupSelectionAsync(this);
}
