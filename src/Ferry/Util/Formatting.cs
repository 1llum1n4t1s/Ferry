namespace Ferry.Util;

/// <summary>表示用フォーマットユーティリティ。</summary>
public static class Formatting
{
    /// <summary>バイト数を人間が読みやすい形式に変換する。</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    /// <summary>ビットレート(bps)を人間が読みやすい形式に変換する。
    /// ネットワークレートの慣例で 1000 区切り（FormatBytes は 1024 区切りだが bps は 10 進）。</summary>
    public static string FormatBitrate(double bps) => bps switch
    {
        < 1000 => $"{bps:F0} bps",
        < 1_000_000 => $"{bps / 1000.0:F1} Kbps",
        < 1_000_000_000 => $"{bps / 1_000_000.0:F1} Mbps",
        _ => $"{bps / 1_000_000_000.0:F2} Gbps",
    };
}
