using System;
using System.Globalization;
using System.Windows.Data;

namespace PhoneShell.Utilities;

public sealed class TabBarAvailableWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var hostWidth = GetDouble(values, 0);
        var rightControlsWidth = GetDouble(values, 1);
        var plusWidth = GetDouble(values, 2);
        var paddingLeft = GetDouble(values, 3);
        var paddingRight = GetDouble(values, 4);
        var rightControlsMarginLeft = GetDouble(values, 5);
        var plusMarginLeft = GetDouble(values, 6);

        if (plusWidth <= 0)
            plusWidth = 30;

        var available = hostWidth
            - paddingLeft
            - paddingRight
            - rightControlsWidth
            - rightControlsMarginLeft
            - plusWidth
            - plusMarginLeft;

        if (double.IsNaN(available) || double.IsInfinity(available))
            return 0d;

        return Math.Max(0, available);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return Array.Empty<object>();
    }

    private static double GetDouble(object[] values, int index)
    {
        if (values.Length <= index || values[index] is null)
            return 0d;

        return values[index] switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d
        };
    }
}
