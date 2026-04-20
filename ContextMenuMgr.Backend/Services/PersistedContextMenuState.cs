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
    /// </summary>
    public bool IsPendingApproval { get; set; }

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
            HandlerClsid = HandlerClsid,
            IconPath = IconPath,
            IconIndex = IconIndex,
            FilePath = FilePath,
            IsWindows11ContextMenu = IsWindows11ContextMenu,
            OnlyWithShift = OnlyWithShift,
            OnlyInExplorer = OnlyInExplorer,
            NoWorkingDirectory = NoWorkingDirectory,
            NeverDefault = NeverDefault,
            ShowAsDisabledIfHidden = ShowAsDisabledIfHidden,
            IsPresentInRegistry = false,
            IsEnabled = DesiredEnabled ?? true,
            Notes = Notes,
            IsDeleted = true,
            IsPendingApproval = IsPendingApproval,
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
            HandlerClsid = entry.HandlerClsid,
            IconPath = entry.IconPath,
            IconIndex = entry.IconIndex,
            FilePath = entry.FilePath,
            IsWindows11ContextMenu = entry.IsWindows11ContextMenu,
            OnlyWithShift = entry.OnlyWithShift,
            OnlyInExplorer = entry.OnlyInExplorer,
            NoWorkingDirectory = entry.NoWorkingDirectory,
            NeverDefault = entry.NeverDefault,
            ShowAsDisabledIfHidden = entry.ShowAsDisabledIfHidden,
            Notes = entry.Notes,
            ObservedEnabled = entry.IsEnabled,
            DesiredEnabled = null,
            IsDeleted = entry.IsDeleted,
            IsPendingApproval = entry.IsPendingApproval,
            BackupFilePath = null,
            DeletedAtUtc = entry.DeletedAtUtc,
            ConsecutiveMissingSnapshots = 0,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
