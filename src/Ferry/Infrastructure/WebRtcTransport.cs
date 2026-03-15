using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Ferry.Infrastructure;

/// <summary>
/// SIPSorcery を使用した WebRTC DataChannel トランスポート。
/// ICE/DTLS/SCTP のネゴシエーションと DataChannel の作成・管理を行う。
/// </summary>
public sealed class WebRtcTransport : IDisposable
{
    // SIPSorcery 内部ログの初期化（静的コンストラクタで1回だけ）
    static WebRtcTransport()
    {
        SIPSorcery.LogFactory.Set(new SIPSorceryLogFactory());
    }

    // ICE サーバー設定
    private const string StunServerUrl = "stun:1llum1n4t1.net:3478";
    private const string TurnServerUrl = "turn:1llum1n4t1.net:3478";
    private const string TurnsServerUrl = "turns:1llum1n4t1.net:5349";

    // coturn shared secret（HMAC-SHA1 一時クレデンシャル生成用）
    // TODO: appsettings.json または環境変数から読み込むように変更
    private const string TurnSharedSecret = "d14767da8c2ef61ce870013d6a9b45175054551b41622e7b850a2919400f23c1";

    /// <summary>クレデンシャルの有効期間（秒）。</summary>
    private const int CredentialTtlSeconds = 86400; // 24時間

    private const string DataChannelLabel = "ferry-data";

    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dataChannel;

    // ICE 候補タイプの追跡（接続経路判定用）
    private bool _hasHostCandidate;
    private bool _hasSrflxCandidate;
    private bool _hasRelayCandidate;

    // Vanilla ICE: gathering 中に収集した全候補（SDP に手動追加するため）
    private readonly List<RTCIceCandidate> _gatheredCandidates = [];

    /// <summary>DataChannel でバイナリデータを受信したときに発火するイベント。</summary>
    public event EventHandler<byte[]>? DataReceived;

    /// <summary>DataChannel が開いたときに発火するイベント。</summary>
    public event EventHandler? ChannelOpened;

    /// <summary>DataChannel が閉じたときに発火するイベント。</summary>
    public event EventHandler? ChannelClosed;

    /// <summary>ICE Candidate が生成されたときに発火するイベント。</summary>
    public event EventHandler<RTCIceCandidate>? IceCandidateGenerated;

    /// <summary>接続経路が確定したときに発火するイベント。</summary>
    public event EventHandler<ConnectionRoute>? RouteChanged;

    /// <summary>DataChannel が開いているかどうか。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>確定した接続経路。</summary>
    public ConnectionRoute Route { get; private set; } = ConnectionRoute.Unknown;

