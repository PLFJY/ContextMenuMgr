using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>
/// Regression tests for WPS/Office synthetic finding approval baselining.
/// </summary>
public sealed class OfficeSuiteCoexistenceApprovalTests
{
    [Fact]
    public async Task UserSelectedDocumentIconProvider_AcknowledgesOnlyTheIconSyntheticFinding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var logger = new FileLogger(Path.Combine(directory, "backend.log"));
            var stateStore = new ContextMenuStateStore(Path.Combine(directory, "state.json"), logger, quarantineDirectory: Path.Combine(directory, "quarantine"));
            var states = new Dictionary<string, PersistedContextMenuState>
            {
                [ContextMenuRegistryCatalog.WpsOfficeDocumentIconSyntheticId] = new()
                {
                    Id = ContextMenuRegistryCatalog.WpsOfficeDocumentIconSyntheticId,
                    SourceRootPath = "special:wps-office-coexistence",
                    IsPendingApproval = true,
                    SuppressNextDetection = true
                },
                ["special:wps-office-association:document-formats"] = new()
                {
                    Id = "special:wps-office-association:document-formats",
                    SourceRootPath = "special:wps-office-coexistence",
                    IsPendingApproval = true
                }
            };
            await stateStore.SaveAsync(states, CancellationToken.None);
            var catalog = new ContextMenuRegistryCatalog(
                logger,
                stateStore,
                new RegistryBackupService(Path.Combine(directory, "backups"), logger),
                new BackendProtectionSettingsStore(Path.Combine(directory, "settings.json"), logger));
            var userContext = new BackendUserContext("S-1-5-21-test", "test", directory, directory, directory, SessionId: 1);

            await catalog.RecordUserSelectedDocumentIconProviderAsync(userContext, CancellationToken.None);

            var saved = await stateStore.LoadAsync(CancellationToken.None);
            Assert.False(saved[ContextMenuRegistryCatalog.WpsOfficeDocumentIconSyntheticId].IsPendingApproval);
            Assert.False(saved[ContextMenuRegistryCatalog.WpsOfficeDocumentIconSyntheticId].SuppressNextDetection);
            Assert.True(saved["special:wps-office-association:document-formats"].IsPendingApproval);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EmptyStateDatabase_ExistingFinding_IsAdoptedWithoutApproval()
    {
        var requiresApproval = OfficeSuiteCoexistenceDetector
            .ShouldMarkNewFindingPendingApproval(hasPersistedBaseline: false);

        Assert.False(requiresApproval);
    }

    [Fact]
    public void EstablishedStateDatabase_NewFinding_RequiresApproval()
    {
        var requiresApproval = OfficeSuiteCoexistenceDetector
            .ShouldMarkNewFindingPendingApproval(hasPersistedBaseline: true);

        Assert.True(requiresApproval);
    }

    [Fact]
    public void RegularMenuBaseline_DoesNotCountAsWpsOfficeBaseline()
    {
        var states = new[]
        {
            new PersistedContextMenuState { SourceRootPath = @"*\shell" }
        };

        Assert.False(ContextMenuRegistryCatalog.HasWpsOfficeSyntheticBaseline(states));
    }

    [Fact]
    public void ExistingWpsOfficeFinding_CountsAsWpsOfficeBaseline()
    {
        var states = new[]
        {
            new PersistedContextMenuState { SourceRootPath = "special:wps-office-coexistence" }
        };

        Assert.True(ContextMenuRegistryCatalog.HasWpsOfficeSyntheticBaseline(states));
    }
}
