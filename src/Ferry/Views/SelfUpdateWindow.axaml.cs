using Avalonia.Controls;
using Avalonia.Interactivity;
using Ferry.Models;
using Ferry.ViewModels;

namespace Ferry.Views;

public partial class SelfUpdateWindow : Window
{
    public SelfUpdateWindow()
    {
        InitializeComponent();
    }

    private void OnDownloadAndInstall(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VelopackUpdate update }
            && DataContext is SelfUpdateViewModel vm)
        {
            vm.DownloadAndApplyUpdate(update);
        }
        e.Handled = true;
    }

    private void OnIgnoreVersion(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VelopackUpdate update })
        {
            // 設定にこのバージョンを無視するよう記録
            if (App.Current is App app)
                app.IgnoreUpdateVersion(update.TagName);
        }
        Close();
        e.Handled = true;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is SelfUpdateViewModel vm)
            vm.CancelDownload();
    }
}
