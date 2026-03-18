using System;
using System.IO;

namespace Ferry.Models;

/// <summary>
/// アプリケーション設定。
/// </summary>
public sealed class AppSettings
{
    /// <summary>このデバイスの一意識別子（初回起動時に自動生成、永続化）。</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>このPCの表示名。</summary>
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>受信ファイルの保存先ディレクトリ。
    /// デフォルトはユーザーの「ダウンロード」フォルダ（Win/Mac 共通）。</summary>
    public string SaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Firebase プロジェクト URL（プレースホルダー）。</summary>
    public string FirebaseDatabaseUrl { get; set; } = string.Empty;

    /// <summary>Firebase Hosting の橋渡しページ URL（プレースホルダー）。</summary>
    public string BridgePageUrl { get; set; } = string.Empty;

    /// <summary>WebSocket リレーサーバーの URL（NAT 越え用）。</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>OS 起動時にアプリを自動起動するか。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>起動時にウィンドウを最小化した状態にするか。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>閉じるボタンでタスクトレイに格納するか。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>テーマモード。"System"（OS 追従）/ "Light" / "Dark"。</summary>
    public string ThemeMode { get; set; } = "System";

    /// <summary>表示言語ロケール（"ja_JP", "en_US" など）。空の場合はシステムロケールを自動検出。</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>起動時に自動更新チェックを行うか。</summary>
    public bool Check4UpdatesOnStartup { get; set; } = true;

    /// <summary>無視する更新バージョンタグ（例: "v1.0.7"）。このバージョンの更新通知は表示されない。</summary>
    public string IgnoreUpdateTag { get; set; } = string.Empty;

    /// <summary>チャット履歴の保持日数。この日数を超えた履歴は自動削除される。</summary>
    public int ChatHistoryRetentionDays { get; set; } = 30;

    // ウィンドウ位置・サイズ（前回終了時の状態を復元）
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}
