#if CONTEXTMENUMGR_IN_PROCESS
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

internal static class DetailedEditRegistryTarget
{
    private const string NotepadClass = @"CLSID\{b298d29a-a6ed-11de-ba8c-a68e55d89593}";
    private const string NotepadSettings = NotepadClass + @"\Settings";
    private const string MachineClassesPath = @"SOFTWARE\Classes";

    internal enum Scope { Machine, CurrentUser, ExplicitUser, Classes }

    internal static (Scope Scope, string SubPath) Parse(string? path)
    {
        var parts = (path ?? string.Empty).Replace('/', '\\').Split('\\');
        if (parts.Length < 2 || parts.Any(part => string.IsNullOrWhiteSpace(part)
            || part is "." or ".." || part.Contains('\0')))
            throw new InvalidOperationException("Detailed Edit requires an unambiguous registry subkey.");

        var scope = parts[0].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => Scope.Machine,
            "HKCU" or "HKEY_CURRENT_USER" => Scope.CurrentUser,
            "HKU" or "HKEY_USERS" when parts.Length > 2 => Scope.ExplicitUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => Scope.Classes,
            _ => throw new InvalidOperationException("Detailed Edit registry root is not supported by In-process Portable.")
        };
        return (scope, string.Join('\\', parts.Skip(1)));
    }

    internal static PipeRequest Bind(PipeRequest request, string sid, Func<RegistryKey> openMergedClasses,
        Func<RegistryKey?> openUserClasses, Func<RegistryKey?> openMachineClasses)
    {
        if (request.RuleStorageKind != nameof(RuleStorageKind.Registry))
            throw new InvalidOperationException("Detailed Edit requires a supported registry target.");

        var (scope, subPath) = Parse(request.RulePath);
        if (scope == Scope.Machine)
        {
            if (request.RuleUserSid is not null)
                throw new InvalidOperationException("Machine-scoped Detailed Edit does not accept a user SID.");
            return request;
        }

        if (request.RuleUserSid is not null && !string.Equals(request.RuleUserSid, sid, StringComparison.OrdinalIgnoreCase)
            || scope == Scope.ExplicitUser && !string.Equals(subPath.Split('\\', 2)[0], sid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Detailed Edit can only target the verified interactive user's registry hive.");

        if (scope != Scope.Classes)
            return request with { RuleUserSid = sid };

        if (!string.Equals(subPath, NotepadSettings, StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(request.RuleKeyName, "Title", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(request.RuleKeyName, "ShowIcon", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Detailed Edit does not define a physical write policy for this Classes rule.");

        using var merged = openMergedClasses();
        using var user = openUserClasses();
        using var machine = openMachineClasses();
        using var mergedClass = merged.OpenSubKey(NotepadClass);
        using var userClass = user?.OpenSubKey(NotepadClass);
        using var machineClass = machine?.OpenSubKey(NotepadClass);
        using var mergedSettings = mergedClass?.OpenSubKey("Settings");
        using var userSettings = userClass?.OpenSubKey("Settings");
        using var machineSettings = machineClass?.OpenSubKey("Settings");

        // Reuse the visible Settings key. A new Settings key needs one identifiable class owner.
        bool writeUser;
        if (mergedClass is null)
            throw new InvalidOperationException("The Notepad++ class is absent from the interactive user's Classes view.");
        if (mergedSettings is not null && userSettings is not null)
            writeUser = true;
        else if (mergedSettings is not null && machineSettings is not null)
            writeUser = false;
        else if (mergedSettings is null && userSettings is null && machineSettings is null
                 && (userClass is null) != (machineClass is null))
            writeUser = userClass is not null;
        else
            throw new InvalidOperationException("The physical source of the Notepad++ Settings key is ambiguous.");

        var physicalSettings = writeUser ? userSettings : machineSettings;
        if (mergedSettings is not null
            && !Equals(mergedSettings.GetValue(request.RuleKeyName, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                physicalSettings?.GetValue(request.RuleKeyName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)))
            throw new InvalidOperationException("The visible Notepad++ setting does not match its physical source.");

        var physicalPath = writeUser
            ? $@"HKEY_USERS\{sid}\Software\Classes\{NotepadSettings}"
            : $@"HKEY_LOCAL_MACHINE\{MachineClassesPath}\{NotepadSettings}";
        return request with { RulePath = physicalPath, RuleUserSid = writeUser ? sid : null };
    }

    internal static PipeRequest Bind(PipeRequest request, PortableInteractiveUser user)
    {
        var context = user.TryGetContext()
            ?? throw new InvalidOperationException("The verified interactive user's registry hive is unavailable.");
        return Bind(request, context.Sid,
            () => user.OpenClassesRoot(RegistryView.Default),
            () => Registry.Users.OpenSubKey($@"{context.Sid}\Software\Classes"),
            () => Registry.LocalMachine.OpenSubKey(MachineClassesPath));
    }
}
#endif
