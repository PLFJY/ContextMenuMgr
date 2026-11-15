namespace ContextMenuMgr.Frontend.Views;

/// <summary>
/// Represents the scene Browser View.
/// </summary>
public partial class SceneBrowserView : System.Windows.Controls.UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SceneBrowserView"/> class.
    /// </summary>
    public SceneBrowserView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ViewModels.SceneContextMenuTabViewModel)?.RefreshSearchImmediately();
        Unloaded += (_, _) => (DataContext as ViewModels.SceneContextMenuTabViewModel)?.CancelPendingSearch();
    }
}
