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
    private const int RebuildDebounceMilliseconds = 120;
    private readonly Windows11ContextMenuService _service;
    private readonly LocalizationService _localization;
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly ListPlaceholderDebugStateService _placeholderDebug;
    private readonly GlobalSearchNavigationFilterService _globalSearchFilterService;
    private readonly FrontendSettingsService _settingsService;
    private CancellationTokenSource? _rebuildCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="Windows11ContextMenuPageViewModel"/> class.
    /// </summary>
    public Windows11ContextMenuPageViewModel(
        Windows11ContextMenuService service,
        LocalizationService localization,
        ContextMenuWorkspaceService workspace,
        ListPlaceholderDebugStateService placeholderDebug,
        GlobalSearchNavigationFilterService globalSearchFilterService,
        FrontendSettingsService settingsService)
    {
        _service = service;
        _localization = localization;
        _workspace = workspace;
        _placeholderDebug = placeholderDebug;
        _globalSearchFilterService = globalSearchFilterService;
        _settingsService = settingsService;

        ItemsView = new ListCollectionView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(Windows11ContextMenuItemViewModel.DisplayName), ListSortDirection.Ascending));

        _localization.LanguageChanged += OnLanguageChanged;
        _service.ItemsChanged += OnItemsChanged;
        _placeholderDebug.PropertyChanged += OnPlaceholderDebugPropertyChanged;
        _globalSearchFilterService.FilterRequested += OnGlobalSearchFilterRequested;
        _workspace.Items.CollectionChanged += OnWorkspaceItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnWorkspaceItemPropertyChanged;
        }

        if (_service.IsSupported)
        {
            if (_service.CurrentItems.Count > 0)
            {
                RebuildItems(_service.CurrentItems);
            }
            else
            {
                _ = EnsureLoadedAsync();
            }
        }

        ApplyPendingGlobalSearchFilter();
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

    public string LoadingItemsText => _localization.Translate("LoadingItemsText");

    public string EmptyItemsText => _localization.Translate("EmptyItemsText");

    public string PackageFamilyLabel => _localization.Translate("Windows11PackageFamilyLabel");

    public string PublisherLabel => _localization.Translate("Windows11PublisherLabel");

    public string ContextTypesLabel => _localization.Translate("Windows11ContextTypesLabel");

    public bool IsSupported => _service.IsSupported;

    public bool IsListLoading => _placeholderDebug.ForceLoadingState || IsLoading;

    public bool IsListEmpty => !IsListLoading
        && (_placeholderDebug.ForceEmptyState || !ItemsView.Cast<object>().Any());

    public bool ShowListPlaceholder => IsListLoading || IsListEmpty;

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
            await _service.RefreshAsync(CancellationToken.None);
            RebuildItems(_service.CurrentItems);
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
        RefreshListPlaceholderState();
    }

    private void OnGlobalSearchFilterRequested(object? sender, GlobalSearchFilterRequestedEventArgs e)
    {
        if (!e.IsWindows11)
        {
            return;
        }

        ApplyGlobalSearchFilter(e.FilterText, e.ItemId);
        _globalSearchFilterService.ConsumePendingWindows11Request();
    }

    private void ApplyPendingGlobalSearchFilter()
    {
        var pending = _globalSearchFilterService.ConsumePendingWindows11Request();
        if (pending is not null)
        {
            ApplyGlobalSearchFilter(pending.FilterText, pending.ItemId);
        }
    }

    private void ApplyGlobalSearchFilter(string filterText, string? itemId)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return;
        }

        SearchText = filterText;
        ItemsView.Refresh();
        RefreshListPlaceholderState();

        FrontendDebugLog.Info(
            nameof(Windows11ContextMenuPageViewModel),
            "GlobalSearchFilterApplied: "
            + "Page/ViewModel=Windows11ContextMenuPageViewModel, "
            + "Category=<null>, "
            + "IsWindows11=True, "
            + $"ItemId={itemId ?? "<null>"}, "
            + $"FilterText='{SanitizeLogText(filterText)}'.");
    }

    partial void OnIsLoadingChanged(bool value)
    {
        RefreshListPlaceholderState();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(SearchLabel));
        OnPropertyChanged(nameof(NoItemsText));
        OnPropertyChanged(nameof(LoadingItemsText));
        OnPropertyChanged(nameof(EmptyItemsText));
        OnPropertyChanged(nameof(PackageFamilyLabel));
        OnPropertyChanged(nameof(PublisherLabel));
        OnPropertyChanged(nameof(ContextTypesLabel));
        ItemsView.Refresh();
        RefreshListPlaceholderState();
    }

    private void OnPlaceholderDebugPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ListPlaceholderDebugStateService.Mode)
            or nameof(ListPlaceholderDebugStateService.ForceLoadingState)
            or nameof(ListPlaceholderDebugStateService.ForceEmptyState)
            or nameof(ListPlaceholderDebugStateService.HasForcedState))
        {
            RefreshListPlaceholderState();
        }
    }

    private async Task EnsureLoadedAsync()
    {
        try
        {
            IsLoading = true;
            await _service.EnsureLoadedAsync(CancellationToken.None);
            RebuildItems(_service.CurrentItems);
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
        // Debounce rebuilds: the service can raise ItemsChanged repeatedly while
        // scanning, and rebuilding rebuilds every grouped item view model.
        _rebuildCts?.Cancel();
        _rebuildCts?.Dispose();
        var cts = _rebuildCts = new CancellationTokenSource();
        _ = DebouncedRebuildItemsAsync(cts);
    }

    private async Task DebouncedRebuildItemsAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(RebuildDebounceMilliseconds, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_rebuildCts, cts))
            {
                return;
            }

            RebuildItems(_service.CurrentItems);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                nameof(Windows11ContextMenuPageViewModel),
                $"RebuildItems failed: {ex}");
        }
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
            var itemViewModel = new Windows11ContextMenuItemViewModel(group.ToArray(), _service, _localization, _settingsService);
            itemViewModel.RefreshPendingApproval(pendingApprovalKeys.Contains(group.Key));
            Items.Add(itemViewModel);
        }

        ItemsView.Refresh();
        RefreshListPlaceholderState();
    }

    private static string CreateLogicalGroupKey(Windows11ContextMenuItemDefinition item)
    {
        if (item.SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore)
        {
            return string.Join("|", "win11-system", item.Entry?.KeyName ?? item.Id);
        }

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
        return obj is Windows11ContextMenuItemViewModel item
               && ContextMenuSearchMatcher.MatchesWindows11Item(item, SearchText);
    }

    private static string SanitizeLogText(string? text)
    {
        return (text ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _rebuildCts?.Cancel();
        _rebuildCts?.Dispose();
        _rebuildCts = null;
        _localization.LanguageChanged -= OnLanguageChanged;
        _service.ItemsChanged -= OnItemsChanged;
        _placeholderDebug.PropertyChanged -= OnPlaceholderDebugPropertyChanged;
        _globalSearchFilterService.FilterRequested -= OnGlobalSearchFilterRequested;
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
        RefreshListPlaceholderState();
    }

    private void OnWorkspaceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContextMenuItemViewModel.IsPendingApproval)
            or nameof(ContextMenuItemViewModel.IsWindows11ContextMenu))
        {
            RefreshPendingApprovalStates();
        }
    }

    private void RefreshListPlaceholderState()
    {
        OnPropertyChanged(nameof(IsListLoading));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(ShowListPlaceholder));
    }
}
