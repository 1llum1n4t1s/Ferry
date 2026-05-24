using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using Ferry.ViewModels;

namespace Ferry.Views;

/// <summary>
/// メインウィンドウ。2カラムレイアウト（サイドバー + 転送/設定）を管理する。
/// </summary>
public partial class MainWindow : Window
{
    private Border? _dropOverlay;
    private ISettingsService? _settingsService;

    /// <summary>ウィンドウ位置保存のデバウンスタイマー（500ms）。</summary>
    private System.Threading.Timer? _savePositionDebounceTimer;

    /// <summary>OnClosing 後の遅延 Bounds 変更で SaveWindowPosition が走らないよう、Closing 入った時点で立てるフラグ。</summary>
    private bool _closed;

    private MainWindowViewModel? _mainVm;
    private ConnectionViewModel? ConnectionVm => _mainVm?.Connection;
    private TransferViewModel? TransferVm => _mainVm?.Transfer;

    // イベント重複登録防止用: 前回購読した ViewModel の参照を保持
    private ConnectionViewModel? _subscribedConnectionVm;
    private MainWindowViewModel? _subscribedMainVm;
    private CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? _subscribedAddNewPeerCommand;

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

        // 最小化→トレイ監視（M-9: 自作 WindowStateObserver を Avalonia 標準の Subscribe(Action<T>) に統一）
        this.GetObservable(WindowStateProperty).Subscribe(state =>
        {
            if (state == WindowState.Minimized
                && _mainVm?.Settings?.MinimizeToTray == true)
            {
                ShowInTaskbar = false;
                Hide();
            }
        });

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

    // === イベント購読 ===

    private void SubscribeToEvents()
    {
        if (_mainVm == null) return;

        // 前回のハンドラを解除（DataContext 変更時の重複購読防止）
        if (_subscribedConnectionVm != null)
        {
            _subscribedConnectionVm.PropertyChanged -= OnConnectionVmPropertyChanged;
            _subscribedConnectionVm.CopyPairingLinkRequested -= OnCopyPairingLinkRequested;
        }
        if (_subscribedMainVm != null)
        {
            _subscribedMainVm.PropertyChanged -= OnMainVmPropertyChangedForEmptyView;
        }
        if (_subscribedAddNewPeerCommand != null)
        {
            _subscribedAddNewPeerCommand.PropertyChanged -= OnAddNewPeerCommandPropertyChanged;
            _subscribedAddNewPeerCommand = null;
        }

        // SelectedPeer 変更 → ピア名更新
        if (ConnectionVm != null)
        {
            ConnectionVm.PropertyChanged += OnConnectionVmPropertyChanged;
            // N-5: ペアリングリンクのクリップボードコピーは View 責務
            ConnectionVm.CopyPairingLinkRequested += OnCopyPairingLinkRequested;
            _subscribedConnectionVm = ConnectionVm;

            // AddNewPeerCommand 完了後に AddMemberWindow を表示。
            // 匿名ラムダだと購読解除できず DataContext 切替で重複→旧 VM 参照リークするため、
            // 名前付きメソッドにして購読/解除を _subscribedAddNewPeerCommand で対称に管理する
            ConnectionVm.AddNewPeerCommand.PropertyChanged += OnAddNewPeerCommandPropertyChanged;
            _subscribedAddNewPeerCommand = ConnectionVm.AddNewPeerCommand;
        }

        // IsSettingsMode 変更時の空ビュー更新（名前付きメソッドで一度だけ登録）
        _mainVm.PropertyChanged += OnMainVmPropertyChangedForEmptyView;
        _subscribedMainVm = _mainVm;

        // 空ビューの表示制御を更新
        UpdateEmptyViewVisibility();
    }

