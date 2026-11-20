using System.Globalization;
using Avalonia.Data.Converters;

namespace Ferry.Converters;

/// <summary>
/// IsOnline (bool) をオンライン状態インジケーターのアクセシブル名（「オンライン」/「オフライン」）に変換する。
/// 色だけで状態を示す Ellipse を、スクリーンリーダーへ伝えるために使う。文言はロケール辞書の
/// セクション見出し (Peer.Section.Online / Offline) を共用する。
/// </summary>
public sealed class OnlineStatusToTextConverter : IValueConverter
{
    public static readonly OnlineStatusToTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        App.Text(value is true ? "Peer.Section.Online" : "Peer.Section.Offline");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
