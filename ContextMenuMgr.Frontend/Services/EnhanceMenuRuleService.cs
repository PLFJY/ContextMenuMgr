using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public sealed class EnhanceMenuRuleService
{
    private readonly IBackendClient _backendClient;

    public EnhanceMenuRuleService(IBackendClient backendClient)
    {
        _backendClient = backendClient;
    }

    public bool IsEnabled(EnhanceMenuItemDefinition definition)
    {
        var relativeGroupPath = NormalizeClassesRootRelativePath(definition.GroupRegistryPath);
        if (string.IsNullOrWhiteSpace(relativeGroupPath))
        {
            return false;
        }

        return string.Equals(definition.Kind, "ShellEx", StringComparison.OrdinalIgnoreCase)
            ? IsShellExEnabled(relativeGroupPath, definition)
            : IsShellEnabled(relativeGroupPath, definition);
    }

    public Task SetEnabledAsync(
        EnhanceMenuItemDefinition definition,
        bool enable,
        CancellationToken cancellationToken)
    {
        return _backendClient.SetEnhanceMenuItemEnabledAsync(
            definition.GroupRegistryPath,
            definition.RawXml,
            enable,
            cancellationToken);
    }

    private static bool IsShellEnabled(string relativeGroupPath, EnhanceMenuItemDefinition definition)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{relativeGroupPath}\shell\{definition.KeyName}", writable: false);
        return key is not null;
    }

    private static bool IsShellExEnabled(string relativeGroupPath, EnhanceMenuItemDefinition definition)
    {
        if (!Guid.TryParse(definition.GuidText, out var expectedGuid))
        {
            return false;
        }

        using var handlersKey = Registry.ClassesRoot.OpenSubKey($@"{relativeGroupPath}\shellex\ContextMenuHandlers", writable: false);
        if (handlersKey is null)
        {
            return false;
        }

        foreach (var subKeyName in handlersKey.GetSubKeyNames())
        {
            using var subKey = handlersKey.OpenSubKey(subKeyName, writable: false);
            var value = subKey?.GetValue(null)?.ToString();
            if (Guid.TryParse(value, out var actualGuid) && actualGuid == expectedGuid)
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeClassesRootRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim().Replace('/', '\\').Trim('\\');
        const string longPrefix = @"HKEY_CLASSES_ROOT\";
        const string shortPrefix = @"HKCR\";

        if (normalized.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[longPrefix.Length..];
        }
        else if (normalized.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[shortPrefix.Length..];
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
