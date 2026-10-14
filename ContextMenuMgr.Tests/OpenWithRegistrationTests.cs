using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: real registry read-back, provenance, recovery conflicts and SID isolation.
[Collection("Registry catalog integration")]
public sealed class OpenWithRegistrationTests
{
    // Permanent: newly discovered structures must never acquire unsafe mutation capabilities.
    [Theory]
    [InlineData("MissingShell")]
    [InlineData("DropTarget")]
    [InlineData("DelegateExecute")]
    [InlineData("InvalidCommandType")]
    public async Task NonstandardApplications_AreDiscoveredAsReadOnlyAndRejectMutations(string structure)
    {
        using var fixture = new Fixture();
        fixture.Create(complete: structure != "MissingShell");
        using (var key = fixture.Open(writable: true))
        {
            key!.SetValue("FriendlyAppName", "Nonstandard application");
            if (structure == "DropTarget")
            {
                key.DeleteSubKeyTree(@"shell\open\command");
                using var drop = key.CreateSubKey(@"shell\open\DropTarget");
                drop.SetValue("CLSID", "{00000000-0000-0000-0000-000000000001}");
            }
            if (structure == "DelegateExecute")
            {
                using var command = key.OpenSubKey(@"shell\open\command", writable: true);
                command!.SetValue("DelegateExecute", "{00000000-0000-0000-0000-000000000001}");
            }
            if (structure == "InvalidCommandType")
            {
                using var command = key.OpenSubKey(@"shell\open\command", writable: true);
                command!.SetValue(null, new byte[] { 1, 2 }, RegistryValueKind.Binary);
            }
        }
        var item = await fixture.Read();
        Assert.False(item.CanEdit);
        Assert.False(item.CanDelete);
        Assert.Equal("User", item.Metadata["RegistryScope"]);
        Assert.Equal("OpenWithUnsupportedRegistration", item.Metadata["ReadOnlyReason"]);
        Assert.False((await fixture.Service.SetEnabledAsync(item, false, null, fixture.Context, default)).Success);
        // A caller cannot grant itself permissions by changing the DTO flags.
        Assert.False((await fixture.Service.DeleteAsync(item with { CanDelete = true }, null, fixture.Context, default)).Success);
        Assert.False((await fixture.Service.UpdateAsync(new PipeRequest { SpecialKind = SpecialMenuKind.OpenWith,
            OpenWithUpdate = new(item.Id, "Unsafe edit", ExpectedEditStamp: "forged") }, fixture.Context, default)).Success);
        using var actual = fixture.Open();
        Assert.Equal("Nonstandard application", actual!.GetValue("FriendlyAppName"));
        Assert.Null(actual.GetValue("NoOpenWith"));
    }

