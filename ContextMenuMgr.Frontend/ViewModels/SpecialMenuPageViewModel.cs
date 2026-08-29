using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents a special menu page view model.
/// </summary>
public partial class SpecialMenuPageViewModel : ObservableObject, IDisposable
{
    private readonly IBackendClient _backendClient;
    private readonly IconPreviewService _iconPreviewService;
    private readonly LocalizationService _localization;
    private readonly ExplorerRestartStateService _explorerRestartState;
    private readonly ListPlaceholderDebugStateService _placeholderDebug;
    private readonly string _titleKey;
    private readonly string _descriptionKey;
    private readonly Dictionary<string, bool> _winXExpandedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _refreshSync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _refreshTask;
    private Task? _autoRefreshTask;
    private int _refreshGeneration;
    private bool _hasLoaded;
    private bool _disposed;
    private bool _suppressShellNewLockSync;
    private bool _suppressDropEffectSync;
    private bool _suppressDocumentIconProviderSync;
    private bool _hasDocumentIconProviderChanged;

    public SpecialMenuPageViewModel(
        SpecialMenuKind kind,
        string titleKey,
        string descriptionKey,
        IBackendClient backendClient,
        IconPreviewService iconPreviewService,
        LocalizationService localization,
        ExplorerRestartStateService explorerRestartState,
        ListPlaceholderDebugStateService placeholderDebug)
    {
        Kind = kind;
        _titleKey = titleKey;
        _descriptionKey = descriptionKey;
        _backendClient = backendClient;
        _iconPreviewService = iconPreviewService;
        _localization = localization;
        _explorerRestartState = explorerRestartState;
        _placeholderDebug = placeholderDebug;
        ItemsView = new ListCollectionView(Items);
        ItemsView.Filter = FilterItem;
        SearchLabel = _localization.Translate("SearchLabel");
        AddText = _localization.Translate("Add");
        EditText = _localization.Translate("Edit");
        RefreshText = _localization.Translate("Refresh");
        RestoreDefaultsText = _localization.Translate("RestoreDefault");
        LockNewMenuText = _localization.Translate("LockNewMenu");
        LockNewMenuDescription = _localization.Translate("LockNewMenuDescription");
        WinXCompatibilityNoticeText = _localization.Translate("WinXCompatibilityNotice");
        WinXCompatibilityNoticeToolTip = _localization.Translate("WinXCompatibilityNoticeToolTip");
        DeleteText = _localization.Translate("Delete");
        UndoText = _localization.Translate("Undo");
        PermanentDeleteText = _localization.Translate("PermanentDelete");
        CancelText = _localization.Translate("DialogCancel");
        DropEffectLabel = _localization.Translate("DefaultDropEffect");
        DocumentIconProviderLabel = _localization.Translate("DocumentIconProvider");
        DocumentIconProviderDescription = _localization.Translate("DocumentIconProviderDescription");
        DocumentIconProviderChangedHint = _localization.Translate("DocumentIconProviderChangedHint");
        DropEffectOptions =
        [
            DefaultDropEffect.Default,
            DefaultDropEffect.Copy,
            DefaultDropEffect.Move,
            DefaultDropEffect.CreateLink
        ];
        _backendClient.NotificationReceived += OnNotificationReceived;
        _localization.LanguageChanged += OnLanguageChanged;
        _placeholderDebug.PropertyChanged += OnPlaceholderDebugPropertyChanged;
        Items.CollectionChanged += OnItemsCollectionChanged;
        WinXGroups.CollectionChanged += OnWinXGroupsCollectionChanged;
    }

    public SpecialMenuKind Kind { get; }

    public ObservableCollection<SpecialMenuItemViewModel> Items { get; } = [];

    public ObservableCollection<WinXGroupNodeViewModel> WinXGroups { get; } = [];

    public ICollectionView ItemsView { get; }

    public string Title => _localization.Translate(_titleKey);

    public string Description => _localization.Translate(_descriptionKey);

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchLabel { get; set; }

    [ObservableProperty]
    public partial string AddText { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; }

    [ObservableProperty]
    public partial string RefreshText { get; set; }

    [ObservableProperty]
    public partial string RestoreDefaultsText { get; set; }

    [ObservableProperty]
    public partial string LockNewMenuText { get; set; }

    [ObservableProperty]
    public partial string LockNewMenuDescription { get; set; }

    [ObservableProperty]
    public partial string WinXCompatibilityNoticeText { get; set; }

    [ObservableProperty]
    public partial string WinXCompatibilityNoticeToolTip { get; set; }

    [ObservableProperty]
    public partial string DeleteText { get; set; }

    [ObservableProperty]
    public partial string UndoText { get; set; }

    [ObservableProperty]
    public partial string PermanentDeleteText { get; set; }

    [ObservableProperty]
    public partial string CancelText { get; set; }

