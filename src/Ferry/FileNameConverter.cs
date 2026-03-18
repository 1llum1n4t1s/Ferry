using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Ferry;

/// <summary>
/// ファイルパスからファイル名のみを抽出するコンバーター。
/// </summary>
public sealed class FileNameConverter : IValueConverter
{
    public static readonly FileNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string path ? System.IO.Path.GetFileName(path) : value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
