using Avalonia.Controls;

namespace Ferry.Views;

/// <summary>
/// メンバー追加（QR コードペアリング）ビュー。右ペイン内にタブとして表示する。
/// 旧 AddMemberWindow を UserControl 化したもの（別ウィンドウ表示をやめ右ペインに統合）。
/// </summary>
public partial class AddMemberView : UserControl
{
    public AddMemberView()
    {
        InitializeComponent();
    }
}
