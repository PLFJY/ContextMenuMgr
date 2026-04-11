using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views;

namespace ContextMenuMgr.Frontend.Views.Pages;

public abstract class NavigationPageHost<TView> : System.Windows.Controls.Page
    where TView : System.Windows.Controls.UserControl, new()
{
    protected NavigationPageHost(object viewModel)
    {
        DataContext = viewModel;
        Content = new TView
        {
            DataContext = viewModel
        };
    }
}

public sealed class FileContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public FileContextMenuPage(FileContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class AllObjectsContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public AllObjectsContextMenuPage(AllObjectsContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class FolderContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public FolderContextMenuPage(FolderContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class DirectoryContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public DirectoryContextMenuPage(DirectoryContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class BackgroundContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public BackgroundContextMenuPage(BackgroundContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class DesktopContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public DesktopContextMenuPage(DesktopContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class DriveContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public DriveContextMenuPage(DriveContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class LibraryContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public LibraryContextMenuPage(LibraryContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class ComputerContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public ComputerContextMenuPage(ComputerContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class RecycleBinContextMenuPage : NavigationPageHost<CategoryPageView>
{
    public RecycleBinContextMenuPage(RecycleBinContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class FileTypesPage : NavigationPageHost<FileTypesPageView>
{
    public FileTypesPage(FileTypesPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class Windows11ContextMenuPage : NavigationPageHost<Windows11ContextMenuPageView>
{
    public Windows11ContextMenuPage(Windows11ContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class OtherRulesPage : NavigationPageHost<OtherRulesPageView>
{
    public OtherRulesPage(OtherRulesPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class ApprovalsPage : NavigationPageHost<ApprovalsPageView>
{
    public ApprovalsPage(ApprovalsPageViewModel viewModel) : base(viewModel)
    {
    }
}

public sealed class SettingsPage : NavigationPageHost<SettingsPageView>
{
    public SettingsPage(SettingsPageViewModel viewModel) : base(viewModel)
    {
    }
}
