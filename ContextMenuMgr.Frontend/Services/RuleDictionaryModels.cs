using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public enum RuleStorageKind
{
    Registry,
    Ini
}

public enum RuleValueEditorKind
{
    Boolean,
    Number,
    String
}

public sealed record EnhanceMenuItemDefinition(
    string GroupRegistryPath,
    string DisplayName,
    string Kind,
    string KeyName,
    string? GuidText,
    string? Tip,
    string? IconPath,
    string RawXml);

public sealed record EnhanceMenuGroupDefinition(
    string Title,
    string RegistryPath,
    string? IconPath,
    IReadOnlyList<EnhanceMenuItemDefinition> Items);

public sealed record DetailedEditRuleClauseDefinition(
    RuleStorageKind StorageKind,
    string Path,
    string? Section,
    string KeyName,
    RegistryValueKind ValueKind,
    string? TurnOnValue,
    string? TurnOffValue);

public sealed record DetailedEditRuleDefinition(
    string DisplayName,
    string? Tip,
    RuleValueEditorKind EditorKind,
    bool RestartExplorer,
    int DefaultNumber,
    int MinNumber,
    int MaxNumber,
    IReadOnlyList<DetailedEditRuleClauseDefinition> Clauses);

public sealed record DetailedEditGroupDefinition(
    string Title,
    string? RegistryPath,
    string? FilePath,
    bool IsIniGroup,
    bool IsAvailable,
    IReadOnlyList<DetailedEditRuleDefinition> Rules);
