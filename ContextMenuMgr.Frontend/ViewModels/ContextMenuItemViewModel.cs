using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.Views;
using System.Windows.Media;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the context Menu Item View Model.
/// </summary>
public partial class ContextMenuItemViewModel : ObservableObject, IDisposable
{
    private readonly IconPreviewService _iconPreviewService;
    private readonly LocalizationService _localization;
    private readonly ContextMenuItemActionsService _actionsService;
    private readonly ContextMenuDeepAnalysisService? _deepAnalysisService;
    private readonly FrontendSettingsService? _settingsService;
    private readonly ShellSubMenuDialogService? _shellSubMenuDialogService;
    private readonly DetailedEditMenuDialogService? _detailedEditMenuDialogService;
    private bool _hasCuratedMenuManagement;
    private readonly Func<ContextMenuItemViewModel, bool, Task<bool>>? _setEnabledAsync;
    private readonly Func<ContextMenuItemViewModel, ContextMenuShellAttribute, bool, Task<bool>>? _setShellAttributeAsync;
    private readonly Func<ContextMenuItemViewModel, string, Task<bool>>? _setDisplayTextAsync;
    private readonly Func<ContextMenuItemViewModel, string, Task<bool>>? _setCommandTextAsync;
    private readonly Func<ContextMenuItemViewModel, Task<bool>>? _acknowledgeItemStateAsync;
    private bool _suppressEnabledSync;
    private bool _suppressAttributeSync;
    private string _detectedChangeSignature = string.Empty;
    private string _consistencyIssueSignature = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextMenuItemViewModel"/> class.
    /// </summary>
    public ContextMenuItemViewModel(
        ContextMenuEntry entry,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        ContextMenuItemActionsService actionsService,
        Func<ContextMenuItemViewModel, bool, Task<bool>>? setEnabledAsync = null,
        Func<ContextMenuItemViewModel, ContextMenuShellAttribute, bool, Task<bool>>? setShellAttributeAsync = null,
        Func<ContextMenuItemViewModel, string, Task<bool>>? setDisplayTextAsync = null,
        Func<ContextMenuItemViewModel, Task<bool>>? acknowledgeItemStateAsync = null,
        Func<ContextMenuItemViewModel, string, Task<bool>>? setCommandTextAsync = null,
        ContextMenuDeepAnalysisService? deepAnalysisService = null,
        FrontendSettingsService? settingsService = null,
        ShellSubMenuDialogService? shellSubMenuDialogService = null,
        DetailedEditMenuDialogService? detailedEditMenuDialogService = null)
    {
        _iconPreviewService = iconPreviewService;
        _localization = localization;
        _actionsService = actionsService;
        _settingsService = settingsService;
        _deepAnalysisService = deepAnalysisService;
        _shellSubMenuDialogService = shellSubMenuDialogService;
        _detailedEditMenuDialogService = detailedEditMenuDialogService;
        _setEnabledAsync = setEnabledAsync;
        _setShellAttributeAsync = setShellAttributeAsync;
        _setDisplayTextAsync = setDisplayTextAsync;
        _setCommandTextAsync = setCommandTextAsync;
        _acknowledgeItemStateAsync = acknowledgeItemStateAsync;
        Entry = entry;
        UserNote = _settingsService?.GetContextMenuItemNote(entry.Id) ?? string.Empty;
        ApplyEntry(entry);
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Gets or sets the entry.
    /// </summary>
    public ContextMenuEntry Entry { get; private set; }

    public string Id => Entry.Id;

    public ContextMenuCategory Category => Entry.Category;

    public string DisplayName
    {
        get
        {
            if (string.Equals(Entry.Id, "special:recyclebin:pintohome", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Entry.DisplayName, "RecycleBinPinToQuickAccess", StringComparison.Ordinal))
            {
                return _localization.Translate("RecycleBinPinToQuickAccess");
            }

            return Entry.Id switch
            {
                "special:wps-office-association:document-formats" => _localization.Translate("WpsOfficeAssociationHijackTitle"),
                "special:wps-office-icon:document-icons" => _localization.Translate("WpsOfficeIconHijackTitle"),
                _ when IsWpsShellNewInjection => GetWpsShellNewInjectionTitle(),
                _ => Entry.DisplayName
            };
        }
    }

    public string KeyName => Entry.KeyName;

    public string Subtitle => KeyName;

    /// <summary>
    /// Gets a safe optional application/source subtitle for compact cards.
    /// It is derived only from an already available executable path.
    /// </summary>
    public string? CompactApplicationName => ContextMenuApplicationIdentityService.GetFriendlyApplicationName(Entry);

    public bool HasCompactApplicationName => !string.IsNullOrWhiteSpace(CompactApplicationName);

    public string ShellPathTail => GetShellPathTail(RegistryPath);

    public string EditableText => Entry.EditableText ?? DisplayName;

    public string RegistryPath => Entry.RegistryPath;

    public string FileTypeRegistrationSource => GetFileTypeRegistrationSource(Entry.SourceRootPath, Entry.RegistryPath);

    public string Notes => Entry.Id switch
    {
        "special:wps-office-association:document-formats" => _localization.Format(
            "WpsOfficeAssociationHijackSummary",
            GetWpsAffectedCount(),
            GetWpsAffectedList("extension")),
        "special:wps-office-icon:document-icons" => _localization.Format(
            "WpsOfficeIconHijackSummary",
            GetWpsAffectedCount(),
            GetWpsAffectedList("progId")),
        _ when IsWpsShellNewInjection => _localization.Format(
            "WpsOfficeShellNewInjectionSummary",
            Entry.KeyName,
            GetWpsShellNewCommandSummary()),
        _ => Entry.Notes ?? string.Empty
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserNote))]
    [NotifyPropertyChangedFor(nameof(UserNoteDisplay))]
    public partial string UserNote { get; private set; }

    public bool HasUserNote => !string.IsNullOrWhiteSpace(UserNote);

    public string UserNoteDisplay => HasUserNote
        ? _localization.Format("UserNoteDisplayFormat", UserNote)
        : string.Empty;

    public bool ShowNotes => !string.IsNullOrWhiteSpace(Entry.Notes);

    public string? NotesToolTip => !string.IsNullOrWhiteSpace(Entry.DetectedChangeDetails)
        && !string.Equals(Entry.DetectedChangeDetails, Notes, StringComparison.Ordinal)
            ? Entry.DetectedChangeDetails
            : null;

    private bool IsWpsShellNewInjection
        => Entry.Id.StartsWith("special:wps-shellnew-injection:", StringComparison.OrdinalIgnoreCase);

    public ImageSource? IconSource => _iconPreviewService.GetIcon(Entry.IconPath, Entry.IconIndex, Entry.FilePath);

    public bool HasIcon => IconSource is not null;

    public bool ShowKeyName => !string.Equals(DisplayName, KeyName, StringComparison.OrdinalIgnoreCase);

    public string CategoryName => ContextMenuCategoryText.GetLocalizedName(Category, _localization);

    public bool IsWindows11ContextMenu => Entry.IsWindows11ContextMenu;

    public bool HasClsidLocation => Guid.TryParse(Entry.HandlerClsid, out _);

    public bool HasFileLocation => !string.IsNullOrWhiteSpace(Entry.FilePath);

    public bool HasRegistryLocation => !string.IsNullOrWhiteSpace(Entry.RegistryPath);

    public bool HasCommandText => !string.IsNullOrWhiteSpace(Entry.CommandText);

    public bool CanEditCommandText => Entry.CanEditCommandText
        && Entry.EntryKind == ContextMenuEntryKind.ShellVerb
        && !Entry.IsWindows11ContextMenu
        && IsPresentInRegistry
        && !IsDeleted;

    public bool ShowReadOnlyCommandText => HasCommandText && !CanEditCommandText;

    public bool ShowInlineCommandText => Entry.EntryKind == ContextMenuEntryKind.ShellVerb && HasCommandText;

    public string? InlineCommandText => ShowInlineCommandText
        ? _localization.Format("InlineCommandTextFormat", Entry.CommandText!)
        : null;

    public bool HasOtherAttributesSection => Entry.EntryKind == ContextMenuEntryKind.ShellVerb && IsPresentInRegistry && !IsDeleted;

    public bool CanEditShellAttributes => HasOtherAttributesSection && !IsAttributesBusy;

    public bool HasActionFlyout => HasOtherAttributesSection
        || HasDetailsActions
        || CanOpenFileTypeBatchManagement
        || CanPrimaryAction
        || CanPermanentlyDelete
        || CanDeepAnalyzeMenuItem;

    public bool CanEditText => Entry.EntryKind == ContextMenuEntryKind.ShellVerb
        && IsPresentInRegistry
        && !IsDeleted
        && !string.IsNullOrWhiteSpace(Entry.EditableText)
        && !string.Equals(KeyName, "open", StringComparison.OrdinalIgnoreCase);

    public bool HasDetailsActions => CanEditUserNote || CanEditText || CanEditCommandText || ShowReadOnlyCommandText || HasFileLocation || HasRegistryLocation || HasClsidLocation || CanSearchOnline;

    public bool CanSearchOnline => !string.IsNullOrWhiteSpace(DisplayName) || !string.IsNullOrWhiteSpace(KeyName);

    public bool CanEditUserNote => _settingsService is not null && !IsWindows11ContextMenu;

    public bool CanDeepAnalyzeMenuItem => _deepAnalysisService is not null
        && ContextMenuDeepAnalysisCapability.CanDeepAnalyze(Entry)
        && !IsDeepAnalyzing;

    public bool CanOpenFileTypeBatchManagement => !IsDeleted
        && IsPresentInRegistry
        && !string.IsNullOrWhiteSpace(Entry.RegistryPath)
        && !string.IsNullOrWhiteSpace(Entry.BackendRegistryPath)
        && Entry.EntryKind switch
        {
            ContextMenuEntryKind.ShellVerb => !string.IsNullOrWhiteSpace(Entry.KeyName)
                                              && (!string.IsNullOrWhiteSpace(Entry.FilePath)
                                                  || !string.IsNullOrWhiteSpace(ExtractCommandExecutablePath(Entry.CommandText))),
            ContextMenuEntryKind.ShellExtension => !string.IsNullOrWhiteSpace(Entry.HandlerClsid),
            _ => false
        };

    public bool IsProtectedFileTypeBatchDelete => ProtectedMenuItemGuard.IsProtectedFileTypeBatchDeleteItem(Entry);

    public bool CanDeleteInFileTypeBatch => !IsDeleted && IsPresentInRegistry && !IsProtectedFileTypeBatchDelete;

    /// <summary>
    /// Gets or sets a value indicating whether enabled.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    [NotifyPropertyChangedFor(nameof(SortDeletedWeight))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeepAnalyzeMenuItem))]
    [NotifyPropertyChangedFor(nameof(HasActionFlyout))]
    public partial bool IsDeepAnalyzing { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether deleted.
    /// </summary>
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
    [NotifyPropertyChangedFor(nameof(CanEditCommandText))]
    [NotifyPropertyChangedFor(nameof(ShowReadOnlyCommandText))]
    [NotifyPropertyChangedFor(nameof(HasDetailsActions))]
    [NotifyPropertyChangedFor(nameof(CanDeepAnalyzeMenuItem))]
    [NotifyPropertyChangedFor(nameof(CanOpenFileTypeBatchManagement))]
    [NotifyPropertyChangedFor(nameof(CanDeleteInFileTypeBatch))]
    public partial bool IsDeleted { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether pending Approval.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    [NotifyPropertyChangedFor(nameof(CanReviewApproval))]
    public partial bool IsPendingApproval { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether backup.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPermanentlyDelete))]
    public partial bool HasBackup { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether consistency Issue.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(ConsistencyText))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial bool HasConsistencyIssue { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether consistency Issue Dismissed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConsistencyIssue))]
    [NotifyPropertyChangedFor(nameof(ConsistencyText))]
    [NotifyPropertyChangedFor(nameof(CanDismissConsistencyIssue))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial bool IsConsistencyIssueDismissed { get; set; }

    /// <summary>
    /// Gets or sets the consistency Issue.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConsistencyText))]
    public partial string? ConsistencyIssue { get; private set; }

    /// <summary>
    /// Gets or sets the deleted At Utc.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletedAtText))]
    public partial DateTimeOffset? DeletedAtUtc { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether present In Registry.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(ShowToggle))]
    [NotifyPropertyChangedFor(nameof(CanPrimaryAction))]
    [NotifyPropertyChangedFor(nameof(HasOtherAttributesSection))]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    [NotifyPropertyChangedFor(nameof(HasActionFlyout))]
    [NotifyPropertyChangedFor(nameof(CanEditCommandText))]
    [NotifyPropertyChangedFor(nameof(ShowReadOnlyCommandText))]
    [NotifyPropertyChangedFor(nameof(HasDetailsActions))]
    [NotifyPropertyChangedFor(nameof(CanReviewApproval))]
    [NotifyPropertyChangedFor(nameof(CanDeepAnalyzeMenuItem))]
    [NotifyPropertyChangedFor(nameof(CanOpenFileTypeBatchManagement))]
    [NotifyPropertyChangedFor(nameof(CanDeleteInFileTypeBatch))]
    public partial bool IsPresentInRegistry { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether toggle Busy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsToggleBusy { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether attributes Busy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool IsAttributesBusy { get; private set; }

    /// <summary>
    /// Gets or sets the detected Change Kind.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectedChange))]
    [NotifyPropertyChangedFor(nameof(DetectedChangeBadgeText))]
    [NotifyPropertyChangedFor(nameof(DetectedChangeText))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial ContextMenuChangeKind DetectedChangeKind { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether detected Change Dismissed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectedChange))]
    [NotifyPropertyChangedFor(nameof(CanDismissDetectedChange))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(SortAttentionWeight))]
    public partial bool IsDetectedChangeDismissed { get; set; }

    /// <summary>
    /// Gets or sets the only With Shift.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool OnlyWithShift { get; set; }

    /// <summary>
    /// Gets or sets the only In Explorer.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool OnlyInExplorer { get; set; }

    /// <summary>
    /// Gets or sets the no Working Directory.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool NoWorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets the never Default.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool NeverDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether as Disabled If Hidden.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditShellAttributes))]
    public partial bool ShowAsDisabledIfHidden { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether approval Remove Flyout Open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsApprovalRemoveFlyoutOpen { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether permanent Delete Flyout Open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPermanentDeleteFlyoutOpen { get; set; }

    /// <summary>
    /// Gets or sets the detected Change Details.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectedChangeText))]
    public partial string? DetectedChangeDetails { get; private set; }

    public string StateLabel => IsDeleted
        ? _localization.Translate("DeletedState")
        : _localization.Translate(IsEnabled ? "Enabled" : "Disabled");

    public string ToggleLabel => _localization.Translate(IsEnabled ? "ToggleOn" : "ToggleOff");

    public string PendingApprovalBadgeText => _localization.Translate("PendingApprovalBadge");

    public bool CanReviewApproval => IsPendingApproval && IsPresentInRegistry && !IsDeleted;

    public bool CanToggle => Entry.CanToggle && !IsDeleted && IsPresentInRegistry && !IsToggleBusy;

    public bool ShowToggle => Entry.CanToggle && !IsDeleted && IsPresentInRegistry;

    public bool CanManageSubMenuItems => (Entry.CanManageSubMenuItems || _hasCuratedMenuManagement) && !IsDeleted && IsPresentInRegistry && !IsSubMenuLoading;

    public string ManageSubMenuItemsToolTip => _localization.Translate("ManageSubMenuItems");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageSubMenuItems))]
    public partial bool IsSubMenuLoading { get; private set; }

    public bool CanPrimaryAction => IsDeleted || IsPresentInRegistry;

    public bool CanPermanentlyDelete => IsDeleted && HasBackup;

    public string PrimaryActionLabel => IsDeleted
        ? _localization.Translate("UndoDelete")
        : _localization.Translate("Delete");

    public string ConsistencyText => string.IsNullOrWhiteSpace(ConsistencyIssue)
        ? string.Empty
        : Entry.HasLegacyGlobalShellExtensionBlock
            ? _localization.Translate("LegacyGlobalShellExtensionBlockWarning")
            : _localization.Translate("ConsistencyIssueGeneric");

    public bool CanDismissConsistencyIssue => HasConsistencyIssue;

    public bool HasDetectedChange => DetectedChangeKind != ContextMenuChangeKind.None;

    public bool CanDismissDetectedChange => HasDetectedChange;

    public bool CanDismissAttention => HasDetectedChange || HasConsistencyIssue;

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

            return summary;
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

    public string DismissAttentionText => _localization.Translate("DismissDetectedChange");

    public string OtherAttributesTitle => _localization.Translate("OtherAttributesTitle");

    public string DetailsTitle => _localization.Translate("DetailsTitle");

    public string ManageApplicationMenusText => _localization.Translate("ManageApplicationMenus");

    public string OnlyWithShiftLabel => _localization.Translate("OnlyWithShiftLabel");

    public string OnlyInExplorerLabel => _localization.Translate("OnlyInExplorerLabel");

    public string NoWorkingDirectoryLabel => _localization.Translate("NoWorkingDirectoryLabel");

    public string NeverDefaultLabel => _localization.Translate("NeverDefaultLabel");

    public string ShowAsDisabledIfHiddenLabel => _localization.Translate("ShowAsDisabledIfHiddenLabel");

    public string DetailsSearchOnline => _localization.Translate("DetailsSearchOnline");

    public string DetailsChangeTextLabel => _localization.Translate("DetailsChangeText");

    public string DetailsEditUserNoteLabel => _localization.Translate("DetailsEditUserNote");

    public string DetailsChangeCommandLabel => _localization.Translate("DetailsChangeCommand");

    public string DetailsCommandTextLabel => _localization.Translate("DetailsCommandText");

    public string DetailsFilePropertiesLabel => _localization.Translate("DetailsFileProperties");

    public string DetailsFileLocationLabel => _localization.Translate("DetailsFileLocation");

    public string DetailsRegistryLocationLabel => _localization.Translate("DetailsRegistryLocation");

    public string DetailsExportRegistryLabel => _localization.Translate("DetailsExportRegistry");

    public string DetailsClsidLocationLabel => _localization.Translate("DetailsClsidLocation");

    public string DeepAnalyzeMenuItemText => _localization.Translate("DeepAnalyzeMenuItemText");

    public string DeepAnalyzeMenuItemTooltip => _localization.Translate("DeepAnalyzeMenuItemTooltip");

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

    /// <summary>
    /// Executes update.
    /// </summary>
    public void Update(ContextMenuEntry entry)
    {
        Entry = entry;
        ApplyEntry(entry);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(KeyName));
        OnPropertyChanged(nameof(EditableText));
        OnPropertyChanged(nameof(RegistryPath));
        OnPropertyChanged(nameof(FileTypeRegistrationSource));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(ShowNotes));
        OnPropertyChanged(nameof(NotesToolTip));
        OnPropertyChanged(nameof(IconSource));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(ShowKeyName));
        OnPropertyChanged(nameof(HasClsidLocation));
        OnPropertyChanged(nameof(HasFileLocation));
        OnPropertyChanged(nameof(HasRegistryLocation));
        OnPropertyChanged(nameof(HasCommandText));
        OnPropertyChanged(nameof(CanEditCommandText));
        OnPropertyChanged(nameof(ShowReadOnlyCommandText));
        OnPropertyChanged(nameof(ShowInlineCommandText));
        OnPropertyChanged(nameof(InlineCommandText));
        OnPropertyChanged(nameof(CompactApplicationName));
        OnPropertyChanged(nameof(HasCompactApplicationName));
        OnPropertyChanged(nameof(CanDeepAnalyzeMenuItem));
        OnPropertyChanged(nameof(CanOpenFileTypeBatchManagement));
        OnPropertyChanged(nameof(IsProtectedFileTypeBatchDelete));
        OnPropertyChanged(nameof(CanDeleteInFileTypeBatch));
        OnPropertyChanged(nameof(CanEditText));
        OnPropertyChanged(nameof(HasOtherAttributesSection));
        OnPropertyChanged(nameof(CanEditShellAttributes));
        OnPropertyChanged(nameof(HasDetailsActions));
        OnPropertyChanged(nameof(HasActionFlyout));
        OnPropertyChanged(nameof(CanSearchOnline));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ShowToggle));
        OnPropertyChanged(nameof(CanManageSubMenuItems));
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
            _hasCuratedMenuManagement = _detailedEditMenuDialogService?.CanManage(entry) == true;
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
    private async Task ChangeCommandTextAsync()
    {
        if (!CanEditCommandText || _setCommandTextAsync is null)
        {
            return;
        }

        var updatedCommand = await TextInputDialog.ShowAsync(
            _localization.Translate("DetailsChangeCommand"),
            _localization.Translate("ItemCommandLabel"),
            Entry.CommandText ?? string.Empty,
            width: 720,
            height: 320,
            multiline: true);

        if (updatedCommand is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(updatedCommand))
        {
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Translate("CommandCannotBeEmpty"),
                _localization.Translate("DetailsChangeCommand"));
            return;
        }

        await _setCommandTextAsync(this, updatedCommand);
    }

    [RelayCommand]
    private async Task EditUserNoteAsync()
    {
        if (IsWindows11ContextMenu)
        {
            return;
        }

        var note = await TextInputDialog.ShowAsync(
            _localization.Translate("DetailsEditUserNote"),
            _localization.Translate("UserNoteLabel"),
            UserNote,
            width: 620,
            height: 300,
            multiline: true);
        if (note is null)
        {
            return;
        }

        if (_settingsService is null)
        {
            return;
        }

        _settingsService.UpdateContextMenuItemNote(Id, note);
        UserNote = note.Trim();
    }

    [RelayCommand]
    private Task OpenFilePropertiesAsync() => _actionsService.OpenFilePropertiesAsync(this);

    [RelayCommand]
    private Task OpenFileLocationAsync() => _actionsService.OpenFileLocationAsync(this);

    [RelayCommand]
    private Task OpenRegistryLocationAsync() => _actionsService.OpenRegistryLocationAsync(this);

    [RelayCommand]
    private Task ExportRegistryAsync() => _actionsService.ExportRegistryAsync(this);

    [RelayCommand]
    private async Task ManageSubMenuItemsAsync()
    {
        if (!CanManageSubMenuItems) return;
        IsSubMenuLoading = true;
        try
        {
            if (Entry.CanManageSubMenuItems && _shellSubMenuDialogService is not null)
                await _shellSubMenuDialogService.ShowAsync(Entry);
            else if (_detailedEditMenuDialogService is not null)
                await _detailedEditMenuDialogService.ShowAsync(Entry);
        }
        finally { IsSubMenuLoading = false; }
    }

    [RelayCommand]
    private Task OpenClsidLocationAsync() => _actionsService.OpenClsidLocationAsync(this);

    [RelayCommand]
    private async Task DeepAnalyzeMenuItemAsync()
    {
        if (!CanDeepAnalyzeMenuItem || _deepAnalysisService is null)
        {
            return;
        }

        var request = new ContextMenuDeepAnalysisRequest
        {
            OperationId = Guid.NewGuid(),
            ItemId = Id,
            DisplayName = DisplayName,
            Category = Category,
            EntryKind = Entry.EntryKind,
            HandlerClsid = Entry.HandlerClsid,
            HandlerFilePath = Entry.FilePath,
            RegistryPath = Entry.RegistryPath,
            BackendRegistryPath = Entry.BackendRegistryPath,
            CommandText = Entry.CommandText,
            IncludeExtendedVerbs = false,
            ProbeMode = ContextMenuDeepAnalysisProbeMode.SpecificHandler
        };

        var dialogViewModel = new ContextMenuDeepAnalysisWindowViewModel(
            _localization,
            request,
            analysisRequest => _deepAnalysisService.AnalyzeAsync(analysisRequest));
        var dialog = new ContextMenuDeepAnalysisWindow(dialogViewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        dialog.Show();
        IsDeepAnalyzing = true;
        try
        {
            await dialogViewModel.RunAsync();
        }
        finally
        {
            IsDeepAnalyzing = false;
        }
    }

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
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(ShowNotes));
        OnPropertyChanged(nameof(NotesToolTip));
        OnPropertyChanged(nameof(KeyName));
        OnPropertyChanged(nameof(CategoryName));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ManageSubMenuItemsToolTip));
        OnPropertyChanged(nameof(PendingApprovalBadgeText));
        OnPropertyChanged(nameof(CanReviewApproval));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ShowToggle));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(ConsistencyText));
        OnPropertyChanged(nameof(DetectedChangeBadgeText));
        OnPropertyChanged(nameof(DetectedChangeText));
        OnPropertyChanged(nameof(DeletedAtText));
        OnPropertyChanged(nameof(MoreActionsText));
        OnPropertyChanged(nameof(OtherAttributesTitle));
        OnPropertyChanged(nameof(DetailsTitle));
        OnPropertyChanged(nameof(ManageApplicationMenusText));
        OnPropertyChanged(nameof(OnlyWithShiftLabel));
        OnPropertyChanged(nameof(OnlyInExplorerLabel));
        OnPropertyChanged(nameof(NoWorkingDirectoryLabel));
        OnPropertyChanged(nameof(NeverDefaultLabel));
        OnPropertyChanged(nameof(ShowAsDisabledIfHiddenLabel));
        OnPropertyChanged(nameof(DetailsChangeTextLabel));
        OnPropertyChanged(nameof(DetailsEditUserNoteLabel));
        OnPropertyChanged(nameof(UserNoteDisplay));
        OnPropertyChanged(nameof(DetailsChangeCommandLabel));
        OnPropertyChanged(nameof(DetailsSearchOnline));
        OnPropertyChanged(nameof(DetailsCommandTextLabel));
        OnPropertyChanged(nameof(DetailsFilePropertiesLabel));
        OnPropertyChanged(nameof(DetailsFileLocationLabel));
        OnPropertyChanged(nameof(DetailsRegistryLocationLabel));
        OnPropertyChanged(nameof(DetailsExportRegistryLabel));
        OnPropertyChanged(nameof(DetailsClsidLocationLabel));
        OnPropertyChanged(nameof(InlineCommandText));
        OnPropertyChanged(nameof(DeepAnalyzeMenuItemText));
        OnPropertyChanged(nameof(DeepAnalyzeMenuItemTooltip));
        OnPropertyChanged(nameof(ApprovalRemoveConfirmationText));
        OnPropertyChanged(nameof(PermanentDeleteConfirmationText));
        OnPropertyChanged(nameof(DismissDetectedChangeText));
        OnPropertyChanged(nameof(DismissConsistencyIssueText));
        OnPropertyChanged(nameof(DismissAttentionText));
    }

    partial void OnIsDetectedChangeDismissedChanged(bool value)
    {
        if (value)
        {
            DetectedChangeKind = ContextMenuChangeKind.None;
            DetectedChangeDetails = null;
        }

        OnPropertyChanged(nameof(CanDismissAttention));
    }

    partial void OnIsConsistencyIssueDismissedChanged(bool value)
    {
        if (value)
        {
            HasConsistencyIssue = false;
            ConsistencyIssue = null;
        }

        OnPropertyChanged(nameof(CanDismissAttention));
    }

    [RelayCommand]
    private async Task DismissDetectedChangeAsync()
    {
        await TryAcknowledgeItemStateAsync();

        // Acknowledge only reconciles the persisted desired/observed state. It
        // does not clear registry-conflict style consistency issues (e.g. both
        // active and disabled registrations exist, or the handler is on the
        // legacy global block list), because the registry itself is unchanged.
        // Always dismiss locally so the user can actually ignore the banner;
        // ApplyEntry keeps the dismissal while the issue signature is unchanged.
        IsDetectedChangeDismissed = true;
        IsConsistencyIssueDismissed = true;
    }

    [RelayCommand]
    private async Task DismissConsistencyIssueAsync()
    {
        await TryAcknowledgeItemStateAsync();
        IsDetectedChangeDismissed = true;
        IsConsistencyIssueDismissed = true;
    }

    private async Task<bool> TryAcknowledgeItemStateAsync()
    {
        if (_acknowledgeItemStateAsync is null)
        {
            return false;
        }

        try
        {
            return await _acknowledgeItemStateAsync(this);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(
                nameof(ContextMenuItemViewModel),
                $"AcknowledgeItemState failed for {Id}: {ex.Message}");
            return false;
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
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private static string GetShellPathTail(string? registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath))
        {
            return string.Empty;
        }

        var normalized = registryPath.Replace('/', '\\').TrimEnd('\\');
        var lastSeparatorIndex = normalized.LastIndexOf('\\');
        return lastSeparatorIndex >= 0
            ? normalized[(lastSeparatorIndex + 1)..]
            : normalized;
    }

    private static string GetFileTypeRegistrationSource(string? sourceRootPath, string? registryPath)
    {
        var root = string.IsNullOrWhiteSpace(sourceRootPath)
            ? registryPath
            : sourceRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        var normalized = root.Replace('/', '\\').Trim('\\');
        foreach (var suffix in new[] { @"\shellex\ContextMenuHandlers", @"\shellex\PropertySheetHandlers", @"\shell" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length].TrimEnd('\\');
                break;
            }
        }

        const string systemFileAssociations = @"SystemFileAssociations\";
        var systemIndex = normalized.IndexOf(systemFileAssociations, StringComparison.OrdinalIgnoreCase);
        if (systemIndex >= 0)
        {
            return normalized[systemIndex..];
        }

        const string classesRoot = @"HKEY_CLASSES_ROOT\";
        if (normalized.StartsWith(classesRoot, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[classesRoot.Length..];
        }

        const string machineClasses = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\";
        if (normalized.StartsWith(machineClasses, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[machineClasses.Length..];
        }

        const string userClassesMarker = @"\Software\Classes\";
        var userClassesIndex = normalized.IndexOf(userClassesMarker, StringComparison.OrdinalIgnoreCase);
        if (userClassesIndex >= 0)
        {
            normalized = normalized[(userClassesIndex + userClassesMarker.Length)..];
        }

        return normalized;
    }

    private static string? ExtractCommandExecutablePath(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return null;
        }

        var trimmed = commandText.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuoteIndex = trimmed.IndexOf('"', 1);
            if (closingQuoteIndex > 1)
            {
                var quoted = trimmed[1..closingQuoteIndex];
                return EndsWithExecutableExtension(quoted) ? quoted : null;
            }
        }

        foreach (var extension in new[] { ".exe", ".dll" })
        {
            var extensionIndex = trimmed.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (extensionIndex > 0)
            {
                return trimmed[..(extensionIndex + extension.Length)].Trim().Trim('"');
            }
        }

        return null;
    }

    private static bool EndsWithExecutableExtension(string value)
        => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           || value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private string GetWpsAffectedCount()
    {
        var details = Entry.DetectedChangeDetails;
        if (string.IsNullOrWhiteSpace(details))
        {
            return "0";
        }

        const string marker = "affectedCount=";
        var start = details.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "0";
        }

        start += marker.Length;
        var end = details.IndexOf(';', start);
        return details[start..(end < 0 ? details.Length : end)].Trim();
    }

    private string GetWpsAffectedList(string key)
    {
        var details = Entry.DetectedChangeDetails;
        if (string.IsNullOrWhiteSpace(details))
        {
            return string.Empty;
        }

        var marker = $"{key}=";
        var values = details
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => ExtractDetailValue(part, marker))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return values.Length == 0 ? string.Empty : string.Join(", ", values);
    }

    private string GetWpsShellNewInjectionTitle()
    {
        if (Entry.KeyName.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return _localization.Translate("WpsOfficeShellNewInjectionPdfTitle");
        }

        if (Entry.KeyName.Contains("pptx", StringComparison.OrdinalIgnoreCase)
            && Entry.KeyName.Contains("aicreate", StringComparison.OrdinalIgnoreCase))
        {
            return _localization.Translate("WpsOfficeShellNewInjectionPptxAiTitle");
        }

        return _localization.Format("WpsOfficeShellNewInjectionTitle", Entry.KeyName);
    }

    private string GetWpsShellNewCommandSummary()
    {
        var command = Entry.CommandText;
        if (string.IsNullOrWhiteSpace(command))
        {
            command = ExtractDetailValue(Entry.DetectedChangeDetails ?? string.Empty, "shellNewCommand=");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        return command.Contains("ksolaunch.exe", StringComparison.OrdinalIgnoreCase)
            ? "ksolaunch.exe"
            : command;
    }

    private static string? ExtractDetailValue(string detail, string marker)
    {
        var start = detail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = detail.IndexOf(',', start);
        return detail[start..(end < 0 ? detail.Length : end)].Trim();
    }
}
