using System.Security.Principal;
using ContextMenuMgr.Backend.Hosting;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

[CollectionDefinition("Registry catalog integration", DisableParallelization = true)]
public sealed class RegistryCatalogIntegrationCollection { }

[Collection("Registry catalog integration")]
public sealed class LegacyShellVerbVisibilityRecoveryTests
{
    [Theory]
    [InlineData(@"Directory\shell")]
    [InlineData(@"Directory\Background\shell")]
    [InlineData(@"Drive\shell")]
    public async Task LegacyVsCodeStylePendingItem_AllowRecoversAndCommits(string parent)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var keyName = $"VSCode_{suffix}";
        var relativePath = $@"Software\Classes\{parent}\{keyName}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-LegacyRecoveryTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            {
                SeedLegacy(key);
                using var command = key.CreateSubKey("command", writable: true)!;
                command.SetValue(null, "legacy-vscode-test.exe \"%1\"", RegistryValueKind.String);
            }

            var (catalog, store, context) = CreateCatalog(root);
            var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None, context);
            var item = Assert.Single(snapshot, candidate =>
                string.Equals(candidate.BackendRegistryPath, PhysicalPath(relativePath), StringComparison.OrdinalIgnoreCase));
            var states = await store.LoadAsync(CancellationToken.None);
            var state = states[item.Id];
            state.DesiredEnabled = false;
            state.ObservedEnabled = false;
            state.IsPendingApproval = true;
            state.PendingApprovalChangeKind = ContextMenuChangeKind.Added;
            state.ShellVerbVisibilityProvenance.Clear();
            await store.SaveAsync(states, CancellationToken.None);

            var response = await catalog.ApplyDecisionAsync(item.Id, ContextMenuDecision.Allow, CancellationToken.None, context);
            Assert.True(response.Success, response.Message);
            Assert.True(response.Item?.IsEnabled);
            Assert.False(response.Item?.IsPendingApproval);
            using (var key = Registry.CurrentUser.OpenSubKey(relativePath)!)
            {
                Assert.True(ShellVerbVisibility.IsEnabled(key));
                Assert.Null(key.GetValue("HideBasedOnVelocityId"));
                Assert.Null(key.GetValue("ProgrammaticAccessOnly"));
                Assert.Null(key.GetValue("LegacyDisable"));
            }
            states = await store.LoadAsync(CancellationToken.None);
            state = states[item.Id];
            Assert.True(state.DesiredEnabled);
            Assert.True(state.ObservedEnabled);
            Assert.False(state.IsPendingApproval);
            Assert.Null(state.PendingApprovalChangeKind);
            Assert.Empty(state.ShellVerbVisibilityProvenance);

            // The compatibility bridge is one-shot. Subsequent mutations use
            // the modern exact-value provenance model.
            var disable = await catalog.ApplyDesiredStateAsync(item.Id, false, CancellationToken.None, context);
            Assert.True(disable.Success, disable.Message);
            states = await store.LoadAsync(CancellationToken.None);
            Assert.Single(states[item.Id].ShellVerbVisibilityProvenance);
            var enable = await catalog.ApplyDesiredStateAsync(item.Id, true, CancellationToken.None, context);
            Assert.True(enable.Success, enable.Message);
            using var restored = Registry.CurrentUser.OpenSubKey(relativePath)!;
            Assert.Null(restored.GetValue("ProgrammaticAccessOnly"));
            Assert.Empty((await store.LoadAsync(CancellationToken.None))[item.Id].ShellVerbVisibilityProvenance);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitEnable_RecoversLegacyDisabledItemOutsideApprovals()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\Classes\Drive\shell\VSCode_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-LegacyRecoveryTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!) SeedLegacy(key);
            var (catalog, store, context) = CreateCatalog(root);
            var item = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context), candidate =>
                string.Equals(candidate.BackendRegistryPath, PhysicalPath(relativePath), StringComparison.OrdinalIgnoreCase));
            var response = await catalog.ApplyDesiredStateAsync(item.Id, true, CancellationToken.None, context);
            Assert.True(response.Success, response.Message);
            using var keyAfter = Registry.CurrentUser.OpenSubKey(relativePath)!;
            Assert.True(ShellVerbVisibility.IsEnabled(keyAfter));
            Assert.Empty((await store.LoadAsync(CancellationToken.None))[item.Id].ShellVerbVisibilityProvenance);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("ordinary", true)]
    [InlineData("opennewwindow", true)]
    [InlineData("showdisabled", true)]
    [InlineData("velocity", false)]
    [InlineData("velocity-kind", false)]
    [InlineData("missing-legacy", false)]
    [InlineData("extra-legacy", false)]
    [InlineData("marker-kind", false)]
    [InlineData("marker-data", false)]
    [InlineData("legacy-kind", false)]
    [InlineData("legacy-data", false)]
    [InlineData("command-flags", false)]
    [InlineData("third-party", false)]
    [InlineData("intent-missing", false)]
    [InlineData("wrong-physical-path", false)]
    public void SignatureAndOwnership_AreStrict(string variant, bool expectedRecovery)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var subPath = variant == "opennewwindow"
            ? $@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}\Folder\shell\opennewwindow"
            : $@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}\Directory\shell\VSCode";
        try
        {
            using var key = Registry.Users.CreateSubKey($@"{WindowsIdentity.GetCurrent().User!.Value}\{subPath}", writable: true)!;
            var physicalPath = PhysicalPath(subPath);
            SeedLegacy(key, variant == "opennewwindow", variant == "showdisabled");
            switch (variant)
            {
                case "velocity": key.SetValue("HideBasedOnVelocityId", 123, RegistryValueKind.DWord); break;
                case "velocity-kind": key.SetValue("HideBasedOnVelocityId", "0x639bc8", RegistryValueKind.String); break;
                case "missing-legacy": key.DeleteValue("LegacyDisable"); break;
                case "extra-legacy": key.SetValue("ShowAsDisabledIfHidden", "", RegistryValueKind.String); break;
                case "marker-kind": key.SetValue("ProgrammaticAccessOnly", "", RegistryValueKind.ExpandString); break;
                case "marker-data": key.SetValue("ProgrammaticAccessOnly", "external", RegistryValueKind.String); break;
                case "legacy-kind": key.SetValue("LegacyDisable", "", RegistryValueKind.ExpandString); break;
                case "legacy-data": key.SetValue("LegacyDisable", "external", RegistryValueKind.String); break;
                case "command-flags": key.SetValue("CommandFlags", 8, RegistryValueKind.DWord); break;
                case "third-party": key.DeleteValue("HideBasedOnVelocityId"); key.DeleteValue("LegacyDisable"); break;
            }
            var state = NewState(physicalPath);
            if (variant == "intent-missing") state.DesiredEnabled = true;
            if (variant == "wrong-physical-path") state.BackendRegistryPath += "_shadow";

            var accepted = LegacyShellVerbVisibilityRecoveryTransaction.TryCreate(
                key, physicalPath, state, state.Id, out var recovery, out _);
            Assert.Equal(expectedRecovery, accepted);
            if (accepted)
            {
                recovery!.Apply(key);
                Assert.True(recovery.Verify(key));
                Assert.True(ShellVerbVisibility.IsEnabled(key));
                recovery.Dispose();
            }
            else
            {
                Assert.Null(recovery);
                if (variant == "third-party")
                {
                    var exception = Assert.Throws<ShellVerbMutationException>(() =>
                        ShellVerbVisibilityTransaction.Create(key, physicalPath, requestedVisible: true, existingProvenance: null));
                    Assert.Equal(PipeErrorCodes.ShellVerbVisibilityProvenanceMissing, exception.ErrorCode);
                    Assert.NotNull(key.GetValue("ProgrammaticAccessOnly"));
                }
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}", throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PostWriteFailure_RollsBackExactLegacyValues(bool cancel, bool viaApproval)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\Classes\Directory\shell\VSCode_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-LegacyRecoveryTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            {
                SeedLegacy(key);
                using var command = key.CreateSubKey("command", writable: true)!;
                command.SetValue(null, "legacy-test.exe %1", RegistryValueKind.String);
            }
            var (catalog, store, context) = CreateCatalog(root);
            var item = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context), candidate =>
                string.Equals(candidate.BackendRegistryPath, PhysicalPath(relativePath), StringComparison.OrdinalIgnoreCase));
            if (viaApproval)
            {
                var states = await store.LoadAsync(CancellationToken.None);
                states[item.Id].IsPendingApproval = true;
                states[item.Id].PendingApprovalChangeKind = ContextMenuChangeKind.Added;
                await store.SaveAsync(states, CancellationToken.None);
            }
            store.BeforeSaveAsync = (_, _) => cancel
                ? Task.FromException(new OperationCanceledException("Injected cancellation"))
                : Task.FromException(new IOException("Injected save failure"));
            var response = viaApproval
                ? await catalog.ApplyDecisionAsync(item.Id, ContextMenuDecision.Allow, CancellationToken.None, context)
                : await catalog.ApplyDesiredStateAsync(item.Id, true, CancellationToken.None, context);
            Assert.False(response.Success);
            Assert.Equal(PipeErrorCodes.RegistryMutationRolledBack, response.ErrorCode);
            using var keyAfter = Registry.CurrentUser.OpenSubKey(relativePath)!;
            AssertLegacy(keyAfter);
            store.BeforeSaveAsync = null;
            var persisted = (await store.LoadAsync(CancellationToken.None))[item.Id];
            Assert.False(persisted.DesiredEnabled);
            Assert.False(persisted.ObservedEnabled);
            Assert.Equal(viaApproval, persisted.IsPendingApproval);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExternalWriteAfterRecovery_CannotBeOverwrittenByRollback()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}\Directory\shell\VSCode";
        try
        {
            using var key = Registry.Users.CreateSubKey($@"{WindowsIdentity.GetCurrent().User!.Value}\{relativePath}", writable: true)!;
            SeedLegacy(key);
            var path = PhysicalPath(relativePath);
            Assert.True(LegacyShellVerbVisibilityRecoveryTransaction.TryCreate(
                key, path, NewState(path), "legacy-test", out var recovery, out _));
            recovery!.Apply(key);
            key.SetValue("ProgrammaticAccessOnly", "external-owner", RegistryValueKind.String);
            var rollback = recovery.TryRollback(key);
            Assert.True(rollback.Conflict);
            Assert.False(rollback.Succeeded);
            Assert.Equal("external-owner", key.GetValue("ProgrammaticAccessOnly"));
            recovery.Dispose();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task ThirdPartyProgrammaticMarker_AllowAndEnablePreservePendingState()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\Classes\Directory\shell\ThirdParty_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-LegacyRecoveryTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            {
                key.SetValue("ProgrammaticAccessOnly", string.Empty, RegistryValueKind.String);
            }
            var (catalog, store, context) = CreateCatalog(root);
            var item = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context), candidate =>
                string.Equals(candidate.BackendRegistryPath, PhysicalPath(relativePath), StringComparison.OrdinalIgnoreCase));
            var states = await store.LoadAsync(CancellationToken.None);
            states[item.Id].IsPendingApproval = true;
            states[item.Id].PendingApprovalChangeKind = ContextMenuChangeKind.Added;
            await store.SaveAsync(states, CancellationToken.None);

            var allow = await catalog.ApplyDecisionAsync(item.Id, ContextMenuDecision.Allow, CancellationToken.None, context);
            var enable = await catalog.ApplyDesiredStateAsync(item.Id, true, CancellationToken.None, context);
            Assert.Equal(PipeErrorCodes.ShellVerbVisibilityProvenanceMissing, allow.ErrorCode);
            Assert.Equal(PipeErrorCodes.ShellVerbVisibilityProvenanceMissing, enable.ErrorCode);
            using var unchanged = Registry.CurrentUser.OpenSubKey(relativePath)!;
            Assert.Equal(string.Empty, unchanged.GetValue("ProgrammaticAccessOnly"));
            states = await store.LoadAsync(CancellationToken.None);
            Assert.False(states[item.Id].DesiredEnabled);
            Assert.True(states[item.Id].IsPendingApproval);
            Assert.Equal(ContextMenuChangeKind.Added, states[item.Id].PendingApprovalChangeKind);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentWriterBeforeFailedCommit_ReturnsRollbackConflictAndPreservesExternalValue()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\Classes\Directory\shell\VSCode_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-LegacyRecoveryTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!) SeedLegacy(key);
            var (catalog, store, context) = CreateCatalog(root);
            var item = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context), candidate =>
                string.Equals(candidate.BackendRegistryPath, PhysicalPath(relativePath), StringComparison.OrdinalIgnoreCase));
            store.BeforeSaveAsync = (_, _) =>
            {
                using var external = Registry.CurrentUser.OpenSubKey(relativePath, writable: true)!;
                external.SetValue("ProgrammaticAccessOnly", "external-owner", RegistryValueKind.String);
                return Task.FromException(new IOException("Injected failed commit after external write"));
            };
            var response = await catalog.ApplyDesiredStateAsync(item.Id, true, CancellationToken.None, context);
            Assert.False(response.Success);
            Assert.Equal(PipeErrorCodes.RegistryMutationRollbackConflict, response.ErrorCode);
            using var keyAfter = Registry.CurrentUser.OpenSubKey(relativePath)!;
            Assert.Equal("external-owner", keyAfter.GetValue("ProgrammaticAccessOnly"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ShadowPathAndRecreatedKey_CannotBorrowLegacyOwnership()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}\Directory\shell\VSCode";
        var userPath = PhysicalPath(relativePath);
        var machinePath = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Directory\shell\VSCode_{suffix}";
        try
        {
            using (var key = Registry.Users.CreateSubKey($@"{WindowsIdentity.GetCurrent().User!.Value}\{relativePath}", writable: true)!)
            {
                SeedLegacy(key);
                var shadowState = NewState(machinePath);
                Assert.False(LegacyShellVerbVisibilityRecoveryTransaction.TryCreate(
                    key, userPath, shadowState, shadowState.Id, out _, out _));
                AssertLegacy(key);
            }

            using var original = Registry.Users.OpenSubKey($@"{WindowsIdentity.GetCurrent().User!.Value}\{relativePath}", writable: false)!;
            Assert.True(LegacyShellVerbVisibilityRecoveryTransaction.TryCreate(
                original, userPath, NewState(userPath), "legacy-test", out var recovery, out _));
            original.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(relativePath);
            using var replacement = Registry.Users.CreateSubKey($@"{WindowsIdentity.GetCurrent().User!.Value}\{relativePath}", writable: true)!;
            SeedLegacy(replacement);
            var error = Assert.Throws<ShellVerbMutationException>(() => recovery!.Apply(replacement));
            Assert.Equal("SHELL_VERB_PHYSICAL_GENERATION_CONFLICT", error.ErrorCode);
            AssertLegacy(replacement);
            recovery!.Dispose();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void RecreatedKeyAfterRecovery_IsRollbackConflictEvenWithSameVisibleValues()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}\Directory\shell\VSCode";
        var usersPath = $@"{WindowsIdentity.GetCurrent().User!.Value}\{relativePath}";
        try
        {
            using (var key = Registry.Users.CreateSubKey(usersPath, writable: true)!)
            {
                SeedLegacy(key);
            }
            var physicalPath = PhysicalPath(relativePath);
            using var original = Registry.Users.OpenSubKey(usersPath, writable: true)!;
            Assert.True(LegacyShellVerbVisibilityRecoveryTransaction.TryCreate(
                original, physicalPath, NewState(physicalPath), "legacy-test", out var recovery, out _));
            recovery!.Apply(original);
            original.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(relativePath);
            using var replacement = Registry.Users.CreateSubKey(usersPath, writable: true)!;
            var rollback = recovery.TryRollback(replacement);
            Assert.True(rollback.Conflict);
            Assert.Null(replacement.GetValue("HideBasedOnVelocityId"));
            recovery.Dispose();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\ContextMenuMgr.Tests\LegacyRecovery\{suffix}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task MonitorDetectionContext_IsPassedToQuarantinePhysicalResolution()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\Classes\Directory\shell\ContextProbe_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-LegacyRecoveryTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!;
            var (catalog, _, context) = CreateCatalog(root);
            var item = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context), candidate =>
                string.Equals(candidate.BackendRegistryPath, PhysicalPath(relativePath), StringComparison.OrdinalIgnoreCase));
            var otherContext = context with { Sid = "S-1-5-21-999999999-999999999-999999999-9999" };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BackendRuntime.QuarantineDetectedItemAsync(
                    catalog, new ContextMenuDetectedEventArgs(item, otherContext), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BackendRuntime.QuarantineDetectedItemAsync(
                    catalog, new ContextMenuDetectedEventArgs(item, null), CancellationToken.None));
            Assert.True(ShellVerbVisibility.IsEnabled(key));
            Assert.Null(key.GetValue("ProgrammaticAccessOnly"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string PhysicalPath(string relativePath)
        => $@"HKEY_USERS\{WindowsIdentity.GetCurrent().User!.Value}\{relativePath}";

    private static PersistedContextMenuState NewState(string physicalPath) => new()
    {
        Id = "legacy-test", EntryKind = ContextMenuEntryKind.ShellVerb,
        BackendRegistryPath = physicalPath, DesiredEnabled = false, ObservedEnabled = false
    };

    private static void SeedLegacy(RegistryKey key, bool openNewWindow = false, bool showDisabled = false)
    {
        key.SetValue("HideBasedOnVelocityId", 0x639bc8, RegistryValueKind.DWord);
        if (showDisabled)
        {
            key.SetValue("ShowAsDisabledIfHidden", string.Empty, RegistryValueKind.String);
            return;
        }
        key.SetValue("ProgrammaticAccessOnly", string.Empty, RegistryValueKind.String);
        if (!openNewWindow) key.SetValue("LegacyDisable", string.Empty, RegistryValueKind.String);
    }

    private static void AssertLegacy(RegistryKey key)
    {
        Assert.Equal(0x639bc8, key.GetValue("HideBasedOnVelocityId"));
        Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("HideBasedOnVelocityId"));
        Assert.Equal(string.Empty, key.GetValue("ProgrammaticAccessOnly"));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind("ProgrammaticAccessOnly"));
        Assert.Equal(string.Empty, key.GetValue("LegacyDisable"));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind("LegacyDisable"));
    }

    private static (ContextMenuRegistryCatalog Catalog, ContextMenuStateStore Store, BackendUserContext Context) CreateCatalog(string root)
    {
        var logger = new FileLogger(Path.Combine(root, "backend.log"));
        var store = new ContextMenuStateStore(Path.Combine(root, "state.json"), logger);
        var catalog = new ContextMenuRegistryCatalog(logger, store,
            new RegistryBackupService(Path.Combine(root, "backups"), logger),
            new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger));
        var context = new BackendUserContext(
            WindowsIdentity.GetCurrent().User!.Value,
            Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            SessionId: null);
        return (catalog, store, context);
    }
}
