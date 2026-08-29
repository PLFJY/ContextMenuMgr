namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the pipe Response.
/// </summary>
public sealed record PipeResponse
{
    /// <summary>
    /// Gets or sets the success.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the structured error code.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Gets or sets the items.
    /// </summary>
    public IReadOnlyList<ContextMenuEntry> Items { get; init; } = [];

    /// <summary>
    /// Gets or sets the item.
    /// </summary>
    public ContextMenuEntry? Item { get; init; }

    public IReadOnlyList<ShellSubMenuItem> ShellSubMenuItems { get; init; } = [];

    public ShellSubMenuItem? ShellSubMenuItem { get; init; }

    /// <summary>
    /// Gets or sets the registry Protection Enabled.
    /// </summary>
    public bool? RegistryProtectionEnabled { get; init; }

    /// <summary>
    /// Gets or sets the special menu items.
    /// </summary>
    public IReadOnlyList<SpecialMenuEntry> SpecialItems { get; init; } = [];

    /// <summary>
    /// Gets or sets the special menu item.
    /// </summary>
    public SpecialMenuEntry? SpecialItem { get; init; }

    /// <summary>
    /// Gets or sets file type analysis results.
    /// </summary>
    public IReadOnlyList<FileTypeAnalysisResult> FileTypeAnalysisResults { get; init; } = [];

    /// <summary>
    /// Gets or sets the client operation id.
    /// </summary>
    public Guid? ClientOperationId { get; init; }

    /// <summary>
    /// Gets or sets the Win11 blocked items list.
    /// </summary>
    public IReadOnlyList<Win11BlockedItem> Win11BlockedItems { get; init; } = [];

    /// <summary>
    /// Gets or sets whether auto-start is enabled.
    /// </summary>
    public bool? AutoStartEnabled { get; init; }

    /// <summary>
    /// Gets or sets whether the Windows 11 modern context menu is disabled for the frontend user.
    /// </summary>
    public bool? Win11ModernContextMenuDisabled { get; init; }

    public OfficeSuiteCoexistenceStatus? OfficeSuiteCoexistence { get; init; }

}
