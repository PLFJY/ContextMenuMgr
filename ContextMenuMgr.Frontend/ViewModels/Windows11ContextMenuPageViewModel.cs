using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the windows11 Context Menu Page View Model.
/// </summary>
public partial class Windows11ContextMenuPageViewModel : ObservableObject, IDisposable
{
    private readonly Windows11ContextMenuService _service;
    private readonly LocalizationService _localization;
    private readonly ContextMenuWorkspaceService _workspace;

    /// <summary>
    /// Initializes a new instance of the <see cref="Windows11ContextMenuPageViewModel"/> class.
    /// </summary>
    public Windows11ContextMenuPageViewModel(
        Windows11ContextMenuService service,
        LocalizationService localization,
        ContextMenuWorkspaceService workspace)
    {
        _service = service;
        _localization = localization;
        _workspace = workspace;

        ItemsView = new ListCollectionView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(Windows11ContextMenuItemViewModel.DisplayName), ListSortDirection.Ascending));

        _localization.LanguageChanged += OnLanguageChanged;
        _service.ItemsChanged += OnItemsChanged;
        _workspace.Items.CollectionChanged += OnWorkspaceItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnWorkspaceItemPropertyChanged;
        }

        if (_service.IsSupported)
        {
            if (_service.HasLoaded)
            {
                RebuildItems(_service.CurrentItems);
            }
            else
            {
                _ = EnsureLoadedAsync();
            }
        }
    }

    /// <summary>
    /// Gets the items.
    /// </summary>
    public ObservableCollection<Windows11ContextMenuItemViewModel> Items { get; } = [];

    /// <summary>
    /// Gets the items View.
    /// </summary>
    public ICollectionView ItemsView { get; }

    /// <summary>
    /// Gets or sets a value indicating whether loading.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>
    /// Gets or sets the search Text.
    /// </summary>
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

    /// <summary>
    /// Refreshes async.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_service.IsSupported || IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var items = await _service.RefreshAsync(CancellationToken.None);
            RebuildItems(items);
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

    private async Task EnsureLoadedAsync()
    {
        try
        {
            IsLoading = true;
            var items = await _service.EnsureLoadedAsync(CancellationToken.None);
            RebuildItems(items);
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

    private void OnItemsChanged(object? sender, EventArgs e)
    {
        RebuildItems(_service.CurrentItems);
    }

    private void RebuildItems(IReadOnlyList<Windows11ContextMenuItemDefinition> items)
    {
        var pendingApprovalKeys = GetPendingApprovalKeys();

        foreach (var existing in Items)
        {
            existing.Dispose();
        }

        Items.Clear();
        foreach (var group in items
                     .GroupBy(CreateLogicalGroupKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static group => group.First().Package.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(static group => group.First().DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var itemViewModel = new Windows11ContextMenuItemViewModel(group.ToArray(), _service, _localization);
            itemViewModel.RefreshPendingApproval(pendingApprovalKeys.Contains(group.Key));
            Items.Add(itemViewModel);
        }

        ItemsView.Refresh();
    }

    private static string CreateLogicalGroupKey(Windows11ContextMenuItemDefinition item)
    {
        var filePath = File.Exists(item.ComServer.Path ?? string.Empty)
            ? item.ComServer.Path
            : item.Package.InstallPath;
        return ContextMenuApprovalIdentity.CreateWindows11LogicalItemKey(
            item.Package.FullName,
            item.DisplayName,
            filePath);
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

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _service.ItemsChanged -= OnItemsChanged;
        _workspace.Items.CollectionChanged -= OnWorkspaceItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
        }

        foreach (var item in Items)
        {
            item.Dispose();
        }
    }

    private HashSet<string> GetPendingApprovalKeys()
    {
        return _workspace.Items
            .Where(static item => item.IsPendingApproval && item.IsWindows11ContextMenu)
            .Select(static item => ContextMenuApprovalIdentity.CreateLogicalItemKey(item.Entry))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshPendingApprovalStates()
    {
        var pendingApprovalKeys = GetPendingApprovalKeys();
        foreach (var item in Items)
        {
            item.RefreshPendingApproval(pendingApprovalKeys.Contains(CreateLogicalGroupKey(item.Definitions[0])));
        }
    }

    private void OnWorkspaceItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ContextMenuItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnWorkspaceItemPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ContextMenuItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
            }
        }

        RefreshPendingApprovalStates();
    }

    private void OnWorkspaceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContextMenuItemViewModel.IsPendingApproval)
            or nameof(ContextMenuItemViewModel.IsWindows11ContextMenu))
        {
            RefreshPendingApprovalStates();
        }
    }
}
