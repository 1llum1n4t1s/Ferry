using Ferry.Services;

namespace Ferry.Infrastructure;

/// <summary>
/// rere #B1-001: Firebase 実装の presence サービス（<see cref="FirebaseSignaling"/>）を生成するファクトリ。
/// データベース URL は構築時に固定し、生成のたびに新しい接続を返す。
/// </summary>
public sealed class FirebasePresenceServiceFactory : IPresenceServiceFactory
{
    private readonly string _databaseUrl;

    public FirebasePresenceServiceFactory(string databaseUrl) => _databaseUrl = databaseUrl;

    public IPresenceService Create() => new FirebaseSignaling(_databaseUrl);
}
