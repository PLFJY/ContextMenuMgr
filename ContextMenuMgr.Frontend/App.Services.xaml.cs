using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views.Pages;
using ContextMenuMgr.Contracts;
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
#if CONTEXTMENUMGR_IN_PROCESS
        services.AddSingleton<MenuListCacheService>(sp => new MenuListCacheService(
            System.IO.Path.Combine(RuntimePaths.DataDirectory, "Cache", "MenuLists"),
            MenuListCacheService.GetFingerprint(sp.GetRequiredService<InProcessBackendRuntime>().UserSid)));
#else
        services.AddSingleton<MenuListCacheService>();
#endif
        services.AddSingleton<MenuListActivityService>();
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
#if CONTEXTMENUMGR_IN_PROCESS
        services.AddSingleton<DetailedEditRuleService>(sp => new DetailedEditRuleService(
            sp.GetRequiredService<IBackendClient>(), sp.GetRequiredService<FrontendSettingsService>(),
            sp.GetRequiredService<LocalizationService>(),
            () => sp.GetRequiredService<InProcessBackendRuntime>().UserSid,
            sp.GetRequiredService<InProcessBackendRuntime>().InteractiveUser));
#else
        services.AddSingleton<DetailedEditRuleService>();
#endif
        services.AddSingleton<Windows11ContextMenuService>();
        services.AddSingleton<ContextMenuGlobalSearchService>();
        services.AddSingleton<GlobalSearchNavigationFilterService>();
        services.AddSingleton<ContextMenuItemActionsService>();
        services.AddSingleton<ContextMenuApplicationIdentityService>();
        services.AddSingleton<ContextMenuDeepAnalysisService>();
        services.AddSingleton<ShellSubMenuDialogService>();
        services.AddSingleton<DetailedEditMenuDialogService>();
#if CONTEXTMENUMGR_IN_PROCESS
        services.AddSingleton<InProcessBackendRuntime>(_ => InProcessBackendRuntime.Create());
        services.AddSingleton<IBackendClient>(sp => new BackendClient(sp.GetRequiredService<InProcessBackendRuntime>().Transport,
            sp.GetRequiredService<MenuListCacheService>(), sp.GetRequiredService<MenuListActivityService>()));
#else
        services.AddSingleton<IBackendClient>(sp => new BackendClient(new NamedPipeBackendTransport(),
            sp.GetRequiredService<MenuListCacheService>(), sp.GetRequiredService<MenuListActivityService>()));
#endif
        services.AddSingleton<IBackendServiceManager, BackendServiceManager>();
        services.AddSingleton<ContextMenuWorkspaceService>();
        services.AddSingleton<ExplorerRestartStateService>();
        services.AddSingleton<IInfoBarService, InfoBarService>();
        services.AddSingleton<MenuListFeedbackService>();
        services.AddSingleton<UpdateCheckService>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>(sp =>
        {
            _ = sp.GetRequiredService<MenuListFeedbackService>();
            return new MainWindow(
            sp.GetRequiredService<ShellViewModel>(),
            sp,
            sp.GetRequiredService<INavigationService>(),
            sp.GetRequiredService<IInfoBarService>(),
            sp.GetRequiredService<UpdateCheckService>(),
            sp.GetRequiredService<FrontendThemeService>(),
            sp.GetRequiredService<MainWindowPlacementService>());
        });

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

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
