using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ShellVerbMutationTransactionIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PersistenceOrCancellationFailure_AfterPhysicalWrite_RollsRegistryBack(
        bool cancellationFailure,
        bool quarantinePath)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var suffix = Guid.NewGuid().ToString("N");
        var extension = $".cmtransaction{suffix}";
        var progId = $"ContextMenuMgr.Tests.Transaction.{suffix}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-TransactionTests", suffix);
        Directory.CreateDirectory(root);
        try
        {
            using (var extensionKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}", writable: true)!)
            {
                extensionKey.SetValue(null, progId, RegistryValueKind.String);
            }
            using (var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\print", writable: true)!)
            {
                verb.SetValue("CommandFlags", 0x48, RegistryValueKind.DWord);
            }
            using (var command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\print\command", writable: true)!)
            {
                command.SetValue(null, "transaction-test.exe \"%1\"", RegistryValueKind.String);
            }

            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var stateStore = new ContextMenuStateStore(Path.Combine(root, "state.json"), logger);
            var catalog = new ContextMenuRegistryCatalog(
                logger,
                stateStore,
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger));
            var userContext = new BackendUserContext(
                sid,
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SessionId: null);

            // Establish normal state before arming the post-write save fault.
            _ = await catalog.GetSnapshotAsync(CancellationToken.None, userContext);
            var scene = await catalog.GetSceneSnapshotAsync(
                ContextMenuSceneKind.CustomExtension,
                extension,
                CancellationToken.None,
                userContext);
            var item = Assert.Single(scene, entry =>
                entry.EntryKind == ContextMenuEntryKind.ShellVerb
                && string.Equals(entry.KeyName, "print", StringComparison.OrdinalIgnoreCase));

            stateStore.BeforeSaveAsync = (_, _) => cancellationFailure
                ? Task.FromException(new OperationCanceledException("Injected post-write cancellation."))
                : Task.FromException(new IOException("Injected state-store failure."));

            if (quarantinePath)
            {
                var exception = await Assert.ThrowsAsync<ShellVerbMutationException>(() =>
                    catalog.QuarantineNewItemAsync(item, CancellationToken.None, userContext));
                Assert.Equal(PipeErrorCodes.RegistryMutationRolledBack, exception.ErrorCode);
            }
            else
            {
                var response = await catalog.ApplyDesiredStateAsync(
                    item.Id,
                    enable: false,
                    CancellationToken.None,
                    userContext,
                    item);

                Assert.False(response.Success);
                Assert.Equal(PipeErrorCodes.RegistryMutationRolledBack, response.ErrorCode);
            }
            using var restored = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\shell\print")!;
            Assert.Null(restored.GetValue("ProgrammaticAccessOnly"));
            Assert.Equal(0x48, Convert.ToInt32(restored.GetValue("CommandFlags")));
            using var commandAfter = restored.OpenSubKey("command")!;
            Assert.Equal("transaction-test.exe \"%1\"", commandAfter.GetValue(null));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{extension}", throwOnMissingSubKey: false);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
