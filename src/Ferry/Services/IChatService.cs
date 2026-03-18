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

    /// <summary>メッセージ削除を送信する。</summary>
    Task SendDeleteMessageAsync(string peerId, Guid messageId, CancellationToken ct = default);

    /// <summary>メッセージ編集を送信する。</summary>
    Task SendEditMessageAsync(string peerId, Guid messageId, string newText, CancellationToken ct = default);

    /// <summary>リアクションを送信する。</summary>
    Task SendReactionAsync(string peerId, Guid messageId, string emoji, CancellationToken ct = default);

    /// <summary>リプライ付きメッセージを送信する。</summary>
    Task SendReplyMessageAsync(string peerId, string text, Guid replyToMessageId, string replyToText, CancellationToken ct = default);

    /// <summary>受信データを処理する（先頭バイトが 0x30-0x35 の場合）。</summary>
    void HandleReceivedData(byte[] data);

    /// <summary>指定ピアとの会話履歴を読み込む。</summary>
    Task<List<ChatMessage>> LoadHistoryAsync(string peerId);

    /// <summary>メッセージを履歴に追加して永続化する。</summary>
    Task AppendMessageAsync(ChatMessage message);

    /// <summary>オフラインキューに溜まったメッセージを送信する。</summary>
    Task FlushOfflineQueueAsync(CancellationToken ct = default);

    /// <summary>メッセージ受信イベント。</summary>
    event EventHandler<ChatMessage>? MessageReceived;

    /// <summary>メッセージ配達確認イベント。</summary>
    event EventHandler<Guid>? MessageDelivered;

    /// <summary>メッセージ削除イベント。</summary>
    event EventHandler<Guid>? MessageDeleted;

    /// <summary>メッセージ編集イベント。</summary>
    event EventHandler<(Guid MessageId, string NewText)>? MessageEdited;

    /// <summary>リアクション受信イベント。</summary>
    event EventHandler<(Guid MessageId, string Emoji, string SenderName)>? ReactionReceived;
}
