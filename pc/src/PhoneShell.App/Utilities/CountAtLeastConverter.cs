using System;
using System.Globalization;
using System.Windows.Data;

namespace PhoneShell.Utilities;

public sealed class CountAtLeastConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int count)
        {
            return false;
        }

        var threshold = 0;
        if (parameter is string text && int.TryParse(text, out var parsed))
        {
            threshold = parsed;
        }
        else if (parameter is int number)
        {
            threshold = number;
        }

        return count >= threshold;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
