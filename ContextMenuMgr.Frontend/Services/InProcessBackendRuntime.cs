#if CONTEXTMENUMGR_IN_PROCESS
using System.Diagnostics;
using System.IO;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal sealed class InProcessBackendRuntime : IAsyncDisposable
{
    private readonly PortableInteractiveUser _user;
    private readonly BackendCore _core;
    private readonly InProcessBackendTransport _transport;
    private readonly FileLogger _logger;
    private readonly ContextMenuStateStore _stateStore;

    private InProcessBackendRuntime(PortableInteractiveUser user, BackendCore core, ContextMenuStateStore stateStore, FileLogger logger)
    {
        _user = user;
        _core = core;
        _stateStore = stateStore;
        _logger = logger;
        _transport = new InProcessBackendTransport(DispatchAsync, () => _user.TryGetContext()?.Sid, logger);
        _stateStore.PortableHostMismatchDetected += OnPortableHostMismatchDetected;
        _stateStore.RecoveryOccurred += OnStateRecoveryOccurred;
    }

    public IBackendTransport Transport => _transport;
    public string UserSid => _user.Context.Sid;
    internal PortableInteractiveUser InteractiveUser => _user;

    public static InProcessBackendRuntime Create()
    {
        if (!RuntimeHosting.IsInProcess || RuntimePaths.PackageKind != RuntimePackageKind.Portable)
            throw new InvalidOperationException("In-process backend composition requires the dedicated Portable hosting build.");

        var user = InProcessBootstrap.User;
        try
        {
            Directory.CreateDirectory(RuntimePaths.LogsDirectory);
            Directory.CreateDirectory(RuntimePaths.LocksDirectory);
            Directory.CreateDirectory(RuntimePaths.DeletedBackupsDirectory);
            var logger = new FileLogger(RuntimePaths.BackendLogPath);
            var identity = new RuntimeHostIdentityProvider(logger, () => user.TryGetContext()?.Sid);
            var state = new ContextMenuStateStore(RuntimePaths.StateDatabasePath, logger, identity);
            var settings = new BackendProtectionSettingsStore(RuntimePaths.BackendProtectionSettingsPath, logger);
            var backups = new RegistryBackupService(RuntimePaths.DeletedBackupsDirectory, logger, identity);
            var core = new BackendCore(logger, state, backups, settings, user.TryOpenClassesRoot,
                () => [user.TryGetContext() ?? throw new InvalidOperationException("The verified desktop user is no longer available.")],
                enableProtectionWorkflows: false, expandInteractiveEnvironment: user.ExpandEnvironmentVariables,
                useFrontendClassesRootForMetadata: true);
            if (!state.IsCurrentHostIdentityVerified)
                throw new InvalidOperationException("Portable data belongs to another host identity. Preserve the data and resolve it before using this runtime.");
            return new InProcessBackendRuntime(user, core, state, logger);
        }
        catch
        {
            user.Dispose();
            throw;
        }
    }

    private async Task<BackendDispatchResult> DispatchAsync(PipeRequest request, InProcessBackendRoute route,
        CancellationToken cancellationToken)
    {
        if (route == InProcessBackendRoute.Host)
        {
            var response = request.Command switch
            {
                PipeCommand.Ping or PipeCommand.SubscribeNotifications => new PipeResponse { Success = true, Message = "Local backend ready." },
                PipeCommand.SetLogLevel when request.LogLevel is { } level => SetLogLevel(level),
                PipeCommand.RestartExplorer => new PipeResponse
                {
                    Success = _user.TryGetContext() is { } desktopUser && RestartExplorer(desktopUser.SessionId) > 0,
                    Message = "Explorer restart requested for the verified desktop session."
                },
                _ => new PipeResponse { Message = $"{request.Command} has no local host implementation." }
            };
            return new(response);
        }

        if (request.Command == PipeCommand.SetDetailedEditRuleValue)
        {
            try
            {
                request = DetailedEditRegistryTarget.Bind(request, _user);
            }
            catch (Exception ex)
            {
                return BackendRequestDispatcher.CreateFailure(request, ex);
            }
        }

        var context = new BackendRequestContext(() => _user.TryGetContext(), _logger);
        return await _core.Dispatcher.DispatchAsync(request, context, cancellationToken).ConfigureAwait(false);
    }

    private PipeResponse SetLogLevel(RuntimeLogLevel level)
    {
        _logger.Configure(level);
        return new PipeResponse { Success = true, Message = "Log level updated." };
    }

    private void OnPortableHostMismatchDetected(object? sender, string message) => _transport.PublishNotification(new BackendNotification
    {
        Kind = PipeNotificationKind.ServiceMessage,
        Message = message,
        Timestamp = DateTimeOffset.UtcNow
    });

    private void OnStateRecoveryOccurred(object? sender, ContextMenuStateStoreRecovery recovery)
    {
        var resourceKey = recovery.Health == ContextMenuStateStoreHealth.RecoveredFromBackup
            ? "ContextMenuStateRecoveredFromBackup"
            : "ContextMenuStateResetAfterCorruption";
        _transport.PublishNotification(new BackendNotification
        {
            Kind = PipeNotificationKind.ServiceMessage,
            Message = resourceKey,
            MessageResourceKey = resourceKey,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    private static int RestartExplorer(int? sessionId)
    {
        if (sessionId is null) return 0;
        var restarted = 0;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId.Value) continue;
                    process.Kill();
                    if (process.WaitForExit(5000)) restarted++;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return restarted;
    }

    public async ValueTask DisposeAsync()
    {
        _stateStore.PortableHostMismatchDetected -= OnPortableHostMismatchDetected;
        _stateStore.RecoveryOccurred -= OnStateRecoveryOccurred;
        await _transport.DisposeAsync().ConfigureAwait(false);
        _user.Dispose();
    }
}
#endif
