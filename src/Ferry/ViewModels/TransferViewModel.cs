using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// 転送パネルの ViewModel。
/// ファイルのドラッグ＆ドロップ、転送リスト、進捗管理を提供する。
/// v1.0.47: 履歴を宛先（PeerId）ごとに保持し、選択中ピアの分だけ VisibleTransfers に出す。
/// 送信は UI 行と同じ TransferId を service に渡し、進捗・キャンセル・一時停止・再送を正確に対応付ける。
/// </summary>
public sealed partial class TransferViewModel : ViewModelBase, IDisposable
{
    private readonly IConnectionService _connectionService;
    private readonly ITransferService _transferService;
    private readonly ConnectionViewModel _connectionViewModel;
    private readonly ISettingsService _settingsService;

    /// <summary>送信失敗時の自動リトライ回数。</summary>
    private const int MaxSendAttempts = 3;

    /// <summary>複数ファイル送信の同時並列本数の内部上限。ユーザー設定ではなく固定値
    /// （設定 UI は撤去済み。設定画面には「自動（最大 10）」の表示のみ残す）。</summary>
    public const int MaxParallelSends = 10;

    [ObservableProperty]
    public partial bool IsDragOver { get; set; }

    [ObservableProperty]
    public partial bool IsTransferring { get; set; }

    /// <summary>選択中ピアの転送アイテムがあるか（空状態表示の切替に使用）。</summary>
    [ObservableProperty]
    public partial bool HasTransfers { get; set; }

    /// <summary>承認待ちの受信アイテムがあるか（サイドメニュー下部パネル表示用）。</summary>
    [ObservableProperty]
    public partial bool HasPendingApproval { get; set; }

    /// <summary>現在 VisibleTransfers にフィルタしている宛先 PeerId。</summary>
    private string _selectedPeerId = string.Empty;

    /// <summary>進行中送信のキャンセル用 CTS（TransferId → CTS）。キャンセル/一時停止操作と再送ループが共有する。</summary>
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sendCtsByItem = new();

    /// <summary>転送中行の毎秒レート(bps)表示を更新するタイマー（UI スレッド駆動）。</summary>
    private readonly DispatcherTimer _rateTimer;

    /// <summary>
    /// 全宛先の転送アイテム（マスター）。宛先を切り替えても消えず保持される。
    /// </summary>
    public ObservableCollection<TransferItem> Transfers { get; } = [];

    /// <summary>
    /// 選択中ピアの転送アイテムだけを抽出したビュー。TransferView はこちらを表示する。
    /// </summary>
    public ObservableCollection<TransferItem> VisibleTransfers { get; } = [];

    /// <summary>
    /// 承認待ちの受信アイテム一覧（サイドバー下部パネルに表示）。
    /// </summary>
    public ObservableCollection<TransferItem> PendingApprovals { get; } = [];

    public TransferViewModel(
        IConnectionService connectionService,
        ITransferService transferService,
        ConnectionViewModel connectionViewModel,
        ISettingsService settingsService)
    {
        _connectionService = connectionService;
        _transferService = transferService;
        _connectionViewModel = connectionViewModel;
        _settingsService = settingsService;

        _transferService.ProgressChanged += OnProgressChanged;
        _transferService.FileReceived += OnFileReceived;
        _transferService.TransferError += OnTransferError;
        _transferService.ApprovalRequested += OnApprovalRequested;

        // 宛先を切り替えたら表示履歴を入れ替える
        _connectionViewModel.PropertyChanged += OnConnectionVmPropertyChanged;
        _selectedPeerId = _connectionViewModel.SelectedPeer?.PeerId ?? string.Empty;

        VisibleTransfers.CollectionChanged += (_, _) => HasTransfers = VisibleTransfers.Count > 0;
        Transfers.CollectionChanged += OnTransfersCollectionChanged;  // rere #C1-001: O(1) 引き索引を自動維持

        // 転送中の毎秒レート表示。DI 組み立ては UI スレッドなのでここで生成・開始してよい。
        _rateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _rateTimer.Tick += OnRateTimerTick;
        _rateTimer.Start();
    }

