using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the guid Metadata Catalog.
/// </summary>
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

    /// <summary>
    /// Gets display Name.
    /// </summary>
    public static string? GetDisplayName(Guid guid) => GetDisplayName(guid, userContext: null);

    /// <summary>
    /// Gets display Name.
    /// </summary>
    public static string? GetDisplayName(Guid guid, BackendUserContext? userContext)
    {
        if (userContext is null)
        {
            return DisplayNameCache.GetOrAdd(guid, static id => ResolveDisplayNameUncached(id, userContext: null).DisplayName);
        }

        return ResolveDisplayNameUncached(guid, userContext).DisplayName;
    }

    internal static GuidDisplayNameResolution ResolveClsidDisplayName(Guid guid, BackendUserContext? userContext = null)
    {
        var checkedRoots = GetClsidRootPaths(guid, userContext).ToArray();

        if (TryGetDictionaryValue(guid, "ResText", out var resourceText))
        {
            var resolved = ResolveIndirectString(MakeAbsolute(guid, resourceText, isName: true));
            if (IsUsefulDisplayName(resolved))
            {
                return new GuidDisplayNameResolution(resolved, "Dictionary.ResText", checkedRoots);
            }
        }

        if (TryGetDictionaryValue(guid, $"{CultureInfo.CurrentUICulture.Name}-Text", out var localizedText)
            && IsUsefulDisplayName(localizedText))
        {
            return new GuidDisplayNameResolution(localizedText, "Dictionary.LocalizedText", checkedRoots);
        }

        if (TryGetDictionaryValue(guid, "Text", out var text))
        {
            var resolved = ResolveIndirectString(text);
            if (IsUsefulDisplayName(resolved))
            {
                return new GuidDisplayNameResolution(resolved, "Dictionary.Text", checkedRoots);
            }
        }

        string? weakClsidDisplayName = null;
        string? weakClsidDisplayNameSource = null;
        foreach (var clsidKey in OpenClsidKeys(guid, userContext))
        {
            using (clsidKey.Key)
            {
                foreach (var valueName in new[] { "LocalizedString", "InfoTip", string.Empty })
                {
                    var resolved = ResolveIndirectString(clsidKey.Key.GetValue(valueName)?.ToString());
                    if (IsUsefulDisplayName(resolved))
                    {
                        var sourceValueName = string.IsNullOrEmpty(valueName) ? "Default" : valueName;
                        if (!IsWeakComClassDisplayName(resolved))
                        {
                            return new GuidDisplayNameResolution(resolved, $"CLSID.{sourceValueName}", checkedRoots);
                        }

                        weakClsidDisplayName ??= resolved;
                        weakClsidDisplayNameSource ??= $"CLSID.{sourceValueName}";
                    }
                }
            }
        }

        return new GuidDisplayNameResolution(
            weakClsidDisplayName,
            weakClsidDisplayNameSource,
            checkedRoots,
            IsWeak: weakClsidDisplayName is not null);
    }

    internal static GuidDisplayNameResolution ResolveDisplayName(Guid guid, BackendUserContext? userContext = null)
    {
        return ResolveDisplayNameUncached(guid, userContext);
    }

    private static GuidDisplayNameResolution ResolveDisplayNameUncached(Guid guid, BackendUserContext? userContext)
    {
        var clsidDisplayName = ResolveClsidDisplayName(guid, userContext);
        if (IsUsefulDisplayName(clsidDisplayName.DisplayName) && !clsidDisplayName.IsWeak)
        {
            return clsidDisplayName;
        }

        var filePath = GetFilePath(guid, userContext);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return clsidDisplayName;
        }

        var fileDisplayName = ResolveFileDisplayName(filePath, clsidDisplayName.CheckedClsidRoots);
        if (fileDisplayName is not null)
        {
            return fileDisplayName;
        }

        return IsUsefulDisplayName(clsidDisplayName.DisplayName)
            ? clsidDisplayName
            : new GuidDisplayNameResolution(null, null, clsidDisplayName.CheckedClsidRoots);
    }

    private static GuidDisplayNameResolution? ResolveFileDisplayName(
        string filePath,
        IReadOnlyList<string> checkedClsidRoots)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
            foreach (var candidate in new[]
                     {
                         ("ProductName", versionInfo.ProductName),
                         ("FileDescription", versionInfo.FileDescription)
                     })
            {
                if (IsUsefulDisplayName(candidate.Item2)
                    && !IsWeakComClassDisplayName(candidate.Item2))
                {
                    return new GuidDisplayNameResolution(candidate.Item2, candidate.Item1, checkedClsidRoots);
                }
            }
        }
        catch
        {
        }

        var fileName = Path.GetFileName(filePath);
        return IsUsefulDisplayName(fileName)
            ? new GuidDisplayNameResolution(fileName, "FileName", checkedClsidRoots)
            : null;
    }

    /// <summary>
    /// Executes static.
    /// </summary>
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

    /// <summary>
    /// Gets file Path.
    /// </summary>
    public static string? GetFilePath(Guid guid) => GetFilePath(guid, userContext: null);

    /// <summary>
    /// Gets file Path.
    /// </summary>
    public static string? GetFilePath(Guid guid, BackendUserContext? userContext)
    {
        if (userContext is null)
        {
            return FilePathCache.GetOrAdd(guid, static id => GetFilePathUncached(id, userContext: null));
        }

        return GetFilePathUncached(guid, userContext);
    }

    private static string? GetFilePathUncached(Guid guid, BackendUserContext? userContext)
    {
        var uwpName = GetUwpName(guid);
        if (!string.IsNullOrWhiteSpace(uwpName))
        {
            var uwpFilePath = UwpPackageHelper.GetFilePath(uwpName, guid);
            if (!string.IsNullOrWhiteSpace(uwpFilePath))
            {
                return uwpFilePath;
            }
        }

        foreach (var clsidKey in OpenClsidKeys(guid, userContext))
        {
            using (clsidKey.Key)
            {
                foreach (var valueName in new[] { "app_path", "core_shell_path", "origin_core_shell_path" })
                {
                    var directPath = NormalizeCandidatePath(clsidKey.Key.GetValue(valueName)?.ToString(), null);
                    if (!string.IsNullOrWhiteSpace(directPath) && File.Exists(directPath))
                    {
                        return directPath;
                    }
                }

                foreach (var subKeyName in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var subKey = clsidKey.Key.OpenSubKey(subKeyName, writable: false);
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
    }

    /// <summary>
    /// Gets uwp Name.
    /// </summary>
    public static string? GetUwpName(Guid guid)
    {
        return UwpNameCache.GetOrAdd(guid, static id =>
        {
            return TryGetDictionaryValue(id, "UwpName", out var uwpName)
                ? uwpName
                : null;
        });
    }

    internal static IEnumerable<string> GetClsidRootPaths(Guid guid, BackendUserContext? userContext = null)
    {
        var guidText = guid.ToString("B");

        foreach (var relativePath in RegistryClsidPaths)
        {
            yield return $@"HKEY_CLASSES_ROOT\{relativePath}\{guidText}";
        }

        foreach (var machinePath in MachineClsidPaths)
        {
            yield return $@"HKEY_LOCAL_MACHINE\{machinePath}\{guidText}";
        }

        if (userContext is not null)
        {
            foreach (var userPath in UserClsidPaths)
            {
                yield return $@"HKEY_USERS\{userContext.Sid}\{userPath}\{guidText}";
            }

            yield break;
        }

        foreach (var sid in EnumerateLoadedUserClassSids())
        {
            foreach (var userPath in UserClsidPaths)
            {
                yield return $@"HKEY_USERS\{sid}\{userPath}\{guidText}";
            }
        }
    }

    private static IEnumerable<ClsidRegistryKey> OpenClsidKeys(Guid guid, BackendUserContext? userContext = null)
    {
        var guidText = guid.ToString("B");

        foreach (var relativePath in RegistryClsidPaths)
        {
            var path = $@"HKEY_CLASSES_ROOT\{relativePath}\{guidText}";
            var classesRootKey = Registry.ClassesRoot.OpenSubKey($@"{relativePath}\{guidText}", writable: false);
            if (classesRootKey is not null)
            {
                yield return new ClsidRegistryKey(path, classesRootKey);
            }
        }

        foreach (var machinePath in MachineClsidPaths)
        {
            var path = $@"HKEY_LOCAL_MACHINE\{machinePath}\{guidText}";
            var localMachineKey = Registry.LocalMachine.OpenSubKey($@"{machinePath}\{guidText}", writable: false);
            if (localMachineKey is not null)
            {
                yield return new ClsidRegistryKey(path, localMachineKey);
            }
        }

        if (userContext is not null)
        {
            foreach (var userPath in UserClsidPaths)
            {
                var path = $@"HKEY_USERS\{userContext.Sid}\{userPath}\{guidText}";
                var currentUserKey = Registry.Users.OpenSubKey($@"{userContext.Sid}\{userPath}\{guidText}", writable: false);
                if (currentUserKey is not null)
                {
                    yield return new ClsidRegistryKey(path, currentUserKey);
                }
            }

            yield break;
        }

        foreach (var sid in EnumerateLoadedUserClassSids())
        {
            foreach (var userPath in UserClsidPaths)
            {
                var path = $@"HKEY_USERS\{sid}\{userPath}\{guidText}";
                var loadedUserKey = Registry.Users.OpenSubKey($@"{sid}\{userPath}\{guidText}", writable: false);
                if (loadedUserKey is not null)
                {
                    yield return new ClsidRegistryKey(path, loadedUserKey);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateLoadedUserClassSids()
    {
        return Registry.Users.GetSubKeyNames()
            .Where(static sid => sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                                 && !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static sid => sid, StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Executes normalize Candidate Path.
    /// </summary>
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

    private static bool IsGuidText(string? value)
    {
        return Guid.TryParse(value, out _);
    }

    private static bool IsUsefulDisplayName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && !IsGuidText(value);
    }

    internal static bool IsWeakComClassDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        if (Guid.TryParse(text, out _))
        {
            return true;
        }

        return text.EndsWith(" Class", StringComparison.OrdinalIgnoreCase)
               || text.Equals("DesktopContext Class", StringComparison.OrdinalIgnoreCase)
               || text.Equals("ContextMenu Class", StringComparison.OrdinalIgnoreCase)
               || text.Equals("Context Menu Class", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ClsidRegistryKey(string Path, RegistryKey Key);

    internal sealed record GuidDisplayNameResolution(
        string? DisplayName,
        string? Source,
        IReadOnlyList<string> CheckedClsidRoots,
        bool IsWeak = false);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        int cchOutBuf,
        IntPtr ppvReserved);
}
