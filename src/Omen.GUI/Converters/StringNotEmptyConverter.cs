using System.Globalization;
using System.Windows.Data;

namespace Omen.GUI.Converters;

public sealed class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
