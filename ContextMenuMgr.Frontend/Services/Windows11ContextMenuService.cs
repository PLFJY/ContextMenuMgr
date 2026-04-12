using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ContextMenuMgr.Frontend.Services;

public sealed class Windows11ContextMenuService
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public async Task<IReadOnlyList<Windows11ContextMenuItemDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return [];
        }

        Windows11Blocks.LoadAll();
        var comPackages = Windows11Packages.GetPackagedComPackages();
        var packageManager = new PackageManager();
        var items = new ConcurrentDictionary<string, Windows11ContextMenuItemDefinition>(StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(
            comPackages,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            },
            async (fullName, ct) =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var package = Windows11Permissions.IsElevated
                        ? packageManager.FindPackage(fullName)
                        : packageManager.FindPackageForUser(string.Empty, fullName);

                    if (package is null)
                    {
                        return;
                    }

                    var version = package.Id.Version;
                    var pkg = new Windows11PackageInfo(
                        package.Id.FamilyName,
                        package.Id.FullName,
                        package.DisplayName,
                        package.PublisherDisplayName,
                        package.Logo.LocalPath,
                        package.InstalledLocation.Path,
                        new Version(version.Major, version.Minor, version.Build, version.Revision));

                    var definitions = await Windows11Packages.AnalyzeManifestAsync(pkg, package.IsBundle, ct);
                    foreach (var definition in definitions)
                    {
                        var materialized = definition with
                        {
                            IsEnabled = GetIsEnabled(definition.Id),
                            IsMachineBlocked = Windows11Blocks.Machine.Contains(definition.Id)
                        };

                        if (!items.TryGetValue(materialized.Id, out var existing)
                            || existing.Package.Version < materialized.Package.Version)
                        {
                            items[materialized.Id] = materialized;
                        }
                    }
                }
                catch
                {
                }
            });

        return items.Values
            .OrderBy(static item => item.Package.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public Task<bool> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!enabled && Windows11Blocks.Machine.Contains(id))
        {
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled)
            {
                foreach (var blocks in Windows11Blocks.GetScopes())
                {
                    blocks.Remove(id);
                }
            }
            else
            {
                Windows11Blocks.GetScope(Windows11Blocks.WriteScope).Add(id);
            }

            return GetIsEnabled(id);
        }, cancellationToken);
    }

    public bool GetIsEnabled(string id)
    {
        return !Windows11Blocks.GetScopes().Any(blocks => blocks.Contains(id));
    }

    public ImageSource? LoadLogo(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(logoPath))
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}

internal static class Windows11Permissions
{
    private static readonly Lazy<bool> IsElevatedLazy = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsElevated => IsElevatedLazy.Value;
}

internal static class Windows11Packages
{
    private const string NamespaceCom = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private const string NamespaceDesktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";

    public static string[] GetPackagedComPackages()
    {
        using var subKey = Registry.ClassesRoot.OpenSubKey(@"PackagedCom\Package");
        return subKey?.GetSubKeyNames() ?? [];
    }

    public static async Task<IEnumerable<Windows11ContextMenuItemDefinition>> AnalyzeManifestAsync(
        Windows11PackageInfo package,
        bool isBundle,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(
            package.InstallPath,
            isBundle ? @"AppxMetadata\AppxBundleManifest.xml" : "AppxManifest.xml");

        if (!File.Exists(manifestPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(manifestPath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore
        });

        var nsResolver = (IXmlNamespaceResolver)reader;
        if (!reader.ReadToFollowing("Package")
            || nsResolver.LookupPrefix(NamespaceDesktop4) is null
            || nsResolver.LookupPrefix(NamespaceCom) is null)
        {
            return [];
        }

        var contextMenus = new Dictionary<string, List<Windows11ContextMenuVerb>>(StringComparer.OrdinalIgnoreCase);
        var comServers = new Dictionary<string, Windows11ComServerInfo>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "FileExplorerContextMenus":
                {
                    var element = (XElement)XNode.ReadFrom(reader);
                    var query =
                        from itemType in element.Elements()
                        where itemType.Name.LocalName == "ItemType"
                        from verb in itemType.Elements()
                        where verb.Name.LocalName == "Verb"
                        let type = itemType.Attribute("Type")?.Value
                        let item = new Windows11ContextMenuVerb(
                            verb.Attribute("Clsid")?.Value,
                            verb.Attribute("Id")?.Value,
                            string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase)
                                ? type
                                : $"File: {type}")
                        group item by item.Clsid;

                    foreach (var group in query)
                    {
                        if (!string.IsNullOrWhiteSpace(group.Key))
                        {
                            contextMenus[group.Key] = group.ToList();
                        }
                    }

                    break;
                }
                case "ComServer":
                {
                    var element = (XElement)XNode.ReadFrom(reader);
                    var query =
                        from server in element.Elements()
                        where server.Name.LocalName is "SurrogateServer" or "ExeServer"
                        from cls in server.Elements()
                        where cls.Name.LocalName == "Class"
                        let item = new Windows11ComServerInfo(
                            cls.Attribute("Id")?.Value,
                            Path.Combine(
                                package.InstallPath,
                                cls.Attribute("Path")?.Value ?? server.Attribute("Executable")?.Value ?? string.Empty),
                            server.Attribute("DisplayName")?.Value)
                        group item by item.Id;

                    foreach (var group in query)
                    {
                        if (!string.IsNullOrWhiteSpace(group.Key))
                        {
                            comServers[group.Key] = group.First();
                        }
                    }

                    break;
                }
            }
        }

        return contextMenus.Keys
            .Intersect(comServers.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(id =>
            {
                var comServer = comServers[id];
                var displayName = string.IsNullOrWhiteSpace(comServer.DisplayName)
                    ? package.DisplayName
                    : comServer.DisplayName;
                var contextTypes = contextMenus[id]
                    .Select(static item => item.Type)
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new Windows11ContextMenuItemDefinition(
                    id,
                    displayName ?? package.DisplayName,
                    package,
                    contextMenus[id],
                    comServer,
                    contextTypes,
                    true,
                    false);
            });
    }
}

