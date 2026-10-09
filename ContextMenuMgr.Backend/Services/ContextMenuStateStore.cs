using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Stores the locally persisted context-menu metadata. State writes are staged in
/// the same directory, validated using the production reader, then atomically
/// replaced. The previous validated current file is retained as one backup.
/// </summary>
public sealed class ContextMenuStateStore
{
    private const int CurrentStateSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _storagePath;
    private readonly string _backupPath;
    private readonly string _quarantineDirectory;
    private readonly FileLogger? _logger;
    private readonly RuntimeHostIdentityProvider? _hostIdentityProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _isCurrentHostIdentityVerified = RuntimePaths.PackageKind != RuntimePackageKind.Portable;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextMenuStateStore"/> class.
    /// </summary>
    public ContextMenuStateStore(
        string storagePath,
        FileLogger? logger = null,
        RuntimeHostIdentityProvider? hostIdentityProvider = null,
        string? quarantineDirectory = null)
    {
        _storagePath = storagePath;
        _backupPath = storagePath + ".bak";
        _quarantineDirectory = quarantineDirectory ?? RuntimePaths.QuarantineDirectory;
        _logger = logger;
        _hostIdentityProvider = hostIdentityProvider;
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public bool IsCurrentHostIdentityVerified => _isCurrentHostIdentityVerified;

    /// <summary>
    /// Gets the recovery state encountered during this process lifetime.
    /// </summary>
    public ContextMenuStateStoreHealth Health { get; private set; } = ContextMenuStateStoreHealth.Healthy;

    public event EventHandler<string>? PortableHostMismatchDetected;

    /// <summary>
    /// Raised once for each automatic corrupted-state recovery. Consumers can
    /// surface a localized notification without parsing storage exceptions.
    /// </summary>
    public event EventHandler<ContextMenuStateStoreRecovery>? RecoveryOccurred;

    /// <summary>
    /// Loads persisted state, recovering a malformed current file from a
    /// validated backup where possible.
    /// </summary>
    public async Task<Dictionary<string, PersistedContextMenuState>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CleanupStaleTemporaryFiles();
            if (!File.Exists(_storagePath))
            {
                _logger?.LogFireAndForget($"ContextMenuStateStoreLoad: Path={_storagePath}, Exists=false, PersistedStateCount=0.");
                return NewStateDictionary();
            }

            try
            {
                var parsed = await ReadValidatedStateFileAsync(_storagePath, cancellationToken);
                return await LoadParsedStateAsync(parsed, _storagePath, cancellationToken);
            }
            catch (Exception ex) when (IsCorruptedStateException(ex))
            {
                return await RecoverCorruptedCurrentAsync(ex, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves state through a unique, same-directory temporary file. A save is
    /// successful only after the temporary file has been flushed, re-read and
    /// structurally validated, and safely installed as the authoritative file.
    /// </summary>
    public async Task SaveAsync(Dictionary<string, PersistedContextMenuState> states, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CleanupStaleTemporaryFiles();
            await SaveCoreAsync(states, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the authoritative state, last-known-good backup, and stale
    /// temporary generations while holding the same gate used by load/save.
    /// The next complete user-context snapshot will create fresh baselines.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TryDeleteStateFile(_storagePath);
            TryDeleteStateFile(_backupPath);

            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                var searchPattern = Path.GetFileName(_storagePath) + ".tmp-*";
                foreach (var path in Directory.EnumerateFiles(directory, searchPattern))
                {
                    TryDeleteStateFile(path);
                }
            }

            Health = ContextMenuStateStoreHealth.Healthy;
            _logger?.LogFireAndForget($"ContextMenuStateStoreReset: Path={_storagePath}, BackupPath={_backupPath}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDeleteStateFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task<Dictionary<string, PersistedContextMenuState>> RecoverCorruptedCurrentAsync(
        Exception currentException,
        CancellationToken cancellationToken)
    {
        _logger?.LogFireAndForget(
            RuntimeLogLevel.Warning,
            $"ContextMenuStateCorruptionDetected: Path={_storagePath}, Format={DetectFormatForLog()}, ExceptionType={currentException.GetType().Name}, Action=quarantine-and-recover.");

        var currentQuarantinePath = await QuarantineCorruptStateFileAsync(
            _storagePath,
            "current",
            currentException,
            cancellationToken);

        if (!File.Exists(_backupPath))
        {
            return ResetAfterCorruption(currentQuarantinePath, backupQuarantinePath: null, "BackupMissing");
        }

        ParsedState backup;
        try
        {
            backup = await ReadValidatedStateFileAsync(_backupPath, cancellationToken);
        }
        catch (Exception ex) when (IsCorruptedStateException(ex))
        {
            _logger?.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"ContextMenuStateBackupValidationFailed: Path={_backupPath}, ExceptionType={ex.GetType().Name}, Action=quarantine.");
            var backupQuarantinePath = await QuarantineCorruptStateFileAsync(_backupPath, "backup", ex, cancellationToken);
            return ResetAfterCorruption(currentQuarantinePath, backupQuarantinePath, "BackupCorrupt");
        }

        var portableRecovery = await TryHandlePortableRecoverySourceAsync(backup, _backupPath, cancellationToken);
        if (portableRecovery is not null)
        {
            return portableRecovery;
        }

        await RestoreBackupAsCurrentAsync(backup, cancellationToken);
        var states = await LoadParsedStateAsync(backup, _storagePath, cancellationToken);
        ReportRecovery(ContextMenuStateStoreHealth.RecoveredFromBackup, currentQuarantinePath, backupQuarantinePath: null);
        _logger?.LogFireAndForget(
            $"ContextMenuStateRecoveredFromBackup: CurrentPath={_storagePath}, BackupPath={_backupPath}, QuarantinePath={currentQuarantinePath}, PersistedStateCount={states.Count}.");
        return states;
    }

    private async Task<Dictionary<string, PersistedContextMenuState>?> TryHandlePortableRecoverySourceAsync(
        ParsedState parsed,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (RuntimePaths.PackageKind != RuntimePackageKind.Portable || parsed.Envelope is null)
        {
            return null;
        }

        var current = _hostIdentityProvider?.GetCurrent() ?? RuntimeHostIdentity.Untrusted("MissingHostIdentityProvider");
        if (!current.IsTrusted)
        {
            _isCurrentHostIdentityVerified = false;
            _logger?.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"PortableHostIdentityCheck: CurrentFingerprintPrefix=untrusted, StoredFingerprintPrefix={GetFingerprintPrefix(parsed.Envelope.HostIdentity?.Fingerprint)}, Action=reset, Reason=UntrustedCurrentIdentity.");
            return NewStateDictionary();
        }

        if (string.Equals(current.Fingerprint, parsed.Envelope.HostIdentity?.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        _isCurrentHostIdentityVerified = true;
        await QuarantineForeignHostFileAsync(sourcePath, current.FingerprintPrefix, GetFingerprintPrefix(parsed.Envelope.HostIdentity?.Fingerprint), cancellationToken);
        await SaveEnvelopeAsync(NewStateDictionary(), current, cancellationToken);
        PortableHostMismatchDetected?.Invoke(
            this,
            "Portable runtime state was created on another Windows installation or user profile, so ContextMenuMgr started with a fresh local state. The old state was moved to Quarantine.");
        return NewStateDictionary();
    }

    private Dictionary<string, PersistedContextMenuState> ResetAfterCorruption(
        string currentQuarantinePath,
        string? backupQuarantinePath,
        string reason)
    {
        ReportRecovery(ContextMenuStateStoreHealth.ResetAfterCorruption, currentQuarantinePath, backupQuarantinePath);
        _logger?.LogFireAndForget(
            RuntimeLogLevel.Warning,
            $"ContextMenuStateResetAfterCorruption: Path={_storagePath}, Reason={reason}, CurrentQuarantinePath={currentQuarantinePath}, BackupQuarantinePath={backupQuarantinePath ?? "<none>"}, Action=fresh-baseline.");
        return NewStateDictionary();
    }

    private async Task SaveCoreAsync(Dictionary<string, PersistedContextMenuState> states, CancellationToken cancellationToken)
    {
        try
        {
            var identity = _hostIdentityProvider?.GetCurrent();
            if (RuntimePaths.PackageKind == RuntimePackageKind.Portable)
            {
                if (identity?.IsTrusted != true)
                {
                    _isCurrentHostIdentityVerified = false;
                    _logger?.LogFireAndForget(
                        RuntimeLogLevel.Warning,
                        $"ContextMenuStateStoreSaveSkipped: Path={_storagePath}, PackageKind=Portable, Reason=UntrustedHostIdentity, Detail={identity?.FailureReason ?? "MissingHostIdentityProvider"}.");
                    return;
                }

                await SaveEnvelopeAsync(states, identity, cancellationToken);
                _isCurrentHostIdentityVerified = true;
                return;
            }

            if (identity?.IsTrusted == true)
            {
                await SaveEnvelopeAsync(states, identity, cancellationToken);
                return;
            }

            await SavePayloadAtomicallyAsync(states, states.Count, schemaVersion: null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogFireAndForget(RuntimeLogLevel.Warning, $"ContextMenuStateSaveFailed: Path={_storagePath}, PersistedStateCount={states.Count}, ExceptionType={ex.GetType().Name}, Exception={ex}");
            throw;
        }
    }

    private async Task<Dictionary<string, PersistedContextMenuState>> LoadParsedStateAsync(
        ParsedState parsed,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var states = parsed.States;
        if (parsed.Envelope is null)
        {
            return await LoadLegacyDictionaryAsync(states, cancellationToken);
        }

        var storedPrefix = GetFingerprintPrefix(parsed.Envelope.HostIdentity?.Fingerprint);
        if (RuntimePaths.PackageKind != RuntimePackageKind.Portable)
        {
            _isCurrentHostIdentityVerified = true;
            _logger?.LogFireAndForget(
                $"ContextMenuStateStoreLoad: Path={sourcePath}, Exists=true, Format=Envelope, SchemaVersion={parsed.Envelope.SchemaVersion}, StoredFingerprintPrefix={storedPrefix}, Action=loaded, PersistedStateCount={states.Count}.");
            return states;
        }

        var current = _hostIdentityProvider?.GetCurrent() ?? RuntimeHostIdentity.Untrusted("MissingHostIdentityProvider");
        if (!current.IsTrusted)
        {
            _isCurrentHostIdentityVerified = false;
            _logger?.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"PortableHostIdentityCheck: CurrentFingerprintPrefix=untrusted, StoredFingerprintPrefix={storedPrefix}, Action=reset, Reason=UntrustedCurrentIdentity.");
            return NewStateDictionary();
        }

        if (string.Equals(current.Fingerprint, parsed.Envelope.HostIdentity?.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _isCurrentHostIdentityVerified = true;
            _logger?.LogFireAndForget(
                $"PortableHostIdentityCheck: CurrentFingerprintPrefix={current.FingerprintPrefix}, StoredFingerprintPrefix={storedPrefix}, Action=loaded, PersistedStateCount={states.Count}.");
            return states;
        }

        _isCurrentHostIdentityVerified = true;
        await QuarantineForeignHostFileAsync(sourcePath, current.FingerprintPrefix, storedPrefix, cancellationToken);
        await SaveEnvelopeAsync(NewStateDictionary(), current, cancellationToken);
        _logger?.LogFireAndForget(
            RuntimeLogLevel.Warning,
            $"StateStoreHostMismatch: CurrentFingerprintPrefix={current.FingerprintPrefix}, StoredFingerprintPrefix={storedPrefix}, Action=quarantined.");
        PortableHostMismatchDetected?.Invoke(
            this,
            "Portable runtime state was created on another Windows installation or user profile, so ContextMenuMgr started with a fresh local state. The old state was moved to Quarantine.");
        return NewStateDictionary();
    }

    private async Task<Dictionary<string, PersistedContextMenuState>> LoadLegacyDictionaryAsync(
        Dictionary<string, PersistedContextMenuState> states,
        CancellationToken cancellationToken)
    {
        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable)
        {
            var current = _hostIdentityProvider?.GetCurrent() ?? RuntimeHostIdentity.Untrusted("MissingHostIdentityProvider");
            if (!current.IsTrusted)
            {
                _isCurrentHostIdentityVerified = false;
                _logger?.LogFireAndForget(
                    RuntimeLogLevel.Warning,
                    "PortableHostIdentityCheck: CurrentFingerprintPrefix=untrusted, StoredFingerprintPrefix=<legacy>, Action=reset, Reason=LegacyStateUntrustedIdentity.");
                return NewStateDictionary();
            }

            _isCurrentHostIdentityVerified = true;
            await SaveEnvelopeAsync(states, current, cancellationToken);
            _logger?.LogFireAndForget(
                $"PortableHostIdentityCheck: CurrentFingerprintPrefix={current.FingerprintPrefix}, StoredFingerprintPrefix=<legacy>, Action=migrated, PersistedStateCount={states.Count}.");
            return states;
        }

        var installerIdentity = _hostIdentityProvider?.GetCurrent();
        if (installerIdentity?.IsTrusted == true)
        {
            await SaveEnvelopeAsync(states, installerIdentity, cancellationToken);
            _logger?.LogFireAndForget(
                $"ContextMenuStateStoreLoad: Path={_storagePath}, Exists=true, Format=Legacy, Action=migrated, PersistedStateCount={states.Count}.");
        }
        else
        {
            _logger?.LogFireAndForget(
                $"ContextMenuStateStoreLoad: Path={_storagePath}, Exists=true, Format=Legacy, Action=loaded, PersistedStateCount={states.Count}.");
        }

        _isCurrentHostIdentityVerified = true;
        return states;
    }

    private Task SaveEnvelopeAsync(
        Dictionary<string, PersistedContextMenuState> states,
        RuntimeHostIdentity identity,
        CancellationToken cancellationToken)
        => SavePayloadAtomicallyAsync(
            new ContextMenuStateEnvelope
            {
                SchemaVersion = CurrentStateSchemaVersion,
                HostIdentity = HostIdentityEnvelope.FromRuntimeIdentity(identity),
                States = states
            },
            states.Count,
            CurrentStateSchemaVersion,
            cancellationToken);

    private async Task SavePayloadAtomicallyAsync(
        object payload,
        int stateCount,
        int? schemaVersion,
        CancellationToken cancellationToken)
    {
        var temporaryPath = CreateTemporaryPath();
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            _logger?.LogFireAndForget($"ContextMenuStateSaveTempCreated: TempPath={temporaryPath}, PersistedStateCount={stateCount}.");
            _ = await ReadValidatedStateFileAsync(temporaryPath, cancellationToken);
            _logger?.LogFireAndForget($"ContextMenuStateSaveValidated: TempPath={temporaryPath}, SchemaVersion={schemaVersion?.ToString() ?? "Legacy"}, PersistedStateCount={stateCount}.");

            await ReplaceCurrentWithValidatedTemporaryAsync(temporaryPath, cancellationToken);
            _logger?.LogFireAndForget(
                $"ContextMenuStateSaveReplaced: Path={_storagePath}, BackupPath={_backupPath}, SchemaVersion={schemaVersion?.ToString() ?? "Legacy"}, PersistedStateCount={stateCount}.");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task ReplaceCurrentWithValidatedTemporaryAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(_storagePath))
        {
            File.Move(temporaryPath, _storagePath, overwrite: false);
            return;
        }

        try
        {
            _ = await ReadValidatedStateFileAsync(_storagePath, cancellationToken);
        }
        catch (Exception ex) when (IsCorruptedStateException(ex))
        {
            // Never turn an invalid authoritative file into the known-good backup.
            _logger?.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"ContextMenuStateCorruptionDetected: Path={_storagePath}, Format={DetectFormatForLog()}, ExceptionType={ex.GetType().Name}, Action=quarantine-before-save.");
            await QuarantineCorruptStateFileAsync(_storagePath, "save-current", ex, cancellationToken);
            File.Move(temporaryPath, _storagePath, overwrite: false);
            return;
        }

        // File.Replace performs a same-volume replacement and creates/updates the
        // backup from the already validated current file. It avoids a delete-then-
        // rename gap while retaining exactly one last known-good generation.
        File.Replace(temporaryPath, _storagePath, _backupPath, ignoreMetadataErrors: true);
    }

    private async Task RestoreBackupAsCurrentAsync(ParsedState backup, CancellationToken cancellationToken)
    {
        var temporaryPath = CreateTemporaryPath();
        try
        {
            // Re-serialize the parsed production contract so the restored current
            // is flushed and validated just like a normal save. The backup remains
            // untouched as the last known-good generation.
            object payload = backup.Envelope is null ? backup.States : backup.Envelope;
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            _ = await ReadValidatedStateFileAsync(temporaryPath, cancellationToken);
            File.Move(temporaryPath, _storagePath, overwrite: false);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task<ParsedState> ReadValidatedStateFileAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllBytesAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The context-menu state root must be a JSON object.");
        }

        if (document.RootElement.TryGetProperty("schemaVersion", out var schemaVersionElement))
        {
            if (!schemaVersionElement.TryGetInt32(out var schemaVersion))
            {
                throw new InvalidDataException("The context-menu state schema version is invalid.");
            }

            if (schemaVersion > CurrentStateSchemaVersion)
            {
                throw new UnsupportedContextMenuStateSchemaException(schemaVersion);
            }

            if (schemaVersion != CurrentStateSchemaVersion
                || !document.RootElement.TryGetProperty("states", out var statesElement)
                || statesElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The context-menu state envelope is structurally invalid.");
            }

            var envelope = document.RootElement.Deserialize<ContextMenuStateEnvelope>(JsonOptions)
                ?? throw new InvalidDataException("The context-menu state envelope could not be deserialized.");
            if (envelope.States is null)
            {
                throw new InvalidDataException("The context-menu state envelope has no states dictionary.");
            }

            var states = ToCaseInsensitiveDictionary(envelope.States);
            ValidateStates(states);
            return new ParsedState(states, envelope);
        }

        var legacyStates = document.RootElement.Deserialize<Dictionary<string, PersistedContextMenuState>>(JsonOptions)
            ?? throw new InvalidDataException("The legacy context-menu state dictionary could not be deserialized.");
        var normalizedLegacyStates = ToCaseInsensitiveDictionary(legacyStates);
        ValidateStates(normalizedLegacyStates);
        return new ParsedState(normalizedLegacyStates, null);
    }

    private async Task<string> QuarantineCorruptStateFileAsync(
        string sourcePath,
        string reason,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.Combine(
            _quarantineDirectory,
            $"corrupt-state-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{reason}");
        var targetPath = MoveToQuarantine(sourcePath, targetDirectory);
        await (_logger?.LogAsync(
            RuntimeLogLevel.Warning,
            $"ContextMenuStateQuarantined: Source={sourcePath}, Target={targetPath}, Reason={reason}, ExceptionType={exception.GetType().Name}, Format={DetectFormatForLog()}.",
            cancellationToken) ?? Task.CompletedTask);
        return targetPath;
    }

    private async Task QuarantineForeignHostFileAsync(
        string sourcePath,
        string currentPrefix,
        string storedPrefix,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.Combine(
            _quarantineDirectory,
            $"foreign-host-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{storedPrefix}");
        var targetPath = MoveToQuarantine(sourcePath, targetDirectory);
        await (_logger?.LogAsync(
            RuntimeLogLevel.Warning,
            $"StateStoreQuarantined: Source={sourcePath}, Target={targetPath}, CurrentFingerprintPrefix={currentPrefix}, StoredFingerprintPrefix={storedPrefix}.",
            cancellationToken) ?? Task.CompletedTask);
    }

    private string MoveToQuarantine(string sourcePath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var targetPath = GetAvailablePath(Path.Combine(targetDirectory, Path.GetFileName(sourcePath)));
        File.Move(sourcePath, targetPath, overwrite: false);
        return targetPath;
    }

    private void ReportRecovery(ContextMenuStateStoreHealth health, string currentQuarantinePath, string? backupQuarantinePath)
    {
        Health = health;
        RecoveryOccurred?.Invoke(this, new ContextMenuStateStoreRecovery(health, currentQuarantinePath, backupQuarantinePath));
    }

    private string CreateTemporaryPath() => _storagePath + ".tmp-" + Guid.NewGuid().ToString("N");

    private void CleanupStaleTemporaryFiles()
    {
        var directory = Path.GetDirectoryName(_storagePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var searchPattern = Path.GetFileName(_storagePath) + ".tmp-*";
        foreach (var path in Directory.EnumerateFiles(directory, searchPattern))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) <= DateTime.UtcNow.AddHours(-24))
                {
                    File.Delete(path);
                    _logger?.LogFireAndForget($"ContextMenuStateTemporaryCleanup: Path={path}, Action=deleted-stale.");
                }
            }
            catch (IOException)
            {
                // A stale file can be held by another process; it is never treated
                // as authoritative and will be retried on a later operation.
            }
            catch (UnauthorizedAccessException)
            {
                // Do not turn an unrelated ACL problem into a state-store recovery.
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The temporary file is disposable and will be considered for stale cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // The save exception remains the caller-visible failure.
        }
    }

