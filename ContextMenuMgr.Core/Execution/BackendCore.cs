namespace ContextMenuMgr.Backend.Services;

/// <summary>Composes one set of shared menu services over the host's runtime stores.</summary>
internal sealed class BackendCore
{
    public BackendCore(FileLogger logger, ContextMenuStateStore stateStore, RegistryBackupService backupService,
        BackendProtectionSettingsStore protectionSettingsStore, TryOpenFrontendClassesRoot tryOpenFrontendClassesRoot,
        Func<IReadOnlyList<BackendUserContext>> resolveLoggedInUsers, Action<string>? logStartup = null)
    {
        logStartup?.Invoke("CreateDefault: creating ContextMenuRegistryCatalog.");
        Catalog = new ContextMenuRegistryCatalog(logger, stateStore, backupService, protectionSettingsStore);
        logStartup?.Invoke("CreateDefault: ContextMenuRegistryCatalog created.");

        logStartup?.Invoke("CreateDefault: creating SpecialMenuService.");
        SpecialMenu = new SpecialMenuService(logger, tryOpenFrontendClassesRoot);
        OpenWith = new OpenWithMonitoringService(stateStore, logger, resolveLoggedInUsers);
        SpecialMenu.OpenWithMonitoring = OpenWith;
        logStartup?.Invoke("CreateDefault: SpecialMenuService created.");

        logStartup?.Invoke("CreateDefault: creating Windows11BlocksService.");
        var windows11BlocksService = new Windows11BlocksService(logger);
        logStartup?.Invoke("CreateDefault: Windows11BlocksService created.");

        logStartup?.Invoke("CreateDefault: creating Win11ClassicContextMenuService.");
        var win11ClassicContextMenuService = new Win11ClassicContextMenuService(logger);
        logStartup?.Invoke("CreateDefault: Win11ClassicContextMenuService created.");

        logStartup?.Invoke("CreateDefault: creating FileTypeSceneMenuService.");
        var fileTypeSceneMenuService = new FileTypeSceneMenuService(Catalog, stateStore, logger);
        logStartup?.Invoke("CreateDefault: FileTypeSceneMenuService created.");

        Dispatcher = new BackendRequestDispatcher(Catalog, SpecialMenu, windows11BlocksService,
            win11ClassicContextMenuService, fileTypeSceneMenuService, logger);
    }

    public ContextMenuRegistryCatalog Catalog { get; }
    public SpecialMenuService SpecialMenu { get; }
    public OpenWithMonitoringService OpenWith { get; }
    public BackendRequestDispatcher Dispatcher { get; }
}
