using System.Text;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the frontend Debug Log.
/// </summary>
internal static class FrontendDebugLog
{
    private static readonly RollingLogFile Writer = RuntimeLogFiles.Create(RuntimePaths.FrontendDebugLogPath);
    private static volatile AppLogLevel _currentLevel = AppLogLevel.Warning;
    private static volatile bool _sessionStarted;

    /// <summary>
    /// Executes configure.
    /// </summary>
    public static void Configure(AppLogLevel logLevel)
    {
        _currentLevel = logLevel;
    }

    /// <summary>
    /// Gets the log File Path.
    /// </summary>
    public static string LogFilePath { get; } = RuntimePaths.FrontendDebugLogPath;

    /// <summary>
    /// Starts session.
    /// </summary>
    public static void StartSession(string reason)
    {
        Writer.BeginSession(reason);
        _sessionStarted = true;
    }

    public static void EndSession(bool normalShutdown) => Writer.EndSession(normalShutdown);

    public static void RequestOperation(PipeCommand command, string message, bool failed = false)
    {
        if (PipeOperationLogging.ShouldWrite(command, _currentLevel.ToRuntimeLogLevel(), failed))
            Write(AppLogLevel.Information, "FrontendOperation", message, force: true);
    }

    /// <summary>
    /// Executes info.
    /// </summary>
    public static void Info(string source, string message) => Write(AppLogLevel.Information, source, message);

    public static void Operation(string source, string message) => Write(AppLogLevel.Information, source, message, force: true);

    /// <summary>
    /// Executes warning.
    /// </summary>
    public static void Warning(string source, string message) => Write(AppLogLevel.Warning, source, message);

    /// <summary>
    /// Executes error.
    /// </summary>
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

    private static void Write(AppLogLevel level, string source, string message, bool force = false)
    {
        if (!force && level < _currentLevel)
        {
            return;
        }

        if (!_sessionStarted) return;
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] [PID={Environment.ProcessId}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
        if (!Writer.Append(line) && (force || level >= AppLogLevel.Error))
            RuntimeLogFiles.WriteEmergency("frontend-crash.log", line);
    }
}
