using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal sealed class NamedPipeBackendTransport(string pipeName) : IBackendTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _notificationSync = new();
    private CancellationTokenSource? _notificationLoopCts;
    private Task? _notificationLoopTask;
    private bool _isConnected;
    private bool _disposed;

    public NamedPipeBackendTransport() : this(PipeConstants.PipeName)
    {
    }

    public event Action<BackendNotification>? NotificationReceived;

    public bool IsConnected => _isConnected;

    public void StartNotifications()
    {
        lock (_notificationSync)
        {
            if (_notificationLoopTask is null || _notificationLoopTask.IsCompleted)
            {
                _notificationLoopCts?.Cancel();
                _notificationLoopCts?.Dispose();
                _notificationLoopCts = new CancellationTokenSource();
                _notificationLoopTask = Task.Run(() => NotificationLoopAsync(_notificationLoopCts.Token), CancellationToken.None);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        CancellationTokenSource? notificationLoopCts;
        Task? notificationLoopTask;
        lock (_notificationSync)
        {
            notificationLoopCts = _notificationLoopCts;
            notificationLoopTask = _notificationLoopTask;
            _notificationLoopCts = null;
            _notificationLoopTask = null;
        }

        notificationLoopCts?.Cancel();
        return notificationLoopTask is null
            ? DisposeNotificationLoopAsync(notificationLoopCts, null)
            : DisposeNotificationLoopAsync(notificationLoopCts, notificationLoopTask);
    }

    public async Task<PipeResponse> SendAsync(PipeRequest request, Action<BackendNotification> onNotification, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = Guid.Empty;
        try
        {
            using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            stream.ReadMode = PipeTransmissionMode.Byte;

            using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true
            };

            correlationId = Guid.NewGuid();
            FrontendDebugLog.Info("NamedPipeBackendClient",
                $"FrontendRequestStarted: CorrelationId={correlationId}, ClientOperationId={request.ClientOperationId}, Command={request.Command}, ItemId={request.ItemId}, QueueWaitMs=0.");
            var envelope = new PipeEnvelope
            {
                MessageType = PipeMessageType.Request,
                CorrelationId = correlationId,
                Request = request
            };

            FrontendDebugLog.RequestOperation(
                request.Command,
                BuildOperationStartLog(correlationId, request));

            var payload = JsonSerializer.Serialize(envelope, JsonOptions);
            await writer.WriteLineAsync(payload).WaitAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new IOException("The backend pipe closed before returning a response.");
                }

                var responseEnvelope = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
                if (responseEnvelope is null)
                {
                    continue;
                }

                if (responseEnvelope.MessageType == PipeMessageType.Notification && responseEnvelope.Notification is not null)
                {
                    FrontendDebugLog.Info(
                        "NamedPipeBackendClient",
                        $"Notification received on request channel: {responseEnvelope.Notification.Kind}, ItemId={responseEnvelope.Notification.Item?.Id}");
                    onNotification(responseEnvelope.Notification);

                    continue;
                }

                if (responseEnvelope.MessageType != PipeMessageType.Response || responseEnvelope.Response is null)
                {
                    continue;
                }

                if (!responseEnvelope.Response.Success)
                {
                    stopwatch.Stop();
                    FrontendDebugLog.Warning(
                        "NamedPipeBackendClient",
                        $"SendRequestAsync <- {request.Command} failed. ErrorCode={responseEnvelope.Response.ErrorCode ?? "<none>"}, Message={responseEnvelope.Response.Message}");
                    FrontendDebugLog.RequestOperation(
                        request.Command,
                        BuildOperationEndLog(correlationId, request, responseEnvelope.Response, stopwatch.ElapsedMilliseconds), failed: true);
                    return responseEnvelope.Response;
                }

                stopwatch.Stop();
                FrontendDebugLog.RequestOperation(
                    request.Command,
                    BuildOperationEndLog(correlationId, request, responseEnvelope.Response, stopwatch.ElapsedMilliseconds));
                FrontendDebugLog.Info("NamedPipeBackendClient", $"SendRequestAsync <- {request.Command} succeeded.");
                return responseEnvelope.Response;
            }
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            FrontendDebugLog.RequestOperation(
                request.Command,
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error=Timeout, ElapsedMs={stopwatch.ElapsedMilliseconds}.", failed: true);
            FrontendDebugLog.Warning("NamedPipeBackendClient", $"SendRequestAsync timed out for {request.Command}: {ex.Message}");
            throw;
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            FrontendDebugLog.RequestOperation(
                request.Command,
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error=Canceled, ElapsedMs={stopwatch.ElapsedMilliseconds}.", failed: true);
            FrontendDebugLog.Info(
                "NamedPipeBackendClient",
                $"SendRequestAsync canceled for {request.Command}. Disposed={_disposed}, CancellationRequested={cancellationToken.IsCancellationRequested}, Message={ex.Message}");
            throw;
        }
        catch (ObjectDisposedException ex) when (_disposed || cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            FrontendDebugLog.RequestOperation(
                request.Command,
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error=Disposed, ElapsedMs={stopwatch.ElapsedMilliseconds}.", failed: true);
            FrontendDebugLog.Info(
                "NamedPipeBackendClient",
                $"SendRequestAsync disposed while {request.Command} was in flight. Disposed={_disposed}, CancellationRequested={cancellationToken.IsCancellationRequested}, Message={ex.Message}");
            throw;
        }
        catch (IOException ex) when (_disposed || cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            FrontendDebugLog.RequestOperation(
                request.Command,
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error=PipeClosedDuringCancellation, ElapsedMs={stopwatch.ElapsedMilliseconds}.", failed: true);
            FrontendDebugLog.Info(
                "NamedPipeBackendClient",
                $"SendRequestAsync pipe closed during cancellation/shutdown for {request.Command}: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            FrontendDebugLog.Error(
                "FrontendOperation",
                ex,
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error={ex.GetType().Name}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            FrontendDebugLog.Error("NamedPipeBackendClient", ex, $"SendRequestAsync failed for {request.Command}.");
            throw;
        }
    }

    private static string BuildOperationStartLog(Guid correlationId, PipeRequest request)
        => $"FrontendOperationStart: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, ItemId={request.ItemId}, BackendRegistryPath={request.Item?.BackendRegistryPath}, SpecialKind={request.SpecialKind}, SceneKind={request.SceneKind}, Enable={request.Enable}, AutoStartEnabled={request.AutoStartEnabled}, ShowTrayIcon={request.ShowTrayIcon}, Timestamp={DateTimeOffset.UtcNow:O}.";

    private static string BuildOperationEndLog(Guid correlationId, PipeRequest request, PipeResponse response, long elapsedMs)
        => $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success={response.Success}, ErrorCode={response.ErrorCode ?? "<none>"}, Message={response.Message}, WireElapsedMs={elapsedMs}, HasItem={response.Item is not null}, HasSpecialItem={response.SpecialItem is not null}.";

    private async Task NotificationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await stream.ConnectAsync(2000, cancellationToken);
                stream.ReadMode = PipeTransmissionMode.Byte;

                using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
                {
                    AutoFlush = true
                };

                var subscriptionEnvelope = new PipeEnvelope
                {
                    MessageType = PipeMessageType.Request,
                    CorrelationId = Guid.NewGuid(),
                    Request = new PipeRequest
                    {
                        Command = PipeCommand.SubscribeNotifications
                    }
                };

                // Keep one background subscription open so the service can push
                // approval prompts immediately after it quarantines a new item.
                await writer.WriteLineAsync(JsonSerializer.Serialize(subscriptionEnvelope, JsonOptions)).WaitAsync(cancellationToken);

                var ackLine = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (ackLine is null)
                {
                    throw new InvalidOperationException("The backend pipe closed before acknowledging the notification subscription.");
                }

                var ackEnvelope = JsonSerializer.Deserialize<PipeEnvelope>(ackLine, JsonOptions);
                if (ackEnvelope?.Response is not { Success: true })
                {
                    throw new InvalidOperationException(ackEnvelope?.Response?.Message ?? "The backend rejected the notification subscription.");
                }

                _isConnected = true;
                FrontendDebugLog.Info("NamedPipeBackendClient", "Notification subscription connected.");

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    var envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
                    if (envelope?.MessageType == PipeMessageType.Notification && envelope.Notification is not null)
                    {
                        NotificationReceived?.Invoke(envelope.Notification);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (TimeoutException ex)
            {
                FrontendDebugLog.Info("NamedPipeBackendClient", $"NotificationLoopAsync timed out while waiting for backend: {ex.Message}");
            }
            catch (Exception ex)
            {
                FrontendDebugLog.Error("NamedPipeBackendClient", ex, "NotificationLoopAsync failed.");
            }
            finally
            {
                _isConnected = false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async ValueTask DisposeNotificationLoopAsync(CancellationTokenSource? notificationLoopCts, Task? notificationLoopTask)
    {
        try
        {
            if (notificationLoopTask is not null)
            {
                await notificationLoopTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            notificationLoopCts?.Dispose();
        }
    }
}
