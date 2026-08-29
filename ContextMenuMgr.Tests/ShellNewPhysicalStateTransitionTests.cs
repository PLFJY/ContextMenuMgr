using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>
/// Controlled HKU tests for ShellNew's physical ShellNew &lt;-&gt; -ShellNew transition.
/// </summary>
public sealed class ShellNewPhysicalStateTransitionTests
{
    [Fact]
    public void ToggleCycle_MovesAndReturnsVerifiedAlternatePhysicalPaths()
    {
        using var fixture = ShellNewRegistryFixture.Create();
        fixture.CreateActiveRegistration();
        var service = fixture.CreateService();
        var item = fixture.CreateEntry(fixture.EnabledPath, isEnabled: true, counterpartPath: fixture.DisabledPath);

        var disabled = service.SetShellNewEnabled(item, enabled: false, context: fixture.Context);
        fixture.AssertPhysicalState(enabled: false);
        AssertReturnedState(disabled, fixture.DisabledPath, isEnabled: false, fixture.EnabledPath);

        var enabled = service.SetShellNewEnabled(disabled, enabled: true, context: fixture.Context);
        fixture.AssertPhysicalState(enabled: true);
        AssertReturnedState(enabled, fixture.EnabledPath, isEnabled: true, fixture.DisabledPath);

        var disabledAgain = service.SetShellNewEnabled(enabled, enabled: false, context: fixture.Context);
        fixture.AssertPhysicalState(enabled: false);
        AssertReturnedState(disabledAgain, fixture.DisabledPath, isEnabled: false, fixture.EnabledPath);
    }

    [Fact]
    public void StaleCounterpartMetadata_IsRecoveredFromActualPhysicalRegistration()
    {
        using var fixture = ShellNewRegistryFixture.Create();
        fixture.CreateActiveRegistration();
        var service = fixture.CreateService();
        var staleItem = fixture.CreateEntry(fixture.EnabledPath, isEnabled: true, counterpartPath: fixture.EnabledPath);

        var result = service.SetShellNewEnabled(staleItem, enabled: false, context: fixture.Context);

        fixture.AssertPhysicalState(enabled: false);
        AssertReturnedState(result, fixture.DisabledPath, isEnabled: false, fixture.EnabledPath);
    }

    [Fact]
    public void MissingPhysicalSource_ThrowsInsteadOfFabricatingRequestedState()
    {
        using var fixture = ShellNewRegistryFixture.Create();
        var service = fixture.CreateService();
        var item = fixture.CreateEntry(fixture.EnabledPath, isEnabled: true, counterpartPath: fixture.DisabledPath);

        var exception = Assert.Throws<InvalidOperationException>(() => service.SetShellNewEnabled(item, enabled: false, context: fixture.Context));

        Assert.Contains("no longer exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertNeitherPathExists();
    }

    [Fact]
    public void ExistingDestinationConflict_ThrowsWithoutOverwritingEitherRegistration()
    {
        using var fixture = ShellNewRegistryFixture.Create();
        fixture.CreateActiveRegistration();
        fixture.CreateDisabledRegistration();
        var service = fixture.CreateService();
        var item = fixture.CreateEntry(fixture.EnabledPath, isEnabled: true, counterpartPath: fixture.DisabledPath);

        var exception = Assert.Throws<InvalidOperationException>(() => service.SetShellNewEnabled(item, enabled: false, context: fixture.Context));

        Assert.Contains("Both active and disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertBothPathsExist();
    }

    private static void AssertReturnedState(SpecialMenuEntry item, string registryPath, bool isEnabled, string counterpartPath)
    {
        Assert.Equal(registryPath, item.RegistryPath);
        Assert.Equal($"{SpecialMenuKind.ShellNew}:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(registryPath))}", item.Id);
        Assert.Equal(isEnabled, item.IsEnabled);
        Assert.Equal(counterpartPath, item.Metadata["DisabledRegistryPath"]);
    }

    private sealed class ShellNewRegistryFixture : IDisposable
    {
        private readonly string _subPath;
        private readonly string _logDirectory;

        private ShellNewRegistryFixture(string sid, string fixtureName)
        {
            _subPath = $@"{sid}\Software\Classes\ContextMenuMgr.Tests.{fixtureName}\.cm112";
            EnabledPath = $@"HKEY_USERS\{_subPath}\ShellNew";
            DisabledPath = $@"HKEY_USERS\{_subPath}\-ShellNew";
            _logDirectory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", fixtureName);
            Context = new BackendUserContext(
                sid,
                "ContextMenuMgr.Tests",
                _logDirectory,
                _logDirectory,
                _logDirectory,
                SessionId: null);
        }

        public BackendUserContext Context { get; }

        public string EnabledPath { get; }

        public string DisabledPath { get; }

        public static ShellNewRegistryFixture Create()
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The current test identity has no user SID.");
            return new ShellNewRegistryFixture(sid, Guid.NewGuid().ToString("N"));
        }

        public SpecialMenuService CreateService()
        {
            Directory.CreateDirectory(_logDirectory);
            return new SpecialMenuService(new FileLogger(Path.Combine(_logDirectory, "backend.log")));
        }

        public SpecialMenuEntry CreateEntry(string registryPath, bool isEnabled, string counterpartPath)
            => new()
            {
                Id = $"{SpecialMenuKind.ShellNew}:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(registryPath))}",
                Kind = SpecialMenuKind.ShellNew,
                DisplayName = "ContextMenuMgr test item",
                KeyName = ".cm112",
                RegistryPath = registryPath,
                IsEnabled = isEnabled,
                Metadata = new Dictionary<string, string> { ["DisabledRegistryPath"] = counterpartPath }
            };

        public void CreateActiveRegistration() => CreateRegistration("ShellNew", "active");

        public void CreateDisabledRegistration() => CreateRegistration("-ShellNew", "disabled");

        public void AssertPhysicalState(bool enabled)
        {
            Assert.Equal(enabled, PathExists("ShellNew"));
            Assert.Equal(!enabled, PathExists("-ShellNew"));
        }

        public void AssertNeitherPathExists()
        {
            Assert.False(PathExists("ShellNew"));
            Assert.False(PathExists("-ShellNew"));
        }

        public void AssertBothPathsExist()
        {
            Assert.True(PathExists("ShellNew"));
            Assert.True(PathExists("-ShellNew"));
        }

        public void Dispose()
        {
            try
            {
                Registry.Users.DeleteSubKeyTree(_subPath, throwOnMissingSubKey: false);
            }
            finally
            {
                try { Directory.Delete(_logDirectory, recursive: true); } catch { }
            }
        }

        private void CreateRegistration(string keyName, string marker)
        {
            using var key = Registry.Users.CreateSubKey($@"{_subPath}\{keyName}", writable: true)
                ?? throw new InvalidOperationException("Unable to create the controlled ShellNew fixture.");
            key.SetValue("NullFile", string.Empty, RegistryValueKind.String);
            key.SetValue("Marker", marker, RegistryValueKind.String);
            key.SetValue("Binary", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            key.SetValue("Multi", new[] { "one", "two" }, RegistryValueKind.MultiString);
            using var nested = key.CreateSubKey("Config\\Nested", writable: true)
                ?? throw new InvalidOperationException("Unable to create the controlled ShellNew nested fixture.");
            nested.SetValue("Value", marker, RegistryValueKind.String);
        }

        private bool PathExists(string keyName)
        {
            using var key = Registry.Users.OpenSubKey($@"{_subPath}\{keyName}", writable: false);
            return key is not null;
        }
    }
}
