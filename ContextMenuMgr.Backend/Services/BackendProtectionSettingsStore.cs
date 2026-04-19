using System.IO;
using System.Text.Json;

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
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="BackendProtectionSettingsStore"/> class.
    /// </summary>
    public BackendProtectionSettingsStore(string storagePath)
    {
        _storagePath = storagePath;
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
                return new BackendProtectionSettings();
            }

            await using var stream = File.OpenRead(_storagePath);
            return await JsonSerializer.DeserializeAsync<BackendProtectionSettings>(stream, JsonOptions, cancellationToken)
                ?? new BackendProtectionSettings();
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
            await using var stream = File.Create(_storagePath);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
