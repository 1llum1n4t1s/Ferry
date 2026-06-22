using System.Net.Http;
using Ferry.Services;

namespace Ferry.Infrastructure;

/// <summary>
/// CF 単独完結移行: Cloudflare 実装の presence サービス（<see cref="CloudflareSignaling"/>）を生成するファクトリ。
/// <see cref="FirebasePresenceServiceFactory"/> の CF 版。共有 HttpClient と CfTokenProvider を構築時に固定する。
/// </summary>
public sealed class CloudflarePresenceServiceFactory : IPresenceServiceFactory
{
    private readonly CfTokenProvider _tokens;
    private readonly string _deviceId;
    private readonly HttpClient? _http;

    public CloudflarePresenceServiceFactory(CfTokenProvider tokens, string deviceId, HttpClient? http = null)
    {
        _tokens = tokens;
        _deviceId = deviceId;
        _http = http;
    }

    public IPresenceService Create() => new CloudflareSignaling(_tokens, _deviceId, _http);
}
