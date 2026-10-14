namespace ContextMenuMgr.Contracts;

/// <summary>
/// Defines special menu surfaces that are not represented by the classic context menu catalog.
/// </summary>
public enum SpecialMenuKind
{
    ShellNew,
    SendTo,
    WinX,
    OpenWith,
    DragDrop,
    CommandStore,
    GuidBlock,
    InternetExplorer
}

/// <summary>
/// Defines default right-button drag drop effects.
/// </summary>
public enum DefaultDropEffect
{
    Default = 0,
    Copy = 1,
    Move = 2,
    CreateLink = 4
}

/// <summary>
/// Defines scene menu item creation kinds.
/// </summary>
public enum SceneMenuItemKind
{
    ShellVerb,
    ShellExtension
}

/// <summary>
/// Represents a special menu entry.
/// </summary>
public sealed record SpecialMenuEntry
{
    public string Id { get; init; } = string.Empty;

    public SpecialMenuKind Kind { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string KeyName { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }

    public string? IconPath { get; init; }

    public int IconIndex { get; init; }

    public string? Path { get; init; }

    public string? RegistryPath { get; init; }

    public string? CommandText { get; init; }

    public string? TargetPath { get; init; }

    public string? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? GroupName { get; init; }

    public string? Notes { get; init; }

    public bool CanEdit { get; init; } = true;

    public bool CanDelete { get; init; } = true;

    public bool CanMove { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record ShellNewCreateRequest(
    string Extension,
    string? DisplayName = null,
    string? IconPath = null,
    string? Command = null,
    string? DataText = null,
    bool BeforeSeparator = false);

public sealed record ShellNewUpdateRequest(
    string Id,
    string? DisplayName = null,
    string? IconPath = null,
    string? Command = null,
    string? DataText = null,
    bool? BeforeSeparator = null);

public sealed record ShellNewSortRequest(string Id, bool MoveUp);

public sealed record ShellNewLockRequest(bool Lock);

public sealed record SendToCreateRequest(
    string DisplayName,
    string TargetPath,
    string? Arguments = null,
    string? WorkingDirectory = null);

public sealed record SendToUpdateRequest(
    string Id,
    string? DisplayName = null,
    string? TargetPath = null,
    string? Arguments = null,
    string? WorkingDirectory = null,
    string? IconPath = null,
    bool? RunAsAdministrator = null);

public sealed record WinXCreateGroupRequest(string GroupName);

public sealed record WinXCreateEntryRequest(
    string DisplayName,
    string TargetPath,
    string GroupName,
    string? Arguments = null,
    string? WorkingDirectory = null,
    string? IconPath = null,
    bool? RunAsAdministrator = null);

public sealed record WinXUpdateEntryRequest(
    string Id,
    string? DisplayName = null,
    string? TargetPath = null,
    string? Arguments = null,
    string? WorkingDirectory = null,
    string? GroupName = null,
    string? IconPath = null,
    bool? RunAsAdministrator = null);

public sealed record WinXMoveRequest(string Id, bool MoveUp);

public sealed record OpenWithCreateRequest(
    string DisplayName,
    string TargetPath,
    string? Arguments = null);

public sealed record OpenWithUpdateRequest(
    string Id,
    string? DisplayName = null,
    string? Command = null,
    string? ExpectedEditStamp = null);

// Inspection evidence is separate from a mutable SpecialMenuEntry / switch state.
public enum OpenWithSourceKind { Applications, ClassesProgIds, ExplorerList, ExplorerProgIds, Capabilities, Package }
public enum OpenWithSourceResolution { Resolved, MultipleRegistrations, Unresolved, Invalid, Inaccessible }
public sealed record OpenWithSourceReference(
    OpenWithSourceKind Kind, string SourcePath, string ValueName,
    OpenWithSourceResolution Resolution, IReadOnlyList<string> RegistrationPaths,
    IReadOnlyList<string> ManageableRegistrationIds);
public sealed record OpenWithAssociationCandidate(string Identifier, IReadOnlyList<OpenWithSourceReference> Sources);
public sealed record OpenWithInspectionDiagnostic(string Code, string SourcePath);
public sealed record OpenWithAssociationInspection(
    string Extension, IReadOnlyList<OpenWithAssociationCandidate> Candidates, IReadOnlyList<OpenWithInspectionDiagnostic> Diagnostics);

public sealed record DragDropCreateRequest(string GuidText, string GroupName);

public sealed record GuidBlockCreateRequest(string GuidText, string? DisplayName = null);

public sealed record IeMenuCreateRequest(string DisplayName, string Command);

public sealed record IeMenuUpdateRequest(string Id, string? DisplayName = null, string? Command = null);

public sealed record FileTypeAnalysisRequest(string Path);

public sealed record FileTypeAnalysisResult
{
    public ContextMenuSceneKind SceneKind { get; init; }

    public string? ScopeValue { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? IconPath { get; init; }
}

public sealed record CreateSceneMenuItemRequest
{
    public ContextMenuSceneKind SceneKind { get; init; }

    public string? ScopeValue { get; init; }

    public SceneMenuItemKind ItemKind { get; init; }

    public string KeyName { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public string? Command { get; init; }

    public string? Icon { get; init; }

    public string? GuidText { get; init; }

    public bool Extended { get; init; }

    public bool OnlyInBrowserWindow { get; init; }

    public bool NoWorkingDirectory { get; init; }

    public bool NeverDefault { get; init; }

    public bool ShowAsDisabledIfHidden { get; init; }
}

public sealed record FileTypeBatchQuery
{
    public string SourceItemId { get; init; } = string.Empty;

    public string SourceDisplayName { get; init; } = string.Empty;

    public ContextMenuEntryKind EntryKind { get; init; }

    public string KeyName { get; init; } = string.Empty;

    public string? CommandExecutablePath { get; init; }

    public string? HandlerClsid { get; init; }

    public string? SourceRegistryPath { get; init; }

    public string? SourceBackendRegistryPath { get; init; }
}
