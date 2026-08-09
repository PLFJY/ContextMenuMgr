using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the scene Context Menu Tab View Model.
/// </summary>
public partial class SceneContextMenuTabViewModel : ObservableObject, IDisposable
{
    private readonly IBackendClient _backendClient;
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly IconPreviewService _iconPreviewService;
    private readonly ContextMenuItemActionsService _actionsService;
    private readonly FrontendSettingsService _settingsService;
    private readonly ContextMenuApplicationIdentityService? _identityService;
    private readonly Func<ContextMenuItemViewModel, Task>? _openBatchManagementAsync;
    private readonly ContextMenuSceneKind _sceneKind;
    private readonly string? _fixedScopeValue;
    private readonly object _refreshSync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _refreshTask;
    private int _refreshGeneration;
    private bool _hasLoaded;
    private bool _disposed;
    private bool _lastHideDisabledItems;

    /// <summary>
    /// Initializes a new instance of the <see cref="SceneContextMenuTabViewModel"/> class.
    /// </summary>
    public SceneContextMenuTabViewModel(
        string iconSymbol,
        string title,
        string description,
        ContextMenuSceneKind sceneKind,
        IBackendClient backendClient,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        ContextMenuItemActionsService actionsService,
        FrontendSettingsService settingsService,
        string? fixedScopeValue = null,
        ContextMenuApplicationIdentityService? identityService = null,
        Func<ContextMenuItemViewModel, Task>? openBatchManagementAsync = null)
    {
        IconSymbol = iconSymbol;
        Title = title;
        Description = description;
        _sceneKind = sceneKind;
        _backendClient = backendClient;
        _workspace = workspace;
        _localization = localization;
        _iconPreviewService = iconPreviewService;
        _actionsService = actionsService;
        _settingsService = settingsService;
        _identityService = identityService;
        _openBatchManagementAsync = openBatchManagementAsync;
        _lastHideDisabledItems = settingsService.Current.HideDisabledItems;

        var itemsView = new ListCollectionView(Items);
        itemsView.Filter = FilterItem;
        ContextMenuListPresentation.ConfigureSort(itemsView);
        ItemsView = itemsView;

        _fixedScopeValue = fixedScopeValue;
        ScopeValue = fixedScopeValue ?? string.Empty;
        SelectorButtonText = localization.Translate("ApplySceneSelection");
        DeleteText = localization.Translate("Delete");
        PermanentDeleteText = localization.Translate("PermanentDelete");
        RegistryMissingText = localization.Translate("RegistryMissingText");
        CancelText = localization.Translate("DialogCancel");
        SearchLabel = localization.Translate("SearchLabel");
        AddMenuItemText = localization.Translate("AddMenuItem");
        BatchManagementText = localization.Translate("FileTypeBatchManageAction");

        _settingsService.SettingsChanged += OnSettingsChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        Items.CollectionChanged += OnItemsCollectionChanged;

        if (!string.IsNullOrWhiteSpace(_fixedScopeValue))
        {
            ObserveFireAndForget(EnsureLoadedAsync(), "FixedScopeInitialRefreshAsync");
        }
    }

