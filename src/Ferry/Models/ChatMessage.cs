using System;
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

    /// <summary>送信日時 (UTC)。</summary>
    public DateTime SentAt { get; init; } = DateTime.UtcNow;

    /// <summary>メッセージの状態。</summary>
    [ObservableProperty]
    private ChatMessageState _state = ChatMessageState.Sending;

    /// <summary>自分が送信したメッセージかどうか。</summary>
    public bool IsFromMe { get; init; }

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
        ChatMessageState.Sending => "送信中...",
        ChatMessageState.Sent => "送信済み",
        ChatMessageState.Delivered => "配達済み",
        ChatMessageState.Failed => "失敗",
        ChatMessageState.WaitingApproval => "承認待ち",
        ChatMessageState.Transferring => $"転送中 {FileProgress * 100:F0}%",
        ChatMessageState.Completed => "完了",
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
