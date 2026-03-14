namespace Ferry.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel。
/// 接続パネル、転送パネル、設定パネルの ViewModel を保持する。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    public ConnectionViewModel Connection { get; }
    public TransferViewModel Transfer { get; }
    public SettingsViewModel Settings { get; }

    public MainWindowViewModel(ConnectionViewModel connection, TransferViewModel transfer, SettingsViewModel settings)
    {
        Connection = connection;
        Transfer = transfer;
        Settings = settings;
    }

    /// <summary>
    /// デザイナー用パラメータなしコンストラクタ。
    /// </summary>
    public MainWindowViewModel()
    {
        // デザイン時のみ使用。実行時は DI 経由のコンストラクタを使用する。
        Connection = null!;
        Transfer = null!;
        Settings = null!;
    }
}
