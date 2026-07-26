using Avalonia.Controls;

namespace Ferry.Views;

/// <summary>
/// 設定ビュー。
///
/// v1.0.38: 保存先選択ダイアログはメイン画面のアドレスバー側 (MainWindow) へ移設済み。
/// ロケール ComboBox も AXAML の宣言的バインディング（ItemsSource / SelectedItem +
/// x:DataType 付き ItemTemplate）へ移したため、code-behind の処理は無くなった。
/// 旧実装は OnLoaded で ItemsSource を代入し DisplayMemberBinding に
/// reflection binding (new Binding("DisplayName")) を渡していたため、Native AOT で
/// IL3050 (RequiresDynamicCode) 警告が出ていた。
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
