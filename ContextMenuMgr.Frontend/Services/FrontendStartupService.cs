using System.Diagnostics;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the frontend Startup Service.
/// All registry operations are now delegated to the backend service.
/// Maintains backward compatibility with synchronous API while internally using async backend calls.
/// </summary>
public sealed class FrontendStartupService
{
    private readonly IBackendClient _backendClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrontendStartupService"/> class.
    /// </summary>
    public FrontendStartupService(IBackendClient backendClient)
    {
        _backendClient = backendClient;
    }

    /// <summary>
    /// Executes is Auto Start Enabled (synchronous wrapper for backward compatibility).
    /// </summary>
    public bool IsAutoStartEnabled()
    {
        try
        {
            // Run synchronously to maintain existing API contract
            return IsAutoStartEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Fallback if backend is unavailable
            return false;
        }
    }

    /// <summary>
    /// Executes is Auto Start Enabled Async.
    /// </summary>
    public async Task<bool> IsAutoStartEnabledAsync(CancellationToken cancellationToken = default)
    {
        return await _backendClient.GetAutoStartEnabledAsync(cancellationToken);
    }

    /// <summary>
    /// Sets auto Start Enabled (synchronous wrapper for backward compatibility).
    /// </summary>
    public void SetAutoStartEnabled(bool enabled)
    {
        try
        {
            // Run synchronously to maintain existing API contract
            SetAutoStartEnabledAsync(enabled, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set auto-start: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets auto Start Enabled Async.
    /// </summary>
    public async Task SetAutoStartEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default,
        bool? showTrayIcon = null)
    {
        await _backendClient.SetAutoStartEnabledAsync(enabled, cancellationToken, showTrayIcon);
    }

    public async Task SetTrayIconPolicyAsync(bool showTrayIcon, CancellationToken cancellationToken = default)
    {
        await _backendClient.SetTrayIconPolicyAsync(showTrayIcon, cancellationToken);
    }
}
