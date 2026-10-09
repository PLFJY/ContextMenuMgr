using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextMenuMgr.Frontend.Services;

public sealed class UpdateCheckService : IDisposable
{
    private const string SourceName = nameof(UpdateCheckService);
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/PLFJY/ContextMenuMgr/releases/latest";
    private const string ReleasePageUrl = "https://github.com/PLFJY/ContextMenuMgr/releases/latest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IInfoBarService _infoBarService;
    private readonly LocalizationService _localization;
    private readonly Lock _gate = new();
    private bool _hasStarted;

    public UpdateCheckService(IInfoBarService infoBarService, LocalizationService localization)
    {
        _infoBarService = infoBarService;
        _localization = localization;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ContextMenuManagerPlus");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public void StartInitialCheck()
    {
        lock (_gate)
        {
            if (_hasStarted)
            {
                return;
            }

            _hasStarted = true;
        }

        _ = CheckForUpdatesSilentlyAsync();
    }

    public async Task CheckForUpdatesSilentlyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await GetLatestReleaseAsync(cancellationToken);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                FrontendDebugLog.Warning(SourceName, "GitHub latest release response did not contain a tag name.");
                return;
            }

            if (!TryCreateComparableVersion(release.TagName, out var latestVersion)
                || !TryCreateComparableVersion(GetCurrentVersionText(), out var currentVersion))
            {
                FrontendDebugLog.Warning(
                    SourceName,
                    $"Unable to compare versions. Current={GetCurrentVersionText()}, Latest={release.TagName}");
                return;
            }

            if (latestVersion <= currentVersion)
            {
                FrontendDebugLog.Info(SourceName, $"No update available. Current={currentVersion}, Latest={latestVersion}.");
                return;
            }

            var title = _localization.Translate("UpdateAvailableTitle");
            var message = _localization.Format(
                "UpdateAvailableMessage",
                release.TagName);
            var releaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? ReleasePageUrl : release.HtmlUrl;

            _infoBarService.ShowInformationalInfoBar(title, message, releaseUrl, releaseUrl);
            FrontendDebugLog.Info(SourceName, $"Update available. Current={currentVersion}, Latest={release.TagName}.");
        }
        catch (OperationCanceledException)
        {
            FrontendDebugLog.Info(SourceName, "Update check was cancelled.");
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error(SourceName, ex, "Update check failed. The error was intentionally not shown to the user.");
        }
    }

#if DEBUG
    public void ShowDebugUpdatePrompt()
    {
        var title = _localization.Translate("UpdateAvailableTitle");
        var message = _localization.Format("UpdateAvailableMessage", "DEBUG");
        _infoBarService.ShowInformationalInfoBar(title, message, ReleasePageUrl, ReleasePageUrl);
        FrontendDebugLog.Info(SourceName, "Forced debug update prompt.");
    }
#endif

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken);
    }

    private static string GetCurrentVersionText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateCheckService).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static bool TryCreateComparableVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var metadataIndex = normalized.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        var parts = normalized
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (parts.Count is < 1 or > 4)
        {
            return false;
        }

        while (parts.Count < 4)
        {
            parts.Add("0");
        }

        if (!Version.TryParse(string.Join('.', parts), out var parsedVersion))
        {
            return false;
        }

        version = parsedVersion;
        return true;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;
    }
}
