using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ferry.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel。
/// 2カラムレイアウト（サイドバー + 転送/設定）を管理する。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
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

    private bool _disposed;

    /// <summary>
    /// アプリ終了時に子 ViewModel の IDisposable をまとめて破棄する
    /// (App.OnFrameworkInitializationCompleted の desktop.Exit から呼ばれる)。
    /// 子の Dispose は再入安全ではない (ConnectionViewModel が SemaphoreSlim / CTS を破棄する) ため
    /// _disposed で多重実行を防ぎ、片方の例外がもう片方の破棄を止めないよう個別に try で隔離する。
    /// Settings は破棄すべきリソースを持たないシングルトンなので対象外。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Transfer?.Dispose(); }
        catch (Exception ex) { Ferry.Util.Logger.LogException("TransferViewModel.Dispose で例外", ex); }
        try { Connection?.Dispose(); }
        catch (Exception ex) { Ferry.Util.Logger.LogException("ConnectionViewModel.Dispose で例外", ex); }
    }
}
