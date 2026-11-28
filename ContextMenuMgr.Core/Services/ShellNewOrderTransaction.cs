using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Backend.Services;

// The caller serializes service mutations. External writers are checked at every
// boundary; registry value/DACL updates do not offer an atomic compare-and-swap.
internal sealed class ShellNewOrderTransaction
{
    private readonly RegistryKey root;
    private readonly string path;
    private readonly FileLogger logger;
    private readonly State? _original;

    internal ShellNewOrderTransaction(RegistryKey root, string path, FileLogger logger)
    {
        this.root = root;
        this.path = path;
        this.logger = logger;
        _original = Read();
    }

    internal const RegistryRights LockRights = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete;

    private sealed record Value(bool Exists, RegistryValueKind Kind, byte[] Bytes)
    {
        internal static Value Read(RegistryKey key)
        {
            uint size = 0;
            var status = RegQueryValueEx(key.Handle, "Classes", IntPtr.Zero, out var kind, null, ref size);
            if (status == 2) return Missing;
            if (status != 0) throw new Win32Exception(status);
            if (size > 16 * 1024 * 1024) throw new InvalidOperationException("ShellNew Classes exceeds the safe read limit.");
            var bytes = new byte[size];
            status = RegQueryValueEx(key.Handle, "Classes", IntPtr.Zero, out kind, bytes, ref size);
            if (status != 0) throw new Win32Exception(status);
            Array.Resize(ref bytes, checked((int)size));
            return new(true, (RegistryValueKind)kind, bytes);
        }

        internal static Value Missing => new(false, RegistryValueKind.Unknown, []);
        internal static Value MultiString(IEnumerable<string> entries) => new(true, RegistryValueKind.MultiString,
            Encoding.Unicode.GetBytes(string.Join("\0", entries) + "\0\0"));

        internal string[] Entries()
        {
            if (!Exists) return [];
            if (Kind != RegistryValueKind.MultiString || Bytes.Length < 4 || Bytes.Length % 2 != 0
                || Bytes[^1] != 0 || Bytes[^2] != 0 || Bytes[^3] != 0 || Bytes[^4] != 0)
                throw new InvalidOperationException("ShellNew Classes has an unexpected registry type or malformed MULTI_SZ. It will be preserved unchanged.");
            var text = new UnicodeEncoding(false, false, true).GetString(Bytes);
            var entries = text.Length == 2 ? [] : text[..^2].Split('\0');
            if (entries.Any(string.IsNullOrEmpty))
                throw new InvalidOperationException("ShellNew Classes contains ambiguous empty entries. It will be preserved unchanged.");
            return entries;
        }

        internal bool Same(Value other) => Exists == other.Exists && Kind == other.Kind && Bytes.SequenceEqual(other.Bytes);
    }

    private sealed record State(Value Classes, byte[] Security);

