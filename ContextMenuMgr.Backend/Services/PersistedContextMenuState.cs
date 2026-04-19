using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

public sealed class PersistedContextMenuState
{
    public string Id { get; set; } = string.Empty;

    public ContextMenuCategory Category { get; set; }

    public ContextMenuEntryKind EntryKind { get; set; }

    public string KeyName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? EditableText { get; set; }

    public string RegistryPath { get; set; } = string.Empty;

    public string BackendRegistryPath { get; set; } = string.Empty;

    public string SourceRootPath { get; set; } = string.Empty;

    public string? CommandText { get; set; }

    public string? HandlerClsid { get; set; }

    public string? IconPath { get; set; }

    public int IconIndex { get; set; }

    public string? FilePath { get; set; }

    public bool IsWindows11ContextMenu { get; set; }

    public bool OnlyWithShift { get; set; }

    public bool OnlyInExplorer { get; set; }

    public bool NoWorkingDirectory { get; set; }

    public bool NeverDefault { get; set; }

    public bool ShowAsDisabledIfHidden { get; set; }

    public string? Notes { get; set; }

    public bool ObservedEnabled { get; set; }

    public bool? DesiredEnabled { get; set; }

    public bool IsDeleted { get; set; }

    public bool IsPendingApproval { get; set; }

    public string? BackupFilePath { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public bool SuppressNextDetection { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

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
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
