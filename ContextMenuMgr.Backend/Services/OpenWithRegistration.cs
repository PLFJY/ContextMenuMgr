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

    internal static RegistryKey CreateNewUserApplication(string path, BackendUserContext context)
    {
        ValidatePath(path, context);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.Users,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        using var hive = root.OpenSubKey(context.Sid, writable: true)
            ?? throw new InvalidOperationException("The frontend user's registry hive is not loaded.");
        using var parent = hive.CreateSubKey(@"Software\Classes\Applications", writable: true);
        var status = RegCreateKeyEx(parent.Handle, path[(path.LastIndexOf('\\') + 1)..], 0, null,
            0, (int)(RegistryRights.ReadKey | RegistryRights.WriteKey), IntPtr.Zero, out var handle, out var disposition);
        if (status != 0)
        {
            handle.Dispose();
            throw new Win32Exception(status);
        }
        if (disposition != 1)
        {
            handle.Dispose();
            throw new InvalidOperationException("This Open With application registration already exists.");
        }
        return RegistryKey.FromHandle(handle);
    }

    internal static string VisibilityStamp(RegistryKey key)
        => Hash(JsonSerializer.Serialize(new[] { ShellVerbVisibility.CaptureValue(key, VisibilityValue),
            ShellVerbVisibility.CaptureValue(key, OwnershipMarker) }));

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

    internal static void SetEnabled(string path, bool enabled, string? expectedStamp, string commandSubPath, string? expectedCommand)
    {
        using var transaction = new RegistryTransaction();
        using var key = transaction.Open(path, writable: true)
            ?? throw new InvalidOperationException("The Open With registration no longer exists.");
        var before = ShellVerbVisibility.CaptureValue(key, VisibilityValue);
        var ownerBefore = ShellVerbVisibility.CaptureValue(key, OwnershipMarker);
        if (expectedStamp is not null && !string.Equals(VisibilityStamp(key), expectedStamp, StringComparison.Ordinal))
            throw new InvalidOperationException("The Open With visibility value changed. Refresh before retrying.");
        using var selectedCommand = transaction.Open(path + "\\" + commandSubPath);
        if (selectedCommand is null || !string.Equals(selectedCommand.GetValue(null)?.ToString(), expectedCommand, StringComparison.Ordinal))
            throw new InvalidOperationException("The Open With command changed. Refresh before retrying.");
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
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegRenameKey(SafeRegistryHandle key, string? subKeyName, string newKeyName);

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, int reserved, string? keyClass,
        int options, int desiredAccess, IntPtr securityAttributes, out SafeRegistryHandle result, out int disposition);
}