    /// <summary>
    /// 1 秒ごとに表示中の転送行のレート(bps)を更新する。InProgress 以外（完了/停止/一時停止）はクリアする。
    /// </summary>
    private void OnRateTimerTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        foreach (var item in VisibleTransfers)
        {
            if (!item.IsInProgress || item.State == TransferState.Paused)
            {
                item.RateText = null;     // 完了/停止/一時停止はレート非表示
                item.RateStartTick = 0;   // 再開時は開始基準を取り直す（停止区間を平均に含めない）
                continue;
            }

            if (item.RateStartTick == 0)
            {
                // 転送開始（または再開）直後の基準点を確定。経過 0 のうちはレート未確定。
                item.RateStartTick = now;
                item.RateStartBytes = item.TransferredBytes;
                continue;
            }

            // 開始からの累積平均（総転送バイト ÷ 経過秒）。瞬間差分と違いチャンクのバーストで乱高下しない。
            var elapsed = (now - item.RateStartTick) / 1000.0;
            var dBytes = item.TransferredBytes - item.RateStartBytes;
            if (elapsed > 0 && dBytes > 0)
                item.RateText = Util.Formatting.FormatBitrate(dBytes * 8 / elapsed);
        }
    }

    // === 宛先別フィルタ ===

    private void OnConnectionVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionViewModel.SelectedPeer)) return;
        _selectedPeerId = _connectionViewModel.SelectedPeer?.PeerId ?? string.Empty;
        if (Dispatcher.UIThread.CheckAccess())
            RebuildVisibleTransfers();
        else
            Dispatcher.UIThread.Post(RebuildVisibleTransfers);
    }

    private bool BelongsToSelectedPeer(TransferItem item) =>
        !string.IsNullOrEmpty(_selectedPeerId) && item.PeerId == _selectedPeerId;

    private void RebuildVisibleTransfers()
    {
        VisibleTransfers.Clear();
        foreach (var t in Transfers.Where(BelongsToSelectedPeer))
            VisibleTransfers.Add(t);
        HasTransfers = VisibleTransfers.Count > 0;
    }

    /// <summary>マスターと、選択中なら表示ビューにアイテムを追加する。</summary>
    private void AddTransfer(TransferItem item)
    {
        Transfers.Add(item);  // _transferIndex は Transfers.CollectionChanged で自動更新 (rere #C1-001)
        if (BelongsToSelectedPeer(item))
            VisibleTransfers.Add(item);
    }

    /// <summary>マスター・表示ビューからアイテムを除去する。</summary>
    private void RemoveTransfer(TransferItem item)
    {
        Transfers.Remove(item);  // _transferIndex は Transfers.CollectionChanged で自動更新 (rere #C1-001)
        VisibleTransfers.Remove(item);
    }

    // rere #C1-001: _transferIndex を Transfers の真の projection として CollectionChanged で自動維持する。
    // AddTransfer 経由でも直接 Transfers.Add でも (テスト/将来コード) 索引が常に一致する。
    private void OnTransfersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                    foreach (TransferItem it in e.NewItems) IndexAdd(it);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                    foreach (TransferItem it in e.OldItems) IndexRemove(it);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems != null)
                    foreach (TransferItem it in e.OldItems) IndexRemove(it);
                if (e.NewItems != null)
                    foreach (TransferItem it in e.NewItems) IndexAdd(it);
                break;
            case NotifyCollectionChangedAction.Reset:
                _transferIndex.Clear();
                foreach (var it in Transfers) IndexAdd(it);
                break;
            // Move: 並び順のみ変化。索引は不変
        }
    }

    private void IndexAdd(TransferItem item)
    {
        if (!_transferIndex.TryGetValue(item.TransferId, out var list))
            _transferIndex[item.TransferId] = list = new List<TransferItem>(1);
        list.Add(item);
    }

    private void IndexRemove(TransferItem item)
    {
        if (_transferIndex.TryGetValue(item.TransferId, out var list))
        {
            list.Remove(item);
            if (list.Count == 0) _transferIndex.Remove(item.TransferId);
        }
    }

    /// <summary>rere #B1-001: サービスが保持する受信 TransferItem (state.Item) は受信ループ
    /// (背景スレッド) から TransferredBytes / State を直接書き換える。これを UI バインドコレクションに
    /// そのまま入れると bound プロパティをスレッド跨ぎで変更してしまう (Avalonia は UI スレッド外の
    /// 変更通知で不正動作しうる)。そこで VM は常にこの VM 所有コピーを bind し、サービスの instance は
    /// unbound のままにする。以後の値反映は OnProgressChanged 等が UI スレッドで TransferId 照合して
    /// このコピーへ行う (相関は TransferId ベースなのでインスタンス共有は不要)。サービス側は無変更。</summary>
    private static TransferItem CreateDisplayCopy(TransferItem src) => new()
    {
        TransferId = src.TransferId,
        PeerId = src.PeerId,
        PeerName = src.PeerName,
        RelativePath = src.RelativePath,
        FileName = src.FileName,
        FileSize = src.FileSize,
        TransferredBytes = src.TransferredBytes,
        TotalChunks = src.TotalChunks,
        Direction = src.Direction,
        State = src.State,
        ErrorMessage = src.ErrorMessage,
        Note = src.Note,
        Sha256Hash = src.Sha256Hash,
        SourceFilePath = src.SourceFilePath,
        SavedFilePath = src.SavedFilePath,
        CompletedAt = src.CompletedAt,
    };

    /// <summary>TransferId → 該当 TransferItem 群の索引 (rere #C1-001)。
    /// 進捗/状態イベント毎に走っていた Transfers の全線形走査 (O(n)) を O(1) 引きに置き換える。
    /// membership は AddTransfer / RemoveTransfer の 2 箇所でのみ変化し Transfers.Add/Remove と 1:1。
    /// 同一 TransferId に複数行が並ぶ (接続断→retry の履歴) ため値は List とし、下記 FindTransfer の
    /// 「非 terminal 優先」意味論は query 時に小リスト上で適用する (索引化しても挙動は不変)。
    /// アクセスは全て UI スレッド (進捗/状態イベントは UI スレッドへ marshal 済み) なので追加ロック不要。</summary>
    private readonly Dictionary<Guid, List<TransferItem>> _transferIndex = new();

    /// <summary>TransferId からアイテムを引く。同じ TransferId の行が複数あれば「非 terminal 行」を優先する。
    /// 接続断 → 自動 retry のシナリオで、受信側にも前 attempt の Cancelled / Error 行が履歴として残ったまま
    /// 新 attempt の FileMeta が来ると同 TransferId の行が並ぶ。素朴な FirstOrDefault は古い terminal 行を
    /// 返してしまい、新 attempt の進捗が古い行を更新する/新行に反映されない不整合になる。
    /// 非 terminal 行を優先することで進行中行が確実に拾われ、履歴側の terminal 行はそのまま残る（履歴粒度は維持）。
    /// rere #C1-001: 全走査をやめ _transferIndex の per-id リスト (通常 1〜2 要素) 上で同じ優先順位を適用する。</summary>
    private TransferItem? FindTransfer(Guid transferId)
    {
        if (!_transferIndex.TryGetValue(transferId, out var list) || list.Count == 0)
            return null;
        return list.FirstOrDefault(t => !t.IsTerminal) ?? list[0];
    }

    private void RecomputeIsTransferring()
    {
        IsTransferring = Transfers.Any(t => t.State == TransferState.InProgress);
        // 複数ペア同時接続対応 Stage 6: PairedPeer ごとに進行中転送件数を集計し、
        // PairedPeer.IsTransferring / ActiveTransferCount を set する（左ペインのバッジ用）。
        // 集計は Transfers の全行を peerId でグルーピングする線形走査（通常 ~10 行）。
        // 一覧外（PairedPeers に居ない peerId）はスキップする。
        RecomputePerPeerTransferCounts();
    }

    /// <summary>複数ペア同時接続対応 Stage 6: TransferItem.PeerId 別に進行中件数を集計して
    /// 該当 <see cref="PairedPeer"/> の <see cref="PairedPeer.IsTransferring"/> / <see cref="PairedPeer.ActiveTransferCount"/>
    /// を更新する。AddTransfer / 完了/エラー/キャンセル/状態遷移時に <see cref="RecomputeIsTransferring"/> 経由で呼ばれ、
    /// 左ペインのピアリストに per-peer 転送バッジを反映する。</summary>
    private void RecomputePerPeerTransferCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in Transfers)
        {
            if (t.State != TransferState.InProgress) continue;
            var pid = t.PeerId;
            if (string.IsNullOrEmpty(pid)) continue;
            counts[pid] = counts.TryGetValue(pid, out var c) ? c + 1 : 1;
        }
        foreach (var peer in _connectionViewModel.PairedPeers)
        {
            var n = counts.TryGetValue(peer.PeerId, out var c) ? c : 0;
            // ObservableProperty の setter は値が同じなら通知しない（生成済 generator がガード）。
            peer.ActiveTransferCount = n;
            peer.IsTransferring = n > 0;
        }
    }

    /// <summary>受信時の宛先を解決する。
    /// 複数ペア同時接続対応 Stage 2: service 側 HandleFileMeta が transport 由来 peerId を権威設定するため、
    /// VM では <paramref name="suggestedPeerId"/>(イベント引数の TransferItem.PeerId) を最優先する。
    /// 空のときだけ旧来の単数 ConnectedPeer / CurrentListeningPeerId / SelectedPeer の逆引きへフォールバックする
    /// （複数ペア同時受信で取り違える経路は service 側で peerId が必ず付帯するため死に経路だが、
    /// テスト経路や旧 transport 互換のために残す）。</summary>
    private (string PeerId, string PeerName) ResolveReceivePeer(string? suggestedPeerId = null)
    {
        var peerId = !string.IsNullOrEmpty(suggestedPeerId)
            ? suggestedPeerId
            : (_connectionService.ConnectedPeer?.SessionId
               ?? _connectionService.CurrentListeningPeerId
               ?? _connectionViewModel.SelectedPeer?.PeerId
               ?? string.Empty);
        var peer = _connectionViewModel.PairedPeers.FirstOrDefault(p => p.PeerId == peerId);
        var name = peer?.DisplayName ?? _connectionViewModel.SelectedPeer?.DisplayName ?? string.Empty;
        return (peerId, name);
    }

    // === 送信 ===

    /// <summary>
    /// ファイル選択ダイアログを開き、選択されたファイルを送信する。
    /// </summary>
    [RelayCommand]
    private async Task BrowseAndSendFilesAsync()
    {
        if (App.GetMainWindow() is not { } mainWindow)
            return;

        var storageProvider = mainWindow.StorageProvider;
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = App.Text("Transfer.SelectFiles"),
        });

        if (files.Count == 0) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null)
            .ToArray();

        if (paths.Length > 0)
            await SendFilesAsync(paths!);
    }

    /// <summary>
    /// ファイル/フォルダパスの配列を受け取り、送信を開始する。
    /// フォルダの場合は中のファイルを再帰的に列挙し、フォルダ構造を保持して送信する。
    /// 未接続の場合はオンデマンドで接続を確立してから転送する。
    /// </summary>
    [RelayCommand]
    private async Task SendFilesAsync(string[] filePaths)
    {
        var peer = _connectionViewModel.SelectedPeer;
        Util.Logger.Log($"SendFilesAsync 開始: {filePaths.Length} パス, SelectedPeer={peer?.DisplayName ?? "null"}, State={_connectionService.State}");

        if (filePaths.Length == 0 || peer == null)
        {
            Util.Logger.Log($"送信スキップ: filePaths={filePaths.Length}, peer={peer?.DisplayName ?? "null"}");
            return;
        }

        var entries = ExpandPaths(filePaths);
        if (entries.Count == 0)
        {
            Util.Logger.Log("送信対象のファイルがありません", Util.LogLevel.Warning);
            return;
        }

        Util.Logger.Log($"送信対象: {entries.Count} ファイル");

        // v1.0.47: 先に全ファイル分のアイテムを生成して即 VisibleTransfers へ出す（5 選択なら 5 行が即時に並ぶ）。
        // 旧実装は foreach で await SendOneFileAsync していたため、1 件完了するまで次の行が出ず
        // 「1 行ずつ遅れて増える」症状になっていた。
        var items = new List<TransferItem>(entries.Count);
        foreach (var (absolutePath, relativePath) in entries)
        {
            var fi = new FileInfo(absolutePath);
            var item = new TransferItem
            {
                FileName = relativePath ?? fi.Name,
                FileSize = fi.Length,
                Direction = TransferDirection.Send,
                State = TransferState.Pending,
                PeerName = peer.DisplayName,
                PeerId = peer.PeerId,
                SourceFilePath = absolutePath,
                RelativePath = relativePath,
            };
            AddTransfer(item);
            items.Add(item);
        }
        RecomputeIsTransferring();

        // Codex #3516870395 対応: 各 item が個別に EnsureConnectedToPeerAsync を並列で呼ぶと、
        // ConnectionService の per-session ゲート取得前に互いの接続試行 CTS を cancel し合い、
        // 無駄なリトライが発生していた。バッチ全体で 1 回だけ先に接続を確立してから並列送信に
        // 入ることで同一ピアへの同時接続試行そのものを起こさない（EnsureConnectedToPeerAsync は
        // 接続済みなら即 return するため、既接続時のオーバーヘッドは無視できる）。
        try
        {
            await EnsureConnectedToPeerAsync(peer, default);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"送信前の接続確立に失敗: {ex.Message}", Util.LogLevel.Error);
            foreach (var item in items)
            {
                item.State = TransferState.Error;
                item.ErrorMessage = ex is PeerUnreachableException or InvalidOperationException
                    ? ex.Message
                    : Util.ErrorText.Describe(ex);
            }
            RecomputeIsTransferring();
            return;
        }

        // 並列転送は内部固定（最大 MaxParallelSends 本まで自動同時送信、ユーザー設定は撤去済み）。
        // ConnectionService の SendAsync は各 transport (TCP/WebSocket/UDP) で SemaphoreSlim 排他されているため
        // length-prefix フレームの交錯は起こらない。受信側は TransferId キーの ConcurrentDictionary で
        // 複数受信を独立管理できる。await 継続は UI スレッドに戻るので ObservableCollection / RecomputeIsTransferring
        // は UI スレッドでシリアル化される (Task.WhenAll 並列でもこの不変条件は維持される)。
        using var sem = new SemaphoreSlim(MaxParallelSends, MaxParallelSends);

        var sendTasks = items.Select(async item =>
        {
            await sem.WaitAsync();
            try
            {
                // 自分の番が来る前にユーザーがキャンセル / 削除した行はスキップする。
                // SendItemAsync 開始前は _sendCtsByItem に CTS が無く、service にも _activeTransfers が無いため、
                // CancelTransfer は単に State を Cancelled に書き換えるだけ。ここで尊重しないと
                // キャンセル済み行も律儀に送ってしまう。
                if (item.State is TransferState.Cancelled or TransferState.Error
                    || FindTransfer(item.TransferId) is null)
                    return;
                await SendItemAsync(item, peer);
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        await Task.WhenAll(sendTasks);
    }

    /// <summary>
    /// 1 ファイルを送信する。接続確立 → 送信を行い、一過性エラーは <see cref="MaxSendAttempts"/> 回まで
    /// 自動リトライして UI に「リトライ中」を表示する。再送（ResendAsync）からも呼ばれる。
    /// </summary>
    private async Task SendOneFileAsync(string absolutePath, string? relativePath, PairedPeer peer)
    {
        var fileInfo = new FileInfo(absolutePath);
        var item = new TransferItem
        {
            FileName = relativePath ?? fileInfo.Name,
            FileSize = fileInfo.Length,
            Direction = TransferDirection.Send,
            State = TransferState.Pending,
            PeerName = peer.DisplayName,
            PeerId = peer.PeerId,
            SourceFilePath = absolutePath,
            RelativePath = relativePath,
        };
        AddTransfer(item);
        RecomputeIsTransferring();
        await SendItemAsync(item, peer);
    }

    /// <summary>
    /// 既に生成・AddTransfer 済みの送信アイテムを実際に転送する。接続確立 → 送信を行い、
    /// 一過性エラーは <see cref="MaxSendAttempts"/> 回まで自動リトライして UI に「リトライ中」を表示する。
    /// 複数選択時は呼び出し側が先に全アイテムを AddTransfer し、これを 1 件ずつ直列に呼ぶ。
    /// </summary>
    private async Task SendItemAsync(TransferItem item, PairedPeer peer)
    {
        var absolutePath = item.SourceFilePath!;
        var relativePath = item.RelativePath;
        var displayName = item.FileName;
        item.State = TransferState.InProgress;
        RecomputeIsTransferring();

        var cts = new CancellationTokenSource();
        _sendCtsByItem[item.TransferId] = cts;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (attempt > 1)
                    {
                        item.Note = App.Text("Transfer.Retrying", attempt, MaxSendAttempts);
                        item.State = TransferState.InProgress;
                    }

                    try
                    {
                        await EnsureConnectedToPeerAsync(peer, cts.Token);
                    }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                    {
                        // 並列送信の相互キャンセルや接続の置換など、この item 自身のキャンセルではない
                        // 接続中断。OCE のまま伝播させると下の no-retry catch が「キャンセル」扱いに
                        // 確定させるため、一過性エラーに変換してリトライ分岐へ回す
                        throw new InvalidOperationException(App.Text("Transfer.ConnectFailed"));
                    }
                    // 複数ペア同時接続対応 Stage 5: UI 行と同じ TransferId に加え、宛先 peerId も明示する。
                    // _connectionService 側で per-peer の transport へ流れ、Stage 4 で並列接続が解禁された後も
                    // 「宛先取り違え」が起きない。
                    await _transferService.SendFileAsync(absolutePath, relativePath, item.TransferId, peer.PeerId, cts.Token);

                    item.State = TransferState.Completed;
                    item.TransferredBytes = item.FileSize;
                    item.CompletedAt = DateTime.UtcNow;
                    item.Note = null;
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    // ユーザーキャンセル / 承認拒否 / 承認タイムアウト → リトライしない
                    Util.Logger.Log($"送信中断 ({displayName}): {ex.Message}", Util.LogLevel.Warning);
                    if (item.State is TransferState.InProgress or TransferState.Pending or TransferState.Paused)
                    {
                        item.State = TransferState.Cancelled;
                        item.ErrorMessage = ex.Message;
                    }
                    item.Note = null;
                    break;
                }
                catch (PeerUnreachableException ex)
                {
                    // 相手が無応答（オフライン/未起動/到達不可）。リトライしても 20s 待ちを空打ちするだけなので
                    // 即終了して明確な理由を出す。相手が戻ったら手動「再送」で送り直せる。
                    Util.Logger.Log($"送信中断 ({displayName}): 相手無応答のためリトライせず終了: {ex.Message}", Util.LogLevel.Warning);
                    item.State = TransferState.Error;
                    item.ErrorMessage = ex.Message;
                    item.Note = null;
                    break;
                }
                catch (Exception ex) when (attempt < MaxSendAttempts && !cts.IsCancellationRequested)
                {
                    // 一過性エラー（切断・送信失敗など）→ 少し待って自動リトライ
                    Util.Logger.Log($"送信失敗 (試行 {attempt}/{MaxSendAttempts}) {displayName}: {ex.Message}", Util.LogLevel.Warning);
                    item.Note = App.Text("Transfer.Retrying", attempt + 1, MaxSendAttempts);
                    try { await Task.Delay(1000 * attempt, cts.Token); }
                    catch (OperationCanceledException)
                    {
                        item.State = TransferState.Cancelled;
                        item.Note = null;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"ファイル送信エラー ({displayName}): {ex.GetType().Name}: {ex.Message}", Util.LogLevel.Error);
                    item.State = TransferState.Error;
                    item.ErrorMessage = Util.ErrorText.Describe(ex);
                    item.Note = null;
                    break;
                }
            }
        }
        finally
        {
            _sendCtsByItem.TryRemove(item.TransferId, out _);
            cts.Dispose();
            RecomputeIsTransferring();
        }
    }

    /// <summary>複数ペア同時接続対応 Stage 5: 指定 peer に接続済みであることを保証する。
    /// 旧 <c>EnsureConnectedAsync</c> は SelectedPeer を書き換えて
    /// <see cref="ConnectionViewModel.ConnectToSelectedPeerAsync"/> 経由で接続していたが、
    /// 並列接続が解禁されると「item A 送信中の selection 切替が item A の宛先を踏み潰す」race の根が残る。
    /// Stage 5 は SelectedPeer を触らず、<see cref="IConnectionService.ConnectToPeerAsync"/> を peerId 指定で
    /// 直接呼ぶ。接続済みかは <see cref="IConnectionService.ConnectedPeers"/> 集合で判定する。
    /// ct は CancelTransfer 経由で渡され、接続待ち中もキャンセルで抜けられる。</summary>
    private async Task EnsureConnectedToPeerAsync(PairedPeer peer, CancellationToken ct)
    {
        // 既に対象 peer へ接続済みなら何もしない（並列接続が解禁された Stage 4 でも安全）。
        if (_connectionService.ConnectedPeers.ContainsKey(peer.PeerId))
            return;

        Util.Logger.Log($"未接続のためオンデマンド接続を開始… 宛先={peer.DisplayName}");
        await _connectionService.ConnectToPeerAsync(peer.PeerId, ct);
        Util.Logger.Log($"オンデマンド接続完了: 宛先={peer.DisplayName}");

        if (!_connectionService.ConnectedPeers.ContainsKey(peer.PeerId))
            throw new InvalidOperationException(App.Text("Transfer.ConnectFailed"));
    }

    /// <summary>
    /// 送信に失敗/キャンセルした履歴を、同じ宛先・同じファイルで送り直す。
    /// </summary>
    [RelayCommand]
    private async Task ResendAsync(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item is null || !item.CanResend || string.IsNullOrEmpty(item.SourceFilePath))
            return;

        var sourcePath = item.SourceFilePath!;
        if (!File.Exists(sourcePath))
        {
            item.State = TransferState.Error;
            item.ErrorMessage = App.Text("Transfer.SourceMissing");
            return;
        }

        var peer = _connectionViewModel.PairedPeers.FirstOrDefault(p => p.PeerId == item.PeerId);
        if (peer == null)
        {
            item.State = TransferState.Error;
            item.ErrorMessage = App.Text("Transfer.PeerMissing");
            return;
        }

        // 別ピアを選択中なら元の宛先に切り替える（着信監視・表示も追従する）
        if (_connectionViewModel.SelectedPeer?.PeerId != peer.PeerId)
            _connectionViewModel.SelectedPeer = peer;

        // 古い失敗行は消して、新しい送信行を作る
        RemoveTransfer(item);
        await SendOneFileAsync(sourcePath, item.RelativePath, peer);
    }

    /// <summary>
    /// ファイル/フォルダパスの配列を、(絶対パス, 相対パス) のリストに展開する。
    /// </summary>
    private static List<(string AbsolutePath, string? RelativePath)> ExpandPaths(string[] paths)
    {
        var result = new List<(string, string?)>();

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                result.Add((path, null));
            }
            else if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                var rootName = dirInfo.Name;
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    var relative = Path.Combine(rootName, Path.GetRelativePath(path, file.FullName));
                    relative = relative.Replace('\\', '/');
                    result.Add((file.FullName, relative));
                }
            }
        }

        return result;
    }

    // === レジューム（接続断による中断からの復旧）===

    /// <summary>
    /// 中断された転送を再開する。
    /// </summary>
    [RelayCommand]
    private async Task ResumeTransferAsync(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item is null || item.State != TransferState.Suspended)
            return;

        item.State = TransferState.InProgress;
        RecomputeIsTransferring();

        try
        {
            var success = await _transferService.ResumeTransferAsync(transferId);
            if (success)
            {
                item.State = TransferState.Completed;
                item.TransferredBytes = item.FileSize;
                item.CompletedAt = DateTime.UtcNow;
            }
            else
            {
                item.State = TransferState.Error;
                item.ErrorMessage = App.Text("Transfer.ResumeFailed");
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"転送レジュームエラー ({transferId}): {ex.GetType().Name}: {ex.Message}", Util.LogLevel.Error);
            item.State = TransferState.Error;
            item.ErrorMessage = Util.ErrorText.Describe(ex);
        }

        RecomputeIsTransferring();
    }

    // === 一時停止 / 再開 / キャンセル / 削除 ===

    /// <summary>送信を一時停止する。</summary>
    [RelayCommand]
    private void PauseTransfer(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item is null || !item.CanPause) return;
        // service が受理した（_activeTransfers に転送が乗っている）場合のみ UI を Paused に。
        // 接続待ち / retry backoff 中はサービスに transfer が無く no-op が返るので UI 偽 Paused 表示を避ける。
        if (_transferService.PauseSendTransfer(transferId.ToString()))
            item.State = TransferState.Paused; // 受理時のみ即時反映（service の進捗イベントでも来る）
    }

    /// <summary>一時停止中の送信を再開する。</summary>
    [RelayCommand]
    private void ResumeSend(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item is null || !item.CanResumeSend) return;
        item.State = TransferState.InProgress;
        _transferService.ResumeSendTransfer(transferId.ToString());
    }

    /// <summary>進行中の送信・受信をキャンセルする。相手にも通知して両側を停止する。</summary>
    [RelayCommand]
    private void CancelTransfer(Guid transferId)
    {
        // service 側を先に呼ぶ：VM の CTS を先に Cancel すると、SendFileAsync が
        // OperationCanceled で _activeTransfers から item を抜けたあとに CancelTransfer が走り、
        // service が「送信中」と認識できず SendRejectFireAndForget を投げ損ねる競合が起きる。
        // 先に service 側で Cancelled 遷移 + FileReject 送信させ、その後 VM 側ループを止める。
        _transferService.CancelTransfer(transferId.ToString());

        // VM 側 CTS（接続待ち・リトライ待機を含む）を中断
        if (_sendCtsByItem.TryGetValue(transferId, out var cts))
            cts.Cancel();

        // まだ SendItemAsync が始まっていない Pending 行（複数選択時の後続）は、
        // service にも _sendCtsByItem にもエントリが無いため、ここで明示的に Cancelled へ遷移させる。
        // これがないと SendFilesAsync の foreach がそのまま送ってしまう（後続の skip ガードとセットで効く）。
        var queued = FindTransfer(transferId);
        if (queued != null && queued.State == TransferState.Pending)
        {
            queued.State = TransferState.Cancelled;
            queued.ErrorMessage = App.Text("State.Cancelled");
        }

        // 承認待ち受信のキャンセルは UI リストからも外す
        var pending = PendingApprovals.FirstOrDefault(t => t.TransferId == transferId);
        if (pending != null)
        {
            PendingApprovals.Remove(pending);
            HasPendingApproval = PendingApprovals.Count > 0;
            pending.State = TransferState.Cancelled;
            if (FindTransfer(pending.TransferId) is null)
                AddTransfer(pending);
        }
    }

    /// <summary>終端状態のアイテムを履歴から削除する。</summary>
    [RelayCommand]
    private void DeleteTransfer(Guid transferId)
    {
        var item = FindTransfer(transferId);
        if (item is null || !item.CanDelete) return;
        RemoveTransfer(item);
    }

    /// <summary>選択中ピアの完了/失敗/キャンセル済み履歴をまとめて削除する。</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        var toRemove = Transfers
            .Where(t => BelongsToSelectedPeer(t) && t.CanDelete)
            .ToList();

        foreach (var item in toRemove)
            RemoveTransfer(item);
    }

    // === service イベント ===

    /// <summary>
    /// 進捗更新イベント。TransferId で照合し、転送済みバイト数と非終端状態を反映する。
    /// </summary>
    private void OnProgressChanged(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = FindTransfer(e.TransferId);
            if (item is null)
            {
                if (e.Direction != TransferDirection.Receive)
                    return; // 送信は VM 側で必ず先に行を作る

                // 進捗が承認イベントより先に来た受信の保険。
                // 複数ペア同時接続対応 Stage 2: service 側 HandleFileMeta が e.PeerId を権威設定済みなので最優先。
                var (peerId, peerName) = ResolveReceivePeer(e.PeerId);
                item = new TransferItem
                {
                    TransferId = e.TransferId,
                    FileName = e.FileName,
                    FileSize = e.FileSize,
                    TotalChunks = e.TotalChunks,
                    Direction = TransferDirection.Receive,
                    State = TransferState.InProgress,
                    PeerId = peerId,
                    PeerName = peerName,
                };
                AddTransfer(item);
            }

            if (item.IsTerminal) return; // 終端状態の行は遅延進捗で巻き戻さない

            item.TransferredBytes = e.TransferredBytes;
            // service が通知する非終端状態（Pending/InProgress/Paused）を反映する
            if (e.State is TransferState.Pending or TransferState.InProgress or TransferState.Paused)
                item.State = e.State;

            RecomputeIsTransferring();
        });
    }

    /// <summary>
    /// ファイル受信完了イベント。
    /// </summary>
    private void OnFileReceived(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = FindTransfer(e.TransferId);
            if (item != null)
            {
                item.State = TransferState.Completed;
                item.TransferredBytes = e.FileSize;
                item.FileName = e.FileName;
                item.SavedFilePath = e.SavedFilePath;
                item.CompletedAt = DateTime.UtcNow;
            }
            else
            {
                // 複数ペア同時接続対応 Stage 2: service 側 e.PeerId を優先。
                var (peerId, peerName) = ResolveReceivePeer(e.PeerId);
                e.PeerId = peerId;
                e.PeerName = peerName;
                e.CompletedAt = DateTime.UtcNow;
                AddTransfer(CreateDisplayCopy(e));  // rere #B1-001: サービス instance を bind しない
            }

            RecomputeIsTransferring();
        });
    }

    /// <summary>
    /// 転送エラーイベント。
    /// </summary>
    private void OnTransferError(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 承認待ち受信のキャンセル通知（送信側タイムアウト等）を反映
            var pending = PendingApprovals.FirstOrDefault(t => t.TransferId == e.TransferId);
            if (pending != null)
            {
                pending.State = TransferState.Cancelled;
                pending.ErrorMessage = e.ErrorMessage;
                PendingApprovals.Remove(pending);
                HasPendingApproval = PendingApprovals.Count > 0;
            }

            // 進行中送信は SendOneFileAsync のリトライループが状態を所有する。
            // service の TransferError でここを終端状態に書き換えるとリトライと競合するのでスキップする。
            if (_sendCtsByItem.ContainsKey(e.TransferId))
                return;

            var item = FindTransfer(e.TransferId);
            if (item != null)
            {
                // service 側の terminal state（Cancelled / Error）をそのまま尊重する。
                // 非終端で来た場合のみ防御的に Error へ昇格。
                item.State = e.State is TransferState.InProgress or TransferState.Pending
                    ? TransferState.Error
                    : e.State;
                item.ErrorMessage = e.ErrorMessage;
                item.Note = null;
            }
            else if (pending == null && e.Direction == TransferDirection.Receive)
            {
                // 進捗より先に来た受信エラーの保険（送信は VM 側に必ず行があるので追加しない）
                if (e.State is TransferState.InProgress or TransferState.Pending)
                    e.State = TransferState.Error;
                // 複数ペア同時接続対応 Stage 2: service 側 e.PeerId を優先（既存ロジックの『空時のみ補完』を踏襲）。
                var (peerId, peerName) = ResolveReceivePeer(e.PeerId);
                if (string.IsNullOrEmpty(e.PeerId)) e.PeerId = peerId;
                if (string.IsNullOrEmpty(e.PeerName)) e.PeerName = peerName;
                AddTransfer(CreateDisplayCopy(e));  // rere #B1-001: サービス instance を bind しない
            }

            RecomputeIsTransferring();
        });
    }

    /// <summary>
    /// ファイル受信承認要求イベント。承認待ちアイテムを UI に追加する。
    /// AutoAcceptFileTransfer=true の場合は UI に出さず即承認する。
    /// </summary>
    private void OnApprovalRequested(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 複数ペア同時接続対応 Stage 2: service 側 HandleFileMeta が FileMeta 到着時点で
            // transport 由来 peerId を権威設定しているので、e.PeerId を ResolveReceivePeer 第1引数に渡して
            // 必ず優先採用させる（既存挙動を踏襲）。PeerName は表示名解決を持つ VM 側で常に設定。
            var (peerId, peerName) = ResolveReceivePeer(e.PeerId);
            if (string.IsNullOrEmpty(e.PeerId)) e.PeerId = peerId;
            e.PeerName = peerName;

            if (_settingsService.Settings.AutoAcceptFileTransfer)
            {
                Util.Logger.Log($"AutoAccept: 即承認: {e.FileName}");
                e.State = TransferState.InProgress;
                AddTransfer(CreateDisplayCopy(e));  // rere #B1-001: サービス instance を bind しない
                _transferService.ApproveTransfer(e.TransferId.ToString());
                RecomputeIsTransferring();
                return;
            }

            PendingApprovals.Add(CreateDisplayCopy(e));  // rere #B1-001: サービス instance を bind しない
            HasPendingApproval = PendingApprovals.Count > 0;
        });
    }

    /// <summary>
    /// 受信を承認する。
    /// </summary>
    [RelayCommand]
    private void ApproveTransfer(Guid transferId)
    {
        var item = PendingApprovals.FirstOrDefault(t => t.TransferId == transferId);
        if (item == null) return;

        PendingApprovals.Remove(item);
        HasPendingApproval = PendingApprovals.Count > 0;

        item.State = TransferState.InProgress;
        AddTransfer(item);
        _transferService.ApproveTransfer(transferId.ToString());
        RecomputeIsTransferring();
    }

    /// <summary>
    /// 受信を拒否する。
    /// </summary>
    [RelayCommand]
    private void RejectTransfer(Guid transferId)
    {
        var item = PendingApprovals.FirstOrDefault(t => t.TransferId == transferId);
        if (item == null) return;

        PendingApprovals.Remove(item);
        HasPendingApproval = PendingApprovals.Count > 0;

        _transferService.RejectTransfer(transferId.ToString());
        item.State = TransferState.Cancelled;
        item.ErrorMessage = App.Text("Transfer.Rejected");
        AddTransfer(item);
    }

    public void Dispose()
    {
        _rateTimer.Stop();
        _rateTimer.Tick -= OnRateTimerTick;

        _transferService.ProgressChanged -= OnProgressChanged;
        _transferService.FileReceived -= OnFileReceived;
        _transferService.TransferError -= OnTransferError;
        _transferService.ApprovalRequested -= OnApprovalRequested;
        _connectionViewModel.PropertyChanged -= OnConnectionVmPropertyChanged;

        foreach (var tid in _sendCtsByItem.Keys.ToArray())
        {
            if (_sendCtsByItem.TryRemove(tid, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
                cts.Dispose();
            }
        }
    }
}
