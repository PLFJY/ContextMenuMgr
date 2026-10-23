using System.Security.Principal;
using System.Security.AccessControl;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

[Collection("Registry catalog integration")]
public sealed class ShellVerbMutationTransactionIntegrationTests
{
    // Permanent: real registry ACLs must distinguish DELETE from value-write
    // access, without bypassing application protection or losing recovery data.
    [Theory]
    [InlineData(RegistryRights.SetValue, false)]
    [InlineData(RegistryRights.Delete, false)]
    [InlineData(RegistryRights.SetValue, true)]
    public async Task Delete_UsesDeleteRights_AndCommitsOnlyVerifiedRemoval(
        RegistryRights deniedRights, bool protectionEnabled)
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var suffix = Guid.NewGuid().ToString("N");
        var progId = $"ContextMenuMgr.Tests.Delete.{suffix}";
        var relativePath = $@"Software\Classes\{progId}";
        var physicalPath = $@"HKEY_USERS\{sid.Value}\{relativePath}\shell\print";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-DeleteTests", suffix);
        Directory.CreateDirectory(root);
        using var target = Registry.CurrentUser.CreateSubKey($@"{relativePath}\shell\print", writable: true)!;
        var originalSecurity = target.GetAccessControl();
        try
        {
            target.SetValue(null, "Delete test");
            using (var command = target.CreateSubKey("command", writable: true)!)
                command.SetValue(null, "delete-test.exe \"%1\"");
            using (var sibling = Registry.CurrentUser.CreateSubKey($@"{relativePath}\shell\sibling", writable: true)!)
                sibling.SetValue(null, "untouched");

            var security = target.GetAccessControl();
            security.AddAccessRule(new RegistryAccessRule(sid, deniedRights,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Deny));
            target.SetAccessControl(security);

            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var stateStore = new ContextMenuStateStore(Path.Combine(root, "state.json"), logger);
            var protectionStore = new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger);
            await protectionStore.SaveAsync(new BackendProtectionSettings
                { LockNewContextMenuItems = protectionEnabled }, CancellationToken.None);
            var catalog = new ContextMenuRegistryCatalog(logger, stateStore,
                new RegistryBackupService(Path.Combine(root, "backups"), logger), protectionStore);
            var item = new ContextMenuEntry
            {
                Id = $@"{progId}\shell|print",
                KeyName = "print",
                DisplayName = "Delete test",
                Category = ContextMenuCategory.File,
                EntryKind = ContextMenuEntryKind.ShellVerb,
                RegistryPath = $@"{progId}\shell\print",
                SourceRootPath = $@"{progId}\shell",
                BackendRegistryPath = physicalPath,
                IsPresentInRegistry = true,
                IsEnabled = true
            };
            var context = new BackendUserContext(sid.Value, Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), SessionId: null);

            var response = await catalog.DeleteItemAsync(item.Id, CancellationToken.None, context, item);
            var shouldDelete = deniedRights != RegistryRights.Delete && !protectionEnabled;
            Assert.Equal(shouldDelete, response.Success);
            using var actual = Registry.CurrentUser.OpenSubKey($@"{relativePath}\shell\print");
            var states = await stateStore.LoadAsync(CancellationToken.None);
            if (shouldDelete)
            {
                Assert.Null(actual);
                Assert.True(response.Item!.IsDeleted);
                Assert.True(states[item.Id].IsDeleted);
                Assert.True(File.Exists(states[item.Id].BackupFilePath));
                var backup = await File.ReadAllTextAsync(states[item.Id].BackupFilePath!);
                Assert.Contains(physicalPath, backup);
                Assert.Contains("delete-test.exe", backup);
            }
            else
            {
                Assert.NotNull(actual);
                Assert.Equal("Delete test", actual.GetValue(null));
                using var command = actual.OpenSubKey("command");
                Assert.Equal("delete-test.exe \"%1\"", command!.GetValue(null));
                Assert.False(states.GetValueOrDefault(item.Id)?.IsDeleted ?? false);
                Assert.Equal(protectionEnabled ? PipeErrorCodes.RegistryWriteProtectionEnabled
                    : PipeErrorCodes.RegistryMutationVerificationFailed, response.ErrorCode);
                if (protectionEnabled)
                    Assert.Empty(Directory.GetFiles(Path.Combine(root, "backups")));
            }
            using var preservedSibling = Registry.CurrentUser.OpenSubKey($@"{relativePath}\shell\sibling");
            Assert.Equal("untouched", preservedSibling!.GetValue(null));
        }
        finally
        {
            using (var remaining = Registry.CurrentUser.OpenSubKey($@"{relativePath}\shell\print"))
            {
                if (remaining is not null)
                {
                    // A freshly loaded RegistrySecurity has no dirty sections;
                    // explicitly mark the saved DACL for restoration.
                    originalSecurity.SetSecurityDescriptorBinaryForm(
                        originalSecurity.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    target.SetAccessControl(originalSecurity);
                }
            }
            target.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceFailure_AfterPhysicalWrite_RollsRegistryBack(
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

            // Establish state, then deny replacement of the authoritative file while allowing snapshot reads.
            _ = await catalog.GetSnapshotAsync(CancellationToken.None, userContext);
            var scene = await catalog.GetSceneSnapshotAsync(
                ContextMenuSceneKind.CustomExtension,
                extension,
                CancellationToken.None,
                userContext);
            var item = Assert.Single(scene, entry =>
                entry.EntryKind == ContextMenuEntryKind.ShellVerb
                && string.Equals(entry.KeyName, "print", StringComparison.OrdinalIgnoreCase));

            using var stateLock = new FileStream(Path.Combine(root, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);

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
