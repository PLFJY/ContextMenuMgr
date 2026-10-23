namespace ContextMenuMgr.Contracts;

/// <summary>Observed registration references, not a guarantee of Explorer runtime isolation.</summary>
public sealed record Windows11ClsidImpact
{
    public bool IsComplete { get; init; }
    public IReadOnlyList<Windows11ClsidReference> References { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public bool AllowsAutomaticDisable => IsComplete
        && !References.Any(static reference => reference.Kind == "Classic")
        && References.Select(static reference => reference.PackageFamilyName)
            .Where(static family => !string.IsNullOrWhiteSpace(family))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
}

public sealed record Windows11ClsidReference
{
    public string Kind { get; init; } = string.Empty;
    public string? PackageFullName { get; init; }
    public string? PackageDisplayName { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? VerbId { get; init; }
    public string? ContextType { get; init; }
    public string? ComServerPath { get; init; }
    public string? RegistryPath { get; init; }
}
