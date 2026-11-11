using System.ComponentModel;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the approvals Page View Model.
/// </summary>
public partial class ApprovalsPageViewModel : ObservableObject, IDisposable
{
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly FrontendNavigationState _navigationState;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalsPageViewModel"/> class.
    /// </summary>
    public ApprovalsPageViewModel(
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        FrontendNavigationState navigationState)
    {
        _workspace = workspace;
        _localization = localization;
        _navigationState = navigationState;
        _localization.LanguageChanged += OnLanguageChanged;
        _navigationState.ApprovalsRequested += OnApprovalsRequested;
        _workspace.Items.CollectionChanged += OnItemsCollectionChanged;
        _workspace.WpsOfficeApprovalItems.CollectionChanged += OnItemsCollectionChanged;
        _workspace.OpenWithReviews.SnapshotChanged += OnOpenWithItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }
        foreach (var item in _workspace.WpsOfficeApprovalItems)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        ItemsView = new ListCollectionView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ApprovalQueueItemViewModel.DisplayName), ListSortDirection.Ascending));
        ((ListCollectionView)ItemsView).IsLiveSorting = true;
        ((ListCollectionView)ItemsView).IsLiveFiltering = true;
        foreach (var property in new[] { nameof(ApprovalQueueItemViewModel.DisplayName), nameof(ApprovalQueueItemViewModel.Subtitle),
            nameof(ApprovalQueueItemViewModel.RegistryPath), nameof(ApprovalQueueItemViewModel.Notes), nameof(ApprovalQueueItemViewModel.CategoryTags) })
            ((ListCollectionView)ItemsView).LiveFilteringProperties.Add(property);
        Items.CollectionChanged += OnRowsCollectionChanged;
        ((INotifyCollectionChanged)ItemsView).CollectionChanged += OnVisibleItemsChanged;
        RefreshLocalizedText();
        RebuildItems();
        ObserveFireAndForget(_workspace.RefreshWpsOfficeApprovalsAsync(), "RefreshWpsOfficeApprovalsAsync");
    }

    /// <summary>
    /// Gets the items.
    /// </summary>
    public ObservableCollection<IApprovalQueueItem> Items { get; } = [];

    /// <summary>
    /// Gets the items View.
    /// </summary>
    public ICollectionView ItemsView { get; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Gets or sets the search Text.
    /// </summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the selected Item.
    /// </summary>
    [ObservableProperty]
    public partial IApprovalQueueItem? SelectedItem { get; set; }

    public OpenWithReviewQueueService OpenWithReviews => _workspace.OpenWithReviews;

    [RelayCommand]
    private Task AcceptRegistrationAsync(OpenWithApprovalItemViewModel item)
        => OpenWithReviews.DecideAsync(item, OpenWithReviewDecision.Accept, CancellationToken.None);

    [RelayCommand]
    private Task RejectRegistrationAsync(OpenWithApprovalItemViewModel item)
        => OpenWithReviews.DecideAsync(item, OpenWithReviewDecision.Reject, CancellationToken.None);

    [RelayCommand]
    private void InspectRegistration(OpenWithApprovalItemViewModel item)
    {
        _navigationState.RequestOpenWith(item.Id);
    }

    public string AllowText => _localization.Translate("Allow");

    public string DenyText => _localization.Translate("Deny");

    public string KeepDisabledText => _localization.Translate("KeepDisabled");

    public string RemoveText => _localization.Translate("Remove");

    public string ConfirmRemoveText => _localization.Translate("ConfirmRemove");

    public string CancelText => _localization.Translate("DialogCancel");

    public string SearchLabel => _localization.Translate("ApprovalsSearchLabel");

    public string SelectAllText => _localization.Translate("SelectAllFilteredItems");

    /// <summary>
    /// Gets the tri-state header square for the current search view: true when every visible row is checked,
    /// null when only some are, false when none are (or nothing is visible).
    /// </summary>
    public bool? IsAllFilteredChecked
    {
        get
        {
            var visible = 0;
            var checkedVisible = 0;
            foreach (IApprovalQueueItem row in ItemsView)
            {
                visible++;
                if (row.IsChecked)
                {
                    checkedVisible++;
                }
            }

            if (visible == 0 || checkedVisible == 0)
            {
                return false;
            }

            return checkedVisible == visible ? true : null;
        }
    }

    /// <summary>
    /// Gets the number of rows checked for a bulk review decision, including rows hidden by the search filter.
    /// </summary>
    public int SelectedCount => Items.Count(static item => item.IsChecked);

    /// <summary>
    /// Gets the localized selected-count hint shown next to the bulk commands; empty when nothing is checked.
    /// </summary>
    public string SelectedCountText => SelectedCount > 0
        ? string.Format(_localization.Translate("SelectedItemsCount"), SelectedCount)
        : string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a bulk review decision is in progress.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AllowSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DenySelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAllFilteredCommand))]
    public partial bool IsBulkBusy { get; private set; }

    private bool _suppressSelectionRefresh;

    private bool CanReviewSelection => !IsBulkBusy && Items.Any(static item => item.IsChecked);

    private bool CanToggleAllFiltered => !IsBulkBusy && ItemsView.Cast<IApprovalQueueItem>().Any();

    /// <summary>
    /// Checks every row visible under the current search, or unchecks them when they are already all checked.
    /// Rows hidden by the search filter are not touched.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleAllFiltered))]
    private void ToggleAllFiltered()
    {
        var visible = ItemsView.Cast<IApprovalQueueItem>().ToArray();
        if (visible.Length == 0)
        {
            return;
        }

        var check = !visible.All(static row => row.IsChecked);
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var row in visible)
            {
                row.IsChecked = check;
            }
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }

        RefreshSelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanReviewSelection))]
    private Task AllowSelectedAsync() => ReviewSelectedAsync(ContextMenuDecision.Allow);

    [RelayCommand(CanExecute = nameof(CanReviewSelection))]
    private Task DenySelectedAsync() => ReviewSelectedAsync(ContextMenuDecision.Deny);

    /// <summary>
    /// Applies the decision to every checked row through its owning domain entry point.
    /// Rows hidden by the search filter stay checked and are included; rows that cannot act keep their check.
    /// </summary>
    private async Task ReviewSelectedAsync(ContextMenuDecision decision)
    {
        if (IsBulkBusy)
        {
            return;
        }

        IsBulkBusy = true;
        var isAccept = decision == ContextMenuDecision.Allow;
        try
        {
            await ToggleBusyPresentation.WaitForFirstFrameAsync();
            foreach (var row in Items.Where(static row => row.IsChecked).ToArray())
            {
                // The checkboxes are disabled while IsBulkBusy, but re-read the intent per row
                // so any non-UI toggle of IsChecked still cancels that row's decision.
                if (!row.IsChecked)
                {
                    continue;
                }

                switch (row)
                {
                    case ApprovalQueueItemViewModel classic:
                        if (!classic.CanReviewApproval)
                        {
                            continue;
                        }

                        classic.IsDecisionBusy = true;
                        try
                        {
                            foreach (var sourceItem in classic.SourceItems.ToArray())
                            {
                                await _workspace.ApplyDecisionAsync(sourceItem, decision);
                            }
                        }
                        finally { classic.IsDecisionBusy = false; }
                        break;
                    case OpenWithApprovalItemViewModel openWith:
                        var openWithDecision = isAccept ? OpenWithReviewDecision.Accept : OpenWithReviewDecision.Reject;
                        if (!(isAccept ? openWith.CanAccept : openWith.CanReject))
                        {
                            continue;
                        }

                        await OpenWithReviews.DecideAsync(openWith, openWithDecision, CancellationToken.None);
                        break;
                }

                row.IsChecked = false;
            }
        }
        finally
        {
            IsBulkBusy = false;
            RefreshSelectionState();
        }
    }

    [RelayCommand]
    private Task AllowAsync(ContextMenuItemViewModel? item)
    {
        return item is null ? Task.CompletedTask : _workspace.ApplyDecisionAsync(item, ContextMenuDecision.Allow);
    }

    [RelayCommand]
    private Task AllowGroupAsync(ApprovalQueueItemViewModel? item)
        => ReviewGroupAsync(item, ContextMenuDecision.Allow);

    [RelayCommand]
    private Task DenyAsync(ContextMenuItemViewModel? item)
    {
        return item is null ? Task.CompletedTask : _workspace.ApplyDecisionAsync(item, ContextMenuDecision.Deny);
    }

    [RelayCommand]
    private Task DenyGroupAsync(ApprovalQueueItemViewModel? item)
        => ReviewGroupAsync(item, ContextMenuDecision.Deny);

    private async Task ReviewGroupAsync(ApprovalQueueItemViewModel? item, ContextMenuDecision decision)
    {
        if (item is null || !item.CanReviewApproval)
        {
            return;
        }

        var sourceItems = item.SourceItems.ToArray();
        item.IsDecisionBusy = true;
        try
        {
            await ToggleBusyPresentation.WaitForFirstFrameAsync();
            foreach (var sourceItem in sourceItems)
            {
                await _workspace.ApplyDecisionAsync(sourceItem, decision);
            }
        }
        finally { item.IsDecisionBusy = false; }
    }

    [RelayCommand]
    private Task OpenRemoveAsync(ApprovalQueueItemViewModel? item)
    {
        if (item is null || !item.CanRemove)
        {
            return Task.CompletedTask;
        }

        if (!item.HasRegistryBackedItem)
        {
            return ConfirmRemoveAsync(item);
        }

        item.IsApprovalRemoveFlyoutOpen = true;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConfirmRemoveAsync(ApprovalQueueItemViewModel? item)
    {
        if (item is null || !item.CanRemove)
        {
            return;
        }

        item.IsApprovalRemoveFlyoutOpen = false;
        foreach (var sourceItem in item.SourceItems.ToArray())
        {
            await _workspace.ApplyDecisionAsync(sourceItem, ContextMenuDecision.Remove);
        }
    }

    // Detach before attach so a Move notification (same row in both lists) nets to one subscription.
    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (IApprovalQueueItem row in e.OldItems)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (IApprovalQueueItem row in e.NewItems)
            {
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IApprovalQueueItem.IsChecked))
        {
            RefreshSelectionState();
        }
    }

    // Live filtering can add or remove visible rows without any IsChecked or SearchText change.
    private void OnVisibleItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        if (_suppressSelectionRefresh)
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(IsAllFilteredChecked));
        AllowSelectedCommand.NotifyCanExecuteChanged();
        DenySelectedCommand.NotifyCanExecuteChanged();
        ToggleAllFilteredCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var row in Items) row.RefreshLocalization();
        RefreshLocalizedText();
        OnPropertyChanged(nameof(AllowText));
        OnPropertyChanged(nameof(DenyText));
        OnPropertyChanged(nameof(KeepDisabledText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(ConfirmRemoveText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(SearchLabel));
        OnPropertyChanged(nameof(SelectAllText));
        OnPropertyChanged(nameof(SelectedCountText));
        RebuildItems();
        ItemsView.Refresh();
    }

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
        RefreshSelectionState();
    }

    private void RefreshLocalizedText()
    {
        Title = _localization.Translate("PendingApprovalTitle");
        Description = _localization.Translate("PendingApprovalDescription");
    }

    private bool FilterItem(object obj)
    {
        if (obj is not IApprovalQueueItem item)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var search = SearchText.Trim();
        return Contains(item.DisplayName, search)
               || Contains(item.Subtitle, search)
               || Contains(item.RegistryPath, search)
               || Contains(item.ShellPathTail, search)
               || Contains(item.Notes, search)
               || item.CategoryTags.Any(tag => Contains(tag.Text, search));
    }

    private static bool Contains(string? value, string search)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
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

        RebuildItems();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContextMenuItemViewModel.IsPendingApproval)
            or nameof(ContextMenuItemViewModel.DisplayName)
            or nameof(ContextMenuItemViewModel.KeyName)
            or nameof(ContextMenuItemViewModel.RegistryPath)
            or nameof(ContextMenuItemViewModel.Notes)
            or nameof(ContextMenuItemViewModel.Category))
        {
            RebuildItems();
        }
    }

    private void RebuildItems()
    {
        var existing = Items.OfType<ApprovalQueueItemViewModel>().ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var grouped = _workspace.Items
            .Concat(_workspace.WpsOfficeApprovalItems)
            .Where(static item => item.IsPendingApproval)
            .GroupBy(CreateApprovalGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                if (!existing.TryGetValue(group.Key, out var row))
                    return new ApprovalQueueItemViewModel(group.ToArray(), _localization);
                row.UpdateSources(group.ToArray());
                return row;
            })
            .Cast<IApprovalQueueItem>().Concat(_workspace.OpenWithReviews.Items)
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var removed = Items.Except(grouped).ToArray();
        IncrementalCollection.Apply(Items, grouped);
        foreach (var row in removed.OfType<ApprovalQueueItemViewModel>()) row.Dispose();

        RefreshSelectionState();
        ApplyFocusRequest();
    }

    private static string CreateApprovalGroupKey(ContextMenuItemViewModel item)
    {
        return ContextMenuApprovalIdentity.CreateLogicalItemKey(item.Entry);
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        foreach (var row in Items)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            if (row is ApprovalQueueItemViewModel classic) classic.Dispose();
        }
        Items.CollectionChanged -= OnRowsCollectionChanged;
        ((INotifyCollectionChanged)ItemsView).CollectionChanged -= OnVisibleItemsChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        _navigationState.ApprovalsRequested -= OnApprovalsRequested;
        _workspace.Items.CollectionChanged -= OnItemsCollectionChanged;
        _workspace.WpsOfficeApprovalItems.CollectionChanged -= OnItemsCollectionChanged;
        _workspace.OpenWithReviews.SnapshotChanged -= OnOpenWithItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        foreach (var item in _workspace.WpsOfficeApprovalItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private void OnApprovalsRequested(object? sender, EventArgs e)
    {
        ObserveFireAndForget(_workspace.RefreshWpsOfficeApprovalsAsync(), "RefreshWpsOfficeApprovalsAsync");
        ObserveFireAndForget(_workspace.OpenWithReviews.RefreshAsync(CancellationToken.None), "RefreshOpenWithReviewsAsync");
        ApplyFocusRequest();
    }

    private void OnOpenWithItemsChanged(object? sender, EventArgs e)
    {
        RebuildItems();
        ItemsView.Refresh();
    }

    private void ApplyFocusRequest()
    {
        if (string.IsNullOrWhiteSpace(_navigationState.FocusItemId))
        {
            return;
        }

        SelectedItem = Items.FirstOrDefault(
            approval => string.Equals(approval.Id, _navigationState.FocusItemId, StringComparison.OrdinalIgnoreCase)
                || approval is ApprovalQueueItemViewModel classic && classic.SourceItems.Any(
                    sourceItem => string.Equals(sourceItem.Id, _navigationState.FocusItemId, StringComparison.OrdinalIgnoreCase)));
    }

    private static async void ObserveFireAndForget(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning("ApprovalsPageViewModel", $"{operationName} failed: {ex.Message}");
        }
    }
}
