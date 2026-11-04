using System.Security.Principal;
using System.Text.Json;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>Permanent regression coverage for ShellNew physical identity and cache integration.</summary>
public sealed class ShellNewPhysicalDeduplicationTests
{
    [Fact]
    public async Task SharedProgIdDiscoveryDeduplicatesBeforeRepeatedCacheRefreshWithoutWritingRegistry()
    {
        using var fixture = SharedProgIdFixture.Create();
        using var classes = Registry.CurrentUser.OpenSubKey("Software\\Classes", writable: false)!;
        var service = fixture.CreateService();
        var before = fixture.ReadFixtureState();
        var discoveries = fixture.Extensions
            .SelectMany(extension => service.EnumerateShellNewForExtension(classes, extension, fixture.Context, CancellationToken.None))
            .ToArray();

        Assert.Equal(3, discoveries.Length);
        Assert.Single(discoveries.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(before, fixture.ReadFixtureState());

        var snapshot = SpecialMenuService.CreateShellNewPresentation(discoveries, new Dictionary<string, int>
        {
            [".cmm152a"] = 4, [".cmm152b"] = 2, [".cmm152c"] = 8
        }, orderLocked: true).Where(item => item.KeyName != "Separator").ToArray();
        var item = Assert.Single(snapshot);
        Assert.Equal(fixture.ProgId + "\\ShellNew", item.RegistryPath!.Split("\\Software\\Classes\\", StringSplitOptions.None).Last());
        Assert.Equal(".cmm152a", item.KeyName);
        Assert.Equal(new[] { ".cmm152a", ".cmm152b", ".cmm152c" }, JsonSerializer.Deserialize<string[]>(item.Metadata["AssociatedExtensions"]));
        Assert.Equal("True", item.Metadata["SharedPhysicalRegistration"]);
        Assert.False(item.CanMove);
        Assert.Contains(".cmm152a, .cmm152b, .cmm152c", item.Notes);

        var cache = new MenuListCacheService(Path.Combine(Path.GetTempPath(), "ContextMenuMgr-152-cache-" + Guid.NewGuid().ToString("N")), null);
        try
        {
            var first = await cache.MergeAsync("ShellNew", snapshot, true, cache.Version);
            var second = await cache.MergeAsync("ShellNew", snapshot, true, cache.Version);
            Assert.Single(first);
            Assert.Single(second);
            Assert.Equal(item.Id, Assert.Single(second).Id);
        }
        finally { await cache.DisposeAsync(); }
        Assert.Equal(before, fixture.ReadFixtureState());
    }

    [Fact]
    public void DistinctPhysicalSourcesAndEnabledDisabledSiblingsRemainSeparate()
    {
        var entries = new[]
        {
            Entry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Cmm152.Machine\ShellNew", ".cmm152m", true, "System"),
            Entry(@"HKEY_USERS\S-1-5-21-100\Software\Classes\Cmm152.User\ShellNew", ".cmm152u", true, "User"),
            Entry(@"HKEY_USERS\S-1-5-21-100\Software\Classes\Cmm152.User\-ShellNew", ".cmm152d", false, "User")
        };

        var result = SpecialMenuService.CreateShellNewPresentation(entries, new Dictionary<string, int>(), orderLocked: true)
            .Where(item => item.KeyName != "Separator").ToArray();

        Assert.Equal(3, result.Length);
        Assert.Equal(3, result.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(result, item => item.IsEnabled && item.RegistryPath!.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, item => item.IsEnabled && item.RegistryPath!.StartsWith("HKEY_USERS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, item => !item.IsEnabled && item.RegistryPath!.EndsWith("-ShellNew", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConflictingDiscoveryForSamePhysicalIdFailsClosedWithDiagnostic()
    {
        var first = Entry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Cmm152.Shared\ShellNew", ".cmm152a", true, "System");
        var conflict = first with { IsEnabled = false };

        var error = Assert.Throws<InvalidOperationException>(() => SpecialMenuService.CreateShellNewPresentation(
            [first, conflict], new Dictionary<string, int>(), orderLocked: true));

        Assert.Contains("Conflicting ShellNew discoveries", error.Message);
        Assert.Contains(first.RegistryPath!, error.Message);
    }

    [Fact]
    public void SharedRegistrationAllowsExtensionSpecificPresentationAndDisablesAmbiguousMoves()
    {
        var shared = Entry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Cmm152.Shared\ShellNew", ".cmm152a", true, "System");
        var second = shared with { KeyName = ".cmm152b", DisplayName = "Other file type", IconPath = "other.ico",
            Metadata = new Dictionary<string, string>(shared.Metadata) { ["Extension"] = ".cmm152b" } };
        var local = Entry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\.cmm152a\ShellNew", ".cmm152a", true, "System");
        var snapshot = SpecialMenuService.CreateShellNewPresentation([second, local, shared],
            new Dictionary<string, int> { [".cmm152a"] = 1, [".cmm152b"] = 0 }, orderLocked: true)
            .Where(item => item.KeyName != "Separator").ToArray();

        Assert.Equal(2, snapshot.Length);
        Assert.All(snapshot, item => Assert.False(item.CanMove));
        var merged = Assert.Single(snapshot, item => item.RegistryPath == shared.RegistryPath);
        Assert.Equal(new[] { ".cmm152a", ".cmm152b" }, JsonSerializer.Deserialize<string[]>(merged.Metadata["AssociatedExtensions"]));
        Assert.Equal(".cmm152a", merged.KeyName);
    }

    [Fact]
    public void SharedRegistrationToggleMovesOnlyItsPhysicalSiblingAndPreservesAssociations()
    {
        using var fixture = SharedProgIdFixture.Create();
        using var classes = Registry.CurrentUser.OpenSubKey("Software\\Classes", writable: false)!;
        var service = fixture.CreateService();
        var discoveries = fixture.Extensions
            .SelectMany(extension => service.EnumerateShellNewForExtension(classes, extension, fixture.Context, CancellationToken.None))
            .ToArray();
        var item = Assert.Single(SpecialMenuService.CreateShellNewPresentation(discoveries,
            new Dictionary<string, int>(), orderLocked: false), entry => entry.KeyName != "Separator");

        var disabled = service.SetShellNewEnabled(item, false, fixture.Context);
        Assert.False(disabled.IsEnabled);
        Assert.EndsWith(@"\-ShellNew", disabled.RegistryPath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(item.Id, disabled.Id);
        Assert.Equal(item.Metadata["AssociatedExtensions"], disabled.Metadata["AssociatedExtensions"]);
        using (var progId = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{fixture.ProgId}", writable: false)!)
        {
            Assert.Null(progId.OpenSubKey("ShellNew"));
            Assert.NotNull(progId.OpenSubKey("-ShellNew"));
        }

        var enabled = service.SetShellNewEnabled(disabled, true, fixture.Context);
        Assert.Equal(item.Id, enabled.Id);
        Assert.Equal(item.Metadata["AssociatedExtensions"], enabled.Metadata["AssociatedExtensions"]);
    }

    [Fact]
    public async Task CacheRejectsUnnormalizedDuplicateSnapshotAndRetainsLastCompleteState()
    {
        var cache = new MenuListCacheService(Path.Combine(Path.GetTempPath(), "ContextMenuMgr-152-cache-" + Guid.NewGuid().ToString("N")), null);
        try
        {
            var first = Entry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Cmm152.One\ShellNew", ".cmm152a", true, "System");
            await cache.MergeAsync("ShellNew", [first], true, cache.Version);
            var duplicate = first with { KeyName = ".cmm152b" };

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.MergeAsync("ShellNew", [first, duplicate], true, cache.Version));

            Assert.Contains("duplicate item identity", error.Message);
            var persisted = await cache.ReadAsync<SpecialMenuEntry>("ShellNew");
            Assert.Single(persisted!);
        }
        finally { await cache.DisposeAsync(); }
    }

    private static SpecialMenuEntry Entry(string path, string extension, bool enabled, string scope)
    {
        var sibling = path.EndsWith("-ShellNew", StringComparison.OrdinalIgnoreCase)
            ? path[..^"-ShellNew".Length] + "ShellNew"
            : path[..^"ShellNew".Length] + "-ShellNew";
        return new SpecialMenuEntry
        {
            Id = $"{SpecialMenuKind.ShellNew}:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path))}",
            Kind = SpecialMenuKind.ShellNew,
            KeyName = extension,
            RegistryPath = path,
            IsEnabled = enabled,
            CanMove = true,
            Metadata = new Dictionary<string, string>
            {
                ["Extension"] = extension,
                ["ProgId"] = "Cmm152.Shared",
                ["CreationKind"] = "NullFile",
                ["DisabledRegistryPath"] = sibling,
                ["SourceScope"] = scope,
                ["BeforeSeparator"] = "False"
            }
        };
    }

    private sealed class SharedProgIdFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _logDirectory;
        public string ProgId { get; } = "Cmm152.Shared." + Guid.NewGuid().ToString("N");
        public string[] Extensions { get; }
        public BackendUserContext Context { get; }

        private SharedProgIdFixture()
        {
            _root = $@"Software\Classes\{ProgId}";
            Extensions = [".cmm152a", ".cmm152b", ".cmm152c"];
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            _logDirectory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-152-logs-" + Guid.NewGuid().ToString("N"));
            Context = new BackendUserContext(sid, "ContextMenuMgr.Tests", _logDirectory, _logDirectory, _logDirectory, SessionId: null);
            using (var progIdKey = Registry.CurrentUser.CreateSubKey(_root, writable: true)!)
            using (var shellNew = progIdKey.CreateSubKey("ShellNew", writable: true)!)
                shellNew.SetValue("NullFile", string.Empty, RegistryValueKind.String);
            foreach (var extension in Extensions)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}", writable: true)!;
                key.SetValue(null, ProgId, RegistryValueKind.String);
            }
        }

        public static SharedProgIdFixture Create() => new();
        public SpecialMenuService CreateService() => new(new FileLogger(Path.Combine(_logDirectory, "backend.log")));

        public string ReadFixtureState()
        {
            var values = new List<string>();
            using var progId = Registry.CurrentUser.OpenSubKey(_root, writable: false)!;
            using var shellNew = progId.OpenSubKey("ShellNew", writable: false)!;
            values.AddRange(shellNew.GetValueNames().OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"SN:{value}:{shellNew.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames)}"));
            foreach (var extension in Extensions)
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: false)!;
                values.Add($"EXT:{extension}:{key.GetValue(null)}");
            }
            return string.Join("|", values);
        }

        public void Dispose()
        {
            foreach (var extension in Extensions)
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{extension}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
            try { if (Directory.Exists(_logDirectory)) Directory.Delete(_logDirectory, recursive: true); } catch { }
        }
    }
}
