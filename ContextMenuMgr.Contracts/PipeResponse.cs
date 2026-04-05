namespace ContextMenuMgr.Contracts;

public sealed record PipeResponse
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<ContextMenuEntry> Items { get; init; } = [];

    public ContextMenuEntry? Item { get; init; }

    public bool? RegistryProtectionEnabled { get; init; }
}
