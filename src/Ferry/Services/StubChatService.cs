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
#pragma warning restore CS0067

    public Task SendMessageAsync(string peerId, string text, CancellationToken ct = default) => Task.CompletedTask;
    public void HandleReceivedData(byte[] data) { }
    public Task<List<ChatMessage>> LoadHistoryAsync(string peerId) => Task.FromResult(new List<ChatMessage>());
    public Task AppendMessageAsync(ChatMessage message) => Task.CompletedTask;
}
