using System.Diagnostics;
using System.IO;
using System.Text;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal static class FrontendDebugLog
{
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(14);
    private static readonly Lock SyncRoot = new();
    private static AppLogLevel _currentLevel = AppLogLevel.Warning;
    private static bool _retentionChecked;

    public static void Configure(AppLogLevel logLevel)
    {
        _currentLevel = logLevel;
        EnsureRetention();
    }

    public static string LogFilePath { get; } = RuntimePaths.FrontendDebugLogPath;

    public static void StartSession(string reason)
    {
        Write(AppLogLevel.Information, "SESSION", $"========== {reason} | PID={Environment.ProcessId} ==========");
    }

    public static void Info(string source, string message) => Write(AppLogLevel.Information, source, message);

    public static void Warning(string source, string message) => Write(AppLogLevel.Warning, source, message);

    public static void Error(string source, Exception exception, string? context = null)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.Append(context).AppendLine();
        }

        builder.Append(exception);
        Write(AppLogLevel.Error, source, builder.ToString());
    }

    private static void Write(AppLogLevel level, string source, string message)
    {
        if (level < _currentLevel)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static void EnsureRetention()
    {
        if (_retentionChecked)
        {
            return;
        }

        _retentionChecked = true;

        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
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
