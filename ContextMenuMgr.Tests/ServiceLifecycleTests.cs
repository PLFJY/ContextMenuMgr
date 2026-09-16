using ContextMenuMgr.Backend.Hosting;
using ContextMenuMgr.Frontend.Services;
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
    public void FailedScConfigDoesNotReportSuccessOrCommitPolicy()
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
        Assert.Equal(5, result.ScExitCode);
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

    [Fact]
    public void RecoveryCommandsAreConservativeAndIdempotent()
    {
        var first = BackendServiceBootstrapper.BuildRecoveryScCommands("ContextMenuManagerPlusService");
        var second = BackendServiceBootstrapper.BuildRecoveryScCommands("ContextMenuManagerPlusService");

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(static value => string.Join('|', value)), second.Select(static value => string.Join('|', value)));
        Assert.Equal(
            "failure|ContextMenuManagerPlusService|reset=|86400|actions=|restart/5000/restart/15000/restart/60000",
            string.Join('|', first[0]));
        Assert.Equal("failureflag|ContextMenuManagerPlusService|1", string.Join('|', first[1]));
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
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    public void FrontendClosePreservesEnabledAutostart(
        bool keepBackgroundAfterClose,
        bool autoStartOnLogin,
        bool expectedBackendShutdown,
        bool expectedTrayExit)
    {
        var actions = FrontendCloseLifecyclePolicy.Evaluate(keepBackgroundAfterClose, autoStartOnLogin);

        Assert.Equal(expectedBackendShutdown, actions.RequestBackendShutdown);
        Assert.Equal(expectedTrayExit, actions.RequestTrayHostExit);
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
            FailureActionsOnNonCrashFailures: recoveryConfigured,
            CurrentStatus: status,
            SystemDrive: @"C:\",
            ServiceExeDrive: @"C:\");
}
