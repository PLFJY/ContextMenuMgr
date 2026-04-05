using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

internal static class ShellMetadataResolver
{
    private static readonly Dictionary<string, int> DefaultVerbNameIndexes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = 8496,
        ["edit"] = 8516,
        ["print"] = 8497,
        ["find"] = 8503,
        ["play"] = 8498,
        ["runas"] = 8505,
        ["explore"] = 8502,
        ["preview"] = 8499
    };

    private static readonly string[] ClsidRoots =
    [
        @"CLSID",
        @"WOW6432Node\CLSID"
    ];

    public static string ResolveVerbDisplayName(RegistryKey itemKey, string fallbackKeyName)
    {
        foreach (var valueName in new[] { "MUIVerb", string.Empty })
        {
            var resolved = ResolveIndirectString(itemKey.GetValue(valueName)?.ToString());
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        if (DefaultVerbNameIndexes.TryGetValue(fallbackKeyName, out var index))
        {
            var resolved = ResolveIndirectString($"@windows.storage.dll,-{index}");
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return fallbackKeyName;
    }

    public static string ResolveShellExtensionDisplayName(string keyName, string? handlerClsid)
    {
        if (Guid.TryParse(handlerClsid, out var handlerGuid))
        {
            var dictionaryName = GuidMetadataCatalog.GetDisplayName(handlerGuid);
            if (!string.IsNullOrWhiteSpace(dictionaryName))
            {
                return dictionaryName;
            }
        }

        if (!string.IsNullOrWhiteSpace(handlerClsid))
        {
            foreach (var clsidKey in EnumerateClsidKeys(handlerClsid))
            {
                using (clsidKey)
                {
                    foreach (var valueName in new[] { "LocalizedString", "InfoTip", string.Empty })
                    {
                        var resolved = ResolveIndirectString(clsidKey.GetValue(valueName)?.ToString());
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            return resolved;
                        }
                    }
                }
            }
        }

        return keyName;
    }

    public static string ResolveResourceString(string? value)
    {
        return ResolveIndirectString(value) ?? value?.Trim() ?? string.Empty;
    }

    public static (string? IconPath, int IconIndex) ResolveVerbIcon(RegistryKey itemKey, string? commandText)
    {
        var iconValue = itemKey.GetValue("Icon")?.ToString();
        if (TryParseIconLocation(iconValue, out var iconPath, out var iconIndex))
        {
            return (iconPath, iconIndex);
        }

        if (itemKey.GetValue("HasLUAShield") is not null)
        {
            return ("imageres.dll", -78);
        }

        var handlerGuid = ExtractVerbHandlerGuid(itemKey);
        if (handlerGuid.HasValue)
        {
            var guidLocation = GuidMetadataCatalog.GetIconLocation(handlerGuid.Value);
            if (!string.IsNullOrWhiteSpace(guidLocation.IconPath))
            {
                return guidLocation;
            }

            var guidFilePath = GuidMetadataCatalog.GetFilePath(handlerGuid.Value);
            if (!string.IsNullOrWhiteSpace(guidFilePath))
            {
                return (guidFilePath, 0);
            }
        }

        var commandPath = ExtractExecutablePath(commandText);
        if (!string.IsNullOrWhiteSpace(commandPath))
        {
            return (commandPath, 0);
        }

        // Some built-in verbs do not declare an Icon value, but their MUIVerb
        // already points at the module that owns the UI resources.
        var muiVerbPath = ExtractResourceModulePath(itemKey.GetValue("MUIVerb")?.ToString());
        if (!string.IsNullOrWhiteSpace(muiVerbPath))
        {
            return (muiVerbPath, 0);
        }

        return ("imageres.dll", -2);
    }

    public static string? ResolveVerbFilePath(RegistryKey itemKey, string? commandText)
    {
        var handlerGuid = ExtractVerbHandlerGuid(itemKey);
        if (handlerGuid.HasValue)
        {
            var guidFilePath = GuidMetadataCatalog.GetFilePath(handlerGuid.Value);
            if (!string.IsNullOrWhiteSpace(guidFilePath))
            {
                return guidFilePath;
            }
        }

        var commandPath = ExtractExecutablePath(commandText);
        if (!string.IsNullOrWhiteSpace(commandPath))
        {
            return commandPath;
        }

        return ExtractResourceModulePath(itemKey.GetValue("MUIVerb")?.ToString());
    }

    public static (string? IconPath, int IconIndex) ResolveShellExtensionIcon(string? handlerClsid)
    {
        if (Guid.TryParse(handlerClsid, out var handlerGuid))
        {
            var guidLocation = GuidMetadataCatalog.GetIconLocation(handlerGuid);
            if (!string.IsNullOrWhiteSpace(guidLocation.IconPath))
            {
                return guidLocation;
            }

            var guidFilePath = GuidMetadataCatalog.GetFilePath(handlerGuid);
            if (!string.IsNullOrWhiteSpace(guidFilePath))
            {
                return (guidFilePath, 0);
            }
        }

        if (string.IsNullOrWhiteSpace(handlerClsid))
        {
            return (null, 0);
        }

        foreach (var clsidKey in EnumerateClsidKeys(handlerClsid))
        {
            using (clsidKey)
            {
                using var defaultIconKey = clsidKey.OpenSubKey("DefaultIcon");
                if (TryParseIconLocation(defaultIconKey?.GetValue(string.Empty)?.ToString(), out var iconPath, out var iconIndex))
                {
                    return (iconPath, iconIndex);
                }

                foreach (var subKeyName in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var moduleKey = clsidKey.OpenSubKey(subKeyName);
                    var modulePath = moduleKey?.GetValue("CodeBase")?.ToString()
                        ?.Replace("file:///", string.Empty, StringComparison.OrdinalIgnoreCase)
                        .Replace('/', '\\');

                    modulePath ??= ExtractExecutablePath(moduleKey?.GetValue(string.Empty)?.ToString());
                    if (!string.IsNullOrWhiteSpace(modulePath))
                    {
                        return (modulePath, 0);
                    }
                }
            }
        }

        return ("imageres.dll", -2);
    }

    public static string? ResolveShellExtensionFilePath(string? handlerClsid)
    {
        if (Guid.TryParse(handlerClsid, out var handlerGuid))
        {
            var guidFilePath = GuidMetadataCatalog.GetFilePath(handlerGuid);
            if (!string.IsNullOrWhiteSpace(guidFilePath))
            {
                return guidFilePath;
            }
        }

        if (string.IsNullOrWhiteSpace(handlerClsid))
        {
            return null;
        }

        foreach (var clsidKey in EnumerateClsidKeys(handlerClsid))
        {
            using (clsidKey)
            {
                foreach (var subKeyName in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var moduleKey = clsidKey.OpenSubKey(subKeyName);
                    var modulePath = moduleKey?.GetValue("CodeBase")?.ToString()
                        ?.Replace("file:///", string.Empty, StringComparison.OrdinalIgnoreCase)
                        .Replace('/', '\\');

                    modulePath ??= ExtractExecutablePath(moduleKey?.GetValue(string.Empty)?.ToString());
                    if (!string.IsNullOrWhiteSpace(modulePath))
                    {
                        return modulePath;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<RegistryKey> EnumerateClsidKeys(string handlerClsid)
    {
        foreach (var root in ClsidRoots)
        {
            var key = Registry.ClassesRoot.OpenSubKey($@"{root}\{NormalizeGuid(handlerClsid)}", writable: false);
            if (key is not null)
            {
                yield return key;
            }
        }
    }

    private static string NormalizeGuid(string guid)
    {
        return Guid.TryParse(guid, out var parsed)
            ? parsed.ToString("B")
            : guid;
    }

    private static Guid? ExtractVerbHandlerGuid(RegistryKey itemKey)
    {
        using var commandKey = itemKey.OpenSubKey("command", writable: false);
        using var dropTargetKey = itemKey.OpenSubKey("DropTarget", writable: false);
        var candidates = new[]
        {
            commandKey?.GetValue("DelegateExecute")?.ToString(),
            dropTargetKey?.GetValue("CLSID")?.ToString(),
            itemKey.GetValue("ExplorerCommandHandler")?.ToString()
        };

        foreach (var candidate in candidates)
        {
            if (Guid.TryParse(candidate, out var guid))
            {
                return guid;
            }
        }

        return null;
    }

    private static string? ResolveIndirectString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('@') && !trimmed.Contains("ms-resource", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var capacity = 1024;
        var builder = new StringBuilder(capacity);
        var hr = SHLoadIndirectString(trimmed, builder, builder.Capacity, nint.Zero);
        if (hr == 0)
        {
            var resolved = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }

        return null;
    }

    private static bool TryParseIconLocation(string? value, out string? iconPath, out int iconIndex)
    {
        iconPath = null;
        iconIndex = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var resolved = ResolveIndirectString(value)?.Trim();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return false;
        }

        if (resolved.StartsWith('@'))
        {
            resolved = resolved[1..].Trim();
        }

        var commaIndex = resolved.LastIndexOf(',');
        if (commaIndex > 0 && int.TryParse(resolved[(commaIndex + 1)..].Trim(), out var parsedIndex))
        {
            iconPath = Environment.ExpandEnvironmentVariables(resolved[..commaIndex].Trim().Trim('"'));
            iconIndex = parsedIndex;
            return !string.IsNullOrWhiteSpace(iconPath);
        }

        iconPath = Environment.ExpandEnvironmentVariables(resolved.Trim().Trim('"'));
        return !string.IsNullOrWhiteSpace(iconPath);
    }

    private static string? ExtractExecutablePath(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(commandText.Trim());
        var trimmed = expanded.Trim('"');
        if (File.Exists(trimmed))
        {
            return trimmed;
        }

        if (expanded.StartsWith('"'))
        {
            var closingQuoteIndex = expanded.IndexOf('"', 1);
            if (closingQuoteIndex > 1)
            {
                var quoted = expanded[1..closingQuoteIndex];
                return File.Exists(quoted) ? quoted : quoted;
            }
        }

        foreach (var extension in new[] { ".dll", ".exe", ".cpl", ".msc" })
        {
            var extensionIndex = expanded.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (extensionIndex > 0)
            {
                var candidate = expanded[..(extensionIndex + extension.Length)].Trim().Trim('"');
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var firstToken = expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstToken) ? null : firstToken.Trim('"');
    }

    private static string? ExtractResourceModulePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (candidate.StartsWith('@'))
        {
            candidate = candidate[1..];
        }

        var commaIndex = candidate.IndexOf(',');
        if (commaIndex > 0)
        {
            candidate = candidate[..commaIndex];
        }

        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (!Path.IsPathRooted(candidate))
        {
            var systemCandidates = new[]
            {
                Path.Combine(Environment.SystemDirectory, candidate),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", candidate),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", candidate),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), candidate)
            };

            return systemCandidates.FirstOrDefault(File.Exists);
        }

        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        int cchOutBuf,
        nint ppvReserved);
}
