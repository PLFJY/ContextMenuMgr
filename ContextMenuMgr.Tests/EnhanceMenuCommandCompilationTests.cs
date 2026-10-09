using System.Diagnostics;
using System.Xml.Linq;
using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class EnhanceMenuCommandCompilationTests
{
    [Theory]
    [InlineData(@"D:\Downloads\Project Plan v2.docx")]
    [InlineData(@"D:\资料\年度 总结 2026.pdf")]
    [InlineData("D:\\test\\a'b $c `d; & (x) %y%,=#[].txt")]
    public void CopyAsPath_PowerShellReceivesCompleteLiteralPath(string path)
    {
        var command = XDocument.Load(DictionaryPath).Descendants("Item")
            .Single(item => item.Attribute("KeyName")?.Value == "CopyAsPath")
            .Descendants("Command").Single().Attribute("Default")!.Value;
        // Exercise the shipped command's parsing without changing the clipboard.
        var testCommand = command.Replace("[Windows.Clipboard]::SetText(", "[Console]::Write(", StringComparison.Ordinal)
            .Replace("%1", path, StringComparison.Ordinal);
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = testCommand["powershell.exe ".Length..],
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        Assert.Equal(path, output);
    }

    [Fact]
    public void EnhanceMenuDictionary_ValidatesAfterCommandChanges()
    {
        using var writer = new StringWriter();

        var exitCode = EnhanceMenuDictionary.ValidateEnhanceMenuDictionary(DictionaryPath, "en-US", writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("validation passed", writer.ToString(), StringComparison.OrdinalIgnoreCase);
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
