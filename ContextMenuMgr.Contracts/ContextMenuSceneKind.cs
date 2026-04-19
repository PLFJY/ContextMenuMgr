namespace ContextMenuMgr.Contracts;

/// <summary>
/// Defines the available context Menu Scene Kind values.
/// </summary>
public enum ContextMenuSceneKind
{
    LnkFile = 0,
    UwpShortcut = 1,
    ExeFile = 2,
    CustomExtension = 3,
    PerceivedType = 4,
    DirectoryType = 5,
    UnknownType = 6,
    CustomRegistryPath = 7
}