    [ObservableProperty]
    public partial string DropEffectLabel { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<DefaultDropEffect> DropEffectOptions { get; set; } = [];

    [ObservableProperty]
    public partial DefaultDropEffect SelectedDropEffect { get; set; }

    [ObservableProperty]
    public partial string DocumentIconProviderLabel { get; set; }

    [ObservableProperty]
    public partial string DocumentIconProviderDescription { get; set; }

    [ObservableProperty]
    public partial string DocumentIconProviderChangedHint { get; set; }

    public string MicrosoftOfficeLabel => "Microsoft Office";

    public string WpsOfficeLabel => "WPS Office";

    [ObservableProperty]
    public partial DocumentIconProvider SelectedDocumentIconProvider { get; set; }

    [ObservableProperty]
    public partial bool IsWpsDocumentIconProviderSelected { get; set; }

    [ObservableProperty]
    public partial bool ShowDocumentIconProvider { get; set; }

    public bool ShowDocumentIconProviderChangedHint => ShowDocumentIconProvider && _hasDocumentIconProviderChanged;

    [ObservableProperty]
    public partial bool IsShellNewOrderLocked { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool ShowShellNewOrderLock => Kind == SpecialMenuKind.ShellNew;

    public bool ShowRestoreDefaults => Kind == SpecialMenuKind.SendTo;

    public bool ShowDropEffectSelector => Kind == SpecialMenuKind.DragDrop;

    public bool ShowWinXTree => Kind == SpecialMenuKind.WinX;

    public bool ShowWinXCompatibilityNotice => Kind == SpecialMenuKind.WinX;

    public string LoadingItemsText => _localization.Translate("LoadingItemsText");

    public string EmptyItemsText => _localization.Translate("EmptyItemsText");

    public bool IsListLoading => _placeholderDebug.ForceLoadingState || IsBusy;

    public bool IsListEmpty
    {
        get
        {
            if (IsListLoading)
            {
                return false;
            }

            if (_placeholderDebug.ForceEmptyState)
            {
                return true;
            }

            if (Kind == SpecialMenuKind.WinX)
            {
                return WinXGroups.Count == 0
                       || WinXGroups.All(static group => group.Items.Count == 0);
            }

            return !ItemsView.Cast<object>().Any();
        }
    }

    public bool ShowListPlaceholder => IsListLoading || IsListEmpty;

    partial void OnSearchTextChanged(string value)
    {
        ItemsView.Refresh();
        RefreshListPlaceholderState();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshListPlaceholderState();
    }

    partial void OnIsShellNewOrderLockedChanged(bool oldValue, bool newValue)
    {
        if (_suppressShellNewLockSync || oldValue == newValue || Kind != SpecialMenuKind.ShellNew)
        {
            return;
        }

        ObserveFireAndForget(SetShellNewOrderLockAsync(oldValue, newValue), "SetShellNewOrderLockAsync");
    }

    partial void OnSelectedDropEffectChanged(DefaultDropEffect oldValue, DefaultDropEffect newValue)
    {
        if (_suppressDropEffectSync || oldValue == newValue || Kind != SpecialMenuKind.DragDrop)
        {
            return;
        }

        ObserveFireAndForget(SetDefaultDropEffectAsync(oldValue, newValue), "SetDefaultDropEffectAsync");
    }

    partial void OnSelectedDocumentIconProviderChanged(DocumentIconProvider oldValue, DocumentIconProvider newValue)
    {
        if (_suppressDocumentIconProviderSync || oldValue == newValue)
        {
            return;
        }

        IsWpsDocumentIconProviderSelected = newValue == DocumentIconProvider.WpsOffice;
    }

    partial void OnIsWpsDocumentIconProviderSelectedChanged(bool oldValue, bool newValue)
    {
        if (_suppressDocumentIconProviderSync || oldValue == newValue || !ShowDocumentIconProvider)
        {
            return;
        }

        var oldProvider = oldValue ? DocumentIconProvider.WpsOffice : DocumentIconProvider.MicrosoftOffice;
        var newProvider = newValue ? DocumentIconProvider.WpsOffice : DocumentIconProvider.MicrosoftOffice;
        ObserveFireAndForget(SetDocumentIconProviderAsync(oldProvider, newProvider), "SetDocumentIconProviderAsync");
    }

    partial void OnShowDocumentIconProviderChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDocumentIconProviderChangedHint));
    }

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
                StartAutoRefreshLoop();
                return Task.CompletedTask;
            }
        }

        return StartRefreshAsync(markLoaded: true);
    }

    private Task StartRefreshAsync(bool markLoaded, bool showBusy = true)
    {
        lock (_refreshSync)
        {
            if (_refreshTask is { IsCompleted: false })
            {
                return _refreshTask;
            }

            var generation = ++_refreshGeneration;
            _refreshTask = RefreshCoreAsync(generation, markLoaded, showBusy);
            return _refreshTask;
        }
    }

    private async Task RefreshCoreAsync(int generation, bool markLoaded, bool showBusy)
    {
        if (showBusy)
        {
            await SetBusyAsync(true);
            await YieldToUiAsync();
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _disposeCts.Token);
            var snapshot = await _backendClient.GetSpecialMenuSnapshotAsync(Kind, linkedCts.Token);
            if (!IsCurrentRefreshGeneration(generation))
            {
                FrontendDebugLog.Info(
                    "SpecialMenuPageViewModel",
                    $"RefreshAsync stale result ignored. Kind={Kind}, Generation={generation}.");
                return;
            }

            await ApplySnapshotAsync(snapshot);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentRefreshGeneration(generation))
                {
                    return;
                }

                StatusText = string.Empty;
                if (markLoaded)
                {
                    _hasLoaded = true;
                    StartAutoRefreshLoop();
                }
            });

            await RefreshOfficeCoexistenceStatusAsync(linkedCts.Token);
        }
        catch (OperationCanceledException ex) when (_disposeCts.IsCancellationRequested || !IsCurrentRefreshGeneration(generation))
        {
            FrontendDebugLog.Info(
                "SpecialMenuPageViewModel",
                $"RefreshAsync canceled benignly. Kind={Kind}, Generation={generation}, Message={ex.Message}");
        }
        catch (ObjectDisposedException ex) when (_disposed)
        {
            FrontendDebugLog.Info(
                "SpecialMenuPageViewModel",
                $"RefreshAsync skipped during disposal. Kind={Kind}, Generation={generation}, Message={ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            FrontendDebugLog.Warning(
                "SpecialMenuPageViewModel",
                $"RefreshAsync canceled by timeout or caller. Kind={Kind}, Generation={generation}, Message={ex.Message}");
            if (showBusy)
            {
                StatusText = _localization.Translate("OtherRulesClickRefreshToRetry");
            }
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                "SpecialMenuPageViewModel",
                $"RefreshAsync failed. Kind={Kind}, Generation={generation}, Message={ex.Message}");
            if (showBusy)
            {
                StatusText = _localization.Format("OtherRulesTabLoadFailed", ex.Message);
            }
        }
        finally
        {
            if (showBusy && IsCurrentRefreshGeneration(generation))
            {
                await SetBusyAsync(false);
            }
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        try
        {
            var operationId = Guid.NewGuid();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var request = await CreateAddRequestAsync(operationId);
            if (request is null)
            {
                return;
            }

            var item = await _backendClient.CreateSpecialMenuItemAsync(request, cts.Token);
            if (item is not null)
            {
                if (Kind == SpecialMenuKind.WinX)
                {
                    _explorerRestartState.MarkRequired();
                    await RefreshAsync();
                }
                else
                {
                    Upsert(item);
                }
            }
        }
        catch (OperationCanceledException) when (Kind == SpecialMenuKind.WinX)
        {
            await FrontendMessageBox.ShowErrorAsync(
                "Win+X create timed out. Check backend logs for WinXCreate stage logs.",
                Title);
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, Title);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SpecialMenuItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (ProtectedMenuItemGuard.IsProtectedOpenItem(item)
                && !await ProtectedMenuItemGuard.ConfirmAsync(_localization))
            {
                return;
            }

            item.IsBusy = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updated = await _backendClient.DeleteSpecialMenuItemAsync(item.Entry, Guid.NewGuid(), cts.Token);
            if (updated is not null)
            {
                item.Update(updated);
            }
            else
            {
                Items.Remove(item);
            }

            if (Kind == SpecialMenuKind.WinX)
            {
                _explorerRestartState.MarkRequired();
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, item.DisplayName);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UndoDeleteAsync(SpecialMenuItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            item.IsBusy = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updated = await _backendClient.UndoDeleteSpecialMenuItemAsync(item.Entry, Guid.NewGuid(), cts.Token);
            if (updated is not null)
            {
                item.Update(updated);
            }
            else
            {
                Items.Remove(item);
            }

            if (Kind == SpecialMenuKind.WinX)
            {
                _explorerRestartState.MarkRequired();
            }
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, item.DisplayName);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenPermanentDeleteFlyoutAsync(SpecialMenuItemViewModel? item)
    {
        if (item is not null)
        {
            item.IsPermanentDeleteFlyoutOpen = true;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConfirmPermanentDeleteAsync(SpecialMenuItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsPermanentDeleteFlyoutOpen = false;
        try
        {
            item.IsBusy = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _backendClient.PurgeDeletedSpecialMenuItemAsync(item.Entry, Guid.NewGuid(), cts.Token);
            Items.Remove(item);
            if (Kind == SpecialMenuKind.WinX)
            {
                _explorerRestartState.MarkRequired();
            }
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, item.DisplayName);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EditAsync(SpecialMenuItemViewModel? item)
    {
        if (item is null || !item.CanEdit)
        {
            return;
        }

        try
        {
            var operationId = Guid.NewGuid();
            var request = await CreateUpdateRequestAsync(item, operationId);
            if (request is null)
            {
                return;
            }

            item.IsBusy = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var updated = await _backendClient.UpdateSpecialMenuItemAsync(request, cts.Token);
            if (updated is not null)
            {
                if (Kind == SpecialMenuKind.WinX)
                {
                    _explorerRestartState.MarkRequired();
                    await RefreshAsync();
                }
                else
                {
                    item.Update(updated);
                }
            }
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, item.DisplayName);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MoveUpAsync(SpecialMenuItemViewModel? item) => await MoveAsync(item, true);

    [RelayCommand]
    private async Task MoveDownAsync(SpecialMenuItemViewModel? item) => await MoveAsync(item, false);

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (Kind != SpecialMenuKind.SendTo)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _backendClient.RestoreSpecialMenuDefaultsAsync(Kind, null, Guid.NewGuid(), cts.Token);
            if (Kind == SpecialMenuKind.WinX)
            {
                _explorerRestartState.MarkRequired();
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, Title);
        }
    }

    private async Task MoveAsync(SpecialMenuItemViewModel? item, bool moveUp)
    {
        if (item is null)
        {
            return;
        }

        if ((moveUp && !item.CanMoveUp) || (!moveUp && !item.CanMoveDown))
        {
            return;
        }

        try
        {
            StatusText = string.Empty;
            item.IsBusy = true;
            var operationId = Guid.NewGuid();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var request = Kind == SpecialMenuKind.ShellNew
                ? new PipeRequest
                {
                    SpecialKind = Kind,
                    ShellNewSort = new ShellNewSortRequest(item.Id, moveUp),
                    ClientOperationId = operationId
                }
                : new PipeRequest
                {
                    SpecialKind = Kind,
                    WinXMove = new WinXMoveRequest(item.Id, moveUp),
                    ClientOperationId = operationId
                };
            var updated = await _backendClient.MoveSpecialMenuItemAsync(request, cts.Token);
            if (updated is not null)
            {
                if (Kind == SpecialMenuKind.WinX)
                {
                    _explorerRestartState.MarkRequired();
                }
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            if (Kind == SpecialMenuKind.ShellNew && IsExpectedShellNewMoveFailure(ex))
            {
                StatusText = ex.Message;
                return;
            }

            await FrontendMessageBox.ShowErrorAsync(ex.Message, item.DisplayName);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task<bool> SetEnabledAsync(SpecialMenuItemViewModel item, bool enabled)
    {
        try
        {
            if (!enabled
                && ProtectedMenuItemGuard.IsProtectedOpenItem(item)
                && !await ProtectedMenuItemGuard.ConfirmAsync(_localization))
            {
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updated = await _backendClient.SetSpecialMenuItemEnabledAsync(item.Entry, enabled, Guid.NewGuid(), cts.Token);
            if (updated is not null)
            {
                item.Update(updated);
                if (Kind == SpecialMenuKind.WinX)
                {
                    _explorerRestartState.MarkRequired();
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, item.DisplayName);
            return false;
        }
    }

    private async Task<PipeRequest?> CreateAddRequestAsync(Guid operationId)
    {
        switch (Kind)
        {
            case SpecialMenuKind.ShellNew:
                var shellNewAddData = await MenuItemFormDialog.ShowAddShellNewAsync(Title, _localization);
                return shellNewAddData is null
                    ? null
                    : new PipeRequest
                    {
                        SpecialKind = Kind,
                        ShellNewCreate = new ShellNewCreateRequest(
                            shellNewAddData.Extension,
                            EmptyToNull(shellNewAddData.DisplayName),
                            EmptyToNull(shellNewAddData.IconPath),
                            EmptyToNull(shellNewAddData.Command),
                            null,
                            shellNewAddData.BeforeSeparator),
                        ClientOperationId = operationId
                    };
            case SpecialMenuKind.SendTo:
                var sendToData = await MenuItemFormDialog.ShowAddSendToAsync(Title, _localization);
                if (sendToData is null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(sendToData.Name))
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), Title);
                    return null;
                }

                return new PipeRequest { SpecialKind = Kind, SendToCreate = new SendToCreateRequest(sendToData.Name, sendToData.TargetPath, sendToData.Arguments), ClientOperationId = operationId };
            case SpecialMenuKind.WinX:
                var winxData = await MenuItemFormDialog.ShowAddWinXAsync(Title, _localization);
                if (winxData is null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(winxData.Name))
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), Title);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(winxData.TargetPath))
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), Title);
                    return null;
                }

                if (string.Equals(winxData.GroupName, "Group:", StringComparison.OrdinalIgnoreCase) || winxData.GroupName.StartsWith("Group:", StringComparison.OrdinalIgnoreCase))
                {
                    return new PipeRequest { SpecialKind = Kind, WinXCreateGroup = new WinXCreateGroupRequest(winxData.GroupName.Length > 6 ? winxData.GroupName[6..].Trim() : string.Empty), ClientOperationId = operationId };
                }

                return new PipeRequest
                {
                    SpecialKind = Kind,
                    WinXCreateEntry = new WinXCreateEntryRequest(
                        winxData.Name,
                        winxData.TargetPath,
                        winxData.GroupName,
                        EmptyToNull(winxData.Arguments),
                        EmptyToNull(winxData.WorkingDirectory),
                        EmptyToNull(winxData.IconPath),
                        winxData.RunAsAdmin),
                    ClientOperationId = operationId
                };
            case SpecialMenuKind.OpenWith:
                var openWithData = await MenuItemFormDialog.ShowAddOpenWithAsync(Title, _localization);
                if (openWithData is null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(openWithData.Name) || string.IsNullOrWhiteSpace(openWithData.TargetPath))
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), Title);
                    return null;
                }

                return new PipeRequest
                {
                    SpecialKind = Kind,
                    OpenWithCreate = new OpenWithCreateRequest(
                        openWithData.Name,
                        openWithData.TargetPath,
                        EmptyToNull(openWithData.Arguments)),
                    ClientOperationId = operationId
                };
            case SpecialMenuKind.DragDrop:
                var dragDrop = await TextInputDialog.ShowAsync(Title, "GUID|Group(Folder/Directory/Drive/AllFilesystemObjects)", "{00000000-0000-0000-0000-000000000000}|Folder");
                var dragDropParts = SplitParts(dragDrop, 2);
                return dragDropParts is null
                    ? null
                    : new PipeRequest { SpecialKind = Kind, DragDropCreate = new DragDropCreateRequest(dragDropParts[0], dragDropParts[1]), ClientOperationId = operationId };
            case SpecialMenuKind.CommandStore:
                var commandStore = await TextInputDialog.ShowAsync(Title, "Key|Name|Command", "my.command|My Command|notepad.exe \"%1\"");
                var commandStoreParts = SplitParts(commandStore, 3);
                return commandStoreParts is null
                    ? null
                    : new PipeRequest
                    {
                        SpecialKind = Kind,
                        SpecialItem = new SpecialMenuEntry { Kind = Kind, KeyName = commandStoreParts[0], DisplayName = commandStoreParts[1], CommandText = commandStoreParts[2] },
                        ClientOperationId = operationId
                    };
            case SpecialMenuKind.GuidBlock:
                var guid = await TextInputDialog.ShowAsync(Title, "GUID", "{00000000-0000-0000-0000-000000000000}");
                return string.IsNullOrWhiteSpace(guid)
                    ? null
                    : new PipeRequest { SpecialKind = Kind, GuidBlockCreate = new GuidBlockCreateRequest(guid), ClientOperationId = operationId };
            case SpecialMenuKind.InternetExplorer:
                var ie = await TextInputDialog.ShowAsync(Title, "Name|Command", "Open with Notepad|notepad.exe");
                var ieParts = SplitParts(ie, 2);
                return ieParts is null
                    ? null
                    : new PipeRequest { SpecialKind = Kind, IeMenuCreate = new IeMenuCreateRequest(ieParts[0], ieParts[1]), ClientOperationId = operationId };
        }

        return null;
    }

    private async Task<PipeRequest?> CreateUpdateRequestAsync(SpecialMenuItemViewModel item, Guid operationId)
    {
        switch (Kind)
        {
            case SpecialMenuKind.ShellNew:
                var shellNewInitialData = new ShellNewFormData
                {
                    Extension = item.Id,
                    DisplayName = item.DisplayName,
                    IconPath = item.Entry.IconPath ?? string.Empty,
                    Command = item.Entry.CommandText ?? string.Empty,
                    BeforeSeparator = false
                };
                var shellNewEditData = await MenuItemFormDialog.ShowEditShellNewAsync(Title, shellNewInitialData, _localization);
                return shellNewEditData is null
                    ? null
                    : new PipeRequest
                    {
                        SpecialKind = Kind,
                        ShellNewUpdate = new ShellNewUpdateRequest(
                            shellNewEditData.Extension,
                            string.IsNullOrWhiteSpace(shellNewEditData.DisplayName) ? null : shellNewEditData.DisplayName,
                            string.IsNullOrWhiteSpace(shellNewEditData.IconPath) ? null : shellNewEditData.IconPath,
                            string.IsNullOrWhiteSpace(shellNewEditData.Command) ? null : shellNewEditData.Command,
                            null,
                            shellNewEditData.BeforeSeparator),
                        ClientOperationId = operationId
                    };
            case SpecialMenuKind.SendTo:
                var sendToInitialData = new MenuItemFormData
                {
                    Name = item.DisplayName,
                    TargetPath = item.Entry.TargetPath ?? string.Empty,
                    Arguments = item.Entry.Arguments ?? string.Empty,
                    WorkingDirectory = item.Entry.WorkingDirectory ?? string.Empty,
                    IconPath = item.Entry.IconPath ?? string.Empty
                };
                var sendToData = await MenuItemFormDialog.ShowEditSendToAsync(Title, sendToInitialData, _localization);
                if (sendToData is null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(sendToData.Name))
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), Title);
                    return null;
                }

                return new PipeRequest
                {
                    SpecialKind = Kind,
                    SendToUpdate = new SendToUpdateRequest(
                        item.Id,
                        sendToData.Name,
                        sendToData.TargetPath,
                        sendToData.Arguments,
                        sendToData.WorkingDirectory,
                        sendToData.IconPath,
                        sendToData.RunAsAdmin),
                    ClientOperationId = operationId
                };
            case SpecialMenuKind.WinX:
                var winxInitialData = new MenuItemFormData
                {
                    Name = item.DisplayName,
                    TargetPath = item.Entry.TargetPath ?? string.Empty,
                    Arguments = item.Entry.Arguments ?? string.Empty,
                    WorkingDirectory = item.Entry.WorkingDirectory ?? string.Empty,
                    IconPath = item.Entry.IconPath ?? string.Empty,
                    GroupName = item.Entry.GroupName ?? string.Empty
                };
                var winxData = await MenuItemFormDialog.ShowEditWinXAsync(Title, winxInitialData, _localization);
                if (winxData is null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(winxData.Name))
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), Title);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(winxData.TargetPath))
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), Title);
                    return null;
                }

                // Guard against editing Win+X group headers as normal entries.
                if (item.Entry.Metadata.GetValueOrDefault("EntryType") == "Group")
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("CannotEditWinXGroupHeader"), Title);
                    return null;
                }

                return new PipeRequest
                {
                    SpecialKind = Kind,
                    WinXUpdateEntry = new WinXUpdateEntryRequest(
                        item.Id,
                        winxData.Name,
                        winxData.TargetPath,
                        EmptyToNull(winxData.Arguments),
                        EmptyToNull(winxData.WorkingDirectory),
                        winxData.GroupName,
                        EmptyToNull(winxData.IconPath),
                        winxData.RunAsAdmin),
                    ClientOperationId = operationId
                };
            case SpecialMenuKind.OpenWith:
                var openWithInitialData = new MenuItemFormData
                {
                    Name = item.DisplayName,
                    TargetPath = item.Entry.TargetPath ?? string.Empty,
                    Arguments = item.Entry.CommandText ?? string.Empty,
                    IconPath = item.Entry.IconPath ?? string.Empty
                };
                var openWithData = await MenuItemFormDialog.ShowEditOpenWithAsync(Title, openWithInitialData, _localization);
                if (openWithData is null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(openWithData.Name))
                {
                    await FrontendMessageBox.ShowErrorAsync(_localization.Translate("TextCannotBeEmpty"), Title);
                    return null;
                }

                return new PipeRequest
                {
                    SpecialKind = Kind,
                    OpenWithUpdate = new OpenWithUpdateRequest(
                        item.Id,
                        openWithData.Name,
                        EmptyToNull(openWithData.Arguments)),
                    ClientOperationId = operationId
                };
            case SpecialMenuKind.CommandStore:
                var commandStore = await TextInputDialog.ShowAsync(Title, "Name|Command|Icon", $"{item.DisplayName}|{item.Entry.CommandText}|{item.Entry.IconPath}");
                var commandStoreParts = SplitParts(commandStore, 3, allowEmptyMiddleParts: true);
                return commandStoreParts is null
                    ? null
                    : new PipeRequest
                    {
                        SpecialKind = Kind,
                        SpecialItem = item.Entry with
                        {
                            DisplayName = commandStoreParts[0],
                            CommandText = EmptyToNull(commandStoreParts[1]),
                            IconPath = EmptyToNull(commandStoreParts[2])
                        },
                        ClientOperationId = operationId
                    };
            case SpecialMenuKind.InternetExplorer:
                var ie = await TextInputDialog.ShowAsync(Title, "Name|Command", $"{item.DisplayName}|{item.Entry.CommandText}");
                var ieParts = SplitParts(ie, 2, allowEmptyMiddleParts: true);
                return ieParts is null
                    ? null
                    : new PipeRequest
                    {
                        SpecialKind = Kind,
                        IeMenuUpdate = new IeMenuUpdateRequest(item.Id, EmptyToNull(ieParts[0]), EmptyToNull(ieParts[1])),
                        ClientOperationId = operationId
                    };
        }

        return null;
    }

    private async Task ApplySnapshotAsync(IEnumerable<SpecialMenuEntry> snapshot)
    {
        var entries = snapshot.ToArray();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => ApplySnapshotCoreAsync(entries)).Task.Unwrap();
            return;
        }

        await ApplySnapshotCoreAsync(entries);
    }

    private async Task ApplySnapshotCoreAsync(IReadOnlyList<SpecialMenuEntry> entries)
    {
        if (Kind == SpecialMenuKind.WinX)
        {
            ApplyWinXSnapshotIncrementally(entries);
            return;
        }

        var isInitialLoad = Items.Count == 0;
        if (isInitialLoad)
        {
            await AddInitialItemsAsync(entries);
        }
        else
        {
            ReconcileItemsIncrementally(entries);
        }

        UpdatePageStateFromSnapshot(entries);
        UpdateShellNewMoveAvailability();
        RebuildWinXGroups();
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            ItemsView.Refresh();
        }

        RefreshListPlaceholderState();
    }

    private async Task AddInitialItemsAsync(IReadOnlyList<SpecialMenuEntry> entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            Items.Add(new SpecialMenuItemViewModel(
                entries[index],
                _iconPreviewService,
                _localization,
                SetEnabledAsync));

            if ((index + 1) % 32 == 0)
            {
                await YieldToUiAsync();
            }
        }
    }

    private static async Task YieldToUiAsync()
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            static () => { },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task SetBusyAsync(bool value)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            IsBusy = value;
            return;
        }

        await dispatcher.InvokeAsync(() => IsBusy = value);
    }

    private void ApplyWinXSnapshotIncrementally(IReadOnlyList<SpecialMenuEntry> entries)
    {
        ReconcileItemsIncrementally(entries);
        UpdatePageStateFromSnapshot(entries);
        ReconcileWinXGroupsIncrementally();
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            ItemsView.Refresh();
        }

        RefreshListPlaceholderState();
    }

    private void ReconcileItemsIncrementally(IReadOnlyList<SpecialMenuEntry> entries)
    {
        var existingById = new Dictionary<string, SpecialMenuItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            existingById[item.Id] = item;
        }

        var newIds = entries
            .Select(static entry => entry.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (!newIds.Contains(Items[index].Id))
            {
                Items.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < entries.Count; targetIndex++)
        {
            var entry = entries[targetIndex];
            if (existingById.TryGetValue(entry.Id, out var item))
            {
                item.Update(entry);
            }
            else
            {
                item = new SpecialMenuItemViewModel(entry, _iconPreviewService, _localization, SetEnabledAsync);
            }

            var currentIndex = Items.IndexOf(item);
            if (currentIndex < 0)
            {
                Items.Insert(targetIndex, item);
            }
            else if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }
    }

    private void Upsert(SpecialMenuEntry entry)
    {
        if (Kind == SpecialMenuKind.WinX)
        {
            ObserveFireAndForget(RefreshAsync(), "WinXUpsertRefreshAsync");
            return;
        }

        var current = Items.FirstOrDefault(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            Items.Add(new SpecialMenuItemViewModel(entry, _iconPreviewService, _localization, SetEnabledAsync));
        }
        else
        {
            current.Update(entry);
        }

        UpdateShellNewMoveAvailability();
        ItemsView.Refresh();
        RebuildWinXGroups();
        RefreshListPlaceholderState();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not SpecialMenuItemViewModel item)
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
               || Contains(item.Subtitle, search)
               || Contains(item.Detail, search);
    }

    private void OnNotificationReceived(object? sender, BackendNotification notification)
    {
        if (notification.SpecialKind != Kind || notification.SpecialItem is null)
        {
            return;
        }

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (Kind == SpecialMenuKind.WinX)
            {
                ObserveFireAndForget(RefreshAsync(), "WinXNotificationRefreshAsync");
                return;
            }

            Upsert(notification.SpecialItem);
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        SearchLabel = _localization.Translate("SearchLabel");
        AddText = _localization.Translate("Add");
        EditText = _localization.Translate("Edit");
        RefreshText = _localization.Translate("Refresh");
        RestoreDefaultsText = _localization.Translate("RestoreDefault");
        LockNewMenuText = _localization.Translate("LockNewMenu");
        LockNewMenuDescription = _localization.Translate("LockNewMenuDescription");
        WinXCompatibilityNoticeText = _localization.Translate("WinXCompatibilityNotice");
        WinXCompatibilityNoticeToolTip = _localization.Translate("WinXCompatibilityNoticeToolTip");
        DeleteText = _localization.Translate("Delete");
        UndoText = _localization.Translate("Undo");
        PermanentDeleteText = _localization.Translate("PermanentDelete");
        CancelText = _localization.Translate("DialogCancel");
        DropEffectLabel = _localization.Translate("DefaultDropEffect");
        DocumentIconProviderLabel = _localization.Translate("DocumentIconProvider");
        DocumentIconProviderDescription = _localization.Translate("DocumentIconProviderDescription");
        DocumentIconProviderChangedHint = _localization.Translate("DocumentIconProviderChangedHint");
        OnPropertyChanged(nameof(MicrosoftOfficeLabel));
        OnPropertyChanged(nameof(WpsOfficeLabel));
        foreach (var item in Items)
        {
            item.RefreshLocalization();
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(LoadingItemsText));
        OnPropertyChanged(nameof(EmptyItemsText));
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

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshListPlaceholderState();
    }

    private void OnWinXGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshListPlaceholderState();
    }

    private async Task SetShellNewOrderLockAsync(bool oldValue, bool newValue)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _backendClient.SetShellNewOrderLockAsync(newValue, Guid.NewGuid(), cts.Token);
            // An automatic refresh may already be reading the pre-operation ACL state.
            // Do not reuse that stale in-flight task as the post-operation refresh.
            await WaitForCurrentRefreshAsync();
            await RefreshAsync();

            _suppressShellNewLockSync = true;
            try
            {
                IsShellNewOrderLocked = newValue;
            }
            finally
            {
                _suppressShellNewLockSync = false;
            }
        }
        catch (Exception ex)
        {
            _suppressShellNewLockSync = true;
            try
            {
                IsShellNewOrderLocked = oldValue;
            }
            finally
            {
                _suppressShellNewLockSync = false;
            }

            StatusText = ex.Message;
        }
    }

    private async Task WaitForCurrentRefreshAsync()
    {
        Task? refreshTask;
        lock (_refreshSync)
        {
            refreshTask = _refreshTask is { IsCompleted: false } task ? task : null;
        }

        if (refreshTask is not null)
        {
            await refreshTask;
        }
    }

    private async Task SetDefaultDropEffectAsync(DefaultDropEffect oldValue, DefaultDropEffect newValue)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _backendClient.UpdateSpecialMenuItemAsync(
                new PipeRequest
                {
                    SpecialKind = Kind,
                    DefaultDropEffect = newValue,
                    ClientOperationId = Guid.NewGuid()
                },
                cts.Token);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _suppressDropEffectSync = true;
            try
            {
                SelectedDropEffect = oldValue;
            }
            finally
            {
                _suppressDropEffectSync = false;
            }

            await FrontendMessageBox.ShowErrorAsync(ex.Message, Title);
        }
    }

    private async Task RefreshOfficeCoexistenceStatusAsync(CancellationToken cancellationToken)
    {
        if (Kind != SpecialMenuKind.ShellNew)
        {
            ShowDocumentIconProvider = false;
            return;
        }

        try
        {
            var status = await _backendClient.GetOfficeSuiteCoexistenceStatusAsync(cancellationToken);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ShowDocumentIconProvider = status?.IsCoexistenceActive == true;
                if (status?.CurrentDocumentIconProvider is not { } provider)
                {
                    return;
                }

                _suppressDocumentIconProviderSync = true;
                try
                {
                    SelectedDocumentIconProvider = provider;
                    IsWpsDocumentIconProviderSelected = provider == DocumentIconProvider.WpsOffice;
                }
                finally
                {
                    _suppressDocumentIconProviderSync = false;
                }
            });
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                "SpecialMenuPageViewModel",
                $"RefreshOfficeCoexistenceStatusAsync failed. Kind={Kind}, Message={ex.Message}");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ShowDocumentIconProvider = false);
        }
    }

    private async Task SetDocumentIconProviderAsync(DocumentIconProvider oldValue, DocumentIconProvider newValue)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var status = await _backendClient.SetDocumentIconProviderAsync(newValue, cts.Token);
            _suppressDocumentIconProviderSync = true;
            try
            {
                SelectedDocumentIconProvider = status?.CurrentDocumentIconProvider ?? newValue;
                IsWpsDocumentIconProviderSelected = SelectedDocumentIconProvider == DocumentIconProvider.WpsOffice;
            }
            finally
            {
                _suppressDocumentIconProviderSync = false;
            }

            await RefreshAsync();
            _hasDocumentIconProviderChanged = true;
            _explorerRestartState.MarkIconCacheRefreshRequired();
            OnPropertyChanged(nameof(ShowDocumentIconProviderChangedHint));
        }
        catch (Exception ex)
        {
            _suppressDocumentIconProviderSync = true;
            try
            {
                SelectedDocumentIconProvider = oldValue;
                IsWpsDocumentIconProviderSelected = oldValue == DocumentIconProvider.WpsOffice;
            }
            finally
            {
                _suppressDocumentIconProviderSync = false;
            }

            await FrontendMessageBox.ShowErrorAsync(ex.Message, Title);
        }
    }

    private void UpdatePageStateFromSnapshot(IEnumerable<SpecialMenuEntry> snapshot)
    {
        if (Kind == SpecialMenuKind.ShellNew)
        {
            var lockedText = snapshot.Select(item => item.Metadata.GetValueOrDefault("OrderLocked")).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            _suppressShellNewLockSync = true;
            try
            {
                IsShellNewOrderLocked = bool.TryParse(lockedText, out var locked) && locked;
            }
            finally
            {
                _suppressShellNewLockSync = false;
            }
        }

        if (Kind == SpecialMenuKind.DragDrop)
        {
            var effectText = snapshot.FirstOrDefault(static item => item.Metadata.GetValueOrDefault("EntryType") == "DefaultDropEffect")?.Notes;
            _suppressDropEffectSync = true;
            try
            {
                SelectedDropEffect = Enum.TryParse<DefaultDropEffect>(effectText, out var effect) ? effect : DefaultDropEffect.Default;
            }
            finally
            {
                _suppressDropEffectSync = false;
            }
        }
    }

    private void RebuildWinXGroups()
    {
        if (Kind != SpecialMenuKind.WinX)
        {
            return;
        }

        ReconcileWinXGroupsIncrementally();
    }

    private void ReconcileWinXGroupsIncrementally()
    {
        foreach (var group in WinXGroups)
        {
            _winXExpandedStates[group.Group.KeyName] = group.IsExpanded;
        }

        var existingGroupsByKey = new Dictionary<string, WinXGroupNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in WinXGroups)
        {
            existingGroupsByKey.TryAdd(group.Group.KeyName, group);
        }
        var groupSpecs = BuildWinXGroupSpecs();
        var targetGroupKeys = groupSpecs
            .Select(static spec => spec.Group.KeyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = WinXGroups.Count - 1; index >= 0; index--)
        {
            if (!targetGroupKeys.Contains(WinXGroups[index].Group.KeyName))
            {
                WinXGroups.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < groupSpecs.Count; targetIndex++)
        {
            var spec = groupSpecs[targetIndex];
            if (!existingGroupsByKey.TryGetValue(spec.Group.KeyName, out var group))
            {
                group = CreateWinXGroupNode(spec.Group);
            }
            else
            {
                group.UpdateGroup(spec.Group);
            }

            if (_winXExpandedStates.TryGetValue(spec.Group.KeyName, out var expanded))
            {
                group.IsExpanded = expanded;
            }

            var currentIndex = WinXGroups.IndexOf(group);
            if (currentIndex < 0)
            {
                WinXGroups.Insert(targetIndex, group);
            }
            else if (currentIndex != targetIndex)
            {
                WinXGroups.Move(currentIndex, targetIndex);
            }

            ReconcileWinXGroupItems(group.Items, spec.Items);
        }

        UpdateWinXMoveAvailability();
        RefreshListPlaceholderState();
    }

    private void RefreshListPlaceholderState()
    {
        OnPropertyChanged(nameof(IsListLoading));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(ShowListPlaceholder));
    }

    private List<(SpecialMenuItemViewModel Group, List<SpecialMenuItemViewModel> Items)> BuildWinXGroupSpecs()
    {
        var specs = new List<(SpecialMenuItemViewModel Group, List<SpecialMenuItemViewModel> Items)>();
        SpecialMenuItemViewModel? currentGroup = null;
        foreach (var item in Items)
        {
            if (item.Entry.Metadata.GetValueOrDefault("EntryType") == "Group")
            {
                currentGroup = item;
                specs.Add((currentGroup, []));
                continue;
            }

            if (currentGroup is null || !string.Equals(currentGroup.KeyName, item.Entry.GroupName, StringComparison.OrdinalIgnoreCase))
            {
                var existingSpecIndex = FindWinXGroupSpecIndex(specs, item.Entry.GroupName);
                currentGroup = existingSpecIndex >= 0 ? specs[existingSpecIndex].Group : null;
                if (currentGroup is null)
                {
                    currentGroup = new SpecialMenuItemViewModel(
                        new SpecialMenuEntry
                        {
                            Id = $"{SpecialMenuKind.WinX}:Synthetic:{item.Entry.GroupName}",
                            Kind = SpecialMenuKind.WinX,
                            DisplayName = item.Entry.GroupName ?? string.Empty,
                            KeyName = item.Entry.GroupName ?? string.Empty,
                            IsEnabled = true,
                            CanEdit = false,
                            CanDelete = false,
                            Metadata = new Dictionary<string, string> { ["EntryType"] = "Group" }
                        },
                        _iconPreviewService,
                        _localization,
                        SetEnabledAsync);
                    specs.Add((currentGroup, []));
                }
            }

            var specIndex = FindWinXGroupSpecIndex(specs, currentGroup.KeyName);
            if (specIndex >= 0)
            {
                specs[specIndex].Items.Add(item);
            }
        }

        return specs;
    }

    private static int FindWinXGroupSpecIndex(
        IReadOnlyList<(SpecialMenuItemViewModel Group, List<SpecialMenuItemViewModel> Items)> specs,
        string? keyName)
    {
        for (var index = 0; index < specs.Count; index++)
        {
            if (string.Equals(specs[index].Group.KeyName, keyName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static void ReconcileWinXGroupItems(
        ObservableCollection<SpecialMenuItemViewModel> currentItems,
        IReadOnlyList<SpecialMenuItemViewModel> targetItems)
    {
        var targetIds = targetItems
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = currentItems.Count - 1; index >= 0; index--)
        {
            if (!targetIds.Contains(currentItems[index].Id))
            {
                currentItems.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < targetItems.Count; targetIndex++)
        {
            var item = targetItems[targetIndex];
            var currentIndex = currentItems.IndexOf(item);
            if (currentIndex < 0)
            {
                currentItems.Insert(targetIndex, item);
            }
            else if (currentIndex != targetIndex)
            {
                currentItems.Move(currentIndex, targetIndex);
            }
        }
    }

    private void UpdateShellNewMoveAvailability()
    {
        if (Kind != SpecialMenuKind.ShellNew)
        {
            return;
        }

        var movableItems = Items
            .Where(static item => item.Entry.CanMove)
            .ToArray();

        for (var index = 0; index < movableItems.Length; index++)
        {
            movableItems[index].SetMoveAvailability(
                IsShellNewOrderLocked && index > 0,
                IsShellNewOrderLocked && index < movableItems.Length - 1);
        }

        foreach (var item in Items.Where(static item => !item.Entry.CanMove))
        {
            item.SetMoveAvailability(false, false);
        }
    }

    private static bool IsExpectedShellNewMoveFailure(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("ShellNew ordering", StringComparison.OrdinalIgnoreCase)
            || message.Contains(@"Discardable\PostSetup\ShellNew", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Access to the registry key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("registry key is denied", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateWinXMoveAvailability()
    {
        for (var groupIndex = 0; groupIndex < WinXGroups.Count; groupIndex++)
        {
            var group = WinXGroups[groupIndex];
            var entries = group.Items
                .Where(static item => item.Entry.Metadata.GetValueOrDefault("EntryType") == "Entry")
                .ToArray();

            for (var itemIndex = 0; itemIndex < entries.Length; itemIndex++)
            {
                var hasItemAboveInGroup = itemIndex > 0;
                var hasItemBelowInGroup = itemIndex < entries.Length - 1;
                var hasGroupAbove = HasAdjacentWinXGroup(groupIndex, moveUp: true);
                var hasGroupBelow = HasAdjacentWinXGroup(groupIndex, moveUp: false);

                entries[itemIndex].SetMoveAvailability(
                    hasItemAboveInGroup || hasGroupAbove,
                    hasItemBelowInGroup || hasGroupBelow);
            }
        }
    }

    private bool HasAdjacentWinXGroup(int groupIndex, bool moveUp)
    {
        var nextIndex = moveUp ? groupIndex - 1 : groupIndex + 1;
        return nextIndex >= 0 && nextIndex < WinXGroups.Count;
    }

    private WinXGroupNodeViewModel CreateWinXGroupNode(SpecialMenuItemViewModel group)
    {
        var node = new WinXGroupNodeViewModel(group)
        {
            IsExpanded = !_winXExpandedStates.TryGetValue(group.KeyName, out var expanded) || expanded
        };
        return node;
    }

    private static string[]? SplitParts(string? value, int count, bool allowEmptyMiddleParts = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('|');
        if (parts.Length < count || (!allowEmptyMiddleParts && parts.Take(count - 1).Any(string.IsNullOrWhiteSpace)))
        {
            return null;
        }

        return parts.Concat(Enumerable.Repeat(string.Empty, count)).Take(count).Select(static part => part.Trim()).ToArray();
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _backendClient.NotificationReceived -= OnNotificationReceived;
        _localization.LanguageChanged -= OnLanguageChanged;
        _placeholderDebug.PropertyChanged -= OnPlaceholderDebugPropertyChanged;
        Items.CollectionChanged -= OnItemsCollectionChanged;
        WinXGroups.CollectionChanged -= OnWinXGroupsCollectionChanged;
    }

    private void StartAutoRefreshLoop()
    {
        if (Kind == SpecialMenuKind.CommandStore)
        {
            return;
        }

        if (_autoRefreshTask is { IsCompleted: false })
        {
            return;
        }

        var token = _disposeCts.Token;
        _autoRefreshTask = Task.Run(() => AutoRefreshLoopAsync(token), token);
    }

    private async Task AutoRefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                await StartRefreshAsync(markLoaded: false, showBusy: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                FrontendDebugLog.Warning(
                    "SpecialMenuPageViewModel",
                    $"AutoRefreshLoopAsync failed. Kind={Kind}, Message={ex.Message}");
            }
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
                "SpecialMenuPageViewModel",
                $"{operationName} canceled. Kind={Kind}, Message={ex.Message}");
        }
        catch (ObjectDisposedException ex)
        {
            FrontendDebugLog.Info(
                "SpecialMenuPageViewModel",
                $"{operationName} skipped during disposal. Kind={Kind}, Message={ex.Message}");
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                "SpecialMenuPageViewModel",
                $"{operationName} failed. Kind={Kind}, Message={ex.Message}");
        }
    }
}

public sealed class ShellNewPageViewModel : SpecialMenuPageViewModel
{
    public ShellNewPageViewModel(IBackendClient backendClient, IconPreviewService iconPreviewService, LocalizationService localization, ExplorerRestartStateService explorerRestartState, ListPlaceholderDebugStateService placeholderDebug)
        : base(SpecialMenuKind.ShellNew, "ShellNewPageTitle", "ShellNewPageDescription", backendClient, iconPreviewService, localization, explorerRestartState, placeholderDebug)
    {
    }
}

public sealed class SendToPageViewModel : SpecialMenuPageViewModel
{
    public SendToPageViewModel(IBackendClient backendClient, IconPreviewService iconPreviewService, LocalizationService localization, ExplorerRestartStateService explorerRestartState, ListPlaceholderDebugStateService placeholderDebug)
        : base(SpecialMenuKind.SendTo, "SendToPageTitle", "SendToPageDescription", backendClient, iconPreviewService, localization, explorerRestartState, placeholderDebug)
    {
    }
}

public sealed class WinXPageViewModel : SpecialMenuPageViewModel
{
    public WinXPageViewModel(IBackendClient backendClient, IconPreviewService iconPreviewService, LocalizationService localization, ExplorerRestartStateService explorerRestartState, ListPlaceholderDebugStateService placeholderDebug)
        : base(SpecialMenuKind.WinX, "WinXPageTitle", "WinXPageDescription", backendClient, iconPreviewService, localization, explorerRestartState, placeholderDebug)
    {
    }
}

public sealed class OpenWithPageViewModel : SpecialMenuPageViewModel
{
    public OpenWithPageViewModel(IBackendClient backendClient, IconPreviewService iconPreviewService, LocalizationService localization, ExplorerRestartStateService explorerRestartState, ListPlaceholderDebugStateService placeholderDebug)
        : base(SpecialMenuKind.OpenWith, "OpenWithPageTitle", "OpenWithPageDescription", backendClient, iconPreviewService, localization, explorerRestartState, placeholderDebug)
    {
    }
}

public sealed partial class WinXGroupNodeViewModel : ObservableObject
{
    public WinXGroupNodeViewModel(SpecialMenuItemViewModel group)
    {
        Group = group;
    }

    public SpecialMenuItemViewModel Group { get; private set; }

    public ObservableCollection<SpecialMenuItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public void UpdateGroup(SpecialMenuItemViewModel group)
    {
        if (ReferenceEquals(Group, group))
        {
            return;
        }

        if (string.Equals(Group.Id, group.Id, StringComparison.OrdinalIgnoreCase))
        {
            Group.Update(group.Entry);
            return;
        }

        Group = group;
        OnPropertyChanged(nameof(Group));
    }
}
