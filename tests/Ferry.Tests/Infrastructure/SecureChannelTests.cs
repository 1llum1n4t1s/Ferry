using System.Collections.Generic;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// rere #D-001(b) Phase 2/3: SecureChannel（暗号ハンドシェイクのネゴシエーション状態機械）を検証する。
///
/// ConnectionService への結線（2 台実機）に先立ち、ここで negotiation の全分岐を決定的に固定する:
/// 両端対応→ハンドシェイク成立→封筒往復 / 片側非対応→平文フォールバック / HMAC 不一致→切断要求 /
/// 順不同（Confirm が Hello より先着）の救済。役割と鍵導出は PairCrypto/PairingHandshake に委譲済み。
/// </summary>
public class SecureChannelTests
{
    private static readonly string DevA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // Ordinal で小 = offerer 役
    private static readonly string DevB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static byte[] Secret(byte fill)
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++) s[i] = fill;
        return s;
    }

    /// <summary>2 チャネル間でフレームを枯渇まで交換し、各自の最終 Outcome を返す。</summary>
    private static (SecureOutcome A, SecureOutcome B) RunHandshake(SecureChannel a, SecureChannel b)
    {
        var outA = SecureOutcome.None;
        var outB = SecureOutcome.None;
        var toA = new Queue<byte[]>(); // B が出したフレーム（A 宛て）
        var toB = new Queue<byte[]>(); // A が出したフレーム（B 宛て）

        void Pump(SecureChannelStep step, Queue<byte[]> dest, ref SecureOutcome acc)
        {
            foreach (var f in step.Send) dest.Enqueue(f);
            if (step.Outcome != SecureOutcome.None) acc = step.Outcome;
        }

        Pump(a.Start(), toB, ref outA);
        Pump(b.Start(), toA, ref outB);

        while (toA.Count > 0 || toB.Count > 0)
        {
            if (toA.Count > 0) Pump(a.OnFrame(toA.Dequeue()), toB, ref outA);
            if (toB.Count > 0) Pump(b.OnFrame(toB.Dequeue()), toA, ref outB);
        }
        return (outA, outB);
    }

    [Fact]
    public void 両端対応なら相互認証が成立し封筒が往復復号できること()
    {
        var secret = Secret(0x11);
        var a = new SecureChannel(DevA, DevB, secret, secureEnabled: true);
        var b = new SecureChannel(DevB, DevA, secret, secureEnabled: true);

        var (outA, outB) = RunHandshake(a, b);

        Assert.Equal(SecureOutcome.Established, outA);
        Assert.Equal(SecureOutcome.Established, outB);
        Assert.True(a.IsSecure);
        Assert.True(b.IsSecure);

        // A→B 封筒往復
        var msg = new byte[] { TransferProtocol.FileMeta, 1, 2, 3, 4, 5 };
        var env = a.Encrypt(msg);
        Assert.NotEqual(msg, env); // 暗号化されている（平文と違う）
        var recv = b.OnFrame(env);
        Assert.Equal(msg, Assert.Single(recv.Deliver));

        // B→A も別鍵方向で復号できる
        var msg2 = new byte[] { TransferProtocol.FileChunk, 9, 9, 9 };
        var back = b.Encrypt(msg2);
        Assert.Equal(msg2, Assert.Single(a.OnFrame(back).Deliver));
    }

    [Fact]
    public void 鍵が違うとHMACが一致せず両端が失敗を返すこと()
    {
        var a = new SecureChannel(DevA, DevB, Secret(0x11), secureEnabled: true);
        var b = new SecureChannel(DevB, DevA, Secret(0x22), secureEnabled: true); // 別 PairSecret

        var (outA, outB) = RunHandshake(a, b);

        Assert.Equal(SecureOutcome.Failed, outA);
        Assert.Equal(SecureOutcome.Failed, outB);
        Assert.False(a.IsSecure);
        Assert.False(b.IsSecure);
    }

    [Fact]
    public void 非対応チャネルはStartで即平文フォールバックしアプリデータを素通しすること()
    {
        // フラグ OFF 相当（ConnectionService は本来チャネルを生成しないが、防御的に検証）。
        var ch = new SecureChannel(DevA, DevB, Secret(0x11), secureEnabled: false);
        var start = ch.Start();
        Assert.Equal(SecureOutcome.FellBackToPlaintext, start.Outcome);
        Assert.Empty(start.Send); // Hello を送らない

        // アプリデータは素通しで配送、紛れ込んだハンドシェイクフレームは捨てる。
        var app = new byte[] { TransferProtocol.FileMeta, 7, 7 };
        Assert.Equal(app, Assert.Single(ch.OnFrame(app).Deliver));
        var stray = new byte[1 + SecureChannel.SessionNonceSize + 16];
        stray[0] = TransferProtocol.SecureHello;
        Assert.Empty(ch.OnFrame(stray).Deliver);
    }

    [Fact]
    public void 相手が平文アプリデータを送ってきたら平文フォールバックして配送すること()
    {
        // 対応側 A が Hello を出したが、相手は非対応で平文アプリデータを返してくるケース。
        var a = new SecureChannel(DevA, DevB, Secret(0x11), secureEnabled: true);
        var start = a.Start();
        Assert.Single(start.Send); // Hello 送信済み（AwaitingHello）

        var app = new byte[] { TransferProtocol.FileMeta, 5 };
        var step = a.OnFrame(app);
        Assert.Equal(SecureOutcome.FellBackToPlaintext, step.Outcome);
        Assert.Equal(app, Assert.Single(step.Deliver)); // 取りこぼさず配送
        Assert.False(a.IsSecure);
    }

    [Fact]
    public void Hello待ちのタイムアウトは平文フォールバックすること()
    {
        var a = new SecureChannel(DevA, DevB, Secret(0x11), secureEnabled: true);
        a.Start();
        var step = a.OnTimeout();
        Assert.Equal(SecureOutcome.FellBackToPlaintext, step.Outcome);
    }

    [Fact]
    public void Start前に先着したフレームをバッファし取りこぼさず成立すること()
    {
        // attach レース: 相手の Hello が自分の Start より先に届くケース。Init バッファで救済して成立する。
        var secret = Secret(0x44);
        var a = new SecureChannel(DevA, DevB, secret, secureEnabled: true);
        var b = new SecureChannel(DevB, DevA, secret, secureEnabled: true);

        var bHello = b.Start().Send[0];

        // A は Start 前に B の Hello を受信 → Init でバッファ（未成立・送信なし）。
        var early = a.OnFrame(bHello);
        Assert.Equal(SecureOutcome.None, early.Outcome);
        Assert.Empty(early.Send);
        Assert.False(a.IsSecure);

        // 以降は通常どおりフレーム交換すれば、バッファ済み Hello も捌かれて両端成立する。
        var outA = SecureOutcome.None;
        var outB = SecureOutcome.None;
        var toA = new Queue<byte[]>();
        var toB = new Queue<byte[]>();
        var aStart = a.Start();
        foreach (var f in aStart.Send) toB.Enqueue(f);
        if (aStart.Outcome != SecureOutcome.None) outA = aStart.Outcome;
        while (toA.Count > 0 || toB.Count > 0)
        {
            if (toB.Count > 0)
            {
                var s = b.OnFrame(toB.Dequeue());
                foreach (var f in s.Send) toA.Enqueue(f);
                if (s.Outcome != SecureOutcome.None) outB = s.Outcome;
            }
            if (toA.Count > 0)
            {
                var s = a.OnFrame(toA.Dequeue());
                foreach (var f in s.Send) toB.Enqueue(f);
                if (s.Outcome != SecureOutcome.None) outA = s.Outcome;
            }
        }

        Assert.Equal(SecureOutcome.Established, outA);
        Assert.Equal(SecureOutcome.Established, outB);
        Assert.True(a.IsSecure);
        Assert.True(b.IsSecure);
    }

    [Fact]
    public void Confirmが先着しても順不同を救済して成立すること()
    {
        var secret = Secret(0x33);
        var a = new SecureChannel(DevA, DevB, secret, secureEnabled: true);
        var b = new SecureChannel(DevB, DevA, secret, secureEnabled: true);

        var aHello = a.Start().Send[0];
        var bHello = b.Start().Send[0];

        // B は A の Hello を受けて Confirm を生成。
        var bConfirm = b.OnFrame(aHello).Send[0];

        // A には B の Confirm が Hello より「先に」届く（順不同）。バッファされるだけで未成立。
        var early = a.OnFrame(bConfirm);
        Assert.Equal(SecureOutcome.None, early.Outcome);
        Assert.False(a.IsSecure);

        // 続いて B の Hello が届くと、A は Confirm を返しつつバッファ済み Confirm を適用して成立する。
        var late = a.OnFrame(bHello);
        Assert.Equal(SecureOutcome.Established, late.Outcome);
        Assert.True(a.IsSecure);
        Assert.NotEmpty(late.Send); // 自分の Confirm も返している
    }
}
