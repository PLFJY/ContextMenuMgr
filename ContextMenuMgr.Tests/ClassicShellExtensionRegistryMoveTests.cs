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
    public void DuplicateContainerSelection_UsesNewestWriteTime_ThenDesiredStateAsTieBreaker()
    {
        var older = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddSeconds(1);

        Assert.True(ContextMenuRegistryCatalog.SelectEnabledSideForDuplicate(
            newestActiveWriteUtc: newer,
            newestDisabledWriteUtc: older,
            desiredEnabled: false));
        Assert.False(ContextMenuRegistryCatalog.SelectEnabledSideForDuplicate(
            newestActiveWriteUtc: older,
            newestDisabledWriteUtc: newer,
            desiredEnabled: true));
        Assert.False(ContextMenuRegistryCatalog.SelectEnabledSideForDuplicate(
            newestActiveWriteUtc: newer,
            newestDisabledWriteUtc: newer,
            desiredEnabled: false));
        Assert.True(ContextMenuRegistryCatalog.SelectEnabledSideForDuplicate(
            newestActiveWriteUtc: null,
            newestDisabledWriteUtc: null,
            desiredEnabled: null));
    }

    [Fact]
    public void RegistryWriteTime_CanBeReadForControlledHandlerKey()
    {
        using var fixture = RegistryMoveFixture.Create();
        fixture.CreateHandler("*", "Timestamp", "{11111111-1111-1111-1111-111111111111}");

        Assert.True(ContextMenuRegistryCatalog.TryGetRegistryWriteTimeUtc(
            fixture.GetActiveAbsolutePath("*", "Timestamp"),
            out var writeUtc));
        Assert.InRange(writeUtc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

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
    public void MoveRegistryKeySafely_ReconcilesEquivalentDuplicateRegistration_KeepsDisabledDestination()
    {
        using var fixture = RegistryMoveFixture.Create();
        const string handlerClsid = "{11111111-1111-1111-1111-111111111111}";

        // The disabled destination already exists because ContextMenuMgr
        // disabled the registration earlier; a third-party update then
        // recreated the active copy with identical content (issue #98).
        fixture.CreateHandler("*", "Foo", handlerClsid, includeNestedValues: true);
        fixture.CreateHandler("*", "Foo", handlerClsid, disabled: true, includeNestedValues: true);

        ContextMenuRegistryCatalog.MoveRegistryKeySafely(
            fixture.GetActiveAbsolutePath("*", "Foo"),
            fixture.GetDisabledAbsolutePath("*", "Foo"));

        // Only the redundant active source is removed; the existing disabled
        // destination is preserved with all values and nested subkeys intact.
        Assert.Null(fixture.Open("*", "ContextMenuHandlers\\Foo"));
        using (var disabled = fixture.Open("*", "-ContextMenuHandlers\\Foo"))
        {
            Assert.NotNull(disabled);
            Assert.Equal(handlerClsid, disabled!.GetValue(null));
            Assert.Equal(RegistryValueKind.String, disabled.GetValueKind(null));
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
    }

    [Fact]
    public void MoveRegistryKeySafely_SameClsidDifferentContent_IsRejectedAsConflict()
    {
        using var fixture = RegistryMoveFixture.Create();
        const string handlerClsid = "{11111111-1111-1111-1111-111111111111}";

        // Same CLSID but different registry trees: the active copy carries
        // additional metadata that the disabled copy does not have. A same-CLsid
        // result alone must not be treated as equivalence.
        fixture.CreateHandler("*", "Foo", handlerClsid, includeNestedValues: true);
        fixture.CreateHandler("*", "Foo", handlerClsid, disabled: true);

        var exception = Assert.Throws<InvalidOperationException>(() => ContextMenuRegistryCatalog.MoveRegistryKeySafely(
            fixture.GetActiveAbsolutePath("*", "Foo"),
            fixture.GetDisabledAbsolutePath("*", "Foo")));

        Assert.Contains("destination already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        using var active = fixture.Open("*", "ContextMenuHandlers\\Foo");
        using var disabled = fixture.Open("*", "-ContextMenuHandlers\\Foo");
        Assert.NotNull(active);
        Assert.NotNull(disabled);
        Assert.Equal(handlerClsid, active!.GetValue(null));
        Assert.Equal(handlerClsid, disabled!.GetValue(null));
        Assert.Equal(42, active.GetValue("Dword"));
        Assert.Null(disabled.GetValue("Dword"));
    }

    [Fact]
    public void MoveRegistryKeySafely_RepeatedReconciliation_IsIdempotent()
    {
        using var fixture = RegistryMoveFixture.Create();
        const string handlerClsid = "{11111111-1111-1111-1111-111111111111}";

        fixture.CreateHandler("*", "Foo", handlerClsid, includeNestedValues: true);
        fixture.CreateHandler("*", "Foo", handlerClsid, disabled: true, includeNestedValues: true);

        // First reconciliation removes the redundant active copy.
        ContextMenuRegistryCatalog.MoveRegistryKeySafely(
            fixture.GetActiveAbsolutePath("*", "Foo"),
            fixture.GetDisabledAbsolutePath("*", "Foo"));
        Assert.Null(fixture.Open("*", "ContextMenuHandlers\\Foo"));
        Assert.NotNull(fixture.Open("*", "-ContextMenuHandlers\\Foo"));

        // A third-party update recreates the active copy again; repeating the
        // same desired disabled operation must reconcile again without error
        // and without corrupting the preserved disabled destination.
        fixture.CreateHandler("*", "Foo", handlerClsid, includeNestedValues: true);
        ContextMenuRegistryCatalog.MoveRegistryKeySafely(
            fixture.GetActiveAbsolutePath("*", "Foo"),
            fixture.GetDisabledAbsolutePath("*", "Foo"));

        Assert.Null(fixture.Open("*", "ContextMenuHandlers\\Foo"));
        using (var disabled = fixture.Open("*", "-ContextMenuHandlers\\Foo"))
        {
            Assert.NotNull(disabled);
            Assert.Equal(handlerClsid, disabled!.GetValue(null));
            Assert.Equal(42, disabled.GetValue("Dword"));
        }
    }

    [Fact]
    public void MoveRegistryKeySafely_EnableDirection_RemovesRedundantDisabledCopy()
    {
        using var fixture = RegistryMoveFixture.Create();
        const string handlerClsid = "{11111111-1111-1111-1111-111111111111}";

        // The active registration exists and an equivalent disabled copy is
        // left over; enabling the item must remove only the redundant disabled
        // copy and preserve the active registration.
        fixture.CreateHandler("*", "Foo", handlerClsid, includeNestedValues: true);
        fixture.CreateHandler("*", "Foo", handlerClsid, disabled: true, includeNestedValues: true);

        ContextMenuRegistryCatalog.MoveRegistryKeySafely(
            fixture.GetDisabledAbsolutePath("*", "Foo"),
            fixture.GetActiveAbsolutePath("*", "Foo"));

        Assert.Null(fixture.Open("*", "-ContextMenuHandlers\\Foo"));
        using (var active = fixture.Open("*", "ContextMenuHandlers\\Foo"))
        {
            Assert.NotNull(active);
            Assert.Equal(handlerClsid, active!.GetValue(null));
            Assert.Equal(42, active.GetValue("Dword"));
            using var nested = active.OpenSubKey("Nested\\Deep");
            Assert.NotNull(nested);
            Assert.Equal("nested-value", nested!.GetValue("Value"));
        }
    }


    [Fact]
    public void ResolvePhysicalShellExtensionEntries_ReturnsAllPhysicalCopiesSharingTheLogicalId()
    {
        const string id = @"*\shellex\ContextMenuHandlers|AABdzCtx";
        var item = new ContextMenuEntry
        {
            Id = id,
            EntryKind = ContextMenuEntryKind.ShellExtension,
            RegistryPath = @"*\shellex\ContextMenuHandlers\AABdzCtx",
            BackendRegistryPath = @"HKEY_USERS\S-1-5-21-test\Software\Classes\*\shellex\ContextMenuHandlers\AABdzCtx",
            CanToggle = true
        };

        ContextMenuEntry Shell(string backendPath, string? entryId = null, ContextMenuEntryKind kind = ContextMenuEntryKind.ShellExtension)
            => new()
            {
                Id = entryId ?? id,
                EntryKind = kind,
                RegistryPath = @"*\shellex\ContextMenuHandlers\AABdzCtx",
                BackendRegistryPath = backendPath,
                CanToggle = true
            };

        var candidates = new[]
        {
            Shell(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\AABdzCtx"),                              // HKLM active
            Shell(@"HKEY_USERS\S-1-5-21-a\Software\Classes\*\shellex\ContextMenuHandlers\AABdzCtx"),                        // HKU active
            Shell(@"HKEY_USERS\S-1-5-21-a\Software\Classes\*\shellex\-ContextMenuHandlers\AABdzCtx"),                      // HKU disabled mirror
            Shell(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\SomeOtherHandler", @"*\shellex\ContextMenuHandlers|SomeOtherHandler"), // different key
            Shell(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shell\SomeVerb", kind: ContextMenuEntryKind.ShellVerb)             // different entry kind
        };

        var resolved = ContextMenuRegistryCatalog.ResolvePhysicalShellExtensionEntries(item, candidates);

        Assert.Equal(3, resolved.Count);
        Assert.All(resolved, entry => Assert.Equal(ContextMenuEntryKind.ShellExtension, entry.EntryKind));
        Assert.All(resolved, entry => Assert.Equal(id, entry.Id));
        Assert.Contains(resolved, entry => entry.BackendRegistryPath.Contains(@"\ContextMenuHandlers\AABdzCtx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resolved, entry => entry.BackendRegistryPath.Contains(@"\-ContextMenuHandlers\AABdzCtx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolvePhysicalShellExtensionEntries_FallsBackToSingleItem_WhenNoPhysicalCandidateMatches()
    {
        var item = new ContextMenuEntry
        {
            Id = @"SomeApp.File\shellex\ContextMenuHandlers|Foo",
            EntryKind = ContextMenuEntryKind.ShellExtension,
            RegistryPath = @"SomeApp.File\shellex\ContextMenuHandlers\Foo",
            BackendRegistryPath = @"HKEY_USERS\S-1-5-21-test\Software\Classes\SomeApp.File\shellex\ContextMenuHandlers\Foo",
            CanToggle = true
        };

        var resolved = ContextMenuRegistryCatalog.ResolvePhysicalShellExtensionEntries(
            item,
            Array.Empty<ContextMenuEntry>());

        var single = Assert.Single(resolved);
        Assert.Same(item, single);
    }

    [Fact]
    public void SceneShellExtensionPhysicalResolution_IncludesBothActiveAndDisabledMirrorRoots()
    {
        var roots = ContextMenuRegistryCatalog.GetPhysicalSourceRootPaths(
            @"SystemFileAssociations\Video\shellex\ContextMenuHandlers",
            ContextMenuEntryKind.ShellExtension);

        Assert.Equal(2, roots.Count);
        Assert.Contains(@"SystemFileAssociations\Video\shellex\ContextMenuHandlers", roots);
        Assert.Contains(@"SystemFileAssociations\Video\shellex\-ContextMenuHandlers", roots);
    }

    [Fact]
    public void SceneOnlyShellExtension_WhenRegularSnapshotMisses_UsesVerifiedDisabledPhysicalEntry()
    {
        const string itemId = @"SystemFileAssociations\Video\shellex\ContextMenuHandlers|SceneHandler";
        const string handlerClsid = "{11111111-1111-1111-1111-111111111111}";
        var activePath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\SystemFileAssociations\Video\shellex\ContextMenuHandlers\SceneHandler";
        var disabledPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\SystemFileAssociations\Video\shellex\-ContextMenuHandlers\SceneHandler";
        var item = CreateSceneShellExtensionEntry(itemId, activePath, handlerClsid, enabled: true);
        var disabledPhysicalEntry = CreateSceneShellExtensionEntry(itemId, disabledPath, handlerClsid, enabled: false);

        var result = ContextMenuRegistryCatalog.ReconcileShellExtensionMutation(
            item,
            [disabledPhysicalEntry],
            refreshedLogicalEntry: null,
            requestedEnabled: false);

        Assert.True(result.IsVerified);
        Assert.True(result.UsedPhysicalSourceFallback);
        Assert.Equal(itemId, result.Entry!.Id);
        Assert.False(result.Entry.IsEnabled);
        Assert.Equal(disabledPath, result.Entry.BackendRegistryPath);
        Assert.Empty(result.ActivePaths);
        Assert.Equal(disabledPath, Assert.Single(result.DisabledPaths));
    }

    [Fact]
    public void SceneShellExtension_RoundTripsBetweenMirrorsWithStableLogicalId()
    {
        using var fixture = RegistryMoveFixture.Create();
        const string classRoot = @"SystemFileAssociations\Video";
        const string keyName = "SceneHandler";
        const string handlerClsid = "{11111111-1111-1111-1111-111111111111}";
        const string itemId = @"SystemFileAssociations\Video\shellex\ContextMenuHandlers|SceneHandler";
        fixture.CreateHandler(classRoot, keyName, handlerClsid, includeNestedValues: true);

        var activePath = fixture.GetActiveAbsolutePath(classRoot, keyName);
        var disabledPath = fixture.GetDisabledAbsolutePath(classRoot, keyName);
        var activeItem = CreateSceneShellExtensionEntry(itemId, activePath, handlerClsid, enabled: true);

        ContextMenuRegistryCatalog.MoveRegistryKeySafely(activePath, disabledPath);
        Assert.Null(fixture.Open(classRoot, $@"ContextMenuHandlers\{keyName}"));
        Assert.NotNull(fixture.Open(classRoot, $@"-ContextMenuHandlers\{keyName}"));
        var disabledResult = ContextMenuRegistryCatalog.ReconcileShellExtensionMutation(
            activeItem,
            [CreateSceneShellExtensionEntry(itemId, disabledPath, handlerClsid, enabled: false)],
            refreshedLogicalEntry: null,
            requestedEnabled: false);
        Assert.True(disabledResult.IsVerified);
        Assert.Equal(itemId, disabledResult.Entry!.Id);

        ContextMenuRegistryCatalog.MoveRegistryKeySafely(disabledPath, activePath);
        Assert.NotNull(fixture.Open(classRoot, $@"ContextMenuHandlers\{keyName}"));
        Assert.Null(fixture.Open(classRoot, $@"-ContextMenuHandlers\{keyName}"));
        var enabledResult = ContextMenuRegistryCatalog.ReconcileShellExtensionMutation(
            disabledResult.Entry,
            [CreateSceneShellExtensionEntry(itemId, activePath, handlerClsid, enabled: true)],
            refreshedLogicalEntry: null,
            requestedEnabled: true);
        Assert.True(enabledResult.IsVerified);
        Assert.Equal(itemId, enabledResult.Entry!.Id);
        Assert.True(enabledResult.Entry.IsEnabled);

        ContextMenuRegistryCatalog.MoveRegistryKeySafely(activePath, disabledPath);
        Assert.Null(fixture.Open(classRoot, $@"ContextMenuHandlers\{keyName}"));
        Assert.NotNull(fixture.Open(classRoot, $@"-ContextMenuHandlers\{keyName}"));
        var finalDisabledResult = ContextMenuRegistryCatalog.ReconcileShellExtensionMutation(
            enabledResult.Entry,
            [CreateSceneShellExtensionEntry(itemId, disabledPath, handlerClsid, enabled: false)],
            refreshedLogicalEntry: null,
            requestedEnabled: false);
        Assert.True(finalDisabledResult.IsVerified);
        Assert.Equal(itemId, finalDisabledResult.Entry!.Id);
        Assert.False(finalDisabledResult.Entry.IsEnabled);
    }

    [Fact]
    public void SceneShellExtension_HandlerMismatchOrAdditionalActiveCopy_FailsVerification()
    {
        const string itemId = @"SystemFileAssociations\Video\shellex\ContextMenuHandlers|SceneHandler";
        const string handlerClsid = "{11111111-1111-1111-1111-111111111111}";
        var activePath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\SystemFileAssociations\Video\shellex\ContextMenuHandlers\SceneHandler";
        var disabledPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\SystemFileAssociations\Video\shellex\-ContextMenuHandlers\SceneHandler";
        var item = CreateSceneShellExtensionEntry(itemId, activePath, handlerClsid, enabled: true);

        var mismatch = ContextMenuRegistryCatalog.ReconcileShellExtensionMutation(
            item,
            [CreateSceneShellExtensionEntry(itemId, disabledPath, "{22222222-2222-2222-2222-222222222222}", enabled: false)],
            refreshedLogicalEntry: null,
            requestedEnabled: false);
        Assert.False(mismatch.IsVerified);
        Assert.Equal(disabledPath, Assert.Single(mismatch.HandlerMismatchedPaths));

        var duplicateActive = ContextMenuRegistryCatalog.ReconcileShellExtensionMutation(
            item,
            [
                CreateSceneShellExtensionEntry(itemId, disabledPath, handlerClsid, enabled: false),
                CreateSceneShellExtensionEntry(itemId, activePath, handlerClsid, enabled: true)
            ],
            refreshedLogicalEntry: null,
            requestedEnabled: false);
        Assert.False(duplicateActive.IsVerified);
        Assert.Equal(activePath, Assert.Single(duplicateActive.MismatchedPhysicalPaths));
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

    private static ContextMenuEntry CreateSceneShellExtensionEntry(
        string itemId,
        string backendRegistryPath,
        string handlerClsid,
        bool enabled)
        => new()
        {
            Id = itemId,
            Category = ContextMenuCategory.File,
            EntryKind = ContextMenuEntryKind.ShellExtension,
            KeyName = "SceneHandler",
            DisplayName = "Scene handler",
            RegistryPath = backendRegistryPath
                .Replace(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(@"HKEY_USERS\S-1-5-21-test\Software\Classes\", string.Empty, StringComparison.OrdinalIgnoreCase),
            BackendRegistryPath = backendRegistryPath,
            SourceRootPath = @"SystemFileAssociations\Video\shellex\ContextMenuHandlers",
            HandlerClsid = handlerClsid,
            IsEnabled = enabled,
            IsPresentInRegistry = true,
            CanToggle = true
        };

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
