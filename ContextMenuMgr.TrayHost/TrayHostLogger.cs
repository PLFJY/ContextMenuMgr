using System.IO;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.TrayHost;

/// <summary>
/// Represents the tray Host Logger.
/// </summary>
internal sealed class TrayHostLogger
{
    private readonly RollingLogFile _writer = RuntimeLogFiles.Create(RuntimePaths.TrayHostLogPath);
    private volatile RuntimeLogLevel _currentLevel = RuntimeLogLevel.Warning;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayHostLogger"/> class.
    /// </summary>
    public TrayHostLogger()
    {
        Configure(TryLoadPersistedLogLevel());
    }

    public void Configure(RuntimeLogLevel logLevel)
    {
        _currentLevel = logLevel;
    }

    /// <summary>
    /// Executes log Async.
    /// </summary>
    public async Task LogAsync(string message)
        => await LogAsync(RuntimeLogLevel.Information, message);

    public async Task LogAsync(RuntimeLogLevel level, string message)
    {
        if (level < _currentLevel)
        {
            return;
        }

        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [PID={Environment.ProcessId}] {message}{Environment.NewLine}";
        await _writer.AppendAsync(line).ConfigureAwait(false);
    }

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
