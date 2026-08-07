using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>OnFrame/Start/OnTimeout の結果（送るフレーム・上位へ渡す平文・状態遷移）。</summary>
public enum SecureOutcome
{
    /// <summary>状態変化なし（ハンドシェイク継続中）。</summary>
    None,
    /// <summary>暗号セッション確立（以降のアプリデータは封筒化）。</summary>
    Established,
    /// <summary>相手が暗号非対応 → 平文にフォールバック（現状動作）。</summary>
    FellBackToPlaintext,
    /// <summary>HMAC 不一致 = 偽ピア/鍵不一致 → 即切断すべき。</summary>
    Failed,
}

/// <summary>1 回の OnFrame/Start/OnTimeout が生む副作用（送信フレーム・配送平文・遷移）。</summary>
public sealed class SecureChannelStep
{
    /// <summary>transport にそのまま書き出すハンドシェイクフレーム（0x30/0x31）。</summary>
    public List<byte[]> Send { get; } = [];

    /// <summary>上位（TransferService）へ渡すアプリ平文（復号済み or 平文素通し）。順序保持・複数可。</summary>
    public List<byte[]> Deliver { get; } = [];

    /// <summary>状態遷移の通知。呼び出し側は Established/FellBack で送信ゲートを開け、Failed で切断する。</summary>
    public SecureOutcome Outcome { get; set; } = SecureOutcome.None;
}

/// <summary>
/// rere #D-001(b) Phase 2/3: 1 接続分の暗号ハンドシェイク + 封筒化を司る自己ネゴシエーション状態機械。
///
/// transport 接続確立直後、暗号対応（v1.0.48 以降は常時 ON、PairSecret 保有が条件）の両端が
/// SecureHello(0x30: sessionNonce + challenge) を送り合い、相手の nonce からセッション鍵を導出して
/// SecureConfirm(0x31: HMAC 応答) を交換する。両者の HMAC が一致して初めて暗号セッション確立。
///
/// ネゴシエーション（混在環境の安全な後方互換）:
///   - 両端対応 → ハンドシェイク成立 → 以降 AES-GCM 封筒。
///   - 自分が非対応(PairSecret 無/フラグ OFF) → Hello を送らず、届いた 0x30/0x31 は黙って捨て、全て平文配送
///     （= 公開鍵交換前の旧ペア互換。ConnectionService は PairSecret 未保有時そもそも本クラスを生成しない）。
///   - 自分が対応(PairSecret 有)なのに相手が Hello を返さない → **平文へ降格せず Failed で切断する**。
///     PairSecret はペアリング時に両端が ECDH で対称に導出するため、正規のピアは必ず Hello を返す。
///     ここで降格を許すと、経路上の第三者が任意フレームを 1 通注入する / Hello を握り潰すだけで
///     両端を平文へ収束させられる（HMAC 相互認証も AES-GCM も確立前にスキップされる）ダウングレード
///     攻撃が成立する。リレー経路は入室に pairId しか要求しないため、この降格経路は実際に到達可能だった。
///
/// I/O 非依存の純ロジック（フレーム in → 副作用 out）なので、ConnectionService 結線（2 台実機検証）に
/// 先立って negotiation/封筒/フォールバック/HMAC 失敗の全分岐を単体テストで決定的に固定できる。
/// 役割(roleTag)・鍵導出方向は <see cref="PairingHandshake"/> / <see cref="PairCrypto"/> の規則に委譲する。
/// </summary>
public sealed class SecureChannel
{
    private enum S { Init, AwaitingHello, AwaitingConfirm, Secure, Plaintext, Closed }

    /// <summary>Hello の sessionNonce 長（HKDF salt 素材）。</summary>
    public const int SessionNonceSize = 16;
    private const int ChallengeSize = PairingHandshake.ChallengeNonceSize; // 16
    private const int HelloPayloadSize = SessionNonceSize + ChallengeSize;

    private readonly byte[]? _pairSecret;
    private readonly bool _isOfferer;
    private readonly byte _myRoleTag;
    private readonly byte _peerRoleTag;
    private readonly byte[] _mySessionNonce;
    private readonly byte[] _myChallenge;

    private S _state = S.Init;
    private SecureSession? _session;
    private byte[]? _hmacKey;
    private byte[]? _pendingPeerConfirm; // 0x31 が 0x30 より先に届いたときの一時バッファ（順不同 transport 対策）

    // Start 前（Init 状態）に届いたフレームの保持先。接続確立直後の attach〜Start の隙間に相手 Hello が
    // 先着しても取りこぼさないため、ここに溜めて Start でまとめて処理する（attach レース対策）。
    private readonly List<byte[]> _preStartBuffer = [];

