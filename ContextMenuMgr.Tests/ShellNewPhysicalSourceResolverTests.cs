using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ShellNewPhysicalSourceResolverTests
{
    [Fact]
    public void CrossHiveAssociation_WithMachineShellNew_SelectsMachineRegistration()
    {
        // The user extension -> machine ProgID/ShellNew case is visible through
        // RegOpenUserClassesRoot. The merged view, rather than this test, decides
        // visibility; this test protects the subsequent physical mutation choice.
        var source = ShellNewPhysicalSourceResolver.Resolve(
            effectiveRegistrationExists: true,
            userRegistrationExists: false,
            machineRegistrationExists: true);

        Assert.Equal(ShellNewPhysicalSource.Machine, source);
    }

    [Fact]
    public void UserRegistration_OverridesDuplicateMachineRegistration()
    {
        var source = ShellNewPhysicalSourceResolver.Resolve(
            effectiveRegistrationExists: true,
            userRegistrationExists: true,
            machineRegistrationExists: true);

        Assert.Equal(ShellNewPhysicalSource.User, source);
    }

    [Fact]
    public void EffectiveRegistrationWithoutPhysicalProvenance_IsNotMutable()
    {
        var source = ShellNewPhysicalSourceResolver.Resolve(
            effectiveRegistrationExists: true,
            userRegistrationExists: false,
            machineRegistrationExists: false);

        Assert.Equal(ShellNewPhysicalSource.Unresolved, source);
    }
}
