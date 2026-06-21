using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QuantifiedSelf.Windows.App.Converters;

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int intValue => intValue,
            long longValue => (int)Math.Clamp(longValue, int.MinValue, int.MaxValue),
            null => 0,
            _ => 0
        };

        var isEmpty = count <= 0;
        var invert = parameter is string text && text.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? isEmpty : !isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
