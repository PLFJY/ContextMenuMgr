using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using System.Windows.Media;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class ContextMenuItemViewModel : ObservableObject
{
    private readonly IconPreviewService _iconPreviewService;
    private readonly LocalizationService _localization;
    private readonly ContextMenuItemActionsService _actionsService;
    private readonly Func<ContextMenuItemViewModel, bool, Task<bool>>? _setEnabledAsync;
    private readonly Func<ContextMenuItemViewModel, ContextMenuShellAttribute, bool, Task<bool>>? _setShellAttributeAsync;
    private readonly Func<ContextMenuItemViewModel, string, Task<bool>>? _setDisplayTextAsync;
    private bool _suppressEnabledSync;
    private bool _suppressAttributeSync;
    private string _detectedChangeSignature = string.Empty;
    private string _consistencyIssueSignature = string.Empty;

    public ContextMenuItemViewModel(
        ContextMenuEntry entry,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        ContextMenuItemActionsService actionsService,
        Func<ContextMenuItemViewModel, bool, Task<bool>>? setEnabledAsync = null,
        Func<ContextMenuItemViewModel, ContextMenuShellAttribute, bool, Task<bool>>? setShellAttributeAsync = null,
        Func<ContextMenuItemViewModel, string, Task<bool>>? setDisplayTextAsync = null)
    {
        _iconPreviewService = iconPreviewService;
        _localization = localization;
        _actionsService = actionsService;
        _setEnabledAsync = setEnabledAsync;
        _setShellAttributeAsync = setShellAttributeAsync;
        _setDisplayTextAsync = setDisplayTextAsync;
        Entry = entry;
        ApplyEntry(entry);
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ContextMenuEntry Entry { get; private set; }

    public string Id => Entry.Id;

    public ContextMenuCategory Category => Entry.Category;

    public string DisplayName => Entry.DisplayName;

    public string KeyName => Entry.KeyName;

    public string EditableText => Entry.EditableText ?? DisplayName;

    public string RegistryPath => Entry.RegistryPath;

    public string Notes => Entry.Notes ?? string.Empty;

    public bool ShowNotes => !string.IsNullOrWhiteSpace(Entry.Notes);

    public ImageSource? IconSource => _iconPreviewService.GetIcon(Entry.IconPath, Entry.IconIndex, Entry.FilePath);

    public bool HasIcon => IconSource is not null;

    public bool ShowKeyName => !string.Equals(DisplayName, KeyName, StringComparison.OrdinalIgnoreCase);

    public string CategoryName => ContextMenuCategoryText.GetLocalizedName(Category, _localization);

    public bool HasClsidLocation => Guid.TryParse(Entry.HandlerClsid, out _);

    public bool HasFileLocation => !string.IsNullOrWhiteSpace(Entry.FilePath);

    public bool HasRegistryLocation => !string.IsNullOrWhiteSpace(Entry.RegistryPath);

    public bool HasCommandText => !string.IsNullOrWhiteSpace(Entry.CommandText);

    public bool HasOtherAttributesSection => Entry.EntryKind == ContextMenuEntryKind.ShellVerb && IsPresentInRegistry && !IsDeleted;

    public bool CanEditShellAttributes => HasOtherAttributesSection && !IsAttributesBusy;

    public bool HasActionFlyout => HasOtherAttributesSection || HasDetailsActions;

    public bool CanEditText => Entry.EntryKind == ContextMenuEntryKind.ShellVerb
        && IsPresentInRegistry
        && !IsDeleted
        && !string.IsNullOrWhiteSpace(Entry.EditableText)
        && !string.Equals(KeyName, "open", StringComparison.OrdinalIgnoreCase);

    public bool HasDetailsActions => CanEditText || HasCommandText || HasFileLocation || HasRegistryLocation || HasClsidLocation || CanSearchOnline;

    public bool CanSearchOnline => !string.IsNullOrWhiteSpace(DisplayName) || !string.IsNullOrWhiteSpace(KeyName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    [NotifyPropertyChangedFor(nameof(SortDeletedWeight))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionLabel))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(ShowToggle))]
    [NotifyPropertyChangedFor(nameof(CanPrimaryAction))]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    [NotifyPropertyChangedFor(nameof(SortDeletedWeight))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    [NotifyPropertyChangedFor(nameof(HasOtherAttributesSection))]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    [NotifyPropertyChangedFor(nameof(HasActionFlyout))]
    public partial bool IsDeleted { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    [NotifyPropertyChangedFor(nameof(CanReviewApproval))]
    public partial bool IsPendingApproval { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPermanentlyDelete))]
    public partial bool HasBackup { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(ConsistencyText))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial bool HasConsistencyIssue { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConsistencyIssue))]
    [NotifyPropertyChangedFor(nameof(ConsistencyText))]
    [NotifyPropertyChangedFor(nameof(CanDismissConsistencyIssue))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial bool IsConsistencyIssueDismissed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConsistencyText))]
    public partial string? ConsistencyIssue { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletedAtText))]
    public partial DateTimeOffset? DeletedAtUtc { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(ShowToggle))]
    [NotifyPropertyChangedFor(nameof(CanPrimaryAction))]
    [NotifyPropertyChangedFor(nameof(HasOtherAttributesSection))]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    [NotifyPropertyChangedFor(nameof(HasActionFlyout))]
    [NotifyPropertyChangedFor(nameof(CanReviewApproval))]
    public partial bool IsPresentInRegistry { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsToggleBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool IsAttributesBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectedChange))]
    [NotifyPropertyChangedFor(nameof(DetectedChangeBadgeText))]
    [NotifyPropertyChangedFor(nameof(DetectedChangeText))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial ContextMenuChangeKind DetectedChangeKind { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectedChange))]
    [NotifyPropertyChangedFor(nameof(CanDismissDetectedChange))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial bool IsDetectedChangeDismissed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool OnlyWithShift { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool OnlyInExplorer { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool NoWorkingDirectory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool NeverDefault { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool ShowAsDisabledIfHidden { get; set; }

    [ObservableProperty]
    public partial bool IsApprovalRemoveFlyoutOpen { get; set; }

    [ObservableProperty]
    public partial bool IsPermanentDeleteFlyoutOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectedChangeText))]
    public partial string? DetectedChangeDetails { get; private set; }

    public string StateLabel => IsDeleted
        ? _localization.Translate("DeletedState")
        : _localization.Translate(IsEnabled ? "Enabled" : "Disabled");

    public string ToggleLabel => _localization.Translate(IsEnabled ? "ToggleOn" : "ToggleOff");

    public string PendingApprovalBadgeText => _localization.Translate("PendingApprovalBadge");

    public bool CanReviewApproval => IsPendingApproval && IsPresentInRegistry && !IsDeleted;

    public bool CanToggle => !IsDeleted && IsPresentInRegistry && !IsToggleBusy;

    public bool ShowToggle => !IsDeleted && IsPresentInRegistry;

    public bool CanPrimaryAction => IsDeleted || IsPresentInRegistry;

    public bool CanPermanentlyDelete => IsDeleted && HasBackup;

    public string PrimaryActionLabel => IsDeleted
        ? _localization.Translate("UndoDelete")
        : _localization.Translate("Delete");

    public string ConsistencyText => string.IsNullOrWhiteSpace(ConsistencyIssue)
        ? string.Empty
        : ConsistencyIssue;

    public bool CanDismissConsistencyIssue => HasConsistencyIssue;

    public bool HasDetectedChange => DetectedChangeKind != ContextMenuChangeKind.None;

    public bool CanDismissDetectedChange => HasDetectedChange;

    public string DetectedChangeBadgeText => _localization.Translate(DetectedChangeKind switch
    {
        ContextMenuChangeKind.Added => "ChangeKindAdded",
        ContextMenuChangeKind.Removed => "ChangeKindRemoved",
        ContextMenuChangeKind.Modified => "ChangeKindModified",
        ContextMenuChangeKind.Reappeared => "ChangeKindReappeared",
        _ => "ChangeKindModified"
    });

    public string DetectedChangeText
    {
        get
        {
            if (!HasDetectedChange)
            {
                return string.Empty;
            }

            var summary = _localization.Format(
                "StartupChangeSummaryFormat",
                CategoryName,
                DetectedChangeBadgeText);

            return string.IsNullOrWhiteSpace(DetectedChangeDetails)
                ? summary
                : $"{summary} {DetectedChangeDetails}";
        }
    }

    public string DeletedAtText => DeletedAtUtc is null
        ? string.Empty
        : _localization.Format("DeletedAtFormat", DeletedAtUtc.Value.LocalDateTime);

    public bool NeedsAttention => IsPendingApproval || HasConsistencyIssue || HasDetectedChange;

    public double CardOpacity => IsDeleted ? 0.56 : 1.0;

    public int SortDeletedWeight => IsDeleted ? 1 : 0;

    public int SortAttentionWeight => HasDetectedChange || IsPendingApproval || HasConsistencyIssue
        ? 0
        : (IsDeleted ? 2 : 1);

    public string MoreActionsText => _localization.Translate("MoreActions");

    public string DismissDetectedChangeText => _localization.Translate("DismissDetectedChange");

    public string DismissConsistencyIssueText => _localization.Translate("DismissDetectedChange");

    public string OtherAttributesTitle => _localization.Translate("OtherAttributesTitle");

    public string DetailsTitle => _localization.Translate("DetailsTitle");

    public string OnlyWithShiftLabel => _localization.Translate("OnlyWithShiftLabel");

    public string OnlyInExplorerLabel => _localization.Translate("OnlyInExplorerLabel");

    public string NoWorkingDirectoryLabel => _localization.Translate("NoWorkingDirectoryLabel");

    public string NeverDefaultLabel => _localization.Translate("NeverDefaultLabel");

    public string ShowAsDisabledIfHiddenLabel => _localization.Translate("ShowAsDisabledIfHiddenLabel");

    public string DetailsSearchOnline => _localization.Translate("DetailsSearchOnline");

    public string DetailsChangeTextLabel => _localization.Translate("DetailsChangeText");

    public string DetailsCommandTextLabel => _localization.Translate("DetailsCommandText");

    public string DetailsFilePropertiesLabel => _localization.Translate("DetailsFileProperties");

    public string DetailsFileLocationLabel => _localization.Translate("DetailsFileLocation");

    public string DetailsRegistryLocationLabel => _localization.Translate("DetailsRegistryLocation");

    public string DetailsExportRegistryLabel => _localization.Translate("DetailsExportRegistry");

    public string DetailsClsidLocationLabel => _localization.Translate("DetailsClsidLocation");

    public string ApprovalRemoveConfirmationText => _localization.Format("ApprovalRemovePrompt", DisplayName);

    public string PermanentDeleteConfirmationText => _localization.Format("PermanentDeletePrompt", DisplayName);

    partial void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        if (_suppressEnabledSync || oldValue == newValue || _setEnabledAsync is null)
        {
            return;
        }

        if (!IsPresentInRegistry || IsDeleted)
        {
            RevertEnabled(oldValue);
            return;
        }

        IsToggleBusy = true;
        _ = SyncEnabledChangeAsync(oldValue, newValue);
    }

    partial void OnOnlyWithShiftChanged(bool oldValue, bool newValue) =>
        SyncShellAttribute(oldValue, newValue, ContextMenuShellAttribute.OnlyWithShift);

    partial void OnOnlyInExplorerChanged(bool oldValue, bool newValue) =>
        SyncShellAttribute(oldValue, newValue, ContextMenuShellAttribute.OnlyInExplorer);

    partial void OnNoWorkingDirectoryChanged(bool oldValue, bool newValue) =>
        SyncShellAttribute(oldValue, newValue, ContextMenuShellAttribute.NoWorkingDirectory);

    partial void OnNeverDefaultChanged(bool oldValue, bool newValue) =>
        SyncShellAttribute(oldValue, newValue, ContextMenuShellAttribute.NeverDefault);

    partial void OnShowAsDisabledIfHiddenChanged(bool oldValue, bool newValue) =>
        SyncShellAttribute(oldValue, newValue, ContextMenuShellAttribute.ShowAsDisabledIfHidden);

    public void Update(ContextMenuEntry entry)
    {
        Entry = entry;
        ApplyEntry(entry);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(KeyName));
        OnPropertyChanged(nameof(EditableText));
        OnPropertyChanged(nameof(RegistryPath));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(ShowNotes));
        OnPropertyChanged(nameof(IconSource));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(ShowKeyName));
        OnPropertyChanged(nameof(HasClsidLocation));
        OnPropertyChanged(nameof(HasFileLocation));
        OnPropertyChanged(nameof(HasRegistryLocation));
        OnPropertyChanged(nameof(HasCommandText));
        OnPropertyChanged(nameof(CanEditText));
        OnPropertyChanged(nameof(HasOtherAttributesSection));
        OnPropertyChanged(nameof(CanEditShellAttributes));
        OnPropertyChanged(nameof(HasDetailsActions));
        OnPropertyChanged(nameof(HasActionFlyout));
        OnPropertyChanged(nameof(CanSearchOnline));
    }

    private void ApplyEntry(ContextMenuEntry entry)
    {
        _suppressEnabledSync = true;
        _suppressAttributeSync = true;
        try
        {
            IsEnabled = entry.IsEnabled;
            IsDeleted = entry.IsDeleted;
            IsPendingApproval = entry.IsPendingApproval;
            HasBackup = entry.HasBackup;
            var newConsistencyIssueSignature = $"{entry.HasConsistencyIssue}|{entry.ConsistencyIssue}";
            if (!string.Equals(_consistencyIssueSignature, newConsistencyIssueSignature, StringComparison.Ordinal))
            {
                IsConsistencyIssueDismissed = false;
            }

            _consistencyIssueSignature = newConsistencyIssueSignature;

            if (!entry.HasConsistencyIssue)
            {
                IsConsistencyIssueDismissed = false;
            }

            HasConsistencyIssue = !IsConsistencyIssueDismissed && entry.HasConsistencyIssue;
            ConsistencyIssue = IsConsistencyIssueDismissed ? null : entry.ConsistencyIssue;
            DeletedAtUtc = entry.DeletedAtUtc;
            IsPresentInRegistry = entry.IsPresentInRegistry;
            var newDetectedChangeSignature = $"{entry.DetectedChangeKind}|{entry.DetectedChangeDetails}";
            if (!string.Equals(_detectedChangeSignature, newDetectedChangeSignature, StringComparison.Ordinal))
            {
                IsDetectedChangeDismissed = false;
            }

            _detectedChangeSignature = newDetectedChangeSignature;

            if (entry.DetectedChangeKind == ContextMenuChangeKind.None)
            {
                IsDetectedChangeDismissed = false;
            }

            DetectedChangeKind = IsDetectedChangeDismissed ? ContextMenuChangeKind.None : entry.DetectedChangeKind;
            DetectedChangeDetails = IsDetectedChangeDismissed ? null : entry.DetectedChangeDetails;
            OnlyWithShift = entry.OnlyWithShift;
            OnlyInExplorer = entry.OnlyInExplorer;
            NoWorkingDirectory = entry.NoWorkingDirectory;
            NeverDefault = entry.NeverDefault;
            ShowAsDisabledIfHidden = entry.ShowAsDisabledIfHidden;
        }
        finally
        {
            _suppressEnabledSync = false;
            _suppressAttributeSync = false;
        }
    }

    private async Task SyncEnabledChangeAsync(bool oldValue, bool newValue)
    {
        if (_setEnabledAsync is null)
        {
            return;
        }

        try
        {
            var success = await _setEnabledAsync(this, newValue);
            if (!success)
            {
                RevertEnabled(oldValue);
            }
        }
        catch
        {
            RevertEnabled(oldValue);
        }
        finally
        {
            IsToggleBusy = false;
        }
    }

    private void RevertEnabled(bool value)
    {
        _suppressEnabledSync = true;
        try
        {
            IsEnabled = value;
        }
        finally
        {
            _suppressEnabledSync = false;
        }
    }

    private void SyncShellAttribute(bool oldValue, bool newValue, ContextMenuShellAttribute attribute)
    {
        if (_suppressAttributeSync || oldValue == newValue || _setShellAttributeAsync is null)
        {
            return;
        }

        if (!CanEditShellAttributes)
        {
            RevertShellAttribute(attribute, oldValue);
            return;
        }

        IsAttributesBusy = true;
        _ = SyncShellAttributeAsync(attribute, oldValue, newValue);
    }

    private async Task SyncShellAttributeAsync(ContextMenuShellAttribute attribute, bool oldValue, bool newValue)
    {
        if (_setShellAttributeAsync is null)
        {
            return;
        }

        try
        {
            var success = await _setShellAttributeAsync(this, attribute, newValue);
            if (!success)
            {
                RevertShellAttribute(attribute, oldValue);
            }
        }
        catch
        {
            RevertShellAttribute(attribute, oldValue);
        }
        finally
        {
            IsAttributesBusy = false;
        }
    }

    private void RevertShellAttribute(ContextMenuShellAttribute attribute, bool value)
    {
        _suppressAttributeSync = true;
        try
        {
            switch (attribute)
            {
                case ContextMenuShellAttribute.OnlyWithShift:
                    OnlyWithShift = value;
                    break;
                case ContextMenuShellAttribute.OnlyInExplorer:
                    OnlyInExplorer = value;
                    break;
                case ContextMenuShellAttribute.NoWorkingDirectory:
                    NoWorkingDirectory = value;
                    break;
                case ContextMenuShellAttribute.NeverDefault:
                    NeverDefault = value;
                    break;
                case ContextMenuShellAttribute.ShowAsDisabledIfHidden:
                    ShowAsDisabledIfHidden = value;
                    break;
            }
        }
        finally
        {
            _suppressAttributeSync = false;
        }
    }

    [RelayCommand]
    private async Task ChangeTextAsync()
    {
        if (!CanEditText || _setDisplayTextAsync is null)
        {
            return;
        }

        var updatedText = await TextInputDialog.ShowAsync(
            _localization.Translate("DetailsChangeText"),
            _localization.Translate("ItemTextLabel"),
            EditableText);

        if (updatedText is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(updatedText))
        {
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Translate("TextCannotBeEmpty"),
                _localization.Translate("DetailsChangeText"));
            return;
        }

        await _setDisplayTextAsync(this, updatedText);
    }

    [RelayCommand]
    private Task SearchOnlineAsync() => _actionsService.OpenWebSearchAsync(this);

    [RelayCommand]
    private Task ShowCommandTextAsync() => _actionsService.ShowCommandTextAsync(this);

    [RelayCommand]
    private Task OpenFilePropertiesAsync() => _actionsService.OpenFilePropertiesAsync(this);

    [RelayCommand]
    private Task OpenFileLocationAsync() => _actionsService.OpenFileLocationAsync(this);

    [RelayCommand]
    private Task OpenRegistryLocationAsync() => _actionsService.OpenRegistryLocationAsync(this);

    [RelayCommand]
    private Task ExportRegistryAsync() => _actionsService.ExportRegistryAsync(this);

    [RelayCommand]
    private Task OpenClsidLocationAsync() => _actionsService.OpenClsidLocationAsync(this);

    [RelayCommand]
    private void CloseApprovalRemoveFlyout()
    {
        IsApprovalRemoveFlyoutOpen = false;
    }

    [RelayCommand]
    private void ClosePermanentDeleteFlyout()
    {
        IsPermanentDeleteFlyoutOpen = false;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(ShowNotes));
        OnPropertyChanged(nameof(KeyName));
        OnPropertyChanged(nameof(CategoryName));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(PendingApprovalBadgeText));
        OnPropertyChanged(nameof(CanReviewApproval));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(ConsistencyText));
        OnPropertyChanged(nameof(DetectedChangeBadgeText));
        OnPropertyChanged(nameof(DetectedChangeText));
        OnPropertyChanged(nameof(DeletedAtText));
        OnPropertyChanged(nameof(MoreActionsText));
        OnPropertyChanged(nameof(OtherAttributesTitle));
        OnPropertyChanged(nameof(DetailsTitle));
        OnPropertyChanged(nameof(OnlyWithShiftLabel));
        OnPropertyChanged(nameof(OnlyInExplorerLabel));
        OnPropertyChanged(nameof(NoWorkingDirectoryLabel));
        OnPropertyChanged(nameof(NeverDefaultLabel));
        OnPropertyChanged(nameof(ShowAsDisabledIfHiddenLabel));
        OnPropertyChanged(nameof(DetailsChangeTextLabel));
        OnPropertyChanged(nameof(DetailsSearchOnline));
        OnPropertyChanged(nameof(DetailsCommandTextLabel));
        OnPropertyChanged(nameof(DetailsFilePropertiesLabel));
        OnPropertyChanged(nameof(DetailsFileLocationLabel));
        OnPropertyChanged(nameof(DetailsRegistryLocationLabel));
        OnPropertyChanged(nameof(DetailsExportRegistryLabel));
        OnPropertyChanged(nameof(DetailsClsidLocationLabel));
        OnPropertyChanged(nameof(ApprovalRemoveConfirmationText));
        OnPropertyChanged(nameof(PermanentDeleteConfirmationText));
        OnPropertyChanged(nameof(DismissDetectedChangeText));
        OnPropertyChanged(nameof(DismissConsistencyIssueText));
    }

    partial void OnIsDetectedChangeDismissedChanged(bool value)
    {
        if (value)
        {
            DetectedChangeKind = ContextMenuChangeKind.None;
            DetectedChangeDetails = null;
        }
    }

    partial void OnIsConsistencyIssueDismissedChanged(bool value)
    {
        if (value)
        {
            HasConsistencyIssue = false;
            ConsistencyIssue = null;
        }
    }

    [RelayCommand]
    private void DismissDetectedChange()
    {
        IsDetectedChangeDismissed = true;
    }

    [RelayCommand]
    private void DismissConsistencyIssue()
    {
        IsConsistencyIssueDismissed = true;
    }
}
