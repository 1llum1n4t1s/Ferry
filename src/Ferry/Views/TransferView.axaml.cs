using Avalonia.Controls;
using Avalonia.Interactivity;
using Ferry.ViewModels;

namespace Ferry.Views;

public partial class TransferView : UserControl
{
    public TransferView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 「受信フォルダを開く」📂 ボタン → 受信保存先を OS のファイルマネージャで開く。
    /// 保存先は親ウィンドウの MainWindowViewModel.Settings.SaveDirectory から取得する
    /// （ファイラ起動は OS プロセス依存なので MVVM 規約に従い View 側で処理）。
    /// </summary>
    private void OnOpenSaveDirClick(object? sender, RoutedEventArgs e)
    {
        var dir = (TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel)?.Settings?.SaveDirectory;
        Ferry.Util.ShellHelper.OpenFolder(dir);
    }
}
