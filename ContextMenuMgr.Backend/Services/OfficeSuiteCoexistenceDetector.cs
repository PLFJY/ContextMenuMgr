using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

public sealed class OfficeSuiteCoexistenceDetector
{
    public const string AssociationHijackCode = "WPS_OFFICE_ASSOCIATION_HIJACK";
    public const string IconHijackCode = "WPS_OFFICE_ICON_HIJACK";
    public const string ShellNewInjectionCode = "WPS_OFFICE_SHELLNEW_INJECTION";

    private const string UserClassesPath = @"Software\Classes";
    private const string MachineClassesPath = @"SOFTWARE\Classes";
    private const string ShellNewOrderPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew";

    private static readonly string[] WpsProbeProgIds =
    [
        "WPS.Doc.6", "WPS.Docx.6", "WPS.RTF.6", "ET.Xlsx.6",
        "WPP.PPTX.6", "KWPS.PDF.9", "KET.Workbook.9", "KWPP.Presentation.9"
    ];

    private static readonly string[] MicrosoftProbeProgIds =
    [
        "Word.Document.12", "Excel.Sheet.12", "PowerPoint.Show.12", "Word.RTF.8"
    ];

    private static readonly string[] WpsMarkers =
    [
        "kingsoft", "wps office", "wpsofficeicon.dll", "ksolaunch.exe", @"\wps "
    ];

    private static readonly string[] MicrosoftMarkers =
    [
        @"microsoft office\root", @"\office16\", "wordicon.exe", "xlicons.exe", "pptico.exe",
        "winword.exe", "excel.exe", "powerpnt.exe"
    ];

    private static readonly string[] WpsPrefixes = ["WPS.", "KWPS.", "ET.", "KET.", "WPP.", "KWPP.", "WPS.PIC.", "Kingsoft"];

    private readonly FileLogger? _logger;

