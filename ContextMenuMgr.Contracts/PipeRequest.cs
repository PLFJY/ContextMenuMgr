namespace ContextMenuMgr.Contracts;

public sealed record PipeRequest
{
    public PipeCommand Command { get; init; }

    public string? ItemId { get; init; }

    public bool? Enable { get; init; }

    public ContextMenuShellAttribute? ShellAttribute { get; init; }

    public string? TextValue { get; init; }

    public string? DefinitionXml { get; init; }

    public ContextMenuSceneKind? SceneKind { get; init; }

    public string? ScopeValue { get; init; }

    public ContextMenuDecision? Decision { get; init; }
}
