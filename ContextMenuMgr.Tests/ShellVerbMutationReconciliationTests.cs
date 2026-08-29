using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ShellVerbMutationReconciliationTests
{
    private const string ItemId = @"WMP11.AssocFile.MP4\shell|Enqueue";
    private const string MachinePath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WMP11.AssocFile.MP4\shell\Enqueue";
    private const string UserPath = @"HKEY_USERS\S-1-5-21-test\Software\Classes\WMP11.AssocFile.MP4\shell\Enqueue";

    [Fact]
    public void DisabledPhysicalShellVerb_WhenNormalSnapshotOmitsIt_RemainsVerifiablyManageable()
    {
        var item = CreateEntry(MachinePath, enabled: true);
        var physicallyDisabled = CreateEntry(MachinePath, enabled: false);

        var result = ContextMenuRegistryCatalog.ReconcileShellVerbMutation(
            item,
            [physicallyDisabled],
            refreshedLogicalEntry: null,
            requestedEnabled: false);

        Assert.True(result.IsVerified);
        Assert.True(result.UsedPhysicalSourceFallback);
        Assert.Equal(1, result.MatchingPhysicalCandidateCount);
        Assert.Equal(0, result.MatchingLogicalCandidateCount);
        Assert.Equal(ItemId, result.Entry!.Id);
        Assert.False(result.Entry.IsEnabled);
        Assert.False(result.DesiredEnabled);
        Assert.False(result.ObservedEnabled);
    }

    [Fact]
    public void EnableAfterDisable_UsesVerifiedPhysicalState()
    {
        var item = CreateEntry(MachinePath, enabled: false);
        var physicallyEnabled = CreateEntry(MachinePath, enabled: true);

        var result = ContextMenuRegistryCatalog.ReconcileShellVerbMutation(
            item,
            [physicallyEnabled],
            refreshedLogicalEntry: null,
            requestedEnabled: true);

        Assert.True(result.IsVerified);
        Assert.True(result.Entry!.IsEnabled);
        Assert.True(result.DesiredEnabled);
        Assert.True(result.ObservedEnabled);
    }

    [Fact]
    public void PhysicalStateThatDoesNotMatchRequest_FailsVerification()
    {
        var item = CreateEntry(MachinePath, enabled: true);
        var physicallyStillEnabled = CreateEntry(MachinePath, enabled: true);

        var result = ContextMenuRegistryCatalog.ReconcileShellVerbMutation(
            item,
            [physicallyStillEnabled],
            refreshedLogicalEntry: null,
            requestedEnabled: false);

        Assert.False(result.IsVerified);
        Assert.Equal(MachinePath, Assert.Single(result.MismatchedPhysicalPaths));
    }

    [Fact]
    public void AdditionalEnabledPhysicalRegistration_PreventsFalseDisableSuccess()
    {
        var item = CreateEntry(MachinePath, enabled: true);
        var result = ContextMenuRegistryCatalog.ReconcileShellVerbMutation(
            item,
            [CreateEntry(MachinePath, enabled: false), CreateEntry(UserPath, enabled: true)],
            refreshedLogicalEntry: null,
            requestedEnabled: false);

        Assert.False(result.IsVerified);
        Assert.Equal(2, result.MatchingPhysicalCandidateCount);
        Assert.Equal(UserPath, Assert.Single(result.MismatchedPhysicalPaths));
    }

    [Fact]
    public void MissingTargetPhysicalKey_FailsInsteadOfFabricatingSuccess()
    {
        var item = CreateEntry(MachinePath, enabled: true);
        var result = ContextMenuRegistryCatalog.ReconcileShellVerbMutation(
            item,
            [CreateEntry(UserPath, enabled: false)],
            refreshedLogicalEntry: null,
            requestedEnabled: false);

        Assert.False(result.IsVerified);
        Assert.False(result.TargetPathExists);
        Assert.Contains("targeted", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    private static ContextMenuEntry CreateEntry(string backendRegistryPath, bool enabled)
        => new()
        {
            Id = ItemId,
            Category = ContextMenuCategory.File,
            EntryKind = ContextMenuEntryKind.ShellVerb,
            KeyName = "Enqueue",
            DisplayName = "Enqueue in Windows Media Player",
            RegistryPath = @"WMP11.AssocFile.MP4\shell\Enqueue",
            BackendRegistryPath = backendRegistryPath,
            SourceRootPath = @"WMP11.AssocFile.MP4\shell",
            IsEnabled = enabled,
            IsPresentInRegistry = true,
            CanToggle = true
        };
}
