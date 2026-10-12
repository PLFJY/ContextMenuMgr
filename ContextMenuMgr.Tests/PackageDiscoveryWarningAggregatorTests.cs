using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class PackageDiscoveryWarningAggregatorTests
{
    [Fact]
    public void RepeatedConditions_SummarizeChanges_RespectUserIsolationAndCapacity()
    {
        var warnings = new PackageDiscoveryWarningAggregator(TimeSpan.FromMinutes(15), 2);
        var now = DateTimeOffset.UtcNow;
        Assert.Single(warnings.Observe("sid-a|metadata", "pkg-a:80073B37", "A failed", now));
        Assert.Empty(warnings.Observe("sid-a|metadata", "pkg-a:80073B37", "A failed", now.AddSeconds(30)));
        var periodic = Assert.Single(warnings.Observe("sid-a|metadata", "pkg-a:80073B37", "A failed", now.AddMinutes(15)));
        Assert.Contains("SuppressedOccurrences=2", periodic);
        Assert.Empty(warnings.Observe("sid-a|metadata", "pkg-a:80073B37", "A failed", now.AddMinutes(16)));
        var changed = warnings.Observe("sid-a|metadata", "pkg-b:80070005", "B failed", now.AddMinutes(17));
        Assert.Equal(2, changed.Count);
        Assert.Contains("SuppressedOccurrences=1", changed[0]);
        Assert.Equal("B failed", changed[1]);
        Assert.Single(warnings.Observe("sid-b|metadata", "pkg-b:80070005", "B failed", now.AddMinutes(18)));
        Assert.Empty(warnings.Observe("sid-a|metadata", "pkg-b:80070005", "B failed", now.AddMinutes(19)));
        // A is most recently seen; adding C evicts B. Returning B emits anew.
        Assert.Single(warnings.Observe("sid-c|metadata", "pkg-c", "C failed", now.AddMinutes(20)));
        var returned = warnings.Observe("sid-b|metadata", "pkg-b:80070005", "B failed", now.AddMinutes(21));
        Assert.Contains(returned, line => line.Contains("Reason=evicted") && line.Contains("SuppressedOccurrences=1"));
        Assert.Contains("B failed", returned);
        Assert.Empty(warnings.Observe("sid-b|metadata", "pkg-b:80070005", "B failed", now.AddMinutes(22)));
        Assert.Contains("Reason=resolved", Assert.Single(warnings.Observe("sid-b|metadata", null, null, now.AddMinutes(23))));
    }
}
