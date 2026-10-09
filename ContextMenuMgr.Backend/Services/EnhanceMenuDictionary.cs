using ContextMenuMgr.Contracts;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ContextMenuMgr.Backend.Services;

/// <summary>Owns Enhance Menu dictionary selection, command compilation, generated files and CLI validation.</summary>
internal static class EnhanceMenuDictionary
{
    internal static EnhanceCommandCompilationResult CompileEnhanceCommandValue(XElement commandElement, string cultureName)
    {
        var fileNameElement = commandElement.Element("FileName");
        var argumentsElement = commandElement.Element("Arguments");
        var shellExecuteElement = commandElement.Element("ShellExecute");
        var powerShellScriptElement = SelectLocalizedElementForWrite(commandElement.Elements("PowerShellScript"), cultureName);

        if (powerShellScriptElement is not null)
        {
            var script = GetDirectElementText(powerShellScriptElement).Trim();
            var runtimeArgument = powerShellScriptElement.Attribute("Argument")?.Value?.Trim();
            var elevate = !bool.TryParse(powerShellScriptElement.Attribute("Elevate")?.Value, out var configuredElevation)
                          || configuredElevation;
            var powerShellCommand = elevate
                ? BuildElevatedPowerShellCommand(script, runtimeArgument)
                : BuildPowerShellCommand(
                    runtimeArgument is null ? script : $"& {{ param($p); {script} }}",
                    string.IsNullOrWhiteSpace(runtimeArgument) ? [] : new[] { runtimeArgument });
            return new EnhanceCommandCompilationResult(
                powerShellCommand,
                "powershell.exe",
                shellExecuteElement is not null,
                shellExecuteElement?.Attribute("Verb")?.Value?.Trim() ?? string.Empty,
                elevate ? "ElevatedPowerShellBlock" : "PowerShellBlock",
                false,
                string.Empty);
        }

        var fileName = fileNameElement?.Value?.Trim();
        var arguments = argumentsElement?.Value?.Trim();
        var generatedFileCreated = false;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = CreateEnhanceCommandFile(fileNameElement, cultureName);
            generatedFileCreated = !string.IsNullOrWhiteSpace(fileName);
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            arguments = CreateEnhanceCommandFile(argumentsElement, cultureName);
            generatedFileCreated = generatedFileCreated || !string.IsNullOrWhiteSpace(arguments);
        }

        fileName = CanonicalizeEnhanceExecutableFileName(fileName);
        var rawFileName = fileName ?? string.Empty;
        var rawArguments = arguments ?? string.Empty;

        string command;
        var wrapperReason = string.Empty;
        var generatedCommandKind = "Direct";
        var shellExecuteVerb = shellExecuteElement?.Attribute("Verb")?.Value?.Trim() ?? string.Empty;
        if (shellExecuteElement is not null)
        {
            var verb = shellExecuteElement.Attribute("Verb")?.Value ?? "open";
            var windowStyle = int.TryParse(shellExecuteElement.Attribute("WindowStyle")?.Value, out var parsedStyle) ? parsedStyle : 1;
            var directory = shellExecuteElement.Attribute("Directory") is { } directoryAttribute
                ? Environment.ExpandEnvironmentVariables(directoryAttribute.Value)
                : string.Empty;
            if (string.Equals(verb, "runas", StringComparison.OrdinalIgnoreCase))
            {
                command = BuildPowerShellRunAsCommand(
                    rawFileName,
                    $"{argumentsElement?.Attribute("Prefix")?.Value}{rawArguments}{argumentsElement?.Attribute("Suffix")?.Value}");
                generatedCommandKind = "PowerShellRunAs";
            }
            else if (!RequiresShellExecuteWrapper(shellExecuteElement))
            {
                fileName = ExpandEnhanceCommandEnvironmentVariables(rawFileName);
                arguments = ExpandEnhanceCommandEnvironmentVariables(rawArguments);
                arguments = CanonicalizeEnhanceCommandArguments(arguments);
                arguments = $"{argumentsElement?.Attribute("Prefix")?.Value}{arguments}{argumentsElement?.Attribute("Suffix")?.Value}";
                command = BuildDirectEnhanceCommand(fileName, arguments);
            }
            else
            {
                wrapperReason = GetShellExecuteWrapperReason(shellExecuteElement);
                fileName = ExpandEnhanceCommandEnvironmentVariables(rawFileName);
                arguments = ExpandEnhanceCommandEnvironmentVariables(rawArguments);
                arguments = CanonicalizeEnhanceCommandArguments(arguments);
                arguments = $"{argumentsElement?.Attribute("Prefix")?.Value}{arguments}{argumentsElement?.Attribute("Suffix")?.Value}";
                command = BuildShellExecuteCommand(fileName, arguments, verb, windowStyle, directory);
                generatedCommandKind = "LegacyMshta";
            }
        }
        else
        {
            fileName = ExpandEnhanceCommandEnvironmentVariables(rawFileName);
            arguments = ExpandEnhanceCommandEnvironmentVariables(rawArguments);
            arguments = CanonicalizeEnhanceCommandArguments(arguments);
            arguments = $"{argumentsElement?.Attribute("Prefix")?.Value}{arguments}{argumentsElement?.Attribute("Suffix")?.Value}";
            command = BuildDirectEnhanceCommand(fileName, arguments);
        }

