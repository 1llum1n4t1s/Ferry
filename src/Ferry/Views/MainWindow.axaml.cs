using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaWebView;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using Ferry.ViewModels;
using WebViewCore.Events;

namespace Ferry.Views;

/// <summary>
/// メインウィンドウ。全画面 WebView で SPA UI を表示し、C#↔JS ブリッジで通信する。
/// </summary>
public partial class MainWindow : Window
{
    private Border? _dropOverlay;
    private ISettingsService? _settingsService;
    private INotificationService? _notificationService;
    private WebView? _webView;

    // ドロップファイルのチャンク受信用
    private readonly Dictionary<string, (string path, FileStream stream, string name)> _dropFiles = [];

    // ViewModel 群（ビジネスロジックの橋渡し）
    private MainWindowViewModel? _mainVm;
    private ConnectionViewModel? ConnectionVm => _mainVm?.Connection;
    private ChatViewModel? ChatVm => _mainVm?.Chat;
    private SettingsViewModel? SettingsVm => _mainVm?.Settings;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        PositionChanged += OnPositionOrSizeChanged;

        _dropOverlay = this.FindControl<Border>("DropOverlay");
        _webView = this.FindControl<WebView>("MainWebView");

        // WebView イベント
        if (_webView != null)
        {
            Util.Logger.Log("WebView コントロール検出、イベント登録中...");
            _webView.WebViewCreated += OnWebViewCreated;
            _webView.WebMessageReceived += OnWebMessageReceived;
            _webView.NavigationCompleted += (_, args) =>
                Util.Logger.Log($"NavigationCompleted: isSuccess={args.IsSuccess}");
        }
        else
        {
            Util.Logger.Log("WebView コントロールが見つかりません！", Util.LogLevel.Error);
        }