    public OfficeSuiteCoexistenceDetector(FileLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Determines whether a newly observed WPS/Office synthetic finding needs review.
    /// Existing findings are adopted as acknowledged baseline entries when the state
    /// database is empty, just like existing registry menu entries.
    /// </summary>
    internal static bool ShouldMarkNewFindingPendingApproval(bool hasPersistedBaseline)
        => hasPersistedBaseline;

    public static IReadOnlyList<ProtectedDocumentAssociation> Catalog { get; } =
    [
        Word(".doc", "Word.Document.8"),
        Word(".docx", "Word.Document.12"),
        Word(".docm", "Word.DocumentMacroEnabled.12"),
        Word(".dot", "Word.Template.8"),
        Word(".dotx", "Word.Template.12"),
        Word(".dotm", "Word.TemplateMacroEnabled.12"),
        Excel(".xls", "Excel.Sheet.8"),
        Excel(".xlsx", "Excel.Sheet.12"),
        Excel(".xlsm", "Excel.SheetMacroEnabled.12"),
        Excel(".xlsb", "Excel.SheetBinaryMacroEnabled.12"),
        Excel(".xlt", "Excel.Template.8"),
        Excel(".xltx", "Excel.Template.12"),
        Excel(".xltm", "Excel.TemplateMacroEnabled.12"),
        PowerPoint(".ppt", "PowerPoint.Show.8"),
        PowerPoint(".pptx", "PowerPoint.Show.12"),
        PowerPoint(".pptm", "PowerPoint.ShowMacroEnabled.12"),
        PowerPoint(".pot", "PowerPoint.Template.8"),
        PowerPoint(".potx", "PowerPoint.Template.12"),
        PowerPoint(".potm", "PowerPoint.TemplateMacroEnabled.12"),
        PowerPoint(".pps", "PowerPoint.SlideShow.8"),
        PowerPoint(".ppsx", "PowerPoint.SlideShow.12"),
        PowerPoint(".ppsm", "PowerPoint.SlideShowMacroEnabled.12"),
        Word(".rtf", "Word.RTF.8"),
        new() { Extension = ".pdf", Group = ProtectedDocumentGroup.Pdf, MicrosoftProgIds = [], WpsProgIdPrefixes = WpsPrefixes, SupportsIconProviderToggle = false },
        Image(".jpg"), Image(".jpeg"), Image(".png"), Image(".bmp"), Image(".gif"), Image(".webp"), Image(".tif"), Image(".tiff"),
        new() { Extension = ".epub", Group = ProtectedDocumentGroup.Ebook, MicrosoftProgIds = [], WpsProgIdPrefixes = WpsPrefixes, SupportsIconProviderToggle = false },
        new() { Extension = ".mobi", Group = ProtectedDocumentGroup.Ebook, MicrosoftProgIds = [], WpsProgIdPrefixes = WpsPrefixes, SupportsIconProviderToggle = false }
    ];

    public OfficeSuiteCoexistenceStatus Detect(BackendUserContext? context)
    {
        var wpsCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var officeCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wpsInstalled = DetectWps(context, wpsCandidates);
        var officeInstalled = DetectMicrosoftOffice(officeCandidates);
        var active = wpsInstalled && officeInstalled;

        if (active)
        {
            _logger?.LogFireAndForget(
                $"WpsOfficeCoexistenceDetected: Sid={DiagnosticLogFormatter.FormatSid(context)}, WpsCandidates={string.Join(";", wpsCandidates)}, MicrosoftOfficeCandidates={string.Join(";", officeCandidates)}.");
        }

        return new OfficeSuiteCoexistenceStatus
        {
            IsWpsInstalled = wpsInstalled,
            IsMicrosoftOfficeInstalled = officeInstalled,
            IsCoexistenceActive = active,
            WpsIconSourceCandidates = wpsCandidates.ToArray(),
            MicrosoftOfficeIconSourceCandidates = officeCandidates.ToArray(),
            ProtectedAssociations = Catalog.Select(item => item with
            {
                CurrentProgId = context is null ? null : ReadUserExtensionDefault(context, item.Extension),
                CurrentOwner = context is null ? null : ClassifyOwner(ReadUserExtensionDefault(context, item.Extension))
            }).ToArray(),
            CurrentDocumentIconProvider = active && context is not null ? DetectCurrentIconProvider(context) : null
        };
    }

    public IReadOnlyList<ContextMenuEntry> DetectSyntheticEntries(BackendUserContext? context)
    {
        if (context is null || !Detect(context).IsCoexistenceActive)
        {
            return [];
        }

        var results = new List<ContextMenuEntry>();
        results.AddRange(DetectAssociationHijacks(context));
        results.AddRange(DetectIconHijacks(context));
        results.AddRange(DetectShellNewInjections(context));
        return results;
    }

    public PipeResponse SetDocumentIconProvider(BackendUserContext context, DocumentIconProvider provider)
    {
        var status = Detect(context);
        if (!status.IsCoexistenceActive)
        {
            return new PipeResponse
            {
                Success = false,
                Message = "WPS/Microsoft Office co-existence is not active."
            };
        }

        using var userClasses = OpenUserClasses(context, writable: true)
            ?? throw new InvalidOperationException("Unable to open the frontend user's Software\\Classes key.");

        var changed = 0;
        foreach (var spec in Catalog.Where(static item => item.SupportsIconProviderToggle))
        {
            foreach (var progId in spec.MicrosoftProgIds)
            {
                var targetIcon = provider == DocumentIconProvider.MicrosoftOffice
                    ? ReadDefaultIcon(Registry.LocalMachine, $@"{MachineClassesPath}\{progId}\DefaultIcon")
                    : FindWpsIconForSpec(context, spec);

                if (string.IsNullOrWhiteSpace(targetIcon))
                {
                    continue;
                }

                using var iconKey = userClasses.CreateSubKey($@"{progId}\DefaultIcon", writable: true);
                iconKey?.SetValue(null, targetIcon, RegistryValueKind.String);
                changed++;

                _logger?.LogFireAndForget(
                    $"WpsDocumentIconProviderChanged: Sid={context.Sid}, Provider={provider}, ProgId={progId}, CurrentValue={targetIcon}, ExpectedOrReferenceValue={targetIcon}, SpecialCode={IconHijackCode}.");
            }
        }

        ShellChangeNotifier.NotifyAssociationsChanged();
        return new PipeResponse
        {
            Success = true,
            Message = $"Document icon provider changed to {provider}. Updated {changed} user-level DefaultIcon values.",
            OfficeSuiteCoexistence = Detect(context)
        };
    }

    private IEnumerable<ContextMenuEntry> DetectAssociationHijacks(BackendUserContext context)
    {
        var details = new List<string>();
        var affectedExtensions = new List<string>();
        foreach (var spec in Catalog)
        {
            if (spec.MicrosoftProgIds.Count == 0)
            {
                continue;
            }

            var currentProgId = ReadUserExtensionDefault(context, spec.Extension);
            if (!IsWpsProgId(currentProgId) || !spec.MicrosoftProgIds.Any(MachineProgIdExists))
            {
                continue;
            }

            var path = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{spec.Extension}";
            affectedExtensions.Add(spec.Extension);
            details.Add($"extension={spec.Extension}, currentProgId={currentProgId}, expectedOfficeProgIds={string.Join(",", spec.MicrosoftProgIds)}, registryPath={path}");
            _logger?.LogFireAndForget(
                $"WpsOfficeAssociationHijackDetected: Sid={context.Sid}, Extension={spec.Extension}, CurrentValue={currentProgId}, ExpectedOrReferenceValue={string.Join(",", spec.MicrosoftProgIds)}, RegistryPath={path}, SpecialCode={AssociationHijackCode}.");
        }

        if (details.Count > 0)
        {
            var registryPath = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}";
            yield return CreateSyntheticEntry(
                id: "special:wps-office-association:document-formats",
                keyName: "Document formats",
                displayName: "WPS changed document file associations",
                registryPath: registryPath,
                details: $"specialCode={AssociationHijackCode}; detectedSuite=WPS Office; affectedCount={details.Count}; changes=[{string.Join(" | ", details)}]",
                notes: $"WPS changed {details.Count} document file association(s): {string.Join(", ", affectedExtensions)}.",
                changeKind: ContextMenuChangeKind.WpsOfficeAssociationHijack);
        }
    }

