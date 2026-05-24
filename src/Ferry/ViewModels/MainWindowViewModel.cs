using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ferry.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel。
/// 2カラムレイアウト（サイドバー + 転送/設定）を管理する。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public ConnectionViewModel Connection { get; }
    public TransferViewModel Transfer { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>設定画面を表示中かどうか（⚙ トグル）。</summary>
    [ObservableProperty]
    public partial bool IsSettingsMode { get; set; }

    public MainWindowViewModel(
        ConnectionViewModel connection,
        TransferViewModel transfer,
        SettingsViewModel settings)
    {
        Connection = connection;
        Transfer = transfer;
        Settings = settings;
    }

    /// <summary>デザイナー用パラメータなしコンストラクタ。</summary>
    public MainWindowViewModel()
    {
        // デザイン時のみ使用。実行時は DI 経由のコンストラクタを使用する。
        Connection = null!;
        Transfer = null!;
        Settings = null!;
    }

    /// <summary>設定モードのトグル。</summary>
    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsMode = !IsSettingsMode;
    }
}
