using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Defines the available rule Storage Kind values.
/// </summary>
public enum RuleStorageKind
{
    Registry,
    Ini
}

/// <summary>
/// Defines the available rule Value Editor Kind values.
/// </summary>
public enum RuleValueEditorKind
{
    Boolean,
    Number,
    String
}

/// <summary>
/// Represents the enhance Menu Item Definition.
/// </summary>
public sealed record EnhanceMenuItemDefinition(
    string GroupRegistryPath,
    string DisplayName,
    string Kind,
    string KeyName,
    string? GuidText,
    string? Tip,
    string? IconPath,
    string RawXml);

/// <summary>
/// Represents the enhance Menu Group Definition.
/// </summary>
public sealed record EnhanceMenuGroupDefinition(
    string Title,
    string RegistryPath,
    string? IconPath,
    IReadOnlyList<EnhanceMenuItemDefinition> Items);

/// <summary>
/// Represents the detailed Edit Rule Clause Definition.
/// </summary>
public sealed record DetailedEditRuleClauseDefinition(
    RuleStorageKind StorageKind,
    string Path,
    string? Section,
    string KeyName,
    RegistryValueKind ValueKind,
    string? TurnOnValue,
    string? TurnOffValue);

/// <summary>
/// Represents the detailed Edit Rule Definition.
/// </summary>
public sealed record DetailedEditRuleDefinition(
    string DisplayName,
    string? Tip,
    RuleValueEditorKind EditorKind,
    bool RestartExplorer,
    int DefaultNumber,
    int MinNumber,
    int MaxNumber,
    IReadOnlyList<DetailedEditRuleClauseDefinition> Clauses);

/// <summary>
/// Represents the detailed Edit Group Definition.
/// </summary>
public sealed record DetailedEditGroupDefinition(
    string Title,
    string? RegistryPath,
    string? FilePath,
    bool IsIniGroup,
    bool IsAvailable,
    IReadOnlyList<DetailedEditRuleDefinition> Rules);
