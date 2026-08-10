using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class PackagedShellNewDiscoveryTests
{
    [Fact]
    public void ParseManifest_OnlyReturnsFileTypesWithShellNewFileName_AndPreservesProvenance()
    {
        const string manifest = """
            <Package xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:uap4="http://schemas.microsoft.com/appx/manifest/uap/windows10/4" xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
              <Applications>
                <Application Id="App" Executable="App.exe" EntryPoint="Windows.FullTrustApplication">
                  <Extensions>
                    <uap:Extension Category="windows.fileTypeAssociation">
                      <uap:FileTypeAssociation Name="txtfile">
                        <uap:SupportedFileTypes>
                          <uap:FileType uap4:ShellNewFileName="Assets\New.txt" uap4:ShellNewDisplayName="ms-resource:NewText" uap10:ShellNewCommandParameters="/new">.txt</uap:FileType>
                          <uap:FileType>.log</uap:FileType>
                        </uap:SupportedFileTypes>
                      </uap:FileTypeAssociation>
                    </uap:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;

        var declarations = PackagedShellNewDiscovery.ParseManifest(
            manifest,
            "Contoso.Editor_1.0.0.0_x64__abc",
            "Contoso.Editor_abc",
            "Contoso.Editor",
            @"C:\Program Files\WindowsApps\Contoso.Editor\AppxManifest.xml");

        var declaration = Assert.Single(declarations);
        Assert.Equal(".txt", declaration.Extension);
        Assert.Equal("Contoso.Editor_abc", declaration.PackageFamilyName);
        Assert.Equal("App", declaration.ApplicationId);
        Assert.Equal("Assets\\New.txt", declaration.ShellNewFileName);
        Assert.Equal("ms-resource:NewText", declaration.ShellNewDisplayName);
        Assert.Equal("/new", declaration.ShellNewCommandParameters);
        Assert.Contains("ShellNewFileName", declaration.FileTypeDeclaration, StringComparison.Ordinal);
        Assert.Contains(".txt", declaration.FileTypeDeclaration, StringComparison.Ordinal);
    }
}