    private static bool IsCorruptedStateException(Exception exception)
        => exception is JsonException or InvalidDataException;

    private static void ValidateStates(Dictionary<string, PersistedContextMenuState> states)
    {
        foreach (var (id, state) in states)
        {
            if (string.IsNullOrWhiteSpace(id) || state is null)
            {
                throw new InvalidDataException("The context-menu state dictionary contains an invalid entry.");
            }
        }
    }

    private static Dictionary<string, PersistedContextMenuState> NewStateDictionary()
        => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, PersistedContextMenuState> ToCaseInsensitiveDictionary(
        Dictionary<string, PersistedContextMenuState>? states)
        => states is null
            ? NewStateDictionary()
            : new Dictionary<string, PersistedContextMenuState>(states, StringComparer.OrdinalIgnoreCase);

    private static string GetFingerprintPrefix(string? fingerprint)
        => string.IsNullOrWhiteSpace(fingerprint)
            ? "<missing>"
            : fingerprint[..Math.Min(12, fingerprint.Length)];

    private static string GetAvailablePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}-{index}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{fileName}-{Guid.NewGuid():N}{extension}");
    }

    private string DetectFormatForLog()
        => File.Exists(_storagePath) ? "UnknownOrInvalid" : "Missing";

    private sealed record ParsedState(
        Dictionary<string, PersistedContextMenuState> States,
        ContextMenuStateEnvelope? Envelope);

    private sealed class ContextMenuStateEnvelope
    {
        public int SchemaVersion { get; set; }

        public HostIdentityEnvelope? HostIdentity { get; set; }

        public Dictionary<string, PersistedContextMenuState>? States { get; set; }
    }

    private sealed class HostIdentityEnvelope
    {
        public string Kind { get; set; } = RuntimeHostIdentity.CurrentKind;

        public string Fingerprint { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int SchemaVersion { get; set; } = RuntimeHostIdentity.CurrentSchemaVersion;

        public static HostIdentityEnvelope FromRuntimeIdentity(RuntimeHostIdentity identity) => new()
        {
            Kind = identity.Kind,
            Fingerprint = identity.Fingerprint ?? string.Empty,
            CreatedAtUtc = identity.CreatedAtUtc,
            SchemaVersion = identity.SchemaVersion
        };
    }
}

public enum ContextMenuStateStoreHealth
{
    Healthy,
    RecoveredFromBackup,
    ResetAfterCorruption
}

public sealed record ContextMenuStateStoreRecovery(
    ContextMenuStateStoreHealth Health,
    string CurrentQuarantinePath,
    string? BackupQuarantinePath);

internal sealed class UnsupportedContextMenuStateSchemaException : InvalidOperationException
{
    public UnsupportedContextMenuStateSchemaException(int schemaVersion)
        : base($"Context-menu state schema version {schemaVersion} is newer than this application supports.")
    {
        SchemaVersion = schemaVersion;
    }

    public int SchemaVersion { get; }
}
