#if CONTEXTMENUMGR_IN_PROCESS
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class InProcessBackendTransportTests : IDisposable
{
    private const string FixtureSid = "S-1-5-21-1-2-3-1001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Local-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ServiceAndObsoleteCommands_AreRejectedBeforeDispatch()
    {
        var routed = new List<PipeCommand>();
        await using var transport = new InProcessBackendTransport((request, _, _) =>
        {
            routed.Add(request.Command);
            return Task.FromResult(new BackendDispatchResult(new PipeResponse { Success = true }));
        }, () => FixtureSid, MakeLogger());
        foreach (var command in new[]
        {
            PipeCommand.EnsureTrayHost, PipeCommand.SubscribeTrayHost, PipeCommand.GetRegistryProtectionSetting,
            PipeCommand.SetRegistryProtectionSetting, PipeCommand.ApplyDecision, PipeCommand.RequestShutdown,
            PipeCommand.RequestPortableExit, PipeCommand.GetAutoStartEnabled, PipeCommand.SetAutoStartEnabled,
            PipeCommand.SetTrayIconPolicy, PipeCommand.GetWpsOfficePendingApprovals, PipeCommand.ReviewOpenWithRegistration,
            PipeCommand.GetOpenWithPendingReviews, PipeCommand.DecideOpenWithReview, PipeCommand.SetDocumentIconProvider,
            PipeCommand.GetEnhanceMenuDefinitions, PipeCommand.GetEnhanceMenuState, (PipeCommand)int.MaxValue
        })
        {
            var operationId = Guid.NewGuid();
            var response = await transport.SendAsync(new PipeRequest { Command = command, ClientOperationId = operationId }, _ => { }, default);
            Assert.False(response.Success);
            Assert.Equal(PipeErrorCodes.BackendCapabilityUnavailable, response.ErrorCode);
            Assert.Equal(operationId, response.ClientOperationId);
        }

        Assert.Empty(routed);
        Assert.Equal(InProcessBackendRoute.Shared, InProcessBackendCapabilities.Route(PipeCommand.SetShellNewOrderLock));
        Assert.Equal(InProcessBackendRoute.Shared, InProcessBackendCapabilities.Route(PipeCommand.SetWin11BlockedItem));
        Assert.Equal(InProcessBackendRoute.Shared, InProcessBackendCapabilities.Route(PipeCommand.SetDocumentIconRepair));
    }

    [Fact]
    public async Task SendTo_UsesSharedDispatcherAndPublishesOneCorrelatedMutationDespiteFailingSubscriber()
    {
        var logger = MakeLogger();
        var caller = new BackendUserContext(FixtureSid, "Fixture", _root, _root, _root, 1);
        var stateStore = new ContextMenuStateStore(Path.Combine(_root, "state.json"));
        var core = new BackendCore(logger, stateStore, new RegistryBackupService(Path.Combine(_root, "DeletedBackups")),
            new BackendProtectionSettingsStore(Path.Combine(_root, "protection.json")),
            (BackendUserContext user, FileLogger log, out IFrontendClassesRoot? root, out string? failure) =>
            {
                throw new InvalidOperationException("SendTo does not open the merged Classes view.");
            }, () => [caller]);
        await using var transport = new InProcessBackendTransport((request, _, token) =>
            core.Dispatcher.DispatchAsync(request, new BackendRequestContext(() => caller, logger), token), () => caller.Sid, logger);

        var folder = Directory.CreateDirectory(caller.GetSendToPath());
        var path = Path.Combine(folder.FullName, "fixture.txt");
        await File.WriteAllTextAsync(path, "fixture");
        var notifications = new List<BackendNotification>();
        Action<BackendNotification> listeners = _ => throw new InvalidOperationException("Subscriber fixture");
        listeners += notifications.Add;
        transport.NotificationReceived += notifications.Add;
        transport.StartNotifications();

        var query = new PipeRequest { Command = PipeCommand.GetSpecialMenuSnapshot, SpecialKind = SpecialMenuKind.SendTo };
        var snapshot = await transport.SendAsync(query, listeners, default);
        var item = Assert.Single(snapshot.SpecialItems);
        Assert.Equal(path, item.Path);

        var update = new PipeRequest { Command = PipeCommand.SetSpecialMenuItemEnabled, SpecialItem = item,
            Enable = false, ClientOperationId = Guid.NewGuid() };
        var response = await transport.SendAsync(update, listeners, default);
        Assert.True(response.Success, response.Message);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.Equal(update.ClientOperationId, response.ClientOperationId);
        var notification = Assert.Single(notifications);
        Assert.Equal(response.ClientOperationId, notification.ClientOperationId);
        Assert.False(notification.SpecialItem!.IsEnabled);

        await transport.SendAsync(query with { Command = PipeCommand.GetSpecialMenuItemState, SpecialItem = item }, listeners, default);
        Assert.Single(notifications);
    }

    [Fact]
    public async Task CanceledWait_IndependentRequestCompletes_AndShutdownDrainsTheAdmittedMutation()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var callerWait = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<BackendNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var transport = new InProcessBackendTransport(async (request, _, executionToken) =>
        {
            if (request.Command == PipeCommand.Ping) return new(new PipeResponse { Success = true });
            entered.SetResult();
            await allowCommit.Task.WaitAsync(budget.Token);
            executionToken.ThrowIfCancellationRequested();
            return new(new PipeResponse { Success = true, Item = new ContextMenuEntry { Id = "fixture" } });
        }, () => FixtureSid, MakeLogger());
        var mutation = new PipeRequest { Command = PipeCommand.SetEnabled, ItemId = "fixture", Enable = true,
            ClientOperationId = Guid.NewGuid() };
        var wait = transport.SendAsync(mutation, value => observed.SetResult(value), callerWait.Token);
        try
        {
            await entered.Task.WaitAsync(budget.Token);
            callerWait.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

            var ping = await transport.SendAsync(new PipeRequest { Command = PipeCommand.Ping }, _ => { }, budget.Token);
            Assert.True(ping.Success);
            var drain = transport.DisposeAsync().AsTask();
            Assert.False(drain.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                transport.SendAsync(new PipeRequest { Command = PipeCommand.Ping }, _ => { }, default));

            allowCommit.SetResult();
            await drain.WaitAsync(budget.Token);
            var notification = await observed.Task.WaitAsync(budget.Token);
            Assert.Equal(mutation.ClientOperationId, notification.ClientOperationId);
            Assert.Equal("fixture", notification.Item!.Id);
        }
        finally { allowCommit.TrySetResult(); }
    }

    [Fact]
    public async Task MutableRequestAndResponseCollections_CannotAliasCachedItems()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var metadata = new Dictionary<string, string> { ["fixture"] = "cached" };
        var backendMetadata = new Dictionary<string, string> { ["fixture"] = "committed" };
        var item = new SpecialMenuEntry { Kind = SpecialMenuKind.OpenWith, Id = "fixture", Metadata = metadata };
        await using var cache = new MenuListCacheService(Path.Combine(_root, "Cache"), null);
        await cache.MergeAsync("fixture", new[] { item }, true, cache.Version, budget.Token);
        var allowRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var transport = new InProcessBackendTransport(async (request, _, _) =>
        {
            await allowRead.Task.WaitAsync(budget.Token);
            var incoming = request.SpecialItem!.Metadata;
            Assert.Equal("cached", incoming["fixture"]);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)incoming)["fixture"] = "changed");
            return new(new PipeResponse { Success = true, SpecialItem = request.SpecialItem with { Metadata = backendMetadata } });
        }, () => FixtureSid, MakeLogger());

        var cached = cache.FindItem<SpecialMenuEntry>(item.Id);
        var wait = transport.SendAsync(new PipeRequest { Command = PipeCommand.SetSpecialMenuItemEnabled, SpecialItem = cached }, _ => { }, budget.Token);
        metadata["fixture"] = "frontend edit";
        allowRead.SetResult();
        var response = await wait;
        Assert.True(response.Success, response.Message);

        backendMetadata["fixture"] = "backend edit";
        Assert.Equal("committed", response.SpecialItem!.Metadata["fixture"]);
        Assert.Equal("frontend edit", cache.FindItem<SpecialMenuEntry>(item.Id)!.Metadata["fixture"]);
    }

    private FileLogger MakeLogger() => new(Path.Combine(_root, "backend.log"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
#endif
