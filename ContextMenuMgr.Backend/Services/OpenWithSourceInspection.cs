using System.Security;
using System.Xml;
using System.Xml.Linq;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace ContextMenuMgr.Backend.Services;

/// <summary>On-demand evidence for one extension. Never a mutation target or an Explorer visibility prediction.</summary>
internal static class OpenWithSourceInspection
{
    private const int Limit = 4096;

    internal static OpenWithAssociationInspection Inspect(string extension, BackendUserContext context,
        Func<string, SpecialMenuEntry> readApplication, CancellationToken cancellationToken)
    {
        extension = extension.Trim().ToLowerInvariant();
        if (extension.Length is < 2 or > 64 || extension[0] != '.'
            || extension.EndsWith('.') || extension.Contains("..", StringComparison.Ordinal)
            || extension[1..].Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Enter a file extension such as .txt.");
        OpenWithRegistration.ValidateUser(context);
        var found = new List<(string Identifier, OpenWithSourceReference Reference)>();
        var diagnostics = new HashSet<OpenWithInspectionDiagnostic>();
        var user = $@"HKEY_USERS\{context.Sid}";
        var classes = new[] { user + @"\Software\Classes", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes" };

        void Diagnostic(string code, string path = "")
        {
            if (diagnostics.Count < 64) diagnostics.Add(new(code, path));
        }

        void TryRead(string path, Action action)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { action(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or System.ComponentModel.Win32Exception)
            { Diagnostic("OpenWithInspectionIncomplete", path); }
        }

        void Add(string id, OpenWithSourceKind kind, string path, string value, bool application = false, string? applicationRoot = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.Count >= Limit) { Diagnostic("OpenWithInspectionLimited"); return; }
            var paths = new List<string>();
            var manageable = new List<string>();
            var applicationReference = !application && id.StartsWith(@"Applications\", StringComparison.OrdinalIgnoreCase)
                && ValidIdentifier(id[@"Applications\".Length..], application: true);
            var resolution = ValidIdentifier(id, application) || applicationReference
                ? OpenWithSourceResolution.Unresolved : OpenWithSourceResolution.Invalid;
            if (resolution != OpenWithSourceResolution.Invalid)
            {
                foreach (var root in classes)
                {
                    if (applicationRoot is not null && root != applicationRoot) continue;
                    var target = root + "\\" + (application ? @"Applications\" : "") + id;
                    try
                    {
                        using var key = OpenWithRegistration.Open(target);
                        if (key is null) continue;
                        paths.Add(target);
                        if (application || applicationReference)
                        {
                            var item = readApplication(target);
                            if (item.CanEdit && item.CanDelete) manageable.Add(item.Id);
                            if (item.Metadata.GetValueOrDefault("ReadOnlyReason") == "OpenWithInaccessibleRegistration")
                                resolution = OpenWithSourceResolution.Inaccessible;
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or System.ComponentModel.Win32Exception)
                    { resolution = OpenWithSourceResolution.Inaccessible; }
                    catch (InvalidOperationException)
                    { resolution = OpenWithSourceResolution.Inaccessible; Diagnostic("OpenWithInspectionIncomplete", target); }
                }
                if (resolution != OpenWithSourceResolution.Inaccessible && paths.Count > 0)
                    resolution = paths.Count > 1 ? OpenWithSourceResolution.MultipleRegistrations : OpenWithSourceResolution.Resolved;
            }
            found.Add((id, new(kind, path, value, resolution, paths, manageable)));
        }

        foreach (var root in classes)
        {
            var progIds = root + $@"\{extension}\OpenWithProgids";
            TryRead(progIds, () =>
            {
                using var key = OpenWithRegistration.Open(root);
                var references = ContextMenuRegistryCatalog.ReadOpenWithProgIdReferences(key, extension);
                if (references.Count > Limit) Diagnostic("OpenWithInspectionLimited");
                foreach (var reference in references.Take(Limit))
                    Add(reference.ProgId, OpenWithSourceKind.ClassesProgIds, reference.SourcePath, reference.ProgId);
            });

            var applications = root + @"\Applications";
            TryRead(applications, () =>
            {
                using var key = OpenWithRegistration.Open(applications);
                var names = key?.GetSubKeyNames() ?? [];
                if (names.Length > Limit) Diagnostic("OpenWithInspectionLimited");
                foreach (var name in names.Take(Limit).Where(n => ValidIdentifier(n, true)))
                {
                    var supported = applications + $@"\{name}\SupportedTypes";
                    TryRead(supported, () =>
                    {
                        using var types = OpenWithRegistration.Open(supported);
                        if (types?.GetValueNames().Contains(extension, StringComparer.OrdinalIgnoreCase) == true)
                            Add(name, OpenWithSourceKind.Applications, supported, extension, application: true, applicationRoot: root);
                    });
                }
            });
        }

        var fileExts = user + $@"\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}";
        TryRead(fileExts, () =>
        {
            using var ids = OpenWithRegistration.Open(fileExts);
            var references = ContextMenuRegistryCatalog.ReadOpenWithProgIdReferences(ids);
            if (references.Count > Limit) Diagnostic("OpenWithInspectionLimited");
            foreach (var reference in references.Take(Limit))
                Add(reference.ProgId, OpenWithSourceKind.ExplorerProgIds, reference.SourcePath, reference.ProgId);
            using var list = OpenWithRegistration.Open(fileExts + @"\OpenWithList");
            foreach (var name in (list?.GetValueNames() ?? []).Take(Limit))
            {
                if (name.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                var app = list!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (app is not null) Add(app, OpenWithSourceKind.ExplorerList, fileExts + @"\OpenWithList", name, application: true);
                else Diagnostic("OpenWithInspectionIncomplete", fileExts + @"\OpenWithList");
            }
        });

        foreach (var hive in new[] { user, "HKEY_LOCAL_MACHINE" })
        {
            var registered = hive + @"\Software\RegisteredApplications";
            TryRead(registered, () =>
            {
                using var key = OpenWithRegistration.Open(registered);
                var names = key?.GetValueNames() ?? [];
                if (names.Length > Limit) Diagnostic("OpenWithInspectionLimited");
                foreach (var name in names.Take(Limit))
                {
                    var relative = key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (relative is null || !relative.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase)
                        || relative.Length > 2048 || relative.Split('\\').Any(p => p is "" or "." or "..") || relative.Contains('/'))
                    { Diagnostic("OpenWithInspectionIncomplete", registered + " [" + name + "]"); continue; }
                    var associations = hive + "\\" + relative + @"\FileAssociations";
                    TryRead(associations, () =>
                    {
                        using var values = OpenWithRegistration.Open(associations);
                        var id = values?.GetValue(extension, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if (id is null) return;
                        // Keep the declaration and its indirection as separate physical evidence.
                        Add(id, OpenWithSourceKind.Capabilities, registered, name);
                        Add(id, OpenWithSourceKind.Capabilities, associations, extension);
                    });
                }
            });
        }

        try
        {
            var count = 0;
            foreach (var package in new PackageManager().FindPackagesForUser(context.Sid))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 512) { Diagnostic("OpenWithInspectionLimited"); break; }
                try
                {
                    var manifest = Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml");
                    using var reader = XmlReader.Create(manifest, new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
                    var doc = XDocument.Load(reader);
                    foreach (var app in doc.Descendants().Where(e => e.Name.LocalName == "Application"))
                    foreach (var association in app.Descendants().Where(e => e.Name.LocalName == "FileTypeAssociation"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!association.Descendants().Any(e => e.Name.LocalName == "FileType" && e.Value.Trim().Equals(extension, StringComparison.OrdinalIgnoreCase))) continue;
                        if (found.Count >= Limit) { Diagnostic("OpenWithInspectionLimited"); break; }
                        var id = package.Id.FamilyName + "!" + (string?)app.Attribute("Id");
                        found.Add((id, new(OpenWithSourceKind.Package, manifest,
                            (string?)association.Attribute("Name") ?? string.Empty,
                            OpenWithSourceResolution.Resolved, [manifest], [])));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { Diagnostic("OpenWithInspectionIncomplete"); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Diagnostic("OpenWithInspectionIncomplete"); }

        return new(extension, found.GroupBy(x => x.Identifier, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OpenWithAssociationCandidate(g.Key, g.Select(x => x.Reference)
                .DistinctBy(r => (r.Kind, r.SourcePath.ToUpperInvariant(), r.ValueName.ToUpperInvariant())).ToArray())).ToArray(), diagnostics.ToArray());
    }

    private static bool ValidIdentifier(string value, bool application)
        => value.Length is > 0 and <= 255 && value == value.Trim()
            && value is not ("." or "..") && !value.Any(c => char.IsControl(c) || c is '\\' or '/' or ':')
            && (!application || value.Contains('.') && !value.EndsWith(".deleted", StringComparison.OrdinalIgnoreCase));
}
