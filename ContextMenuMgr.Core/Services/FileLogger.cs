using System.IO;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the file Logger.
/// </summary>
public sealed class FileLogger
{
    private readonly RollingLogFile _writer;
    private volatile RuntimeLogLevel _currentLevel = RuntimeLogLevel.Warning;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLogger"/> class.
    /// </summary>
    public FileLogger(string logPath)
    {
        _writer = RuntimeLogFiles.Create(logPath);
        RuntimeLogFiles.RecoverEmergency("backend-fallback.log");
        Configure(TryLoadPersistedLogLevel());
    }

    public RuntimeLogLevel CurrentLevel => _currentLevel;

    public void Configure(RuntimeLogLevel logLevel)
    {
        _currentLevel = logLevel;
    }

    /// <summary>
    /// Executes log Async.
    /// </summary>
    public async Task LogAsync(string message, CancellationToken cancellationToken = default)
        => await LogAsync(RuntimeLogLevel.Information, message, cancellationToken);

    public async Task LogAsync(RuntimeLogLevel level, string message, CancellationToken cancellationToken = default)
    {
        if (level < _currentLevel)
        {
            return;
        }

        await WriteLineAsync(message, cancellationToken);
    }

    public Task LogOperationAsync(string message, CancellationToken cancellationToken = default)
        => WriteLineAsync(message, cancellationToken);

    public Task LogRequestOperationAsync(PipeCommand command, string message, bool failed = false,
        CancellationToken cancellationToken = default)
        => PipeOperationLogging.ShouldWrite(command, _currentLevel, failed)
            ? WriteLineAsync(message, cancellationToken) : Task.CompletedTask;

    private async Task WriteLineAsync(string message, CancellationToken cancellationToken = default)
    {
        var line = FormatLine(message);
        if (!await _writer.AppendAsync(line, cancellationToken).ConfigureAwait(false))
            RuntimeLogFiles.WriteEmergency("backend-fallback.log", line);
    }

    private static string FormatLine(string message)
        => $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [PID={Environment.ProcessId}] {message}{Environment.NewLine}";

    public Task LogInformationAsync(string message, CancellationToken cancellationToken = default)
        => LogAsync(RuntimeLogLevel.Information, message, cancellationToken);

    public Task LogWarningAsync(string message, CancellationToken cancellationToken = default)
        => LogAsync(RuntimeLogLevel.Warning, message, cancellationToken);

    public Task LogErrorAsync(string message, CancellationToken cancellationToken = default)
        => LogAsync(RuntimeLogLevel.Error, message, cancellationToken);

    public Task LogErrorAsync(string message, Exception exception, CancellationToken cancellationToken = default)
        => LogAsync(RuntimeLogLevel.Error, $"{message}{Environment.NewLine}{exception}", cancellationToken);

    /// <summary>
    /// Executes log Fire And Forget.
    /// </summary>
    public void LogFireAndForget(string message) => LogFireAndForget(RuntimeLogLevel.Information, message);

    public void LogFireAndForget(RuntimeLogLevel level, string message)
    {
        // Existing non-awaited callers use bounded synchronous best effort, never accumulating Tasks.
        if (level < _currentLevel) return;
        var line = FormatLine(message);
        if (!_writer.Append(line)) RuntimeLogFiles.WriteEmergency("backend-fallback.log", line);
    }

    public void LogFireAndForget(RuntimeLogLevel level, string message, Exception exception)
        => LogFireAndForget(level, $"{message}{Environment.NewLine}{exception}");

    private static RuntimeLogLevel TryLoadPersistedLogLevel()
    {
        try
        {
            if (!File.Exists(RuntimePaths.SettingsPath))
            {
                return RuntimeLogLevel.Warning;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(RuntimePaths.SettingsPath));
            if (!document.RootElement.TryGetProperty("logLevel", out var property))
            {
                return RuntimeLogLevel.Warning;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt32(out var value) && Enum.IsDefined(typeof(RuntimeLogLevel), value)
                    => (RuntimeLogLevel)value,
                JsonValueKind.String when Enum.TryParse<RuntimeLogLevel>(property.GetString(), ignoreCase: true, out var level)
                    => level,
                _ => RuntimeLogLevel.Warning
            };
        }
        catch
        {
            return RuntimeLogLevel.Warning;
        }
    }

}
