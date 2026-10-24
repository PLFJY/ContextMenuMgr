using CommunityToolkit.Mvvm.ComponentModel;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the frontend Navigation State.
/// </summary>
public partial class FrontendNavigationState : ObservableObject
{
    /// <summary>
    /// Gets or sets the focus Item Id.
    /// </summary>
    [ObservableProperty]
    public partial string? FocusItemId { get; set; }

    public event EventHandler? ApprovalsRequested;
    public event EventHandler? OpenWithRequested;
    public string? OpenWithFocusPath { get; private set; }

    public void RequestOpenWith(string path)
    {
        OpenWithFocusPath = path;
        OpenWithRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Executes request Approvals.
    /// </summary>
    public void RequestApprovals(string? focusItemId)
    {
        FocusItemId = focusItemId;
        ApprovalsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Executes clear Focus Item.
    /// </summary>
    public void ClearFocusItem()
    {
        FocusItemId = null;
    }
}
