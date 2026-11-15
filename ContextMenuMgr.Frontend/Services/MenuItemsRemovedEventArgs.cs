using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

public sealed class MenuItemsRemovedEventArgs(PipeRequest request, IReadOnlyList<string> itemIds, bool notifyUser = false) : EventArgs
{
    public PipeRequest Request { get; } = request;
    public IReadOnlyList<string> ItemIds { get; } = itemIds;
    public bool NotifyUser { get; } = notifyUser;

    public bool Matches(ContextMenuEntry entry) => ItemIds.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)
        && (Request.DeletedItemsPurge is not { } purge || entry.IsDeleted && purge.Items.Any(expected =>
            string.Equals(expected.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && expected.DeletedAtUtc == entry.DeletedAtUtc));
}
