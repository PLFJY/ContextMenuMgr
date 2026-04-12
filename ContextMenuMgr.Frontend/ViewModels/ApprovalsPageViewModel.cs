using System.ComponentModel;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class ApprovalsPageViewModel : ObservableObject, IDisposable
{
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;

    public ApprovalsPageViewModel(ContextMenuWorkspaceService workspace, LocalizationService localization)
    {
        _workspace = workspace;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        _workspace.Items.CollectionChanged += OnItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        ItemsView = new ListCollectionView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ApprovalQueueItemViewModel.DisplayName), ListSortDirection.Ascending));
        RefreshLocalizedText();
        RebuildItems();
    }

    public ObservableCollection<ApprovalQueueItemViewModel> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

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
        if (item is null)
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
        if (item is null)
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
        RefreshLocalizedText();
        OnPropertyChanged(nameof(AllowText));
        OnPropertyChanged(nameof(KeepDisabledText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(ConfirmRemoveText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(SearchLabel));
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
               || Contains(item.KeyName, search)
               || Contains(item.RegistryPath, search)
               || Contains(item.Notes, search)
               || item.Categories.Any(category => Contains(category, search));
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
        ItemsView.Refresh();
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
            ItemsView.Refresh();
        }
    }

    private void RebuildItems()
    {
        var grouped = _workspace.Items
            .Where(static item => item.IsPendingApproval)
            .GroupBy(CreateApprovalGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ApprovalQueueItemViewModel(group.ToArray(), _localization))
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Items.Clear();
        foreach (var item in grouped)
        {
            Items.Add(item);
        }
    }

    private static string CreateApprovalGroupKey(ContextMenuItemViewModel item)
    {
        return string.Join("|",
            item.DisplayName,
            item.KeyName,
            item.Entry.EntryKind.ToString(),
            item.Entry.HandlerClsid ?? string.Empty,
            item.Entry.CommandText ?? string.Empty,
            item.Entry.EditableText ?? string.Empty,
            item.Entry.FilePath ?? string.Empty);
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _workspace.Items.CollectionChanged -= OnItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }
}
