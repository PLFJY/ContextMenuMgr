using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public sealed class Windows11ContextMenuService
{
    private const string NamespaceCom = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private const string NamespaceDesktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private const string PackageRepositoryPath = @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public async Task<IReadOnlyList<Windows11ContextMenuItemDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        Windows11ContextMenuBlocks.LoadAll();
        var packages = GetPackagedComPackageNames();
        var items = new ConcurrentDictionary<string, Windows11ContextMenuItemDefinition>(StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(
            packages,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4
            },
            async (fullName, ct) =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var package = TryGetPackageInfo(fullName);
                    if (package is null)
                    {
                        return;
                    }

                    foreach (var definition in await AnalyzeManifestAsync(package, ct))
                    {
                        if (!items.TryGetValue(definition.Id, out var existing)
                            || existing.Package.Version < definition.Package.Version)
                        {
                            items[definition.Id] = definition with
                            {
                                IsEnabled = IsEnabled(definition.Id),
                                IsMachineBlocked = Windows11ContextMenuBlocks.Machine.Contains(definition.Id)
                            };
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

    public async Task<bool> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled && Windows11ContextMenuBlocks.Machine.Contains(id))
        {
            return false;
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled)
            {
                Windows11ContextMenuBlocks.User.Remove(id);
            }
            else
            {
                Windows11ContextMenuBlocks.User.Add(id);
            }
        }, cancellationToken);

        return IsEnabled(id);
    }

    public bool IsEnabled(string id)
    {
        return !Windows11ContextMenuBlocks.User.Contains(id)
               && !Windows11ContextMenuBlocks.Machine.Contains(id);
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

    private static Windows11PackageInfo? TryGetPackageInfo(string fullName)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{PackageRepositoryPath}\{fullName}");
            if (key is null)
            {
                return null;
            }

            var installPath = key.GetValue("PackageRootFolder") as string;
            if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            {
                return null;
            }

            var displayName = key.GetValue("DisplayName") as string;
            var (packageName, version, familyName) = ParsePackageIdentity(fullName);
            var manifestMetadata = TryReadManifestMetadata(installPath);
            var resolvedDisplayName = ResolveDisplayName(displayName, packageName);

            return new Windows11PackageInfo(
                familyName,
                fullName,
                resolvedDisplayName,
                manifestMetadata.PublisherDisplayName,
                manifestMetadata.LogoPath,
                installPath,
                version);
        }
        catch
        {
            return null;
        }
    }

    private static string[] GetPackagedComPackageNames()
    {
        using var subKey = Registry.ClassesRoot.OpenSubKey(@"PackagedCom\Package");
        return subKey?.GetSubKeyNames() ?? [];
    }

    private static async Task<IEnumerable<Windows11ContextMenuItemDefinition>> AnalyzeManifestAsync(
        Windows11PackageInfo package,
        CancellationToken cancellationToken)
    {
        var manifestPath = ResolveManifestPath(package.InstallPath);
        if (manifestPath is null)
        {
            return [];
        }

        await using var stream = File.OpenRead(manifestPath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore
        });

        var namespaceResolver = (IXmlNamespaceResolver)reader;
        if (!reader.ReadToFollowing("Package")
            || namespaceResolver.LookupPrefix(NamespaceDesktop4) is null
            || namespaceResolver.LookupPrefix(NamespaceCom) is null)
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
                    var verbs =
                        from itemType in element.Elements()
                        where itemType.Name.LocalName == "ItemType"
                        let type = itemType.Attribute("Type")?.Value
                        from verb in itemType.Elements()
                        where verb.Name.LocalName == "Verb"
                        let clsid = verb.Attribute("Clsid")?.Value
                        let id = verb.Attribute("Id")?.Value
                        let normalizedType = string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase)
                            ? type
                            : $"File: {type}"
                        group new Windows11ContextMenuVerb(clsid, id, normalizedType) by clsid;

                    foreach (var verbGroup in verbs)
                    {
                        if (string.IsNullOrWhiteSpace(verbGroup.Key))
                        {
                            continue;
                        }

                        contextMenus[verbGroup.Key] = verbGroup.ToList();
                    }

                    break;
                }
                case "ComServer":
                {
                    var element = (XElement)XNode.ReadFrom(reader);
                    var servers =
                        from server in element.Elements()
                        where server.Name.LocalName is "SurrogateServer" or "ExeServer"
                        from cls in server.Elements()
                        where cls.Name.LocalName == "Class"
                        let id = cls.Attribute("Id")?.Value
                        let displayName = server.Attribute("DisplayName")?.Value
                        let executablePath = cls.Attribute("Path")?.Value ?? server.Attribute("Executable")?.Value
                        let path = string.IsNullOrWhiteSpace(executablePath)
                            ? null
                            : Path.Combine(package.InstallPath, executablePath)
                        group new Windows11ComServerInfo(id, path, displayName) by id;

                    foreach (var serverGroup in servers)
                    {
                        if (string.IsNullOrWhiteSpace(serverGroup.Key))
                        {
                            continue;
                        }

                        comServers[serverGroup.Key] = serverGroup.First();
                    }

                    break;
                }
            }
        }

        return contextMenus.Keys
            .Intersect(comServers.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(id =>
            {
                var verbs = contextMenus[id];
                var server = comServers[id];
                var displayName = string.IsNullOrWhiteSpace(server.DisplayName)
                    ? package.DisplayName
                    : server.DisplayName;

                var contextTypes = verbs
                    .Select(static item => item.Type)
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new Windows11ContextMenuItemDefinition(
                    id,
                    displayName ?? package.DisplayName,
                    package,
                    verbs,
                    server,
                    contextTypes,
                    true,
                    false);
            });
    }

    private static string ResolveDisplayName(string? displayName, string packageName)
    {
        if (string.IsNullOrWhiteSpace(displayName)
            || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return packageName;
        }

        return displayName;
    }

    private static (string PackageName, Version Version, string FamilyName) ParsePackageIdentity(string fullName)
    {
        var parts = fullName.Split('_');
        var packageName = parts.Length > 0 ? parts[0] : fullName;
        var version = parts.Length > 1 && Version.TryParse(parts[1], out var parsedVersion)
            ? parsedVersion
            : new Version(0, 0);
        var publisherId = parts.Length > 0 ? parts[^1] : "unknown";
        var familyName = $"{packageName}_{publisherId}";
        return (packageName, version, familyName);
    }

    private static Windows11ManifestMetadata TryReadManifestMetadata(string installPath)
    {
        try
        {
            var manifestPath = ResolveManifestPath(installPath);
            if (manifestPath is null || !File.Exists(manifestPath))
            {
                return Windows11ManifestMetadata.Empty;
            }

            var document = XDocument.Load(manifestPath);
            var properties = document.Root?.Elements().FirstOrDefault(static element => element.Name.LocalName == "Properties");
            if (properties is null)
            {
                return Windows11ManifestMetadata.Empty;
            }

            var publisherDisplayName = NormalizeManifestString(
                properties.Elements().FirstOrDefault(static element => element.Name.LocalName == "PublisherDisplayName")?.Value);

            var logoRelativePath =
                FirstNonEmpty(
                    properties.Elements().FirstOrDefault(static element => element.Name.LocalName == "Logo")?.Value,
                    document.Descendants().FirstOrDefault(static element => element.Name.LocalName == "VisualElements")
                        ?.Attributes().FirstOrDefault(static attribute => attribute.Name.LocalName == "Square44x44Logo")?.Value,
                    document.Descendants().FirstOrDefault(static element => element.Name.LocalName == "VisualElements")
                        ?.Attributes().FirstOrDefault(static attribute => attribute.Name.LocalName == "Square150x150Logo")?.Value);

            var logoPath = NormalizeLogoPath(installPath, logoRelativePath);
            return new Windows11ManifestMetadata(publisherDisplayName, logoPath);
        }
        catch
        {
            return Windows11ManifestMetadata.Empty;
        }
    }

    private static string? ResolveManifestPath(string installPath)
    {
        var appxManifestPath = Path.Combine(installPath, "AppxManifest.xml");
        if (File.Exists(appxManifestPath))
        {
            return appxManifestPath;
        }

        var bundleManifestPath = Path.Combine(installPath, @"AppxMetadata\AppxBundleManifest.xml");
        return File.Exists(bundleManifestPath) ? bundleManifestPath : null;
    }

    private static string? NormalizeLogoPath(string installPath, string? logoRelativePath)
    {
        if (string.IsNullOrWhiteSpace(logoRelativePath))
        {
            return null;
        }

        if (Path.IsPathRooted(logoRelativePath))
        {
            return File.Exists(logoRelativePath) ? logoRelativePath : null;
        }

        var combinedPath = Path.Combine(installPath, logoRelativePath.TrimStart('\\'));
        return File.Exists(combinedPath) ? combinedPath : null;
    }

    private static string? NormalizeManifestString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }
}

