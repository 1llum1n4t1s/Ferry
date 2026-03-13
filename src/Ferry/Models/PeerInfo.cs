namespace Ferry.Models;

/// <summary>
/// 接続先ピアの情報。
/// </summary>
public sealed class PeerInfo
{
    /// <summary>セッション ID (UUID v4)。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>相手の表示名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>接続状態。</summary>
    public PeerState State { get; set; } = PeerState.Disconnected;
}

/// <summary>
/// ピアの接続状態。
/// </summary>
public enum PeerState
{
    /// <summary>未接続。</summary>
    Disconnected,

    /// <summary>接続待機中（QR 表示中）。</summary>
    WaitingForPairing,

    /// <summary>ペアリング要求受信。</summary>
    PairingRequested,

    /// <summary>WebRTC 接続確立中。</summary>
    Connecting,

    /// <summary>接続済み（ファイル転送可能）。</summary>
    Connected,

    /// <summary>エラー発生。</summary>
    Error,

    /// <summary>再接続中（切断後の自動復帰）。</summary>
    Reconnecting,
}
