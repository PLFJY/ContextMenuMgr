namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the pipe Request.
/// </summary>
public sealed record PipeRequest
{
    /// <summary>
    /// Gets or sets the command.
    /// </summary>
    public PipeCommand Command { get; init; }

    /// <summary>
    /// Gets or sets the item Id.
    /// </summary>
    public string? ItemId { get; init; }

    /// <summary>
    /// Gets or sets the enable.
    /// </summary>
    public bool? Enable { get; init; }

    /// <summary>
    /// Gets or sets the shell Attribute.
    /// </summary>
    public ContextMenuShellAttribute? ShellAttribute { get; init; }

    /// <summary>
    /// Gets or sets the text Value.
    /// </summary>
    public string? TextValue { get; init; }

    /// <summary>
    /// Gets or sets the definition Xml.
    /// </summary>
    public string? DefinitionXml { get; init; }

    /// <summary>
    /// Gets or sets the scene Kind.
    /// </summary>
    public ContextMenuSceneKind? SceneKind { get; init; }

    /// <summary>
    /// Gets or sets the scope Value.
    /// </summary>
    public string? ScopeValue { get; init; }

    /// <summary>
    /// Gets or sets the decision.
    /// </summary>
    public ContextMenuDecision? Decision { get; init; }
}
