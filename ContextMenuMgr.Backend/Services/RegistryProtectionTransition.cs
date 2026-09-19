using System.Security.AccessControl;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

internal sealed record RegistryProtectionTarget(
    RegistryHive Hive,
    string RegistryPath,
    string SubKeyPath)
{
    public string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.Users => "HKEY_USERS",
        _ => Hive.ToString()
    };
}

internal sealed record RegistryProtectionObservation(
    bool Exists,
    RegistryProtectionAclSnapshot? AclState)
{
    public static RegistryProtectionObservation Missing { get; } = new(false, null);
}

internal interface IRegistryProtectionTargetAccessor
{
    RegistryProtectionObservation Observe(RegistryProtectionTarget target);

    bool ApplyProtectionState(RegistryProtectionTarget target, bool enabled);

    bool RestoreProtectionState(
        RegistryProtectionTarget target,
        RegistryProtectionAclSnapshot previousState);
}

internal interface IRegistryProtectionSettingsStore
{
    Task<BackendProtectionSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(BackendProtectionSettings settings, CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken);
}

internal sealed class RegistryProtectionTargetResult
{
    public required RegistryProtectionTarget Target { get; init; }

    public bool Exists { get; set; }

    public bool RequestedEnabled { get; init; }

    public RegistryProtectionObservedState PreviousProtectionState { get; set; } = RegistryProtectionObservedState.Unknown;

    public RegistryProtectionObservedState ObservedProtectionState { get; set; } = RegistryProtectionObservedState.Unknown;

    public RegistryProtectionAclSnapshot? PreviousAclState { get; set; }

    public RegistryProtectionAclSnapshot? ObservedAclState { get; set; }

    public bool ApplyAttempted { get; set; }

    public bool ApplySucceeded { get; set; }

    public bool VerificationSucceeded { get; set; }

    public string? Error { get; set; }

    public bool RollbackAttempted { get; set; }

    public bool? RollbackSucceeded { get; set; }

    public string? RollbackError { get; set; }

    public bool Converged => !Exists || (ApplySucceeded && VerificationSucceeded);
}

internal sealed class RegistryProtectionTransitionResult
{
    public bool RequestedEnabled { get; init; }

    public bool Succeeded { get; set; }

    public bool PersistedValue { get; set; }

    public bool SettingsSaveAttempted { get; set; }

    public bool SettingsSaveSucceeded { get; set; }

    public string? SettingsSaveError { get; set; }

    public bool RollbackAttempted { get; set; }

    public bool? RollbackSucceeded { get; set; }

    public IReadOnlyList<RegistryProtectionTargetResult> Targets { get; init; } = [];

    public int VerifiedTargetCount => Targets.Count(static target => target.Exists && target.VerificationSucceeded);

    public int FailedTargetCount => Targets.Count(static target => !target.Converged);
}

internal sealed class RegistryProtectionTransition
{
    private readonly IRegistryProtectionTargetAccessor _targetAccessor;
    private readonly IRegistryProtectionSettingsStore _settingsStore;

    public RegistryProtectionTransition(
        IRegistryProtectionTargetAccessor targetAccessor,
        IRegistryProtectionSettingsStore settingsStore)
    {
        _targetAccessor = targetAccessor;
        _settingsStore = settingsStore;
    }

