using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the context Menu Category Text.
/// </summary>
internal static class ContextMenuCategoryText
{
    /// <summary>
    /// Executes static.
    /// </summary>
    public static (string NameKey, string DescriptionKey) GetResourceKeys(ContextMenuCategory category) => category switch
    {
        ContextMenuCategory.File => ("FileCategoryName", "FileCategoryDescription"),
        ContextMenuCategory.AllFileSystemObjects => ("AllObjectsCategoryName", "AllObjectsCategoryDescription"),
        ContextMenuCategory.Folder => ("FolderCategoryName", "FolderCategoryDescription"),
        ContextMenuCategory.Directory => ("DirectoryCategoryName", "DirectoryCategoryDescription"),
        ContextMenuCategory.DirectoryBackground => ("BackgroundCategoryName", "BackgroundCategoryDescription"),
        ContextMenuCategory.DesktopBackground => ("DesktopCategoryName", "DesktopCategoryDescription"),
        ContextMenuCategory.Drive => ("DriveCategoryName", "DriveCategoryDescription"),
        ContextMenuCategory.Library => ("LibraryCategoryName", "LibraryCategoryDescription"),
        ContextMenuCategory.Computer => ("ComputerCategoryName", "ComputerCategoryDescription"),
        ContextMenuCategory.RecycleBin => ("RecycleBinCategoryName", "RecycleBinCategoryDescription"),
        _ => ("FileCategoryName", "FileCategoryDescription")
    };

    /// <summary>
    /// Gets localized Name.
    /// </summary>
    public static string GetLocalizedName(ContextMenuCategory category, LocalizationService localization)
    {
        var (nameKey, _) = GetResourceKeys(category);
        return localization.Translate(nameKey);
    }
}
