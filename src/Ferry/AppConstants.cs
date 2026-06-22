namespace Ferry;

/// <summary>
/// アプリ全体で共有する接続先 URL の定数集約。すべて settings.json から書き換え不可
/// （改ざん面削減。App.axaml.cs の UpdateBaseUrl と同方針）。Firebase / Bridge / Relay の
/// プロジェクト移行時はここを 1 箇所書き換えて再リリースする。
///
/// rere #D-004: FirebaseDatabaseUrl / BridgePageUrl は旧来 settings.json で上書き可能で、
/// const の RelayUrl と非対称だった（settings.json を改竄すると接続先 Firebase や
/// ペアリング QR の宛先を攻撃者サーバへ向けられた）。3 URL を const に一本化して対称化する。
/// </summary>
public static class AppConstants
{
    /// <summary>Firebase Realtime DB の URL（シグナリング / プレゼンス用）。</summary>
    public const string FirebaseDatabaseUrl = "https://ferry-edf09-default-rtdb.firebaseio.com";

    /// <summary>Bridge ページ（スマホでQRスキャンして2台をペアリング）の URL。</summary>
    public const string BridgePageUrl = "https://ferry-edf09.web.app";

    /// <summary>WebSocket リレーサーバーの URL（NAT 越え用、Cloudflare Workers + Durable Objects 経由）。</summary>
    public const string RelayUrl = "wss://relay.ferry.nephilim.jp/ferry-relay";

    // === #D-001a Phase B: Firebase Custom Token Auth ===
    /// <summary>Workers の PC 用 Custom Token 発行エンドポイント。</summary>
    public const string WorkersAuthTokenUrl = "https://relay.ferry.nephilim.jp/auth/token";

    /// <summary>Workers の Bridge 用 short-lived Custom Token 発行エンドポイント（Bridge から叩く）。</summary>
    public const string WorkersPairTokenUrl = "https://relay.ferry.nephilim.jp/pair/token";

    /// <summary>
    /// Firebase Identity Toolkit の Web API Key（公開鍵相当・bridge.js の firebaseConfig.apiKey と同じ）。
    /// Custom Token を idToken に交換する signInWithCustomToken エンドポイントで使用。
    /// </summary>
    public const string FirebaseWebApiKey = "AIzaSyCOPRMYBv4keAHBjvFm4lgdfMoVva6rxTE";

    // === CF 単独完結移行 (docs/design/cf-only-migration.md) ===
    /// <summary>CF 単独完結の Worker API ベース URL（signaling/presence/pairs/pair）。</summary>
    public const string CfApiBaseUrl = "https://relay.ferry.nephilim.jp";

    /// <summary>CF pairing inbox の WebSocket URL（成立通知の真 push 経路）。</summary>
    public const string CfInboxWsUrl = "wss://relay.ferry.nephilim.jp/inbox";

    /// <summary>CF 単独完結の Bridge QR ページ URL（relay Worker の Static Assets で配信）。
    /// API（/pair/create）と同一オリジンなので Bridge → server 呼出は CORS 不要。
    /// UseCloudflareSignaling 時のみ QR の宛先をこちらに向ける（Firebase 版 BridgePageUrl と dual-path 並存）。</summary>
    public const string CfBridgePageUrl = "https://relay.ferry.nephilim.jp";
}