    public async Task<RegistryProtectionTransitionResult> ExecuteAsync(
        bool requestedEnabled,
        BackendProtectionSettings currentSettings,
        IReadOnlyList<RegistryProtectionTarget> targets)
    {
        var targetResults = targets
            .Select(target => new RegistryProtectionTargetResult
            {
                Target = target,
                RequestedEnabled = requestedEnabled
            })
            .ToArray();

        var result = new RegistryProtectionTransitionResult
        {
            RequestedEnabled = requestedEnabled,
            PersistedValue = currentSettings.LockNewContextMenuItems,
            Targets = targetResults
        };

        ObserveInitialState(targetResults);

        // Enabling cannot be rolled back safely unless every applicable target's
        // pre-operation protection semantics were observed first.
        if (requestedEnabled && targetResults.Any(static target => target.Error is not null))
        {
            return result;
        }

        foreach (var targetResult in targetResults)
        {
            ApplyAndVerify(targetResult);
        }

        if (targetResults.Any(static target => !target.Converged))
        {
            if (requestedEnabled)
            {
                RollbackEnable(targetResults, result);
            }

            return result;
        }

        if (currentSettings.LockNewContextMenuItems == requestedEnabled)
        {
            result.SettingsSaveSucceeded = true;
            result.Succeeded = true;
            return result;
        }

        result.SettingsSaveAttempted = true;
        var previousPersistedValue = currentSettings.LockNewContextMenuItems;
        currentSettings.LockNewContextMenuItems = requestedEnabled;
        try
        {
            // Once ACL mutation begins, finish persistence/rollback even if the
            // client disconnects or cancels its pipe request.
            await _settingsStore.SaveAsync(currentSettings, CancellationToken.None);
            result.SettingsSaveSucceeded = true;
            result.PersistedValue = requestedEnabled;
            result.Succeeded = true;
            return result;
        }
        catch (Exception ex)
        {
            currentSettings.LockNewContextMenuItems = previousPersistedValue;
            result.SettingsSaveError = FormatException(ex);
            result.SettingsSaveSucceeded = false;

            if (requestedEnabled)
            {
                RollbackEnable(targetResults, result);
            }

            return result;
        }
    }

    private void ObserveInitialState(IEnumerable<RegistryProtectionTargetResult> targetResults)
    {
        foreach (var targetResult in targetResults)
        {
            try
            {
                var observation = _targetAccessor.Observe(targetResult.Target);
                targetResult.Exists = observation.Exists;
                targetResult.PreviousAclState = observation.AclState;
                targetResult.PreviousProtectionState = observation.AclState?.State
                    ?? (observation.Exists
                        ? RegistryProtectionObservedState.Unknown
                        : RegistryProtectionObservedState.Disabled);
            }
            catch (Exception ex)
            {
                targetResult.Exists = true;
                targetResult.Error = $"ACL read failed: {FormatException(ex)}";
            }
        }
    }

    private void ApplyAndVerify(RegistryProtectionTargetResult targetResult)
    {
        if (!targetResult.Exists)
        {
            targetResult.ApplySucceeded = true;
            targetResult.VerificationSucceeded = true;
            targetResult.ObservedProtectionState = RegistryProtectionObservedState.Disabled;
            return;
        }

        var alreadyConverged = targetResult.PreviousAclState is not null
            && IsDesiredState(targetResult.PreviousAclState.State, targetResult.RequestedEnabled);

        if (alreadyConverged)
        {
            targetResult.ApplySucceeded = true;
        }
        else
        {
            targetResult.ApplyAttempted = true;
            try
            {
                var existed = _targetAccessor.ApplyProtectionState(
                    targetResult.Target,
                    targetResult.RequestedEnabled);
                targetResult.ApplySucceeded = true;
                if (!existed)
                {
                    targetResult.Exists = false;
                }
            }
            catch (Exception ex)
            {
                targetResult.ApplySucceeded = false;
                AppendError(targetResult, $"ACL change failed: {FormatException(ex)}");
            }
        }

        try
        {
            var postObservation = _targetAccessor.Observe(targetResult.Target);
            targetResult.Exists = postObservation.Exists;
            targetResult.ObservedAclState = postObservation.AclState;
            targetResult.ObservedProtectionState = postObservation.AclState?.State
                ?? (postObservation.Exists
                    ? RegistryProtectionObservedState.Unknown
                    : RegistryProtectionObservedState.Disabled);

            targetResult.VerificationSucceeded = !postObservation.Exists
                || (postObservation.AclState is not null
                    && IsDesiredState(postObservation.AclState.State, targetResult.RequestedEnabled));

            if (!targetResult.VerificationSucceeded)
            {
                AppendError(
                    targetResult,
                    $"ACL read-back verification failed: observed {targetResult.ObservedProtectionState}.");
            }
        }
        catch (Exception ex)
        {
            targetResult.VerificationSucceeded = false;
            targetResult.ObservedProtectionState = RegistryProtectionObservedState.Unknown;
            AppendError(targetResult, $"ACL read-back failed: {FormatException(ex)}");
        }
    }

