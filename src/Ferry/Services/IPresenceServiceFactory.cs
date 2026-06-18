namespace Ferry.Services;

/// <summary>
/// プレゼンスサービスの生成ファクトリ。
/// rere #B1-001: ConnectionViewModel は StartPresenceMonitoring のたびに新しい presence 接続を
/// 生成する（再購読のため）。VM が Infrastructure を直接 new せずに済むよう、生成をこのファクトリへ委譲する。
/// </summary>
public interface IPresenceServiceFactory
{
    /// <summary>新しい presence サービスインスタンスを生成する。呼び出し側が Dispose する責務を持つ。</summary>
    IPresenceService Create();
}
