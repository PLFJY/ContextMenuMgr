using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class MutationNotificationDeliveryTests
{
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
}
