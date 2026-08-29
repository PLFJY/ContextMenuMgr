using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>
/// Executable form of the six monitoring and state-database rules documented in
/// docs/registry-model.md.
/// </summary>
public sealed class ContextMenuMonitoringRulesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyStateDatabase_AdoptsCurrentEnabledState(bool enabled)
    {
        var state = PersistedContextMenuState.FromEntry(BuildEntry(enabled));

        Assert.Equal(enabled, state.ObservedEnabled);
        Assert.Equal(enabled, state.DesiredEnabled);
        Assert.False(state.IsPendingApproval);
    }

    [Fact]
    public void ExplicitMarker_EstablishesBaselineEvenWhenSourceHasNoItems()
    {
        var states = new Dictionary<string, PersistedContextMenuState>(StringComparer.OrdinalIgnoreCase)
        {
            [ContextMenuRegistryCatalog.RegularBaselineMarkerId] = new()
            {
                Id = ContextMenuRegistryCatalog.RegularBaselineMarkerId,
                SourceRootPath = "internal:baseline",
                IsDeleted = true
            }
        };

        Assert.True(ContextMenuRegistryCatalog.HasPersistedBaseline(
            states,
            ContextMenuRegistryCatalog.RegularBaselineMarkerId,
            static state => state.SourceRootPath == "monitored"));
    }

    [Fact]
    public void EmptyStateWithoutMarker_HasNoBaseline()
    {
        var states = new Dictionary<string, PersistedContextMenuState>(StringComparer.OrdinalIgnoreCase);

        Assert.False(ContextMenuRegistryCatalog.HasPersistedBaseline(
            states,
            ContextMenuRegistryCatalog.RegularBaselineMarkerId,
            static state => state.SourceRootPath == "monitored"));
    }

    [Fact]
    public void DeletedRecoveryRecord_DoesNotEstablishMonitoringBaseline()
    {
        var states = new Dictionary<string, PersistedContextMenuState>(StringComparer.OrdinalIgnoreCase)
        {
            ["deleted"] = new()
            {
                Id = "deleted",
                SourceRootPath = "monitored",
                IsDeleted = true,
                BackupFilePath = @"C:\Backups\deleted.reg"
            }
        };

        Assert.False(ContextMenuRegistryCatalog.HasPersistedBaseline(
            states,
            ContextMenuRegistryCatalog.RegularBaselineMarkerId,
            static state => state.SourceRootPath == "monitored"));
    }

    [Fact]
    public void RuntimeUnknownItem_EntersApproval()
    {
        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            BuildEntry(enabled: true),
            state: null,
            hasBaseline: true,
            isBaselineEstablishment: false);

        Assert.Equal(ItemMonitorAction.QuarantineAdded, action);
    }

    [Fact]
    public void ExistingEnabledItemExternallyDisabled_IsModifiedOnly()
    {
        var state = PersistedContextMenuState.FromEntry(BuildEntry(enabled: true));
        var actual = BuildEntry(enabled: false);

        Assert.Equal(
            ContextMenuChangeKind.Modified,
            ContextMenuChangeClassifier.GetDetectedChangeKind(actual, state, hasBaseline: true));
        Assert.False(ContextMenuChangeClassifier.ShouldReconcileDisabledState(actual, state));
        Assert.Null(ContextMenuChangeClassifier.GetConsistencyIssue(actual, state));
    }

    [Fact]
    public void ExistingDisabledItemExternallyEnabled_IsSilentlyReconciled()
    {
        var state = PersistedContextMenuState.FromEntry(BuildEntry(enabled: false));
        var actual = BuildEntry(enabled: true);

        Assert.True(ContextMenuChangeClassifier.ShouldReconcileDisabledState(actual, state));
        Assert.Equal(
            ItemMonitorAction.ReconcileDisabledState,
            ContextMenuChangeClassifier.ClassifyItemMonitorAction(
                actual,
                state,
                hasBaseline: true,
                isBaselineEstablishment: false));
        Assert.Null(ContextMenuChangeClassifier.GetConsistencyIssue(actual, state));
    }

    [Fact]
    public void OfflineUnknownItem_IsAddedWithoutRuntimeQuarantineAction()
    {
        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            BuildEntry(enabled: true),
            state: null,
            hasBaseline: true,
            isBaselineEstablishment: true);

        Assert.Equal(ItemMonitorAction.OfflineAddedHighlight, action);
    }

    [Fact]
    public void OfflineEnabledStateChange_IsModified()
    {
        var state = PersistedContextMenuState.FromEntry(BuildEntry(enabled: true));
        var actual = BuildEntry(enabled: false);

        Assert.Equal(
            ItemMonitorAction.MetadataModifiedHighlight,
            ContextMenuChangeClassifier.ClassifyItemMonitorAction(
                actual,
                state,
                hasBaseline: true,
                isBaselineEstablishment: true));
    }

    [Fact]
    public void OfflineDisabledStateChange_IsModifiedInsteadOfReconciled()
    {
        var state = PersistedContextMenuState.FromEntry(BuildEntry(enabled: false));
        var actual = BuildEntry(enabled: true);

        Assert.Equal(
            ItemMonitorAction.MetadataModifiedHighlight,
            ContextMenuChangeClassifier.ClassifyItemMonitorAction(
                actual,
                state,
                hasBaseline: true,
                isBaselineEstablishment: true));
        Assert.Equal(
            ContextMenuChangeKind.Modified,
            ContextMenuChangeClassifier.GetDetectedChangeKind(actual, state, hasBaseline: true));
    }

    private static ContextMenuEntry BuildEntry(bool enabled)
        => new()
        {
            Id = @"*\shell|rule-test",
            Category = ContextMenuCategory.File,
            EntryKind = ContextMenuEntryKind.ShellVerb,
            KeyName = "rule-test",
            DisplayName = "Rule test",
            RegistryPath = @"*\shell\rule-test",
            BackendRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shell\rule-test",
            SourceRootPath = @"*\shell",
            IsEnabled = enabled,
            IsPresentInRegistry = true
        };
}
