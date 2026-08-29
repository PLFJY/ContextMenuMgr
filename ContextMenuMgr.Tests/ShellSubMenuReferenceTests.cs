using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ShellSubMenuReferenceTests
{
    [Fact]
    public void DisableReference_RemovesOnlyThisParentsReference_AndPreservesSeparators()
    {
        var disabled = new List<PersistedShellSubMenuReferenceState>();

        var parentA = ContextMenuRegistryCatalog.UpdateSubCommandsReference("A;|;B;C", "B", enable: false, disabled);
        var parentB = "B;D";

        Assert.Equal("A;|;C", parentA);
        Assert.Equal("B;D", parentB);
        Assert.Single(disabled);
        Assert.Equal("B", disabled[0].ReferenceName);
        Assert.Equal(2, disabled[0].OriginalIndex);
    }

    [Fact]
    public void EnableReference_UsesLatestLiveValue_AndDoesNotDiscardExternalAddition()
    {
        var disabled = new List<PersistedShellSubMenuReferenceState>
        {
            new() { ReferenceName = "B", OriginalIndex = 1 }
        };

        var result = ContextMenuRegistryCatalog.UpdateSubCommandsReference("A;C;D", "B", enable: true, disabled);

        Assert.Equal("A;B;C;D", result);
        Assert.Empty(disabled);
    }

    [Fact]
    public void DisabledReference_RemainsRestorableAcrossRepeatedOperations()
    {
        var disabled = new List<PersistedShellSubMenuReferenceState>();
        var afterDisable = ContextMenuRegistryCatalog.UpdateSubCommandsReference("A;B;C", "B", false, disabled);
        var afterEnable = ContextMenuRegistryCatalog.UpdateSubCommandsReference(afterDisable, "B", true, disabled);
        var afterSecondDisable = ContextMenuRegistryCatalog.UpdateSubCommandsReference(afterEnable, "B", false, disabled);

        Assert.Equal("A;C", afterDisable);
        Assert.Equal("A;B;C", afterEnable);
        Assert.Equal("A;C", afterSecondDisable);
        Assert.Single(disabled);
    }
}
