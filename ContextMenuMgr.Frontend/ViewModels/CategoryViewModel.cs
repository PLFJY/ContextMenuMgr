using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class CategoryViewModel : ObservableObject
{
    private readonly LocalizationService _localization;

    public CategoryViewModel(ContextMenuCategory category, LocalizationService localization)
    {
        Category = category;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ItemCountLabel));
        RefreshLocalizedText();
    }

    public ContextMenuCategory Category { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    public ObservableCollection<ContextMenuItemViewModel> Items { get; } = [];

    public string ItemCountLabel
    {
        get
        {
            if (_localization.UsesChinese())
            {
                return _localization.Format("ItemCountFormat", Items.Count);
            }

            var pluralSuffix = Items.Count == 1 ? string.Empty : _localization.Translate("ItemCountPluralSuffix");
            return _localization.Format("ItemCountFormat", Items.Count, pluralSuffix);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedText();
        OnPropertyChanged(nameof(ItemCountLabel));
    }

    private void RefreshLocalizedText()
    {
        var (nameKey, descriptionKey) = ContextMenuCategoryText.GetResourceKeys(Category);

        Name = _localization.Translate(nameKey);
        Description = _localization.Translate(descriptionKey);
    }
}
