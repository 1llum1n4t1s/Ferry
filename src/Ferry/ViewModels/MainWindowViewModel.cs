using System;
using System.Threading.Tasks;
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

    /// <summary>ペアリング先追加画面（右ペイン内タブ）を表示中かどうか。</summary>
    [ObservableProperty]
    public partial bool IsAddPeerMode { get; set; }

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

    /// <summary>
    /// ペアリング先追加タブ（右ペイン）を表示する。
    /// タブを即アクティブにしてから QR ペアリングセッションを生成する
    /// （生成完了を待たず切替えてレスポンスを良くする）。
    /// </summary>
    [RelayCommand]
    private async Task ShowAddPeerAsync()
    {
        IsAddPeerMode = true;
        if (Connection is not null)
            await Connection.AddNewPeerCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 設定タブへ切り替えたら宛先選択とペアリング追加タブを解除する。
    /// 「宛先」「設定」「ペアリング追加」は同一タブグループの排他選択で、設定を選ぶと
    /// 宛先リストの選択ハイライトが外れる。逆方向（宛先選択 → 設定解除）は MainWindow
    /// コードビハインドの OnConnectionVmPropertyChanged が担う。
    /// </summary>
    partial void OnIsSettingsModeChanged(bool value)
    {
        if (value && Connection is not null)
        {
            // 宛先リストの選択は外すが、直前ピアの着信監視は維持する
            // （設定タブ表示中も相手からの転送を受けられるようにする）
            Connection.DeselectKeepingListener();
            IsAddPeerMode = false;
        }
    }

    /// <summary>
    /// ペアリング追加タブへ切り替えたら宛先選択と設定タブを解除する（タブグループの排他選択）。
    /// </summary>
    partial void OnIsAddPeerModeChanged(bool value)
    {
        if (value && Connection is not null)
        {
            // 宛先リストの選択は外すが、直前ピアの着信監視は維持する
            // （ペアリング追加タブ表示中も相手からの転送を受けられるようにする）
            Connection.DeselectKeepingListener();
            IsSettingsMode = false;
        }
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
