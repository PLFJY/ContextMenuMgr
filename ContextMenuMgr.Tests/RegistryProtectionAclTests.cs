using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class RegistryProtectionAclTests
{
    [Fact]
    public void RuleDefinition_ContainsExactlyTheFourIntendedCombinations()
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var rules = RegistryProtectionAcl.CreateProtectionRules();

        Assert.Equal(4, rules.Count);
        Assert.Contains(rules, rule => IsProtectionRule(rule, users, InheritanceFlags.None));
        Assert.Contains(rules, rule => IsProtectionRule(rule, authenticatedUsers, InheritanceFlags.None));
        Assert.Contains(rules, rule => IsProtectionRule(rule, users, InheritanceFlags.ContainerInherit));
        Assert.Contains(rules, rule => IsProtectionRule(rule, authenticatedUsers, InheritanceFlags.ContainerInherit));
    }

    [Fact]
    public void Enable_AddsEveryRequiredProtectionSemantic()
    {
        var security = new RegistrySecurity();

        Assert.True(RegistryProtectionAcl.ApplyProtectionRules(security));

        var snapshot = RegistryProtectionAcl.Observe(security);
        Assert.Equal(RegistryProtectionObservedState.Enabled, snapshot.State);
        Assert.True(RegistryProtectionAcl.HasProtectionRules(security));
        Assert.All(
            RegistryProtectionAcl.RequiredRuleIdentities,
            identity => Assert.Equal(
                RegistryProtectionAcl.ProtectedRights,
                snapshot.GetRights(identity)));
    }

    [Fact]
    public void Disable_RemovesProtectionSemantics()
    {
        var security = CreateProtectedSecurity();

        Assert.True(RegistryProtectionAcl.RemoveProtectionRules(security));

        Assert.True(RegistryProtectionAcl.HasNoProtectionRules(security));
        Assert.Equal(RegistryProtectionObservedState.Disabled, RegistryProtectionAcl.Observe(security).State);
    }

    [Fact]
    public void EnableAndDisable_AreIdempotent_AndDoNotAccumulateRules()
    {
        var security = new RegistrySecurity();

        RegistryProtectionAcl.ApplyProtectionRules(security);
        var firstEnableRules = CountExplicitProtectionRules(security);
        Assert.False(RegistryProtectionAcl.ApplyProtectionRules(security));
        Assert.Equal(firstEnableRules, CountExplicitProtectionRules(security));

        RegistryProtectionAcl.RemoveProtectionRules(security);
        Assert.False(RegistryProtectionAcl.RemoveProtectionRules(security));
        Assert.Equal(0, CountExplicitProtectionRules(security));

        RegistryProtectionAcl.ApplyProtectionRules(security);
        Assert.Equal(firstEnableRules, CountExplicitProtectionRules(security));
        Assert.True(RegistryProtectionAcl.HasProtectionRules(security));
    }

    [Fact]
    public void EnableAndDisable_PreserveUnrelatedAllowAndDenyRights()
    {
        var security = new RegistrySecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        security.AddAccessRule(new RegistryAccessRule(system, RegistryRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(administrators, RegistryRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(users, RegistryRights.ReadKey, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            users,
            RegistryRights.Delete,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));

        RegistryProtectionAcl.ApplyProtectionRules(security);
        RegistryProtectionAcl.RemoveProtectionRules(security);

        var rules = GetRules(security, includeInherited: true);
        Assert.Contains(rules, rule => IsRule(rule, system, RegistryRights.FullControl, AccessControlType.Allow));
        Assert.Contains(rules, rule => IsRule(rule, administrators, RegistryRights.FullControl, AccessControlType.Allow));
        Assert.Contains(rules, rule => IsRule(rule, users, RegistryRights.ReadKey, AccessControlType.Allow));
        Assert.Contains(
            rules,
            rule => IsRule(rule, users, RegistryRights.Delete, AccessControlType.Deny)
                    && rule.InheritanceFlags == InheritanceFlags.None
                    && rule.PropagationFlags == PropagationFlags.None);
        Assert.DoesNotContain(
            rules,
            rule => rule.AccessControlType == AccessControlType.Deny
                    && rule.IdentityReference.Equals(users)
                    && (rule.RegistryRights & RegistryProtectionAcl.ProtectedRights) != 0);
    }

    [Fact]
    public void Disable_DoesNotRemoveInheritedLookalikeRule()
    {
        const string sddl = "O:BAG:BAD:AI(D;CIID;0x00000006;;;BU)";
        var descriptor = new RawSecurityDescriptor(sddl);
        var binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        var security = new RegistrySecurity();
        security.SetSecurityDescriptorBinaryForm(binary);

        var inheritedBefore = Assert.Single(GetRules(security, includeInherited: true), static rule => rule.IsInherited);
        Assert.False(RegistryProtectionAcl.RemoveProtectionRules(security));
        var inheritedAfter = Assert.Single(GetRules(security, includeInherited: true), static rule => rule.IsInherited);

        Assert.Equal(inheritedBefore.IdentityReference, inheritedAfter.IdentityReference);
        Assert.Equal(inheritedBefore.RegistryRights, inheritedAfter.RegistryRights);
        Assert.Equal(RegistryProtectionObservedState.Disabled, RegistryProtectionAcl.Observe(security).State);
    }

    [Fact]
    public void Disable_RemovesOnlyProtectedBitsFromMergedDenyRule()
    {
        var security = new RegistrySecurity();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        security.AddAccessRule(new RegistryAccessRule(
            users,
            RegistryProtectionAcl.ProtectedRights | RegistryRights.Delete,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));

        RegistryProtectionAcl.RemoveProtectionRules(security);

        var remaining = Assert.Single(
            GetRules(security, includeInherited: false),
            rule => rule.AccessControlType == AccessControlType.Deny
                    && rule.IdentityReference.Equals(users)
                    && rule.InheritanceFlags == InheritanceFlags.None);
        Assert.Equal(RegistryRights.Delete, remaining.RegistryRights);
    }

    private static RegistrySecurity CreateProtectedSecurity()
    {
        var security = new RegistrySecurity();
        RegistryProtectionAcl.ApplyProtectionRules(security);
        return security;
    }

    private static int CountExplicitProtectionRules(RegistrySecurity security)
        => GetRules(security, includeInherited: false).Count(rule =>
            rule.AccessControlType == AccessControlType.Deny
            && (rule.RegistryRights & RegistryProtectionAcl.ProtectedRights) != 0);

    private static List<RegistryAccessRule> GetRules(RegistrySecurity security, bool includeInherited)
        => security
            .GetAccessRules(includeExplicit: true, includeInherited, typeof(SecurityIdentifier))
            .OfType<RegistryAccessRule>()
            .ToList();

    private static bool IsRule(
        RegistryAccessRule rule,
        SecurityIdentifier identity,
        RegistryRights rights,
        AccessControlType type)
        => rule.IdentityReference.Equals(identity)
           && rule.RegistryRights == rights
           && rule.AccessControlType == type;

    private static bool IsProtectionRule(
        RegistryAccessRule rule,
        SecurityIdentifier identity,
        InheritanceFlags inheritanceFlags)
        => IsRule(rule, identity, RegistryProtectionAcl.ProtectedRights, AccessControlType.Deny)
           && rule.InheritanceFlags == inheritanceFlags
           && rule.PropagationFlags == PropagationFlags.None;
}
