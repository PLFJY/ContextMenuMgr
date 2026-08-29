using System.IO;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the file Logger.
/// </summary>
public sealed class FileLogger
{
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _logPath;
    private readonly string _fallbackLogPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private RuntimeLogLevel _currentLevel = RuntimeLogLevel.Warning;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLogger"/> class.
    /// </summary>
    public FileLogger(string logPath)
    {
        _logPath = logPath;
        _fallbackLogPath = Path.Combine(
            RuntimePaths.LogsDirectory,
            "backend-fallback.log");

        EnsureDirectoryExists(_logPath);
        EnsureDirectoryExists(_fallbackLogPath);
        PruneOldLogs(_logPath);
        PruneOldLogs(_fallbackLogPath);
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

    private async Task WriteLineAsync(string message, CancellationToken cancellationToken = default)
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
                try
                {
                    await File.AppendAllTextAsync(_fallbackLogPath, line, cancellationToken);
                }
                catch
                {
                    // 静默处理 - 日志不应该导致功能失败
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

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
    public void LogFireAndForget(string message) => _ = LogAsync(message);

    public void LogFireAndForget(RuntimeLogLevel level, string message) => _ = LogAsync(level, message);

    public void LogFireAndForget(RuntimeLogLevel level, string message, Exception exception)
        => _ = LogAsync(level, $"{message}{Environment.NewLine}{exception}");

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
