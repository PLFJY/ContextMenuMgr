using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the named Pipe Backend Client.
/// </summary>
public sealed class NamedPipeBackendClient : IBackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _notificationSync = new();
    private CancellationTokenSource? _notificationLoopCts;
    private Task? _notificationLoopTask;
    private bool _isConnected;

    public event EventHandler<BackendNotification>? NotificationReceived;

    public bool IsConnected => _isConnected;

    /// <summary>
    /// Executes connect Async.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
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

        await PingAsync(cancellationToken);
    }

    /// <summary>
    /// Executes ping Async.
    /// </summary>
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest { Command = PipeCommand.Ping },
            cancellationToken);
    }

    /// <summary>
    /// Ensures tray Host Async.
    /// </summary>
    public async Task EnsureTrayHostAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest { Command = PipeCommand.EnsureTrayHost },
            cancellationToken);
    }

    /// <summary>
    /// Executes request Shutdown Async.
    /// </summary>
    public async Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest { Command = PipeCommand.RequestShutdown },
            cancellationToken);
    }

    /// <summary>
    /// Gets snapshot Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest { Command = PipeCommand.GetSnapshot },
            cancellationToken);

        return response.Items;
    }

    /// <summary>
    /// Gets scene Snapshot Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> GetSceneSnapshotAsync(
        ContextMenuSceneKind sceneKind,
        string? scopeValue,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.GetSceneSnapshot,
                SceneKind = sceneKind,
                ScopeValue = scopeValue
            },
            cancellationToken);

        return response.Items;
    }

    /// <summary>
    /// Sets enhance Menu Item Enabled Async.
    /// </summary>
    public async Task SetEnhanceMenuItemEnabledAsync(
        string groupRegistryPath,
        string itemXml,
        bool enable,
        CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetEnhanceMenuItemEnabled,
                ScopeValue = groupRegistryPath,
                DefinitionXml = itemXml,
                Enable = enable
            },
            cancellationToken);
    }

    /// <summary>
    /// Executes acknowledge Item State Async.
    /// </summary>
    public async Task<ContextMenuEntry?> AcknowledgeItemStateAsync(string itemId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.AcknowledgeItemState,
                ItemId = itemId
            },
            cancellationToken);

        return response.Item;
    }

    /// <summary>
    /// Sets enabled Async.
    /// </summary>
    public async Task<ContextMenuEntry?> SetEnabledAsync(string itemId, bool enable, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetEnabled,
                ItemId = itemId,
                Enable = enable
            },
            cancellationToken);

        return response.Item;
    }

    /// <summary>
    /// Sets shell Attribute Async.
    /// </summary>
    public async Task<ContextMenuEntry?> SetShellAttributeAsync(
        string itemId,
        ContextMenuShellAttribute attribute,
        bool enable,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetShellAttribute,
                ItemId = itemId,
                ShellAttribute = attribute,
                Enable = enable
            },
            cancellationToken);

        return response.Item;
    }

    /// <summary>
    /// Sets display Text Async.
    /// </summary>
    public async Task<ContextMenuEntry?> SetDisplayTextAsync(
        string itemId,
        string textValue,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetDisplayText,
                ItemId = itemId,
                TextValue = textValue
            },
            cancellationToken);

        return response.Item;
    }

    /// <summary>
    /// Gets registry Protection Setting Async.
    /// </summary>
    public async Task<bool> GetRegistryProtectionSettingAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.GetRegistryProtectionSetting
            },
            cancellationToken);

        return response.RegistryProtectionEnabled ?? false;
    }

    /// <summary>
    /// Sets registry Protection Setting Async.
    /// </summary>
    public async Task<bool> SetRegistryProtectionSettingAsync(bool enable, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetRegistryProtectionSetting,
                Enable = enable
            },
            cancellationToken);

        return response.RegistryProtectionEnabled ?? enable;
    }

    /// <summary>
    /// Applies decision Async.
    /// </summary>
    public async Task<ContextMenuEntry?> ApplyDecisionAsync(string itemId, ContextMenuDecision decision, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.ApplyDecision,
                ItemId = itemId,
                Decision = decision
            },
            cancellationToken);

        return response.Item;
    }

    /// <summary>
    /// Deletes item Async.
    /// </summary>
    public async Task<ContextMenuEntry?> DeleteItemAsync(string itemId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.DeleteItem,
                ItemId = itemId
            },
            cancellationToken);

        return response.Item;
    }

    /// <summary>
    /// Executes undo Delete Async.
    /// </summary>
    public async Task<ContextMenuEntry?> UndoDeleteAsync(string itemId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.UndoDelete,
                ItemId = itemId
            },
            cancellationToken);

        return response.Item;
    }

    /// <summary>
    /// Executes purge Deleted Item Async.
    /// </summary>
    public async Task PurgeDeletedItemAsync(string itemId, CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.PurgeDeletedItem,
                ItemId = itemId
            },
            cancellationToken);
    }

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public ValueTask DisposeAsync()
    {
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
        _sendLock.Dispose();
        return notificationLoopTask is null
            ? DisposeNotificationLoopAsync(notificationLoopCts, null)
            : DisposeNotificationLoopAsync(notificationLoopCts, notificationLoopTask);
    }

    private async Task<PipeResponse> SendRequestAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        FrontendDebugLog.Info("NamedPipeBackendClient", $"SendRequestAsync -> {request.Command}, ItemId={request.ItemId}");

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            using var stream = new NamedPipeClientStream(".", PipeConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            FrontendDebugLog.Info("NamedPipeBackendClient", $"Connecting one-shot pipe for {request.Command}.");
            await stream.ConnectAsync(2000, cancellationToken);
            stream.ReadMode = PipeTransmissionMode.Byte;
            FrontendDebugLog.Info("NamedPipeBackendClient", $"Connected one-shot pipe for {request.Command}.");

            using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true
            };

            var envelope = new PipeEnvelope
            {
                MessageType = PipeMessageType.Request,
                CorrelationId = Guid.NewGuid(),
                Request = request
            };

            var payload = JsonSerializer.Serialize(envelope, JsonOptions);
            FrontendDebugLog.Info("NamedPipeBackendClient", $"Writing request payload for {request.Command}.");
            await writer.WriteLineAsync(payload).WaitAsync(cancellationToken);
            FrontendDebugLog.Info("NamedPipeBackendClient", $"Request payload written for {request.Command}.");

            while (true)
            {
                FrontendDebugLog.Info("NamedPipeBackendClient", $"Waiting for response line for {request.Command}.");
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (line is null)
                {
                    throw new InvalidOperationException("The backend pipe closed before returning a response.");
                }

                FrontendDebugLog.Info("NamedPipeBackendClient", $"Received line for {request.Command}. Length={line.Length}");
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
                    NotificationReceived?.Invoke(this, responseEnvelope.Notification);
                    continue;
                }

                if (responseEnvelope.MessageType != PipeMessageType.Response || responseEnvelope.Response is null)
                {
                    continue;
                }

                if (!responseEnvelope.Response.Success)
                {
                    FrontendDebugLog.Info(
                        "NamedPipeBackendClient",
                        $"SendRequestAsync <- {request.Command} failed. Message={responseEnvelope.Response.Message}");
                    throw new InvalidOperationException(responseEnvelope.Response.Message);
                }

                FrontendDebugLog.Info("NamedPipeBackendClient", $"SendRequestAsync <- {request.Command} succeeded.");
                return responseEnvelope.Response;
            }
        }
        catch (TimeoutException ex)
        {
            FrontendDebugLog.Info("NamedPipeBackendClient", $"SendRequestAsync timed out for {request.Command}: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("NamedPipeBackendClient", ex, $"SendRequestAsync failed for {request.Command}.");
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task NotificationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var stream = new NamedPipeClientStream(".", PipeConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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
                        NotificationReceived?.Invoke(this, envelope.Notification);
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
