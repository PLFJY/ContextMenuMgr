using System.Collections;
using System.Globalization;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

internal static class DiagnosticLogFormatter
{
    private const int MaxStringLength = 512;
    private const int MaxBinaryPreviewBytes = 32;

    public static string FormatRegistryPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? "<null>" : path;

    public static string FormatRegistryValue(string? valueName)
        => string.IsNullOrEmpty(valueName) ? "(Default)" : valueName;

    public static string FormatRegistryValueData(object? value, RegistryValueKind? kind = null)
    {
        if (value is null)
        {
            return "<null>";
        }

        var valueKind = kind ?? TryInferRegistryValueKind(value);
        if (valueKind == RegistryValueKind.Binary || value is byte[] bytes)
        {
            bytes = value as byte[] ?? [];
            var preview = bytes.Take(MaxBinaryPreviewBytes).Select(static b => b.ToString("X2", CultureInfo.InvariantCulture));
            var suffix = bytes.Length > MaxBinaryPreviewBytes ? " ...<truncated>" : string.Empty;
            return $"REG_BINARY Length={bytes.Length}, Preview={string.Join(' ', preview)}{suffix}";
        }

        if (value is string[] strings)
        {
            return Truncate(string.Join(";", strings));
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            return Truncate(string.Join(";", enumerable.Cast<object?>().Select(static item => item?.ToString() ?? "<null>")));
        }

        return Truncate(value.ToString() ?? string.Empty);
    }

    public static string FormatSid(BackendUserContext? context)
        => string.IsNullOrWhiteSpace(context?.Sid) ? "<null>" : context.Sid;

    public static string FormatUserHivePath(BackendUserContext context, string subPath)
        => $@"HKEY_USERS\{context.Sid}\{subPath.TrimStart('\\')}";

    public static string FormatException(Exception ex) => ex.ToString();

    public static string FormatAclRule(RegistryAccessRule rule)
        => $"Identity={rule.IdentityReference.Value}, Rights={FormatRegistryRights(rule.RegistryRights)}, Type={rule.AccessControlType}, Inheritance={rule.InheritanceFlags}, Propagation={rule.PropagationFlags}, IsInherited={rule.IsInherited}";

    public static string FormatRegistryRights(RegistryRights rights)
        => rights.ToString();

    public static string FormatBool(bool value) => value ? "true" : "false";

    public static string BuildRegistryOperationLog(
        string action,
        string? path,
        string? valueName = null,
        RegistryValueKind? kind = null,
        object? data = null,
        bool? writable = null,
        string? result = null)
    {
        return string.Join(
            ", ",
            new[]
            {
                $"Action={action}",
                $"Path={FormatRegistryPath(path)}",
                $"ValueName={FormatRegistryValue(valueName)}",
                kind is null ? null : $"ValueKind={kind}",
                data is null ? null : $"ValueData={FormatRegistryValueData(data, kind)}",
                writable is null ? null : $"Writable={FormatBool(writable.Value)}",
                string.IsNullOrWhiteSpace(result) ? null : $"Result={result}"
            }.Where(static part => part is not null));
    }

    public static string BuildAclOperationLog(
        string action,
        string? path,
        RegistryRights rights,
        string? result = null)
    {
        return string.Join(
            ", ",
            new[]
            {
                $"Action={action}",
                $"Path={FormatRegistryPath(path)}",
                $"Rights={FormatRegistryRights(rights)}",
                string.IsNullOrWhiteSpace(result) ? null : $"Result={result}"
            }.Where(static part => part is not null));
    }

    private static RegistryValueKind TryInferRegistryValueKind(object value)
    {
        return value switch
        {
            int => RegistryValueKind.DWord,
            long => RegistryValueKind.QWord,
            byte[] => RegistryValueKind.Binary,
            string[] => RegistryValueKind.MultiString,
            _ => RegistryValueKind.String
        };
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxStringLength
            ? value
            : string.Concat(value.AsSpan(0, MaxStringLength), "...<truncated>");
    }
}
