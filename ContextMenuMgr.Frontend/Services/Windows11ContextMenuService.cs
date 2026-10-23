using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Projects backend declarations and verifies registry state after user-scoped Win11 changes.
/// </summary>
public sealed class Windows11ContextMenuService
{
    private readonly IBackendClient _backendClient;
    private readonly LocalizationService _localization;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly HashSet<string> _pendingItems = new(StringComparer.OrdinalIgnoreCase);
    public bool IsOperationPending(string id) => _pendingItems.Contains(id);

    private readonly HashSet<string> _failedItems = new(StringComparer.OrdinalIgnoreCase);
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
                    CurrentItems = CurrentItems.Where(item => !returnedIds.Contains(item.Id))
                        .Concat(verified.Select(CreateDefinition)).ToArray();
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
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try { await RefreshCoreAsync(cancellationToken); }
        finally { _operationGate.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var entries = await _backendClient.GetWin11ContextMenuSnapshotAsync(cancellationToken)
            .WaitAsync(BackendRequestBudgets.Windows11Snapshot, cancellationToken);
        CurrentItems = entries.Select(CreateDefinition).ToArray();
        _uncertainItems.Clear();
        _failedItems.Clear();
        OnItemsChanged();
        HasLoaded?.Invoke(this, EventArgs.Empty);
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

        if (CurrentItems.Count == 0)
        {
            await RefreshAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Loads logo.
    /// </summary>
    public static async Task<ImageSource?> LoadLogo(Windows11PackageInfo package, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(package.LogoPath) || !File.Exists(package.LogoPath))
            {
                return null;
            }

            using var fs = new FileStream(package.LogoPath, FileMode.Open, FileAccess.Read);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = fs;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    #region Private methods

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
    public string LogoPath { get; init; } = string.Empty;
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
