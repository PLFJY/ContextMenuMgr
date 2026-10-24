using System.Text.Json;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ContextMenuStateStoreTests
{
    // Permanent: schema-3 review/rule data from released builds has no new generation or journal fields.
    [Fact]
    public async Task OpenWithReleasedSchema3_PreservesReviewsRulesAndClassicStateWhenNewFieldsAreAbsent()
    {
        using var fixture = new StateStoreFixture();
        const string sid = "S-1-5-21-1-2-3-1001";
        const string path = @"HKEY_USERS\S-1-5-21-1-2-3-1001\Software\Classes\Applications\retained.exe";
        await fixture.CreateStore().WithOpenWithAsync(state =>
        {
            state.Users[sid] = new() { Registrations = new() { [path] = "observed-stamp" }, Reviews = new() { [path] = "Changed" } };
            state.Rules[sid + "|" + path] = new() { Sid = sid, Path = path, Authorized = true,
                CommandIdentity = "exact command", ExecutablePath = @"C:\retained.exe", ExecutableHash = "hash" };
            return true;
        }, true, default);
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.StatePath))!;
        var user = envelope["openWith"]!["users"]![sid]!.AsObject();
        foreach (var field in new[] { "generations", "reviewVersions", "visibilityIntents", "missingRegistrations", "pendingIsolations", "quarantines" }) user.Remove(field);
        await File.WriteAllTextAsync(fixture.StatePath, envelope.ToJsonString());
        var store = fixture.CreateStore();
        var classic = CreateStates("keep classic", false);
        await store.SaveAsync(classic, default);
        await fixture.CreateStore().WithOpenWithAsync(state =>
        {
            Assert.Equal("Changed", state.Users[sid].Reviews[path]);
            Assert.Equal("observed-stamp", state.Users[sid].Registrations[path]);
            Assert.Empty(state.Users[sid].VisibilityIntents);
            Assert.Empty(state.Users[sid].Generations);
            Assert.Empty(state.Users[sid].PendingIsolations);
            Assert.Empty(state.Users[sid].Quarantines);
            Assert.True(state.Rules[sid + "|" + path].Authorized);
            return true;
        }, false, default);
        Assert.Equal("keep classic", (await store.LoadAsync(default))["entry"].DisplayName);
    }

    // Permanent: independent section merge, recovery and reset protect saved authorization
    // from concurrent ordinary menu saves; schema 2 must migrate without invented rules.
    [Fact]
    public async Task Win11RecoveryJournal_PreservesAliasesTypesAndScopeAcrossReload()
    {
        using var fixture = new StateStoreFixture();
        var states = CreateStates("menu", false);
        states["entry"].Windows11BlockBackups = [new()
        {
            UserSid = "S-1-5-21-1-2-3-1001", Clsid = "{11111111-1111-1111-1111-111111111111}",
            PhysicalRegistryPath = @"HKEY_USERS\S-1-5-21-1-2-3-1001\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked",
            OriginalValues = [new() { Name = "11111111-1111-1111-1111-111111111111", Existed = true,
                Kind = (int)RegistryValueKind.MultiString, StringArrayValue = ["Old setting", "Retain data"] },
                new() { Name = "{11111111-1111-1111-1111-111111111111}", Existed = true,
                    Kind = (int)RegistryValueKind.Binary, BinaryBase64 = Convert.ToBase64String([1, 2, 3]) }]
        }];
        await fixture.CreateStore().SaveAsync(states, default);
        var loaded = (await fixture.CreateStore().LoadAsync(default))["entry"];
        var backup = Assert.Single(loaded.Windows11BlockBackups);
        Assert.Equal(states["entry"].Windows11BlockBackups[0].PhysicalRegistryPath, backup.PhysicalRegistryPath);
        Assert.Equal("S-1-5-21-1-2-3-1001", backup.UserSid);
        Assert.Equal(2, backup.OriginalValues.Count);
        Assert.Equal(new[] { "Old setting", "Retain data" }, backup.OriginalValues[0].StringArrayValue);
        Assert.Equal((int)RegistryValueKind.MultiString, backup.OriginalValues[0].Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(backup.OriginalValues[1].BinaryBase64!));
        Assert.False(loaded.DesiredEnabled); // Recovery metadata must not change the saved policy.
    }

    [Fact]
    public async Task OpenWithSection_SurvivesClassicSavesAndRecovery_AndResetRevokesIt()
    {
        using var fixture = new StateStoreFixture();
        await File.WriteAllTextAsync(fixture.StatePath, "{\"schemaVersion\":2,\"states\":{}}");
        var store = fixture.CreateStore();
        Assert.Empty(await store.WithOpenWithAsync(s => s.Rules, false, default));
        var staleClassic = await store.LoadAsync(default);
        await store.WithOpenWithAsync(s =>
        {
            s.Rules["user|path"] = new OpenWithProtectionRule
            {
                Sid = "S-1-5-21-1-2-3-1001", Path = @"HKEY_USERS\S-1-5-21-1-2-3-1001\Software\Classes\Applications\example.exe",
                Authorized = true, CommandIdentity = "verified-generation", ExecutablePath = @"C:\Example\example.exe",
                ExecutableHash = "verified-binary"
            };
            var rule = s.Rules["user|path"];
            s.Users[rule.Sid] = new()
            {
                Registrations = new() { [rule.Path] = "observed" },
                Reviews = new() { [rule.Path] = "Changed" },
                Quarantines = new() { [rule.Path] = "owned-visibility" },
                PendingIsolations = new() { [rule.Path] = "Changed" },
                VisibilityIntents = new() { [rule.Path] = new() { BeforeGeneration = "physical-generation",
                    CommandIdentity = "verified-generation", ContentGeneration = "physical-content", IsolationReviewStatus = "Changed" } }
            };
            return true;
        }, true, default);
        await Task.WhenAll(store.SaveAsync(staleClassic, default), store.WithOpenWithAsync(s =>
        {
            s.Rules["user|path"].Attempts = 1;
            s.Rules["user|path"].Outcome = "Uncertain";
            return true;
        }, true, default));
        await store.SaveAsync(CreateStates("after-monitor", false), default);
        var rule = Assert.Single(await fixture.CreateStore().WithOpenWithAsync(s => s.Rules.Values.ToArray(), false, default));
        Assert.True(rule.Authorized);
        Assert.Equal("Uncertain", rule.Outcome);
        Assert.Equal(1, rule.Attempts);
        await fixture.CreateStore().WithOpenWithAsync(s =>
        {
            var baseline = s.Users[rule.Sid];
            Assert.Equal("owned-visibility", baseline.Quarantines[rule.Path]);
            Assert.Equal("Changed", baseline.PendingIsolations[rule.Path]);
            Assert.Equal("Changed", baseline.VisibilityIntents[rule.Path].IsolationReviewStatus);
            return true;
        }, false, default);
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath)))
            Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
        await File.WriteAllTextAsync(fixture.StatePath, "{corrupt");
        var recovered = fixture.CreateStore();
        Assert.Single(await recovered.WithOpenWithAsync(s => s.Rules, false, default));
        Assert.Equal(ContextMenuStateStoreHealth.RecoveredFromBackup, recovered.Health);
        Assert.Equal("owned-visibility", await recovered.WithOpenWithAsync(s => s.Users[rule.Sid].Quarantines[rule.Path], false, default));
        await recovered.ResetAsync(default);
        Assert.Empty(await recovered.WithOpenWithAsync(s => s.Rules, false, default));
        Assert.Empty(await recovered.WithOpenWithAsync(s => s.Users, false, default));
        Assert.Empty(await recovered.LoadAsync(default));
    }

    [Fact]
    public async Task SaveAsync_ReplacesCurrentAndKeepsPreviousValidatedStateAsBackup()
    {
        using var fixture = new StateStoreFixture();
        var store = fixture.CreateStore();

        await store.SaveAsync(CreateStates("first", isDeleted: true), CancellationToken.None);
        await store.SaveAsync(CreateStates("second", isDeleted: false), CancellationToken.None);

        var current = await fixture.CreateStore().LoadAsync(CancellationToken.None);
        var backup = await fixture.CreateStore(fixture.BackupPath).LoadAsync(CancellationToken.None);

        Assert.Equal("second", current["entry"].DisplayName);
        Assert.Equal("first", backup["entry"].DisplayName);
        Assert.True(backup["entry"].IsDeleted);
        Assert.Equal("C:\\Backups\\entry.reg", backup["entry"].BackupFilePath);
        Assert.True(File.Exists(fixture.BackupPath));
    }

    [Fact]
    public async Task LoadAsync_CorruptCurrentWithValidBackup_QuarantinesAndRestoresBackup()
    {
        using var fixture = new StateStoreFixture();
        var store = fixture.CreateStore();
        await store.SaveAsync(CreateStates("known-good", isDeleted: true), CancellationToken.None);
        await store.SaveAsync(CreateStates("newer", isDeleted: false), CancellationToken.None);
        await File.WriteAllTextAsync(fixture.StatePath, "{ \"schemaVersion\": 2, \"states\": { \"win11|{2430f218-b743-4fd6-97bf-5c76541b4ae9}|File\": { \"desiredEnabled\": false }");

        var recoveryStore = fixture.CreateStore();
        var recoveryNotifications = new List<ContextMenuStateStoreRecovery>();
        recoveryStore.RecoveryOccurred += (_, recovery) => recoveryNotifications.Add(recovery);
        var recovered = await recoveryStore.LoadAsync(CancellationToken.None);

        Assert.Equal(ContextMenuStateStoreHealth.RecoveredFromBackup, recoveryStore.Health);
        Assert.Single(recoveryNotifications);
        Assert.Equal(ContextMenuStateStoreHealth.RecoveredFromBackup, recoveryNotifications[0].Health);
        Assert.Equal("known-good", recovered["entry"].DisplayName);
        Assert.True(recovered["entry"].IsDeleted);
        Assert.False(recovered["entry"].ObservedEnabled);
        Assert.False(recovered["entry"].DesiredEnabled);
        Assert.True(recovered["entry"].IsPendingApproval);
        Assert.Equal(ContextMenuChangeKind.Reappeared, recovered["entry"].PendingApprovalChangeKind);
        Assert.Equal(@"C:\Backups\entry.reg", recovered["entry"].BackupFilePath);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), recovered["entry"].DeletedAtUtc);
        Assert.True(recovered["entry"].SuppressNextDetection);
        Assert.Equal(3, recovered["entry"].ConsecutiveMissingSnapshots);
        Assert.Equal("preserve every persisted dimension", recovered["entry"].Notes);
        Assert.True(Directory.EnumerateFiles(fixture.QuarantineDirectory, "context-menu-state.json", SearchOption.AllDirectories).Any());

        var restoredCurrent = await fixture.CreateStore().LoadAsync(CancellationToken.None);
        Assert.Equal("known-good", restoredCurrent["entry"].DisplayName);
    }

    [Fact]
    public async Task LoadAsync_CorruptCurrentAndBackup_ReturnsFreshStateAndCanSaveAgain()
    {
        using var fixture = new StateStoreFixture();
        var store = fixture.CreateStore();
        await store.SaveAsync(CreateStates("first", isDeleted: false), CancellationToken.None);
        await store.SaveAsync(CreateStates("second", isDeleted: false), CancellationToken.None);
        await File.WriteAllTextAsync(fixture.StatePath, "{ \"states\": ");
        await File.WriteAllTextAsync(fixture.BackupPath, "{ \"states\": ");

        var recoveryStore = fixture.CreateStore();
        var recovered = await recoveryStore.LoadAsync(CancellationToken.None);

        Assert.Empty(recovered);
        Assert.Equal(ContextMenuStateStoreHealth.ResetAfterCorruption, recoveryStore.Health);
        Assert.Equal(2, Directory.EnumerateFiles(fixture.QuarantineDirectory, "context-menu-state.json*", SearchOption.AllDirectories).Count());

        await recoveryStore.SaveAsync(CreateStates("rebuilt", isDeleted: false), CancellationToken.None);
        var rebuilt = await fixture.CreateStore().LoadAsync(CancellationToken.None);
        Assert.Equal("rebuilt", rebuilt["entry"].DisplayName);
    }

    [Fact]
    public async Task LoadAsync_ValidCurrent_IgnoresCorruptBackup()
    {
        using var fixture = new StateStoreFixture();
        var store = fixture.CreateStore();
        await store.SaveAsync(CreateStates("first", isDeleted: false), CancellationToken.None);
        await store.SaveAsync(CreateStates("current", isDeleted: false), CancellationToken.None);
        await File.WriteAllTextAsync(fixture.BackupPath, "{ \"states\": ");

        var legacyStore = fixture.CreateStore();
        var loaded = await legacyStore.LoadAsync(CancellationToken.None);

        Assert.Equal("current", loaded["entry"].DisplayName);
        Assert.False(Directory.Exists(fixture.QuarantineDirectory));
    }

    [Fact]
    public async Task ResetAsync_RemovesCurrentAndBackup_ThenLoadsEmpty()
    {
        using var fixture = new StateStoreFixture();
        var store = fixture.CreateStore();
        await store.SaveAsync(CreateStates("first", isDeleted: false), CancellationToken.None);
        await store.SaveAsync(CreateStates("second", isDeleted: false), CancellationToken.None);

        await store.ResetAsync(CancellationToken.None);

        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(File.Exists(fixture.BackupPath));
        Assert.Empty(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_LegacyDictionary_RemainsSupported()
    {
        using var fixture = new StateStoreFixture();
        await File.WriteAllTextAsync(
            fixture.StatePath,
            JsonSerializer.Serialize(CreateStates("legacy", isDeleted: true)));

        var legacyStore = fixture.CreateStore();
        var loaded = await legacyStore.LoadAsync(CancellationToken.None);

        Assert.Equal("legacy", loaded["entry"].DisplayName);
        Assert.True(loaded["entry"].IsDeleted);
        Assert.Equal(ContextMenuStateStoreHealth.Healthy, legacyStore.Health);
    }

    [Fact]
    public async Task ShellVerbVisibilityProvenance_RoundTripsAcrossStoreReload()
    {
        using var fixture = new StateStoreFixture();
        var states = CreateStates("with-provenance", isDeleted: false);
        states["entry"].ShellVerbVisibilityProvenance.Add(new PersistedShellVerbVisibilityProvenance
        {
            PhysicalRegistryPath = @"HKEY_USERS\S-1-5-21-test\Software\Classes\Test.File\shell\print",
            GenerationFingerprint = "ABC123",
            OriginalValues =
            [
                new PersistedRegistryValueSnapshot
                {
                    Name = "ProgrammaticAccessOnly",
                    Existed = true,
                    Kind = (int)Microsoft.Win32.RegistryValueKind.ExpandString,
                    StringValue = "%TEST_VALUE%"
                }
            ]
        });

        await fixture.CreateStore().SaveAsync(states, CancellationToken.None);
        var loaded = await fixture.CreateStore().LoadAsync(CancellationToken.None);

        var provenance = Assert.Single(loaded["entry"].ShellVerbVisibilityProvenance);
        Assert.Equal(@"HKEY_USERS\S-1-5-21-test\Software\Classes\Test.File\shell\print", provenance.PhysicalRegistryPath);
        Assert.Equal("ABC123", provenance.GenerationFingerprint);
        var value = Assert.Single(provenance.OriginalValues);
        Assert.Equal((int)Microsoft.Win32.RegistryValueKind.ExpandString, value.Kind);
        Assert.Equal("%TEST_VALUE%", value.StringValue);
    }

    [Fact]
    public async Task RecycleBinPinToStartProvenance_RoundTripsAcrossStoreReload()
    {
        using var fixture = new StateStoreFixture();
        var states = CreateStates("pin-to-start", isDeleted: false);
        states["entry"].RecycleBinPinToStartProvenances.Add(new PersistedRecycleBinPinToStartProvenance
        {
            UserSid = "S-1-5-21-1-2-3-1001",
            MachineHandlerClsid = "{470C0EBD-5D73-4D58-9CED-E91E22E23282}",
            FolderBefore = new PersistedRecycleBinRegistryKeySnapshot
            {
                Existed = true,
                DefaultValue = new PersistedRegistryValueSnapshot
                { Existed = true, Kind = (int)RegistryValueKind.ExpandString, StringValue = "%TEST%" }
            },
            DirectoryBefore = new PersistedRecycleBinRegistryKeySnapshot(),
            FolderManaged = new PersistedRecycleBinRegistryKeySnapshot { Existed = true },
            DirectoryManaged = new PersistedRecycleBinRegistryKeySnapshot { Existed = true }
        });
        await fixture.CreateStore().SaveAsync(states, CancellationToken.None);
        var loaded = Assert.Single((await fixture.CreateStore().LoadAsync(CancellationToken.None))["entry"].RecycleBinPinToStartProvenances);
        Assert.True(loaded.FolderBefore.Existed);
        Assert.Equal((int)RegistryValueKind.ExpandString, loaded.FolderBefore.DefaultValue.Kind);
        Assert.Equal("%TEST%", loaded.FolderBefore.DefaultValue.StringValue);
        Assert.False(loaded.DirectoryBefore.Existed);
    }

    [Fact]
    public async Task ShellVerbVisibilityProvenance_ReloadedAfterRestart_RestoresExactRegistryState()
    {
        using var fixture = new StateStoreFixture();
        var keyPath = $@"Software\ContextMenuMgr.Tests\StateStoreProvenance\{Guid.NewGuid():N}";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            {
                key.SetValue("CommandFlags", 0x48, RegistryValueKind.DWord);
                using var command = key.CreateSubKey("command", writable: true)!;
                command.SetValue(null, "restart-test.exe \"%1\"", RegistryValueKind.String);
            }

            PersistedShellVerbVisibilityProvenance provenance;
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
            {
                var disable = ShellVerbVisibilityTransaction.Create(
                    key,
                    key.Name,
                    requestedVisible: false,
                    existingProvenance: null);
                disable.Apply(key);
                provenance = Assert.IsType<PersistedShellVerbVisibilityProvenance>(disable.Provenance);
            }

            var states = CreateStates("restart-provenance", isDeleted: false);
            states["entry"].ShellVerbVisibilityProvenance.Add(provenance);
            await fixture.CreateStore().SaveAsync(states, CancellationToken.None);

            // A new store instance represents the next backend process lifetime.
            var reloaded = await fixture.CreateStore().LoadAsync(CancellationToken.None);
            var reloadedProvenance = Assert.Single(reloaded["entry"].ShellVerbVisibilityProvenance);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
            {
                var enable = ShellVerbVisibilityTransaction.Create(
                    key,
                    key.Name,
                    requestedVisible: true,
                    reloadedProvenance);
                enable.Apply(key);
                Assert.True(enable.Verify(key));
                Assert.Null(key.GetValue(ShellVerbVisibility.ManagedMarkerName));
                Assert.Equal(0x48, Convert.ToInt32(key.GetValue("CommandFlags")));
                using var command = key.OpenSubKey("command", writable: false)!;
                Assert.Equal("restart-test.exe \"%1\"", command.GetValue(null));
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task OldStateWithoutVisibilityProvenance_DeserializesToEmptyCollection()
    {
        using var fixture = new StateStoreFixture();
        await File.WriteAllTextAsync(
            fixture.StatePath,
            "{\"entry\":{\"id\":\"entry\",\"displayName\":\"legacy\",\"sourceRootPath\":\"Test.File\\\\shell\"}}");

        var loaded = await fixture.CreateStore().LoadAsync(CancellationToken.None);

        Assert.Empty(loaded["entry"].ShellVerbVisibilityProvenance);
        Assert.Empty(loaded["entry"].RecycleBinPinToStartProvenances);
    }

    [Fact]
    public async Task LoadAsync_UnsupportedFutureSchema_DoesNotQuarantineOrReset()
    {
        using var fixture = new StateStoreFixture();
        await File.WriteAllTextAsync(fixture.StatePath, "{ \"schemaVersion\": 99, \"states\": {} }");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.CreateStore().LoadAsync(CancellationToken.None));

        Assert.True(File.Exists(fixture.StatePath));
        Assert.False(Directory.Exists(fixture.QuarantineDirectory));
    }

    private static Dictionary<string, PersistedContextMenuState> CreateStates(string displayName, bool isDeleted)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["entry"] = new PersistedContextMenuState
            {
                Id = "entry",
                Category = ContextMenuCategory.File,
                EntryKind = ContextMenuEntryKind.ShellVerb,
                KeyName = "entry",
                DisplayName = displayName,
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\*\shell\entry",
                BackendRegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\*\shell\entry",
                SourceRootPath = @"*\shell",
                ObservedEnabled = !isDeleted,
                DesiredEnabled = false,
                IsDeleted = isDeleted,
                IsPendingApproval = true,
                PendingApprovalChangeKind = ContextMenuChangeKind.Reappeared,
                BackupFilePath = @"C:\Backups\entry.reg",
                DeletedAtUtc = DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"),
                SuppressNextDetection = true,
                ConsecutiveMissingSnapshots = 3,
                Notes = "preserve every persisted dimension"
            }
        };

    private sealed class StateStoreFixture : IDisposable
    {
        public StateStoreFixture()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-StateStoreTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootDirectory);
        }

        public string RootDirectory { get; }

        public string StatePath => Path.Combine(RootDirectory, "context-menu-state.json");

        public string BackupPath => StatePath + ".bak";

        public string QuarantineDirectory => Path.Combine(RootDirectory, "Quarantine");

        public ContextMenuStateStore CreateStore(string? path = null)
            => new(path ?? StatePath, quarantineDirectory: QuarantineDirectory);

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
