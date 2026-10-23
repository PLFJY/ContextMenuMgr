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
            if (entry.Windows11SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore)
            {
                return string.Join("|", "win11-system", entry.KeyName);
            }

            return CreateWindows11LogicalItemKey(
                entry.Windows11PackageFamilyName ?? ExtractWin11PackageKey(entry.RegistryPath),
                entry.HandlerClsid);
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
    public static string CreateWindows11LogicalItemKey(string? packageKey, string? handlerClsid)
        => string.Join("|", "win11", packageKey ?? string.Empty,
            Guid.TryParse(handlerClsid, out var clsid) ? clsid.ToString("B") : handlerClsid ?? string.Empty);

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
