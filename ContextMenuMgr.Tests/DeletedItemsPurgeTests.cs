using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: a recovery-record purge is conditional, user-scoped and durable before backup cleanup.
[Collection("Registry catalog integration")]
public sealed class DeletedItemsPurgeTests
{
    [Fact]
    public async Task AcknowledgingADeletedRecord_PreservesItsBackupAndRecoveryIdentity()
    {
        using var fixture = new Fixture();
        var item = fixture.Deleted("deleted-acknowledgement");
        await fixture.Store.SaveAsync(new Dictionary<string, PersistedContextMenuState> { [item.Id] = item }, default);
        var response = await fixture.Catalog.AcknowledgeItemStateAsync(item.Id, default, fixture.Context, item.ToDeletedEntry());
        Assert.True(response.Success, response.Message);
        Assert.True(response.Item!.IsDeleted);
        Assert.Equal(item.DeletedAtUtc, response.Item.DeletedAtUtc);
        var stored = (await fixture.Store.LoadAsync(default))[item.Id];
        Assert.True(stored.IsDeleted);
        Assert.Equal(item.BackupFilePath, stored.BackupFilePath);
        Assert.True(File.Exists(item.BackupFilePath));
    }

    [Fact]
    public async Task FixedSelection_LeavesOtherCategoriesRestoredAndRedeletedRecordsIntact()
    {
        using var fixture = new Fixture();
        var selected = fixture.Deleted("selected");
        var omitted = fixture.Deleted("omitted");
        var restored = fixture.Deleted("restored");
        restored.IsDeleted = false;
        var redeleted = fixture.Deleted("redeleted");
        var other = fixture.Deleted("other", ContextMenuCategory.Folder);
        await fixture.Store.SaveAsync(new[] { selected, omitted, restored, redeleted, other }.ToDictionary(state => state.Id), default);

        var response = await fixture.Catalog.PurgeDeletedItemsAsync(new()
        {
            Category = ContextMenuCategory.File,
            Items = [Reference(selected), Reference(restored), Reference(other), new(redeleted.Id, redeleted.DeletedAtUtc!.Value.AddMinutes(-1))]
        }, default, fixture.Context);

        Assert.True(response.Success, response.Message);
        Assert.Equal(selected.Id, Assert.Single(response.DeletedItemsPurge!.PurgedItems).Id);
        Assert.Equal(3, response.DeletedItemsPurge.SkippedItemIds.Count);
        var persisted = await fixture.Store.LoadAsync(default);
        Assert.False(persisted.ContainsKey(selected.Id));
        Assert.Equal(4, persisted.Count);
        Assert.False(File.Exists(selected.BackupFilePath));
        Assert.All(new[] { omitted, restored, redeleted, other }, state => Assert.True(File.Exists(state.BackupFilePath)));
    }

    [Fact]
    public async Task ForeignUserRecord_RejectsTheEntireSelectionBeforeSavingOrRemovingBackups()
    {
        using var fixture = new Fixture();
        var own = fixture.Deleted("own");
        var foreign = fixture.Deleted("foreign");
        foreign.BackendRegistryPath = @"HKEY_USERS\S-1-5-18\Software\Classes\*\shell\foreign";
        await fixture.Store.SaveAsync(new[] { own, foreign }.ToDictionary(state => state.Id), default);
        var response = await fixture.Catalog.PurgeDeletedItemsAsync(new()
        { Category = ContextMenuCategory.File, Items = [Reference(own), Reference(foreign)] }, default, fixture.Context);
        Assert.False(response.Success);
        Assert.Equal(2, (await fixture.Store.LoadAsync(default)).Count);
        Assert.True(File.Exists(own.BackupFilePath));
        Assert.True(File.Exists(foreign.BackupFilePath));
    }

    [Fact]
    public async Task FailedStateReplacement_PreservesRecoveryRecordAndBackup()
    {
        using var fixture = new Fixture();
        var item = fixture.Deleted("locked-state");
        await fixture.Store.SaveAsync(new Dictionary<string, PersistedContextMenuState> { [item.Id] = item }, default);
        using (File.Open(fixture.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.Catalog.PurgeDeletedItemsAsync(new()
            { Category = ContextMenuCategory.File, Items = [Reference(item)] }, default, fixture.Context));
        Assert.True((await fixture.Store.LoadAsync(default)).ContainsKey(item.Id));
        Assert.True(File.Exists(item.BackupFilePath));
    }

    [Fact]
    public async Task BackupCleanupFailure_IsReportedAfterDurableStateRemoval()
    {
        using var fixture = new Fixture();
        var item = fixture.Deleted("locked-backup");
        await fixture.Store.SaveAsync(new Dictionary<string, PersistedContextMenuState> { [item.Id] = item }, default);
        using (File.Open(item.BackupFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var response = await fixture.Catalog.PurgeDeletedItemsAsync(new()
            { Category = ContextMenuCategory.File, Items = [Reference(item)] }, default, fixture.Context);
            Assert.True(response.Success, response.Message);
            Assert.Equal(item.Id, Assert.Single(response.DeletedItemsPurge!.BackupCleanupFailedItemIds));
            Assert.Equal(item.Id, Assert.Single(response.DeletedItemsPurge.PurgedItems).Id);
        }
        Assert.False((await fixture.Store.LoadAsync(default)).ContainsKey(item.Id));
        Assert.True(File.Exists(item.BackupFilePath));
    }

    private static DeletedItemReference Reference(PersistedContextMenuState state) => new(state.Id, state.DeletedAtUtc);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-purge-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(_root, "state.json");
        public ContextMenuStateStore Store { get; }
        public ContextMenuRegistryCatalog Catalog { get; }
        public BackendUserContext Context { get; } = new(WindowsIdentity.GetCurrent().User!.Value, Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), null);

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_root, "backups"));
            var logger = new FileLogger(Path.Combine(_root, "backend.log"));
            Store = new ContextMenuStateStore(StatePath, logger);
            Catalog = new ContextMenuRegistryCatalog(logger, Store, new RegistryBackupService(Path.Combine(_root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(_root, "protection.json"), logger));
        }

        public PersistedContextMenuState Deleted(string name, ContextMenuCategory category = ContextMenuCategory.File)
        {
            var source = category == ContextMenuCategory.Folder ? @"Folder\shell" : @"*\shell";
            var backup = Path.Combine(_root, "backups", name + ".reg");
            File.WriteAllText(backup, "Windows Registry Editor Version 5.00");
            return new PersistedContextMenuState { Id = source + "|" + name, Category = category,
                EntryKind = ContextMenuEntryKind.ShellVerb, KeyName = name, DisplayName = name, SourceRootPath = source,
                RegistryPath = source + "\\" + name,
                BackendRegistryPath = $@"HKEY_USERS\{Context.Sid}\Software\Classes\{source}\{name}",
                IsDeleted = true, BackupFilePath = backup, DeletedAtUtc = DateTimeOffset.UtcNow };
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
