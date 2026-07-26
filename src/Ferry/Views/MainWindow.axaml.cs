using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Reactive; // AnonymousObserver<T>。旧来 FirebaseDatabase.net→System.Reactive が transitive 提供していた Subscribe(Action<T>) 拡張が Step 6 のパッケージ撤去で消えたため、Avalonia 自前の observer で明示購読する
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

    /// <summary>左ペイン（サイドバー）の列定義。GridSplitter ドラッグ幅の保存/復元に使う。</summary>
    private ColumnDefinition? _sidebarColumn;

    // サイドバー幅復元時のクランプ範囲（AXAML の Border MinWidth / GridSplitter Width と一致させる）
    private const double DefaultSidebarWidth = 220;
    private const double MinSidebarWidth = 180;
    private const double MinMainPaneWidth = 300;
    private const double SplitterWidth = 4;

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
    private SettingsViewModel? _subscribedSettingsVm;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        PositionChanged += OnPositionOrSizeChanged;

        _dropOverlay = this.FindControl<Border>("DropOverlay");

        // 左右スプリッターのドラッグ完了時にサイドバー幅を保存（ウィンドウ位置保存のデバウンスに相乗り）
        var mainGrid = this.FindControl<Grid>("MainContentGrid");
        if (mainGrid != null && mainGrid.ColumnDefinitions.Count > 0)
            _sidebarColumn = mainGrid.ColumnDefinitions[0];
        if (this.FindControl<GridSplitter>("PaneSplitter") is { } paneSplitter)
            paneSplitter.DragCompleted += OnSplitterDragCompleted;

        // 宛先リストのセクション見出し（PeerListSection）行を選択・フォーカス・hover 不可にする。
        // VisiblePeers は見出しとピアを混在させるため、見出しコンテナだけ無効化する（コンテナ再利用にも追従）。
        PeerListBox.ContainerPrepared += OnPeerListContainerPrepared;

        // ドラッグ＆ドロップ
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);

        // 最小化→トレイ監視（M-9: 自作 WindowStateObserver を Avalonia 標準の observer 購読に統一）
        this.GetObservable(WindowStateProperty).Subscribe(new AnonymousObserver<WindowState>(state =>
        {
            // macOS は最小化=Dock 格納が OS 慣習なので、トレイ格納(Hide)は Windows/Linux のみで行う。
            if (state == WindowState.Minimized
                && _mainVm?.Settings?.MinimizeToTray == true
                && !OperatingSystem.IsMacOS())
            {
                ShowInTaskbar = false;
                Hide();
            }
            // ① ウィンドウが前面か（表示中かつ非最小化）に応じて presence ポーリングを開閉する
            UpdatePresenceForeground();
        }));

        // ① トレイ格納(Hide)/復帰(Show) は IsVisible 変化として届くのでこちらも監視
        this.GetObservable(IsVisibleProperty).Subscribe(new AnonymousObserver<bool>(_ => UpdatePresenceForeground()));

        // 初期最小化起動
        Loaded += (_, _) =>
        {
            if (_mainVm?.Settings?.StartMinimized == true)
            {
                WindowState = WindowState.Minimized;
                if (_mainVm.Settings.MinimizeToTray && !OperatingSystem.IsMacOS())
                {
                    ShowInTaskbar = false;
                    Hide();
                }
            }
            UpdatePresenceForeground();
        };
    }

    /// <summary>
    /// ① ウィンドウの可視状態に合わせて presence ポーリングの稼働を切り替える。
    /// 画面に出ていて最小化されていないときだけポーリングを回し、トレイ格納/最小化中は止めて
    /// リクエスト数と帯域を節約する（Heartbeat は ConnectionViewModel 側で継続）。
    /// DataContext 未設定（ConnectionVm == null）の早期発火は ?. で安全にスキップする。
    /// </summary>
    private void UpdatePresenceForeground()
    {
        var foreground = IsVisible && WindowState != WindowState.Minimized;
        ConnectionVm?.SetPresencePollingActive(foreground);
        // CodeRabbit 指摘: PairSyncService の visibility gate もここで配線（旧実装は SetActive が呼ばれず
        // 常時ポーリング = 帯域節約効果が出ていなかった）。前面復帰時に同期も再開する。
        (Avalonia.Application.Current as App)?.PairSyncService?.SetActive(foreground);
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
            _subscribedConnectionVm.CopyPairingCodeRequested -= OnCopyPairingCodeRequested;
        }
        if (_subscribedMainVm != null)
        {
            _subscribedMainVm.PropertyChanged -= OnMainVmPropertyChangedForEmptyView;
        }
        if (_subscribedSettingsVm != null)
        {
            _subscribedSettingsVm.BrowseSaveDirectoryRequested -= OnBrowseSaveDirectoryRequested;
        }

        // SelectedPeer 変更 → ピア名更新
        if (ConnectionVm != null)
        {
            ConnectionVm.PropertyChanged += OnConnectionVmPropertyChanged;
            // N-5: ペアリングリンクのクリップボードコピーは View 責務
            ConnectionVm.CopyPairingCodeRequested += OnCopyPairingCodeRequested;
            _subscribedConnectionVm = ConnectionVm;
            // ① VM 確定時に現在の可視状態を同期（コンストラクタの早期発火で取りこぼした分を反映）
            UpdatePresenceForeground();
        }

        // IsSettingsMode 変更時の空ビュー更新（名前付きメソッドで一度だけ登録）
        _mainVm.PropertyChanged += OnMainVmPropertyChangedForEmptyView;
        _subscribedMainVm = _mainVm;

        // 保存先アドレスバーの「変更」ボタン → フォルダ選択ダイアログ（v1.0.47: 設定画面から移設したので View 側はここで処理）
        if (_mainVm.Settings != null)
        {
            _mainVm.Settings.BrowseSaveDirectoryRequested += OnBrowseSaveDirectoryRequested;
            _subscribedSettingsVm = _mainVm.Settings;
        }

        // 空ビューの表示制御を更新
        UpdateEmptyViewVisibility();
    }

    private void OnConnectionVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.SelectedPeer))
        {
            var peer = ConnectionVm?.SelectedPeer;
            if (peer != null && _mainVm != null)
            {
                // 宛先を選んだら設定タブ・ペアリング追加タブを解除（同一タブグループの排他選択）
                _mainVm.IsSettingsMode = false;
                _mainVm.IsAddPeerMode = false;
            }
            UpdateEmptyViewVisibility();
        }
    }

    private void UpdateEmptyViewVisibility()
    {
        var emptyView = this.FindControl<Border>("EmptyView");
        if (emptyView != null && _mainVm != null)
        {
            emptyView.IsVisible = !_mainVm.IsSettingsMode && !_mainVm.IsAddPeerMode
                                  && ConnectionVm?.SelectedPeer == null;
        }
    }

    /// <summary>設定 / ペアリング追加タブ切替時に空ビューの表示を更新する（重複登録防止のため名前付きメソッド化）。</summary>
    private void OnMainVmPropertyChangedForEmptyView(object? sender, PropertyChangedEventArgs pe)
    {
        if (pe.PropertyName == nameof(MainWindowViewModel.IsSettingsMode)
            || pe.PropertyName == nameof(MainWindowViewModel.IsAddPeerMode))
        {
            UpdateEmptyViewVisibility();
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
        // ウィンドウ位置は即保存（デバウンス解除）
        _savePositionDebounceTimer?.Dispose();
        _savePositionDebounceTimer = null;
        SaveWindowPosition();

        // 既にアプリ終了処理が進行中なら、ウィンドウは素直に閉じる。
        // ・X ボタン経路で _closed=true 後に desktop.Shutdown() が各ウィンドウを Close → 本ハンドラを
        //   再入させるケース（Avalonia の Shutdown() は再入で例外を投げるので二重呼び出しを防ぐ）。
        // ・トレイ「終了」/ Cmd+Q / OS シャットダウンで App.IsExplicitShutdown が立っているケース
        //   （macOS でも Hide せず実際に終了させる。これが無いと mac では下の Hide で永遠に終了できない）。
        if (_closed || App.IsExplicitShutdown)
            return;

        // macOS: 赤信号ボタン(Close)はアプリを終了せずウィンドウを隠す慣習。終了はメニューバー(トレイ)の
        // 「終了」/ Cmd+Q に委ねる（そちらは App.IsExplicitShutdown を立てるので上の分岐で素通りする）。
        // これがないと mac で × を押すたびに転送中 transport が切れてしまう。
        // （アプリ生存は ShutdownMode.OnExplicitShutdown 前提。復帰はメニューバーアイコン/Dock から）。
        if (OperatingSystem.IsMacOS())
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Win/Linux の X ボタン押下時は標準の感覚どおり常に終了させる。
        // 「MinimizeToTray」設定は最小化ボタン押下時のトレイ格納のみに効かせる
        // (WindowStateProperty observable 側で処理済み)。
        // ShutdownMode=OnExplicitShutdown 下では X ボタン Close でプロセスが終わらないため、
        // ここで明示的に desktop.Shutdown() を呼ぶ（再入は上の _closed ガードで吸収）。
        _closed = true;
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
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

        // サイドバー幅を復元。広い画面で保存された値や手編集 settings.json で右ペインが
        // 潰れないよう、左ペイン最小幅と右ペイン最小幅の範囲にクランプしてから適用する。
        // 未保存時は AXAML 既定の 220 にフォールバック。
        if (_sidebarColumn != null)
        {
            var availableWidth = Bounds.Width > 0 ? Bounds.Width : Width;
            var maxSidebarWidth = Math.Max(MinSidebarWidth, availableWidth - SplitterWidth - MinMainPaneWidth);
            var restoredWidth = s.SidebarWidth ?? DefaultSidebarWidth;
            _sidebarColumn.Width = new GridLength(
                Math.Clamp(restoredWidth, MinSidebarWidth, maxSidebarWidth), GridUnitType.Pixel);
        }
    }

    /// <summary>スプリッターのドラッグ完了時にサイドバー幅を保存する（デバウンス経由）。</summary>
    private void OnSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
        => DebounceSaveWindowPosition();

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

        // サイドバー幅はウィンドウ最大化/通常に関わらず固定 px なので常に保存する
        if (_sidebarColumn != null && _sidebarColumn.Width.IsAbsolute && _sidebarColumn.Width.Value > 0)
            s.SidebarWidth = _sidebarColumn.Width.Value;

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
    /// 宛先リスト行のサブメニュー → 「ピン留め / 解除」クリック。VM のトグルコマンドへ委譲する。
    /// </summary>
    private void OnTogglePinClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is PairedPeer peer)
            ConnectionVm?.TogglePinCommand.Execute(peer);
    }

    /// <summary>
    /// VisiblePeers のセクション見出し（PeerListSection）行を選択・フォーカス・hover 不可にする。
    /// コンテナはピア行と見出し行の間で再利用されうるので、DataContext の型を毎回見て出し分ける。
    /// </summary>
    private void OnPeerListContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ListBoxItem item) return;
        bool isHeader = item.DataContext is PeerListSection;
        item.IsHitTestVisible = !isHeader;
        item.Focusable = !isHeader;
        item.Classes.Set("sectionHeader", isHeader);
    }

    /// <summary>
    /// 設定画面の「変更」ボタン → フォルダ選択ダイアログ。
    /// SettingsViewModel.BrowseSaveDirectoryRequested を受けて TopLevel 経由でピッカーを開く（MVVM 規約遵守）。
    /// </summary>
    private async void OnBrowseSaveDirectoryRequested(object? sender, EventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null || _mainVm?.Settings is not { } svm) return;

            var dirs = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = App.Text("Settings.SaveDirectory"),
            });

            if (dirs.Count > 0 && dirs[0].TryGetLocalPath() is { } path)
                svm.SaveDirectory = path;
        }
        catch (Exception ex)
        {
            Ferry.Util.Logger.Log($"保存先選択でエラー: {ex.Message}", Ferry.Util.LogLevel.Warning);
        }
    }

    /// <summary>
    /// N-5: ConnectionViewModel.CopyPairingCodeRequested を受けて TopLevel.Clipboard 経由でコピーする。
    /// MVVM 規約遵守: ViewModel は Avalonia の TopLevel/Clipboard に依存しない。
    /// </summary>
    private async void OnCopyPairingCodeRequested(object? sender, string url)
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
