using System.IO;
using System.Collections.Concurrent;
using System.Xml.Linq;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using ContextMenuMgr.Contracts;
using Windows.Management.Deployment;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Projects backend declarations and verifies registry state after user-scoped Win11 changes.
/// </summary>
public sealed class Windows11ContextMenuService : IDisposable
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> Logos = new(StringComparer.OrdinalIgnoreCase);
    private readonly IBackendClient _backendClient;
    private readonly LocalizationService _localization;
    private readonly object _refreshSync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _refreshTask;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly HashSet<string> _pendingItems = new(StringComparer.OrdinalIgnoreCase);
    public bool IsOperationPending(string id) => _pendingItems.Contains(id);

    private readonly HashSet<string> _failedItems = new(StringComparer.OrdinalIgnoreCase);
    public bool HasSnapshot { get; private set; }
    public bool IsSnapshotComplete { get; private set; }
    public bool CanMutate => _backendClient.IsConnected || _hasLiveSnapshot;
    private bool _hasLiveSnapshot;
    public bool RequiresServiceUpdate { get; private set; }
    public bool HasOperationError(string id) => _failedItems.Contains(id);

    private readonly HashSet<string> _uncertainItems = new(StringComparer.OrdinalIgnoreCase);

    public bool IsStateUncertain(string id) => _uncertainItems.Contains(id);

    /// <summary>
    /// Gets or sets the current items.
    /// </summary>
    public IReadOnlyList<Windows11ContextMenuItemDefinition> CurrentItems { get; private set; } = [];

    public event EventHandler? ItemsChanged;
    public event EventHandler? HasLoaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="Windows11ContextMenuService"/> class.
    /// </summary>
    public Windows11ContextMenuService(IBackendClient backendClient, LocalizationService localization)
    {
        _backendClient = backendClient;
        _localization = localization;
        _backendClient.ItemsRemoved += OnItemsRemoved;
    }

    /// <summary>
    /// Gets a value indicating whether this instance is supported.
    /// </summary>
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Sets enabled Async.
    /// </summary>
    public async Task SetEnabledAsync(string id, string displayName, bool enable, CancellationToken cancellationToken = default, bool applicationScope = false)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (!_pendingItems.Add(id))
            throw new InvalidOperationException("A change to this application is already in progress.");
        var entered = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            entered = true;
            if (_uncertainItems.Contains(id))
                throw new InvalidOperationException(_localization.Translate("Windows11StateUncertain"));
            var selected = CurrentItems.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var targetIds = CurrentItems.Where(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || applicationScope && selected?.SourceKind == Windows11ContextMenuSourceKind.PackagedCom
                    && item.SourceKind == Windows11ContextMenuSourceKind.PackagedCom
                    && item.Package.FamilyName.Equals(selected.Package.FamilyName, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var restoreMachine = enable && CurrentItems.Any(item => targetIds.Contains(item.Id) && item.IsMachineBlocked);
            if (targetIds.Count == 0) targetIds.Add(id);
            Exception? requestError = null;
            IReadOnlyList<ContextMenuEntry> verified = [];
            try
            {
                var request = enable
                    ? _backendClient.RemoveWin11BlockedItemAsync(id, restoreMachine, Guid.NewGuid(), cancellationToken, applicationScope)
                    : _backendClient.SetWin11BlockedItemAsync(id, displayName, blockMachine: false, Guid.NewGuid(), cancellationToken, applicationScope);
                verified = await request.WaitAsync(BackendRequestBudgets.Windows11Mutation, cancellationToken);
            }
            catch (Exception ex) { requestError = ex; }
            // Successful responses contain physical read-back committed to the
            // state store. Only missing/lost responses need another snapshot.
            var returnedIds = verified.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasVerifiedResult = requestError is null && targetIds.IsSubsetOf(returnedIds)
                && verified.Where(entry => targetIds.Contains(entry.Id)).All(entry => entry.IsEnabled == enable);
            try
            {
                if (hasVerifiedResult)
                {
                    var definitions = await CreateDefinitionsAsync(verified, cancellationToken);
                    CurrentItems = CurrentItems.Where(item => !returnedIds.Contains(item.Id))
                        .Concat(definitions).ToArray();
                    _uncertainItems.Remove(id);
                    _failedItems.Remove(id);
                }
                else await RefreshCoreAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _uncertainItems.Add(id);
                throw new InvalidOperationException(_localization.Translate("Windows11StateUncertain"), requestError ?? ex);
            }
            RequiresServiceUpdate = applicationScope && requestError?.Message.Contains(
                "The request was missing required data.", StringComparison.OrdinalIgnoreCase) == true;
            if (requestError is not null && requestError is not TimeoutException) throw requestError;
            var actual = CurrentItems.Where(item => targetIds.Contains(item.Id)).ToArray();
            var matched = actual.Length == targetIds.Count && actual.All(item => item.IsEnabled == enable);
            if (actual.Length != targetIds.Count || requestError is TimeoutException && !matched)
                _uncertainItems.Add(id);
            if (!matched)
                throw requestError ?? new InvalidOperationException(_localization.Translate("Windows11StateUncertain"));
        }
        catch
        {
            _failedItems.Add(id);
            throw;
        }
        finally
        {
            _pendingItems.Remove(id);
            if (entered) _operationGate.Release();
            OnItemsChanged();
        }
    }

    /// <summary>
    /// Executes refresh Async.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported) return Task.CompletedTask;
        lock (_refreshSync)
        {
            if (_refreshTask is not { IsCompleted: false }) _refreshTask = RefreshQueuedAsync();
            return _refreshTask.WaitAsync(cancellationToken);
        }
    }

    private async Task RefreshQueuedAsync()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        budget.CancelAfter(BackendRequestBudgets.Windows11Snapshot);
        await _operationGate.WaitAsync(budget.Token);
        try { await RefreshCoreAsync(budget.Token); }
        finally { _operationGate.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await RestoreCacheAsync(cancellationToken);
        var entries = await _backendClient.GetWin11ContextMenuSnapshotAsync(cancellationToken)
            .WaitAsync(BackendRequestBudgets.Windows11Snapshot, cancellationToken);
        entries = MenuListData.Current(entries);
        IsSnapshotComplete = MenuListData.AllowsRemoval(entries);
        var fresh = await CreateDefinitionsAsync(entries, cancellationToken);
        CurrentItems = MenuListData.AllowsRemoval(entries) ? fresh
            : fresh.Concat(CurrentItems.Where(old => !fresh.Any(item => item.Id.Equals(old.Id, StringComparison.OrdinalIgnoreCase)))).ToArray();
        HasSnapshot = true;
        _hasLiveSnapshot = true;
        _uncertainItems.Clear();
        _failedItems.Clear();
        OnItemsChanged();
        HasLoaded?.Invoke(this, EventArgs.Empty);
    }

    public async Task RestoreCacheAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported || HasSnapshot) return;
        var cached = await _backendClient.ReadCachedListAsync<ContextMenuEntry>(new PipeRequest {
            Command = PipeCommand.GetWin11ContextMenuSnapshot }, cancellationToken);
        if (cached is null || HasSnapshot) return;
        var definitions = await CreateDefinitionsAsync(cached, cancellationToken);
        if (HasSnapshot) return;
        CurrentItems = definitions;
        HasSnapshot = true;
        OnItemsChanged();
    }

    /// <summary>
    /// Ensures loaded Async.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return;
        }

        if (!_hasLiveSnapshot)
        {
            await RefreshAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Loads logo.
    /// </summary>
    public static async Task<ImageSource?> LoadLogo(Windows11ContextMenuItemDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.SourceKind != Windows11ContextMenuSourceKind.PackagedCom) return null;
        var package = definition.Package;
        var pending = Logos.GetOrAdd(package.FullName, _ => new Lazy<Task<ImageSource?>>(
            () => Task.Run(() => ReadLogoAsync(package))));
        var image = await pending.Value.WaitAsync(cancellationToken);
        // Do not permanently cache missing resources; a later refresh can retry.
        if (image is null) Logos.TryRemove(new KeyValuePair<string, Lazy<Task<ImageSource?>>>(package.FullName, pending));
        return image;
    }

    private static async Task<ImageSource?> ReadLogoAsync(Windows11PackageInfo package)
    {
        try
        {
            var installed = new PackageManager().FindPackageForUser(string.Empty, package.FullName);
            if (installed is null) return null;
            var apps = await installed.GetAppListEntriesAsync();
            // The UI controls the whole package. Prefer its named application,
            // then a stable representative when the package exposes several apps.
            var app = apps.OrderByDescending(entry => string.Equals(entry.DisplayInfo.DisplayName,
                    package.DisplayName, StringComparison.CurrentCultureIgnoreCase))
                .ThenBy(entry => entry.AppUserModelId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var icon = TryReadAppIcon(installed, app?.AppUserModelId);
            if (icon is not null) return icon;
            if (app is null) return null;
            using var logo = await app.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48)).OpenReadAsync();
            using var fs = logo.AsStreamForRead();
            return DecodeLogo(fs);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(nameof(Windows11ContextMenuService),
                $"Package icon unavailable: Package={package.FullName}, HResult=0x{ex.HResult:X8}.");
            return null;
        }
    }

    private static ImageSource? TryReadAppIcon(Windows.ApplicationModel.Package package, string? appUserModelId)
    {
        try
        {
            var root = Path.GetFullPath(package.InstalledLocation.Path) + Path.DirectorySeparatorChar;
            var manifest = XDocument.Load(Path.Combine(root, "AppxManifest.xml"));
            var appId = appUserModelId?.Split('!').Last();
            var app = manifest.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "Applications")?
                .Elements().Where(element => element.Name.LocalName == "Application")
                .OrderBy(element => (string?)element.Attribute("Id"), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(element => appId is null || (string?)element.Attribute("Id") == appId);
            var logo = app?.Elements().FirstOrDefault(element => element.Name.LocalName == "VisualElements")?
                .Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Square44x44Logo")?.Value;
            if (string.IsNullOrWhiteSpace(logo)) return null;
            var path = Path.GetFullPath(Path.Combine(root, logo));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
            var extension = Path.GetExtension(path);
            // Use app-list assets without tile padding when the package supplies
            // them; let Windows resolve other resource layouts through GetLogo.
            foreach (var size in new[] { 48, 64, 96, 256, 32, 24, 20, 16 })
            foreach (var form in new[] { "_altform-unplated", "" })
            {
                var candidate = $"{stem}.targetsize-{size}{form}{extension}";
                if (!File.Exists(candidate)) continue;
                using var stream = File.OpenRead(candidate);
                return DecodeLogo(stream);
            }
            return null;
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning(nameof(Windows11ContextMenuService),
                $"Package app-list icon unavailable: Package={package.Id.FullName}, HResult=0x{ex.HResult:X8}.");
            return null;
        }
    }

    private static ImageSource DecodeLogo(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        _backendClient.ItemsRemoved -= OnItemsRemoved;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    #region Private methods

    private void OnItemsRemoved(object? sender, MenuItemsRemovedEventArgs e)
    {
        if (e.Request.SpecialItem is not null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Apply()
        {
            CurrentItems = CurrentItems.Where(item => !e.ItemIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
            OnItemsChanged();
        }
        if (dispatcher is not null && !dispatcher.CheckAccess()) _ = dispatcher.BeginInvoke((Action)Apply); else Apply();
    }

    private void OnItemsChanged()
    {
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static Windows11ContextMenuItemDefinition CreateDefinition(ContextMenuEntry entry)
    {
        if (entry.Windows11SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore)
        {
            return new Windows11ContextMenuItemDefinition(
                entry.Id,
                entry.DisplayName,
                new Windows11PackageInfo(
                    "CommandStore",
                    "System Command",
                    string.Empty)
                {
                    FamilyName = entry.KeyName,
                    PublisherDisplayName = "Microsoft Windows"
                },
                [],
                new Windows11ComServerInfo(entry.HandlerClsid, entry.FilePath, entry.DisplayName),
                [ToContextType(entry.Category)])
            {
                IsEnabled = entry.IsEnabled,
                SourceKind = entry.Windows11SourceKind,
                IsProtected = entry.IsProtectedSystemItem,
                Entry = entry
            };
        }

        var packageFullName = entry.Windows11PackageFullName
            ?? ContextMenuApprovalIdentity.ExtractWin11PackageKey(entry.RegistryPath);
        if (string.IsNullOrWhiteSpace(packageFullName)
            || string.Equals(packageFullName, entry.RegistryPath, StringComparison.OrdinalIgnoreCase))
        {
            packageFullName = entry.DisplayName;
        }

        var installPath = entry.Windows11PackageInstallPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(installPath) && !string.IsNullOrWhiteSpace(entry.FilePath))
        {
            installPath = File.Exists(entry.FilePath)
                ? Path.GetDirectoryName(entry.FilePath) ?? string.Empty
                : string.Empty;
        }

        var contextTypes = entry.Windows11ContextTypes.Count > 0
            ? entry.Windows11ContextTypes
            : [ToContextType(entry.Category)];
        var contextMenus = entry.Windows11Verbs
            .Select(verb => new Windows11ContextMenuVerb(verb.Id, verb.Id, [verb.ContextType]))
            .ToArray();
        var comDisplayName = entry.Windows11ComServerDisplayName
            ?? entry.Windows11ComClassDisplayName
            ?? entry.DisplayName;

        return new Windows11ContextMenuItemDefinition(
            entry.Id,
            entry.DisplayName,
            new Windows11PackageInfo(
                packageFullName,
                entry.Windows11PackageDisplayName ?? packageFullName,
                installPath)
            {
                FamilyName = entry.Windows11PackageFamilyName ?? packageFullName.Split('_')[0],
                PublisherDisplayName = entry.Windows11PackagePublisherDisplayName ?? string.Empty
            },
            contextMenus,
            new Windows11ComServerInfo(entry.HandlerClsid, entry.FilePath, comDisplayName),
            contextTypes)
        {
            IsEnabled = entry.IsEnabled,
            IsMachineBlocked = entry.IsMachineBlocked,
            SourceKind = entry.Windows11SourceKind,
            IsProtected = entry.IsProtectedSystemItem,
            Entry = entry
        };
    }

    private static Task<Windows11ContextMenuItemDefinition[]> CreateDefinitionsAsync(
        IReadOnlyList<ContextMenuEntry> entries, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            // Localized package resources can be unavailable to LocalSystem. Read
            // display-only metadata as the frontend user, never as another SID.
            var definitions = entries.Select(CreateDefinition).ToArray();
            PackageManager? manager = null;
            foreach (var group in definitions
                         .Where(item => item.SourceKind == Windows11ContextMenuSourceKind.PackagedCom)
                         .GroupBy(item => item.Package.FullName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    manager ??= new PackageManager();
                    var package = manager.FindPackageForUser(string.Empty, group.Key);
                    if (package is null) continue;
                    var displayName = package.DisplayName;
                    if (string.IsNullOrWhiteSpace(displayName)
                        || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(displayName, package.Id.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(displayName, group.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var definition in group)
                    {
                        var index = Array.IndexOf(definitions, definition);
                        definitions[index] = definition with { Package = definition.Package with { DisplayName = displayName } };
                    }
                }
                catch (Exception ex)
                {
                    // A presentation failure must not hide declarations or alter
                    // discovery completeness / authoritative switch state.
                    FrontendDebugLog.Warning(nameof(Windows11ContextMenuService),
                        $"Package display name unavailable: Package={group.Key}, HResult=0x{ex.HResult:X8}.");
                }
            }
            return definitions;
        }, cancellationToken);

    private static string ToContextType(ContextMenuCategory category) => category switch
    {
        ContextMenuCategory.DirectoryBackground => @"Directory\Background",
        ContextMenuCategory.DesktopBackground => "DesktopBackground",
        ContextMenuCategory.Drive => "Drive",
        ContextMenuCategory.Folder or ContextMenuCategory.Directory => "Directory",
        _ => "*"
    };

    #endregion
}

#region Public type definitions for ViewModel compatibility

/// <summary>
/// Represents a Win11 context menu item definition.
/// </summary>
public sealed record Windows11ContextMenuItemDefinition(
    string Id,
    string DisplayName,
    Windows11PackageInfo Package,
    IReadOnlyList<Windows11ContextMenuVerb> ContextMenus,
    Windows11ComServerInfo ComServer,
    IReadOnlyList<string> ContextTypes)
{
    public bool IsEnabled { get; init; } = true;
    public bool IsMachineBlocked { get; init; } = false;
    public Windows11ContextMenuSourceKind SourceKind { get; init; } = Windows11ContextMenuSourceKind.PackagedCom;
    public bool IsProtected { get; init; }
    public ContextMenuEntry? Entry { get; init; }
}

/// <summary>
/// Represents a Win11 package info.
/// </summary>
public sealed record Windows11PackageInfo(
    string FullName,
    string DisplayName,
    string InstallPath)
{
    public string PublisherDisplayName { get; init; } = string.Empty;
    public string FamilyName { get; init; } = DisplayName;
}

/// <summary>
/// Represents a Win11 context menu verb.
/// </summary>
public sealed record Windows11ContextMenuVerb(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ContextTypes);

/// <summary>
/// Represents a Win11 COM server info.
/// </summary>
public sealed record Windows11ComServerInfo(
    string? Id,
    string? Path,
    string? DisplayName);

#endregion
