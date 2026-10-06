using System.Globalization;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class RecycleBinPinToStartLocalizationTests
{
    [Theory]
    [InlineData("en-US", "Pin to Start")]
    [InlineData("zh-CN", "固定到“开始”")]
    [InlineData("zh-TW", "釘選到「開始」")]
    [InlineData("zh-HK", "釘選到「開始」")]
    [InlineData("fr-FR", "Pin to Start")]
    public void SystemItemLabel_UsesWindowsCultureMapping(string windowsCulture, string expected)
    {
        Assert.Equal(expected, LocalizationService.TranslateForSystemCulture(
            "RecycleBinPinToStart", CultureInfo.GetCultureInfo(windowsCulture)));
    }
}
