using CommunityToolkit.Mvvm.ComponentModel;

namespace ContextMenuMgr.Frontend.Services;

public partial class FrontendNavigationState : ObservableObject
{
    [ObservableProperty]
    public partial string? FocusItemId { get; set; }

    public event EventHandler? ApprovalsRequested;

    public void RequestApprovals(string? focusItemId)
    {
        FocusItemId = focusItemId;
        ApprovalsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ClearFocusItem()
    {
        FocusItemId = null;
    }
}
