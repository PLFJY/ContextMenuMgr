using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>
/// Regression tests for WPS/Office synthetic finding approval baselining.
/// </summary>
public sealed class OfficeSuiteCoexistenceApprovalTests
{
    [Fact]
    public void EmptyStateDatabase_ExistingFinding_IsAdoptedWithoutApproval()
    {
        var requiresApproval = OfficeSuiteCoexistenceDetector
            .ShouldMarkNewFindingPendingApproval(hasPersistedBaseline: false);

        Assert.False(requiresApproval);
    }

    [Fact]
    public void EstablishedStateDatabase_NewFinding_RequiresApproval()
    {
        var requiresApproval = OfficeSuiteCoexistenceDetector
            .ShouldMarkNewFindingPendingApproval(hasPersistedBaseline: true);

        Assert.True(requiresApproval);
    }

    [Fact]
    public void RegularMenuBaseline_DoesNotCountAsWpsOfficeBaseline()
    {
        var states = new[]
        {
            new PersistedContextMenuState { SourceRootPath = @"*\shell" }
        };

        Assert.False(ContextMenuRegistryCatalog.HasWpsOfficeSyntheticBaseline(states));
    }

    [Fact]
    public void ExistingWpsOfficeFinding_CountsAsWpsOfficeBaseline()
    {
        var states = new[]
        {
            new PersistedContextMenuState { SourceRootPath = "special:wps-office-coexistence" }
        };

        Assert.True(ContextMenuRegistryCatalog.HasWpsOfficeSyntheticBaseline(states));
    }
}
