using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

[Collection("Registry catalog integration")]
public sealed class InternalToggleBaselineTests
{
    [Fact]
    public async Task Monitor_StartupReenable_RemainsModifiedWithoutSilentCorrection()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"ContextMenuMgr.Tests.Offline.{suffix}";
        var relativePath = $@"Software\Classes\*\shell\{name}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-OfflineDriftTests", suffix);
        Directory.CreateDirectory(root);
        ContextMenuRegistryMonitor? monitor = null;
        using var stop = new CancellationTokenSource();
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            using (var command = key.CreateSubKey("command", writable: true)!)
            {
                command.SetValue(null, "offline-test.exe \"%1\"");
            }

            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var stateStore = new ContextMenuStateStore(Path.Combine(root, "state.json"), logger);
            var catalog = new ContextMenuRegistryCatalog(
                logger, stateStore,
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger));
            var context = new BackendUserContext(
                WindowsIdentity.GetCurrent().User!.Value,
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SessionId: null);
            var item = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                entry => entry.KeyName == name);
            Assert.True((await catalog.ApplyDesiredStateAsync(
                item.Id, false, CancellationToken.None, context)).Success);

            // The registry changes while the monitor is stopped.
            using (var key = Registry.CurrentUser.OpenSubKey(relativePath, writable: true)!)
            {
                key.DeleteValue("ProgrammaticAccessOnly", throwOnMissingValue: false);
            }

            monitor = new ContextMenuRegistryMonitor(catalog, logger, () => context, TimeSpan.FromMilliseconds(250));
            monitor.Start(stop.Token);
            await monitor.BaselineEstablished.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(750);

            var offline = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                entry => entry.Id == item.Id);
            Assert.True(offline.IsEnabled);
            Assert.Equal(ContextMenuChangeKind.Modified, offline.DetectedChangeKind);
        }
        finally
        {
            stop.Cancel();
            if (monitor is not null)
            {
                await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* Asynchronous diagnostic logging may still be closing. */ }
            }
        }
    }

    [Fact]
    public async Task Monitor_RetriesRuntimeQuarantineAfterWriteFailure()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"ContextMenuMgr.Tests.Retry.{suffix}";
        var relativePath = $@"Software\Classes\*\shell\{name}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-MonitorRetryTests", suffix);
        Directory.CreateDirectory(root);
        ContextMenuRegistryMonitor? monitor = null;
        using var stop = new CancellationTokenSource();
        try
        {
            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var stateStore = new ContextMenuStateStore(Path.Combine(root, "state.json"), logger);
            var protection = new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger);
            var catalog = new ContextMenuRegistryCatalog(
                logger, stateStore,
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                protection);
            var context = new BackendUserContext(
                WindowsIdentity.GetCurrent().User!.Value,
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SessionId: null);
            monitor = new ContextMenuRegistryMonitor(catalog, logger, () => context, TimeSpan.FromMilliseconds(500));
            var detections = 0;
            var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.ItemDetected += async (_, detected) =>
            {
                Interlocked.Increment(ref detections);
                try
                {
                    await catalog.QuarantineNewItemAsync(detected.Item, CancellationToken.None, detected.UserContext);
                }
                catch (ShellVerbMutationException)
                {
                    // The first attempt is deliberately blocked by preflight.
                    firstFailure.TrySetResult();
                }
            };
            monitor.Start(stop.Token);
            await monitor.BaselineEstablished.WaitAsync(TimeSpan.FromSeconds(15));

            await protection.SaveAsync(
                new BackendProtectionSettings { LockNewContextMenuItems = true },
                CancellationToken.None);
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            using (var command = key.CreateSubKey("command", writable: true)!)
            {
                command.SetValue(null, "retry-test.exe \"%1\"");
            }

            await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await protection.SaveAsync(
                new BackendProtectionSettings { LockNewContextMenuItems = false },
                CancellationToken.None);

            var committed = false;
            var retryDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < retryDeadline)
            {
                var states = await stateStore.LoadAsync(CancellationToken.None);
                var state = states.Values.FirstOrDefault(entry => entry.KeyName == name);
                using var key = Registry.CurrentUser.OpenSubKey(relativePath);
                if (state?.IsPendingApproval == true
                    && key?.GetValue("ProgrammaticAccessOnly") is not null)
                {
                    committed = true;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(committed, "The failed quarantine was not retried and committed.");
            Assert.True(Volatile.Read(ref detections) >= 2);
        }
        finally
        {
            stop.Cancel();
            if (monitor is not null)
            {
                await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* Asynchronous diagnostic logging may still be closing. */ }
            }
        }
    }

    [Fact]
    public async Task Monitor_RecreatedVerbBeforeNextPoll_IsSilentlyDisabled()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"ContextMenuMgr.Tests.FastRecreated.{suffix}";
        var relativePath = $@"Software\Classes\*\shell\{name}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-MonitorRecreationTests", suffix);
        Directory.CreateDirectory(root);
        ContextMenuRegistryMonitor? monitor = null;
        using var stop = new CancellationTokenSource();
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            using (var command = key.CreateSubKey("command", writable: true)!)
            {
                command.SetValue(null, "first-version.exe \"%1\"");
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

            monitor = new ContextMenuRegistryMonitor(catalog, logger, () => context, TimeSpan.FromSeconds(8));
            monitor.Start(stop.Token);
            await monitor.BaselineEstablished.WaitAsync(TimeSpan.FromSeconds(15));
            var item = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                entry => entry.KeyName == name);
            var disabled = await catalog.ApplyDesiredStateAsync(item.Id, false, CancellationToken.None, context);
            Assert.True(disabled.Success, disabled.Message);

            // Recreate the key before the monitor's next settled observation.
            Registry.CurrentUser.DeleteSubKeyTree(relativePath);
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            using (var command = key.CreateSubKey("command", writable: true)!)
            {
                command.SetValue(null, "second-version.exe \"%1\"");
            }

            var corrected = false;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            while (DateTimeOffset.UtcNow < deadline)
            {
                using var key = Registry.CurrentUser.OpenSubKey(relativePath);
                if (key?.GetValue("ProgrammaticAccessOnly") is not null)
                {
                    corrected = true;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(corrected, "The monitor did not re-disable the recreated menu item.");
            var snapshot = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                entry => entry.Id == item.Id);
            Assert.False(snapshot.IsEnabled);
            Assert.Equal(ContextMenuChangeKind.None, snapshot.DetectedChangeKind);
        }
        finally
        {
            stop.Cancel();
            if (monitor is not null)
            {
                await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* Asynchronous diagnostic logging may still be closing. */ }
            }
        }
    }

    [Fact]
    public async Task RecreatedVisibleShellVerb_WithChangedCommand_IsSilentlyDisabledAgain()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"ContextMenuMgr.Tests.Recreated.{suffix}";
        var relativePath = $@"Software\Classes\*\shell\{name}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-RecreatedVerbTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            using (var command = key.CreateSubKey("command", writable: true)!)
            {
                command.SetValue(null, "first-version.exe \"%1\"");
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

            var initial = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                entry => entry.KeyName == name);
            var disable = await catalog.ApplyDesiredStateAsync(initial.Id, false, CancellationToken.None, context);
            Assert.True(disable.Success, disable.Message);

            // The other application replaces its visible registration between
            // monitor polls, retaining the stable menu Id but changing its
            // physical generation and removing our visibility marker.
            Registry.CurrentUser.DeleteSubKeyTree(relativePath);
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath, writable: true)!)
            using (var command = key.CreateSubKey("command", writable: true)!)
            {
                command.SetValue(null, "second-version.exe \"%1\"");
            }

            var observed = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                entry => entry.Id == initial.Id);
            Assert.True(observed.IsEnabled);
            Assert.Equal(ContextMenuChangeKind.Modified, observed.DetectedChangeKind);

            var reconciliation = await catalog.ReconcilePersistedDisabledItemsAsync(
                [observed], CancellationToken.None, context);
            Assert.Contains(initial.Id, reconciliation.ReconciledItemIds);
            Assert.Empty(reconciliation.FailedItemIds);

            var corrected = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                entry => entry.Id == initial.Id);
            Assert.False(corrected.IsEnabled);
            Assert.Equal(ContextMenuChangeKind.None, corrected.DetectedChangeKind);
            var state = (await stateStore.LoadAsync(CancellationToken.None))[initial.Id];
            Assert.False(state.DesiredEnabled);
            Assert.False(state.ObservedEnabled);
            Assert.Single(state.ShellVerbVisibilityProvenance);
            using var current = Registry.CurrentUser.OpenSubKey(relativePath)!;
            using var currentCommand = current.OpenSubKey("command")!;
            Assert.Equal("second-version.exe \"%1\"", currentCommand.GetValue(null));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { /* Asynchronous diagnostic logging may still be closing. */ }
            }
        }
    }

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

            Assert.DoesNotContain(await catalog.GetReadOnlySnapshotAsync(CancellationToken.None),
                entry => entry.KeyName == name);
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

            if (!shellExtension)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(relativePath, writable: true)!)
                {
                    key.SetValue("ProgrammaticAccessOnly", "external-disable");
                }

                var changed = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                    entry => entry.Id == item.Id);
                Assert.Equal(ContextMenuChangeKind.Modified, changed.DetectedChangeKind);
                var missingContext = await catalog.AcknowledgeItemStateAsync(item.Id, CancellationToken.None);
                Assert.False(missingContext.Success);
                var acknowledged = await catalog.AcknowledgeItemStateAsync(item.Id, CancellationToken.None, context);
                Assert.True(acknowledged.Success, acknowledged.Message);
                var accepted = Assert.Single(await catalog.GetSnapshotAsync(CancellationToken.None, context),
                    entry => entry.Id == item.Id);
                Assert.Equal(ContextMenuChangeKind.None, accepted.DetectedChangeKind);
                Assert.False((await stateStore.LoadAsync(CancellationToken.None))[item.Id].DesiredEnabled);
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
