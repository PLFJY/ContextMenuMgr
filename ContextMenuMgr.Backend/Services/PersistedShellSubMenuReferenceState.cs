namespace ContextMenuMgr.Backend.Services;

/// <summary>Restore metadata for a reference removed from one parent's SubCommands value.</summary>
public sealed class PersistedShellSubMenuReferenceState
{
    public string ReferenceName { get; set; } = string.Empty;
    public int OriginalIndex { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
