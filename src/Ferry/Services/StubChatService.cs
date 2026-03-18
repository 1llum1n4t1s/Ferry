using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// チャットサービスのスタブ実装（デザイン/テスト用）。
/// </summary>
public sealed class StubChatService : IChatService
{
#pragma warning disable CS0067 // イベントは未使用だがインターフェース実装に必要
    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<Guid>? MessageDelivered;
    public event EventHandler<Guid>? MessageDeleted;
    public event EventHandler<(Guid MessageId, string NewText)>? MessageEdited;
    public event EventHandler<(Guid MessageId, string Emoji, string SenderName)>? ReactionReceived;
#pragma warning restore CS0067

    public Task SendMessageAsync(string peerId, string text, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendDeleteMessageAsync(string peerId, Guid messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendEditMessageAsync(string peerId, Guid messageId, string newText, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendReactionAsync(string peerId, Guid messageId, string emoji, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendReplyMessageAsync(string peerId, string text, Guid replyToMessageId, string replyToText, CancellationToken ct = default) => Task.CompletedTask;
    public void HandleReceivedData(byte[] data) { }
    public Task<List<ChatMessage>> LoadHistoryAsync(string peerId) => Task.FromResult(new List<ChatMessage>());
    public Task AppendMessageAsync(ChatMessage message) => Task.CompletedTask;
    public Task FlushOfflineQueueAsync(CancellationToken ct = default) => Task.CompletedTask;
}
