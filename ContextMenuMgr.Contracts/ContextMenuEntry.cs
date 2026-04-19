namespace ContextMenuMgr.Contracts;

public sealed record ContextMenuEntry
{
    public string Id { get; init; } = string.Empty;

    public ContextMenuCategory Category { get; init; }

    public ContextMenuEntryKind EntryKind { get; init; }

    public string KeyName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? EditableText { get; init; }

    public string RegistryPath { get; init; } = string.Empty;

    public string BackendRegistryPath { get; init; } = string.Empty;

    public string SourceRootPath { get; init; } = string.Empty;

    public string? CommandText { get; init; }

    public string? HandlerClsid { get; init; }

    public string? IconPath { get; init; }

    public int IconIndex { get; init; }

    public string? FilePath { get; init; }

    public bool IsWindows11ContextMenu { get; init; }

    public bool OnlyWithShift { get; init; }

    public bool OnlyInExplorer { get; init; }

    public bool NoWorkingDirectory { get; init; }

    public bool NeverDefault { get; init; }

    public bool ShowAsDisabledIfHidden { get; init; }

    public bool IsEnabled { get; init; }

    public bool IsPresentInRegistry { get; init; } = true;

    public string? Notes { get; init; }

    public bool IsDeleted { get; init; }

    public bool IsPendingApproval { get; init; }

    public bool HasBackup { get; init; }

    public DateTimeOffset? DeletedAtUtc { get; init; }

    public bool HasConsistencyIssue { get; init; }

    public string? ConsistencyIssue { get; init; }

    public ContextMenuChangeKind DetectedChangeKind { get; init; }

    public string? DetectedChangeDetails { get; init; }
}
