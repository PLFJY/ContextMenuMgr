using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views;
namespace ContextMenuMgr.Frontend.Services;
public sealed class ShellSubMenuDialogService
{
    private readonly IBackendClient _backend; private readonly LocalizationService _localization;
    public ShellSubMenuDialogService(IBackendClient backend, LocalizationService localization) { _backend = backend; _localization = localization; }
    public async Task ShowAsync(ContextMenuEntry parent)
    { var vm = new ShellSubMenuWindowViewModel(parent, _backend, _localization); var window = new ShellSubMenuWindow(vm) { Owner = System.Windows.Application.Current?.MainWindow }; window.Show(); await vm.LoadAsync(); }
}
