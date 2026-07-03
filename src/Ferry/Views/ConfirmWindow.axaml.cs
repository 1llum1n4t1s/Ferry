using Avalonia.Controls;
using Avalonia.Interactivity;
using Ferry.Models;

namespace Ferry.Views;

/// <summary>
/// 汎用確認ダイアログ。Komorebi の <c>Confirm</c> を Ferry 用に移植した版で、
/// <see cref="SetData"/> で本文とボタン構成を指定し、<see cref="Window.ShowDialog{TResult}"/>
/// の戻り値（bool）で OK / Yes 押下か否かを返す。
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
        // SetData() が呼ばれなくても破綻しないよう OkCancel デフォルトを当てておく。
        // 呼び出し側が SetData を呼べばラベルが上書きされる
        BtnYes.Content = App.Text("Sure");
        BtnNo.Content = App.Text("Cancel");
        MessageText.Text = string.Empty;
    }

    /// <summary>
    /// 本文とボタンラベルを設定する。ボタンラベルはロケールリソースから取得する。
    /// 呼び出し側は <c>ShowDialog&lt;bool?&gt;</c> を使い、
    /// <c>true</c>=Yes/OK、<c>false</c>=No/Cancel、<c>null</c>=X ボタン (ダイアログ取消) として区別する。
    /// </summary>
    public void SetData(string message, ConfirmButtonType buttonType)
    {
        MessageText.Text = message;
        // Ok 以外で再利用されても破綻しないよう毎回可視に戻してから構成を当てる
        BtnNo.IsVisible = true;

        switch (buttonType)
        {
            case ConfirmButtonType.OkCancel:
                BtnYes.Content = App.Text("Sure");
                BtnNo.Content = App.Text("Cancel");
                break;
            case ConfirmButtonType.YesNo:
                BtnYes.Content = App.Text("Yes");
                BtnNo.Content = App.Text("No");
                break;
            case ConfirmButtonType.Ok:
                BtnYes.Content = App.Text("Sure");
                BtnNo.IsVisible = false;
                break;
        }
    }

    // bool? を返すことで、Close を呼ばずにウィンドウ右上 X で閉じられた場合は null になり、
    // No/Cancel の明示的 false と区別できる
    private void OnYes(object? sender, RoutedEventArgs e) => Close((bool?)true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close((bool?)false);
}
