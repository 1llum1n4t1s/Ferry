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

    /// <summary>転送相手のピア ID（宛先ごとの履歴フィルタに使用）。
    /// 送信時は宛先 PeerId、受信時は接続元 PeerId を設定する。</summary>
    public string PeerId { get; set; } = string.Empty;

    /// <summary>フォルダ送信時の相対パス（例: "フォルダ名/サブ/ファイル.txt"）。単独ファイルは null。
    /// 再送時に元のフォルダ構造を保って送り直すために保持する。</summary>
    public string? RelativePath { get; set; }

    /// <summary>ファイル名。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    public partial string FileName { get; set; } = string.Empty;

    /// <summary>ファイルサイズ (バイト)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    public partial long FileSize { get; set; }

    /// <summary>転送済みバイト数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    public partial long TransferredBytes { get; set; }

    /// <summary>最後に確認済みのチャンクインデックス。</summary>
    public int LastConfirmedChunkIndex { get; set; } = -1;

    /// <summary>送信側フロー制御: 受信側 FlowAck が報告した「書き込み済みチャンク数」。
    /// v1.0.46 追加。SendChunksAsync がこの値を基準にウィンドウを超えて先行しないよう待機する。
    /// 受信スレッドからの更新 / 送信ループからの読み取りを跨ぐため Volatile で読み書きする。
    /// プロパティではなくフィールドにしているのは ref (Volatile.Read/Write) に渡すため。</summary>
    public int FlowAckedChunks;

    /// <summary>レート計算用: 前回サンプル時の TransferredBytes。v1.0.47 で追加（UI バインド非対象の素フィールド）。</summary>
    public long LastSampledBytes;

    /// <summary>レート計算用: 前回サンプル時刻 (Environment.TickCount64)。0 は未サンプル。</summary>
    public long LastSampleTick;

    /// <summary>チャンク総数。</summary>
    public int TotalChunks { get; set; }

    /// <summary>転送方向。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionSymbol))]
    [NotifyPropertyChangedFor(nameof(DisplayFilePath))]
    [NotifyPropertyChangedFor(nameof(CanResend))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResumeSend))]
    public partial TransferDirection Direction { get; set; }

    /// <summary>転送状態。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(StateColorHex))]
    [NotifyPropertyChangedFor(nameof(IsInProgress))]
    [NotifyPropertyChangedFor(nameof(IsWaitingApproval))]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    [NotifyPropertyChangedFor(nameof(CanResend))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResumeSend))]
    [NotifyPropertyChangedFor(nameof(CanResumeSuspended))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial TransferState State { get; set; } = TransferState.Pending;

    /// <summary>エラーメッセージ（State が Error の場合）。</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>進行中の補足メッセージ（リトライ中・再接続中などの一時表示）。空なら非表示。</summary>
    [ObservableProperty]
    public partial string? Note { get; set; }

    /// <summary>転送中の毎秒レート表示テキスト（例「12.3 Mbps」）。v1.0.47 で追加。
    /// TransferViewModel の 1 秒タイマーが InProgress 項目について更新し、停止/完了でクリアする。</summary>
    [ObservableProperty]
    public partial string? RateText { get; set; }

    /// <summary>転送相手のピア名（送信先 or 送信元の表示名）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayInfo))]
    public partial string PeerName { get; set; } = string.Empty;

    /// <summary>転送完了日時。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletedAtText))]
    public partial DateTime? CompletedAt { get; set; }

    /// <summary>転送開始（行生成）日時。履歴の「いつ送受信したか」を秒まで表示するため。
    /// 生成後不変なので readonly。各 new TransferItem で自動的に現在時刻が入る。</summary>
    public DateTime CreatedAt { get; } = DateTime.Now;

    /// <summary>SHA-256 ハッシュ値（転送完了後の検証用）。</summary>
    public string? Sha256Hash { get; set; }

    /// <summary>送信元ファイルパス（送信側で保持、レジューム時に使用）。
    /// v1.0.38 review fix v11: DisplayFilePath を UI に伝えるため ObservableProperty 化 +
    /// 変更通知を DisplayFilePath / Direction にも伝播する</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayFilePath))]
    public partial string? SourceFilePath { get; set; }

    /// <summary>受信ファイルの保存先パス（受信完了後に設定）。
    /// v1.0.38 review fix v11: CompleteReceive で後付けされる値が UI に届くよう ObservableProperty 化</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayFilePath))]
    public partial string? SavedFilePath { get; set; }

    /// <summary>UI 表示用のフルパス。送信なら SourceFilePath、受信なら SavedFilePath を返す (受信完了前は空)。</summary>
    public string DisplayFilePath => Direction switch
    {
        TransferDirection.Send => SourceFilePath ?? string.Empty,
        TransferDirection.Receive => SavedFilePath ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>ファイルサイズの表示テキスト。</summary>
    public string FileSizeText => Util.Formatting.FormatBytes(FileSize);

    /// <summary>進捗率 (0.0〜1.0)。</summary>
    public double Progress => FileSize > 0 ? (double)TransferredBytes / FileSize : 0;

    /// <summary>転送中かどうか。</summary>
    public bool IsInProgress => State == TransferState.InProgress;

    /// <summary>承認待ちかどうか。</summary>
    public bool IsWaitingApproval => State == TransferState.WaitingApproval;

    /// <summary>終端状態（完了・エラー・キャンセル）か。</summary>
    public bool IsTerminal => State is TransferState.Completed or TransferState.Error or TransferState.Cancelled;

    /// <summary>再送可能か（送信が失敗/キャンセルで終わり、送信元ファイルパスが残っている）。</summary>
    public bool CanResend =>
        Direction == TransferDirection.Send
        && State is TransferState.Error or TransferState.Cancelled
        && !string.IsNullOrEmpty(SourceFilePath);

    /// <summary>キャンセル可能か（送受信が進行中・承認待ち・一時停止のいずれか）。</summary>
    public bool CanCancel =>
        State is TransferState.InProgress or TransferState.Pending
              or TransferState.WaitingApproval or TransferState.Paused;

    /// <summary>一時停止可能か（送信中のみ）。</summary>
    public bool CanPause => Direction == TransferDirection.Send && State == TransferState.InProgress;

    /// <summary>一時停止からの再開が可能か（送信が一時停止中）。</summary>
    public bool CanResumeSend => Direction == TransferDirection.Send && State == TransferState.Paused;

    /// <summary>接続断による中断からのレジュームが可能か。</summary>
    public bool CanResumeSuspended => State == TransferState.Suspended;

    /// <summary>履歴から削除可能か（終端状態 or 中断）。進行中は削除させない。</summary>
    public bool CanDelete => IsTerminal || State == TransferState.Suspended;

    /// <summary>方向アイコン。</summary>
    public string DirectionSymbol => Direction == TransferDirection.Send ? "↑" : "↓";

    /// <summary>状態表示色（Avalonia の Color 型ではなくテーマリソースのキー的な hex を返す）。</summary>
    public string StateColorHex => State switch
    {
        TransferState.Completed => "#30D158",   // TahoeGreen
        TransferState.Error => "#FF453A",       // TahoeRed
        TransferState.InProgress => "#007AFF",  // TahoeAccent
        TransferState.WaitingApproval => "#FF9F0A", // TahoeOrange
        TransferState.Paused => "#FF9F0A",      // TahoeOrange（停止中）
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
        TransferState.Paused => App.Text("State.Paused"),
        _ => "",
    };

    /// <summary>完了日時の表示テキスト。</summary>
    public string CompletedAtText => CompletedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? string.Empty;

    /// <summary>履歴行に常時出す送受信日時テキスト（秒まで）。</summary>
    public string CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>詳細情報テキスト（サイズ＋進捗＋ピア名）。送受信日時は CreatedAtText で別途常時表示する。</summary>
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
                    ? sizeText
                    : $"{sizeText}  {peerPart}",
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

    /// <summary>一時停止中（送信をユーザー操作で停止、再開可能）。</summary>
    Paused,
}
