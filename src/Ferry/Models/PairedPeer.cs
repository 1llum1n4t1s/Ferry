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
    [property: JsonIgnore]
    private ConnectionRoute _route = ConnectionRoute.Unknown;

    /// <summary>接続状態テキスト（ランタイム専用）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _connectionStatusText = string.Empty;

    /// <summary>相手がオンライン（アプリ起動中）かどうか（ランタイム専用）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isOnline;

    /// <summary>最新メッセージのプレビュー（サイドバー表示用、ランタイム専用）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _lastMessagePreview = string.Empty;

    /// <summary>最新メッセージの日時（サイドバー表示用、ランタイム専用）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private DateTime? _lastMessageAt;

    /// <summary>未読メッセージ数（サイドバー表示用、ランタイム専用）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _unreadCount;

    /// <summary>ファイル着信中かどうか（サイドバーの📦アイコン表示用、ランタイム専用）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasIncomingFile;

    /// <summary>最新メッセージ日時の表示テキスト。</summary>
    [JsonIgnore]
    public string LastMessageAtText => LastMessageAt?.ToLocalTime().ToString("HH:mm") ?? string.Empty;

    /// <summary>接続経路の表示テキスト。</summary>
    [JsonIgnore]
    public string RouteText => Route switch
    {
        ConnectionRoute.Direct => "🟢 LAN 直接",
        ConnectionRoute.StunAssisted => "🟡 P2P（STUN）",
        ConnectionRoute.Relay => "🔴 リレー（TURN）",
        _ => string.Empty,
    };

    partial void OnLastMessageAtChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(LastMessageAtText));
    }

    partial void OnRouteChanged(ConnectionRoute value)
    {
        OnPropertyChanged(nameof(RouteText));
    }
}
