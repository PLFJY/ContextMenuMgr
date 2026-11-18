using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: real registry read-back, durable exact undo, conflict preservation and scope boundaries.
public sealed class DocumentIconRepairMutationTests : IDisposable
{
    private readonly string _path = @"Software\ContextMenuMgr.Tests\DocumentIconRepair\" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _root;
    private readonly RegistryKey _user;
    private readonly RegistryKey _machine;
    private const string OfficeIcon = @"C:\Microsoft Office\root\Office16\wordicon.exe,13";
    private const string WpsIcon = @"%LOCALAPPDATA%\Kingsoft\WPS Office\office6\wpsofficeicon.dll,37";
    private const string ProgId = "Word.Document.12";

    public DocumentIconRepairMutationTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_path)!;
        _user = _root.CreateSubKey("User")!;
        _machine = _root.CreateSubKey("Machine")!;
        Directory.CreateDirectory(_directory);
        using var office = _machine.CreateSubKey($@"{ProgId}\DefaultIcon")!;
        office.SetValue("", OfficeIcon);
    }

    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    public async Task Repair_InheritsOfficeIcon_AndRestoresPersistedExactOriginalWithoutChangingAssociations(RegistryValueKind kind)
    {
        using (var key = _user.CreateSubKey($@"{ProgId}\DefaultIcon")!)
        {
            key.SetValue("", WpsIcon, kind);
            key.SetValue("Unrelated", new byte[] { 1, 2, 3 });
        }
        using (var key = _user.CreateSubKey(@"WPS.Docx.6\DefaultIcon")!) key.SetValue("", WpsIcon);
        using (var key = _user.CreateSubKey(@".docx")!) key.SetValue("", "WPS.Docx.6");
        var originals = DocumentIconRepairMutation.Capture(_user, _machine);
        Assert.Single(originals);
        var logger = new FileLogger(Path.Combine(_directory, "backend.log"));
        var store = new ContextMenuStateStore(Path.Combine(_directory, "state.json"), logger);
        await store.SaveAsync(new Dictionary<string, PersistedContextMenuState> {
            ["internal:document-icon-repair|test"] = new() { Id = "internal:document-icon-repair|test",
                SourceRootPath = "internal:document-icon-repair", DocumentIconRepair = new() { UserSid = "test", Backups = originals } }
        }, CancellationToken.None);

        var repair = new DocumentIconRepairMutation(_user, _machine, originals, true);
        repair.Apply(_user);
        Assert.True(DocumentIconRepairMutation.IsApplied(_user, _machine, originals));
        using (var key = _user.OpenSubKey($@"{ProgId}\DefaultIcon")!) Assert.Null(key.GetValue(""));
        using (var key = _machine.OpenSubKey($@"{ProgId}\DefaultIcon")!) Assert.Equal(OfficeIcon, key.GetValue(""));
        using (var key = _user.OpenSubKey(@"WPS.Docx.6\DefaultIcon")!) Assert.Equal(WpsIcon, key.GetValue(""));
        using (var key = _user.OpenSubKey(".docx")!) Assert.Equal("WPS.Docx.6", key.GetValue(""));

        var saved = await new ContextMenuStateStore(Path.Combine(_directory, "state.json"), logger).LoadAsync(CancellationToken.None);
        var undo = new DocumentIconRepairMutation(_user, _machine, saved.Values.Single().DocumentIconRepair!.Backups, false);
        undo.Apply(_user);
        using var restored = _user.OpenSubKey($@"{ProgId}\DefaultIcon")!;
        Assert.Equal(kind, restored.GetValueKind(""));
        Assert.Equal(WpsIcon, restored.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.GetValue("Unrelated"));
    }

    [Fact]
    public void AlreadyHealthyIcons_AreDetectedWithoutRecoveryMetadata_AndWpsOwnIconsArePreserved()
    {
        using (var key = _user.CreateSubKey(@"WPS.Docx.6\DefaultIcon")!) key.SetValue("", WpsIcon);
        using (var key = _user.CreateSubKey(".docx")!) key.SetValue("", "WPS.Docx.6");
        Assert.True(DocumentIconRepairMutation.IsHealthy(_user, _machine));
        SetOverride(ProgId, WpsIcon);
        Assert.False(DocumentIconRepairMutation.IsHealthy(_user, _machine));
        var originals = DocumentIconRepairMutation.Capture(_user, _machine);
        new DocumentIconRepairMutation(_user, _machine, originals, true).Apply(_user);
        Assert.True(DocumentIconRepairMutation.IsHealthy(_user, _machine));
        new DocumentIconRepairMutation(_user, _machine, originals, false).Apply(_user);
        Assert.False(DocumentIconRepairMutation.IsHealthy(_user, _machine));
        using var unchanged = _user.OpenSubKey(".docx")!;
        Assert.Equal("WPS.Docx.6", unchanged.GetValue(""));
    }

    [Fact]
    public void ExcelTemplates_UseInstalledOfficeRegistration_AndIgnoreOrphanWpsClassOverrides()
    {
        using (var key = _machine.CreateSubKey(@".xltx")!) key.SetValue("", "Excel.Template");
        using (var key = _machine.CreateSubKey(@"Excel.Template\DefaultIcon")!) key.SetValue("", @"C:\Office16\xlicons.exe,2");
        SetOverride("Excel.Template", WpsIcon);
        SetOverride("Excel.Template.12", WpsIcon);
        var originals = DocumentIconRepairMutation.Capture(_user, _machine);
        Assert.Equal("Excel.Template", Assert.Single(originals).ProgId);
        new DocumentIconRepairMutation(_user, _machine, originals, true).Apply(_user);
        Assert.True(DocumentIconRepairMutation.IsHealthy(_user, _machine));
        using var orphan = _user.OpenSubKey(@"Excel.Template.12\DefaultIcon")!;
        Assert.Equal(WpsIcon, orphan.GetValue(""));
        new DocumentIconRepairMutation(_user, _machine, originals, false).Apply(_user);
        using var restored = _user.OpenSubKey(@"Excel.Template\DefaultIcon")!;
        Assert.Equal(WpsIcon, restored.GetValue(""));
    }

    [Fact]
    public void Restore_RejectsNewerExternalIconBeforeChangingAnyOtherTarget()
    {
        SetOverride(ProgId, WpsIcon);
        using (var key = _machine.CreateSubKey(@"Excel.Sheet.12\DefaultIcon")!) key.SetValue("", @"C:\Office16\xlicons.exe,1");
        SetOverride("Excel.Sheet.12", WpsIcon);
        var originals = DocumentIconRepairMutation.Capture(_user, _machine);
        new DocumentIconRepairMutation(_user, _machine, originals, true).Apply(_user);
        SetOverride("Excel.Sheet.12", @"C:\AnotherApp\custom.ico");
        var error = Assert.Throws<DocumentIconRepairException>(() => new DocumentIconRepairMutation(_user, _machine, originals, false));
        Assert.Equal(PipeErrorCodes.DocumentIconRepairConflict, error.ErrorCode);
        using var word = _user.OpenSubKey($@"{ProgId}\DefaultIcon")!;
        Assert.Null(word.GetValue(""));
        using var excel = _user.OpenSubKey(@"Excel.Sheet.12\DefaultIcon")!;
        Assert.Equal(@"C:\AnotherApp\custom.ico", excel.GetValue(""));
    }

    [Fact]
    public void InterruptedRepair_CanUndoBothAlreadyRepairedAndUnchangedOriginalValues()
    {
        SetOverride(ProgId, WpsIcon);
        using (var key = _machine.CreateSubKey(@"Excel.Sheet.12\DefaultIcon")!) key.SetValue("", @"C:\Office16\xlicons.exe,1");
        SetOverride("Excel.Sheet.12", WpsIcon);
        var originals = DocumentIconRepairMutation.Capture(_user, _machine);
        using (var key = _user.OpenSubKey($@"{ProgId}\DefaultIcon", true)!) key.DeleteValue("");
        new DocumentIconRepairMutation(_user, _machine, originals, false).Apply(_user);
        using var word = _user.OpenSubKey($@"{ProgId}\DefaultIcon")!;
        Assert.Equal(WpsIcon, word.GetValue(""));
        using var excel = _user.OpenSubKey(@"Excel.Sheet.12\DefaultIcon")!;
        Assert.Equal(WpsIcon, excel.GetValue(""));
    }

    [Fact]
    public void Repair_PreservesUnrelatedOverrides_AndRequiresAnOfficeMachineSource()
    {
        SetOverride(ProgId, @"C:\Custom\word.ico");
        Assert.Empty(DocumentIconRepairMutation.Capture(_user, _machine));
        SetOverride(ProgId, WpsIcon);
        using (var key = _machine.OpenSubKey($@"{ProgId}\DefaultIcon", true)!) key.SetValue("", WpsIcon);
        var error = Assert.Throws<DocumentIconRepairException>(() => DocumentIconRepairMutation.Capture(_user, _machine));
        Assert.Equal(PipeErrorCodes.DocumentIconRepairSourceUnavailable, error.ErrorCode);
        using var unchanged = _user.OpenSubKey($@"{ProgId}\DefaultIcon")!;
        Assert.Equal(WpsIcon, unchanged.GetValue(""));
        using (var key = _user.OpenSubKey($@"{ProgId}\DefaultIcon", true)!) key.DeleteValue("");
        Assert.False(DocumentIconRepairMutation.IsHealthy(_user, _machine));
        var machineError = Assert.Throws<DocumentIconRepairException>(() => DocumentIconRepairMutation.Capture(_user, _machine));
        Assert.Equal(PipeErrorCodes.DocumentIconRepairSourceUnavailable, machineError.ErrorCode);
        Assert.Null(unchanged.GetValue(""));
    }

    [Fact]
    public void Rollback_DoesNotOverwriteNewerExternalIcon()
    {
        SetOverride(ProgId, WpsIcon);
        var mutation = new DocumentIconRepairMutation(_user, _machine, DocumentIconRepairMutation.Capture(_user, _machine), true);
        mutation.Apply(_user);
        SetOverride(ProgId, @"C:\Newer\icon.ico");
        Assert.False(mutation.TryRollback(null));
        using var key = _user.OpenSubKey($@"{ProgId}\DefaultIcon")!;
        Assert.Equal(@"C:\Newer\icon.ico", key.GetValue(""));
    }

    [Fact]
    public async Task RecoveryRecord_PreventsStateResetFromDiscardingUndo_AndRejectsAnotherSid()
    {
        SetOverride(ProgId, WpsIcon);
        var logger = new FileLogger(Path.Combine(_directory, "backend.log"));
        var store = new ContextMenuStateStore(Path.Combine(_directory, "state.json"), logger);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var id = "internal:document-icon-repair|" + sid;
        await store.SaveAsync(new Dictionary<string, PersistedContextMenuState> {
            [id] = new() { Id = id, SourceRootPath = "internal:document-icon-repair",
                DocumentIconRepair = new() { UserSid = "S-1-5-21-111-222-333-444",
                    Backups = DocumentIconRepairMutation.Capture(_user, _machine) } }
        }, CancellationToken.None);
        var catalog = new ContextMenuRegistryCatalog(logger, store,
            new RegistryBackupService(Path.Combine(_directory, "backups"), logger),
            new BackendProtectionSettingsStore(Path.Combine(_directory, "settings.json"), logger));
        var reset = await catalog.ResetStateDatabaseAsync(CancellationToken.None);
        Assert.False(reset.Success);
        Assert.NotNull((await store.LoadAsync(CancellationToken.None))[id].DocumentIconRepair);
        var context = new BackendUserContext(sid, "test", _directory, _directory, _directory, SessionId: 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetOfficeSuiteCoexistenceStatusAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task ExplicitDisableWithoutBackup_PersistsAcrossReload_AndDoesNotPreventReset()
    {
        var logger = new FileLogger(Path.Combine(_directory, "backend.log"));
        var storagePath = Path.Combine(_directory, "state.json");
        var store = new ContextMenuStateStore(storagePath, logger);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var context = new BackendUserContext(sid, "test", _directory, _directory, _directory, SessionId: 1);
        var catalog = new ContextMenuRegistryCatalog(logger, store,
            new RegistryBackupService(Path.Combine(_directory, "backups"), logger),
            new BackendProtectionSettingsStore(Path.Combine(_directory, "settings.json"), logger));
        var response = await catalog.SetDocumentIconRepairAsync(context, false, CancellationToken.None);
        Assert.True(response.Success);
        Assert.False(response.OfficeSuiteCoexistence!.IsDocumentIconRepairEnabled);
        Assert.False(response.OfficeSuiteCoexistence.CanRestoreDocumentIcons);
        var freshStore = new ContextMenuStateStore(storagePath, logger);
        var saved = await freshStore.LoadAsync(CancellationToken.None);
        var preference = saved["internal:document-icon-repair|" + sid].DocumentIconRepair!;
        Assert.False(preference.Enabled);
        Assert.Empty(preference.Backups);
        var freshCatalog = new ContextMenuRegistryCatalog(logger, freshStore,
            new RegistryBackupService(Path.Combine(_directory, "backups"), logger),
            new BackendProtectionSettingsStore(Path.Combine(_directory, "settings.json"), logger));
        var status = await freshCatalog.GetOfficeSuiteCoexistenceStatusAsync(context, CancellationToken.None);
        Assert.False(status.IsDocumentIconRepairEnabled);
        Assert.False(status.CanRestoreDocumentIcons);
        Assert.Equal(response.OfficeSuiteCoexistence.IsDocumentIconRepairApplied, status.IsDocumentIconRepairApplied);
        Assert.True((await freshCatalog.ResetStateDatabaseAsync(CancellationToken.None)).Success);
    }

    [Fact]
    public void RecoveryRecord_CannotWriteOutsideKnownOfficeIconTargets()
    {
        SetOverride(ProgId, WpsIcon);
        var originals = DocumentIconRepairMutation.Capture(_user, _machine);
        originals[0].ProgId = @"Word.Document.12\shell\command";
        Assert.Throws<InvalidDataException>(() => new DocumentIconRepairMutation(_user, _machine, originals, false));
        Assert.Null(_user.OpenSubKey(@"Word.Document.12\shell"));
    }

    private void SetOverride(string progId, string value)
    {
        using var key = _user.CreateSubKey($@"{progId}\DefaultIcon")!;
        key.SetValue("", value);
    }

    public void Dispose()
    {
        _user.Dispose(); _machine.Dispose(); _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        Directory.Delete(_directory, recursive: true);
    }
}
