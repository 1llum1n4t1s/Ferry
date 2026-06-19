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

    // rere #D-004: Firebase DB / Bridge / Relay の各 URL は攻撃面削減のため settings.json から撤去し、
    // 書き換え不可の const (Ferry.AppConstants) に一本化した。旧 settings.json に残る
    // FirebaseDatabaseUrl / BridgePageUrl キーは System.Text.Json が未知キーとして無視し、
    // 次回 SaveAsync で自然に消える（互換上の副作用なし）。UpdateBaseUrl も同方針。

    // N-2: 旧 RunAtStartup は AutoStartWithWindows と意味が重複・実機能なしのため削除済み

    /// <summary>起動時にウィンドウを最小化した状態にするか。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>閉じるボタンでタスクトレイに格納するか。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>テーマモード。"System"（OS 追従）/ "Light" / "Dark"。</summary>
    public string ThemeMode { get; set; } = "System";

    /// <summary>表示言語ロケール（"ja_JP", "en_US" など）。空の場合はシステムロケールを自動検出。</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>無視する更新バージョンタグ（例: "v1.0.7"）。このバージョンの更新通知は表示されない。</summary>
    public string IgnoreUpdateTag { get; set; } = string.Empty;

    // --- 通知設定 ---

    /// <summary>受信サウンドを再生するか。</summary>
    public bool EnableNotificationSound { get; set; } = true;

    /// <summary>ピアごとの通知ミュート設定。キーは PeerId。</summary>
    public HashSet<string> MutedPeerIds { get; set; } = [];

    // --- ファイル転送設定 ---
    // ReceiveFileSavePath は v1.0.38 で SaveDirectory と重複していたため削除済み (受信側でも SaveDirectory を使う)

    /// <summary>ファイル受信を自動承認するか。
    /// rere レビュー #A1-005: 旧デフォルト true は #A1-001 (Firebase 匿名ペアリング強制成立)
    /// と組み合わせで「攻撃者が peer になりすまし → 確認なしで Downloads に投下」経路を作っていた。
    /// 既定 false に変更してユーザー明示承認を必須に。既存ユーザーの settings.json に保存済みの
    /// 値は維持されるので、自動承認したい人は設定画面で明示的に ON できる。</summary>
    public bool AutoAcceptFileTransfer { get; set; } = false;

    // rere #D-001(b): 旧 EnableSecureChannel トグルは v1.0.48 で撤去（常時 ON 化）。
    // PairSecret を保有するペアとは自動的に HMAC + AES-GCM 暗号化、未交換ペアは平文フォールバック。
    // 旧 settings.json に残る `EnableSecureChannel` キーは System.Text.Json が未知キーとして無視し、
    // 次回 SaveAsync で自然に消える（#D-004 と同じ互換パターン）。

    /// <summary>アップロード帯域制限 (KB/s)。0 で無制限。
    /// 送信側 SendChunksAsync の各チャンク送信前に TokenBucket でレート整形する。</summary>
    public int UploadKBps { get; set; } = 0;

    /// <summary>ダウンロード帯域制限 (KB/s)。0 で無制限。
    /// 受信側 HandleFileChunk でチャンク書き込み後にスリープしてレート整形する。
    /// 受信ループが減速すると TCP/WebSocket のバックプレッシャーが上流へ伝わる。</summary>
    public int DownloadKBps { get; set; } = 0;

    /// <summary>同時並列転送数 (1〜8)。複数ファイル選択時の同時送信本数。
    /// 1 は従来動作（直列）、N>1 で N 個まで同時に送信する。各 transport の SendAsync は
    /// SemaphoreSlim でフレーム単位に直列化済みなので、メッセージ交錯は起こらない。</summary>
    public int ParallelTransferCount { get; set; } = 1;

    // N-1: 旧 Theme / AccentColor / FontSize は ThemeMode と二重定義 + 未実装だったため削除済み
    // テーマは ThemeMode で一元管理する

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

    /// <summary>サイドバー（左ペイン）の幅 px。左右スプリッターのドラッグ位置を永続化する。未設定時は既定 220。</summary>
    public double? SidebarWidth { get; set; }

    // Codex 第11弾 #3 で導入した LatestConsumedPairingAtMs は global timestamp gate で、
    // 「1 台目ペアリング後に 2 台目を 30〜60s 遅い時計でペアリング」のような正規 pairings entry まで
    // 一律に弾く副作用があった (Codex 第12弾 #4)。 第12弾で per-pairingId 永続化 (SeenPairingIds) に
    // 置換したため撤去。 旧 settings.json に残る `LatestConsumedPairingAtMs` キーは
    // System.Text.Json が未知キーとして無視し、 次回 SaveAsync で自然に消える (#D-004 互換パターン)。

    /// <summary>
    /// Codex 第12弾 #4 (P2) fix: 過去 consume 済みの pairingId を固定サイズ LRU で永続化する。
    /// 再起動跨ぎで in-memory <c>ConnectionService._seenPairingIds</c> が空になっても、
    /// Firebase に残った old pairings entry (cleanup 前) を replay として弾く。
    /// 上限 <see cref="Ferry.Services.ConnectionService.SeenPairingIdsCap"/> 件を超えたら先頭 (= 最古) から落とす。
    /// 旧 LatestConsumedPairingAtMs (global timestamp gate) の副作用 (=clock skew 60s 遅れの正規 peer まで
    /// 弾く) を回避する。
    /// </summary>
    public List<string> SeenPairingIds { get; set; } = [];
}
