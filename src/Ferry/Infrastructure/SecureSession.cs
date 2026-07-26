using System;
using System.Security.Cryptography;
using System.Threading;

namespace Ferry.Infrastructure;

/// <summary>
/// rere #D-001(b): 1 接続分の暗号セッション状態を保持する（送信鍵 keyTx / 受信鍵 keyRx / 送信カウンタ /
/// 受信リプレイ窓）。ConnectionService が接続確立ごとに生成し、切断時に破棄する。
///
/// 送信は <see cref="EncryptOutgoing"/> がカウンタを単調増加させて封筒化（並列送信でも Interlocked で一意採番）。
/// 受信は <see cref="DecryptIncoming"/> が復号 → カウンタを anti-replay 窓で検証し、リプレイ（同一 nonce 再利用）と
/// 窓外の古いカウンタを冪等に拒否する。UDP の順不同/重複到達に耐えるため IPsec 風スライディングウィンドウ（1024 幅）。
///
/// SharedSecret を持たない既ペアは SecureSession を構築せず ConnectionService が平文素通しする
/// （本クラスは「鍵が確立している接続」専用。null フォールバックは呼び出し側の責務）。
/// </summary>
public sealed class SecureSession
{
    /// <summary>anti-replay 窓の幅（カウンタ個数）。UDP 順不同の許容範囲。</summary>
    public const int ReplayWindowSize = 1024;

    private readonly byte[] _keyTx;
    private readonly byte[] _keyRx;

    /// <summary>Phase 1 HMAC 相互認証で使うセッション認証鍵。</summary>
    public byte[] HmacKey { get; }

    // 送信カウンタ。0 から単調増加（Interlocked で並列採番）。型は受信窓と揃えて ulong に統一する
    // （long/ulong 混在は理論上のカウンタ枯渇時に負インデックスを生む。crypto レビュー #D-001b で指摘）。
    private ulong _sendCounter;

    // 受信 anti-replay 窓。_rxInitialized=false の間は未受信。_rxHighest はこれまで受理した最大カウンタ。
    private readonly Lock _rxLock = new();
    private bool _rxInitialized;
    private ulong _rxHighest;
    private readonly bool[] _rxSeen = new bool[ReplayWindowSize];

    public SecureSession(byte[] keyTx, byte[] keyRx, byte[] hmacKey)
    {
        _keyTx = keyTx;
        _keyRx = keyRx;
        HmacKey = hmacKey;
    }

    /// <summary>
    /// 平文を送信用に封筒化する。カウンタは Interlocked で単調増加し、並列送信でも nonce が一意になる。
    /// </summary>
    public byte[] EncryptOutgoing(ReadOnlySpan<byte> plaintext)
    {
        // Interlocked.Increment(ref ulong) は最初の呼び出しで 1 を返すので 1 引いて 0 始まりにする。
        var counter = Interlocked.Increment(ref _sendCounter) - 1;
        return PairCrypto.Encrypt(_keyTx, counter, plaintext);
    }

    /// <summary>
    /// 受信封筒を復号する。改竄時は <see cref="CryptographicException"/> を投げ、
    /// リプレイ/窓外の古いカウンタは null を返す（呼び出し側は黙って破棄する）。
    /// </summary>
    public byte[]? DecryptIncoming(ReadOnlySpan<byte> envelope)
    {
        var (plaintext, counter) = PairCrypto.Decrypt(_keyRx, envelope); // 改竄は例外
        return AcceptCounter(counter) ? plaintext : null;
    }

    /// <summary>
    /// anti-replay スライディングウィンドウでカウンタを検証する。
    /// 受理可能なら窓を更新して true、リプレイ（既受信）や窓より古ければ false。
    /// 受信ループは単一スレッドだが、防御的に lock で保護する（コストは AES-GCM に対し無視可能）。
    /// </summary>
    private bool AcceptCounter(ulong counter)
    {
        const ulong window = ReplayWindowSize;
        lock (_rxLock)
        {
            if (!_rxInitialized)
            {
                // 初回受信。窓をクリアして当該スロットだけ立てる。
                _rxInitialized = true;
                Array.Clear(_rxSeen);
                _rxSeen[(int)(counter % window)] = true;
                _rxHighest = counter;
                return true;
            }

            if (counter > _rxHighest)
            {
                // 窓を前進。新たに窓内へ入るスロット（_rxHighest+1..counter）は過去のカウンタが
                // 周回した残骸を持ちうるのでクリアしてから当該を立てる。
                var diff = counter - _rxHighest;
                if (diff >= window)
                {
                    Array.Clear(_rxSeen);
                }
                else
                {
                    for (var c = _rxHighest + 1; c <= counter; c++)
                        _rxSeen[(int)(c % window)] = false;
                }
                _rxSeen[(int)(counter % window)] = true;
                _rxHighest = counter;
                return true;
            }

            // counter <= _rxHighest: 窓内なら未受信のみ受理、窓外（古すぎ）は拒否。
            // 全て ulong 演算（counter <= _rxHighest なので減算はアンダーフローしない）。
            if (_rxHighest - counter >= window)
                return false;

            var slot = (int)(counter % window);
            if (_rxSeen[slot])
                return false; // リプレイ
            _rxSeen[slot] = true;
            return true;
        }
    }
}
