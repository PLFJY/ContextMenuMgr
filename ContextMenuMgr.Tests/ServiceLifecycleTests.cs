using System.Text;
using System.Text.Json;
using System.Reflection;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Backend.Hosting;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ServiceLifecycleTests
{
    [Fact]
    public void SuccessfulStartupConfigurationCommitsPolicyAfterVerification()
    {
        var policyCommits = 0;

        var result = ExecuteTransition(
            enabled: false,
            configure: new ServiceCommandResult(true, 0, "OK"),
            configuration: CreateConfiguration(startValue: 3, delayed: false),
            commitPolicy: () => policyCommits++);

        Assert.True(result.Success);
        Assert.True(result.PolicyCommitted);
        Assert.Equal("STARTUP_MANUAL", result.Code);
        Assert.Equal(1, policyCommits);
    }

    [Fact]
    public void FailedStartupConfigurationDoesNotReportSuccessOrCommitPolicy()
    {
        var policyCommits = 0;

        var result = ExecuteTransition(
            enabled: false,
            configure: new ServiceCommandResult(false, 5, "Access denied"),
            configuration: CreateConfiguration(startValue: 2, delayed: false),
            commitPolicy: () => policyCommits++);

        Assert.False(result.Success);
        Assert.False(result.PolicyCommitted);
        Assert.Equal("SERVICE_STARTUP_CONFIG_FAILED", result.Code);
        Assert.Equal(5, result.Win32Error);
        Assert.Equal(0, policyCommits);
    }

    [Fact]
    public void StartupConfigurationMismatchDoesNotCommitPolicy()
    {
        var policyCommits = 0;

        var result = ExecuteTransition(
            enabled: false,
            configure: new ServiceCommandResult(true, 0, "OK"),
            configuration: CreateConfiguration(startValue: 2, delayed: false),
            commitPolicy: () => policyCommits++);

        Assert.False(result.Success);
        Assert.Equal("SERVICE_STARTUP_CONFIG_MISMATCH", result.Code);
        Assert.Equal(0, policyCommits);
    }

    [Theory]
    [InlineData(true, @"C:\Applications\ContextMenuMgrPlus\ContextMenuMgr.Backend.exe", @"C:\Windows", "auto")]
    [InlineData(true, @"D:\Applications\ContextMenuMgrPlus\ContextMenuMgr.Backend.exe", @"C:\Windows", "delayed-auto")]
    [InlineData(true, @"E:\Applications\ContextMenuMgrPlus\ContextMenuMgr.Backend.exe", @"C:\Windows", "delayed-auto")]
    [InlineData(true, @"\\server\share\ContextMenuMgr.Backend.exe", @"C:\Windows", "delayed-auto")]
    [InlineData(false, @"C:\Applications\ContextMenuMgrPlus\ContextMenuMgr.Backend.exe", @"C:\Windows", "demand")]
    [InlineData(false, @"D:\Applications\ContextMenuMgrPlus\ContextMenuMgr.Backend.exe", @"C:\Windows", "demand")]
    public void RequestedStartupModeAccountsForSystemVolume(
        bool enabled,
        string servicePath,
        string windowsDirectory,
        string expected)
    {
        Assert.Equal(expected, BackendServiceBootstrapper.GetRequestedStartupMode(enabled, servicePath, windowsDirectory));
    }

    [Fact]
    public void EnablingWithMissingServiceIsAnExplicitFailureAndDoesNotCommitPolicy()
    {
        var committed = false;

        var result = ServiceAutostartTransition.Execute(
            enabled: true,
            serviceExists: false,
            requestedStartupMode: "auto",
            configureStartupMode: () => throw new InvalidOperationException("must not configure"),
            readConfiguration: () => throw new InvalidOperationException("must not read"),
            configureRecovery: () => throw new InvalidOperationException("must not configure recovery"),
            convergeRuntime: () => throw new InvalidOperationException("must not converge"),
            commitPolicy: () => committed = true);

        Assert.False(result.Success);
        Assert.Equal("SERVICE_NOT_INSTALLED", result.Code);
        Assert.False(committed);
    }

    [Fact]
    public void DisablingWithMissingServiceIsIdempotentAndClearsPolicy()
    {
        var committed = false;

        var result = ServiceAutostartTransition.Execute(
            enabled: false,
            serviceExists: false,
            requestedStartupMode: "demand",
            configureStartupMode: () => throw new InvalidOperationException("must not configure"),
            readConfiguration: () => throw new InvalidOperationException("must not read"),
            configureRecovery: () => throw new InvalidOperationException("must not configure recovery"),
            convergeRuntime: () => throw new InvalidOperationException("must not converge"),
            commitPolicy: () => committed = true);

        Assert.True(result.Success);
        Assert.Equal("NOT_INSTALLED", result.Code);
        Assert.True(result.PolicyCommitted);
        Assert.True(committed);
    }

    [Fact]
    public void EnabledStoppedServiceConvergesThroughRunningAndPipeBeforePolicyCommit()
    {
        var calls = new List<string>();
        var configuration = CreateConfiguration(
            startValue: 2,
            delayed: true,
            status: "Stopped",
            recoveryConfigured: true);

        var result = ServiceAutostartTransition.Execute(
            enabled: true,
            serviceExists: true,
            requestedStartupMode: "delayed-auto",
            configureStartupMode: () =>
            {
                calls.Add("configure");
                return new ServiceCommandResult(true, 0, "OK");
            },
            readConfiguration: () => configuration,
            configureRecovery: () =>
            {
                calls.Add("recovery");
                return new ServiceCommandResult(true, 0, "OK");
            },
            convergeRuntime: () =>
            {
                calls.Add("start");
                calls.Add("running");
                calls.Add("ping");
                configuration = configuration with { CurrentStatus = "Running" };
                return new ServiceRuntimeConvergenceResult(true, "OK", "Ping ready");
            },
            commitPolicy: () => calls.Add("policy"));

        Assert.True(result.Success);
        Assert.Equal(["configure", "recovery", "start", "running", "ping", "policy"], calls);
        Assert.Equal("Running", result.ServiceStatus);
    }

    [Theory]
    [InlineData("SERVICE_START_FAILED")]
    [InlineData("SERVICE_NOT_RUNNING")]
    [InlineData("BACKEND_PIPE_NOT_READY")]
    public void RuntimeConvergenceFailureIsNotFullSuccess(string failureCode)
    {
        var committed = false;

        var result = ServiceAutostartTransition.Execute(
            enabled: true,
            serviceExists: true,
            requestedStartupMode: "auto",
            configureStartupMode: () => new ServiceCommandResult(true, 0, "OK"),
            readConfiguration: () => CreateConfiguration(2, delayed: false, recoveryConfigured: true),
            configureRecovery: () => new ServiceCommandResult(true, 0, "OK"),
            convergeRuntime: () => new ServiceRuntimeConvergenceResult(false, failureCode, "failed"),
            commitPolicy: () => committed = true);

        Assert.False(result.Success);
        Assert.Equal(failureCode, result.Code);
        Assert.False(committed);
    }

    [Fact]
    public void RecoveryConfigurationFailureDoesNotCommitPolicy()
    {
        var committed = false;

        var result = ServiceAutostartTransition.Execute(
            enabled: true,
            serviceExists: true,
            requestedStartupMode: "auto",
            configureStartupMode: () => new ServiceCommandResult(true, 0, "OK"),
            readConfiguration: () => CreateConfiguration(2, delayed: false),
            configureRecovery: () => new ServiceCommandResult(false, 5, "Access denied"),
            convergeRuntime: () => throw new InvalidOperationException("must not converge"),
            commitPolicy: () => committed = true);

        Assert.False(result.Success);
        Assert.Equal("SERVICE_RECOVERY_CONFIG_FAILED", result.Code);
        Assert.False(committed);
    }

    [Fact]
    public void UnverifiedRecoveryConfigurationDoesNotCommitPolicy()
    {
        var committed = false;

        var result = ServiceAutostartTransition.Execute(
            enabled: true,
            serviceExists: true,
            requestedStartupMode: "auto",
            configureStartupMode: () => new ServiceCommandResult(true, 0, "OK"),
            readConfiguration: () => CreateConfiguration(2, delayed: false, recoveryConfigured: false),
            configureRecovery: () => new ServiceCommandResult(true, 0, "OK"),
            convergeRuntime: () => throw new InvalidOperationException("must not converge"),
            commitPolicy: () => committed = true);

        Assert.False(result.Success);
        Assert.Equal("SERVICE_RECOVERY_CONFIG_MISMATCH", result.Code);
        Assert.False(committed);
    }

    [Theory]
    [InlineData(86400, 1, 5000, 3, true, true)]
    [InlineData(0, 1, 5000, 3, true, false)]
    [InlineData(86400, 1, 6000, 3, true, false)]
    [InlineData(86400, 2, 5000, 3, true, false)]
    [InlineData(86400, 1, 5000, 2, true, false)]
    [InlineData(86400, 1, 5000, 3, false, false)]
    public void RecoveryReadBackMustMatchActionsResetAndFlagBeforeAutostartCommit(
        uint reset, uint firstType, uint firstDelay, int count, bool flag, bool expected)
    {
        var actions = new ServiceRecoveryAction[] { new(firstType, firstDelay), new(1, 15000), new(1, 60000) }.Take(count).ToArray();
        var observed = new ServiceRecoveryConfiguration(reset, actions, flag);
        var snapshot = CreateConfiguration(2, false, recoveryConfigured: true) with
        {
            FailureActionsPresent = actions.Length > 0,
            FailureActionsMatch = observed.HasExpectedActions,
            FailureActionsOnNonCrashFailures = observed.OnNonCrashFailures
        };
        var committed = false;
        var result = ServiceAutostartTransition.Execute(true, true, "auto",
            () => new(true, 0, "OK"), () => snapshot, () => new(true, 0, "API accepted configuration"),
            () => new(true, "OK", "Running/Ping"), () => committed = true);
        Assert.Equal(expected, observed.IsConfigured);
        Assert.Equal(expected, result.Success);
        Assert.Equal(expected, committed);
    }

    [Fact]
    public void StartupFailureIsRecoveryEligible()
    {
        var state = new BackendServiceTerminationState();

        state.MarkStartupFailure();

        Assert.Equal(BackendServiceStopReason.StartupFailure, state.StopReason);
        Assert.Equal(BackendServiceTerminationState.StartupFailureExitCode, state.ExitCode);
    }

    [Fact]
    public void SuccessfulRuntimeRemainsZeroUntilAnIntentionalStop()
    {
        var state = new BackendServiceTerminationState();

        Assert.Equal(BackendServiceStopReason.Unknown, state.StopReason);
        Assert.Equal(0, state.ExitCode);

        state.MarkIntentionalStop(BackendServiceStopReason.FrontendRequest);

        Assert.Equal(BackendServiceStopReason.FrontendRequest, state.StopReason);
        Assert.Equal(0, state.ExitCode);
    }

    [Theory]
    [InlineData("FrontendRequest")]
    [InlineData("Uninstall")]
    [InlineData("ForceRepair")]
    [InlineData("ExplicitServiceStop")]
    [InlineData("WindowsShutdown")]
    public void IntentionalStopsRemainSuccessful(string reasonName)
    {
        var reason = Enum.Parse<BackendServiceStopReason>(reasonName);
        var state = new BackendServiceTerminationState();

        state.MarkIntentionalStop(reason);

        Assert.Equal(reason, state.StopReason);
        Assert.Equal(0, state.ExitCode);
    }

    [Fact]
    public void UnexpectedServiceRunReturnIsAFailure()
    {
        var state = new BackendServiceTerminationState();

        state.MarkUnexpectedRunReturn();

        Assert.Equal(BackendServiceStopReason.UnexpectedProcessExit, state.StopReason);
        Assert.NotEqual(0, state.ExitCode);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void FrontendCloseHonorsKeepBackgroundSetting(
        bool keepBackgroundAfterClose,
        bool expectedBackendShutdown,
        bool expectedTrayExit)
    {
        var actions = FrontendCloseLifecyclePolicy.Evaluate(keepBackgroundAfterClose);

        Assert.Equal(expectedBackendShutdown, actions.RequestBackendShutdown);
        Assert.Equal(expectedTrayExit, actions.RequestTrayHostExit);
    }

    // Permanent lifecycle invariants. These tests never register, delete or stop
    // an SCM service and are not Windows shutdown integration tests.
    [Fact]
    public async Task OpeningSettingsBeforeBackendReadinessNeverSendsPreferenceWritesOrPrematureQueries()
    {
        using var files = new LifecycleFiles();
        var path = Path.Combine(files.Directory, "frontend-settings.json");
        const string saved = "{\"showTrayIcon\":true,\"lockNewContextMenuItems\":true,\"autoStartOnLogin\":true}";
        File.WriteAllText(path, saved);
        var settings = new FrontendSettingsService(null, path);
        var localization = new LocalizationService(settings);
        var backend = DispatchProxy.Create<IBackendClient, StartupRequestRecorder>();
        var manager = DispatchProxy.Create<IBackendServiceManager, StartupRequestRecorder>();
        var trust = new PortablePackageTrustService();
        // Unused UI actions and TrayHost transport are deliberately absent:
        // this fixture must not reach the real control pipe or SCM.
        await using var workspace = new ContextMenuWorkspaceService(backend, manager,
            null!, null!, null!, null!, null!, localization, settings, null!, trust);
        using var page = new SettingsPageViewModel(settings, new FrontendStartupService(backend),
            null!, workspace, localization, new FrontendThemeService(settings), null!,
            new ExplorerRestartStateService(), trust);

        Assert.False(workspace.IsBackendReady);
        Assert.Empty(((StartupRequestRecorder)(object)backend).Requests);
        Assert.Equal(saved, File.ReadAllText(path));
        Assert.True(settings.Current.AutoStartOnLogin); // Unknown must not become false.
    }

    public class StartupRequestRecorder : DispatchProxy
    {
        public List<string> Requests { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            if (method.Name.StartsWith("add_", StringComparison.Ordinal)
                || method.Name.StartsWith("remove_", StringComparison.Ordinal)) return null;
            if (method.Name == nameof(IAsyncDisposable.DisposeAsync)) return ValueTask.CompletedTask;
            Requests.Add(method.Name);
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType == typeof(Task<bool>)) return Task.FromResult(false);
            if (method.ReturnType == typeof(bool)) return false;
            if (method.ReturnType == typeof(System.ServiceProcess.ServiceControllerStatus?)) return null;
            throw new InvalidOperationException($"Unexpected startup request: {method.Name}");
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("{}", true)]
    [InlineData("{\"keepBackgroundAfterClose\":false}", true)]
    [InlineData("{\"removePortableServiceOnExit\":false}", false)]
    public void PortableSettingsMigrationPreservesExitPolicyThroughSaveAndReset(string? json, bool removal)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Lifecycle-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "frontend-settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            if (json is not null) File.WriteAllText(path, json);
            var settings = new FrontendSettingsService(null, path);
            if (json is null) settings.EnsureInitialSettingsPersisted();
            Assert.Equal(removal, settings.Current.RemovePortableServiceOnExit);
            Assert.False(settings.Current.KeepBackgroundAfterClose);
            Assert.False(settings.Current.AutoStartOnLogin);
            settings.UpdateShowTrayIcon(false);
            Assert.Equal(removal, new FrontendSettingsService(null, path).Current.RemovePortableServiceOnExit);
            using (var stream = File.OpenRead(path)) Assert.Equal(removal, PortableServiceExitPolicy.Read(stream).RemoveOnExit);
            settings.ResetToDefaults();
            using var defaults = File.OpenRead(path);
            var policy = PortableServiceExitPolicy.Read(defaults);
            Assert.True(policy.RemoveOnExit);
            Assert.False(policy.AutoStart);
            Assert.False(policy.KeepBackground);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, false, false, true, false)]
    public void ExitAndShutdownPoliciesKeepRetentionAndAutostartSeparate(
        bool remove, bool autostart, bool background, bool shutdown, bool expected)
    {
        var policy = new PortableServiceExitPolicy(remove, autostart, background);
        Assert.Equal(expected, policy.AllowsRemoval(RuntimePackageKind.Portable, shutdown));
        Assert.False(policy.AllowsRemoval(RuntimePackageKind.Installer, shutdown));
        var actions = FrontendCloseLifecyclePolicy.Evaluate(background);
        Assert.Equal(!background, actions.RequestBackendShutdown);
        Assert.Equal(!background, actions.RequestTrayHostExit);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"removePortableServiceOnExit\":null}")]
    [InlineData("{\"autoStartOnLogin\":\"false\"}")]
    [InlineData("{\"removePortableServiceOnExit\":true,\"RemovePortableServiceOnExit\":false}")]
    public void MalformedOrAmbiguousPolicyDoesNotGrantDeletion(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.ThrowsAny<JsonException>(() => PortableServiceExitPolicy.Read(stream));
    }

    [Fact]
    public void MissingOrInaccessibleSettingsNeverCreateAnEnabledLifecyclePolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Lifecycle-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<FileNotFoundException>(() => FrontendSettingsFile.UpdateLifecycle(path, false, true));
        Assert.False(File.Exists(path));
        try
        {
            File.WriteAllText(path, "{\"removePortableServiceOnExit\":false}");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => FrontendSettingsFile.UpdateLifecycle(path, false, true));
            using var stream = File.OpenRead(path);
            Assert.False(PortableServiceExitPolicy.Read(stream).RemoveOnExit);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExclusiveFrontendIgnoresOfflineProfilesButRequiredOrLoadedAutostartPoliciesStillVetoRemoval()
    {
        // Offline unrelated profiles are not current consumers. The caller's
        // hive and every profile during Windows shutdown remain required.
        PortableServiceCleanup.VerifyUserPolicy(null, requireLoaded: false);
        Assert.Throws<IOException>(() => PortableServiceCleanup.VerifyUserPolicy(null, requireLoaded: true));

        var keyPath = @"Software\ContextMenuMgr-LifecycleTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var hive = Registry.CurrentUser.CreateSubKey(keyPath);
            PortableServiceCleanup.VerifyUserPolicy(hive, requireLoaded: true);
            using var policy = hive.CreateSubKey(@"Software\ContextMenuMgr\Frontend");
            policy.SetValue("StartWithWindows", 0, RegistryValueKind.DWord);
            PortableServiceCleanup.VerifyUserPolicy(hive, requireLoaded: true);
            foreach (var value in new object[] { 1, "0", 0L })
            {
                policy.SetValue("StartWithWindows", value);
                // A loaded user's enabled or malformed policy cannot be
                // ignored even if that user's hive is not required.
                Assert.Throws<IOException>(() => PortableServiceCleanup.VerifyUserPolicy(hive, requireLoaded: false));
            }
            policy.SetValue("StartWithWindows", 0, RegistryValueKind.DWord);
            using var run = hive.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            run.SetValue("ContextMenuManagerPlus.TrayHost", "retained startup command");
            Assert.Throws<IOException>(() => PortableServiceCleanup.VerifyUserPolicy(hive, requireLoaded: false));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AtomicLifecycleCommitPreservesUnrelatedSettingsAndEnforcesExclusion(bool autostart, bool expectedRemoval)
    {
        var path = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Lifecycle-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            FrontendSettingsFile.Write(path, "{\"theme\":2,\"keepBackgroundAfterClose\":true}");
            FrontendSettingsFile.UpdateLifecycle(path, autostart, removeOnExit: true);
            using var stream = File.OpenRead(path);
            var policy = PortableServiceExitPolicy.Read(stream);
            Assert.Equal(autostart, policy.AutoStart);
            Assert.Equal(expectedRemoval, policy.RemoveOnExit);
            Assert.True(policy.KeepBackground);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2, document.RootElement.GetProperty("theme").GetInt32());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("SCM", false)]
    [InlineData("UserPolicy", false)]
    [InlineData("Settings", true)]
    public void FailedPortableStartupTransactionRollsBackBeforeReturning(string stage, bool transitionSuccess)
    {
        using var files = new LifecycleFiles();
        var calls = new List<string>();
        var result = PortableStartupTransaction.Execute(
            files.Directory,
            () => { calls.Add("transition"); return new(transitionSuccess, stage, "failure"); },
            () => { calls.Add("settings"); throw new IOException("settings unavailable"); },
            () => calls.Add("rollback"));
        Assert.False(result.Success);
        Assert.False(PortableServiceExitPolicy.HasUnsettledStartupTransaction(files.Directory));
        Assert.Equal(transitionSuccess ? new[] { "transition", "settings", "rollback" } : new[] { "transition", "rollback" }, calls);
    }

    [Fact]
    public void PortableStartupRollbackFailureIsExplicitAndCannotReportSuccess()
    {
        using var files = new LifecycleFiles();
        var result = PortableStartupTransaction.Execute(
            files.Directory,
            () => new(false, "SCM", "failure"), () => throw new Exception("must not commit"),
            () => throw new IOException("SCM rollback unavailable"));
        Assert.False(result.Success);
        Assert.Equal("PORTABLE_STARTUP_ROLLBACK_FAILED", result.Code);
        Assert.True(PortableServiceExitPolicy.HasUnsettledStartupTransaction(files.Directory));
        result = PortableStartupTransaction.Execute(files.Directory,
            () => throw new Exception("must not retry uncertain SCM write"), () => { }, () => { });
        Assert.False(result.Success);
    }

    [Fact]
    public void CommittedStartupPolicySurvivesConcurrentPreferenceSaveAndClearsVeto()
    {
        using var files = new LifecycleFiles();
        var path = Path.Combine(files.Directory, "frontend-settings.json");
        FrontendSettingsFile.Write(path, "{\"theme\":1}");
        var result = PortableStartupTransaction.Execute(files.Directory,
            () => new(true, "STARTUP_AUTO", "verified"),
            () => FrontendSettingsFile.UpdateLifecycle(path, autoStart: true, removeOnExit: false),
            () => throw new Exception("successful commit must not roll back"));
        Assert.True(result.Success);
        Assert.False(PortableServiceExitPolicy.HasUnsettledStartupTransaction(files.Directory));
        // The frontend still has its old lifecycle snapshot while the UAC
        // process completes. Saving a theme must preserve the new disk policy.
        FrontendSettingsFile.Write(path, "{\"theme\":2,\"autoStartOnLogin\":false,\"removePortableServiceOnExit\":true}", preserveLifecycle: true);
        using var stream = File.OpenRead(path);
        var policy = PortableServiceExitPolicy.Read(stream);
        Assert.True(policy.AutoStart);
        Assert.False(policy.RemoveOnExit);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, document.RootElement.GetProperty("theme").GetInt32());
    }

    [Fact]
    public void LifecycleRollbackDoesNotOverwriteAnExternalPolicyChange()
    {
        using var files = new LifecycleFiles();
        var path = Path.Combine(files.Directory, "frontend-settings.json");
        FrontendSettingsFile.Write(path, "{}");
        var previous = new PortableServiceExitPolicy(true, false, false);
        FrontendSettingsFile.UpdateLifecycle(path, autoStart: true, removeOnExit: false, expected: previous);
        Assert.Throws<IOException>(() => FrontendSettingsFile.UpdateLifecycle(path, false, true, expected: previous));
        using var stream = File.OpenRead(path);
        Assert.True(PortableServiceExitPolicy.Read(stream).AutoStart);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"removePortableServiceOnExit\":false,\"RemovePortableServiceOnExit\":true}")]
    public void UnrelatedFrontendSaveCannotTurnCorruptPolicyIntoDeletionPermission(string json)
    {
        using var files = new LifecycleFiles();
        var path = Path.Combine(files.Directory, "frontend-settings.json");
        File.WriteAllText(path, json);
        var settings = new FrontendSettingsService(null, path);
        settings.UpdateShowTrayIcon(false);
        using var stream = File.OpenRead(path);
        Assert.False(PortableServiceExitPolicy.Read(stream).RemoveOnExit);
    }

    private sealed class LifecycleFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Lifecycle-" + Guid.NewGuid().ToString("N"));
        public LifecycleFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    [Theory]
    [InlineData("\"C:\\App\\backend.exe\" --service", 42, true)]
    [InlineData("\"C:\\OtherCopy\\backend.exe\" --service", 42, false)]
    [InlineData("\"C:\\App\\backend.exe\" --service --other", 42, false)]
    [InlineData("C:\\App\\backend.exe --service", 42, false)]
    [InlineData("\"C:\\App\\backend.exe\" --service", 43, false)]
    public void CleanupRequiresExactRunningRegistration(string imagePath, uint pid, bool expected)
        => Assert.Equal(expected, PortableServiceCleanup.OwnsRegistration(imagePath, @"C:\App\backend.exe", pid, 42));

    [Fact]
    public void AnotherUserSessionTrayHostOrFrontendPreventsFrontendStop()
    {
        Assert.True(PortableServiceConsumers.IsExclusiveFrontend(42, 1, [1], [42]));
        Assert.False(PortableServiceConsumers.IsExclusiveFrontend(42, 1, [1, 2], [42]));
        Assert.False(PortableServiceConsumers.IsExclusiveFrontend(42, 1, [1], [42, 43]));
        Assert.False(PortableServiceConsumers.IsExclusiveFrontend(42, 0, [], [42]));
    }

    [Theory]
    [InlineData(0, "MarkedForDeletion")]
    [InlineData(1072, "AlreadyMarkedForDeletion")]
    [InlineData(1060, "Absent")]
    [InlineData(5, "AccessDenied")]
    [InlineData(1115, "ShutdownInProgress")]
    [InlineData(1722, "ScmUnavailable")]
    [InlineData(87, "NativeFailure")]
    public void EveryDeletionOutcomePrecedesOneSuccessfulIntentionalStop(int error, string code)
    {
        var sequence = new ServiceStopSequence();
        var calls = new List<string>();
        var termination = new BackendServiceTerminationState();
        sequence.Request(
            () => { calls.Add(PortableCleanupResult.FromWin32("DeleteService", error).Code); },
            () => sequence.Stop(null, () => { termination.MarkIntentionalStop(BackendServiceStopReason.FrontendRequest); calls.Add("stop"); }));
        sequence.Request(() => throw new Exception("duplicate deletion"), () => throw new Exception("duplicate request"));
        sequence.Stop(() => throw new Exception("shutdown race"), () => throw new Exception("duplicate cleanup"));
        Assert.Equal(new[] { code, "stop" }, calls);
        Assert.Equal(0, termination.ExitCode); // Existing non-crash SCM recovery must not restart this stop.
    }

    [Fact]
    public void UnexpectedDeletionExceptionCannotBypassResourceCleanup()
    {
        var sequence = new ServiceStopSequence();
        var stops = 0;
        Assert.Throws<IOException>(() => sequence.Stop(() => throw new IOException("SCM unavailable"), () => stops++));
        sequence.Stop(null, () => stops++);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void ConcurrentFrontendAndWindowsShutdownDeleteAndCleanUpAtMostOnce()
    {
        var sequence = new ServiceStopSequence();
        var deletions = 0;
        var stops = 0;
        Parallel.Invoke(
            () => sequence.Request(() => Interlocked.Increment(ref deletions),
                () => sequence.Stop(null, () => Interlocked.Increment(ref stops))),
            () => sequence.Stop(() => Interlocked.Increment(ref deletions), () => Interlocked.Increment(ref stops)));
        Assert.Equal(1, deletions);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task FastReopenObservesPendingDeletionWithoutMutatingService()
    {
        var observations = new Queue<PortableServiceRegistrationState>([
            PortableServiceRegistrationState.PendingDeletion, PortableServiceRegistrationState.Absent]);
        var state = await PortableServiceStartup.WaitForSettledAsync(observations.Dequeue, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(PortableServiceRegistrationState.Absent, state);
        state = await PortableServiceStartup.WaitForSettledAsync(() => PortableServiceRegistrationState.PendingDeletion, TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(PortableServiceRegistrationState.PendingDeletion, state);
        state = await PortableServiceStartup.WaitForSettledAsync(() => PortableServiceRegistrationState.Foreign, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(PortableServiceRegistrationState.Foreign, state);
    }

    [Fact]
    public void PinnedRuntimeIdentityPreventsReplacementUntilDeletionCheckCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "backend.exe");
        var replacement = Path.Combine(directory, "replacement.exe");
        try
        {
            File.WriteAllText(path, "original");
            File.WriteAllText(replacement, "replacement");
            using (var files = new PortableRuntimeIdentity())
            {
                files.Pin(path);
                Assert.True(Record.Exception(() => File.Move(replacement, path, overwrite: true)) is IOException or UnauthorizedAccessException);
                Assert.True(Record.Exception(() => Directory.Move(directory, directory + "-moved")) is IOException or UnauthorizedAccessException);
                Assert.Equal("original", File.ReadAllText(path));
            }
            File.Move(replacement, path, overwrite: true);
            Assert.Equal("replacement", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FailedSettingsSaveRestoresEffectiveLifecycleSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"autoStartOnLogin\":false,\"removePortableServiceOnExit\":false}");
            var settings = new FrontendSettingsService(null, path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(settings.TryUpdateLifecycleSettings(true, false));
            Assert.False(settings.Current.AutoStartOnLogin);
            Assert.False(settings.Current.RemovePortableServiceOnExit);
            Assert.NotNull(settings.LastSaveError);
            using var stream = File.OpenRead(path);
            Assert.False(PortableServiceExitPolicy.Read(stream).AutoStart);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExplicitScmStopNeverAcquiresPortableDeletionSemantics()
    {
        var sequence = new ServiceStopSequence();
        var stops = 0;
        sequence.Stop(null, () => stops++);
        sequence.Request(() => throw new Exception("late frontend delete"), () => throw new Exception("late stop"));
        sequence.Stop(() => throw new Exception("late shutdown delete"), () => stops++);
        Assert.Equal(1, stops);
    }

    private static ServiceAutostartTransitionResult ExecuteTransition(
        bool enabled,
        ServiceCommandResult configure,
        ServiceConfigurationSnapshot configuration,
        Action commitPolicy)
        => ServiceAutostartTransition.Execute(
            enabled,
            serviceExists: true,
            requestedStartupMode: enabled ? "auto" : "demand",
            configureStartupMode: () => configure,
            readConfiguration: () => configuration,
            configureRecovery: () => new ServiceCommandResult(true, 0, "OK"),
            convergeRuntime: () => new ServiceRuntimeConvergenceResult(true, "OK", "Ready"),
            commitPolicy);

    private static ServiceConfigurationSnapshot CreateConfiguration(
        int startValue,
        bool delayed,
        string status = "Stopped",
        bool recoveryConfigured = false)
        => new(
            Exists: true,
            ServiceName: "ContextMenuManagerPlusService",
            ImagePath: @"C:\App\ContextMenuMgr.Backend.exe --service",
            ServiceExePath: @"C:\App\ContextMenuMgr.Backend.exe",
            StartValue: startValue,
            ConfiguredStartType: startValue switch
            {
                2 when delayed => "DelayedAutomatic",
                2 => "Automatic",
                3 => "Manual",
                _ => "Unknown"
            },
            DelayedAutoStart: delayed,
            FailureActionsPresent: recoveryConfigured,
            FailureActionsMatch: recoveryConfigured,
            FailureActionsOnNonCrashFailures: recoveryConfigured,
            CurrentStatus: status,
            SystemDrive: @"C:\",
            ServiceExeDrive: @"C:\");
}
