using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the file Context Menu Page View Model.
/// </summary>
public sealed class FileContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.File, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the all Objects Context Menu Page View Model.
/// </summary>
public sealed class AllObjectsContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.AllFileSystemObjects, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the folder Context Menu Page View Model.
/// </summary>
public sealed class FolderContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.Folder, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the directory Context Menu Page View Model.
/// </summary>
public sealed class DirectoryContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.Directory, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the background Context Menu Page View Model.
/// </summary>
public sealed class BackgroundContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.DirectoryBackground, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the desktop Context Menu Page View Model.
/// </summary>
public sealed class DesktopContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.DesktopBackground, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the drive Context Menu Page View Model.
/// </summary>
public sealed class DriveContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.Drive, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the library Context Menu Page View Model.
/// </summary>
public sealed class LibraryContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.Library, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the computer Context Menu Page View Model.
/// </summary>
public sealed class ComputerContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.Computer, workspace, backendClient, localization, settingsService, globalSearchFilterService);

/// <summary>
/// Represents the recycle Bin Context Menu Page View Model.
/// </summary>
public sealed class RecycleBinContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    IBackendClient backendClient,
    LocalizationService localization,
    FrontendSettingsService settingsService,
    GlobalSearchNavigationFilterService globalSearchFilterService)
    : CategoryPageViewModel(ContextMenuCategory.RecycleBin, workspace, backendClient, localization, settingsService, globalSearchFilterService);
