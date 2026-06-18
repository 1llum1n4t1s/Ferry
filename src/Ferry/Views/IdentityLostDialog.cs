using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Ferry.Views;

/// <summary>
/// rere #D-001(a) Phase B / Q2 採用案: identity.key 紛失検出 → clean slate UI。
/// Workers /auth/token が 401 DEVICE_PUBKEY_MISMATCH を返したときに表示する。
///
/// XAML を使わず純コードで構築（最小実装・ロケール依存なし・新規 axaml/AOT 注意点回避）。
/// </summary>
public sealed class IdentityLostDialog : Window
{
    /// <summary>[やり直す] が押されたら true（呼出側で DeviceId 再生成 + peers.json reset を実行）。</summary>
    public bool ResetConfirmed { get; private set; }

    public IdentityLostDialog()
    {
        Title = "Ferry - 端末識別の鍵";
        Width = 520;
        Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var root = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
        };

        root.Children.Add(new TextBlock
        {
            Text = "端末識別の鍵が壊れています",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
        });

        root.Children.Add(new TextBlock
        {
            Text =
                "OS 再インストール / ディスクエラー / アプリ更新失敗で端末の長期鍵 (identity.key) が " +
                "紛失した可能性があります。新しい鍵を生成してペアリングをやり直しますか？\n" +
                "やり直す場合、現在のペア一覧はすべて消去されます（相手側の登録も含めて再登録が必要）。",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var laterBtn = new Button
        {
            Content = "後で",
            Padding = new Thickness(16, 6),
        };
        laterBtn.Click += (_, _) =>
        {
            ResetConfirmed = false;
            Close();
        };
        buttons.Children.Add(laterBtn);

        var resetBtn = new Button
        {
            Content = "やり直す",
            Padding = new Thickness(16, 6),
            FontWeight = FontWeight.SemiBold,
        };
        resetBtn.Click += (_, _) =>
        {
            ResetConfirmed = true;
            Close();
        };
        buttons.Children.Add(resetBtn);

        root.Children.Add(buttons);
        Content = root;
    }
}
