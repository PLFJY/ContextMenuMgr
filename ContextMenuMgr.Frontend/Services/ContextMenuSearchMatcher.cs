using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Services;

public static class ContextMenuSearchMatcher
{
    public static bool MatchesClassicItem(ContextMenuItemViewModel item, string? query)
    {
        return TryScoreClassicItem(item, query, out _);
    }

    public static bool TryScoreClassicItem(ContextMenuItemViewModel item, string? query, out int score)
    {
        return TryScore(
            query,
            new[]
            {
                item.DisplayName,
                item.KeyName,
                item.Subtitle,
                item.RegistryPath,
                item.Entry.BackendRegistryPath,
                item.Entry.SourceRootPath,
                item.ShellPathTail,
                item.Entry.HandlerClsid,
                item.Entry.FilePath,
                item.Entry.CommandText,
                item.Notes,
                item.UserNote
            },
            item.DisplayName,
            item.KeyName,
            item.Entry.CommandText,
            item.RegistryPath,
            item.Entry.BackendRegistryPath,
            item.Entry.SourceRootPath,
            item.Entry.FilePath,
            item.Entry.HandlerClsid,
            out score);
    }

    public static bool MatchesClassicEntry(ContextMenuEntry entry, string? categoryName, string? stateLabel, string? query)
    {
        return TryScoreClassicEntry(entry, categoryName, stateLabel, null, query, out _);
    }

    public static bool TryScoreClassicEntry(
        ContextMenuEntry entry,
        string? categoryName,
        string? stateLabel,
        string? userNote,
        string? query,
        out int score)
    {
        return TryScore(
            query,
            new[]
            {
                entry.DisplayName,
                entry.KeyName,
                entry.RegistryPath,
                entry.BackendRegistryPath,
                entry.SourceRootPath,
                GetShellPathTail(entry.RegistryPath),
                entry.HandlerClsid,
                entry.FilePath,
                entry.CommandText,
                entry.Notes,
                userNote,
                categoryName,
                stateLabel
            },
            entry.DisplayName,
            entry.KeyName,
            entry.CommandText,
            entry.RegistryPath,
            entry.BackendRegistryPath,
            entry.SourceRootPath,
            entry.FilePath,
            entry.HandlerClsid,
            out score);
    }

    public static bool MatchesWindows11Item(Windows11ContextMenuItemViewModel item, string? query)
    {
        return TryScoreWindows11Item(item, query, out _);
    }

    public static bool TryScoreWindows11Item(Windows11ContextMenuItemViewModel item, string? query, out int score)
    {
        var definitions = item.Definitions;
        return TryScoreWindows11Definitions(
            definitions,
            item.DisplayName,
            item.PackageFamilyName,
            item.PublisherName,
            item.ContextTypesText,
            item.ComServerPath,
            item.IsEnabled ? null : string.Empty,
            item.UserNote,
            query,
            out score);
    }

    public static bool TryScoreWindows11Definitions(
        IReadOnlyList<Windows11ContextMenuItemDefinition> definitions,
        string? displayName,
        string? packageFamilyName,
        string? publisherName,
        string? contextTypesText,
        string? comServerPath,
        string? stateLabel,
        string? userNote,
        string? query,
        out int score)
    {
        var primary = definitions.Count > 0 ? definitions[0] : null;
        var entryFields = definitions
            .Select(static definition => definition.Entry)
            .Where(static entry => entry is not null)
            .SelectMany(static entry => new[]
            {
                entry!.DisplayName,
                entry!.KeyName,
                entry!.RegistryPath,
                entry.BackendRegistryPath,
                entry.SourceRootPath,
                entry.FilePath,
                entry.HandlerClsid,
                entry.CommandText,
                entry.Notes
            });
        var fields = new List<string?>
        {
            displayName,
            packageFamilyName,
            publisherName,
            contextTypesText,
            comServerPath,
            userNote,
            primary?.Package.FullName,
            primary?.Package.DisplayName,
            primary?.Package.InstallPath,
            primary?.ComServer.Id,
            primary?.ComServer.DisplayName,
            stateLabel
        };
        fields.AddRange(definitions.SelectMany(static definition => definition.ContextTypes));
        fields.AddRange(definitions.SelectMany(static definition => definition.ContextMenus.Select(menu => menu.DisplayName)));
        fields.AddRange(entryFields);

        return TryScore(
            query,
            fields,
            displayName,
            packageFamilyName,
            null,
            primary?.Entry?.RegistryPath,
            primary?.Entry?.BackendRegistryPath,
            primary?.Entry?.SourceRootPath,
            comServerPath ?? primary?.Entry?.FilePath,
            primary?.ComServer.Id ?? primary?.Entry?.HandlerClsid,
            out score);
    }

