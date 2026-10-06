using ContextMenuMgr.Frontend.Resources;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the localization Service.
/// </summary>
public sealed class LocalizationService
{
    private readonly FrontendSettingsService _settingsService;
    private AppLanguageOption _selectedLanguage = AppLanguageOption.System;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalizationService"/> class.
    /// </summary>
    public LocalizationService(FrontendSettingsService settingsService)
    {
        _settingsService = settingsService;
        _selectedLanguage = _settingsService.Current.Language;
        ApplyPersistedLanguage();
    }

    public event EventHandler? LanguageChanged;

    public AppLanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage == value)
            {
                return;
            }

            _selectedLanguage = value;
            _settingsService.UpdateLanguage(value);
            ApplyPersistedLanguage();
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets the selected UI culture name.
    /// </summary>
    public string CurrentCultureName => GetSupportedCultureName(GetSelectedCulture());

    /// <summary>
    /// Applies persisted Language.
    /// </summary>
    public void ApplyPersistedLanguage()
    {
        ApplyCulture();
    }

    /// <summary>
    /// Executes translate.
    /// </summary>
    public string Translate(string key)
    {
        return Strings.ResourceManager.GetString(key, GetSelectedCulture()) ?? key;
    }

    /// <summary>
    /// Resolves a Windows-owned label using the interactive user's Windows UI
    /// language, independently of the application's selected language.
    /// </summary>
    public string TranslateSystem(string key)
    {
        return TranslateForSystemCulture(key, GetSystemCulture());
    }

    internal static string TranslateForSystemCulture(string key, CultureInfo systemCulture)
    {
        return Strings.ResourceManager.GetString(key, GetSupportedCulture(systemCulture)) ?? key;
    }

    /// <summary>
    /// Executes format.
    /// </summary>
    public string Format(string key, params object[] args)
    {
        return string.Format(GetFormattingCulture(), Translate(key), args);
    }

    /// <summary>
    /// Executes uses Chinese.
    /// </summary>
    public bool UsesChinese()
    {
        return GetSelectedCulture().Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    public bool UsesChineseTraditional()
    {
        return string.Equals(CurrentCultureName, "zh-TW", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyCulture()
    {
        var culture = GetSelectedCulture();
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;

        if (Application.Current is not null)
        {
            Application.Current.Resources["CurrentLanguage"] = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        }
    }

    private CultureInfo GetFormattingCulture()
    {
        return GetSelectedCulture();
    }

    private CultureInfo GetSelectedCulture()
    {
        return _selectedLanguage switch
        {
            AppLanguageOption.System => GetSupportedCulture(GetSystemCulture()),
            AppLanguageOption.ChineseSimplified => CultureInfo.GetCultureInfo("zh-CN"),
            AppLanguageOption.EnglishUnitedStates => CultureInfo.GetCultureInfo("en-US"),
            AppLanguageOption.ChineseTraditionalTaiwan => CultureInfo.GetCultureInfo("zh-TW"),
            _ => GetSupportedCulture(GetSystemCulture())
        };
    }

    private static CultureInfo GetSupportedCulture(CultureInfo culture)
    {
        return CultureInfo.GetCultureInfo(GetSupportedCultureName(culture));
    }

    private static string GetSupportedCultureName(CultureInfo culture)
    {
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(culture.Name, "zh-TW", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(culture.Name, "zh-HK", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(culture.Name, "zh-MO", StringComparison.OrdinalIgnoreCase)
                ? "zh-TW"
                : "zh-CN";
        }

        return "en-US";
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

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern ushort GetUserDefaultUILanguage();
    }
}
