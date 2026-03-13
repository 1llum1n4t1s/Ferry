namespace Ferry.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel。
/// 接続パネルと転送パネルの ViewModel を保持する。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    public ConnectionViewModel Connection { get; }
    public TransferViewModel Transfer { get; }

    public MainWindowViewModel(ConnectionViewModel connection, TransferViewModel transfer)
    {
        Connection = connection;
        Transfer = transfer;
    }

    /// <summary>
    /// デザイナー用パラメータなしコンストラクタ。
    /// </summary>
    public MainWindowViewModel()
    {
        // デザイン時のみ使用。実行時は DI 経由のコンストラクタを使用する。
        Connection = null!;
        Transfer = null!;
    }
}
