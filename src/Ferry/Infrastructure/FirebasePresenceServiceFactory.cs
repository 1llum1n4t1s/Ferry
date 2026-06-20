using Ferry.Services;

namespace Ferry.Infrastructure;

/// <summary>
/// rere #B1-001: Firebase 実装の presence サービス（<see cref="FirebaseSignaling"/>）を生成するファクトリ。
/// データベース URL は構築時に固定し、生成のたびに新しい接続を返す。
/// </summary>
public sealed class FirebasePresenceServiceFactory : IPresenceServiceFactory
{
    private readonly string _databaseUrl;
    private readonly FirebaseAuthClient? _authClient;

    public FirebasePresenceServiceFactory(string databaseUrl, FirebaseAuthClient? authClient = null)
    {
        _databaseUrl = databaseUrl;
        _authClient = authClient;
    }

    public IPresenceService Create() => new FirebaseSignaling(_databaseUrl, _authClient);
}
