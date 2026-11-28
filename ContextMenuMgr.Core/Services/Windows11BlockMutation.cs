using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>One CLSID across its user/machine values, with exact backup and conditional rollback.</summary>
internal sealed class Windows11BlockMutation(Guid clsid, string userSid, bool enable, string marker)
{
    private sealed record Target(PersistedWindows11BlockBackup Before,
        IReadOnlyList<PersistedRegistryValueSnapshot> After,
        IReadOnlyList<RegistryValueMutationTransaction> Mutations);

    private readonly List<Target> _targets = [];
    public IReadOnlyList<PersistedWindows11BlockBackup> Backups => _targets
        .Where(target => target.Mutations.Count > 0 && target.Before.OriginalValues.Count > 0)
        .Select(target => target.Before).ToArray();
    public bool Applied => _targets.SelectMany(target => target.Mutations).Any(mutation => mutation.Applied);

    public void AddTarget(RegistryKey key)
    {
        var originals = Capture(key);
        // An existing external block already fulfils Disable. Do not take it over.
        // Duplicate values owned by us can safely be normalized during Disable.
        var preserveExisting = !enable && originals.Count > 0 && (originals.Count == 1
            || originals.Any(value => value.Kind != (int)RegistryValueKind.String || value.StringValue != marker));
        IReadOnlyList<PersistedRegistryValueSnapshot> after = enable ? [] : preserveExisting ? originals :
            [new() { Name = clsid.ToString("B"), Existed = true, Kind = (int)RegistryValueKind.String, StringValue = marker }];
        var mutations = originals.Select(value => value.Name).Concat(after.Select(value => value.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(name =>
            {
                var written = after.FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? new PersistedRegistryValueSnapshot { Name = name };
                return ShellVerbVisibility.ValueMatches(key, written) ? null : RegistryValueMutationTransaction.Create(
                    key, key.Name, written, originals.FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? new PersistedRegistryValueSnapshot { Name = name }, Windows11ClsidImpactIndex.NativeView);
            }).OfType<RegistryValueMutationTransaction>().ToArray();
        _targets.Add(new(new() { PhysicalRegistryPath = key.Name, Clsid = clsid.ToString("B"),
            UserSid = userSid, OriginalValues = originals }, after, mutations));
    }

    public void Apply()
    {
        // Validate every scope before changing the first value.
        foreach (var target in _targets)
        {
            using var key = Open(target.Before.PhysicalRegistryPath, false);
            if (!Matches(key, target.Before.OriginalValues))
                throw new InvalidOperationException("The menu settings changed. Refresh and try again.");
        }
        foreach (var mutation in _targets.SelectMany(target => target.Mutations))
        {
            using var key = mutation.OpenKey(true)!;
            mutation.Apply(key);
        }
        if (!Verify()) throw new InvalidOperationException("The menu change could not be confirmed.");
    }

    public string ReadBack() => System.Text.Json.JsonSerializer.Serialize(_targets.Select(target =>
    {
        using var key = Open(target.Before.PhysicalRegistryPath, false);
        return new { target.Before.PhysicalRegistryPath, Values = Capture(key) };
    }));

    public bool Verify() => _targets.All(target =>
    {
        using var key = Open(target.Before.PhysicalRegistryPath, false);
        return Matches(key, target.After);
    });

    public ShellVerbRollbackResult TryRollback()
    {
        if (!Applied) return new(false, true, false, null);
        var errors = new List<string>();
        var conflict = false;
        foreach (var mutation in _targets.SelectMany(target => target.Mutations).Reverse())
        {
            if (!mutation.Applied) continue;
            try
            {
                using var key = mutation.OpenKey(true)!;
                var result = mutation.TryRollback(key);
                conflict |= result.Conflict;
                using var fresh = mutation.OpenKey(false)!;
                if (!result.Succeeded || !mutation.VerifyRolledBack(fresh))
                    errors.Add(result.Error ?? "Rollback read-back failed.");
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        foreach (var target in _targets)
        {
            try
            {
                using var fresh = Open(target.Before.PhysicalRegistryPath, false);
                if (!Matches(fresh, target.Before.OriginalValues))
                {
                    conflict = true;
                    errors.Add("External menu changes were preserved during rollback.");
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        return new(true, errors.Count == 0 && !conflict, conflict, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private List<PersistedRegistryValueSnapshot> Capture(RegistryKey key) => key.GetValueNames()
        .Where(name => Guid.TryParse(name, out var value) && value == clsid)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .Select(name => ShellVerbVisibility.CaptureValue(key, name)).ToList();

    private bool Matches(RegistryKey key, IReadOnlyList<PersistedRegistryValueSnapshot> expected)
        => Capture(key).Select(value => value.Name).SequenceEqual(expected.Select(value => value.Name), StringComparer.OrdinalIgnoreCase)
            && expected.All(value => ShellVerbVisibility.ValueMatches(key, value));

    private static RegistryKey Open(string path, bool writable) => RegistryValueMutationTransaction.OpenKey(
        path, Windows11ClsidImpactIndex.NativeView, writable)
        ?? throw new InvalidOperationException("The menu settings are no longer available.");
}
