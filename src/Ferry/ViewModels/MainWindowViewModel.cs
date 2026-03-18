using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ferry.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel。
/// 2カラムレイアウト（サイドバー + チャット/設定）を管理する。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public ConnectionViewModel Connection { get; }
    public TransferViewModel Transfer { get; }
    public ChatViewModel Chat { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>設定画面を表示中かどうか（⚙ トグル）。</summary>
    [ObservableProperty]
    private bool _isSettingsMode;

    public MainWindowViewModel(
        ConnectionViewModel connection,
        TransferViewModel transfer,
        ChatViewModel chat,
        SettingsViewModel settings)
    {
        Connection = connection;
        Transfer = transfer;
        Chat = chat;
        Settings = settings;
    }

    /// <summary>デザイナー用パラメータなしコンストラクタ。</summary>
    public MainWindowViewModel()
    {
        // デザイン時のみ使用。実行時は DI 経由のコンストラクタを使用する。
        Connection = null!;
        Transfer = null!;
        Chat = null!;
        Settings = null!;
    }

    /// <summary>設定モードのトグル。</summary>
    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsMode = !IsSettingsMode;
    }
}
