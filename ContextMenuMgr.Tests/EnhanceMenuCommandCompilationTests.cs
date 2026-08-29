using System.Xml.Linq;
using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class EnhanceMenuCommandCompilationTests
{
    [Fact]
    public void TakeOwnership_File_GrantsAdministratorsFullControlAfterOwnershipSucceeds()
    {
        var command = CompileCommand("HKEY_CLASSES_ROOT\\*", "TakeOwnerShip");

        Assert.Contains("takeown.exe /f $p /a", command, StringComparison.Ordinal);
        Assert.Contains("icacls.exe $p /grant ''*S-1-5-32-544:F''", command, StringComparison.Ordinal);
        Assert.Contains("\"%1\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrators", command, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            command.IndexOf("takeown.exe", StringComparison.Ordinal)
            < command.IndexOf("$LASTEXITCODE -ne 0", StringComparison.Ordinal)
            && command.IndexOf("$LASTEXITCODE -ne 0", StringComparison.Ordinal)
            < command.IndexOf("icacls.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void TakeOwnership_Directory_UsesRecursiveOwnershipAndAclGrant()
    {
        var command = CompileCommand("HKEY_CLASSES_ROOT\\Directory", "TakeOwnerShip");

        Assert.Contains("takeown.exe /f $p /a /r /d y", command, StringComparison.Ordinal);
        Assert.Contains("icacls.exe $p /grant ''*S-1-5-32-544:F'' /t /c", command, StringComparison.Ordinal);
        Assert.Contains("\"%v\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrators", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ElevatedPowerShell_WaitsForChildAndReturnsItsExitCode()
    {
        var command = CompileCommand("HKEY_CLASSES_ROOT\\*", "TakeOwnerShip");

        Assert.Contains("Start-Process powershell.exe -Verb RunAs -Wait -PassThru", command, StringComparison.Ordinal);
        Assert.Contains("exit $process.ExitCode", command, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanThumbCache_CompilesReferenceBatBehaviorWithRuntimeUserProfileAndExplorerRecovery()
    {
        var command = CompileCommand("HKEY_CLASSES_ROOT\\CLSID\\{645FF040-5081-101B-9F08-00AA002F954E}", "CleanThumbCache");

        Assert.DoesNotContain("Start-Process powershell.exe -Verb RunAs", command, StringComparison.Ordinal);
        Assert.Contains("$env:USERPROFILE", command, StringComparison.Ordinal);
        Assert.DoesNotContain("systemprofile", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stop-Process -Name explorer -Force -ErrorAction Stop", command, StringComparison.Ordinal);
        Assert.Contains("attrib.exe -h -s -r $iconCache", command, StringComparison.Ordinal);
        Assert.Contains("attrib.exe /s /d -h -s -r", command, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $iconCache -Force -ErrorAction Stop", command, StringComparison.Ordinal);
        foreach (var cacheName in new[]
                 {
                     "thumbcache_32.db", "thumbcache_96.db", "thumbcache_102.db", "thumbcache_256.db",
                     "thumbcache_1024.db", "thumbcache_idx.db", "thumbcache_sr.db"
                 })
        {
            Assert.Contains(cacheName, command, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("iconcache_*.db", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'IconStreams', 'PastIconsStream'", command, StringComparison.Ordinal);
        Assert.Contains("Remove-ItemProperty -LiteralPath $trayNotify -Name $name -ErrorAction Stop", command, StringComparison.Ordinal);
        Assert.Contains("try {", command, StringComparison.Ordinal);
        Assert.Contains("AutoRestartShell", command, StringComparison.Ordinal);
        Assert.Contains("finally { if ($autoRestartShell -eq 0) { Start-Process -FilePath 'C:\\Windows\\explorer.exe' } }", command, StringComparison.Ordinal);
        Assert.True(command.IndexOf("try {", StringComparison.Ordinal) < command.IndexOf("Stop-Process", StringComparison.Ordinal));
        Assert.True(command.IndexOf("Remove-ItemProperty", StringComparison.Ordinal) < command.IndexOf("finally", StringComparison.Ordinal));
    }

    [Fact]
    public void EnhanceMenuDictionary_ValidatesAfterCommandChanges()
    {
        using var writer = new StringWriter();

        var exitCode = ContextMenuRegistryCatalog.ValidateEnhanceMenuDictionary(DictionaryPath, "en-US", writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("validation passed", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string CompileCommand(string registryPath, string keyName)
    {
        var document = XDocument.Load(DictionaryPath);
        var command = document
            .Descendants("Group")
            .Single(group => string.Equals(group.Element("RegPath")?.Value.Trim(), registryPath, StringComparison.Ordinal))
            .Element("Shell")!
            .Elements("Item")
            .Single(item => string.Equals(item.Attribute("KeyName")?.Value, keyName, StringComparison.Ordinal))
            .Descendants("Command")
            .Single();

        return ContextMenuRegistryCatalog.CompileEnhanceCommandForValidation(command, "en-US");
    }

    private static string DictionaryPath => FindRepositoryFile(Path.Combine("ContextMenuMgr.Frontend", "Resources", "EnhanceMenusDic.xml"));

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not find the Enhance Menu dictionary.", relativePath);
    }
}
