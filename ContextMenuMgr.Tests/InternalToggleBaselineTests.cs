using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

[Collection("Registry catalog integration")]
public sealed class InternalToggleBaselineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassicToggle_UsesVerifiedPostWriteEntryAsBaseline(bool shellExtension)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"ContextMenuMgr.Tests.{suffix}";
        var relativePath = shellExtension
            ? $@"Software\Classes\*\shellex\ContextMenuHandlers\{name}"
            : $@"Software\Classes\*\shell\{name}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-InternalToggleTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            {
                if (shellExtension)
                {
                    key.SetValue(null, "{11111111-1111-1111-1111-111111111111}");
                }
                else
                {
                    using var command = key.CreateSubKey("command", writable: true)!;
                    command.SetValue(null, "internal-toggle-test.exe \"%1\"");
                }
            }

            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var stateStore = new ContextMenuStateStore(Path.Combine(root, "state.json"), logger);
            var catalog = new ContextMenuRegistryCatalog(
                logger,
                stateStore,
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger));
            var context = new BackendUserContext(
                WindowsIdentity.GetCurrent().User!.Value,
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SessionId: null);

            var baseline = await catalog.GetSnapshotAsync(CancellationToken.None, context);
            var item = Assert.Single(baseline, entry => entry.KeyName == name);
            foreach (var enabled in new[] { false, true })
            {
                var response = await catalog.ApplyDesiredStateAsync(item.Id, enabled, CancellationToken.None, context);
                Assert.True(response.Success, $"RequestedEnabled={enabled}: {response.Message}");
                Assert.Equal(ContextMenuChangeKind.None, response.Item!.DetectedChangeKind);

                var current = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                    entry => entry.Id == item.Id);
                Assert.Equal(enabled, current.IsEnabled);
                Assert.Equal(ContextMenuChangeKind.None, current.DetectedChangeKind);

                var states = await stateStore.LoadAsync(CancellationToken.None);
                var state = states[item.Id];
                Assert.Equal(current.BackendRegistryPath, state.BackendRegistryPath);
                Assert.Equal(enabled, state.DesiredEnabled);
                Assert.Equal(enabled, state.ObservedEnabled);
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            var disabledPath = $@"Software\Classes\*\shellex\-ContextMenuHandlers\{name}";
            Registry.CurrentUser.DeleteSubKeyTree(disabledPath, throwOnMissingSubKey: false);
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* Asynchronous diagnostic logging may still be closing. */ }
            }
        }
    }
}
