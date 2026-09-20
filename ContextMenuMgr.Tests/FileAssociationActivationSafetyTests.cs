using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class FileAssociationActivationSafetyTests
{
    [Fact]
    public async Task GenericDisable_RejectsActiveOpenBeforePhysicalMutation()
    {
        var fixture = CreateFixture();
        var runtimeRoot = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-ActivationSafetyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        try
        {
            using (var extension = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.Extension}", writable: true)!)
            {
                extension.SetValue(null, fixture.ProgId, RegistryValueKind.String);
            }
            using (var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.ProgId}\shell", writable: true)!)
            {
                shell.SetValue(null, "open", RegistryValueKind.String);
            }
            using (var command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.ProgId}\shell\open\command", writable: true)!)
            {
                command.SetValue(null, "test-open.exe \"%1\"", RegistryValueKind.String);
            }

            var logger = new FileLogger(Path.Combine(runtimeRoot, "backend.log"));
            var catalog = new ContextMenuRegistryCatalog(
                logger,
                new ContextMenuStateStore(Path.Combine(runtimeRoot, "state.json"), logger),
                new RegistryBackupService(Path.Combine(runtimeRoot, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(runtimeRoot, "protection.json"), logger));
            var scene = await catalog.GetSceneSnapshotAsync(
                ContextMenuSceneKind.CustomExtension,
                fixture.Extension,
                CancellationToken.None,
                fixture.UserContext);
            var item = Assert.Single(scene, entry =>
                entry.EntryKind == ContextMenuEntryKind.ShellVerb
                && string.Equals(entry.KeyName, "open", StringComparison.OrdinalIgnoreCase));

            var response = await catalog.ApplyDesiredStateAsync(
                item.Id,
                enable: false,
                CancellationToken.None,
                fixture.UserContext,
                item);

            Assert.False(response.Success);
            Assert.Equal(PipeErrorCodes.FileTypeActivationVerbProtected, response.ErrorCode);
            using var open = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{fixture.ProgId}\shell\open")!;
            Assert.Null(open.GetValue(ShellVerbVisibility.ManagedMarkerName));
        }
        finally
        {
            fixture.Dispose();
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public void ParentShellDefaultOpen_IsProtectedBeforeMutation()
    {
        var fixture = CreateFixture();
        try
        {
            using (var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.ProgId}\shell", writable: true)!)
            {
                shell.SetValue(null, "open", RegistryValueKind.String);
            }
            using (var command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.ProgId}\shell\open\command", writable: true)!)
            {
                command.SetValue(null, "test-open.exe \"%1\"", RegistryValueKind.String);
            }

            Assert.True(ContextMenuRegistryCatalog.IsProtectedActivationVerb(
                fixture.CreateEntry("open"),
                fixture.UserContext,
                out var reason));
            Assert.Equal("ParentShellDefaultSelectsTarget", reason);
            using var open = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{fixture.ProgId}\shell\open")!;
            Assert.Null(open.GetValue("ProgrammaticAccessOnly"));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void EffectiveDisposableExtensionAssociation_ProtectsOpenFallback()
    {
        var fixture = CreateFixture();
        try
        {
            using (var extension = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.Extension}", writable: true)!)
            {
                extension.SetValue(null, fixture.ProgId, RegistryValueKind.String);
            }
            using (var command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.ProgId}\shell\open\command", writable: true)!)
            {
                command.SetValue(null, "test-open.exe \"%1\"", RegistryValueKind.String);
            }

            Assert.True(ContextMenuRegistryCatalog.IsProtectedActivationVerb(
                fixture.CreateEntry("open"),
                fixture.UserContext,
                out var reason));
            Assert.Equal("EffectiveFileAssociationUsesProgIdOpenVerb", reason);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void PrintVisibilityMutation_DoesNotChangeOpenOrAssociationMetadata()
    {
        var fixture = CreateFixture();
        try
        {
            using (var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.ProgId}\shell", writable: true)!)
            {
                shell.SetValue(null, "open", RegistryValueKind.String);
            }
            using (var openCommand = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.ProgId}\shell\open\command", writable: true)!)
            {
                openCommand.SetValue(null, "test-open.exe \"%1\"", RegistryValueKind.String);
            }
            using (var printCommand = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fixture.ProgId}\shell\print\command", writable: true)!)
            {
                printCommand.SetValue(null, "test-print.exe \"%1\"", RegistryValueKind.String);
            }

            using var print = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{fixture.ProgId}\shell\print", writable: true)!;
            var disable = ShellVerbVisibilityTransaction.Create(print, print.Name, requestedVisible: false, existingProvenance: null);
            disable.Apply(print);
            var enable = ShellVerbVisibilityTransaction.Create(print, print.Name, requestedVisible: true, disable.Provenance);
            enable.Apply(print);

            using var shellAfter = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{fixture.ProgId}\shell")!;
            using var openAfter = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{fixture.ProgId}\shell\open\command")!;
            using var printAfter = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{fixture.ProgId}\shell\print\command")!;
            Assert.Equal("open", shellAfter.GetValue(null));
            Assert.Equal("test-open.exe \"%1\"", openAfter.GetValue(null));
            Assert.Equal("test-print.exe \"%1\"", printAfter.GetValue(null));
            Assert.Null(print.GetValue("ProgrammaticAccessOnly"));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static Fixture CreateFixture()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        return new Fixture(
            $"ContextMenuMgr.Tests.{suffix}",
            $".cmtest{suffix}",
            sid,
            new BackendUserContext(sid, "test", string.Empty, string.Empty, string.Empty, null));
    }

    private sealed record Fixture(
        string ProgId,
        string Extension,
        string Sid,
        BackendUserContext UserContext) : IDisposable
    {
        public ContextMenuEntry CreateEntry(string verb)
            => new()
            {
                Id = $@"{ProgId}\shell|{verb}",
                Category = ContextMenuCategory.File,
                EntryKind = ContextMenuEntryKind.ShellVerb,
                KeyName = verb,
                DisplayName = verb,
                RegistryPath = $@"{ProgId}\shell\{verb}",
                BackendRegistryPath = $@"HKEY_USERS\{Sid}\Software\Classes\{ProgId}\shell\{verb}",
                SourceRootPath = $@"{ProgId}\shell",
                IsEnabled = true,
                IsPresentInRegistry = true,
                CanToggle = true
            };

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Extension}", throwOnMissingSubKey: false);
        }
    }
}
