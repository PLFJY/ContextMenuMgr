using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Markup;
using ContextMenuMgr.Frontend.Resources;

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

    private void ApplyCulture()
    {
        var culture = GetSelectedCulture();
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;

        if (System.Windows.Application.Current is not null)
        {
            System.Windows.Application.Current.Resources["CurrentLanguage"] = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
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
            AppLanguageOption.System => GetSystemCulture(),
            AppLanguageOption.ChineseSimplified => CultureInfo.GetCultureInfo("zh-CN"),
            AppLanguageOption.EnglishUnitedStates => CultureInfo.GetCultureInfo("en-US"),
            _ => GetSystemCulture()
        };
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
