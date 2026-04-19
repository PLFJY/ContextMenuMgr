using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views;

namespace ContextMenuMgr.Frontend.Views.Pages;

/// <summary>
/// Represents the navigation Page Host.
/// </summary>
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

/// <summary>
/// Represents the file Context Menu Page.
/// </summary>
public sealed class FileContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileContextMenuPage"/> class.
    /// </summary>
    public FileContextMenuPage(FileContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the all Objects Context Menu Page.
/// </summary>
public sealed class AllObjectsContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AllObjectsContextMenuPage"/> class.
    /// </summary>
    public AllObjectsContextMenuPage(AllObjectsContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the folder Context Menu Page.
/// </summary>
public sealed class FolderContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FolderContextMenuPage"/> class.
    /// </summary>
    public FolderContextMenuPage(FolderContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the directory Context Menu Page.
/// </summary>
public sealed class DirectoryContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DirectoryContextMenuPage"/> class.
    /// </summary>
    public DirectoryContextMenuPage(DirectoryContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the background Context Menu Page.
/// </summary>
public sealed class BackgroundContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundContextMenuPage"/> class.
    /// </summary>
    public BackgroundContextMenuPage(BackgroundContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the desktop Context Menu Page.
/// </summary>
public sealed class DesktopContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopContextMenuPage"/> class.
    /// </summary>
    public DesktopContextMenuPage(DesktopContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the drive Context Menu Page.
/// </summary>
public sealed class DriveContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DriveContextMenuPage"/> class.
    /// </summary>
    public DriveContextMenuPage(DriveContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the library Context Menu Page.
/// </summary>
public sealed class LibraryContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryContextMenuPage"/> class.
    /// </summary>
    public LibraryContextMenuPage(LibraryContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the computer Context Menu Page.
/// </summary>
public sealed class ComputerContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ComputerContextMenuPage"/> class.
    /// </summary>
    public ComputerContextMenuPage(ComputerContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the recycle Bin Context Menu Page.
/// </summary>
public sealed class RecycleBinContextMenuPage : NavigationPageHost<CategoryPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecycleBinContextMenuPage"/> class.
    /// </summary>
    public RecycleBinContextMenuPage(RecycleBinContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the file Types Page.
/// </summary>
public sealed class FileTypesPage : NavigationPageHost<FileTypesPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileTypesPage"/> class.
    /// </summary>
    public FileTypesPage(FileTypesPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the windows11 Context Menu Page.
/// </summary>
public sealed class Windows11ContextMenuPage : NavigationPageHost<Windows11ContextMenuPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Windows11ContextMenuPage"/> class.
    /// </summary>
    public Windows11ContextMenuPage(Windows11ContextMenuPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the other Rules Page.
/// </summary>
public sealed class OtherRulesPage : NavigationPageHost<OtherRulesPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OtherRulesPage"/> class.
    /// </summary>
    public OtherRulesPage(OtherRulesPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the approvals Page.
/// </summary>
public sealed class ApprovalsPage : NavigationPageHost<ApprovalsPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalsPage"/> class.
    /// </summary>
    public ApprovalsPage(ApprovalsPageViewModel viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Represents the settings Page.
/// </summary>
public sealed class SettingsPage : NavigationPageHost<SettingsPageView>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsPage"/> class.
    /// </summary>
    public SettingsPage(SettingsPageViewModel viewModel) : base(viewModel)
    {
    }
}
