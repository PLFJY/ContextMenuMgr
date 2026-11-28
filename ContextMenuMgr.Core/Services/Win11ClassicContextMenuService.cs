using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>Authenticated per-user Windows 11 classic context menu configuration.</summary>
public sealed class Win11ClassicContextMenuService(FileLogger logger)
{
    private const string ClsidPath = @"Software\Classes\CLSID";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task<PipeResponse> GetDisabledAsync(Guid? operationId, BackendUserContext? userContext,
        CancellationToken cancellationToken) => ExecuteAsync(null, operationId, userContext, cancellationToken);

    public Task<PipeResponse> SetDisabledAsync(bool disabled, Guid? operationId, BackendUserContext? userContext,
        CancellationToken cancellationToken) => ExecuteAsync(disabled, operationId, userContext, cancellationToken);

    private async Task<PipeResponse> ExecuteAsync(bool? disabled, Guid? operationId,
        BackendUserContext? context, CancellationToken cancellationToken)
    {
        var entered = false;
        Win11ClassicContextMenuMutation? mutation = null;
        try
        {
            if (!OperatingSystem.IsWindows() || !SupportsOverrides(Environment.OSVersion.Version))
                return new() { Success = disabled != true, ClientOperationId = operationId,
                    Message = "This setting only applies to Windows 11.", Win11ModernContextMenuDisabled = false,
                    Win11ClassicContextMenuChanged = false };
            if (context is null) throw new InvalidOperationException("Authenticated interactive user context is required.");
            await Gate.WaitAsync(cancellationToken);
            entered = true;
            using var root = OpenRoot(context, disabled is not null);
            var changed = false;
            string status;
            try
            {
                if (disabled is not null)
                {
                    mutation = new(root, ClsidPath, disabled.Value);
                    changed = mutation.Apply(cancellationToken);
                }
                // Reopen the authenticated hive and all targets after mutation.
                using var fresh = OpenRoot(context);
                status = Win11ClassicContextMenuMutation.ReadStatus(fresh, ClsidPath);
                if (disabled is not null && (!mutation!.Verify()
                    || (status == "Complete") != disabled.Value || status == "Conflict"))
                    throw new InvalidOperationException("Compatibility registry state changed during verification.");
            }
            catch
            {
                if (mutation is not null)
                {
                    var rollback = mutation.TryRollback();
                    await logger.LogAsync(RuntimeLogLevel.Warning,
                        $"Win11ClassicContextMenuRollback: Sid={context.Sid}, Succeeded={rollback.Succeeded}, Conflict={rollback.Conflict}, Error={rollback.Error}", CancellationToken.None);
                    if (!rollback.Succeeded)
                        throw new InvalidOperationException($"Compatibility operation failed; rollback could not be fully verified: {rollback.Error}. Refresh before retrying.");
                }
                throw;
            }
            await logger.LogAsync($"Win11ClassicContextMenu: Sid={context.Sid}, Status={status}, Changed={changed}.", CancellationToken.None);
            return new() { Success = true, ClientOperationId = operationId,
                Win11ModernContextMenuDisabled = status == "Complete", Win11ClassicContextMenuStatus = status,
                Win11ClassicContextMenuChanged = changed,
                Message = $"Windows 11 compatibility override state: {status}." };
        }
        catch (Exception ex)
        {
            await logger.LogAsync(RuntimeLogLevel.Error, $"Win11ClassicContextMenu: Sid={context?.Sid}, Failed={ex}", CancellationToken.None);
            return new() { Success = false, ClientOperationId = operationId, Message = ex.Message };
        }
        finally { if (entered) Gate.Release(); }
    }

    // Both overrides on every supported Windows 11 version; rollout is not
    // inferred from the marketing release or a cumulative-update build number.
    internal static bool SupportsOverrides(Version version) => version >= new Version(10, 0, 22000);

    private static RegistryKey OpenRoot(BackendUserContext context, bool writable = false)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        return users.OpenSubKey(context.Sid, writable)
            ?? throw new InvalidOperationException($"The registry hive for user {context.Sid} is not loaded.");
    }
}
