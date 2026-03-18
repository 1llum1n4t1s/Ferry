using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace Ferry;

/// <summary>
/// bool → HorizontalAlignment コンバーター。
/// true → Right、false → Left。
/// </summary>
public sealed class BoolToAlignmentConverter : IValueConverter
{
    public static readonly BoolToAlignmentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
