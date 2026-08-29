using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the registry Backup Service.
/// </summary>
public sealed class RegistryBackupService
{
    private const string ForeignBackupRestoreMessage =
        "This backup belongs to a different Windows installation or user profile and cannot be restored safely.";

    private readonly string _backupRootDirectory;
    private readonly RuntimeHostIdentityProvider? _hostIdentityProvider;
    private readonly FileLogger? _logger;
    private readonly string _backupDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="RegistryBackupService"/> class.
    /// </summary>
    public RegistryBackupService(
        string backupDirectory,
        FileLogger? logger = null,
        RuntimeHostIdentityProvider? hostIdentityProvider = null)
    {
        _backupRootDirectory = backupDirectory;
        _logger = logger;
        _hostIdentityProvider = hostIdentityProvider;
        _backupDirectory = ResolveBackupDirectory();
        Directory.CreateDirectory(_backupDirectory);
        QuarantineForeignPortableBackups();
    }

    public string BackupDirectory => _backupDirectory;

    /// <summary>
    /// Executes export Key Async.
    /// </summary>
    public async Task<string> ExportKeyAsync(string registryPath, CancellationToken cancellationToken)
    {
        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable && !IsCurrentHostTrusted(out var identity))
        {
            await LogForeignBackupBlockedAsync(
                "RegistryBackupExportBlockedForeignHost",
                null,
                identity,
                cancellationToken);
            throw new InvalidOperationException(ForeignBackupRestoreMessage);
        }