public sealed record Windows11ContextMenuItemDefinition(
    string Id,
    string DisplayName,
    Windows11PackageInfo Package,
    IReadOnlyList<Windows11ContextMenuVerb> ContextMenus,
    Windows11ComServerInfo ComServer,
    IReadOnlyList<string> ContextTypes,
    bool IsEnabled,
    bool IsMachineBlocked);

public sealed record Windows11PackageInfo(
    string FamilyName,
    string FullName,
    string DisplayName,
    string? PublisherDisplayName,
    string? LogoPath,
    string InstallPath,
    Version Version);

internal sealed record Windows11ManifestMetadata(
    string? PublisherDisplayName,
    string? LogoPath)
{
    public static Windows11ManifestMetadata Empty { get; } = new(null, null);
}

public sealed record Windows11ContextMenuVerb(
    string? Clsid,
    string? Id,
    string? Type);

public sealed record Windows11ComServerInfo(
    string? Id,
    string? Path,
    string? DisplayName);

internal sealed class Windows11ContextMenuBlocks
{
    private const string BlockedRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private readonly RegistryKey _baseKey;
    private HashSet<string> _items = [];

    private Windows11ContextMenuBlocks(RegistryHive hive)
    {
        _baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
    }

    public static Windows11ContextMenuBlocks User { get; } = new(RegistryHive.CurrentUser);

    public static Windows11ContextMenuBlocks Machine { get; } = new(RegistryHive.LocalMachine);

    public void Load()
    {
        using var subKey = _baseKey.OpenSubKey(BlockedRegistryPath);
        _items = subKey?.GetValueNames()
            .Select(static name => name.Trim('{', '}'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
    }

    public bool Contains(string id) => _items.Contains(id);

    public void Add(string id)
    {
        using var subKey = _baseKey.OpenSubKey(BlockedRegistryPath, writable: true)
                         ?? _baseKey.CreateSubKey(BlockedRegistryPath);
        subKey.SetValue(ToRegistryName(id), string.Empty);
        _items.Add(id);
    }

    public void Remove(string id)
    {
        if (!_items.Contains(id))
        {
            return;
        }

        using var subKey = _baseKey.OpenSubKey(BlockedRegistryPath, writable: true);
        subKey?.DeleteValue(ToRegistryName(id), throwOnMissingValue: false);
        _items.Remove(id);
    }

    public static void LoadAll()
    {
        User.Load();
        Machine.Load();
    }

    private static string ToRegistryName(string id) => $"{{{id}}}";
}
