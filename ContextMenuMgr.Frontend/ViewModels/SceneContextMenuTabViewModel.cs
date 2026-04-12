using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class SceneContextMenuTabViewModel : ObservableObject, IDisposable
{
    private readonly IBackendClient _backendClient;
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly IconPreviewService _iconPreviewService;
    private readonly ContextMenuItemActionsService _actionsService;
    private readonly FrontendSettingsService _settingsService;
    private readonly ContextMenuSceneKind _sceneKind;
    private readonly string? _fixedScopeValue;

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
        string? fixedScopeValue = null)
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

        ItemsView = new ListCollectionView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ContextMenuItemViewModel.SortAttentionWeight), ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ContextMenuItemViewModel.SortDeletedWeight), ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ContextMenuItemViewModel.DisplayName), ListSortDirection.Ascending));

        _fixedScopeValue = fixedScopeValue;
        ScopeValue = fixedScopeValue ?? string.Empty;
        SelectorButtonText = localization.Translate("ApplySceneSelection");
        DeleteText = localization.Translate("Delete");
        PermanentDeleteText = localization.Translate("PermanentDelete");
        RegistryMissingText = localization.Translate("RegistryMissingText");
        CancelText = localization.Translate("DialogCancel");
        SearchLabel = localization.Translate("SearchLabel");

        _settingsService.SettingsChanged += OnSettingsChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        Items.CollectionChanged += OnItemsCollectionChanged;

        if (!string.IsNullOrWhiteSpace(_fixedScopeValue))
        {
            _ = RefreshAsync();
        }
    }

    public ObservableCollection<ContextMenuItemViewModel> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    public string IconSymbol { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScopeValue { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectorLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectorButtonText { get; set; }

    [ObservableProperty]
    public partial string SearchLabel { get; set; }

    [ObservableProperty]
    public partial string DeleteText { get; set; }

    [ObservableProperty]
    public partial string PermanentDeleteText { get; set; }

    [ObservableProperty]
    public partial string RegistryMissingText { get; set; }

    [ObservableProperty]
    public partial string CancelText { get; set; }

    [ObservableProperty]
    public partial bool HasTextSelector { get; set; }

    [ObservableProperty]
    public partial bool HasOptionSelector { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<SceneOptionViewModel> Options { get; set; } = [];

    [ObservableProperty]
    public partial SceneOptionViewModel? SelectedOption { get; set; }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedOptionChanged(SceneOptionViewModel? value)
    {
        if (value is not null)
        {
            ScopeValue = value.Value;
            _ = RefreshAsync();
        }
    }

    public void ConfigureTextSelector(string label, string initialValue)
    {
        HasTextSelector = true;
        HasOptionSelector = false;
        SelectorLabel = label;
        ScopeValue = initialValue;
    }

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

    [RelayCommand]
    public async Task RefreshAsync()
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var snapshot = await _backendClient.GetSceneSnapshotAsync(_sceneKind, scopeValue, cts.Token);
            ApplySnapshot(snapshot);
            EmptyText = snapshot.Count == 0
                ? _localization.Translate("SceneNoItems")
                : string.Empty;
        }
        catch (Exception ex)
        {
            ApplySnapshot([]);
            EmptyText = _localization.Format("BackendUnavailableStatus", ex.Message);
        }
        finally
        {
            IsBusy = false;
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
        await RunAndRefreshAsync(() => _workspace.PermanentlyDeleteAsync(item));
    }

    private async Task<bool> SetEnabledAsync(ContextMenuItemViewModel item, bool enable)
    {
        var success = await _workspace.SetEnabledAsync(item, enable);
        if (success)
        {
            await RefreshAsync();
        }

        return success;
    }

    private async Task<bool> SetShellAttributeAsync(ContextMenuItemViewModel item, ContextMenuShellAttribute attribute, bool enable)
    {
        var success = await _workspace.SetShellAttributeAsync(item, attribute, enable);
        if (success)
        {
            await RefreshAsync();
        }

        return success;
    }

    private async Task<bool> SetDisplayTextAsync(ContextMenuItemViewModel item, string textValue)
    {
        var success = await _workspace.SetDisplayTextAsync(item, textValue);
        if (success)
        {
            await RefreshAsync();
        }

        return success;
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

        if (_settingsService.Current.HideDisabledItems && !item.IsEnabled && !item.IsDeleted)
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
        ItemsView.Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        SelectorButtonText = _localization.Translate("ApplySceneSelection");
        DeleteText = _localization.Translate("Delete");
        PermanentDeleteText = _localization.Translate("PermanentDelete");
        RegistryMissingText = _localization.Translate("RegistryMissingText");
        CancelText = _localization.Translate("DialogCancel");
        SearchLabel = _localization.Translate("SearchLabel");
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

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        Items.CollectionChanged -= OnItemsCollectionChanged;
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }
}
