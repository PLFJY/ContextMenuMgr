using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class RecycleBinPinToStartTests
{
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private const string Handler = "{470C0EBD-5D73-4D58-9CED-E91E22E23282}";

    [Theory]
    [InlineData(null, null)]
    [InlineData("Start Menu Pin", null)]
    [InlineData("{00000000-0000-0000-0000-000000000000}", null)]
    [InlineData("{470C0EBD-5D73-4d58-9CED-E91E22E23282}", Handler)]
    public void MachineHandler_OnlyUsableClsidIsAccepted(string? source, string? expected)
        => Assert.Equal(expected, RecycleBinPinToStartMutation.ValidateMachineHandler(source));

    [Fact]
    public async Task DisableAndEnable_UseBothSidQualifiedKeys_AndRestoreAbsentBaseline()
    {
        var registry = new FakeRegistry { Handler = Handler };
        var mutation = new RecycleBinPinToStartMutation(registry);
        Assert.False(mutation.IsManagedDisabled(Sid, Handler, null));
        var provenance = await mutation.ExecuteAsync(false, Sid, Handler, null, _ => Task.CompletedTask);
        Assert.NotNull(provenance);
        Assert.False(provenance.FolderBefore.Existed);
        Assert.False(provenance.DirectoryBefore.Existed);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.FolderPath(Sid)).DefaultValue.Existed);
        Assert.Equal(Handler, registry.Read(RecycleBinPinToStartMutation.DirectoryPath(Sid)).DefaultValue.StringValue);
        Assert.True(mutation.IsManagedDisabled(Sid, Handler, provenance));
        Assert.All(registry.WrittenPaths, path => Assert.StartsWith($@"HKEY_USERS\{Sid}\", path));

        var restored = await mutation.ExecuteAsync(true, Sid, Handler, provenance, _ => Task.CompletedTask);
        Assert.Null(restored);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.FolderPath(Sid)).Existed);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.DirectoryPath(Sid)).Existed);
        await mutation.ExecuteAsync(true, Sid, Handler, null, _ => Task.CompletedTask); // idempotent
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreExistingUserOverride_IsNeverOverwritten(bool folder)
    {
        var registry = new FakeRegistry { Handler = Handler };
        var path = folder ? RecycleBinPinToStartMutation.FolderPath(Sid) : RecycleBinPinToStartMutation.DirectoryPath(Sid);
        var existing = new PersistedRecycleBinRegistryKeySnapshot
        {
            Existed = true,
            DefaultValue = new PersistedRegistryValueSnapshot
            { Existed = true, Kind = (int)RegistryValueKind.ExpandString, StringValue = "%CUSTOM%" },
            OtherValues = [new PersistedRegistryValueSnapshot
                { Existed = true, Name = "ThirdParty", Kind = (int)RegistryValueKind.DWord, IntegerValue = 7 }],
            SubKeyNames = ["Child"]
        };
        registry.Keys[path] = existing;
        var captured = RecycleBinPinToStartMutation.CaptureProvenance(Sid, Handler,
            folder ? existing : new(), folder ? new() : existing);
        Assert.True((folder ? captured.FolderBefore : captured.DirectoryBefore).Existed);
        Assert.Equal((int)RegistryValueKind.ExpandString,
            (folder ? captured.FolderBefore : captured.DirectoryBefore).DefaultValue.Kind);
        Assert.Equal("%CUSTOM%", (folder ? captured.FolderBefore : captured.DirectoryBefore).DefaultValue.StringValue);
        var mutation = new RecycleBinPinToStartMutation(registry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation.ExecuteAsync(false, Sid, Handler, null, _ => Task.CompletedTask));
        Assert.Same(existing, registry.Read(path));
        Assert.Empty(registry.WrittenPaths);
    }

    [Fact]
    public async Task FailureAfterFolderWrite_RollsBackWithoutCommitting()
    {
        var registry = new FakeRegistry { Handler = Handler };
        registry.AfterWrite = (path, count) =>
        {
            if (count == 2 && path == RecycleBinPinToStartMutation.DirectoryPath(Sid))
                throw new InvalidOperationException("second write failed");
        };
        var committed = false;
        var error = await Assert.ThrowsAsync<RecycleBinPinToStartMutationException>(() =>
            new RecycleBinPinToStartMutation(registry).ExecuteAsync(false, Sid, Handler, null,
                _ => { committed = true; return Task.CompletedTask; }));
        Assert.False(error.RollbackConflict);
        Assert.False(committed);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.FolderPath(Sid)).Existed);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.DirectoryPath(Sid)).Existed);
    }

    [Fact]
    public async Task VerificationFailure_RollsBackBothKeys()
    {
        var registry = new FakeRegistry { Handler = Handler };
        registry.AfterWrite = (path, _) =>
        {
            if (path == RecycleBinPinToStartMutation.DirectoryPath(Sid))
                registry.Keys[path] = new PersistedRecycleBinRegistryKeySnapshot { Existed = true };
        };
        var error = await Assert.ThrowsAsync<RecycleBinPinToStartMutationException>(() =>
            new RecycleBinPinToStartMutation(registry).ExecuteAsync(false, Sid, Handler, null, _ => Task.CompletedTask));
        Assert.True(error.RollbackConflict); // The unexpected value belongs to another writer.
        Assert.False(registry.Read(RecycleBinPinToStartMutation.FolderPath(Sid)).Existed);
        Assert.True(registry.Read(RecycleBinPinToStartMutation.DirectoryPath(Sid)).Existed);
    }

    [Fact]
    public async Task TransientReadbackFailure_RollsBackBothUnchangedWrites()
    {
        var registry = new FakeRegistry { Handler = Handler };
        registry.ReadOverride = (path, count) =>
            path == RecycleBinPinToStartMutation.FolderPath(Sid) && count == 4
                ? new PersistedRecycleBinRegistryKeySnapshot { Existed = false }
                : null;
        var error = await Assert.ThrowsAsync<RecycleBinPinToStartMutationException>(() =>
            new RecycleBinPinToStartMutation(registry).ExecuteAsync(false, Sid, Handler, null, _ => Task.CompletedTask));
        Assert.False(error.RollbackConflict);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.FolderPath(Sid)).Existed);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.DirectoryPath(Sid)).Existed);
    }

    [Fact]
    public async Task FailedStateSave_RollsBackBothKeys()
    {
        var registry = new FakeRegistry { Handler = Handler };
        var error = await Assert.ThrowsAsync<RecycleBinPinToStartMutationException>(() =>
            new RecycleBinPinToStartMutation(registry).ExecuteAsync(false, Sid, Handler, null,
                _ => throw new IOException("save failed")));
        Assert.False(error.RollbackConflict);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.FolderPath(Sid)).Existed);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.DirectoryPath(Sid)).Existed);
    }

    [Fact]
    public async Task ExternalChangeBeforeRollback_IsPreservedAndReported()
    {
        var registry = new FakeRegistry { Handler = Handler };
        var folder = RecycleBinPinToStartMutation.FolderPath(Sid);
        registry.AfterWrite = (path, _) =>
        {
            if (path == RecycleBinPinToStartMutation.DirectoryPath(Sid))
            {
                registry.Keys[folder] = new PersistedRecycleBinRegistryKeySnapshot
                { Existed = true, OtherValues = [new PersistedRegistryValueSnapshot
                    { Name = "External", Existed = true, Kind = (int)RegistryValueKind.String, StringValue = "keep" }] };
                throw new IOException("second write failed");
            }
        };
        var error = await Assert.ThrowsAsync<RecycleBinPinToStartMutationException>(() =>
            new RecycleBinPinToStartMutation(registry).ExecuteAsync(false, Sid, Handler, null, _ => Task.CompletedTask));
        Assert.True(error.RollbackConflict);
        Assert.Equal("keep", Assert.Single(registry.Read(folder).OtherValues).StringValue);
    }

    [Fact]
    public async Task RepeatedDisable_PreservesOriginalProvenance_AndStaleGenerationFails()
    {
        var registry = new FakeRegistry { Handler = Handler };
        var mutation = new RecycleBinPinToStartMutation(registry);
        var provenance = await mutation.ExecuteAsync(false, Sid, Handler, null, _ => Task.CompletedTask);
        var count = registry.WrittenPaths.Count;
        var repeated = await mutation.ExecuteAsync(false, Sid, Handler, provenance, _ => Task.CompletedTask);
        Assert.Same(provenance, repeated);
        Assert.Equal(count, registry.WrittenPaths.Count);
        registry.Keys[RecycleBinPinToStartMutation.FolderPath(Sid)] = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation.ExecuteAsync(true, Sid, Handler, provenance, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task LostStateDatabase_DoesNotAdoptManagedShape_OrAffectAnotherUser()
    {
        var registry = new FakeRegistry { Handler = Handler };
        var mutation = new RecycleBinPinToStartMutation(registry);
        await mutation.ExecuteAsync(false, Sid, Handler, null, _ => Task.CompletedTask);
        var otherSid = "S-1-5-21-111-222-333-1002";
        Assert.False(mutation.IsManagedDisabled(otherSid, Handler, null));
        await mutation.ExecuteAsync(true, otherSid, Handler, null, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutation.ExecuteAsync(true, Sid, Handler, null, _ => Task.CompletedTask));
        Assert.True(registry.Read(RecycleBinPinToStartMutation.FolderPath(Sid)).Existed);
        Assert.True(registry.Read(RecycleBinPinToStartMutation.DirectoryPath(Sid)).Existed);
        Assert.False(registry.Read(RecycleBinPinToStartMutation.FolderPath(otherSid)).Existed);
    }

    [Fact]
    public void MissingMachineSource_IsUnsupported()
    {
        var registry = new FakeRegistry();
        Assert.Null(new RecycleBinPinToStartMutation(registry).GetMachineHandler());
        Assert.Empty(registry.WrittenPaths);
    }

    [Fact]
    public async Task CatalogQuery_MissingMachineSource_DoesNotExposeItem()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CatalogFixture(new FakeRegistry());
        var user = new BackendUserContext(WindowsIdentity.GetCurrent().User!.Value, "test", "", "", "", null);
        var original = new ContextMenuEntry { Id = RecycleBinPinToStartMutation.Id };
        var result = await fixture.Catalog.GetContextMenuItemStateAsync(original.Id, original, CancellationToken.None, user);
        Assert.True(result.Success);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task TargetedCatalogQuery_UsesPhysicalStateAndDoesNotNeedSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = new FakeRegistry { Handler = Handler };
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using var fixture = new CatalogFixture(registry);
        var user = new BackendUserContext(sid, "test", "", "", "", null);
        var original = new ContextMenuEntry { Id = RecycleBinPinToStartMutation.Id };
        var enabled = await fixture.Catalog.GetContextMenuItemStateAsync(original.Id, original, CancellationToken.None, user);
        Assert.True(enabled.Success);
        Assert.True(enabled.Item!.IsEnabled);
        var provenance = await new RecycleBinPinToStartMutation(registry).ExecuteAsync(false, sid, Handler, null, _ => Task.CompletedTask);
        var states = await fixture.Store.LoadAsync(CancellationToken.None);
        states[original.Id] = PersistedContextMenuState.FromEntry(enabled.Item);
        states[original.Id].RecycleBinPinToStartProvenances.Add(provenance!);
        await fixture.Store.SaveAsync(states, CancellationToken.None);
        var disabled = await fixture.Catalog.GetContextMenuItemStateAsync(original.Id, original, CancellationToken.None, user);
        Assert.True(disabled.Success);
        Assert.False(disabled.Item!.IsEnabled);
    }

    [Fact]
    public async Task CatalogToggle_CommitsProvenanceAndTargetedQuerySeesAuthoritativeState()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = new FakeRegistry { Handler = Handler };
        using var fixture = new CatalogFixture(registry);
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var user = new BackendUserContext(sid, "test", "", "", "", null);
        var disabled = await fixture.Catalog.ApplyDesiredStateAsync(
            RecycleBinPinToStartMutation.Id, false, CancellationToken.None, user);
        Assert.True(disabled.Success, disabled.Message);
        Assert.False(disabled.Item!.IsEnabled);
        var persisted = (await fixture.Store.LoadAsync(CancellationToken.None))[RecycleBinPinToStartMutation.Id];
        Assert.Single(persisted.RecycleBinPinToStartProvenances);
        var queried = await fixture.Catalog.GetContextMenuItemStateAsync(disabled.Item.Id, disabled.Item,
            CancellationToken.None, user);
        Assert.True(queried.Success);
        Assert.False(queried.Item!.IsEnabled);

        var enabled = await fixture.Catalog.ApplyDesiredStateAsync(
            RecycleBinPinToStartMutation.Id, true, CancellationToken.None, user);
        Assert.True(enabled.Success, enabled.Message);
        Assert.True(enabled.Item!.IsEnabled);
        Assert.Empty((await fixture.Store.LoadAsync(CancellationToken.None))
            [RecycleBinPinToStartMutation.Id].RecycleBinPinToStartProvenances);
    }

    [Fact]
    public async Task CatalogToggle_WithoutFrontendSid_FailsWithoutRegistryWrite()
    {
        var registry = new FakeRegistry { Handler = Handler };
        using var fixture = new CatalogFixture(registry);
        var result = await fixture.Catalog.ApplyDesiredStateAsync(
            RecycleBinPinToStartMutation.Id, false, CancellationToken.None, userContext: null);
        Assert.False(result.Success);
        Assert.Empty(registry.WrittenPaths);
    }

    [Fact]
    public async Task CatalogToggle_StoresIndependentProvenanceForTwoUsers()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = new FakeRegistry { Handler = Handler };
        using var fixture = new CatalogFixture(registry);
        var first = new BackendUserContext(WindowsIdentity.GetCurrent().User!.Value, "first", "", "", "", null);
        var second = new BackendUserContext(Sid, "second", "", "", "", null);
        Assert.True((await fixture.Catalog.ApplyDesiredStateAsync(RecycleBinPinToStartMutation.Id,
            false, CancellationToken.None, first)).Success);
        Assert.True((await fixture.Catalog.ApplyDesiredStateAsync(RecycleBinPinToStartMutation.Id,
            false, CancellationToken.None, second)).Success);
        var saved = (await fixture.Store.LoadAsync(CancellationToken.None))[RecycleBinPinToStartMutation.Id];
        Assert.Equal(2, saved.RecycleBinPinToStartProvenances.Count);
        Assert.True((await fixture.Catalog.ApplyDesiredStateAsync(RecycleBinPinToStartMutation.Id,
            true, CancellationToken.None, first)).Success);
        saved = (await fixture.Store.LoadAsync(CancellationToken.None))[RecycleBinPinToStartMutation.Id];
        Assert.Equal(Sid, Assert.Single(saved.RecycleBinPinToStartProvenances).UserSid);
        var otherState = await fixture.Catalog.GetContextMenuItemStateAsync(RecycleBinPinToStartMutation.Id,
            new ContextMenuEntry { Id = RecycleBinPinToStartMutation.Id }, CancellationToken.None, second);
        Assert.False(otherState.Item!.IsEnabled);
    }

    private sealed class FakeRegistry : IRecycleBinPinToStartRegistry
    {
        public string? Handler { get; set; }
        public Dictionary<string, PersistedRecycleBinRegistryKeySnapshot> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> WrittenPaths { get; } = [];
        public Action<string, int>? AfterWrite { get; set; }
        public Func<string, int, PersistedRecycleBinRegistryKeySnapshot?>? ReadOverride { get; set; }
        private readonly Dictionary<string, int> _readCounts = new(StringComparer.OrdinalIgnoreCase);
        public string? ReadMachineHandler() => Handler;
        public PersistedRecycleBinRegistryKeySnapshot Read(string path)
        {
            _readCounts[path] = _readCounts.GetValueOrDefault(path) + 1;
            return ReadOverride?.Invoke(path, _readCounts[path]) ?? Keys.GetValueOrDefault(path) ?? new();
        }
        public void Write(string path, PersistedRecycleBinRegistryKeySnapshot expected, PersistedRecycleBinRegistryKeySnapshot next)
        {
            if (!RecycleBinPinToStartMutation.Same(Read(path), expected)) throw new IOException("concurrent change");
            WrittenPaths.Add(path);
            if (next.Existed) Keys[path] = next;
            else Keys.Remove(path);
            AfterWrite?.Invoke(path, WrittenPaths.Count);
        }
    }

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CmmPinToStart", Guid.NewGuid().ToString("N"));
        public ContextMenuStateStore Store { get; }
        public ContextMenuRegistryCatalog Catalog { get; }
        public CatalogFixture(IRecycleBinPinToStartRegistry registry)
        {
            Directory.CreateDirectory(_root);
            var logger = new FileLogger(Path.Combine(_root, "log.txt"));
            Store = new ContextMenuStateStore(Path.Combine(_root, "state.json"), logger);
            Catalog = new ContextMenuRegistryCatalog(logger, Store,
                new RegistryBackupService(Path.Combine(_root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(_root, "protection.json"), logger),
                new WindowsRegistryProtectionTargetAccessor(), null, registry);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