    /// <summary>
    /// ICE サーバー設定を含む RTCConfiguration を生成する。
    /// </summary>
    private static RTCConfiguration CreateConfig()
    {
        var (username, credential) = GenerateTurnCredentials();
        return new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = StunServerUrl },
                new() { urls = TurnServerUrl, username = username, credential = credential },
                new() { urls = TurnsServerUrl, username = username, credential = credential },
            }
        };
    }

    /// <summary>
    /// RTCPeerConnection を初期化し、共通イベントを登録する。
    /// </summary>
    private RTCPeerConnection InitPeerConnection()
    {
        var pc = new RTCPeerConnection(CreateConfig());

        pc.onicecandidate += candidate =>
        {
            // ICE 候補タイプを追跡（接続経路判定に使用）
            switch (candidate.type)
            {
                case RTCIceCandidateType.host:
                    _hasHostCandidate = true;
                    break;
                case RTCIceCandidateType.srflx:
                    _hasSrflxCandidate = true;
                    break;
                case RTCIceCandidateType.relay:
                    _hasRelayCandidate = true;
                    break;
            }

            // Vanilla ICE: SDP に後で追加するために全候補を収集
            _gatheredCandidates.Add(candidate);

            IceCandidateGenerated?.Invoke(this, candidate);
        };

        pc.oniceconnectionstatechange += iceState =>
        {
            Util.Logger.Log($"ICE 接続状態: {iceState}");
        };

        pc.onicegatheringstatechange += gatherState =>
        {
            Util.Logger.Log($"ICE 収集状態: {gatherState}");
        };

        pc.onconnectionstatechange += state =>
        {
            Util.Logger.Log($"WebRTC 接続状態: {state}");

            if (state == RTCPeerConnectionState.connected)
            {
                // 接続経路を判定: WebRTC は host > srflx > relay の優先順で接続する
                Route = DetermineConnectionRoute();
                Util.Logger.Log($"接続経路: {Route}");
                RouteChanged?.Invoke(this, Route);
            }
            else if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
            {
                IsConnected = false;
                Route = ConnectionRoute.Unknown;
                ChannelClosed?.Invoke(this, EventArgs.Empty);
            }
        };

        // リモートからの DataChannel 受信（Answer 側）
        pc.ondatachannel += channel =>
        {
            Util.Logger.Log($"DataChannel 受信: {channel.label}");
            AttachDataChannelEvents(channel);
            _dataChannel = channel;
        };

        _pc = pc;
        return pc;
    }

    /// <summary>
    /// DataChannel にイベントハンドラを登録する。
    /// </summary>
    private void AttachDataChannelEvents(RTCDataChannel channel)
    {
        channel.onopen += () =>
        {
            Util.Logger.Log("DataChannel 開通");
            IsConnected = true;
            ChannelOpened?.Invoke(this, EventArgs.Empty);
        };

        channel.onclose += () =>
        {
            Util.Logger.Log("DataChannel 閉鎖");
            IsConnected = false;
            ChannelClosed?.Invoke(this, EventArgs.Empty);
        };

        channel.onmessage += (_, type, data) =>
        {
            if (type == DataChannelPayloadProtocols.WebRTC_Binary)
            {
                DataReceived?.Invoke(this, data);
            }
        };
    }

    /// <summary>
    /// WebRTC ピア接続を作成し、SDP Offer を生成する（発信側）。
    /// ICE 候補の収集完了を待ってから SDP を返す（Vanilla ICE）。
    /// これにより全候補（host/srflx/relay）が SDP に含まれ、
    /// trickle ICE に依存しない確実な接続が可能になる。
    /// </summary>
    /// <returns>SDP Offer 文字列（全 ICE 候補を含む）。</returns>
    public async Task<string> CreateOfferAsync(CancellationToken ct = default)
    {
        var pc = InitPeerConnection();

        // DataChannel を作成（Offer 側）
        var dc = await pc.createDataChannel(DataChannelLabel, null);
        AttachDataChannelEvents(dc);
        _dataChannel = dc;

        // SDP Offer 生成（この時点では host 候補のみ SDP に含まれる）
        _gatheredCandidates.Clear();
        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);

        // ICE 収集完了を待つ（srflx/relay 候補の収集を待機）
        await WaitForIceGatheringCompleteAsync(pc, ct);

        // SDP に収集した全候補を手動追加（SIPSorcery は localDescription を更新しないため）
        var sdp = AppendGatheredCandidatesToSdp(offer.sdp);
        Util.Logger.Log($"SDP Offer 生成完了（Vanilla ICE: 全候補収集済み, SDP長={sdp.Length}）");
        LogSdpCandidates(sdp, "Offer");
        return sdp;
    }

    /// <summary>
    /// リモートの SDP Offer を受け取り、SDP Answer を生成する（着信側）。
    /// ICE 候補の収集完了を待ってから SDP を返す（Vanilla ICE）。
    /// </summary>
    public async Task<string> CreateAnswerAsync(string remoteSdp, CancellationToken ct = default)
    {
        var pc = InitPeerConnection();

        // リモート Offer を設定
        var remoteOffer = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = remoteSdp,
        };

        Util.Logger.Log($"リモート Offer 受信（SDP長={remoteSdp.Length}）");
        LogSdpCandidates(remoteSdp, "Remote Offer");

        var result = pc.setRemoteDescription(remoteOffer);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"リモート SDP の設定に失敗: {result}");
        }

        // SIPSorcery が SDP 内の候補を正しくパースしない場合に備え、明示的に追加
        AddCandidatesFromSdp(remoteSdp);

        // SDP Answer 生成（この時点では host 候補のみ SDP に含まれる）
        _gatheredCandidates.Clear();
        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);

        // ICE 収集完了を待つ（srflx/relay 候補の収集を待機）
        await WaitForIceGatheringCompleteAsync(pc, ct);

        // SDP に収集した全候補を手動追加（SIPSorcery は localDescription を更新しないため）
        var sdp = AppendGatheredCandidatesToSdp(answer.sdp);
        Util.Logger.Log($"SDP Answer 生成完了（Vanilla ICE: 全候補収集済み, SDP長={sdp.Length}）");
        LogSdpCandidates(sdp, "Answer");
        return sdp;
    }

    /// <summary>
    /// リモートの SDP Answer を適用する（発信側が使用）。
    /// </summary>
    public Task SetRemoteAnswerAsync(string remoteSdp, CancellationToken ct = default)
    {
        if (_pc == null)
            throw new InvalidOperationException("PeerConnection が初期化されていません");

        Util.Logger.Log($"リモート Answer 受信（SDP長={remoteSdp.Length}）");
        LogSdpCandidates(remoteSdp, "Remote Answer");

        var remoteAnswer = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = remoteSdp,
        };

        var result = _pc.setRemoteDescription(remoteAnswer);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"リモート Answer の設定に失敗: {result}");
        }

        Util.Logger.Log("リモート SDP Answer 設定完了");

        // SIPSorcery が SDP 内の候補を正しくパースしない場合に備え、
        // 明示的に addIceCandidate で全候補を追加する（ベルトとサスペンダー方式）
        AddCandidatesFromSdp(remoteSdp);

        return Task.CompletedTask;
    }

    /// <summary>
    /// リモートの ICE Candidate を追加する。
    /// remote description が設定された後に呼ぶこと。
    /// </summary>
    public Task AddIceCandidateAsync(string candidate, CancellationToken ct = default)
    {
        if (_pc == null)
            throw new InvalidOperationException("PeerConnection が初期化されていません");

        var init = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMLineIndex = 0,
            sdpMid = "0",
        };
        _pc.addIceCandidate(init);
        return Task.CompletedTask;
    }

    /// <summary>
    /// DataChannel 経由でバイナリデータを送信する。
    /// </summary>
    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_dataChannel == null || !IsConnected)
            throw new InvalidOperationException("DataChannel が開いていません");

        _dataChannel.send(data);
        return Task.CompletedTask;
    }

    /// <summary>
    /// coturn の shared secret 認証用の一時クレデンシャルを生成する。
    /// username = "{有効期限UNIXタイムスタンプ}:ferry"
    /// credential = HMAC-SHA1(sharedSecret, username) の Base64
    /// </summary>
    internal static (string Username, string Credential) GenerateTurnCredentials()
    {
        var expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + CredentialTtlSeconds;
        var username = $"{expiry}:ferry";

        var keyBytes = Encoding.UTF8.GetBytes(TurnSharedSecret);
        var messageBytes = Encoding.UTF8.GetBytes(username);
        var hash = HMACSHA1.HashData(keyBytes, messageBytes);
        var credential = Convert.ToBase64String(hash);

        return (username, credential);
    }

    /// <summary>
    /// ICE 候補の収集が完了するまで待機する。
    /// Vanilla ICE: SDP に全候補を含めるために収集完了を待つ。
    /// </summary>
    private static async Task WaitForIceGatheringCompleteAsync(RTCPeerConnection pc, CancellationToken ct)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete)
        {
            Util.Logger.Log("ICE 収集: 既に完了済み");
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pc.onicegatheringstatechange += state =>
        {
            if (state == RTCIceGatheringState.complete)
                tcs.TrySetResult();
        };

        // 登録後に再チェック（レースコンディション対策）
        if (pc.iceGatheringState == RTCIceGatheringState.complete)
        {
            tcs.TrySetResult();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        await using var _ = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            await tcs.Task;
            Util.Logger.Log("ICE 収集完了（Vanilla ICE 準備完了）");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Util.Logger.Log("ICE 収集タイムアウト（10秒）: 収集済みの候補で続行", Util.LogLevel.Warning);
        }
    }

    /// <summary>
    /// gathering 中に収集した ICE 候補のうち、SDP に未掲載のものを追加する。
    /// SIPSorcery の localDescription は setLocalDescription 時点の SDP しか保持しないため、
    /// srflx/relay 候補を手動で SDP に追加して Vanilla ICE を実現する。
    /// </summary>
    private string AppendGatheredCandidatesToSdp(string originalSdp)
    {
        // SDP 内の既存候補を収集（重複防止）
        var existingCandidates = new HashSet<string>();
        foreach (var line in originalSdp.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("a=candidate:"))
            {
                existingCandidates.Add(trimmed);
            }
        }

        // 未掲載の候補を a=candidate: 行として追加
        var newCandidateLines = new List<string>();
        foreach (var candidate in _gatheredCandidates)
        {
            // SIPSorcery の RTCIceCandidate.ToString() は "foundation component ..." 形式（candidate: プレフィックスなし）
            // SDP の候補行は "a=candidate:foundation component ..." 形式が必要
            var candidateStr = candidate.ToString();
            var candidateLine = candidateStr.StartsWith("candidate:")
                ? $"a={candidateStr}"
                : $"a=candidate:{candidateStr}";

            if (!existingCandidates.Contains(candidateLine))
            {
                newCandidateLines.Add(candidateLine);
                Util.Logger.Log($"SDP 候補追加: {candidateLine}");
            }
        }

        if (newCandidateLines.Count == 0)
        {
            Util.Logger.Log("SDP 候補追加: 追加なし（全候補が既に SDP に含まれている）");
            return originalSdp;
        }

        // SDP の改行コードを検出（SIPSorcery は \r\n を使用）
        var lineEnding = originalSdp.Contains("\r\n") ? "\r\n" : "\n";

        // SDP の最後の m= セクション内に候補行を挿入
        var lines = originalSdp.TrimEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

        // 挿入位置: 最後の a= 行の直後
        var insertIndex = lines.Count;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("a="))
            {
                insertIndex = i + 1;
                break;
            }
        }

        foreach (var line in newCandidateLines)
        {
            lines.Insert(insertIndex, line);
            insertIndex++;
        }

        var lineEndingName = lineEnding == "\r\n" ? "CRLF" : "LF";
        Util.Logger.Log($"SDP 候補追加完了: {newCandidateLines.Count} 件追加 (改行={lineEndingName})");
        return string.Join(lineEnding, lines) + lineEnding;
    }

    /// <summary>
    /// SDP に含まれる ICE 候補行をログ出力する（診断用）。
    /// </summary>
    private static void LogSdpCandidates(string sdp, string label)
    {
        var lines = sdp.Split('\n');
        var candidateCount = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("a=candidate:"))
            {
                candidateCount++;
                Util.Logger.Log($"SDP [{label}] 候補 {candidateCount}: {trimmed}");
            }
            else if (trimmed.StartsWith("a=ice-ufrag:") || trimmed.StartsWith("a=ice-pwd:"))
            {
                Util.Logger.Log($"SDP [{label}] {trimmed}");
            }
        }
        Util.Logger.Log($"SDP [{label}] 候補数合計: {candidateCount}");
    }

    /// <summary>
    /// 接続経路を判定する。
    /// WebRTC は host（LAN 直接）→ srflx（STUN P2P）→ relay（TURN リレー）の優先順で接続するため、
    /// ローカルで生成された ICE 候補タイプから経路を推定する。
    /// </summary>
    private ConnectionRoute DetermineConnectionRoute()
    {
        // relay 候補しか成功しなかった場合は TURN リレー
        // host 候補が存在する場合、WebRTC は最優先で使うため LAN 直接の可能性が高い
        // srflx のみの場合は STUN による NAT 越え
        if (!_hasHostCandidate && !_hasSrflxCandidate && _hasRelayCandidate)
            return ConnectionRoute.Relay;

        if (_hasHostCandidate)
            return ConnectionRoute.Direct;

        if (_hasSrflxCandidate)
            return ConnectionRoute.StunAssisted;

        if (_hasRelayCandidate)
            return ConnectionRoute.Relay;

        return ConnectionRoute.Unknown;
    }

    /// <summary>
    /// 接続を閉じる。
    /// </summary>
    public void Close()
    {
        IsConnected = false;
        Route = ConnectionRoute.Unknown;
        _hasHostCandidate = false;
        _hasSrflxCandidate = false;
        _hasRelayCandidate = false;
        _gatheredCandidates.Clear();
        _dataChannel = null;

        if (_pc != null)
        {
            _pc.Close("ユーザーによる切断");
            _pc.Dispose();
            _pc = null;
        }
    }

    public void Dispose()
    {
        Close();
    }

    /// <summary>
    /// SDP 文字列から ICE 候補行を抽出し、addIceCandidate で明示的に追加する。
    /// SIPSorcery の setRemoteDescription が SDP 内候補を正しくパースしない場合の保険。
    /// </summary>
    private void AddCandidatesFromSdp(string sdp)
    {
        if (_pc == null) return;

        var count = 0;
        foreach (var line in sdp.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("a=candidate:")) continue;

            // "a=candidate:" プレフィックスを除去して candidate 文字列のみにする
            var candidateValue = trimmed["a=".Length..];
            var init = new RTCIceCandidateInit
            {
                candidate = candidateValue,
                sdpMLineIndex = 0,
                sdpMid = "0",
            };
            _pc.addIceCandidate(init);
            count++;
        }
        Util.Logger.Log($"リモート SDP から ICE 候補を明示追加: {count} 件");
    }
}

/// <summary>
/// SIPSorcery 内部ログを Ferry のログシステムに転送する ILoggerFactory 実装。
/// パッケージ追加なしで動作する。ICE 接続チェックのデバッグに使用。
/// </summary>
internal sealed class SIPSorceryLogFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new SIPSorceryLogger(categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

internal sealed class SIPSorceryLogger(string category) : ILogger
{
    // ICE 関連のログのみ出力（他は大量すぎるのでフィルタ）
    private static readonly Regex IceLogPattern = new(
        @"ICE|candidate|STUN|TURN|connectivity|check|nominate|relay|binding",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (IceLogPattern.IsMatch(message))
        {
            var shortCategory = category.Length > 30 ? category[^30..] : category;
            Util.Logger.Log($"[SIPSorcery:{shortCategory}] {message}");
        }
    }
}
