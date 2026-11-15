using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Views;

public partial class ApplicationGroupsPageView : Page
{
    private readonly ObservableCollection<object> _rows = [];
    private readonly HashSet<ApplicationGroupViewModel> _subscribedGroups = [];
    private ApplicationGroupsPageViewModel? _viewModel;
    private DispatcherOperation? _pendingRows;
    private bool _attached;

    public ApplicationGroupsPageView()
    {
        InitializeComponent();
        ApplicationRows.ItemsSource = _rows;
        DataContextChanged += (_, _) => { Detach(); _viewModel = DataContext as ApplicationGroupsPageViewModel; if (IsLoaded) Attach(); };
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        if (_attached || _viewModel is null) return;
        _attached = true;
        _viewModel.Groups.CollectionChanged += OnGroupsChanged;
        SynchronizeRows();
    }

    private void Detach()
    {
        _pendingRows?.Abort();
        _pendingRows = null;
        if (_viewModel is not null && _attached) _viewModel.Groups.CollectionChanged -= OnGroupsChanged;
        foreach (var group in _subscribedGroups) group.Items.CollectionChanged -= OnGroupItemsChanged;
        _subscribedGroups.Clear();
        _attached = false;
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRows();
    private void OnGroupItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRows();

    private void QueueRows()
    {
        if (_pendingRows is not null) return;
        _pendingRows = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
        { _pendingRows = null; if (_attached) SynchronizeRows(); }));
    }

    private void SynchronizeRows()
    {
        if (_viewModel is null) return;
        var groups = _viewModel.Groups.ToHashSet();
        foreach (var removed in _subscribedGroups.Except(groups).ToArray())
        { removed.Items.CollectionChanged -= OnGroupItemsChanged; _subscribedGroups.Remove(removed); }
        foreach (var added in groups.Except(_subscribedGroups))
        { added.Items.CollectionChanged += OnGroupItemsChanged; _subscribedGroups.Add(added); }
        var rows = new List<object> { _viewModel };
        foreach (var group in _viewModel.Groups)
        {
            rows.Add(group);
            rows.AddRange(group.Items);
        }
        IncrementalCollection.Apply(_rows, rows);
    }
}
