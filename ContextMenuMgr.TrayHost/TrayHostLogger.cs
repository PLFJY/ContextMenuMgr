using System.IO;
using System.Text;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.TrayHost;

internal sealed class TrayHostLogger
{
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);
    private readonly string _logFilePath = RuntimePaths.TrayHostLogPath;

    public TrayHostLogger()
    {
        PruneOldLogs();
    }

    public async Task LogAsync(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(_logFilePath, line, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
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
