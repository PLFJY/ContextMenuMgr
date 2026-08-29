using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class DetailedEditDictionaryMatchingTests
{
    [Theory]
    [InlineData("{23170F69-40C1-278A-1000-000100020000}", "23170f69-40c1-278a-1000-000100020000")]
    [InlineData("{5B69A6B4-393B-459C-8EBB-214237A9E7AC}", "5b69a6b4-393b-459c-8ebb-214237a9e7ac")]
    public void HandlerClsid_AndDictionaryGuid_AreEquivalentRegardlessOfBraces(string handler, string dictionary)
    {
        Assert.True(Guid.TryParse(handler, out var handlerGuid));
        Assert.True(DetailedEditMenuDialogService.MatchesHandlerClsid(handlerGuid, dictionary));
    }
}
