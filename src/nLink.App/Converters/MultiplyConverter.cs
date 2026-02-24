using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace NLink.App.Converters;

public sealed class MultiplyConverter : IValueConverter
{
    private const double DefaultWidth = 480d;
    private const double MinWidth = 240d;
    private const double MaxWidth = 720d;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value as double? ?? (value is double d ? d : DefaultWidth);
        if (width <= 0)
        {
            return DefaultWidth;
        }

        var multiplier = 1d;
        if (parameter is string s &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            multiplier = parsed;
        }

        var result = width * multiplier;
        if (double.IsNaN(result) || double.IsInfinity(result) || result <= 0)
        {
            return DefaultWidth;
        }

        return Math.Clamp(result, MinWidth, MaxWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}
