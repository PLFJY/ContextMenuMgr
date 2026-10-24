using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Backend.Services;

/// <summary>Physical Applications registration operations; no file-association changes.</summary>
internal static class OpenWithRegistration
{
    internal static readonly object Sync = new();
    private const string VisibilityValue = "NoOpenWith";
    private const string OwnershipMarker = "ContextMenuMgrPlus.OpenWithVisibility";
    private const string OwnershipPrefix = "ContextMenuMgrPlus.OpenWith.v1:";

    internal static void ValidateUser(BackendUserContext context)
    {
        var sid = new System.Security.Principal.SecurityIdentifier(context.Sid);
        if (!sid.IsAccountSid())
            throw new InvalidOperationException("Open With requires an interactive account SID.");
        using var hive = Registry.Users.OpenSubKey(context.Sid)
            ?? throw new InvalidOperationException("The frontend user's registry hive is not loaded.");
    }

    internal static string ValidatePath(string path, BackendUserContext context, bool deleted = false)
    {
        ValidateUser(context);
        var userPrefix = $@"HKEY_USERS\{context.Sid}\Software\Classes\Applications\";
        const string machinePrefix = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Applications\";
        var prefix = path.StartsWith(userPrefix, StringComparison.OrdinalIgnoreCase) ? userPrefix
            : path.StartsWith(machinePrefix, StringComparison.OrdinalIgnoreCase) ? machinePrefix
            : throw new UnauthorizedAccessException("The path is outside the caller's Open With Applications registrations.");
        var name = path[prefix.Length..];
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/')
            || name is "." or ".." || name != name.Trim() || name.Any(char.IsControl)
            || !name.Contains('.') || (!deleted && name.EndsWith(".deleted", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Invalid Open With application key.");
        return path;
    }

    internal static RegistryKey? Open(string path, bool writable = false)
    {
        var isMachine = path.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase);
        // Explorer uses the native Classes view, even when the service is built for x86.
        using var root = RegistryKey.OpenBaseKey(isMachine ? RegistryHive.LocalMachine : RegistryHive.Users,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        return root.OpenSubKey(path[(path.IndexOf('\\') + 1)..], writable);
    }

    internal static void Create(string path, string displayName, string command, CancellationToken cancellationToken)
    {
        using var transaction = new RegistryTransaction();
        using var key = transaction.Create(path, requireNew: true);
        key.SetValue("FriendlyAppName", displayName, RegistryValueKind.String);
        using var commandKey = transaction.Create(path + @"\shell\open\command");
        commandKey.SetValue(null, command, RegistryValueKind.String);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        VerifyCommitted(() =>
        {
            using var actual = Open(path);
            using var actualCommand = Open(path + @"\shell\open\command");
            return actual?.GetValue("FriendlyAppName") as string == displayName
                && actual.GetValueKind("FriendlyAppName") == RegistryValueKind.String
                && actualCommand?.GetValue(null) as string == command
                && actualCommand.GetValueKind(string.Empty) == RegistryValueKind.String;
        });
    }

    internal static void Update(string path, string commandSubPath, string? displayName, string? command,
        string expectedStamp, string? expectedCommand, CancellationToken cancellationToken)
    {
        using var transaction = new RegistryTransaction();
        using var key = transaction.Open(path, writable: true)
            ?? throw new InvalidOperationException("The Open With registration no longer exists.");
        using var commandKey = transaction.Open(path + "\\" + commandSubPath, writable: true)
            ?? throw new InvalidOperationException("The Open With command no longer exists.");
        var verbName = commandSubPath.Split('\\')[1];
        if (!CanManage(key, verbName, commandKey, expectedCommand, path, transaction)
            || EditStamp(key, path, transaction) != expectedStamp
            || commandKey.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != expectedCommand)
            throw new InvalidOperationException("The Open With registration changed. Refresh before editing.");
        // Do not change command generation underneath a visibility ownership marker.
        if (command is not null && command != expectedCommand && ShellVerbVisibility.CaptureValue(key, OwnershipMarker).Existed)
            throw new InvalidOperationException("Enable the Applications registration before changing its command.");
        var nameBefore = ShellVerbVisibility.CaptureValue(key, "FriendlyAppName");
        if (displayName is not null && nameBefore.Existed
            && nameBefore.Kind is not ((int)RegistryValueKind.String or (int)RegistryValueKind.ExpandString))
            throw new InvalidOperationException("The FriendlyAppName value has an unsupported type; it was preserved.");
        if (displayName is not null) key.SetValue("FriendlyAppName", displayName,
            nameBefore.Existed ? (RegistryValueKind)nameBefore.Kind : RegistryValueKind.String);
        if (command is not null) commandKey.SetValue(null, command, commandKey.GetValueKind(string.Empty));
        var after = EditStamp(key, path, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        VerifyCommitted(() =>
        {
            using var actual = Open(path);
            return actual is not null && EditStamp(actual, path) == after;
        });
    }

    internal static string EditStamp(RegistryKey key, string path) => EditStamp(key, path, null);

    internal static string CommandIdentity(RegistryKey key, string path) => OwnershipValue(key, path);

    private static string EditStamp(RegistryKey key, string path, RegistryTransaction? transaction)
        => Hash(OwnershipValue(key, path, transaction) + VisibilityStamp(key)
            + JsonSerializer.Serialize(ShellVerbVisibility.CaptureValue(key, "FriendlyAppName")));

    private static void VerifyCommitted(Func<bool> verify)
    {
        try
        {
            if (verify()) return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("OpenWithOperationUncertain", ex);
        }
        throw new InvalidOperationException("OpenWithOperationUncertain");
    }

    internal static string VisibilityStamp(RegistryKey key)
        => Hash(JsonSerializer.Serialize(new[] { ShellVerbVisibility.CaptureValue(key, VisibilityValue),
            ShellVerbVisibility.CaptureValue(key, OwnershipMarker) }));

    internal static string RegistrationStamp(RegistryKey key, string path, bool includeVisibility = true)
        => RegistrationStamp(key, path, includeVisibility, null);

    private static string RegistrationStamp(RegistryKey key, string path, bool includeVisibility,
        RegistryTransaction? transaction)
    {
        var material = new StringBuilder(path.ToUpperInvariant());
        var nodes = 0;
        var values = 0;
        Read(key, path, 0);
        return Hash(material.ToString());

        void Read(RegistryKey current, string currentPath, int depth)
        {
            if (++nodes > 512 || depth > 16) throw new InvalidOperationException("OpenWithRegistrationTooLarge");
            material.Append('\n').Append(currentPath.ToUpperInvariant());
            if (includeVisibility)
            {
                var error = RegQueryInfoKey(current.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var written);
                if (error != 0) throw new Win32Exception(error);
                material.Append(':').Append(written);
            }
            foreach (var name in current.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!includeVisibility && depth == 0 && (name.Equals(VisibilityValue, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(OwnershipMarker, StringComparison.OrdinalIgnoreCase))) continue;
                if (++values > 4096) throw new InvalidOperationException("OpenWithRegistrationTooLarge");
                material.Append(JsonSerializer.Serialize(ShellVerbVisibility.CaptureValue(current, name)));
                if (material.Length > 1024 * 1024) throw new InvalidOperationException("OpenWithRegistrationTooLarge");
            }
            foreach (var name in current.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                var childPath = currentPath + "\\" + name;
                using var child = transaction is null ? current.OpenSubKey(name) : transaction.Open(childPath);
                if (child is null) throw new InvalidOperationException("OpenWithStaleReview");
                Read(child, childPath, depth + 1);
            }
        }
    }

    internal static bool CanManage(RegistryKey app, string? verbName, RegistryKey? command, string? target)
        => CanManage(app, verbName, command, target, null, null);

    private static bool CanManage(RegistryKey app, string? verbName, RegistryKey? command, string? target,
        string? path, RegistryTransaction? transaction)
    {
        if (verbName is null || command is null || string.IsNullOrWhiteSpace(target)
            || command.GetValueKind(string.Empty) is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            return false;
        using var shell = transaction is null ? app.OpenSubKey("shell") : transaction.Open(path + @"\shell");
        foreach (var name in shell?.GetSubKeyNames() ?? [])
        {
            var verbPath = path + @"\shell\" + name;
            using var verb = transaction is null ? shell!.OpenSubKey(name) : transaction.Open(verbPath);
            using var cmd = transaction is null ? verb?.OpenSubKey("command") : transaction.Open(verbPath + @"\command");
            using var drop = transaction is null ? verb?.OpenSubKey("DropTarget") : transaction.Open(verbPath + @"\DropTarget");
            using var dde = transaction is null ? verb?.OpenSubKey("ddeexec") : transaction.Open(verbPath + @"\ddeexec");
            if (verb is null || drop is not null || dde is not null
                || verb.GetValueNames().Any(n => new[] { "DelegateExecute", "ExplorerCommandHandler", "SubCommands", "ExtendedSubCommandsKey" }.Contains(n, StringComparer.OrdinalIgnoreCase))
                || cmd?.GetValueNames().Any(n => n.Equals("DelegateExecute", StringComparison.OrdinalIgnoreCase)) == true)
                return false;
        }
        return true;
    }

    internal static string VisibilityOwnership(RegistryKey key, string path, bool manageable)
    {
        var visibility = ShellVerbVisibility.CaptureValue(key, VisibilityValue);
        var marker = ShellVerbVisibility.CaptureValue(key, OwnershipMarker);
        if (!visibility.Existed) return marker.Existed ? "Changed" : "Enabled";
        if (!marker.Existed) return "Unknown";
        return manageable && visibility.Kind == (int)RegistryValueKind.String && visibility.StringValue == string.Empty
            && marker.Kind == (int)RegistryValueKind.String && marker.StringValue == OwnershipValue(key, path)
                ? "Owned" : "Changed";
    }

    private static string OwnershipValue(RegistryKey key, string path, RegistryTransaction? transaction = null)
    {
        // Bind ownership to this physical hive/path and every command-bearing verb.
        using var shell = transaction is null ? key.OpenSubKey("shell") : transaction.Open(path + @"\shell");
        if (shell is null)
            throw new InvalidOperationException("The application registration has no shell commands.");
        var material = new StringBuilder(path.ToUpperInvariant());
        foreach (var name in shell.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            var verbPath = path + @"\shell\" + name;
            using var verb = transaction is null ? shell.OpenSubKey(name) : transaction.Open(verbPath);
            if (verb is null) throw new InvalidOperationException("The application registration changed while being read.");
            using var command = transaction is null ? verb.OpenSubKey("command") : transaction.Open(verbPath + @"\command");
            material.Append('\n').Append(name.ToUpperInvariant()).Append(':')
                .Append(JsonSerializer.Serialize(new[] {
                    ShellVerbVisibility.CaptureValue(verb, string.Empty),
                    ShellVerbVisibility.CaptureValue(verb, "DelegateExecute"),
                    ShellVerbVisibility.CaptureValue(verb, "ExplorerCommandHandler"),
                    command is null ? null : ShellVerbVisibility.CaptureValue(command, string.Empty),
                    command is null ? null : ShellVerbVisibility.CaptureValue(command, "DelegateExecute") }));
        }
        return OwnershipPrefix + Hash(material.ToString());
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr keyClass, IntPtr classLength,
        IntPtr reserved, IntPtr subKeyCount, IntPtr maxSubKeyName, IntPtr maxClass, IntPtr valueCount,
        IntPtr maxValueName, IntPtr maxValueLength, IntPtr securityLength, out long lastWriteTime);

    internal static void SetEnabled(string path, bool enabled, string? expectedStamp, string commandSubPath, string? expectedCommand,
        string? expectedEditStamp = null, string? expectedRegistrationStamp = null)
    {
        using var transaction = new RegistryTransaction();
        using var key = transaction.Open(path, writable: true)
            ?? throw new InvalidOperationException("The Open With registration no longer exists.");
        var before = ShellVerbVisibility.CaptureValue(key, VisibilityValue);
        var ownerBefore = ShellVerbVisibility.CaptureValue(key, OwnershipMarker);
        if (expectedStamp is not null && !string.Equals(VisibilityStamp(key), expectedStamp, StringComparison.Ordinal))
            throw new InvalidOperationException("The Open With visibility value changed. Refresh before retrying.");
        if (expectedEditStamp is not null && EditStamp(key, path, transaction) != expectedEditStamp)
            throw new InvalidOperationException("The Open With registration changed. Refresh before retrying.");
        if (expectedRegistrationStamp is not null && RegistrationStamp(key, path, true, transaction) != expectedRegistrationStamp)
            throw new InvalidOperationException("OpenWithStaleReview");
        using var selectedCommand = transaction.Open(path + "\\" + commandSubPath);
        if (selectedCommand is null || !string.Equals(selectedCommand.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, expectedCommand, StringComparison.Ordinal))
            throw new InvalidOperationException("The Open With command changed. Refresh before retrying.");
        if (!CanManage(key, commandSubPath.Split('\\')[1], selectedCommand, expectedCommand, path, transaction))
            throw new InvalidOperationException("OpenWithUnsupportedRegistration");
        var ownedValue = OwnershipValue(key, path, transaction);
        if (enabled && before.Existed
            && (before.Kind != (int)RegistryValueKind.String || before.StringValue != string.Empty
                || ownerBefore.Kind != (int)RegistryValueKind.String || ownerBefore.StringValue != ownedValue))
            throw new InvalidOperationException("OpenWithExternalVisibility");

        // Never overwrite an existing third-party value, including an empty REG_SZ.
        if ((!enabled && before.Existed) || (enabled && !before.Existed))
            return;

        if (!enabled && ownerBefore.Existed
            && (ownerBefore.Kind != (int)RegistryValueKind.String || ownerBefore.StringValue != ownedValue))
            throw new InvalidOperationException("OpenWithExternalVisibility");

        if (!ShellVerbVisibility.ValueMatches(key, before) || !ShellVerbVisibility.ValueMatches(key, ownerBefore))
            throw new InvalidOperationException("The Open With registration changed before the write.");
        var after = enabled
            ? new PersistedRegistryValueSnapshot { Name = VisibilityValue, Existed = false }
            : new PersistedRegistryValueSnapshot { Name = VisibilityValue, Existed = true,
                Kind = (int)RegistryValueKind.String, StringValue = string.Empty };
        var ownerAfter = enabled
            ? new PersistedRegistryValueSnapshot { Name = OwnershipMarker, Existed = false }
            : new PersistedRegistryValueSnapshot { Name = OwnershipMarker, Existed = true,
                Kind = (int)RegistryValueKind.String, StringValue = ownedValue };
        // Keep the documented empty REG_SZ. Commit it and its provenance together;
        // a conflicting non-transacted installer write aborts this transaction.
        ShellVerbVisibility.RestoreValue(key, after);
        ShellVerbVisibility.RestoreValue(key, ownerAfter);
        if (!ShellVerbVisibility.ValueMatches(key, after) || !ShellVerbVisibility.ValueMatches(key, ownerAfter))
            throw new InvalidOperationException("The Open With staged registry mutation could not be verified.");
        transaction.Commit();
        using var readBack = Open(path)
            ?? throw new InvalidOperationException("The Open With registration disappeared after the write.");
        if (!ShellVerbVisibility.ValueMatches(readBack, after) || !ShellVerbVisibility.ValueMatches(readBack, ownerAfter)
            || OwnershipValue(readBack, path) != ownedValue)
            throw new InvalidOperationException("The Open With registry mutation could not be verified. Refresh to check the physical state.");
    }

    internal static void Rename(string sourcePath, string destinationPath)
    {
        if (!sourcePath[..sourcePath.LastIndexOf('\\')].Equals(
                destinationPath[..destinationPath.LastIndexOf('\\')], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Open With recovery must stay within the same Applications key.");
        using var source = Open(sourcePath, writable: true)
            ?? throw new InvalidOperationException($"The Open With source no longer exists: {sourcePath}");
        using var collision = Open(destinationPath);
        if (collision is not null)
            throw new InvalidOperationException($"The Open With destination already exists: {destinationPath}");

        // Native rename is atomic and preserves the tree and ACL. No copy/delete fallback:
        // an interrupted copy must never become a recovery backup or overwrite a registration.
        var status = RegRenameKey(source.Handle, null, destinationPath[(destinationPath.LastIndexOf('\\') + 1)..]);
        if (status != 0)
            throw new Win32Exception(status, $"Unable to rename Open With registration: {sourcePath}");
        using var oldKey = Open(sourcePath);
        using var newKey = Open(destinationPath);
        if (oldKey is not null || newKey is null)
            throw new InvalidOperationException("Open With rename read-back failed. Existing keys were retained; refresh before recovery.");
    }

    internal static void Purge(string backupPath)
    {
        using var transaction = new RegistryTransaction();
        transaction.DeleteTree(backupPath);
        transaction.Commit();
        using var readBack = Open(backupPath);
        if (readBack is not null)
            throw new InvalidOperationException("The Open With backup purge could not be verified.");
    }

    /// <summary>Short-lived TxR operations; failure rolls back without overwriting other writers.</summary>
    private sealed class RegistryTransaction : IDisposable
    {
        private readonly SafeFileHandle _handle = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, null);
        private bool _committed;

        public RegistryTransaction()
        {
            if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public RegistryKey? Open(string path, bool writable = false)
        {
            var rights = (int)RegistryRights.ReadKey | (writable ? (int)RegistryRights.WriteKey : 0) | ViewFlag;
            var status = RegOpenKeyTransacted(RootHandle(path), Relative(path), 0, rights, out var key, _handle, IntPtr.Zero);
            if (status == 0) return RegistryKey.FromHandle(key);
            key.Dispose();
            if (status is 2 or 3) return null;
            throw new Win32Exception(status);
        }

        public RegistryKey Create(string path, bool requireNew = false)
        {
            // Anchor creation beneath an already-open hive. A concurrent profile unload
            // must fail this handle, never create a replacement SID key under HKEY_USERS.
            var anchorEnd = path.IndexOf('\\', path.IndexOf('\\') + 1);
            using var anchor = Open(path[..anchorEnd], writable: true)
                ?? throw new InvalidOperationException("The frontend user's registry hive is not loaded.");
            var status = RegCreateKeyTransacted(anchor.Handle.DangerousGetHandle(), path[(anchorEnd + 1)..], 0, null, 0,
                (int)(RegistryRights.ReadKey | RegistryRights.WriteKey) | ViewFlag, IntPtr.Zero,
                out var key, out var disposition, _handle, IntPtr.Zero);
            if (status == 0 && (!requireNew || disposition == 1)) return RegistryKey.FromHandle(key);
            key.Dispose();
            if (status != 0) throw new Win32Exception(status);
            throw new InvalidOperationException("This Open With application registration already exists.");
        }

        public void DeleteTree(string path)
        {
            using (var key = Open(path) ?? throw new InvalidOperationException("The Open With backup no longer exists."))
                foreach (var name in key.GetSubKeyNames()) DeleteTree(path + "\\" + name);
            var status = RegDeleteKeyTransacted(RootHandle(path), Relative(path), ViewFlag, 0, _handle, IntPtr.Zero);
            if (status != 0) throw new Win32Exception(status);
        }

        public void Commit()
        {
            if (!CommitTransaction(_handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _committed = true;
        }

        public void Dispose()
        {
            if (!_committed && !_handle.IsInvalid) RollbackTransaction(_handle);
            _handle.Dispose();
        }

        private static int ViewFlag => Environment.Is64BitOperatingSystem ? 0x100 : 0;
        private static string Relative(string path) => path[(path.IndexOf('\\') + 1)..];
        // Use the predefined root handle; RegistryKey.Handle would request write access
        // to all of HKEY_USERS, which an ordinary caller does not need or possess.
        private static IntPtr RootHandle(string path) => new(unchecked((int)(
            path.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase) ? 0x80000002 : 0x80000003)));

        [DllImport("KtmW32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr unitOfWork, int options,
            int isolationLevel, int isolationFlags, int timeout, string? description);
        [DllImport("KtmW32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CommitTransaction(SafeFileHandle transaction);
        [DllImport("KtmW32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RollbackTransaction(SafeFileHandle transaction);
        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyTransactedW", CharSet = CharSet.Unicode)]
        private static extern int RegOpenKeyTransacted(IntPtr root, string path, int options, int access,
            out SafeRegistryHandle key, SafeFileHandle transaction, IntPtr extendedParameter);
        [DllImport("advapi32.dll", EntryPoint = "RegDeleteKeyTransactedW", CharSet = CharSet.Unicode)]
        private static extern int RegDeleteKeyTransacted(IntPtr root, string path, int view, int reserved,
            SafeFileHandle transaction, IntPtr extendedParameter);
        [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyTransactedW", CharSet = CharSet.Unicode)]
        private static extern int RegCreateKeyTransacted(IntPtr root, string path, int reserved, string? keyClass,
            int options, int access, IntPtr securityAttributes, out SafeRegistryHandle key, out int disposition,
            SafeFileHandle transaction, IntPtr extendedParameter);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegRenameKey(SafeRegistryHandle key, string? subKeyName, string newKeyName);

}
