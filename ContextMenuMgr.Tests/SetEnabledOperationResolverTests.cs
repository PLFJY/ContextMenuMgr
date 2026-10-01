using System.Reflection;
using System.Diagnostics;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class SetEnabledOperationResolverTests
{
    private static readonly ContextMenuEntry Original = new()
    {
        Id = @"*\shellex\ContextMenuHandlers|Test",
        BackendRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\Test",
        EntryKind = ContextMenuEntryKind.ShellExtension,
        IsEnabled = true,
        IsPresentInRegistry = true
    };

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task TimedOutResponse_UsesTargetedAuthoritativeState(bool actualEnabled, int expected)
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        fake.State = Original with { IsEnabled = actualEnabled };
        var result = await SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));

        Assert.Equal((SetEnabledOutcome)expected, result.Outcome);
        Assert.Equal(actualEnabled, result.Item!.IsEnabled);
        Assert.Equal(1, fake.Mutations);
        Assert.Equal(1, fake.Queries);
    }

    [Fact]
    public async Task FailedVerification_LeavesOutcomeUncertainWithoutRetry()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        fake.QueryFails = true;
        var result = await SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        Assert.Equal(SetEnabledOutcome.Uncertain, result.Outcome);
        Assert.Null(result.Item);
        Assert.Equal(1, fake.Mutations);
        Assert.Equal(1, fake.Queries);
    }

    [Fact]
    public async Task ShutdownCancellation_DoesNotStartOutcomeVerification()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
                TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), shutdown.Token));
        Assert.Equal(0, fake.Queries);
    }

    [Fact]
    public async Task StructuredBackendFailure_IsNotTreatedAsTimeoutOrRetried()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        fake.MutationFailure = new BackendRequestException("Write protected.", "PROTECTED");
        await Assert.ThrowsAsync<BackendRequestException>(() =>
            SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
                TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)));
        Assert.Equal(1, fake.Mutations);
        Assert.Equal(0, fake.Queries);
    }

    [Fact]
    public async Task MutationLongerThanOldFiveSecondLimit_CompletesWithoutVerification()
    {
        var client = DispatchProxy.Create<IBackendClient, TimedOutBackend>();
        var fake = (TimedOutBackend)client;
        fake.State = Original with { IsEnabled = false };
        fake.MutationDelay = TimeSpan.FromMilliseconds(5100);
        var elapsed = Stopwatch.StartNew();
        var result = await SetEnabledOperationResolver.ExecuteAsync(client, Original, false,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        Assert.True(elapsed.Elapsed > TimeSpan.FromSeconds(5));
        Assert.Equal(SetEnabledOutcome.Applied, result.Outcome);
        Assert.Equal(0, fake.Queries);
    }

    public class TimedOutBackend : DispatchProxy
    {
        public ContextMenuEntry? State { get; set; }
        public bool QueryFails { get; set; }
        public Exception? MutationFailure { get; set; }
        public TimeSpan? MutationDelay { get; set; }
        public int Mutations { get; private set; }
        public int Queries { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IBackendClient.SetEnabledAsync))
            {
                Mutations++;
                if (MutationDelay is { } delay)
                    return DelayedSuccessAsync(delay, (CancellationToken)args![2]!);
                return Task.FromException<ContextMenuEntry?>(MutationFailure ?? new TimeoutException("Response was not delivered."));
            }
            if (targetMethod?.Name == nameof(IBackendClient.GetContextMenuItemStateAsync))
            {
                Queries++;
                return QueryFails
                    ? Task.FromException<ContextMenuEntry?>(new TimeoutException("Query unavailable."))
                    : Task.FromResult(State);
            }
            throw new NotSupportedException(targetMethod?.Name);
        }

        private async Task<ContextMenuEntry?> DelayedSuccessAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return State;
        }
    }
}