    /// <summary>暗号セッションが確立済みか（確立後の送信は封筒化する）。</summary>
    public bool IsSecure => _state == S.Secure;

    /// <summary>PairSecret を保持する（＝暗号化されるべき）チャネルか。
    /// false は公開鍵交換前の旧ペアで、平文フォールバックが正当な唯一のケース
    /// （<see cref="Start"/> が即 <see cref="SecureOutcome.FellBackToPlaintext"/> を返す経路）。
    /// 送信側はこれを見て「PairSecret があるのに未確立」を平文で流さず失敗させる（fail-closed）。</summary>
    public bool HasPairSecret => _pairSecret != null;

    /// <summary>
    /// 接続相手とのチャネルを構築する。<paramref name="secureEnabled"/> が false または
    /// <paramref name="pairSecret"/> が null のときは平文専用チャネル（Start は即フォールバックを返す）。
    /// </summary>
    public SecureChannel(string myDeviceId, string peerDeviceId, byte[]? pairSecret, bool secureEnabled)
    {
        if (secureEnabled && pairSecret is { Length: > 0 })
        {
            _pairSecret = pairSecret;
            _isOfferer = PairingHandshake.IsOffererRole(myDeviceId, peerDeviceId);
            _myRoleTag = _isOfferer ? PairingHandshake.RoleLow : PairingHandshake.RoleHigh;
            _peerRoleTag = PairingHandshake.PeerRoleTag(_myRoleTag);
            _mySessionNonce = PairCrypto.RandomBytes(SessionNonceSize);
            _myChallenge = PairingHandshake.BuildChallenge();
        }
        else
        {
            _mySessionNonce = [];
            _myChallenge = [];
        }
    }

    /// <summary>
    /// ハンドシェイクを開始する。対応時は Hello を Send に積み、Start 前にバッファした先着フレームを
    /// まとめて処理する（attach レースの救済）。非対応(_pairSecret==null)時は即平文フォールバック。
    /// </summary>
    public SecureChannelStep Start()
    {
        var step = new SecureChannelStep();
        if (_pairSecret == null)
        {
            _state = S.Plaintext;
            step.Outcome = SecureOutcome.FellBackToPlaintext;
            DrainPreStartBuffer(step); // 平文として溜まっていた分を配送
            return step;
        }
        _state = S.AwaitingHello;
        step.Send.Add(BuildHello());
        DrainPreStartBuffer(step); // 先着していた相手 Hello/Confirm を今処理
        return step;
    }

    /// <summary>transport から届いた 1 フレームを処理する。</summary>
    public SecureChannelStep OnFrame(byte[] frame)
    {
        var step = new SecureChannelStep();
        Process(frame, step);
        return step;
    }

    private void Process(byte[] frame, SecureChannelStep step)
    {
        switch (_state)
        {
            case S.Init:
                // Start 前に届いた = attach 直後の先着。取りこぼさずバッファし、Start でまとめて捌く。
                _preStartBuffer.Add(frame);
                break;

            case S.Plaintext:
                if (!IsHandshakeFrame(frame)) step.Deliver.Add(frame); // 0x30/0x31 の混入は捨てる
                break;

            case S.Secure:
                if (IsHandshakeFrame(frame)) break; // 確立後の遅延 dup ハンドシェイクは無視
                try
                {
                    var pt = _session!.DecryptIncoming(frame);
                    if (pt != null) step.Deliver.Add(pt); // リプレイ/窓外は null（黙って破棄）
                }
                catch (CryptographicException) { /* 改竄/遅延 dup: 黙って破棄 */ }
                break;

            case S.AwaitingHello:
                HandleAwaitingHello(frame, step);
                break;

            case S.AwaitingConfirm:
                HandleAwaitingConfirm(frame, step);
                break;

            // Closed: 何もしない
        }
    }

    private void DrainPreStartBuffer(SecureChannelStep step)
    {
        if (_preStartBuffer.Count == 0) return;
        var buffered = _preStartBuffer.ToArray();
        _preStartBuffer.Clear();
        foreach (var f in buffered)
            Process(f, step); // 状態は AwaitingHello / Plaintext に遷移済みなので通常処理に乗る
    }