        Directory.CreateDirectory(_backupDirectory);
        var backupPath = Path.Combine(_backupDirectory, $"{GetSafeFileName(registryPath)}.reg");
        await RunRegAsync(
            action: "RegistryBackupExport",
            arguments: $"export \"{registryPath}\" \"{backupPath}\" /y",
            registryPath: registryPath,
            backupPath: backupPath,
            cancellationToken: cancellationToken);
        return backupPath;
    }

    /// <summary>
    /// Executes restore Backup Async.
    /// </summary>
    public async Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken)
    {
        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable
            && (!IsCurrentHostTrusted(out var identity) || !IsCurrentHostBackupPath(backupFilePath)))
        {
            await LogForeignBackupBlockedAsync(
                "BackupRestoreBlockedForeignHost",
                backupFilePath,
                identity,
                cancellationToken);
            throw new InvalidOperationException(ForeignBackupRestoreMessage);
        }

        await RunRegAsync(
            action: "RegistryBackupImport",
            arguments: $"import \"{backupFilePath}\"",
            registryPath: null,
            backupPath: backupFilePath,
            cancellationToken: cancellationToken);
    }

    public bool IsCurrentHostBackupPath(string? backupFilePath)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath))
        {
            return false;
        }

        try
        {
            var fullBackupPath = Path.GetFullPath(backupFilePath);
            var fullDirectory = Path.GetFullPath(_backupDirectory);
            return fullBackupPath.StartsWith(
                fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes backup File.
    /// </summary>
    public void DeleteBackupFile(string? backupFilePath)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath))
        {
            return;
        }

        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable && !IsCurrentHostBackupPath(backupFilePath))
        {
            _logger?.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"RegistryBackupDeleteSkippedForeignHost: BackupFilePath={backupFilePath}, CurrentBackupDirectory={_backupDirectory}.");
            return;
        }

        var existsBefore = File.Exists(backupFilePath);
        if (existsBefore)
        {
            File.Delete(backupFilePath);
        }

        _logger?.LogFireAndForget($"RegistryBackupDelete: BackupFilePath={backupFilePath}, ExistsBefore={existsBefore}, Result={(existsBefore ? "Deleted" : "Skipped")}.");
    }

    public void ClearCurrentHostBackups()
    {
        Directory.CreateDirectory(_backupDirectory);
        foreach (var path in Directory.EnumerateFiles(_backupDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }

        _logger?.LogFireAndForget($"RegistryBackupDirectoryCleared: Directory={_backupDirectory}.");
    }

    private string ResolveBackupDirectory()
    {
        if (RuntimePaths.PackageKind != RuntimePackageKind.Portable)
        {
            return _backupRootDirectory;
        }

        var identity = _hostIdentityProvider?.GetCurrent();
        if (identity?.IsTrusted == true)
        {
            return RuntimePaths.GetHostScopedDeletedBackupsDirectory(identity.FingerprintPrefix);
        }

        _logger?.LogFireAndForget(
            RuntimeLogLevel.Warning,
            $"DeletedBackupsHostMismatch: CurrentFingerprintPrefix=untrusted, Action=reset, Reason={identity?.FailureReason ?? "MissingHostIdentityProvider"}.");
        return RuntimePaths.GetHostScopedDeletedBackupsDirectory("untrusted");
    }

    private void QuarantineForeignPortableBackups()
    {
        if (RuntimePaths.PackageKind != RuntimePackageKind.Portable || !Directory.Exists(_backupRootDirectory))
        {
            return;
        }

        var identity = _hostIdentityProvider?.GetCurrent();
        if (identity?.IsTrusted != true)
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(_backupRootDirectory, "*.reg", SearchOption.TopDirectoryOnly))
            {
                QuarantineBackupPath(path, identity.FingerprintPrefix);
            }

            foreach (var directory in Directory.EnumerateDirectories(_backupRootDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(_backupDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                QuarantineBackupPath(directory, identity.FingerprintPrefix);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"DeletedBackupsHostMismatch: CurrentFingerprintPrefix={identity.FingerprintPrefix}, Action=quarantine-scan-failed, Exception={ex.Message}.");
        }
    }

    private void QuarantineBackupPath(string path, string currentPrefix)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var targetDirectory = Path.Combine(
            RuntimePaths.QuarantineDirectory,
            $"foreign-host-{timestamp}",
            "DeletedBackups");
        Directory.CreateDirectory(targetDirectory);
        var targetPath = GetAvailablePath(Path.Combine(targetDirectory, Path.GetFileName(path)));
        if (Directory.Exists(path))
        {
            Directory.Move(path, targetPath);
        }
        else if (File.Exists(path))
        {
            File.Move(path, targetPath, overwrite: false);
        }

        _logger?.LogFireAndForget(
            RuntimeLogLevel.Warning,
            $"DeletedBackupsHostMismatch: Source={path}, Target={targetPath}, CurrentFingerprintPrefix={currentPrefix}, Action=quarantined.");
    }

    private bool IsCurrentHostTrusted(out RuntimeHostIdentity? identity)
    {
        identity = _hostIdentityProvider?.GetCurrent();
        return identity?.IsTrusted == true;
    }

    private async Task LogForeignBackupBlockedAsync(
        string action,
        string? backupFilePath,
        RuntimeHostIdentity? identity,
        CancellationToken cancellationToken)
    {
        await (_logger?.LogAsync(
            RuntimeLogLevel.Warning,
            $"{action}: BackupFilePath={backupFilePath ?? "<null>"}, CurrentBackupDirectory={_backupDirectory}, CurrentFingerprintPrefix={identity?.FingerprintPrefix ?? "untrusted"}, Message={ForeignBackupRestoreMessage}",
            cancellationToken) ?? Task.CompletedTask);
    }

    private async Task RunRegAsync(
        string action,
        string arguments,
        string? registryPath,
        string backupPath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
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
        stopwatch.Stop();

        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);

        await (_logger?.LogAsync(
            process.ExitCode == 0 ? RuntimeLogLevel.Information : RuntimeLogLevel.Warning,
            $"{action}: RegistryPath={DiagnosticLogFormatter.FormatRegistryPath(registryPath)}, BackupPath={backupPath}, RegExeArguments={arguments}, ExitCode={process.ExitCode}, ElapsedMs={stopwatch.ElapsedMilliseconds}{(process.ExitCode == 0 ? string.Empty : $", Stdout={DiagnosticLogFormatter.FormatRegistryValueData(standardOutput)}, Stderr={DiagnosticLogFormatter.FormatRegistryValueData(standardError)}")}.",
            cancellationToken) ?? Task.CompletedTask);

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

    private static string GetAvailablePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}-{index}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{fileName}-{Guid.NewGuid():N}{extension}");
    }
}
