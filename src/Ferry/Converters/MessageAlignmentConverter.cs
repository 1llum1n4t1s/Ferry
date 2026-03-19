using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace Ferry.Converters;

/// <summary>
/// IsFromMe (bool) を HorizontalAlignment に変換する。
/// true → Right（自分のメッセージ）、false → Left（相手のメッセージ）。
/// </summary>
public sealed class MessageAlignmentConverter : IValueConverter
{
    public static readonly MessageAlignmentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
