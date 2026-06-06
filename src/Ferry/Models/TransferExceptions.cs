using System.IO;

namespace Ferry.Models;

/// <summary>
/// 送信中（承認待ち中・チャンク送信中）に接続断が起きて転送が中断されたことを示す。
/// <see cref="IOException"/> 派生にすることで、TransferViewModel.SendItemAsync の
/// <c>catch (Exception ex) when (attempt &lt; MaxSendAttempts &amp;&amp; ...)</c> ブランチが
/// 自動的に拾い、transient エラーとして MaxSendAttempts までリトライに乗せる。
///
/// 重要: <see cref="System.OperationCanceledException"/> ではないため、ユーザーキャンセル /
/// 受信側拒否 / 承認タイムアウトの「no-retry catch」と型で明確に区別される。
/// 旧実装は OnConnectionLost が承認待ち TCS を TrySetResult(false) で「拒否扱い」にしていて
/// VM 側で transient retry に乗らずに 1 回で Cancelled 終了していた。
/// </summary>
public sealed class ConnectionLostDuringTransferException : IOException
{
    public ConnectionLostDuringTransferException(string message) : base(message) { }
}
