namespace ContextMenuMgr.Contracts;

public enum ShellSubMenuSourceKind
{
    SubCommands,
    ExtendedSubCommandsKey,
    ParentShell
}

/// <summary>One registry-defined child of a cascading classic shell verb.</summary>
public sealed record ShellSubMenuItem
{
    public string Id { get; init; } = string.Empty;
    public string ParentId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string KeyName { get; init; } = string.Empty;
    public string? ReferenceName { get; init; }
    public string? CommandText { get; init; }
    public string? IconPath { get; init; }
    public int IconIndex { get; init; }
    public bool IsEnabled { get; init; }
    public bool CanToggle { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsShared { get; init; }
    public ShellSubMenuSourceKind SourceKind { get; init; }
}
