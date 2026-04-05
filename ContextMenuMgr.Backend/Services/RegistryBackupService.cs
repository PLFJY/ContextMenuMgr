using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ContextMenuMgr.Backend.Services;

public sealed class RegistryBackupService
{
    private readonly string _backupDirectory;

    public RegistryBackupService(string backupDirectory)
    {
        _backupDirectory = backupDirectory;
        Directory.CreateDirectory(_backupDirectory);
    }

    public async Task<string> ExportKeyAsync(string relativeRegistryPath, CancellationToken cancellationToken)
    {
        var backupPath = Path.Combine(_backupDirectory, $"{GetSafeFileName(relativeRegistryPath)}.reg");
        await RunRegAsync($"export \"HKCR\\{relativeRegistryPath}\" \"{backupPath}\" /y", cancellationToken);
        return backupPath;
    }

    public async Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken)
    {
        await RunRegAsync($"import \"{backupFilePath}\"", cancellationToken);
    }

    public void DeleteBackupFile(string? backupFilePath)
    {
        if (!string.IsNullOrWhiteSpace(backupFilePath) && File.Exists(backupFilePath))
        {
            File.Delete(backupFilePath);
        }
    }

    private static async Task RunRegAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start reg.exe.");

        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
        }
    }

    private static string GetSafeFileName(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }
}
