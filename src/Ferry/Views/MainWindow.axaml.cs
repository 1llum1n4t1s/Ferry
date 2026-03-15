using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ferry.ViewModels;

namespace Ferry.Views;

public partial class MainWindow : Window
{
    private Border? _dropOverlay;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;

        // ToggleButton の排他制御（セグメントコントロール）
        var tabTransfer = this.FindControl<ToggleButton>("TabTransfer");
        var tabSettings = this.FindControl<ToggleButton>("TabSettings");

        if (tabTransfer != null && tabSettings != null)
        {
            tabTransfer.Click += (_, _) =>
            {
                tabTransfer.IsChecked = true;
                tabSettings.IsChecked = false;
            };
            tabSettings.Click += (_, _) =>
            {
                tabSettings.IsChecked = true;
                tabTransfer.IsChecked = false;
            };
        }

        // ドロップオーバーレイの参照を取得
        _dropOverlay = this.FindControl<Border>("DropOverlay");

        // ウィンドウ全体のドラッグ＆ドロップイベント
        // Bubble ルーティング（DragDrop イベントは Bubble のみ対応）+ handledEventsToo で確実にハンドリング
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 最小化起動の設定が有効ならウィンドウを最小化
        if (DataContext is MainWindowViewModel vm && vm.Settings.StartMinimized)
        {
            WindowState = WindowState.Minimized;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // トレイ格納が有効なら閉じる代わりに最小化
        if (DataContext is MainWindowViewModel vm && vm.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
        }
    }

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
        // オーバーレイを非表示に
        if (_dropOverlay != null)
            _dropOverlay.IsVisible = false;

        if (DataContext is not MainWindowViewModel mainVm)
            return;

        var transferVm = mainVm.Transfer;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;

        var filePaths = files
            .OfType<IStorageFile>()
            .Select(f => f.Path.LocalPath)
            .Where(p => System.IO.File.Exists(p))
            .ToArray();

        if (filePaths.Length > 0 && transferVm.SendFilesCommand.CanExecute(filePaths))
        {
            Util.Logger.Log($"ファイルドロップ: {filePaths.Length} ファイル");
            transferVm.SendFilesCommand.Execute(filePaths);
        }

        e.Handled = true;
    }
}
