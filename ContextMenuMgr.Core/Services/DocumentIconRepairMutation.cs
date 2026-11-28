using Microsoft.Win32;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>Removes WPS overrides of Office icons and conditionally restores exact originals.</summary>
internal sealed class DocumentIconRepairMutation
{
    private readonly List<RegistryValueMutationTransaction> _mutations = [];
    private readonly List<(string ProgId, PersistedRegistryValueSnapshot Value)> _targets = [];
    private static readonly HashSet<string> AllowedProgIds = OfficeSuiteCoexistenceDetector.Catalog
        .SelectMany(spec => spec.MicrosoftProgIds).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static List<PersistedDocumentIconBackup> Capture(RegistryKey userClasses, RegistryKey machineClasses)
    {
        var backups = new List<PersistedDocumentIconBackup>();
        foreach (var progId in AllowedProgIds)
        {
            using var key = userClasses.OpenSubKey($@"{progId}\DefaultIcon");
            using var machine = machineClasses.OpenSubKey($@"{progId}\DefaultIcon");
            var original = key is null ? new PersistedRegistryValueSnapshot { Name = string.Empty }
                : ShellVerbVisibility.CaptureValue(key, string.Empty);
            var machineIcon = machine?.GetValue(null)?.ToString();
            if (!OfficeSuiteCoexistenceDetector.IsWpsPath(original.Existed ? original.StringValue : machineIcon)) continue;
            if (!OfficeSuiteCoexistenceDetector.IsWpsPath(original.StringValue)
                || !OfficeSuiteCoexistenceDetector.IsMicrosoftOfficePath(machineIcon))
                throw new DocumentIconRepairException(PipeErrorCodes.DocumentIconRepairSourceUnavailable,
                    $"The original Microsoft Office icon is unavailable. ProgId={progId}. Repair Office before changing document icons.");
            backups.Add(new() { ProgId = progId, Original = original });
        }
        return backups;
    }

    public static bool IsApplied(RegistryKey userClasses, RegistryKey machineClasses, IReadOnlyList<PersistedDocumentIconBackup> backups)
        => backups.All(backup => Read(userClasses, backup.ProgId).Existed == false)
           && IsHealthy(userClasses, machineClasses);

    public static bool IsHealthy(RegistryKey userClasses, RegistryKey machineClasses)
    {
        var available = false;
        foreach (var progId in AllowedProgIds)
        {
            using var machine = machineClasses.OpenSubKey($@"{progId}\DefaultIcon");
            using var user = userClasses.OpenSubKey($@"{progId}\DefaultIcon");
            var machineIcon = machine?.GetValue(null)?.ToString();
            var userIcon = user?.GetValue(null)?.ToString();
            var effectiveIcon = userIcon ?? machineIcon;
            if (OfficeSuiteCoexistenceDetector.IsWpsPath(effectiveIcon)) return false;
            available |= OfficeSuiteCoexistenceDetector.IsMicrosoftOfficePath(effectiveIcon);
        }
        return available;
    }

