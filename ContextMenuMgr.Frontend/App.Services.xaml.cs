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
        services.AddSingleton<FrontendStartupService>();
        services.AddSingleton<TrayHostProcessService>();
        services.AddSingleton<FrontendNavigationState>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IconPreviewService>();
        services.AddSingleton<RuleDictionaryCatalogService>();
        services.AddSingleton<EnhanceMenuRuleService>();
        services.AddSingleton<DetailedEditRuleService>();
        services.AddSingleton<Windows11ContextMenuService>();
        services.AddSingleton<ContextMenuItemActionsService>();
        services.AddSingleton<IBackendClient, NamedPipeBackendClient>();
        services.AddSingleton<IBackendServiceManager, BackendServiceManager>();
        services.AddSingleton<ContextMenuWorkspaceService>();

        services.AddTransient<ShellViewModel>();
        services.AddTransient<MainWindow>();

        services.AddTransient<FileContextMenuPageViewModel>();
        services.AddTransient<AllObjectsContextMenuPageViewModel>();
        services.AddTransient<FolderContextMenuPageViewModel>();
        services.AddTransient<DirectoryContextMenuPageViewModel>();
        services.AddTransient<BackgroundContextMenuPageViewModel>();
        services.AddTransient<DesktopContextMenuPageViewModel>();
        services.AddTransient<DriveContextMenuPageViewModel>();
        services.AddTransient<LibraryContextMenuPageViewModel>();
        services.AddTransient<ComputerContextMenuPageViewModel>();
        services.AddTransient<RecycleBinContextMenuPageViewModel>();
        services.AddTransient<FileTypesPageViewModel>();
        services.AddTransient<Windows11ContextMenuPageViewModel>();
        services.AddTransient<OtherRulesPageViewModel>();
        services.AddTransient<ApprovalsPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();

        services.AddTransient<FileContextMenuPage>();
        services.AddTransient<AllObjectsContextMenuPage>();
        services.AddTransient<FolderContextMenuPage>();
        services.AddTransient<DirectoryContextMenuPage>();
        services.AddTransient<BackgroundContextMenuPage>();
        services.AddTransient<DesktopContextMenuPage>();
        services.AddTransient<DriveContextMenuPage>();
        services.AddTransient<LibraryContextMenuPage>();
        services.AddTransient<ComputerContextMenuPage>();
        services.AddTransient<RecycleBinContextMenuPage>();
        services.AddTransient<FileTypesPage>();
        services.AddTransient<Windows11ContextMenuPage>();
        services.AddTransient<OtherRulesPage>();
        services.AddTransient<ApprovalsPage>();
        services.AddTransient<SettingsPage>();

        return services.BuildServiceProvider();
    }
}
