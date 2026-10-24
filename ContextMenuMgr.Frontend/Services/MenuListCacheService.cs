using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>Disposable presentation snapshots, independent of backend state and authorization.</summary>
public sealed class MenuListCacheService : IAsyncDisposable
{
    private const long FileLimit = 32L * 1024 * 1024;
    private const long TotalLimit = 128L * 1024 * 1024;
    private readonly string _directory;
    private readonly string? _fingerprint;
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<(Type, string), IListState> _states = [];
    private readonly Dictionary<(Type, string), (long Version, object? Item)> _changes = new(ItemKeyComparer.Instance);
    private readonly Dictionary<(Type, string), int> _pending = new(ItemKeyComparer.Instance);
    private long _version;
    private int _epoch;
    private Task _pendingWrites = Task.CompletedTask;

    public MenuListCacheService() : this(Path.Combine(RuntimePaths.DataDirectory, "Cache", "MenuLists"), GetFingerprint()) { }

    public MenuListCacheService(string directory, string? fingerprint)
    {
        _fingerprint = fingerprint;
        _directory = fingerprint is null ? directory : Path.Combine(directory, RuntimeIdentityFingerprint.Prefix(fingerprint), fingerprint);
    }

    public long Version { get { lock (_sync) return _version; } }
    public int Generation { get { lock (_sync) return _epoch; } }

    public static string Scope(PipeCommand command, object? argument = null)
        => command + ":" + JsonSerializer.Serialize(argument);

    public Task<MenuListSnapshot<T>?> ReadAsync<T>(string scope, CancellationToken token = default)
    {
        lock (_sync)
            if (_states.TryGetValue((typeof(T), scope), out var state))
                return Task.FromResult<MenuListSnapshot<T>?>(new MenuListSnapshot<T>(((ListState<T>)state).Items, state.Complete, true));
        return Task.Run(() => ReadCoreAsync<T>(scope, token), token);
    }

