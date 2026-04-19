using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ContextMenuMgr.Frontend.Converters;

/// <summary>
/// Represents the null Or Empty To Visibility Converter.
/// </summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Executes convert.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value?.ToString())
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Executes convert Back.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
