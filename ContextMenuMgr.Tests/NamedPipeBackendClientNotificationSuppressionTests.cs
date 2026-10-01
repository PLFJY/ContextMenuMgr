using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using System.IO.Pipes;
using System.Text.Json;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class NamedPipeBackendClientNotificationSuppressionTests
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
    public void LocalShellNewNotificationBeforeResponse_IsSuppressedAndCannotCreateDuplicate()
    {
        var operationId = Guid.NewGuid();
        var cache = new RecentClientOperationCache();
        var client = new NamedPipeBackendClient(cache);
        var oldItem = CreateShellNewItem(@"HKEY_USERS\sid\Software\Classes\.txt\ShellNew", isEnabled: true);
        var updatedItem = CreateShellNewItem(@"HKEY_USERS\sid\Software\Classes\.txt\-ShellNew", isEnabled: false);
        var items = new List<SpecialMenuEntry> { oldItem };
        client.NotificationReceived += (_, notification) =>
        {
            var item = notification.SpecialItem!;
            var current = items.FirstOrDefault(existing => string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                items.Add(item);
            }
        };

        // SendRequestAsync registers the operation before its response is read.
        cache.Register(operationId);
        var notification = new BackendNotification
        {
            SpecialKind = SpecialMenuKind.ShellNew,
            SpecialItem = updatedItem,
            ClientOperationId = operationId
        };

        // This subscriber mirrors SpecialMenuPageViewModel.Upsert's Id-only match.
        Assert.False(client.TryForwardSubscriptionNotification(notification));

        // The direct response then updates the ViewModel representing the original item.
        items[0] = updatedItem;

        Assert.Single(items);
        Assert.Equal(updatedItem.Id, items[0].Id);
        Assert.False(items[0].IsEnabled);
    }

    [Fact]
    public void LocalNotificationAfterDirectResponse_IsStillSuppressedDuringRetentionWindow()
    {
        var operationId = Guid.NewGuid();
        var cache = new RecentClientOperationCache();
        var client = new NamedPipeBackendClient(cache);

        // A successful SendRequestAsync retains this entry after it returns.
        cache.Register(operationId);

        Assert.False(client.TryForwardSubscriptionNotification(new BackendNotification { ClientOperationId = operationId }));
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

    private static SpecialMenuEntry CreateShellNewItem(string id, bool isEnabled)
        => new()
        {
            Id = id,
            Kind = SpecialMenuKind.ShellNew,
            DisplayName = "Text Document",
            KeyName = ".txt",
            IsEnabled = isEnabled
        };
}
