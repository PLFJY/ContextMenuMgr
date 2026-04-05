using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class CategoryPageViewModel : ObservableObject
{
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly FrontendSettingsService _settingsService;

    public CategoryPageViewModel(
        ContextMenuCategory category,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        FrontendSettingsService settingsService)
    {
        Category = category;
        _workspace = workspace;
        _localization = localization;
        _settingsService = settingsService;
        _localization.LanguageChanged += OnLanguageChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _workspace.Items.CollectionChanged += OnItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        ItemsView = new ListCollectionView(_workspace.Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ContextMenuItemViewModel.SortAttentionWeight), ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ContextMenuItemViewModel.SortDeletedWeight), ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ContextMenuItemViewModel.DisplayName), ListSortDirection.Ascending));

        RefreshLocalizedText();
    }

    public ContextMenuCategory Category { get; }

    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public string PermanentDeleteText => _localization.Translate("PermanentDelete");

    public string RegistryMissingText => _localization.Translate("RegistryMissingText");

    public string SearchLabel => _localization.Translate("SearchLabel");

    public string DeleteText => _localization.Translate("Delete");

    public string CancelText => _localization.Translate("DialogCancel");

    [RelayCommand]
    private Task DeleteOrUndoAsync(ContextMenuItemViewModel? item)
    {
        return item is null ? Task.CompletedTask : _workspace.DeleteOrUndoAsync(item);
    }

    [RelayCommand]
    private Task OpenPermanentDeleteFlyoutAsync(ContextMenuItemViewModel? item)
    {
        if (item is not null)
        {
            item.IsPermanentDeleteFlyoutOpen = true;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConfirmPermanentDeleteAsync(ContextMenuItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsPermanentDeleteFlyoutOpen = false;
        await _workspace.PermanentlyDeleteAsync(item);
    }

    private bool FilterItem(object obj)
    {
        if (obj is not ContextMenuItemViewModel item || item.Category != Category)
        {
            return false;
        }

        if (_settingsService.Current.HideDisabledItems && !item.IsEnabled && !item.IsDeleted)
        {
            return false;
        }

        return MatchesSearch(item);
    }

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedText();
        OnPropertyChanged(nameof(PermanentDeleteText));
        OnPropertyChanged(nameof(RegistryMissingText));
        OnPropertyChanged(nameof(SearchLabel));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(CancelText));
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ItemsView.Refresh();
    }

    private void RefreshLocalizedText()
    {
        var (nameKey, descriptionKey) = ContextMenuCategoryText.GetResourceKeys(Category);

        Title = _localization.Translate(nameKey);
        Description = _localization.Translate(descriptionKey);
    }

    private bool MatchesSearch(ContextMenuItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var search = SearchText.Trim();
        return Contains(item.DisplayName, search)
               || Contains(item.KeyName, search)
               || Contains(item.RegistryPath, search)
               || Contains(item.Notes, search);
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

        ItemsView.Refresh();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContextMenuItemViewModel.DisplayName)
            or nameof(ContextMenuItemViewModel.KeyName)
            or nameof(ContextMenuItemViewModel.RegistryPath)
            or nameof(ContextMenuItemViewModel.Notes)
            or nameof(ContextMenuItemViewModel.IsDeleted)
            or nameof(ContextMenuItemViewModel.HasDetectedChange)
            or nameof(ContextMenuItemViewModel.IsPendingApproval)
            or nameof(ContextMenuItemViewModel.HasConsistencyIssue))
        {
            ItemsView.Refresh();
        }
    }
}
