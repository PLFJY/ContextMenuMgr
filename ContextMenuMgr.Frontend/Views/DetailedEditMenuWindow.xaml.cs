using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Wpf.Ui.Controls;
namespace ContextMenuMgr.Frontend.Views;
public sealed partial class DetailedEditMenuWindow : FluentWindow
{
    public DetailedEditMenuWindow(DetailedEditGroupViewModel group, LocalizationService localization)
    { InitializeComponent(); DataContext = new DetailedEditMenuWindowViewModel(group, localization); }
}
public sealed class DetailedEditMenuWindowViewModel
{
    public DetailedEditMenuWindowViewModel(DetailedEditGroupViewModel group, LocalizationService localization) { Group = group; Title = localization.Translate("ManageSubMenuItems"); }
    public DetailedEditGroupViewModel Group { get; }
    public string Title { get; }
}