    private static bool IsOwnedAce(GenericAce ace) => ace is CommonAce rule
        && !rule.IsCallback && rule.AceFlags == AceFlags.None && rule.AceQualifier == AceQualifier.AccessDenied
        && rule.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid)
        && (rule.AccessMask == (int)LockRights || rule.AccessMask == (int)RegistryRights.WriteKey);

    private static byte[] WithLock(byte[] original, bool locked)
    {
        var descriptor = new RawSecurityDescriptor(original, 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            if (locked) throw new InvalidOperationException("A null ShellNew DACL cannot be safely converted to a narrow lock.");
            return original;
        }
        var dacl = new RawAcl(descriptor.DiscretionaryAcl.Revision, descriptor.DiscretionaryAcl.Count + 1);
        if (locked) dacl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, (int)LockRights,
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), false, null));
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            if (!IsOwnedAce(ace)) dacl.InsertAce(dacl.Count, ace);
        descriptor.DiscretionaryAcl = dacl;
        return Binary(descriptor);
    }

    private static bool Locked(byte[] security)
    {
        var dacl = new RawSecurityDescriptor(security, 0).DiscretionaryAcl;
        return dacl is not null && dacl.Cast<GenericAce>().Any(ace => ace is CommonAce rule
            && rule.AceQualifier == AceQualifier.AccessDenied && rule.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid)
            && (rule.AccessMask & (int)LockRights) != 0);
    }

    private static byte[] Binary(RawSecurityDescriptor descriptor)
    {
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static byte[] ReadSecurity(RegistryKey key)
    {
        uint size = 0;
        var status = RegGetKeySecurity(key.Handle, 7 /* Owner | Group | DACL */, null, ref size);
        if (status != 122 /* ERROR_INSUFFICIENT_BUFFER */) throw new Win32Exception(status);
        var bytes = new byte[size];
        status = RegGetKeySecurity(key.Handle, 7, bytes, ref size);
        if (status != 0) throw new Win32Exception(status);
        return bytes;
    }

    internal static bool ReadLocked(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadPermissions);
        // Read failures propagate as Unknown, never as Locked/Unlocked.
        return key is not null && Locked(ReadSecurity(key));
    }

    private static string[] BuildOrder(Value original, IReadOnlyList<string> discovered, IReadOnlyList<string> proposed)
    {
        var known = discovered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (known.Count == 0 || known.Count != discovered.Count || proposed.Count != known.Count
            || proposed.Distinct(StringComparer.OrdinalIgnoreCase).Count() != proposed.Count || !known.SetEquals(proposed)
            || proposed.Any(x => string.IsNullOrWhiteSpace(x) || x.Contains('\0')))
            throw new InvalidOperationException("ShellNew order requires a complete, nonempty classic discovery and a permutation of its entries. No values were written.");
        var existing = original.Entries();
        var pending = new Queue<string>(proposed);
        // Preserve unknown entries, their spelling and relative slots. Even duplicate
        // existing entries are retained: we have no provenance authorizing deletion.
        var result = new List<string>();
        foreach (var entry in existing)
            result.Add(known.Contains(entry) && pending.Count > 0 ? pending.Dequeue() : entry);
        result.AddRange(pending);
        return result.ToArray();
    }

    private RegistryKey? Open(RegistryRights rights) => root.OpenSubKey(path,
        (rights & (RegistryRights.SetValue | RegistryRights.ChangePermissions)) != 0
            ? RegistryKeyPermissionCheck.ReadWriteSubTree : RegistryKeyPermissionCheck.ReadSubTree, rights);

    private State? Read()
    {
        using var key = Open(RegistryRights.QueryValues | RegistryRights.ReadPermissions);
        return key is null ? null : new State(Value.Read(key), ReadSecurity(key));
    }

    private static bool Same(State? a, State? b) => a is null ? b is null : b is not null && a.Classes.Same(b.Classes) && SameAcl(a.Security, b.Security);

    private static bool SameAcl(byte[] left, byte[] right)
    {
        static byte[] Comparable(byte[] bytes)
        {
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            // Windows computes SE_DACL_AUTO_INHERITED on an unprotected DACL.
            // Protection, Owner/Group and every raw ACE/flag still participate.
            descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
            return Binary(descriptor);
        }
        return Comparable(left).SequenceEqual(Comparable(right));
    }

    private void Require(State? expected)
    {
        var actual = Read();
        if (!Same(actual, expected))
        {
            logger.LogFireAndForget(RuntimeLogLevel.Warning, $"ShellNewOrderConflict: Path={root.Name}\\{path}, AclMatches={actual is not null && expected is not null && SameAcl(actual.Security, expected.Security)}, ValueMatches={actual is not null && expected is not null && actual.Classes.Same(expected.Classes)}.");
            throw new InvalidOperationException("ShellNew physical order value or ACL changed concurrently. No conflicting state will be overwritten.");
        }
    }

    private void WriteAcl(State before, State after)
    {
        Require(before);
        using var key = Open(RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)
            ?? throw new InvalidOperationException("ShellNew order key disappeared.");
        // Write only the raw DACL. RegistrySecurity/CommonAcl coalesces adjacent
        // same-SID deny ACEs, which would erase unknown third-party rule boundaries.
        // Owner/Group, inheritance policy and unknown ACEs are verified separately.
        var status = RegSetKeySecurity(key.Handle, 4 /* DACL only */, after.Security);
        if (status != 0) throw new Win32Exception(status);
        Require(after);
    }

    private void WriteValue(State before, State after)
    {
        Require(before);
        using var key = Open(RegistryRights.SetValue) ?? throw new InvalidOperationException("ShellNew order key disappeared.");
        var status = after.Classes.Exists
            ? RegSetValueEx(key.Handle, "Classes", 0, (uint)after.Classes.Kind, after.Classes.Bytes, (uint)after.Classes.Bytes.Length)
            : RegDeleteValue(key.Handle, "Classes");
        if (status != 0 && !(status == 2 && !after.Classes.Exists)) throw new Win32Exception(status);
        Require(after);
    }

    internal void Execute(bool locked, IReadOnlyList<string>? discovered, IReadOnlyList<string>? proposed, CancellationToken cancellationToken, bool requireLocked = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = _original;
        if (requireLocked && (original is null || !Locked(original.Security)))
            throw new InvalidOperationException("ShellNew ordering requires a verified existing lock. Refresh before retrying.");
        var classes = original?.Classes ?? Value.Missing;
        var planned = proposed is null ? classes : Value.MultiString(BuildOrder(classes, discovered ?? [], proposed));
        if (locked && proposed is null) throw new InvalidOperationException("Authoritative ShellNew discovery is required to lock.");
        if (original is null && !locked)
        {
            Require(original);
            if (ReadLocked(root, path)) throw new InvalidOperationException("ShellNew lock appeared during unlock verification.");
            return;
        }
        var expected = original;
        var ownedStates = new List<State>();
        if (original is not null) ownedStates.Add(original);
        // Preflight ACL write access without requesting denied value-write rights.
        using (var preflight = Open(RegistryRights.ReadPermissions | RegistryRights.ChangePermissions))
        {
            if (original is not null && preflight is null) throw new InvalidOperationException("ShellNew order key disappeared.");
        }
        if (original is not null)
        {
            var unlockedAcl = WithLock(original.Security, false);
            var finalAcl = WithLock(original.Security, locked);
            if (Locked(unlockedAcl) || Locked(finalAcl) != locked)
                throw new InvalidOperationException("An unrelated ShellNew deny rule prevents this operation. Use verified external ACL repair; no ownership fallback is attempted.");
        }
        Require(original);
        cancellationToken.ThrowIfCancellationRequested();
        // After this point cancellation/disconnection must not interrupt commit or
        // rollback. All operations are synchronous and verified through new handles.
        try
        {
            if (original is null)
            {
                using var created = root.CreateSubKey(path, writable: false)
                    ?? throw new InvalidOperationException("Unable to create ShellNew order key.");
                expected = Read() ?? throw new InvalidOperationException("ShellNew order key creation could not be verified.");
                if (expected.Classes.Exists) throw new InvalidOperationException("ShellNew order key was created concurrently.");
                original = expected;
                ownedStates.Add(expected);
                logger.LogFireAndForget($"ShellNewOrderKeyCreated: Path={root.Name}\\{path}. Empty key is retained on rollback; no existing key will be deleted.");
            }
            var unlocked = expected! with { Security = WithLock(expected!.Security, false) };
            if (!expected.Classes.Same(planned) && !SameAcl(expected.Security, unlocked.Security))
            {
                var before = expected;
                expected = unlocked;
                ownedStates.Add(expected);
                WriteAcl(before, expected);
            }
            if (!expected.Classes.Same(planned))
            {
                var before = expected;
                expected = expected with { Classes = planned };
                ownedStates.Add(expected);
                WriteValue(before, expected);
            }
            var final = expected with { Security = WithLock(expected.Security, locked) };
            if (!SameAcl(expected.Security, final.Security))
            {
                var before = expected;
                expected = final;
                ownedStates.Add(expected);
                WriteAcl(before, expected);
            }
            Require(expected);
            if (ReadLocked(root, path) != locked) throw new InvalidOperationException("ShellNew lock physical read-back failed.");
            logger.LogFireAndForget($"ShellNewOrderTransactionVerified: Path={root.Name}\\{path}, Locked={locked}, ClassesBytes={planned.Bytes.Length}, ClassesExisted={classes.Exists}, OriginalKind={classes.Kind}.");
        }
        catch (Exception failure)
        {
            try
            {
                if (!Same(Read(), original))
                {
                    var observed = Read();
                    if (observed is null || !ownedStates.Any(state => Same(observed, state)))
                        throw new InvalidOperationException("Rollback refused: physical value or ACL no longer matches an operation-owned state.");
                    expected = observed;
                    if (original is null || expected is null) throw new InvalidOperationException("Original key state unavailable.");
                    // Undo only our value and DACL, only while both still match an
                    // operation-owned generation. Unknown external changes stop rollback.
                    var unlocked = expected with { Security = WithLock(expected.Security, false) };
                    if (!expected.Classes.Same(original.Classes))
                    {
                        if (!SameAcl(expected.Security, unlocked.Security)) { WriteAcl(expected, unlocked); expected = unlocked; }
                        var restoredValue = expected with { Classes = original.Classes };
                        WriteValue(expected, restoredValue);
                        expected = restoredValue;
                    }
                    if (!SameAcl(expected.Security, original.Security)) WriteAcl(expected, original);
                    Require(original);
                }
                logger.LogFireAndForget(RuntimeLogLevel.Warning, $"ShellNewOrderRollbackVerified: Path={root.Name}\\{path}, OriginalValueExisted={classes.Exists}.");
            }
            catch (Exception rollback)
            {
                logger.LogFireAndForget(RuntimeLogLevel.Error, $"ShellNewOrderRollbackUncertain: Path={root.Name}\\{path}, Failure={failure}, Rollback={rollback}. External state preserved; manual inspection required.");
                throw new AggregateException("ShellNew operation failed and rollback is uncertain. Inspect the physical Classes value and ACL before retrying.", failure, rollback);
            }
            throw;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, byte[]? data, ref uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, uint reserved, uint type, byte[] data, uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValue(SafeRegistryHandle key, string name);

    [DllImport("advapi32.dll")]
    private static extern int RegGetKeySecurity(SafeRegistryHandle key, uint information, byte[]? descriptor, ref uint length);

    [DllImport("advapi32.dll")]
    private static extern int RegSetKeySecurity(SafeRegistryHandle key, uint information, byte[] descriptor);

}
