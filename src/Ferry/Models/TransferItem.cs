using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ferry.Models;

/// <summary>
/// ファイル転送アイテム。転送履歴や進行中の転送を表す。
/// ObservableObject 継承で UI バインディングの変更通知を提供。
/// </summary>
public sealed partial class TransferItem : ObservableObject
{
    /// <summary>転送セッションの一意識別子（レジューム時の照合に使用）。</summary>
    public Guid TransferId { get; set; } = Guid.NewGuid();

    /// <summary>ファイル名。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    private string _fileName = string.Empty;

    /// <summary>ファイルサイズ (バイト)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    private long _fileSize;

    /// <summary>転送済みバイト数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    private long _transferredBytes;

    /// <summary>最後に確認済みのチャンクインデックス。</summary>
    public int LastConfirmedChunkIndex { get; set; } = -1;

    /// <summary>チャンク総数。</summary>
    public int TotalChunks { get; set; }

    /// <summary>転送方向。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionSymbol))]
    private TransferDirection _direction;

    /// <summary>転送状態。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(StateColorHex))]
    [NotifyPropertyChangedFor(nameof(IsInProgress))]
    [NotifyPropertyChangedFor(nameof(IsWaitingApproval))]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    private TransferState _state = TransferState.Pending;

    /// <summary>エラーメッセージ（State が Error の場合）。</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>転送相手のピア名（送信先 or 送信元の表示名）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    private string _peerName = string.Empty;

    /// <summary>転送完了日時。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletedAtText))]
    private DateTime? _completedAt;

    /// <summary>SHA-256 ハッシュ値（転送完了後の検証用）。</summary>
    public string? Sha256Hash { get; set; }

    /// <summary>送信元ファイルパス（送信側で保持、レジューム時に使用）。</summary>
    public string? SourceFilePath { get; set; }

    /// <summary>受信ファイルの保存先パス（受信完了後に設定）。</summary>
    public string? SavedFilePath { get; set; }

    /// <summary>ファイルサイズの表示テキスト。</summary>
    public string FileSizeText => Util.Formatting.FormatBytes(FileSize);

    /// <summary>進捗率 (0.0〜1.0)。</summary>
    public double Progress => FileSize > 0 ? (double)TransferredBytes / FileSize : 0;

    /// <summary>転送中かどうか。</summary>
    public bool IsInProgress => State == TransferState.InProgress;

    /// <summary>承認待ちかどうか。</summary>
    public bool IsWaitingApproval => State == TransferState.WaitingApproval;

    /// <summary>方向アイコン。</summary>
    public string DirectionSymbol => Direction == TransferDirection.Send ? "↑" : "↓";

    /// <summary>状態表示色（Avalonia の Color 型ではなくテーマリソースのキー的な hex を返す）。</summary>
    public string StateColorHex => State switch
    {
        TransferState.Completed => "#30D158",   // TahoeGreen
        TransferState.Error => "#FF453A",       // TahoeRed
        TransferState.InProgress => "#007AFF",  // TahoeAccent
        TransferState.WaitingApproval => "#FF9F0A", // TahoeOrange
        _ => "#99EBEBF5",                       // TahoeTextSecondary
    };

    /// <summary>状態表示テキスト。</summary>
    public string StateText => State switch
    {
        TransferState.Pending => App.Text("State.Pending"),
        TransferState.InProgress => Direction == TransferDirection.Send ? App.Text("State.Sending") : App.Text("State.Receiving"),
        TransferState.Completed => App.Text("State.Completed"),
        TransferState.Error => ErrorMessage ?? App.Text("State.Error"),
        TransferState.Cancelled => App.Text("State.Cancelled"),
        TransferState.Suspended => App.Text("State.Suspended"),
        TransferState.WaitingApproval => App.Text("State.WaitingApproval"),
        _ => "",
    };

    /// <summary>完了日時の表示テキスト。</summary>
    public string CompletedAtText => CompletedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? string.Empty;

    /// <summary>詳細情報テキスト（サイズ＋進捗＋ピア名＋完了日時）。</summary>
    public string DisplayInfo
    {
        get
        {
            var sizeText = Util.Formatting.FormatBytes(FileSize);
            var peerPart = !string.IsNullOrEmpty(PeerName)
                ? (Direction == TransferDirection.Send ? $"→ {PeerName}" : $"← {PeerName}")
                : string.Empty;
            return State switch
            {
                TransferState.InProgress => $"{Util.Formatting.FormatBytes(TransferredBytes)} / {sizeText}  ({Progress * 100:F0}%)",
                TransferState.Completed => string.IsNullOrEmpty(peerPart)
                    ? $"{sizeText}  {CompletedAtText}"
                    : $"{sizeText}  {peerPart}  {CompletedAtText}",
                _ => string.IsNullOrEmpty(peerPart) ? sizeText : $"{sizeText}  {peerPart}",
            };
        }
    }

}

/// <summary>転送方向。</summary>
public enum TransferDirection
{
    /// <summary>送信。</summary>
    Send,

    /// <summary>受信。</summary>
    Receive,
}

/// <summary>転送状態。</summary>
public enum TransferState
{
    /// <summary>待機中。</summary>
    Pending,

    /// <summary>転送中。</summary>
    InProgress,

    /// <summary>完了。</summary>
    Completed,

    /// <summary>エラー。</summary>
    Error,

    /// <summary>キャンセル。</summary>
    Cancelled,

    /// <summary>一時停止（接続断による中断、レジューム可能）。</summary>
    Suspended,

    /// <summary>受信承認待ち。</summary>
    WaitingApproval,
}
