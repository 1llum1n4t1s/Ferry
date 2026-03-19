using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Ferry.Converters;

/// <summary>
/// IsFromMe (bool) をバブル背景色に変換する。
/// true → AccentBrush（自分のメッセージ）、false → SurfaceBrush（相手のメッセージ）。
/// </summary>
public sealed class MessageBubbleColorConverter : IValueConverter
{
    public static readonly MessageBubbleColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "AccentBrush" : "GlassBrush";
        if (Application.Current?.TryFindResource(key, out var resource) == true && resource is IBrush brush)
            return brush;
        return value is true ? new SolidColorBrush(Color.Parse("#007AFF")) : new SolidColorBrush(Color.Parse("#2A2A2E"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
