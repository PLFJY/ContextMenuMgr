using System.Windows.Controls;

namespace ContextMenuMgr.Frontend.Views;

/// <summary>
/// Represents the approvals Page View.
/// </summary>
public partial class ApprovalsPageView : Page
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalsPageView"/> class.
    /// </summary>
    public ApprovalsPageView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.ApprovalsPageViewModel viewModel)
                await viewModel.OpenWithReviews.RefreshAsync(CancellationToken.None);
        };
    }
}
