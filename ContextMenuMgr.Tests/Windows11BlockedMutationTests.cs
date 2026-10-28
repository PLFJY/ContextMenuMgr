using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: actual value recovery, precise backup, conflict rollback and scope isolation.
public sealed class Windows11BlockedMutationTests : IDisposable
{
    private readonly string _path = @"Software\ContextMenuMgr.Tests\Win11Blocks\" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _clsid = Guid.NewGuid();
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    // Synthetic Entra-shaped SID: only proves the ownership marker keeps any token SID
    // verbatim. It is not a Windows token and it has no loaded hive.
    private const string EntraShapedSid = "S-1-12-1-111-222-333-444";
    private readonly FileLogger _logger;

    public Windows11BlockedMutationTests()
    {
        Directory.CreateDirectory(_directory);
        _logger = new FileLogger(Path.Combine(_directory, "backend.log"));
    }

    [Fact]
    public void OwnedBlock_RoundTripsWithFreshReadBack_AndPreservesOtherPolicies()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_path)!)
        {
            key.SetValue("Unrelated", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            var disable = Windows11BlocksService.CreateMutation(key, _clsid, _sid, false, _logger);
            disable.Apply();
            Assert.True(disable.Verify());
        }
        using (var fresh = Registry.CurrentUser.OpenSubKey(_path, writable: true)!)
        {
            Assert.Equal(RegistryValueKind.String, fresh.GetValueKind(_clsid.ToString("B")));
            var enable = Windows11BlocksService.CreateMutation(fresh, _clsid, _sid, true, _logger);
            enable.Apply();
            Assert.True(enable.Verify());
        }
        using var verification = Registry.CurrentUser.OpenSubKey(_path)!;
        Assert.Null(verification.GetValue(_clsid.ToString("B")));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(verification.GetValue("Unrelated")));
    }

    [Fact]
    public void ExistingExternalBlock_IsPreservedByDisable_AndExplicitRestoreCapturesItsExactValue()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_path)!;
        var alias = _clsid.ToString("D");
        var data = new[] { "External setting", "Preserve original" };
        key.SetValue(alias, data, RegistryValueKind.MultiString);
        var disable = Windows11BlocksService.CreateMutation(key, _clsid, _sid, false, _logger);
        disable.Apply();
        Assert.False(disable.Applied);
        Assert.Equal(data, Assert.IsType<string[]>(key.GetValue(alias)));
        var restore = Windows11BlocksService.CreateMutation(key, _clsid, _sid, true, _logger);
        var backup = Assert.Single(Assert.Single(restore.Backups).OriginalValues);
        Assert.Equal((int)RegistryValueKind.MultiString, backup.Kind);
        Assert.Equal(alias, backup.Name);
        Assert.Equal(data, backup.StringArrayValue);
        restore.Apply();
        using (var fresh = Registry.CurrentUser.OpenSubKey(_path)!) Assert.Null(fresh.GetValue(alias));
        Assert.True(restore.TryRollback().Succeeded);
        using var rolledBack = Registry.CurrentUser.OpenSubKey(_path)!;
        Assert.Equal(data, Assert.IsType<string[]>(rolledBack.GetValue(alias)));
    }

    [Fact]
    public void Restore_CleansDuplicateAliasesAndBothScopes_WithoutTouchingOtherMenus()
    {
        using var user = Registry.CurrentUser.CreateSubKey(_path + @"\User")!;
        using var machine = Registry.CurrentUser.CreateSubKey(_path + @"\Machine")!;
        user.SetValue(_clsid.ToString("B"), "Old release", RegistryValueKind.String);
        user.SetValue(_clsid.ToString("D"), new byte[] { 4, 5 }, RegistryValueKind.Binary);
        machine.SetValue(_clsid.ToString("B"), new[] { "Machine setting" }, RegistryValueKind.MultiString);
        machine.SetValue("Unrelated", "Keep", RegistryValueKind.String);
        var restore = Windows11BlocksService.CreateMutation(user, _clsid, _sid, true, _logger, machine);
        Assert.Equal(3, restore.Backups.Sum(backup => backup.OriginalValues.Count));
        restore.Apply();
        Assert.True(restore.Verify());
        Assert.Equal("Keep", machine.GetValue("Unrelated"));
        // A conflicting writer must survive rollback, while the other scope recovers.
        machine.SetValue(_clsid.ToString("B"), "Changed externally", RegistryValueKind.String);
        Assert.True(restore.TryRollback().Conflict);
        using var fresh = Registry.CurrentUser.OpenSubKey(_path + @"\User")!;
        Assert.Equal("Old release", fresh.GetValue(_clsid.ToString("B")));
        Assert.Equal(new byte[] { 4, 5 }, Assert.IsType<byte[]>(fresh.GetValue(_clsid.ToString("D"))));
        Assert.Equal("Changed externally", machine.GetValue(_clsid.ToString("B")));
    }

    [Fact]
    public void ConcurrentPolicyChange_BlocksApplyAndRollback_WithoutOverwritingIt()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_path)!;
        var planned = Windows11BlocksService.CreateMutation(key, _clsid, _sid, false, _logger);
        key.SetValue(_clsid.ToString("D"), "External alias", RegistryValueKind.String);
        Assert.Throws<InvalidOperationException>(() => planned.Apply());
        Assert.False(planned.Applied);
        key.DeleteValue(_clsid.ToString("D"));
        var mutation = Windows11BlocksService.CreateMutation(key, _clsid, _sid, false, _logger);
        mutation.Apply();
        key.SetValue(_clsid.ToString("B"), "New external policy", RegistryValueKind.String);
        Assert.True(mutation.TryRollback().Conflict);
        using var verification = Registry.CurrentUser.OpenSubKey(_path)!;
        Assert.Equal("New external policy", verification.GetValue(_clsid.ToString("B")));
    }

    [Fact]
    public void FailedApplicationChange_CanRollBackCompletedMenus_WithFreshVerification()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_path)!;
        var first = Windows11BlocksService.CreateMutation(key, _clsid, _sid, false, _logger);
        var nextId = Guid.NewGuid();
        var next = Windows11BlocksService.CreateMutation(key, nextId, _sid, false, _logger);
        first.Apply();
        key.SetValue(nextId.ToString("B"), "Late external setting", RegistryValueKind.String);
        Assert.Throws<InvalidOperationException>(() => next.Apply());
        Assert.True(first.TryRollback().Succeeded);
        using var fresh = Registry.CurrentUser.OpenSubKey(_path)!;
        Assert.Null(fresh.GetValue(_clsid.ToString("B")));
        Assert.Equal("Late external setting", fresh.GetValue(nextId.ToString("B")));
    }

    [Fact]
    public void SyntheticEntraShapedSid_RoundTripsItsOwnershipMarker_WithoutShapeClassification()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_path)!)
        {
            var disable = Windows11BlocksService.CreateMutation(key, _clsid, EntraShapedSid, false, _logger);
            disable.Apply();
            Assert.True(disable.Verify());
        }
        using (var fresh = Registry.CurrentUser.OpenSubKey(_path)!)
        {
            Assert.Equal(RegistryValueKind.String, fresh.GetValueKind(_clsid.ToString("B")));
            Assert.Equal($"ContextMenuMgrPlus.Win11Blocked.v1|{EntraShapedSid}|{_clsid:B}", fresh.GetValue(_clsid.ToString("B")));
        }
        using (var fresh = Registry.CurrentUser.OpenSubKey(_path, writable: true)!)
        {
            var enable = Windows11BlocksService.CreateMutation(fresh, _clsid, EntraShapedSid, true, _logger);
            enable.Apply();
            Assert.True(enable.Verify());
        }
        using var restored = Registry.CurrentUser.OpenSubKey(_path)!;
        Assert.Null(restored.GetValue(_clsid.ToString("B")));
    }

    [Fact]
    public void MissingOrUnloadedUser_CannotMutate()
    {
        var clsid = _clsid.ToString("B");
        Assert.Throws<InvalidOperationException>(
            () => Windows11BlocksService.CreateMutation(clsid, null, false, _logger));

        var unloaded = new BackendUserContext("S-1-5-21-111-222-333-444", "Unavailable", "", "", "", null);
        var hiveFailure = Assert.Throws<InvalidOperationException>(
            () => Windows11BlocksService.CreateMutation(clsid, unloaded, false, _logger));
        Assert.Contains("hive is not loaded", hiveFailure.Message);

        var unloadedEntra = new BackendUserContext(EntraShapedSid, "Unavailable", "", "", "", null);
        var entraHiveFailure = Assert.Throws<InvalidOperationException>(
            () => Windows11BlocksService.CreateMutation(clsid, unloadedEntra, false, _logger));
        Assert.Contains("hive is not loaded", entraHiveFailure.Message);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        Directory.Delete(_directory, recursive: true);
    }
}