    private IEnumerable<ContextMenuEntry> DetectIconHijacks(BackendUserContext context)
    {
        var details = new List<string>();
        var affectedProgIds = new List<string>();
        foreach (var progId in Catalog.SelectMany(static spec => spec.MicrosoftProgIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var currentIcon = ReadDefaultIcon(Registry.Users, $@"{context.Sid}\{UserClassesPath}\{progId}\DefaultIcon");
            if (!IsWpsPath(currentIcon))
            {
                continue;
            }

            var hklmIcon = ReadDefaultIcon(Registry.LocalMachine, $@"{MachineClassesPath}\{progId}\DefaultIcon");
            var affectedExtensions = Catalog
                .Where(spec => spec.MicrosoftProgIds.Contains(progId, StringComparer.OrdinalIgnoreCase))
                .Select(static spec => spec.Extension)
                .ToArray();
            var path = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{progId}\DefaultIcon";
            affectedProgIds.Add(progId);
            details.Add($"progId={progId}, currentHkcuIconPath={currentIcon}, hklmOfficeIconPath={hklmIcon}, registryPath={path}, affectedExtensions={string.Join(",", affectedExtensions)}");
            _logger?.LogFireAndForget(
                $"WpsOfficeIconHijackDetected: Sid={context.Sid}, ProgId={progId}, CurrentValue={currentIcon}, ExpectedOrReferenceValue={hklmIcon}, RegistryPath={path}, SpecialCode={IconHijackCode}.");
        }

        if (details.Count > 0)
        {
            var registryPath = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}";
            yield return CreateSyntheticEntry(
                id: ContextMenuRegistryCatalog.WpsOfficeDocumentIconSyntheticId,
                keyName: "Document icons",
                displayName: "WPS changed document icons",
                registryPath: registryPath,
                details: $"specialCode={IconHijackCode}; detectedSuite=WPS Office; affectedCount={details.Count}; changes=[{string.Join(" | ", details)}]",
                notes: $"WPS changed {details.Count} Office document icon override(s): {string.Join(", ", affectedProgIds)}.",
                changeKind: ContextMenuChangeKind.WpsOfficeIconHijack);
        }
    }

