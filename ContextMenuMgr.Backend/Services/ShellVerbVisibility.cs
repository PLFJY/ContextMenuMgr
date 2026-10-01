using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Models Explorer context-menu visibility independently from Shell verb
/// availability and default activation.
/// </summary>
internal static class ShellVerbVisibility
{
    internal const string ManagedMarkerName = "ProgrammaticAccessOnly";
    private const int HideBasedOnVelocityIdDisabledValue = 0x639bc8;

    public static bool IsOpenInNewWindowVerb(string registryPath)
    {
        var normalized = registryPath.Replace('/', '\\').TrimEnd('\\');
        return normalized.EndsWith(@"\Folder\shell\opennewwindow", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, @"Folder\shell\opennewwindow", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEnabled(RegistryKey itemKey) => GetState(itemKey).ContextMenuVisible;

    public static ShellVerbVisibilityState GetState(RegistryKey itemKey)
    {
        var hiddenByVelocity = TryGetInt32(itemKey.GetValue("HideBasedOnVelocityId"), out var velocityId)
                               && velocityId == HideBasedOnVelocityIdDisabledValue;
        var hiddenByLegacyDisable = itemKey.GetValue("LegacyDisable") is not null;
        var programmaticAccessOnly = itemKey.GetValue(ManagedMarkerName) is not null;

        return new ShellVerbVisibilityState(
            ContextMenuVisible: !hiddenByVelocity && !hiddenByLegacyDisable && !programmaticAccessOnly,
            ProgrammaticallyInvocable: programmaticAccessOnly ? true : null,
            HiddenByProgrammaticAccessOnly: programmaticAccessOnly,
            HiddenByLegacyDisable: hiddenByLegacyDisable,
            HiddenByVelocityPolicy: hiddenByVelocity);
    }

    /// <summary>
    /// Compatibility writer for control domains without classic state-store
    /// provenance. Classic mutations use ShellVerbVisibilityTransaction.
    /// CommandFlags and unrelated visibility values are never touched.
    /// </summary>
    public static void SetEnabled(RegistryKey menuKey, string registryPath, bool enable)
    {
        _ = registryPath;
        if (enable)
        {
            menuKey.DeleteValue(ManagedMarkerName, throwOnMissingValue: false);
        }
        else
        {
            menuKey.SetValue(ManagedMarkerName, string.Empty, RegistryValueKind.String);
        }
    }

    public static PersistedRegistryValueSnapshot CaptureValue(RegistryKey key, string valueName)
    {
        if (!key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return new PersistedRegistryValueSnapshot { Name = valueName, Existed = false };
        }

        var kind = key.GetValueKind(valueName);
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var snapshot = new PersistedRegistryValueSnapshot
        {
            Name = valueName,
            Existed = true,
            Kind = (int)kind
        };

        switch (kind)
        {
            case RegistryValueKind.DWord:
            case RegistryValueKind.QWord:
                snapshot.IntegerValue = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                break;
            case RegistryValueKind.Binary:
            case RegistryValueKind.None:
                snapshot.BinaryBase64 = Convert.ToBase64String((byte[]?)value ?? []);
                break;
            case RegistryValueKind.MultiString:
                snapshot.StringArrayValue = (string[]?)value ?? [];
                break;
            default:
                snapshot.StringValue = value?.ToString() ?? string.Empty;
                break;
        }

        return snapshot;
    }

    public static void RestoreValue(RegistryKey key, PersistedRegistryValueSnapshot snapshot)
    {
        if (!snapshot.Existed)
        {
            key.DeleteValue(snapshot.Name, throwOnMissingValue: false);
            return;
        }

        var kind = (RegistryValueKind)snapshot.Kind;
        object value = kind switch
        {
            RegistryValueKind.DWord => checked((int)(snapshot.IntegerValue ?? 0)),
            RegistryValueKind.QWord => snapshot.IntegerValue ?? 0L,
            RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(snapshot.BinaryBase64 ?? string.Empty),
            RegistryValueKind.MultiString => snapshot.StringArrayValue ?? [],
            _ => snapshot.StringValue ?? string.Empty
        };
        key.SetValue(snapshot.Name, value, kind);
    }

    public static bool ValueMatches(RegistryKey key, PersistedRegistryValueSnapshot expected)
        => SnapshotsEqual(CaptureValue(key, expected.Name), expected);

    public static bool SnapshotsEqual(PersistedRegistryValueSnapshot left, PersistedRegistryValueSnapshot right)
        => string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
           && left.Existed == right.Existed
           && (!left.Existed
               || left.Kind == right.Kind
               && left.IntegerValue == right.IntegerValue
               && string.Equals(left.StringValue, right.StringValue, StringComparison.Ordinal)
               && string.Equals(left.BinaryBase64, right.BinaryBase64, StringComparison.Ordinal)
               && (left.StringArrayValue ?? []).SequenceEqual(right.StringArrayValue ?? [], StringComparer.Ordinal));

    /// <summary>
    /// Fingerprints physical registration identity without visibility values.
    /// </summary>
    public static string ComputeGenerationFingerprint(RegistryKey itemKey)
    {
        var material = new StringBuilder();
        AppendValue(material, itemKey, null);
        AppendValue(material, itemKey, "DelegateExecute");
        AppendValue(material, itemKey, "ExplorerCommandHandler");
        using var command = itemKey.OpenSubKey("command", writable: false);
        if (command is not null)
        {
            AppendValue(material, command, null);
            AppendValue(material, command, "DelegateExecute");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    private static void AppendValue(StringBuilder builder, RegistryKey key, string? name)
    {
        var valueName = name ?? string.Empty;
        var exists = key.GetValueNames().Any(candidate => string.Equals(candidate, valueName, StringComparison.OrdinalIgnoreCase));
        builder.Append(name ?? "(Default)").Append('=').Append(exists ? '1' : '0');
        if (exists)
        {
            var snapshot = CaptureValue(key, valueName);
            builder.Append(':').Append(snapshot.Kind).Append(':')
                .Append(snapshot.StringValue).Append(':').Append(snapshot.IntegerValue).Append(':')
                .Append(snapshot.BinaryBase64).Append(':')
                .AppendJoin('\u001f', snapshot.StringArrayValue ?? []);
        }
        builder.Append('\u001e');
    }

    private static bool TryGetInt32(object? value, out int result)
    {
        try
        {
            if (value is null)
            {
                result = 0;
                return false;
            }

            result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }
}

internal sealed record ShellVerbVisibilityState(
    bool ContextMenuVisible,
    bool? ProgrammaticallyInvocable,
    bool HiddenByProgrammaticAccessOnly,
    bool HiddenByLegacyDisable,
    bool HiddenByVelocityPolicy);

internal interface IShellVerbVisibilityMutation
{
    string PhysicalRegistryPath { get; }
    bool RequestedVisible { get; }
    PersistedShellVerbVisibilityProvenance? Provenance { get; }
    bool Applied { get; }
    void Apply(RegistryKey key);
    bool Verify(RegistryKey key);
    ShellVerbRollbackResult TryRollback(RegistryKey key);
    bool VerifyRolledBack(RegistryKey key);
}

internal sealed class ShellVerbVisibilityTransaction : IShellVerbVisibilityMutation
{
    private readonly PersistedRegistryValueSnapshot _before;
    private readonly PersistedRegistryValueSnapshot _written;

    private ShellVerbVisibilityTransaction(
        string physicalRegistryPath,
        bool requestedVisible,
        PersistedShellVerbVisibilityProvenance? provenance,
        PersistedRegistryValueSnapshot before,
        PersistedRegistryValueSnapshot written,
        string generationFingerprint)
    {
        PhysicalRegistryPath = physicalRegistryPath;
        RequestedVisible = requestedVisible;
        Provenance = provenance;
        _before = before;
        _written = written;
        GenerationFingerprint = generationFingerprint;
    }

    public string PhysicalRegistryPath { get; }
    public bool RequestedVisible { get; }
    public string GenerationFingerprint { get; }
    public PersistedShellVerbVisibilityProvenance? Provenance { get; }
    public bool Applied { get; private set; }

    public static ShellVerbVisibilityTransaction Create(
        RegistryKey key,
        string physicalRegistryPath,
        bool requestedVisible,
        PersistedShellVerbVisibilityProvenance? existingProvenance)
    {
        var before = ShellVerbVisibility.CaptureValue(key, ShellVerbVisibility.ManagedMarkerName);
        var fingerprint = ShellVerbVisibility.ComputeGenerationFingerprint(key);
        if (existingProvenance is not null
            && !string.Equals(existingProvenance.GenerationFingerprint, fingerprint, StringComparison.Ordinal))
        {
            if (requestedVisible || !ShellVerbVisibility.GetState(key).ContextMenuVisible)
            {
                throw new ShellVerbMutationException(
                    "SHELL_VERB_PHYSICAL_GENERATION_CONFLICT",
                    "The Shell verb registration was recreated or materially changed; its old visibility recovery data was not applied.");
            }

            // A third party recreated the same logical verb as a visible new
            // physical generation. Its old restore data must never be applied
            // to this generation; capture a fresh before value for re-disable.
            existingProvenance = null;
        }

        var provenance = existingProvenance;
        PersistedRegistryValueSnapshot written;
        if (!requestedVisible)
        {
            if (existingProvenance is not null)
            {
                var original = existingProvenance.OriginalValues.Single(value =>
                    string.Equals(value.Name, ShellVerbVisibility.ManagedMarkerName, StringComparison.OrdinalIgnoreCase));
                var managedWrite = CreateManagedHiddenMarker();
                if (!ShellVerbVisibility.ValueMatches(key, managedWrite)
                    && !ShellVerbVisibility.ValueMatches(key, original))
                {
                    if (!ShellVerbVisibility.GetState(key).ContextMenuVisible)
                    {
                        throw new ShellVerbMutationException(
                            "SHELL_VERB_VISIBILITY_CONCURRENT_CHANGE",
                            "The Shell verb visibility value was changed by another process; it was not overwritten.");
                    }

                    // A visible value is no longer the hidden registration that
                    // the old provenance described. Re-disable it with fresh
                    // restore data, preserving the new owner's current value.
                    existingProvenance = null;
                    provenance = null;
                }
            }

            provenance ??= new PersistedShellVerbVisibilityProvenance
            {
                PhysicalRegistryPath = physicalRegistryPath,
                GenerationFingerprint = fingerprint,
                OriginalValues = [Clone(before)]
            };
            written = ShellVerbVisibility.GetState(key).ContextMenuVisible
                ? CreateManagedHiddenMarker()
                : Clone(before);
        }
        else if (existingProvenance is null)
        {
            if (!ShellVerbVisibility.GetState(key).ContextMenuVisible)
            {
                throw new ShellVerbMutationException(
                    PipeErrorCodes.ShellVerbVisibilityProvenanceMissing,
                    "This Shell verb is hidden by registry metadata that ContextMenuMgrPlus did not create, so it cannot be removed safely.");
            }
            written = Clone(before);
        }
        else
        {
            var original = existingProvenance.OriginalValues.Single(value =>
                string.Equals(value.Name, ShellVerbVisibility.ManagedMarkerName, StringComparison.OrdinalIgnoreCase));
            var managedWrite = CreateManagedHiddenMarker();
            if (!ShellVerbVisibility.ValueMatches(key, managedWrite)
                && !ShellVerbVisibility.ValueMatches(key, original))
            {
                throw new ShellVerbMutationException(
                    "SHELL_VERB_VISIBILITY_CONCURRENT_CHANGE",
                    "The Shell verb visibility value was changed by another process; it was not overwritten.");
            }
            written = Clone(original);
        }

        return new ShellVerbVisibilityTransaction(
            physicalRegistryPath,
            requestedVisible,
            provenance,
            before,
            written,
            fingerprint);
    }

    public void Apply(RegistryKey key)
    {
        ShellVerbVisibility.RestoreValue(key, _written);
        Applied = true;
    }

    public bool Verify(RegistryKey key)
        => ShellVerbVisibility.ValueMatches(key, _written)
           && ShellVerbVisibility.GetState(key).ContextMenuVisible == RequestedVisible;

    public ShellVerbRollbackResult TryRollback(RegistryKey key)
    {
        if (!Applied)
        {
            return new ShellVerbRollbackResult(false, true, false, null);
        }
        if (!ShellVerbVisibility.ValueMatches(key, _written))
        {
            return new ShellVerbRollbackResult(true, false, true, "The managed visibility value was changed by another process.");
        }

        ShellVerbVisibility.RestoreValue(key, _before);
        var succeeded = ShellVerbVisibility.ValueMatches(key, _before);
        return new ShellVerbRollbackResult(true, succeeded, false, succeeded ? null : "Rollback read-back did not match the captured value.");
    }

    public bool VerifyRolledBack(RegistryKey key)
        => ShellVerbVisibility.ValueMatches(key, _before);

    private static PersistedRegistryValueSnapshot Clone(PersistedRegistryValueSnapshot value)
        => new()
        {
            Name = value.Name,
            Existed = value.Existed,
            Kind = value.Kind,
            StringValue = value.StringValue,
            IntegerValue = value.IntegerValue,
            BinaryBase64 = value.BinaryBase64,
            StringArrayValue = value.StringArrayValue?.ToArray()
        };

    private static PersistedRegistryValueSnapshot CreateManagedHiddenMarker()
        => new()
        {
            Name = ShellVerbVisibility.ManagedMarkerName,
            Existed = true,
            Kind = (int)RegistryValueKind.String,
            StringValue = string.Empty
        };
}

/// <summary>
/// One-time bridge for the exact destructive hide representation written by
/// v1.7.5. It never manufactures modern provenance for unknown original values.
/// </summary>
internal sealed class LegacyShellVerbVisibilityRecoveryTransaction : IShellVerbVisibilityMutation, IDisposable
{
    private static readonly string[] VisibilityNames =
        ["HideBasedOnVelocityId", "ProgrammaticAccessOnly", "LegacyDisable", "ShowAsDisabledIfHidden", "CommandFlags"];
    private readonly PersistedRegistryValueSnapshot[] _before;
    private readonly PersistedRegistryValueSnapshot[] _written;
    private readonly PersistedRegistryValueSnapshot[] _observedVisibility;
    private readonly string _generationFingerprint;
    private readonly DateTimeOffset _plannedWriteTime;
    private readonly RegistryKey _planningKey;
    private DateTimeOffset _writtenWriteTime;
    private int _appliedCount;

    private LegacyShellVerbVisibilityRecoveryTransaction(
        string physicalRegistryPath,
        PersistedRegistryValueSnapshot[] before,
        PersistedRegistryValueSnapshot[] written,
        PersistedRegistryValueSnapshot[] observedVisibility,
        string generationFingerprint,
        DateTimeOffset plannedWriteTime,
        RegistryKey planningKey)
    {
        PhysicalRegistryPath = physicalRegistryPath;
        _before = before;
        _written = written;
        _observedVisibility = observedVisibility;
        _generationFingerprint = generationFingerprint;
        _plannedWriteTime = plannedWriteTime;
        _planningKey = planningKey;
    }

    public string PhysicalRegistryPath { get; }
    public bool RequestedVisible => true;
    public PersistedShellVerbVisibilityProvenance? Provenance => null;
    public bool Applied => _appliedCount > 0;

    public static bool TryCreate(
        RegistryKey key,
        string physicalRegistryPath,
        PersistedContextMenuState? state,
        string itemId,
        out LegacyShellVerbVisibilityRecoveryTransaction? transaction,
        out string reason)
    {
        transaction = null;
        if (state is null || !string.Equals(state.Id, itemId, StringComparison.OrdinalIgnoreCase)
            || state.EntryKind != ContextMenuEntryKind.ShellVerb || state.IsWindows11ContextMenu
            || state.IsDeleted || state.DesiredEnabled != false || state.ObservedEnabled != false
            || !string.Equals(state.BackendRegistryPath, physicalRegistryPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(key.Name, physicalRegistryPath, StringComparison.OrdinalIgnoreCase)
            || state.ShellVerbVisibilityProvenance?.Any(p => string.Equals(
                p.PhysicalRegistryPath, physicalRegistryPath, StringComparison.OrdinalIgnoreCase)) == true)
        {
            reason = "PersistedPhysicalOwnershipMissing";
            return false;
        }

        var values = VisibilityNames.ToDictionary(name => name, name => ShellVerbVisibility.CaptureValue(key, name), StringComparer.OrdinalIgnoreCase);
        static bool EmptyString(PersistedRegistryValueSnapshot value) =>
            value.Existed && value.Kind == (int)RegistryValueKind.String && value.StringValue == string.Empty;
        static bool Missing(PersistedRegistryValueSnapshot value) => !value.Existed;
        var velocity = values["HideBasedOnVelocityId"];
        if (!velocity.Existed || velocity.Kind != (int)RegistryValueKind.DWord || velocity.IntegerValue != 0x639bc8)
        {
            reason = "VelocityMarkerMismatch";
            return false;
        }

        var flags = values["CommandFlags"];
        if (flags.Existed && (flags.Kind != (int)RegistryValueKind.DWord || ((flags.IntegerValue ?? 0) & 8) != 0))
        {
            reason = "CommandFlagsAmbiguous";
            return false;
        }

        var showDisabled = values["ShowAsDisabledIfHidden"];
        var openNewWindow = ShellVerbVisibility.IsOpenInNewWindowVerb(physicalRegistryPath);
        if (showDisabled.Existed)
        {
            if (!EmptyString(showDisabled) || !Missing(values["ProgrammaticAccessOnly"])
                || !Missing(values["LegacyDisable"]))
            {
                reason = "ShowAsDisabledSignatureMismatch";
                return false;
            }
        }
        else if (!EmptyString(values["ProgrammaticAccessOnly"])
                 || (openNewWindow ? !Missing(values["LegacyDisable"]) : !EmptyString(values["LegacyDisable"])))
        {
            reason = openNewWindow ? "OpenNewWindowSignatureMismatch" : "OrdinarySignatureMismatch";
            return false;
        }

        if (!ContextMenuRegistryCatalog.TryGetRegistryWriteTimeUtc(physicalRegistryPath, out var plannedWriteTime))
        {
            reason = "PhysicalWriteTimeUnavailable";
            return false;
        }

        var changed = showDisabled.Existed
            ? new[] { values["HideBasedOnVelocityId"] }
            : openNewWindow
                ? new[] { values["HideBasedOnVelocityId"], values["ProgrammaticAccessOnly"] }
                : new[] { values["HideBasedOnVelocityId"], values["ProgrammaticAccessOnly"], values["LegacyDisable"] };
        var generationFingerprint = ShellVerbVisibility.ComputeGenerationFingerprint(key);
        var planningKey = ContextMenuRegistryCatalog.OpenRegistryKey(physicalRegistryPath, writable: false);
        try
        {
            if (planningKey is null
                || !ContextMenuRegistryCatalog.TryGetRegistryWriteTimeUtc(physicalRegistryPath, out var confirmedWriteTime)
                || confirmedWriteTime != plannedWriteTime
                || !string.Equals(ShellVerbVisibility.ComputeGenerationFingerprint(planningKey), generationFingerprint, StringComparison.Ordinal)
                || values.Values.Any(value => !ShellVerbVisibility.ValueMatches(planningKey, value)))
            {
                planningKey?.Dispose();
                reason = "PhysicalGenerationChangedDuringPlanning";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            planningKey?.Dispose();
            reason = "PhysicalGenerationChangedDuringPlanning";
            return false;
        }
        transaction = new LegacyShellVerbVisibilityRecoveryTransaction(
            physicalRegistryPath,
            changed,
            changed.Select(value => new PersistedRegistryValueSnapshot { Name = value.Name, Existed = false }).ToArray(),
            values.Values.ToArray(),
            generationFingerprint,
            plannedWriteTime,
            planningKey);
        reason = showDisabled.Existed ? "ShowAsDisabledIfHidden" : openNewWindow ? "OpenNewWindow" : "Ordinary";
        return true;
    }

    public void Apply(RegistryKey key)
    {
        if (!MatchesPhysicalGeneration(key, _plannedWriteTime)
            || _observedVisibility.Any(value => !ShellVerbVisibility.ValueMatches(key, value)))
        {
            throw new ShellVerbMutationException(
                "SHELL_VERB_PHYSICAL_GENERATION_CONFLICT",
                "The legacy Shell verb registration changed before recovery; no visibility metadata was removed.");
        }

        try
        {
            for (var i = 0; i < _written.Length; i++)
            {
                try
                {
                    ShellVerbVisibility.RestoreValue(key, _written[i]);
                }
                finally
                {
                    // A provider can throw after committing a value. Include
                    // that value in compare-before-rollback if read-back sees it.
                    if (ShellVerbVisibility.ValueMatches(key, _written[i])) _appliedCount = i + 1;
                }
            }
        }
        finally
        {
            if (Applied)
            {
                ContextMenuRegistryCatalog.TryGetRegistryWriteTimeUtc(PhysicalRegistryPath, out _writtenWriteTime);
            }
        }
    }

    public bool Verify(RegistryKey key) => Applied && MatchesPhysicalGeneration(key, _writtenWriteTime)
        && _written.All(value => ShellVerbVisibility.ValueMatches(key, value))
        && UnchangedVisibilityMatches(key)
        && ShellVerbVisibility.GetState(key).ContextMenuVisible;

    public ShellVerbRollbackResult TryRollback(RegistryKey key)
    {
        if (!Applied) return new ShellVerbRollbackResult(false, true, false, null);
        if (!MatchesPhysicalGeneration(key, _writtenWriteTime)
            || _written.Where((_, index) => index < _appliedCount).Any(value => !ShellVerbVisibility.ValueMatches(key, value))
            || _before.Where((_, index) => index >= _appliedCount).Any(value => !ShellVerbVisibility.ValueMatches(key, value))
            || !UnchangedVisibilityMatches(key))
        {
            return new ShellVerbRollbackResult(true, false, true, "The physical registration changed after legacy recovery.");
        }

        for (var i = _appliedCount - 1; i >= 0; i--) ShellVerbVisibility.RestoreValue(key, _before[i]);
        var succeeded = VerifyRolledBack(key);
        return new ShellVerbRollbackResult(true, succeeded, false, succeeded ? null : "Legacy recovery rollback read-back failed.");
    }

    public bool VerifyRolledBack(RegistryKey key) => _observedVisibility.All(value => ShellVerbVisibility.ValueMatches(key, value));

    public void Dispose() => _planningKey.Dispose();

    private bool UnchangedVisibilityMatches(RegistryKey key)
        => _observedVisibility.Where(value => !_before.Any(changed =>
                string.Equals(changed.Name, value.Name, StringComparison.OrdinalIgnoreCase)))
            .All(value => ShellVerbVisibility.ValueMatches(key, value));

    private bool MatchesPhysicalGeneration(RegistryKey key, DateTimeOffset writeTime)
        => writeTime != default
           && PlanningKeyStillExists()
           && string.Equals(key.Name, PhysicalRegistryPath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ShellVerbVisibility.ComputeGenerationFingerprint(key), _generationFingerprint, StringComparison.Ordinal)
           && ContextMenuRegistryCatalog.TryGetRegistryWriteTimeUtc(PhysicalRegistryPath, out var currentWriteTime)
           && currentWriteTime == writeTime;

    private bool PlanningKeyStillExists()
    {
        try
        {
            _ = _planningKey.GetValueNames();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
    }
}

internal sealed record ShellVerbRollbackResult(bool Attempted, bool Succeeded, bool Conflict, string? Error);

internal sealed class RegistryValueMutationTransaction
{
    private readonly PersistedRegistryValueSnapshot _before;
    private readonly PersistedRegistryValueSnapshot _written;

    private RegistryValueMutationTransaction(
        string physicalRegistryPath,
        PersistedRegistryValueSnapshot before,
        PersistedRegistryValueSnapshot written)
    {
        PhysicalRegistryPath = physicalRegistryPath;
        _before = before;
        _written = written;
    }

    public string PhysicalRegistryPath { get; }
    public bool Applied { get; private set; }

    public static RegistryValueMutationTransaction Create(
        RegistryKey key,
        string physicalRegistryPath,
        PersistedRegistryValueSnapshot written)
        => new(
            physicalRegistryPath,
            ShellVerbVisibility.CaptureValue(key, written.Name),
            written);

    public void Apply(RegistryKey key)
    {
        ShellVerbVisibility.RestoreValue(key, _written);
        Applied = true;
    }

    public bool Verify(RegistryKey key) => ShellVerbVisibility.ValueMatches(key, _written);

    public ShellVerbRollbackResult TryRollback(RegistryKey key)
    {
        if (!Applied)
        {
            return new ShellVerbRollbackResult(false, true, false, null);
        }
        if (!ShellVerbVisibility.ValueMatches(key, _written))
        {
            return new ShellVerbRollbackResult(true, false, true, "The registry value was changed by another process.");
        }
        ShellVerbVisibility.RestoreValue(key, _before);
        var succeeded = ShellVerbVisibility.ValueMatches(key, _before);
        return new ShellVerbRollbackResult(true, succeeded, false, succeeded ? null : "Rollback read-back failed.");
    }

    public bool VerifyRolledBack(RegistryKey key)
        => ShellVerbVisibility.ValueMatches(key, _before);
}

internal sealed class ShellVerbMutationException : Exception
{
    public ShellVerbMutationException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}
