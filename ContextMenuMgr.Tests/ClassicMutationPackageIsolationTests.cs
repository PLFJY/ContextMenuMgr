using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

[Collection("Registry catalog integration")]
public sealed class ClassicMutationPackageIsolationTests
{
    [Fact]
    public async Task ClassicToggleAndTargetedRead_DoNotDiscoverPackages_ButGlobalSnapshotDoes()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var suffix = Guid.NewGuid().ToString("N");
        var keyName = $"ContextMenuMgrTest{suffix}";
        var relativePath = $@"Software\Classes\*\shell\{keyName}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-ClassicIsolation", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath)!)
            using (var command = key.CreateSubKey("command")!)
                command.SetValue(null, "cmd.exe /c exit", RegistryValueKind.String);

            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var store = new ContextMenuStateStore(Path.Combine(root, "state.json"), logger);
            var scans = 0;
            var packageCache = new PackagedContextMenuScanCache(_ =>
            {
                Interlocked.Increment(ref scans);
                return [];
            });
            var catalog = new ContextMenuRegistryCatalog(logger, store,
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger),
                new WindowsRegistryProtectionTargetAccessor(),
                new Windows11ContextMenuCatalog(logger, packageCache));
            var context = new BackendUserContext(WindowsIdentity.GetCurrent().User!.Value,
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), null);
            var itemId = $@"*\shell|{keyName}";
            var response = await catalog.ApplyDesiredStateAsync(itemId, false, CancellationToken.None, context);
            Assert.True(response.Success, response.Message);
            Assert.False(response.Item!.IsEnabled);
            Assert.Equal(0, scans);

            var state = await catalog.GetContextMenuItemStateAsync(itemId, response.Item, CancellationToken.None, context);
            Assert.True(state.Success);
            Assert.False(state.Item!.IsEnabled);
            Assert.Equal(0, scans);

            var wrongSource = await catalog.GetContextMenuItemStateAsync(itemId,
                response.Item! with
                {
                    BackendRegistryPath = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shell\{keyName}"
                }, CancellationToken.None, context);
            Assert.True(wrongSource.Success);
            Assert.Null(wrongSource.Item); // Do not silently switch to the HKU copy.

            _ = await catalog.GetSnapshotAsync(CancellationToken.None, context);
            Assert.Equal(1, scans);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
