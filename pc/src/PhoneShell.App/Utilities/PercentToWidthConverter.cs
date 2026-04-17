using System.Globalization;
using System.Windows.Data;

namespace PhoneShell.Utilities;

public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return 0d;

        var totalWidth = values[0] is double width ? width : 0d;
        var percent = values[1] is double value ? value : 0d;
        if (totalWidth <= 0d || percent <= 0d)
            return 0d;

        return totalWidth * Math.Clamp(percent, 0d, 100d) / 100d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
