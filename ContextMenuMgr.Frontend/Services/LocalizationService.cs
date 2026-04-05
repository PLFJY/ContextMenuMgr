using System.Globalization;
using System.Windows.Markup;
using ContextMenuMgr.Frontend.Resources;

namespace ContextMenuMgr.Frontend.Services;

public sealed class LocalizationService
{
    private readonly FrontendSettingsService _settingsService;
    private AppLanguageOption _selectedLanguage = AppLanguageOption.System;

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

    public void ApplyPersistedLanguage()
    {
        ApplyCulture();
    }

    public string Translate(string key)
    {
        return Strings.ResourceManager.GetString(key, GetSelectedCulture()) ?? key;
    }

    public string Format(string key, params object[] args)
    {
        return string.Format(GetFormattingCulture(), Translate(key), args);
    }

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
            AppLanguageOption.System => CultureInfo.CurrentUICulture,
            AppLanguageOption.ChineseSimplified => CultureInfo.GetCultureInfo("zh-CN"),
            AppLanguageOption.EnglishUnitedStates => CultureInfo.GetCultureInfo("en-US"),
            _ => CultureInfo.CurrentUICulture
        };
    }
}
