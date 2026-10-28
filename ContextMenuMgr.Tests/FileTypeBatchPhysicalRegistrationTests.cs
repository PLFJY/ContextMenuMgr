using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

[Collection("Registry catalog integration")]
public sealed class FileTypeBatchPhysicalRegistrationTests
{
    [Fact]
    public async Task OnDemandDiscovery_KeepsTwoHundredSeventyThreePhysicalRegistrationsDistinct()
    {
        if (!OperatingSystem.IsWindows()) return;
        var suffix = Guid.NewGuid().ToString("N");
        var extensions = Enumerable.Range(0, 91).Select(i => $".cmm{suffix}{i:D2}").ToArray();
        var family = $"cmm{suffix}";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-FileTypeBatch", suffix);
        Directory.CreateDirectory(root);
        try
        {
            foreach (var extension in extensions)
            {
                for (var action = 0; action < 3; action++)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(
                        $@"Software\Classes\SystemFileAssociations\{extension}\shell\{family}{action}_action\command")!;
                    key.SetValue(null, $"\"C:\\Missing\\{suffix}\\tool.exe\" /{action} \"%1\"", RegistryValueKind.String);
                }
            }
            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var catalog = new ContextMenuRegistryCatalog(logger,
                new ContextMenuStateStore(Path.Combine(root, "state.json"), logger),
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger));
            var context = new BackendUserContext(WindowsIdentity.GetCurrent().User!.Value,
                Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), null);
            var sourceId = $@"SystemFileAssociations\{extensions[0]}\shell|{family}0_action";
            var source = (await catalog.FindEntriesByIdAsync(sourceId, CancellationToken.None, context)).Single();
            var found = await catalog.FindRelatedFileTypeMenuItemsAsync(new FileTypeBatchQuery
            {
                SourceItemId = source.Id, EntryKind = source.EntryKind,
                SourceBackendRegistryPath = source.BackendRegistryPath
            }, CancellationToken.None, context);
            Assert.Equal(273, found.Count);
            Assert.Equal(273, found.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(3, found.Select(item => item.KeyName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            foreach (var extension in extensions)
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}", false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GenericLauncherAndSharedTitle_DoNotBecomeStrongApplicationMatches()
    {
        if (!OperatingSystem.IsWindows()) return;
        var suffix = Guid.NewGuid().ToString("N");
        var names = new[] { $".cmm{suffix}a", $".cmm{suffix}b", $".cmm{suffix}c" };
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-FileTypeBatch", suffix);
        Directory.CreateDirectory(root);
        try
        {
            Create(names[0], $"batch{suffix}0_open", $"\"{Environment.SystemDirectory}\\rundll32.exe\" C:\\Missing\\a.dll,Entry");
            Create(names[1], $"batch{suffix}1_print", $"\"{Environment.SystemDirectory}\\rundll32.exe\" C:\\Missing\\b.dll,Entry");
            Create(names[2], $"other{suffix}0_open", "\"C:\\Missing\\other.exe\" \"%1\"");
            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var catalog = new ContextMenuRegistryCatalog(logger,
                new ContextMenuStateStore(Path.Combine(root, "state.json"), logger),
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger));
            var context = new BackendUserContext(WindowsIdentity.GetCurrent().User!.Value,
                Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), null);
            var id = $@"SystemFileAssociations\{names[0]}\shell|batch{suffix}0_open";
            var source = (await catalog.FindEntriesByIdAsync(id, CancellationToken.None, context)).Single();
            var found = await catalog.FindRelatedFileTypeMenuItemsAsync(new FileTypeBatchQuery
            {
                SourceItemId = source.Id, EntryKind = source.EntryKind,
                SourceBackendRegistryPath = source.BackendRegistryPath
            }, CancellationToken.None, context);
            Assert.Contains(found, item => item.KeyName.EndsWith("1_print", StringComparison.Ordinal)
                && item.FileTypeBatchMatchKind == FileTypeBatchMatchKind.RegistrationFamilyCandidate);
            Assert.DoesNotContain(found, item => item.KeyName.StartsWith("other", StringComparison.Ordinal));
            Assert.DoesNotContain(found, item => item.KeyName.EndsWith("1_print", StringComparison.Ordinal)
                && item.FileTypeBatchMatchKind is FileTypeBatchMatchKind.ExactAction or FileTypeBatchMatchKind.SameModule);
        }
        finally
        {
            foreach (var name in names)
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{name}", false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        static void Create(string extension, string verb, string commandText)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{extension}\shell\{verb}")!;
            key.SetValue("MUIVerb", "Shared display title", RegistryValueKind.String);
            using var command = key.CreateSubKey("command")!;
            command.SetValue(null, commandText, RegistryValueKind.String);
        }
    }

    [Fact]
    public async Task BatchDiscoveryAndDeletion_PreservePhysicalActionsAndRejectStaleSource()
    {
        if (!OperatingSystem.IsWindows()) return;
        var suffix = Guid.NewGuid().ToString("N");
        var family = $"     cmmtest{suffix}";
        var extensions = new[] { "jpeg", "png", "bmp", "gif" }
            .Select(extension => $".cmm{suffix}{extension}").ToArray();
        var basePath = @"Software\Classes\SystemFileAssociations";
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-FileTypeBatch", suffix);
        Directory.CreateDirectory(root);
        try
        {
            foreach (var extension in extensions)
            {
                CreateVerb(extension, $"{family}0_open", $"\"C:\\Missing\\{suffix}\\viewer.exe\" \"%1\"");
                CreateVerb(extension, $"{family}1_print", $"\"C:\\Missing\\{suffix}\\viewer.exe\" /print \"%1\"");
                CreateVerb(extension, $"{family}2_compress", $"\"C:\\Missing\\{suffix}\\compress.exe\" \"%1\"");
            }

            var logger = new FileLogger(Path.Combine(root, "backend.log"));
            var catalog = new ContextMenuRegistryCatalog(logger,
                new ContextMenuStateStore(Path.Combine(root, "state.json"), logger),
                new RegistryBackupService(Path.Combine(root, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(root, "protection.json"), logger));
            var context = new BackendUserContext(WindowsIdentity.GetCurrent().User!.Value,
                Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), null);
            var sourceId = $@"SystemFileAssociations\{extensions[0]}\shell|{family}0_open";
            var source = (await catalog.FindEntriesByIdAsync(sourceId, CancellationToken.None, context))
                .Single(item => item.BackendRegistryPath.StartsWith("HKEY_USERS\\", StringComparison.OrdinalIgnoreCase));
            var query = new FileTypeBatchQuery
            {
                SourceItemId = source.Id, EntryKind = source.EntryKind, KeyName = source.KeyName,
                SourceBackendRegistryPath = source.BackendRegistryPath,
                CommandExecutablePath = source.FilePath
            };
            var found = await catalog.FindRelatedFileTypeMenuItemsAsync(query, CancellationToken.None, context);
            Assert.Equal(12, found.Count);
            Assert.Equal(12, found.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(4, found.Count(item => item.FileTypeBatchMatchKind == FileTypeBatchMatchKind.ExactAction));
            Assert.Equal(4, found.Count(item => item.FileTypeBatchMatchKind == FileTypeBatchMatchKind.SameModule));
            Assert.Equal(4, found.Count(item => item.FileTypeBatchMatchKind == FileTypeBatchMatchKind.RegistrationFamilyCandidate));
            Assert.All(found, item => Assert.StartsWith("     ", item.KeyName));

            var stale = found.Single(item => item.RegistryPath.Contains(extensions[1], StringComparison.OrdinalIgnoreCase)
                && item.KeyName.EndsWith("0_open", StringComparison.Ordinal));
            using (var command = Registry.CurrentUser.OpenSubKey($@"{basePath}\{extensions[1]}\shell\{family}0_open\command", true)!)
                command.SetValue(null, "C:\\Changed\\other.exe \"%1\"");
            var rejected = await catalog.DeleteFileTypeBatchItemAsync(stale, CancellationToken.None, context);
            Assert.False(rejected.Success);
            Assert.NotNull(Registry.CurrentUser.OpenSubKey($@"{basePath}\{extensions[1]}\shell\{family}0_open"));

            var target = found.Single(item => item.RegistryPath.Contains(extensions[2], StringComparison.OrdinalIgnoreCase)
                && item.KeyName.EndsWith("1_print", StringComparison.Ordinal));
            var deleted = await catalog.DeleteFileTypeBatchItemAsync(target, CancellationToken.None, context);
            Assert.True(deleted.Success, deleted.Message);
            Assert.True(deleted.Item!.HasBackup);
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"{basePath}\{extensions[2]}\shell\{family}1_print"));
            Assert.NotNull(Registry.CurrentUser.OpenSubKey($@"{basePath}\{extensions[2]}\shell\{family}0_open"));
            Assert.NotNull(Registry.CurrentUser.OpenSubKey($@"{basePath}\{extensions[2]}\shell\{family}2_compress"));
            var restored = await catalog.UndoDeleteAsync(target.Id, CancellationToken.None, context);
            Assert.True(restored.Success, restored.Message);
            Assert.NotNull(Registry.CurrentUser.OpenSubKey($@"{basePath}\{extensions[2]}\shell\{family}1_print"));

            var sourceBatchEntry = found.Single(item => string.Equals(item.BackendRegistryPath,
                source.BackendRegistryPath, StringComparison.OrdinalIgnoreCase));
            Assert.True((await catalog.DeleteFileTypeBatchItemAsync(sourceBatchEntry,
                CancellationToken.None, context)).Success);
            var afterSourceDeletion = await catalog.FindRelatedFileTypeMenuItemsAsync(query, CancellationToken.None, context);
            Assert.Contains(afterSourceDeletion, item => item.Id == sourceBatchEntry.Id
                && item.IsDeleted && item.HasBackup);
            Assert.True((await catalog.UndoDeleteAsync(sourceBatchEntry.Id,
                CancellationToken.None, context)).Success);
        }
        finally
        {
            foreach (var extension in extensions)
                Registry.CurrentUser.DeleteSubKeyTree($@"{basePath}\{extension}", throwOnMissingSubKey: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        static void CreateVerb(string extension, string name, string commandText)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{extension}\shell\{name}\command")!;
            key.SetValue(null, commandText, RegistryValueKind.String);
        }
    }
}
