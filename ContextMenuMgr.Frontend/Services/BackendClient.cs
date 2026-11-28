using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal sealed class BackendClient : IBackendClient
{
    private readonly IBackendTransport _transport;
    private readonly MenuListCacheService _listCache;
    private readonly MenuListActivityService _activity;
    private readonly RecentClientOperationCache _recentLocalOperations;
    private bool _disposed;
    public event EventHandler<MenuItemsRemovedEventArgs>? ItemsRemoved;
    public async Task<MenuListSnapshot<T>?> ReadCachedListAsync<T>(PipeRequest request, CancellationToken token)
    {
        var scope = ListScope(request);
        var cached = await _listCache.ReadAsync<T>(scope, token).ConfigureAwait(false);
        if (!HasPackagedPartition(request)) return cached;
        var packaged = await _listCache.ReadAsync<T>(scope + "|Packaged", token).ConfigureAwait(false);
        return cached is null && packaged is null ? null : CombineSnapshot(cached, packaged, true);
    }

    private static bool HasPackagedPartition(PipeRequest request) => request.Command == PipeCommand.GetSnapshot
        || request.Command == PipeCommand.GetSpecialMenuSnapshot && request.SpecialKind == SpecialMenuKind.ShellNew;
    private static bool IsPackaged<T>(T item) => item switch
    {
        ContextMenuEntry entry => entry.IsWindows11ContextMenu,
        SpecialMenuEntry entry => entry.Metadata.GetValueOrDefault("ProviderType") == "PackagedShellNew",
        _ => false
    };
    private static MenuListSnapshot<T> CombineSnapshot<T>(MenuListSnapshot<T>? classic,
        MenuListSnapshot<T>? packaged, bool cached = false)
        => new((classic ?? new MenuListSnapshot<T>([], false)).Concat(packaged ?? new MenuListSnapshot<T>([], false)).ToArray(),
            classic?.IsComplete == true && packaged?.IsComplete == true, cached,
            cached ? null : () => CombineSnapshot(classic?.CaptureCurrent(), packaged?.CaptureCurrent()),
            item => IsPackaged(item) ? packaged?.IsComplete == true : classic?.IsComplete == true);

    private async Task<MenuListSnapshot<T>> MergePartitionsAsync<T>(PipeRequest request, IReadOnlyList<T> items,
        PipeResponse response, long version, int generation, CancellationToken token)
    {
        var scope = ListScope(request);
        if (!HasPackagedPartition(request)) return await _listCache.MergeAsync(scope, items,
            response.SnapshotIsComplete == true, version, token, generation).ConfigureAwait(false);
        var classic = await _listCache.MergeAsync(scope, items.Where(item => !IsPackaged(item)).ToArray(),
            response.ClassicSnapshotIsComplete == true, version, token, generation).ConfigureAwait(false);
        var packaged = await _listCache.MergeAsync(scope + "|Packaged", items.Where(IsPackaged).ToArray(),
            response.SnapshotIsComplete == true, version, token, generation).ConfigureAwait(false);
        return CombineSnapshot(classic, packaged);
    }

    public BackendClient(IBackendTransport transport, MenuListCacheService cache, MenuListActivityService activity)
        : this(transport, cache, activity, new RecentClientOperationCache())
    {
    }

    internal BackendClient(IBackendTransport transport, MenuListCacheService cache, MenuListActivityService activity,
        RecentClientOperationCache recentLocalOperations)
    {
        _transport = transport;
        _listCache = cache;
        _activity = activity;
        _recentLocalOperations = recentLocalOperations;
        _transport.NotificationReceived += OnNotificationReceived;
    }

    private static string ListScope(PipeRequest request) => MenuListCacheService.Scope(request.Command,
        new { request.SceneKind, request.ScopeValue, request.SpecialKind, request.ParentItemId, request.FileTypeBatchQuery });

    private static bool IsListQuery(PipeCommand command) => command is PipeCommand.GetSnapshot
        or PipeCommand.GetSceneSnapshot or PipeCommand.GetSpecialMenuSnapshot or PipeCommand.GetWin11ContextMenuSnapshot
        or PipeCommand.GetWpsOfficePendingApprovals or PipeCommand.GetShellSubMenuItems or PipeCommand.FindRelatedFileTypeMenuItems;

    private Task<PipeResponse> SendRequestAsync(PipeRequest request, CancellationToken token)
        => Task.Run(() => ExecuteRequestAsync(request, token), token);

    private async Task<PipeResponse> ExecuteRequestAsync(PipeRequest request, CancellationToken token)
    {
        using var activity = IsListQuery(request.Command) ? _activity.Begin() : null;
        if (request.Item is null && request.ItemId is { } itemId)
            request = request with { Item = _listCache.FindItem<ContextMenuEntry>(itemId) };
        var version = _listCache.Version;
        var generation = _listCache.Generation;
        var purgeSelection = request.DeletedItemsPurge?.Items ?? [];
        foreach (var selected in purgeSelection) _listCache.BeginMutation<ContextMenuEntry>(selected.Id);
        var mutation = request.Command is PipeCommand.SetEnabled or PipeCommand.SetShellAttribute
            or PipeCommand.SetDisplayText or PipeCommand.SetCommandText or PipeCommand.DeleteItem or PipeCommand.UndoDelete
            or PipeCommand.PurgeDeletedItem or PipeCommand.ApplyDecision or PipeCommand.AcknowledgeItemState
            or PipeCommand.SetSpecialMenuItemEnabled or PipeCommand.UpdateSpecialMenuItem or PipeCommand.DeleteSpecialMenuItem
            or PipeCommand.UndoSpecialMenuItem or PipeCommand.PurgeSpecialMenuItem or PipeCommand.MoveSpecialMenuItem
            or PipeCommand.SetShellSubMenuItemEnabled or PipeCommand.SetWin11ApplicationEnabled
            or PipeCommand.SetWin11BlockedItem or PipeCommand.RemoveWin11BlockedItem or PipeCommand.ReviewOpenWithRegistration;
        var id = request.SubMenuItemId ?? request.SpecialItem?.Id ?? request.ItemId ?? string.Empty;
        if (mutation)
        {
            if (request.SubMenuItemId is not null) _listCache.BeginMutation<ShellSubMenuItem>(id);
            else if (request.SpecialItem is not null) _listCache.BeginMutation<SpecialMenuEntry>(id);
            else _listCache.BeginMutation<ContextMenuEntry>(id);
        }
        PipeResponse? result = null;
        var removedResult = false;
        var mutationResultApplied = false;
        try
        {
            result = await SendRequestCoreAsync(request, token).ConfigureAwait(false);
            if ((mutation || request.Command == PipeCommand.PurgeDeletedItems) && generation != _listCache.Generation)
                throw new OperationCanceledException("The menu state was reset while the operation was completing.");
            if (request.Command == PipeCommand.ResetStateDatabase) await _listCache.InvalidateAsync().ConfigureAwait(false);
            if (request.Command is PipeCommand.PurgeDeletedItem or PipeCommand.PurgeSpecialMenuItem
                || request.Command == PipeCommand.DeleteSpecialMenuItem && request.SpecialItem?.Kind == SpecialMenuKind.WinX && result.SpecialItem is null)
            {
                if (request.SpecialItem is not null) _listCache.ApplyResult<SpecialMenuEntry>(id, null, removed: true, generation: generation);
                else _listCache.ApplyResult<ContextMenuEntry>(id, null, removed: true, generation: generation);
                mutationResultApplied = true;
                ItemsRemoved?.Invoke(this, new MenuItemsRemovedEventArgs(request, new[] { id }));
            }
            if (request.Command == PipeCommand.ReviewOpenWithRegistration && result.SnapshotIsComplete == true)
            {
                var selected = result.SpecialItems.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                _listCache.ApplyResult(id, selected, removed: selected is null, generation: generation);
                mutationResultApplied = true;
                var scopeRequest = new PipeRequest { Command = PipeCommand.GetSpecialMenuSnapshot, SpecialKind = SpecialMenuKind.OpenWith };
                result = result with { SpecialItems = await MergePartitionsAsync(scopeRequest, result.SpecialItems, result, version, generation, token).ConfigureAwait(false) };
            }
            if (IsListQuery(request.Command))
            {
                if (request.Command == PipeCommand.GetSpecialMenuSnapshot)
                    result = result with { SpecialItems = await MergePartitionsAsync(request, result.SpecialItems, result, version, generation, token).ConfigureAwait(false) };
                else if (request.Command == PipeCommand.GetShellSubMenuItems)
                    result = result with { ShellSubMenuItems = await MergePartitionsAsync(request, result.ShellSubMenuItems, result, version, generation, token).ConfigureAwait(false) };
                else result = result with { Items = await MergePartitionsAsync(request, result.Items, result, version, generation, token).ConfigureAwait(false) };
            }
            if (result.Item is { } item && !mutation && request.Command == PipeCommand.GetContextMenuItemState
                && !_listCache.ApplyResult(item.Id, item, completesMutation: false, observedVersion: version, generation: generation))
                throw new OperationCanceledException("A newer operation superseded this state query.");
            if (result.SpecialItem is { } special && !mutation && request.Command == PipeCommand.GetSpecialMenuItemState
                && !_listCache.ApplyResult(special.Id, special, completesMutation: false, observedVersion: version, generation: generation))
                throw new OperationCanceledException("A newer operation superseded this special menu query.");
            if (request.Command == PipeCommand.DecideOpenWithReview && result.SpecialItem is { } reviewed)
            {
                _listCache.ApplyResult(reviewed.Id, reviewed, completesMutation: false, observedVersion: version, generation: generation);
                NotificationReceived?.Invoke(this, new BackendNotification { Kind = PipeNotificationKind.ItemStateChanged,
                    SpecialKind = SpecialMenuKind.OpenWith, UserSid = result.OpenWithReviews?.UserSid, SpecialItem = reviewed });
            }
            foreach (var entry in result.Items.Where(_ => mutation)) _listCache.ApplyResult(entry.Id, entry, completesMutation: false, generation: generation);
            return result;
        }
        catch (BackendRequestException ex) when (ex.ErrorCode == PipeErrorCodes.MenuItemRemoved && ex.RemovedItemIds.Count > 0)
        {
            removedResult = true;
            var accepted = new List<string>();
            foreach (var removed in ex.RemovedItemIds)
            {
                var applied = request.SubMenuItemId is not null
                    ? _listCache.ApplyResult<ShellSubMenuItem>(removed, null, true, mutation, version, generation)
                    : request.SpecialItem is not null
                        ? _listCache.ApplyResult<SpecialMenuEntry>(removed, null, true, mutation, version, generation)
                        : _listCache.ApplyResult<ContextMenuEntry>(removed, null, true, mutation, version, generation);
                if (applied) accepted.Add(removed);
            }
            if (accepted.Count > 0) ItemsRemoved?.Invoke(this, new MenuItemsRemovedEventArgs(request, accepted, notifyUser: mutation));
            throw;
        }
        finally
        {
            if (purgeSelection.Count > 0)
            {
                var removedIds = _listCache.ApplyDeletedItemsPurge(purgeSelection, result?.DeletedItemsPurge, completesMutation: true, generation);
                if (removedIds.Count > 0) ItemsRemoved?.Invoke(this, new MenuItemsRemovedEventArgs(request, removedIds));
            }
            if (mutation && !removedResult && !mutationResultApplied)
            {
                if (request.SubMenuItemId is not null) _listCache.ApplyResult(id, result?.ShellSubMenuItem, generation: generation);
                else if (request.SpecialItem is not null) _listCache.ApplyResult(id, result?.SpecialItem, generation: generation);
                else _listCache.ApplyResult(id, result?.Item ?? result?.Items.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)), generation: generation);
            }
        }
    }

    private void ApplyPurgedItems(DeletedItemsPurgeResult result, PipeRequest request, int generation)
    {
        var accepted = _listCache.ApplyDeletedItemsPurge(result.PurgedItems, result, completesMutation: false, generation);
        if (accepted.Count > 0) ItemsRemoved?.Invoke(this, new MenuItemsRemovedEventArgs(request, accepted));
    }

    public event EventHandler<BackendNotification>? NotificationReceived;

    public bool IsConnected => _transport.IsConnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _transport.StartNotifications();
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

    public async Task RequestPortableExitAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(new PipeRequest { Command = PipeCommand.RequestPortableExit }, cancellationToken);
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
    public async Task<ContextMenuEntry?> AcknowledgeItemStateAsync(string itemId, CancellationToken cancellationToken, ContextMenuEntry? item = null)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.AcknowledgeItemState,
                ItemId = itemId,
                Item = item
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

    public async Task<ContextMenuEntry?> GetContextMenuItemStateAsync(
        string itemId, ContextMenuEntry item, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest
        {
            Command = PipeCommand.GetContextMenuItemState,
            ItemId = itemId,
            Item = item
        }, cancellationToken);
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

    public async Task<IReadOnlyList<ShellSubMenuItem>> GetShellSubMenuItemsAsync(string parentItemId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest { Command = PipeCommand.GetShellSubMenuItems, ParentItemId = parentItemId }, cancellationToken);
        return response.ShellSubMenuItems;
    }

    public async Task<ShellSubMenuItem?> SetShellSubMenuItemEnabledAsync(string parentItemId, string childItemId, bool enable, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest { Command = PipeCommand.SetShellSubMenuItemEnabled, ParentItemId = parentItemId, SubMenuItemId = childItemId, Enable = enable }, cancellationToken);
        return response.ShellSubMenuItem;
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

    public async Task<ContextMenuEntry?> DeleteFileTypeBatchItemAsync(
        ContextMenuEntry item, Guid operationId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest
        {
            Command = PipeCommand.DeleteFileTypeBatchItem,
            Item = item,
            ItemId = item.Id,
            ClientOperationId = Guid.NewGuid(),
            FileTypeBatchOperationId = operationId
        }, cancellationToken);
        return response.Item;
    }

    public async Task<ContextMenuEntry?> GetFileTypeBatchItemStateAsync(
        ContextMenuEntry item, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest
        {
            Command = PipeCommand.GetFileTypeBatchItemState,
            Item = item,
            ItemId = item.Id
        }, cancellationToken);
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

    public async Task<DeletedItemsPurgeResult> PurgeDeletedItemsAsync(DeletedItemsPurgeRequest request, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest
        {
            Command = PipeCommand.PurgeDeletedItems,
            DeletedItemsPurge = request
        }, cancellationToken);
        return response.DeletedItemsPurge
            ?? throw new InvalidOperationException("The backend does not support bulk cleanup. Update the backend service.");
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

    public async Task<IReadOnlyList<SpecialMenuEntry>> ReviewOpenWithRegistrationAsync(SpecialMenuEntry item,
        bool? protect, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest
        {
            Command = PipeCommand.ReviewOpenWithRegistration, SpecialItem = item,
            OpenWithProtection = protect, ClientOperationId = Guid.NewGuid()
        }, cancellationToken);
        return response.SpecialItems;
    }

    public async Task<OpenWithReviewSnapshot> GetOpenWithPendingReviewsAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest { Command = PipeCommand.GetOpenWithPendingReviews }, cancellationToken);
        return response.OpenWithReviews ?? throw new InvalidOperationException("Missing Open With review snapshot.");
    }

    public async Task<OpenWithReviewSnapshot> DecideOpenWithReviewAsync(OpenWithReviewRequest request, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest { Command = PipeCommand.DecideOpenWithReview,
            OpenWithReview = request, ClientOperationId = Guid.NewGuid() }, cancellationToken);
        return response.OpenWithReviews ?? throw new InvalidOperationException("Missing Open With review decision result.");
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

    public async Task<SpecialMenuEntry?> GetSpecialMenuItemStateAsync(SpecialMenuEntry item, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest { Command = PipeCommand.GetSpecialMenuItemState,
            SpecialItem = item, SpecialKind = item.Kind }, cancellationToken);
        return response.SpecialItem;
    }

    public async Task<bool> GetShellNewOrderLockAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new PipeRequest { Command = PipeCommand.GetShellNewOrderLock }, cancellationToken);
        return response.ShellNewOrderLocked ?? throw new InvalidOperationException("ShellNew lock state is unknown.");
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
    public async Task<IReadOnlyList<ContextMenuEntry>> SetWin11BlockedItemAsync(
        string handlerClsid,
        string displayName,
        bool blockMachine,
        Guid clientOperationId,
        CancellationToken cancellationToken, bool applicationScope = false)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = applicationScope ? PipeCommand.SetWin11ApplicationEnabled : PipeCommand.SetWin11BlockedItem,
                Enable = false,
                ItemId = handlerClsid,
                DisplayName = displayName,
                BlockMachine = blockMachine,
                ClientOperationId = clientOperationId
            },
            cancellationToken);
        return response.Items;
    }

    /// <summary>
    /// Removes Win11 blocked item Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> RemoveWin11BlockedItemAsync(
        string handlerClsid,
        bool unblockMachine,
        Guid clientOperationId,
        CancellationToken cancellationToken, bool applicationScope = false)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = applicationScope ? PipeCommand.SetWin11ApplicationEnabled : PipeCommand.RemoveWin11BlockedItem,
                Enable = true,
                ItemId = handlerClsid,
                UnblockMachine = unblockMachine,
                ClientOperationId = clientOperationId
            },
            cancellationToken);
        return response.Items;
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

        return response.Win11ModernContextMenuDisabled
            ?? throw new InvalidOperationException("The backend did not report the context menu state.");
    }

    public async Task<bool> SetWin11ModernContextMenuDisabledAsync(bool disabled, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetWin11ModernContextMenuDisabled,
                Enable = disabled
            },
            cancellationToken);
        if (response.Win11ModernContextMenuDisabled != disabled)
            throw new InvalidOperationException("The backend did not verify the requested context menu state.");
        return response.Win11ClassicContextMenuChanged
            ?? throw new InvalidOperationException("The backend did not verify the context menu change. Update the backend service and refresh.");
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

    public async Task<OfficeSuiteCoexistenceStatus?> SetDocumentIconRepairAsync(
        bool enable,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.SetDocumentIconRepair,
                DocumentIconRepairEnabled = enable
            },
            cancellationToken);
        return response.OfficeSuiteCoexistence;
    }

    public async Task ResetStateDatabaseAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new PipeRequest
            {
                Command = PipeCommand.ResetStateDatabase
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _recentLocalOperations.Clear();
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _transport.NotificationReceived -= OnNotificationReceived;
        }
    }

    private async Task<PipeResponse> SendRequestCoreAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new OperationCanceledException("The backend client is shutting down.");
        }

        request = PrepareRequestForSend(request);
        try
        {
            var response = await _transport.SendAsync(request, notification =>
            {
                if (notification.ClientOperationId != request.ClientOperationId)
                    TryForwardSubscriptionNotification(notification);
            }, cancellationToken).ConfigureAwait(false);

            if (!response.Success)
                throw new BackendRequestException(response.Message, response.ErrorCode,
                    response.RegistryProtectionEnabled, response.RemovedItemIds);

            _recentLocalOperations.MarkCompleted(request.ClientOperationId);
            return response;
        }
        catch
        {
            _recentLocalOperations.Remove(request.ClientOperationId);
            throw;
        }
    }

    internal PipeRequest PrepareRequestForSend(PipeRequest request)
    {
        var preparedRequest = request.ClientOperationId is null
            ? request with { ClientOperationId = Guid.NewGuid() }
            : request;

        // Register before serialization/write so a fast rebroadcast on the
        // subscription connection cannot race ahead of local suppression.
        _recentLocalOperations.Register(preparedRequest.ClientOperationId);
        return preparedRequest;
    }

    private void OnNotificationReceived(BackendNotification notification)
        => TryForwardSubscriptionNotification(notification);

    internal bool TryForwardSubscriptionNotification(BackendNotification notification)
    {
        if ((notification.SpecialKind == SpecialMenuKind.OpenWith || notification.Kind == PipeNotificationKind.DeletedItemsPurged) && !string.Equals(notification.UserSid,
            System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value, StringComparison.OrdinalIgnoreCase)) return false;
        if (_recentLocalOperations.Contains(notification.ClientOperationId))
        {
            return false;
        }

        if (notification.Kind == PipeNotificationKind.DeletedItemsPurged && notification.DeletedItemsPurge is { } purged)
            ApplyPurgedItems(purged, new PipeRequest { Command = PipeCommand.PurgeDeletedItems,
                DeletedItemsPurge = new DeletedItemsPurgeRequest { Items = purged.PurgedItems } }, _listCache.Generation);
        NotificationReceived?.Invoke(this, notification);
        return true;
    }

}