    private async Task<MenuListSnapshot<T>?> ReadCoreAsync<T>(string scope, CancellationToken token)
    {
        int epoch;
        lock (_sync)
        {
            epoch = _epoch;
            if (_states.TryGetValue((typeof(T), scope), out var state))
                return new MenuListSnapshot<T>(((ListState<T>)state).Items, state.Complete, true);
        }
        if (_fingerprint is null) return null;
        await _diskGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var path = GetPath<T>(scope);
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > FileLimit || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            await using var stream = File.OpenRead(path);
            var saved = await JsonSerializer.DeserializeAsync<CacheFile<T>>(stream, cancellationToken: token).ConfigureAwait(false);
            if (saved is null || saved.SchemaVersion != 1 || saved.IdentityVersion != RuntimeIdentityFingerprint.SchemaVersion
                || saved.IdentityKind != RuntimeIdentityFingerprint.Kind || saved.Fingerprint != _fingerprint || saved.Scope != scope
                || saved.Items is null) return null;
            if (saved.Items.Any(item => string.IsNullOrWhiteSpace(Id(item)))
                || saved.Items.Select(Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.Items.Count) return null;
            lock (_sync)
            {
                if (epoch != _epoch) return null;
                // A live scan/operation may have finished during disk I/O.
                if (!_states.ContainsKey((typeof(T), scope)))
                    _states[(typeof(T), scope)] = new ListState<T>(scope, saved.Items, true);
                var current = (ListState<T>)_states[(typeof(T), scope)];
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return new MenuListSnapshot<T>(current.Items, current.Complete, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            FrontendDebugLog.Warning(nameof(MenuListCacheService), $"Cache read skipped: {ex.Message}");
            return null;
        }
        finally { _diskGate.Release(); }
    }

    public async Task<MenuListSnapshot<T>> MergeAsync<T>(string scope, IReadOnlyList<T> scanned, bool complete,
        long scanVersion, CancellationToken token = default, int? generation = null)
    {
        ListState<T> state;
        int epoch;
        lock (_sync)
        {
            if (generation is int expected && expected != _epoch) throw new OperationCanceledException("The list cache was reset during discovery.");
            epoch = _epoch;
            var previous = _states.GetValueOrDefault((typeof(T), scope)) as ListState<T>;
            var result = scanned.ToDictionary(Id, StringComparer.OrdinalIgnoreCase);
            if (previous is not null)
                foreach (var item in previous.Items)
                {
                    var id = Id(item);
                    if (result.TryGetValue(id, out var scannedItem) && MenuListData.Equivalent(item, scannedItem)) result[id] = item;
                    if (!complete || ChangedSince<T>(id, scanVersion)) result.TryAdd(id, item);
                }
            foreach (var id in result.Keys.ToArray())
            {
                if (_changes.TryGetValue((typeof(T), id), out var change) && change.Version > scanVersion)
                {
                    if (change.Item is T updated) result[id] = updated;
                    else result.Remove(id);
                }
                else if (_pending.ContainsKey((typeof(T), id)))
                {
                    var old = previous is null ? default : previous.Items.FirstOrDefault(item => Id(item).Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (old is not null) result[id] = old;
                    else result.Remove(id);
                }
            }
            state = new ListState<T>(scope, result.Values.ToArray(), complete);
            _states[(typeof(T), scope)] = state;
        }
        if (state.Complete) await SaveAsync(state, epoch, token).ConfigureAwait(false);
        // The merged list is complete for presentation only if the scan itself was complete.
        return new MenuListSnapshot<T>(state.Items, complete, captureCurrent: () =>
        {
            lock (_sync)
            {
                if (epoch != _epoch) throw new OperationCanceledException("The list snapshot predates a reset.");
                var latest = (ListState<T>)_states[(typeof(T), scope)];
                return new MenuListSnapshot<T>(latest.Items, latest.Complete);
            }
        });
    }

    private bool ChangedSince<T>(string id, long version)
        => _pending.ContainsKey((typeof(T), id)) || _changes.TryGetValue((typeof(T), id), out var change) && change.Version > version;

    public T? FindItem<T>(string id)
    {
        lock (_sync)
            return _states.Values.OfType<ListState<T>>().SelectMany(state => state.Items)
                .FirstOrDefault(item => Id(item).Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public void BeginMutation<T>(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_sync)
        {
            var key = (typeof(T), id);
            _pending[key] = _pending.GetValueOrDefault(key) + 1;
            _version++;
        }
    }

    public bool ApplyResult<T>(string id, T? item, bool removed = false, bool completesMutation = true, long? observedVersion = null, int? generation = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        IListState[] states;
        int epoch;
        lock (_sync)
        {
            var key = (typeof(T), id);
            if (!completesMutation && ChangedSince<T>(id, observedVersion ?? long.MaxValue)) return false;
            if (completesMutation)
            {
                if (_pending.GetValueOrDefault(key) > 1) _pending[key]--;
                else _pending.Remove(key);
            }
            if (generation is int expected && expected != _epoch) return false;
            if (item is not null || removed)
            {
                _changes[key] = (++_version, removed ? null : item);
                foreach (var state in _states.Values.OfType<ListState<T>>()) state.Apply(id, item, removed);
            }
            else
            {
                var known = _states.Values.OfType<ListState<T>>().SelectMany(state => state.Items)
                    .FirstOrDefault(entry => Id(entry).Equals(id, StringComparison.OrdinalIgnoreCase));
                if (known is not null) _changes[key] = (++_version, known);
                else _version++;
            }
            states = _states.Values.OfType<ListState<T>>().Where(state => state.Complete
                && (removed || state.Items.Any(entry => Id(entry).Equals(id, StringComparison.OrdinalIgnoreCase)))).Cast<IListState>().ToArray();
            epoch = _epoch;
        }
        lock (_sync) _pendingWrites = SaveStatesAfterAsync(_pendingWrites, states, epoch);
        return true;
    }

    public async Task InvalidateAsync()
    {
        lock (_sync) { _epoch++; _version++; _states.Clear(); _changes.Clear(); }
        if (_fingerprint is null) return;
        await _diskGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_directory))
                foreach (var path in Directory.EnumerateFiles(_directory, "*.json")) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { FrontendDebugLog.Warning(nameof(MenuListCacheService), $"Cache reset failed: {ex.Message}"); }
        finally { _diskGate.Release(); }
    }

    public Task FlushAsync() { lock (_sync) return _pendingWrites; }
    public async ValueTask DisposeAsync() => await FlushAsync().ConfigureAwait(false);

    private async Task SaveStatesAfterAsync(Task previous, IListState[] states, int epoch)
    {
        await previous.ConfigureAwait(false);
        await SaveStatesAsync(states, epoch).ConfigureAwait(false);
    }

    private async Task SaveStatesAsync(IListState[] states, int epoch)
    {
        foreach (var state in states) await SaveAsync(state, epoch, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SaveAsync(IListState state, int epoch, CancellationToken token)
    {
        if (_fingerprint is null) return;
        await _diskGate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            byte[] bytes;
            long revision;
            lock (_sync)
            {
                if (state.HasPending(_pending.Keys)) return;
                if (epoch != _epoch || !ReferenceEquals(_states.GetValueOrDefault((state.ItemType, state.Scope)), state)) return;
                revision = state.Revision;
                bytes = state.Serialize(_fingerprint);
            }
            if (bytes.LongLength > FileLimit) return;
            Directory.CreateDirectory(_directory);
            temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            lock (_sync)
            {
                if (epoch != _epoch || revision != state.Revision || state.HasPending(_pending.Keys)
                    || !ReferenceEquals(_states.GetValueOrDefault((state.ItemType, state.Scope)), state)) return;
                File.Move(temporary, GetPath(state.ItemType, state.Scope), overwrite: true);
            }
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total <= TotalLimit) break;
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { FrontendDebugLog.Warning(nameof(MenuListCacheService), $"Cache write skipped: {ex.Message}"); }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _diskGate.Release();
        }
    }

    private string GetPath<T>(string scope) => GetPath(typeof(T), scope);
    private string GetPath(Type type, string scope) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type.Name + "|" + scope))) + ".json");

    private static string? GetFingerprint()
    {
        try
        {
            var machine = RuntimeIdentityFingerprint.ReadMachineGuid();
            using var identity = WindowsIdentity.GetCurrent();
            return machine is not null && identity.User is { } sid ? RuntimeIdentityFingerprint.Compute(machine, sid.Value) : null;
        }
        catch (Exception ex)
        { FrontendDebugLog.Warning(nameof(MenuListCacheService), $"Cache identity unavailable: {ex.Message}"); return null; }
    }

    internal static string Id<T>(T item) => item switch
    {
        ContextMenuEntry entry => entry.Id,
        SpecialMenuEntry entry => entry.Id,
        ShellSubMenuItem entry => entry.Id,
        _ => throw new NotSupportedException(typeof(T).Name)
    };

    private sealed class ItemKeyComparer : IEqualityComparer<(Type, string)>
    {
        public static readonly ItemKeyComparer Instance = new();
        public bool Equals((Type, string) left, (Type, string) right)
            => left.Item1 == right.Item1 && StringComparer.OrdinalIgnoreCase.Equals(left.Item2, right.Item2);
        public int GetHashCode((Type, string) key)
            => HashCode.Combine(key.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Item2));
    }

    private interface IListState
    {
        string Scope { get; }
        Type ItemType { get; }
        bool Complete { get; }
        long Revision { get; }
        bool HasPending(IEnumerable<(Type, string)> pending);
        byte[] Serialize(string fingerprint);
    }

    private sealed class ListState<T>(string scope, IReadOnlyList<T> items, bool complete) : IListState
    {
        public string Scope => scope;
        public Type ItemType => typeof(T);
        public bool Complete => complete;
        public long Revision { get; private set; }
        public IReadOnlyList<T> Items { get; private set; } = items;
        public void Apply(string id, T? item, bool removed)
        {
            if (!Items.Any(entry => Id(entry).Equals(id, StringComparison.OrdinalIgnoreCase))) return;
            Items = removed || item is null
                ? Items.Where(entry => !Id(entry).Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray()
                : Items.Select(entry => Id(entry).Equals(id, StringComparison.OrdinalIgnoreCase) ? item : entry).ToArray();
            Revision++;
        }
        public bool HasPending(IEnumerable<(Type, string)> pending)
            => pending.Any(key => key.Item1 == typeof(T) && Items.Any(item => Id(item).Equals(key.Item2, StringComparison.OrdinalIgnoreCase)));
        public byte[] Serialize(string fingerprint) => JsonSerializer.SerializeToUtf8Bytes(new CacheFile<T>(
            1, RuntimeIdentityFingerprint.SchemaVersion, RuntimeIdentityFingerprint.Kind, fingerprint, scope, DateTimeOffset.UtcNow, Items));
    }

    private sealed record CacheFile<T>(int SchemaVersion, int IdentityVersion, string IdentityKind,
        string Fingerprint, string Scope, DateTimeOffset SavedAtUtc, IReadOnlyList<T> Items);
}
