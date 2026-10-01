using System.Windows.Controls;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Views;

/// <summary>
/// Represents the reusable special menu content view.
/// </summary>
public partial class SpecialMenuContentView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SpecialMenuContentView"/> class.
    /// </summary>
    public SpecialMenuContentView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not SpecialMenuPageViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnDocumentIconProviderChecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not SpecialMenuPageViewModel viewModel
            || sender is not RadioButton { IsChecked: true } radio
            || viewModel.IsDocumentIconProviderBusy
            || !viewModel.ShowDocumentIconProvider
            || !Enum.TryParse<DocumentIconProvider>(radio.Tag as string, out var provider))
        {
            return;
        }

        viewModel.SelectedDocumentIconProvider = provider;
    }

}
