namespace Ferry.Infrastructure;

/// <summary>
/// rere #D-003: signaling/{pairId} 配下の per-sender ノードのキーパスを構築する純関数群。
///
/// 旧実装は signaling/{pairId}/offer・/answer・/{role}Endpoint の単一ノードを双方が共有していたため、
/// 2 台が同時に ConnectToPeerAsync すると offer が後勝ち上書きで消え、両者が相手の answer を待ち続けて
/// デッドロック (失敗/不要リレー) になる構造だった。送信元 deviceId でキー化すると双方が別キーに書くので
/// 相互上書きが構造的に起きなくなる。読み手は常に「ペア相手 (peerId) のキー」をピンポイントで読む。
///
/// これは probe が既に採用済みの per-nonce key (probeOffers/{nonce}) と同じ分離パターンの横展開。
///
/// 書き手は自分の deviceId キー、読み手はペア相手の deviceId キーを使う規約。呼び出し側 (ConnectionService)
/// が _deviceId / peerId のどちらを渡すかで誤らないよう、各メソッドの引数名で意図を明示する。
/// AOT 安全 (文字列結合のみ・リフレクション無し)。
/// </summary>
public static class SignalingPaths
{
    /// <summary>offer コレクションのノード名。各エントリは送信元 deviceId でキー化される。</summary>
    public const string OffersNode = "offers";

    /// <summary>answer コレクションのノード名。各エントリは answerer の deviceId でキー化される。</summary>
    public const string AnswersNode = "answers";

    /// <summary>UDP 外部エンドポイントコレクションのノード名。各エントリは送信元 deviceId でキー化される。</summary>
    public const string EndpointsNode = "endpoints";

    /// <summary>signaling/{pairId}/offers/{senderDeviceId}。テスト用のフルパス表現
    /// (FirebaseSignaling は .Child(pairId).Child(OffersNode).Child(senderDeviceId) で同じノードを指す)。</summary>
    public static string OfferPath(string pairId, string senderDeviceId)
        => $"signaling/{pairId}/{OffersNode}/{senderDeviceId}";

    /// <summary>signaling/{pairId}/answers/{answererDeviceId}。</summary>
    public static string AnswerPath(string pairId, string answererDeviceId)
        => $"signaling/{pairId}/{AnswersNode}/{answererDeviceId}";

    /// <summary>signaling/{pairId}/endpoints/{senderDeviceId}。</summary>
    public static string EndpointPath(string pairId, string senderDeviceId)
        => $"signaling/{pairId}/{EndpointsNode}/{senderDeviceId}";
}
