namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the context Menu Entry.
/// </summary>
public sealed record ContextMenuEntry
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the category.
    /// </summary>
    public ContextMenuCategory Category { get; init; }

    /// <summary>
    /// Gets or sets the entry Kind.
    /// </summary>
    public ContextMenuEntryKind EntryKind { get; init; }

    /// <summary>
    /// Gets or sets the key Name.
    /// </summary>
    public string KeyName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the display Name.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the editable Text.
    /// </summary>
    public string? EditableText { get; init; }

    /// <summary>
    /// Gets or sets the registry Path.
    /// </summary>
    public string RegistryPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the backend Registry Path.
    /// </summary>
    public string BackendRegistryPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the source Root Path.
    /// </summary>
    public string SourceRootPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the command Text.
    /// </summary>
    public string? CommandText { get; init; }

    /// <summary>
    /// Gets or sets the handler Clsid.
    /// </summary>
    public string? HandlerClsid { get; init; }

    /// <summary>
    /// Gets or sets the icon Path.
    /// </summary>
    public string? IconPath { get; init; }

    /// <summary>
    /// Gets or sets the icon Index.
    /// </summary>
    public int IconIndex { get; init; }

    /// <summary>
    /// Gets or sets the file Path.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether windows11 Context Menu.
    /// </summary>
    public bool IsWindows11ContextMenu { get; init; }

    /// <summary>
    /// Gets or sets the only With Shift.
    /// </summary>
    public bool OnlyWithShift { get; init; }

    /// <summary>
    /// Gets or sets the only In Explorer.
    /// </summary>
    public bool OnlyInExplorer { get; init; }

    /// <summary>
    /// Gets or sets the no Working Directory.
    /// </summary>
    public bool NoWorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the never Default.
    /// </summary>
    public bool NeverDefault { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether as Disabled If Hidden.
    /// </summary>
    public bool ShowAsDisabledIfHidden { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether enabled.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether present In Registry.
    /// </summary>
    public bool IsPresentInRegistry { get; init; } = true;

    /// <summary>
    /// Gets or sets the notes.
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether deleted.
    /// </summary>
    public bool IsDeleted { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether pending Approval.
    /// </summary>
    public bool IsPendingApproval { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether backup.
    /// </summary>
    public bool HasBackup { get; init; }

    /// <summary>
    /// Gets or sets the deleted At Utc.
    /// </summary>
    public DateTimeOffset? DeletedAtUtc { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether consistency Issue.
    /// </summary>
    public bool HasConsistencyIssue { get; init; }

    /// <summary>
    /// Gets or sets the consistency Issue.
    /// </summary>
    public string? ConsistencyIssue { get; init; }

    /// <summary>
    /// Gets or sets the detected Change Kind.
    /// </summary>
    public ContextMenuChangeKind DetectedChangeKind { get; init; }

    /// <summary>
    /// Gets or sets the detected Change Details.
    /// </summary>
    public string? DetectedChangeDetails { get; init; }
}
