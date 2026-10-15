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
        await store.WithOpenWithAsync(state =>
        {
            if (!state.Users.TryGetValue(context.Sid, out var baseline))
            {
                // First complete, settled user snapshot adopts current physical state, including zero items.
                state.Users[context.Sid] = new OpenWithUserBaseline
                {
                    Registrations = live.ToDictionary(p => p.Key, p => Stamp(p.Value), StringComparer.OrdinalIgnoreCase)
                };
                return false;
            }
            foreach (var (path, item) in settled)
            {
                var stamp = Stamp(item);
                if (!baseline.Registrations.TryGetValue(path, out var previous))
                {
                    baseline.Reviews[path] = snapshot.Any(e => !IsLive(e) && PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase))
                        ? "ReRegistered" : "New";
                    notifications.Add(item);
                }
                else if (previous != stamp && !state.Rules.ContainsKey(RuleKey(context.Sid, path)))
                {
                    baseline.Reviews[path] = "Changed";
                    notifications.Add(item);
                }
                baseline.Registrations[path] = stamp;
            }
            foreach (var path in baseline.Registrations.Keys.Where(p => !live.ContainsKey(p)
                         && settlement.AbsenceSettled(p, now)).ToArray())
            {
                baseline.Registrations.Remove(path);
                baseline.Reviews.Remove(path);
            }
            return true;
        }, save: true, token);

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
            Changed?.Invoke(this, new BackendNotification
            {
                Kind = PipeNotificationKind.OpenWithChanged, SpecialKind = SpecialMenuKind.OpenWith,
                UserSid = context.Sid, SpecialItem = notifications[0],
                MessageResourceKey = "OpenWithMonitoringChanged", Message = "Open With registrations changed. Review the Open With page."
            });
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
            if (item.Id != "openwith:use-store")
            {
                var path = OpenWithRegistration.ValidatePath(PathOf(item), context);
                // Remove intent durably first. Failure prevents a misleading enable that would be undone.
                await store.WithOpenWithAsync(state =>
                {
                    if (enabled) state.Rules.Remove(RuleKey(context.Sid, path));
                    if (state.Users.TryGetValue(context.Sid, out var baseline)) baseline.Reviews.Remove(path);
                    return true;
                }, true, token);
            }
            var updated = SpecialMenuService.SetOpenWithEnabled(item, enabled, context);
            return await store.WithOpenWithAsync(state =>
            {
                if (state.Users.TryGetValue(context.Sid, out var baseline) && IsLive(updated))
                    baseline.Registrations[PathOf(updated)] = Stamp(updated);
                return Decorate(updated, context, state);
            }, true, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    internal async Task<PipeResponse> ReviewAsync(SpecialMenuEntry item, bool? protect,
        BackendUserContext context, CancellationToken token)
    {
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
                    else
                    {
                        // Acknowledge exactly the generation shown to the user, including read-only entries.
                        var current = SpecialMenuService.ReadOpenWithRegistration(path, context);
                        if (Stamp(current) != Stamp(item)) throw new InvalidOperationException("OpenWithOperationUncertain");
                        if (current.IsEnabled) state.Rules.Remove(RuleKey(context.Sid, path));
                        if (state.Users.TryGetValue(context.Sid, out var baseline)) baseline.Registrations[path] = Stamp(current);
                    }
                    if (state.Users.TryGetValue(context.Sid, out var user)) user.Reviews.Remove(path);
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
            var signature = string.Join('|', items.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + Stamp(p.Value)));
            if (_whole != signature) { _whole = signature; _wholeSince = now; }
            foreach (var path in _candidates.Keys.Where(p => !items.ContainsKey(p)).ToArray())
                _missing.TryAdd(path, now);
            var result = new Dictionary<string, SpecialMenuEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, item) in items)
            {
                _missing.Remove(path);
                var stamp = Stamp(item);
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