internal sealed class Windows11Blocks : IReadOnlyCollection<string>
{
    internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private readonly RegistryKey _baseKey;
    private HashSet<string> _items = [];
    private readonly Lazy<bool> _isReadOnly;

    private Windows11Blocks(RegistryHive hive)
    {
        Scope = hive;
        _baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        _isReadOnly = new Lazy<bool>(() =>
        {
            try
            {
                using var subKey = _baseKey.OpenSubKey(RegistryPath, writable: true)
                                 ?? _baseKey.OpenSubKey(RegistryPath[..RegistryPath.LastIndexOf('\\')], writable: true);
                return false;
            }
            catch (SecurityException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        });
    }

    public RegistryHive Scope { get; }

    public bool IsReadOnly => _isReadOnly.Value;

    public int Count => _items.Count;

    public void Load()
    {
        using var subKey = _baseKey.OpenSubKey(RegistryPath);
        _items = subKey?.GetValueNames()
            .Select(static value => value.Trim('{', '}'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
    }

    public void Add(string id)
    {
        using var subKey = _baseKey.OpenSubKey(RegistryPath, writable: true) ?? _baseKey.CreateSubKey(RegistryPath);
        subKey.SetValue(ToRegistryName(id), string.Empty);
        _items.Add(id);
    }

    public void Remove(string id)
    {
        if (!_items.Contains(id))
        {
            return;
        }

        using var subKey = _baseKey.OpenSubKey(RegistryPath, writable: true);
        if (subKey is null)
        {
            _items = [];
            return;
        }

        subKey.DeleteValue(ToRegistryName(id), throwOnMissingValue: false);
        _items.Remove(id);
    }

    public bool Contains(string id) => _items.Contains(id);

    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static Windows11Blocks User { get; } = new(RegistryHive.CurrentUser);

    public static Windows11Blocks Machine { get; } = new(RegistryHive.LocalMachine);

    public static RegistryHive WriteScope { get; set; } = RegistryHive.CurrentUser;

    public static IEnumerable<Windows11Blocks> GetScopes()
    {
        yield return User;
        yield return Machine;
    }

    public static Windows11Blocks GetScope(RegistryHive hive)
    {
        return hive switch
        {
            RegistryHive.CurrentUser => User,
            RegistryHive.LocalMachine => Machine,
            _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, null)
        };
    }

    public static void LoadAll()
    {
        foreach (var blocks in GetScopes())
        {
            blocks.Load();
        }
    }

    private static string ToRegistryName(string value) => '{' + value + '}';
}

public sealed record Windows11PackageInfo(
    string FamilyName,
    string FullName,
    string DisplayName,
    string PublisherDisplayName,
    string LogoPath,
    string InstallPath,
    Version Version);

public sealed record Windows11ContextMenuVerb(
    string? Clsid,
    string? Id,
    string? Type);

public sealed record Windows11ComServerInfo(
    string? Id,
    string? Path,
    string? DisplayName);

public sealed record Windows11ContextMenuItemDefinition(
    string Id,
    string DisplayName,
    Windows11PackageInfo Package,
    IReadOnlyList<Windows11ContextMenuVerb> ContextMenus,
    Windows11ComServerInfo ComServer,
    IReadOnlyList<string> ContextTypes,
    bool IsEnabled,
    bool IsMachineBlocked);
