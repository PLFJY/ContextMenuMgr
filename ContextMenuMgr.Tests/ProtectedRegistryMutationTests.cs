using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

[Collection("Registry catalog integration")]
public sealed class ProtectedRegistryMutationTests
{
    // Permanent: decision persistence and the machine/user fallback boundary.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingDecision_CommitsOnlyAfterPhysicalMutation(bool denyWrite)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var path = $@"Software\Classes\Directory\shell\ContextMenuMgr.Tests.{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), $"ContextMenuMgr-ProtectedTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var created = Registry.CurrentUser.CreateSubKey(path, writable: true)!;
        using var fixture = Registry.CurrentUser.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)!;
        var original = Descriptor(fixture);
        try
        {
            using (var command = fixture.CreateSubKey("command")) command.SetValue(null, "fixture.exe %1");
            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var store = new ContextMenuStateStore(Path.Combine(root, "state.json"), logger);
            var catalog = new ContextMenuRegistryCatalog(logger, store,
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger));
            var context = new BackendUserContext(sid.Value, Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), null);
            var physical = $@"HKEY_USERS\{sid.Value}\{path}";
            var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None, context);
            var item = Assert.Single(snapshot, candidate => candidate.BackendRegistryPath.Equals(physical, StringComparison.OrdinalIgnoreCase));
            var states = await store.LoadAsync(CancellationToken.None);
            states[item.Id].IsPendingApproval = true;
            states[item.Id].PendingApprovalChangeKind = ContextMenuChangeKind.Added;
            states[item.Id].DesiredEnabled = true;
            await store.SaveAsync(states, CancellationToken.None);
            if (denyWrite)
            {
                var security = fixture.GetAccessControl();
                security.AddAccessRule(new RegistryAccessRule(sid, RegistryRights.SetValue, AccessControlType.Deny));
                fixture.SetAccessControl(security);
            }
            var beforeMutation = Descriptor(fixture);
            var response = await catalog.ApplyDecisionAsync(item.Id, ContextMenuDecision.Deny, CancellationToken.None, context);
            Assert.Equal(!denyWrite, response.Success);
            using var actual = Registry.CurrentUser.OpenSubKey(path)!;
            Assert.Equal(denyWrite, ShellVerbVisibility.IsEnabled(actual));
            Assert.Equal(beforeMutation, Descriptor(actual));
            var persisted = (await store.LoadAsync(CancellationToken.None))[item.Id];
            Assert.Equal(denyWrite, persisted.IsPendingApproval);
            Assert.Equal(denyWrite, persisted.DesiredEnabled);
            if (denyWrite)
            {
                Assert.Empty(persisted.ShellVerbVisibilityProvenance);
                var error = Assert.Throws<ProtectedRegistryMutationException>(() =>
                    ProtectedRegistryMutation.Execute(physical, _ => Assert.Fail("HKU fallback ran"), _ => { }));
                Assert.Equal(PipeErrorCodes.ProtectedRegistryMutationFailed, error.ErrorCode);
                Assert.Equal(beforeMutation, Descriptor(actual));
            }
            else
            {
                Assert.Single(persisted.ShellVerbVisibilityProvenance);
                var enable = await catalog.ApplyDesiredStateAsync(item.Id, true, CancellationToken.None, context);
                Assert.True(enable.Success, enable.Message);
                Assert.Null(actual.GetValue("ProgrammaticAccessOnly"));
            }
        }
        finally
        {
            Restore(fixture, original, AccessControlSections.Access);
            fixture.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
            Directory.Delete(root, true);
        }
    }

    // Permanent: actual owner/group/DACL restoration, conditional value rollback
    // and external ACL conflict handling. Separate from ordinary automated tests.
    [LocalSystemTheory]
    [Trait("Category", "LocalSystem")]
    [InlineData("success")]
    [InlineData("verification-failure")]
    [InlineData("value-conflict")]
    [InlineData("security-conflict")]
    [InlineData("rollback-denied")]
    public void ProtectedFallback_PreservesSecurityAndRollbackProvenance(string scenario)
    {
        var root = $@"SOFTWARE\Classes\ContextMenuMgr.Tests.{Guid.NewGuid():N}";
        var path = $@"{root}\shell\fixture";
        using var created = Registry.LocalMachine.CreateSubKey(path, writable: true)!;
        using var fixture = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)!;
        var cleanupDescriptor = Descriptor(fixture);
        try
        {
            using (var command = fixture.CreateSubKey("command")) command.SetValue(null, "fixture.exe %1");
            var security = new RegistrySecurity();
            security.SetAccessRuleProtection(true, false);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(administrators);
            security.SetGroup(administrators);
            security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                RegistryRights.ReadKey, AccessControlType.Allow));
            fixture.SetAccessControl(security);
            var original = Descriptor(fixture);
            var transaction = ShellVerbVisibilityTransaction.Create(fixture, fixture.Name, false, null);
            Assert.ThrowsAny<Exception>(() =>
            {
                using var ordinary = Registry.LocalMachine.OpenSubKey(path, writable: true)!;
                ordinary.SetValue("ProgrammaticAccessOnly", "");
            });
            byte[]? externalDescriptor = null;
            var error = Record.Exception(() => ProtectedRegistryMutation.Execute(fixture.Name, transaction.Apply, key =>
            {
                Assert.True(transaction.Verify(key));
                if (scenario is "security-conflict" or "rollback-denied")
                {
                    var changed = fixture.GetAccessControl();
                    changed.AddAccessRule(new RegistryAccessRule(administrators, RegistryRights.ReadPermissions, AccessControlType.Allow));
                    if (scenario == "rollback-denied")
                        changed.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                            RegistryRights.SetValue, AccessControlType.Deny));
                    fixture.SetAccessControl(changed);
                    externalDescriptor = Descriptor(fixture);
                }
                if (scenario == "value-conflict") key.SetValue("ProgrammaticAccessOnly", "installer");
                if (scenario != "success") throw new IOException("Verification failure after the physical write.");
            }));
            if (scenario == "success") Assert.Null(error);
            else
            {
                var failure = Assert.IsType<ProtectedRegistryMutationException>(error);
                Assert.Equal(scenario is "security-conflict" or "rollback-denied" ? PipeErrorCodes.RegistrySecurityRestoreFailed
                    : PipeErrorCodes.ProtectedRegistryMutationFailed, failure.ErrorCode);
            }
            Assert.Equal(externalDescriptor ?? original, Descriptor(fixture));
            Assert.True(transaction.Applied);
            Assert.Single(transaction.Provenance!.OriginalValues);
            if (scenario == "security-conflict") return;
            if (scenario == "rollback-denied")
            {
                var rollbackFailure = Assert.Throws<ProtectedRegistryMutationException>(() =>
                    ProtectedRegistryMutation.Execute(fixture.Name, key => transaction.TryRollback(key), _ => { }));
                Assert.Equal(PipeErrorCodes.ProtectedRegistryMutationFailed, rollbackFailure.ErrorCode);
                Assert.Equal(externalDescriptor, Descriptor(fixture));
                Assert.Equal("", fixture.GetValue("ProgrammaticAccessOnly"));
                return;
            }

            ShellVerbRollbackResult? rollback = null;
            ProtectedRegistryMutation.Execute(fixture.Name, key => rollback = transaction.TryRollback(key), _ => { });
            Assert.Equal(scenario == "value-conflict", rollback!.Conflict);
            Assert.Equal(scenario != "value-conflict", rollback.Succeeded);
            Assert.Equal(original, Descriptor(fixture));
            using var actual = Registry.LocalMachine.OpenSubKey(path)!;
            if (scenario == "value-conflict") Assert.Equal("installer", actual.GetValue("ProgrammaticAccessOnly"));
            else Assert.True(transaction.VerifyRolledBack(actual));
        }
        finally
        {
            // The fixture's original full-access handle remains valid even if
            // a deliberate conflict denies fresh opens. Only this GUID tree is removed.
            Restore(fixture, cleanupDescriptor);
            fixture.Dispose();
            Registry.LocalMachine.DeleteSubKeyTree(root, false);
        }
    }

    private static byte[] Descriptor(RegistryKey key) => key.GetAccessControl(
        AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access).GetSecurityDescriptorBinaryForm();

    private static void Restore(RegistryKey key, byte[] descriptor,
        AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
    {
        var security = new RegistrySecurity();
        security.SetSecurityDescriptorBinaryForm(descriptor, sections);
        key.SetAccessControl(security);
    }

    private sealed class LocalSystemTheoryAttribute : TheoryAttribute
    {
        public LocalSystemTheoryAttribute()
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!identity.IsSystem) Skip = "Requires an explicitly launched disposable LocalSystem testhost; never elevates or changes an installed service.";
        }
    }
}
