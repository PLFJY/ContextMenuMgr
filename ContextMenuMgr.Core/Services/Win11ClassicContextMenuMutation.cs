using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

// A compensating operation over narrowly owned default values. No subtree deletion.
// The service gate serializes our callers; external registry writers have no CAS API.
internal sealed class Win11ClassicContextMenuMutation
{
    internal static readonly string[] Clsids =
    ["{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", "{51668040-5e31-4023-bbe3-149163bd80f1}"];
    private sealed record Target(string Path, bool ClsidExisted, bool KeyExisted,
        PersistedRegistryValueSnapshot Before)
    {
        internal RegistryValueMutationTransaction? Mutation;
        internal bool Touched;
    }
    private readonly RegistryKey _root;
    private readonly Target[] _targets;
    private readonly bool _disabled;
    private static PersistedRegistryValueSnapshot Missing => new() { Name = string.Empty };
    private static PersistedRegistryValueSnapshot Empty => new()
        { Name = string.Empty, Existed = true, Kind = (int)RegistryValueKind.String, StringValue = string.Empty };

    internal Win11ClassicContextMenuMutation(RegistryKey root, string clsidPath, bool disabled)
    {
        _root = root;
        _disabled = disabled;
        _targets = Clsids.Select(clsid =>
        {
            var path = clsidPath + @"\" + clsid;
            using var parent = root.OpenSubKey(path);
            using var key = root.OpenSubKey(path + @"\InprocServer32");
            var before = key is null ? Missing : ShellVerbVisibility.CaptureValue(key, string.Empty);
            if (before.Existed && !ShellVerbVisibility.SnapshotsEqual(before, Empty))
                throw new InvalidOperationException($"Conflicting COM default value at {root.Name}\\{path}. Existing registration was preserved.");
            // Validate available target write access before changing either CLSID.
            using var writable = root.OpenSubKey(path + @"\InprocServer32", true);
            using var writableParent = root.OpenSubKey(path, true);
            return new Target(path, parent is not null, key is not null, before);
        }).ToArray();
    }

    internal static string ReadStatus(RegistryKey root, string clsidPath)
    {
        var states = Clsids.Select(clsid =>
        {
            using var key = root.OpenSubKey(clsidPath + @"\" + clsid + @"\InprocServer32");
            if (key is null) return "Absent";
            var value = ShellVerbVisibility.CaptureValue(key, string.Empty);
            return ShellVerbVisibility.SnapshotsEqual(value, Empty) ? "Complete" : value.Existed ? "Conflict" : "Incomplete";
        }).ToArray();
        return states.All(x => x == "Complete") ? "Complete"
            : states.All(x => x == "Absent") ? "Absent"
            : states.Contains("Conflict") ? "Conflict" : "Incomplete";
    }

    private void RequireOriginal(Target target)
    {
        using var key = _root.OpenSubKey(target.Path + @"\InprocServer32");
        if ((key is not null) != target.KeyExisted
            || !ShellVerbVisibility.SnapshotsEqual(key is null ? Missing : ShellVerbVisibility.CaptureValue(key, string.Empty), target.Before))
            throw new InvalidOperationException("The compatibility override changed concurrently. Refresh before retrying.");
    }

    internal bool Apply(CancellationToken cancellationToken)
    {
        foreach (var target in _targets) RequireOriginal(target);
        cancellationToken.ThrowIfCancellationRequested();
        // Once started, finish verification or compensation even if the pipe times out.
        var changed = false;
        foreach (var target in _targets)
        {
            RequireOriginal(target);
            if (_disabled && ShellVerbVisibility.SnapshotsEqual(target.Before, Empty)) continue;
            if (!_disabled && !target.KeyExisted) continue;
            target.Touched = true;
            using (var key = _disabled
                ? _root.CreateSubKey(target.Path + @"\InprocServer32", true)
                : _root.OpenSubKey(target.Path + @"\InprocServer32", true))
            {
                if (key is null) throw new InvalidOperationException("The user registry target is no longer available.");
                target.Mutation = RegistryValueMutationTransaction.Create(key, key.Name,
                    _disabled ? Empty : Missing, target.Before);
                target.Mutation.Apply(key);
                changed |= !ShellVerbVisibility.SnapshotsEqual(target.Before, _disabled ? Empty : Missing);
            }
            if (!_disabled) changed |= Prune(target.Path, removeClsid: true);
        }
        if (!Verify()) throw new InvalidOperationException("Compatibility registry read-back did not match the requested state.");
        return changed;
    }

    internal bool Verify() => _targets.All(target =>
    {
        using var key = _root.OpenSubKey(target.Path + @"\InprocServer32");
        return _disabled ? key is not null && ShellVerbVisibility.ValueMatches(key, Empty)
            : key is null || !ShellVerbVisibility.CaptureValue(key, string.Empty).Existed;
    });

    private bool Prune(string path, bool removeClsid)
    {
        var changed = DeleteIfEmpty(path + @"\InprocServer32");
        if (removeClsid) changed |= DeleteIfEmpty(path);
        return changed;
    }

    private bool DeleteIfEmpty(string path)
    {
        using (var key = _root.OpenSubKey(path))
            if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0) return false;
        // DeleteSubKey refuses child trees. Recheck values immediately before deletion.
        using (var key = _root.OpenSubKey(path))
            if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0) return false;
        _root.DeleteSubKey(path, false);
        return true;
    }

    internal ShellVerbRollbackResult TryRollback()
    {
        var errors = new List<string>();
        var conflict = false;
        foreach (var target in _targets.Reverse().Where(x => x.Touched))
        {
            try
            {
                if (target.Mutation is { Applied: true } mutation)
                {
                    using var existing = _root.OpenSubKey(target.Path + @"\InprocServer32", true);
                    // A removed key is expected only during restoration, never recreate
                    // a concurrently deleted key during disable compensation.
                    if (existing is null && _disabled) throw new InvalidOperationException("Registry target disappeared during rollback.");
                    using var recreated = existing is null ? _root.CreateSubKey(target.Path + @"\InprocServer32", true) : null;
                    var result = mutation.TryRollback(existing ?? recreated!);
                    conflict |= result.Conflict;
                    if (!result.Succeeded) throw new InvalidOperationException(result.Error);
                    using var fresh = _root.OpenSubKey(target.Path + @"\InprocServer32");
                    if (fresh is null || !mutation.VerifyRolledBack(fresh)) throw new InvalidOperationException("Rollback read-back failed.");
                }
                if (!target.KeyExisted) Prune(target.Path, !target.ClsidExisted);
                using var restored = _root.OpenSubKey(target.Path + @"\InprocServer32");
                if ((restored is not null) != target.KeyExisted
                    || !ShellVerbVisibility.SnapshotsEqual(restored is null ? Missing
                        : ShellVerbVisibility.CaptureValue(restored, string.Empty), target.Before))
                {
                    conflict = true;
                    throw new InvalidOperationException("Original registry state could not be confirmed after rollback; external changes were preserved.");
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        return new(_targets.Any(x => x.Touched), errors.Count == 0 && !conflict, conflict,
            errors.Count == 0 ? null : string.Join("; ", errors));
    }
}
