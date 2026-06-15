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
}
