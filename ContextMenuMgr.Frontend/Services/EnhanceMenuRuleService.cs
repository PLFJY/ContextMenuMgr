using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the enhance Menu Rule Service.
/// </summary>
public sealed class EnhanceMenuRuleService
{
    private readonly IBackendClient _backendClient;
    private readonly LocalizationService _localization;
    private readonly FrontendSettingsService _settingsService;
#if CONTEXTMENUMGR_IN_PROCESS
    private readonly PortableInteractiveUser? _portableUser;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="EnhanceMenuRuleService"/> class.
    /// </summary>
#if CONTEXTMENUMGR_IN_PROCESS
    internal EnhanceMenuRuleService(
#else
    public EnhanceMenuRuleService(
#endif
        IBackendClient backendClient,
        LocalizationService localization,
        FrontendSettingsService settingsService
#if CONTEXTMENUMGR_IN_PROCESS
        , PortableInteractiveUser? portableUser = null
#endif
        )
    {
        _backendClient = backendClient;
        _localization = localization;
        _settingsService = settingsService;
#if CONTEXTMENUMGR_IN_PROCESS
        _portableUser = portableUser;
#endif
    }

    /// <summary>
    /// Executes is Enabled.
    /// </summary>
    public bool IsEnabled(EnhanceMenuItemDefinition definition)
    {
        var relativeGroupPath = NormalizeClassesRootRelativePath(definition.GroupRegistryPath);
        if (string.IsNullOrWhiteSpace(relativeGroupPath))
        {
            return false;
        }

        return string.Equals(definition.Kind, "ShellEx", StringComparison.OrdinalIgnoreCase)
            ? IsShellExEnabled(relativeGroupPath, definition)
            : IsShellEnabled(relativeGroupPath, definition);
    }

    /// <summary>
    /// Sets enabled Async.
    /// </summary>
    public Task SetEnabledAsync(
        EnhanceMenuItemDefinition definition,
        bool enable,
        CancellationToken cancellationToken)
    {
        var relativeGroupPath = NormalizeClassesRootRelativePath(definition.GroupRegistryPath);
        var targetPath = string.Equals(definition.Kind, "ShellEx", StringComparison.OrdinalIgnoreCase)
            ? $@"{relativeGroupPath}\shellex\ContextMenuHandlers"
            : $@"{relativeGroupPath}\shell";
        if (RegistryProtectionDialog.ShouldBlockProtectedPathMutation(_settingsService, targetPath))
        {
            throw new BackendRequestException(
                _localization.Translate("RegistryProtectionBlocksEditMessage"),
                PipeErrorCodes.RegistryWriteProtectionEnabled);
        }

        return _backendClient.SetEnhanceMenuItemEnabledAsync(
            definition.GroupRegistryPath,
            definition.RawXml,
            enable,
            _localization.CurrentCultureName,
            cancellationToken);
    }

    private bool IsShellEnabled(string relativeGroupPath, EnhanceMenuItemDefinition definition)
    {
        using var classesRoot = OpenClassesRoot();
        using var key = classesRoot.OpenSubKey($@"{relativeGroupPath}\shell\{definition.KeyName}", writable: false);
        return key is not null;
    }

    private bool IsShellExEnabled(string relativeGroupPath, EnhanceMenuItemDefinition definition)
    {
        if (!Guid.TryParse(definition.GuidText, out var expectedGuid))
        {
            return false;
        }

        using var classesRoot = OpenClassesRoot();
        using var handlersKey = classesRoot.OpenSubKey($@"{relativeGroupPath}\shellex\ContextMenuHandlers", writable: false);
        if (handlersKey is null)
        {
            return false;
        }

        foreach (var subKeyName in handlersKey.GetSubKeyNames())
        {
            using var subKey = handlersKey.OpenSubKey(subKeyName, writable: false);
            var value = subKey?.GetValue(null)?.ToString();
            if (Guid.TryParse(value, out var actualGuid) && actualGuid == expectedGuid)
            {
                return true;
            }
        }

        return false;
    }

    private RegistryKey OpenClassesRoot()
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (_portableUser is not null) return _portableUser.OpenClassesRoot(RegistryView.Default);
#endif
        return RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
    }

    private static string? NormalizeClassesRootRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim().Replace('/', '\\').Trim('\\');
        const string longPrefix = @"HKEY_CLASSES_ROOT\";
        const string shortPrefix = @"HKCR\";

        if (normalized.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[longPrefix.Length..];
        }
        else if (normalized.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[shortPrefix.Length..];
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
