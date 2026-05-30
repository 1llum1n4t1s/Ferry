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

    /// <summary>表示名（デバイス名）。</summary>
    public required string DisplayName { get; set; }

    /// <summary>ペアリング日時 (UTC)。</summary>
    public DateTime PairedAt { get; init; } = DateTime.UtcNow;

    /// <summary>最終転送日時 (UTC)。</summary>
    public DateTime? LastTransferAt { get; set; }

    /// <summary>現在の接続経路（接続時に更新、未接続時は Unknown）。ランタイム専用。</summary>
    [ObservableProperty]
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

    partial void OnRouteChanged(ConnectionRoute value)
    {
        OnPropertyChanged(nameof(RouteText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsLanRoute));
        OnPropertyChanged(nameof(IsP2pRoute));
        OnPropertyChanged(nameof(IsRelayRoute));
        OnPropertyChanged(nameof(IsRouteUnknown));
        OnPropertyChanged(nameof(RouteBadgeIcon));
        OnPropertyChanged(nameof(RouteBadgeLabel));
        OnPropertyChanged(nameof(RouteBadgeTooltip));
    }
}
