using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ContextMenuWorkspaceNotificationScopeTests
{
    [Fact]
    public void SceneOnlyItemStateChanged_CannotEnterGlobalWorkspace()
    {
        const string sceneItemId = @"SystemFileAssociations\Video\shellex\ContextMenuHandlers|SceneHandler";
        var notification = new BackendNotification
        {
            Kind = PipeNotificationKind.ItemStateChanged,
            Item = new ContextMenuEntry
            {
                Id = sceneItemId,
                Category = ContextMenuCategory.File,
                SourceRootPath = @"SystemFileAssociations\Video\shellex\ContextMenuHandlers"
            }
        };

        var shouldUpsert = ContextMenuWorkspaceService.ShouldUpsertNotificationItem(
            notification,
            Array.Empty<string>());

        Assert.False(shouldUpsert);
    }

    [Fact]
    public void ExistingGlobalItemStateChanged_UpdatesWorkspaceItem()
    {
        const string regularItemId = @"*\shellex\ContextMenuHandlers|RegularHandler";
        var notification = new BackendNotification
        {
            Kind = PipeNotificationKind.ItemStateChanged,
            Item = new ContextMenuEntry
            {
                Id = regularItemId.ToUpperInvariant(),
                Category = ContextMenuCategory.File,
                SourceRootPath = @"*\shellex\ContextMenuHandlers",
                IsEnabled = false
            }
        };

        var shouldUpsert = ContextMenuWorkspaceService.ShouldUpsertNotificationItem(
            notification,
            [regularItemId]);

        Assert.True(shouldUpsert);
    }

    [Fact]
    public void ItemDetected_CanAddNewRegularWorkspaceItem()
    {
        var notification = new BackendNotification
        {
            Kind = PipeNotificationKind.ItemDetected,
            Item = new ContextMenuEntry
            {
                Id = @"*\shell|NewRegularVerb",
                Category = ContextMenuCategory.File,
                SourceRootPath = @"*\shell",
                IsPendingApproval = true
            }
        };

        var shouldUpsert = ContextMenuWorkspaceService.ShouldUpsertNotificationItem(
            notification,
            Array.Empty<string>());

        Assert.True(shouldUpsert);
        Assert.True(notification.Item.IsPendingApproval);
    }
}
