using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Ferry.Converters;

/// <summary>
/// int 値が 0 より大きいかどうかを bool に変換する。
/// 未読バッジの IsVisible 用。
/// </summary>
public sealed class IntGreaterThanZeroConverter : IValueConverter
{
    public static readonly IntGreaterThanZeroConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is int i && i > 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
