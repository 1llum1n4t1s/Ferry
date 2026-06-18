using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// 接続管理サービス。
/// ペアリング（QR スキャン → Bridge ページ経由のマッチング）と
/// オンデマンド接続（転送時の一時的な WebRTC 接続）を分離して管理する。
/// </summary>
public interface IConnectionService
{
    // === 状態 ===

    /// <summary>現在の接続状態。</summary>
    PeerState State { get; }

    /// <summary>接続中のピア情報。未接続時は null。</summary>
    PeerInfo? ConnectedPeer { get; }

    /// <summary>現在の接続経路（LAN 直接 / STUN P2P / TURN リレー）。</summary>
    ConnectionRoute Route { get; }

    // === イベント ===

    /// <summary>状態が変化したときに発火するイベント。</summary>
    event EventHandler<PeerState>? StateChanged;

    /// <summary>接続経路が確定したときに発火するイベント。</summary>
    event EventHandler<ConnectionRoute>? RouteChanged;

    /// <summary>ペアリングが完了したときに発火するイベント。</summary>
    event EventHandler<PairedPeer>? PairingCompleted;

    /// <summary>DataChannel でバイナリデータを受信したときに発火するイベント。</summary>
    event EventHandler<byte[]>? DataReceived;

    /// <summary>接続が切断されたときに発火するイベント（転送中の切断検知用）。</summary>
    event EventHandler? ConnectionLost;

    /// <summary>接続フェーズの詳細ステータスメッセージが更新されたときに発火するイベント。</summary>
    event EventHandler<string>? StatusMessageChanged;

    // === ペアリング（QR スキャン → Bridge ページ経由） ===

    /// <summary>
    /// ペアリングセッションを開始し、セッション ID を返す。
    /// QR コード URL 生成に使用する。
    /// Firebase でマッチング完了を監視し、完了時に PairingCompleted を発火する。
    /// </summary>
    Task<string> StartPairingSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// v1.0.38: アプリ内ペアリングコード貼り付け。URL ではなく 32 文字 hex (相手の sessionId) を受け取る。
    /// ブラウザでうっかり開かれる事故を防ぐため URL から GUID 風文字列に変更。
    /// </summary>
    Task<(bool Success, string Message)> PairFromCodeAsync(string code, CancellationToken ct = default)
        => Task.FromResult((false, "未実装"));

    /// <summary>
    /// ペアリングセッションをキャンセルする。
    /// </summary>
    Task CancelPairingAsync(CancellationToken ct = default);

    /// <summary>
    /// pairing watch を停止する。新規ペアリング成立確定時に呼ぶ。
    /// stale/既知ピアの再検知では呼ばず watcher を生かしたままにする。
    /// </summary>
    void StopPairingWatch() { }

    // === 着信接続監視 ===

    /// <summary>
    /// 指定ピアからの接続要求（Offer）をバックグラウンドで監視開始する。
    /// Offer を検知したら自動的に Answer を返して WebRTC 接続を確立する。
    /// </summary>
    void StartListeningForConnection(string peerId);

    /// <summary>
    /// 着信接続監視を停止する。
    /// </summary>
    void StopListeningForConnection();

    /// <summary>現在着信監視中のピア ID（未監視なら null）。</summary>
    string? CurrentListeningPeerId => null;

    /// <summary>rere #D-001(b): QR に載せる自分の長期公開鍵(base64url SPKI)。未対応実装は空文字。</summary>
    string PublicKeyForQr => string.Empty;

    /// <summary>
    /// rere #D-001(a) Phase B: QR に載せる PairingNonce（32hex）。Bridge が <c>/pair/token</c> を叩く時に
    /// <c>sessions/{sid}/PairingNonce</c> との一致を確認するための短命トークン。未対応実装は空文字。
    /// </summary>
    string LastPairingNonce => string.Empty;

    // === オンデマンド接続（送信側が呼ぶ） ===

    /// <summary>
    /// ペアリング済みピアに接続する（Offer を作成して送信）。
    /// Firebase シグナリングで SDP/ICE 交換 → WebRTC 確立。
    /// </summary>
    Task ConnectToPeerAsync(string peerId, CancellationToken ct = default);

    /// <summary>
    /// 軽量プローブで経路だけ判定して返す（データチャンネルは確立せず即切断）。
    /// TCP → UDP ホールパンチの順で試行し、両方失敗した場合は <see cref="ConnectionRoute.Relay"/> を推定で返す
    /// （リレーへの実接続は Cloudflare Workers の DO duration コスト回避のため行わない）。
    /// 既に通常接続中（<see cref="PeerState.Connecting"/> / <see cref="PeerState.Connected"/>）の場合は
    /// 現在の <see cref="Route"/> をそのまま返し、何もしない。
    /// メンバーリストの経路バッジを「ファイル送信前から」表示する用途。
    /// </summary>
    /// <returns>判定された接続経路。判定不能（例外時）は <see cref="ConnectionRoute.Unknown"/>。</returns>
    Task<ConnectionRoute> ProbeRouteAsync(string peerId, CancellationToken ct = default)
        => Task.FromResult(ConnectionRoute.Unknown);

    /// <summary>
    /// DataChannel 経由でバイナリデータを送信する。
    /// </summary>
    Task SendAsync(byte[] data, CancellationToken ct = default);

    /// <summary>
    /// DataChannel 経由でバイナリデータを送信する（<see cref="ReadOnlyMemory{T}"/> 版）。
    /// P-1: 送信パスの alloc 削減のため、ArrayPool 借用バッファをコピーなしで渡せるオーバーロードを追加。
    /// デフォルト実装は <c>ToArray()</c> で旧 API に委譲する。<see cref="ConnectionService"/> は
    /// transport の Memory 版に直結することでホットパスのコピーを完全に解消する。
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => SendAsync(data.ToArray(), ct);

    /// <summary>
    /// 接続を切断し、リソースを解放する。
    /// </summary>
    Task DisconnectAsync(CancellationToken ct = default);

    // === #D-001a Phase B: pairs/{pairId} SSoT 連携 ===

    /// <summary>外部から pairId を導出するための公開ヘルパー（既定実装は空文字＝旧テストの互換維持）。</summary>
    string GeneratePairIdFor(string peerId) => string.Empty;

    /// <summary>Firebase pairs/{pairId} を SSoT として削除する（既定実装は no-op＝旧テストの互換維持）。</summary>
    Task DeletePairFromFirebaseAsync(string peerId, CancellationToken ct = default) => Task.CompletedTask;
}
