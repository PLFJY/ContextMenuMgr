using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.TrayHost;

internal sealed class TrayLocalizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ResourceManager ResourceManager = new("ContextMenuMgr.TrayHost.Resources.Strings", Assembly.GetExecutingAssembly());
    private CultureInfo _culture;

    public TrayLocalizationService()
    {
        _culture = LoadSelectedCulture();
    }

    public CultureInfo CurrentCulture => _culture;

    public string Translate(string key)
        => ResourceManager.GetString(key, _culture) ?? key;

    public string Format(string key, params object[] args)
        => string.Format(_culture, Translate(key), args);

    public void Reload()
    {
        _culture = LoadSelectedCulture();
    }

    private static CultureInfo LoadSelectedCulture()
    {
        try
        {
            var settingsPath = RuntimePaths.SettingsPath;
            if (!File.Exists(settingsPath))
            {
                return GetSystemCulture();
            }

            using var stream = File.OpenRead(settingsPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true
            });

            if (!TryGetPropertyIgnoreCase(document.RootElement, "language", out var languageElement))
            {
                return GetSystemCulture();
            }

            return languageElement.GetInt32() switch
            {
                1 => CultureInfo.GetCultureInfo("zh-CN"),
                2 => CultureInfo.GetCultureInfo("en-US"),
                _ => GetSystemCulture()
            };
        }
        catch
        {
            return GetSystemCulture();
        }
    }

    private static CultureInfo GetSystemCulture()
    {
        try
        {
            var languageId = NativeMethods.GetUserDefaultUILanguage();
            return CultureInfo.GetCultureInfo(languageId);
        }
        catch
        {
            return CultureInfo.InstalledUICulture;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern ushort GetUserDefaultUILanguage();
    }
}