    /// <summary>ハンドシェイクのタイムアウト。PairSecret 保有チャネルは平文へ降格せず失敗（切断）にする。</summary>
    public SecureChannelStep OnTimeout()
    {
        var step = new SecureChannelStep();
        if (_state is S.AwaitingHello or S.AwaitingConfirm)
        {
            // AwaitingHello / AwaitingConfirm は PairSecret 保有時にしか入らない状態。同じ PairSecret を
            // 持つ正規ピアならハンドシェイクは対称に進むので、Hello / Confirm が返らないのは異常
            // （相手が鍵を失っている、または経路上の第三者がフレームを握り潰している）。
            // 旧実装は Hello 待ちだけ平文へ降格していたが、それは「Hello を落とせば暗号が外れる」という
            // ダウングレード攻撃そのものなので、安全側で切断する。
            _state = S.Closed;
            step.Outcome = SecureOutcome.Failed;
        }
        return step;
    }

    /// <summary>送信アプリデータを封筒化する（IsSecure のときのみ呼ぶ。平文時は呼び出し側が素通しする）。</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => _session!.EncryptOutgoing(plaintext);

    private void HandleAwaitingHello(byte[] frame, SecureChannelStep step)
    {
        if (frame.Length == 1 + HelloPayloadSize && frame[0] == TransferProtocol.SecureHello)
        {
            var peerNonce = frame[1..(1 + SessionNonceSize)];
            var peerChallenge = frame[(1 + SessionNonceSize)..(1 + HelloPayloadSize)];
            DeriveKeys(peerNonce);
            step.Send.Add(BuildConfirm(PairingHandshake.BuildResponse(_hmacKey!, peerChallenge, _myRoleTag)));
            _state = S.AwaitingConfirm;

            // 先に届いていた Confirm があれば今処理する（順不同到達対策）。
            if (_pendingPeerConfirm != null)
            {
                var c = _pendingPeerConfirm;
                _pendingPeerConfirm = null;
                ApplyConfirm(c, step);
            }
        }
        else if (frame.Length >= 1 && frame[0] == TransferProtocol.SecureConfirm)
        {
            _pendingPeerConfirm = frame; // Hello より先に届いた → バッファ
        }
        else
        {
            // Hello 待ちに平文アプリデータが来た。PairSecret 保有ペアの正規ピアは必ず Hello を返すので、
            // これは (a) 経路上の第三者がフレームを注入した (b) 相手が鍵を失っている のいずれか。
            // どちらも平文で続行してよい理由にならないため、降格せず切断する（フレームも配送しない）。
            // 暗号非対応の旧ペア（PairSecret 無し）は ctor が平文専用チャネルを作るのでここへ来ない。
            _state = S.Closed;
            step.Outcome = SecureOutcome.Failed;
        }
    }

    private void HandleAwaitingConfirm(byte[] frame, SecureChannelStep step)
    {
        if (frame.Length >= 1 && frame[0] == TransferProtocol.SecureConfirm)
            ApplyConfirm(frame, step);
        // 0x30 の dup やその他は無視（両端が送信ゲートしているのでアプリデータはまだ来ない）。
    }

    private void ApplyConfirm(byte[] frame, SecureChannelStep step)
    {
        var response = frame[1..];
        if (PairingHandshake.VerifyResponse(_hmacKey!, _myChallenge, _peerRoleTag, response))
        {
            _state = S.Secure;
            step.Outcome = SecureOutcome.Established;
        }
        else
        {
            _state = S.Closed;
            step.Outcome = SecureOutcome.Failed;
        }
    }

    private void DeriveKeys(byte[] peerNonce)
    {
        // offerNonce/answerNonce は役割で固定（両端が同じ salt 順序を組む）。
        var offerNonce = _isOfferer ? _mySessionNonce : peerNonce;
        var answerNonce = _isOfferer ? peerNonce : _mySessionNonce;
        var (keyTx, keyRx, hmacKey) = PairCrypto.DeriveSessionKeys(_pairSecret!, offerNonce, answerNonce, _isOfferer);
        _hmacKey = hmacKey;
        _session = new SecureSession(keyTx, keyRx, hmacKey);
    }

    private byte[] BuildHello()
    {
        var f = new byte[1 + HelloPayloadSize];
        f[0] = TransferProtocol.SecureHello;
        Buffer.BlockCopy(_mySessionNonce, 0, f, 1, SessionNonceSize);
        Buffer.BlockCopy(_myChallenge, 0, f, 1 + SessionNonceSize, ChallengeSize);
        return f;
    }

    private static byte[] BuildConfirm(byte[] response)
    {
        var f = new byte[1 + response.Length];
        f[0] = TransferProtocol.SecureConfirm;
        Buffer.BlockCopy(response, 0, f, 1, response.Length);
        return f;
    }

    private static bool IsHandshakeFrame(byte[] f)
        => f.Length >= 1 && (f[0] == TransferProtocol.SecureHello || f[0] == TransferProtocol.SecureConfirm);
}
