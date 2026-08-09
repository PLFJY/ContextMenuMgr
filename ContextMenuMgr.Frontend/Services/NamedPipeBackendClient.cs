using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
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
    private bool _disposed;

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
        string cultureName,
        CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetEnhanceMenuItemEnabled,
                ScopeValue = groupRegistryPath,
                DefinitionXml = itemXml,
                Enable = enable,
                CultureName = cultureName
            },
            cancellationToken);
    }

    /// <summary>
    /// Sets detailed edit rule value Async.
    /// </summary>
    public async Task SetDetailedEditRuleValueAsync(
        string storageKind,
        string path,
        string? section,
        string keyName,
        string valueKind,
        string? value,
        string? userSid,
        CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetDetailedEditRuleValue,
                RuleStorageKind = storageKind,
                RulePath = path,
                RuleSection = section,
                RuleKeyName = keyName,
                RuleValueKind = valueKind,
                RuleValue = value,
                RuleUserSid = userSid
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
    public async Task<ContextMenuEntry?> SetEnabledAsync(
        string itemId,
        bool enable,
        CancellationToken cancellationToken,
        ContextMenuEntry? item = null)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetEnabled,
                ItemId = itemId,
                Enable = enable,
                Item = item
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

    public async Task<ContextMenuEntry?> SetCommandTextAsync(
        string itemId,
        string commandText,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetCommandText,
                ItemId = itemId,
                TextValue = commandText
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
    public async Task<ContextMenuEntry?> DeleteItemAsync(
        string itemId,
        CancellationToken cancellationToken,
        ContextMenuEntry? item = null)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.DeleteItem,
                ItemId = itemId,
                Item = item
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

    public async Task<IReadOnlyList<SpecialMenuEntry>> GetSpecialMenuSnapshotAsync(
        SpecialMenuKind kind,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.GetSpecialMenuSnapshot,
                SpecialKind = kind
            },
            cancellationToken);

        return response.SpecialItems;
    }

    public async Task<SpecialMenuEntry?> SetSpecialMenuItemEnabledAsync(
        SpecialMenuEntry item,
        bool enable,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetSpecialMenuItemEnabled,
                SpecialItem = item,
                Enable = enable,
                ClientOperationId = clientOperationId
            },
            cancellationToken);

        return response.SpecialItem;
    }

    public async Task<SpecialMenuEntry?> CreateSpecialMenuItemAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(request with { Command = PipeCommand.CreateSpecialMenuItem }, cancellationToken);
        return response.SpecialItem;
    }

    public async Task<SpecialMenuEntry?> UpdateSpecialMenuItemAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(request with { Command = PipeCommand.UpdateSpecialMenuItem }, cancellationToken);
        return response.SpecialItem;
    }

    public async Task<SpecialMenuEntry?> DeleteSpecialMenuItemAsync(SpecialMenuEntry item, Guid clientOperationId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.DeleteSpecialMenuItem,
                SpecialKind = item.Kind,
                SpecialItem = item,
                ClientOperationId = clientOperationId
            },
            cancellationToken);
        return response?.SpecialItem;
    }

    public async Task<SpecialMenuEntry?> UndoDeleteSpecialMenuItemAsync(SpecialMenuEntry item, Guid clientOperationId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.UndoSpecialMenuItem,
                SpecialKind = item.Kind,
                SpecialItem = item,
                ClientOperationId = clientOperationId
            },
            cancellationToken);
        return response?.SpecialItem;
    }

    public async Task PurgeDeletedSpecialMenuItemAsync(SpecialMenuEntry item, Guid clientOperationId, CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.PurgeSpecialMenuItem,
                SpecialKind = item.Kind,
                SpecialItem = item,
                ClientOperationId = clientOperationId
            },
            cancellationToken);
    }

    public async Task<SpecialMenuEntry?> MoveSpecialMenuItemAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(request with { Command = PipeCommand.MoveSpecialMenuItem }, cancellationToken);
        return response.SpecialItem;
    }

    public async Task RestoreSpecialMenuDefaultsAsync(
        SpecialMenuKind kind,
        string? scopeValue,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.RestoreSpecialMenuDefaults,
                SpecialKind = kind,
                ScopeValue = scopeValue,
                ClientOperationId = clientOperationId
            },
            cancellationToken);
    }

    public async Task SetShellNewOrderLockAsync(bool locked, Guid clientOperationId, CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetShellNewOrderLock,
                ShellNewLock = new ShellNewLockRequest(locked),
                ClientOperationId = clientOperationId
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<FileTypeAnalysisResult>> AnalyzeFileTypeContextAsync(string path, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.AnalyzeFileTypeContext,
                FileTypeAnalysis = new FileTypeAnalysisRequest(path)
            },
            cancellationToken);

        return response.FileTypeAnalysisResults;
    }

    public async Task<ContextMenuEntry?> CreateSceneMenuItemAsync(
        CreateSceneMenuItemRequest request,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.CreateSceneMenuItem,
                CreateSceneMenuItem = request,
                ClientOperationId = clientOperationId
            },
            cancellationToken);

        return response.Item;
    }

    public async Task<IReadOnlyList<ContextMenuEntry>> FindRelatedFileTypeMenuItemsAsync(
        FileTypeBatchQuery query,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.FindRelatedFileTypeMenuItems,
                FileTypeBatchQuery = query
            },
            cancellationToken);

        return response.Items;
    }

    /// <summary>
    /// Restarts explorer Async.
    /// </summary>
    public async Task RestartExplorerAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest { Command = PipeCommand.RestartExplorer },
            cancellationToken);
    }

    public async Task RepairRuntimeDataAclAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest { Command = PipeCommand.RepairRuntimeDataAcl },
            cancellationToken);
    }

    /// <summary>
    /// Sets Win11 blocked item Async.
    /// </summary>
    public async Task SetWin11BlockedItemAsync(
        string handlerClsid,
        string displayName,
        bool blockMachine,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetWin11BlockedItem,
                ItemId = handlerClsid,
                DisplayName = displayName,
                BlockMachine = blockMachine,
                ClientOperationId = clientOperationId
            },
            cancellationToken);
    }

    /// <summary>
    /// Removes Win11 blocked item Async.
    /// </summary>
    public async Task RemoveWin11BlockedItemAsync(
        string handlerClsid,
        bool unblockMachine,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.RemoveWin11BlockedItem,
                ItemId = handlerClsid,
                UnblockMachine = unblockMachine,
                ClientOperationId = clientOperationId
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets Win11 blocked items Async.
    /// </summary>
    public async Task<IReadOnlyList<Win11BlockedItem>> GetWin11BlockedItemsAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.GetWin11BlockedItems
            },
            cancellationToken);

        return response.Win11BlockedItems;
    }

    /// <summary>
    /// Gets Win11 context menu snapshot Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> GetWin11ContextMenuSnapshotAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.GetWin11ContextMenuSnapshot
            },
            cancellationToken);

        return response.Items;
    }

    public async Task<bool> GetWin11ModernContextMenuDisabledAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.GetWin11ModernContextMenuDisabled
            },
            cancellationToken);

        return response.Win11ModernContextMenuDisabled ?? false;
    }

    public async Task SetWin11ModernContextMenuDisabledAsync(bool disabled, CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetWin11ModernContextMenuDisabled,
                Enable = disabled
            },
            cancellationToken);
    }

    /// <summary>
    /// Sets auto start enabled Async.
    /// </summary>
    public async Task SetAutoStartEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken,
        bool? showTrayIcon = null)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetAutoStartEnabled,
                AutoStartEnabled = enabled,
                ShowTrayIcon = showTrayIcon
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets auto start enabled Async.
    /// </summary>
    public async Task<bool> GetAutoStartEnabledAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.GetAutoStartEnabled
            },
            cancellationToken);

        return response.AutoStartEnabled ?? false;
    }

    public async Task SetTrayIconPolicyAsync(bool showTrayIcon, CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetTrayIconPolicy,
                ShowTrayIcon = showTrayIcon
            },
            cancellationToken);
    }

    public async Task SetLogLevelAsync(RuntimeLogLevel logLevel, CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetLogLevel,
                LogLevel = logLevel
            },
            cancellationToken);
    }

    public async Task<OfficeSuiteCoexistenceStatus?> GetOfficeSuiteCoexistenceStatusAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest { Command = PipeCommand.GetOfficeSuiteCoexistenceStatus },
            cancellationToken);
        return response.OfficeSuiteCoexistence;
    }

    public async Task<IReadOnlyList<ContextMenuEntry>> GetWpsOfficePendingApprovalsAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest { Command = PipeCommand.GetWpsOfficePendingApprovals },
            cancellationToken);
        return response.Items;
    }

    public async Task<OfficeSuiteCoexistenceStatus?> SetDocumentIconProviderAsync(
        DocumentIconProvider provider,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetDocumentIconProvider,
                DocumentIconProvider = provider
            },
            cancellationToken);
        return response.OfficeSuiteCoexistence;
    }

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
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

    private async Task<PipeResponse> SendRequestAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new OperationCanceledException("The backend client is shutting down.");
        }

        await _sendLock.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var correlationId = Guid.Empty;
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

            correlationId = Guid.NewGuid();
            var envelope = new PipeEnvelope
            {
                MessageType = PipeMessageType.Request,
                CorrelationId = correlationId,
                Request = request
            };

            FrontendDebugLog.Operation(
                "FrontendOperation",
                BuildOperationStartLog(correlationId, request));

            var payload = JsonSerializer.Serialize(envelope, JsonOptions);
            await writer.WriteLineAsync(payload).WaitAsync(cancellationToken);

            while (true)
            {
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (line is null)
                {
                    throw new InvalidOperationException("The backend pipe closed before returning a response.");
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
                    if (responseEnvelope.Notification.ClientOperationId != request.ClientOperationId)
                    {
                        NotificationReceived?.Invoke(this, responseEnvelope.Notification);
                    }
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
                    FrontendDebugLog.Operation(
                        "FrontendOperation",
                        BuildOperationEndLog(correlationId, request, responseEnvelope.Response, stopwatch.ElapsedMilliseconds));
                    throw new BackendRequestException(responseEnvelope.Response.Message, responseEnvelope.Response.ErrorCode);
                }

                stopwatch.Stop();
                FrontendDebugLog.Operation(
                    "FrontendOperation",
                    BuildOperationEndLog(correlationId, request, responseEnvelope.Response, stopwatch.ElapsedMilliseconds));
                FrontendDebugLog.Info("NamedPipeBackendClient", $"SendRequestAsync <- {request.Command} succeeded.");
                return responseEnvelope.Response;
            }
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error=Timeout, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            FrontendDebugLog.Warning("NamedPipeBackendClient", $"SendRequestAsync timed out for {request.Command}: {ex.Message}");
            throw;
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error=Canceled, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            FrontendDebugLog.Info(
                "NamedPipeBackendClient",
                $"SendRequestAsync canceled for {request.Command}. Disposed={_disposed}, CancellationRequested={cancellationToken.IsCancellationRequested}, Message={ex.Message}");
            throw;
        }
        catch (ObjectDisposedException ex) when (_disposed || cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error=Disposed, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            FrontendDebugLog.Info(
                "NamedPipeBackendClient",
                $"SendRequestAsync disposed while {request.Command} was in flight. Disposed={_disposed}, CancellationRequested={cancellationToken.IsCancellationRequested}, Message={ex.Message}");
            throw;
        }
        catch (IOException ex) when (_disposed || cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success=false, Error=PipeClosedDuringCancellation, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
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
        finally
        {
            _sendLock.Release();
        }
    }

    private static string BuildOperationStartLog(Guid correlationId, PipeRequest request)
        => $"FrontendOperationStart: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, ItemId={request.ItemId}, SpecialKind={request.SpecialKind}, SceneKind={request.SceneKind}, Enable={request.Enable}, AutoStartEnabled={request.AutoStartEnabled}, ShowTrayIcon={request.ShowTrayIcon}, Timestamp={DateTimeOffset.UtcNow:O}.";

    private static string BuildOperationEndLog(Guid correlationId, PipeRequest request, PipeResponse response, long elapsedMs)
        => $"FrontendOperationEnd: CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success={response.Success}, ErrorCode={response.ErrorCode ?? "<none>"}, Message={response.Message}, ElapsedMs={elapsedMs}, HasItem={response.Item is not null}, HasSpecialItem={response.SpecialItem is not null}.";

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
