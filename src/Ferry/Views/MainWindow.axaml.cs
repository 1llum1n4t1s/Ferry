using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ferry.Services;
using Ferry.ViewModels;

namespace Ferry.Views;

public partial class MainWindow : Window
{
    private Border? _dropOverlay;
    private ISettingsService? _settingsService;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        PositionChanged += OnPositionOrSizeChanged;

        // ドロップオーバーレイの参照を取得
        _dropOverlay = this.FindControl<Border>("DropOverlay");

        // ＋ ボタンで AddMemberWindow ダイアログを開く
        var addMemberButton = this.FindControl<Button>("AddMemberButton");
        if (addMemberButton != null)
            addMemberButton.Click += OnAddMemberClick;

        // ピアリスト選択変更でチャットを読み込み
        var peerListBox = this.FindControl<ListBox>("PeerListBox");
        if (peerListBox != null)
            peerListBox.SelectionChanged += OnPeerSelectionChanged;

        // ウィンドウ全体のドラッグ＆ドロップイベント
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);

        // WindowState の変更を非同期監視
        this.GetObservable(WindowStateProperty).Subscribe(new WindowStateObserver(state =>
        {
            if (state == WindowState.Minimized
                && DataContext is MainWindowViewModel vm
                && vm.Settings.MinimizeToTray)
            {
                ShowInTaskbar = false;
                Hide();
            }
        }));

        // 初期最小化起動の処理
        Loaded += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm && vm.Settings.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                if (vm.Settings.MinimizeToTray)
                {
                    ShowInTaskbar = false;
                    Hide();
                }
            }
        };
    }

    /// <summary>
    /// 外部から ISettingsService を注入する（ウィンドウ位置の保存/復元用）。
    /// </summary>
    public void SetSettingsService(ISettingsService settingsService) => _settingsService = settingsService;

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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowPosition();
        // 閉じるボタンは常に通常の終了処理（トレイ格納しない）
    }

    /// <summary>WindowState の変更を非同期で監視するオブザーバー。</summary>
    private sealed class WindowStateObserver(Action<WindowState> onNext) : IObserver<WindowState>
    {
        public void OnNext(WindowState value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    // === ウィンドウ位置の保存/復元 ===

    private void RestoreWindowPosition()
    {
        var s = _settingsService?.Settings;
        if (s?.WindowWidth > 0 && s?.WindowHeight > 0)
        {
            Width = s.WindowWidth!.Value;
            Height = s.WindowHeight!.Value;
        }
        if (s?.WindowLeft != null && s?.WindowTop != null)
        {
            Position = new PixelPoint((int)s.WindowLeft.Value, (int)s.WindowTop.Value);
        }
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

    // === ＋ ボタン → AddMemberWindow ダイアログ ===

    private async void OnAddMemberClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel mainVm) return;

        // ペアリングセッションを開始
        mainVm.Connection.StartSessionCommand.Execute(null);

        var dialog = new AddMemberWindow
        {
            DataContext = mainVm.Connection,
        };

        await dialog.ShowDialog(this);
    }

    // === ピア選択変更 → チャット読み込み ===

    private async void OnPeerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel mainVm) return;

        var selectedPeer = mainVm.Connection.SelectedPeer;
        if (selectedPeer != null)
        {
            // 設定モードを解除してチャットを表示
            mainVm.IsSettingsMode = false;
            await mainVm.Chat.LoadChatAsync(selectedPeer.PeerId);
        }
    }

    // === ドラッグ＆ドロップ処理 ===

    private bool HasFiles(DragEventArgs e)
    {
        try
        {
            if (e.DataTransfer.Contains(DataFormat.File))
                return true;
        }
        catch { /* ignore */ }

        try
        {
            var files = e.DataTransfer.TryGetFiles();
            return files != null && files.Any();
        }
        catch { return false; }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var hasFiles = HasFiles(e);
        Util.Logger.Log($"DragEnter: hasFiles={hasFiles}", Util.LogLevel.Debug);
        if (hasFiles && _dropOverlay != null)
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
        if (_dropOverlay != null)
            _dropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Util.Logger.Log("OnDrop 呼び出し", Util.LogLevel.Debug);

        if (_dropOverlay != null)
            _dropOverlay.IsVisible = false;

        if (DataContext is not MainWindowViewModel mainVm)
        {
            Util.Logger.Log("OnDrop: DataContext が MainWindowViewModel ではない", Util.LogLevel.Warning);
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            Util.Logger.Log("OnDrop: TryGetFiles() が null", Util.LogLevel.Warning);
            return;
        }

        // ファイルとフォルダの両方のパスを取得
        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p))
            .ToArray();

        if (paths.Length > 0)
        {
            Util.Logger.Log($"ドロップ: {paths.Length} パス");

            // チャットが表示中なら入力エリアに添付
            if (mainVm.Chat.IsChatVisible && mainVm.Chat.SelectedPeerId != null)
            {
                mainVm.Chat.AddAttachedFiles(paths);
            }
        }

        e.Handled = true;
    }
}
