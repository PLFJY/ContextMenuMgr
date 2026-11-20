using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: physical dual-target state, scoped recovery and external-change preservation.
public sealed class Win11ClassicContextMenuTests : IDisposable
{
    private readonly string _path = @"Software\ContextMenuMgr.Tests\ClassicMenu\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    public Win11ClassicContextMenuTests() => _root = Registry.CurrentUser.CreateSubKey(_path)!;
    private static string Target(int index) => @"CLSID\" + Win11ClassicContextMenuMutation.Clsids[index] + @"\InprocServer32";
    private Win11ClassicContextMenuMutation Plan(bool disabled) => new(_root, "CLSID", disabled);
    private string Status => Win11ClassicContextMenuMutation.ReadStatus(_root, "CLSID");
    private void Write(int index, object value, RegistryValueKind kind)
    {
        using var key = _root.CreateSubKey(Target(index));
        key.SetValue("", value, kind);
    }

    [Fact]
    public void DualOverride_RoundTripsIdempotently_AndVerifiesTypesThroughFreshHandles()
    {
        Assert.Equal("Absent", Status);
        Assert.True(Plan(true).Apply(default));
        Assert.Equal("Complete", Status);
        for (var i = 0; i < 2; i++)
        {
            using var key = _root.OpenSubKey(Target(i))!;
            Assert.Equal(RegistryValueKind.String, key.GetValueKind(""));
            Assert.Equal("", key.GetValue(""));
        }
        Assert.False(Plan(true).Apply(default));
        Assert.True(Plan(false).Apply(default));
        Assert.Equal("Absent", Status);
        Assert.False(Plan(false).Apply(default));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void PartialOrOldSingleOverride_IsIncomplete_AndCanBeUpgraded(int index, bool hasDefault)
    {
        using (var key = _root.CreateSubKey(Target(index)))
            if (hasDefault) key.SetValue("", "", RegistryValueKind.String);
        Assert.Equal("Incomplete", Status);
        Plan(true).Apply(default);
        Assert.Equal("Complete", Status);
        Plan(false).Apply(default);
        Assert.Equal("Absent", Status);
    }

    [Theory]
    [InlineData(RegistryValueKind.String, "external.dll")]
    [InlineData(RegistryValueKind.ExpandString, "")]
    [InlineData(RegistryValueKind.DWord, 0)]
    public void ConflictingDefault_IsReported_AndNeitherTargetIsOverwritten(RegistryValueKind kind, object value)
    {
        Write(1, value, kind);
        Assert.Equal("Conflict", Status);
        Assert.Throws<InvalidOperationException>(() => Plan(true));
        Assert.Throws<InvalidOperationException>(() => Plan(false));
        Assert.Null(_root.OpenSubKey(Target(0)));
        using var key = _root.OpenSubKey(Target(1))!;
        Assert.Equal(kind, key.GetValueKind(""));
        Assert.Equal(value, key.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [Fact]
    public void Restoration_PreservesUnrelatedValuesAndChildTrees()
    {
        using (var key = _root.CreateSubKey(Target(0)))
        {
            key.SetValue("Other", new byte[] { 1, 2 });
            using var child = key.CreateSubKey("Child");
            child.SetValue("", "Preserved");
        }
        using (var parent = _root.CreateSubKey(@"CLSID\" + Win11ClassicContextMenuMutation.Clsids[1]))
            parent.SetValue("Other", 42);
        Plan(true).Apply(default);
        Plan(false).Apply(default);
        using var fresh = _root.OpenSubKey(Target(0))!;
        Assert.Null(fresh.GetValue(""));
        Assert.False(Plan(false).Apply(default));
        Assert.Equal(new byte[] { 1, 2 }, fresh.GetValue("Other"));
        using var childRead = fresh.OpenSubKey("Child")!;
        Assert.Equal("Preserved", childRead.GetValue(""));
        using var parentRead = _root.OpenSubKey(@"CLSID\" + Win11ClassicContextMenuMutation.Clsids[1])!;
        Assert.Equal(42, parentRead.GetValue("Other"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SecondMutationDenied_CompensatesFirst_AndAllowsLaterRecovery(bool disabled)
    {
        Write(0, "", RegistryValueKind.String);
        Write(1, "", RegistryValueKind.String);
        if (disabled)
        {
            using var first = _root.OpenSubKey(Target(0), true)!;
            first.DeleteValue("");
            using var second = _root.OpenSubKey(Target(1), true)!;
            second.DeleteValue("");
        }
        var mutation = Plan(disabled);
        using var denied = _root.OpenSubKey(Target(1), true)!;
        var originalAcl = denied.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var acl = denied.GetAccessControl();
        acl.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.SetValue, AccessControlType.Deny));
        denied.SetAccessControl(acl);
        try
        {
            var error = Record.Exception(() => mutation.Apply(default));
            Assert.True(error is UnauthorizedAccessException or System.Security.SecurityException);
            Assert.True(mutation.TryRollback().Succeeded);
            using var first = _root.OpenSubKey(Target(0))!;
            Assert.Equal(!disabled, first.GetValueNames().Contains(""));
        }
        finally
        {
            var restoreAcl = new RegistrySecurity();
            restoreAcl.SetSecurityDescriptorBinaryForm(originalAcl, AccessControlSections.Access);
            denied.SetAccessControl(restoreAcl);
        }
        Plan(disabled).Apply(default);
        Assert.Equal(disabled ? "Complete" : "Absent", Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadBackMismatch_ConditionalRollbackPreservesExternalWriter(bool disabled)
    {
        if (!disabled) Plan(true).Apply(default);
        var mutation = Plan(disabled);
        mutation.Apply(default);
        Write(1, "external.dll", RegistryValueKind.String);
        Assert.False(mutation.Verify());
        var rollback = mutation.TryRollback();
        Assert.False(rollback.Succeeded);
        Assert.True(rollback.Conflict);
        using var external = _root.OpenSubKey(Target(1))!;
        Assert.Equal("external.dll", external.GetValue(""));
        using var first = _root.OpenSubKey(Target(0));
        Assert.Equal(!disabled, first?.GetValue("") is not null);
    }

    [Fact]
    public void CancellationBeforeMutation_DoesNotChangeRegistry()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Plan(true).Apply(cts.Token));
        Assert.Equal("Absent", Status);
    }

    [Fact]
    public void ConcurrentPlan_RejectsStaleStateWithoutOverwritingNewerOperation()
    {
        var stale = Plan(true);
        Plan(true).Apply(default);
        Assert.Throws<InvalidOperationException>(() => stale.Apply(default));
        Assert.True(stale.TryRollback().Succeeded);
        Assert.Equal("Complete", Status);
    }

    [Fact]
    public async Task MissingIdentityOrUnloadedSid_FailsWithoutFallingBackToServiceHkcu()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new Win11ClassicContextMenuService(new FileLogger(Path.Combine(directory, "backend.log")));
            Assert.False((await service.GetDisabledAsync(null, null, default)).Success);
            foreach (var sid in new[] { "S-1-5-21-123-456-789-1001", "S-1-12-1-123-456-789-1002" })
            {
                var context = new BackendUserContext(sid, "test", "", "", "", 1);
                var response = await service.SetDisabledAsync(true, null, context, default);
                Assert.False(response.Success);
                Assert.Null(response.Win11ModernContextMenuDisabled);
                Assert.False((await service.GetDisabledAsync(null, context, default)).Success);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RegistryReadDenied_IsAnError_NotAnEnabledOrDisabledState()
    {
        Write(1, "", RegistryValueKind.String);
        using var key = _root.OpenSubKey(Target(1), true)!;
        var original = key.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var acl = key.GetAccessControl();
        acl.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.QueryValues, AccessControlType.Deny));
        key.SetAccessControl(acl);
        try { Assert.ThrowsAny<Exception>(() => Win11ClassicContextMenuMutation.ReadStatus(_root, "CLSID")); }
        finally
        {
            var restore = new RegistrySecurity();
            restore.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            key.SetAccessControl(restore);
        }
    }

    [Theory]
    [InlineData(19045, false)]
    [InlineData(22000, true)]
    [InlineData(22631, true)]
    [InlineData(26100, true)]
    [InlineData(26200, true)]
    [InlineData(26300, true)]
    public void CompatibilityPolicy_UsesBothTargetsOnAllWindows11Builds(int build, bool supported)
        => Assert.Equal(supported, Win11ClassicContextMenuService.SupportsOverrides(new Version(10, 0, build)));

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, false);
    }
}
