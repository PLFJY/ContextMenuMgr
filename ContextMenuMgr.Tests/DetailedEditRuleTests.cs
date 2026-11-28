using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class DetailedEditRuleTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string OtherSid = "S-1-5-21-1-2-3-1002";
    private const string Notepad = @"CLSID\{b298d29a-a6ed-11de-ba8c-a68e55d89593}\Settings";

    [Fact]
    public async Task BuiltInExplorerRuleConstructsRequestsForBothMachineAndInteractiveUser()
    {
        var settings = new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var localization = new LocalizationService(settings);
        var rule = BuiltInRules(localization)
            .Single(item => item.Clauses.Count == 2 && item.Clauses.All(clause => clause.KeyName == "NoViewContextMenu"));
        var requests = new List<PipeRequest>();
        var bound = new List<PipeRequest>();
        await using var cache = new MenuListCacheService(Path.GetTempPath(), null);
#if CONTEXTMENUMGR_IN_PROCESS
        var transport = new InProcessBackendTransport((request, _, _) =>
        {
            requests.Add(request);
            bound.Add(Bind(request));
            return Task.FromResult(new ContextMenuMgr.Backend.Services.BackendDispatchResult(new PipeResponse { Success = true }));
        }, () => Sid, new ContextMenuMgr.Backend.Services.FileLogger(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log")));
#else
        var transport = new RequestCapture(requests.Add);
#endif
        await using var client = new BackendClient(transport, cache, new MenuListActivityService());

        await new DetailedEditRuleService(client, settings, localization, () => Sid)
            .WriteBooleanAsync(rule, false, default);

        Assert.Equal(new[] { "1", "1" }, requests.Select(request => request.RuleValue));
        Assert.All(requests, request => Assert.Equal(PipeCommand.SetDetailedEditRuleValue, request.Command));
        Assert.StartsWith(@"HKEY_LOCAL_MACHINE\", requests[0].RulePath);
        Assert.StartsWith(@"HKEY_CURRENT_USER\", requests[1].RulePath);
        Assert.Equal(Sid, requests[1].RuleUserSid);
#if CONTEXTMENUMGR_IN_PROCESS
        Assert.Null(requests[0].RuleUserSid);
        Assert.Equal(requests[0], bound[0]);
        Assert.Equal(Sid, bound[1].RuleUserSid);
#else
        Assert.Equal(Sid, requests[0].RuleUserSid);
#endif
    }

#if CONTEXTMENUMGR_IN_PROCESS
    [Fact]
    public void UserHivesBindOnlyToTheVerifiedUser()
    {
        Assert.Equal(Sid, Bind(Request(@"HKCU\Software\Fixture")).RuleUserSid);
        Assert.Equal(Sid, Bind(Request($@"HKU\{Sid}\Software\Fixture")).RuleUserSid);
        Assert.Throws<InvalidOperationException>(() => Bind(Request(@"HKCU\Software\Fixture", OtherSid)));
        Assert.Throws<InvalidOperationException>(() => Bind(Request($@"HKU\{Sid}\Software\Fixture", OtherSid)));
        Assert.Throws<InvalidOperationException>(() => Bind(Request($@"HKU\{OtherSid}\Software\Fixture", Sid)));
    }

    [Fact]
    public void RejectsUnsupportedAndRootOnlyTargets()
    {
        foreach (var path in new[] { @"HKEY_CURRENT_CONFIG\Fixture", "HKLM", $@"HKU\{Sid}",
                     @"HKLM\\Software", @"HKCR\Arbitrary", @"HKCU\..\Fixture" })
            Assert.Throws<InvalidOperationException>(() => Bind(Request(path)));
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("user-overrides-machine")]
    [InlineData("new-settings-for-user")]
    [InlineData("ambiguous-owner")]
    [InlineData("visible-value-mismatch")]
    public void BuiltInClassesRuleBindsVisibleSettingsToItsPhysicalSource(string fixture)
    {
        var rootPath = $@"Software\ContextMenuMgr.Tests\{Guid.NewGuid():N}";
        using var root = Registry.CurrentUser.CreateSubKey(rootPath);
        try
        {
            using var merged = root.CreateSubKey("Merged");
            using var user = root.CreateSubKey("User");
            using var machine = root.CreateSubKey("Machine");
            using var registration = merged.CreateSubKey(Notepad[..Notepad.LastIndexOf('\\')]);
            if (fixture is "machine" or "user-overrides-machine" or "visible-value-mismatch")
            {
                using var machineSetting = machine.CreateSubKey(Notepad);
                machineSetting.SetValue("Title", "Machine");
                using var mergedSetting = merged.CreateSubKey(Notepad);
                mergedSetting.SetValue("Title", fixture == "machine" ? "Machine" : "Interactive");
                if (fixture != "machine")
                {
                    using var userSetting = user.CreateSubKey(Notepad);
                    userSetting.SetValue("Title", fixture == "visible-value-mismatch" ? "Different" : "Interactive");
                }
            }
            else
            {
                using var userClass = user.CreateSubKey(Notepad[..Notepad.LastIndexOf('\\')]);
                if (fixture == "ambiguous-owner")
                    machine.CreateSubKey(Notepad[..Notepad.LastIndexOf('\\')]).Dispose();
            }

            var settings = new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
            var title = BuiltInRules(new LocalizationService(settings)).SelectMany(rule => rule.Clauses)
                .Single(clause => clause.KeyName == "Title"
                    && string.Equals(clause.Path, $@"HKEY_CLASSES_ROOT\{Notepad}", StringComparison.OrdinalIgnoreCase));
            var request = Request(title.Path, Sid) with { RuleKeyName = title.KeyName };
            PipeRequest Resolve() => DetailedEditRegistryTarget.Bind(request, Sid,
                () => root.OpenSubKey("Merged")!, () => root.OpenSubKey("User"), () => root.OpenSubKey("Machine"));
            if (fixture is "ambiguous-owner" or "visible-value-mismatch")
            {
                Assert.Throws<InvalidOperationException>(() => Resolve());
                return;
            }

            var bound = Resolve();
            if (fixture == "machine")
            {
                Assert.Equal($@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\{Notepad}", bound.RulePath);
                Assert.Null(bound.RuleUserSid);
            }
            else
            {
                Assert.Equal($@"HKEY_USERS\{Sid}\Software\Classes\{Notepad}", bound.RulePath);
                Assert.Equal(Sid, bound.RuleUserSid);
            }
        }
        finally
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }

    private static PipeRequest Request(string path, string? sid = null) => new()
    {
        Command = PipeCommand.SetDetailedEditRuleValue, RuleStorageKind = nameof(RuleStorageKind.Registry),
        RulePath = path, RuleKeyName = "Title", RuleUserSid = sid
    };

    private static PipeRequest Bind(PipeRequest request) => DetailedEditRegistryTarget.Bind(request, Sid,
        () => throw new InvalidOperationException("The fixture has no merged Classes view."), () => null, () => null);
#endif

    private static IEnumerable<DetailedEditRuleDefinition> BuiltInRules(LocalizationService localization)
    {
#if CONTEXTMENUMGR_IN_PROCESS
        var catalog = new RuleDictionaryCatalogService(localization, portableUser: null);
#else
        var catalog = new RuleDictionaryCatalogService(localization);
#endif
        return catalog.LoadDetailedEditGroups().SelectMany(group => group.Rules);
    }

#if !CONTEXTMENUMGR_IN_PROCESS
    private sealed class RequestCapture(Action<PipeRequest> capture) : IBackendTransport
    {
        public event Action<BackendNotification>? NotificationReceived { add { } remove { } }
        public bool IsConnected => true;
        public void StartNotifications() { }
        public Task<PipeResponse> SendAsync(PipeRequest request, Action<BackendNotification> onNotification, CancellationToken token)
        {
            capture(request);
            return Task.FromResult(new PipeResponse { Success = true });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#endif
}
