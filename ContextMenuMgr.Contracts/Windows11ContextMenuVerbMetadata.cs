namespace ContextMenuMgr.Contracts;

/// <summary>
/// Preserves one file Explorer context-menu verb declaration from an AppX/MSIX manifest.
/// The manifest identifier is metadata and is not necessarily the title shown by Explorer.
/// </summary>
public sealed record Windows11ContextMenuVerbMetadata
{
    public string Id { get; init; } = string.Empty;

    public string HandlerClsid { get; init; } = string.Empty;

    public string ContextType { get; init; } = string.Empty;
}
