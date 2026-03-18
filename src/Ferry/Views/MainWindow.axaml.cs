using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaWebView;
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
    private WebView? _webView;

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
            using var doc = JsonDocument.Parse(json);
            var action = doc.RootElement.GetProperty("action").GetString();
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;

            Util.Logger.Log($"JS→C#: {action}", Util.LogLevel.Debug);

            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                switch (action)
                {
                    case "ready":
                        await OnUiReady();
                        break;
                    case "selectPeer":
                        await OnSelectPeer(data.GetString()!);
                        break;
                    case "sendMessage":
                        await OnSendMessage(data.GetString()!);
                        break;
                    case "attachFile":
                        await OnAttachFile();
                        break;
                    case "approveFile":
                        OnApproveFile(data.GetString()!);
                        break;
                    case "rejectFile":
                        OnRejectFile(data.GetString()!);
                        break;
                    case "addMember":
                        await OnAddMember();
                        break;
                    case "toggleSettings":
                        SendToJs("showView", "settings");
                        await SendSettingsToJs();
                        break;
                    case "saveSetting":
                        OnSaveSetting(data);
                        break;
                    case "checkUpdate":
                        if (Avalonia.Application.Current is App app)
                            app.Check4Update(true);
                        break;
                    case "removePeer":
                        if (ConnectionVm != null)
                            await ConnectionVm.RemovePeerCommand.ExecuteAsync(data.GetString()!);
                        await SendPeersToJs();
                        break;
                    case "showChat":
                        SendToJs("showView", "chat");
                        break;
                    case "browseSaveDir":
                        await OnBrowseSaveDirectory();
                        break;
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

        // ロケール送信
        await SendLocaleToJs();

        // ピアリスト送信
        await SendPeersToJs();

        // 設定送信
        await SendSettingsToJs();

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
        if (ChatVm == null || string.IsNullOrWhiteSpace(text)) return;
        ChatVm.MessageText = text;
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

    // === データ送信ヘルパー ===

    private Task SendPeersToJs()
    {
        if (ConnectionVm == null) return Task.CompletedTask;
        var peers = ConnectionVm.PairedPeers.Select(p => new
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
            "Transfer.Approve", "Transfer.Reject",
            "State.Sending", "State.Sent", "State.Completed", "State.Error",
            "State.WaitingApproval", "State.Receiving",
            "Connection.RemovePeer",
            "Pairing.LinkLabel",
        };

        var texts = keys.ToDictionary(k => k, k => App.Text(k));
        SendToJsAsync("loadTexts", new { locale, texts });
        return Task.CompletedTask;
    }

    // === サービスイベント → JS 通知 ===

    private void SubscribeToServiceEvents()
    {
        if (ChatVm == null) return;

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
                });

                // ファイル転送のプログレス/状態変更を軽量にプッシュ
                if (msg.Type == ChatMessageType.File)
                {
                    msg.PropertyChanged += (_, pe) =>
                    {
                        var id = msg.MessageId.ToString();
                        if (pe.PropertyName == nameof(msg.FileProgress))
                            SendToJsAsync("updateProgress", new { id, progress = msg.FileProgress });
                        else if (pe.PropertyName == nameof(msg.State))
                            SendToJsAsync("updateState", new { id, state = msg.State.ToString() });
                    };
                }
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
        if (s?.WindowWidth > 0 && s?.WindowHeight > 0)
        {
            Width = s.WindowWidth!.Value;
            Height = s.WindowHeight!.Value;
        }
        if (s?.WindowLeft != null && s?.WindowTop != null)
            Position = new PixelPoint((int)s.WindowLeft.Value, (int)s.WindowTop.Value);
    }

    private void SaveWindowPosition()
    {
        if (_settingsService == null || WindowState != WindowState.Normal) return;
        var s = _settingsService.Settings;
        s.WindowLeft = Position.X;
        s.WindowTop = Position.Y;
        s.WindowWidth = Width;
        s.WindowHeight = Height;
        _ = _settingsService.SaveAsync();
    }

    private void OnPositionOrSizeChanged(object? sender, EventArgs e) => SaveWindowPosition();

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

    // === HTML インライン化 ===

    /// <summary>
    /// index.html 内の &lt;link rel="stylesheet"&gt; と &lt;script src&gt; を
    /// インラインの &lt;style&gt; と &lt;script&gt; に展開する。
    /// HtmlContent プロパティは about:blank で読み込むため相対パスが使えない。
    /// </summary>
    private static string BuildInlinedHtml(string indexPath)
    {
        var baseDir = Path.GetDirectoryName(indexPath)!;
        var html = File.ReadAllText(indexPath);

        // <link rel="stylesheet" href="xxx"> → <style>...</style>
        html = System.Text.RegularExpressions.Regex.Replace(html,
            @"<link\s+rel=""stylesheet""\s+href=""([^""]+)""\s*/?>",
            match =>
            {
                var cssPath = Path.Combine(baseDir, match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(cssPath))
                    return $"<style>\n{File.ReadAllText(cssPath)}\n</style>";
                return match.Value;
            });

        // <script src="xxx"></script> → <script>...</script>
        html = System.Text.RegularExpressions.Regex.Replace(html,
            @"<script\s+src=""([^""]+)""\s*>\s*</script>",
            match =>
            {
                var jsPath = Path.Combine(baseDir, match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(jsPath))
                    return $"<script>\n{File.ReadAllText(jsPath)}\n</script>";
                return match.Value;
            });

        return html;
    }
}
