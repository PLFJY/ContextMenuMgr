using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class MutationOutcomeTests
{
    // Permanent: authoritative queue refresh/decision ordering, uncertain outcomes and SID filtering.
    [Fact]
    public async Task OpenWithQueue_RefreshRacesTimeoutsAndLateSignalsCannotLoseOrResurrectReviews()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
                try
                {
                    var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
                    var client = DispatchProxy.Create<IBackendClient, ReviewBackend>();
                    var backend = (ReviewBackend)client;
                    var settings = new FrontendSettingsService(null, Path.Combine(directory, "settings.json"));
                    var localization = new LocalizationService(settings);
                    var icons = new IconPreviewService();
                    await using var workspace = new ContextMenuWorkspaceService(client, null!, null!, null!, null!, null!,
                        icons, localization, settings, null!, null!);
                    var queue = workspace.OpenWithReviews;
                    using var regular = new ContextMenuItemViewModel(new ContextMenuEntry { Id = "classic|review",
                        DisplayName = "Regular", IsPendingApproval = true }, localization, icons, null!);
                    using var wps = new ContextMenuItemViewModel(new ContextMenuEntry { Id = "special:wps-office-association:test",
                        DisplayName = "WPS", IsPendingApproval = true }, localization, icons, null!);
                    workspace.Items.Add(regular);
                    workspace.WpsOfficeApprovalItems.Add(wps);
                    using var win11 = new Windows11ContextMenuService(client, localization);
                    using var search = new ContextMenuGlobalSearchService(workspace, win11, localization, icons, settings);
                    using var shell = new ShellViewModel(workspace, localization, win11, search,
                        new GlobalSearchNavigationFilterService(), null!, new ExplorerRestartStateService(), client);
                    OpenWithPendingReview Review(string name, OpenWithReviewStatus status, string version = "observed")
                    {
                        var path = $@"HKEY_USERS\{sid}\Software\Classes\Applications\{name}.exe";
                        return new(sid, path, version, status, new SpecialMenuEntry { Id = path,
                            DisplayName = name, Kind = SpecialMenuKind.OpenWith, IsEnabled = false, CommandText = "test.exe %1" },
                            status == OpenWithReviewStatus.New, status == OpenWithReviewStatus.New, null, false, false, status == OpenWithReviewStatus.New);
                    }
                    var pending = Review("new", OpenWithReviewStatus.New);
                    var conflict = Review("attention", OpenWithReviewStatus.Conflict);
                    var navigation = new FrontendNavigationState();
                    navigation.RequestApprovals(pending.RegistryPath);
                    using var page = new ApprovalsPageViewModel(workspace, localization, navigation);
                    backend.Snapshot = new(sid, [pending, conflict, Review("information", OpenWithReviewStatus.Reapplied), Review("missing", OpenWithReviewStatus.Missing)]);
                    Assert.True(await queue.RefreshAsync(default));
                    Assert.Equal(2, queue.Items.Count);
                    var row = Assert.Single(queue.Items, i => i.Id == pending.RegistryPath);
                    Assert.Equal(4, page.Items.Count);
                    Assert.Equal("4", shell.ApprovalsBadge.Value);
                    Assert.Same(row, page.SelectedItem);
                    page.SearchText = pending.RegistryPath;
                    Assert.Same(row, Assert.Single(page.ItemsView.Cast<IApprovalQueueItem>()));
                    page.SearchText = "test.exe";
                    Assert.Equal(2, page.ItemsView.Cast<IApprovalQueueItem>().Count());
                    page.SearchText = string.Empty;
                    Assert.True(row.CanReject);
                    backend.QueryFails = true;
                    Assert.False(await queue.RefreshAsync(default));
                    Assert.Same(row, Assert.Single(queue.Items, i => i.Id == pending.RegistryPath));
                    Assert.False(row.CanReject);
                    Assert.Equal(2, queue.Items.Count);
                    Assert.Equal("4", shell.ApprovalsBadge.Value);
                    backend.QueryFails = false;
                    await queue.RefreshAsync(default);
                    backend.Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var decision = queue.DecideAsync(row, OpenWithReviewDecision.Reject, default);
                    Assert.True(row.IsBusy);
                    await queue.DecideAsync(row, OpenWithReviewDecision.Reject, default);
                    Assert.Equal(1, backend.Decisions);
                    var queuedRefresh = queue.RefreshAsync(default);
                    backend.Snapshot = new(sid, [conflict]);
                    backend.Decision.SetResult(backend.Snapshot);
                    await decision;
                    await queuedRefresh;
                    Assert.Single(queue.Items);
                    Assert.Equal("3", shell.ApprovalsBadge.Value);
                    Assert.Equal(3, page.Items.Count);
                    Assert.True(regular.IsPendingApproval);
                    Assert.True(wps.IsPendingApproval);
                    var queries = backend.Queries;
                    Assert.False(queue.Invalidate(new BackendNotification { SpecialKind = SpecialMenuKind.OpenWith,
                        UserSid = "S-1-5-21-1-2-3-1001", SpecialItem = pending.Registration }, default));
                    Assert.Equal(queries, backend.Queries);
                    Assert.True(queue.Invalidate(new BackendNotification { Kind = PipeNotificationKind.OpenWithChanged,
                        SpecialKind = SpecialMenuKind.OpenWith, UserSid = sid, SpecialItem = pending.Registration,
                        OpenWithPendingCount = 99, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10) }, default));
                    await queue.RefreshAsync(default);
                    Assert.Single(queue.Items); // payload, count and timestamp cannot resurrect resolved state
                    backend.Snapshot = new(sid, [pending with { Generation = "second generation" }, conflict]);
                    await queue.RefreshAsync(default);
                    row = Assert.Single(queue.Items, i => i.Id == pending.RegistryPath);
                    backend.Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    backend.Decision.SetException(new TimeoutException("Response lost after submission."));
                    backend.QueryFails = true;
                    await queue.DecideAsync(row, OpenWithReviewDecision.Accept, default);
                    Assert.True(row.IsStateUncertain);
                    Assert.False(row.CanAccept);
                    Assert.Equal(2, queue.Items.Count);
                    await queue.DecideAsync(row, OpenWithReviewDecision.Accept, default);
                    Assert.Equal(2, backend.Decisions);
                    backend.QueryFails = false;
                    await queue.RefreshAsync(default);
                    Assert.False(row.IsStateUncertain);
                    Assert.True(row.CanAccept);
                    backend.Query = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var olderRead = queue.RefreshAsync(default);
                    var waitingDecision = queue.DecideAsync(row, OpenWithReviewDecision.Accept, default);
                    backend.Query.SetResult(new(sid, [pending with { Generation = "third generation" }, conflict]));
                    await olderRead;
                    await waitingDecision;
                    Assert.Equal(2, backend.Decisions); // stale click queued behind refresh never writes
                    Assert.Same(row, Assert.Single(queue.Items, i => i.Id == pending.RegistryPath));
                    backend.Query = null;
                    var isolationFailure = Review("isolation failed", OpenWithReviewStatus.IsolationFailed);
                    backend.Snapshot = new(sid, [pending, conflict, isolationFailure]);
                    await queue.RefreshAsync(default);
                    var failedIsolation = Assert.Single(queue.Items, i => i.Id == isolationFailure.RegistryPath);
                    Assert.False(failedIsolation.CanAccept);
                    Assert.False(failedIsolation.CanReject);
                    Assert.Equal("5", shell.ApprovalsBadge.Value);
                    backend.Snapshot = new("S-1-5-21-1-2-3-1001", []);
                    Assert.False(await queue.RefreshAsync(default));
                    Assert.Equal(3, queue.Items.Count); // cross-user response cannot clear this user's queue
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
                }
            });
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
    }

    public class ReviewBackend : DispatchProxy
    {
        public OpenWithReviewSnapshot Snapshot { get; set; } = null!;
        public bool QueryFails { get; set; }
        public int Queries { get; private set; }
        public int Decisions { get; private set; }
        public TaskCompletionSource<OpenWithReviewSnapshot>? Query { get; set; }
        public TaskCompletionSource<OpenWithReviewSnapshot>? Decision { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name.StartsWith("add_", StringComparison.Ordinal) == true
                || method?.Name.StartsWith("remove_", StringComparison.Ordinal) == true) return null;
            if (method?.Name == nameof(IBackendClient.DisposeAsync)) return ValueTask.CompletedTask;
            if (method?.Name == nameof(IBackendClient.ReadCachedListAsync))
                return Task.FromException<MenuListSnapshot<ContextMenuEntry>?>(new IOException("Secondary source unavailable."));
            if (method?.Name == nameof(IBackendClient.GetOpenWithPendingReviewsAsync))
            {
                Queries++;
                return QueryFails ? Task.FromException<OpenWithReviewSnapshot>(new IOException("Read failed."))
                    : Query?.Task ?? Task.FromResult(Snapshot);
            }
            if (method?.Name == nameof(IBackendClient.DecideOpenWithReviewAsync)) { Decisions++; return Decision!.Task; }
            throw new NotSupportedException(method?.Name);
        }
    }

    [Fact]
    public void ContextMenuRequestWithoutOperationId_GetsOneAndRegistersItBeforeSend()
    {
        var cache = new RecentClientOperationCache();
        var client = new NamedPipeBackendClient(cache);
        var request = new PipeRequest
        {
            Command = PipeCommand.SetEnabled,
            ItemId = @"*\shellex\ContextMenuHandlers|RegularHandler",
            Enable = false
        };

        var preparedRequest = client.PrepareRequestForSend(request);

        Assert.Null(request.ClientOperationId);
        Assert.NotNull(preparedRequest.ClientOperationId);
        Assert.NotEqual(Guid.Empty, preparedRequest.ClientOperationId);
        Assert.True(cache.Contains(preparedRequest.ClientOperationId));
        Assert.False(client.TryForwardSubscriptionNotification(new BackendNotification
        {
            Kind = PipeNotificationKind.ItemStateChanged,
            Item = new ContextMenuEntry { Id = request.ItemId },
            ClientOperationId = preparedRequest.ClientOperationId
        }));
    }

    [Fact]
    public void CallerProvidedOperationId_IsPreservedAndRegistered()
    {
        var operationId = Guid.NewGuid();
        var cache = new RecentClientOperationCache();
        var client = new NamedPipeBackendClient(cache);
        var request = new PipeRequest
        {
            Command = PipeCommand.SetEnabled,
            ItemId = @"*\shellex\ContextMenuHandlers|RegularHandler",
            Enable = false,
            ClientOperationId = operationId
        };

        var preparedRequest = client.PrepareRequestForSend(request);

        Assert.Same(request, preparedRequest);
        Assert.Equal(operationId, preparedRequest.ClientOperationId);
        Assert.True(cache.Contains(operationId));
    }

    [Fact]
    public void DifferentClientOperationNotification_IsForwarded()
    {
        var cache = new RecentClientOperationCache();
        var client = new NamedPipeBackendClient(cache);
        var received = false;
        client.NotificationReceived += (_, _) => received = true;
        cache.Register(Guid.NewGuid());

        Assert.True(client.TryForwardSubscriptionNotification(new BackendNotification { ClientOperationId = Guid.NewGuid() }));
        Assert.True(received);
    }

    [Fact]
    public void BackendOriginatedNotificationWithoutClientOperationId_IsForwarded()
    {
        var client = new NamedPipeBackendClient(new RecentClientOperationCache());
        var received = false;
        client.NotificationReceived += (_, _) => received = true;

        Assert.True(client.TryForwardSubscriptionNotification(new BackendNotification()));
        Assert.True(received);
    }

    [Fact]
    public void FailedOrCancelledOperationCanBeRemovedAndEntriesExpire()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new RecentClientOperationCache(() => now);
        var client = new NamedPipeBackendClient(cache);
        var failedRequest = client.PrepareRequestForSend(new PipeRequest
        {
            Command = PipeCommand.SetEnabled,
            ItemId = "failed-item",
            Enable = false
        });

        cache.Remove(failedRequest.ClientOperationId);
        Assert.False(cache.Contains(failedRequest.ClientOperationId));

        var operationId = Guid.NewGuid();
        cache.Register(operationId);
        cache.MarkCompleted(operationId);
        now = now.AddSeconds(11);
        Assert.False(cache.Contains(operationId));
    }

    [Fact]
    public void LongRunningLocalOperation_StaysSuppressedUntilTimeoutThenForwardsLateSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new RecentClientOperationCache(() => now);
        var client = new NamedPipeBackendClient(cache);
        var id = Guid.NewGuid();
        cache.Register(id);
        now = now.AddSeconds(46);
        Assert.False(client.TryForwardSubscriptionNotification(new BackendNotification { ClientOperationId = id }));
        cache.Remove(id);
        Assert.True(client.TryForwardSubscriptionNotification(new BackendNotification { ClientOperationId = id }));
    }

    [Fact]
    public async Task SlowBackgroundRequest_DoesNotDelayIndependentSetEnabledSend()
    {
        var pipeName = $"ContextMenuMgr.Test.{Guid.NewGuid():N}";
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        async Task ServeAsync(TaskCompletionSource received, Task? hold, ContextMenuEntry? item)
        {
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 2,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(server, leaveOpen: true);
            using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(timeout.Token);
            var request = JsonSerializer.Deserialize<PipeEnvelope>(line!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            received.SetResult();
            if (hold is not null) await hold.WaitAsync(timeout.Token);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeEnvelope
            {
                MessageType = PipeMessageType.Response,
                CorrelationId = request.CorrelationId,
                Response = new PipeResponse { Success = true, Item = item, Items = [] }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        var slowServer = ServeAsync(firstReceived, releaseFirst.Task, null);
        var fastServer = ServeAsync(secondReceived, null,
            new ContextMenuEntry { Id = "test|item", IsEnabled = false });
        var client = new NamedPipeBackendClient(new RecentClientOperationCache(), pipeName);
        var slow = client.GetWpsOfficePendingApprovalsAsync(timeout.Token);
        await firstReceived.Task.WaitAsync(timeout.Token);
        var fast = client.SetEnabledAsync("test|item", false, timeout.Token);
        await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(slow.IsCompleted);
        Assert.False((await fast)!.IsEnabled);
        releaseFirst.SetResult();
        await slow;
        await Task.WhenAll(slowServer, fastServer);
    }

    [Fact]
    public void RapidLocalOperationsRemainBounded()
    {
        var cache = new RecentClientOperationCache();

        for (var index = 0; index < 300; index++)
        {
            cache.Register(Guid.NewGuid());
        }

        Assert.Equal(256, cache.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateAuthoritativeSuccess_IsNotOverwrittenByOlderFailedTask(bool shellAttribute)
    {
        var settings = new FrontendSettingsService(null,
            Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid() + ".json"));
        var localization = new LocalizationService(settings);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new ContextMenuEntry
        {
            Id = @"*\shell|Test",
            EntryKind = ContextMenuEntryKind.ShellVerb,
            IsEnabled = true,
            IsPresentInRegistry = true
        };
        using var item = new ContextMenuItemViewModel(original, localization, null!, null!,
            (_, _) =>
            {
                requestStarted.TrySetResult();
                return finishRequest.Task;
            }, (_, _, _) =>
            {
                requestStarted.TrySetResult();
                return finishRequest.Task;
            });
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == (shellAttribute ? nameof(item.IsAttributesBusy) : nameof(item.IsToggleBusy))
                && !(shellAttribute ? item.IsAttributesBusy : item.IsToggleBusy))
                idle.TrySetResult();
        };

        if (shellAttribute) item.OnlyWithShift = true;
        else item.IsEnabled = false;
        Assert.True(shellAttribute ? item.IsAttributesBusy : item.IsToggleBusy);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        item.Update(shellAttribute ? original with { OnlyWithShift = true } : original with { IsEnabled = false });
        finishRequest.SetResult(false);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (shellAttribute) Assert.True(item.OnlyWithShift);
        else Assert.False(item.IsEnabled);
        Assert.False(item.IsToggleBusy || item.IsAttributesBusy);
    }

    [Fact]
    public async Task SuccessfulMutation_IsPublishedWhenDirectResponsePipeIsBroken()
    {
        var operationId = Guid.NewGuid();
        var cache = new RecentClientOperationCache();
        var client = new NamedPipeBackendClient(cache);
        var received = new List<BackendNotification>();
        client.NotificationReceived += (_, notification) => received.Add(notification);
        cache.Register(operationId);
        cache.Remove(operationId); // The initiating client stopped waiting.

        var error = await NamedPipeBackendServer.PublishAndDeliverResponseAsync(
            new PipeRequest { Command = PipeCommand.SetEnabled },
            new PipeResponse
            {
                Success = true,
                ClientOperationId = operationId,
                Item = new ContextMenuEntry { Id = "test|item", IsEnabled = false }
            },
            notification =>
            {
                client.TryForwardSubscriptionNotification(notification);
                return Task.CompletedTask;
            },
            () => throw new IOException("Pipe is broken."));

        Assert.NotNull(error);
        Assert.Single(received);
        Assert.Equal(operationId, received[0].ClientOperationId);
        Assert.False(received[0].Item!.IsEnabled);
    }

    [Fact]
    public async Task SpecialMenuFailedRequest_DoesNotRevertAuthoritativeReadBackOrAllowUncertainRetry()
    {
        var settings = new FrontendSettingsService(null,
            Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid() + ".json"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new SpecialMenuEntry { Id = "OpenWith:test", Kind = SpecialMenuKind.OpenWith, IsEnabled = true };
        var item = new SpecialMenuItemViewModel(original, null!, new LocalizationService(settings), (_, _) =>
        {
            started.TrySetResult();
            return finish.Task;
        });
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(item.IsBusy) && !item.IsBusy) idle.TrySetResult();
        };
        item.IsEnabled = false;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        item.IsStateUncertain = true;
        finish.SetResult(false);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(item.CanToggle);
        Assert.False(item.CanDelete);
        Assert.False(item.IsEnabled);
        item.Update(original with { IsEnabled = false });
        Assert.True(item.CanToggle);

        // A late authoritative refresh must also survive an older failing request.
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        item.IsEnabled = true;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        item.Update(original);
        finish.SetResult(false);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(item.IsEnabled);
    }

    // Permanent: read-back must not generate another invalidation and a cross-client query loop.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetedStateQueries_DoNotPublishMutationNotifications(bool special)
    {
        var published = 0;
        var delivered = false;
        await NamedPipeBackendServer.PublishAndDeliverResponseAsync(
            new PipeRequest { Command = special ? PipeCommand.GetSpecialMenuItemState : PipeCommand.GetContextMenuItemState },
            new PipeResponse { Success = true,
                Item = special ? null : new ContextMenuEntry { Id = "classic" },
                SpecialItem = special ? new SpecialMenuEntry { Id = "special", Kind = SpecialMenuKind.SendTo } : null },
            _ => { published++; return Task.CompletedTask; }, () => { delivered = true; return Task.CompletedTask; });
        Assert.True(delivered);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task FailedMutation_DoesNotPublishSuccessfulStateChange()
    {
        var published = 0;
        _ = await NamedPipeBackendServer.PublishAndDeliverResponseAsync(
            new PipeRequest { Command = PipeCommand.SetEnabled },
            new PipeResponse { Success = false, Item = new ContextMenuEntry { Id = "test|item" } },
            _ => { published++; return Task.CompletedTask; },
            () => Task.CompletedTask);
        Assert.Equal(0, published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResponseCorrelation_PreservesHandlerIdOrInheritsRequestId(bool handlerHasId)
    {
        var requestId = Guid.NewGuid();
        Guid? responseId = handlerHasId ? Guid.NewGuid() : null;
        var request = new PipeRequest { Command = PipeCommand.SetEnabled, ClientOperationId = requestId };
        var response = new PipeResponse { Success = true, ClientOperationId = responseId };

        var correlated = NamedPipeBackendServer.CorrelateResponseWithRequest(request, response);

        Assert.Equal(responseId ?? requestId, correlated.ClientOperationId);
        Assert.Equal(responseId, response.ClientOperationId);
        if (handlerHasId) Assert.Same(response, correlated);
    }

    private static readonly ContextMenuEntry Original = new()
    {
        Id = @"*\shellex\ContextMenuHandlers|Test",
        BackendRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\Test",
        EntryKind = ContextMenuEntryKind.ShellExtension,
        IsEnabled = true,
        IsPresentInRegistry = true
    };

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task TimedOutResponse_UsesTargetedAuthoritativeState(bool actualEnabled, int expected)
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        fake.State = Original with { IsEnabled = actualEnabled };
        var result = await SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));

        Assert.Equal((SetEnabledOutcome)expected, result.Outcome);
        Assert.Equal(actualEnabled, result.Item!.IsEnabled);
        Assert.Equal(1, fake.Mutations);
        Assert.Equal(1, fake.Queries);
    }

    [Fact]
    public async Task FailedVerification_LeavesOutcomeUncertainWithoutRetry()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        fake.QueryFails = true;
        var result = await SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        Assert.Equal(SetEnabledOutcome.Uncertain, result.Outcome);
        Assert.Null(result.Item);
        Assert.Equal(1, fake.Mutations);
        Assert.Equal(1, fake.Queries);
    }

    [Fact]
    public async Task ShutdownCancellation_DoesNotStartOutcomeVerification()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
                TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), shutdown.Token));
        Assert.Equal(0, fake.Queries);
    }

    [Fact]
    public async Task StructuredBackendFailure_IsNotTreatedAsTimeoutOrRetried()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        fake.MutationFailure = new BackendRequestException("Write protected.", "PROTECTED");
        await Assert.ThrowsAsync<BackendRequestException>(() =>
            SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
                TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)));
        Assert.Equal(1, fake.Mutations);
        Assert.Equal(0, fake.Queries);
    }

    [Fact]
    public async Task Win11LostResponse_UsesReadBack_AndRetainsTheFullDeclarationTarget()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        var id = $"win11|{Guid.NewGuid():B}|File|Test.Package_abc";
        fake.State = new ContextMenuEntry { Id = id, IsWindows11ContextMenu = true, IsEnabled = false };
        var service = new Windows11ContextMenuService(client,
            new LocalizationService(new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        await service.SetEnabledAsync(id, "Test", false);
        Assert.Equal(id, fake.LastMutationTarget);
        Assert.False(Assert.Single(service.CurrentItems).IsEnabled);
        Assert.Equal(1, fake.Mutations);
        Assert.Equal(1, fake.Queries);
    }

    [Fact]
    public async Task Win11ExplicitFailure_RemainsVisibleEvenWhenReadBackMatches()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        fake.MutationFailure = new BackendRequestException("State save failed.", "SAVE_FAILED");
        fake.State = new ContextMenuEntry { Id = "win11|test", IsWindows11ContextMenu = true, IsEnabled = false };
        var service = new Windows11ContextMenuService(client,
            new LocalizationService(new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        await Assert.ThrowsAsync<BackendRequestException>(() => service.SetEnabledAsync(fake.State.Id, "Test", false));
        Assert.False(Assert.Single(service.CurrentItems).IsEnabled);
        Assert.Equal(1, fake.Mutations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Win11UnconfirmedOutcome_PreventsRetryUntilSuccessfulRefresh(bool queryFails)
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        var id = $"win11|{Guid.NewGuid():B}|File|Test.Package_abc";
        fake.State = new ContextMenuEntry { Id = id, IsWindows11ContextMenu = true, IsEnabled = true };
        fake.QueryFails = queryFails;
        var service = new Windows11ContextMenuService(client,
            new LocalizationService(new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        if (queryFails)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(id, "Test", false));
        else
            await Assert.ThrowsAsync<TimeoutException>(() => service.SetEnabledAsync(id, "Test", false));
        Assert.True(service.IsStateUncertain(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(id, "Test", false));
        Assert.Equal(1, fake.Mutations);
        Assert.Equal(1, fake.Queries);
        fake.QueryFails = false;
        fake.State = fake.State with { IsEnabled = false };
        await service.RefreshAsync(CancellationToken.None);
        Assert.False(service.IsStateUncertain(id));
        Assert.False(Assert.Single(service.CurrentItems).IsEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Win11ApplicationAction_UsesOneRequestAndVerifiesEveryMenu(bool partial)
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        var first = new ContextMenuEntry { Id = "win11|one|File|Sample_abc", IsWindows11ContextMenu = true,
            Windows11PackageFamilyName = "Sample_abc", IsEnabled = true };
        var second = first with { Id = "win11|two|File|Sample_abc" };
        fake.States = [first, second, first with { Id = "win11|other|File|Other_abc", Windows11PackageFamilyName = "Other_abc" }];
        var service = new Windows11ContextMenuService(client,
            new LocalizationService(new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        await service.RefreshAsync(default);
        fake.States = [first with { IsEnabled = false }, second with { IsEnabled = partial }, fake.States[2]];
        if (partial)
            await Assert.ThrowsAsync<TimeoutException>(() => service.SetEnabledAsync(first.Id, "Sample", false, applicationScope: true));
        else
            await service.SetEnabledAsync(first.Id, "Sample", false, applicationScope: true);
        Assert.Equal(partial, service.IsStateUncertain(first.Id));
        Assert.True(fake.LastApplicationScope);
        Assert.Equal(1, fake.Mutations);
        Assert.True(service.CurrentItems.Single(item => item.Id == fake.States[2].Id).IsEnabled);
    }

    [Fact]
    public async Task Win11PendingApplication_RejectsDuplicateSubmissionUntilTheFirstRequestCompletes()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        var finish = new TaskCompletionSource<IReadOnlyList<ContextMenuEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Win11MutationCompletion = finish.Task;
        fake.State = new ContextMenuEntry { Id = "win11|pending", IsWindows11ContextMenu = true, IsEnabled = false };
        var service = new Windows11ContextMenuService(client,
            new LocalizationService(new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        var first = service.SetEnabledAsync(fake.State.Id, "App", false);
        Assert.True(service.IsOperationPending(fake.State.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(fake.State.Id, "App", false));
        Assert.Equal(1, fake.Mutations);
        finish.SetResult([fake.State]);
        await first;
        Assert.False(service.IsOperationPending(fake.State.Id));
        Assert.False(Assert.Single(service.CurrentItems).IsEnabled);
    }

    // Permanent: committed results must update every linked declaration without
    // another scan; an incomplete application response still requires read-back.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Win11CommittedApplicationResult_MergesLinkedStates_AndQueriesOnlyIfIncomplete(bool incomplete)
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        var first = new ContextMenuEntry { Id = "win11|one|File|App_abc", IsWindows11ContextMenu = true,
            Windows11PackageFamilyName = "App_abc", IsEnabled = true };
        var second = first with { Id = "win11|two|Directory|App_abc" };
        var shared = first with { Id = "win11|one|File|Shared_abc", Windows11PackageFamilyName = "Shared_abc" };
        var unrelated = first with { Id = "win11|other|File|Other_abc", Windows11PackageFamilyName = "Other_abc" };
        fake.States = [first, second, shared, unrelated];
        var service = new Windows11ContextMenuService(client,
            new LocalizationService(new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
        await service.RefreshAsync(default);
        fake.States = [first with { IsEnabled = false }, second with { IsEnabled = false }, shared with { IsEnabled = false }, unrelated];
        fake.Win11MutationCompletion = Task.FromResult<IReadOnlyList<ContextMenuEntry>>(incomplete ? [fake.States[0]] : fake.States.Take(3).ToArray());
        await service.SetEnabledAsync(first.Id, "App", false, applicationScope: true);
        Assert.Equal(incomplete ? 2 : 1, fake.Queries);
        Assert.Equal(1, fake.Mutations);
        Assert.All(service.CurrentItems.Where(item => item.Id != unrelated.Id), item => Assert.False(item.IsEnabled));
        Assert.True(service.CurrentItems.Single(item => item.Id == unrelated.Id).IsEnabled);
        Assert.False(service.IsStateUncertain(first.Id));
    }

    public class TimedOutBackend : DispatchProxy
    {
        public ContextMenuEntry? State { get; set; }
        public IReadOnlyList<ContextMenuEntry>? States { get; set; }
        public bool LastApplicationScope { get; private set; }
        public bool QueryFails { get; set; }
        public Exception? MutationFailure { get; set; }
        public Task<IReadOnlyList<ContextMenuEntry>>? Win11MutationCompletion { get; set; }
        public int Mutations { get; private set; }
        public int Queries { get; private set; }
        public string? LastMutationTarget { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name is "add_ItemsRemoved" or "remove_ItemsRemoved") return null;
            if (targetMethod?.Name == nameof(IBackendClient.ReadCachedListAsync))
                return Task.FromResult<MenuListSnapshot<ContextMenuEntry>?>(null);
            if (targetMethod?.Name is nameof(IBackendClient.SetWin11BlockedItemAsync) or nameof(IBackendClient.RemoveWin11BlockedItemAsync))
            {
                Mutations++;
                LastMutationTarget = args![0]?.ToString();
                LastApplicationScope = args[^1] is true;
                return Win11MutationCompletion ?? Task.FromException<IReadOnlyList<ContextMenuEntry>>(MutationFailure ?? new TimeoutException("Response was not delivered."));
            }
            if (targetMethod?.Name == nameof(IBackendClient.GetWin11ContextMenuSnapshotAsync))
            {
                Queries++;
                return QueryFails
                    ? Task.FromException<IReadOnlyList<ContextMenuEntry>>(new TimeoutException("Verification unavailable."))
                    : Task.FromResult<IReadOnlyList<ContextMenuEntry>>(States ?? (State is null ? [] : [State]));
            }
            if (targetMethod?.Name == nameof(IBackendClient.SetEnabledAsync))
            {
                Mutations++;
                return Task.FromException<ContextMenuEntry?>(MutationFailure ?? new TimeoutException("Response was not delivered."));
            }
            if (targetMethod?.Name == nameof(IBackendClient.GetContextMenuItemStateAsync))
            {
                Queries++;
                return QueryFails
                    ? Task.FromException<ContextMenuEntry?>(new TimeoutException("Query unavailable."))
                    : Task.FromResult(State);
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
