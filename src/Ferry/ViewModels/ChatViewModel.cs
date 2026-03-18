using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// チャット画面の ViewModel。メッセージ送受信・ファイル転送のタイムライン表示を管理する。
/// </summary>
public sealed partial class ChatViewModel : ViewModelBase, IDisposable
{
    private readonly IChatService _chatService;
    private readonly IConnectionService _connectionService;
    private readonly ITransferService _transferService;
    private readonly ISettingsService _settingsService;
    private readonly ConnectionViewModel _connectionViewModel;

    /// <summary>ピアリストの再描画が必要なときに発火するイベント。</summary>
    public event Action? PeerListChanged;

    /// <summary>現在表示中の会話メッセージ。</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    /// <summary>送信前の添付ファイルパス。</summary>
    public ObservableCollection<string> AttachedFiles { get; } = [];

    /// <summary>添付ファイルがあるかどうか。</summary>
    [ObservableProperty]
    private bool _hasAttachedFiles;

    /// <summary>入力中のメッセージテキスト。</summary>
    [ObservableProperty]
    private string _messageText = string.Empty;

    /// <summary>選択中のピアID。</summary>
    [ObservableProperty]
    private string? _selectedPeerId;

    /// <summary>チャット表示中かどうか（ピア選択済みかつ設定モードでない）。</summary>
    [ObservableProperty]
    private bool _isChatVisible;

    public ChatViewModel(
        IChatService chatService,
        IConnectionService connectionService,
        ITransferService transferService,
        ISettingsService settingsService,
        ConnectionViewModel connectionViewModel)
    {
        _chatService = chatService;
        _connectionService = connectionService;
        _transferService = transferService;
        _settingsService = settingsService;
        _connectionViewModel = connectionViewModel;

        // チャットメッセージ受信
        _chatService.MessageReceived += OnMessageReceived;
        _chatService.MessageDelivered += OnMessageDelivered;

        // メッセージ操作イベント（リモートピアからの通知）
        _chatService.MessageDeleted += OnRemoteDeleted;
        _chatService.MessageEdited += OnRemoteEdited;
        _chatService.ReactionReceived += OnRemoteReaction;

        // 添付ファイル変更を監視
        AttachedFiles.CollectionChanged += (_, _) => HasAttachedFiles = AttachedFiles.Count > 0;

        // ファイル転送イベント → チャットタイムラインに統合
        _transferService.ApprovalRequested += OnApprovalRequested;
        _transferService.ProgressChanged += OnFileProgressChanged;
        _transferService.FileReceived += OnFileReceived;
        _transferService.TransferError += OnFileTransferError;
    }

    /// <summary>リモートからメッセージ削除通知を受けたときのイベント。</summary>
    public event EventHandler<Guid>? OnRemoteMessageDeleted;
    /// <summary>リモートからメッセージ編集通知を受けたときのイベント。</summary>
    public event EventHandler<(Guid MessageId, string NewText)>? OnRemoteMessageEdited;
    /// <summary>リモートからリアクション通知を受けたときのイベント。</summary>
    public event EventHandler<(Guid MessageId, string Emoji, string SenderName)>? OnRemoteReactionReceived;

