using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public sealed class FileContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.File, workspace, localization, settingsService);

public sealed class AllObjectsContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.AllFileSystemObjects, workspace, localization, settingsService);

public sealed class FolderContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Folder, workspace, localization, settingsService);

public sealed class DirectoryContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Directory, workspace, localization, settingsService);

public sealed class BackgroundContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.DirectoryBackground, workspace, localization, settingsService);

public sealed class DesktopContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.DesktopBackground, workspace, localization, settingsService);

public sealed class DriveContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Drive, workspace, localization, settingsService);

public sealed class LibraryContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Library, workspace, localization, settingsService);

public sealed class ComputerContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.Computer, workspace, localization, settingsService);

public sealed class RecycleBinContextMenuPageViewModel(
    ContextMenuWorkspaceService workspace,
    LocalizationService localization,
    FrontendSettingsService settingsService)
    : CategoryPageViewModel(ContextMenuCategory.RecycleBin, workspace, localization, settingsService);
