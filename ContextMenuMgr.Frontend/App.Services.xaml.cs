using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace ContextMenuMgr.Frontend;

/// <summary>
/// Represents the app.
/// </summary>
public partial class App
{
    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddNavigationViewPageProvider();
        services.AddSingleton<INavigationService, NavigationService>();

        services.AddSingleton<RuntimeDataAclRepairClient>();
        services.AddSingleton<FrontendSettingsService>();
        services.AddSingleton<FrontendStartupService>();
        services.AddSingleton<PortablePackageTrustService>();
        services.AddSingleton<TrayHostProcessService>();
        services.AddSingleton<FrontendNavigationState>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<FrontendThemeService>();
        services.AddSingleton<MainWindowPlacementService>();
        services.AddSingleton<IconPreviewService>();
        services.AddSingleton<RuleDictionaryCatalogService>();
        services.AddSingleton<EnhanceMenuRuleService>();
        services.AddSingleton<DetailedEditRuleService>();
        services.AddSingleton<Windows11ContextMenuService>();
        services.AddSingleton<ContextMenuGlobalSearchService>();
        services.AddSingleton<GlobalSearchNavigationFilterService>();
        services.AddSingleton<ContextMenuItemActionsService>();
        services.AddSingleton<ContextMenuApplicationIdentityService>();
        services.AddSingleton<ContextMenuDeepAnalysisService>();
        services.AddSingleton<ShellSubMenuDialogService>();
        services.AddSingleton<DetailedEditMenuDialogService>();
        services.AddSingleton<ListPlaceholderDebugStateService>();
        services.AddSingleton<IBackendClient, NamedPipeBackendClient>();
        services.AddSingleton<IBackendServiceManager, BackendServiceManager>();
        services.AddSingleton<ContextMenuWorkspaceService>();
        services.AddSingleton<ExplorerRestartStateService>();
        services.AddSingleton<IInfoBarService, InfoBarService>();
        services.AddSingleton<UpdateCheckService>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<ShellViewModel>(),
            sp,
            sp.GetRequiredService<INavigationService>(),
            sp.GetRequiredService<IInfoBarService>(),
            sp.GetRequiredService<UpdateCheckService>(),
            sp.GetRequiredService<FrontendThemeService>(),
            sp.GetRequiredService<MainWindowPlacementService>()));

        services.AddSingleton<FileContextMenuPageViewModel>();
        services.AddSingleton<AllObjectsContextMenuPageViewModel>();
        services.AddSingleton<FolderContextMenuPageViewModel>();
        services.AddSingleton<DirectoryContextMenuPageViewModel>();
        services.AddSingleton<BackgroundContextMenuPageViewModel>();
        services.AddSingleton<DesktopContextMenuPageViewModel>();
        services.AddSingleton<DriveContextMenuPageViewModel>();
        services.AddSingleton<LibraryContextMenuPageViewModel>();
        services.AddSingleton<ComputerContextMenuPageViewModel>();
        services.AddSingleton<RecycleBinContextMenuPageViewModel>();
        services.AddSingleton<ApplicationGroupsPageViewModel>();
        services.AddSingleton<Windows11ContextMenuPageViewModel>();
        services.AddSingleton<FileTypesPageViewModel>();
        services.AddSingleton<FileTypeMenuAnalysisPageViewModel>();
        services.AddSingleton<ShellNewPageViewModel>();
        services.AddSingleton<SendToPageViewModel>();
        services.AddSingleton<WinXPageViewModel>();
        services.AddSingleton<OpenWithPageViewModel>();
        services.AddSingleton<OtherRulesPageViewModel>();
        services.AddSingleton<ApprovalsPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();

        services.AddSingleton<FileContextMenuPage>();
        services.AddSingleton<AllObjectsContextMenuPage>();
        services.AddSingleton<FolderContextMenuPage>();
        services.AddSingleton<DirectoryContextMenuPage>();
        services.AddSingleton<BackgroundContextMenuPage>();
        services.AddSingleton<DesktopContextMenuPage>();
        services.AddSingleton<DriveContextMenuPage>();
        services.AddSingleton<LibraryContextMenuPage>();
        services.AddSingleton<ComputerContextMenuPage>();
        services.AddSingleton<RecycleBinContextMenuPage>();
        services.AddSingleton<ApplicationGroupsPage>();
        services.AddSingleton<Windows11ContextMenuPage>();
        services.AddSingleton<FileTypesPage>();
        services.AddSingleton<FileTypeMenuAnalysisPage>();
        services.AddSingleton<ShellNewPage>();
        services.AddSingleton<SendToPage>();
        services.AddSingleton<WinXPage>();
        services.AddSingleton<OpenWithPage>();
        services.AddSingleton<OtherRulesPage>();
        services.AddSingleton<ApprovalsPage>();
        services.AddSingleton<SettingsPage>();

        return services.BuildServiceProvider();
    }
}
