using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the file Context Menu Page View Model.
/// </summary>
public sealed class FileContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.File, workspace, localization, settingsService);

/// <summary>
/// Represents the all Objects Context Menu Page View Model.
/// </summary>
public sealed class AllObjectsContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.AllFileSystemObjects, workspace, localization, settingsService);

/// <summary>
/// Represents the folder Context Menu Page View Model.
/// </summary>
public sealed class FolderContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Folder, workspace, localization, settingsService);

/// <summary>
/// Represents the directory Context Menu Page View Model.
/// </summary>
public sealed class DirectoryContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Directory, workspace, localization, settingsService);

/// <summary>
/// Represents the background Context Menu Page View Model.
/// </summary>
public sealed class BackgroundContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.DirectoryBackground, workspace, localization, settingsService);

/// <summary>
/// Represents the desktop Context Menu Page View Model.
/// </summary>
public sealed class DesktopContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.DesktopBackground, workspace, localization, settingsService);

/// <summary>
/// Represents the drive Context Menu Page View Model.
/// </summary>
public sealed class DriveContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Drive, workspace, localization, settingsService);

/// <summary>
/// Represents the library Context Menu Page View Model.
/// </summary>
public sealed class LibraryContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Library, workspace, localization, settingsService);

/// <summary>
/// Represents the computer Context Menu Page View Model.
/// </summary>
public sealed class ComputerContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Computer, workspace, localization, settingsService);

/// <summary>
/// Represents the recycle Bin Context Menu Page View Model.
/// </summary>
public sealed class RecycleBinContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.RecycleBin, workspace, localization, settingsService);
