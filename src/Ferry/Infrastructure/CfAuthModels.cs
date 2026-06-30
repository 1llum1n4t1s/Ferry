using System;
using System.Text.Json.Serialization;

namespace Ferry.Infrastructure;

// CF 単独完結移行 Step 6: Workers /auth/token の認証 DTO を FirebaseAuthClient.cs から分離。
// CfTokenProvider が使用する経路非依存の型。Firebase ログイン leg（signInWithCustomToken 系 DTO）は
// FirebaseAuthClient ごと撤去済で、ここには CF 認証で実際に使う型のみを残す。

/// <summary>/auth/token 401 DEVICE_PUBKEY_MISMATCH をクライアント側で識別するための例外。
/// CfTokenProvider が throw し、ConnectionService / PairSyncService / App.axaml.cs が catch する。</summary>
public sealed class IdentityLostException : Exception
{
    public IdentityLostException(string message) : base(message) { }
}

/// <summary>/auth/token への署名付きリクエスト（deviceId + 公開鍵 + ts + ECDSA 署名）。</summary>
public sealed class AuthTokenRequest
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("pubKeySpki")] public string PubKeySpki { get; set; } = string.Empty;
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("sig")] public string Sig { get; set; } = string.Empty;
}

/// <summary>/auth/token のレスポンス。CF 単独完結では cfToken（自前 HMAC bearer）を使う。</summary>
public sealed class AuthTokenResponse
{
    [JsonPropertyName("customToken")] public string CustomToken { get; set; } = string.Empty;
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; set; }

    /// <summary>CF 用 HMAC bearer。CloudflareSignaling の Bearer 注入に使う。</summary>
    [JsonPropertyName("cfToken")] public string? CfToken { get; set; }
}

/// <summary>/auth/token エラーレスポンス（DEVICE_PUBKEY_MISMATCH の識別等）。</summary>
public sealed class AuthErrorResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("serverTime")] public long? ServerTime { get; set; }
}

[JsonSerializable(typeof(AuthTokenRequest))]
[JsonSerializable(typeof(AuthTokenResponse))]
[JsonSerializable(typeof(AuthErrorResponse))]
internal partial class CfAuthJsonContext : JsonSerializerContext { }
