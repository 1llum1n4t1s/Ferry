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
    //
    // CF 単独完結 Step 6: 旧 dual-path フラグ `UseCloudflareSignaling` / `MigratedToCloudflareDefault` は
    // 撤去（常時 Cloudflare 経路）。旧 settings.json に残るこれらのキーも上記と同じく未知キーとして無視され
    // 次回保存で自然に消える（#D-004 互換パターン）。v1.0.65 で全クライアントが CF へ移行済み。

    /// <summary>アップロード帯域制限 (KB/s)。0 で無制限。
    /// 送信側 SendChunksAsync の各チャンク送信前に TokenBucket でレート整形する。</summary>
    public int UploadKBps { get; set; } = 0;

    /// <summary>ダウンロード帯域制限 (KB/s)。0 で無制限。
    /// 受信側 HandleFileChunk でチャンク書き込み後にスリープしてレート整形する。
    /// 受信ループが減速すると TCP/WebSocket のバックプレッシャーが上流へ伝わる。</summary>
    public int DownloadKBps { get; set; } = 0;

    /// <summary>旧「同時並列転送数」設定（未使用）。並列本数は TransferViewModel.MaxParallelSends の
    /// 内部固定（最大 10）に移行し設定 UI も撤去した。既存 settings.json のデシリアライズ互換のため
    /// プロパティだけ残置している（読み書きとも参照しない）。</summary>
    public int ParallelTransferCount { get; set; } = 1;

    /// <summary>宛先リストの各セクション内ソート基準（既定 Name）。📌ピン/🟢オンライン/⚪オフラインの
    /// セクション分割は固定で、この値は各セクション内の並び順を決める。System.Text.Json は enum を数値で
    /// 永続化する（AOT source-gen 対応）。旧 settings.json に無ければ既定 Name(0)。</summary>
    public PeerSortMode PeerListSortMode { get; set; } = PeerSortMode.Name;

    // 旧マルチストリーム転送 PoC のフラグ (ForceRelay / RelayStreamCount) は 2026-07 に不採用で撤去。
    // System.Text.Json は未知プロパティを読み飛ばすため、旧 settings.json に残っていても無害。

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
    /// 弾く) を回避する。更新は <see cref="AddSeenPairingId"/> 経由で行う（copy-on-write の不変条件を所有者が守る）。
    /// </summary>
    public List<string> SeenPairingIds { get; set; } = [];

    /// <summary>
    /// <see cref="SeenPairingIds"/> に pairingId を LRU で追加する。既出なら false（呼び出し側は保存不要）。
    ///
    /// Codex 第12弾 verify critical fix: 既存 List を mutate せず copy-on-write で新 List 参照に差し替える。
    /// 旧 List 参照は他経路 (SettingsViewModel / MainWindow 等) で in-flight な
    /// <c>JsonSerializer.SerializeToUtf8Bytes</c> が enumerate しているかもしれず、mutate すると
    /// 「Collection was modified」で別経路の SaveAsync が落ちるため。新 List は誰も enumerate していない
    /// 不変オブジェクトとして差し替える。LRU の所有者である本クラスがこの不変条件を守る（呼び出し側は
    /// 戻り値 true のときだけ <c>SaveAsync</c> すればよい）。
    /// </summary>
    /// <param name="pairingId">追加する pairingId。</param>
    /// <param name="cap">LRU 上限（超過分は先頭=最古から落とす）。</param>
    /// <returns>新規追加なら true、既出で変更なしなら false。</returns>
    public bool AddSeenPairingId(string pairingId, int cap)
    {
        lock (this)
        {
            if (SeenPairingIds.Contains(pairingId)) return false;
            var next = new List<string>(SeenPairingIds.Count + 1);
            // 既存件数 + 新 1 件で cap 超過分を先頭から skip する形でコピー。
            var skip = Math.Max(0, SeenPairingIds.Count + 1 - cap);
            for (int i = skip; i < SeenPairingIds.Count; i++)
                next.Add(SeenPairingIds[i]);
            next.Add(pairingId);
            SeenPairingIds = next;
            return true;
        }
    }
}
