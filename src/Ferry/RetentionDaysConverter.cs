using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Ferry;

/// <summary>
/// チャット履歴保持日数を表示テキストに変換するコンバーター。
/// 0 → 「無期限」、それ以外 → 「N日」。
/// </summary>
public sealed class RetentionDaysConverter : IValueConverter
{
    public static readonly RetentionDaysConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int days)
        {
            if (days <= 0) return App.Text("Settings.ChatRetention.Unlimited");
            return $"{days}{App.Text("Settings.ChatRetention.Days")}";
        }
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
