using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// チャットメッセージの送受信・履歴管理を行うサービス。
/// </summary>
public interface IChatService
{
    /// <summary>テキストメッセージを送信する。</summary>
    Task SendMessageAsync(string peerId, string text, CancellationToken ct = default);

    /// <summary>受信データを処理する（先頭バイトが 0x30-0x31 の場合）。</summary>
    void HandleReceivedData(byte[] data);

    /// <summary>指定ピアとの会話履歴を読み込む。</summary>
    Task<List<ChatMessage>> LoadHistoryAsync(string peerId);

    /// <summary>メッセージを履歴に追加して永続化する。</summary>
    Task AppendMessageAsync(ChatMessage message);

    /// <summary>メッセージ受信イベント。</summary>
    event EventHandler<ChatMessage>? MessageReceived;

    /// <summary>メッセージ配達確認イベント。</summary>
    event EventHandler<Guid>? MessageDelivered;
}
