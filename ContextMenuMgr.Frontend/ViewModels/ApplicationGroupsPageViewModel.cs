using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.Views;

namespace ContextMenuMgr.Frontend.ViewModels;

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
            .Where(candidate => focusedIdentity is not null
                ? string.Equals(candidate.Identity, focusedIdentity, StringComparison.OrdinalIgnoreCase)
                : string.IsNullOrWhiteSpace(query) || ContextMenuSearchMatcher.MatchesFields(query, [candidate.Identity, .. candidate.Fields]))
            .GroupBy(candidate => candidate.Identity, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => (Identity: group.Key, Items: group.Select(candidate => candidate.Item).ToArray())).ToArray(), cts.Token);
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
                group = new ApplicationGroupViewModel(spec.Identity, spec.Items, _workspace, _localization);
            else group.UpdateItems(spec.Items);
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
                group.RefreshToggleActions();
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
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;

    public ApplicationGroupViewModel(
        string identity,
        IReadOnlyList<ContextMenuItemViewModel> items,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization)
    {
        Identity = identity;
        Items = new ObservableCollection<ContextMenuItemViewModel>(items);
        _workspace = workspace;
        _localization = localization;
    }

    public string Identity { get; }
    public ObservableCollection<ContextMenuItemViewModel> Items { get; }
    public void UpdateItems(IReadOnlyList<ContextMenuItemViewModel> items)
    {
        if (Items.SequenceEqual(items)) return;
        IncrementalCollection.Apply(Items, items);
        OnPropertyChanged(nameof(CountText));
        RefreshToggleActions();
    }
    public string CountText => _localization.Format("ApplicationGroupCountFormat", Items.Count);
    public string DisableAllText => _localization.Translate(
        IsDisabling ? "ApplicationGroupDisabling"
        : ShowEnableAll ? "ApplicationGroupEnableAll"
        : "ApplicationGroupDisableAll");
    public bool CanDisableAll => !IsDisabling && Items.Any(static item => item.IsEnabled && item.CanToggle);
    public bool CanEnableAll => !IsDisabling && Items.Any(static item => !item.IsEnabled && item.CanToggle);
    public bool ShowEnableAll => CanEnableAll && !CanDisableAll;
    public bool CanToggleAll => CanDisableAll || CanEnableAll;

    public void RefreshToggleActions()
    {
        OnPropertyChanged(nameof(CanDisableAll));
        OnPropertyChanged(nameof(CanEnableAll));
        OnPropertyChanged(nameof(ShowEnableAll));
        OnPropertyChanged(nameof(CanToggleAll));
        OnPropertyChanged(nameof(DisableAllText));
        DisableAllCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisableAllText))]
    [NotifyPropertyChangedFor(nameof(CanDisableAll))]
    [NotifyPropertyChangedFor(nameof(CanEnableAll))]
    [NotifyPropertyChangedFor(nameof(ShowEnableAll))]
    [NotifyPropertyChangedFor(nameof(CanToggleAll))]
    public partial bool IsDisabling { get; private set; }

    [RelayCommand(CanExecute = nameof(CanToggleAll))]
    private async Task DisableAllAsync()
    {
        var enable = !CanDisableAll;
        IsDisabling = true;
        DisableAllCommand.NotifyCanExecuteChanged();
        try
        {
            foreach (var item in Items.Where(item => item.CanToggle && item.IsEnabled != enable).ToArray())
            {
                await _workspace.SetEnabledAsync(item, enable);
            }
        }
        finally
        {
            IsDisabling = false;
            DisableAllCommand.NotifyCanExecuteChanged();
        }
    }
}
