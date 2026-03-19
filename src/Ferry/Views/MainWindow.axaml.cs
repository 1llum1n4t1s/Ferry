using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using Ferry.ViewModels;

namespace Ferry.Views;

/// <summary>
/// メインウィンドウ。2カラムレイアウト（サイドバー + チャット/設定）を管理する。
/// </summary>
public partial class MainWindow : Window
{
    private Border? _dropOverlay;
    private ISettingsService? _settingsService;
    private INotificationService? _notificationService;

    /// <summary>ウィンドウ位置保存のデバウンスタイマー（500ms）。</summary>
    private System.Threading.Timer? _savePositionDebounceTimer;

    private MainWindowViewModel? _mainVm;
    private ConnectionViewModel? ConnectionVm => _mainVm?.Connection;
    private ChatViewModel? ChatVm => _mainVm?.Chat;

    // イベント重複登録防止用: 前回購読した ViewModel の参照を保持
    private ConnectionViewModel? _subscribedConnectionVm;
    private ChatViewModel? _subscribedChatVm;
    private MainWindowViewModel? _subscribedMainVm;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        PositionChanged += OnPositionOrSizeChanged;

        _dropOverlay = this.FindControl<Border>("DropOverlay");

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
        SubscribeToEvents();
    }

    public void SetSettingsService(ISettingsService settingsService) => _settingsService = settingsService;
    public void SetNotificationService(INotificationService notificationService) => _notificationService = notificationService;

    // === イベント購読 ===

    private void SubscribeToEvents()
    {
        if (_mainVm == null) return;

        // 前回のハンドラを解除
        if (_subscribedConnectionVm != null)
        {
            _subscribedConnectionVm.PropertyChanged -= OnConnectionVmPropertyChanged;
        }
        if (_subscribedChatVm != null)
        {
            _subscribedChatVm.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        }
        if (_subscribedMainVm != null)
        {
            _subscribedMainVm.PropertyChanged -= OnMainVmPropertyChangedForEmptyView;
        }

        // SelectedPeer 変更 → チャット読み込み + ピア名更新
        if (ConnectionVm != null)
        {
            ConnectionVm.PropertyChanged += OnConnectionVmPropertyChanged;
            _subscribedConnectionVm = ConnectionVm;

            // AddNewPeerCommand 実行後に AddMemberWindow ダイアログを表示
            ConnectionVm.AddNewPeerCommand.PropertyChanged += async (_, pe) =>
            {
                if (pe.PropertyName == nameof(ConnectionVm.AddNewPeerCommand.IsRunning)
                    && !ConnectionVm.AddNewPeerCommand.IsRunning)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var dialog = new AddMemberWindow { DataContext = ConnectionVm };
                        await dialog.ShowDialog(this);
                    });
                }
            };
        }

        // 受信メッセージの通知処理
        if (ChatVm != null)
        {
            ChatVm.Messages.CollectionChanged += OnMessagesCollectionChanged;
            _subscribedChatVm = ChatVm;
        }

        // IsSettingsMode 変更時の空ビュー更新（名前付きメソッドで一度だけ登録）
        _mainVm.PropertyChanged += OnMainVmPropertyChangedForEmptyView;
        _subscribedMainVm = _mainVm;

        // 空ビューの表示制御を更新
        UpdateEmptyViewVisibility();
    }

    private void OnConnectionVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.SelectedPeer))
        {
            var peer = ConnectionVm?.SelectedPeer;
            if (peer != null && ChatVm != null)
            {
                // 設定モードを解除
                if (_mainVm != null) _mainVm.IsSettingsMode = false;

                // チャット履歴読み込み
                _ = ChatVm.LoadChatAsync(peer.PeerId);

                // ピア名をバインディング経由で設定
                ChatVm.PeerDisplayName = peer.DisplayName;
            }
            UpdateEmptyViewVisibility();
        }
    }

    private void OnMessagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;

        foreach (ChatMessage msg in e.NewItems)
        {
            // 受信メッセージ かつ ウィンドウが非アクティブなら通知
            if (!msg.IsFromMe && !IsActive)
            {
                var platformHandle = this.TryGetPlatformHandle();
                var hwnd = platformHandle?.Handle ?? IntPtr.Zero;
                WindowFlash.Flash(hwnd);

                var senderName = ConnectionVm?.SelectedPeer?.DisplayName ?? string.Empty;
                var preview = msg.Type == ChatMessageType.File ? msg.FileName ?? string.Empty : msg.Text;
                _notificationService?.NotifyMessageReceived(msg.PeerId, senderName, preview);
            }
        }
    }

    private void UpdateEmptyViewVisibility()
    {
        var emptyView = this.FindControl<Border>("EmptyView");
        if (emptyView != null && _mainVm != null)
        {
            emptyView.IsVisible = !_mainVm.IsSettingsMode && ConnectionVm?.SelectedPeer == null;
        }
    }

    /// <summary>IsSettingsMode 変更時に空ビューの表示を更新する（重複登録防止のため名前付きメソッド化）。</summary>
    private void OnMainVmPropertyChangedForEmptyView(object? sender, PropertyChangedEventArgs pe)
    {
        if (pe.PropertyName == nameof(MainWindowViewModel.IsSettingsMode))
        {
            var emptyView = this.FindControl<Border>("EmptyView");
            if (emptyView != null && _mainVm != null)
                emptyView.IsVisible = !_mainVm.IsSettingsMode && ConnectionVm?.SelectedPeer == null;
        }
    }

    // === ウィンドウ位置の保存/復元 ===

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RestoreWindowPosition();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            DebounceSaveWindowPosition();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 閉じる時はデバウンスせず即座に保存
        _savePositionDebounceTimer?.Dispose();
        _savePositionDebounceTimer = null;
        SaveWindowPosition();
    }

    /// <summary>500ms デバウンスでウィンドウ位置を保存する。連続的な位置/サイズ変更時の IO 負荷を軽減。</summary>
    private void DebounceSaveWindowPosition()
    {
        _savePositionDebounceTimer?.Dispose();
        _savePositionDebounceTimer = new System.Threading.Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(SaveWindowPosition),
            null,
            500,
            System.Threading.Timeout.Infinite);
    }

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

    private void OnPositionOrSizeChanged(object? sender, EventArgs e) => DebounceSaveWindowPosition();

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
        }

        e.Handled = true;
    }
}
