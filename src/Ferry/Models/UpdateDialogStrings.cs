using VelopackUpdateDialog;

namespace Ferry.Models;

/// <summary>
/// VelopackUpdateDialog.Avalonia の表示文字列を Ferry のローカライズリソースへ接続する。
/// プロパティ getter で <see cref="App.Text(string, object[])"/> を呼ぶため、
/// ダイアログ表示時点で有効な locale が反映される。
/// </summary>
public sealed class UpdateDialogStrings : IUpdateDialogStrings
{
    /// <summary>シングルトン インスタンス。</summary>
    public static readonly UpdateDialogStrings Instance = new();

    /// <inheritdoc />
    public string Title => App.Text("SelfUpdate.Title");

    /// <inheritdoc />
    public string AvailableHeader => App.Text("SelfUpdate.Available");

    /// <inheritdoc />
    public string DownloadAndInstall => App.Text("SelfUpdate.DownloadAndInstall");

    /// <inheritdoc />
    public string IgnoreThisVersion => App.Text("SelfUpdate.IgnoreThisVersion");

    /// <inheritdoc />
    public string UpToDateMessage => App.Text("SelfUpdate.UpToDate");

    /// <inheritdoc />
    public string ErrorHeader => App.Text("SelfUpdate.Error");

    /// <inheritdoc />
    public string Close => App.Text("Close");

    /// <inheritdoc />
    public string CheckingMessage => App.Text("SelfUpdate.Checking");
}
