using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Ferry.Converters;

/// <summary>
/// 値がパラメータと等しい場合に true を返すコンバーター。
/// enum 値の文字列比較にも対応する。
/// </summary>
public sealed class EqualConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        var valueStr = value.ToString();
        var paramStr = parameter.ToString();
        return string.Equals(valueStr, paramStr, StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 値がパラメータと等しくない場合に true を返すコンバーター。
/// </summary>
public sealed class NotEqualConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return true;

        var valueStr = value.ToString();
        var paramStr = parameter.ToString();
        return !string.Equals(valueStr, paramStr, StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
