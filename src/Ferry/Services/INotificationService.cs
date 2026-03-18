namespace Ferry.Services;

/// <summary>
/// 通知関連のサービスインターフェース（タスクバー点滅・受信音再生）。
/// </summary>
public interface INotificationService
{
    /// <summary>メッセージ受信通知を発行する。</summary>
    /// <param name="peerId">送信元のピアID</param>
    /// <param name="senderName">送信者の表示名</param>
    /// <param name="preview">メッセージのプレビューテキスト</param>
    void NotifyMessageReceived(string peerId, string senderName, string preview);
}