    /// <summary>
    /// Gets the items.
    /// </summary>
    public ObservableCollection<ContextMenuItemViewModel> Items { get; } = [];

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
    /// Gets the icon Symbol.
    /// </summary>
    public string IconSymbol { get; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    [ObservableProperty]
    public partial string Description { get; set; }

    /// <summary>
    /// Gets or sets the search Text.
    /// </summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the scope Value.
    /// </summary>
    [ObservableProperty]
    public partial string ScopeValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether busy.
    /// </summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Gets or sets the empty Text.
    /// </summary>
    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the selector Label.
    /// </summary>
    [ObservableProperty]
    public partial string SelectorLabel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the selector Button Text.
    /// </summary>
    [ObservableProperty]
    public partial string SelectorButtonText { get; set; }

    /// <summary>
    /// Gets or sets the search Label.
    /// </summary>
    [ObservableProperty]
    public partial string SearchLabel { get; set; }

    [ObservableProperty]
    public partial string AddMenuItemText { get; set; }

    [ObservableProperty]
    public partial string BatchManagementText { get; set; }

    public string CompactViewText => _localization.Translate("CompactView");

    public string DetailedViewText => _localization.Translate("DetailedView");

    public string HideDisabledItemsText => _localization.Translate("HideDisabledItems");

    public string DisplayModeText => _localization.Translate("DisplayMode");

    public string CompactViewTooltip => _localization.Translate("CompactViewTooltip");

    public string DetailedViewTooltip => _localization.Translate("DetailedViewTooltip");

    public bool HasBatchManagementAction => _openBatchManagementAsync is not null;

    /// <summary>
    /// Gets or sets the delete Text.
    /// </summary>
    [ObservableProperty]
    public partial string DeleteText { get; set; }

    /// <summary>
    /// Gets or sets the permanent Delete Text.
    /// </summary>
    [ObservableProperty]
    public partial string PermanentDeleteText { get; set; }

    /// <summary>
    /// Gets or sets the registry Missing Text.
    /// </summary>
    [ObservableProperty]
    public partial string RegistryMissingText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether cel Text.
    /// </summary>
    [ObservableProperty]
    public partial string CancelText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether text Selector.
    /// </summary>
    [ObservableProperty]
    public partial bool HasTextSelector { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether option Selector.
    /// </summary>
    [ObservableProperty]
    public partial bool HasOptionSelector { get; set; }

    /// <summary>
    /// Gets or sets the options.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<SceneOptionViewModel> Options { get; set; } = [];

    /// <summary>
    /// Gets or sets the selected Option.
    /// </summary>
    [ObservableProperty]
    public partial SceneOptionViewModel? SelectedOption { get; set; }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedOptionChanged(SceneOptionViewModel? value)
    {
        if (value is not null)
        {
            ScopeValue = value.Value;
            ObserveFireAndForget(RefreshAsync(), "SelectedOptionRefreshAsync");
        }
    }

    /// <summary>
    /// Executes configure Text Selector.
    /// </summary>
    public void ConfigureTextSelector(string label, string initialValue)
    {
        HasTextSelector = true;
        HasOptionSelector = false;
        SelectorLabel = label;
        ScopeValue = initialValue;
    }

    /// <summary>
    /// Executes configure Option Selector.
    /// </summary>
    public void ConfigureOptionSelector(string label, IEnumerable<SceneOptionViewModel> options, string? selectedValue = null)
    {
        HasOptionSelector = true;
        HasTextSelector = false;
        SelectorLabel = label;
        Options = new ObservableCollection<SceneOptionViewModel>(options);
        SelectedOption = Options.FirstOrDefault(option => string.Equals(option.Value, selectedValue, StringComparison.OrdinalIgnoreCase))
            ?? Options.FirstOrDefault();
        ScopeValue = SelectedOption?.Value ?? string.Empty;
    }

    /// <summary>
    /// Refreshes async.
    /// </summary>
    [RelayCommand]
    public Task RefreshAsync()
    {
        return StartRefreshAsync(markLoaded: true);
    }

    public Task EnsureLoadedAsync()
    {
        lock (_refreshSync)
        {
            if (_hasLoaded)
            {
                return Task.CompletedTask;
            }
        }

        return StartRefreshAsync(markLoaded: true);
    }

    private Task StartRefreshAsync(bool markLoaded)
    {
        lock (_refreshSync)
        {
            if (_refreshTask is { IsCompleted: false })
            {
                return _refreshTask;
            }

            var generation = ++_refreshGeneration;
            _refreshTask = RefreshCoreAsync(generation, markLoaded);
            return _refreshTask;
        }
    }

    private async Task RefreshCoreAsync(int generation, bool markLoaded)
    {
        var scopeValue = ResolveScopeValue();
        if (RequiresScopeValue() && string.IsNullOrWhiteSpace(scopeValue))
        {
            ApplySnapshot([]);
            EmptyText = _localization.Translate("SceneSelectionRequired");
            return;
        }

        IsBusy = true;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _disposeCts.Token);
            var snapshot = await _backendClient.GetSceneSnapshotAsync(_sceneKind, scopeValue, linkedCts.Token);
            if (!IsCurrentRefreshGeneration(generation))
            {
                FrontendDebugLog.Info(
                    "SceneContextMenuTabViewModel",
                    $"RefreshAsync stale result ignored. SceneKind={_sceneKind}, Generation={generation}.");
                return;
            }

            ApplySnapshot(snapshot);
            EmptyText = snapshot.Count == 0
                ? _localization.Translate("SceneNoItems")
                : string.Empty;
            if (markLoaded)
            {
                _hasLoaded = true;
            }
        }
        catch (OperationCanceledException ex) when (_disposeCts.IsCancellationRequested || !IsCurrentRefreshGeneration(generation))
        {
            FrontendDebugLog.Info(
                "SceneContextMenuTabViewModel",
                $"RefreshAsync canceled benignly. SceneKind={_sceneKind}, Generation={generation}, Message={ex.Message}");
        }
        catch (ObjectDisposedException ex) when (_disposed)
        {
            FrontendDebugLog.Info(
                "SceneContextMenuTabViewModel",
                $"RefreshAsync skipped during disposal. SceneKind={_sceneKind}, Generation={generation}, Message={ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            FrontendDebugLog.Warning(
                "SceneContextMenuTabViewModel",
                $"RefreshAsync canceled by timeout or caller. SceneKind={_sceneKind}, Generation={generation}, Message={ex.Message}");
            EmptyText = _localization.Translate("OtherRulesClickRefreshToRetry");
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                "SceneContextMenuTabViewModel",
                $"RefreshAsync failed. SceneKind={_sceneKind}, Generation={generation}, Message={ex.Message}");
            ApplySnapshot([]);
            EmptyText = _localization.Format("OtherRulesTabLoadFailed", ex.Message);
        }
        finally
        {
            if (IsCurrentRefreshGeneration(generation))
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private Task DeleteOrUndoAsync(ContextMenuItemViewModel? item)
    {
        return item is null
            ? Task.CompletedTask
            : RunAndRefreshAsync(() => _workspace.DeleteOrUndoAsync(item));
    }

    [RelayCommand]
    private async Task AddMenuItemAsync()
    {
        var scopeValue = ResolveScopeValue();
        if (RequiresScopeValue() && string.IsNullOrWhiteSpace(scopeValue))
        {
            EmptyText = _localization.Translate("SceneSelectionRequired");
            return;
        }

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

        try
        {
            if (_settingsService.Current.LockNewContextMenuItems)
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return;
            }

            var commandText = string.IsNullOrWhiteSpace(formData.Arguments)
                ? formData.TargetPath
                : $"{formData.TargetPath} {formData.Arguments}";

            var request = new CreateSceneMenuItemRequest
            {
                SceneKind = _sceneKind,
                ScopeValue = scopeValue,
                ItemKind = SceneMenuItemKind.ShellVerb,
                KeyName = formData.Name.ToLowerInvariant().Replace(" ", "-"),
                DisplayName = formData.Name,
                Command = commandText,
                Icon = string.IsNullOrWhiteSpace(formData.IconPath) ? null : formData.IconPath
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updated = await _backendClient.CreateSceneMenuItemAsync(request, Guid.NewGuid(), cts.Token);
            if (updated is not null)
            {
                ApplySnapshot(Items.Select(static item => item.Entry).Append(updated).ToArray());
            }
            else
            {
                await RefreshAsync();
            }
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
    private Task OpenPermanentDeleteFlyoutAsync(ContextMenuItemViewModel? item)
    {
        if (item is not null)
        {
            item.IsPermanentDeleteFlyoutOpen = true;
        }

        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanOpenBatchManagement))]
    private Task OpenBatchManagementAsync(ContextMenuItemViewModel? item)
    {
        return item is null || _openBatchManagementAsync is null
            ? Task.CompletedTask
            : _openBatchManagementAsync(item);
    }

    private bool CanOpenBatchManagement(ContextMenuItemViewModel? item)
    {
        return item is not null
               && _openBatchManagementAsync is not null
               && (_identityService?.CanBatchManageFileTypeItem(item) ?? item.CanOpenFileTypeBatchManagement);
    }

    [RelayCommand]
    private async Task ConfirmPermanentDeleteAsync(ContextMenuItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsPermanentDeleteFlyoutOpen = false;
        await RunAndRefreshAsync(() => _workspace.PermanentlyDeleteAsync(item));
    }

    private async Task<bool> SetEnabledAsync(ContextMenuItemViewModel item, bool enable)
    {
        return await _workspace.SetEnabledAsync(item, enable);
    }

    private async Task<bool> SetShellAttributeAsync(ContextMenuItemViewModel item, ContextMenuShellAttribute attribute, bool enable)
    {
        return await _workspace.SetShellAttributeAsync(item, attribute, enable);
    }

    private async Task<bool> SetDisplayTextAsync(ContextMenuItemViewModel item, string textValue)
    {
        return await _workspace.SetDisplayTextAsync(item, textValue);
    }

    private async Task RunAndRefreshAsync(Func<Task> action)
    {
        await action();
        await RefreshAsync();
    }

    private void ApplySnapshot(IReadOnlyList<ContextMenuEntry> snapshot)
    {
        var existing = Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in snapshot)
        {
            if (existing.Remove(entry.Id, out var current))
            {
                current.Update(entry);
            }
            else
            {
                Items.Add(new ContextMenuItemViewModel(
                    entry,
                    _localization,
                    _iconPreviewService,
                    _actionsService,
                    SetEnabledAsync,
                    SetShellAttributeAsync,
                    SetDisplayTextAsync,
                    AcknowledgeItemStateAsync));
            }
        }

        foreach (var stale in existing.Values.ToList())
        {
            Items.Remove(stale);
        }

        ItemsView.Refresh();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not ContextMenuItemViewModel item)
        {
            return false;
        }

