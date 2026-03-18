using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// チャットメッセージの送受信・履歴永続化を行う。
/// 履歴は AES-256-GCM で暗号化してローカルファイルに保存する。
/// </summary>
public sealed class ChatService : IChatService
{
    private readonly IConnectionService _connectionService;
    private readonly ISettingsService _settingsService;

    /// <summary>ピアごとの履歴キャッシュ。</summary>
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _historyCache = new();

    /// <summary>永続化用ロック。</summary>
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>暗号化キー導出用のソルト。</summary>
    private static readonly byte[] EncryptionSalt =
        "Ferry-ChatHistory-2026"u8.ToArray();

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<Guid>? MessageDelivered;

    public ChatService(IConnectionService connectionService, ISettingsService settingsService)
    {
        _connectionService = connectionService;
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(string peerId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var message = new ChatMessage
        {
            PeerId = peerId,
            SenderDeviceId = _settingsService.Settings.DeviceId,
            Type = ChatMessageType.Text,
            Text = text,
            IsFromMe = true,
            State = ChatMessageState.Sending,
        };

        // プロトコル: [0x30] [MessageId 16byte] [UTF-8 テキスト]
        var textBytes = Encoding.UTF8.GetBytes(text);
        var payload = new byte[1 + 16 + textBytes.Length];
        payload[0] = TransferProtocol.ChatMessage;
        message.MessageId.TryWriteBytes(payload.AsSpan(1));
        textBytes.CopyTo(payload, 17);

        try
        {
            await _connectionService.SendAsync(payload);
            message.State = ChatMessageState.Sent;
        }
        catch
        {
            message.State = ChatMessageState.Failed;
        }

        await AppendMessageAsync(message);
    }

    /// <inheritdoc />
    public void HandleReceivedData(byte[] data)
    {
        if (data.Length < 1) return;

        switch (data[0])
        {
            case TransferProtocol.ChatMessage when data.Length >= 17:
                HandleChatMessage(data);
                break;
            case TransferProtocol.ChatAck when data.Length >= 17:
                HandleChatAck(data);
                break;
        }
    }

    private void HandleChatMessage(byte[] data)
    {
        var messageId = new Guid(data.AsSpan(1, 16));
        var text = Encoding.UTF8.GetString(data, 17, data.Length - 17);

        // 配達確認を送信（ACK）
        var ack = new byte[17];
        ack[0] = TransferProtocol.ChatAck;
        messageId.TryWriteBytes(ack.AsSpan(1));
        _ = _connectionService.SendAsync(ack);

        // 送信者のピアIDを特定（接続中のピアから取得）
        var peerId = _connectionService.ConnectedPeer?.SessionId ?? string.Empty;

        var message = new ChatMessage
        {
            MessageId = messageId,
            PeerId = peerId,
            SenderDeviceId = string.Empty,
            Type = ChatMessageType.Text,
            Text = text,
            IsFromMe = false,
            State = ChatMessageState.Delivered,
        };

        _ = AppendMessageAsync(message);
        MessageReceived?.Invoke(this, message);
    }

    private void HandleChatAck(byte[] data)
    {
        var messageId = new Guid(data.AsSpan(1, 16));
        MessageDelivered?.Invoke(this, messageId);
    }

    /// <inheritdoc />
    public async Task<List<ChatMessage>> LoadHistoryAsync(string peerId)
    {
        if (_historyCache.TryGetValue(peerId, out var cached))
            return FilterByRetention(cached);

        var filePath = GetHistoryFilePath(peerId);

        // 旧 JSON ファイルからの移行（暗号化前のファイルがあれば読み込んで暗号化保存）
        var legacyPath = GetLegacyHistoryFilePath(peerId);
        if (!File.Exists(filePath) && File.Exists(legacyPath))
        {
            try
            {
                var legacyJson = await File.ReadAllTextAsync(legacyPath);
                var legacyMessages = JsonSerializer.Deserialize(legacyJson, ChatMessageJsonContext.Default.ListChatMessage)
                                     ?? new List<ChatMessage>();
                _historyCache[peerId] = legacyMessages;
                await SaveHistoryAsync(peerId, legacyMessages);
                File.Delete(legacyPath); // 移行完了後に旧ファイル削除
                return FilterByRetention(legacyMessages);
            }
            catch
            {
                // 移行失敗 — 空から開始
            }
        }

        if (!File.Exists(filePath))
        {
            var empty = new List<ChatMessage>();
            _historyCache[peerId] = empty;
            return empty;
        }

        try
        {
            var encryptedData = await File.ReadAllBytesAsync(filePath);
            var json = Decrypt(encryptedData);
            var messages = JsonSerializer.Deserialize(json, ChatMessageJsonContext.Default.ListChatMessage)
                           ?? new List<ChatMessage>();
            _historyCache[peerId] = messages;
            return FilterByRetention(messages);
        }
        catch
        {
            var empty = new List<ChatMessage>();
            _historyCache[peerId] = empty;
            return empty;
        }
    }

    /// <inheritdoc />
    public async Task AppendMessageAsync(ChatMessage message)
    {
        var history = await LoadHistoryAsync(message.PeerId);
        history.Add(message);

        // 保持期間外の古いメッセージを削除
        PurgeExpiredMessages(history);

        await SaveHistoryAsync(message.PeerId, history);
    }

    private async Task SaveHistoryAsync(string peerId, List<ChatMessage> messages)
    {
        await _saveLock.WaitAsync();
        try
        {
            var filePath = GetHistoryFilePath(peerId);
            var dir = Path.GetDirectoryName(filePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(messages, ChatMessageJsonContext.Default.ListChatMessage);
            var encryptedData = Encrypt(json);
            await File.WriteAllBytesAsync(filePath, encryptedData);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // === 暗号化 ===

    /// <summary>デバイスIDから暗号化キーを導出する。</summary>
    private byte[] DeriveKey()
    {
        var deviceId = _settingsService.Settings.DeviceId;
        var keyMaterial = Encoding.UTF8.GetBytes(deviceId);
        return Rfc2898DeriveBytes.Pbkdf2(keyMaterial, EncryptionSalt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>AES-256-GCM で暗号化する。</summary>
    private byte[] Encrypt(string plainText)
    {
        var key = DeriveKey();
        var nonce = new byte[12]; // AesGcm の nonce は 12 バイト
        RandomNumberGenerator.Fill(nonce);

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16]; // 認証タグ

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // 出力フォーマット: [nonce 12][tag 16][ciphertext ...]
        var result = new byte[12 + 16 + cipherBytes.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, 12);
        cipherBytes.CopyTo(result, 28);
        return result;
    }

    /// <summary>AES-256-GCM で復号する。</summary>
    private string Decrypt(byte[] encryptedData)
    {
        if (encryptedData.Length < 28)
            throw new CryptographicException("暗号化データが短すぎます");

        var key = DeriveKey();
        var nonce = encryptedData.AsSpan(0, 12);
        var tag = encryptedData.AsSpan(12, 16);
        var cipherText = encryptedData.AsSpan(28);
        var plainBytes = new byte[cipherText.Length];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipherText, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    // === 保持期間管理 ===

    /// <summary>保持期間を超えたメッセージを削除する。</summary>
    private void PurgeExpiredMessages(List<ChatMessage> messages)
    {
        var retentionDays = _settingsService.Settings.ChatHistoryRetentionDays;
        if (retentionDays <= 0) return; // 0以下は無期限

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        messages.RemoveAll(m => m.SentAt < cutoff);
    }

    /// <summary>保持期間でフィルタしたリストを返す（元リストは変更しない）。</summary>
    private List<ChatMessage> FilterByRetention(List<ChatMessage> messages)
    {
        var retentionDays = _settingsService.Settings.ChatHistoryRetentionDays;
        if (retentionDays <= 0) return messages; // 0以下は無期限

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        return messages.Where(m => m.SentAt >= cutoff).ToList();
    }

    // === ファイルパス ===

    private static string GetHistoryFilePath(string peerId)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Ferry", "chat", $"{peerId}.enc");
    }

    private static string GetLegacyHistoryFilePath(string peerId)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Ferry", "chat", $"{peerId}.json");
    }
}