    // Permanent: both fields are one operation, including invalid input and cancellation after staging.
    [Fact]
    public async Task UpdateValidationAndTransactionAbort_PreserveNameRawCommandAndAcl()
    {
        using var fixture = new Fixture();
        fixture.Create();
        using (var key = fixture.Open(writable: true)) key!.SetValue("FriendlyAppName", "Before");
        var before = await fixture.Read();
        Assert.Equal("Before", before.DisplayName);
        var acl = fixture.Acl();
        // Expanding the original target to the service's environment must not authorize a new literal target.
        var expandedTarget = Environment.ExpandEnvironmentVariables(before.CommandText!);
        foreach (var invalid in new[] { "\"C:\\different.exe\" \"%1\"", "\"unterminated", "   ", expandedTarget })
        {
            var response = await fixture.Service.UpdateAsync(new PipeRequest
            {
                SpecialKind = SpecialMenuKind.OpenWith,
                OpenWithUpdate = new(before.Id, "Must not persist", invalid, before.Metadata["EditStamp"])
            }, fixture.Context, default);
            Assert.False(response.Success);
            using var actual = fixture.Open();
            Assert.Equal("Before", actual!.GetValue("FriendlyAppName"));
        }
        Assert.Throws<OperationCanceledException>(() => OpenWithRegistration.Update(fixture.Path, @"shell\open\command",
            "Canceled", before.CommandText + " /test", before.Metadata["EditStamp"], before.CommandText, new CancellationToken(true)));
        using (var key = fixture.Open()) Assert.Equal("Before", key!.GetValue("FriendlyAppName"));
        var updated = await fixture.Service.UpdateAsync(new PipeRequest
        {
            SpecialKind = SpecialMenuKind.OpenWith,
            OpenWithUpdate = new(before.Id, "After", before.CommandText + " /test", before.Metadata["EditStamp"])
        }, fixture.Context, default);
        Assert.True(updated.Success, updated.Message);
        Assert.Equal("After", updated.SpecialItem!.DisplayName);
        using (var key = fixture.Open())
        using (var command = key!.OpenSubKey(@"shell\open\command"))
        {
            Assert.Equal("After", key.GetValue("FriendlyAppName"));
            Assert.Equal(before.CommandText + " /test", command!.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(RegistryValueKind.ExpandString, command.GetValueKind(string.Empty));
            fixture.AssertData(key);
        }
        Assert.Equal(acl, fixture.Acl());
        Assert.False((await fixture.Service.UpdateAsync(new PipeRequest { SpecialKind = SpecialMenuKind.OpenWith,
            OpenWithUpdate = new(before.Id, "Stale", before.CommandText, before.Metadata["EditStamp"]) }, fixture.Context, default)).Success);
    }

    [Fact]
    public void CreateTransaction_CanceledAfterStagingLeavesNoKeyAndExistingKeysAreNeverOverwritten()
    {
        using var fixture = new Fixture();
        Assert.Throws<OperationCanceledException>(() => OpenWithRegistration.Create(fixture.Path, "Canceled",
            "\"C:\\test.exe\" \"%1\"", new CancellationToken(true)));
        Assert.Null(fixture.Open());
        OpenWithRegistration.Create(fixture.Path, "Created", "\"C:\\test.exe\" \"%1\"", default);
        Assert.Throws<InvalidOperationException>(() => OpenWithRegistration.Create(fixture.Path, "Overwrite", "other.exe", default));
        using var actual = fixture.Open();
        using var command = actual!.OpenSubKey(@"shell\open\command");
        Assert.Equal("Created", actual.GetValue("FriendlyAppName"));
        Assert.Equal("\"C:\\test.exe\" \"%1\"", command!.GetValue(null));
    }

    [Fact]
    public async Task ToggleAndDeleteRestoreCycles_ReturnPhysicalStateAndPreserveRawValuesAndAcl()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var item = await fixture.Read();
        var initialAcl = fixture.Acl();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var disabled = await fixture.Service.SetEnabledAsync(item, false, null, fixture.Context, default);
            Assert.True(disabled.Success, disabled.Message);
            item = Assert.IsType<SpecialMenuEntry>(disabled.SpecialItem);
            Assert.False(item.IsEnabled);
            using (var key = fixture.Open())
            {
                Assert.Equal(string.Empty, key!.GetValue("NoOpenWith"));
                Assert.StartsWith("ContextMenuMgrPlus.OpenWith.v1:", Assert.IsType<string>(key.GetValue("ContextMenuMgrPlus.OpenWithVisibility")));
                fixture.AssertData(key);
            }
            var deleted = await fixture.Service.DeleteAsync(item, null, fixture.Context, default);
            Assert.True(deleted.Success, deleted.Message);
            Assert.Null(fixture.Open());
            item = Assert.IsType<SpecialMenuEntry>(deleted.SpecialItem);
            Assert.Equal(fixture.Path + ".deleted", item.Metadata["DeletedPath"]);
            Assert.False(item.IsEnabled);
            using (var backup = fixture.Open(".deleted")) fixture.AssertData(backup!);
            Assert.Equal(initialAcl, fixture.Acl(".deleted"));
            item = await fixture.Read(".deleted"); // Same recovery after a refresh/service instance change.
            var restored = await fixture.Service.UndoDeleteAsync(item, null, fixture.Context, default);
            Assert.True(restored.Success, restored.Message);
            Assert.Null(fixture.Open(".deleted"));
            item = Assert.IsType<SpecialMenuEntry>(restored.SpecialItem);
            Assert.False(item.IsEnabled);
            var enabled = await fixture.Service.SetEnabledAsync(item, true, null, fixture.Context, default);
            Assert.True(enabled.Success, enabled.Message);
            item = Assert.IsType<SpecialMenuEntry>(enabled.SpecialItem);
            Assert.True(item.IsEnabled);
            using var keyAfter = fixture.Open();
            Assert.DoesNotContain("NoOpenWith", keyAfter!.GetValueNames(), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("ContextMenuMgrPlus.OpenWithVisibility", keyAfter.GetValueNames(), StringComparer.OrdinalIgnoreCase);
            fixture.AssertData(keyAfter);
            Assert.Equal(initialAcl, fixture.Acl());
        }
    }

    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.Binary)]
    public async Task PreexistingNoOpenWith_IsPreservedOnDisableAndCannotBeRemovedOnEnable(RegistryValueKind kind)
    {
        using var fixture = new Fixture();
        fixture.Create();
        using (var key = fixture.Open(writable: true))
            key!.SetValue("NoOpenWith", kind switch
            {
                RegistryValueKind.DWord => 0,
                RegistryValueKind.Binary => new byte[] { 7, 8 },
                _ => string.Empty
            }, kind);
        var item = await fixture.Read();
        using var original = fixture.Open();
        Assert.Equal("Unknown", item.Metadata["VisibilityOwnership"]);
        var before = ShellVerbVisibility.CaptureValue(original!, "NoOpenWith");
        var disable = await fixture.Service.SetEnabledAsync(item, false, null, fixture.Context, default);
        Assert.True(disable.Success, disable.Message);
        var enable = await fixture.Service.SetEnabledAsync(disable.SpecialItem!, true, null, fixture.Context, default);
        Assert.False(enable.Success);
        using var after = fixture.Open();
        Assert.True(ShellVerbVisibility.ValueMatches(after!, before));
        Assert.False((await fixture.Read()).IsEnabled);
    }

    [Fact]
    public async Task ThirdPartyVisibilityOrCommandChange_RejectsStaleRequestsAndPreservesNewState()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var old = await fixture.Read();
        using (var key = fixture.Open(writable: true)) key!.SetValue("NoOpenWith", "third-party", RegistryValueKind.String);
        var failed = await fixture.Service.SetEnabledAsync(old, true, null, fixture.Context, default);
        Assert.False(failed.Success);
        using (var key = fixture.Open()) Assert.Equal("third-party", key!.GetValue("NoOpenWith"));
        using (var key = fixture.Open(writable: true)) key!.DeleteValue("NoOpenWith");
        var disabled = await fixture.Service.SetEnabledAsync(await fixture.Read(), false, null, fixture.Context, default);
        Assert.True(disabled.Success, disabled.Message);
        using (var key = fixture.Open(writable: true))
        using (var command = key!.OpenSubKey(@"shell\open\command", writable: true))
            command!.SetValue(null, "changed-application.exe \"%1\"");
        failed = await fixture.Service.SetEnabledAsync(disabled.SpecialItem!, true, null, fixture.Context, default);
        Assert.False(failed.Success);
        // Even a refreshed request cannot use old provenance on a replacement command generation.
        failed = await fixture.Service.SetEnabledAsync(await fixture.Read(), true, null, fixture.Context, default);
        Assert.False(failed.Success);
        using var after = fixture.Open();
        Assert.NotNull(after!.GetValue("NoOpenWith"));
    }

    [Fact]
    public async Task DeleteCollisionAndUndoCollision_DoNotOverwriteRegistrationsOrPartialBackups()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var item = await fixture.Read();
        fixture.Create(".deleted", complete: false);
        var failure = await fixture.Service.DeleteAsync(item, null, fixture.Context, default);
        Assert.False(failure.Success);
        var incomplete = await fixture.Read(".deleted");
        Assert.Equal("True", incomplete.Metadata["IsDeleted"]);
        Assert.False(incomplete.CanDelete);
        Assert.False((await fixture.Service.UndoDeleteAsync(incomplete, null, fixture.Context, default)).Success);
        Assert.False((await fixture.Service.PurgeDeletedAsync(incomplete, null, fixture.Context, default)).Success);
        using (var source = fixture.Open()) fixture.AssertData(source!);
        using (var partial = fixture.Open(".deleted")) Assert.Null(partial!.OpenSubKey("shell"));
        fixture.Remove(".deleted");
        var deleted = await fixture.Service.DeleteAsync(item, null, fixture.Context, default);
        Assert.True(deleted.Success, deleted.Message);
        fixture.Create(); // Installer recreates the application while the backup exists.
        failure = await fixture.Service.UndoDeleteAsync(deleted.SpecialItem!, null, fixture.Context, default);
        Assert.False(failure.Success);
        using (var source = fixture.Open()) fixture.AssertData(source!);
        using (var backup = fixture.Open(".deleted")) fixture.AssertData(backup!);
        var snapshot = await fixture.Service.GetSnapshotAsync(SpecialMenuKind.OpenWith, fixture.Context, default);
        var registrations = snapshot.Where(entry => entry.Metadata.GetValueOrDefault("AppRegistryPath") == fixture.Path).ToArray();
        Assert.Equal(2, registrations.Length);
        Assert.Equal(2, registrations.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public async Task MissingSourceBackupAndInvalidBackupPath_ReturnFailuresWithoutCreatingKeys()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var item = await fixture.Read();
        fixture.Remove();
        Assert.False((await fixture.Service.DeleteAsync(item, null, fixture.Context, default)).Success);
        Assert.False((await fixture.Service.UndoDeleteAsync(item, null, fixture.Context, default)).Success);
        Assert.False((await fixture.Service.PurgeDeletedAsync(item, null, fixture.Context, default)).Success);
        fixture.Create();
        var malicious = item with { Metadata = new Dictionary<string, string>(item.Metadata) { ["DeletedPath"] = fixture.Path } };
        Assert.False((await fixture.Service.PurgeDeletedAsync(malicious, null, fixture.Context, default)).Success);
        using var source = fixture.Open();
        fixture.AssertData(source!);
    }

    [Fact]
    public async Task WrongSidUnloadedHiveAndUnrelatedMetadata_AreRejectedBeforeMutation()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var item = await fixture.Read();
        const string unloadedSid = "S-1-5-21-4294967290-4294967290-4294967290-9999";
        var wrongContext = fixture.Context with { Sid = unloadedSid };
        Assert.False((await fixture.Service.SetEnabledAsync(item, false, null, wrongContext, default)).Success);
        Assert.False((await fixture.Service.DeleteAsync(item, null, wrongContext, default)).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GetSnapshotAsync(SpecialMenuKind.OpenWith, wrongContext, default));
        using var unloaded = Registry.Users.OpenSubKey(unloadedSid);
        Assert.Null(unloaded);
        var badMetadata = item with { Metadata = new Dictionary<string, string>(item.Metadata)
            { ["AppRegistryPath"] = $@"HKEY_USERS\{fixture.Context.Sid}\Software\Classes\Unrelated" } };
        Assert.False((await fixture.Service.DeleteAsync(badMetadata, null, fixture.Context, default)).Success);
        var machineItem = item with { RegistryPath = item.RegistryPath!.Replace($@"HKEY_USERS\{fixture.Context.Sid}\Software", "HKEY_LOCAL_MACHINE\\SOFTWARE") };
        Assert.False((await fixture.Service.SetEnabledAsync(machineItem, false, null, fixture.Context with { Sid = "S-1-5-18" }, default)).Success);
        using var unchanged = fixture.Open();
        Assert.Null(unchanged!.GetValue("NoOpenWith"));
    }

    [Fact]
    public async Task ProtectedKeys_FailToggleDeleteUndoAndPurgeWithPhysicalSourcesRetained()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var item = await fixture.Read();
        using (fixture.Deny("", RegistryRights.SetValue))
            Assert.False((await fixture.Service.SetEnabledAsync(item, false, null, fixture.Context, default)).Success);
        using (fixture.Deny("", RegistryRights.Delete))
            Assert.False((await fixture.Service.DeleteAsync(item, null, fixture.Context, default)).Success);
        using (var original = fixture.Open()) fixture.AssertData(original!);
        var deleted = await fixture.Service.DeleteAsync(item, null, fixture.Context, default);
        Assert.True(deleted.Success, deleted.Message);
        using (fixture.Deny(".deleted", RegistryRights.SetValue))
            Assert.False((await fixture.Service.UndoDeleteAsync(deleted.SpecialItem!, null, fixture.Context, default)).Success);
        // Other descendants may already have staged deletions when this leaf fails.
        // The complete backup must survive a failed purge, not just its root key.
        using (fixture.Deny(".deleted\\shell\\open\\command", RegistryRights.Delete))
            Assert.False((await fixture.Service.PurgeDeletedAsync(deleted.SpecialItem!, null, fixture.Context, default)).Success);
        Assert.Null(fixture.Open());
        using (var backup = fixture.Open(".deleted")) fixture.AssertData(backup!);
        Assert.True((await fixture.Service.PurgeDeletedAsync(deleted.SpecialItem!, null, fixture.Context, default)).Success);
        Assert.Null(fixture.Open(".deleted"));
    }

    [Fact]
    public async Task ReleasedRepeatedDeletedSuffix_RestoresOnlyTheSelectedBackup()
    {
        using var fixture = new Fixture();
        fixture.Create(".deleted.deleted");
        var item = await fixture.Read(".deleted.deleted");
        Assert.Equal(fixture.Path + ".deleted.deleted", item.Metadata["DeletedPath"]);
        var result = await fixture.Service.UndoDeleteAsync(item, null, fixture.Context, default);
        Assert.True(result.Success, result.Message);
        using var restored = fixture.Open();
        fixture.AssertData(restored!);
        Assert.Null(fixture.Open(".deleted.deleted"));
    }

    [Fact]
    public async Task ConcurrentRequests_RejectStaleVisibilityInsteadOfOverwritingCommittedState()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var old = await fixture.Read();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            fixture.Service.SetEnabledAsync(old, false, null, fixture.Context, default))));
        Assert.Single(results, response => response.Success);
        var current = await fixture.Read();
        Assert.False(current.IsEnabled);
        Assert.False((await fixture.Service.SetEnabledAsync(old, true, null, fixture.Context, default)).Success);
        Assert.True((await fixture.Service.SetEnabledAsync(current, true, null, fixture.Context, default)).Success);
        using var physical = fixture.Open();
        Assert.Null(physical!.GetValue("NoOpenWith"));
    }

    [Fact]
    public async Task ApplicationsMutations_LeaveProgIdsFileExtsAndCapabilitiesUnchanged()
    {
        using var fixture = new Fixture();
        var suffix = Guid.NewGuid().ToString("N");
        var extension = ".cmopenwith" + suffix;
        var progId = "ContextMenuMgr.Tests.OpenWith." + suffix;
        var capabilitiesRoot = @"Software\ContextMenuMgr.Tests\" + progId;
        var extensionPath = @"Software\Classes\" + extension;
        var progIdPath = @"Software\Classes\" + progId;
        var fileExtsPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension;
        try
        {
            fixture.Create();
            using var extensionKey = Registry.CurrentUser.CreateSubKey(extensionPath)!;
            extensionKey.SetValue(null, progId);
            using var candidates = extensionKey.CreateSubKey("OpenWithProgids");
            candidates.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            using var command = Registry.CurrentUser.CreateSubKey(progIdPath + @"\shell\open\command")!;
            command.SetValue(null, "association-handler.exe \"%1\"");
            using var userCandidates = Registry.CurrentUser.CreateSubKey(fileExtsPath + @"\OpenWithProgids")!;
            userCandidates.SetValue(progId, string.Empty);
            using var recent = Registry.CurrentUser.CreateSubKey(fileExtsPath + @"\OpenWithList")!;
            var appName = fixture.Path[(fixture.Path.LastIndexOf('\\') + 1)..];
            recent.SetValue("a", appName);
            recent.SetValue("MRUList", "a");
            using var associations = Registry.CurrentUser.CreateSubKey(capabilitiesRoot + @"\Capabilities\FileAssociations")!;
            associations.SetValue(extension, progId);
            using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")!;
            registered.SetValue(progId, capabilitiesRoot + @"\Capabilities");

            using (var app = fixture.Open(writable: true))
            using (var supported = app!.CreateSubKey("SupportedTypes")) supported.SetValue(extension, string.Empty);
            var inspected = await fixture.Service.InspectOpenWithSourcesAsync(extension, fixture.Context, default);
            var progIdCandidate = Assert.Single(inspected.Candidates, c => c.Identifier == progId);
            Assert.Contains(progIdCandidate.Sources, r => r.Kind == OpenWithSourceKind.ClassesProgIds);
            Assert.Contains(progIdCandidate.Sources, r => r.Kind == OpenWithSourceKind.ExplorerProgIds);
            Assert.Equal(2, progIdCandidate.Sources.Count(r => r.Kind == OpenWithSourceKind.Capabilities));
            Assert.All(progIdCandidate.Sources, r =>
            {
                Assert.Equal(OpenWithSourceResolution.Resolved, r.Resolution);
                Assert.Contains($@"HKEY_USERS\{fixture.Context.Sid}\{progIdPath}", r.RegistrationPaths);
                Assert.Empty(r.ManageableRegistrationIds); // A ProgID command is not an Applications mutation target.
            });
            var applicationCandidate = Assert.Single(inspected.Candidates, c => c.Identifier == appName);
            Assert.Equal(2, applicationCandidate.Sources.Count);
            Assert.All(applicationCandidate.Sources, r => Assert.Single(r.ManageableRegistrationIds));
            Assert.Contains(applicationCandidate.Sources, r => r.SourcePath == fixture.Path + @"\SupportedTypes");
            Assert.Contains(applicationCandidate.Sources, r => r.SourcePath.EndsWith(@"\OpenWithList"));
            candidates.SetValue(progId + ".Missing", string.Empty);
            candidates.SetValue(@"Invalid\Nested", string.Empty);
            command.DeleteValue(string.Empty); // Existing ProgID with no conventional command must still resolve.
            inspected = await fixture.Service.InspectOpenWithSourcesAsync(extension, fixture.Context, default);
            Assert.All(Assert.Single(inspected.Candidates, c => c.Identifier == progId).Sources,
                r => Assert.Equal(OpenWithSourceResolution.Resolved, r.Resolution));
            Assert.Equal(OpenWithSourceResolution.Unresolved,
                Assert.Single(Assert.Single(inspected.Candidates, c => c.Identifier == progId + ".Missing").Sources).Resolution);
            Assert.Equal(OpenWithSourceResolution.Invalid,
                Assert.Single(Assert.Single(inspected.Candidates, c => c.Identifier == @"Invalid\Nested").Sources).Resolution);
            command.SetValue(null, "association-handler.exe \"%1\"");
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.InspectOpenWithSourcesAsync(@".test\..\Other", fixture.Context, default));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.InspectOpenWithSourcesAsync(extension, fixture.Context, new CancellationToken(true)));

            var disabled = await fixture.Service.SetEnabledAsync(await fixture.Read(), false, null, fixture.Context, default);
            Assert.True(disabled.Success, disabled.Message);
            var deleted = await fixture.Service.DeleteAsync(disabled.SpecialItem!, null, fixture.Context, default);
            Assert.True(deleted.Success, deleted.Message);
            Assert.True((await fixture.Service.UndoDeleteAsync(deleted.SpecialItem!, null, fixture.Context, default)).Success);
            deleted = await fixture.Service.DeleteAsync(await fixture.Read(), null, fixture.Context, default);
            Assert.True(deleted.Success, deleted.Message);
            Assert.True((await fixture.Service.PurgeDeletedAsync(deleted.SpecialItem!, null, fixture.Context, default)).Success);

            Assert.Equal(progId, extensionKey.GetValue(null));
            Assert.Equal(RegistryValueKind.None, candidates.GetValueKind(progId));
            Assert.Equal(Array.Empty<byte>(), Assert.IsType<byte[]>(candidates.GetValue(progId)));
            Assert.Equal("association-handler.exe \"%1\"", command.GetValue(null));
            Assert.Equal(string.Empty, userCandidates.GetValue(progId));
            Assert.Equal(appName, recent.GetValue("a"));
            Assert.Equal("a", recent.GetValue("MRUList"));
            Assert.Equal(progId, associations.GetValue(extension));
            Assert.Equal(capabilitiesRoot + @"\Capabilities", registered.GetValue(progId));
        }
        finally
        {
            foreach (var path in new[] { extensionPath, progIdPath, fileExtsPath, capabilitiesRoot })
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue(progId, throwOnMissingValue: false);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _name = "ContextMenuMgr.Tests.OpenWith." + Guid.NewGuid().ToString("N") + ".exe";
        private readonly string _logDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
        private readonly RegistryKey _hive;
        public Fixture()
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            Context = new BackendUserContext(sid, Environment.UserName, _logDirectory, _logDirectory, _logDirectory, null);
            _hive = RegistryKey.OpenBaseKey(RegistryHive.Users, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            Directory.CreateDirectory(_logDirectory);
            Service = new SpecialMenuService(new FileLogger(System.IO.Path.Combine(_logDirectory, "backend.log")));
        }
        public BackendUserContext Context { get; }
        public SpecialMenuService Service { get; }
        private string SubPath => $@"{Context.Sid}\Software\Classes\Applications\{_name}";
        public string Path => $@"HKEY_USERS\{SubPath}";
        public RegistryKey? Open(string suffix = "", bool writable = false) => _hive.OpenSubKey(SubPath + suffix, writable);
        public void Remove(string suffix = "") => _hive.DeleteSubKeyTree(SubPath + suffix, throwOnMissingSubKey: false);
        public void Create(string suffix = "", bool complete = true)
        {
            using var key = _hive.CreateSubKey(SubPath + suffix, writable: true)!;
            key.SetValue("RawExpand", "%USERPROFILE%\\test", RegistryValueKind.ExpandString);
            key.SetValue("Binary", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            key.SetValue("Multi", new[] { "one", "two" }, RegistryValueKind.MultiString);
            using var nested = key.CreateSubKey("Nested");
            nested.SetValue("Sentinel", 123, RegistryValueKind.DWord);
            if (!complete) return;
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(null, "\"%SystemRoot%\\System32\\notepad.exe\" \"%1\"", RegistryValueKind.ExpandString);
        }
        public async Task<SpecialMenuEntry> Read(string suffix = "")
        {
            var items = await Service.GetSnapshotAsync(SpecialMenuKind.OpenWith, Context, default);
            return Assert.Single(items, entry => entry.Metadata.GetValueOrDefault("AppRegistryPath") == Path
                && entry.Metadata.GetValueOrDefault("DeletedPath") == (suffix.Length == 0 ? "" : Path + suffix));
        }
        public void AssertData(RegistryKey key)
        {
            Assert.Equal("%USERPROFILE%\\test", key.GetValue("RawExpand", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("RawExpand"));
            Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(key.GetValue("Binary")));
            Assert.Equal(new[] { "one", "two" }, Assert.IsType<string[]>(key.GetValue("Multi")));
            using var nested = key.OpenSubKey("Nested");
            Assert.Equal(123, nested!.GetValue("Sentinel"));
        }
        public string Acl(string suffix = "")
        {
            using var key = Open(suffix)!;
            return key.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }
        public IDisposable Deny(string suffix, RegistryRights rights)
        {
            using var key = _hive.OpenSubKey(SubPath + suffix, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!;
            var security = key.GetAccessControl();
            var original = security.GetSecurityDescriptorBinaryForm();
            security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(Context.Sid), rights,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Deny));
            key.SetAccessControl(security);
            return new RestoreAcl(_hive, SubPath + suffix, original);
        }
        public void Dispose()
        {
            foreach (var suffix in new[] { "", ".deleted", ".deleted.deleted" }) Remove(suffix);
            _hive.Dispose();
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

    private sealed class RestoreAcl(RegistryKey hive, string path, byte[] original) : IDisposable
    {
        public void Dispose()
        {
            using var key = hive.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions)!;
            var security = new RegistrySecurity();
            security.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            key.SetAccessControl(security);
        }
    }
}
