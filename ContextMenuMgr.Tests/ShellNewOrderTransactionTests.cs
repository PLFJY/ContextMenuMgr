using System.Security.AccessControl;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: physical value preservation, narrow ACL changes, conditional rollback
// and cancellation. Every mutation is confined to a unique disposable HKCU key.
public sealed class ShellNewOrderTransactionTests
{
    [Fact]
    public void LockMoveUnlock_VerifiesPhysicalValueAndPreservesForeignRulesAndSecurity()
    {
        using var fixture = new Fixture();
        fixture.Key.SetValue("Classes", new[] { ".a", "third-party", ".b", "unknown" }, RegistryValueKind.MultiString);
        var security = fixture.Key.GetAccessControl(Sections);
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
            RegistryRights.QueryValues, AccessControlType.Deny));
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            RegistryRights.EnumerateSubKeys, AccessControlType.Deny));
        fixture.Key.SetAccessControl(security);
        var original = fixture.Descriptor();
        fixture.Transaction.Execute(true, [".a", ".b"], [".a", ".b"], CancellationToken.None);
        Assert.True(ShellNewOrderTransaction.ReadLocked(Registry.CurrentUser, fixture.Path));
        fixture.Transaction.Execute(true, [".a", ".b"], [".b", ".a"], CancellationToken.None);
        Assert.Equal(new[] { ".b", "third-party", ".a", "unknown" }, fixture.Classes());
        fixture.Transaction.Execute(false, null, null, CancellationToken.None);
        Assert.False(ShellNewOrderTransaction.ReadLocked(Registry.CurrentUser, fixture.Path));
        Assert.Equal(original, fixture.Descriptor());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("packaged-only")]
    [InlineData("missing-proposed")]
    [InlineData("foreign-proposed")]
    public void InvalidOrIncompleteProposal_LeavesNonemptyValueAndAclUntouched(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Key.SetValue("Classes", new[] { ".a", ".b", "uncertain" }, RegistryValueKind.MultiString);
        var original = fixture.Descriptor();
        var discovered = scenario is "empty" or "packaged-only" ? Array.Empty<string>() : new[] { ".a", ".b" };
        var proposed = scenario == "foreign-proposed" ? new[] { ".a", ".foreign" }
            : scenario == "missing-proposed" ? new[] { ".a" } : Array.Empty<string>();
        Assert.Throws<InvalidOperationException>(() => fixture.Transaction.Execute(true, discovered, proposed, CancellationToken.None));
        Assert.Equal(new[] { ".a", ".b", "uncertain" }, fixture.Classes());
        Assert.Equal(original, fixture.Descriptor());
    }

    [Fact]
    public void UncertainExistingEntries_AreRetainedEvenWhenOnlyOneClassicItemIsDiscovered()
    {
        using var fixture = new Fixture();
        fixture.Key.SetValue("Classes", new[] { ".a", ".unresolved", "PackageId", ".other" }, RegistryValueKind.MultiString);
        fixture.Transaction.Execute(true, [".a"], [".a"], CancellationToken.None);
        Assert.Equal(new[] { ".a", ".unresolved", "PackageId", ".other" }, fixture.Classes());
        fixture.Transaction.Execute(false, null, null, CancellationToken.None);
    }

    [Fact]
    public void WrongValueKind_IsNotConvertedOrOverwritten()
    {
        using var fixture = new Fixture();
        fixture.Key.SetValue("Classes", "%preserve%", RegistryValueKind.ExpandString);
        var original = fixture.Descriptor();
        Assert.Throws<InvalidOperationException>(() => fixture.Transaction.Execute(true, [".a"], [".a"], CancellationToken.None));
        Assert.Equal(RegistryValueKind.ExpandString, fixture.Key.GetValueKind("Classes"));
        Assert.Equal("%preserve%", fixture.Key.GetValue("Classes", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(original, fixture.Descriptor());
    }

    [Fact]
    public void ValueWriteDeniedAfterUnlock_RestoresOriginalValueAndExactAcl()
    {
        using var fixture = new Fixture();
        fixture.Key.SetValue("Classes", new[] { ".a", ".b", "unknown" }, RegistryValueKind.MultiString);
        var security = fixture.Key.GetAccessControl(Sections);
        // The World lock can be safely removed. This unrelated user rule then
        // refuses the value write, exercising rollback after an actual DACL change.
        security.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.SetValue, AccessControlType.Deny));
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), ShellNewOrderTransaction.LockRights, AccessControlType.Deny));
        fixture.Key.SetAccessControl(security);
        var original = fixture.Descriptor();
        var failure = Record.Exception(() => fixture.Transaction.Execute(true, [".a", ".b"], [".b", ".a"], CancellationToken.None));
        Assert.True(failure is UnauthorizedAccessException or System.Security.SecurityException, failure?.ToString());
        Assert.Equal(new[] { ".a", ".b", "unknown" }, fixture.Classes());
        Assert.Equal(original, fixture.Descriptor());
    }

    [Fact]
    public void UnknownWorldDeny_IsPreservedAndUnlockFailsBeforeChangingAcl()
    {
        using var fixture = new Fixture();
        var security = fixture.Key.GetAccessControl(Sections);
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            RegistryRights.SetValue | RegistryRights.Delete | RegistryRights.EnumerateSubKeys, AccessControlType.Deny));
        fixture.Key.SetAccessControl(security);
        var original = fixture.Descriptor();
        Assert.Throws<InvalidOperationException>(() => fixture.Transaction.Execute(false, null, null, CancellationToken.None));
        Assert.Equal(original, fixture.Descriptor());
    }

    [Fact]
    public void CancellationBeforeMutation_PreservesAbsentValueAndAcl()
    {
        using var fixture = new Fixture();
        var original = fixture.Descriptor();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Transaction.Execute(true, [".a"], [".a"], canceled.Token));
        Assert.DoesNotContain("Classes", fixture.Key.GetValueNames());
        Assert.Equal(original, fixture.Descriptor());
    }

    [Fact]
    public void Unlock_DoesNotRequireDiscoveryOrCreateMissingClasses()
    {
        using var fixture = new Fixture();
        var security = fixture.Key.GetAccessControl(Sections);
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), ShellNewOrderTransaction.LockRights, AccessControlType.Deny));
        fixture.Key.SetAccessControl(security);
        fixture.Transaction.Execute(false, null, null, CancellationToken.None);
        Assert.DoesNotContain("Classes", fixture.Key.GetValueNames());
        Assert.False(ShellNewOrderTransaction.ReadLocked(Registry.CurrentUser, fixture.Path));
    }

    [Fact]
    public void ExternalValueAndAclChangeSinceCapture_IsNeverOverwritten()
    {
        using var fixture = new Fixture();
        fixture.Key.SetValue("Classes", new[] { ".a", ".b" }, RegistryValueKind.MultiString);
        var transaction = fixture.Transaction;
        fixture.Key.SetValue("Classes", new[] { "external", ".b", ".a" }, RegistryValueKind.MultiString);
        var security = fixture.Key.GetAccessControl(Sections);
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null), RegistryRights.QueryValues, AccessControlType.Deny));
        fixture.Key.SetAccessControl(security);
        var externalDescriptor = fixture.Descriptor();
        Assert.Throws<InvalidOperationException>(() => transaction.Execute(true, [".a", ".b"], [".a", ".b"], CancellationToken.None));
        Assert.Equal(new[] { "external", ".b", ".a" }, fixture.Classes());
        Assert.Equal(externalDescriptor, fixture.Descriptor());
    }

    [Theory]
    [InlineData("odd")]
    [InlineData("unterminated")]
    [InlineData("empty-middle")]
    public void MalformedMultiString_IsPreservedByteForByte(string scenario)
    {
        using var fixture = new Fixture();
        var bytes = scenario switch
        {
            "odd" => new byte[] { 1, 0, 0 },
            "unterminated" => Encoding.Unicode.GetBytes(".a"),
            _ => Encoding.Unicode.GetBytes(".a\0\0.b\0\0")
        };
        Assert.Equal(0, RegSetValueEx(fixture.Key.Handle, "Classes", 0, 7, bytes, (uint)bytes.Length));
        static (uint Kind, byte[] Bytes) ReadPhysical(RegistryKey key)
        {
            uint size = 0;
            Assert.Equal(0, RegQueryValueEx(key.Handle, "Classes", IntPtr.Zero, out var kind, null, ref size));
            var actual = new byte[size];
            Assert.Equal(0, RegQueryValueEx(key.Handle, "Classes", IntPtr.Zero, out kind, actual, ref size));
            return (kind, actual);
        }
        // Windows may append a string terminator even for the native write.
        // Assert against authoritative read-back, not the requested input bytes.
        var baseline = ReadPhysical(fixture.Key);
        var original = fixture.Descriptor();
        Assert.Throws<InvalidOperationException>(() => fixture.Transaction.Execute(true, [".a"], [".a"], CancellationToken.None));
        using var fresh = Registry.CurrentUser.OpenSubKey(fixture.Path, writable: false)!;
        var actual = ReadPhysical(fresh);
        Assert.Equal(7u, actual.Kind);
        Assert.Equal(baseline.Bytes, actual.Bytes);
        Assert.Equal(original, fixture.Descriptor());
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, uint reserved, uint type, byte[] data, uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, byte[]? data, ref uint size);

    private const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;

    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = @"Software\ContextMenuMgr.Tests\ShellNewOrder-" + Guid.NewGuid().ToString("N");
        internal RegistryKey Key { get; }
        internal ShellNewOrderTransaction Transaction => new(Registry.CurrentUser, Path, _logger);
        private readonly FileLogger _logger;
        private readonly RegistrySecurity _initial;
        private readonly string _logDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            Key = Registry.CurrentUser.CreateSubKey(Path)!;
            _initial = Key.GetAccessControl(Sections);
            Directory.CreateDirectory(_logDirectory);
            _logger = new FileLogger(System.IO.Path.Combine(_logDirectory, "backend.log"));
        }
        internal string Descriptor()
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path, RegistryKeyPermissionCheck.Default, RegistryRights.ReadPermissions)!;
            var descriptor = new RawSecurityDescriptor(key.GetAccessControl(Sections).GetSecurityDescriptorBinaryForm(), 0);
            descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
            return descriptor.GetSddlForm(Sections);
        }
        internal string[] Classes()
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path, RegistryKeyPermissionCheck.Default, RegistryRights.QueryValues)!;
            return (string[])key.GetValue("Classes")!;
        }
        public void Dispose()
        {
            // A pre-existing handle with WRITE_DAC remains usable to remove ONLY
            // this fixture's deny rules, even if an assertion failed while locked.
            Key.SetAccessControl(_initial);
            Key.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(Path, false);
        }
    }
}
