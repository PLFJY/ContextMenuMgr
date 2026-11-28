using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using System.Text;

namespace ContextMenuMgr.Backend.Services;

/// <summary>Owns the explicit machine-wide GUID Block list, independent of per-registration classic toggles.</summary>
internal static class GuidBlockMenuService
{
    private const string GuidBlockedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    internal static IReadOnlyList<SpecialMenuEntry> GetSnapshot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(GuidBlockedPath, writable: false);
        if (key is null)
        {
            return [];
        }

        return key.GetValueNames()
            .Where(static name => Guid.TryParse(name, out _))
            .Select(name =>
            {
                var guid = Guid.Parse(name);
                var icon = GuidMetadataCatalog.GetIconLocation(guid);
                return new SpecialMenuEntry
                {
                    Id = $"{SpecialMenuKind.GuidBlock}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}",
                    Kind = SpecialMenuKind.GuidBlock,
                    DisplayName = GuidMetadataCatalog.GetDisplayName(guid) ?? key.GetValue(name)?.ToString() ?? name,
                    KeyName = name,
                    IsEnabled = true,
                    IconPath = icon.IconPath,
                    IconIndex = icon.IconIndex,
                    RegistryPath = $@"HKEY_LOCAL_MACHINE\{GuidBlockedPath}",
                    TargetPath = GuidMetadataCatalog.GetFilePath(guid),
                    Metadata = new Dictionary<string, string> { ["Guid"] = name }
                };
            })
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static SpecialMenuEntry Create(GuidBlockCreateRequest request)
    {
        if (!Guid.TryParse(request.GuidText, out var guid))
        {
            throw new InvalidOperationException("The GUID format is invalid.");
        }

        using var key = Registry.LocalMachine.CreateSubKey(GuidBlockedPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open Blocked shell extensions key.");
        key.SetValue(guid.ToString("B"), request.DisplayName ?? string.Empty, RegistryValueKind.String);
        return GetSnapshot().First(item => string.Equals(item.KeyName, guid.ToString("B"), StringComparison.OrdinalIgnoreCase));
    }

    internal static SpecialMenuEntry SetEnabled(SpecialMenuEntry item, bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.LocalMachine.CreateSubKey(GuidBlockedPath, writable: true)
                ?? throw new InvalidOperationException("Unable to open Blocked shell extensions key.");
            key.SetValue(item.KeyName, item.DisplayName ?? string.Empty, RegistryValueKind.String);
        }
        else
        {
            Delete(item.KeyName);
        }

        return item with { IsEnabled = enabled };
    }

    internal static void Delete(string valueName, FileLogger? logger = null)
    {
        using var key = Registry.LocalMachine.OpenSubKey(GuidBlockedPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
        logger?.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("DeleteRegistryValue", $@"HKEY_LOCAL_MACHINE\{GuidBlockedPath}", valueName, null, null, writable: true, result: key is null ? "MissingKey" : "Success"));
    }
}
