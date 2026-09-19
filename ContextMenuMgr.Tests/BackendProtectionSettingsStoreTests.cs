using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class BackendProtectionSettingsStoreTests
{
    [Fact]
    public async Task SaveFailure_DoesNotTruncatePreviousAuthoritativeValue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ContextMenuMgr.Tests.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "backend-protection-settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new BackendProtectionSettingsStore(path);
            await store.SaveAsync(
                new BackendProtectionSettings { LockNewContextMenuItems = true },
                CancellationToken.None);

            await using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(
                    new BackendProtectionSettings { LockNewContextMenuItems = false },
                    CancellationToken.None));
            }

            var reloaded = await store.LoadAsync(CancellationToken.None);
            Assert.True(reloaded.LockNewContextMenuItems);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
