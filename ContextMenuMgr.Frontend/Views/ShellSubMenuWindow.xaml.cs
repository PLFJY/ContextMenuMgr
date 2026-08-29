using ContextMenuMgr.Frontend.ViewModels;
using Wpf.Ui.Controls;
namespace ContextMenuMgr.Frontend.Views;
public partial class ShellSubMenuWindow : FluentWindow
{ public ShellSubMenuWindow(ShellSubMenuWindowViewModel viewModel) { InitializeComponent(); DataContext = viewModel; } }
