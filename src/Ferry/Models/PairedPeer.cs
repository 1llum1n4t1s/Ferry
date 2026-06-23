using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ferry.Models;

/// <summary>
/// ペアリング済みピアの永続化情報。
/// QR ペアリング完了後にローカルに保存し、PC 再起動後も再接続可能にする。
/// </summary>
public sealed partial class PairedPeer : ObservableObject
{
    /// <summary>相手の一意識別子。</summary>
    public required string PeerId { get; init; }

    private string _displayName = string.Empty;

    /// <summary>表示名（デバイス名）。相手の設定変更を presence 経由で受けて更新するため変更通知を出す。
    /// これで左ペイン（ピアリスト）・右ペイン上の名前が代入時に即更新される。
    /// [ObservableProperty](partial) ではなく明示バッキングフィールドにしているのは、
    /// AOT の System.Text.Json ソースジェネレータ（PeerRegistryJsonContext）が確実にシリアライズ対象として
    /// 扱える通常プロパティに保つため。peers.json には従来どおり永続化される。</summary>
    public required string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    /// <summary>ペアリング日時 (UTC)。</summary>
    public DateTime PairedAt { get; init; } = DateTime.UtcNow;

    /// <summary>最終転送日時 (UTC)。</summary>
    public DateTime? LastTransferAt { get; set; }

    /// <summary>
    /// rere #D-001(b): ペア相互認証(HMAC) + AES-GCM 封筒のルート鍵(base64, 32B)。
    /// QR で交換した相手の公開鍵 × 自分の秘密鍵の ECDH から導出して永続する。
    /// null の旧ペア（pk 交換前にペア済み / コード貼付で pk 無し）は平文フォールバックする。
    /// peers.json に平文保存するが、ローカルディスクへの読み取りは本アプリの信頼モデル外
    /// （脅威はネットワーク MITM であり、ローカル権限を得た攻撃者は DeviceId 等も読める）。</summary>
    public string? PairSecret { get; set; }

    /// <summary>
    /// Codex P1 fix (PR #10): pairs/{pairId} SSoT を一度でも観察済みかどうか。
    ///
    /// false = 旧 peers.json から upgrade した「pre-SSoT peer」(pairs/{pairId} がまだ書かれていない
    /// 可能性がある) → PairSyncService は 404 を見たら 1 度だけ backfill を試みる。
    /// true = 過去に 200 で観察 or 新規 PairingDetected で SSoT に書き込み完了 → 404 は本当の
    /// remote unpair と判定して通常カウンタへ流す (backfill で resurrect しない)。
    ///
    /// peers.json に永続するので、再起動後も「観察済みフラグ」は引き継がれる。
    /// </summary>
    public bool PairsSsotObserved { get; set; }

    /// <summary>現在の接続経路（接続時に更新、未接続時は Unknown）。ランタイム専用。
    /// 派生プロパティ群は [NotifyPropertyChangedFor] で宣言的に連動通知する (TransferItem と同パターン。
    /// 手書き OnRouteChanged 列挙だと派生プロパティ追加時に通知漏れが起きやすい)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteText))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsLanRoute))]
    [NotifyPropertyChangedFor(nameof(IsP2pRoute))]
    [NotifyPropertyChangedFor(nameof(IsRelayRoute))]
    [NotifyPropertyChangedFor(nameof(IsRouteUnknown))]
    [NotifyPropertyChangedFor(nameof(RouteBadgeIcon))]
    [NotifyPropertyChangedFor(nameof(RouteBadgeLabel))]
    [NotifyPropertyChangedFor(nameof(RouteBadgeTooltip))]
    [JsonIgnore]
    public partial ConnectionRoute Route { get; set; } = ConnectionRoute.Unknown;

    /// <summary>接続状態テキスト（ランタイム専用）。</summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial string ConnectionStatusText { get; set; } = string.Empty;

    /// <summary>相手がオンライン（アプリ起動中）かどうか（ランタイム専用）。</summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial bool IsOnline { get; set; }

