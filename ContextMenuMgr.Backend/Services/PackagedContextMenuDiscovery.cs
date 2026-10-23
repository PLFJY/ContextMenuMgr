using System.Security.Cryptography;
using System.Text;
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
    private static readonly PackageDiscoveryWarningAggregator Warnings = new(TimeSpan.FromMinutes(15), 32);

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
        var failureFingerprint = new byte[32];
        string? enumerationFailure = null;
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
                        throw new InvalidDataException("Package identity or manifest is unavailable.");
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
                    var failure = $"Package={packageName}, Stage={stage}, Exception={ex.GetType().Name}, HResult=0x{ex.HResult:X8}";
                    AddFailureFingerprint(failureFingerprint, failure + ex.Message);
                    if (skippedPackageSamples.Count < 3)
                    {
                        skippedPackageSamples.Add(failure);
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
            enumerationFailure = $"PackagedContextMenuDiscoveryFailed: Sid={userSid}, Exception={ex.GetType().Name}, HResult=0x{ex.HResult:X8}: {ex.Message}";
        }

        ReportWarning(userSid + "|enumeration", enumerationFailure is null ? null : Fingerprint(enumerationFailure), enumerationFailure, logger);
        if (skippedPackageCount > 0 || enumerationFailure is null)
            ReportWarning(userSid + "|metadata", skippedPackageCount == 0 ? null : $"{skippedPackageCount}:{Convert.ToHexString(failureFingerprint)}",
                skippedPackageCount == 0 ? null : $"PackagedContextMenuPackagesSkipped: Sid={userSid}, Count={skippedPackageCount}, Signature={Convert.ToHexString(failureFingerprint)}, Samples=[{string.Join("; ", skippedPackageSamples)}].", logger);

        return Discover(
            packages,
            static package => File.ReadAllText(package.ManifestPath),
            logger,
            cancellationToken,
            userSid,
            enumerationComplete: enumerationFailure is null)
            .Select(definition => definition with
            {
                DiscoveryComplete = definition.DiscoveryComplete && enumerationFailure is null && skippedPackageCount == 0
            }).ToArray();
    }

    internal static IReadOnlyList<PackagedContextMenuDefinition> Discover(
        IEnumerable<PackagedContextMenuPackage> packages,
        Func<PackagedContextMenuPackage, string> manifestLoader,
        FileLogger? logger = null,
        CancellationToken cancellationToken = default,
        string? userSid = null,
        bool enumerationComplete = true)
    {
        var skippedManifestCount = 0;
        var skippedManifestSamples = new List<string>(3);
        var failureFingerprint = new byte[32];
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
                skippedManifestCount++;
                var failure = $"Package={package.FullName}, Stage=ManifestReadParse, ManifestPath={package.ManifestPath}, Exception={ex.GetType().Name}, HResult=0x{ex.HResult:X8}: {ex.Message}";
                AddFailureFingerprint(failureFingerprint, failure);
                if (skippedManifestSamples.Count < 3) skippedManifestSamples.Add(failure);
            }
        }

        if (skippedManifestCount > 0 || enumerationComplete)
            ReportWarning((userSid ?? "<unspecified>") + "|manifest",
                skippedManifestCount == 0 ? null : $"{skippedManifestCount}:{Convert.ToHexString(failureFingerprint)}",
                skippedManifestCount == 0 ? null : $"PackagedContextMenuManifestSkipped: Sid={userSid}, Count={skippedManifestCount}, Signature={Convert.ToHexString(failureFingerprint)}, Samples=[{string.Join("; ", skippedManifestSamples)}].", logger);

        return definitions
            .Select(definition => definition with { DiscoveryComplete = enumerationComplete && skippedManifestCount == 0 })
            .OrderBy(static definition => definition.Package.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static definition => definition.Clsid, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ReportWarning(string scope, string? signature, string? detail, FileLogger? logger)
    {
        if (logger is null) return;
        foreach (var message in Warnings.Observe(scope, signature, detail, DateTimeOffset.UtcNow))
            logger.LogFireAndForget(RuntimeLogLevel.Warning, message);
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AddFailureFingerprint(byte[] aggregate, string failure)
    {
        // Order-independent package-set digest includes every failure, not just displayed samples.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(failure));
        var carry = 0;
        for (var i = 0; i < aggregate.Length; i++)
        {
            var sum = aggregate[i] + digest[i] + carry;
            aggregate[i] = (byte)sum;
            carry = sum >> 8;
        }
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
    IReadOnlyList<string> ContextTypes)
{
    public bool DiscoveryComplete { get; init; } = true;
}
