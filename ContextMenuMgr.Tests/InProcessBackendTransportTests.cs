#if CONTEXTMENUMGR_IN_PROCESS
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class InProcessBackendTransportTests : IDisposable
{
    private const string FixtureSid = "S-1-5-21-1-2-3-1001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Local-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CapabilityPolicy_MatchesTheReviewedPipeCommandInventory()
    {
        var expectedHost = new[] { PipeCommand.Ping, PipeCommand.SubscribeNotifications, PipeCommand.SetLogLevel,
            PipeCommand.RestartExplorer, PipeCommand.RepairRuntimeDataAcl };
        var expectedShared = new[]
        {
            PipeCommand.GetSnapshot, PipeCommand.GetSceneSnapshot, PipeCommand.SetEnhanceMenuItemEnabled,
            PipeCommand.SetDetailedEditRuleValue, PipeCommand.AcknowledgeItemState, PipeCommand.SetEnabled,
            PipeCommand.SetShellAttribute, PipeCommand.SetDisplayText, PipeCommand.SetCommandText,
            PipeCommand.GetShellSubMenuItems, PipeCommand.SetShellSubMenuItemEnabled, PipeCommand.DeleteItem,
            PipeCommand.UndoDelete, PipeCommand.PurgeDeletedItem, PipeCommand.ResetStateDatabase,
            PipeCommand.GetSpecialMenuSnapshot, PipeCommand.SetSpecialMenuItemEnabled, PipeCommand.CreateSpecialMenuItem,
            PipeCommand.UpdateSpecialMenuItem, PipeCommand.DeleteSpecialMenuItem, PipeCommand.UndoSpecialMenuItem,
            PipeCommand.PurgeSpecialMenuItem, PipeCommand.MoveSpecialMenuItem, PipeCommand.RestoreSpecialMenuDefaults,
            PipeCommand.SetShellNewOrderLock, PipeCommand.SetWin11BlockedItem, PipeCommand.RemoveWin11BlockedItem,
            PipeCommand.GetWin11BlockedItems, PipeCommand.GetWin11ContextMenuSnapshot,
            PipeCommand.GetWin11ModernContextMenuDisabled, PipeCommand.SetWin11ModernContextMenuDisabled,
            PipeCommand.AnalyzeFileTypeContext, PipeCommand.CreateSceneMenuItem, PipeCommand.FindRelatedFileTypeMenuItems,
            PipeCommand.GetOfficeSuiteCoexistenceStatus, PipeCommand.GetContextMenuItemState,
            PipeCommand.InspectOpenWithSources, PipeCommand.GetShellNewOrderLock, PipeCommand.SetWin11ApplicationEnabled,
            PipeCommand.GetSpecialMenuItemState, PipeCommand.DeleteFileTypeBatchItem, PipeCommand.GetFileTypeBatchItemState,
            PipeCommand.PurgeDeletedItems, PipeCommand.SetDocumentIconRepair
        };
        var expectedUnavailable = new[]
        {
            PipeCommand.EnsureTrayHost, PipeCommand.SubscribeTrayHost, PipeCommand.GetRegistryProtectionSetting,
            PipeCommand.SetRegistryProtectionSetting, PipeCommand.ApplyDecision, PipeCommand.SetAutoStartEnabled,
            PipeCommand.GetAutoStartEnabled, PipeCommand.SetTrayIconPolicy, PipeCommand.GetWpsOfficePendingApprovals,
            PipeCommand.SetDocumentIconProvider, PipeCommand.GetEnhanceMenuDefinitions, PipeCommand.GetEnhanceMenuState,
            PipeCommand.RequestShutdown, PipeCommand.GetOpenWithPendingReviews, PipeCommand.ReviewOpenWithRegistration,
            PipeCommand.DecideOpenWithReview, PipeCommand.RequestPortableExit
        };

        Assert.Equal(66, Enum.GetValues<PipeCommand>().Length);
        Assert.Equal(expectedHost.Length, Enum.GetValues<PipeCommand>().Count(command =>
            InProcessBackendCapabilities.Route(command) == InProcessBackendRoute.Host));
        Assert.Equal(expectedShared.Length, Enum.GetValues<PipeCommand>().Count(command =>
            InProcessBackendCapabilities.Route(command) == InProcessBackendRoute.Shared));
        Assert.Equal(expectedUnavailable.Length, Enum.GetValues<PipeCommand>().Count(command =>
            InProcessBackendCapabilities.Route(command) == InProcessBackendRoute.Unavailable));
        Assert.All(expectedHost, command => Assert.Equal(InProcessBackendRoute.Host, InProcessBackendCapabilities.Route(command)));
        Assert.All(expectedShared, command => Assert.Equal(InProcessBackendRoute.Shared, InProcessBackendCapabilities.Route(command)));
        Assert.All(expectedUnavailable, command => Assert.Equal(InProcessBackendRoute.Unavailable, InProcessBackendCapabilities.Route(command)));
    }

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
        var subscription = new List<BackendNotification>();
        transport.NotificationReceived += subscription.Add;
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
        Assert.Equal(notification.ClientOperationId, Assert.Single(subscription).ClientOperationId);

        await transport.SendAsync(query with { Command = PipeCommand.GetSpecialMenuItemState, SpecialItem = item }, listeners, default);
        Assert.Single(notifications);
        Assert.Single(subscription);
    }

    [Theory]
    [InlineData("cancel-before-commit")]
    [InlineData("cancel-during-publication")]
    [InlineData("success")]
    [InlineData("failure")]
    public async Task ClientAppliesCommittedSendToMutationOnce(string outcome)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource<BackendNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var eventCount = 0;
        var operationId = Guid.NewGuid();
        var logger = MakeLogger();
        var user = new BackendUserContext(FixtureSid, "Fixture", _root, _root, _root, 1);
        var core = new BackendCore(logger, new ContextMenuStateStore(Path.Combine(_root, "state.json")),
            new RegistryBackupService(Path.Combine(_root, "DeletedBackups")),
            new BackendProtectionSettingsStore(Path.Combine(_root, "protection.json")),
            (BackendUserContext _, FileLogger _, out IFrontendClassesRoot? classes, out string? failure) =>
            {
                classes = null;
                failure = "Fixture has no optional SendTo icons.";
                return false;
            }, () => new[] { user });
        var path = Path.Combine(Directory.CreateDirectory(user.GetSendToPath()).FullName, "fixture.txt");
        await File.WriteAllTextAsync(path, "fixture", budget.Token);
        await using var cache = new MenuListCacheService(Path.Combine(_root, "Cache"), null);
        await using var transport = new InProcessBackendTransport(async (request, _, token) =>
        {
            if (request.Command == PipeCommand.Ping) return new(new PipeResponse { Success = true });
            if (request.Command == PipeCommand.SetSpecialMenuItemEnabled)
            {
                Assert.Equal(operationId, request.ClientOperationId);
                Interlocked.Increment(ref executions);
                entered.TrySetResult();
                await commit.Task.WaitAsync(budget.Token);
                if (outcome == "failure") File.Delete(path);
            }
            return await core.Dispatcher.DispatchAsync(request, new BackendRequestContext(() => user, logger), token);
        }, () => FixtureSid, logger);
        await using var client = new BackendClient(transport, cache, new MenuListActivityService(),
            new RecentClientOperationCache(), () => FixtureSid);
        client.NotificationReceived += (_, _) => throw new InvalidOperationException("Subscriber fixture");
        client.NotificationReceived += (_, notification) =>
        {
            Interlocked.Increment(ref eventCount);
            delivered.TrySetResult(notification);
        };
        transport.NotificationReceived += notification =>
        {
            // Runs after the actual client has seen the event, before the transport can return its response.
            if (notification.ClientOperationId == operationId && outcome == "cancel-during-publication")
                caller.Cancel();
        };
        await client.ConnectAsync(budget.Token);
        var item = Assert.Single(await client.GetSpecialMenuSnapshotAsync(SpecialMenuKind.SendTo, budget.Token));
        Assert.Equal(path, item.Path);

        var pending = client.SetSpecialMenuItemEnabledAsync(item, false, operationId, caller.Token);
        try
        {
            await entered.Task.WaitAsync(budget.Token);
            if (outcome == "cancel-before-commit")
            {
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.True(cache.FindItem<SpecialMenuEntry>(item.Id)!.IsEnabled);
            }
            commit.SetResult();

            switch (outcome)
            {
                case "success":
                    Assert.False((await pending)!.IsEnabled);
                    Assert.Equal(0, eventCount);
                    break;
                case "failure":
                    await Assert.ThrowsAsync<BackendRequestException>(() => pending);
                    Assert.Equal(0, eventCount);
                    break;
                default:
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                    var notification = await delivered.Task.WaitAsync(budget.Token);
                    Assert.Equal(operationId, notification.ClientOperationId);
                    Assert.False(notification.SpecialItem!.IsEnabled);
                    Assert.Equal(1, eventCount);
                    break;
            }

            if (outcome != "failure")
            {
                Assert.False(cache.FindItem<SpecialMenuEntry>(item.Id)!.IsEnabled);
                Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
            }
            Assert.Equal(1, executions);
        }
        finally { commit.TrySetResult(); }
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

    [Fact]
    public async Task OpenWithNotifications_AreScopedToVerifiedInteractiveUserRatherThanElevatedProcessSid()
    {
        await using var cache = new MenuListCacheService(Path.Combine(_root, "Cache"), null);
        await using var transport = new InProcessBackendTransport((_, _, _) =>
            Task.FromResult(new BackendDispatchResult(new PipeResponse { Success = true })),
            () => FixtureSid, MakeLogger());
        await using var client = new BackendClient(transport, cache, new MenuListActivityService(),
            new RecentClientOperationCache(), () => FixtureSid);
        var delivered = new List<BackendNotification>();
        client.NotificationReceived += (_, notification) => delivered.Add(notification);

        transport.StartNotifications();
        transport.PublishNotification(new BackendNotification
        {
            SpecialKind = SpecialMenuKind.OpenWith,
            UserSid = FixtureSid
        });
        transport.PublishNotification(new BackendNotification
        {
            SpecialKind = SpecialMenuKind.OpenWith,
            UserSid = "S-1-5-21-9-8-7-500"
        });

        Assert.Single(delivered);
        Assert.Equal(FixtureSid, delivered[0].UserSid);
    }

    [Fact]
    public async Task LateBulkPurgeRemovesOnlyMatchingRecoveryRecordsForTheVerifiedUser()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var deletedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var item = new ContextMenuEntry { Id = "deleted", IsDeleted = true, DeletedAtUtc = deletedAt };
        var replaced = item with { Id = "replaced", DeletedAtUtc = deletedAt.AddDays(1) };
        var references = new[] { new DeletedItemReference(item.Id, deletedAt), new DeletedItemReference(replaced.Id, deletedAt) };
        var purged = new DeletedItemsPurgeResult { PurgedItems = references };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<BackendNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new MenuListCacheService(Path.Combine(_root, "Cache"), null);
        await cache.MergeAsync("fixture", new[] { item, replaced }, true, cache.Version, budget.Token);
        await using var transport = new InProcessBackendTransport(async (request, _, _) =>
        {
            if (request.Command == PipeCommand.Ping) return new(new PipeResponse { Success = true });
            entered.SetResult();
            await commit.Task.WaitAsync(budget.Token);
            return new(new PipeResponse { Success = true, DeletedItemsPurge = purged });
        }, () => FixtureSid, MakeLogger());
        await using var client = new BackendClient(transport, cache, new MenuListActivityService(),
            new RecentClientOperationCache(), () => FixtureSid);
        await client.ConnectAsync(budget.Token);
        var pending = client.PurgeDeletedItemsAsync(new DeletedItemsPurgeRequest { Items = references }, caller.Token);
        try
        {
            await entered.Task.WaitAsync(budget.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            transport.PublishNotification(new BackendNotification { Kind = PipeNotificationKind.DeletedItemsPurged,
                DeletedItemsPurge = purged, UserSid = "S-1-5-21-9-8-7-500" });
            Assert.NotNull(cache.FindItem<ContextMenuEntry>(item.Id));
            client.ItemsRemoved += (_, _) => throw new InvalidOperationException("Subscriber fixture");
            client.NotificationReceived += (_, notification) => observed.TrySetResult(notification);
            commit.SetResult();
            var notification = await observed.Task.WaitAsync(budget.Token);
            Assert.Equal(FixtureSid, notification.UserSid);
            Assert.NotNull(notification.ClientOperationId);
            Assert.Null(cache.FindItem<ContextMenuEntry>(item.Id));
            Assert.Equal(deletedAt.AddDays(1), cache.FindItem<ContextMenuEntry>(replaced.Id)!.DeletedAtUtc);
        }
        finally { commit.TrySetResult(); }
    }

    [Fact]
    public async Task FileTypeAnalysis_OpensClassesViewForTheResolvedRequestUser()
    {
        var caller = new BackendUserContext(FixtureSid, "Fixture", _root, _root, _root, 1);
        string? classesRootSid = null;
        bool TryOpenClasses(BackendUserContext user, FileLogger _, out IFrontendClassesRoot? root, out string? failure)
        {
            classesRootSid = user.Sid;
            root = new TestClassesRoot(RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default));
            failure = null;
            return true;
        }

        var logger = MakeLogger();
        var core = new BackendCore(logger, new ContextMenuStateStore(Path.Combine(_root, "state.json")),
            new RegistryBackupService(Path.Combine(_root, "DeletedBackups")),
            new BackendProtectionSettingsStore(Path.Combine(_root, "protection.json")), TryOpenClasses, () => [caller]);
        await using var transport = new InProcessBackendTransport((request, _, token) =>
            core.Dispatcher.DispatchAsync(request, new BackendRequestContext(() => caller, logger), token),
            () => caller.Sid, logger);
        var sample = Path.Combine(_root, "sample.g2fixture");
        await File.WriteAllTextAsync(sample, "fixture");

        var response = await transport.SendAsync(new PipeRequest
        {
            Command = PipeCommand.AnalyzeFileTypeContext,
            FileTypeAnalysis = new FileTypeAnalysisRequest(sample)
        }, _ => { }, default);

        Assert.True(response.Success, response.Message);
        Assert.Equal(FixtureSid, classesRootSid);
        Assert.Contains(response.FileTypeAnalysisResults!, result => result.ScopeValue == ".g2fixture");
    }

    private FileLogger MakeLogger() => new(Path.Combine(_root, "backend.log"));

    private sealed class TestClassesRoot(RegistryKey classesRoot) : IFrontendClassesRoot
    {
        public RegistryKey ClassesRoot { get; } = classesRoot;
        public void Dispose() => ClassesRoot.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
#endif