        return new EnhanceCommandCompilationResult(
            command,
            fileName ?? string.Empty,
            shellExecuteElement is not null,
            shellExecuteVerb,
            generatedCommandKind,
            generatedFileCreated,
            wrapperReason);
    }

    internal static int ValidateEnhanceMenuDictionary(string dictionaryPath, string? cultureName, TextWriter writer)
    {
        var normalizedCulture = NormalizeEnhanceCultureName(cultureName);
        var document = XDocument.Load(dictionaryPath, LoadOptions.PreserveWhitespace);
        var commandElements = document.Descendants("Command").ToList();
        var flaggedCount = 0;

        writer.WriteLine($"EnhanceMenus validation: Path={dictionaryPath}, Culture={normalizedCulture}, Commands={commandElements.Count}");
        writer.WriteLine("ItemKey\tRegistryPath\tCommandKind\tFlags");

        foreach (var commandElement in commandElements)
        {
            if (!ShouldIncludeNode(commandElement, normalizedCulture))
            {
                continue;
            }

            var itemKey = GetEnhanceCommandKeyName(commandElement);
            var registryPath = GetEnhanceDiagnosticRegistryPath(commandElement);
            var (commandKind, command) = CompileEnhanceDiagnosticCommand(commandElement, normalizedCulture);
            var flags = GetEnhanceCommandLegacyFlags(command);

            if (flags.Count > 0)
            {
                flaggedCount++;
            }

            writer.WriteLine($"{itemKey}\t{registryPath}\t{commandKind}\t{(flags.Count == 0 ? "OK" : string.Join(", ", flags))}");
        }

        writer.WriteLine(flaggedCount == 0
            ? "EnhanceMenus validation passed: no flagged legacy command patterns."
            : $"EnhanceMenus validation failed: {flaggedCount} command(s) contain flagged legacy patterns.");

        return flaggedCount == 0 ? 0 : 2;
    }

    internal static int ValidateEnhanceLocalizationSelection(TextWriter writer)
    {
        var failures = new List<string>();
        var valueNodes = ParseElements(
            """
            <Root>
              <REG_SZ MUIVerb="系统信息" />
              <REG_SZ MUIVerb="System Info"><Culture>en-US</Culture></REG_SZ>
              <REG_SZ MUIVerb="系統資訊"><Culture>zh-TW</Culture></REG_SZ>
            </Root>
            """);
        var scriptNodes = ParseElements(
            """
            <Root>
              <PowerShellScript>simplified-script</PowerShellScript>
              <PowerShellScript>traditional-script<Culture>zh-TW</Culture></PowerShellScript>
              <PowerShellScript>english-script<Culture>en-US</Culture></PowerShellScript>
            </Root>
            """);

        ExpectEqual("zh-CN value selection", "系统信息", GetFinalSelectedMuiVerb(valueNodes, "zh-CN"), failures);
        ExpectEqual("zh-TW value selection", "系統資訊", GetFinalSelectedMuiVerb(valueNodes, "zh-TW"), failures);
        ExpectEqual("en-US value selection", "System Info", GetFinalSelectedMuiVerb(valueNodes, "en-US"), failures);
        ExpectFalse(
            "zh-CN excludes zh-TW value node",
            SelectLocalizedElementsForWrite(valueNodes, "zh-CN").Any(element => HasExactNormalizedCulture(element, "zh-TW")),
            failures);
        ExpectEqual("zh-TW normalization", "zh-TW", NormalizeEnhanceCultureName("zh-TW"), failures);
        ExpectEqual(
            "PowerShellScript zh-CN selection",
            "simplified-script",
            GetDirectElementText(SelectLocalizedElementForWrite(scriptNodes, "zh-CN")!).Trim(),
            failures);
        ExpectEqual(
            "PowerShellScript en-US selection",
            "english-script",
            GetDirectElementText(SelectLocalizedElementForWrite(scriptNodes, "en-US")!).Trim(),
            failures);

        if (failures.Count == 0)
        {
            writer.WriteLine("Enhance localization selection validation passed.");
            return 0;
        }

        writer.WriteLine("Enhance localization selection validation failed:");
        foreach (var failure in failures)
        {
            writer.WriteLine($"- {failure}");
        }

        return 1;

        static IReadOnlyList<XElement> ParseElements(string xml)
            => XElement.Parse(xml).Elements().ToList();

        static string? GetFinalSelectedMuiVerb(IReadOnlyList<XElement> elements, string cultureName)
            => SelectLocalizedElementsForWrite(elements, cultureName)
                .Select(element => element.Attribute("MUIVerb")?.Value)
                .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));

        static void ExpectEqual(string name, string expected, string? actual, List<string> failures)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                failures.Add($"{name}: expected '{expected}', got '{actual ?? "<null>"}'.");
            }
        }

        static void ExpectFalse(string name, bool actual, List<string> failures)
        {
            if (actual)
            {
                failures.Add($"{name}: expected false, got true.");
            }
        }
    }

    private static (string CommandKind, string Command) CompileEnhanceDiagnosticCommand(XElement commandElement, string cultureName)
    {
        var defaultValue = commandElement.Attribute("Default")?.Value;
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            var command = ExpandEnhanceCommandEnvironmentVariables(CanonicalizeEnhanceCommandDefaultValue(defaultValue));
            return ("Default", command);
        }

        if (commandElement.Element("Value") is not null
            && commandElement.Element("PowerShellScript") is null
            && commandElement.Element("FileName") is null
            && commandElement.Element("Arguments") is null)
        {
            return ("RegistryValuesOnly", string.Empty);
        }

        var compilation = CompileEnhanceCommandValue(commandElement, cultureName);
        return (compilation.GeneratedCommandKind, compilation.Command);
    }

    private static string GetEnhanceDiagnosticRegistryPath(XElement commandElement)
    {
        var groupElement = commandElement.Ancestors("Group").FirstOrDefault();
        var rootPath = groupElement?.Element("RegPath")?.Value?.Trim();
        var keyParts = commandElement
            .Ancestors()
            .TakeWhile(element => !string.Equals(element.Name.LocalName, "Group", StringComparison.OrdinalIgnoreCase))
            .Where(element => element.Attribute("KeyName") is not null)
            .Reverse()
            .Select(element => element.Attribute("KeyName")!.Value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        keyParts.Add("command");
        return string.IsNullOrWhiteSpace(rootPath)
            ? string.Join(@"\", keyParts)
            : rootPath + @"\shell\" + string.Join(@"\", keyParts);
    }

    private static IReadOnlyList<string> GetEnhanceCommandLegacyFlags(string command)
    {
        var flags = new List<string>();
        AddFlagIfMatch(flags, command, @"(?i)\bmshta\b", "mshta");
        AddFlagIfMatch(flags, command, @"(?i)\bvbscript:", "vbscript:");
        AddFlagIfMatch(flags, command, @"(?i)\bWscript\.exe\b", "Wscript.exe");
        AddFlagIfMatch(flags, command, @"(?i)\.vbs\b", ".vbs");
        AddFlagIfMatch(flags, command, @"(?i)^\s*""?cmd(?:\.exe)?""?(?=\s|$)", "bare cmd");
        AddFlagIfMatch(flags, command, @"(?i)^\s*""?explorer(?:\.exe)?""?(?=\s|$)", "bare explorer");
        AddFlagIfMatch(flags, command, @"(?i)ContextMenuMgr", "ContextMenuMgr");
        AddFlagIfMatch(flags, command, @"(?i)\bBackend\b", "Backend");
        AddFlagIfMatch(flags, command, @"(?i)\bTrayHost\b", "TrayHost");
        AddFlagIfMatch(flags, command, @"(?i)\bNamedPipe\b", "NamedPipe");
        AddFlagIfMatch(flags, command, @"(?i)\bpipe\b", "pipe");
        return flags;
    }

    private static void AddFlagIfMatch(List<string> flags, string command, string pattern, string flag)
    {
        if (Regex.IsMatch(command, pattern))
        {
            flags.Add(flag);
        }
    }

    internal sealed record EnhanceCommandCompilationResult(
        string Command,
        string FileName,
        bool HasShellExecute,
        string ShellExecuteVerb,
        string GeneratedCommandKind,
        bool GeneratedFileCreated,
        string WrapperReason);

    private static bool RequiresShellExecuteWrapper(XElement shellExecuteElement)
        => !string.IsNullOrEmpty(GetShellExecuteWrapperReason(shellExecuteElement));

    private static string GetShellExecuteWrapperReason(XElement shellExecuteElement)
    {
        var verb = shellExecuteElement.Attribute("Verb")?.Value?.Trim();
        if (!string.IsNullOrEmpty(verb)
            && !string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase))
        {
            // TODO: runas/admin enhance items need a separate modern launcher strategy.
            return $"Verb={verb}";
        }

        var directory = shellExecuteElement.Attribute("Directory")?.Value?.Trim();
        if (!string.IsNullOrEmpty(directory))
        {
            return "Directory";
        }

        foreach (var attribute in shellExecuteElement.Attributes())
        {
            var name = attribute.Name.LocalName;
            if (string.Equals(name, "Verb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "WindowStyle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Directory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(attribute.Value))
            {
                return $"Attribute={name}";
            }
        }

        return string.Empty;
    }

    private static string BuildDirectEnhanceCommand(string fileName, string arguments)
    {
        var command = QuoteEnhanceExecutablePath(fileName);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            command += $" {arguments}";
        }

        return command;
    }

    private static string QuoteEnhanceExecutablePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var trimmed = fileName.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed;
        }

        if (trimmed.Contains(' ')
            && (Path.IsPathRooted(trimmed) || Regex.IsMatch(trimmed, @"^%[^%]+%[\\/]", RegexOptions.IgnoreCase)))
        {
            return $"\"{trimmed}\"";
        }

        return trimmed;
    }

    internal static string GetEnhanceCommandKeyName(XElement commandElement)
    {
        foreach (var element in commandElement.AncestorsAndSelf())
        {
            var keyName = element.Attribute("KeyName")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                return keyName;
            }
        }

        return "<unknown>";
    }

    private static string CanonicalizeEnhanceExecutableFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var trimmed = fileName.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        return trimmed.Equals("cmd", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            ? @"C:\Windows\System32\cmd.exe"
            : trimmed.Equals("explorer", StringComparison.OrdinalIgnoreCase)
              || trimmed.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                ? @"C:\Windows\explorer.exe"
                : trimmed;
    }

    internal static string CanonicalizeEnhanceCommandDefaultValue(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        var leadingWhitespaceLength = command.Length - command.TrimStart().Length;
        var leadingWhitespace = command[..leadingWhitespaceLength];
        var trimmedStart = command[leadingWhitespaceLength..];

        foreach (var (prefix, replacement) in new[]
                 {
                     ("cmd.exe ", @"C:\Windows\System32\cmd.exe "),
                     ("cmd ", @"C:\Windows\System32\cmd.exe "),
                     ("explorer.exe ", @"C:\Windows\explorer.exe "),
                     ("explorer ", @"C:\Windows\explorer.exe ")
                 })
        {
            if (trimmedStart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = trimmedStart[prefix.Length..];
                return leadingWhitespace + replacement + rest;
            }
        }

        return command;
    }

    private static string CanonicalizeEnhanceCommandArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return arguments;
        }

        return Regex.Replace(
            arguments,
            @"(?i)(^|[&|]\s*)start\s+explorer(?:\.exe)?(?=\s|$)",
            match => $@"{match.Groups[1].Value}start C:\Windows\\explorer.exe");
    }

    internal static string ExpandEnhanceCommandEnvironmentVariables(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var protectedTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var protectedValue = Regex.Replace(value, @"C:\Windows", AddProtectedToken, RegexOptions.IgnoreCase);
        protectedValue = Regex.Replace(
            protectedValue,
            @"%(TEMP|TMP|LOCALAPPDATA|APPDATA|USERPROFILE)%",
            AddProtectedToken,
            RegexOptions.IgnoreCase);

        var expanded = Environment.ExpandEnvironmentVariables(protectedValue);
        foreach (var (token, original) in protectedTokens)
        {
            expanded = expanded.Replace(token, original, StringComparison.Ordinal);
        }

        return expanded;

        string AddProtectedToken(Match match)
        {
            var token = $"\uF001{protectedTokens.Count}\uF001";
            protectedTokens[token] = match.Value;
            return token;
        }
    }

    private static string CreateEnhanceCommandFile(XElement? parentElement, string cultureName)
    {
        if (parentElement is null)
        {
            return string.Empty;
        }

        var generatedDir = RuntimePaths.GeneratedProgramsDirectory;
        Directory.CreateDirectory(generatedDir);

        var path = string.Empty;
        var createFileElement = SelectLocalizedElementForWrite(parentElement.Elements("CreateFile"), cultureName);
        if (createFileElement is not null)
        {
            var fileName = createFileElement.Attribute("FileName")?.Value;
            var content = createFileElement.Attribute("Content")?.Value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var safeFileName = SanitizeEnhanceProgramFileName(fileName);
                var filePath = Path.Combine(generatedDir, safeFileName);
                var encoding = string.Equals(Path.GetExtension(fileName), ".bat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(fileName), ".cmd", StringComparison.OrdinalIgnoreCase)
                        ? Encoding.Default
                        : Encoding.Unicode;

                path = filePath;

                File.Delete(filePath);
                File.WriteAllText(filePath, content, encoding);
            }
        }

        return path;
    }

    private static string GetDirectElementText(XElement element)
        => string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value));

    private static string BuildPowerShellRunAsCommand(string fileName, string arguments)
    {
        var runtimeArguments = GetRuntimePlaceholderArguments(arguments);
        var script = "& { "
                     + BuildPowerShellParamList(runtimeArguments)
                     + $"Start-Process -Verb RunAs -FilePath {QuotePowerShellSingleQuotedString(fileName)}";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            script += $" -ArgumentList ({BuildPowerShellStringExpression(arguments, runtimeArguments)})";
        }

        script += " }";
        return BuildPowerShellCommand(script, runtimeArguments);
    }

    private static string BuildElevatedPowerShellCommand(string script, string? runtimeArgument)
    {
        var runtimeArguments = string.IsNullOrWhiteSpace(runtimeArgument)
            ? []
            : new[] { runtimeArgument };

        var innerScript = runtimeArguments.Length > 0
            ? $"& {{ param($p); {script} }}"
            : $"& {{ {script} }}";
        var innerScriptExpression = BuildPowerShellStringExpression(innerScript, []);
        var innerCommandLineExpression = string.Join(
            " + ",
            QuotePowerShellSingleQuotedString("-NoProfile -ExecutionPolicy Bypass -Command "),
            "[char]34",
            innerScriptExpression,
            "[char]34");

        if (runtimeArguments.Length > 0)
        {
            innerCommandLineExpression += " + ' ' + [char]34 + $p0 + [char]34";
        }

        var outerScript = "& { "
                          + BuildPowerShellParamList(runtimeArguments)
                          + "$process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList ("
                          + innerCommandLineExpression
                          + "); exit $process.ExitCode }";
        return BuildPowerShellCommand(outerScript, runtimeArguments);
    }

    private static string BuildPowerShellCommand(string script, IReadOnlyList<string> runtimeArguments)
    {
        var command = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"{script}\"";
        foreach (var argument in runtimeArguments)
        {
            command += $" \"{argument}\"";
        }

        return command;
    }

    private static string BuildPowerShellParamList(IReadOnlyList<string> runtimeArguments)
    {
        if (runtimeArguments.Count == 0)
        {
            return string.Empty;
        }

        return "param("
               + string.Join(",", Enumerable.Range(0, runtimeArguments.Count).Select(index => $"$p{index}"))
               + ");";
    }

    private static string BuildPowerShellStringExpression(string value, IReadOnlyList<string> runtimeArguments)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        var parts = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            var placeholderIndex = -1;
            var placeholderValue = string.Empty;
            var placeholderVariable = string.Empty;
            for (var i = 0; i < runtimeArguments.Count; i++)
            {
                var candidate = runtimeArguments[i];
                var candidateIndex = value.IndexOf(candidate, index, StringComparison.OrdinalIgnoreCase);
                if (candidateIndex >= 0 && (placeholderIndex < 0 || candidateIndex < placeholderIndex))
                {
                    placeholderIndex = candidateIndex;
                    placeholderValue = candidate;
                    placeholderVariable = $"$p{i}";
                }
            }

            var nextLiteralEnd = placeholderIndex >= 0 ? placeholderIndex : value.Length;
            AddPowerShellLiteralExpressionParts(parts, value[index..nextLiteralEnd]);
            if (placeholderIndex < 0)
            {
                break;
            }

            parts.Add(placeholderVariable);
            index = placeholderIndex + placeholderValue.Length;
        }

        return parts.Count == 0 ? "''" : string.Join(" + ", parts);
    }

    private static void AddPowerShellLiteralExpressionParts(List<string> parts, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '"')
            {
                continue;
            }

            if (i > start)
            {
                parts.Add(QuotePowerShellSingleQuotedString(value[start..i]));
            }

            parts.Add("[char]34");
            start = i + 1;
        }

        if (start < value.Length)
        {
            parts.Add(QuotePowerShellSingleQuotedString(value[start..]));
        }
    }

    private static string QuotePowerShellSingleQuotedString(string value)
        => $"'{(value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)}'";

    private static string[] GetRuntimePlaceholderArguments(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var placeholder in new[] { "%1", "%v" })
        {
            if (value.Contains(placeholder, StringComparison.OrdinalIgnoreCase)
                && !result.Contains(placeholder, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(placeholder);
            }
        }

        return result.ToArray();
    }

    private static string BuildShellExecuteCommand(
        string fileName,
        string arguments,
        string verb,
        int windowStyle,
        string? directory)
    {
        arguments = arguments.Replace("\"", "\"\"");
        directory = directory is null
            ? Path.GetDirectoryName(ExtractExecutablePath(fileName))
            : directory;

        return "mshta vbscript:createobject(\"shell.application\").shellexecute"
            + $"(\"{fileName}\",\"{arguments}\",\"{directory}\",\"{verb}\",{windowStyle})(close)";
    }

    private static string SanitizeEnhanceProgramFileName(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new InvalidOperationException("CreateFile requires a valid file name.");
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            safeFileName = safeFileName.Replace(invalidChar, '_');
        }

        if (safeFileName is "." or "..")
        {
            throw new InvalidOperationException("CreateFile file name cannot be a relative path segment.");
        }

        return safeFileName;
    }

    private static string ExtractExecutablePath(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return rawValue;
        }

        var trimmed = rawValue.Trim();
        if (File.Exists(trimmed))
        {
            return trimmed;
        }

        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".dll", ".msc", ".cpl", ".ocx", ".ps1", ".vbs", ".js", ".hta" })
        {
            var index = trimmed.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var candidate = trimmed[..(index + extension.Length)];
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return trimmed;
    }

    internal static bool ShouldIncludeNode(XElement element, string cultureName)
    {
        if (!HasRequiredFiles(element))
        {
            return false;
        }

        if (!MatchesOsVersion(element))
        {
            return false;
        }

        return MatchesCulture(element, cultureName);
    }

    internal static XElement? SelectLocalizedElementForWrite(IEnumerable<XElement> elements, string cultureName)
    {
        var candidates = elements.Where(IsValidLocalizedNode).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var normalizedCultureName = NormalizeEnhanceCultureName(cultureName);
        var exact = candidates.FirstOrDefault(element => HasExactNormalizedCulture(element, normalizedCultureName));
        if (exact is not null)
        {
            return exact;
        }

        var noCulture = candidates.FirstOrDefault(IsNoCultureNode);
        if (noCulture is not null)
        {
            return noCulture;
        }

        if (!IsChineseEnhanceCultureName(normalizedCultureName))
        {
            var english = candidates.FirstOrDefault(element => HasExactNormalizedCulture(element, "en-US"));
            if (english is not null)
            {
                return english;
            }
        }

        return candidates[0];
    }

    internal static IReadOnlyList<XElement> SelectLocalizedElementsForWrite(IEnumerable<XElement> elements, string cultureName)
    {
        var candidates = elements.Where(IsValidLocalizedNode).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var normalizedCultureName = NormalizeEnhanceCultureName(cultureName);
        var selected = new List<XElement>();
        selected.AddRange(candidates.Where(IsNoCultureNode));
        selected.AddRange(candidates.Where(element => HasExactNormalizedCulture(element, normalizedCultureName)));

        return selected.Count > 0 ? selected : [candidates[0]];
    }

    private static bool IsValidLocalizedNode(XElement element)
        => HasRequiredFiles(element) && MatchesOsVersion(element);

    private static bool IsNoCultureNode(XElement element)
        => string.IsNullOrWhiteSpace(element.Element("Culture")?.Value);

    internal static bool HasExactNormalizedCulture(XElement element, string cultureName)
    {
        var culture = element.Element("Culture")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(culture))
        {
            return false;
        }

        return TryNormalizeEnhanceCultureName(culture, out var normalizedElementCulture)
            && string.Equals(normalizedElementCulture, NormalizeEnhanceCultureName(cultureName), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRequiredFiles(XElement element)
    {
        foreach (var fileElement in element.Elements("FileExists"))
        {
            var candidate = Environment.ExpandEnvironmentVariables(fileElement.Value.Trim());
            if (!File.Exists(candidate))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesCulture(XElement element, string cultureName)
    {
        var culture = element.Element("Culture")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(culture))
        {
            return true;
        }

        return TryNormalizeEnhanceCultureName(culture, out var normalizedElementCulture)
            && string.Equals(normalizedElementCulture, NormalizeEnhanceCultureName(cultureName), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesOsVersion(XElement element)
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

    internal static string NormalizeEnhanceCultureName(string? cultureName)
    {
        if (TryNormalizeEnhanceCultureName(cultureName, out var normalizedCultureName)
            || TryNormalizeEnhanceCultureName(CultureInfo.CurrentUICulture.Name, out normalizedCultureName))
        {
            return normalizedCultureName;
        }

        return "en-US";
    }

    private static bool TryNormalizeEnhanceCultureName(string? cultureName, out string normalizedCultureName)
    {
        normalizedCultureName = "en-US";
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName.Trim());
            normalizedCultureName = culture.Name switch
            {
                "zh-CN" or "zh-Hans" or "zh-SG" => "zh-CN",
                "zh-TW" or "zh-Hant" or "zh-HK" or "zh-MO" => "zh-TW",
                "zh" => "zh-CN",
                "en" or "en-US" => "en-US",
                _ => "en-US"
            };

            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static bool IsChineseEnhanceCultureName(string cultureName)
        => string.Equals(cultureName, "zh-CN", StringComparison.OrdinalIgnoreCase)
           || string.Equals(cultureName, "zh-TW", StringComparison.OrdinalIgnoreCase);
}