    private void RollbackEnable(
        IReadOnlyList<RegistryProtectionTargetResult> targetResults,
        RegistryProtectionTransitionResult operationResult)
    {
        var rollbackCandidates = targetResults
            .Where(static target => target.ApplyAttempted && target.PreviousAclState is not null)
            .ToArray();

        operationResult.RollbackAttempted = rollbackCandidates.Length > 0;
        operationResult.RollbackSucceeded = true;

        foreach (var targetResult in rollbackCandidates)
        {
            targetResult.RollbackAttempted = true;
            try
            {
                _targetAccessor.RestoreProtectionState(
                    targetResult.Target,
                    targetResult.PreviousAclState!);
                var observation = _targetAccessor.Observe(targetResult.Target);
                var rolledBack = !observation.Exists
                    || (observation.AclState is not null
                        && observation.AclState.SemanticallyEquals(targetResult.PreviousAclState!));
                targetResult.RollbackSucceeded = rolledBack;
                if (!rolledBack)
                {
                    targetResult.RollbackError =
                        $"Rollback verification failed: observed {observation.AclState?.State.ToString() ?? "Unknown"}.";
                }
            }
            catch (Exception ex)
            {
                targetResult.RollbackSucceeded = false;
                targetResult.RollbackError = FormatException(ex);
            }

            if (targetResult.RollbackSucceeded != true)
            {
                operationResult.RollbackSucceeded = false;
            }
        }
    }

    private static bool IsDesiredState(RegistryProtectionObservedState state, bool enabled)
        => enabled
            ? state == RegistryProtectionObservedState.Enabled
            : state == RegistryProtectionObservedState.Disabled;

    private static void AppendError(RegistryProtectionTargetResult result, string error)
        => result.Error = string.IsNullOrWhiteSpace(result.Error)
            ? error
            : $"{result.Error} | {error}";

    private static string FormatException(Exception exception)
        => $"{exception.GetType().Name}: {exception.Message}";
}

internal sealed class WindowsRegistryProtectionTargetAccessor : IRegistryProtectionTargetAccessor
{
    public RegistryProtectionObservation Observe(RegistryProtectionTarget target)
    {
        using var key = OpenTarget(
            target,
            RegistryKeyPermissionCheck.ReadSubTree,
            RegistryRights.ReadKey | RegistryRights.ReadPermissions);
        if (key is null)
        {
            return RegistryProtectionObservation.Missing;
        }

        var security = key.GetAccessControl(AccessControlSections.Access);
        return new RegistryProtectionObservation(true, RegistryProtectionAcl.Observe(security));
    }

    public bool ApplyProtectionState(RegistryProtectionTarget target, bool enabled)
    {
        using var key = OpenTarget(
            target,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
        if (key is null)
        {
            return false;
        }

        var security = key.GetAccessControl(AccessControlSections.Access);
        var changed = enabled
            ? RegistryProtectionAcl.ApplyProtectionRules(security)
            : RegistryProtectionAcl.RemoveProtectionRules(security);
        if (changed)
        {
            key.SetAccessControl(security);
        }

        return true;
    }

    public bool RestoreProtectionState(
        RegistryProtectionTarget target,
        RegistryProtectionAclSnapshot previousState)
    {
        using var key = OpenTarget(
            target,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
        if (key is null)
        {
            return false;
        }

        var security = key.GetAccessControl(AccessControlSections.Access);
        if (RegistryProtectionAcl.RestoreProtectionState(security, previousState))
        {
            key.SetAccessControl(security);
        }

        return true;
    }

    private static RegistryKey? OpenTarget(
        RegistryProtectionTarget target,
        RegistryKeyPermissionCheck permissionCheck,
        RegistryRights rights)
    {
        using var baseKey = RegistryKey.OpenBaseKey(target.Hive, RegistryView.Default);
        return baseKey.OpenSubKey(target.SubKeyPath, permissionCheck, rights);
    }
}
