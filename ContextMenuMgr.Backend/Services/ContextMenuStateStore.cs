using System.Text.Json;

namespace ContextMenuMgr.Backend.Services;

public sealed class ContextMenuStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _storagePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ContextMenuStateStore(string storagePath)
    {
        _storagePath = storagePath;
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<Dictionary<string, PersistedContextMenuState>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(Dictionary<string, PersistedContextMenuState> states, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(states, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, PersistedContextMenuState>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storagePath))
        {
            return new Dictionary<string, PersistedContextMenuState>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.OpenRead(_storagePath);
        var states = await JsonSerializer.DeserializeAsync<Dictionary<string, PersistedContextMenuState>>(stream, JsonOptions, cancellationToken);
        return states is null
            ? new Dictionary<string, PersistedContextMenuState>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PersistedContextMenuState>(states, StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveCoreAsync(Dictionary<string, PersistedContextMenuState> states, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_storagePath);
        await JsonSerializer.SerializeAsync(stream, states, JsonOptions, cancellationToken);
    }
}
