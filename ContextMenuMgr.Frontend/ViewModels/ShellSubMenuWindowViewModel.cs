using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public sealed partial class ShellSubMenuItemViewModel : ObservableObject
{
    private readonly IBackendClient _backend;
    private readonly LocalizationService _localization;
    private readonly string _parentId;
    private bool _suppress;

    public ShellSubMenuItemViewModel(ShellSubMenuItem item, string parentId, IBackendClient backend, LocalizationService localization)
    {
        Item = item; _parentId = parentId; _backend = backend; _localization = localization; IsEnabled = item.IsEnabled;
    }
    public ShellSubMenuItem Item { get; private set; }
    public string DisplayName => Item.DisplayName;
    public string? CommandText => Item.CommandText;
    public bool IsSeparator => Item.IsSeparator;
    public bool CanToggle => Item.CanToggle && !IsBusy;
    public string ToggleLabel => _localization.Translate(IsEnabled ? "ToggleOn" : "ToggleOff");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    public partial bool IsEnabled { get; set; }
    partial void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        if (_suppress || oldValue == newValue || !Item.CanToggle) return;
        if (IsBusy) { Revert(oldValue); return; }
        IsBusy = true;
        _ = SetEnabledAsync(oldValue, newValue);
    }
    private async Task SetEnabledAsync(bool oldValue, bool value)
    {
        try
        {
            await ToggleBusyPresentation.WaitForFirstFrameAsync();
            var updated = await _backend.SetShellSubMenuItemEnabledAsync(_parentId, Item.Id, value, CancellationToken.None);
            if (updated is null) { Revert(oldValue); return; }
            Item = updated; _suppress = true; IsEnabled = updated.IsEnabled; _suppress = false;
        }
        catch { Revert(oldValue); }
        finally { IsBusy = false; }
    }
    public void Update(ShellSubMenuItem item)
    {
        if (IsBusy || MenuListData.Equivalent(Item, item)) return;
        Item = item; Revert(item.IsEnabled);
        OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(CommandText));
        OnPropertyChanged(nameof(IsSeparator)); OnPropertyChanged(nameof(CanToggle));
    }
    private void Revert(bool value) { _suppress = true; IsEnabled = value; _suppress = false; }
}

public sealed partial class ShellSubMenuWindowViewModel : ObservableObject, IDisposable
{
    private readonly IBackendClient _backend;
    private readonly LocalizationService _localization;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loadTask;
    private bool _hasSnapshot;
    private bool _disposed;
    public ShellSubMenuWindowViewModel(ContextMenuEntry parent, IBackendClient backend, LocalizationService localization)
    {
        Parent = parent; _backend = backend; _localization = localization;
        Title = localization.Translate("ManageSubMenuItems"); LoadingText = localization.Translate("LoadingStatus");
        _backend.ItemsRemoved += OnItemsRemoved;
    }
    public ContextMenuEntry Parent { get; }
    public string ParentDisplayName => Parent.DisplayName;
    public string Title { get; }
    public string LoadingText { get; }
    public ObservableCollection<ShellSubMenuItemViewModel> Items { get; } = [];
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial string ErrorMessage { get; private set; } = string.Empty;
    public Task LoadAsync() => _loadTask is { IsCompleted: false } ? _loadTask : _loadTask = LoadCoreAsync();
    private async Task LoadCoreAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            if (!_hasSnapshot)
            {
                var cached = await _backend.ReadCachedListAsync<ShellSubMenuItem>(new PipeRequest {
                    Command = PipeCommand.GetShellSubMenuItems, ParentItemId = Parent.Id }, timeout.Token);
                if (cached is not null && !_disposed) Apply(cached);
            }
            IsLoading = !_hasSnapshot;
            var fresh = await _backend.GetShellSubMenuItemsAsync(Parent.Id, timeout.Token);
            if (!_disposed) { Apply(fresh); ErrorMessage = string.Empty; }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) ErrorMessage = ex.Message; }
        finally { if (!_disposed) IsLoading = false; }
    }
    private void Apply(IReadOnlyList<ShellSubMenuItem> entries)
    {
        entries = MenuListData.Current(entries);
        _hasSnapshot = true; IsLoading = false;
        var existing = Items.ToDictionary(item => item.Item.Id, StringComparer.OrdinalIgnoreCase);
        var target = new List<ShellSubMenuItemViewModel>();
        foreach (var entry in entries)
        {
            if (!existing.Remove(entry.Id, out var row)) row = new ShellSubMenuItemViewModel(entry, Parent.Id, _backend, _localization);
            else row.Update(entry);
            target.Add(row);
        }
        target.AddRange(existing.Values.Where(item => item.IsBusy || !MenuListData.AllowsRemoval(entries)));
        IncrementalCollection.Apply(Items, target);
    }
    private void OnItemsRemoved(object? sender, MenuItemsRemovedEventArgs e)
    {
        if (e.Request.ParentItemId != Parent.Id) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Remove()
        {
            if (_disposed) return;
            foreach (var row in Items.Where(row => e.ItemIds.Contains(row.Item.Id, StringComparer.OrdinalIgnoreCase)).ToArray()) Items.Remove(row);
        }
        if (dispatcher is not null && !dispatcher.CheckAccess()) _ = dispatcher.BeginInvoke((Action)Remove); else Remove();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.ItemsRemoved -= OnItemsRemoved;
        _shutdown.Cancel(); _shutdown.Dispose();
    }
}
