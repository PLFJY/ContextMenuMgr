using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Text.Json;

namespace ContextMenuMgr.TrayHost;

internal sealed class TrayLocalizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ResourceManager ResourceManager = new("ContextMenuMgr.TrayHost.Resources.Strings", Assembly.GetExecutingAssembly());
    private readonly CultureInfo _culture;

    public TrayLocalizationService()
    {
        _culture = LoadSelectedCulture();
    }

    public string Translate(string key)
        => ResourceManager.GetString(key, _culture) ?? key;

    public string Format(string key, params object[] args)
        => string.Format(_culture, Translate(key), args);

    private static CultureInfo LoadSelectedCulture()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ContextMenuMgr",
                "frontend-settings.json");
            if (!File.Exists(settingsPath))
            {
                return CultureInfo.CurrentUICulture;
            }

            using var stream = File.OpenRead(settingsPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true
            });

            if (!document.RootElement.TryGetProperty("Language", out var languageElement))
            {
                return CultureInfo.CurrentUICulture;
            }

            return languageElement.GetInt32() switch
            {
                1 => CultureInfo.GetCultureInfo("zh-CN"),
                2 => CultureInfo.GetCultureInfo("en-US"),
                _ => CultureInfo.CurrentUICulture
            };
        }
        catch
        {
            return CultureInfo.CurrentUICulture;
        }
    }
}
