using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using System.Security.Principal;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>
/// Controlled registry integration tests for the verified classic Shell Extension
/// registration move. Every fixture is created below a unique test-only HKU
/// Software\Classes key and removed in finally; no installed handler is touched.
/// </summary>
public sealed class ClassicShellExtensionRegistryMoveTests
{
    [Fact]
    public void MoveRegistryKeySafely_PreservesNestedValues_AndOnlyMovesTheSelectedSameClsidRegistration()
    {
        using var fixture = RegistryMoveFixture.Create();
        const string handlerClsid = "{11111111-1111-1111-1111-111111111111}";

        fixture.CreateHandler("*", "Foo", handlerClsid, includeNestedValues: true);
        fixture.CreateHandler("Directory", "Foo", handlerClsid);
        fixture.CreateHandler("Folder", "Foo", handlerClsid);

        var fileActivePath = fixture.GetActiveAbsolutePath("*", "Foo");
        var fileDisabledPath = fixture.GetDisabledAbsolutePath("*", "Foo");

        ContextMenuRegistryCatalog.MoveRegistryKeySafely(fileActivePath, fileDisabledPath);

        Assert.Null(fixture.Open("*", "ContextMenuHandlers\\Foo"));
        using (var disabled = fixture.Open("*", "-ContextMenuHandlers\\Foo"))
        {
            Assert.NotNull(disabled);
            Assert.Equal(handlerClsid, disabled!.GetValue(null));
            Assert.Equal(42, disabled.GetValue("Dword"));
            Assert.Equal(RegistryValueKind.DWord, disabled.GetValueKind("Dword"));
            Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(disabled.GetValue("Binary")));
            Assert.Equal(RegistryValueKind.Binary, disabled.GetValueKind("Binary"));
            Assert.Equal(new[] { "one", "two" }, Assert.IsType<string[]>(disabled.GetValue("MultiString")));
            Assert.Equal(RegistryValueKind.MultiString, disabled.GetValueKind("MultiString"));
            using var nested = disabled.OpenSubKey("Nested\\Deep");
            Assert.NotNull(nested);
            Assert.Equal("nested-value", nested!.GetValue("Value"));
        }

        // Same CLSID, separate physical registrations: only File moved.
        using (var directory = fixture.Open("Directory", "ContextMenuHandlers\\Foo"))
        {
            Assert.NotNull(directory);
        }

        using (var folder = fixture.Open("Folder", "ContextMenuHandlers\\Foo"))
        {
            Assert.NotNull(folder);
        }
        Assert.Null(fixture.Open("Directory", "-ContextMenuHandlers\\Foo"));
        Assert.Null(fixture.Open("Folder", "-ContextMenuHandlers\\Foo"));

        ContextMenuRegistryCatalog.MoveRegistryKeySafely(fileDisabledPath, fileActivePath);

