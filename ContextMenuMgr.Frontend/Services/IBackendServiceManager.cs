using System.ServiceProcess;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Defines the contract for i Backend Service Manager.
/// </summary>
public interface IBackendServiceManager
{
    bool IsServiceInstalled();

    ServiceControllerStatus? GetServiceStatus();

    Task<BackendServiceBootstrapResult> InstallOrRepairServiceAsync(CancellationToken cancellationToken);

    Task<BackendServiceBootstrapResult> StopServiceAsync(CancellationToken cancellationToken);

    Task<BackendServiceBootstrapResult> UninstallServiceAsync(CancellationToken cancellationToken);

    Task<BackendServiceBootstrapResult> SetServiceAutoStartEnabledAsync(bool enabled, CancellationToken cancellationToken);
}
