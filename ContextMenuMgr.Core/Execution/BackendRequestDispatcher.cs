using System.IO;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

internal sealed class BackendRequestDispatcher
{
    private readonly ContextMenuRegistryCatalog _catalog;
    private readonly SpecialMenuService _specialMenuService;
    private readonly Windows11BlocksService _windows11BlocksService;
    private readonly Win11ClassicContextMenuService _win11ClassicContextMenuService;
    private readonly FileTypeSceneMenuService _fileTypeSceneMenuService;
    private readonly FileLogger _logger;

    public BackendRequestDispatcher(ContextMenuRegistryCatalog catalog, SpecialMenuService specialMenuService,
        Windows11BlocksService windows11BlocksService, Win11ClassicContextMenuService win11ClassicContextMenuService,
        FileTypeSceneMenuService fileTypeSceneMenuService, FileLogger logger)
    {
        _catalog = catalog;
        _specialMenuService = specialMenuService;
        _windows11BlocksService = windows11BlocksService;
        _win11ClassicContextMenuService = win11ClassicContextMenuService;
        _fileTypeSceneMenuService = fileTypeSceneMenuService;
        _logger = logger;
    }

    public async Task<BackendDispatchResult> DispatchAsync(PipeRequest request, BackendRequestContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var coverage = MenuDiscoveryCoverage.Begin();
            var absence = await VerifyRequestTargetAsync(request, context, cancellationToken);
            var response = absence ?? await HandleRequestAsync(request, context, cancellationToken);
            if (request.Command is PipeCommand.GetSnapshot or PipeCommand.GetSceneSnapshot
                or PipeCommand.GetSpecialMenuSnapshot or PipeCommand.GetWin11ContextMenuSnapshot
                or PipeCommand.GetWpsOfficePendingApprovals or PipeCommand.GetShellSubMenuItems
                or PipeCommand.FindRelatedFileTypeMenuItems or PipeCommand.ReviewOpenWithRegistration)
                response = response with { SnapshotIsComplete = response.Success && coverage.Complete,
                    ClassicSnapshotIsComplete = response.Success && coverage.ClassicComplete };
            return new(CorrelateResponseWithRequest(request, response));
        }
        catch (Exception ex)
        {
            return CreateFailure(request, ex);
        }
    }

    internal static BackendDispatchResult CreateFailure(PipeRequest request, Exception ex)
        => new(CorrelateResponseWithRequest(request, new PipeResponse
        {
            Success = false,
            ErrorCode = ex is DocumentIconRepairException repairError ? repairError.ErrorCode
                : request.Command is PipeCommand.SetDocumentIconRepair or PipeCommand.GetOfficeSuiteCoexistenceStatus
                    ? ex is InvalidDataException ? PipeErrorCodes.DocumentIconRepairRecoveryInvalid
                        : ex is UnauthorizedAccessException or System.Security.SecurityException ? PipeErrorCodes.DocumentIconRepairAccessDenied
                        : PipeErrorCodes.DocumentIconRepairFailed
                    : null,
            Message = ex.Message
        }), ex);

    internal static PipeResponse CorrelateResponseWithRequest(PipeRequest request, PipeResponse response)
        => response.ClientOperationId is null && request.ClientOperationId is { } operationId
            ? response with { ClientOperationId = operationId }
            : response;

    internal static async Task PublishMutationNotificationsAsync(PipeRequest request, PipeResponse response, Func<BackendNotification, Task> publish)
    {
        if (response.Success && response.DeletedItemsPurge is { } purged && purged.PurgedItems.Count > 0)
            await publish(new BackendNotification
            {
                Kind = PipeNotificationKind.DeletedItemsPurged,
                DeletedItemsPurge = purged,
                ClientOperationId = response.ClientOperationId
            });
        if (response.Success && response.Item is not null
            && request.Command != PipeCommand.GetContextMenuItemState)
            await publish(new BackendNotification
            {
                Kind = PipeNotificationKind.ItemStateChanged,
                Item = response.Item,
                Message = response.Message,
                ClientOperationId = response.ClientOperationId,
                Timestamp = DateTimeOffset.UtcNow
            });
        if (response.Success && response.SpecialItem is not null && request.Command != PipeCommand.GetSpecialMenuItemState)
            await publish(new BackendNotification
            {
                Kind = PipeNotificationKind.ItemStateChanged,
                SpecialKind = response.SpecialItem.Kind,
                SpecialItem = response.SpecialItem,
                Message = response.Message,
                ClientOperationId = response.ClientOperationId,
                Timestamp = DateTimeOffset.UtcNow
            });
    }

    private async Task<PipeResponse> HandleRequestAsync(PipeRequest request, BackendRequestContext context, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            PipeCommand.GetSnapshot => new PipeResponse
            {
                Success = true,
                Message = "Snapshot loaded.",
                Items = await _catalog.GetSnapshotAsync(
                    cancellationToken,
                    await context.ResolveFrontendUserContextAsync(cancellationToken))
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
                        await context.ResolveFrontendUserContextAsync(cancellationToken))
                },
            PipeCommand.SetEnhanceMenuItemEnabled when request.ScopeValue is not null && request.DefinitionXml is not null && request.Enable is not null
                => await HandleSetEnhanceMenuItemEnabledAsync(request, context, cancellationToken),
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
                => await _catalog.AcknowledgeItemStateAsync(
                    request.ItemId,
                    cancellationToken,
                    await context.ResolveFrontendUserContextAsync(cancellationToken), request.Item),
            PipeCommand.SetEnabled when request.ItemId is not null && request.Enable is not null
                => await HandleSetEnabledAsync(request, context, cancellationToken),
            PipeCommand.GetContextMenuItemState when request.ItemId is not null && request.Item is not null
                => await _catalog.GetContextMenuItemStateAsync(request.ItemId, request.Item,
                    cancellationToken, await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.SetShellAttribute when request.ItemId is not null && request.Enable is not null && request.ShellAttribute is not null
                => await _catalog.ApplyShellAttributeAsync(request.ItemId, request.ShellAttribute.Value, request.Enable.Value, cancellationToken, await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.SetDisplayText when request.ItemId is not null && request.TextValue is not null
                => await _catalog.ApplyDisplayTextAsync(request.ItemId, request.TextValue, cancellationToken, await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.SetCommandText when request.ItemId is not null && request.TextValue is not null
                => await _catalog.ApplyCommandTextAsync(request.ItemId, request.TextValue, cancellationToken, await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.GetShellSubMenuItems when request.ParentItemId is not null
                => await _catalog.GetShellSubMenuItemsAsync(request.ParentItemId, cancellationToken, await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.SetShellSubMenuItemEnabled when request.ParentItemId is not null && request.SubMenuItemId is not null && request.Enable is not null
                => await _catalog.SetShellSubMenuItemEnabledAsync(request.ParentItemId, request.SubMenuItemId, request.Enable.Value, cancellationToken, await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.GetRegistryProtectionSetting
                => await _catalog.GetRegistryProtectionSettingAsync(cancellationToken),
            PipeCommand.SetRegistryProtectionSetting when request.Enable is not null
                => await _catalog.SetRegistryProtectionSettingAsync(
                    request.Enable.Value,
                    await context.ResolveFrontendUserContextAsync(cancellationToken),
                    cancellationToken),
            PipeCommand.ApplyDecision when request.ItemId is not null && request.Decision is not null
                => await HandleApplyDecisionAsync(request, context, cancellationToken),
            PipeCommand.DeleteItem when request.ItemId is not null
                => await _catalog.DeleteItemAsync(
                    request.ItemId,
                    cancellationToken,
                    await context.ResolveFrontendUserContextAsync(cancellationToken),
                    request.Item),
            PipeCommand.UndoDelete when request.ItemId is not null
                => await _catalog.UndoDeleteAsync(
                    request.ItemId,
                    cancellationToken,
                    await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.PurgeDeletedItem when request.ItemId is not null
                => await _catalog.PurgeDeletedItemAsync(request.ItemId, cancellationToken),
            PipeCommand.PurgeDeletedItems when request.DeletedItemsPurge is not null
                => await _catalog.PurgeDeletedItemsAsync(request.DeletedItemsPurge, cancellationToken,
                    await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.ResetStateDatabase
                => await _catalog.ResetStateDatabaseAsync(cancellationToken),
            PipeCommand.InspectOpenWithSources when request.ScopeValue is not null
                => new PipeResponse
                {
                    Success = true,
                    OpenWithInspection = await _specialMenuService.InspectOpenWithSourcesAsync(request.ScopeValue,
                        await context.ResolveSpecialMenuUserContextAsync(cancellationToken),
                        cancellationToken)
                },
            PipeCommand.ReviewOpenWithRegistration when request.SpecialItem is not null
                && _specialMenuService.OpenWithMonitoring is { } monitoring
                => await monitoring.ReviewAsync(request.SpecialItem, request.OpenWithProtection,
                    await context.ResolveSpecialMenuUserContextAsync(cancellationToken)
                        ?? throw new InvalidOperationException("Open With requires an authenticated user."), cancellationToken),
            PipeCommand.GetOpenWithPendingReviews when _specialMenuService.OpenWithMonitoring is { } reviewQuery
                => new PipeResponse { Success = true, OpenWithReviews = await reviewQuery.GetPendingReviewsAsync(
                    await context.ResolveSpecialMenuUserContextAsync(cancellationToken)
                        ?? throw new InvalidOperationException("Open With requires an authenticated user."), cancellationToken) },
            PipeCommand.DecideOpenWithReview when request.OpenWithReview is not null
                && _specialMenuService.OpenWithMonitoring is { } decisions
                => await decisions.DecideReviewAsync(request.OpenWithReview,
                    await context.ResolveSpecialMenuUserContextAsync(cancellationToken)
                        ?? throw new InvalidOperationException("Open With requires an authenticated user."), cancellationToken),
            PipeCommand.GetSpecialMenuSnapshot when request.SpecialKind is not null
                => new PipeResponse
                {
                    Success = true,
                    Message = "Special menu snapshot loaded.",
                    SpecialItems = await _specialMenuService.GetSnapshotAsync(
                        request.SpecialKind.Value,
                        await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                        cancellationToken)
                },
            PipeCommand.GetSpecialMenuItemState when request.SpecialItem is not null
                => await _specialMenuService.GetItemStateAsync(request.SpecialItem,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken), cancellationToken),
            PipeCommand.SetSpecialMenuItemEnabled when request.SpecialItem is not null && request.Enable is not null
                => await _specialMenuService.SetEnabledAsync(
                    request.SpecialItem,
                    request.Enable.Value,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                    cancellationToken),
            PipeCommand.CreateSpecialMenuItem
                => await _specialMenuService.CreateAsync(
                    request,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                    cancellationToken),
            PipeCommand.UpdateSpecialMenuItem
                => await _specialMenuService.UpdateAsync(
                    request,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                    cancellationToken),
            PipeCommand.DeleteSpecialMenuItem when request.SpecialItem is not null
                => await _specialMenuService.DeleteAsync(
                    request.SpecialItem,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                    cancellationToken),
            PipeCommand.UndoSpecialMenuItem when request.SpecialItem is not null
                => await _specialMenuService.UndoDeleteAsync(
                    request.SpecialItem,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                    cancellationToken),
            PipeCommand.PurgeSpecialMenuItem when request.SpecialItem is not null
                => await _specialMenuService.PurgeDeletedAsync(
                    request.SpecialItem,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                    cancellationToken),
            PipeCommand.MoveSpecialMenuItem
                => await _specialMenuService.MoveAsync(
                    request,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                    cancellationToken),
            PipeCommand.RestoreSpecialMenuDefaults when request.SpecialKind is not null
                => await _specialMenuService.RestoreDefaultsAsync(
                    request.SpecialKind.Value,
                    request.ScopeValue,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
                    cancellationToken),
            PipeCommand.GetShellNewOrderLock => await _specialMenuService.GetShellNewOrderLockAsync(
                await context.ResolveSpecialMenuUserContextAsync(cancellationToken), cancellationToken),
            PipeCommand.SetShellNewOrderLock when request.ShellNewLock is not null
                => await _specialMenuService.SetShellNewOrderLockAsync(
                    request.ShellNewLock.Lock,
                    request.ClientOperationId,
                    await ResolveSpecialMenuUserContextIfNeededAsync(request, context, cancellationToken),
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
                        await context.ResolveFrontendUserContextAsync(cancellationToken))
                },
            PipeCommand.DeleteFileTypeBatchItem when request.Item is not null
                => await _catalog.DeleteFileTypeBatchItemAsync(request.Item, cancellationToken,
                    await context.ResolveFrontendUserContextAsync(cancellationToken), request.FileTypeBatchOperationId),
            PipeCommand.GetFileTypeBatchItemState when request.Item is not null
                => await _catalog.GetFileTypeBatchItemStateAsync(request.Item, cancellationToken,
                    await context.ResolveFrontendUserContextAsync(cancellationToken)),
            PipeCommand.SetWin11BlockedItem when request.ItemId is not null
                => await HandleWin11ToggleAsync(request, context, enable: false, applicationScope: false, cancellationToken),
            PipeCommand.RemoveWin11BlockedItem when request.ItemId is not null
                => await HandleWin11ToggleAsync(request, context, enable: true, applicationScope: false, cancellationToken),
            PipeCommand.SetWin11ApplicationEnabled when request.ItemId is not null && request.Enable is not null
                => await HandleWin11ToggleAsync(request, context, request.Enable.Value, applicationScope: true, cancellationToken),
            PipeCommand.GetWin11BlockedItems
                => await HandleGetWin11BlockedItemsAsync(request, context, cancellationToken),
            PipeCommand.GetWin11ContextMenuSnapshot
                => await HandleGetWin11ContextMenuSnapshotAsync(request, context, cancellationToken),
            PipeCommand.GetWin11ModernContextMenuDisabled
                => await HandleGetWin11ModernContextMenuDisabledAsync(request, context, cancellationToken),
            PipeCommand.SetWin11ModernContextMenuDisabled when request.Enable is not null
                => await HandleSetWin11ModernContextMenuDisabledAsync(request, context, cancellationToken),
            PipeCommand.GetOfficeSuiteCoexistenceStatus
                => new PipeResponse
                {
                    Success = true,
                    Message = "Office suite co-existence status loaded.",
                    OfficeSuiteCoexistence = await _catalog.GetOfficeSuiteCoexistenceStatusAsync(
                        await context.ResolveFrontendUserContextAsync(cancellationToken), cancellationToken)
                },
            PipeCommand.GetWpsOfficePendingApprovals
                => new PipeResponse
                {
                    Success = true,
                    Message = "WPS Office pending approvals loaded.",
                    Items = await _catalog.GetWpsOfficePendingApprovalsAsync(
                        cancellationToken,
                        await context.ResolveFrontendUserContextAsync(cancellationToken))
                },
            PipeCommand.SetDocumentIconRepair when request.DocumentIconRepairEnabled is not null
                => await HandleSetDocumentIconRepairAsync(request.DocumentIconRepairEnabled.Value, context, cancellationToken),
            PipeCommand.SetDocumentIconProvider
                => new PipeResponse { Success = false, Message = "Update the frontend to use reversible document icon repair." },
            _ => new PipeResponse
            {
                Success = false,
                Message = "The request was missing required data."
            }
        };
    }

    private async Task<PipeResponse> HandleSetEnabledAsync(
        PipeRequest request,
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
        return await _catalog.ApplyDesiredStateAsync(
            request.ItemId ?? string.Empty,
            request.Enable.GetValueOrDefault(),
            cancellationToken,
            userContext,
            request.Item);
    }

    private async Task<PipeResponse> HandleSetDocumentIconRepairAsync(
        bool enable,
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
        if (userContext is null)
        {
            return new PipeResponse
            {
                Success = false,
                ErrorCode = PipeErrorCodes.DocumentIconRepairFailed,
                Message = "Cannot change document icons: frontend user context is not available."
            };
        }

        return await _catalog.SetDocumentIconRepairAsync(userContext, enable, cancellationToken);
    }

    private async Task<PipeResponse> HandleSetEnhanceMenuItemEnabledAsync(
        PipeRequest request,
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
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
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
        return await _catalog.ApplyDecisionAsync(
            request.ItemId ?? string.Empty,
            request.Decision.GetValueOrDefault(),
            cancellationToken,
            userContext);
    }

    private async Task<PipeResponse> HandleWin11ToggleAsync(
        PipeRequest request, BackendRequestContext context, bool enable, bool applicationScope, CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
        return await _catalog.SetWindows11EnabledAsync(request.ItemId ?? string.Empty,
            enable, (enable ? request.UnblockMachine : request.BlockMachine) ?? false,
            request.ClientOperationId, userContext, cancellationToken, applicationScope);
    }

    private async Task<PipeResponse> HandleGetWin11BlockedItemsAsync(
        PipeRequest request,
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
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
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
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
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
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
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var userContext = await context.ResolveFrontendUserContextAsync(cancellationToken);
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

    private static string NormalizeGuid(string guidText)
    {
        return Guid.TryParse(guidText, out var guid)
            ? guid.ToString("B")
            : guidText.Trim('{', '}');
    }

    private async Task<PipeResponse?> VerifyRequestTargetAsync(PipeRequest request, BackendRequestContext context, CancellationToken token)
    {
        // Cached DTOs never authorize a write or create a replacement registration.
        if (request.SpecialItem is { } special && request.Command is
            (PipeCommand.SetSpecialMenuItemEnabled or PipeCommand.DeleteSpecialMenuItem or PipeCommand.UpdateSpecialMenuItem))
        {
            var state = await _specialMenuService.GetItemStateAsync(special,
                await ResolveSpecialMenuUserContextIfNeededAsync(request, context, token), token);
            if (!state.Success) return state;
        }
        if (request.Item is { IsDeleted: false, IsWindows11ContextMenu: false } original && request.Command is
            (PipeCommand.SetShellAttribute or PipeCommand.SetDisplayText or PipeCommand.SetCommandText or PipeCommand.DeleteItem))
        {
            var state = await _catalog.GetContextMenuItemStateAsync(original.Id, original, token,
                await context.ResolveFrontendUserContextAsync(token));
            if (!state.Success) return state;
        }
        return null;
    }

    private async Task<BackendUserContext?> ResolveSpecialMenuUserContextIfNeededAsync(
        PipeRequest request,
        BackendRequestContext context,
        CancellationToken cancellationToken)
    {
        var kind = TryGetSpecialMenuKindForUserContext(request);
        if (kind is null || !RequiresSpecialMenuUserContext(kind.Value))
        {
            return null;
        }

        return await context.ResolveSpecialMenuUserContextAsync(cancellationToken);
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
}

internal readonly record struct BackendDispatchResult(PipeResponse Response, Exception? Error = null);
