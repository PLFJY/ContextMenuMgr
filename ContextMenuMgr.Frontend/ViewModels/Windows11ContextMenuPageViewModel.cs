using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class Windows11ContextMenuPageViewModel : ObservableObject
{
    private readonly Windows11ContextMenuService _service;
    private readonly LocalizationService _localization;

    public Windows11ContextMenuPageViewModel(
        Windows11ContextMenuService service,
        LocalizationService localization)
    {
        _service = service;
        _localization = localization;

        ItemsView = new ListCollectionView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(Windows11ContextMenuItemViewModel.DisplayName), ListSortDirection.Ascending));

        _localization.LanguageChanged += OnLanguageChanged;
        if (_service.IsSupported)
        {
            _ = RefreshAsync();
        }
    }

    public ObservableCollection<Windows11ContextMenuItemViewModel> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public string Title => _localization.Translate("Windows11PageTitle");

    public string Description => _localization.Translate("Windows11PageDescription");

    public string SearchLabel => _localization.Translate("SearchLabel");

    public string NoItemsText => _localization.Translate("Windows11NoItems");

    public string PackageFamilyLabel => _localization.Translate("Windows11PackageFamilyLabel");

    public string PublisherLabel => _localization.Translate("Windows11PublisherLabel");

    public string ContextTypesLabel => _localization.Translate("Windows11ContextTypesLabel");

    public bool IsSupported => _service.IsSupported;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_service.IsSupported)
        {
            return;
        }

        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var items = await _service.LoadAsync(CancellationToken.None);
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(new Windows11ContextMenuItemViewModel(item, _service, _localization));
            }

            ItemsView.Refresh();
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("Windows11PageTitle"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(SearchLabel));
        OnPropertyChanged(nameof(NoItemsText));
        OnPropertyChanged(nameof(PackageFamilyLabel));
        OnPropertyChanged(nameof(PublisherLabel));
        OnPropertyChanged(nameof(ContextTypesLabel));
        ItemsView.Refresh();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not Windows11ContextMenuItemViewModel item)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var search = SearchText.Trim();
        return Contains(item.DisplayName, search)
               || Contains(item.PackageFamilyName, search)
               || Contains(item.PublisherName, search)
               || Contains(item.ContextTypesText, search)
               || Contains(item.ComServerPath, search);
    }

    private static bool Contains(string? value, string search)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}
