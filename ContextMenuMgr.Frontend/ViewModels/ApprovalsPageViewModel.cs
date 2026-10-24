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
        RefreshLocalizedText();
        RebuildItems();
        ObserveFireAndForget(_workspace.RefreshWpsOfficeApprovalsAsync(), "RefreshWpsOfficeApprovalsAsync");
    }

    /// <summary>
    /// Gets the items.
    /// </summary>
    public ObservableCollection<ApprovalQueueItemViewModel> Items { get; } = [];

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
    public partial ApprovalQueueItemViewModel? SelectedItem { get; set; }

    public string AllowText => _localization.Translate("Allow");

    public string KeepDisabledText => _localization.Translate("KeepDisabled");

    public string RemoveText => _localization.Translate("Remove");

    public string ConfirmRemoveText => _localization.Translate("ConfirmRemove");

    public string CancelText => _localization.Translate("DialogCancel");

    public string SearchLabel => _localization.Translate("SearchLabel");

    [RelayCommand]
    private Task AllowAsync(ContextMenuItemViewModel? item)
    {
        return item is null ? Task.CompletedTask : _workspace.ApplyDecisionAsync(item, ContextMenuDecision.Allow);
    }

    [RelayCommand]
    private async Task AllowGroupAsync(ApprovalQueueItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var sourceItem in item.SourceItems.ToArray())
        {
            await _workspace.ApplyDecisionAsync(sourceItem, ContextMenuDecision.Allow);
        }
    }

    [RelayCommand]
    private Task DenyAsync(ContextMenuItemViewModel? item)
    {
        return item is null ? Task.CompletedTask : _workspace.ApplyDecisionAsync(item, ContextMenuDecision.Deny);
    }

    [RelayCommand]
    private async Task DenyGroupAsync(ApprovalQueueItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var sourceItem in item.SourceItems.ToArray())
        {
            await _workspace.ApplyDecisionAsync(sourceItem, ContextMenuDecision.Deny);
        }
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

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var row in Items) row.RefreshLocalization();
        RefreshLocalizedText();
        OnPropertyChanged(nameof(AllowText));
        OnPropertyChanged(nameof(KeepDisabledText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(ConfirmRemoveText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(SearchLabel));
        RebuildItems();
        ItemsView.Refresh();
    }

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
    }

    private void RefreshLocalizedText()
    {
        Title = _localization.Translate("PendingApprovalTitle");
        Description = _localization.Translate("PendingApprovalDescription");
    }

    private bool FilterItem(object obj)
    {
        if (obj is not ApprovalQueueItemViewModel item)
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
        var existing = Items.ToDictionary(item => CreateApprovalGroupKey(item.PrimaryItem), StringComparer.OrdinalIgnoreCase);
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
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var removed = Items.Except(grouped).ToArray();
        IncrementalCollection.Apply(Items, grouped);
        foreach (var row in removed) row.Dispose();

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
        foreach (var row in Items) row.Dispose();
        _localization.LanguageChanged -= OnLanguageChanged;
        _navigationState.ApprovalsRequested -= OnApprovalsRequested;
        _workspace.Items.CollectionChanged -= OnItemsCollectionChanged;
        _workspace.WpsOfficeApprovalItems.CollectionChanged -= OnItemsCollectionChanged;
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
        ApplyFocusRequest();
    }

    private void ApplyFocusRequest()
    {
        if (string.IsNullOrWhiteSpace(_navigationState.FocusItemId))
        {
            return;
        }

        SelectedItem = Items.FirstOrDefault(
            approval => approval.SourceItems.Any(
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
