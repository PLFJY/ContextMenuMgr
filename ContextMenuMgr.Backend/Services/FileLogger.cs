using System.IO;

namespace ContextMenuMgr.Backend.Services;

public sealed class FileLogger
{
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);
    private readonly string _logPath;
    private readonly string _fallbackLogPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileLogger(string logPath)
    {
        _logPath = logPath;
        _fallbackLogPath = Path.Combine(Path.GetTempPath(), "ContextMenuMgr", "backend.log");

        EnsureDirectoryExists(_logPath);
        EnsureDirectoryExists(_fallbackLogPath);
        PruneOldLogs(_logPath);
        PruneOldLogs(_fallbackLogPath);
    }

    public async Task LogAsync(string message, CancellationToken cancellationToken = default)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await File.AppendAllTextAsync(_logPath, line, cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                await File.AppendAllTextAsync(_fallbackLogPath, line, cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void LogFireAndForget(string message) => _ = LogAsync(message);

    private static void EnsureDirectoryExists(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void PruneOldLogs(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTimeOffset.Now.Subtract(LogRetention);
            foreach (var file in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var lastWriteTime = File.GetLastWriteTimeUtc(file);
                    if (lastWriteTime < cutoff.UtcDateTime)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
