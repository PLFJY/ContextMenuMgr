using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Provides Win11 context menu services.
/// Note: Registry write operations should be delegated to backend service in future refactoring.
/// Current implementation maintains backward compatibility while marking areas for improvement.
/// </summary>
public sealed class Windows11ContextMenuService
{
    private readonly IBackendClient _backendClient;
    private readonly LocalizationService _localization;

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
    public async Task SetEnabledAsync(string id, string displayName, bool enable, CancellationToken cancellationToken = default)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var handlerClsid = ExtractHandlerId(id);
        if (enable)
        {
            await _backendClient.RemoveWin11BlockedItemAsync(handlerClsid, unblockMachine: false, Guid.NewGuid(), cancellationToken);
        }
        else
        {
            await _backendClient.SetWin11BlockedItemAsync(handlerClsid, displayName, blockMachine: false, Guid.NewGuid(), cancellationToken);
        }

        UpdateCachedState(handlerClsid, enable);
    }

    /// <summary>
    /// Gets is Enabled Async.
    /// </summary>
    public async Task<bool> IsEnabledAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return true;
        }

        try
        {
            var blockedItems = await _backendClient.GetWin11BlockedItemsAsync(cancellationToken);
            var normalizedId = NormalizeGuid(ExtractHandlerId(id));
            return !blockedItems.Any(item =>
                string.Equals(NormalizeGuid(item.Clsid), normalizedId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Fallback to local check if backend is unavailable
            return true;
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

        try
        {
            var entries = await _backendClient.GetWin11ContextMenuSnapshotAsync(cancellationToken);
            var definitions = entries
                .Select(CreateDefinition)
                .ToList();

            CurrentItems = definitions;
            OnItemsChanged();
            HasLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to refresh Win11 items: {ex.Message}");
        }
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

    private static string NormalizeGuid(string guidText) =>
        Guid.TryParse(guidText, out var guid)
            ? guid.ToString("B")
            : guidText.Trim('{', '}');

    private static string ExtractHandlerId(string id)
    {
        var parts = id.Split('|');
        return parts.Length >= 2
               && (string.Equals(parts[0], "win11", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(parts[0], "win11-system", StringComparison.OrdinalIgnoreCase))
            ? parts[1]
            : id;
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

    private void UpdateCachedState(string handlerClsid, bool isEnabled)
    {
        var normalizedClsid = NormalizeGuid(handlerClsid);
        CurrentItems = CurrentItems
            .Select(item => string.Equals(NormalizeGuid(ExtractHandlerId(item.Id)), normalizedClsid, StringComparison.OrdinalIgnoreCase)
                ? item with { IsEnabled = isEnabled }
                : item)
            .ToArray();
    }

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
