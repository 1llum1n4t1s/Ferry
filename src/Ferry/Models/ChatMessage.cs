using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ferry.Models;

/// <summary>チャットメッセージ。</summary>
public sealed partial class ChatMessage : ObservableObject
{
    /// <summary>メッセージの一意識別子。</summary>
    public Guid MessageId { get; init; } = Guid.NewGuid();

    /// <summary>会話相手のピアID。</summary>
    public required string PeerId { get; init; }

    /// <summary>送信者のデバイスID。</summary>
    public required string SenderDeviceId { get; init; }

    /// <summary>メッセージ種別。</summary>
    public required ChatMessageType Type { get; init; }

    /// <summary>メッセージ本文（Type=Text の場合）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>ファイル名（Type=File の場合）。</summary>
    public string? FileName { get; set; }

    /// <summary>ファイルサイズ（Type=File の場合）。</summary>
    public long? FileSize { get; set; }

    /// <summary>ファイル転送の TransferId（Type=File の場合）。</summary>
    public Guid? TransferId { get; set; }

    /// <summary>ファイルの保存先パス（受信完了時）またはソースファイルパス（送信時）。</summary>
    public string? FilePath { get; set; }

    /// <summary>送信日時 (UTC)。</summary>
    public DateTime SentAt { get; init; } = DateTime.UtcNow;

    /// <summary>メッセージの状態。</summary>
    [ObservableProperty]
    private ChatMessageState _state = ChatMessageState.Sending;

    /// <summary>自分が送信したメッセージかどうか。</summary>
    public bool IsFromMe { get; init; }

    /// <summary>リプライ先メッセージID（引用返信時）。</summary>
    public Guid? ReplyToMessageId { get; set; }

    /// <summary>リプライ先のテキストプレビュー（表示用）。</summary>
    public string? ReplyToText { get; set; }

    /// <summary>リプライ先の送信者名（表示用）。</summary>
    public string? ReplyToSenderName { get; set; }

    /// <summary>リアクション一覧。キーは絵文字、値は送信者リスト。</summary>
    [JsonIgnore]
    public Dictionary<string, List<string>> Reactions { get; set; } = [];

    /// <summary>編集済みかどうか。</summary>
    public bool IsEdited { get; set; }

    /// <summary>削除済みかどうか。</summary>
    public bool IsDeleted { get; set; }

    /// <summary>削除済みメッセージの表示テキスト。</summary>
    [JsonIgnore]
    public string DeletedText => App.Text("Chat.DeletedMessage");

    /// <summary>転送進捗率 (0.0〜1.0)。Type=File のとき使用。</summary>
    [ObservableProperty]
    private double _fileProgress;

    partial void OnStateChanged(ChatMessageState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsWaitingApproval));
        OnPropertyChanged(nameof(IsTransferring));
    }

    partial void OnFileProgressChanged(double value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ProgressBarWidth));
    }

    /// <summary>承認待ちかどうか（Type=File のとき使用）。</summary>
    [JsonIgnore]
    public bool IsWaitingApproval => Type == ChatMessageType.File && State == ChatMessageState.WaitingApproval;

    /// <summary>状態の表示テキスト。</summary>
    [JsonIgnore]
    public string StateText => State switch
    {
        ChatMessageState.Sending => App.Text("State.Pending"),
        ChatMessageState.Sent => App.Text("State.Sending"),
        ChatMessageState.Delivered => "✓✓",
        ChatMessageState.Failed => App.Text("State.Error"),
        ChatMessageState.WaitingApproval => App.Text("State.WaitingApproval"),
        ChatMessageState.Transferring => $"{App.Text("State.Sending")} {FileProgress * 100:F0}%",
        ChatMessageState.Completed => App.Text("State.Completed"),
        _ => "",
    };

    /// <summary>送信日時の表示テキスト。</summary>
    [JsonIgnore]
    public string SentAtText => SentAt.ToLocalTime().ToString("HH:mm");

    /// <summary>ファイルサイズの表示テキスト。</summary>
    [JsonIgnore]
    public string FileSizeText => FileSize.HasValue ? FormatBytes(FileSize.Value) : string.Empty;

    // --- XAML バインディング用ヘルパープロパティ ---

    /// <summary>テキストメッセージかどうか（XAML の IsVisible 用）。</summary>
    [JsonIgnore]
    public bool IsTextMessage => Type == ChatMessageType.Text;

    /// <summary>ファイルメッセージかどうか（XAML の IsVisible 用）。</summary>
    [JsonIgnore]
    public bool IsFileMessage => Type == ChatMessageType.File;

    /// <summary>システムメッセージかどうか（XAML の IsVisible 用）。</summary>
    [JsonIgnore]
    public bool IsSystemMessage => Type == ChatMessageType.System;

    /// <summary>転送中かどうか（プログレスバー表示用）。</summary>
    [JsonIgnore]
    public bool IsTransferring => State == ChatMessageState.Transferring;

    /// <summary>プログレスバーの幅（親の幅に依存せず固定幅で表現）。</summary>
    [JsonIgnore]
    public double ProgressBarWidth => FileProgress * 300;

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

/// <summary>メッセージ種別。</summary>
public enum ChatMessageType
{
    /// <summary>テキストメッセージ。</summary>
    Text,

    /// <summary>ファイル転送。</summary>
    File,

    /// <summary>システムメッセージ（接続/切断通知等）。</summary>
    System,
}

/// <summary>メッセージの状態。</summary>
public enum ChatMessageState
{
    /// <summary>送信中。</summary>
    Sending,

    /// <summary>送信完了（相手に到達前）。</summary>
    Sent,

    /// <summary>配達済み（相手が受信）。</summary>
    Delivered,

    /// <summary>送信失敗。</summary>
    Failed,

    /// <summary>承認待ち（ファイル受信時）。</summary>
    WaitingApproval,

    /// <summary>転送中（ファイル）。</summary>
    Transferring,

    /// <summary>完了（ファイル転送完了）。</summary>
    Completed,
}
