using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Ferry.Converters;

/// <summary>
/// IsOnline (bool) をオンラインインジケーター色に変換する。
/// true → GreenBrush、false → 灰色。
/// </summary>
public sealed class OnlineStatusToBrushConverter : IValueConverter
{
    public static readonly OnlineStatusToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            if (Application.Current?.TryFindResource("GreenBrush", out var resource) == true && resource is IBrush brush)
                return brush;
            return new SolidColorBrush(Color.Parse("#30D158"));
        }
        return new SolidColorBrush(Color.Parse("#636366"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
