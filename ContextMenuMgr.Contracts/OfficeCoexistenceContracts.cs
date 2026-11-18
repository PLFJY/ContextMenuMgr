namespace ContextMenuMgr.Contracts;

// Retained only to decode the released SetDocumentIconProvider wire field.
// That command is rejected; callers must use reversible SetDocumentIconRepair.
public enum DocumentIconProvider
{
    MicrosoftOffice = 0,
    WpsOffice = 1
}

public enum ProtectedDocumentGroup
{
    Word,
    Excel,
    PowerPoint,
    Pdf,
    Image,
    Ebook
}

public sealed record ProtectedDocumentAssociation
{
    public string Extension { get; init; } = string.Empty;

    public ProtectedDocumentGroup Group { get; init; }

    public IReadOnlyList<string> MicrosoftProgIds { get; init; } = [];

    public IReadOnlyList<string> WpsProgIdPrefixes { get; init; } = [];

    public bool SupportsIconRepair { get; init; }

    public string? CurrentOwner { get; init; }

    public string? CurrentProgId { get; init; }
}

public sealed record OfficeSuiteCoexistenceStatus
{
    public bool IsWpsInstalled { get; init; }

    public bool IsMicrosoftOfficeInstalled { get; init; }

    public bool IsCoexistenceActive { get; init; }

    public IReadOnlyList<string> WpsIconSourceCandidates { get; init; } = [];

    public IReadOnlyList<string> MicrosoftOfficeIconSourceCandidates { get; init; } = [];

    public IReadOnlyList<ProtectedDocumentAssociation> ProtectedAssociations { get; init; } = [];

    public bool? IsDocumentIconRepairEnabled { get; init; }

    public bool IsDocumentIconRepairApplied { get; init; }

    public bool? CanRestoreDocumentIcons { get; init; }
}