    public DocumentIconRepairMutation(RegistryKey userClasses, RegistryKey machineClasses,
        IReadOnlyList<PersistedDocumentIconBackup> backups, bool enable)
    {
        // Validate all values before creating keys or changing the first value.
        var plans = new List<(string ProgId, PersistedRegistryValueSnapshot Before, PersistedRegistryValueSnapshot After)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backup in backups)
        {
            if (!AllowedProgIds.Contains(backup.ProgId) || !seen.Add(backup.ProgId)
                || backup.Original.Name != string.Empty || !backup.Original.Existed
                || backup.Original.Kind is not ((int)RegistryValueKind.String or (int)RegistryValueKind.ExpandString)
                || !OfficeSuiteCoexistenceDetector.IsWpsPath(backup.Original.StringValue))
                throw new InvalidDataException("The document icon recovery record is invalid.");
            using var machine = machineClasses.OpenSubKey($@"{backup.ProgId}\DefaultIcon");
            if (!OfficeSuiteCoexistenceDetector.IsMicrosoftOfficePath(machine?.GetValue(null)?.ToString()))
                throw new DocumentIconRepairException(PipeErrorCodes.DocumentIconRepairSourceUnavailable,
                    "The Office document registration changed. Refresh before restoring its icons.");
            var repaired = new PersistedRegistryValueSnapshot { Name = string.Empty };
            var desired = enable ? repaired : backup.Original;
            var other = enable ? backup.Original : repaired;
            using var key = userClasses.OpenSubKey($@"{backup.ProgId}\DefaultIcon");
            var current = key is null ? repaired : ShellVerbVisibility.CaptureValue(key, string.Empty);
            if (!Matches(key, desired) && !Matches(key, other))
                throw new DocumentIconRepairException(PipeErrorCodes.DocumentIconRepairConflict,
                    "A document icon was changed by another application. Its newer setting was preserved.");
            _targets.Add((backup.ProgId, desired));
            if (!Matches(key, desired)) plans.Add((backup.ProgId, current, desired));
        }
        foreach (var plan in plans)
        {
            using var key = userClasses.CreateSubKey($@"{plan.ProgId}\DefaultIcon", writable: true)
                ?? throw new IOException("Unable to open the document icon setting.");
            var view = userClasses.View == RegistryView.Default
                ? Environment.Is64BitProcess ? RegistryView.Registry64 : RegistryView.Registry32
                : userClasses.View;
            _mutations.Add(RegistryValueMutationTransaction.Create(key, key.Name, plan.After, plan.Before, view));
        }
    }

    public void Apply(RegistryKey userClasses)
    {
        foreach (var mutation in _mutations)
        {
            using var key = mutation.OpenKey(true) ?? throw new IOException("The document icon setting disappeared.");
            try { mutation.Apply(key); }
            catch (InvalidOperationException ex)
            {
                throw new DocumentIconRepairException(PipeErrorCodes.DocumentIconRepairConflict,
                    $"The document icon changed after planning. {ex.Message}");
            }
        }
        Verify(userClasses);
    }

    public void Verify(RegistryKey userClasses)
    {
        foreach (var target in _targets)
        {
            using var key = userClasses.OpenSubKey($@"{target.ProgId}\DefaultIcon");
            if (!Matches(key, target.Value)) throw new IOException("The document icon change could not be confirmed.");
        }
    }

    public bool TryRollback(FileLogger? logger)
    {
        var success = true;
        foreach (var mutation in _mutations.AsEnumerable().Reverse())
        {
            if (!mutation.Applied) continue;
            try
            {
                using var key = mutation.OpenKey(true);
                var result = key is null ? null : mutation.TryRollback(key);
                using var fresh = mutation.OpenKey(false);
                if (result?.Succeeded == true && fresh is not null && mutation.VerifyRolledBack(fresh)) continue;
                success = false;
                logger?.LogFireAndForget($"WpsDocumentIconRollbackFailed: Path={mutation.PhysicalRegistryPath}, Error={result?.Error ?? "Key unavailable"}.");
            }
            catch (Exception ex)
            {
                success = false;
                logger?.LogFireAndForget($"WpsDocumentIconRollbackFailed: Path={mutation.PhysicalRegistryPath}, Error={ex.Message}.");
            }
        }
        return success;
    }

    private static PersistedRegistryValueSnapshot Read(RegistryKey classes, string progId)
    {
        if (!AllowedProgIds.Contains(progId)) throw new InvalidDataException("Invalid document recovery target.");
        using var key = classes.OpenSubKey($@"{progId}\DefaultIcon");
        return key is null ? new() { Name = string.Empty } : ShellVerbVisibility.CaptureValue(key, string.Empty);
    }

    private static bool Matches(RegistryKey? key, PersistedRegistryValueSnapshot value)
        => key is null ? !value.Existed : ShellVerbVisibility.ValueMatches(key, value);
}

internal sealed class DocumentIconRepairException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
