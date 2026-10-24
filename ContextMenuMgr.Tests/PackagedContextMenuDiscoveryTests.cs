using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using System.Runtime.InteropServices;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class PackagedContextMenuDiscoveryTests
{
    private const string PythonClsid = "{C7E29CB0-9691-4DE8-B72B-6719DDC0B4A1}";

    // Permanent: colliding declarations retain their sources and cannot authorize automatic blocking.
    [Fact]
    public async Task SharedClsid_PreservesPackagesAndClassicReferences_AndSuspendsAutomaticBlocking()
    {
        var first = Assert.Single(PackagedContextMenuDiscovery.ParseManifest(PythonManifest, CreatePackage()));
        var second = first with { Package = CreatePackage("Other.Package_1.0.0.0_x64__abc", "Other.Package_abc") };
        var catalog = new Windows11ContextMenuCatalog(null, new PackagedContextMenuScanCache(_ => [first, second]));
        var context = new BackendUserContext(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
            Environment.UserName, string.Empty, string.Empty, string.Empty, null);
        var snapshot = await catalog.EnumerateEntriesAsync(CancellationToken.None, context);
        var packaged = snapshot.Where(entry => entry.Category == ContextMenuCategory.File
            && entry.Windows11SourceKind == Windows11ContextMenuSourceKind.PackagedCom).ToArray();
        Assert.Equal(2, packaged.Length);
        Assert.All(packaged, entry =>
        {
            Assert.False(entry.CanAutomaticallyDisable);
            Assert.Equal(2, entry.Windows11ClsidImpact!.References.Where(reference => reference.Kind == "Package")
                .Select(reference => reference.PackageFullName).Distinct().Count());
            Assert.False(ContextMenuChangeClassifier.ShouldReconcileDisabledState(entry with { IsEnabled = true },
                PersistedContextMenuState.FromEntry(entry with { IsEnabled = false })));
        });

        var path = @"Software\ContextMenuMgr.Tests\Win11References\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path)!;
            using (var classic = root.CreateSubKey(@"Test.ProgId\shell\Open"))
                classic.SetValue("ExplorerCommandHandler", PythonClsid);
            using (var direct = root.CreateSubKey(@"Test.ProgId\shellex\DropHandler"))
                direct.SetValue(null, PythonClsid);
            using (var named = root.CreateSubKey($@"Test.ProgId\shellex\ContextMenuHandlers\{PythonClsid}"))
                named.SetValue(null, "Invalid default; the GUID key is authoritative");
            var classicIndex = new Windows11ClsidImpactIndex([first]);
            classicIndex.ReadClasses(root, CancellationToken.None);
            var impact = classicIndex.Get(PythonClsid);
            Assert.False(impact.AllowsAutomaticDisable);
            Assert.Contains(impact.References, reference => reference.Kind == "Classic"
                && reference.RegistryPath!.EndsWith(@"Test.ProgId\shell\Open"));
            Assert.Contains(impact.References, reference => reference.RegistryPath!.EndsWith(@"shellex\DropHandler"));
            Assert.Contains(impact.References, reference => reference.RegistryPath!.EndsWith($@"ContextMenuHandlers\{PythonClsid}"));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    // Permanent: upgrade must preserve the original package policy without creating false Added/pending states.
    [Fact]
    public void LegacyIdentityMigration_PreservesKnownPolicy_AndAdoptsPreviouslyHiddenSources()
    {
        var definition = Assert.Single(PackagedContextMenuDiscovery.ParseManifest(PythonManifest, CreatePackage()));
        var original = Windows11ContextMenuCatalog.CreateEntry(definition, ContextMenuCategory.File, new(false, true), "sid");
        var other = Windows11ContextMenuCatalog.CreateEntry(definition with
        { Package = CreatePackage("Other.Package_1.0.0.0_x64__abc", "Other.Package_abc") }, ContextMenuCategory.File, new(false, true), "sid");
        var legacyId = $"win11|{original.HandlerClsid}|File";
        var state = PersistedContextMenuState.FromEntry(original with { Id = legacyId });
        state.IsPendingApproval = true;
        var states = new Dictionary<string, PersistedContextMenuState>(StringComparer.OrdinalIgnoreCase) { [legacyId] = state };
        Assert.True(Windows11ContextMenuCatalog.MigrateStateIdentities(states, [original, other]));
        Assert.False(states.ContainsKey(legacyId));
        Assert.True(states[original.Id].IsPendingApproval);
        Assert.False(states[other.Id].IsPendingApproval);
        Assert.False(states[other.Id].DesiredEnabled);
        Assert.Equal(ContextMenuChangeKind.None, ContextMenuChangeClassifier.GetDetectedChangeKind(other, states[other.Id], true));
        Assert.False(Windows11ContextMenuCatalog.MigrateStateIdentities(states, [original, other]));
    }

    [Fact]
    public void ParseManifest_UnversionedComSurrogateServer_RemainsSupported()
    {
        const string manifest = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
                     xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <desktop4:Extension Category="windows.fileExplorerContextMenus">
                      <desktop4:FileExplorerContextMenus>
                        <desktop4:ItemType Type="Directory">
                          <desktop4:Verb Id="OpenWithContoso" Clsid="{11111111-1111-1111-1111-111111111111}" />
                        </desktop4:ItemType>
                      </desktop4:FileExplorerContextMenus>
                    </desktop4:Extension>
                  </Extensions>
                </Application>
              </Applications>
              <Extensions>
                <com:Extension Category="windows.comServer">
                  <com:ComServer>
                    <com:SurrogateServer DisplayName="Contoso Shell Extension">
                      <com:Class Id="{11111111-1111-1111-1111-111111111111}" Path="bin\ContosoShell.dll" />
                    </com:SurrogateServer>
                  </com:ComServer>
                </com:Extension>
              </Extensions>
            </Package>
            """;

        var definition = Assert.Single(PackagedContextMenuDiscovery.ParseManifest(manifest, CreatePackage()));

        Assert.Equal("{11111111-1111-1111-1111-111111111111}", definition.Clsid);
        Assert.Equal("OpenWithContoso", Assert.Single(definition.Verbs).Id);
        Assert.Equal("Directory", Assert.Single(definition.ContextTypes));
        Assert.Equal("SurrogateServer", definition.ComServer.ServerKind);
        Assert.Equal(Path.Combine(CreatePackage().InstallPath, @"bin\ContosoShell.dll"), definition.ComServer.Path);
    }

    [Fact]
    public void ParseManifest_Com4ExeServer_PreservesPythonStyleMetadata()
    {
        var definition = Assert.Single(PackagedContextMenuDiscovery.ParseManifest(PythonManifest, CreatePackage(
            fullName: "PythonSoftwareFoundation.PythonManager_25.0.0.0_x64__qbz5n2kfra8p0",
            familyName: "PythonSoftwareFoundation.PythonManager_qbz5n2kfra8p0",
            displayName: "Python Install Manager")));

        Assert.Equal(PythonClsid, definition.Clsid, ignoreCase: true);
        Assert.Equal("File: .py", Assert.Single(definition.ContextTypes));
        Assert.Equal("EditInIdle", Assert.Single(definition.Verbs).Id);
        Assert.Equal("pyshellext.exe", definition.ComServer.DeclaredPath);
        Assert.Equal("ExeServer", definition.ComServer.ServerKind);
        Assert.Equal("Python Shell Extension", definition.ComServer.ServerDisplayName);
        Assert.Equal("EditInIdleCommand", definition.ComServer.ClassDisplayName);
        Assert.EndsWith("pyshellext.exe", definition.ComServer.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PythonSoftwareFoundation.PythonManager_qbz5n2kfra8p0", definition.Package.FamilyName);
    }

    [Fact]
    public void ParseManifest_ReturnsOnlyComClassReferencedByContextMenuVerb()
    {
        var manifest = PythonManifest.Replace(
            "</com4:ExeServer>",
            "<com4:Class Id=\"{22222222-2222-2222-2222-222222222222}\" DisplayName=\"UnusedCommand\" /></com4:ExeServer>",
            StringComparison.Ordinal);

        var definition = Assert.Single(PackagedContextMenuDiscovery.ParseManifest(manifest, CreatePackage()));

        Assert.Equal(PythonClsid, definition.Clsid, ignoreCase: true);
    }

    [Fact]
    public void ParseManifest_SkipsMalformedOrUncorrelatedDeclarations_WithoutLosingValidItem()
    {
        var manifest = PythonManifest
            .Replace(
                "</desktop4:ItemType>",
                "<desktop4:Verb Id=\"BadGuid\" Clsid=\"not-a-guid\" /><desktop4:Verb Id=\"MissingServer\" Clsid=\"{33333333-3333-3333-3333-333333333333}\" /></desktop4:ItemType>",
                StringComparison.Ordinal)
            .Replace(
                "</com4:ExeServer>",
                "<com4:Class Id=\"also-not-a-guid\" /></com4:ExeServer>",
                StringComparison.Ordinal);

        var definition = Assert.Single(PackagedContextMenuDiscovery.ParseManifest(manifest, CreatePackage()));

        Assert.Equal(PythonClsid, definition.Clsid, ignoreCase: true);
        Assert.Equal("EditInIdle", Assert.Single(definition.Verbs).Id);
    }

    [Fact]
    public void ParseManifest_DoesNotFabricateComPathWhenManifestDoesNotDeclareOne()
    {
        var manifest = PythonManifest.Replace(
            " Executable=\"pyshellext.exe\"",
            string.Empty,
            StringComparison.Ordinal);

        var definition = Assert.Single(PackagedContextMenuDiscovery.ParseManifest(manifest, CreatePackage()));

        Assert.Null(definition.ComServer.DeclaredPath);
        Assert.Null(definition.ComServer.Path);
    }

    [Fact]
    public void Discover_DeduplicatesSamePackageFullName()
    {
        var package = CreatePackage();
        var loadCount = 0;

        var definitions = PackagedContextMenuDiscovery.Discover(
            [package, package with { DisplayName = "Duplicate hint" }],
            _ =>
            {
                loadCount++;
                return PythonManifest;
            });

        Assert.Single(definitions);
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public void Discover_MalformedPackageDoesNotAbortOtherPackages()
    {
        using var coverage = MenuDiscoveryCoverage.Begin();
        var malformed = CreatePackage(fullName: "Broken.Package_1.0.0.0_x64__abc");
        var valid = CreatePackage(fullName: "Valid.Package_1.0.0.0_x64__abc");

        var definitions = PackagedContextMenuDiscovery.Discover(
            [malformed, valid],
            package => package.FullName.StartsWith("Broken", StringComparison.Ordinal)
                ? "<Package"
                : PythonManifest);

        var definition = Assert.Single(definitions);
        Assert.Equal(valid.FullName, definition.Package.FullName);
        Assert.False(definition.DiscoveryComplete);
        Assert.False(coverage.Complete);
        Assert.True(coverage.ClassicComplete);
        Assert.False(new Windows11ClsidImpactIndex(definitions).Get(definition.Clsid).AllowsAutomaticDisable);
    }

    // Permanent: unavailable service-side localized labels must not hide declarations
    // or prevent deletion diff; unreadable manifests still make discovery incomplete.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnavailableDisplayMetadata_PreservesDeclarationsAndCompleteCoverage(bool unavailableName, bool unavailablePublisher)
    {
        using var coverage = MenuDiscoveryCoverage.Begin();
        var package = CreatePackage();
        package = package with
        {
            DisplayName = PackagedContextMenuDiscovery.ReadOptionalDisplayMetadata(
                () => unavailableName ? throw new COMException("Package resources unavailable", unchecked((int)0x80073B37)) : package.DisplayName,
                package.Name, out var nameFailure),
            PublisherDisplayName = PackagedContextMenuDiscovery.ReadOptionalDisplayMetadata(
                () => unavailablePublisher ? throw new COMException("Package resources unavailable", unchecked((int)0x80073B37)) : package.PublisherDisplayName,
                string.Empty, out var publisherFailure)
        };

        var definition = Assert.Single(PackagedContextMenuDiscovery.Discover([package], _ => PythonManifest));
        Assert.Equal(PythonClsid, definition.Clsid, ignoreCase: true);
        Assert.Equal("EditInIdle", Assert.Single(definition.Verbs).Id);
        Assert.Equal(package.FamilyName, definition.Package.FamilyName);
        Assert.Equal(unavailableName ? package.Name : "Contoso Package", definition.Package.DisplayName);
        Assert.Equal(unavailablePublisher ? string.Empty : "Contoso Ltd.", definition.Package.PublisherDisplayName);
        Assert.Equal(unavailableName, nameFailure is COMException);
        Assert.Equal(unavailablePublisher, publisherFailure is COMException);
        Assert.True(definition.DiscoveryComplete);
        Assert.True(coverage.Complete);
        Assert.True(coverage.ClassicComplete);
    }

    [Fact]
    public void FrontendProjection_PreservesMachineBlockAndPackagedMetadata()
    {
        var entry = new ContextMenuEntry
        {
            Id = $"win11|{PythonClsid}|File",
            Category = ContextMenuCategory.File,
            EntryKind = ContextMenuEntryKind.ShellExtension,
            DisplayName = "Python Shell Extension",
            RegistryPath = $@"PackagedCom\Package\Python.Package_1.0.0.0_x64__abc\Class\{PythonClsid}",
            HandlerClsid = PythonClsid,
            FilePath = @"C:\Program Files\WindowsApps\Python.Package\pyshellext.exe",
            IsWindows11ContextMenu = true,
            IsEnabled = false,
            IsMachineBlocked = true,
            Windows11PackageFullName = "Python.Package_1.0.0.0_x64__abc",
            Windows11PackageFamilyName = "Python.Package_abc",
            Windows11PackageDisplayName = "Python Install Manager",
            Windows11PackagePublisherDisplayName = "Python Software Foundation",
            Windows11PackageInstallPath = @"C:\Program Files\WindowsApps\Python.Package",
            Windows11ContextTypes = ["File: .py"],
            Windows11Verbs =
            [
                new Windows11ContextMenuVerbMetadata
                {
                    Id = "EditInIdle",
                    HandlerClsid = PythonClsid,
                    ContextType = "File: .py"
                }
            ],
            Windows11ComServerDisplayName = "Python Shell Extension",
            Windows11ComClassDisplayName = "EditInIdleCommand"
        };

        var definition = Windows11ContextMenuService.CreateDefinition(entry);

        Assert.True(definition.IsMachineBlocked);
        Assert.False(definition.IsEnabled);
        Assert.Equal("Python.Package_1.0.0.0_x64__abc", definition.Package.FullName);
        Assert.Equal("Python.Package_abc", definition.Package.FamilyName);
        Assert.Equal("Python Software Foundation", definition.Package.PublisherDisplayName);
        Assert.Equal("File: .py", Assert.Single(definition.ContextTypes));
        Assert.Equal("EditInIdle", Assert.Single(definition.ContextMenus).Id);
        Assert.EndsWith("pyshellext.exe", definition.ComServer.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static PackagedContextMenuPackage CreatePackage(
        string fullName = "Contoso.Package_1.0.0.0_x64__abc",
        string familyName = "Contoso.Package_abc",
        string displayName = "Contoso Package") =>
        new(
            fullName,
            familyName,
            "Contoso.Package",
            displayName,
            "Contoso Ltd.",
            @"C:\Program Files\WindowsApps\Contoso.Package",
            @"C:\Program Files\WindowsApps\Contoso.Package\AppxManifest.xml");

    private const string PythonManifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
                 xmlns:com4="http://schemas.microsoft.com/appx/manifest/com/windows10/4">
          <Applications>
            <Application Id="PythonManager">
              <Extensions>
                <desktop4:Extension Category="windows.fileExplorerContextMenus">
                  <desktop4:FileExplorerContextMenus>
                    <desktop4:ItemType Type=".py">
                      <desktop4:Verb Id="EditInIdle" Clsid="C7E29CB0-9691-4DE8-B72B-6719DDC0B4A1" />
                    </desktop4:ItemType>
                  </desktop4:FileExplorerContextMenus>
                </desktop4:Extension>
              </Extensions>
            </Application>
          </Applications>
          <Extensions>
            <com4:Extension Category="windows.comServer">
              <com4:ComServer>
                <com4:ExeServer Executable="pyshellext.exe" DisplayName="Python Shell Extension">
                  <com4:Class Id="C7E29CB0-9691-4DE8-B72B-6719DDC0B4A1" DisplayName="EditInIdleCommand" />
                </com4:ExeServer>
              </com4:ComServer>
            </com4:Extension>
          </Extensions>
        </Package>
        """;
}
