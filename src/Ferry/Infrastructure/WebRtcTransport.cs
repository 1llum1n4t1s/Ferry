using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using SIPSorcery.Net;

namespace Ferry.Infrastructure;

/// <summary>
/// SIPSorcery を使用した WebRTC DataChannel トランスポート。
/// ICE/DTLS/SCTP のネゴシエーションと DataChannel の作成・管理を行う。
/// </summary>
public sealed class WebRtcTransport : IDisposable
{
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

            IceCandidateGenerated?.Invoke(this, candidate);
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
    /// </summary>
    /// <returns>SDP Offer 文字列。</returns>
    public async Task<string> CreateOfferAsync(CancellationToken ct = default)
    {
        var pc = InitPeerConnection();

        // DataChannel を作成（Offer 側）
        var dc = await pc.createDataChannel(DataChannelLabel, null);
        AttachDataChannelEvents(dc);
        _dataChannel = dc;

        // SDP Offer 生成
        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);

        Util.Logger.Log("SDP Offer 生成完了");
        return offer.sdp;
    }

    /// <summary>
    /// リモートの SDP Offer を受け取り、SDP Answer を生成する（着信側）。
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

        var result = pc.setRemoteDescription(remoteOffer);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"リモート SDP の設定に失敗: {result}");
        }

        // SDP Answer 生成
        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);

        Util.Logger.Log("SDP Answer 生成完了");
        return answer.sdp;
    }

    /// <summary>
    /// リモートの SDP Answer を適用する（発信側が使用）。
    /// </summary>
    public Task SetRemoteAnswerAsync(string remoteSdp, CancellationToken ct = default)
    {
        if (_pc == null)
            throw new InvalidOperationException("PeerConnection が初期化されていません");

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
        return Task.CompletedTask;
    }

    /// <summary>
    /// リモートの ICE Candidate を追加する。
    /// </summary>
    public Task AddIceCandidateAsync(string candidate, CancellationToken ct = default)
    {
        if (_pc == null)
            throw new InvalidOperationException("PeerConnection が初期化されていません");

        _pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate });
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
}
