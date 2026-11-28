using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Backend.Hosting;

namespace ContextMenuMgr.Backend.Services;

// Named pipes keep the frontend unprivileged while still allowing duplex messaging
// for snapshot queries, toggle requests, and service-pushed notifications.
/// <summary>
/// Represents the named Pipe Backend Server.
/// </summary>
public sealed class NamedPipeBackendServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BackendRequestDispatcher _dispatcher;
    private readonly SpecialMenuService _specialMenuService;
    private readonly AutoStartService _autoStartService;
    private readonly ExplorerRestartService _explorerRestartService;
    private readonly FileLogger _logger;
    private readonly ConcurrentDictionary<Guid, PipeClientConnection> _clients = new();
    private readonly Lock _clientLifecycleGate = new();
    private bool _portableExitAccepted;
    private CancellationTokenSource? _acceptLoopCts;
    private Task? _acceptLoopTask;

    internal event EventHandler<BackendStopRequestedEventArgs>? BackendShutdownRequested;
    public event EventHandler? EnsureTrayHostRequested;

    internal NamedPipeBackendServer(BackendRequestDispatcher dispatcher, SpecialMenuService specialMenuService,
        AutoStartService autoStartService, ExplorerRestartService explorerRestartService, FileLogger logger)
    {
        _dispatcher = dispatcher;
        _specialMenuService = specialMenuService;
        _autoStartService = autoStartService;
        _explorerRestartService = explorerRestartService;
        _logger = logger;
    }

    /// <summary>
    /// Executes start.
    /// </summary>
    public void Start(CancellationToken cancellationToken)
    {
        _acceptLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_acceptLoopCts.Token), _acceptLoopCts.Token);
    }

    /// <summary>
    /// Executes stop.
    /// </summary>
    public async Task StopAsync()
    {
        _acceptLoopCts?.Cancel();
        await _specialMenuService.StopShellNewMutationsAsync();
    }

    /// <summary>
    /// Executes broadcast Notification.
    /// </summary>
    public void BroadcastNotification(BackendNotification notification)
    {
        _ = BroadcastNotificationAsync(notification, CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream();

                await server.WaitForConnectionAsync(cancellationToken);
                server.ReadMode = PipeTransmissionMode.Byte;
                await _logger.LogAsync($"PipeConnectionAccepted: PipeName={PipeConstants.PipeName}, Timestamp={DateTimeOffset.UtcNow:O}.", cancellationToken);
                _ = Task.Run(() => ObserveClientTaskAsync(server, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                await _logger.LogAsync(RuntimeLogLevel.Error, $"Named pipe accept loop failed: {ex}", cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task ObserveClientTaskAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(stream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        pipeSecurity.SetAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        pipeSecurity.SetAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
                    PipeConstants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    pipeSecurity);
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var connection = new PipeClientConnection(stream);
        lock (_clientLifecycleGate)
        {
            if (_portableExitAccepted) { connection.Dispose(); return; }
            _clients[connection.Id] = connection;
        }
        await _logger.LogAsync($"PipeClientConnected: ConnectionId={connection.Id}, Timestamp={DateTimeOffset.UtcNow:O}.", cancellationToken);

        try
        {
            while (stream.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var line = await connection.Reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                var envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
                if (envelope?.MessageType != PipeMessageType.Request || envelope.Request is null)
                {
                    await _logger.LogAsync(RuntimeLogLevel.Warning, $"Pipe payload from {connection.Id} was not a valid request.", cancellationToken);
                    continue;
                }

                if (envelope.Request.Command == PipeCommand.SubscribeNotifications)
                {
                    connection.IsNotificationSubscriber = true;
                    await _logger.LogAsync($"SubscribeNotifications: ConnectionId={connection.Id}, CorrelationId={envelope.CorrelationId}, ClientOperationId={envelope.Request.ClientOperationId}, Timestamp={DateTimeOffset.UtcNow:O}.", cancellationToken);
                }

                if (envelope.Request.Command == PipeCommand.SubscribeTrayHost)
                {
                    connection.IsNotificationSubscriber = true;
                    await _logger.LogAsync($"SubscribeTrayHost: ConnectionId={connection.Id}, CorrelationId={envelope.CorrelationId}, ClientOperationId={envelope.Request.ClientOperationId}, Timestamp={DateTimeOffset.UtcNow:O}.", cancellationToken);
                }

                if (connection.UserContext is null)
                {
                    connection.UserContext = new BackendUserContextResolver(_logger).TryResolveFromPipeClient(stream);
                    if (connection.UserContext is { } subscribedUser)
                        _specialMenuService.OpenWithMonitoring?.ObserveUser(subscribedUser);
                }

                BackendDispatchResult result;
                var stopwatch = Stopwatch.StartNew();
                await _logger.LogAsync(BuildRequestStartLog(connection.Id, envelope.CorrelationId, envelope.Request), cancellationToken);
                await _logger.LogRequestOperationAsync(envelope.Request.Command,
                    BuildOperationStartLog(connection.Id, envelope.CorrelationId, envelope.Request), cancellationToken: cancellationToken);
                try
                {
                    var context = new BackendRequestContext(
                        () => new BackendUserContextResolver(_logger).TryResolveFromPipeClient(stream), _logger);
                    var hostResponse = await HandleHostRequestAsync(envelope.Request, stream, context, cancellationToken);
                    result = hostResponse is null
                        ? await _dispatcher.DispatchAsync(envelope.Request, context, cancellationToken)
                        : new(BackendRequestDispatcher.CorrelateResponseWithRequest(envelope.Request, hostResponse));
                }
                catch (Exception ex)
                {
                    result = BackendRequestDispatcher.CreateFailure(envelope.Request, ex);
                }

                if (result.Error is { } error)
                {
                    await _logger.LogAsync(
                        RuntimeLogLevel.Error,
                        $"PipeRequestException: ConnectionId={connection.Id}, CorrelationId={envelope.CorrelationId}, Command={envelope.Request.Command}, ClientOperationId={envelope.Request.ClientOperationId}, Exception={error}",
                        cancellationToken);
                }
                stopwatch.Stop();
                var response = result.Response;

                await _logger.LogAsync(BuildRequestEndLog(connection.Id, envelope.CorrelationId, envelope.Request, response, stopwatch.ElapsedMilliseconds), cancellationToken);
                await _logger.LogRequestOperationAsync(envelope.Request.Command,
                    BuildOperationEndLog(connection.Id, envelope.CorrelationId, envelope.Request, response, stopwatch.ElapsedMilliseconds),
                    failed: !response.Success, cancellationToken: CancellationToken.None);

                var deliveryFailure = await PublishAndDeliverResponseAsync(
                    envelope.Request, response,
                    notification => notification.SpecialKind == SpecialMenuKind.OpenWith || notification.Kind == PipeNotificationKind.DeletedItemsPurged
                        ? connection.UserContext is { } caller
                            ? BroadcastNotificationAsync(notification with { UserSid = caller.Sid }, cancellationToken)
                            : Task.CompletedTask
                        : BroadcastNotificationAsync(notification, cancellationToken),
                    () => connection.SendAsync(
                        new PipeEnvelope
                        {
                            MessageType = PipeMessageType.Response,
                            CorrelationId = envelope.CorrelationId,
                            Response = response
                        }, cancellationToken));
                if (response.Success && response.Item is not null
                    && envelope.Request.Command != PipeCommand.GetContextMenuItemState)
                    await _logger.LogAsync($"BackendMutationNotification: CorrelationId={envelope.CorrelationId}, ClientOperationId={response.ClientOperationId}, Command={envelope.Request.Command}, ItemId={response.Item.Id}, BackendRegistryPath={response.Item.BackendRegistryPath}.", cancellationToken);
                if (deliveryFailure is not null)
                {
                    var notificationPublished = response.Item is not null
                        && envelope.Request.Command != PipeCommand.GetContextMenuItemState
                        || response.SpecialItem is not null && envelope.Request.Command != PipeCommand.GetSpecialMenuItemState
                        || response.DeletedItemsPurge is { PurgedItems.Count: > 0 };
                    await _logger.LogAsync(RuntimeLogLevel.Warning,
                        $"DirectResponseWriteFailed: CorrelationId={envelope.CorrelationId}, ClientOperationId={response.ClientOperationId}, Command={envelope.Request.Command}, ItemId={response.Item?.Id}, NotificationBroadcastBeforeResponseFailure={notificationPublished}, Message={deliveryFailure.Message}.", CancellationToken.None);
                    break;
                }
                if (!response.Success)
                {
                    await _logger.LogAsync(RuntimeLogLevel.Warning,
                        $"Pipe request {envelope.Request.Command} returned failure for {connection.Id}: {response.Message}",
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Pipe client error: ConnectionId={connection.Id}, Exception={ex}", cancellationToken);
        }
        finally
        {
            var wasSubscriber = connection.IsNotificationSubscriber;
            _clients.TryRemove(connection.Id, out _);
            connection.Dispose();
            await _logger.LogAsync($"PipeClientDisconnected: ConnectionId={connection.Id}, WasSubscriber={wasSubscriber}, Timestamp={DateTimeOffset.UtcNow:O}.", CancellationToken.None);
            if (wasSubscriber)
            {
                await _logger.LogAsync($"Pipe subscriber disconnected: {connection.Id}", CancellationToken.None);
            }
        }
    }

    internal static async Task<IOException?> PublishAndDeliverResponseAsync(
        PipeRequest request, PipeResponse response,
        Func<BackendNotification, Task> publish,
        Func<Task> deliver)
    {
        await BackendRequestDispatcher.PublishMutationNotificationsAsync(request, response, publish);
        try
        {
            await deliver();
            return null;
        }
        catch (IOException ex) when (response.Success)
        {
            return ex;
        }
    }

    private async Task<PipeResponse?> HandleHostRequestAsync(PipeRequest request, NamedPipeServerStream stream,
        BackendRequestContext context, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            PipeCommand.Ping => new PipeResponse
            {
                Success = true,
                Message = "Backend is reachable."
            },
            PipeCommand.EnsureTrayHost => HandleEnsureTrayHostRequest(),
            PipeCommand.SubscribeTrayHost => new PipeResponse
            {
                Success = true,
                Message = "Tray host subscription established."
            },
            PipeCommand.SubscribeNotifications => new PipeResponse
            {
                Success = true,
                Message = "Notification subscription established."
            },
            PipeCommand.RequestShutdown => HandleShutdownRequest(),
            PipeCommand.RequestPortableExit => HandlePortableExitRequest(stream),
            PipeCommand.SetLogLevel when request.LogLevel is not null
                => HandleSetLogLevelRequest(request.LogLevel.Value),
            PipeCommand.RestartExplorer
                => await HandleRestartExplorerRequestAsync(stream, cancellationToken),
            PipeCommand.RepairRuntimeDataAcl
                => await HandleRepairRuntimeDataAclAsync(cancellationToken),
            PipeCommand.SetAutoStartEnabled when request.AutoStartEnabled is not null
                => await _autoStartService.SetAutoStartEnabledAsync(
                    request.AutoStartEnabled.Value,
                    request.ClientOperationId,
                    await context.ResolveFrontendUserContextAsync(cancellationToken),
                    cancellationToken,
                    request.ShowTrayIcon),
            PipeCommand.GetAutoStartEnabled
                => await _autoStartService.GetAutoStartEnabledAsync(
                    request.ClientOperationId,
                    await context.ResolveFrontendUserContextAsync(cancellationToken),
                    cancellationToken),
            PipeCommand.SetTrayIconPolicy when request.ShowTrayIcon is not null
                => await _autoStartService.SetTrayIconPolicyAsync(
                    request.ShowTrayIcon.Value,
                    request.ClientOperationId,
                    await context.ResolveFrontendUserContextAsync(cancellationToken),
                    cancellationToken),
            _ => null
        };
    }

    private async Task<BackendUserContext?> ResolveFrontendUserSessionContextAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var resolver = new BackendUserContextResolver(_logger);
        var userContext = resolver.TryResolveFromPipeClientWithSessionFallback(stream);
        if (userContext?.SessionId is null)
        {
            await _logger.LogAsync(
                RuntimeLogLevel.Warning,
                "Frontend user session context could not be resolved for session-scoped request.",
                cancellationToken);
        }

        return userContext;
    }

    private async Task BroadcastNotificationAsync(BackendNotification notification, CancellationToken cancellationToken)
    {
        var envelope = new PipeEnvelope
        {
            MessageType = PipeMessageType.Notification,
            Notification = notification
        };

        var subscribers = _clients.Values.Where(connection => connection.IsNotificationSubscriber
            && (notification.SpecialKind == SpecialMenuKind.OpenWith
                ? notification.UserSid is not null && notification.UserSid.Equals(connection.UserContext?.Sid, StringComparison.OrdinalIgnoreCase)
                : notification.UserSid is null || notification.UserSid.Equals(connection.UserContext?.Sid, StringComparison.OrdinalIgnoreCase))).ToList();
        var removedDeadSubscribers = 0;
        await _logger.LogAsync(
            $"BroadcastNotificationStart: Kind={notification.Kind}, ClientOperationId={notification.ClientOperationId}, SubscriberCount={subscribers.Count}, Timestamp={DateTimeOffset.UtcNow:O}.",
            cancellationToken);

        foreach (var connection in subscribers)
        {
            try
            {
                await connection.SendAsync(envelope, cancellationToken);
            }
            catch
            {
                _clients.TryRemove(connection.Id, out _);
                connection.Dispose();
                removedDeadSubscribers++;
            }
        }

        await _logger.LogAsync(
            $"BroadcastNotificationEnd: Kind={notification.Kind}, SubscriberCount={subscribers.Count}, RemovedDeadSubscribers={removedDeadSubscribers}, Timestamp={DateTimeOffset.UtcNow:O}.",
            cancellationToken);
    }

    /// <summary>
    /// Executes broadcast Service Stopping Async.
    /// </summary>
    public async Task BroadcastServiceStoppingAsync(CancellationToken cancellationToken)
    {
        await BroadcastNotificationAsync(
            new BackendNotification
            {
                Kind = PipeNotificationKind.ServiceStopping,
                Message = "Backend service is stopping.",
                Timestamp = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private PipeResponse HandleShutdownRequest()
    {
        BackendShutdownRequested?.Invoke(this, new(BackendServiceStopReason.FrontendRequest));
        return new PipeResponse
        {
            Success = true,
            Message = "Backend shutdown requested."
        };
    }

    private PipeResponse HandlePortableExitRequest(NamedPipeServerStream stream)
    {
        try
        {
            // A just-completed install may still be releasing its lifecycle
            // lease when the frontend closes. Bound this wait inside the
            // frontend's three-second request budget; never retry a mutation.
            using var lifecycle = ServiceLifecycleLease.Acquire(TimeSpan.FromSeconds(1));
            using var caller = PortableExitCaller.Authenticate(stream,
                new BackendUserContextResolver(_logger).TryResolveFromPipeClient(stream));
            PortableServiceCleanup.VerifyFrontendCanStop(caller);
            lock (_clientLifecycleGate)
            {
                foreach (var client in _clients.Values)
                    if (!caller.OwnsConnectionOrClosed(client.Stream))
                        throw new IOException("Another or unresolvable pipe consumer still uses the service.");
                _portableExitAccepted = true;
                try { BackendShutdownRequested?.Invoke(this, new(BackendServiceStopReason.FrontendRequest) { PortableCaller = caller }); }
                catch { _portableExitAccepted = false; throw; }
            }
            return new PipeResponse { Success = true, Message = "Portable exit accepted; consult service lifecycle log for deletion outcome." };
        }
        catch (Exception ex)
        {
            BackendEmergencyLogger.Log(ex, "PortableExitRejected: runtime retained.");
            return new PipeResponse { Success = false, ErrorCode = "PORTABLE_EXIT_REFUSED", Message = ex.Message };
        }
    }

    private PipeResponse HandleEnsureTrayHostRequest()
    {
        EnsureTrayHostRequested?.Invoke(this, EventArgs.Empty);
        return new PipeResponse
        {
            Success = true,
            Message = "Tray host startup requested."
        };
    }

    private PipeResponse HandleSetLogLevelRequest(RuntimeLogLevel logLevel)
    {
        _logger.Configure(logLevel);
        _logger.LogFireAndForget($"Backend log level set to {logLevel}.");
        return new PipeResponse
        {
            Success = true,
            Message = $"Backend log level set to {logLevel}."
        };
    }

    private async Task<PipeResponse> HandleRepairRuntimeDataAclAsync(CancellationToken cancellationToken)
    {
        await _logger.LogAsync(
            $"RepairRuntimeDataAcl: Root={RuntimePaths.RootDirectory}, Result=Started.",
            cancellationToken);

        var result = RuntimeDataAclRepairService.RepairRuntimeDataDirectory(
            RuntimePaths.RootDirectory,
            cancellationToken);

        await _logger.LogAsync(
            result.Success ? RuntimeLogLevel.Information : RuntimeLogLevel.Warning,
            $"RepairRuntimeDataAcl: Root={RuntimePaths.RootDirectory}, Success={result.Success}, Code={result.Code}, Detail={result.Detail}",
            cancellationToken);

        return new PipeResponse
        {
            Success = result.Success,
            ErrorCode = result.Success ? null : result.Code,
            Message = string.IsNullOrWhiteSpace(result.Detail)
                ? (result.Success ? "Runtime data ACL repaired." : "Runtime data ACL repair failed.")
                : result.Detail
        };
    }

    private async Task<PipeResponse> HandleRestartExplorerRequestAsync(
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        await _logger.LogAsync(
            "RestartExplorerRequest: Sid=<null>, SessionId=<null>, Result=Started.",
            cancellationToken);

        var userContext = await ResolveFrontendUserSessionContextAsync(stream, cancellationToken);
        if (userContext is null)
        {
            MenuDiscoveryCoverage.MarkIncomplete();
            await _logger.LogAsync(
                RuntimeLogLevel.Warning,
                "RestartExplorerRequest: Sid=<null>, SessionId=<null>, Result=Failed, Reason=MissingFrontendUserContext.",
                cancellationToken);

            return new PipeResponse
            {
                Success = false,
                Message = "Cannot restart Explorer: frontend user context is not available."
            };
        }

        var sid = string.IsNullOrWhiteSpace(userContext.Sid) ? "<null>" : userContext.Sid;
        var sessionId = userContext.SessionId?.ToString() ?? "<null>";
        await _logger.LogAsync(
            $"RestartExplorerRequest: Sid={sid}, SessionId={sessionId}, Result=ContextResolved.",
            cancellationToken);

        if (!userContext.SessionId.HasValue)
        {
            await _logger.LogAsync(
                RuntimeLogLevel.Warning,
                $"RestartExplorerRequest: Sid={sid}, SessionId=<null>, Result=Failed, Reason=MissingFrontendUserSession.",
                cancellationToken);

            return new PipeResponse
            {
                Success = false,
                Message = "Cannot restart Explorer: frontend user session is not available."
            };
        }

        var killedCount = _explorerRestartService.RestartExplorer(userContext.SessionId);

        await _logger.LogAsync(
            $"RestartExplorerRequest: Sid={sid}, SessionId={userContext.SessionId.Value}, KilledCount={killedCount}, Result=Success.",
            cancellationToken);

        return new PipeResponse
        {
            Success = true,
            Message = killedCount == 0
                ? "Explorer restart requested, but no explorer.exe process was found in the frontend user session."
                : "Explorer restart requested."
        };
    }

    private static string BuildRequestStartLog(Guid connectionId, Guid correlationId, PipeRequest request)
        => $"PipeRequestStart: ConnectionId={connectionId}, CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, ItemId={request.ItemId}, SpecialKind={request.SpecialKind}, SceneKind={request.SceneKind}, Enable={request.Enable}, AutoStartEnabled={request.AutoStartEnabled}, ShowTrayIcon={request.ShowTrayIcon}, ShellNewLock={request.ShellNewLock?.Lock}, Timestamp={DateTimeOffset.UtcNow:O}.";

    private static string BuildRequestEndLog(Guid connectionId, Guid correlationId, PipeRequest request, PipeResponse response, long elapsedMs)
        => $"PipeRequestEnd: ConnectionId={connectionId}, CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success={response.Success}, Message={DiagnosticLogFormatter.FormatRegistryValueData(response.Message)}, ElapsedMs={elapsedMs}, HasItem={response.Item is not null}, HasSpecialItem={response.SpecialItem is not null}, ItemId={response.Item?.Id}, SpecialItemId={response.SpecialItem?.Id}.";

    private static string BuildOperationStartLog(Guid connectionId, Guid correlationId, PipeRequest request)
        => $"BackendOperationStart: ConnectionId={connectionId}, CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, ItemId={request.ItemId}, SpecialKind={request.SpecialKind}, SceneKind={request.SceneKind}, Enable={request.Enable}, Decision={request.Decision},{OpenWithReviewLogFields(request)} ShellAttribute={request.ShellAttribute}, AutoStartEnabled={request.AutoStartEnabled}, ShowTrayIcon={request.ShowTrayIcon}, Timestamp={DateTimeOffset.UtcNow:O}.";

    private static string BuildOperationEndLog(Guid connectionId, Guid correlationId, PipeRequest request, PipeResponse response, long elapsedMs)
        => $"BackendOperationEnd: ConnectionId={connectionId}, CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success={response.Success}, ErrorCode={response.ErrorCode ?? "<none>"},{OpenWithReviewLogFields(request)} Message={DiagnosticLogFormatter.FormatRegistryValueData(response.Message)}, ElapsedMs={elapsedMs}, ItemId={response.Item?.Id ?? request.ItemId}, SpecialItemId={response.SpecialItem?.Id}.";

    private static string OpenWithReviewLogFields(PipeRequest request)
    {
        OpenWithReviewDecision? decision = request.Command switch
        {
            PipeCommand.DecideOpenWithReview => request.OpenWithReview?.Decision,
            PipeCommand.ReviewOpenWithRegistration when request.SpecialItem is not null && request.OpenWithProtection is null
                => OpenWithReviewDecision.Accept,
            _ => null
        };
        if (decision is null) return string.Empty;
        var path = request.OpenWithReview?.RegistryPath
            ?? request.SpecialItem?.Metadata.GetValueOrDefault("AppRegistryPath")
            ?? request.SpecialItem?.RegistryPath;
        var safePath = DiagnosticLogFormatter.FormatRegistryValueData(path?.Replace("\r", "\\r").Replace("\n", "\\n"));
        return $" OpenWithReviewDecision={decision}, OpenWithRegistryPath={safePath},";
    }

    private sealed class PipeClientConnection : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly NamedPipeServerStream _stream;

        /// <summary>
        /// Executes pipe Client Connection.
        /// </summary>
        public PipeClientConnection(NamedPipeServerStream stream)
        {
            Id = Guid.NewGuid();
            _stream = stream;
            Reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            Writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true
            };
        }

        /// <summary>
        /// Gets the id.
        /// </summary>
        public Guid Id { get; }

        public NamedPipeServerStream Stream => _stream;

        /// <summary>
        /// Gets the reader.
        /// </summary>
        public StreamReader Reader { get; }

        /// <summary>
        /// Gets the writer.
        /// </summary>
        public StreamWriter Writer { get; }

        /// <summary>
        /// Gets or sets a value indicating whether notification Subscriber.
        /// </summary>
        public bool IsNotificationSubscriber { get; set; }

        public BackendUserContext? UserContext { get; set; }

        /// <summary>
        /// Executes send Async.
        /// </summary>
        public async Task SendAsync(PipeEnvelope envelope, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(envelope, JsonOptions);

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await Writer.WriteLineAsync(payload).WaitAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Executes dispose.
        /// </summary>
        public void Dispose()
        {
            Reader.Dispose();
            Writer.Dispose();
            _stream.Dispose();
            _writeLock.Dispose();
        }
    }
}