    public static string CreateSearchBlob(IEnumerable<string?> fields)
    {
        return string.Join(
            " ",
            fields.Where(static field => !string.IsNullOrWhiteSpace(field))
                .Select(static field => field!.Trim()));
    }

    public static bool MatchesFields(string? query, params string?[] fields)
    {
        var trimmedQuery = query?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return true;
        }

        var tokens = Tokenize(trimmedQuery);
        if (tokens.Count == 0)
        {
            return false;
        }

        // Normalize every field and token exactly once instead of re-scanning
        // punctuation on every field/token combination. This matters on the
        // app-groups page, which matches every sub-item on every keystroke.
        var normalizedFields = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            normalizedFields[i] = NormalizePunctuation(fields[i] ?? string.Empty);
        }

        foreach (var token in tokens)
        {
            var normalizedToken = NormalizePunctuation(token);
            var matched = false;
            for (var i = 0; i < fields.Length; i++)
            {
                if (Contains(fields[i], normalizedFields[i], token, normalizedToken))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryScore(
        string? query,
        IReadOnlyList<string?> fields,
        string? displayName,
        string? keyName,
        string? commandText,
        string? registryPath,
        string? backendRegistryPath,
        string? sourceRootPath,
        string? filePath,
        string? handlerClsid,
        out int score)
    {
        score = 0;
        var trimmedQuery = query?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return true;
        }

        var tokens = Tokenize(trimmedQuery);
        if (tokens.Count == 0 || tokens.Any(token => !AnyFieldContains(fields, token)))
        {
            return false;
        }

        score = ScoreField(displayName, trimmedQuery, 1000, 800, 650)
                + ScoreContains(keyName, trimmedQuery, 500)
                + ScoreContains(handlerClsid, trimmedQuery, 450)
                + ScoreContains(commandText, trimmedQuery, 350)
                + ScoreContains(registryPath, trimmedQuery, 280)
                + ScoreContains(backendRegistryPath, trimmedQuery, 280)
                + ScoreContains(sourceRootPath, trimmedQuery, 240)
                + ScoreContains(filePath, trimmedQuery, 240);

        foreach (var token in tokens)
        {
            score += fields.Any(field => Contains(field, token)) ? 10 : 0;
        }

        return true;
    }

    private static List<string> Tokenize(string query)
    {
        return query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool AnyFieldContains(IReadOnlyList<string?> fields, string token)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (Contains(fields[i], token))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string? field, string token)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        return field.Contains(token, StringComparison.OrdinalIgnoreCase)
               || NormalizePunctuation(field).Contains(NormalizePunctuation(token), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(
        string? field,
        string normalizedField,
        string token,
        string normalizedToken)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        return field.Contains(token, StringComparison.OrdinalIgnoreCase)
               || normalizedField.Contains(normalizedToken, StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreField(string? value, string query, int exact, int startsWith, int contains)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (string.Equals(value.Trim(), query, StringComparison.OrdinalIgnoreCase))
        {
            return exact;
        }

        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return startsWith;
        }

        return Contains(value, query) ? contains : 0;
    }

    private static int ScoreContains(string? value, string query, int score)
    {
        return Contains(value, query) ? score : 0;
    }

    private static string NormalizePunctuation(string value)
    {
        return new string(value
            .Where(static character => !char.IsPunctuation(character)
                                       && !char.IsSeparator(character)
                                       && !char.IsSymbol(character))
            .ToArray());
    }

    private static string GetShellPathTail(string? registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath))
        {
            return string.Empty;
        }

        var normalized = registryPath.Replace('/', '\\').TrimEnd('\\');
        var lastSeparatorIndex = normalized.LastIndexOf('\\');
        return lastSeparatorIndex >= 0
            ? normalized[(lastSeparatorIndex + 1)..]
            : normalized;
    }
}
