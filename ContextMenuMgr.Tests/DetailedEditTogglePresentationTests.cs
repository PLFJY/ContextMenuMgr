using System.Reflection;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class DetailedEditTogglePresentationTests
{
    [Fact]
    public async Task BooleanWrite_BusyBlocksOverlappingChangesAndClearsOnSuccess()
    {
        var settings = new FrontendSettingsService(null,
            Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid() + ".json"));
        var localization = new LocalizationService(settings);
        var backend = DispatchProxy.Create<IBackendClient, PendingRuleWrite>();
        var proxy = (PendingRuleWrite)backend;
        var service = new DetailedEditRuleService(backend, settings, localization);
        var clause = new DetailedEditRuleClauseDefinition(
            RuleStorageKind.Registry,
            "HKEY_CURRENT_USER\\Software\\ContextMenuMgrTests\\" + Guid.NewGuid().ToString("N"),
            null, "Enabled", RegistryValueKind.DWord, "1", "0");
        var definition = new DetailedEditRuleDefinition(
            "Test rule", null, RuleValueEditorKind.Boolean, false, 0, 0, 1, [clause]);
        var item = new DetailedEditRuleViewModel(definition, service, localization);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.IsBusy) && !item.IsBusy) idle.TrySetResult();
        };

        Assert.True(item.BoolValue); // Missing value defaults to enabled.
        item.BoolValue = false;
        Assert.True(item.IsBusy);
        Assert.False(item.CanToggle);
        await proxy.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        item.BoolValue = true;
        Assert.False(item.BoolValue);
        Assert.Equal(1, proxy.Calls);

        proxy.Complete();
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(item.IsBusy);
        Assert.True(item.CanToggle);
        Assert.False(item.BoolValue);
    }

    public class PendingRuleWrite : DispatchProxy
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IBackendClient.SetDetailedEditRuleValueAsync))
                throw new NotSupportedException(targetMethod?.Name);
            Calls++;
            Invoked.TrySetResult();
            return _completion.Task;
        }

        public void Complete() => _completion.TrySetResult();
    }
}
