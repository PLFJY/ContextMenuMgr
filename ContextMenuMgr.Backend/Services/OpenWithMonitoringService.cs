using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>Bounded Applications-only reconciliation, driven by the existing registry monitor.</summary>
internal sealed class OpenWithMonitoringService(ContextMenuStateStore store, FileLogger logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, BackendUserContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Settlement> _settlements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private int _sessionBoundary;
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _isolationRetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextSessionScan;
    internal event EventHandler<BackendNotification>? Changed;

    internal void ObserveUser(BackendUserContext context) => _contexts[context.Sid] = context;

    internal void NotifySessionBoundary() => Interlocked.Exchange(ref _sessionBoundary, 1);

    internal async Task PollAsync(BackendUserContext? fallback, DateTimeOffset now, CancellationToken token)
    {
        if (fallback is not null) ObserveUser(fallback);
        await _gate.WaitAsync(token);
        try
        {
            if (Interlocked.Exchange(ref _sessionBoundary, 0) != 0) _settlements.Clear();
            if (now >= _nextSessionScan)
            {
                _nextSessionScan = now.AddSeconds(30);
                foreach (var user in new BackendUserContextResolver(logger).ResolveLoggedInUsers()) ObserveUser(user);
            }
        }
        finally { _gate.Release(); }
        // Yield the coordinator between users so a frontend snapshot or intent change
        // does not wait for every RDP session's reconciliation.
        foreach (var context in _contexts.Values.ToArray())
        {
            token.ThrowIfCancellationRequested();
            await _gate.WaitAsync(token);
            try
            {
                if (_retryAfter.TryGetValue(context.Sid, out var retryAt) && now < retryAt) continue;
                try
                {
                    OpenWithRegistration.ValidateUser(context);
                    using var classes = OpenWithRegistration.Open($@"HKEY_USERS\{context.Sid}\Software\Classes")
                        ?? throw new InvalidOperationException("The user's Classes hive is not loaded yet.");
                    await ReconcileAsync(context, now, token);
                    _failures.Remove(context.Sid);
                    _retryAfter.Remove(context.Sid);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _settlements.Remove(context.Sid);
                    _retryAfter[context.Sid] = now.AddSeconds(30);
                    logger.LogFireAndForget(RuntimeLogLevel.Warning,
                        $"OpenWithMonitorDeferred: Sid={context.Sid}, Error={ex.Message}");
                    if (!_failures.TryGetValue(context.Sid, out var failure) || failure != ex.Message)
                    {
                        _failures[context.Sid] = ex.Message;
                        Changed?.Invoke(this, new BackendNotification
                        {
                            Kind = PipeNotificationKind.OpenWithChanged, SpecialKind = SpecialMenuKind.OpenWith,
                            UserSid = context.Sid, MessageResourceKey = "OpenWithMonitoringUnavailable",
                            Message = "Open With monitoring is waiting for registry access or durable storage. Refresh the Open With page to inspect the current state."
                        });
                    }
                }
            }
            finally { _gate.Release(); }
        }
    }

    private async Task ReconcileAsync(BackendUserContext context, DateTimeOffset now, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        var snapshot = SpecialMenuService.GetOpenWithItems(context, budget.Token, rootLimit: 4096);
        // Access failures are not evidence of deletion or of a changed registration.
        if (snapshot.Any(e => e.Metadata.GetValueOrDefault("ReadOnlyReason") == "OpenWithInaccessibleRegistration"))
            throw new InvalidOperationException("OpenWithInaccessibleRegistration");
        var live = snapshot.Where(IsLive).ToDictionary(PathOf, StringComparer.OrdinalIgnoreCase);
        if (!_settlements.TryGetValue(context.Sid, out var settlement))
            _settlements[context.Sid] = settlement = new Settlement(now);
        var settled = settlement.Observe(live, now);
        if (now < settlement.Started.AddSeconds(30) || !settlement.InitialSnapshotSettled(now)) return;

        var notifications = new List<SpecialMenuEntry>();
        var isolations = new List<(SpecialMenuEntry Item, string Status)>();
        await store.WithOpenWithAsync(state =>
        {
            if (!state.Users.TryGetValue(context.Sid, out var baseline))
            {
                // First complete, settled user snapshot adopts current physical state, including zero items.
                state.Users[context.Sid] = new OpenWithUserBaseline
                {
                    Registrations = live.ToDictionary(p => p.Key, p => Stamp(p.Value), StringComparer.OrdinalIgnoreCase),
                    Generations = live.ToDictionary(p => p.Key, p => Generation(p.Value), StringComparer.OrdinalIgnoreCase)
                };
                return false;
            }
            foreach (var (path, item) in settled)
            {
                if (baseline.VisibilityIntents.ContainsKey(path)) continue;
                // Older states could publish a failed isolation as a review. It is not an approval.
                if (baseline.Reviews.GetValueOrDefault(path) == "IsolationFailed")
                {
                    baseline.Reviews.Remove(path);
                    baseline.ReviewVersions.Remove(path);
                    baseline.Quarantines.Remove(path);
                }
                var stamp = Stamp(item);
                var status = baseline.PendingIsolations.GetValueOrDefault(path);
                if (!baseline.Registrations.TryGetValue(path, out var previous))
                    status = baseline.MissingRegistrations.Contains(path)
                        || snapshot.Any(e => !IsLive(e) && PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase))
                        ? "ReRegistered" : "New";
                else if (previous != stamp || baseline.Generations.TryGetValue(path, out var oldGeneration)
                         && oldGeneration != Generation(item))
                    status = "Changed";
                else if (item.IsEnabled && baseline.Reviews.GetValueOrDefault(path) is "New" or "Changed" or "ReRegistered")
                    status = baseline.Reviews[path]; // previously released review records also need verified isolation
                if (status is not null && !state.Rules.ContainsKey(RuleKey(context.Sid, path)))
                {
                    baseline.PendingIsolations[path] = status;
                    isolations.Add((item, status));
                }
                baseline.Registrations[path] = stamp;
                baseline.Generations[path] = Generation(item);
                baseline.MissingRegistrations.Remove(path);
            }
            foreach (var path in baseline.Registrations.Keys.Where(p => !live.ContainsKey(p)
                         && settlement.AbsenceSettled(p, now)).ToArray())
            {
                if (baseline.VisibilityIntents.ContainsKey(path)) continue;
                baseline.Registrations.Remove(path);
                baseline.Reviews.Remove(path);
                baseline.Generations.Remove(path);
                baseline.ReviewVersions.Remove(path);
                baseline.MissingRegistrations.Add(path);
                baseline.PendingIsolations.Remove(path);
                baseline.Quarantines.Remove(path);
                _isolationRetryAfter.Remove(RuleKey(context.Sid, path));
            }
            return true;
        }, save: true, token);

        var recovered = await ReconcileVisibilityIntentsAsync(context, token);
        notifications.AddRange(recovered);
        if (recovered.Count > 0) ShellChangeNotifier.NotifyAssociationsChanged();
        foreach (var (item, status) in isolations)
        {
            var key = RuleKey(context.Sid, PathOf(item));
            if (_isolationRetryAfter.TryGetValue(key, out var retry) && now < retry) continue;
            var isolated = await IsolateForReviewAsync(item, status, context, token);
            if (isolated is null) _isolationRetryAfter[key] = now.AddSeconds(30);
            else _isolationRetryAfter.Remove(key);
            if (isolated is not null) notifications.Add(isolated);
        }

        var rules = await store.WithOpenWithAsync(state => state.Rules.Values
            .Where(r => r.Sid.Equals(context.Sid, StringComparison.OrdinalIgnoreCase)).ToArray(), false, token);
        foreach (var rule in rules)
        {
            token.ThrowIfCancellationRequested();
            if (!settled.TryGetValue(rule.Path, out var item)) continue;
            if (await ReapplyAsync(context, rule, item, now, token)) notifications.Add(item);
        }
        settlement.PruneAbsent(now);
        // One signal per user/cycle. Snapshot, not this potentially stale DTO, is authoritative.
        if (notifications.Count > 0)
        {
            var pending = await store.WithOpenWithAsync(state => PendingPaths(state, context).Count(), false, token);
            Changed?.Invoke(this, new BackendNotification
            {
                Kind = PipeNotificationKind.OpenWithChanged, SpecialKind = SpecialMenuKind.OpenWith,
                UserSid = context.Sid, SpecialItem = notifications[0],
                OpenWithPendingCount = pending,
                MessageResourceKey = "OpenWithMonitoringChanged", Message = "Open With registrations changed. Review Pending Approvals."
            });
        }
    }

    private static void CompleteIsolation(OpenWithUserBaseline baseline, string path, SpecialMenuEntry item, string status,
        bool quarantineWritten)
    {
        if (item.IsEnabled) throw new InvalidOperationException("OpenWithIsolationFailed");
        baseline.Registrations[path] = Stamp(item);
        baseline.Generations[path] = Generation(item);
        baseline.Reviews[path] = status;
        baseline.ReviewVersions[path] = Guid.NewGuid().ToString("N");
        baseline.PendingIsolations.Remove(path);
        var visibility = item.Metadata.GetValueOrDefault("VisibilityStamp") ?? "";
        if (quarantineWritten) baseline.Quarantines[path] = visibility;
        else if (baseline.Quarantines.GetValueOrDefault(path) != visibility
                 || item.Metadata.GetValueOrDefault("VisibilityOwnership") != "Owned") baseline.Quarantines.Remove(path);
    }

    private async Task<SpecialMenuEntry?> IsolateForReviewAsync(SpecialMenuEntry item, string status,
        BackendUserContext context, CancellationToken token)
    {
        var path = PathOf(item);
        try
        {
            if (item.IsEnabled)
            {
                var isolated = await SetVisibilityCoreAsync(item, false, false, context, token, isolationStatus: status);
                ShellChangeNotifier.NotifyAssociationsChanged();
                return isolated;
            }
            // Existing disabled registrations need no write, including third-party NoOpenWith values.
            return await store.WithOpenWithAsync<SpecialMenuEntry?>(state =>
            {
                lock (OpenWithRegistration.Sync)
                {
                    var current = SpecialMenuService.ReadOpenWithRegistration(path, context);
                    if (Generation(current) != Generation(item) || current.IsEnabled)
                        throw new InvalidOperationException("OpenWithStaleReview");
                    var baseline = state.Users[context.Sid];
                    if (baseline.VisibilityIntents.ContainsKey(path)) throw new InvalidOperationException("OpenWithOperationUncertain");
                    CompleteIsolation(baseline, path, current, status, quarantineWritten: false);
                    return Decorate(current, context, state);
                }
            }, true, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogFireAndForget(RuntimeLogLevel.Warning, $"OpenWithIsolationFailed: Path={path}, Error={ex.Message}");
            // Keep an unresolved journal for read-back. A verified failed write is not an approval
            // and needs no repeated mutation until the physical registration changes.
            return await store.WithOpenWithAsync(state =>
            {
                var baseline = state.Users[context.Sid];
                if (baseline.VisibilityIntents.ContainsKey(path)) return null;
                var current = SpecialMenuService.ReadOpenWithRegistration(path, context);
                if (Generation(current) != Generation(item)) return null;
                if (current.IsEnabled)
                {
                    baseline.PendingIsolations.Remove(path);
                    baseline.Reviews.Remove(path);
                    baseline.ReviewVersions.Remove(path);
                    baseline.Quarantines.Remove(path);
                    return null;
                }
                if (baseline.PendingIsolations.TryGetValue(path, out var pendingStatus))
                    CompleteIsolation(baseline, path, current, pendingStatus, quarantineWritten: false);
                return Decorate(current, context, state);
            }, true, token);
        }
    }

    private async Task<bool> ReapplyAsync(BackendUserContext context, OpenWithProtectionRule observed,
        SpecialMenuEntry item, DateTimeOffset now, CancellationToken token)
    {
        if (!observed.Authorized || observed.Outcome == "Conflict") return false;
        if (!item.IsEnabled && item.CanEdit && observed.Outcome is "Satisfied" or "Reapplied"
            && observed.CommandIdentity == item.Metadata.GetValueOrDefault("CommandIdentity")
            && item.Metadata.GetValueOrDefault("VisibilityOwnership") == "Owned") return false;
        var key = RuleKey(context.Sid, observed.Path);
        var outcomeChanged = false;
        // Persist an attempt BEFORE touching the registry. A crash or failed outcome save leaves
        // Uncertain, which can only reconcile to satisfied or review; it never retries a write.
        var attempt = await store.WithOpenWithAsync(state =>
        {
            if (!state.Rules.TryGetValue(key, out var rule) || !rule.Authorized) return false;
            if (state.Users[context.Sid].VisibilityIntents.ContainsKey(rule.Path)) return false;
            var old = rule.Outcome;
            try
            {
                OpenWithRegistration.ValidatePath(rule.Path, context);
                if (!rule.Path.StartsWith($@"HKEY_USERS\{context.Sid}\", StringComparison.OrdinalIgnoreCase)
                    || rule.Sid != context.Sid || !item.CanEdit || rule.CommandIdentity != item.Metadata.GetValueOrDefault("CommandIdentity"))
                    throw new InvalidOperationException("IdentityConflict");
                var ownership = item.Metadata.GetValueOrDefault("VisibilityOwnership");
                if (ownership is "Changed" or "Unknown") throw new InvalidOperationException("VisibilityConflict");
                if (!item.IsEnabled)
                {
                    rule.Outcome = "Satisfied";
                    if (old == "Uncertain") state.Users[context.Sid].Reviews[rule.Path] = "Reapplied";
                    if (old is not ("Satisfied" or "Reapplied")) rule.LastVerifiedUtc = now;
                    outcomeChanged = old == "Uncertain";
                    return false;
                }
                if (old is "Uncertain" or "Conflict") throw new InvalidOperationException("ReviewRequired");
                if (now < rule.NextAttemptUtc) return false;
                if (rule.Attempts >= 3) throw new InvalidOperationException("RetryLimit");
                if (rule.ExecutablePath != item.TargetPath || rule.ExecutableHash != ExecutableHash(item.TargetPath, token))
                    throw new InvalidOperationException("IdentityConflict");
                rule.Attempts++;
                rule.NextAttemptUtc = now.AddSeconds(rule.Attempts * rule.Attempts * 30);
                rule.Outcome = "Uncertain";
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rule.Outcome = "Conflict";
                outcomeChanged = old != "Conflict";
                state.Users[context.Sid].Reviews[rule.Path] = "Conflict";
                if (old != rule.Outcome)
                    logger.LogFireAndForget(RuntimeLogLevel.Warning, $"OpenWithProtectionConflict: Path={rule.Path}, Reason={ex.Message}");
                return false;
            }
        }, true, token);
        if (!attempt) return outcomeChanged;

        try
        {
            // Re-resolve under both persistence and registry serialization. A queued UI rule change
            // cannot authorize a stale action; external writes are checked again inside TxR.
            await store.WithOpenWithAsync(state =>
            {
                var rule = state.Rules[key];
                if (!rule.Authorized || rule.Outcome != "Uncertain") return false;
                token.ThrowIfCancellationRequested();
                lock (OpenWithRegistration.Sync)
                {
                    var current = SpecialMenuService.ReadOpenWithRegistration(rule.Path, context);
                    using var executable = OpenExecutable(current.TargetPath);
                    if (rule.CommandIdentity != current.Metadata.GetValueOrDefault("CommandIdentity")
                        || rule.ExecutablePath != current.TargetPath || rule.ExecutableHash != HashExecutable(executable, token))
                        throw new InvalidOperationException("OpenWithIdentityConflict");
                    if (current.Metadata.GetValueOrDefault("VisibilityOwnership") is "Unknown" or "Changed")
                        throw new InvalidOperationException("OpenWithExternalVisibility");
                    if (current.IsEnabled) SpecialMenuService.SetOpenWithEnabled(current, false, context);
                    var actual = SpecialMenuService.ReadOpenWithRegistration(rule.Path, context);
                    if (actual.IsEnabled || actual.Metadata.GetValueOrDefault("VisibilityOwnership") != "Owned")
                        throw new InvalidOperationException("OpenWithOperationUncertain");
                    rule.Outcome = "Reapplied";
                    rule.LastVerifiedUtc = now;
                    state.Users[context.Sid].Reviews[rule.Path] = "Reapplied";
                    state.Users[context.Sid].Registrations[rule.Path] = Stamp(actual);
                    state.Users[context.Sid].Generations[rule.Path] = Generation(actual);
                }
                return true;
            }, true, CancellationToken.None);
            ShellChangeNotifier.NotifyAssociationsChanged();
        }
        catch (Exception ex)
        {
            logger.LogFireAndForget(RuntimeLogLevel.Warning, $"OpenWithProtectionUncertain: Path={observed.Path}, Error={ex.Message}");
            // Durable Uncertain remains. Next poll performs read-back, never a blind write retry.
        }
        return true;
    }

    internal async Task<IReadOnlyList<SpecialMenuEntry>> GetSnapshotAsync(BackendUserContext context, CancellationToken token)
    {
        ObserveUser(context);
        await _gate.WaitAsync(token);
        try { return await SnapshotCoreAsync(context, token); }
        finally { _gate.Release(); }
    }

    private Task<IReadOnlyList<SpecialMenuEntry>> SnapshotCoreAsync(BackendUserContext context, CancellationToken token)
    {
        var items = SpecialMenuService.GetOpenWithItems(context, token).ToList();
        return store.WithOpenWithAsync<IReadOnlyList<SpecialMenuEntry>>(state =>
        {
            foreach (var rule in state.Rules.Values.Where(r => r.Sid == context.Sid))
                if (!items.Any(e => PathOf(e).Equals(rule.Path, StringComparison.OrdinalIgnoreCase)))
                    items.Add(new SpecialMenuEntry
                    {
                        Id = "openwith-rule:" + rule.Path, Kind = SpecialMenuKind.OpenWith,
                        DisplayName = rule.Path[(rule.Path.LastIndexOf('\\') + 1)..], RegistryPath = rule.Path,
                        CanEdit = false, CanDelete = false,
                        Metadata = new Dictionary<string, string> { ["AppRegistryPath"] = rule.Path, ["RuleOnly"] = "true", ["RegistryScope"] = "User" }
                    });
            return items.Select(e => Decorate(e, context, state)).ToArray();
        }, false, token);
    }

    internal async Task<SpecialMenuEntry> SetEnabledAsync(SpecialMenuEntry item, bool enabled,
        BackendUserContext context, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (item.Id == "openwith:use-store") return SpecialMenuService.SetOpenWithEnabled(item, enabled, context);
            // Ordinary visibility controls do not acknowledge a registration review.
            return await SetVisibilityCoreAsync(item, enabled, resolveReview: false, context, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<SpecialMenuEntry> SetVisibilityCoreAsync(SpecialMenuEntry item, bool enabled,
        bool resolveReview, BackendUserContext context, CancellationToken token, string? reviewGeneration = null,
        string? isolationStatus = null)
    {
        var path = OpenWithRegistration.ValidatePath(PathOf(item), context);
        var intent = new OpenWithVisibilityIntent
        {
            BeforeGeneration = Generation(item), ContentGeneration = item.Metadata.GetValueOrDefault("ContentStamp") ?? "",
            CommandIdentity = item.Metadata.GetValueOrDefault("CommandIdentity") ?? "",
            Enabled = enabled, ResolveReview = resolveReview, IsolationReviewStatus = isolationStatus
        };
        // A missing baseline is not created by UI actions. The monitor alone adopts it.
        var journaled = await store.WithOpenWithAsync(state =>
        {
            lock (OpenWithRegistration.Sync)
            {
                var current = SpecialMenuService.ReadOpenWithRegistration(path, context);
                if (Generation(current) != intent.BeforeGeneration || string.IsNullOrEmpty(intent.BeforeGeneration))
                    throw new InvalidOperationException("OpenWithStaleReview");
                if (!current.CanEdit) throw new InvalidOperationException("OpenWithUnsupportedRegistration");
                if (!state.Users.TryGetValue(context.Sid, out var baseline))
                {
                    if (resolveReview || isolationStatus is not null) throw new InvalidOperationException("OpenWithStaleReview");
                    return false;
                }
                if (resolveReview)
                {
                    var review = CreateReview(state, context, path, current);
                    if (review.Generation != reviewGeneration || !(enabled ? review.CanAccept && review.IsQuarantined : review.CanReject))
                        throw new InvalidOperationException("OpenWithStaleReview");
                }
                else if (enabled && (baseline.PendingIsolations.ContainsKey(path)
                    || baseline.Reviews.GetValueOrDefault(path) is "New" or "Changed" or "ReRegistered" or "IsolationFailed"))
                    throw new InvalidOperationException("OpenWithReviewRequired");
                if (isolationStatus is not null && baseline.PendingIsolations.GetValueOrDefault(path) != isolationStatus)
                    throw new InvalidOperationException("OpenWithStaleReview");
                if (baseline.VisibilityIntents.ContainsKey(path)) throw new InvalidOperationException("OpenWithOperationUncertain");
                baseline.VisibilityIntents[path] = intent;
                return true;
            }
        }, true, token);
        if (!journaled)
        {
            // No review or rule exists before the monitor baseline; keep existing manual controls usable.
            return SpecialMenuService.SetOpenWithEnabled(item, enabled, context);
        }
        try
        {
            return await store.WithOpenWithAsync(state =>
            {
                if (!state.Users.TryGetValue(context.Sid, out var baseline)
                    || !baseline.VisibilityIntents.TryGetValue(path, out var saved)
                    || saved.BeforeGeneration != intent.BeforeGeneration || saved.Enabled != enabled)
                    throw new InvalidOperationException("OpenWithOperationUncertain");
                token.ThrowIfCancellationRequested();
                lock (OpenWithRegistration.Sync)
                {
                    var updated = SpecialMenuService.SetOpenWithEnabled(item, enabled, context);
                    CompleteVisibility(state, baseline, path, updated, saved, context);
                    return Decorate(updated, context, state);
                }
            }, true, CancellationToken.None);
        }
        catch
        {
            // The durable journal survives failures of either registry mutation or the final state save.
            // Reconciliation is read-only with respect to the registry; never retry the write here.
            try { await ReconcileVisibilityIntentsAsync(context, CancellationToken.None); }
            catch (Exception ex) { logger.LogFireAndForget(RuntimeLogLevel.Warning, $"OpenWithDecisionUncertain: Path={path}, Error={ex.Message}"); }
            throw;
        }
    }

    private static void CompleteVisibility(OpenWithMonitorState state, OpenWithUserBaseline baseline, string path,
        SpecialMenuEntry current, OpenWithVisibilityIntent intent, BackendUserContext context)
    {
        baseline.Registrations[path] = Stamp(current);
        baseline.Generations[path] = Generation(current);
        baseline.VisibilityIntents.Remove(path);
        if (intent.IsolationReviewStatus is { } status)
            CompleteIsolation(baseline, path, current, status, quarantineWritten: true);
        if (intent.Enabled)
        {
            state.Rules.Remove(RuleKey(context.Sid, path));
            baseline.Quarantines.Remove(path);
        }
        if (intent.ResolveReview)
        {
            baseline.Reviews.Remove(path);
            baseline.ReviewVersions.Remove(path);
            baseline.Quarantines.Remove(path);
            baseline.PendingIsolations.Remove(path);
        }
    }

    private Task<IReadOnlyList<SpecialMenuEntry>> ReconcileVisibilityIntentsAsync(BackendUserContext context, CancellationToken token)
        => store.WithOpenWithAsync<IReadOnlyList<SpecialMenuEntry>>(state =>
        {
            var completed = new List<SpecialMenuEntry>();
            if (!state.Users.TryGetValue(context.Sid, out var baseline)) return completed;
            foreach (var (path, intent) in baseline.VisibilityIntents.ToArray())
            {
                token.ThrowIfCancellationRequested();
                OpenWithRegistration.ValidatePath(path, context);
                lock (OpenWithRegistration.Sync)
                {
                    SpecialMenuEntry current;
                    try { current = SpecialMenuService.ReadOpenWithRegistration(path, context); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                    { continue; }
                    if (Generation(current) == intent.BeforeGeneration)
                    {
                        baseline.VisibilityIntents.Remove(path); // verified no physical change; retain review and rule
                        continue;
                    }
                    if (current.IsEnabled == intent.Enabled && current.CanEdit
                        && current.Metadata.GetValueOrDefault("CommandIdentity") == intent.CommandIdentity
                        && current.Metadata.GetValueOrDefault("ContentStamp") == intent.ContentGeneration
                        && current.Metadata.GetValueOrDefault("VisibilityOwnership") == (intent.Enabled ? "Enabled" : "Owned"))
                    {
                        CompleteVisibility(state, baseline, path, current, intent, context);
                        completed.Add(Decorate(current, context, state));
                    }
                }
            }
            return completed;
        }, true, token);

    internal async Task<OpenWithReviewSnapshot> GetPendingReviewsAsync(BackendUserContext context, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        await _gate.WaitAsync(budget.Token);
        try { return await PendingCoreAsync(context, budget.Token); }
        finally { _gate.Release(); }
    }

    private static IEnumerable<string> PendingPaths(OpenWithMonitorState state, BackendUserContext context)
    {
        if (!state.Users.TryGetValue(context.Sid, out var baseline)) return [];
        return baseline.Reviews.Where(p => p.Value is "Conflict" or "Uncertain"
            || (p.Value is "New" or "Changed" or "ReRegistered") && !baseline.PendingIsolations.ContainsKey(p.Key))
            .Select(p => p.Key).Concat(baseline.VisibilityIntents.Keys)
            .Concat(state.Rules.Values.Where(r => r.Sid.Equals(context.Sid, StringComparison.OrdinalIgnoreCase)
                && r.Authorized && r.Outcome is "Conflict" or "Uncertain").Select(r => r.Path))
            .Where(p => baseline.Registrations.ContainsKey(p) || baseline.VisibilityIntents.ContainsKey(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private Task<OpenWithReviewSnapshot> PendingCoreAsync(BackendUserContext context, CancellationToken token)
    {
        OpenWithRegistration.ValidateUser(context);
        return store.WithOpenWithAsync(state =>
        {
            var paths = PendingPaths(state, context).Take(8193).ToArray();
            if (paths.Length > 8192) throw new InvalidOperationException("OpenWithReviewLimitExceeded");
            var reviews = new List<OpenWithPendingReview>();
            foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                OpenWithRegistration.ValidatePath(path, context);
                SpecialMenuEntry current;
                try { current = SpecialMenuService.ReadOpenWithRegistration(path, context); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    // A partial read cannot erase a durable review or fabricate a live approval.
                    current = new SpecialMenuEntry { Id = path, Kind = SpecialMenuKind.OpenWith,
                        DisplayName = path[(path.LastIndexOf('\\') + 1)..], RegistryPath = path,
                        CanEdit = false, CanDelete = false, Metadata = new Dictionary<string, string>
                        { ["AppRegistryPath"] = path, ["ReadOnlyReason"] = "OpenWithOperationUncertain" } };
                }
                var review = CreateReview(state, context, path, current);
                if (review.Status != OpenWithReviewStatus.IsolationFailed) reviews.Add(review);
            }
            return new OpenWithReviewSnapshot(context.Sid, reviews, state.Users.ContainsKey(context.Sid));
        }, false, token);
    }

    private static OpenWithPendingReview CreateReview(OpenWithMonitorState state, BackendUserContext context,
        string path, SpecialMenuEntry current)
    {
        var baseline = state.Users[context.Sid];
        var decorated = Decorate(current, context, state);
        var statusText = decorated.Metadata.GetValueOrDefault("MonitoringStatus");
        var status = Enum.TryParse<OpenWithReviewStatus>(statusText, out var parsed) ? parsed : OpenWithReviewStatus.Uncertain;
        var verified = !string.IsNullOrEmpty(Generation(current))
            && baseline.Registrations.GetValueOrDefault(path) == Stamp(current)
            && (!baseline.Generations.TryGetValue(path, out var expected) || expected == Generation(current));
        if (!verified || baseline.VisibilityIntents.ContainsKey(path)) status = OpenWithReviewStatus.Uncertain;
        if (verified && !baseline.VisibilityIntents.ContainsKey(path)
            && (baseline.PendingIsolations.ContainsKey(path)
                || current.IsEnabled && status is OpenWithReviewStatus.New or OpenWithReviewStatus.Changed or OpenWithReviewStatus.ReRegistered))
            status = OpenWithReviewStatus.IsolationFailed;
        var ordinary = status is OpenWithReviewStatus.New or OpenWithReviewStatus.Changed or OpenWithReviewStatus.ReRegistered;
        var quarantined = !current.IsEnabled && current.Metadata.GetValueOrDefault("VisibilityOwnership") == "Owned"
            && baseline.Quarantines.TryGetValue(path, out var visibility) && visibility == current.Metadata.GetValueOrDefault("VisibilityStamp");
        var protectedItem = decorated.Metadata.GetValueOrDefault("PersistentProtection") == "True";
        var canAccept = ordinary && !current.IsEnabled && (!quarantined || current.CanEdit && !protectedItem);
        var canReject = ordinary && !current.IsEnabled && current.CanEdit;
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            context.Sid, path.ToUpperInvariant(), baseline.ReviewVersions.GetValueOrDefault(path),
            baseline.Reviews.GetValueOrDefault(path), Generation(current)))));
        return new(context.Sid, path, version, status, decorated, canAccept, canReject,
            status is OpenWithReviewStatus.Uncertain or OpenWithReviewStatus.Conflict or OpenWithReviewStatus.IsolationFailed ? "OpenWithMonitor" + status
                : quarantined && protectedItem ? "OpenWithStopProtectionBeforeAllow"
                : !current.CanEdit ? current.Metadata.GetValueOrDefault("ReadOnlyReason")
                : null, protectedItem,
            string.IsNullOrEmpty(Generation(current)) ? null : current.IsEnabled, quarantined);
    }

    internal async Task<PipeResponse> DecideReviewAsync(OpenWithReviewRequest request,
        BackendUserContext context, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var path = OpenWithRegistration.ValidatePath(request.RegistryPath, context);
            SpecialMenuEntry? updated = null;
            var release = false;
            var current = await store.WithOpenWithAsync(state =>
            {
                if (!PendingPaths(state, context).Contains(path, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("OpenWithStaleReview");
                lock (OpenWithRegistration.Sync)
                {
                    var physical = SpecialMenuService.ReadOpenWithRegistration(path, context);
                    var review = CreateReview(state, context, path, physical);
                    if (review.Generation != request.Generation
                        || !(request.Decision == OpenWithReviewDecision.Accept ? review.CanAccept
                            : request.Decision == OpenWithReviewDecision.Reject && review.CanReject))
                        throw new InvalidOperationException("OpenWithStaleReview");
                    release = request.Decision == OpenWithReviewDecision.Accept && review.IsQuarantined;
                    if (!release)
                    {
                        var baseline = state.Users[context.Sid];
                        baseline.Registrations[path] = Stamp(physical);
                        baseline.Generations[path] = Generation(physical);
                        baseline.Reviews.Remove(path);
                        baseline.ReviewVersions.Remove(path);
                        baseline.Quarantines.Remove(path);
                        baseline.PendingIsolations.Remove(path);
                        updated = Decorate(physical, context, state);
                    }
                    return physical;
                }
            }, true, token);
            if (release)
            {
                updated = await SetVisibilityCoreAsync(current, true, resolveReview: true, context, token, request.Generation);
                ShellChangeNotifier.NotifyAssociationsChanged();
            }
            return new PipeResponse { Success = true, SpecialItem = updated,
                OpenWithReviews = await PendingCoreAsync(context, CancellationToken.None), Message = "Open With review saved." };
        }
        finally { _gate.Release(); }
    }

    internal async Task<PipeResponse> ReviewAsync(SpecialMenuEntry item, bool? protect,
        BackendUserContext context, CancellationToken token)
    {
        // Released clients used this command for acknowledgement. Keep one decision implementation.
        if (protect is null)
        {
            var path = OpenWithRegistration.ValidatePath(PathOf(item), context);
            var snapshot = await GetPendingReviewsAsync(context, token);
            var review = snapshot.Reviews.FirstOrDefault(r => r.RegistryPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (review is null || Generation(item) != Generation(review.Registration))
                throw new InvalidOperationException("OpenWithStaleReview");
            var result = await DecideReviewAsync(new(path, review.Generation, OpenWithReviewDecision.Accept), context, token);
            return result with { SpecialItems = await GetSnapshotAsync(context, CancellationToken.None) };
        }
        ObserveUser(context);
        await _gate.WaitAsync(token);
        try
        {
            var path = OpenWithRegistration.ValidatePath(PathOf(item), context);
            await store.WithOpenWithAsync(state =>
            {
                lock (OpenWithRegistration.Sync)
                {
                    if (protect == true)
                    {
                        if (state.Users.TryGetValue(context.Sid, out var pendingUser) && pendingUser.VisibilityIntents.ContainsKey(path))
                            throw new InvalidOperationException("OpenWithOperationUncertain");
                        if (pendingUser is not null && (pendingUser.PendingIsolations.ContainsKey(path)
                            || pendingUser.Reviews.GetValueOrDefault(path) is "New" or "Changed" or "ReRegistered" or "IsolationFailed"))
                            throw new InvalidOperationException("OpenWithReviewRequired");
                        if (!path.StartsWith($@"HKEY_USERS\{context.Sid}\", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("OpenWithUserProtectionOnly");
                        var current = SpecialMenuService.ReadOpenWithRegistration(path, context);
                        if (current.IsEnabled || !current.CanEdit
                            || current.Metadata.GetValueOrDefault("VisibilityOwnership") != "Owned"
                            || current.Metadata.GetValueOrDefault("EditStamp") != item.Metadata.GetValueOrDefault("EditStamp"))
                            throw new InvalidOperationException("OpenWithProtectionRequiresDisabled");
                        using var executable = OpenExecutable(current.TargetPath);
                        state.Rules[RuleKey(context.Sid, path)] = new OpenWithProtectionRule
                        {
                            Sid = context.Sid, Path = path, Authorized = true, LastVerifiedUtc = DateTimeOffset.UtcNow,
                            CommandIdentity = current.Metadata["CommandIdentity"], ExecutablePath = current.TargetPath!,
                            ExecutableHash = HashExecutable(executable, token)
                        };
                    }
                    else if (protect == false) state.Rules.Remove(RuleKey(context.Sid, path));
                    if (state.Users.TryGetValue(context.Sid, out var user)
                        && !user.VisibilityIntents.ContainsKey(path) && user.Reviews.GetValueOrDefault(path) is "Conflict" or "Uncertain")
                    {
                        user.Reviews.Remove(path);
                        user.ReviewVersions.Remove(path);
                        user.Quarantines.Remove(path);
                    }
                    return true;
                }
            }, true, token);
            var snapshot = await SnapshotCoreAsync(context, CancellationToken.None);
            return new PipeResponse { Success = true, SpecialItems = snapshot,
                SpecialItem = snapshot.FirstOrDefault(e => PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase)),
                Message = "Open With review saved." };
        }
        finally { _gate.Release(); }
    }

    private static SpecialMenuEntry Decorate(SpecialMenuEntry item, BackendUserContext context, OpenWithMonitorState state)
    {
        var path = PathOf(item);
        var metadata = new Dictionary<string, string>(item.Metadata);
        state.Users.TryGetValue(context.Sid, out var baseline);
        metadata["MonitoringStatus"] = baseline?.Reviews.GetValueOrDefault(path) ?? string.Empty;
        var protectedItem = state.Rules.TryGetValue(RuleKey(context.Sid, path), out var rule) && rule.Authorized;
        metadata["PersistentProtection"] = protectedItem.ToString();
        if (protectedItem && rule!.Outcome is "Conflict" or "Uncertain") metadata["MonitoringStatus"] = rule.Outcome;
        if (metadata.GetValueOrDefault("RuleOnly") == "true") metadata["MonitoringStatus"] = "Missing";
        if (baseline?.PendingIsolations.ContainsKey(path) == true) metadata["MonitoringStatus"] = "IsolationFailed";
        if (baseline?.VisibilityIntents.ContainsKey(path) == true) metadata["MonitoringStatus"] = "Uncertain";
        return item with { Metadata = metadata };
    }

    private static string ExecutableHash(string? path, CancellationToken token)
    {
        using var executable = OpenExecutable(path);
        return HashExecutable(executable, token);
    }

    private static FileStream OpenExecutable(string? path)
    {
        // Exact binary identity deliberately requires review for updates that change bytes or paths.
        // No service environment expansion, PATH search, network reads, or unverified publisher metadata.
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('%'))
            throw new InvalidOperationException("OpenWithIdentityUnavailable");
        if (new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new InvalidOperationException("OpenWithIdentityUnavailable");
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null;
             current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("OpenWithIdentityUnavailable");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length <= 128 * 1024 * 1024) return file;
            throw new InvalidOperationException("OpenWithIdentityUnavailable");
        }
        catch { file.Dispose(); throw; }
    }

    private static string HashExecutable(FileStream file, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int count;
        while ((count = file.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string RuleKey(string sid, string path) => sid + "|" + path;
    private static string PathOf(SpecialMenuEntry item) => item.Metadata.GetValueOrDefault("AppRegistryPath") ?? string.Empty;
    private static string Generation(SpecialMenuEntry item) => item.Metadata.GetValueOrDefault("RegistrationStamp") ?? string.Empty;
    private static bool IsLive(SpecialMenuEntry item) => item.Id != "openwith:use-store"
        && !string.IsNullOrEmpty(PathOf(item)) && string.IsNullOrEmpty(item.Metadata.GetValueOrDefault("DeletedPath"));
    private static string Stamp(SpecialMenuEntry item) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', item.Metadata.GetValueOrDefault("CommandIdentity"), item.Metadata.GetValueOrDefault("EditStamp"),
            item.Metadata.GetValueOrDefault("VisibilityStamp"), item.CommandText, item.CanEdit.ToString()))));

    private sealed class Settlement(DateTimeOffset started)
    {
        internal DateTimeOffset Started { get; } = started;
        private readonly Dictionary<string, (string Stamp, DateTimeOffset Since)> _candidates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _missing = new(StringComparer.OrdinalIgnoreCase);
        private string _whole = string.Empty;
        private DateTimeOffset _wholeSince;
        internal Dictionary<string, SpecialMenuEntry> Observe(Dictionary<string, SpecialMenuEntry> items, DateTimeOffset now)
        {
            var signature = string.Join('|', items.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + Generation(p.Value)));
            if (_whole != signature) { _whole = signature; _wholeSince = now; }
            foreach (var path in _candidates.Keys.Where(p => !items.ContainsKey(p)).ToArray())
                _missing.TryAdd(path, now);
            var result = new Dictionary<string, SpecialMenuEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, item) in items)
            {
                _missing.Remove(path);
                var stamp = Generation(item);
                if (!_candidates.TryGetValue(path, out var old) || old.Stamp != stamp)
                    _candidates[path] = (stamp, now);
                else if (now >= old.Since.AddSeconds(10)) result[path] = item;
            }
            return result;
        }
        internal bool InitialSnapshotSettled(DateTimeOffset now) => now >= _wholeSince.AddSeconds(10);
        internal void PruneAbsent(DateTimeOffset now)
        {
            foreach (var path in _missing.Where(p => now >= p.Value.AddSeconds(10)).Select(p => p.Key).ToArray())
            {
                _candidates.Remove(path);
                _missing.Remove(path);
            }
        }
        internal bool AbsenceSettled(string path, DateTimeOffset now)
        {
            _missing.TryAdd(path, now);
            if (now < _missing[path].AddSeconds(10)) return false;
            _candidates.Remove(path);
            _missing.Remove(path);
            return true;
        }
    }
}
