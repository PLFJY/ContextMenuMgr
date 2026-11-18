using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Views;

/// <summary>
/// Represents the reusable special menu content view.
/// </summary>
public partial class SpecialMenuContentView : UserControl
{
    private SpecialMenuPageViewModel? _viewModel;
    private readonly CollectionContainer _readOnlyRows = new();
    private bool _subscribed;
    private System.Windows.Window? _owner;
    /// <summary>
    /// Initializes a new instance of the <see cref="SpecialMenuContentView"/> class.
    /// </summary>
    public SpecialMenuContentView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += (_, _) => ConfigureRows();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void ConfigureRows()
    {
        Unsubscribe();
        _viewModel = DataContext as SpecialMenuPageViewModel;
        if (_viewModel is null) { NormalMenuList.ItemsSource = null; return; }
        var rows = new CompositeCollection { new CollectionContainer { Collection = _viewModel.ItemsView } };
        if (_viewModel.Kind == SpecialMenuKind.OpenWith)
        {
            rows.Add(_viewModel);
            rows.Add(_readOnlyRows);
        }
        NormalMenuList.ItemsSource = rows;
        UpdateReadOnlyRows();
        if (IsLoaded) Subscribe();
    }

    private void Subscribe()
    {
        if (_subscribed || _viewModel is null) return;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _owner = System.Windows.Window.GetWindow(this);
        if (_owner is not null) _owner.Activated += OnOwnerActivated;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (_subscribed && _viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_owner is not null) _owner.Activated -= OnOwnerActivated;
        _owner = null;
        _subscribed = false;
    }

    private async void OnOwnerActivated(object? sender, EventArgs e)
    {
        try
        {
            if (_viewModel is not null) await _viewModel.RefreshDocumentIconRepairOnActivationAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ContextMenuMgr.Frontend.Services.FrontendDebugLog.Warning(nameof(SpecialMenuContentView),
                $"Document icon state refresh after activation failed: {ex}");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpecialMenuPageViewModel.IsOpenWithReadOnlyExpanded)) UpdateReadOnlyRows();
    }

    private void UpdateReadOnlyRows() => _readOnlyRows.Collection = _viewModel is { IsOpenWithReadOnlyExpanded: true }
        ? _viewModel.OpenWithReadOnlyItemsView : Array.Empty<SpecialMenuItemViewModel>();

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Subscribe();
        UpdateReadOnlyRows();
        if (DataContext is not SpecialMenuPageViewModel viewModel || viewModel.IsNavigationTransitioning)
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

}
