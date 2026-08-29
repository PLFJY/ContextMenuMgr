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
    /// Gets or sets the item payload used when a command targets a scene-only item.
    /// </summary>
    public ContextMenuEntry? Item { get; init; }

    /// <summary>Trusted top-level parent identity used by shell submenu operations.</summary>
    public string? ParentItemId { get; init; }

    /// <summary>Stable child identity used by shell submenu operations.</summary>
    public string? SubMenuItemId { get; init; }

    /// <summary>
    /// Gets or sets the shell Attribute.
    /// </summary>
    public ContextMenuShellAttribute? ShellAttribute { get; init; }

    /// <summary>
    /// Gets or sets the text Value.
    /// </summary>
    public string? TextValue { get; init; }

    /// <summary>
    /// Gets or sets the rule storage kind.
    /// </summary>
    public string? RuleStorageKind { get; init; }

    /// <summary>
    /// Gets or sets the rule path.
    /// </summary>
    public string? RulePath { get; init; }

    /// <summary>
    /// Gets or sets the INI section for rule values.
    /// </summary>
    public string? RuleSection { get; init; }

    /// <summary>
    /// Gets or sets the rule key name.
    /// </summary>
    public string? RuleKeyName { get; init; }

    /// <summary>
    /// Gets or sets the rule registry value kind.
    /// </summary>
    public string? RuleValueKind { get; init; }

    /// <summary>
    /// Gets or sets the rule value. Null means delete/reset.
    /// </summary>
    public string? RuleValue { get; init; }

    /// <summary>
    /// Gets or sets the interactive user SID for HKCU registry writes requested by the frontend.
    /// </summary>
    public string? RuleUserSid { get; init; }

    /// <summary>
    /// Gets or sets the definition Xml.
    /// </summary>
    public string? DefinitionXml { get; init; }

    /// <summary>
    /// Gets or sets the UI culture name.
    /// </summary>
    public string? CultureName { get; init; }

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

    /// <summary>
    /// Gets or sets the client operation id used to suppress duplicate local notifications.
    /// </summary>
    public Guid? ClientOperationId { get; init; }

    /// <summary>
    /// Gets or sets the special menu kind.
    /// </summary>
    public SpecialMenuKind? SpecialKind { get; init; }

    /// <summary>
    /// Gets or sets the special menu item.
    /// </summary>
    public SpecialMenuEntry? SpecialItem { get; init; }

    public ShellNewCreateRequest? ShellNewCreate { get; init; }

    public ShellNewUpdateRequest? ShellNewUpdate { get; init; }

    public ShellNewSortRequest? ShellNewSort { get; init; }

    public ShellNewLockRequest? ShellNewLock { get; init; }

    public SendToCreateRequest? SendToCreate { get; init; }

    public SendToUpdateRequest? SendToUpdate { get; init; }

    public WinXCreateGroupRequest? WinXCreateGroup { get; init; }

    public WinXCreateEntryRequest? WinXCreateEntry { get; init; }

    public WinXUpdateEntryRequest? WinXUpdateEntry { get; init; }

    public WinXMoveRequest? WinXMove { get; init; }

    public OpenWithCreateRequest? OpenWithCreate { get; init; }

    public OpenWithUpdateRequest? OpenWithUpdate { get; init; }

    public DragDropCreateRequest? DragDropCreate { get; init; }

    public GuidBlockCreateRequest? GuidBlockCreate { get; init; }

    public IeMenuCreateRequest? IeMenuCreate { get; init; }

    public IeMenuUpdateRequest? IeMenuUpdate { get; init; }

    public FileTypeAnalysisRequest? FileTypeAnalysis { get; init; }

    public CreateSceneMenuItemRequest? CreateSceneMenuItem { get; init; }

    public FileTypeBatchQuery? FileTypeBatchQuery { get; init; }

    public DefaultDropEffect? DefaultDropEffect { get; init; }

    /// <summary>
    /// Gets or sets the display name for Win11 blocked items.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets or sets whether to block/unblock at machine level.
    /// </summary>
    public bool? BlockMachine { get; init; }

    /// <summary>
    /// Gets or sets whether to unblock at machine level.
    /// </summary>
    public bool? UnblockMachine { get; init; }

    /// <summary>
    /// Gets or sets whether auto-start is enabled.
    /// </summary>
    public bool? AutoStartEnabled { get; init; }

    /// <summary>
    /// Gets or sets whether the TrayHost notification-area icon should be visible.
    /// </summary>
    public bool? ShowTrayIcon { get; init; }

    /// <summary>
    /// Gets or sets the shared runtime log level.
    /// </summary>
    public RuntimeLogLevel? LogLevel { get; init; }

    public DocumentIconProvider? DocumentIconProvider { get; init; }

}
