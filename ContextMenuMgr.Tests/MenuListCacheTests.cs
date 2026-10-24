using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>Permanent coverage for host isolation, incomplete discovery and scan/mutation ordering.</summary>
public sealed class MenuListCacheTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-cache-" + Guid.NewGuid().ToString("N"));
    private readonly List<MenuListCacheService> _caches = [];
    private const string Scope = "classic";
    private static ContextMenuEntry Entry(string id = "item", bool enabled = true) => new()
    { Id = id, DisplayName = id, IsEnabled = enabled, BackendRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shell\" + id };
    private MenuListCacheService Cache(string? fingerprint = "aaaaaaaaaaaa0000000000000000000000000000000000000000000000000000")
    { var cache = new MenuListCacheService(_root, fingerprint); _caches.Add(cache); return cache; }

    [Fact]
    public async Task RestartReadsCompleteSnapshotIncludingEmptyResult()
    {
        var cache = Cache();
        await cache.MergeAsync(Scope, new[] { Entry() }, true, cache.Version);
        var restored = await Cache().ReadAsync<ContextMenuEntry>(Scope);
        Assert.True(restored!.IsFromCache);
        Assert.True(restored.IsComplete);
        Assert.Equal("item", Assert.Single(restored).Id);
        await cache.MergeAsync(Scope, Array.Empty<ContextMenuEntry>(), true, cache.Version);
        Assert.Empty((await Cache().ReadAsync<ContextMenuEntry>(Scope))!);
    }

    [Theory]
    [InlineData("other-device", "same-user")]
    [InlineData("same-device", "other-user")]
    public async Task PortableCopiesNeverReuseAnotherDeviceOrUsersCache(string machine, string user)
    {
        var identity = RuntimeIdentityFingerprint.Compute("same-device", "same-user");
        var writer = Cache(identity);
        await writer.MergeAsync(Scope, new[] { Entry() }, true, writer.Version);
        Assert.Null(await Cache(RuntimeIdentityFingerprint.Compute(machine, user)).ReadAsync<ContextMenuEntry>(Scope));
        Assert.NotNull(await Cache(identity).ReadAsync<ContextMenuEntry>(Scope));
    }

    [Fact]
    public async Task MatchingDirectoryPrefixDoesNotBypassFullIdentityVerification()
    {
        var writer = Cache();
        await writer.MergeAsync(Scope, new[] { Entry() }, true, writer.Version);
        const string foreign = "aaaaaaaaaaaa1111111111111111111111111111111111111111111111111111";
        var source = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        var foreignDirectory = Path.Combine(_root, RuntimeIdentityFingerprint.Prefix(foreign), foreign);
        Directory.CreateDirectory(foreignDirectory);
        File.Copy(source, Path.Combine(foreignDirectory, Path.GetFileName(source)));
        Assert.Null(await Cache(foreign).ReadAsync<ContextMenuEntry>(Scope));
    }

    [Fact]
    public async Task UnknownIdentityUsesMemoryOnlyAndCorruptOrFutureFilesAreIgnored()
    {
        var memory = Cache(null);
        await memory.MergeAsync(Scope, new[] { Entry() }, true, memory.Version);
        Assert.NotNull(await memory.ReadAsync<ContextMenuEntry>(Scope));
        Assert.False(Directory.Exists(_root));
        var writer = Cache();
        await writer.MergeAsync(Scope, new[] { Entry() }, true, writer.Version);
        var path = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":999"));
        Assert.Null(await Cache().ReadAsync<ContextMenuEntry>(Scope));
        await File.WriteAllTextAsync(path, "{truncated");
        Assert.Null(await Cache().ReadAsync<ContextMenuEntry>(Scope));
    }

    [Fact]
    public async Task PartialDiscoveryDoesNotRemoveRowsOrReplaceLastCompleteDiskSnapshot()
    {
        var cache = Cache();
        await cache.MergeAsync(Scope, new[] { Entry("one"), Entry("two") }, true, cache.Version);
        var partial = await cache.MergeAsync(Scope, new[] { Entry("one", false) }, false, cache.Version);
        Assert.False(partial.IsComplete);
        Assert.Equal(2, partial.Count);
        Assert.False(partial.Single(item => item.Id == "one").IsEnabled);
        var persisted = await Cache().ReadAsync<ContextMenuEntry>(Scope);
        Assert.True(persisted!.Single(item => item.Id == "one").IsEnabled);
        Assert.Equal(2, persisted!.Count);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("ITEM")]
    public async Task OlderScanAndAlreadyReturnedSnapshotCannotOverwriteSuccessfulOperationOrRemoval(string mutationId)
    {
        var cache = Cache();
        await cache.MergeAsync(Scope, new[] { Entry() }, true, cache.Version);
        var scanVersion = cache.Version;
        var alreadyReturned = await cache.MergeAsync(Scope, new[] { Entry() }, true, scanVersion);
        cache.BeginMutation<ContextMenuEntry>(mutationId);
        cache.ApplyResult(mutationId, Entry(enabled: false));
        var stale = await cache.MergeAsync(Scope, new[] { Entry() }, true, scanVersion);
        Assert.False(Assert.Single(stale).IsEnabled);
        Assert.False(Assert.Single(alreadyReturned.CaptureCurrent()).IsEnabled);
        cache.BeginMutation<ContextMenuEntry>(mutationId);
        cache.ApplyResult<ContextMenuEntry>(mutationId, null, removed: true);
        Assert.Empty(await cache.MergeAsync(Scope, new[] { Entry() }, true, scanVersion));
        Assert.Empty(alreadyReturned.CaptureCurrent());
        await cache.FlushAsync();
        Assert.Empty((await Cache().ReadAsync<ContextMenuEntry>(Scope))!);
        // A new complete scan may legitimately discover a registration recreated later.
        Assert.Single(await cache.MergeAsync(Scope, new[] { Entry() }, true, cache.Version));
    }

    [Fact]
    public async Task PendingMutationRetainsKnownStateWithoutPersistingAnIntermediateScan()
    {
        var cache = Cache();
        await cache.MergeAsync(Scope, new[] { Entry() }, true, cache.Version);
        var version = cache.Version;
        cache.BeginMutation<ContextMenuEntry>("item");
        var during = await cache.MergeAsync(Scope, new[] { Entry(enabled: false) }, true, version);
        Assert.True(Assert.Single(during).IsEnabled);
        Assert.True(Assert.Single((await Cache().ReadAsync<ContextMenuEntry>(Scope))!).IsEnabled);
        cache.ApplyResult("item", Entry(enabled: false));
        await cache.FlushAsync();
        Assert.False(Assert.Single((await Cache().ReadAsync<ContextMenuEntry>(Scope))!).IsEnabled);
    }

    [Fact]
    public async Task ReadOnlyVerificationCannotFinishOrOverwriteANewerPendingMutation()
    {
        var cache = Cache();
        await cache.MergeAsync(Scope, new[] { Entry() }, true, cache.Version);
        var version = cache.Version;
        cache.BeginMutation<ContextMenuEntry>("item");
        Assert.False(cache.ApplyResult("item", Entry(), completesMutation: false, observedVersion: version));
        Assert.False(cache.ApplyResult<ContextMenuEntry>("item", null, removed: true, completesMutation: false, observedVersion: version));
        var during = await cache.MergeAsync(Scope, Array.Empty<ContextMenuEntry>(), true, version);
        Assert.Single(during);
        cache.ApplyResult("item", Entry(enabled: false));
        Assert.False(Assert.Single(during.CaptureCurrent()).IsEnabled);
    }

    [Fact]
    public async Task ResetInvalidatesInFlightScansAndAlreadyReturnedSnapshots()
    {
        var cache = Cache();
        var generation = cache.Generation;
        var snapshot = await cache.MergeAsync(Scope, new[] { Entry() }, true, cache.Version);
        await cache.InvalidateAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.MergeAsync(Scope, new[] { Entry() }, true, 0, generation: generation));
        Assert.ThrowsAny<OperationCanceledException>(() => snapshot.CaptureCurrent());
        Assert.Null(await Cache().ReadAsync<ContextMenuEntry>(Scope));
    }

    [Fact]
    public void ProjectionDiffPreservesObjectsAndNeverResetsUnchangedContainers()
    {
        var first = new object(); var second = new object(); var third = new object();
        var rows = new ObservableCollection<object> { first, second };
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);
        IncrementalCollection.Apply(rows, new[] { first, second });
        Assert.Empty(events);
        IncrementalCollection.Apply(rows, new[] { second, first, third });
        Assert.Equal(new[] { NotifyCollectionChangedAction.Move, NotifyCollectionChangedAction.Add }, events);
        Assert.Same(first, rows[1]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
    }

    [Fact]
    public async Task CachedPartialPackageDiscoveryCannotBecomeCompleteOrInvalidateClassicCoverage()
    {
        var scans = 0;
        var cache = new PackagedContextMenuScanCache(_ =>
        {
            Interlocked.Increment(ref scans);
            MenuDiscoveryCoverage.MarkPackagedIncomplete();
            return [];
        });
        using (var first = MenuDiscoveryCoverage.Begin())
        {
            Assert.Empty(await cache.GetAsync("user", default));
            Assert.False(first.Complete);
            Assert.True(first.ClassicComplete);
        }
        using (var second = MenuDiscoveryCoverage.Begin())
        {
            Assert.Empty(await cache.GetAsync("user", default));
            Assert.False(second.Complete);
            Assert.True(second.ClassicComplete);
        }
        Assert.Equal(1, scans);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var cache in _caches) await cache.FlushAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