    private IEnumerable<ContextMenuEntry> DetectShellNewInjections(BackendUserContext context)
    {
        using var userRoot = OpenUserRoot(context, writable: false);
        using var orderKey = userRoot?.OpenSubKey(ShellNewOrderPath, writable: false);
        if (orderKey?.GetValue("Classes") is not string[] classes)
        {
            yield break;
        }

        foreach (var className in classes.Where(IsWpsShellNewClass).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var shellNewKey = userRoot?.OpenSubKey($@"{UserClassesPath}\{className}\ShellNew", writable: false);
            var command = shellNewKey?.GetValue("Command")?.ToString();
            if (!IsWpsPath(command))
            {
                continue;
            }

            var path = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{className}\ShellNew";
            var orderPath = $@"HKEY_USERS\{context.Sid}\{ShellNewOrderPath}";
            var details = $"specialCode={ShellNewInjectionCode}; className={className}; shellNewCommand={command}; shellNewOrderLocation={orderPath}; registryPath={path}";
            _logger?.LogFireAndForget(
                $"WpsOfficeShellNewInjectionDetected: Sid={context.Sid}, Class={className}, CurrentValue={command}, ExpectedOrReferenceValue=<none>, RegistryPath={path}, SpecialCode={ShellNewInjectionCode}.");
            yield return CreateSyntheticEntry(
                id: $"special:wps-shellnew-injection:{className}",
                keyName: className,
                displayName: className.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    ? "WPS injected PDF ShellNew item"
                    : $"WPS injected {className} ShellNew item",
                registryPath: path,
                details: details,
                changeKind: ContextMenuChangeKind.WpsOfficeShellNewInjection,
                commandText: command);
        }
    }

