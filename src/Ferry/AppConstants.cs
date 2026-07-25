namespace Ferry;

/// <summary>
/// アプリ全体で共有する接続先 URL の定数集約。すべて settings.json から書き換え不可
/// （改ざん面削減。App.axaml.cs の UpdateBaseUrl と同方針）。接続先の
/// プロジェクト移行時はここを 1 箇所書き換えて再リリースする。
///
/// rere #D-004: 各 URL を const に一本化して RelayUrl と対称化（settings.json 改竄で
/// 接続先を攻撃者サーバへ向けられないようにする）。CF 単独完結 Step 6 で Firebase 系定数は撤去済み。
/// </summary>
public static class AppConstants
{
    /// <summary>WebSocket リレーサーバーの URL（NAT 越え用、Cloudflare Workers + Durable Objects 経由）。</summary>
    public const string RelayUrl = "wss://watashiba.kagayoi.com/ferry-relay";

    // === 認証トークン発行エンドポイント（relay Worker の自前 HMAC bearer） ===
    /// <summary>Workers の PC 用トークン発行エンドポイント（CfTokenProvider が cfToken を取得）。</summary>
    public const string WorkersAuthTokenUrl = "https://watashiba.kagayoi.com/auth/token";

    // === CF 単独完結 (docs/design/cf-only-migration.md) ===
    /// <summary>CF 単独完結の Worker API ベース URL（signaling/presence/pairs/pair）。</summary>
    public const string CfApiBaseUrl = "https://watashiba.kagayoi.com";

    /// <summary>CF pairing inbox の WebSocket URL（成立通知の真 push 経路）。</summary>
    public const string CfInboxWsUrl = "wss://watashiba.kagayoi.com/inbox";

    /// <summary>Bridge QR ページ URL（relay Worker の Static Assets で配信）。
    /// API（/pair/create）と同一オリジンなので Bridge → server 呼出は CORS 不要。</summary>
    public const string CfBridgePageUrl = "https://watashiba.kagayoi.com";
}
