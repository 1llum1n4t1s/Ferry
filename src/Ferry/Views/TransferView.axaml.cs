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
    /// ヘッダーの「保存先を開く」ボタン → OS のファイルマネージャで受信ファイル保存先を開く。
    /// 保存先パスは MainWindowViewModel.Settings.SaveDirectory が真の保持元なので、
    /// TopLevel.DataContext から辿って取得する（TransferViewModel は SaveDirectory を持たない）。
    /// </summary>
    private void OnOpenSaveDirClick(object? sender, RoutedEventArgs e)
    {
        string? dir = null;
        if (TopLevel.GetTopLevel(this) is Window window
            && window.DataContext is MainWindowViewModel mvm)
        {
            dir = mvm.Settings?.SaveDirectory;
        }
        Ferry.Util.ShellHelper.OpenFolder(dir);
    }
}
