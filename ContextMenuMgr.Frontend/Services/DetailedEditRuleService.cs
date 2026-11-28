using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the detailed Edit Rule Service.
/// </summary>
public sealed class DetailedEditRuleService
{
    private readonly IBackendClient _backendClient;
    private readonly FrontendSettingsService _settingsService;
    private readonly LocalizationService _localization;
    private readonly Func<string?>? _interactiveUserSid;
#if CONTEXTMENUMGR_IN_PROCESS
    private readonly PortableInteractiveUser? _portableUser;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="DetailedEditRuleService"/> class.
    /// </summary>
    public DetailedEditRuleService(IBackendClient backendClient, FrontendSettingsService settingsService,
        LocalizationService localization)
        : this(backendClient, settingsService, localization, null
#if CONTEXTMENUMGR_IN_PROCESS
            , null
#endif
            )
    {
    }

    internal DetailedEditRuleService(
        IBackendClient backendClient,
        FrontendSettingsService settingsService,
        LocalizationService localization,
        Func<string?>? interactiveUserSid = null
#if CONTEXTMENUMGR_IN_PROCESS
        , PortableInteractiveUser? portableUser = null
#endif
        )
    {
        _backendClient = backendClient;
        _settingsService = settingsService;
        _localization = localization;
        _interactiveUserSid = interactiveUserSid;
#if CONTEXTMENUMGR_IN_PROCESS
        _portableUser = portableUser;
#endif
    }

