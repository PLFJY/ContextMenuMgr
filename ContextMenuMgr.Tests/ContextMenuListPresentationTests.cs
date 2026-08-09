using System.Text.Json;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ContextMenuListPresentationTests
{
    [Fact]
    public void SettingsWithoutViewMode_DefaultsToCompact()
    {
        var settings = JsonSerializer.Deserialize<FrontendSettings>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(settings);
        Assert.Equal(ContextMenuListViewMode.Compact, settings!.ContextMenuListViewMode);
    }

    [Fact]
    public void ViewMode_PersistsReloadsAndResetDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "frontend-settings.json");
        try
        {
            var service = new FrontendSettingsService(null, settingsPath);
            service.UpdateContextMenuListViewMode(ContextMenuListViewMode.Compact);
            service.UpdateHideDisabledItems(true);

            var reloaded = new FrontendSettingsService(null, settingsPath);
            Assert.Equal(ContextMenuListViewMode.Compact, reloaded.Current.ContextMenuListViewMode);
            Assert.True(reloaded.Current.HideDisabledItems);

            reloaded.ResetToDefaults();
            Assert.Equal(ContextMenuListViewMode.Compact, reloaded.Current.ContextMenuListViewMode);
            Assert.False(reloaded.Current.HideDisabledItems);
            Assert.False(File.Exists(settingsPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, true)]
    public void HideDisabledItems_PreservesDeletedRecoveryItems(bool enabled, bool deleted, bool hideDisabled, bool visible)
    {
        var isVisible = ContextMenuListPresentation.IsVisibleWithDisabledFilter(enabled, deleted, hideDisabled);

        Assert.Equal(visible, isVisible);
    }

    [Fact]
    public void CompactApplicationName_NeverFallsBackToRawIdentityValues()
    {
        var clsidOnly = new ContextMenuEntry { HandlerClsid = "{11111111-1111-1111-1111-111111111111}" };
        var registryOnly = new ContextMenuEntry { RegistryPath = "HKEY_CLASSES_ROOT\\*\\shell\\sample" };
        var command = new ContextMenuEntry { CommandText = "\"C:\\Program Files\\Example\\ExampleApp.exe\" \"%1\"" };

        Assert.Null(ContextMenuApplicationIdentityService.GetFriendlyApplicationName(clsidOnly));
        Assert.Null(ContextMenuApplicationIdentityService.GetFriendlyApplicationName(registryOnly));
        Assert.Equal("ExampleApp", ContextMenuApplicationIdentityService.GetFriendlyApplicationName(command));
    }

    [Fact]
    public void SortOrder_RemainsAttentionDeletedAndDisplayName_WithoutEnabledState()
    {
        Assert.Equal(
            ["SortAttentionWeight", "SortDeletedWeight", "DisplayName"],
            ContextMenuListPresentation.SortPropertyNames);
        Assert.DoesNotContain(ContextMenuListPresentation.SortPropertyNames, propertyName => string.Equals(propertyName, "IsEnabled", StringComparison.Ordinal));
    }
}
