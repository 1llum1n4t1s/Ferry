using System;

namespace Ferry.Infrastructure;

/// <summary>
/// rere #D-001(b) Phase 1: transport 接続確立直後の HMAC 相互認証ハンドシェイクの純関数群。
///
/// 偽ピア / signaling すり替え MITM を排除するゲート。両端がセッション認証鍵(hmacKey)を共有していれば、
/// 互いに乱数チャレンジを送り、HMAC(hmacKey, peerChallenge || myRoleTag) を応答して検証する。一致しなければ即切断。
///
/// roleTag は deviceId 序列（GeneratePairId と同じ Ordinal 比較）で固定的に決まり、
/// どちらが実際に SDP offer を出したかとは独立。各端が自分の roleTag で応答し相手は相手の roleTag で検証することで、
/// チャレンジをそのまま反射する reflection attack を防ぐ。
///
/// 検証は <see cref="PairCrypto.VerifyHmac"/> が定数時間比較する。ConnectionService 層で新メッセージ種別
/// 0x30(Challenge)/0x31(Response) を処理し TransferService には渡さない（transport 上の認証ゲート）。
/// </summary>
public static class PairingHandshake
{
    /// <summary>チャレンジ乱数の長さ（バイト）。</summary>
    public const int ChallengeNonceSize = 16;

    /// <summary>deviceId 序列が小さい側の roleTag（GeneratePairId の Ordinal 比較で先頭になる側）。</summary>
    public const byte RoleLow = 0x01;

    /// <summary>deviceId 序列が大きい側の roleTag。</summary>
    public const byte RoleHigh = 0x02;

    /// <summary>
    /// deviceId 序列（Ordinal 比較）から「自分が offerer 役か」を決める唯一のソース。GeneratePairId と同じ規則。
    /// DeriveSessionKeys の isOfferer も RoleTagFor もこれを通すことで、ConnectionService が両者を別々に
    /// 計算して食い違わせる（→ keyTx/keyRx 逆転で復号全失敗）誤用を防ぐ（crypto レビュー #D-001b）。
    /// 同一 deviceId（self-connect）は roleTag が対称化して reflection 防御が無効になるため例外で弾く。
    /// </summary>
    public static bool IsOffererRole(string myDeviceId, string peerDeviceId)
    {
        var cmp = string.CompareOrdinal(myDeviceId, peerDeviceId);
        if (cmp == 0)
            throw new ArgumentException("自分自身の deviceId とはペアになれません（self-connect）。", nameof(peerDeviceId));
        return cmp < 0;
    }

    /// <summary>
    /// deviceId 序列から自分の roleTag を決める（<see cref="IsOffererRole"/> から導出）。両端で一貫する。
    /// </summary>
    public static byte RoleTagFor(string myDeviceId, string peerDeviceId)
        => IsOffererRole(myDeviceId, peerDeviceId) ? RoleLow : RoleHigh;

    /// <summary>相手の roleTag を返す（自分の裏返し）。相手の応答を検証する際に使う。</summary>
    public static byte PeerRoleTag(byte myRoleTag)
        => myRoleTag == RoleLow ? RoleHigh : RoleLow;

    /// <summary>新しいチャレンジ乱数を生成する。</summary>
    public static byte[] BuildChallenge() => PairCrypto.RandomBytes(ChallengeNonceSize);

    /// <summary>
    /// 相手のチャレンジへの応答を作る: HMAC(hmacKey, peerChallenge || myRoleTag)。
    /// </summary>
    public static byte[] BuildResponse(byte[] hmacKey, ReadOnlySpan<byte> peerChallenge, byte myRoleTag)
        => PairCrypto.ComputeHmac(hmacKey, peerChallenge, myRoleTag);

    /// <summary>
    /// 自分のチャレンジに対する相手の応答を検証する（相手は相手の roleTag で応答しているはず）。
    /// </summary>
    public static bool VerifyResponse(byte[] hmacKey, ReadOnlySpan<byte> myChallenge, byte peerRoleTag, ReadOnlySpan<byte> peerResponse)
        => PairCrypto.VerifyHmac(hmacKey, myChallenge, peerRoleTag, peerResponse);
}
