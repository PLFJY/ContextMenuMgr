using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class NamedPipeBackendClientNotificationSuppressionTests
{
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
        var operationId = Guid.NewGuid();

        cache.Register(operationId);
        cache.Remove(operationId);
        Assert.False(cache.Contains(operationId));

        cache.Register(operationId);
        now = now.AddSeconds(11);
        Assert.False(cache.Contains(operationId));
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
