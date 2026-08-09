﻿﻿﻿using System.Collections.Concurrent;
using System.Diagnostics;
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
/// <summary>
/// Represents the named Pipe Backend Server.
/// </summary>
public sealed class NamedPipeBackendServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ContextMenuRegistryCatalog _catalog;
    private readonly SpecialMenuService _specialMenuService;
    private readonly Windows11BlocksService _windows11BlocksService;
    private readonly Win11ClassicContextMenuService _win11ClassicContextMenuService;
    private readonly AutoStartService _autoStartService;
    private readonly FileTypeSceneMenuService _fileTypeSceneMenuService;
    private readonly ExplorerRestartService _explorerRestartService;
    private readonly FileLogger _logger;
    private readonly ConcurrentDictionary<Guid, PipeClientConnection> _clients = new();
    private CancellationTokenSource? _acceptLoopCts;
    private Task? _acceptLoopTask;

    public event EventHandler? BackendShutdownRequested;
    public event EventHandler? EnsureTrayHostRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeBackendServer"/> class.
    /// </summary>
    public NamedPipeBackendServer(
        ContextMenuRegistryCatalog catalog,
        SpecialMenuService specialMenuService,
        Windows11BlocksService windows11BlocksService,
        Win11ClassicContextMenuService win11ClassicContextMenuService,
        AutoStartService autoStartService,
        FileTypeSceneMenuService fileTypeSceneMenuService,
        ExplorerRestartService explorerRestartService,
        FileLogger logger)
    {
        _catalog = catalog;
        _specialMenuService = specialMenuService;
        _windows11BlocksService = windows11BlocksService;
        _win11ClassicContextMenuService = win11ClassicContextMenuService;
        _autoStartService = autoStartService;
        _fileTypeSceneMenuService = fileTypeSceneMenuService;
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
    public void Stop()
    {
        _acceptLoopCts?.Cancel();
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
        _clients[connection.Id] = connection;
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

                PipeResponse response;
                var stopwatch = Stopwatch.StartNew();
                await _logger.LogAsync(BuildRequestStartLog(connection.Id, envelope.CorrelationId, envelope.Request), cancellationToken);
                await _logger.LogOperationAsync(BuildOperationStartLog(connection.Id, envelope.CorrelationId, envelope.Request), cancellationToken);
                try
                {
                    // Request handlers are allowed to fail independently; the pipe
                    // stays alive and the caller receives a structured error response.
                    response = await HandleRequestAsync(envelope.Request, stream, cancellationToken);
                }
                catch (Exception ex)
                {
                    await _logger.LogAsync(
                        RuntimeLogLevel.Error,
                        $"PipeRequestException: ConnectionId={connection.Id}, CorrelationId={envelope.CorrelationId}, Command={envelope.Request.Command}, ClientOperationId={envelope.Request.ClientOperationId}, Exception={ex}",
                        cancellationToken);
                    response = new PipeResponse
                    {
                        Success = false,
                        Message = ex.Message
                    };
                }
                finally
                {
                    stopwatch.Stop();
                }

                await _logger.LogAsync(BuildRequestEndLog(connection.Id, envelope.CorrelationId, envelope.Request, response, stopwatch.ElapsedMilliseconds), cancellationToken);
                await _logger.LogOperationAsync(BuildOperationEndLog(connection.Id, envelope.CorrelationId, envelope.Request, response, stopwatch.ElapsedMilliseconds), cancellationToken);

                await connection.SendAsync(
                    new PipeEnvelope
                    {
                        MessageType = PipeMessageType.Response,
                        CorrelationId = envelope.CorrelationId,
                        Response = response
                    },
                    cancellationToken);
                if (!response.Success)
                {
                    await _logger.LogAsync(
                        RuntimeLogLevel.Warning,
                        $"Pipe request {envelope.Request.Command} returned failure for {connection.Id}: {response.Message}",
                        cancellationToken);
                }

                if (response.Success && response.Item is not null)
                {
                    // Successful state-changing requests are rebroadcast so other
                    // connected surfaces can update without polling.
                    await BroadcastNotificationAsync(
                        new BackendNotification
                        {
                            Kind = PipeNotificationKind.ItemStateChanged,
                            Item = response.Item,
                            Message = response.Message,
                            ClientOperationId = response.ClientOperationId,
                            Timestamp = DateTimeOffset.UtcNow
                        },
                        cancellationToken);
                }

                if (response.Success && response.SpecialItem is not null)
                {
                    await BroadcastNotificationAsync(
                        new BackendNotification
                        {
                            Kind = PipeNotificationKind.ItemStateChanged,
                            SpecialKind = response.SpecialItem.Kind,
                            SpecialItem = response.SpecialItem,
                            Message = response.Message,
                            ClientOperationId = response.ClientOperationId,
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

    private async Task<PipeResponse> HandleRequestAsync(PipeRequest request, NamedPipeServerStream stream, CancellationToken cancellationToken)
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
            PipeCommand.SetLogLevel when request.LogLevel is not null
                => HandleSetLogLevelRequest(request.LogLevel.Value),
            PipeCommand.GetSnapshot => new PipeResponse
            {
                Success = true,
                Message = "Snapshot loaded.",
                Items = await _catalog.GetSnapshotAsync(
                    cancellationToken,
                    await ResolveFrontendUserContextAsync(stream, cancellationToken))
            },
            PipeCommand.GetSceneSnapshot when request.SceneKind is not null
                => new PipeResponse
                {
                    Success = true,
                    Message = "Scene snapshot loaded.",
                    Items = await _catalog.GetSceneSnapshotAsync(
                        request.SceneKind.Value,
                        request.ScopeValue,
                        cancellationToken,
                        await ResolveFrontendUserContextAsync(stream, cancellationToken))
                },
            PipeCommand.SetEnhanceMenuItemEnabled when request.ScopeValue is not null && request.DefinitionXml is not null && request.Enable is not null
                => await HandleSetEnhanceMenuItemEnabledAsync(request, stream, cancellationToken),
            PipeCommand.SetDetailedEditRuleValue
                => await _catalog.SetDetailedEditRuleValueAsync(
                    request.RuleStorageKind,
                    request.RulePath,
                    request.RuleSection,
                    request.RuleKeyName,
                    request.RuleValueKind,
                    request.RuleValue,
                    request.RuleUserSid,
                    cancellationToken),
            PipeCommand.AcknowledgeItemState when request.ItemId is not null
                => await _catalog.AcknowledgeItemStateAsync(request.ItemId, cancellationToken),
            PipeCommand.SetEnabled when request.ItemId is not null && request.Enable is not null
                => await HandleSetEnabledAsync(request, stream, cancellationToken),
            PipeCommand.SetShellAttribute when request.ItemId is not null && request.Enable is not null && request.ShellAttribute is not null
                => await _catalog.ApplyShellAttributeAsync(request.ItemId, request.ShellAttribute.Value, request.Enable.Value, cancellationToken),
            PipeCommand.SetDisplayText when request.ItemId is not null && request.TextValue is not null
                => await _catalog.ApplyDisplayTextAsync(request.ItemId, request.TextValue, cancellationToken),
            PipeCommand.SetCommandText when request.ItemId is not null && request.TextValue is not null
                => await _catalog.ApplyCommandTextAsync(request.ItemId, request.TextValue, cancellationToken),
            PipeCommand.GetRegistryProtectionSetting
                => await _catalog.GetRegistryProtectionSettingAsync(cancellationToken),
            PipeCommand.SetRegistryProtectionSetting when request.Enable is not null
                => await _catalog.SetRegistryProtectionSettingAsync(
                    request.Enable.Value,
                    await ResolveFrontendUserContextAsync(stream, cancellationToken),
                    cancellationToken),
            PipeCommand.ApplyDecision when request.ItemId is not null && request.Decision is not null
                => await HandleApplyDecisionAsync(request, stream, cancellationToken),
            PipeCommand.DeleteItem when request.ItemId is not null
                => await _catalog.DeleteItemAsync(
                    request.ItemId,
                    cancellationToken,
                    await ResolveFrontendUserContextAsync(stream, cancellationToken),
                    request.Item),
            PipeCommand.UndoDelete when request.ItemId is not null
                => await _catalog.UndoDeleteAsync(
                    request.ItemId,
                    cancellationToken,
                    await ResolveFrontendUserContextAsync(stream, cancellationToken)),
            PipeCommand.PurgeDeletedItem when request.ItemId is not null
                => await _catalog.PurgeDeletedItemAsync(request.ItemId, cancellationToken),
            PipeCommand.GetSpecialMenuSnapshot when request.SpecialKind is not null
                => new PipeResponse
                {
                    Success = true,
                    Message = "Special menu snapshot loaded.",
                    SpecialItems = await _specialMenuService.GetSnapshotAsync(
                        request.SpecialKind.Value,
                        await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                        cancellationToken)
                },
            PipeCommand.SetSpecialMenuItemEnabled when request.SpecialItem is not null && request.Enable is not null
                => await _specialMenuService.SetEnabledAsync(
                    request.SpecialItem,
                    request.Enable.Value,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.CreateSpecialMenuItem
                => await _specialMenuService.CreateAsync(
                    request,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.UpdateSpecialMenuItem
                => await _specialMenuService.UpdateAsync(
                    request,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.DeleteSpecialMenuItem when request.SpecialItem is not null
                => await _specialMenuService.DeleteAsync(
                    request.SpecialItem,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.UndoSpecialMenuItem when request.SpecialItem is not null
                => await _specialMenuService.UndoDeleteAsync(
                    request.SpecialItem,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.PurgeSpecialMenuItem when request.SpecialItem is not null
                => await _specialMenuService.PurgeDeletedAsync(
                    request.SpecialItem,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.MoveSpecialMenuItem
                => await _specialMenuService.MoveAsync(
                    request,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.RestoreSpecialMenuDefaults when request.SpecialKind is not null
                => await _specialMenuService.RestoreDefaultsAsync(
                    request.SpecialKind.Value,
                    request.ScopeValue,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.SetShellNewOrderLock when request.ShellNewLock is not null
                => await _specialMenuService.SetShellNewOrderLockAsync(
                    request.ShellNewLock.Lock,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, stream, cancellationToken),
                    cancellationToken),
            PipeCommand.AnalyzeFileTypeContext when request.FileTypeAnalysis is not null
                => new PipeResponse
                {
                    Success = true,
                    Message = "File type context analyzed.",
                    FileTypeAnalysisResults = await _fileTypeSceneMenuService.AnalyzeAsync(request.FileTypeAnalysis, cancellationToken)
                },
            PipeCommand.CreateSceneMenuItem when request.CreateSceneMenuItem is not null
                => await _fileTypeSceneMenuService.CreateSceneMenuItemAsync(
                    request.CreateSceneMenuItem,
                    request.ClientOperationId,
                    cancellationToken),
            PipeCommand.FindRelatedFileTypeMenuItems when request.FileTypeBatchQuery is not null
                => new PipeResponse
                {
                    Success = true,
                    Message = "Related file-type menu items loaded.",
                    Items = await _catalog.FindRelatedFileTypeMenuItemsAsync(
                        request.FileTypeBatchQuery,
                        cancellationToken,
                        await ResolveFrontendUserContextAsync(stream, cancellationToken))
                },
            PipeCommand.RestartExplorer
                => await HandleRestartExplorerRequestAsync(stream, cancellationToken),
            PipeCommand.RepairRuntimeDataAcl
                => await HandleRepairRuntimeDataAclAsync(cancellationToken),
            PipeCommand.SetWin11BlockedItem when request.ItemId is not null
                => await HandleSetWin11BlockedItemAsync(request, stream, cancellationToken),
            PipeCommand.RemoveWin11BlockedItem when request.ItemId is not null
                => await HandleRemoveWin11BlockedItemAsync(request, stream, cancellationToken),
            PipeCommand.GetWin11BlockedItems
                => await HandleGetWin11BlockedItemsAsync(request, stream, cancellationToken),
            PipeCommand.GetWin11ContextMenuSnapshot
                => await HandleGetWin11ContextMenuSnapshotAsync(request, stream, cancellationToken),
            PipeCommand.GetWin11ModernContextMenuDisabled
                => await HandleGetWin11ModernContextMenuDisabledAsync(request, stream, cancellationToken),
            PipeCommand.SetWin11ModernContextMenuDisabled when request.Enable is not null
                => await HandleSetWin11ModernContextMenuDisabledAsync(request, stream, cancellationToken),
            PipeCommand.SetAutoStartEnabled when request.AutoStartEnabled is not null
                => await _autoStartService.SetAutoStartEnabledAsync(
                    request.AutoStartEnabled.Value,
                    request.ClientOperationId,
                    await ResolveFrontendUserContextAsync(stream, cancellationToken),
                    cancellationToken,
                    request.ShowTrayIcon),
            PipeCommand.GetAutoStartEnabled
                => await _autoStartService.GetAutoStartEnabledAsync(
                    request.ClientOperationId,
                    await ResolveFrontendUserContextAsync(stream, cancellationToken),
                    cancellationToken),
            PipeCommand.SetTrayIconPolicy when request.ShowTrayIcon is not null
                => await _autoStartService.SetTrayIconPolicyAsync(
                    request.ShowTrayIcon.Value,
                    request.ClientOperationId,
                    await ResolveFrontendUserContextAsync(stream, cancellationToken),
                    cancellationToken),
            PipeCommand.GetOfficeSuiteCoexistenceStatus
                => new PipeResponse
                {
                    Success = true,
                    Message = "Office suite co-existence status loaded.",
                    OfficeSuiteCoexistence = _catalog.GetOfficeSuiteCoexistenceStatus(
                        await ResolveFrontendUserContextAsync(stream, cancellationToken))
                },
            PipeCommand.GetWpsOfficePendingApprovals
                => new PipeResponse
                {
                    Success = true,
                    Message = "WPS Office pending approvals loaded.",
                    Items = await _catalog.GetWpsOfficePendingApprovalsAsync(
                        cancellationToken,
                        await ResolveFrontendUserContextAsync(stream, cancellationToken))
                },
            PipeCommand.SetDocumentIconProvider when request.DocumentIconProvider is not null
                => await HandleSetDocumentIconProviderAsync(request.DocumentIconProvider.Value, stream, cancellationToken),
            _ => new PipeResponse
            {
                Success = false,
                Message = "The request was missing required data."
            }
        };
    }

    private async Task<PipeResponse> HandleSetEnabledAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        return await _catalog.ApplyDesiredStateAsync(
            request.ItemId ?? string.Empty,
            request.Enable.GetValueOrDefault(),
            cancellationToken,
            userContext,
            request.Item);
    }

    private async Task<PipeResponse> HandleSetDocumentIconProviderAsync(
        DocumentIconProvider provider,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        if (userContext is null)
        {
            return new PipeResponse
            {
                Success = false,
                Message = "Cannot change document icons: frontend user context is not available."
            };
        }

        return _catalog.SetDocumentIconProvider(userContext, provider);
    }

    private async Task<PipeResponse> HandleSetEnhanceMenuItemEnabledAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        return await _catalog.SetEnhanceMenuItemEnabledAsync(
            request.ScopeValue ?? string.Empty,
            request.DefinitionXml ?? string.Empty,
            request.Enable.GetValueOrDefault(),
            request.CultureName,
            userContext!,
            cancellationToken);
    }

    private async Task<PipeResponse> HandleApplyDecisionAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        return await _catalog.ApplyDecisionAsync(
            request.ItemId ?? string.Empty,
            request.Decision.GetValueOrDefault(),
            cancellationToken,
            userContext);
    }

    private async Task<PipeResponse> HandleSetWin11BlockedItemAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        var handlerClsid = request.ItemId ?? string.Empty;
        var blockMachine = request.BlockMachine ?? false;
        if (IsWindows11SystemCommandKey(handlerClsid))
        {
            var systemResponse = await _catalog.SetWindows11SystemCommandEnabledAsync(
                handlerClsid,
                enable: false,
                request.ClientOperationId,
                cancellationToken);
            await LogWin11CommandAsync(
                PipeCommand.SetWin11BlockedItem,
                userContext,
                blockMachine,
                handlerClsid,
                systemResponse.Success ? "system-command-succeeded" : "system-command-failed",
                cancellationToken);
            return systemResponse;
        }

        await LogWin11CommandAsync(
            PipeCommand.SetWin11BlockedItem,
            userContext,
            blockMachine,
            handlerClsid,
            "started",
            cancellationToken);

        var response = await _windows11BlocksService.SetWin11BlockedItemAsync(
            handlerClsid,
            request.DisplayName ?? string.Empty,
            blockMachine,
            request.ClientOperationId,
            userContext,
            cancellationToken);

        await LogWin11CommandAsync(
            PipeCommand.SetWin11BlockedItem,
            userContext,
            blockMachine,
            handlerClsid,
            response.Success ? "succeeded" : "failed",
            cancellationToken);

        return response;
    }

    private async Task<PipeResponse> HandleRemoveWin11BlockedItemAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        var handlerClsid = request.ItemId ?? string.Empty;
        var unblockMachine = request.UnblockMachine ?? false;
        if (IsWindows11SystemCommandKey(handlerClsid))
        {
            var systemResponse = await _catalog.SetWindows11SystemCommandEnabledAsync(
                handlerClsid,
                enable: true,
                request.ClientOperationId,
                cancellationToken);
            await LogWin11CommandAsync(
                PipeCommand.RemoveWin11BlockedItem,
                userContext,
                unblockMachine,
                handlerClsid,
                systemResponse.Success ? "system-command-succeeded" : "system-command-failed",
                cancellationToken);
            return systemResponse;
        }

        await LogWin11CommandAsync(
            PipeCommand.RemoveWin11BlockedItem,
            userContext,
            unblockMachine,
            handlerClsid,
            "started",
            cancellationToken);

        var response = await _windows11BlocksService.RemoveWin11BlockedItemAsync(
            handlerClsid,
            unblockMachine,
            request.ClientOperationId,
            userContext,
            cancellationToken);

        await LogWin11CommandAsync(
            PipeCommand.RemoveWin11BlockedItem,
            userContext,
            unblockMachine,
            handlerClsid,
            response.Success ? "succeeded" : "failed",
            cancellationToken);

        return response;
    }

    private async Task<PipeResponse> HandleGetWin11BlockedItemsAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        await LogWin11CommandAsync(
            PipeCommand.GetWin11BlockedItems,
            userContext,
            machineScope: false,
            handlerClsid: null,
            result: "started",
            cancellationToken);

        var response = await _windows11BlocksService.GetWin11BlockedItemsAsync(
            request.ClientOperationId,
            userContext,
            cancellationToken);

        await LogWin11CommandAsync(
            PipeCommand.GetWin11BlockedItems,
            userContext,
            machineScope: false,
            handlerClsid: null,
            result: response.Success ? "succeeded" : "failed",
            cancellationToken);

        return response;
    }

    private async Task<PipeResponse> HandleGetWin11ContextMenuSnapshotAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        await LogWin11CommandAsync(
            PipeCommand.GetWin11ContextMenuSnapshot,
            userContext,
            machineScope: false,
            handlerClsid: null,
            result: "started",
            cancellationToken);

        var items = await _catalog.GetWindows11SnapshotAsync(cancellationToken, userContext);
        await LogWin11CommandAsync(
            PipeCommand.GetWin11ContextMenuSnapshot,
            userContext,
            machineScope: false,
            handlerClsid: null,
            result: "succeeded",
            cancellationToken);

        return new PipeResponse
        {
            Success = true,
            Message = "Win11 context menu snapshot loaded.",
            Items = items,
            ClientOperationId = request.ClientOperationId
        };
    }

    private async Task<PipeResponse> HandleGetWin11ModernContextMenuDisabledAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        await LogWin11CommandAsync(
            PipeCommand.GetWin11ModernContextMenuDisabled,
            userContext,
            machineScope: false,
            handlerClsid: null,
            result: "started",
            cancellationToken);

        var response = await _win11ClassicContextMenuService.GetDisabledAsync(
            request.ClientOperationId,
            userContext,
            cancellationToken);

        await LogWin11CommandAsync(
            PipeCommand.GetWin11ModernContextMenuDisabled,
            userContext,
            machineScope: false,
            handlerClsid: null,
            result: response.Success ? "succeeded" : "failed",
            cancellationToken);

        return response;
    }

    private async Task<PipeResponse> HandleSetWin11ModernContextMenuDisabledAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var userContext = await ResolveFrontendUserContextAsync(stream, cancellationToken);
        await LogWin11CommandAsync(
            PipeCommand.SetWin11ModernContextMenuDisabled,
            userContext,
            machineScope: false,
            handlerClsid: null,
            result: $"started, Disabled={request.Enable.GetValueOrDefault()}",
            cancellationToken);

        var response = await _win11ClassicContextMenuService.SetDisabledAsync(
            request.Enable.GetValueOrDefault(),
            request.ClientOperationId,
            userContext,
            cancellationToken);

        await LogWin11CommandAsync(
            PipeCommand.SetWin11ModernContextMenuDisabled,
            userContext,
            machineScope: false,
            handlerClsid: null,
            result: response.Success ? "succeeded" : "failed",
            cancellationToken);

        return response;
    }

    private async Task LogWin11CommandAsync(
        PipeCommand command,
        BackendUserContext? userContext,
        bool machineScope,
        string? handlerClsid,
        string result,
        CancellationToken cancellationToken)
    {
        var sid = string.IsNullOrWhiteSpace(userContext?.Sid) ? "<null>" : userContext.Sid;
        var normalizedClsid = string.IsNullOrWhiteSpace(handlerClsid) ? "<none>" : NormalizeGuid(handlerClsid);
        await _logger.LogAsync(
            $"Win11 command {command}: Sid={sid}, Machine={machineScope}, Clsid={normalizedClsid}, Result={result}.",
            cancellationToken);
    }

    private static bool IsWindows11SystemCommandKey(string? itemId) =>
        !string.IsNullOrWhiteSpace(itemId)
        && itemId.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeGuid(string guidText)
    {
        return Guid.TryParse(guidText, out var guid)
            ? guid.ToString("B")
            : guidText.Trim('{', '}');
    }

    private async Task<BackendUserContext?> ResolveSpecialMenuUserContextIfNeededAsync(
        PipeRequest request,
        NamedPipeServerStream stream,
        CancellationToken cancellationToken)
    {
        var kind = TryGetSpecialMenuKindForUserContext(request);
        if (kind is null || !RequiresSpecialMenuUserContext(kind.Value))
        {
            return null;
        }

        return await ResolveSpecialMenuUserContextAsync(stream, cancellationToken);
    }

    private static bool RequiresSpecialMenuUserContext(SpecialMenuKind kind)
    {
        return kind is SpecialMenuKind.ShellNew
            or SpecialMenuKind.SendTo
            or SpecialMenuKind.WinX
            or SpecialMenuKind.OpenWith;
    }

    private static SpecialMenuKind? TryGetSpecialMenuKindForUserContext(PipeRequest request)
    {
        if (request.SpecialItem is not null)
        {
            return request.SpecialItem.Kind;
        }

        if (request.SpecialKind is not null)
        {
            return request.SpecialKind.Value;
        }

        if (request.ShellNewCreate is not null
            || request.ShellNewUpdate is not null
            || request.ShellNewSort is not null
            || request.ShellNewLock is not null)
        {
            return SpecialMenuKind.ShellNew;
        }

        if (request.SendToCreate is not null
            || request.SendToUpdate is not null)
        {
            return SpecialMenuKind.SendTo;
        }

        if (request.WinXCreateGroup is not null
            || request.WinXCreateEntry is not null
            || request.WinXUpdateEntry is not null
            || request.WinXMove is not null)
        {
            return SpecialMenuKind.WinX;
        }

        if (request.OpenWithCreate is not null
            || request.OpenWithUpdate is not null)
        {
            return SpecialMenuKind.OpenWith;
        }

        return null;
    }

    private async Task<BackendUserContext?> ResolveFrontendUserContextAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var resolver = new BackendUserContextResolver(_logger);
        var userContext = resolver.TryResolveFromPipeClient(stream)
            ?? resolver.TryResolveInteractiveUserFallback();
        if (userContext is null)
        {
            await _logger.LogAsync(
                RuntimeLogLevel.Warning,
                "Frontend user context could not be resolved for user-scoped request.",
                cancellationToken);
        }

        return userContext;
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

    // Resolve only for context-aware special menus (ShellNew / SendTo / WinX).
    // Do not call this from ordinary catalog, Win11, registry protection,
    // auto-start, file-type scene, or restart routes.
    private async Task<BackendUserContext?> ResolveSpecialMenuUserContextAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var resolver = new BackendUserContextResolver(_logger);
        var userContext = resolver.TryResolveFromPipeClient(stream)
            ?? resolver.TryResolveInteractiveUserFallback();
        if (userContext is null)
        {
            await _logger.LogAsync(RuntimeLogLevel.Warning, "Context-aware special menu request could not resolve frontend user context.", cancellationToken);
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

        var subscribers = _clients.Values.Where(static connection => connection.IsNotificationSubscriber).ToList();
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
        => $"BackendOperationStart: ConnectionId={connectionId}, CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, ItemId={request.ItemId}, SpecialKind={request.SpecialKind}, SceneKind={request.SceneKind}, Enable={request.Enable}, Decision={request.Decision}, ShellAttribute={request.ShellAttribute}, AutoStartEnabled={request.AutoStartEnabled}, ShowTrayIcon={request.ShowTrayIcon}, Timestamp={DateTimeOffset.UtcNow:O}.";

    private static string BuildOperationEndLog(Guid connectionId, Guid correlationId, PipeRequest request, PipeResponse response, long elapsedMs)
        => $"BackendOperationEnd: ConnectionId={connectionId}, CorrelationId={correlationId}, Command={request.Command}, ClientOperationId={request.ClientOperationId}, Success={response.Success}, ErrorCode={response.ErrorCode ?? "<none>"}, Message={DiagnosticLogFormatter.FormatRegistryValueData(response.Message)}, ElapsedMs={elapsedMs}, ItemId={response.Item?.Id ?? request.ItemId}, SpecialItemId={response.SpecialItem?.Id}.";

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
