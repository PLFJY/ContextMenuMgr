namespace ContextMenuMgr.Contracts;

/// <summary>
/// Provides a stable logical identity for approval grouping and notification deduplication.
/// </summary>
public static class ContextMenuApprovalIdentity
{
    /// <summary>
    /// Creates a stable logical key for approval grouping and notification deduplication.
    /// </summary>
    public static string CreateLogicalItemKey(ContextMenuEntry entry)
    {
        if (entry.IsWindows11ContextMenu)
        {
            return CreateWindows11LogicalItemKey(
                ExtractWin11PackageKey(entry.RegistryPath),
                entry.DisplayName,
                entry.FilePath);
        }

        return string.Join("|",
            "classic",
            entry.DisplayName,
            entry.KeyName,
            entry.EntryKind.ToString(),
            entry.HandlerClsid ?? string.Empty,
            entry.CommandText ?? string.Empty,
            entry.EditableText ?? string.Empty,
            entry.FilePath ?? string.Empty);
    }

    /// <summary>
    /// Creates the logical grouping key used for Win11 packaged context-menu items.
    /// </summary>
    public static string CreateWindows11LogicalItemKey(string? packageKey, string? displayName, string? filePath)
    {
        // Win11 packaged menu items can surface under multiple categories and
        // may even use category-specific COM class IDs while still mapping to
        // the same user-visible command. Group them by package + display name
        // + module path instead of category-specific IDs.
        return string.Join("|",
            "win11",
            packageKey ?? string.Empty,
            displayName ?? string.Empty,
            filePath ?? string.Empty);
    }

    /// <summary>
    /// Extracts the packaged-COM package full name from a Win11 registry path.
    /// </summary>
    public static string ExtractWin11PackageKey(string? registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath))
        {
            return string.Empty;
        }

        const string packagedComPrefix = @"PackagedCom\Package\";
        const string classSeparator = @"\Class\";
        if (!registryPath.StartsWith(packagedComPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return registryPath;
        }

        var startIndex = packagedComPrefix.Length;
        var separatorIndex = registryPath.IndexOf(classSeparator, startIndex, StringComparison.OrdinalIgnoreCase);
        return separatorIndex > startIndex
            ? registryPath[startIndex..separatorIndex]
            : registryPath;
    }
}
