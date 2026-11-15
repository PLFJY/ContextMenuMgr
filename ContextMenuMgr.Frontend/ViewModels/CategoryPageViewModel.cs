using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.Views.Pages;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the category Page View Model.
/// </summary>
public partial class CategoryPageViewModel : ObservableObject, IDisposable
{
    private const string DefaultCustomMenuItemKeyName = "CustomMenuItem";
    private static readonly string[] SelectedObjectPlaceholders = ["%1", "%V", "%L", "%*"];
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly IBackendClient _backendClient;
    private readonly LocalizationService _localization;
    private readonly FrontendSettingsService _settingsService;
    private readonly GlobalSearchNavigationFilterService _globalSearchFilterService;
    private readonly DelayedLoadingPresentation _loadingPresentation = new();
    private readonly HashSet<string> _loggedDesktopCompatibilityItemIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _lastHideDisabledItems;
    private CancellationTokenSource? _searchDebounce;
    private string _appliedSearchText = string.Empty;
    private IReadOnlyList<DeletedItemReference> _purgeSelection = [];
    private System.Windows.Threading.DispatcherOperation? _pendingDeletedCount;
    private int _deletedCandidateCount;


    /// <summary>
    /// Initializes a new instance of the <see cref="CategoryPageViewModel"/> class.
    /// </summary>
    public CategoryPageViewModel(
        ContextMenuCategory category,
        ContextMenuWorkspaceService workspace,
        IBackendClient backendClient,
        LocalizationService localization,
        FrontendSettingsService settingsService,
        GlobalSearchNavigationFilterService globalSearchFilterService)
    {
        Category = category;
        _workspace = workspace;
        _backendClient = backendClient;
        _localization = localization;
        _settingsService = settingsService;
        _globalSearchFilterService = globalSearchFilterService;
        _lastHideDisabledItems = settingsService.Current.HideDisabledItems;
        _localization.LanguageChanged += OnLanguageChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _loadingPresentation.PropertyChanged += OnLoadingPresentationChanged;
        _globalSearchFilterService.FilterRequested += OnGlobalSearchFilterRequested;
        _workspace.Items.CollectionChanged += OnItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        var itemsView = new ListCollectionView(_workspace.Items);
        using (itemsView.DeferRefresh())
        {
            itemsView.Filter = FilterItem;
            ContextMenuListPresentation.ConfigureSort(itemsView);
        }
        ItemsView = itemsView;

        RefreshLocalizedText();
        _loadingPresentation.Update(!_workspace.HasSnapshot && (_workspace.IsLoading || _workspace.IsServiceBootstrapInProgress));
        RefreshListPlaceholderState();
        ApplyPendingGlobalSearchFilter();
    }

    /// <summary>
    /// Gets the category.
    /// </summary>
    public ContextMenuCategory Category { get; }

    /// <summary>
    /// Gets the items View.
    /// </summary>
    public ICollectionView ItemsView { get; }

    public bool HideDisabledItems
    {
        get => _settingsService.Current.HideDisabledItems;
        set => _settingsService.UpdateHideDisabledItems(value);
    }

    public bool IsCompactView
    {
        get => _settingsService.Current.ContextMenuListViewMode == ContextMenuListViewMode.Compact;
        set
        {
            if (value)
            {
                _settingsService.UpdateContextMenuListViewMode(ContextMenuListViewMode.Compact);
            }
        }
    }

    public bool IsDetailedView
    {
        get => _settingsService.Current.ContextMenuListViewMode == ContextMenuListViewMode.Detailed;
        set
        {
            if (value)
            {
                _settingsService.UpdateContextMenuListViewMode(ContextMenuListViewMode.Detailed);
            }
        }
    }

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

    public string PermanentDeleteText => _localization.Translate("PermanentDelete");

    public string PurgeDeletedItemsText => _localization.Translate("PurgeDeletedItems");
    public string PurgeDeletedItemsTooltip => _localization.Translate("PurgeDeletedItemsTooltip");
    public string ConfirmPurgeDeletedItemsText => _localization.Translate("ConfirmPurgeDeletedItems");
    public string PurgeDeletedItemsConfirmationText => _localization.Format("PurgeDeletedItemsPrompt", Title, _purgeSelection.Count);
    public string ScrollToTopText => _localization.Translate("ScrollToTop");
    public string ScrollToBottomText => _localization.Translate("ScrollToBottom");
    public bool CanPurgeDeletedItems => _workspace.IsBackendReady && !IsPurgingDeletedItems && _deletedCandidateCount > 0;

