using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public sealed partial class ShellSubMenuItemViewModel : ObservableObject
{
    private readonly IBackendClient _backend;
    private readonly string _parentId;
    private bool _suppress;

    public ShellSubMenuItemViewModel(ShellSubMenuItem item, string parentId, IBackendClient backend)
    {
        Item = item; _parentId = parentId; _backend = backend; IsEnabled = item.IsEnabled;
    }
    public ShellSubMenuItem Item { get; private set; }
    public string DisplayName => Item.DisplayName;
    public string? CommandText => Item.CommandText;
    public bool IsSeparator => Item.IsSeparator;
    public bool CanToggle => Item.CanToggle && !IsBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; private set; }
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }
    partial void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        if (_suppress || oldValue == newValue || !Item.CanToggle) return;
        _ = SetEnabledAsync(oldValue, newValue);
    }
    private async Task SetEnabledAsync(bool oldValue, bool value)
    {
        IsBusy = true;
        try
        {
            var updated = await _backend.SetShellSubMenuItemEnabledAsync(_parentId, Item.Id, value, CancellationToken.None);
            if (updated is null) { Revert(oldValue); return; }
            Item = updated; _suppress = true; IsEnabled = updated.IsEnabled; _suppress = false;
        }
        catch { Revert(oldValue); }
        finally { IsBusy = false; }
    }
    private void Revert(bool value) { _suppress = true; IsEnabled = value; _suppress = false; }
}

public sealed partial class ShellSubMenuWindowViewModel : ObservableObject
{
    private readonly IBackendClient _backend;
    public ShellSubMenuWindowViewModel(ContextMenuEntry parent, IBackendClient backend, LocalizationService localization)
    { Parent = parent; _backend = backend; Title = localization.Translate("ManageSubMenuItems"); LoadingText = localization.Translate("LoadingStatus"); }
    public ContextMenuEntry Parent { get; }
    public string ParentDisplayName => Parent.DisplayName;
    public string Title { get; }
    public string LoadingText { get; }
    public ObservableCollection<ShellSubMenuItemViewModel> Items { get; } = [];
    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;
    [ObservableProperty] public partial string ErrorMessage { get; private set; } = string.Empty;
    public async Task LoadAsync()
    {
        try { foreach (var item in await _backend.GetShellSubMenuItemsAsync(Parent.Id, CancellationToken.None)) Items.Add(new ShellSubMenuItemViewModel(item, Parent.Id, _backend)); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }
}
