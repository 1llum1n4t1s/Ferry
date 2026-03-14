using System;
using System.ComponentModel;
using Avalonia.Controls;
using Ferry.ViewModels;

namespace Ferry.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
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
}
