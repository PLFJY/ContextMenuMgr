using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: only physical absence in the authenticated source can remove a cached row.
[Collection("Registry catalog integration")]
public sealed class MenuItemPresenceTests
{
    [Fact]
    public async Task ClassicMissingSourceIsExplicit_WhileDeniedIncompleteAndForeignSourcesAreNotRemoval()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var classesRoot = $@"ContextMenuMgr.Tests.Presence.{suffix}\shell";
        var relative = $@"Software\Classes\{classesRoot}\verb";
        var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Presence-" + suffix);
        Directory.CreateDirectory(directory);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var user = new BackendUserContext(sid, Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), null);
        var logger = new FileLogger(Path.Combine(directory, "backend.log"));
        var catalog = new ContextMenuRegistryCatalog(logger,
            new ContextMenuStateStore(Path.Combine(directory, "state.json"), logger),
            new RegistryBackupService(Path.Combine(directory, "backups"), logger),
            new BackendProtectionSettingsStore(Path.Combine(directory, "protection.json"), logger));
        var original = new ContextMenuEntry
        {
            Id = classesRoot + "|verb", EntryKind = ContextMenuEntryKind.ShellVerb,
            BackendRegistryPath = $@"HKEY_USERS\{sid}\{relative}", SourceRootPath = classesRoot,
            KeyName = "verb", DisplayName = "Presence test", IsEnabled = true
        };
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relative)!)
            using (var command = key.CreateSubKey("command")!) command.SetValue(null, "presence-test.exe \"%1\"");
            Assert.True((await catalog.GetContextMenuItemStateAsync(original.Id, original, default, user)).Success);

            using (var held = Registry.CurrentUser.OpenSubKey(relative, RegistryKeyPermissionCheck.ReadWriteSubTree,
                       RegistryRights.ReadKey | RegistryRights.ChangePermissions)!)
            {
                var before = held.GetAccessControl();
                var denied = new RegistrySecurity();
                denied.SetSecurityDescriptorBinaryForm(before.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                denied.AddAccessRule(new RegistryAccessRule(identity.User!, RegistryRights.ReadKey, AccessControlType.Deny));
                try
                {
                    held.SetAccessControl(denied);
                    var failure = await Record.ExceptionAsync(() => catalog.GetContextMenuItemStateAsync(original.Id, original, default, user));
                    Assert.True(failure is UnauthorizedAccessException or System.Security.SecurityException, failure?.ToString());
                }
                finally
                {
                    var restore = new RegistrySecurity();
                    restore.SetSecurityDescriptorBinaryForm(before.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    held.SetAccessControl(restore);
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(relative);
            var missing = await catalog.GetContextMenuItemStateAsync(original.Id, original, default, user);
            Assert.False(missing.Success);
            Assert.Equal(PipeErrorCodes.MenuItemRemoved, missing.ErrorCode);
            Assert.Equal(new[] { original.Id }, missing.RemovedItemIds);

            using (MenuDiscoveryCoverage.Begin())
            {
                MenuDiscoveryCoverage.MarkIncomplete();
                Assert.NotEqual(PipeErrorCodes.MenuItemRemoved,
                    (await catalog.GetContextMenuItemStateAsync(original.Id, original, default, user)).ErrorCode);
            }
            var foreign = original with { BackendRegistryPath = $@"HKEY_USERS\S-1-5-21-1-2-3-9999\{relative}" };
            Assert.NotEqual(PipeErrorCodes.MenuItemRemoved,
                (await catalog.GetContextMenuItemStateAsync(original.Id, foreign, default, user)).ErrorCode);
            Assert.NotEqual(PipeErrorCodes.MenuItemRemoved,
                (await catalog.GetContextMenuItemStateAsync(original.Id, foreign, default,
                    user with { Sid = "S-1-5-21-1-2-3-9999" })).ErrorCode);
            Assert.NotEqual(PipeErrorCodes.MenuItemRemoved,
                (await catalog.GetContextMenuItemStateAsync(original.Id, original with { IsDeleted = true }, default, user)).ErrorCode);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\ContextMenuMgr.Tests.Presence.{suffix}", false);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SendToMissingItemIsExplicit_ButUnavailableProfileAndOutsideRootAreRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-SendToPresence-" + Guid.NewGuid().ToString("N"));
        var user = new BackendUserContext(WindowsIdentity.GetCurrent().User!.Value, Environment.UserName,
            directory, directory, directory, null);
        var sendTo = user.GetSendToPath();
        Directory.CreateDirectory(sendTo);
        var service = new SpecialMenuService(new FileLogger(Path.Combine(directory, "backend.log")));
        var path = Path.Combine(sendTo, "missing.txt");
        var entry = new SpecialMenuEntry { Id = "SendTo:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path)), Kind = SpecialMenuKind.SendTo, Path = path };
        try
        {
            var result = await service.GetItemStateAsync(entry, user, default);
            Assert.Equal(PipeErrorCodes.MenuItemRemoved, result.ErrorCode);
            Directory.Delete(sendTo);
            await Assert.ThrowsAnyAsync<IOException>(() => service.GetItemStateAsync(entry, user, default));
            Directory.CreateDirectory(sendTo);
            var outside = Path.Combine(directory, "other.txt");
            var forged = entry with { Id = "SendTo:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(outside)), Path = outside };
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetItemStateAsync(forged, user, default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
