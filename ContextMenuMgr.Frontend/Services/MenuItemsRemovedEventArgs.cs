using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

public sealed class MenuItemsRemovedEventArgs(PipeRequest request, IReadOnlyList<string> itemIds, bool notifyUser = false) : EventArgs
{
    public PipeRequest Request { get; } = request;
    public IReadOnlyList<string> ItemIds { get; } = itemIds;
    public bool NotifyUser { get; } = notifyUser;
}