    /// <summary>
    /// Executes read Boolean.
    /// </summary>
    public bool ReadBoolean(DetailedEditRuleDefinition definition)
    {
        foreach (var clause in definition.Clauses)
        {
            var currentValue = ReadValue(clause);
            if (currentValue is null)
            {
                continue;
            }

            if (Matches(currentValue, clause.TurnOnValue, clause.ValueKind))
            {
                return true;
            }

            if (Matches(currentValue, clause.TurnOffValue, clause.ValueKind))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Executes write Boolean Async.
    /// </summary>
    public async Task WriteBooleanAsync(
        DetailedEditRuleDefinition definition,
        bool enabled,
        CancellationToken cancellationToken)
    {
        foreach (var clause in definition.Clauses)
        {
            var targetValue = enabled ? clause.TurnOnValue : clause.TurnOffValue;
            await WriteValueAsync(clause, targetValue, cancellationToken);
        }
    }

    /// <summary>
    /// Executes read Number.
    /// </summary>
    public int ReadNumber(DetailedEditRuleDefinition definition)
    {
        var clause = definition.Clauses[0];
        var value = ReadValue(clause);
        if (value is null)
        {
            return definition.DefaultNumber;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return definition.DefaultNumber;
        }

        return Math.Clamp(number, definition.MinNumber, definition.MaxNumber);
    }

    /// <summary>
    /// Executes write Number Async.
    /// </summary>
    public async Task WriteNumberAsync(
        DetailedEditRuleDefinition definition,
        int value,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(value, definition.MinNumber, definition.MaxNumber);
        var clause = definition.Clauses[0];
        await WriteValueAsync(clause, clamped.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>
    /// Executes read String.
    /// </summary>
    public string ReadString(DetailedEditRuleDefinition definition)
    {
        var clause = definition.Clauses[0];
        return ReadValue(clause) ?? string.Empty;
    }

    /// <summary>
    /// Executes write String Async.
    /// </summary>
    public async Task WriteStringAsync(
        DetailedEditRuleDefinition definition,
        string value,
        CancellationToken cancellationToken)
    {
        var clause = definition.Clauses[0];
        await WriteValueAsync(clause, value, cancellationToken);
    }

    private string? ReadValue(DetailedEditRuleClauseDefinition clause)
    {
        return clause.StorageKind switch
        {
            RuleStorageKind.Registry => ReadRegistryValue(clause),
            RuleStorageKind.Ini => ReadIniValue(clause with { Path = ResolvePath(clause.Path) }),
            _ => null
        };
    }

    private async Task WriteValueAsync(
        DetailedEditRuleClauseDefinition clause,
        string? value,
        CancellationToken cancellationToken)
    {
        if (clause.StorageKind == RuleStorageKind.Registry)
        {
            if (RegistryProtectionDialog.ShouldBlockProtectedPathMutation(_settingsService, clause.Path))
            {
                throw new BackendRequestException(
                    _localization.Translate("RegistryProtectionBlocksEditMessage"),
                    Contracts.PipeErrorCodes.RegistryWriteProtectionEnabled);
            }

            await _backendClient.SetDetailedEditRuleValueAsync(
                clause.StorageKind.ToString(),
                clause.Path,
                clause.Section,
                clause.KeyName,
                clause.ValueKind.ToString(),
                value,
                GetCurrentUserSid(),
                cancellationToken);
            return;
        }

        // 非 Registry 类型（如 Ini）可以同步写入本地文件
        WriteNonRegistryValue(clause with { Path = ResolvePath(clause.Path) }, value);
    }

    private static void WriteNonRegistryValue(DetailedEditRuleClauseDefinition clause, string? value)
    {
        switch (clause.StorageKind)
        {
            case RuleStorageKind.Ini:
                WriteIniValue(clause, value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported storage kind for synchronous write: {clause.StorageKind}");
        }
    }

    private string? ReadRegistryValue(DetailedEditRuleClauseDefinition clause)
    {
        var (baseKey, subPath) = OpenRegistryBaseKey(clause.Path);
        using (baseKey)
        {
        using var key = baseKey?.OpenSubKey(subPath, writable: false);
        var value = key?.GetValue(clause.KeyName);
        return value switch
        {
            null => null,
            byte[] bytes => string.Join(" ", bytes.Select(static b => b.ToString("X2", CultureInfo.InvariantCulture))),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
        }
    }

    private string? ReadIniValue(DetailedEditRuleClauseDefinition clause)
    {
        if (string.IsNullOrWhiteSpace(clause.Section))
        {
            return null;
        }

        var buffer = new char[1024];
        var length = GetPrivateProfileString(
            clause.Section,
            clause.KeyName,
            string.Empty,
            buffer,
            buffer.Length,
            clause.Path);

        return length == 0 ? null : new string(buffer, 0, length);
    }

    private static void WriteIniValue(DetailedEditRuleClauseDefinition clause, string? value)
    {
        if (string.IsNullOrWhiteSpace(clause.Section))
        {
            throw new InvalidOperationException("INI section is required.");
        }

        var directory = Path.GetDirectoryName(clause.Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!WritePrivateProfileString(clause.Section, clause.KeyName, value, clause.Path))
        {
            throw new InvalidOperationException($"Unable to write INI value: {clause.Path}");
        }
    }

    private static bool Matches(string actualValue, string? expectedValue, RegistryValueKind kind)
    {
        if (expectedValue is null)
        {
            return false;
        }

        return kind switch
        {
            RegistryValueKind.Binary => string.Equals(
                NormalizeBinary(actualValue),
                NormalizeBinary(expectedValue),
                StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string NormalizeBinary(string value)
    {
        return string.Join(
            " ",
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private (RegistryKey? BaseKey, string SubPath) OpenRegistryBaseKey(string fullPath)
    {
        var normalized = fullPath.Replace('/', '\\');
        var separatorIndex = normalized.IndexOf('\\');
        var root = separatorIndex >= 0 ? normalized[..separatorIndex] : normalized;
        var subPath = separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : string.Empty;

        var sid = GetCurrentUserSid();
        return root.ToUpperInvariant() switch
        {
            "HKEY_CLASSES_ROOT" or "HKCR" => (OpenClassesRoot(), subPath),
            "HKEY_CURRENT_USER" or "HKCU" => (OpenUserHive(sid), subPath),
            "HKEY_LOCAL_MACHINE" or "HKLM" => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default), subPath),
            "HKEY_USERS" or "HKU" => (RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default), subPath),
            _ => throw new InvalidOperationException($"Unsupported registry root: {fullPath}")
        };
    }

    private string? GetCurrentUserSid()
    {
        if (_interactiveUserSid is not null) return _interactiveUserSid();
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch
        {
            return null;
        }
    }

    private RegistryKey OpenClassesRoot()
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (_portableUser is not null) return _portableUser.OpenClassesRoot(RegistryView.Default);
#endif
        return RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
    }

    private static RegistryKey OpenUserHive(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("The interactive user's SID could not be verified.");
        var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        try
        {
            return users.OpenSubKey(sid, writable: false) ?? throw new InvalidOperationException("The interactive user's registry hive is unavailable.");
        }
        finally { users.Dispose(); }
    }

    private string ResolvePath(string path)
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (_portableUser is not null) return _portableUser.ResolveUserPath(path);
#endif
        return Environment.ExpandEnvironmentVariables(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPrivateProfileString(
        string lpAppName,
        string lpKeyName,
        string lpDefault,
        char[] lpReturnedString,
        int nSize,
        string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(
        string lpAppName,
        string lpKeyName,
        string? lpString,
        string lpFileName);
}
