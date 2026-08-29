using System.IO;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the backend Protection Settings Store.
/// </summary>
public sealed class BackendProtectionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _storagePath;
    private readonly FileLogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="BackendProtectionSettingsStore"/> class.
    /// </summary>
    public BackendProtectionSettingsStore(string storagePath, FileLogger? logger = null)
    {
        _storagePath = storagePath;
        _logger = logger;
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Loads async.
    /// </summary>
    public async Task<BackendProtectionSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_storagePath))
            {
                _logger?.LogFireAndForget($"BackendProtectionSettingsStoreLoad: Path={_storagePath}, Exists=false.");
                return new BackendProtectionSettings();
            }

            await using var stream = File.OpenRead(_storagePath);
            var settings = await JsonSerializer.DeserializeAsync<BackendProtectionSettings>(stream, JsonOptions, cancellationToken)
                ?? new BackendProtectionSettings();
            _logger?.LogFireAndForget($"BackendProtectionSettingsStoreLoad: Path={_storagePath}, Exists=true, LockNewContextMenuItems={settings.LockNewContextMenuItems}.");
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Executes save Async.
    /// </summary>
    public async Task SaveAsync(BackendProtectionSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await using var stream = File.Create(_storagePath);
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                _logger?.LogFireAndForget($"BackendProtectionSettingsStoreSave: Path={_storagePath}, LockNewContextMenuItems={settings.LockNewContextMenuItems}, Result=Success.");
            }
            catch (Exception ex)
            {
                _logger?.LogFireAndForget(RuntimeLogLevel.Warning, $"BackendProtectionSettingsStoreSaveFailed: Path={_storagePath}, Exception={ex}");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_storagePath))
            {
                File.Delete(_storagePath);
            }

            _logger?.LogFireAndForget($"BackendProtectionSettingsStoreReset: Path={_storagePath}.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
