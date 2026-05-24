namespace Ferry.Models;

/// <summary>
/// 確認ダイアログ <see cref="Ferry.Views.ConfirmWindow"/> のボタン構成。
/// </summary>
public enum ConfirmButtonType
{
    /// <summary>OK / キャンセル。破壊的でない操作の確認に使う。</summary>
    OkCancel,

    /// <summary>はい / いいえ。Yes/No 二択の問いかけに使う。</summary>
    YesNo,
}
