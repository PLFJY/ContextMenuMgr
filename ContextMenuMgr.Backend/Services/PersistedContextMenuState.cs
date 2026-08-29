using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the persisted Context Menu State.
/// </summary>
public sealed class PersistedContextMenuState
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category.
    /// </summary>
    public ContextMenuCategory Category { get; set; }

    /// <summary>
    /// Gets or sets the entry Kind.
    /// </summary>
    public ContextMenuEntryKind EntryKind { get; set; }

    /// <summary>
    /// Gets or sets the key Name.
    /// </summary>
    public string KeyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display Name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the editable Text.
    /// </summary>
    public string? EditableText { get; set; }

    /// <summary>
    /// Gets or sets the registry Path.
    /// </summary>
    public string RegistryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the backend Registry Path.
    /// </summary>
    public string BackendRegistryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source Root Path.
    /// </summary>
    public string SourceRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the command Text.
    /// </summary>
    public string? CommandText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether command text can be edited.
    /// </summary>
    public bool CanEditCommandText { get; set; }

    /// <summary>
    /// Gets or sets the handler Clsid.
    /// </summary>
    public string? HandlerClsid { get; set; }

    /// <summary>
    /// Gets or sets the icon Path.
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// Gets or sets the icon Index.
    /// </summary>
    public int IconIndex { get; set; }

    /// <summary>
    /// Gets or sets the file Path.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether windows11 Context Menu.
    /// </summary>
    public bool IsWindows11ContextMenu { get; set; }

    /// <summary>
    /// Gets or sets the Windows 11 context-menu source kind.
    /// </summary>
    public Windows11ContextMenuSourceKind Windows11SourceKind { get; set; } = Windows11ContextMenuSourceKind.PackagedCom;

    /// <summary>
    /// Gets or sets a value indicating whether this Windows 11 item is protected from safe modification.
    /// </summary>
    public bool IsProtectedSystemItem { get; set; }

    /// <summary>
    /// Gets or sets the only With Shift.
    /// </summary>
    public bool OnlyWithShift { get; set; }

    /// <summary>
    /// Gets or sets the only In Explorer.
    /// </summary>
    public bool OnlyInExplorer { get; set; }

    /// <summary>
    /// Gets or sets the no Working Directory.
    /// </summary>
    public bool NoWorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets the never Default.
    /// </summary>
    public bool NeverDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether as Disabled If Hidden.
    /// </summary>
    public bool ShowAsDisabledIfHidden { get; set; }

    /// <summary>
    /// Gets or sets the notes.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Gets or sets the observed Enabled.
    /// </summary>
    public bool ObservedEnabled { get; set; }

    /// <summary>
    /// Gets or sets the desired Enabled.
    /// </summary>
    public bool? DesiredEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether deleted.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether pending Approval.
    /// Setting this to false automatically clears <see cref="PendingApprovalChangeKind"/>.
    /// </summary>
    public bool IsPendingApproval
    {
        get => _isPendingApproval;
        set
        {
            _isPendingApproval = value;
            if (!value)
            {
                _pendingApprovalChangeKind = null;
            }
        }
    }
    private bool _isPendingApproval;

    /// <summary>
    /// Gets or sets the change kind that originated the current pending approval.
    /// Active code writes <see cref="ContextMenuChangeKind.Added"/> only.
    /// <see cref="ContextMenuChangeKind.Reappeared"/> remains readable solely for
    /// compatibility with old state files. Files without this field deserialize
    /// safely to <c>null</c>.
    /// </summary>
    public ContextMenuChangeKind? PendingApprovalChangeKind
    {
        get => _pendingApprovalChangeKind;
        set
        {
            // If someone sets a non-null change kind while IsPendingApproval is false,
            // automatically flip IsPendingApproval to true to maintain consistency.
            _pendingApprovalChangeKind = value;
            if (value is not null && !_isPendingApproval)
            {
                _isPendingApproval = true;
            }
        }
    }
    private ContextMenuChangeKind? _pendingApprovalChangeKind;

    /// <summary>
    /// Gets or sets the backup File Path.
    /// </summary>
    public string? BackupFilePath { get; set; }

    /// <summary>
    /// Gets or sets the deleted At Utc.
    /// </summary>
    public DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the suppress Next Detection.
    /// </summary>
    public bool SuppressNextDetection { get; set; }

    /// <summary>
    /// Gets or sets the updated At Utc.
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the number of consecutive settled snapshots where this item was missing.
    /// </summary>
    public int ConsecutiveMissingSnapshots { get; set; }

    /// <summary>
    /// Executes to Deleted Entry.
    /// </summary>
    public ContextMenuEntry ToDeletedEntry(string? consistencyIssue = null)
    {
        return new ContextMenuEntry
        {
            Id = Id,
            Category = Category,
            EntryKind = EntryKind,
            KeyName = KeyName,
            DisplayName = DisplayName,
            EditableText = EditableText,
            RegistryPath = RegistryPath,
            BackendRegistryPath = BackendRegistryPath,
            SourceRootPath = SourceRootPath,
            CommandText = CommandText,
            CanEditCommandText = CanEditCommandText,
            HandlerClsid = HandlerClsid,
            IconPath = IconPath,
            IconIndex = IconIndex,
            FilePath = FilePath,
            IsWindows11ContextMenu = IsWindows11ContextMenu,
            Windows11SourceKind = Windows11SourceKind,
            IsProtectedSystemItem = IsProtectedSystemItem,
            OnlyWithShift = OnlyWithShift,
            OnlyInExplorer = OnlyInExplorer,
            NoWorkingDirectory = NoWorkingDirectory,
            NeverDefault = NeverDefault,
            ShowAsDisabledIfHidden = ShowAsDisabledIfHidden,
            IsPresentInRegistry = false,
            IsEnabled = DesiredEnabled ?? true,
            Notes = Notes,
            IsDeleted = true,
            // Deleted records are recovery metadata, not monitored entries.
            IsPendingApproval = false,
            HasBackup = !string.IsNullOrWhiteSpace(BackupFilePath),
            DeletedAtUtc = DeletedAtUtc,
            DetectedChangeKind = ContextMenuChangeKind.None,
            HasConsistencyIssue = !string.IsNullOrWhiteSpace(consistencyIssue),
            ConsistencyIssue = consistencyIssue
        };
    }

    /// <summary>
    /// Executes from Entry.
    /// </summary>
    public static PersistedContextMenuState FromEntry(ContextMenuEntry entry)
    {
        return new PersistedContextMenuState
        {
            Id = entry.Id,
            Category = entry.Category,
            EntryKind = entry.EntryKind,
            KeyName = entry.KeyName,
            DisplayName = entry.DisplayName,
            EditableText = entry.EditableText,
            RegistryPath = entry.RegistryPath,
            BackendRegistryPath = entry.BackendRegistryPath,
            SourceRootPath = entry.SourceRootPath,
            CommandText = entry.CommandText,
            CanEditCommandText = entry.CanEditCommandText,
            HandlerClsid = entry.HandlerClsid,
            IconPath = entry.IconPath,
            IconIndex = entry.IconIndex,
            FilePath = entry.FilePath,
            IsWindows11ContextMenu = entry.IsWindows11ContextMenu,
            Windows11SourceKind = entry.Windows11SourceKind,
            IsProtectedSystemItem = entry.IsProtectedSystemItem,
            OnlyWithShift = entry.OnlyWithShift,
            OnlyInExplorer = entry.OnlyInExplorer,
            NoWorkingDirectory = entry.NoWorkingDirectory,
            NeverDefault = entry.NeverDefault,
            ShowAsDisabledIfHidden = entry.ShowAsDisabledIfHidden,
            Notes = entry.Notes,
            ObservedEnabled = entry.IsEnabled,
            // Every adopted item records the registry state that formed the
            // baseline. An enabled baseline may later be reported as externally
            // disabled, while a disabled baseline is continuously enforced if a
            // third party enables it again.
            DesiredEnabled = entry.IsEnabled,
            IsDeleted = entry.IsDeleted,
            IsPendingApproval = entry.IsPendingApproval,
            PendingApprovalChangeKind = null,
            BackupFilePath = null,
            DeletedAtUtc = entry.DeletedAtUtc,
            ConsecutiveMissingSnapshots = 0,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
