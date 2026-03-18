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
}