    /// <summary>
    /// AddNewPeerCommand の IsRunning が true→false に遷移したタイミングで AddMemberWindow を表示する。
    /// async void だが try-catch でアプリ強制終了を防ぐ。
    /// </summary>
    private async void OnAddNewPeerCommandPropertyChanged(object? sender, PropertyChangedEventArgs pe)
    {
        try
        {
            if (pe.PropertyName != "IsRunning") return;
            var vm = ConnectionVm;
            if (vm == null || vm.AddNewPeerCommand.IsRunning) return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new AddMemberWindow { DataContext = vm };
                await dialog.ShowDialog(this);
            });
        }
        catch (Exception ex)
        {
            Ferry.Util.Logger.Log($"AddMemberWindow 表示に失敗: {ex.Message}", Ferry.Util.LogLevel.Error);
        }
    }

    private void OnConnectionVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.SelectedPeer))
        {
            var peer = ConnectionVm?.SelectedPeer;
            if (peer != null)
            {
                // 設定モードを解除
                if (_mainVm != null) _mainVm.IsSettingsMode = false;
            }
            UpdateEmptyViewVisibility();
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
        _closed = true;
        _savePositionDebounceTimer?.Dispose();
        _savePositionDebounceTimer = null;
        SaveWindowPosition();
    }

    /// <summary>500ms デバウンスでウィンドウ位置を保存する。連続的な位置/サイズ変更時の IO 負荷を軽減。</summary>
    private void DebounceSaveWindowPosition()
    {
        // OnClosing 後の遅延 Bounds 変更で SaveWindowPosition が走るのを防ぐ
        // （閉じる過程の minimize 等で BoundsProperty が変わるケース）
        if (_closed) return;

        _savePositionDebounceTimer?.Dispose();
        _savePositionDebounceTimer = new System.Threading.Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_closed) return;
                SaveWindowPosition();
            }),
            null,
            500,
            System.Threading.Timeout.Infinite);
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

    // Avalonia 12 の Contains / TryGetFiles は例外を投げない契約。
    // 旧コードは try-catch で握り潰していたが、例外を制御フローに使うアンチパターンなので除去
    private static bool HasFiles(DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File)) return true;
        var files = e.DataTransfer.TryGetFiles();
        return files is not null && files.Any();
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

        if (paths.Length > 0 && ConnectionVm?.SelectedPeer != null && _mainVm?.IsSettingsMode != true)
        {
            TransferVm?.SendFilesCommand.Execute(paths);
        }

        e.Handled = true;
    }

    /// <summary>
    /// ピアリスト行の 3 点リーダーメニュー → 「削除」クリック。
    /// 確認ダイアログを表示し、OK が押されたときだけ
    /// <see cref="ConnectionViewModel.RemovePeerCommand"/> を呼ぶ。
    /// </summary>
    private async void OnDeletePeerClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem mi || mi.DataContext is not PairedPeer peer) return;
            // await を挟むので await 前にローカルへ束縛する（DataContext 切替で
            // ConnectionVm プロパティが null/別 VM になっても本処理は元の VM を保つ）
            var vm = ConnectionVm;
            if (vm is null) return;

            var dialog = new ConfirmWindow();
            dialog.SetData(App.Text("Member.Delete.Confirm", peer.DisplayName), ConfirmButtonType.OkCancel);
            // ShowDialog<bool?> で「X ボタン (null) / Cancel (false) / OK (true)」を区別する。
            // 削除実行は明示的に true の場合のみ。
            var result = await dialog.ShowDialog<bool?>(this);
            if (result != true) return;

            await vm.RemovePeerCommand.ExecuteAsync(peer.PeerId);
        }
        catch (Exception ex)
        {
            Ferry.Util.Logger.Log($"ピア削除フローでエラー: {ex.Message}", Ferry.Util.LogLevel.Error);
        }
    }

    /// <summary>
    /// 3 点リーダーボタン押下が ListBoxItem に伝播して SelectedPeer を書き換えるのを防ぐ。
    /// </summary>
    private void OnDotMenuPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// N-5: ConnectionViewModel.CopyPairingLinkRequested を受けて TopLevel.Clipboard 経由でコピーする。
    /// MVVM 規約遵守: ViewModel は Avalonia の TopLevel/Clipboard に依存しない。
    /// </summary>
    private async void OnCopyPairingLinkRequested(object? sender, string url)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(url);
            ConnectionVm?.NotifyPairingLinkCopied();
        }
        catch (Exception ex)
        {
            Ferry.Util.Logger.Log($"クリップボードコピーエラー: {ex.Message}", Ferry.Util.LogLevel.Warning);
        }
    }
}