    private bool DetectWps(BackendUserContext? context, ISet<string> candidates)
    {
        foreach (var progId in WpsProbeProgIds)
        {
            if (context is not null && UserProgIdExists(context, progId))
            {
                candidates.Add($@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{progId}");
            }

            if (MachineProgIdExists(progId))
            {
                candidates.Add($@"HKEY_LOCAL_MACHINE\{MachineClassesPath}\{progId}");
            }

            AddIconCandidateIfWps(context, progId, candidates);
        }

        if (context is not null)
        {
            AddUninstallCandidates(Registry.Users, $@"{context.Sid}\Software\Microsoft\Windows\CurrentVersion\Uninstall", candidates, IsWpsPath);
        }

        AddUninstallCandidates(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", candidates, IsWpsPath);
        AddUninstallCandidates(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", candidates, IsWpsPath);
        return candidates.Count > 0;
    }

    private static bool DetectMicrosoftOffice(ISet<string> candidates)
    {
        foreach (var progId in MicrosoftProbeProgIds)
        {
            if (MachineProgIdExists(progId))
            {
                candidates.Add($@"HKEY_LOCAL_MACHINE\{MachineClassesPath}\{progId}");
            }

            var icon = ReadDefaultIcon(Registry.LocalMachine, $@"{MachineClassesPath}\{progId}\DefaultIcon");
            if (IsMicrosoftOfficePath(icon))
            {
                candidates.Add(icon!);
            }
        }

        foreach (var path in new[]
                 {
                     @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
                     @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
                     @"C:\Program Files\Microsoft Office\root\Office16\POWERPNT.EXE",
                     @"C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
                     @"C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE",
                     @"C:\Program Files (x86)\Microsoft Office\root\Office16\POWERPNT.EXE"
                 })
        {
            if (File.Exists(path))
            {
                candidates.Add(path);
            }
        }

        return candidates.Count > 0;
    }

    private static ContextMenuEntry CreateSyntheticEntry(
        string id,
        string keyName,
        string displayName,
        string registryPath,
        string details,
        ContextMenuChangeKind changeKind,
        string? notes = null,
        string? commandText = null)
        => new()
        {
            Id = id,
            Category = ContextMenuCategory.File,
            EntryKind = ContextMenuEntryKind.ShellVerb,
            KeyName = keyName,
            DisplayName = displayName,
            RegistryPath = registryPath,
            BackendRegistryPath = registryPath,
            SourceRootPath = "special:wps-office-coexistence",
            CommandText = commandText,
            IsEnabled = true,
            IsPresentInRegistry = true,
            IsPendingApproval = true,
            DetectedChangeKind = changeKind,
            DetectedChangeDetails = details,
            Notes = notes ?? details
        };

    private static DocumentIconProvider DetectCurrentIconProvider(BackendUserContext context)
    {
        var wps = 0;
        var microsoft = 0;
        foreach (var progId in Catalog.SelectMany(static spec => spec.MicrosoftProgIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var icon = ReadDefaultIcon(Registry.Users, $@"{context.Sid}\{UserClassesPath}\{progId}\DefaultIcon")
                       ?? ReadDefaultIcon(Registry.LocalMachine, $@"{MachineClassesPath}\{progId}\DefaultIcon");
            if (IsWpsPath(icon))
            {
                wps++;
            }
            else if (IsMicrosoftOfficePath(icon))
            {
                microsoft++;
            }
        }

        return wps > microsoft ? DocumentIconProvider.WpsOffice : DocumentIconProvider.MicrosoftOffice;
    }

    private static string? FindWpsIconForSpec(BackendUserContext context, ProtectedDocumentAssociation spec)
    {
        var extensionProgId = ReadUserExtensionDefault(context, spec.Extension);
        if (IsWpsProgId(extensionProgId))
        {
            var icon = ReadDefaultIcon(Registry.Users, $@"{context.Sid}\{UserClassesPath}\{extensionProgId}\DefaultIcon")
                       ?? ReadDefaultIcon(Registry.LocalMachine, $@"{MachineClassesPath}\{extensionProgId}\DefaultIcon");
            if (IsWpsPath(icon))
            {
                return icon;
            }
        }

        return Catalog
            .Where(item => item.Group == spec.Group)
            .Select(item => ReadUserExtensionDefault(context, item.Extension))
            .Where(IsWpsProgId)
            .Select(progId => ReadDefaultIcon(Registry.Users, $@"{context.Sid}\{UserClassesPath}\{progId}\DefaultIcon"))
            .FirstOrDefault(IsWpsPath);
    }

    private void AddIconCandidateIfWps(BackendUserContext? context, string progId, ISet<string> candidates)
    {
        if (context is not null)
        {
            var userIcon = ReadDefaultIcon(Registry.Users, $@"{context.Sid}\{UserClassesPath}\{progId}\DefaultIcon");
            if (IsWpsPath(userIcon))
            {
                candidates.Add(userIcon!);
            }
        }

        var machineIcon = ReadDefaultIcon(Registry.LocalMachine, $@"{MachineClassesPath}\{progId}\DefaultIcon");
        if (IsWpsPath(machineIcon))
        {
            candidates.Add(machineIcon!);
        }
    }

    private static void AddUninstallCandidates(RegistryKey root, string subPath, ISet<string> candidates, Func<string?, bool> predicate)
    {
        using var key = root.OpenSubKey(subPath, writable: false);
        if (key is null)
        {
            return;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            using var app = key.OpenSubKey(name, writable: false);
            var text = string.Join(";", app?.GetValue("DisplayName"), app?.GetValue("InstallLocation"), app?.GetValue("DisplayIcon"));
            if (predicate(text))
            {
                candidates.Add($@"{root.Name}\{subPath}\{name}");
            }
        }
    }

    private static string? ReadUserExtensionDefault(BackendUserContext context, string extension)
    {
        using var key = Registry.Users.OpenSubKey($@"{context.Sid}\{UserClassesPath}\{extension}", writable: false);
        return key?.GetValue(null)?.ToString();
    }

    private static string? ReadDefaultIcon(RegistryKey root, string subPath)
    {
        using var key = root.OpenSubKey(subPath, writable: false);
        return key?.GetValue(null)?.ToString();
    }

    private static RegistryKey? OpenUserRoot(BackendUserContext context, bool writable)
        => string.IsNullOrWhiteSpace(context.Sid) ? null : Registry.Users.OpenSubKey(context.Sid, writable);

    private static RegistryKey? OpenUserClasses(BackendUserContext context, bool writable)
        => Registry.Users.OpenSubKey($@"{context.Sid}\{UserClassesPath}", writable);

    private static bool UserProgIdExists(BackendUserContext context, string progId)
    {
        using var key = Registry.Users.OpenSubKey($@"{context.Sid}\{UserClassesPath}\{progId}", writable: false);
        return key is not null;
    }

    private static bool MachineProgIdExists(string progId)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{MachineClassesPath}\{progId}", writable: false);
        return key is not null;
    }

    private static bool IsWpsShellNewClass(string className)
        => className.Contains("wps", StringComparison.OrdinalIgnoreCase)
           || className.Contains("kwps", StringComparison.OrdinalIgnoreCase)
           || className.Contains("ket", StringComparison.OrdinalIgnoreCase)
           || className.Contains("wpp", StringComparison.OrdinalIgnoreCase)
           || className.Contains("kingsoft", StringComparison.OrdinalIgnoreCase)
           || className.Contains("aicreate", StringComparison.OrdinalIgnoreCase)
           || className.Contains("wpsshellnew", StringComparison.OrdinalIgnoreCase);

    private static bool IsWpsProgId(string? progId)
        => !string.IsNullOrWhiteSpace(progId)
           && WpsPrefixes.Any(prefix => progId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsWpsPath(string? value)
        => ContainsAny(value, WpsMarkers) || IsWpsProgId(value);

    private static bool IsMicrosoftOfficePath(string? value)
        => ContainsAny(value, MicrosoftMarkers);

    private static bool ContainsAny(string? value, IEnumerable<string> markers)
        => !string.IsNullOrWhiteSpace(value)
           && markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string? ClassifyOwner(string? progId)
    {
        if (IsWpsProgId(progId))
        {
            return "WPS Office";
        }

        if (!string.IsNullOrWhiteSpace(progId) && progId.Contains("Office", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Office";
        }

        return string.IsNullOrWhiteSpace(progId) ? "unknown" : "system";
    }

    private static ProtectedDocumentAssociation Word(string extension, params string[] progIds)
        => new() { Extension = extension, Group = ProtectedDocumentGroup.Word, MicrosoftProgIds = progIds, WpsProgIdPrefixes = WpsPrefixes, SupportsIconProviderToggle = progIds.Length > 0 };

    private static ProtectedDocumentAssociation Excel(string extension, params string[] progIds)
        => new() { Extension = extension, Group = ProtectedDocumentGroup.Excel, MicrosoftProgIds = progIds, WpsProgIdPrefixes = WpsPrefixes, SupportsIconProviderToggle = progIds.Length > 0 };

    private static ProtectedDocumentAssociation PowerPoint(string extension, params string[] progIds)
        => new() { Extension = extension, Group = ProtectedDocumentGroup.PowerPoint, MicrosoftProgIds = progIds, WpsProgIdPrefixes = WpsPrefixes, SupportsIconProviderToggle = progIds.Length > 0 };

    private static ProtectedDocumentAssociation Image(string extension)
        => new() { Extension = extension, Group = ProtectedDocumentGroup.Image, MicrosoftProgIds = [], WpsProgIdPrefixes = WpsPrefixes, SupportsIconProviderToggle = false };
}