    [ObservableProperty]
    public partial bool IsPurgeDeletedItemsFlyoutOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPurgeDeletedItems))]
    public partial bool IsPurgingDeletedItems { get; private set; }

    private IEnumerable<ContextMenuItemViewModel> GetDeletedCandidates() => _workspace.Items.Where(item =>
        !item.IsWindows11ContextMenu && item.IsDeleted && !item.IsDeleteBusy && !item.IsDeleteOutcomeUncertain
        && IsItemVisibleInCurrentCategory(item));

    [RelayCommand]
    private void OpenPurgeDeletedItemsFlyout()
    {
        if (!CanPurgeDeletedItems) return;
        _purgeSelection = GetDeletedCandidates().Select(item => new DeletedItemReference(item.Id, item.DeletedAtUtc)).ToArray();
        OnPropertyChanged(nameof(PurgeDeletedItemsConfirmationText));
        IsPurgeDeletedItemsFlyoutOpen = true;
    }

    [RelayCommand]
    private void ClosePurgeDeletedItemsFlyout() => IsPurgeDeletedItemsFlyoutOpen = false;

    [RelayCommand]
    private async Task ConfirmPurgeDeletedItemsAsync()
    {
        if (IsPurgingDeletedItems || _purgeSelection.Count == 0) return;
        var request = new DeletedItemsPurgeRequest { Category = Category, Items = _purgeSelection };
        IsPurgeDeletedItemsFlyoutOpen = false;
        IsPurgingDeletedItems = true;
        try { await _workspace.PurgeDeletedItemsAsync(request); }
        finally { IsPurgingDeletedItems = false; }
    }

    public string RegistryMissingText => _localization.Translate("RegistryMissingText");

    public string SearchLabel => _localization.Translate("SearchLabel");

    public string AddMenuItemText => _localization.Translate("AddMenuItem");

    public string CompactViewText => _localization.Translate("CompactView");

    public string DetailedViewText => _localization.Translate("DetailedView");

    public string HideDisabledItemsText => _localization.Translate("HideDisabledItems");

    public string DisplayModeText => _localization.Translate("DisplayMode");

    public string CompactViewTooltip => _localization.Translate("CompactViewTooltip");

    public string DetailedViewTooltip => _localization.Translate("DetailedViewTooltip");

    public string DeleteText => _localization.Translate("Delete");

    public string CancelText => _localization.Translate("DialogCancel");

    public string LoadingItemsText => _localization.Translate("LoadingItemsText");

    public string EmptyItemsText => _localization.Translate("EmptyItemsText");

    public bool IsListLoading => !_workspace.HasSnapshot && _loadingPresentation.IsVisible;

    public bool ShowListContent => !IsListLoading && !HasListLoadFailure && !IsListEmpty;

    public bool HasListLoadFailure => !IsListLoading && !_workspace.HasSnapshot && _workspace.HasMenuLoadFailure;

    public string ListPlaceholderText
    {
        get
        {
            if (IsListLoading)
            {
                return LoadingItemsText;
            }

            if (HasListLoadFailure)
            {
                return _workspace.MenuLoadFailureText;
            }

            return EmptyItemsText;
        }
    }

    public bool IsListEmpty
    {
        get
        {
            if (IsListLoading || (!_workspace.HasSnapshot && (_workspace.IsLoading || _workspace.IsServiceBootstrapInProgress)) || HasListLoadFailure)
            {
                return false;
            }

            return !ItemsView.Cast<object>().Any();
        }
    }

    public bool ShowListPlaceholder => IsListLoading || HasListLoadFailure || IsListEmpty;

    [RelayCommand]
    private async Task AddMenuItemAsync()
    {
        var formData = await MenuItemFormDialog.ShowAddSceneMenuItemAsync(AddMenuItemText, _localization);
        if (formData is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(formData.Name))
        {
            await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), AddMenuItemText);
            return;
        }

        if (string.IsNullOrWhiteSpace(formData.TargetPath))
        {
            await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TargetPathCannotBeEmpty"), AddMenuItemText);
            return;
        }

        try
        {
            if (_settingsService.Current.LockNewContextMenuItems)
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return;
            }

            var request = new CreateSceneMenuItemRequest
            {
                SceneKind = ContextMenuSceneKind.CustomRegistryPath,
                ScopeValue = GetScopeValue(Category),
                ItemKind = SceneMenuItemKind.ShellVerb,
                KeyName = CreateSafeKeyName(formData.Name),
                DisplayName = formData.Name.Trim(),
                Command = BuildCommandText(Category, formData.TargetPath, formData.Arguments),
                Icon = string.IsNullOrWhiteSpace(formData.IconPath) ? null : formData.IconPath.Trim()
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _backendClient.CreateSceneMenuItemAsync(request, Guid.NewGuid(), cts.Token);
            await _workspace.RefreshAsync();
        }
        catch (Exception ex)
        {
            if (RegistryProtectionDialog.IsRegistryProtectionError(ex))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return;
            }

            await FrontendMessageBox.ShowErrorAsync(ex.Message, AddMenuItemText);
        }
    }

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
        if (obj is not ContextMenuItemViewModel item || !IsItemVisibleInCurrentCategory(item))
        {
            return false;
        }

        if (item.IsWindows11ContextMenu)
        {
            return false;
        }

        if (!ContextMenuListPresentation.IsVisibleWithDisabledFilter(
                item.IsEnabled,
                item.IsDeleted,
                _settingsService.Current.HideDisabledItems,
                item.IsToggleBusy || item.IsToggleOutcomeUncertain))
        {
            return false;
        }

        return MatchesSearch(item);
    }

    private bool IsItemVisibleInCurrentCategory(ContextMenuItemViewModel item)
    {
        if (item.Category == Category)
        {
            return true;
        }

        if (Category == ContextMenuCategory.DesktopBackground
            && item.Category == ContextMenuCategory.DirectoryBackground
            && IsDirectoryBackgroundItemVisibleOnDesktopBackgroundPage(item))
        {
            LogDesktopBackgroundCompatibilityItemVisible(item);
            return true;
        }

        return false;
    }

    private static bool IsDirectoryBackgroundItemVisibleOnDesktopBackgroundPage(ContextMenuItemViewModel item)
    {
        return item.Entry.SourceRootPath.StartsWith(@"Directory\Background\", StringComparison.OrdinalIgnoreCase)
               || item.Entry.RegistryPath.StartsWith(@"Directory\Background\", StringComparison.OrdinalIgnoreCase)
               || item.Entry.BackendRegistryPath.Contains(@"\Directory\Background\", StringComparison.OrdinalIgnoreCase);
    }

    private void LogDesktopBackgroundCompatibilityItemVisible(ContextMenuItemViewModel item)
    {
        if (!ShouldLogDesktopBackgroundCompatibilityItem(item)
            || !_loggedDesktopCompatibilityItemIds.Add(item.Id))
        {
            return;
        }

        FrontendDebugLog.Info(
            nameof(CategoryPageViewModel),
            "DesktopBackgroundCompatibilityItemVisible: "
            + $"DisplayName={item.DisplayName}, "
            + $"KeyName={item.KeyName}, "
            + $"ItemId={item.Id}, "
            + $"SourceCategory={item.Category}, "
            + $"VisibleCategory={Category}, "
            + $"RegistryPath={item.RegistryPath}, "
            + $"HandlerClsid={item.Entry.HandlerClsid ?? "<null>"}.");
    }

    private static bool ShouldLogDesktopBackgroundCompatibilityItem(ContextMenuItemViewModel item)
    {
        return ContainsNvidiaSignal(item.DisplayName)
               || ContainsNvidiaSignal(item.KeyName)
               || ContainsNvidiaSignal(item.Entry.HandlerClsid)
               || ContainsNvidiaSignal(item.Entry.FilePath);
    }

    private static bool ContainsNvidiaSignal(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && (value.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("Nv", StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSearchTextChanged(string value)
    {
        CancelPendingSearch();
        if (string.IsNullOrWhiteSpace(value)) { RefreshSearchImmediately(); return; }
        _searchDebounce = new CancellationTokenSource();
        _ = RefreshSearchAsync(_searchDebounce.Token);
    }

    private async Task RefreshSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(120, token);
            if (!token.IsCancellationRequested) RefreshSearchImmediately();
        }
        catch (OperationCanceledException) { }
    }

    public void CancelPendingSearch()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
    }

    public void RefreshSearchImmediately()
    {
        CancelPendingSearch();
        if (string.Equals(_appliedSearchText, SearchText, StringComparison.Ordinal)) return;
        _appliedSearchText = SearchText;
        ItemsView.Refresh();
        RefreshListPlaceholderState();
    }

    private void OnGlobalSearchFilterRequested(object? sender, GlobalSearchFilterRequestedEventArgs e)
    {
        if (e.Category != Category || e.TargetPageType != GetTargetPageType(Category))
        {
            return;
        }

        ApplyGlobalSearchFilter(e.FilterText, e.ItemId);
        _globalSearchFilterService.ConsumePendingRequest(e.TargetPageType);
    }

    private void ApplyPendingGlobalSearchFilter()
    {
        var pending = _globalSearchFilterService.ConsumePendingRequest(GetTargetPageType(Category));
        if (pending is not null && pending.Category == Category)
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
        RefreshSearchImmediately();

        FrontendDebugLog.Info(
            nameof(CategoryPageViewModel),
            "GlobalSearchFilterApplied: "
            + "Page/ViewModel=CategoryPageViewModel, "
            + $"Category={Category}, "
            + "IsWindows11=False, "
            + $"ItemId={itemId ?? "<null>"}, "
            + $"FilterText='{SanitizeLogText(filterText)}'.");
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedText();
        OnPropertyChanged(nameof(PermanentDeleteText));
        OnPropertyChanged(nameof(PurgeDeletedItemsText));
        OnPropertyChanged(nameof(PurgeDeletedItemsTooltip));
        OnPropertyChanged(nameof(ConfirmPurgeDeletedItemsText));
        OnPropertyChanged(nameof(PurgeDeletedItemsConfirmationText));
        OnPropertyChanged(nameof(ScrollToTopText));
        OnPropertyChanged(nameof(ScrollToBottomText));
        OnPropertyChanged(nameof(RegistryMissingText));
        OnPropertyChanged(nameof(SearchLabel));
        OnPropertyChanged(nameof(AddMenuItemText));
        OnPropertyChanged(nameof(CompactViewText));
        OnPropertyChanged(nameof(DetailedViewText));
        OnPropertyChanged(nameof(HideDisabledItemsText));
        OnPropertyChanged(nameof(DisplayModeText));
        OnPropertyChanged(nameof(CompactViewTooltip));
        OnPropertyChanged(nameof(DetailedViewTooltip));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(LoadingItemsText));
        OnPropertyChanged(nameof(EmptyItemsText));
        OnPropertyChanged(nameof(ListPlaceholderText));
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var hideDisabledItems = _settingsService.Current.HideDisabledItems;
        if (_lastHideDisabledItems != hideDisabledItems)
        {
            _lastHideDisabledItems = hideDisabledItems;
            ItemsView.Refresh();
            RefreshListPlaceholderState();
        }

        OnPropertyChanged(nameof(HideDisabledItems));
        OnPropertyChanged(nameof(IsCompactView));
        OnPropertyChanged(nameof(IsDetailedView));
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuWorkspaceService.IsBackendReady)) OnPropertyChanged(nameof(CanPurgeDeletedItems));
        if (e.PropertyName is nameof(ContextMenuWorkspaceService.IsLoading)
            or nameof(ContextMenuWorkspaceService.IsServiceBootstrapInProgress)
            or nameof(ContextMenuWorkspaceService.MenuLoadFailureText)
            or nameof(ContextMenuWorkspaceService.HasMenuLoadFailure))
        {
            if (e.PropertyName is nameof(ContextMenuWorkspaceService.IsLoading)
                or nameof(ContextMenuWorkspaceService.IsServiceBootstrapInProgress))
            {
                _loadingPresentation.Update(!_workspace.HasSnapshot && (_workspace.IsLoading || _workspace.IsServiceBootstrapInProgress));
            }
            RefreshListPlaceholderState();
        }
    }

    private void OnLoadingPresentationChanged(object? sender, PropertyChangedEventArgs e) => RefreshListPlaceholderState();

    private void RefreshLocalizedText()
    {
        var (nameKey, descriptionKey) = ContextMenuCategoryText.GetResourceKeys(Category);

        Title = _localization.Translate(nameKey);
        Description = _localization.Translate(descriptionKey);
    }

    private bool MatchesSearch(ContextMenuItemViewModel item)
    {
        return string.IsNullOrWhiteSpace(_appliedSearchText) || ContextMenuSearchMatcher.MatchesClassicItem(item, _appliedSearchText);
    }

    private static Type GetTargetPageType(ContextMenuCategory category) => category switch
    {
        ContextMenuCategory.File => typeof(FileContextMenuPage),
        ContextMenuCategory.AllFileSystemObjects => typeof(AllObjectsContextMenuPage),
        ContextMenuCategory.Folder => typeof(FolderContextMenuPage),
        ContextMenuCategory.Directory => typeof(DirectoryContextMenuPage),
        ContextMenuCategory.DirectoryBackground => typeof(BackgroundContextMenuPage),
        ContextMenuCategory.DesktopBackground => typeof(DesktopContextMenuPage),
        ContextMenuCategory.Drive => typeof(DriveContextMenuPage),
        ContextMenuCategory.Library => typeof(LibraryContextMenuPage),
        ContextMenuCategory.Computer => typeof(ComputerContextMenuPage),
        ContextMenuCategory.RecycleBin => typeof(RecycleBinContextMenuPage),
        _ => typeof(FileContextMenuPage)
    };

    private static string GetScopeValue(ContextMenuCategory category) => category switch
    {
        ContextMenuCategory.File => @"HKCR\*\shell",
        ContextMenuCategory.Folder => @"HKCR\Folder\shell",
        ContextMenuCategory.Directory => @"HKCR\Directory\shell",
        ContextMenuCategory.DirectoryBackground => @"HKCR\Directory\Background\shell",
        ContextMenuCategory.DesktopBackground => @"HKCR\DesktopBackground\shell",
        ContextMenuCategory.AllFileSystemObjects => @"HKCR\AllFilesystemObjects\shell",
        ContextMenuCategory.Drive => @"HKCR\Drive\shell",
        ContextMenuCategory.Library => @"HKCR\LibraryFolder\shell",
        ContextMenuCategory.Computer => @"HKCR\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell",
        ContextMenuCategory.RecycleBin => @"HKCR\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell",
        _ => @"HKCR\*\shell"
    };

    private static string BuildCommandText(ContextMenuCategory category, string targetPath, string? arguments)
    {
        var command = QuoteCommandPart(targetPath.Trim());
        var normalizedArguments = arguments?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedArguments))
        {
            command += " " + normalizedArguments;
        }

        if (!ContainsSelectedObjectPlaceholder(normalizedArguments))
        {
            command += " " + QuoteCommandPart(GetDefaultSelectedObjectPlaceholder(category));
        }

        return command;
    }

    private static bool ContainsSelectedObjectPlaceholder(string arguments)
    {
        return SelectedObjectPlaceholders.Any(placeholder => arguments.Contains(placeholder, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDefaultSelectedObjectPlaceholder(ContextMenuCategory category) => category switch
    {
        ContextMenuCategory.File or ContextMenuCategory.AllFileSystemObjects => "%1",
        _ => "%V"
    };

    private static string QuoteCommandPart(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string CreateSafeKeyName(string displayName)
    {
        var invalidCharacters = new HashSet<char>(['\\', '/', '*', '?', '"', '<', '>', '|']);
        var keyName = new string(displayName.Where(character => !char.IsControl(character) && !invalidCharacters.Contains(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(keyName) ? DefaultCustomMenuItemKeyName : keyName;
    }

    private static string SanitizeLogText(string? text)
    {
        return (text ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
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

        RefreshListPlaceholderState();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContextMenuItemViewModel.IsEnabled) or nameof(ContextMenuItemViewModel.IsDeleted)
            or nameof(ContextMenuItemViewModel.DisplayName) or nameof(ContextMenuItemViewModel.IsPendingApproval)
            or nameof(ContextMenuItemViewModel.IsDeleteBusy) or nameof(ContextMenuItemViewModel.IsDeleteOutcomeUncertain))
            RefreshListPlaceholderState();
    }

    private void RefreshListPlaceholderState()
    {
        if (_pendingDeletedCount is null)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _pendingDeletedCount = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, (Action)(() =>
            {
                _pendingDeletedCount = null;
                _deletedCandidateCount = GetDeletedCandidates().Count();
                OnPropertyChanged(nameof(CanPurgeDeletedItems));
            }));
        }
        OnPropertyChanged(nameof(IsListLoading));
        OnPropertyChanged(nameof(ShowListContent));
        OnPropertyChanged(nameof(HasListLoadFailure));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(ShowListPlaceholder));
        OnPropertyChanged(nameof(ListPlaceholderText));
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        CancelPendingSearch();
        _pendingDeletedCount?.Abort();
        _localization.LanguageChanged -= OnLanguageChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _loadingPresentation.PropertyChanged -= OnLoadingPresentationChanged;
        _loadingPresentation.Dispose();
        _globalSearchFilterService.FilterRequested -= OnGlobalSearchFilterRequested;
        _workspace.Items.CollectionChanged -= OnItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }
}
