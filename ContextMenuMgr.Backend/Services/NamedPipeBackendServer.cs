using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

// Named pipes keep the frontend unprivileged while still allowing duplex messaging
// for snapshot queries, toggle requests, and service-pushed notifications.
public sealed class NamedPipeBackendServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ContextMenuRegistryCatalog _catalog;
    private readonly FileLogger _logger;
    private readonly ConcurrentDictionary<Guid, PipeClientConnection> _clients = new();
    private CancellationTokenSource? _acceptLoopCts;
    private Task? _acceptLoopTask;

    public event EventHandler? BackendShutdownRequested;
    public event EventHandler? EnsureTrayHostRequested;

    public NamedPipeBackendServer(ContextMenuRegistryCatalog catalog, FileLogger logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public void Start(CancellationToken cancellationToken)
    {
        _acceptLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_acceptLoopCts.Token), _acceptLoopCts.Token);
    }

    public void Stop()
    {
        _acceptLoopCts?.Cancel();
    }

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
                await _logger.LogAsync("Named pipe server instance created.", cancellationToken);

                await server.WaitForConnectionAsync(cancellationToken);
                server.ReadMode = PipeTransmissionMode.Byte;
                await _logger.LogAsync("Named pipe client handshake completed.", cancellationToken);
                _ = ObserveClientTaskAsync(server, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                await _logger.LogAsync($"Named pipe accept loop failed: {ex.Message}", cancellationToken);
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
        await _logger.LogAsync("HandleClientAsync entered.", cancellationToken);
        var connection = new PipeClientConnection(stream);
        _clients[connection.Id] = connection;

        try
        {
            await _logger.LogAsync($"Pipe client connected: {connection.Id}", cancellationToken);

            while (stream.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var line = await connection.Reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                await _logger.LogAsync($"Pipe request raw payload received from {connection.Id}. Length={line.Length}", cancellationToken);

                var envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
                if (envelope?.MessageType != PipeMessageType.Request || envelope.Request is null)
                {
                    await _logger.LogAsync($"Pipe payload from {connection.Id} was not a valid request.", cancellationToken);
                    continue;
                }

                await _logger.LogAsync($"Pipe request {envelope.Request.Command} received from {connection.Id}.", cancellationToken);

                if (envelope.Request.Command == PipeCommand.SubscribeNotifications)
                {
                    connection.IsNotificationSubscriber = true;
                    await _logger.LogAsync($"Connection {connection.Id} marked as frontend notification subscriber.", cancellationToken);
                }

                if (envelope.Request.Command == PipeCommand.SubscribeTrayHost)
                {
                    connection.IsNotificationSubscriber = true;
                    await _logger.LogAsync($"Connection {connection.Id} marked as tray-host subscriber.", cancellationToken);
                }

                PipeResponse response;
                try
                {
                    response = await HandleRequestAsync(envelope.Request, cancellationToken);
                }
                catch (Exception ex)
                {
                    await _logger.LogAsync(
                        $"Pipe request {envelope.Request.Command} failed for {connection.Id}: {ex.Message}",
                        cancellationToken);
                    response = new PipeResponse
                    {
                        Success = false,
                        Message = ex.Message
                    };
                }

                await connection.SendAsync(
                    new PipeEnvelope
                    {
                        MessageType = PipeMessageType.Response,
                        CorrelationId = envelope.CorrelationId,
                        Response = response
                    },
                    cancellationToken);
                await _logger.LogAsync($"Pipe response for {envelope.Request.Command} sent to {connection.Id}. Success={response.Success}", cancellationToken);

                if (response.Success && response.Item is not null)
                {
                    await BroadcastNotificationAsync(
                        new BackendNotification
                        {
                            Kind = PipeNotificationKind.ItemStateChanged,
                            Item = response.Item,
                            Message = response.Message,
                            Timestamp = DateTimeOffset.UtcNow
                        },
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Pipe client error: {ex.Message}", cancellationToken);
        }
        finally
        {
            _clients.TryRemove(connection.Id, out _);
            connection.Dispose();
            await _logger.LogAsync($"Pipe client disconnected: {connection.Id}", CancellationToken.None);
        }
    }

    private async Task<PipeResponse> HandleRequestAsync(PipeRequest request, CancellationToken cancellationToken)
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
            PipeCommand.GetSnapshot => new PipeResponse
            {
                Success = true,
                Message = "Snapshot loaded.",
                Items = await _catalog.GetSnapshotAsync(cancellationToken)
            },
            PipeCommand.GetSceneSnapshot when request.SceneKind is not null
                => new PipeResponse
                {
                    Success = true,
                    Message = "Scene snapshot loaded.",
                    Items = await _catalog.GetSceneSnapshotAsync(request.SceneKind.Value, request.ScopeValue, cancellationToken)
                },
            PipeCommand.SetEnhanceMenuItemEnabled when request.ScopeValue is not null && request.DefinitionXml is not null && request.Enable is not null
                => await _catalog.SetEnhanceMenuItemEnabledAsync(request.ScopeValue, request.DefinitionXml, request.Enable.Value, cancellationToken),
            PipeCommand.AcknowledgeItemState when request.ItemId is not null
                => await _catalog.AcknowledgeItemStateAsync(request.ItemId, cancellationToken),
            PipeCommand.SetEnabled when request.ItemId is not null && request.Enable is not null
                => await _catalog.ApplyDesiredStateAsync(request.ItemId, request.Enable.Value, cancellationToken),
            PipeCommand.SetShellAttribute when request.ItemId is not null && request.Enable is not null && request.ShellAttribute is not null
                => await _catalog.ApplyShellAttributeAsync(request.ItemId, request.ShellAttribute.Value, request.Enable.Value, cancellationToken),
            PipeCommand.SetDisplayText when request.ItemId is not null && request.TextValue is not null
                => await _catalog.ApplyDisplayTextAsync(request.ItemId, request.TextValue, cancellationToken),
            PipeCommand.GetRegistryProtectionSetting
                => await _catalog.GetRegistryProtectionSettingAsync(cancellationToken),
            PipeCommand.SetRegistryProtectionSetting when request.Enable is not null
                => await _catalog.SetRegistryProtectionSettingAsync(request.Enable.Value, cancellationToken),
            PipeCommand.ApplyDecision when request.ItemId is not null && request.Decision is not null
                => await _catalog.ApplyDecisionAsync(
                    request.ItemId,
                    request.Decision.Value,
                    cancellationToken),
            PipeCommand.DeleteItem when request.ItemId is not null
                => await _catalog.DeleteItemAsync(request.ItemId, cancellationToken),
            PipeCommand.UndoDelete when request.ItemId is not null
                => await _catalog.UndoDeleteAsync(request.ItemId, cancellationToken),
            PipeCommand.PurgeDeletedItem when request.ItemId is not null
                => await _catalog.PurgeDeletedItemAsync(request.ItemId, cancellationToken),
            _ => new PipeResponse
            {
                Success = false,
                Message = "The request was missing required data."
            }
        };
    }

    private async Task BroadcastNotificationAsync(BackendNotification notification, CancellationToken cancellationToken)
    {
        var envelope = new PipeEnvelope
        {
            MessageType = PipeMessageType.Notification,
            Notification = notification
        };

        foreach (var connection in _clients.Values.ToList())
        {
            try
            {
                await connection.SendAsync(envelope, cancellationToken);
            }
            catch
            {
                _clients.TryRemove(connection.Id, out _);
                connection.Dispose();
            }
        }
    }

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
        BackendShutdownRequested?.Invoke(this, EventArgs.Empty);
        return new PipeResponse
        {
            Success = true,
            Message = "Backend shutdown requested."
        };
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

    private sealed class PipeClientConnection : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly NamedPipeServerStream _stream;

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

        public Guid Id { get; }

        public StreamReader Reader { get; }

        public StreamWriter Writer { get; }

        public bool IsNotificationSubscriber { get; set; }

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

        public void Dispose()
        {
            Reader.Dispose();
            Writer.Dispose();
            _stream.Dispose();
            _writeLock.Dispose();
        }
    }
}
