using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

internal static class GuidMetadataCatalog
{
    private static readonly Lazy<Dictionary<Guid, Dictionary<string, string>>> Entries = new(LoadEntries);
    private static readonly ConcurrentDictionary<Guid, string?> FilePathCache = new();
    private static readonly ConcurrentDictionary<Guid, string?> DisplayNameCache = new();
    private static readonly ConcurrentDictionary<Guid, (string? IconPath, int IconIndex)> IconCache = new();
    private static readonly ConcurrentDictionary<Guid, string?> UwpNameCache = new();

    private static readonly string[] RegistryClsidPaths =
    [
        @"CLSID",
        @"WOW6432Node\CLSID"
    ];

    private static readonly string[] MachineClsidPaths =
    [
        @"SOFTWARE\Classes\CLSID",
        @"SOFTWARE\WOW6432Node\Classes\CLSID"
    ];

    private static readonly string[] UserClsidPaths =
    [
        @"Software\Classes\CLSID",
        @"Software\Classes\WOW6432Node\CLSID"
    ];

    public static string? GetDisplayName(Guid guid)
    {
        return DisplayNameCache.GetOrAdd(guid, static id =>
        {
            if (TryGetDictionaryValue(id, "ResText", out var resourceText))
            {
                var resolved = ResolveIndirectString(MakeAbsolute(id, resourceText, isName: true));
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            if (TryGetDictionaryValue(id, $"{CultureInfo.CurrentUICulture.Name}-Text", out var localizedText)
                && !string.IsNullOrWhiteSpace(localizedText))
            {
                return localizedText;
            }

            if (TryGetDictionaryValue(id, "Text", out var text))
            {
                var resolved = ResolveIndirectString(text);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            foreach (var clsidKey in OpenClsidKeys(id))
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

            var filePath = GetFilePath(id);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            try
            {
                var description = FileVersionInfo.GetVersionInfo(filePath).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description;
                }
            }
            catch
            {
            }

            return Path.GetFileName(filePath);
        });
    }

    public static (string? IconPath, int IconIndex) GetIconLocation(Guid guid)
    {
        return IconCache.GetOrAdd(guid, static id =>
        {
            if (TryGetDictionaryValue(id, "Icon", out var iconValue))
            {
                var absolute = MakeAbsolute(id, iconValue, isName: false);
                if (TryParseIconLocation(absolute, out var iconPath, out var iconIndex))
                {
                    iconPath = NormalizeCandidatePath(iconPath, GetFilePath(id));
                    return (iconPath, iconIndex);
                }
            }

            var filePath = GetFilePath(id);
            return string.IsNullOrWhiteSpace(filePath)
                ? (null, 0)
                : (filePath, 0);
        });
    }

    public static string? GetFilePath(Guid guid)
    {
        return FilePathCache.GetOrAdd(guid, static id =>
        {
            var uwpName = GetUwpName(id);
            if (!string.IsNullOrWhiteSpace(uwpName))
            {
                var uwpFilePath = UwpPackageHelper.GetFilePath(uwpName, id);
                if (!string.IsNullOrWhiteSpace(uwpFilePath))
                {
                    return uwpFilePath;
                }
            }

            foreach (var clsidKey in OpenClsidKeys(id))
            {
                using (clsidKey)
                {
                    foreach (var subKeyName in new[] { "InprocServer32", "LocalServer32" })
                    {
                        using var subKey = clsidKey.OpenSubKey(subKeyName, writable: false);
                        if (subKey is null)
                        {
                            continue;
                        }

                        var codeBase = subKey.GetValue("CodeBase")?.ToString()
                            ?.Replace("file:///", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .Replace('/', '\\');
                        if (!string.IsNullOrWhiteSpace(codeBase) && File.Exists(codeBase))
                        {
                            return codeBase;
                        }

                        var filePath = ExtractExecutablePath(subKey.GetValue(string.Empty)?.ToString());
                        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                        {
                            return filePath;
                        }
                    }
                }
            }

            return null;
        });
    }

    public static string? GetUwpName(Guid guid)
    {
        return UwpNameCache.GetOrAdd(guid, static id =>
        {
            return TryGetDictionaryValue(id, "UwpName", out var uwpName)
                ? uwpName
                : null;
        });
    }

    private static IEnumerable<RegistryKey> OpenClsidKeys(Guid guid)
    {
        var guidText = guid.ToString("B");

        foreach (var relativePath in RegistryClsidPaths)
        {
            var classesRootKey = Registry.ClassesRoot.OpenSubKey($@"{relativePath}\{guidText}", writable: false);
            if (classesRootKey is not null)
            {
                yield return classesRootKey;
            }
        }

        foreach (var machinePath in MachineClsidPaths)
        {
            var localMachineKey = Registry.LocalMachine.OpenSubKey($@"{machinePath}\{guidText}", writable: false);
            if (localMachineKey is not null)
            {
                yield return localMachineKey;
            }
        }

        foreach (var userPath in UserClsidPaths)
        {
            var currentUserKey = Registry.CurrentUser.OpenSubKey($@"{userPath}\{guidText}", writable: false);
            if (currentUserKey is not null)
            {
                yield return currentUserKey;
            }
        }
    }

    private static bool TryGetDictionaryValue(Guid guid, string key, out string value)
    {
        value = string.Empty;
        if (Entries.Value.TryGetValue(guid, out var section) && section.TryGetValue(key, out var storedValue))
        {
            value = storedValue.Trim();
            return !string.IsNullOrWhiteSpace(value);
        }
        return false;
    }

    private static Dictionary<Guid, Dictionary<string, string>> LoadEntries()
    {
        var result = new Dictionary<Guid, Dictionary<string, string>>();
        var filePath = Path.Combine(AppContext.BaseDirectory, "Resources", "GuidInfosDic.ini");
        if (!File.Exists(filePath))
        {
            return result;
        }

        Dictionary<string, string>? currentSection = null;
        foreach (var rawLine in File.ReadLines(filePath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var sectionName = line[1..^1];
                if (Guid.TryParse(sectionName, out var guid))
                {
                    currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[guid] = currentSection;
                }
                else
                {
                    currentSection = null;
                }

                continue;
            }

            if (currentSection is null)
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();
            currentSection[key] = value;
        }

        return result;
    }

    private static string MakeAbsolute(Guid guid, string value, bool isName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var result = value.Trim();
        if (isName && !result.StartsWith('@'))
        {
            return result;
        }

        if (isName)
        {
            result = result[1..];
            if (result.StartsWith("{*?ms-resource://", StringComparison.OrdinalIgnoreCase)
                && result.EndsWith('}'))
            {
                var packageName = UwpPackageHelper.GetPackageName(GetUwpName(guid));
                if (!string.IsNullOrWhiteSpace(packageName))
                {
                    result = "@{" + packageName + result[2..];
                    return result;
                }
            }
        }

        result = NormalizeCandidatePath(result, GetFilePath(guid));

        return isName ? "@" + result : result;
    }

    public static string NormalizeCandidatePath(string? candidate, string? baseFilePath)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return candidate ?? string.Empty;
        }

        var normalized = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        if (normalized.StartsWith('@'))
        {
            normalized = normalized[1..].Trim();
        }

        if (normalized.StartsWith('*') && !string.IsNullOrWhiteSpace(baseFilePath))
        {
            normalized = baseFilePath + normalized[1..];
        }
        else if (!Path.IsPathRooted(normalized) && !string.IsNullOrWhiteSpace(baseFilePath))
        {
            var baseDirectory = Path.GetDirectoryName(baseFilePath);
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                try
                {
                    normalized = Path.GetFullPath(Path.Combine(baseDirectory, normalized));
                }
                catch
                {
                }
            }
        }

        return normalized;
    }

    private static bool TryParseIconLocation(string? value, out string? iconPath, out int iconIndex)
    {
        iconPath = null;
        iconIndex = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (expanded.StartsWith('@'))
        {
            expanded = expanded[1..].Trim();
        }

        var commaIndex = expanded.LastIndexOf(',');
        if (commaIndex > 0 && int.TryParse(expanded[(commaIndex + 1)..].Trim(), out var parsedIndex))
        {
            iconPath = expanded[..commaIndex].Trim().Trim('"');
            iconIndex = parsedIndex;
            return !string.IsNullOrWhiteSpace(iconPath);
        }

        iconPath = expanded;
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
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return expanded[1..closingQuote];
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

        var buffer = new StringBuilder(1024);
        var hr = SHLoadIndirectString(trimmed, buffer, buffer.Capacity, IntPtr.Zero);
        if (hr == 0)
        {
            var resolved = buffer.ToString().Trim();
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }

        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        int cchOutBuf,
        IntPtr ppvReserved);
}
