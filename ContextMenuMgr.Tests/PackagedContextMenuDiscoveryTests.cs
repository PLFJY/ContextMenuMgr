using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class PackagedContextMenuDiscoveryTests
{
    private const string PythonClsid = "{C7E29CB0-9691-4DE8-B72B-6719DDC0B4A1}";

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
        var malformed = CreatePackage(fullName: "Broken.Package_1.0.0.0_x64__abc");
        var valid = CreatePackage(fullName: "Valid.Package_1.0.0.0_x64__abc");

        var definitions = PackagedContextMenuDiscovery.Discover(
            [malformed, valid],
            package => package.FullName.StartsWith("Broken", StringComparison.Ordinal)
                ? "<Package"
                : PythonManifest);

        var definition = Assert.Single(definitions);
        Assert.Equal(valid.FullName, definition.Package.FullName);
    }

    [Fact]
    public void MapCategories_PreservesExistingMappings()
    {
        var categories = Windows11ContextMenuCatalog.MapCategories(
                ["File: .py", "Directory", "File: Directory\\Background", "File: Drive", "File: Folder"])
            .ToHashSet();

        Assert.Contains(ContextMenuCategory.File, categories);
        Assert.Contains(ContextMenuCategory.Directory, categories);
        Assert.Contains(ContextMenuCategory.DirectoryBackground, categories);
        Assert.Contains(ContextMenuCategory.Drive, categories);
        Assert.Contains(ContextMenuCategory.Folder, categories);
    }

    [Theory]
    [InlineData(false, false, true, "None")]
    [InlineData(false, true, false, "User")]
    [InlineData(true, false, false, "Machine")]
    [InlineData(true, true, false, "Both")]
    public void CreateBlockedState_CombinesMachineAndUserScopes(
        bool machineBlocked,
        bool userBlocked,
        bool expectedEnabled,
        string expectedSource)
    {
        var state = Windows11ContextMenuCatalog.CreateBlockedState(machineBlocked, userBlocked);

        Assert.Equal(expectedEnabled, state.IsEnabled);
        Assert.Equal(machineBlocked, state.IsMachineBlocked);
        Assert.Equal(userBlocked, state.IsUserBlocked);
        Assert.Equal(expectedSource, state.Source);
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
