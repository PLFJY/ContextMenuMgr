using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ShellVerbVisibilityTests
{
    [Fact]
    public void ManagedTransaction_DisableThenEnable_RestoresExactOriginalValues()
    {
        WithTestKey(key =>
        {
            key.SetValue("LegacyDisable", "third-party", RegistryValueKind.String);
            key.SetValue("HideBasedOnVelocityId", 1234, RegistryValueKind.DWord);
            key.SetValue("CommandFlags", 0x48, RegistryValueKind.DWord);
            var expectedLegacy = ShellVerbVisibility.CaptureValue(key, "LegacyDisable");
            var expectedVelocity = ShellVerbVisibility.CaptureValue(key, "HideBasedOnVelocityId");
            var expectedFlags = ShellVerbVisibility.CaptureValue(key, "CommandFlags");
            var expectedProgrammatic = ShellVerbVisibility.CaptureValue(key, "ProgrammaticAccessOnly");

            var disable = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, existingProvenance: null);
            disable.Apply(key);
            Assert.True(disable.Verify(key));

            var enable = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: true, disable.Provenance);
            enable.Apply(key);

            Assert.True(ShellVerbVisibility.ValueMatches(key, expectedLegacy));
            Assert.True(ShellVerbVisibility.ValueMatches(key, expectedVelocity));
            Assert.True(ShellVerbVisibility.ValueMatches(key, expectedFlags));
            Assert.True(ShellVerbVisibility.ValueMatches(key, expectedProgrammatic));
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void ManagedTransaction_PreservesPreExistingVisibilityMetadata(
        bool programmaticAccessOnly,
        bool legacyDisable,
        bool velocityMarker)
    {
        WithTestKey(key =>
        {
            if (programmaticAccessOnly)
            {
                key.SetValue("ProgrammaticAccessOnly", "vendor-data", RegistryValueKind.ExpandString);
            }
            if (legacyDisable)
            {
                key.SetValue("LegacyDisable", "vendor-data", RegistryValueKind.String);
            }
            if (velocityMarker)
            {
                key.SetValue("HideBasedOnVelocityId", 0x639bc8, RegistryValueKind.DWord);
            }
            var beforeProgrammatic = ShellVerbVisibility.CaptureValue(key, "ProgrammaticAccessOnly");
            var beforeLegacy = ShellVerbVisibility.CaptureValue(key, "LegacyDisable");
            var beforeVelocity = ShellVerbVisibility.CaptureValue(key, "HideBasedOnVelocityId");

            var disable = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, existingProvenance: null);
            disable.Apply(key);
            var enable = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: true, disable.Provenance);
            enable.Apply(key);

            Assert.True(ShellVerbVisibility.ValueMatches(key, beforeProgrammatic));
            Assert.True(ShellVerbVisibility.ValueMatches(key, beforeLegacy));
            Assert.True(ShellVerbVisibility.ValueMatches(key, beforeVelocity));
        });
    }

    [Theory]
    [InlineData(0x008)]
    [InlineData(0x020)]
    [InlineData(0x040)]
    [InlineData(0x028)]
    [InlineData(0x048)]
    [InlineData(unchecked((int)0x40000008))]
    public void CommandFlags_AreNeverVisibilityOrMutationState(int commandFlags)
    {
        WithTestKey(key =>
        {
            key.SetValue("CommandFlags", commandFlags, RegistryValueKind.DWord);
            Assert.True(ShellVerbVisibility.IsEnabled(key));

            var disable = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, existingProvenance: null);
            disable.Apply(key);
            var enable = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: true, disable.Provenance);
            enable.Apply(key);

            Assert.Equal(commandFlags, Convert.ToInt32(key.GetValue("CommandFlags")));
            Assert.True(ShellVerbVisibility.IsEnabled(key));
        });
    }

    [Fact]
    public void RepeatedDisable_DoesNotReplaceOriginalProvenance()
    {
        WithTestKey(key =>
        {
            var first = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, existingProvenance: null);
            first.Apply(key);
            var original = Assert.Single(first.Provenance!.OriginalValues);
            Assert.False(original.Existed);

            var repeated = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, first.Provenance);
            repeated.Apply(key);

            Assert.Same(first.Provenance, repeated.Provenance);
            Assert.False(Assert.Single(repeated.Provenance!.OriginalValues).Existed);
        });
    }

    [Fact]
    public void Rollback_RestoresCapturedValue_WhenCurrentStillMatchesOurWrite()
    {
        WithTestKey(key =>
        {
            var transaction = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, existingProvenance: null);
            transaction.Apply(key);

            var rollback = transaction.TryRollback(key);

            Assert.True(rollback.Attempted);
            Assert.True(rollback.Succeeded);
            Assert.False(rollback.Conflict);
            Assert.Null(key.GetValue("ProgrammaticAccessOnly"));
        });
    }

    [Fact]
    public void Rollback_DoesNotOverwriteConcurrentExternalMutation()
    {
        WithTestKey(key =>
        {
            var transaction = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, existingProvenance: null);
            transaction.Apply(key);
            key.SetValue("ProgrammaticAccessOnly", "third-party-change", RegistryValueKind.String);

            var rollback = transaction.TryRollback(key);

            Assert.True(rollback.Attempted);
            Assert.False(rollback.Succeeded);
            Assert.True(rollback.Conflict);
            Assert.Equal("third-party-change", key.GetValue("ProgrammaticAccessOnly"));
        });
    }

    [Fact]
    public void Enable_DoesNotOverwriteConcurrentExternalVisibilityValue()
    {
        WithTestKey(key =>
        {
            var disable = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, existingProvenance: null);
            disable.Apply(key);
            key.SetValue("ProgrammaticAccessOnly", "third-party-change", RegistryValueKind.String);

            var exception = Assert.Throws<ShellVerbMutationException>(() =>
                ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: true, disable.Provenance));

            Assert.Equal("SHELL_VERB_VISIBILITY_CONCURRENT_CHANGE", exception.ErrorCode);
            Assert.Equal("third-party-change", key.GetValue("ProgrammaticAccessOnly"));
        });
    }

    [Fact]
    public void RecreatedPhysicalGeneration_RejectsOldProvenance()
    {
        WithTestKey(key =>
        {
            using (var command = key.CreateSubKey("command"))
            {
                command.SetValue(null, "first.exe %1");
            }
            var disable = ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, existingProvenance: null);
            disable.Apply(key);
            using (var command = key.OpenSubKey("command", writable: true)!)
            {
                command.SetValue(null, "replacement.exe %1");
            }

            var exception = Assert.Throws<ShellVerbMutationException>(() =>
                ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: false, disable.Provenance));

            Assert.Equal("SHELL_VERB_PHYSICAL_GENERATION_CONFLICT", exception.ErrorCode);
        });
    }

    [Fact]
    public void GenerationFingerprint_IgnoresDisplayTextButTracksCommandIdentity()
    {
        WithTestKey(key =>
        {
            using (var command = key.CreateSubKey("command"))
            {
                command.SetValue(null, "first.exe %1");
            }
            var original = ShellVerbVisibility.ComputeGenerationFingerprint(key);

            key.SetValue("MUIVerb", "Renamed by the user");
            Assert.Equal(original, ShellVerbVisibility.ComputeGenerationFingerprint(key));

            using (var command = key.OpenSubKey("command", writable: true)!)
            {
                command.SetValue(null, "replacement.exe %1");
            }
            Assert.NotEqual(original, ShellVerbVisibility.ComputeGenerationFingerprint(key));
        });
    }

    [Fact]
    public void ManagedCommandEdit_RefreshesProvenanceForLaterExactRestore()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var relativePath = $@"{sid}\Software\ContextMenuMgr.Tests\ProvenanceRefresh\{Guid.NewGuid():N}";
        var absolutePath = $@"HKEY_USERS\{relativePath}";
        try
        {
            using var key = Registry.Users.CreateSubKey(relativePath, writable: true)!;
            using (var command = key.CreateSubKey("command", writable: true)!)
            {
                command.SetValue(null, "first.exe %1", RegistryValueKind.String);
            }

            var disable = ShellVerbVisibilityTransaction.Create(
                key,
                absolutePath,
                requestedVisible: false,
                existingProvenance: null);
            disable.Apply(key);
            var state = new PersistedContextMenuState
            {
                ShellVerbVisibilityProvenance = [disable.Provenance!]
            };

            using (var command = key.OpenSubKey("command", writable: true)!)
            {
                command.SetValue(null, "second.exe %1", RegistryValueKind.String);
            }
            ContextMenuRegistryCatalog.RefreshShellVerbProvenanceGeneration(state, absolutePath);

            var enable = ShellVerbVisibilityTransaction.Create(
                key,
                absolutePath,
                requestedVisible: true,
                Assert.Single(state.ShellVerbVisibilityProvenance));
            enable.Apply(key);

            Assert.True(enable.Verify(key));
            Assert.Null(key.GetValue(ShellVerbVisibility.ManagedMarkerName));
            using var commandAfter = key.OpenSubKey("command", writable: false)!;
            Assert.Equal("second.exe %1", commandAfter.GetValue(null));
        }
        finally
        {
            Registry.Users.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void UnownedHiddenVerb_CannotBeBlindlyEnabled()
    {
        WithTestKey(key =>
        {
            key.SetValue("LegacyDisable", string.Empty);
            var exception = Assert.Throws<ShellVerbMutationException>(() =>
                ShellVerbVisibilityTransaction.Create(key, key.Name, requestedVisible: true, existingProvenance: null));
            Assert.Equal(PipeErrorCodes.ShellVerbVisibilityProvenanceMissing, exception.ErrorCode);
            Assert.NotNull(key.GetValue("LegacyDisable"));
        });
    }

    [Fact]
    public void ProgrammaticAccessOnly_IsHiddenButExplicitlyInvocable()
    {
        WithTestKey(key =>
        {
            key.SetValue("ProgrammaticAccessOnly", string.Empty);
            var state = ShellVerbVisibility.GetState(key);
            Assert.False(state.ContextMenuVisible);
            Assert.True(state.ProgrammaticallyInvocable);
        });
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Classes\Directory\Background\shell\Powershell", true)]
    [InlineData(@"HKEY_USERS\S-1-5-21-test\Software\Classes\Directory\Background\shell\Powershell", false)]
    [InlineData(@"Directory\Background\shell\Powershell", false)]
    public void ProtectedFallback_OnlyAllowsPhysicalMachineClassesPaths(string path, bool expected)
        => Assert.Equal(expected, ProtectedRegistryMutation.IsEligibleMachineClassesPath(path));

    [Fact]
    public void ProtectedFallback_RejectsUserHiveBeforeAnySecurityChange()
    {
        var exception = Assert.Throws<ProtectedRegistryMutationException>(() =>
            ProtectedRegistryMutation.Execute(
                @"HKEY_USERS\S-1-5-21-test\Software\Classes\Directory\Background\shell\Powershell",
                _ => throw new InvalidOperationException("Must not run."),
                _ => throw new InvalidOperationException("Must not run.")));

        Assert.Equal(PipeErrorCodes.ProtectedRegistryMutationFailed, exception.ErrorCode);
    }

    private static void WithTestKey(Action<RegistryKey> test)
    {
        var path = $@"Software\ContextMenuMgr.Tests\ShellVerbVisibility\{Guid.NewGuid():N}";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)!;
            test(key);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
