using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QuantifiedSelf.Windows.App.Converters;

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string;
        var hasText = !string.IsNullOrWhiteSpace(text);
        var invert = parameter is string parameterText && parameterText.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? !hasText : hasText;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
