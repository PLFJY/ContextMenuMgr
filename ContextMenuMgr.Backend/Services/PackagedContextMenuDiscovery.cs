using System.Xml.Linq;
using ContextMenuMgr.Contracts;
using Windows.Management.Deployment;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Discovers Windows 11 Explorer context-menu declarations from packages installed
/// for one interactive user. Package manifests are read-only discovery inputs.
/// </summary>
internal static class PackagedContextMenuDiscovery
{
    private const string AppxManifestNamespacePrefix = "http://schemas.microsoft.com/appx/manifest/";
    private const string FileExplorerContextMenusCategory = "windows.fileExplorerContextMenus";
    private const string ComServerCategory = "windows.comServer";

    public static IReadOnlyList<PackagedContextMenuDefinition> FindForUser(
        string userSid,
        FileLogger? logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return [];
        }

        var packages = new List<PackagedContextMenuPackage>();
        var skippedPackageCount = 0;
        var skippedPackageSamples = new List<string>(3);
        try
        {
            var packageManager = new PackageManager();
            foreach (var package in packageManager.FindPackagesForUser(userSid))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageName = "<unavailable>";
                var stage = "PackageIdentity";
                try
                {
                    packageName = package.Id.FullName;
                    stage = "InstalledLocation";
                    var installPath = package.InstalledLocation.Path;
                    stage = "ManifestPath";
                    var manifestPath = GetManifestPath(installPath);
                    if (string.IsNullOrWhiteSpace(packageName)
                        || string.IsNullOrWhiteSpace(manifestPath))
                    {
                        continue;
                    }

                    stage = "PackageMetadata";
                    packages.Add(new PackagedContextMenuPackage(
                        packageName,
                        package.Id.FamilyName,
                        package.Id.Name,
                        package.DisplayName,
                        package.PublisherDisplayName,
                        installPath,
                        manifestPath));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    skippedPackageCount++;
                    if (skippedPackageSamples.Count < 3)
                    {
                        skippedPackageSamples.Add(
                            $"Package={packageName}, Stage={stage}, Exception={ex.GetType().Name}, HResult=0x{ex.HResult:X8}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"PackagedContextMenuDiscoveryFailed: Sid={userSid}, Exception={ex.GetType().Name}, HResult=0x{ex.HResult:X8}: {ex.Message}");
        }

        if (skippedPackageCount > 0)
        {
            logger?.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"PackagedContextMenuPackagesSkipped: Sid={userSid}, Count={skippedPackageCount}, Samples=[{string.Join("; ", skippedPackageSamples)}].");
        }

        return Discover(
            packages,
            static package => File.ReadAllText(package.ManifestPath),
            logger,
            cancellationToken);
    }

    internal static IReadOnlyList<PackagedContextMenuDefinition> Discover(
        IEnumerable<PackagedContextMenuPackage> packages,
        Func<PackagedContextMenuPackage, string> manifestLoader,
        FileLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var definitions = new List<PackagedContextMenuDefinition>();
        foreach (var package in packages
                     .Where(static package => !string.IsNullOrWhiteSpace(package.FullName))
                     .DistinctBy(static package => package.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                definitions.AddRange(ParseManifest(manifestLoader(package), package));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogFireAndForget(
                    RuntimeLogLevel.Warning,
                    $"PackagedContextMenuManifestSkipped: PackageFullName={package.FullName}, ManifestPath={package.ManifestPath}, Exception={ex.GetType().Name}: {ex.Message}");
            }
        }

        return definitions
            .OrderBy(static definition => definition.Package.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static definition => definition.Clsid, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<PackagedContextMenuDefinition> ParseManifest(
        string manifestXml,
        PackagedContextMenuPackage package)
    {
        var document = XDocument.Parse(manifestXml, LoadOptions.PreserveWhitespace);
        if (document.Root is not { } root
            || root.Name.LocalName != "Package"
            || !IsAppxManifestNamespace(root.Name.NamespaceName))
        {
            return [];
        }

        var contextMenus = ParseContextMenus(root);
        var comServers = ParseComServers(root, package.InstallPath);

        return contextMenus
            .Where(pair => comServers.ContainsKey(pair.Key))
            .Select(pair =>
            {
                var comServer = comServers[pair.Key];
                var displayName = FirstNonEmpty(
                    comServer.ServerDisplayName,
                    comServer.ClassDisplayName,
                    package.DisplayName,
                    package.Name,
                    package.FullName)!;
                var contextTypes = pair.Value
                    .Select(static verb => verb.ContextType)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new PackagedContextMenuDefinition(
                    pair.Key,
                    displayName,
                    package,
                    pair.Value,
                    comServer,
                    contextTypes);
            })
            .ToArray();
    }

    private static Dictionary<string, List<PackagedContextMenuVerb>> ParseContextMenus(XElement root)
    {
        var contextMenus = new Dictionary<string, List<PackagedContextMenuVerb>>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in FindExtensions(root, FileExplorerContextMenusCategory))
        {
            foreach (var contextMenusElement in extension
                         .Descendants()
                         .Where(static element => element.Name.LocalName == "FileExplorerContextMenus"
                                                  && IsAppxManifestNamespace(element.Name.NamespaceName)))
            {
                foreach (var itemType in contextMenusElement
                             .Descendants()
                             .Where(static element => element.Name.LocalName == "ItemType"
                                                      && IsAppxManifestNamespace(element.Name.NamespaceName)))
                {
                    var itemTypeValue = GetAttribute(itemType, "Type")?.Trim();
                    if (string.IsNullOrWhiteSpace(itemTypeValue))
                    {
                        continue;
                    }

                    var contextType = string.Equals(itemTypeValue, "Directory", StringComparison.OrdinalIgnoreCase)
                        ? itemTypeValue
                        : $"File: {itemTypeValue}";

                    foreach (var verb in itemType
                                 .Descendants()
                                 .Where(static element => element.Name.LocalName == "Verb"
                                                          && IsAppxManifestNamespace(element.Name.NamespaceName)))
                    {
                        if (!TryNormalizeGuid(GetAttribute(verb, "Clsid"), out var clsid))
                        {
                            continue;
                        }

                        if (!contextMenus.TryGetValue(clsid, out var verbs))
                        {
                            verbs = [];
                            contextMenus.Add(clsid, verbs);
                        }

                        verbs.Add(new PackagedContextMenuVerb(
                            GetAttribute(verb, "Id")?.Trim() ?? string.Empty,
                            clsid,
                            contextType));
                    }
                }
            }
        }

        return contextMenus;
    }

    private static Dictionary<string, PackagedComServer> ParseComServers(XElement root, string installPath)
    {
        var comServers = new Dictionary<string, PackagedComServer>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in FindExtensions(root, ComServerCategory))
        {
            foreach (var comServer in extension
                         .Descendants()
                         .Where(static element => element.Name.LocalName == "ComServer"
                                                  && IsAppxManifestNamespace(element.Name.NamespaceName)))
            {
                foreach (var server in comServer
                             .Descendants()
                             .Where(static element => element.Name.LocalName is "SurrogateServer" or "ExeServer"
                                                      && IsAppxManifestNamespace(element.Name.NamespaceName)))
                {
                    var serverKind = server.Name.LocalName;
                    var serverDisplayName = GetAttribute(server, "DisplayName")?.Trim();
                    foreach (var cls in server
                                 .Descendants()
                                 .Where(static element => element.Name.LocalName == "Class"
                                                          && IsAppxManifestNamespace(element.Name.NamespaceName)))
                    {
                        if (!TryNormalizeGuid(GetAttribute(cls, "Id"), out var clsid))
                        {
                            continue;
                        }

                        var declaredPath = FirstNonEmpty(
                            GetAttribute(cls, "Path"),
                            GetAttribute(cls, "Executable"),
                            GetAttribute(server, "Executable"),
                            GetAttribute(server, "Path"));
                        var resolvedPath = ResolveDeclaredPath(installPath, declaredPath);

                        comServers.TryAdd(
                            clsid,
                            new PackagedComServer(
                                clsid,
                                resolvedPath,
                                declaredPath?.Trim(),
                                serverDisplayName,
                                GetAttribute(cls, "DisplayName")?.Trim(),
                                serverKind));
                    }
                }
            }
        }

        return comServers;
    }

    private static IEnumerable<XElement> FindExtensions(XElement root, string category) =>
        root.Descendants()
            .Where(element => element.Name.LocalName == "Extension"
                              && element.Parent?.Name.LocalName == "Extensions"
                              && IsAppxManifestNamespace(element.Name.NamespaceName)
                              && string.Equals(GetAttribute(element, "Category"), category, StringComparison.OrdinalIgnoreCase));

    private static string? GetManifestPath(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        var primaryManifest = Path.Combine(installPath, "AppxManifest.xml");
        if (File.Exists(primaryManifest))
        {
            return primaryManifest;
        }

        var bundleManifest = Path.Combine(installPath, "AppxMetadata", "AppxBundleManifest.xml");
        return File.Exists(bundleManifest) ? bundleManifest : null;
    }

    private static string? ResolveDeclaredPath(string installPath, string? declaredPath)
    {
        if (string.IsNullOrWhiteSpace(declaredPath))
        {
            return null;
        }

        var path = declaredPath.Trim();
        return Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(installPath)
            ? path
            : Path.Combine(installPath, path);
    }

    private static bool TryNormalizeGuid(string? value, out string normalizedGuid)
    {
        if (Guid.TryParse(value, out var guid))
        {
            normalizedGuid = guid.ToString("B");
            return true;
        }

        normalizedGuid = string.Empty;
        return false;
    }

    private static bool IsAppxManifestNamespace(string namespaceName) =>
        namespaceName.StartsWith(AppxManifestNamespacePrefix, StringComparison.OrdinalIgnoreCase);

    private static string? GetAttribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}

internal sealed record PackagedContextMenuPackage(
    string FullName,
    string FamilyName,
    string Name,
    string DisplayName,
    string PublisherDisplayName,
    string InstallPath,
    string ManifestPath);

internal sealed record PackagedContextMenuVerb(
    string Id,
    string Clsid,
    string ContextType);

internal sealed record PackagedComServer(
    string Clsid,
    string? Path,
    string? DeclaredPath,
    string? ServerDisplayName,
    string? ClassDisplayName,
    string ServerKind);

internal sealed record PackagedContextMenuDefinition(
    string Clsid,
    string DisplayName,
    PackagedContextMenuPackage Package,
    IReadOnlyList<PackagedContextMenuVerb> Verbs,
    PackagedComServer ComServer,
    IReadOnlyList<string> ContextTypes);