    /// <summary>ピア選択時にチャット履歴を読み込む。</summary>
    public async Task LoadChatAsync(string peerId)
    {
        SelectedPeerId = peerId;
        Messages.Clear();

        var history = await _chatService.LoadHistoryAsync(peerId);
        foreach (var msg in history)
            Messages.Add(msg);

#if DEBUG
        // テストデータ（履歴が空の場合のみ）
        if (Messages.Count == 0)
        {
            Messages.Add(new ChatMessage { PeerId = peerId, SenderDeviceId = "test", Type = ChatMessageType.Text, Text = "こんにちは！Ferryのテストメッセージです", IsFromMe = true, State = ChatMessageState.Sent });
            Messages.Add(new ChatMessage { PeerId = peerId, SenderDeviceId = "peer", Type = ChatMessageType.Text, Text = "お、チャット機能いい感じだね！", IsFromMe = false, State = ChatMessageState.Delivered });
            Messages.Add(new ChatMessage { PeerId = peerId, SenderDeviceId = "test", Type = ChatMessageType.File, FileName = "screenshot.png", FileSize = 1_234_567, IsFromMe = true, State = ChatMessageState.Completed, FileProgress = 1.0 });
            Messages.Add(new ChatMessage { PeerId = peerId, SenderDeviceId = "peer", Type = ChatMessageType.File, FileName = "document.pdf", FileSize = 5_678_901, IsFromMe = false, State = ChatMessageState.WaitingApproval });
            Messages.Add(new ChatMessage { PeerId = peerId, SenderDeviceId = "test", Type = ChatMessageType.Text, Text = "ファイル送ったよ！確認してね 📦", IsFromMe = true, State = ChatMessageState.Delivered });
            Messages.Add(new ChatMessage { PeerId = peerId, SenderDeviceId = "peer", Type = ChatMessageType.Text, Text = "ありがとう！今から確認する", IsFromMe = false, State = ChatMessageState.Delivered });
        }
#endif

        IsChatVisible = true;

        // 未読カウントをリセット
        var peer = _connectionViewModel.PairedPeers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer != null)
        {
            peer.UnreadCount = 0;
            peer.HasIncomingFile = false;
        }
    }

    /// <summary>検索用にピアの履歴を読み込む（UI の Messages コレクションに影響しない）。</summary>
    public async Task<List<ChatMessage>> LoadHistoryForSearchAsync(string peerId)
    {
        return await _chatService.LoadHistoryAsync(peerId);
    }

    /// <summary>テキストメッセージを送信する。</summary>
    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var hasText = !string.IsNullOrWhiteSpace(MessageText);
        var hasFiles = AttachedFiles.Count > 0;

        if ((!hasText && !hasFiles) || SelectedPeerId == null)
        {
            Util.Logger.Log($"メッセージ送信スキップ: hasText={hasText}, hasFiles={hasFiles}, peerId={SelectedPeerId ?? "null"}", Util.LogLevel.Warning);
            return;
        }

        var text = MessageText.Trim();
        MessageText = string.Empty;
        var filesToSend = AttachedFiles.ToArray();
        AttachedFiles.Clear();

        // 未接続ならオンデマンド接続
        if (_connectionService.State != PeerState.Connected)
        {
            try { await _connectionViewModel.ConnectToSelectedPeerAsync(); }
            catch (Exception ex)
            {
                Util.Logger.Log($"メッセージ送信前の接続失敗: {ex.Message}", Util.LogLevel.Error);
                return;
            }
        }

        // テキストメッセージ送信
        if (hasText)
        {
            var message = new ChatMessage
            {
                PeerId = SelectedPeerId,
                SenderDeviceId = _settingsService.Settings.DeviceId,
                Type = ChatMessageType.Text,
                Text = text,
                IsFromMe = true,
                State = ChatMessageState.Sending,
            };
            Messages.Add(message);

            try
            {
                await _chatService.SendMessageAsync(SelectedPeerId, text);
                message.State = ChatMessageState.Sent;
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"メッセージ送信失敗: {ex.Message}", Util.LogLevel.Error);
                message.State = ChatMessageState.Failed;
            }

            // 送信結果を履歴に永続化
            await _chatService.AppendMessageAsync(message);
            UpdatePeerPreview(SelectedPeerId, $"あなた: {text}");
        }

        // 添付ファイル送信
        if (filesToSend.Length > 0)
            await SendFilesViaChatAsync(filesToSend);
    }

    /// <summary>添付ファイルを入力エリアに追加する（D&Dやファイルピッカーから）。</summary>
    public void AddAttachedFiles(string[] paths)
    {
        foreach (var path in paths)
        {
            if (!AttachedFiles.Contains(path) && (System.IO.File.Exists(path) || System.IO.Directory.Exists(path)))
                AttachedFiles.Add(path);
        }
    }

    /// <summary>添付ファイルを削除する。</summary>
    [RelayCommand]
    private void RemoveAttachedFile(string path) => AttachedFiles.Remove(path);

    /// <summary>ファイル添付（ファイルピッカー → 入力エリアに追加）。</summary>
    [RelayCommand]
    private async Task AttachFileAsync()
    {
        if (SelectedPeerId == null) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } mainWindow)
            return;

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = App.Text("Chat.AttachFile"),
        });

        if (files.Count == 0) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null)
            .Cast<string>()
            .ToArray();

        AddAttachedFiles(paths);
    }

    /// <summary>ファイルをチャット経由で送信する。</summary>
    public async Task SendFilesViaChatAsync(string[] filePaths)
    {
        if (SelectedPeerId == null || _connectionViewModel.SelectedPeer == null) return;

        // 未接続ならオンデマンド接続
        if (_connectionService.State != PeerState.Connected)
        {
            try { await _connectionViewModel.ConnectToSelectedPeerAsync(); }
            catch (Exception ex)
            {
                Util.Logger.Log($"ファイル送信前の接続失敗: {ex.Message}", Util.LogLevel.Error);
                return;
            }
        }

        foreach (var path in filePaths)
        {
            if (!System.IO.File.Exists(path)) continue;

            var fileInfo = new System.IO.FileInfo(path);
            var chatMsg = new ChatMessage
            {
                PeerId = SelectedPeerId,
                SenderDeviceId = _settingsService.Settings.DeviceId,
                Type = ChatMessageType.File,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                FilePath = path,
                IsFromMe = true,
                State = ChatMessageState.Transferring,
            };
            Messages.Add(chatMsg);

            try
            {
                await _transferService.SendFileAsync(path);
                chatMsg.State = ChatMessageState.Completed;
                chatMsg.FileProgress = 1.0;
                UpdatePeerPreview(SelectedPeerId, $"📎 {fileInfo.Name}");
            }
            catch
            {
                chatMsg.State = ChatMessageState.Failed;
            }
        }
    }

    /// <summary>受信ファイルを承認する。</summary>
    [RelayCommand]
    private void ApproveFile(Guid transferId)
    {
        _transferService.ApproveTransfer(transferId.ToString());
        var msg = Messages.FirstOrDefault(m => m.TransferId == transferId);
        if (msg != null) msg.State = ChatMessageState.Transferring;
    }

    /// <summary>受信ファイルを拒否する。</summary>
    [RelayCommand]
    private void RejectFile(Guid transferId)
    {
        _transferService.RejectTransfer(transferId.ToString());
        var msg = Messages.FirstOrDefault(m => m.TransferId == transferId);
        if (msg != null) msg.State = ChatMessageState.Failed;
    }

    // --- イベントハンドラ ---

    private void OnMessageReceived(object? sender, ChatMessage e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.PeerId == SelectedPeerId)
            {
                Messages.Add(e);
            }
            else
            {
                // 別のピアからのメッセージ → 未読バッジ更新
                var peer = _connectionViewModel.PairedPeers.FirstOrDefault(p => p.PeerId == e.PeerId);
                if (peer != null) peer.UnreadCount++;
            }
            UpdatePeerPreview(e.PeerId, e.Text);
        });
    }

    private void OnMessageDelivered(object? sender, Guid messageId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.MessageId == messageId);
            if (msg != null) msg.State = ChatMessageState.Delivered;
        });
    }

    private void OnApprovalRequested(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var peerId = _connectionViewModel.SelectedPeer?.PeerId ?? string.Empty;
            var chatMsg = new ChatMessage
            {
                PeerId = peerId,
                SenderDeviceId = string.Empty,
                Type = ChatMessageType.File,
                FileName = e.FileName,
                FileSize = e.FileSize,
                TransferId = e.TransferId,
                IsFromMe = false,
                State = ChatMessageState.WaitingApproval,
            };

            if (peerId == SelectedPeerId)
            {
                Messages.Add(chatMsg);
            }

            // サイドバーに📦アイコン表示
            var peer = _connectionViewModel.PairedPeers.FirstOrDefault(p => p.PeerId == peerId);
            if (peer != null)
            {
                peer.HasIncomingFile = true;
                if (peerId != SelectedPeerId) peer.UnreadCount++;
            }

            UpdatePeerPreview(peerId, $"📦 {e.FileName}");
        });
    }

    private void OnFileProgressChanged(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var msg = Messages.LastOrDefault(m =>
                m.Type == ChatMessageType.File &&
                m.State == ChatMessageState.Transferring);
            if (msg != null && e.FileSize > 0)
            {
                msg.FileProgress = (double)e.TransferredBytes / e.FileSize;
            }
        });
    }

    private void OnFileReceived(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.TransferId == e.TransferId)
                      ?? Messages.LastOrDefault(m =>
                          m.Type == ChatMessageType.File &&
                          m.State == ChatMessageState.Transferring &&
                          !m.IsFromMe);
            if (msg != null)
            {
                msg.State = ChatMessageState.Completed;
                msg.FileProgress = 1.0;
                msg.FilePath = e.SavedFilePath;
            }
        });
    }

    private void OnFileTransferError(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var msg = Messages.LastOrDefault(m =>
                m.Type == ChatMessageType.File &&
                m.State == ChatMessageState.Transferring);
            if (msg != null)
            {
                msg.State = ChatMessageState.Failed;
            }
        });
    }

    private void UpdatePeerPreview(string peerId, string preview)
    {
        var peer = _connectionViewModel.PairedPeers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer != null)
        {
            peer.LastMessagePreview = preview;
            peer.LastMessageAt = DateTime.UtcNow;
            PeerListChanged?.Invoke();
        }
    }

    // --- メッセージ操作パブリックメソッド ---

    public async Task DeleteMessageAsync(Guid messageId)
    {
        if (SelectedPeerId == null) return;
        await _chatService.SendDeleteMessageAsync(SelectedPeerId, messageId);
        var msg = Messages.FirstOrDefault(m => m.MessageId == messageId);
        if (msg != null) { msg.IsDeleted = true; msg.Text = string.Empty; }
    }

    public async Task EditMessageAsync(Guid messageId, string newText)
    {
        if (SelectedPeerId == null) return;
        await _chatService.SendEditMessageAsync(SelectedPeerId, messageId, newText);
        var msg = Messages.FirstOrDefault(m => m.MessageId == messageId);
        if (msg != null) { msg.Text = newText; msg.IsEdited = true; }
    }

    public async Task SendReactionAsync(Guid messageId, string emoji)
    {
        if (SelectedPeerId == null) return;
        await _chatService.SendReactionAsync(SelectedPeerId, messageId, emoji);
    }

    public async Task SendReplyAsync(string text, Guid replyToId, string replyToText)
    {
        if (SelectedPeerId == null) return;
        if (_connectionService.State != PeerState.Connected)
        {
            try { await _connectionViewModel.ConnectToSelectedPeerAsync(); }
            catch (Exception ex) { Util.Logger.Log($"リプライ送信前の接続失敗: {ex.Message}", Util.LogLevel.Error); return; }
        }
        await _chatService.SendReplyMessageAsync(SelectedPeerId, text, replyToId, replyToText);
    }

    public async Task RetryMessageAsync(Guid messageId)
    {
        if (SelectedPeerId == null) return;
        var msg = Messages.FirstOrDefault(m => m.MessageId == messageId);
        if (msg?.Text == null) return;
        if (_connectionService.State != PeerState.Connected)
            await _connectionViewModel.ConnectToSelectedPeerAsync();
        try { await _chatService.SendMessageAsync(SelectedPeerId, msg.Text); msg.State = ChatMessageState.Sent; }
        catch { msg.State = ChatMessageState.Failed; }
    }

    // --- リモートからのメッセージ操作イベントハンドラ ---

    private void OnRemoteDeleted(object? sender, Guid messageId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.MessageId == messageId);
            if (msg != null) { msg.IsDeleted = true; msg.Text = string.Empty; }
            OnRemoteMessageDeleted?.Invoke(this, messageId);
        });
    }

    private void OnRemoteEdited(object? sender, (Guid MessageId, string NewText) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.MessageId == e.MessageId);
            if (msg != null) { msg.Text = e.NewText; msg.IsEdited = true; }
            OnRemoteMessageEdited?.Invoke(this, e);
        });
    }

    private void OnRemoteReaction(object? sender, (Guid MessageId, string Emoji, string SenderName) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnRemoteReactionReceived?.Invoke(this, e);
        });
    }

    public void Dispose()
    {
        _chatService.MessageReceived -= OnMessageReceived;
        _chatService.MessageDelivered -= OnMessageDelivered;
        _chatService.MessageDeleted -= OnRemoteDeleted;
        _chatService.MessageEdited -= OnRemoteEdited;
        _chatService.ReactionReceived -= OnRemoteReaction;
        _transferService.ApprovalRequested -= OnApprovalRequested;
        _transferService.ProgressChanged -= OnFileProgressChanged;
        _transferService.FileReceived -= OnFileReceived;
        _transferService.TransferError -= OnFileTransferError;
    }
}
