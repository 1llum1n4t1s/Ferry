using System;
using System.IO;

namespace Ferry.Models;

/// <summary>
/// アプリケーション設定。
/// </summary>
public sealed class AppSettings
{
    /// <summary>このPCの表示名。</summary>
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>受信ファイルの保存先ディレクトリ。</summary>
    public string SaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Ferry");

    /// <summary>Firebase プロジェクト URL（プレースホルダー）。</summary>
    public string FirebaseDatabaseUrl { get; set; } = string.Empty;

    /// <summary>Firebase Hosting の橋渡しページ URL（プレースホルダー）。</summary>
    public string BridgePageUrl { get; set; } = string.Empty;

    /// <summary>OS 起動時にアプリを自動起動するか。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>起動時にウィンドウを最小化した状態にするか。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>閉じるボタンでタスクトレイに格納するか。</summary>
    public bool MinimizeToTray { get; set; }
}
