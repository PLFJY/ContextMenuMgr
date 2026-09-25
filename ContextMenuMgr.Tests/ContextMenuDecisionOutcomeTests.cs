using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ContextMenuDecisionOutcomeTests
{
    [Theory]
    [InlineData(ContextMenuDecision.Allow, true, false, false, true)]
    [InlineData(ContextMenuDecision.Allow, false, false, false, false)]
    [InlineData(ContextMenuDecision.Deny, false, false, false, true)]
    [InlineData(ContextMenuDecision.Deny, true, false, false, false)]
    [InlineData(ContextMenuDecision.Remove, false, true, false, true)]
    [InlineData(ContextMenuDecision.Remove, false, false, false, false)]
    [InlineData(ContextMenuDecision.Remove, false, true, true, false)]
    public void SnapshotState_DeterminesWhetherDecisionWasApplied(
        ContextMenuDecision decision,
        bool enabled,
        bool deleted,
        bool pending,
        bool expected)
    {
        var snapshot = new[]
        {
            new ContextMenuEntry
            {
                Id = "sample",
                IsEnabled = enabled,
                IsDeleted = deleted,
                IsPendingApproval = pending
            }
        };

        Assert.Equal(expected, ContextMenuWorkspaceService.IsDecisionApplied(snapshot, "sample", decision));
    }

    [Theory]
    [InlineData(ContextMenuDecision.Allow, false)]
    [InlineData(ContextMenuDecision.Deny, true)]
    [InlineData(ContextMenuDecision.Remove, true)]
    public void MissingItem_IsOnlyConfirmedForDenyOrRemove(ContextMenuDecision decision, bool expected)
    {
        Assert.Equal(expected, ContextMenuWorkspaceService.IsDecisionApplied([], "sample", decision));
    }
}