        // ドラッグ＆ドロップ
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);

        // 最小化→トレイ監視
        this.GetObservable(WindowStateProperty).Subscribe(new WindowStateObserver(state =>
        {
            if (state == WindowState.Minimized
                && _mainVm?.Settings?.MinimizeToTray == true)
            {
                ShowInTaskbar = false;
                Hide();
            }
        }));

        // 初期最小化起動
        Loaded += (_, _) =>
        {
            if (_mainVm?.Settings?.StartMinimized == true)
            {
                WindowState = WindowState.Minimized;
                if (_mainVm.Settings.MinimizeToTray)
                {
                    ShowInTaskbar = false;
                    Hide();
                }
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _mainVm = DataContext as MainWindowViewModel;
        SubscribeToServiceEvents();
    }

    public void SetSettingsService(ISettingsService settingsService) => _settingsService = settingsService;
    public void SetNotificationService(INotificationService notificationService) => _notificationService = notificationService;

    // === WebView 初期化 ===

    private void OnWebViewCreated(object? sender, WebViewCreatedEventArgs e)
    {
        if (!e.IsSucceed)
        {
            Util.Logger.Log($"WebView 作成失敗: {e.Message}", Util.LogLevel.Error);
            return;
        }

        Util.Logger.Log("WebView 作成成功、HTML を読み込み中...");

        // WebUI/index.html を読み込み（HtmlContent プロパティ経由、CSS/JS をインライン化）
        var indexPath = Path.Combine(AppContext.BaseDirectory, "WebUI", "index.html");
        if (File.Exists(indexPath))
        {
            var html = BuildInlinedHtml(indexPath);
            _webView!.HtmlContent = html;
        }
        else
        {
            Util.Logger.Log($"index.html が見つかりません: {indexPath}", Util.LogLevel.Error);
            _webView!.HtmlContent = "<html><body style='background:#161618;color:#F5F5F7;font-family:sans-serif;padding:40px'><h1>Ferry</h1><p>WebUI/index.html が見つかりません</p></body></html>";
        }
    }

    // === JS → C# メッセージ受信 ===

    private void OnWebMessageReceived(object? sender, WebViewMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.Message;

            // using var doc はこのメソッドの終了時に dispose されるため、
            // InvokeAsync のラムダ内で JsonElement を参照すると破壊される。
            // 必要な値を事前に文字列として取り出す。
            string? action;
            string? dataStr = null;
            string? settingJson = null;
            using (var doc = JsonDocument.Parse(json))
            {
                action = doc.RootElement.GetProperty("action").GetString();
                if (doc.RootElement.TryGetProperty("data", out var d))
                {
                    if (d.ValueKind == JsonValueKind.String)
                        dataStr = d.GetString();
                    else if (d.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        settingJson = d.GetRawText();
                }
            }

            Util.Logger.Log($"JS→C#: {action}", Util.LogLevel.Debug);

            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    switch (action)
                    {
                        case "ready":
                            await OnUiReady();
                            break;
                        case "selectPeer":
                            await OnSelectPeer(dataStr!);
                            break;
                        case "sendMessage":
                            await OnSendMessage(dataStr!);
                            break;
                        case "attachFile":
                            await OnAttachFile();
                            break;
                        case "approveFile":
                            OnApproveFile(dataStr!);
                            break;
                        case "rejectFile":
                            OnRejectFile(dataStr!);
                            break;
                        case "addMember":
                            await OnAddMember();
                            break;
                        case "toggleSettings":
                            SendToJs("showView", "settings");
                            await SendSettingsToJs();
                            break;
                        case "saveSetting":
                            if (settingJson != null)
                            {
                                using var settingDoc = JsonDocument.Parse(settingJson);
                                OnSaveSetting(settingDoc.RootElement);
                            }
                            break;
                        case "checkUpdate":
                            if (Avalonia.Application.Current is App app)
                                app.Check4Update(true);
                            break;
                        case "removePeer":
                            if (ConnectionVm != null)
                                await ConnectionVm.RemovePeerCommand.ExecuteAsync(dataStr!);
                            await SendPeersToJs();
                            break;
                        case "showChat":
                            SendToJs("showView", "chat");
                            break;
                        case "browseSaveDir":
                            await OnBrowseSaveDirectory();
                            break;
                        case "browseReceiveFileSavePath":
                            await OnBrowseReceiveFileSavePath();
                            break;
                        case "openFile":
                            OnOpenFile(dataStr!);
                            break;
                        case "openFolder":
                            OnOpenFolder(dataStr!);
                            break;
                        case "cancelTransfer":
                            OnCancelTransfer(dataStr!);
                            break;
                        case "pasteImage":
                            await OnPasteImage(dataStr!);
                            break;
                        case "searchMessages":
                            await OnSearchMessages(dataStr!);
                            break;
                        case "copyMessage":
                            await OnCopyMessage(dataStr!);
                            break;
                        case "deleteMessage":
                            await OnDeleteMessage(dataStr!);
                            break;
                        case "editMessage":
                            SendToJsAsync("showEditDialog", dataStr);
                            break;
                        case "submitEdit":
                            await OnSubmitEdit(dataStr!);
                            break;
                        case "replyMessage":
                            OnReplyMessage(dataStr!);
                            break;
                        case "sendReply":
                            await OnSendReply(dataStr!);
                            break;
                        case "reactMessage":
                            SendToJsAsync("showReactionPicker", dataStr);
                            break;
                        case "sendReaction":
                            await OnSendReaction(dataStr!);
                            break;
                        case "retryMessage":
                            await OnRetryMessage(dataStr!);
                            break;
                        case "dropFileStart":
                            OnDropFileStart(dataStr!);
                            break;
                        case "dropFileChunk":
                            OnDropFileChunk(dataStr!);
                            break;
                        case "dropFileEnd":
                            OnDropFileEnd(dataStr!);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"JS→C# アクション実行エラー ({action}): {ex}", Util.LogLevel.Error);
                }
            });
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"WebMessage 処理エラー: {ex.Message}", Util.LogLevel.Error);
        }
    }

    // === C# → JS メッセージ送信 ===

    private void SendToJs(string action, object? data = null)
    {
        if (_webView == null) return;
        var json = JsonSerializer.Serialize(new { action, data });
        // PostWebMessageAsString で送信
        _webView.PostWebMessageAsString(json, new Uri("about:blank"));
    }

    private async void SendToJsAsync(string action, object? data = null)
    {
        if (_webView == null) return;
        var json = JsonSerializer.Serialize(new { action, data });
        await _webView.ExecuteScriptAsync($"window.receiveBridgeMessage({EscapeJsString(json)})");
    }

    private static string EscapeJsString(string s)
    {
        return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r") + "'";
    }

    // === UI Ready — 初期データ送信 ===

    private async Task OnUiReady()
    {
        Util.Logger.Log("UI Ready — 初期データ送信");

        // ロケールを先に送信（他の UI 描画で翻訳キーが必要なため）
        await SendLocaleToJs();

        // ピアリスト・設定を並列送信
        await Task.WhenAll(SendPeersToJs(), SendSettingsToJs());
        SendThemeToJs();

        // 最初のピアを自動選択
        if (ConnectionVm?.PairedPeers.Count > 0)
        {
            var first = ConnectionVm.PairedPeers[0];
            ConnectionVm.SelectedPeer = first;
            await OnSelectPeer(first.PeerId);
        }
    }

    // === アクションハンドラ ===

    private async Task OnSelectPeer(string peerId)
    {
        if (ConnectionVm == null || ChatVm == null) return;

        var peer = ConnectionVm.PairedPeers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer == null) return;

        ConnectionVm.SelectedPeer = peer;
        if (_mainVm != null) _mainVm.IsSettingsMode = false;

        await ChatVm.LoadChatAsync(peerId);

        // チャット履歴を JS に送信
        SendToJsAsync("showView", "chat");
        SendToJsAsync("loadHistory", ChatVm.Messages.Select(m => new
        {
            id = m.MessageId,
            type = m.Type.ToString().ToLowerInvariant(),
            text = m.Text,
            isFromMe = m.IsFromMe,
            state = m.State.ToString(),
            sentAt = m.SentAtText,
            fileName = m.FileName,
            fileSize = m.FileSizeText,
            fileProgress = m.FileProgress,
            transferId = m.TransferId?.ToString(),
            filePath = m.FilePath,
            thumbnailData = BuildThumbnailData(m),
        }).ToArray());

        SendToJsAsync("peerSelected", new
        {
            peerId = peer.PeerId,
            displayName = peer.DisplayName,
            isOnline = peer.IsOnline,
        });
    }

    private async Task OnSendMessage(string text)
    {
        if (ChatVm == null) return;
        ChatVm.MessageText = text ?? string.Empty;
        await ChatVm.SendMessageCommand.ExecuteAsync(null);
    }

    private async Task OnAttachFile()
    {
        if (ChatVm == null) return;
        await ChatVm.AttachFileCommand.ExecuteAsync(null);
    }

    private void OnApproveFile(string transferId)
    {
        if (Guid.TryParse(transferId, out var id))
            ChatVm?.ApproveFileCommand.Execute(id);
    }

    private void OnRejectFile(string transferId)
    {
        if (Guid.TryParse(transferId, out var id))
            ChatVm?.RejectFileCommand.Execute(id);
    }

    private async Task OnAddMember()
    {
        if (ConnectionVm == null || _mainVm == null) return;
        ConnectionVm.StartSessionCommand.Execute(null);
        var dialog = new AddMemberWindow { DataContext = ConnectionVm };
        await dialog.ShowDialog(this);
        await SendPeersToJs();
    }

    private void OnSaveSetting(JsonElement data)
    {
        if (SettingsVm == null) return;
        var key = data.GetProperty("key").GetString();
        var value = data.GetProperty("value");

        switch (key)
        {
            case "displayName":
                SettingsVm.DisplayName = value.GetString() ?? string.Empty;
                break;
            case "theme":
                SettingsVm.SelectedThemeIndex = value.GetInt32();
                break;
            case "locale":
                SettingsVm.SelectedLocale = value.GetString() ?? "en_US";
                _ = SendLocaleToJs();
                break;
            case "saveDirectory":
                SettingsVm.SaveDirectory = value.GetString() ?? string.Empty;
                break;
            case "runAtStartup":
                SettingsVm.RunAtStartup = value.GetBoolean();
                break;
            case "startMinimized":
                SettingsVm.StartMinimized = value.GetBoolean();
                break;
            case "minimizeToTray":
                SettingsVm.MinimizeToTray = value.GetBoolean();
                break;
            case "chatRetentionDays":
                SettingsVm.ChatHistoryRetentionDays = value.GetInt32();
                break;
            case "enableNotificationSound":
                SettingsVm.EnableNotificationSound = value.GetBoolean();
                break;
            case "autoAcceptFileTransfer":
                SettingsVm.AutoAcceptFileTransfer = value.GetBoolean();
                break;
            case "accentColor":
                SettingsVm.AccentColor = value.GetString() ?? "#007AFF";
                SendThemeToJs();
                break;
            case "fontSize":
                SettingsVm.FontSize = value.GetString() ?? "medium";
                SendThemeToJs();
                break;
            case "autoStartWithWindows":
                SettingsVm.AutoStartWithWindows = value.GetBoolean();
                break;
        }
    }

    private async Task OnBrowseSaveDirectory()
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "保存先フォルダを選択",
        });
        if (dirs.Count > 0)
        {
            var path = dirs[0].TryGetLocalPath();
            if (path != null && SettingsVm != null)
            {
                SettingsVm.SaveDirectory = path;
                await SendSettingsToJs();
            }
        }
    }

    private async Task OnBrowseReceiveFileSavePath()
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "受信ファイルの保存先フォルダを選択",
        });
        if (dirs.Count > 0)
        {
            var path = dirs[0].TryGetLocalPath();
            if (path != null && SettingsVm != null)
            {
                SettingsVm.ReceiveFileSavePath = path;
                await SendSettingsToJs();
            }
        }
    }

    // === メッセージ操作ハンドラ ===

    private async Task OnCopyMessage(string msgId)
    {
        var msg = ChatVm?.Messages.FirstOrDefault(m => m.MessageId.ToString() == msgId);
        if (msg?.Text == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(msg.Text);
    }

    private async Task OnDeleteMessage(string msgId)
    {
        if (!Guid.TryParse(msgId, out var id)) return;
        try { await ChatVm!.DeleteMessageAsync(id); SendToJsAsync("messageDeleted", msgId); }
        catch (Exception ex) { Util.Logger.Log($"メッセージ削除失敗: {ex.Message}", Util.LogLevel.Error); }
    }

    private async Task OnSubmitEdit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);
            var newText = doc.RootElement.GetProperty("newText").GetString()!;
            await ChatVm!.EditMessageAsync(id, newText);
            SendToJsAsync("messageEdited", new { id = id.ToString(), newText });
        }
        catch (Exception ex) { Util.Logger.Log($"メッセージ編集失敗: {ex.Message}", Util.LogLevel.Error); }
    }

    private void OnReplyMessage(string msgId)
    {
        var msg = ChatVm?.Messages.FirstOrDefault(m => m.MessageId.ToString() == msgId);
        if (msg == null) return;
        SendToJsAsync("showReplyBar", new { id = msgId, text = msg.Text ?? "", senderName = msg.IsFromMe ? "あなた" : (ConnectionVm?.SelectedPeer?.DisplayName ?? "") });
    }

    private async Task OnSendReply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("text").GetString()!;
            var replyToId = Guid.Parse(doc.RootElement.GetProperty("replyToId").GetString()!);
            var replyToText = doc.RootElement.GetProperty("replyToText").GetString() ?? "";
            await ChatVm!.SendReplyAsync(text, replyToId, replyToText);
        }
        catch (Exception ex) { Util.Logger.Log($"リプライ送信失敗: {ex.Message}", Util.LogLevel.Error); }
    }

    private async Task OnSendReaction(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var msgId = Guid.Parse(doc.RootElement.GetProperty("msgId").GetString()!);
            var emoji = doc.RootElement.GetProperty("emoji").GetString()!;
            await ChatVm!.SendReactionAsync(msgId, emoji);
            SendToJsAsync("reactionReceived", new { id = msgId.ToString(), emoji, senderName = "あなた" });
        }
        catch (Exception ex) { Util.Logger.Log($"リアクション送信失敗: {ex.Message}", Util.LogLevel.Error); }
    }

    private async Task OnRetryMessage(string msgId)
    {
        if (!Guid.TryParse(msgId, out var id)) return;
        try { await ChatVm!.RetryMessageAsync(id); }
        catch (Exception ex) { Util.Logger.Log($"メッセージ再送失敗: {ex.Message}", Util.LogLevel.Error); }
    }

    // === テーマ送信 ===

    private void SendThemeToJs()
    {
        if (SettingsVm == null) return;
        var theme = SettingsVm.Theme;
        if (string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase))
        {
            var app = Avalonia.Application.Current;
            theme = app?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "dark" : "light";
        }
        else
        {
            theme = theme.ToLowerInvariant();
        }
        SendToJsAsync("applyTheme", new
        {
            theme,
            accentColor = SettingsVm.AccentColor,
            fontSize = SettingsVm.FontSize,
        });
    }

    // === 全ピア横断メッセージ検索 ===

    private async Task OnSearchMessages(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || ConnectionVm == null || ChatVm == null) return;
        var results = new List<object>();
        foreach (var peer in ConnectionVm.PairedPeers)
        {
            var history = await ChatVm.LoadHistoryForSearchAsync(peer.PeerId);
            var matches = history
                .Where(m => m.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                .Select(m => new
                {
                    peerId = peer.PeerId,
                    peerName = peer.DisplayName,
                    text = m.Text,
                    sentAt = m.SentAtText,
                    messageId = m.MessageId.ToString(),
                });
            results.AddRange(matches);
        }
        SendToJsAsync("searchResults", results.Take(50).ToArray());
    }

    // === データ送信ヘルパー ===

    private Task SendPeersToJs()
    {
        if (ConnectionVm == null) return Task.CompletedTask;
        var peers = ConnectionVm.PairedPeers
            .OrderByDescending(p => p.LastMessageAt ?? DateTime.MinValue)
            .Select(p => new
            {
                peerId = p.PeerId,
                displayName = p.DisplayName,
                isOnline = p.IsOnline,
                unreadCount = p.UnreadCount,
                hasIncomingFile = p.HasIncomingFile,
                lastMessagePreview = p.LastMessagePreview,
                connectionStatusText = p.ConnectionStatusText,
                route = p.Route.ToString(),
            }).ToArray();
        SendToJsAsync("loadPeers", peers);
        return Task.CompletedTask;
    }

    private Task SendSettingsToJs()
    {
        if (SettingsVm == null) return Task.CompletedTask;
        SendToJsAsync("loadSettings", new
        {
            displayName = SettingsVm.DisplayName,
            selectedThemeIndex = SettingsVm.SelectedThemeIndex,
            selectedLocale = SettingsVm.SelectedLocale,
            saveDirectory = SettingsVm.SaveDirectory,
            runAtStartup = SettingsVm.RunAtStartup,
            startMinimized = SettingsVm.StartMinimized,
            minimizeToTray = SettingsVm.MinimizeToTray,
            chatRetentionDays = SettingsVm.ChatHistoryRetentionDays,
            versionText = SettingsVm.VersionText,
            localeOptions = App.LocaleOptions.Select(l => new { key = l.Key, displayName = l.DisplayName }).ToArray(),
            chatRetentionOptions = SettingsVm.ChatRetentionOptions,
            enableNotificationSound = SettingsVm.EnableNotificationSound,
            receiveFileSavePath = SettingsVm.ReceiveFileSavePath,
            autoAcceptFileTransfer = SettingsVm.AutoAcceptFileTransfer,
            theme = SettingsVm.Theme,
            accentColor = SettingsVm.AccentColor,
            fontSize = SettingsVm.FontSize,
            autoStartWithWindows = SettingsVm.AutoStartWithWindows,
        });
        return Task.CompletedTask;
    }

    private Task SendLocaleToJs()
    {
        // en_US のキーを全部送信（他言語も App.Text で取得）
        var locale = SettingsVm?.SelectedLocale ?? "en_US";
        var keys = new[]
        {
            "Tab.Members", "Tab.AddMember", "Tab.Settings",
            "Drop.Message", "Drop.Description",
            "Chat.Placeholder", "Chat.SelectMember", "Chat.AttachFile",
            "Settings.General", "Settings.DisplayName", "Settings.DisplayName.Placeholder",
            "Settings.Appearance", "Settings.Theme", "Settings.Theme.System", "Settings.Theme.Light", "Settings.Theme.Dark",
            "Settings.Language", "Settings.File", "Settings.SaveDirectory", "Settings.SaveDirectory.Browse",
            "Settings.Behavior", "Settings.RunAtStartup", "Settings.RunAtStartup.Desc",
            "Settings.StartMinimized", "Settings.StartMinimized.Desc",
            "Settings.MinimizeToTray", "Settings.MinimizeToTray.Desc",
            "Settings.Version", "Settings.CheckUpdate",
            "Settings.ChatRetention", "Settings.ChatRetention.Days", "Settings.ChatRetention.Unlimited",
            "Transfer.Approve", "Transfer.Reject", "Transfer.OpenFolder",
            "State.Sending", "State.Sent", "State.Completed", "State.Error",
            "State.WaitingApproval", "State.Receiving",
            "Connection.RemovePeer",
            "Pairing.LinkLabel",
            "Settings.Notification", "Settings.NotificationSound", "Settings.NotificationSound.Desc",
            "Settings.FileTransfer", "Settings.ReceiveFileSavePath", "Settings.ReceiveFileSavePath.Default",
            "Settings.AutoAcceptFile", "Settings.AutoAcceptFile.Desc",
            "Settings.AccentColor",
            "Settings.FontSize", "Settings.FontSize.Small", "Settings.FontSize.Medium", "Settings.FontSize.Large",
            "Settings.AutoStartWithWindows", "Settings.AutoStartWithWindows.Desc",
        };

        var texts = keys.ToDictionary(k => k, k => App.Text(k));
        SendToJsAsync("loadTexts", new { locale, texts });
        return Task.CompletedTask;
    }

    // === サービスイベント → JS 通知 ===

    private void SubscribeToServiceEvents()
    {
        if (ChatVm == null) return;

        ChatVm.PeerListChanged += () => Dispatcher.UIThread.Post(() => SendPeersToJs());
        ChatVm.OnRemoteMessageDeleted += (_, id) => SendToJsAsync("messageDeleted", id.ToString());
        ChatVm.OnRemoteMessageEdited += (_, e) => SendToJsAsync("messageEdited", new { id = e.MessageId.ToString(), newText = e.NewText });
        ChatVm.OnRemoteReactionReceived += (_, e) => SendToJsAsync("reactionReceived", new { id = e.MessageId.ToString(), emoji = e.Emoji, senderName = e.SenderName });
        ChatVm.AttachedFilesChanged += (names) => SendToJsAsync("filesAttached", names);

        ChatVm.AttachedFiles.CollectionChanged += (_, args) =>
        {
            if (ChatVm.AttachedFiles.Count == 0)
                SendToJsAsync("clearAttachments", null);
        };

        ChatVm.Messages.CollectionChanged += (_, args) =>
        {
            if (args.NewItems == null) return;
            foreach (ChatMessage msg in args.NewItems)
            {
                SendToJsAsync("newMessage", new
                {
                    id = msg.MessageId,
                    type = msg.Type.ToString().ToLowerInvariant(),
                    text = msg.Text,
                    isFromMe = msg.IsFromMe,
                    state = msg.State.ToString(),
                    sentAt = msg.SentAtText,
                    fileName = msg.FileName,
                    fileSize = msg.FileSizeText,
                    fileProgress = msg.FileProgress,
                    transferId = msg.TransferId?.ToString(),
                    filePath = msg.FilePath,
                    thumbnailData = BuildThumbnailData(msg),
                });

                // 受信メッセージ かつ ウィンドウが非アクティブなら通知を発火
                if (!msg.IsFromMe && !IsActive)
                {
                    var platformHandle = this.TryGetPlatformHandle();
                    var hwnd = platformHandle?.Handle ?? IntPtr.Zero;
                    WindowFlash.Flash(hwnd);

                    var senderName = ConnectionVm?.SelectedPeer?.DisplayName ?? string.Empty;
                    var preview = msg.Type == ChatMessageType.File ? msg.FileName ?? string.Empty : msg.Text;
                    _notificationService?.NotifyMessageReceived(msg.PeerId, senderName, preview);
                }

                // メッセージの状態変更を JS にプッシュ
                msg.PropertyChanged += (_, pe) =>
                {
                    var id = msg.MessageId.ToString();
                    if (pe.PropertyName == nameof(msg.FileProgress))
                        SendToJsAsync("updateProgress", new { id, progress = msg.FileProgress });
                    else if (pe.PropertyName == nameof(msg.State))
                        SendToJsAsync("updateState", new
                        {
                            id,
                            state = msg.State.ToString(),
                            filePath = msg.FilePath,
                            thumbnailData = BuildThumbnailData(msg),
                        });
                };
            }
        };

        // ピアのオンライン状態変更を監視
        if (ConnectionVm != null)
        {
            ConnectionVm.PairedPeers.CollectionChanged += (_, _) =>
            {
                _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => SendPeersToJs());
            };
        }
    }

    // === ウィンドウ位置 ===

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RestoreWindowPosition();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            SaveWindowPosition();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => SaveWindowPosition();

    private sealed class WindowStateObserver(Action<WindowState> onNext) : IObserver<WindowState>
    {
        public void OnNext(WindowState value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private void RestoreWindowPosition()
    {
        var s = _settingsService?.Settings;
        if (s == null) return;

        if (s.WindowWidth > 0 && s.WindowHeight > 0)
        {
            Width = s.WindowWidth!.Value;
            Height = s.WindowHeight!.Value;
        }

        if (s.WindowLeft != null && s.WindowTop != null)
        {
            Position = new PixelPoint((int)s.WindowLeft.Value, (int)s.WindowTop.Value);
        }
        else if (!double.IsNaN(s.WindowX) && !double.IsNaN(s.WindowY))
        {
            Position = new PixelPoint((int)s.WindowX, (int)s.WindowY);
        }

        if (s.IsWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowPosition()
    {
        if (_settingsService == null) return;
        var s = _settingsService.Settings;

        s.IsWindowMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            s.WindowLeft = Position.X;
            s.WindowTop = Position.Y;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
        }

        _ = _settingsService.SaveAsync();
    }

    private void OnPositionOrSizeChanged(object? sender, EventArgs e) => SaveWindowPosition();

    // === ファイル操作アクション ===

    private void OnOpenFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { Util.Logger.Log($"ファイルを開けませんでした: {ex.Message}", Util.LogLevel.Error); }
    }

    private void OnOpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Util.Logger.Log($"フォルダを開けませんでした: {ex.Message}", Util.LogLevel.Error); }
    }

    private void OnCancelTransfer(string transferId)
    {
        if (string.IsNullOrEmpty(transferId)) return;
        if (Avalonia.Application.Current is App app)
            app.TransferService?.CancelTransfer(transferId);
        var msg = ChatVm?.Messages.FirstOrDefault(m => m.TransferId?.ToString() == transferId);
        if (msg != null) msg.State = ChatMessageState.Failed;
    }

    private async Task OnPasteImage(string json)
    {
        if (ChatVm == null) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var base64Data = doc.RootElement.GetProperty("data").GetString();
            var fileName = doc.RootElement.GetProperty("name").GetString() ?? "clipboard-image.png";
            if (string.IsNullOrEmpty(base64Data)) return;

            var base64 = base64Data.Contains(',') ? base64Data[(base64Data.IndexOf(',') + 1)..] : base64Data;
            var bytes = Convert.FromBase64String(base64);
            var tempDir = Path.Combine(Path.GetTempPath(), "Ferry");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

            var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}_{fileName}");
            await File.WriteAllBytesAsync(tempPath, bytes);
            ChatVm.AddAttachedFiles([tempPath]);
            SendToJsAsync("filesAttached", new[] { fileName });
        }
        catch (Exception ex) { Util.Logger.Log($"クリップボード画像処理エラー: {ex.Message}", Util.LogLevel.Error); }
    }

    // === 画像サムネイル ===

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];

    private static string? BuildThumbnailData(ChatMessage msg)
    {
        if (msg.Type != ChatMessageType.File || string.IsNullOrEmpty(msg.FilePath)) return null;
        var ext = Path.GetExtension(msg.FilePath).ToLowerInvariant();
        if (!Array.Exists(ImageExtensions, e => e == ext)) return null;
        if (!File.Exists(msg.FilePath)) return null;
        try
        {
            var bytes = File.ReadAllBytes(msg.FilePath);
            var mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/png",
            };
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }

    // === ドラッグ＆ドロップ ===

    private bool HasFiles(DragEventArgs e)
    {
        try { if (e.DataTransfer.Contains(DataFormat.File)) return true; }
        catch { }
        try { var files = e.DataTransfer.TryGetFiles(); return files != null && files.Any(); }
        catch { return false; }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasFiles(e) && _dropOverlay != null)
        {
            _dropOverlay.IsVisible = true;
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_dropOverlay != null) _dropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dropOverlay != null) _dropOverlay.IsVisible = false;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToArray();

        if (paths.Length > 0 && ChatVm?.IsChatVisible == true && ChatVm.SelectedPeerId != null)
        {
            ChatVm.AddAttachedFiles(paths);
            // JS に添付ファイル通知
            SendToJsAsync("filesAttached", paths.Select(p => Path.GetFileName(p)).ToArray());
        }

        e.Handled = true;
    }

    // === WebView からのファイルドロップ受信（チャンク転送） ===

    private void OnDropFileStart(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var name = doc.RootElement.GetProperty("name").GetString()!;

            var tempDir = Path.Combine(Path.GetTempPath(), "Ferry", "drops");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"{id}_{name}");

            var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            _dropFiles[id] = (tempPath, stream, name);
            Util.Logger.Log($"ドロップファイル受信開始: {name}", Util.LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ドロップファイル開始エラー: {ex.Message}", Util.LogLevel.Error);
        }
    }

    private void OnDropFileChunk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var base64 = doc.RootElement.GetProperty("data").GetString()!;

            if (_dropFiles.TryGetValue(id, out var entry))
            {
                var bytes = Convert.FromBase64String(base64);
                entry.stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ドロップファイルチャンクエラー: {ex.Message}", Util.LogLevel.Error);
        }
    }

    private void OnDropFileEnd(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString()!;

            if (_dropFiles.TryGetValue(id, out var entry))
            {
                entry.stream.Flush();
                entry.stream.Dispose();
                _dropFiles.Remove(id);

                Util.Logger.Log($"ドロップファイル受信完了: {entry.name} → {entry.path}");

                if (ChatVm?.IsChatVisible == true && ChatVm.SelectedPeerId != null)
                {
                    ChatVm.AddAttachedFiles([entry.path]);
                    SendToJsAsync("filesAttached", new[] { entry.name });
                }
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ドロップファイル完了エラー: {ex.Message}", Util.LogLevel.Error);
        }
    }

    // === HTML インライン化 ===

    /// <summary>
    /// index.html 内の &lt;link rel="stylesheet"&gt; と &lt;script src&gt; を
    /// インラインの &lt;style&gt; と &lt;script&gt; に展開する。
    /// HtmlContent プロパティは about:blank で読み込むため相対パスが使えない。
    /// </summary>
    // 事前コンパイル済み Regex（毎回のコンパイルコストを回避）
    private static readonly System.Text.RegularExpressions.Regex StyleLinkRegex = new(
        @"<link\s+rel=""stylesheet""\s+href=""([^""]+)""\s*/?>",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ScriptSrcRegex = new(
        @"<script\s+src=""([^""]+)""\s*>\s*</script>",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string BuildInlinedHtml(string indexPath)
    {
        var baseDir = Path.GetDirectoryName(indexPath)!;
        var html = File.ReadAllText(indexPath);

        // <link rel="stylesheet" href="xxx"> → <style>...</style>
        html = StyleLinkRegex.Replace(html, match =>
        {
            var cssPath = Path.Combine(baseDir, match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(cssPath))
                return $"<style>\n{File.ReadAllText(cssPath)}\n</style>";
            return match.Value;
        });

        // <script src="xxx"></script> → <script>...</script>
        html = ScriptSrcRegex.Replace(html, match =>
        {
            var jsPath = Path.Combine(baseDir, match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(jsPath))
                return $"<script>\n{File.ReadAllText(jsPath)}\n</script>";
            return match.Value;
        });

        return html;
    }
}
