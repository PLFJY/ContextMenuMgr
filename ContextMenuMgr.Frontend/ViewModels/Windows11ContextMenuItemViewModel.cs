using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>One application row; the backend commits its menus as one operation.</summary>
public partial class Windows11ContextMenuItemViewModel : ObservableObject, IDisposable
{
    private static readonly ConcurrentDictionary<string, ImageSource?> LogoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Windows11ContextMenuService _service;
    private readonly LocalizationService _localization;
    private readonly FrontendSettingsService? _settingsService;
    private readonly EventHandler _languageChangedHandler;
    private readonly Windows11ContextMenuItemDefinition _primaryDefinition;
    private readonly Task<ImageSource?> _logoTask;
    private bool _suppressSync;

    public Windows11ContextMenuItemViewModel(
        IReadOnlyList<Windows11ContextMenuItemDefinition> definitions,
        Windows11ContextMenuService service, LocalizationService localization,
        FrontendSettingsService? settingsService = null)
    {
        Definitions = definitions;
        _primaryDefinition = definitions[0];
        _service = service;
        _localization = localization;
        _settingsService = settingsService;
        _logoTask = LoadLogoAsync(_primaryDefinition.Package);
        _ = _logoTask.ContinueWith(_ =>
        {
            OnPropertyChanged(nameof(LogoSource));
            OnPropertyChanged(nameof(HasLogo));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        RefreshState(definitions.All(definition => definition.IsEnabled));
        IsBusy = service.IsOperationPending(Id);
        IsStateUncertain = service.IsStateUncertain(Id);
        HasOperationError = service.HasOperationError(Id);
        UserNote = _settingsService?.GetContextMenuItemNote(Id) ?? string.Empty;
        // v1.7.10 notes used CLSID/category without package family.
        if (string.IsNullOrWhiteSpace(UserNote) && Id.Split('|') is { Length: 4 } parts)
            UserNote = _settingsService?.GetContextMenuItemNote(string.Join("|", parts.Take(3))) ?? string.Empty;
        _languageChangedHandler = (_, _) =>
        {
            OnPropertyChanged(nameof(ToggleLabel));
            OnPropertyChanged(nameof(PartialStateText));
            OnPropertyChanged(nameof(EnableAllText));
            OnPropertyChanged(nameof(DisableAllText));
            OnPropertyChanged(nameof(ContextTypesText));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PendingApprovalText));
            OnPropertyChanged(nameof(EditUserNoteLabel));
            OnPropertyChanged(nameof(RetryText));
            OnPropertyChanged(nameof(PublisherText));
            OnPropertyChanged(nameof(PackageIdentityText));
            OnPropertyChanged(nameof(FileLocationText));
        };
        _localization.LanguageChanged += _languageChangedHandler;
    }

