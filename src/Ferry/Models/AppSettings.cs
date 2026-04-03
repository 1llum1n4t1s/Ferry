using System;
using System.Collections.Generic;
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

    // --- 通知設定 ---

    /// <summary>受信サウンドを再生するか。</summary>
    public bool EnableNotificationSound { get; set; } = true;

    /// <summary>ピアごとの通知ミュート設定。キーは PeerId。</summary>
    public HashSet<string> MutedPeerIds { get; set; } = [];

    // --- ファイル転送設定 ---

    /// <summary>受信ファイルの保存先フォルダ。空の場合はダウンロードフォルダ。</summary>
    public string ReceiveFileSavePath { get; set; } = string.Empty;

    /// <summary>ファイル受信を自動承認するか。</summary>
    public bool AutoAcceptFileTransfer { get; set; } = true;

    // --- 外観設定 ---

    /// <summary>テーマ ("dark" / "light" / "system")。</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>アクセントカラー (hex)。</summary>
    public string AccentColor { get; set; } = "#007AFF";

    /// <summary>フォントサイズ ("small" / "medium" / "large")。</summary>
    public string FontSize { get; set; } = "medium";

    // --- アプリ動作設定 ---

    /// <summary>Windows 起動時に自動起動するか。</summary>
    public bool AutoStartWithWindows { get; set; } = false;

    // --- ウィンドウ状態 ---

    /// <summary>ウィンドウ位置・サイズ（前回終了時の状態を復元）。</summary>
    public double? WindowLeft { get; set; }

    /// <summary>ウィンドウ上端座標。</summary>
    public double? WindowTop { get; set; }

    /// <summary>ウィンドウ幅。</summary>
    public double? WindowWidth { get; set; }

    /// <summary>ウィンドウ高さ。</summary>
    public double? WindowHeight { get; set; }

    /// <summary>ウィンドウ X 座標。</summary>
    public double WindowX { get; set; } = double.NaN;

    /// <summary>ウィンドウ Y 座標。</summary>
    public double WindowY { get; set; } = double.NaN;

    /// <summary>ウィンドウが最大化されているか。</summary>
    public bool IsWindowMaximized { get; set; } = false;
}