        using (var active = fixture.Open("*", "ContextMenuHandlers\\Foo"))
        {
            Assert.NotNull(active);
        }
        Assert.Null(fixture.Open("*", "-ContextMenuHandlers\\Foo"));
    }

    [Fact]
    public void MoveRegistryKeySafely_RejectsDestinationCollisionWithoutOverwritingEitherRegistration()
    {
        using var fixture = RegistryMoveFixture.Create();
        fixture.CreateHandler("*", "Foo", "{11111111-1111-1111-1111-111111111111}");
        fixture.CreateHandler("*", "Foo", "{22222222-2222-2222-2222-222222222222}", disabled: true);

        var exception = Assert.Throws<InvalidOperationException>(() => ContextMenuRegistryCatalog.MoveRegistryKeySafely(
            fixture.GetActiveAbsolutePath("*", "Foo"),
            fixture.GetDisabledAbsolutePath("*", "Foo")));

        Assert.Contains("destination already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        using var active = fixture.Open("*", "ContextMenuHandlers\\Foo");
        using var disabled = fixture.Open("*", "-ContextMenuHandlers\\Foo");
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", active!.GetValue(null));
        Assert.Equal("{22222222-2222-2222-2222-222222222222}", disabled!.GetValue(null));
    }

    [Fact]
    public void UnsupportedPropertySheetHandler_IsReadOnlyAndExcludedFromDisabledStateReconciliation()
    {
        var entry = new ContextMenuEntry
        {
            EntryKind = ContextMenuEntryKind.ShellExtension,
            RegistryPath = @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shellex\PropertySheetHandlers\Properties",
            BackendRegistryPath = @"HKEY_USERS\S-1-5-21-test\Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shellex\PropertySheetHandlers\Properties",
            IsEnabled = true,
            CanToggle = ContextMenuRegistryCatalog.SupportsClassicShellExtensionContainerToggle(
                @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shellex\PropertySheetHandlers\Properties")
        };
        var state = new PersistedContextMenuState { DesiredEnabled = false };

        Assert.False(entry.CanToggle);
        Assert.False(ContextMenuChangeClassifier.ShouldReconcileDisabledState(entry, state));
    }

    [Fact]
    public void LegacyGlobalBlock_IsReportedAsACompatibilityWarning_NotAsThePerRegistrationState()
    {
        var activeClassicEntry = new ContextMenuEntry
        {
            EntryKind = ContextMenuEntryKind.ShellExtension,
            HandlerClsid = "{11111111-1111-1111-1111-111111111111}",
            IsEnabled = true
        };

        Assert.NotNull(ContextMenuRegistryCatalog.GetLegacyGlobalShellExtensionBlockConsistencyIssue(
            activeClassicEntry,
            hasLegacyGlobalShellExtensionBlock: true));
        Assert.Null(ContextMenuRegistryCatalog.GetLegacyGlobalShellExtensionBlockConsistencyIssue(
            activeClassicEntry with { IsEnabled = false },
            hasLegacyGlobalShellExtensionBlock: true));
        Assert.Null(ContextMenuRegistryCatalog.GetLegacyGlobalShellExtensionBlockConsistencyIssue(
            activeClassicEntry with { IsWindows11ContextMenu = true },
            hasLegacyGlobalShellExtensionBlock: true));
    }

    private sealed class RegistryMoveFixture : IDisposable
    {
        private readonly string _subPath;
        private readonly string _absolutePrefix;
        private bool _disposed;

        private RegistryMoveFixture(string sid, string fixtureName)
        {
            _subPath = $@"{sid}\Software\Classes\ContextMenuMgr.Tests.{fixtureName}";
            _absolutePrefix = $@"HKEY_USERS\{_subPath}";
        }

        public static RegistryMoveFixture Create()
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The current test identity has no user SID.");
            return new RegistryMoveFixture(sid, Guid.NewGuid().ToString("N"));
        }

        public void CreateHandler(string classRoot, string keyName, string clsid, bool disabled = false, bool includeNestedValues = false)
        {
            var container = disabled ? "-ContextMenuHandlers" : "ContextMenuHandlers";
            using var key = Registry.Users.CreateSubKey($@"{_subPath}\{classRoot}\shellex\{container}\{keyName}", writable: true)
                ?? throw new InvalidOperationException("Unable to create the controlled Shell Extension fixture.");
            key.SetValue(string.Empty, clsid, RegistryValueKind.String);

            if (!includeNestedValues)
            {
                return;
            }

            key.SetValue("Dword", 42, RegistryValueKind.DWord);
            key.SetValue("Binary", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            key.SetValue("MultiString", new[] { "one", "two" }, RegistryValueKind.MultiString);
            using var nested = key.CreateSubKey("Nested\\Deep", writable: true)
                ?? throw new InvalidOperationException("Unable to create a controlled nested fixture key.");
            nested.SetValue("Value", "nested-value", RegistryValueKind.String);
        }

        public RegistryKey? Open(string classRoot, string relativePath)
            => Registry.Users.OpenSubKey($@"{_subPath}\{classRoot}\shellex\{relativePath}", writable: false);

        public string GetActiveAbsolutePath(string classRoot, string keyName)
            => $@"{_absolutePrefix}\{classRoot}\shellex\ContextMenuHandlers\{keyName}";

        public string GetDisabledAbsolutePath(string classRoot, string keyName)
            => $@"{_absolutePrefix}\{classRoot}\shellex\-ContextMenuHandlers\{keyName}";

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Registry.Users.DeleteSubKeyTree(_subPath, throwOnMissingSubKey: false);
        }
    }
}
