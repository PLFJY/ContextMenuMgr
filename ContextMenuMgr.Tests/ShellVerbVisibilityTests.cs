using Microsoft.Win32;
using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ShellVerbVisibilityTests
{
    [Fact]
    public void SetEnabled_DisableThenEnable_RoundTripsVisibilityValues()
    {
        var path = $@"Software\ContextMenuMgr.Tests\ShellVerbVisibility\{Guid.NewGuid():N}";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)!;

            ShellVerbVisibility.SetEnabled(key, @"Directory\Background\shell\Powershell", enable: false);

            Assert.False(ShellVerbVisibility.IsEnabled(key));
            Assert.Equal(0x639bc8, Convert.ToInt32(key.GetValue("HideBasedOnVelocityId")));
            Assert.NotNull(key.GetValue("ProgrammaticAccessOnly"));
            Assert.NotNull(key.GetValue("LegacyDisable"));

            ShellVerbVisibility.SetEnabled(key, @"Directory\Background\shell\Powershell", enable: true);

            Assert.True(ShellVerbVisibility.IsEnabled(key));
            Assert.Null(key.GetValue("HideBasedOnVelocityId"));
            Assert.Null(key.GetValue("ProgrammaticAccessOnly"));
            Assert.Null(key.GetValue("LegacyDisable"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Classes\Directory\Background\shell\Powershell", true)]
    [InlineData(@"HKEY_USERS\S-1-5-21-test\Software\Classes\Directory\Background\shell\Powershell", false)]
    [InlineData(@"Directory\Background\shell\Powershell", false)]
    public void ProtectedFallback_OnlyAllowsPhysicalMachineClassesPaths(string path, bool expected)
        => Assert.Equal(expected, ProtectedRegistryMutation.IsEligibleMachineClassesPath(path));

    [Fact]
    public void ProtectedFallback_RejectsUserHiveBeforeAnySecurityChange()
    {
        var exception = Assert.Throws<ProtectedRegistryMutationException>(() =>
            ProtectedRegistryMutation.Execute(
                @"HKEY_USERS\S-1-5-21-test\Software\Classes\Directory\Background\shell\Powershell",
                _ => throw new InvalidOperationException("Must not run."),
                _ => throw new InvalidOperationException("Must not run.")));

        Assert.Equal("PROTECTED_REGISTRY_MUTATION_FAILED", exception.ErrorCode);
    }
}
