using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public sealed class RuleDictionaryCatalogService
{
    private readonly LocalizationService _localization;

    public RuleDictionaryCatalogService(LocalizationService localization)
    {
        _localization = localization;
    }

    public IReadOnlyList<EnhanceMenuGroupDefinition> LoadEnhanceMenuGroups()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "EnhanceMenusDic.xml");
        if (!File.Exists(path))
        {
            return [];
        }

        var document = XDocument.Load(path);
        var result = new List<EnhanceMenuGroupDefinition>();
        foreach (var groupElement in document.Root?.Elements("Group") ?? [])
        {
            var title = ResolveText(groupElement.Elements("Text"));
            var registryPath = groupElement.Element("RegPath")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(registryPath))
            {
                continue;
            }

            var items = new List<EnhanceMenuItemDefinition>();
            var shellItems = groupElement.Element("Shell")?.Elements("Item") ?? [];
            foreach (var itemElement in shellItems.Where(JudgeOsVersion).Where(JudgeFileExists))
            {
                var displayName = ResolveEnhanceShellDisplayName(itemElement);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                items.Add(new EnhanceMenuItemDefinition(
                    registryPath,
                    displayName,
                    "Shell",
                    itemElement.Attribute("KeyName")?.Value?.Trim() ?? displayName,
                    null,
                    ResolveText(itemElement.Elements("Tip")),
                    ResolveEnhanceShellIcon(itemElement),
                    itemElement.ToString(SaveOptions.DisableFormatting)));
            }

            var shellExItems = groupElement.Element("ShellEx")?.Elements("Item") ?? [];
            foreach (var itemElement in shellExItems.Where(JudgeOsVersion).Where(JudgeFileExists))
            {
                var displayName = ResolveText(itemElement.Elements("Text"));
                var keyName = itemElement.Element("KeyName")?.Value?.Trim()
                    ?? itemElement.Element("Guid")?.Value?.Trim()
                    ?? displayName;
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(keyName))
                {
                    continue;
                }

                items.Add(new EnhanceMenuItemDefinition(
                    registryPath,
                    displayName,
                    "ShellEx",
                    keyName,
                    itemElement.Element("Guid")?.Value?.Trim(),
                    ResolveTip(itemElement),
                    itemElement.Element("Icon")?.Value?.Trim(),
                    itemElement.ToString(SaveOptions.DisableFormatting)));
            }

            result.Add(new EnhanceMenuGroupDefinition(
                title,
                registryPath,
                groupElement.Element("Icon")?.Value?.Trim(),
                items));
        }

        return result;
    }

    public IReadOnlyList<DetailedEditGroupDefinition> LoadDetailedEditGroups()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "DetailedEditDic.xml");
        if (!File.Exists(path))
        {
            return [];
        }

        var document = XDocument.Load(path);
        var result = new List<DetailedEditGroupDefinition>();
        foreach (var groupElement in document.Root?.Elements("Group") ?? [])
        {
            var rules = new List<DetailedEditRuleDefinition>();
            foreach (var itemElement in groupElement.Elements("Item").Where(JudgeOsVersion).Where(JudgeFileExists))
            {
                var displayName = ResolveText(itemElement.Elements("Text"));
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var editorKind = ResolveEditorKind(itemElement);
                var clauses = BuildRuleClauses(groupElement, itemElement);
                if (clauses.Count == 0)
                {
                    continue;
                }

                var firstRule = itemElement.Elements("Rule").FirstOrDefault();
                rules.Add(new DetailedEditRuleDefinition(
                    displayName,
                    ResolveTip(itemElement),
                    editorKind,
                    itemElement.Element("RestartExplorer") is not null,
                    ParseInt(firstRule?.Attribute("Default")?.Value, 0),
                    ParseInt(firstRule?.Attribute("Min")?.Value, int.MinValue),
                    ParseInt(firstRule?.Attribute("Max")?.Value, int.MaxValue),
                    clauses));
            }

            var title = ResolveText(groupElement.Elements("Text"));
            if (string.IsNullOrWhiteSpace(title) || rules.Count == 0)
            {
                continue;
            }

            var groupPath = groupElement.Element("RegPath")?.Value?.Trim();
            var filePath = groupElement.Element("FilePath")?.Value?.Trim();
            var isIniGroup = groupElement.Element("IsIniGroup") is not null;

            result.Add(new DetailedEditGroupDefinition(
                title,
                groupPath,
                filePath,
                isIniGroup,
                IsDetailedGroupAvailable(groupElement, isIniGroup, groupPath, filePath),
                rules));
        }

        return result;
    }

    private List<DetailedEditRuleClauseDefinition> BuildRuleClauses(XElement groupElement, XElement itemElement)
    {
        var result = new List<DetailedEditRuleClauseDefinition>();
        var isIniGroup = groupElement.Element("IsIniGroup") is not null;
        var basePath = (isIniGroup ? groupElement.Element("FilePath") : groupElement.Element("RegPath"))?.Value?.Trim();

        foreach (var ruleElement in itemElement.Elements("Rule"))
        {
            var path = isIniGroup
                ? ruleElement.Attribute("FilePath")?.Value?.Trim() ?? basePath
                : ResolveRegistryPath(basePath, ruleElement.Attribute("RegPath")?.Value?.Trim());

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var keyName = isIniGroup
                ? ruleElement.Attribute("KeyName")?.Value?.Trim()
                : ruleElement.Attribute("ValueName")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(keyName))
            {
                continue;
            }

            result.Add(new DetailedEditRuleClauseDefinition(
                isIniGroup ? RuleStorageKind.Ini : RuleStorageKind.Registry,
                ExpandPath(path),
                ruleElement.Attribute("Section")?.Value?.Trim(),
                keyName,
                ParseRegistryValueKind(ruleElement.Attribute("ValueKind")?.Value),
                ruleElement.Attribute("On")?.Value,
                ruleElement.Attribute("Off")?.Value));
        }

        return result;
    }

    private bool IsDetailedGroupAvailable(XElement groupElement, bool isIniGroup, string? groupPath, string? filePath)
    {
        var guidElements = groupElement.Elements("Guid").ToArray();
        if (guidElements.Length > 0)
        {
            return guidElements
                .Select(element => element.Value.Trim())
                .Any(TryResolveGuidFilePath);
        }

        if (isIniGroup)
        {
            return !string.IsNullOrWhiteSpace(filePath) && File.Exists(ExpandPath(filePath));
        }

        if (string.IsNullOrWhiteSpace(groupPath))
        {
            return false;
        }

        return RegistryPathExists(ExpandPath(groupPath));
    }

    private bool TryResolveGuidFilePath(string guidText)
    {
        if (!Guid.TryParse(guidText, out var guid))
        {
            return false;
        }

        var clsidPath = $@"CLSID\{guid:B}";
        using var clsidKey = Registry.ClassesRoot.OpenSubKey(clsidPath, writable: false);
        var candidates = new[]
        {
            clsidKey?.OpenSubKey("InprocServer32", writable: false)?.GetValue(null)?.ToString(),
            clsidKey?.OpenSubKey("LocalServer32", writable: false)?.GetValue(null)?.ToString()
        };

        return candidates
            .Select(ExtractFilePath)
            .Any(File.Exists);
    }

    private static string? ExtractFilePath(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var trimmed = rawValue.Trim();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote] : trimmed.Trim('"');
        }

        foreach (var extension in new[] { ".dll", ".exe", ".cpl", ".ocx" })
        {
            var index = trimmed.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return trimmed[..(index + extension.Length)];
            }
        }

        return trimmed;
    }

    private bool JudgeOsVersion(XElement element)
    {
        foreach (var versionElement in element.Elements("OSVersion"))
        {
            if (!Version.TryParse(versionElement.Value.Trim(), out var version))
            {
                continue;
            }

            var compare = versionElement.Attribute("Compare")?.Value?.Trim() ?? ">=";
            var current = Environment.OSVersion.Version.CompareTo(version);
            var matched = compare switch
            {
                ">" => current > 0,
                "<" => current < 0,
                "=" => current == 0,
                ">=" => current >= 0,
                "<=" => current <= 0,
                _ => true
            };

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static bool JudgeFileExists(XElement element)
    {
        foreach (var fileElement in element.Elements("FileExists"))
        {
            var candidate = ExpandPath(fileElement.Value.Trim());
            if (!File.Exists(candidate))
            {
                return false;
            }
        }

        return true;
    }

    private string? ResolveEnhanceShellDisplayName(XElement itemElement)
    {
        var muiVerb = itemElement
            .Element("Value")?
            .Elements()
            .FirstOrDefault(element => ResolveMuiVerb(element) is not null);

        var text = muiVerb is null ? null : ResolveMuiVerb(muiVerb);
        return string.IsNullOrWhiteSpace(text)
            ? itemElement.Attribute("KeyName")?.Value?.Trim()
            : ResolveResourceString(text);
    }

    private string? ResolveEnhanceShellIcon(XElement itemElement)
    {
        var iconElement = itemElement
            .Element("Value")?
            .Elements()
            .FirstOrDefault(element => element.Attribute("Icon") is not null);

        var explicitIcon = iconElement?.Attribute("Icon")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitIcon))
        {
            return explicitIcon;
        }

        var commandElement = itemElement.Element("SubKey")?.Element("Command");
        if (commandElement is null)
        {
            return null;
        }

        var defaultCommand = commandElement.Attribute("Default")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(defaultCommand))
        {
            var commandIcon = ExtractCommandExecutable(defaultCommand);
            if (!string.IsNullOrWhiteSpace(commandIcon))
            {
                return commandIcon;
            }
        }

        var fileName = commandElement.Element("FileName")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return ExpandPath(fileName);
        }

        return null;
    }

    private static string? ExtractCommandExecutable(string command)
    {
        var expanded = ExpandPath(command);
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return null;
        }

        if (File.Exists(expanded))
        {
            return expanded;
        }

        if (expanded.StartsWith('"'))
        {
            var endQuote = expanded.IndexOf('"', 1);
            if (endQuote > 1)
            {
                var quotedPath = expanded[1..endQuote];
                if (File.Exists(quotedPath))
                {
                    return quotedPath;
                }
            }
        }

        foreach (var extension in new[] { ".exe", ".dll", ".cpl", ".msc", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".hta" })
        {
            var index = expanded.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var candidate = expanded[..(index + extension.Length)].Trim('"', ' ');
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string? ResolveMuiVerb(XElement valueElement)
    {
        if (!JudgeCulture(valueElement))
        {
            return null;
        }

        var value = valueElement.Attribute("MUIVerb")?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private string? ResolveTip(XElement itemElement)
    {
        return ResolveText(itemElement.Elements("Tip"));
    }

    private string? ResolveText(IEnumerable<XElement> elements)
    {
        var preferredCulture = _localization.UsesChinese()
            ? "zh-CN"
            : "en-US";

        foreach (var element in elements)
        {
            var culture = element.Element("Culture")?.Value?.Trim();
            if (string.Equals(culture, preferredCulture, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveResourceString(GetElementValue(element));
            }
        }

        var withoutCulture = elements.FirstOrDefault(element => element.Element("Culture") is null);
        if (withoutCulture is not null)
        {
            return ResolveResourceString(GetElementValue(withoutCulture));
        }

        return elements
            .Select(GetElementValue)
            .Select(ResolveResourceString)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? ResolveResourceString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var source = value.Trim();
        if (!source.StartsWith('@'))
        {
            return source;
        }

        var buffer = new char[1024];
        var hr = SHLoadIndirectString(source, buffer, buffer.Length, nint.Zero);
        if (hr != 0)
        {
            return source;
        }

        var resolved = new string(buffer).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(resolved) ? source : resolved;
    }

    private bool JudgeCulture(XElement element)
    {
        var culture = element.Element("Culture")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(culture))
        {
            return true;
        }

        return string.Equals(culture, "en-US", StringComparison.OrdinalIgnoreCase)
            ? !_localization.UsesChinese()
            : string.Equals(culture, "zh-CN", StringComparison.OrdinalIgnoreCase) && _localization.UsesChinese();
    }

    private static string? GetElementValue(XElement element)
    {
        var attr = element.Attribute("Value")?.Value;
        if (!string.IsNullOrWhiteSpace(attr))
        {
            return attr.Trim();
        }

        return element.Nodes()
            .OfType<XText>()
            .Select(static node => node.Value.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static RuleValueEditorKind ResolveEditorKind(XElement itemElement)
    {
        if (itemElement.Element("IsNumberItem") is not null)
        {
            return RuleValueEditorKind.Number;
        }

        if (itemElement.Element("IsStringItem") is not null)
        {
            return RuleValueEditorKind.String;
        }

        return RuleValueEditorKind.Boolean;
    }

    private static RegistryValueKind ParseRegistryValueKind(string? rawKind)
    {
        return rawKind?.Trim().ToUpperInvariant() switch
        {
            "REG_SZ" => RegistryValueKind.String,
            "REG_BINARY" => RegistryValueKind.Binary,
            "REG_QWORD" => RegistryValueKind.QWord,
            "REG_MULTI_SZ" => RegistryValueKind.MultiString,
            "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
            _ => RegistryValueKind.DWord
        };
    }

    private static int ParseInt(string? raw, int fallback)
    {
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static string? ResolveRegistryPath(string? groupPath, string? rulePath)
    {
        if (string.IsNullOrWhiteSpace(rulePath))
        {
            return groupPath;
        }

        if (rulePath.StartsWith('\\'))
        {
            return $"{groupPath}{rulePath}";
        }

        return rulePath;
    }

    private static bool RegistryPathExists(string fullPath)
    {
        try
        {
            var normalized = ExpandPath(fullPath).Replace('/', '\\');
            var separatorIndex = normalized.IndexOf('\\');
            var root = separatorIndex >= 0 ? normalized[..separatorIndex] : normalized;
            var subPath = separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : string.Empty;

            using var key = root.ToUpperInvariant() switch
            {
                "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot.OpenSubKey(subPath, writable: false),
                "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser.OpenSubKey(subPath, writable: false),
                "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine.OpenSubKey(subPath, writable: false),
                "HKEY_USERS" or "HKU" => Registry.Users.OpenSubKey(subPath, writable: false),
                _ => null
            };

            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string ExpandPath(string path)
    {
        return Environment.ExpandEnvironmentVariables(path);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        char[] pszOutBuf,
        int cchOutBuf,
        nint ppvReserved);
}
