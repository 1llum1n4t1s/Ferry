using System;
using System.Globalization;
using Avalonia;
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

    // フォールバック色を SolidColorBrush として 1 度だけ確保（毎回 new 不要）
    private static readonly IBrush s_onlineFallback = new SolidColorBrush(Color.Parse("#30D158"));
    private static readonly IBrush s_offlineFallback = new SolidColorBrush(Color.Parse("#FF453A"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Avalonia 12 では Application.FindResource が直接公開されないため TryGetResource を使用。
        // ActualThemeVariant を渡して Light/Dark 切替時にも正しいブラシを返すよう正規化（N-3）
        var key = value is true ? "GreenBrush" : "OfflineBrush";
        var fallback = value is true ? s_onlineFallback : s_offlineFallback;

        if (Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var resource)
            && resource is IBrush brush)
        {
            return brush;
        }
        return fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