    public IReadOnlyList<Windows11ContextMenuItemDefinition> Definitions { get; }
    public string Id => _primaryDefinition.Id;
    public string DisplayName => IsSystemCommand ? _primaryDefinition.DisplayName : _primaryDefinition.Package.DisplayName;
    public string PublisherName => _primaryDefinition.Package.PublisherDisplayName;
    public string PackageFamilyName => _primaryDefinition.Package.FamilyName;
    public string ComServerPath => _primaryDefinition.ComServer.Path ?? string.Empty;
    public string PublisherText => string.IsNullOrWhiteSpace(PublisherName) ? string.Empty : _localization.Format("Windows11Publisher", PublisherName);
    public string PackageIdentityText => IsSystemCommand ? string.Empty : _localization.Format("Windows11PackageIdentity", PackageFamilyName);
    public string FileLocationText => string.IsNullOrWhiteSpace(ComServerPath) ? string.Empty : _localization.Format("Windows11FileLocation", ComServerPath);
    public bool HasPublisher => !string.IsNullOrWhiteSpace(PublisherName);
    public bool HasFileLocation => !string.IsNullOrWhiteSpace(ComServerPath);
    public bool IsSystemCommand => _primaryDefinition.SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore;
    public bool IsProtected => Definitions.Any(definition => definition.IsProtected);
    public bool IsMachineBlocked => Definitions.Any(definition => definition.IsMachineBlocked);
    public bool IsPartiallyEnabled => Definitions.Any(definition => definition.IsEnabled) && !Definitions.All(definition => definition.IsEnabled);
    public string ContextTypesText => string.Join(" · ", Definitions.SelectMany(definition => definition.ContextTypes)
        .Select(LocalizeContextType).Distinct());
    public ImageSource? LogoSource => _logoTask.IsCompletedSuccessfully ? _logoTask.Result : null;
    public bool HasLogo => LogoSource is not null;
    public string ToggleLabel => _localization.Translate(IsEnabled ? "ToggleOn" : "ToggleOff");
    public bool ShowToggle => !IsPartiallyEnabled;
    public bool ShowMixedActions => IsPartiallyEnabled && !IsBusy;
    public bool ShowMixedBusy => IsPartiallyEnabled && IsBusy;
    public string EnableAllText => _localization.Translate("Windows11EnableAll");
    public string DisableAllText => _localization.Translate("Windows11DisableAll");
    public string PartialStateText => _localization.Translate("Windows11PartiallyEnabled");
    public string PendingApprovalText => _localization.Translate("PendingApprovalBadge");
    public string EditUserNoteLabel => _localization.Translate("DetailsEditUserNote");
    public string RetryText => _localization.Translate("Refresh");
    public bool CanEditUserNote => _settingsService is not null;
    public bool HasUserNote => !string.IsNullOrWhiteSpace(UserNote);
    public bool CanToggle => !IsBusy && !IsProtected && !IsStateUncertain;
    public bool CanRetry => !IsBusy && (HasOperationError || IsStateUncertain);
    public string StatusText => _localization.Translate(IsStateUncertain ? "Windows11StateUncertain"
        : HasOperationError ? _service.RequiresServiceUpdate ? "Windows11ServiceUpdateRequired" : "Windows11ChangeFailed" : IsProtected ? "Windows11MenuUnavailable" : "");
    public bool HasStatus => IsStateUncertain || HasOperationError || IsProtected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserNote))]
    public partial string UserNote { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(ShowMixedActions))]
    [NotifyPropertyChangedFor(nameof(ShowMixedBusy))]
    [NotifyCanExecuteChangedFor(nameof(EnableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsPendingApproval { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableAllCommand))]
    public partial bool IsStateUncertain { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool HasOperationError { get; set; }

    public void RefreshState(bool isEnabled)
    {
        _suppressSync = true;
        try { IsEnabled = isEnabled; }
        finally { _suppressSync = false; }
    }

    public void RefreshPendingApproval(bool isPendingApproval) => IsPendingApproval = isPendingApproval;

    partial void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        OnPropertyChanged(nameof(ToggleLabel));
        if (_suppressSync || oldValue == newValue) return;
        if (!CanToggle) { RefreshState(oldValue); return; }
        IsBusy = true;
        _ = SyncAsync(oldValue, newValue);
    }

    private async Task SyncAsync(bool oldValue, bool newValue)
    {
        try
        {
            var otherApps = Definitions.SelectMany(definition => definition.Entry?.Windows11ClsidImpact?.References ?? [])
                .Where(reference => reference.Kind == "Package" && !string.Equals(reference.PackageFamilyName, PackageFamilyName, StringComparison.OrdinalIgnoreCase))
                .Select(reference => reference.PackageDisplayName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
            var notices = new List<string>();
            if (newValue && IsMachineBlocked) notices.Add(_localization.Translate("Windows11RestoreAllUsersConfirm"));
            if (otherApps.Length > 0) notices.Add(_localization.Format("Windows11SharedAppConfirm", string.Join("、", otherApps)));
            else if (Definitions.Any(definition => definition.Entry?.Windows11ClsidImpact?.References.Any(reference => reference.Kind == "Classic") == true))
                notices.Add(_localization.Translate("Windows11MoreOptionsConfirm"));
            var confirmation = notices.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, notices);
            if (confirmation is not null && !await FrontendMessageBox.ShowConfirmAsync(confirmation, DisplayName))
            { RefreshState(oldValue); return; }
            await ToggleBusyPresentation.WaitForFirstFrameAsync();
            await _service.SetEnabledAsync(Id, DisplayName, newValue, CancellationToken.None, applicationScope: !IsSystemCommand);
            RefreshState(newValue);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(nameof(Windows11ContextMenuItemViewModel), ex.ToString());
            var actual = _service.CurrentItems.Where(item => Definitions.Any(definition => definition.Id == item.Id)).ToArray();
            RefreshState(actual.Length > 0 ? actual.All(item => item.IsEnabled) : oldValue);
            IsStateUncertain = _service.IsStateUncertain(Id);
            HasOperationError = true;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private Task EnableAllAsync() => SetAllAsync(true);

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private Task DisableAllAsync() => SetAllAsync(false);

    private Task SetAllAsync(bool enable)
    {
        if (!CanToggle) return Task.CompletedTask;
        IsBusy = true;
        return SyncAsync(IsEnabled, enable);
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        IsBusy = true;
        try
        {
            await _service.RefreshAsync(CancellationToken.None);
            IsStateUncertain = false;
            HasOperationError = false;
            var actual = _service.CurrentItems.Where(item => Definitions.Any(definition => definition.Id == item.Id)).ToArray();
            if (actual.Length > 0) RefreshState(actual.All(item => item.IsEnabled));
        }
        catch (Exception ex) { FrontendDebugLog.Warning(nameof(Windows11ContextMenuItemViewModel), ex.ToString()); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditUserNote))]
    private async Task EditUserNoteAsync()
    {
        if (_settingsService is null) return;
        var note = await TextInputDialog.ShowAsync(EditUserNoteLabel, _localization.Translate("UserNoteLabel"),
            UserNote, width: 620, height: 300, multiline: true);
        if (note is null) return;
        _settingsService.UpdateContextMenuItemNote(Id, note);
        UserNote = note.Trim();
    }

    private string LocalizeContextType(string type)
    {
        if (type.StartsWith("File:", StringComparison.OrdinalIgnoreCase)) type = type[5..].Trim();
        return _localization.Translate(type switch
        {
            "Directory" or "Folder" => "FolderCategoryName",
            @"Directory\Background" or @"LibraryFolder\Background" => "BackgroundCategoryName",
            "Drive" => "DriveCategoryName",
            "DesktopBackground" => "DesktopCategoryName",
            "LibraryFolder" or "UserLibraryFolder" => "LibraryCategoryName",
            _ => "FileCategoryName"
        });
    }

    private static Task<ImageSource?> LoadLogoAsync(Windows11PackageInfo package)
    {
        var path = package.LogoPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Task.FromResult<ImageSource?>(null);
        if (LogoCache.TryGetValue(path, out var cached)) return Task.FromResult(cached);
        return Task.Run(() => Windows11ContextMenuService.LoadLogo(package, CancellationToken.None))
            .ContinueWith(task => LogoCache[path] = task.Result, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose() => _localization.LanguageChanged -= _languageChangedHandler;
}
