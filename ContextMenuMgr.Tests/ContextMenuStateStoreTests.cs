using System.Text.Json;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ContextMenuStateStoreTests
{
    [Fact]
    public void BackendOperationHealth_PingFails_IsServiceUnavailable()
    {
        Assert.Equal(
            BackendOperationHealth.ServiceUnavailable,
            BackendOperationHealthClassifier.FromPingResult(pingSucceeded: false));
    }

    [Fact]
    public void BackendOperationHealth_PingSucceedsButSnapshotFails_IsOperationFailure()
    {
        Assert.Equal(
            BackendOperationHealth.OperationFailed,
            BackendOperationHealthClassifier.FromPingResult(pingSucceeded: true));
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
