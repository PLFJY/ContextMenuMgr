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

    [Fact]
    public async Task LateAuthoritativeSuccess_IsNotOverwrittenByOlderFailedTask()
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
            });
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.IsToggleBusy) && !item.IsToggleBusy)
                idle.TrySetResult();
        };

        item.IsEnabled = false;
        Assert.True(item.IsToggleBusy);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        item.Update(original with { IsEnabled = false });
        finishRequest.SetResult(false);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(item.IsEnabled);
        Assert.False(item.IsToggleBusy);
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

    public class TimedOutBackend : DispatchProxy
    {
        public ContextMenuEntry? State { get; set; }
        public bool QueryFails { get; set; }
        public Exception? MutationFailure { get; set; }
        public int Mutations { get; private set; }
        public int Queries { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
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