    /// <summary>複数ペア同時接続対応 Stage 0: このピアに紐づく進行中転送が 1 件以上あるか（ランタイム専用）。
    /// 左ペインの転送中バッジ用。TransferViewModel が Transfers を PeerId グルーピングして集計 set する
    /// （Stage 6 で配線）。現状(Stage 0)は誰も set しないので false 固定で挙動不変。</summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial bool IsTransferring { get; set; }

    /// <summary>複数ペア同時接続対応 Stage 0: このピアに紐づく進行中転送の件数（ランタイム専用）。
    /// 左ペインの件数バッジ用。Stage 6 で TransferViewModel が集計 set する。</summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial int ActiveTransferCount { get; set; }

    /// <summary>
    /// IsOnline が false → true に切り替わった瞬間に発火するエッジトリガーイベント。
    /// ConnectionViewModel がこれを購読して、Online になった瞬間に経路 Probe を 1 回だけ走らせる。
    /// event は JSON シリアライズの対象外なので JsonIgnore 不要。
    /// </summary>
    public event EventHandler? WentOnline;

    partial void OnIsOnlineChanged(bool oldValue, bool newValue)
    {
        if (!oldValue && newValue)
            WentOnline?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>接続経路の表示テキスト（旧 UI / ログ用）。</summary>
    [JsonIgnore]
    public string RouteText => Route switch
    {
        ConnectionRoute.Direct => "🟢 LAN 直接",
        ConnectionRoute.StunAssisted => "🟡 P2P（STUN）",
        ConnectionRoute.Relay => "🔴 リレー（TURN）",
        _ => string.Empty,
    };

    // === 接続経路バッジ用派生プロパティ（MainWindow メンバーリストのピル型バッジで使用）===

    /// <summary>接続経路バッジを表示するか（Unknown 以外）。</summary>
    [JsonIgnore]
    public bool IsConnected => Route != ConnectionRoute.Unknown;

    /// <summary>LAN 直接接続中か（バッジ色切替用）。</summary>
    [JsonIgnore]
    public bool IsLanRoute => Route == ConnectionRoute.Direct;

    /// <summary>STUN ホールパンチ経由接続中か（バッジ色切替用）。</summary>
    [JsonIgnore]
    public bool IsP2pRoute => Route == ConnectionRoute.StunAssisted;

    /// <summary>WebSocket リレー経由接続中か（バッジ色切替用）。</summary>
    [JsonIgnore]
    public bool IsRelayRoute => Route == ConnectionRoute.Relay;

    /// <summary>接続経路がまだ確定していない（状態取得前）か。バッジの「状態取得中」dimmed 表示用。</summary>
    [JsonIgnore]
    public bool IsRouteUnknown => Route == ConnectionRoute.Unknown;

    /// <summary>バッジに表示するアイコン文字（短縮、Material Symbol 風）。Unknown は「状態取得中」を表す砂時計。</summary>
    [JsonIgnore]
    public string RouteBadgeIcon => Route switch
    {
        ConnectionRoute.Direct => "⚡",       // ⚡ 高速ボルト = LAN 直接
        ConnectionRoute.StunAssisted => "✧", // ✧ スター = NAT 越え P2P
        ConnectionRoute.Relay => "↻",        // ↻ リトライ風矢印 = リレー経由
        _ => "⏳",                            // ⏳ 砂時計 = 経路未確定（状態取得中）
    };

    /// <summary>バッジに表示する短いラベル（経路は全大文字、未確定時は「状態取得中」）。</summary>
    [JsonIgnore]
    public string RouteBadgeLabel => Route switch
    {
        ConnectionRoute.Direct => "LAN",
        ConnectionRoute.StunAssisted => "P2P",
        ConnectionRoute.Relay => "RELAY",
        _ => "状態取得中",
    };

    /// <summary>ツールチップ用の詳細説明。</summary>
    [JsonIgnore]
    public string RouteBadgeTooltip => Route switch
    {
        ConnectionRoute.Direct => "LAN 直接接続（TCP）— 最速・最高帯域",
        ConnectionRoute.StunAssisted => "P2P 直接接続（UDP ホールパンチ + STUN）— インターネット越え",
        ConnectionRoute.Relay => "サーバー経由リレー（TURN/WebSocket）— ファイアウォール越え",
        _ => "接続経路を確認中…",
    };

}
