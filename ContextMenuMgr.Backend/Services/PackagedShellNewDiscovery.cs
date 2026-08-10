using System.Xml.Linq;
using ContextMenuMgr.Contracts;
using Windows.Management.Deployment;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Finds ShellNew declarations made by installed MSIX/AppX packages for one
/// interactive user. Package manifests are discovery-only: callers must not
/// use these records as a registry or package mutation target.
/// </summary>
internal static class PackagedShellNewDiscovery
{
    internal const string ProviderType = "PackagedShellNew";

    public static IReadOnlyList<PackagedShellNewDeclaration> FindForUser(string userSid, FileLogger logger)
    {
        var declarations = new List<PackagedShellNewDeclaration>();
        try
        {
            var packageManager = new PackageManager();
            foreach (var package in packageManager.FindPackagesForUser(userSid))
            {
                try
                {
                    var manifestPath = Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    declarations.AddRange(ParseManifest(
                        File.ReadAllText(manifestPath),
                        package.Id.FullName,
                        package.Id.FamilyName,
                        package.Id.Name,
                        manifestPath));
                }
                catch (Exception ex)
                {
                    logger.LogFireAndForget(RuntimeLogLevel.Warning,
                        $"PackagedShellNewPackageSkipped: Sid={userSid}, Exception={ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogFireAndForget(RuntimeLogLevel.Warning,
                $"PackagedShellNewDiscoveryFailed: Sid={userSid}, Exception={ex.GetType().Name}: {ex.Message}");
        }

        return declarations
            .OrderBy(static item => item.Extension, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ApplicationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<PackagedShellNewDeclaration> ParseManifest(
        string manifestXml,
        string packageFullName,
        string packageFamilyName,
        string packageName,
        string manifestPath)
    {
        var document = XDocument.Parse(manifestXml, LoadOptions.PreserveWhitespace);
        return document
            .Descendants()
            .Where(static element => element.Name.LocalName == "Application")
            .SelectMany(application => application
                .Descendants()
                .Where(static element => element.Name.LocalName == "FileTypeAssociation")
                .SelectMany(association => association
                    .Descendants()
                    .Where(static element => element.Name.LocalName == "FileType")
                    .Select(fileType => CreateDeclaration(
                        fileType,
                        association,
                        application,
                        packageFullName,
                        packageFamilyName,
                        packageName,
                        manifestPath))))
            .Where(static item => item is not null)
            .Cast<PackagedShellNewDeclaration>()
            .ToArray();
    }

    private static PackagedShellNewDeclaration? CreateDeclaration(
        XElement fileType,
        XElement association,
        XElement application,
        string packageFullName,
        string packageFamilyName,
        string packageName,
        string manifestPath)
    {
        var extension = fileType.Value.Trim();
        var shellNewFileName = GetAttribute(fileType, "ShellNewFileName");
        var shellNewDisplayName = GetAttribute(fileType, "ShellNewDisplayName");
        var shellNewCommandParameters = GetAttribute(fileType, "ShellNewCommandParameters");
        if (!extension.StartsWith(".", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(shellNewFileName))
        {
            return null;
        }

        return new PackagedShellNewDeclaration(
            extension,
            packageFullName,
            packageFamilyName,
            packageName,
            GetAttribute(application, "Id") ?? string.Empty,
            GetAttribute(application, "Executable") ?? string.Empty,
            GetAttribute(application, "EntryPoint") ?? string.Empty,
            GetAttribute(association, "Name") ?? string.Empty,
            manifestPath,
            shellNewFileName,
            shellNewDisplayName,
            shellNewCommandParameters,
            fileType.ToString(SaveOptions.DisableFormatting));
    }

    private static string? GetAttribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

}

internal sealed record PackagedShellNewDeclaration(
    string Extension,
    string PackageFullName,
    string PackageFamilyName,
    string PackageName,
    string ApplicationId,
    string ApplicationExecutable,
    string ApplicationEntryPoint,
    string FileTypeAssociationName,
    string ManifestPath,
    string ShellNewFileName,
    string? ShellNewDisplayName,
    string? ShellNewCommandParameters,
    string FileTypeDeclaration);