        if (!ContextMenuListPresentation.IsVisibleWithDisabledFilter(
                item.IsEnabled,
                item.IsDeleted,
                _settingsService.Current.HideDisabledItems))
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
               || Contains(item.Notes, search);
    }

    private async Task<bool> AcknowledgeItemStateAsync(ContextMenuItemViewModel item)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _backendClient.AcknowledgeItemStateAsync(item.Id, cts.Token);
            await RefreshAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveScopeValue()
    {
        return string.IsNullOrWhiteSpace(_fixedScopeValue)
            ? ScopeValue?.Trim()
            : _fixedScopeValue;
    }

    private bool RequiresScopeValue() =>
        _sceneKind is ContextMenuSceneKind.CustomExtension
            or ContextMenuSceneKind.PerceivedType
            or ContextMenuSceneKind.DirectoryType
            or ContextMenuSceneKind.CustomRegistryPath;

    private static bool Contains(string? value, string search)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var hideDisabledItems = _settingsService.Current.HideDisabledItems;
        if (_lastHideDisabledItems != hideDisabledItems)
        {
            _lastHideDisabledItems = hideDisabledItems;
            ItemsView.Refresh();
        }

        OnPropertyChanged(nameof(HideDisabledItems));
        OnPropertyChanged(nameof(IsCompactView));
        OnPropertyChanged(nameof(IsDetailedView));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        SelectorButtonText = _localization.Translate("ApplySceneSelection");
        DeleteText = _localization.Translate("Delete");
        PermanentDeleteText = _localization.Translate("PermanentDelete");
        RegistryMissingText = _localization.Translate("RegistryMissingText");
        CancelText = _localization.Translate("DialogCancel");
        SearchLabel = _localization.Translate("SearchLabel");
        AddMenuItemText = _localization.Translate("AddMenuItem");
        BatchManagementText = _localization.Translate("FileTypeBatchManageAction");
        OnPropertyChanged(nameof(CompactViewText));
        OnPropertyChanged(nameof(DetailedViewText));
        OnPropertyChanged(nameof(HideDisabledItemsText));
        OnPropertyChanged(nameof(DisplayModeText));
        OnPropertyChanged(nameof(CompactViewTooltip));
        OnPropertyChanged(nameof(DetailedViewTooltip));
        if (!Items.Any())
        {
            EmptyText = RequiresScopeValue() && string.IsNullOrWhiteSpace(ResolveScopeValue())
                ? _localization.Translate("SceneSelectionRequired")
                : _localization.Translate("SceneNoItems");
        }
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
            or nameof(ContextMenuItemViewModel.HasConsistencyIssue)
            or nameof(ContextMenuItemViewModel.IsEnabled))
        {
            ItemsView.Refresh();
        }
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        Items.CollectionChanged -= OnItemsCollectionChanged;
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private bool IsCurrentRefreshGeneration(int generation)
    {
        lock (_refreshSync)
        {
            return generation == _refreshGeneration && !_disposed;
        }
    }

    private void ObserveFireAndForget(Task task, string operationName)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveFireAndForgetAsync(task, operationName);
    }

    private async Task ObserveFireAndForgetAsync(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException ex)
        {
            FrontendDebugLog.Info(
                "SceneContextMenuTabViewModel",
                $"{operationName} canceled. SceneKind={_sceneKind}, Message={ex.Message}");
        }
        catch (ObjectDisposedException ex)
        {
            FrontendDebugLog.Info(
                "SceneContextMenuTabViewModel",
                $"{operationName} skipped during disposal. SceneKind={_sceneKind}, Message={ex.Message}");
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                "SceneContextMenuTabViewModel",
                $"{operationName} failed. SceneKind={_sceneKind}, Message={ex.Message}");
        }
    }
}
