using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ContextMenuMgr.Frontend;

public partial class App
{
    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<FrontendSettingsService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IconPreviewService>();
        services.AddSingleton<RuleDictionaryCatalogService>();
        services.AddSingleton<EnhanceMenuRuleService>();
        services.AddSingleton<DetailedEditRuleService>();
        services.AddSingleton<ContextMenuItemActionsService>();
        services.AddSingleton<IBackendClient, NamedPipeBackendClient>();
        services.AddSingleton<IBackendServiceManager, BackendServiceManager>();
        services.AddSingleton<ContextMenuWorkspaceService>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();

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
        services.AddSingleton<FileTypesPageViewModel>();
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
        services.AddSingleton<FileTypesPage>();
        services.AddSingleton<OtherRulesPage>();
        services.AddSingleton<ApprovalsPage>();
        services.AddSingleton<SettingsPage>();

        return services.BuildServiceProvider();
    }
}
