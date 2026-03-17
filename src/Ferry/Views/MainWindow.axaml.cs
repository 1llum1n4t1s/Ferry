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

        // ウィンドウ全体のドラッグ＆ドロップイベント
        // Bubble ルーティング（DragDrop イベントは Bubble のみ対応）+ handledEventsToo で確実にハンドリング
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);

        // WindowState の変更を非同期監視（IRUZ パターン）
        // OnPropertyChanged 内での Hide() はタイミング問題があるため GetObservable を使用
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

        // 初期最小化起動の処理（Loaded 後に実行することでタスクバーから確実に消える）
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

        // ウィンドウ位置・サイズの復元
        RestoreWindowPosition();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // サイズ変更時も位置保存
        if (change.Property == BoundsProperty)
            SaveWindowPosition();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // ウィンドウ位置を保存
        SaveWindowPosition();

        // トレイ格納が有効なら閉じる代わりにトレイに格納
        if (DataContext is MainWindowViewModel vm && vm.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
        }
    }

    /// <summary>
    /// WindowState の変更を非同期で監視するオブザーバー。
    /// </summary>
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

    // === ドラッグ＆ドロップ処理 ===

    private bool HasFiles(DragEventArgs e)
    {
        // DataTransfer.Contains と TryGetFiles の両方を試行（Avalonia バージョン互換）
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

        // オーバーレイを非表示に
        if (_dropOverlay != null)
            _dropOverlay.IsVisible = false;

        if (DataContext is not MainWindowViewModel mainVm)
        {
            Util.Logger.Log("OnDrop: DataContext が MainWindowViewModel ではない", Util.LogLevel.Warning);
            return;
        }

        var transferVm = mainVm.Transfer;

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

        Util.Logger.Log($"OnDrop: paths={paths.Length}, CanExecute={transferVm.SendFilesCommand.CanExecute(paths)}", Util.LogLevel.Debug);

        if (paths.Length > 0 && transferVm.SendFilesCommand.CanExecute(paths))
        {
            Util.Logger.Log($"ドロップ: {paths.Length} パス");
            transferVm.SendFilesCommand.Execute(paths);
        }

        e.Handled = true;
    }
}
