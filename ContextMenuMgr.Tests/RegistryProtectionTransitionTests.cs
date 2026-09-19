using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class RegistryProtectionTransitionTests
{
    [Fact]
    public async Task PartialDisableFailure_KeepsSettingTrue_AndRetryConvergesWithoutRelocking()
    {
        var targets = CreateTargets(3);
        var accessor = new FakeTargetAccessor(targets, initiallyEnabled: true);
        accessor.State(targets[2]).ApplyFailuresRemaining = 1;
        var store = new FakeSettingsStore(initialValue: true);
        var transition = new RegistryProtectionTransition(accessor, store);

        var first = await transition.ExecuteAsync(
            requestedEnabled: false,
            new BackendProtectionSettings { LockNewContextMenuItems = true },
            targets);

        Assert.False(first.Succeeded);
        Assert.True(first.PersistedValue);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(targets[0]));
        Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(targets[1]));
        Assert.Equal(RegistryProtectionObservedState.Enabled, accessor.ObservedState(targets[2]));
        Assert.Equal(targets[2], Assert.Single(first.Targets, static target => !target.Converged).Target);
        Assert.False(first.RollbackAttempted);

        var second = await transition.ExecuteAsync(
            requestedEnabled: false,
            new BackendProtectionSettings { LockNewContextMenuItems = true },
            targets);

        Assert.True(second.Succeeded);
        Assert.False(second.PersistedValue);
        Assert.Equal(1, store.SaveCount);
        Assert.All(targets, target => Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(target)));
    }

    [Fact]
    public async Task PartialEnableFailure_RollsBackOnlyNewProtection_AndPreservesPreexistingRights()
    {
        var targets = CreateTargets(3);
        var accessor = new FakeTargetAccessor(targets, initiallyEnabled: false);
        accessor.State(targets[0]).Security.AddAccessRule(RegistryProtectionAcl.CreateProtectionRules()[0]);
        var preexisting = RegistryProtectionAcl.Observe(accessor.State(targets[0]).Security);
        accessor.State(targets[2]).ApplyFailuresRemaining = 1;
        var store = new FakeSettingsStore(initialValue: false);
        var transition = new RegistryProtectionTransition(accessor, store);

        var result = await transition.ExecuteAsync(
            requestedEnabled: true,
            new BackendProtectionSettings { LockNewContextMenuItems = false },
            targets);

        Assert.False(result.Succeeded);
        Assert.False(result.PersistedValue);
        Assert.True(result.RollbackAttempted);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(targets[2], Assert.Single(result.Targets, static target => !target.Converged).Target);
        Assert.True(RegistryProtectionAcl.Observe(accessor.State(targets[0]).Security).SemanticallyEquals(preexisting));
        Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(targets[1]));
    }

    [Fact]
    public async Task PartialEnableFailure_ReportsRollbackFailure()
    {
        var targets = CreateTargets(2);
        var accessor = new FakeTargetAccessor(targets, initiallyEnabled: false);
        accessor.State(targets[0]).RollbackFailuresRemaining = 1;
        accessor.State(targets[1]).ApplyFailuresRemaining = 1;
        var transition = new RegistryProtectionTransition(accessor, new FakeSettingsStore(false));

        var result = await transition.ExecuteAsync(
            true,
            new BackendProtectionSettings { LockNewContextMenuItems = false },
            targets);

        Assert.False(result.Succeeded);
        Assert.True(result.RollbackAttempted);
        Assert.False(result.RollbackSucceeded);
        Assert.False(result.Targets[0].RollbackSucceeded);
        Assert.NotNull(result.Targets[0].RollbackError);
    }

    [Fact]
    public async Task EnablePersistenceFailure_RollsBackIntroducedProtection()
    {
        var targets = CreateTargets(2);
        var accessor = new FakeTargetAccessor(targets, initiallyEnabled: false);
        var store = new FakeSettingsStore(initialValue: false) { ThrowOnSave = true };
        var transition = new RegistryProtectionTransition(accessor, store);

        var result = await transition.ExecuteAsync(
            true,
            new BackendProtectionSettings { LockNewContextMenuItems = false },
            targets);

        Assert.False(result.Succeeded);
        Assert.False(result.PersistedValue);
        Assert.False(result.SettingsSaveSucceeded);
        Assert.True(result.RollbackAttempted);
        Assert.True(result.RollbackSucceeded);
        Assert.All(targets, target => Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(target)));
    }

    [Fact]
    public async Task DisablePersistenceFailure_DoesNotRelockTargets()
    {
        var targets = CreateTargets(2);
        var accessor = new FakeTargetAccessor(targets, initiallyEnabled: true);
        var store = new FakeSettingsStore(initialValue: true) { ThrowOnSave = true };
        var transition = new RegistryProtectionTransition(accessor, store);

        var result = await transition.ExecuteAsync(
            false,
            new BackendProtectionSettings { LockNewContextMenuItems = true },
            targets);

        Assert.False(result.Succeeded);
        Assert.True(result.PersistedValue);
        Assert.False(result.SettingsSaveSucceeded);
        Assert.False(result.RollbackAttempted);
        Assert.All(targets, target => Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(target)));

        store.ThrowOnSave = false;
        var retry = await transition.ExecuteAsync(
            false,
            new BackendProtectionSettings { LockNewContextMenuItems = true },
            targets);
        Assert.True(retry.Succeeded);
        Assert.False(store.CurrentValue);
        Assert.All(targets, target => Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(target)));
    }

    [Fact]
    public async Task VerificationFailure_DoesNotCommitRequestedState()
    {
        var target = Assert.Single(CreateTargets(1));
        var accessor = new FakeTargetAccessor([target], initiallyEnabled: false);
        accessor.State(target).IgnoreApply = true;
        var store = new FakeSettingsStore(initialValue: false);
        var transition = new RegistryProtectionTransition(accessor, store);

        var result = await transition.ExecuteAsync(
            true,
            new BackendProtectionSettings { LockNewContextMenuItems = false },
            [target]);

        Assert.False(result.Succeeded);
        Assert.False(result.Targets[0].VerificationSucceeded);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(target));
    }

    [Fact]
    public async Task MissingTarget_IsNonApplicable()
    {
        var targets = CreateTargets(2);
        var accessor = new FakeTargetAccessor(targets, initiallyEnabled: false);
        accessor.State(targets[1]).Exists = false;
        var store = new FakeSettingsStore(initialValue: false);
        var transition = new RegistryProtectionTransition(accessor, store);

        var result = await transition.ExecuteAsync(
            true,
            new BackendProtectionSettings { LockNewContextMenuItems = false },
            targets);

        Assert.True(result.Succeeded);
        Assert.True(result.PersistedValue);
        Assert.True(result.Targets[1].VerificationSucceeded);
        Assert.False(result.Targets[1].Exists);
    }

    [Fact]
    public async Task InitialEnableObservationFailure_PreventsEveryMutation()
    {
        var targets = CreateTargets(2);
        var accessor = new FakeTargetAccessor(targets, initiallyEnabled: false);
        accessor.State(targets[1]).ObserveFailuresRemaining = 1;
        var transition = new RegistryProtectionTransition(accessor, new FakeSettingsStore(false));

        var result = await transition.ExecuteAsync(
            true,
            new BackendProtectionSettings { LockNewContextMenuItems = false },
            targets);

        Assert.False(result.Succeeded);
        Assert.Equal(0, accessor.TotalApplyCount);
        Assert.All(targets, target => Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.ObservedState(target)));
    }

    [Fact]
    public async Task MissingFrontendContext_FailsBeforeAnyTargetMutation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var accessor = new FakeTargetAccessor([], initiallyEnabled: false);
            var store = new FakeSettingsStore(initialValue: true);
            var catalog = CreateCatalog(directory, store, accessor);

            var response = await catalog.SetRegistryProtectionSettingAsync(
                false,
                userContext: null,
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.True(response.RegistryProtectionEnabled);
            Assert.Equal(0, accessor.TotalApplyCount);
            Assert.Equal(0, store.SaveCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResetStateDatabase_DoesNotResetProtectionPersistenceWithoutAclTransition()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var accessor = new FakeTargetAccessor([], initiallyEnabled: false);
            var store = new FakeSettingsStore(initialValue: true);
            var catalog = CreateCatalog(directory, store, accessor);

            var response = await catalog.ResetStateDatabaseAsync(CancellationToken.None);

            Assert.True(response.Success);
            Assert.True(store.CurrentValue);
            Assert.Equal(0, store.ResetCount);
            Assert.Equal(0, accessor.TotalApplyCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentCatalogTransitions_AreSerializedThroughPersistence()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current test identity has no SID.");
        var directory = CreateTemporaryDirectory();
        try
        {
            var accessor = new FakeTargetAccessor([], initiallyEnabled: false)
            {
                CreateUnknownTargetsOnDemand = true,
                MutationDelay = TimeSpan.FromMilliseconds(2)
            };
            var store = new FakeSettingsStore(initialValue: false);
            var catalog = CreateCatalog(directory, store, accessor);
            var userContext = new BackendUserContext(
                sid,
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SessionId: null);

            using var start = new ManualResetEventSlim(initialState: false);
            var enableTask = Task.Run(async () =>
            {
                start.Wait();
                return await catalog.SetRegistryProtectionSettingAsync(true, userContext, CancellationToken.None);
            });
            var disableTask = Task.Run(async () =>
            {
                start.Wait();
                return await catalog.SetRegistryProtectionSettingAsync(false, userContext, CancellationToken.None);
            });
            start.Set();
            var responses = await Task.WhenAll(enableTask, disableTask);

            Assert.All(responses, static response => Assert.True(response.Success));
            Assert.Equal(1, accessor.MaxConcurrentMutations);
            Assert.InRange(store.SaveCount, 1, 2);
            Assert.All(
                accessor.States,
                state => Assert.Equal(
                    store.CurrentValue ? RegistryProtectionObservedState.Enabled : RegistryProtectionObservedState.Disabled,
                    RegistryProtectionAcl.Observe(state.Security).State));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WindowsAccessor_ReadsBackAclFromDisposableUserRegistryKey()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current test identity has no SID.");
        var relativePath = $@"{sid}\Software\ContextMenuMgr.Tests.RegistryProtection.{Guid.NewGuid():N}";
        using var created = Registry.Users.CreateSubKey(relativePath, writable: true)
            ?? throw new InvalidOperationException("Unable to create the disposable ACL test key.");
        created.Close();

        var target = new RegistryProtectionTarget(
            RegistryHive.Users,
            $@"HKEY_USERS\{relativePath}",
            relativePath);
        var accessor = new WindowsRegistryProtectionTargetAccessor();
        try
        {
            Assert.True(accessor.ApplyProtectionState(target, enabled: true));
            Assert.Equal(RegistryProtectionObservedState.Enabled, accessor.Observe(target).AclState?.State);

            Assert.True(accessor.ApplyProtectionState(target, enabled: false));
            Assert.Equal(RegistryProtectionObservedState.Disabled, accessor.Observe(target).AclState?.State);
        }
        finally
        {
            try
            {
                accessor.ApplyProtectionState(target, enabled: false);
            }
            finally
            {
                Registry.Users.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            }
        }
    }

    private static IReadOnlyList<RegistryProtectionTarget> CreateTargets(int count)
        => Enumerable.Range(0, count)
            .Select(index => new RegistryProtectionTarget(
                RegistryHive.LocalMachine,
                $@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Test{index}",
                $@"SOFTWARE\Classes\Test{index}"))
            .ToArray();

    private static ContextMenuRegistryCatalog CreateCatalog(
        string directory,
        IRegistryProtectionSettingsStore store,
        IRegistryProtectionTargetAccessor accessor)
    {
        var logger = new FileLogger(Path.Combine(directory, "backend.log"));
        return new ContextMenuRegistryCatalog(
            logger,
            new ContextMenuStateStore(Path.Combine(directory, "state.json"), logger),
            new RegistryBackupService(Path.Combine(directory, "backups"), logger),
            store,
            accessor);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ContextMenuMgr.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeSettingsStore : IRegistryProtectionSettingsStore
    {
        public FakeSettingsStore(bool initialValue)
        {
            CurrentValue = initialValue;
        }

        public bool CurrentValue { get; private set; }

        public bool ThrowOnSave { get; set; }

        public int SaveCount { get; private set; }

        public int ResetCount { get; private set; }

        public Task<BackendProtectionSettings> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new BackendProtectionSettings { LockNewContextMenuItems = CurrentValue });

        public Task SaveAsync(BackendProtectionSettings settings, CancellationToken cancellationToken)
        {
            if (ThrowOnSave)
            {
                throw new IOException("Simulated settings persistence failure.");
            }

            CurrentValue = settings.LockNewContextMenuItems;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            CurrentValue = false;
            ResetCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTargetAccessor : IRegistryProtectionTargetAccessor
    {
        private readonly Dictionary<RegistryProtectionTarget, FakeTargetState> _states;
        private int _concurrentMutations;

        public FakeTargetAccessor(IEnumerable<RegistryProtectionTarget> targets, bool initiallyEnabled)
        {
            _states = targets.ToDictionary(
                static target => target,
                _ => new FakeTargetState(CreateSecurity(initiallyEnabled)));
        }

        public bool CreateUnknownTargetsOnDemand { get; set; }

        public TimeSpan MutationDelay { get; set; }

        public int MaxConcurrentMutations { get; private set; }

        public int TotalApplyCount { get; private set; }

        public IReadOnlyCollection<FakeTargetState> States => _states.Values;

        public FakeTargetState State(RegistryProtectionTarget target)
        {
            if (_states.TryGetValue(target, out var state))
            {
                return state;
            }

            if (!CreateUnknownTargetsOnDemand)
            {
                throw new KeyNotFoundException(target.RegistryPath);
            }

            state = new FakeTargetState(new RegistrySecurity());
            _states.Add(target, state);
            return state;
        }

        public RegistryProtectionObservedState ObservedState(RegistryProtectionTarget target)
            => RegistryProtectionAcl.Observe(State(target).Security).State;

        public RegistryProtectionObservation Observe(RegistryProtectionTarget target)
        {
            var state = State(target);
            if (state.ObserveFailuresRemaining > 0)
            {
                state.ObserveFailuresRemaining--;
                throw new UnauthorizedAccessException("Simulated ACL read failure.");
            }

            return state.Exists
                ? new RegistryProtectionObservation(true, RegistryProtectionAcl.Observe(state.Security))
                : RegistryProtectionObservation.Missing;
        }

        public bool ApplyProtectionState(RegistryProtectionTarget target, bool enabled)
        {
            TotalApplyCount++;
            var state = State(target);
            EnterMutation();
            try
            {
                if (MutationDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(MutationDelay);
                }

                if (state.ApplyFailuresRemaining > 0)
                {
                    state.ApplyFailuresRemaining--;
                    throw new UnauthorizedAccessException("Simulated ACL mutation failure.");
                }

                if (!state.Exists || state.IgnoreApply)
                {
                    return state.Exists;
                }

                if (enabled)
                {
                    RegistryProtectionAcl.ApplyProtectionRules(state.Security);
                }
                else
                {
                    RegistryProtectionAcl.RemoveProtectionRules(state.Security);
                }

                return true;
            }
            finally
            {
                ExitMutation();
            }
        }

        public bool RestoreProtectionState(
            RegistryProtectionTarget target,
            RegistryProtectionAclSnapshot previousState)
        {
            var state = State(target);
            EnterMutation();
            try
            {
                if (state.RollbackFailuresRemaining > 0)
                {
                    state.RollbackFailuresRemaining--;
                    throw new UnauthorizedAccessException("Simulated rollback failure.");
                }

                if (!state.Exists)
                {
                    return false;
                }

                RegistryProtectionAcl.RestoreProtectionState(state.Security, previousState);
                return true;
            }
            finally
            {
                ExitMutation();
            }
        }

        private void EnterMutation()
        {
            var current = Interlocked.Increment(ref _concurrentMutations);
            MaxConcurrentMutations = Math.Max(MaxConcurrentMutations, current);
        }

        private void ExitMutation() => Interlocked.Decrement(ref _concurrentMutations);

        private static RegistrySecurity CreateSecurity(bool enabled)
        {
            var security = new RegistrySecurity();
            if (enabled)
            {
                RegistryProtectionAcl.ApplyProtectionRules(security);
            }

            return security;
        }
    }

    private sealed class FakeTargetState
    {
        public FakeTargetState(RegistrySecurity security)
        {
            Security = security;
        }

        public RegistrySecurity Security { get; }

        public bool Exists { get; set; } = true;

        public bool IgnoreApply { get; set; }

        public int ObserveFailuresRemaining { get; set; }

        public int ApplyFailuresRemaining { get; set; }

        public int RollbackFailuresRemaining { get; set; }
    }
}
