using System.Globalization;
using System.Windows.Data;

namespace ContextMenuMgr.Frontend.Converters;

/// <summary>Keeps the outgoing service message readable while its banner animates closed.</summary>
public sealed class RetainNonEmptyTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && !string.IsNullOrWhiteSpace(text) ? text : Binding.DoNothing;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
