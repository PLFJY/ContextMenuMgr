using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

internal enum RegistryProtectionObservedState
{
    Unknown,
    Disabled,
    Partial,
    Enabled
}

internal readonly record struct RegistryProtectionRuleIdentity(
    string Sid,
    InheritanceFlags InheritanceFlags);

internal sealed class RegistryProtectionAclSnapshot
{
    private readonly Dictionary<RegistryProtectionRuleIdentity, RegistryRights> _coverage;

    internal RegistryProtectionAclSnapshot(
        IReadOnlyDictionary<RegistryProtectionRuleIdentity, RegistryRights> coverage)
    {
        _coverage = RegistryProtectionAcl.RequiredRuleIdentities.ToDictionary(
            static identity => identity,
            identity => coverage.TryGetValue(identity, out var rights)
                ? rights & RegistryProtectionAcl.ProtectedRights
                : 0);
    }

    public RegistryProtectionObservedState State
    {
        get
        {
            var coveredRuleCount = _coverage.Values.Count(static rights => rights != 0);
            if (coveredRuleCount == 0)
            {
                return RegistryProtectionObservedState.Disabled;
            }

            return _coverage.Values.All(static rights => rights == RegistryProtectionAcl.ProtectedRights)
                ? RegistryProtectionObservedState.Enabled
                : RegistryProtectionObservedState.Partial;
        }
    }

    public RegistryRights GetRights(RegistryProtectionRuleIdentity identity)
        => _coverage.GetValueOrDefault(identity) & RegistryProtectionAcl.ProtectedRights;

    public bool SemanticallyEquals(RegistryProtectionAclSnapshot other)
        => RegistryProtectionAcl.RequiredRuleIdentities.All(
            identity => GetRights(identity) == other.GetRights(identity));
}

internal static class RegistryProtectionAcl
{
    internal const RegistryRights ProtectedRights = RegistryRights.CreateSubKey | RegistryRights.SetValue;

    private static readonly string BuiltinUsersSid =
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;

    private static readonly string AuthenticatedUsersSid =
        new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value;

    internal static readonly RegistryProtectionRuleIdentity[] RequiredRuleIdentities =
    [
        new(BuiltinUsersSid, InheritanceFlags.None),
        new(AuthenticatedUsersSid, InheritanceFlags.None),
        new(BuiltinUsersSid, InheritanceFlags.ContainerInherit),
        new(AuthenticatedUsersSid, InheritanceFlags.ContainerInherit)
    ];

    public static IReadOnlyList<RegistryAccessRule> CreateProtectionRules()
        => RequiredRuleIdentities
            .Select(identity => CreateRule(identity, ProtectedRights))
            .ToArray();

    public static RegistryProtectionAclSnapshot Observe(RegistrySecurity security)
    {
        var coverage = RequiredRuleIdentities.ToDictionary(
            static identity => identity,
            static _ => (RegistryRights)0);

        foreach (var rule in GetExplicitRules(security))
        {
            if (!TryGetProtectionIdentity(rule, out var identity))
            {
                continue;
            }

            var protectedRights = rule.RegistryRights & ProtectedRights;
            coverage[identity] |= protectedRights;

            // A ContainerInherit ACE without InheritOnly applies both to the
            // current key and to child keys. Windows may therefore canonicalize
            // the separate direct + inheritable rules into this one ACE.
            if (identity.InheritanceFlags == InheritanceFlags.ContainerInherit)
            {
                var directIdentity = new RegistryProtectionRuleIdentity(
                    identity.Sid,
                    InheritanceFlags.None);
                coverage[directIdentity] |= protectedRights;
            }
        }

        return new RegistryProtectionAclSnapshot(coverage);
    }

    public static bool HasProtectionRules(RegistrySecurity security)
        => Observe(security).State == RegistryProtectionObservedState.Enabled;

    public static bool HasNoProtectionRules(RegistrySecurity security)
        => Observe(security).State == RegistryProtectionObservedState.Disabled;

    public static bool ApplyProtectionRules(RegistrySecurity security)
    {
        var before = Observe(security);
        var changed = false;

        foreach (var identity in RequiredRuleIdentities)
        {
            var missingRights = ProtectedRights & ~before.GetRights(identity);
            if (missingRights == 0)
            {
                continue;
            }

            security.AddAccessRule(CreateRule(identity, missingRights));
            changed = true;
        }

        return changed;
    }

    public static bool RemoveProtectionRules(RegistrySecurity security)
        => RemoveProtectionRights(security, CreateCompleteSnapshot());

    public static bool RestoreProtectionState(
        RegistrySecurity security,
        RegistryProtectionAclSnapshot desiredState)
    {
        var changed = RemoveProtectionRules(security);
        foreach (var identity in RequiredRuleIdentities)
        {
            var rights = desiredState.GetRights(identity);
            if (rights == 0)
            {
                continue;
            }

            security.AddAccessRule(CreateRule(identity, rights));
            changed = true;
        }

        return changed;
    }

    public static bool RemoveProtectionRights(
        RegistrySecurity security,
        RegistryProtectionAclSnapshot rightsToRemove)
    {
        var changed = false;
        var rules = GetExplicitRules(security).ToArray();

        foreach (var rule in rules)
        {
            if (!TryGetProtectionIdentity(rule, out var identity))
            {
                continue;
            }

            var removableRights = rule.RegistryRights & rightsToRemove.GetRights(identity);
            if (removableRights == 0)
            {
                continue;
            }

            var preservedRights = rule.RegistryRights & ~removableRights;
            security.RemoveAccessRuleSpecific(rule);
            if (preservedRights != 0)
            {
                security.AddAccessRule(CreateRule(identity, preservedRights));
            }

            changed = true;
        }

        return changed;
    }

    internal static RegistryProtectionAclSnapshot CreateCompleteSnapshot()
        => new(RequiredRuleIdentities.ToDictionary(
            static identity => identity,
            static _ => ProtectedRights));

    private static IEnumerable<RegistryAccessRule> GetExplicitRules(RegistrySecurity security)
        => security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<RegistryAccessRule>();

    private static bool TryGetProtectionIdentity(
        RegistryAccessRule rule,
        out RegistryProtectionRuleIdentity identity)
    {
        identity = default;
        if (rule.AccessControlType != AccessControlType.Deny
            || rule.PropagationFlags != PropagationFlags.None
            || rule.IdentityReference is not SecurityIdentifier sid)
        {
            return false;
        }

        var candidate = new RegistryProtectionRuleIdentity(sid.Value, rule.InheritanceFlags);
        if (!RequiredRuleIdentities.Contains(candidate))
        {
            return false;
        }

        identity = candidate;
        return true;
    }

    private static RegistryAccessRule CreateRule(
        RegistryProtectionRuleIdentity identity,
        RegistryRights rights)
        => new(
            new SecurityIdentifier(identity.Sid),
            rights,
            identity.InheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Deny);
}
