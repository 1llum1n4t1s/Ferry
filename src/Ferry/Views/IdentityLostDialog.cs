using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Ferry.Views;

/// <summary>
/// rere #D-001(a) Phase B / Q2 採用案: identity.key 紛失検出 → clean slate UI。
/// Workers /auth/token が 401 DEVICE_PUBKEY_MISMATCH を返したときに表示する。
///
/// XAML を使わず純コードで構築し、文言は 18 言語のロケール辞書（Text.IdentityLost.*）から引く。
/// </summary>
public sealed class IdentityLostDialog : Window
{
    /// <summary>[やり直す] が押されたら true（呼出側で DeviceId 再生成 + peers.json reset を実行）。</summary>
    public bool ResetConfirmed { get; private set; }

    public IdentityLostDialog()
    {
        Title = App.Text("IdentityLost.Title");
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
            Text = App.Text("IdentityLost.Heading"),
            FontSize = 18,
            FontWeight = FontWeight.Bold,
        });

        root.Children.Add(new TextBlock
        {
            Text = App.Text("IdentityLost.Body") + "\n" + App.Text("IdentityLost.Warning"),
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
            Content = App.Text("IdentityLost.Later"),
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
            Content = App.Text("IdentityLost.Reset"),
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

    /// <summary>
    /// clean slate 実行完了後に表示する確認ダイアログ。
    ///
    /// 既に in-memory の deviceIdentity / cfTokenProvider は **古い鍵を保持している**ため、
    /// 再起動せずに続行すると新しい identity.key と整合しなくなり、次回の `/auth/token` でまた
    /// `DEVICE_PUBKEY_MISMATCH` → IdentityLostDialog ループに陥る。新しい鍵を採用するには
    /// プロセスを終了させて次回起動で再構築する必要がある。
    /// </summary>
    public static Task ShowRestartRequiredAsync(Window? owner)
    {
        var dialog = new Window
        {
            Title = App.Text("IdentityLost.Restart.Title"),
            Width = 460,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var root = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = App.Text("IdentityLost.Restart.Heading"),
            FontSize = 18,
            FontWeight = FontWeight.Bold,
        });
        root.Children.Add(new TextBlock
        {
            Text = App.Text("IdentityLost.Restart.Body"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
        });
        var okBtn = new Button
        {
            Content = App.Text("IdentityLost.Restart.Exit"),
            Padding = new Thickness(16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            FontWeight = FontWeight.SemiBold,
        };
        okBtn.Click += (_, _) => dialog.Close();
        root.Children.Add(okBtn);
        dialog.Content = root;

        return owner != null && owner.IsVisible
            ? dialog.ShowDialog(owner)
            : ShowAndAwaitCloseAsync(dialog);
    }

    /// <summary>
    /// CodeRabbit P0 fix: owner 不在で modeless 表示するときに <see cref="Window.Closed"/> までを await できる
    /// 形にする。旧実装 <c>Task.Run(() =&gt; dialog.Show())</c> は (a) UI スレッド外で <c>Show()</c> を呼ぶため
    /// Avalonia が InvalidOperationException を出す可能性があり、(b) <c>Show()</c> が void なので Task が
    /// 即座に完了して呼出側 (<c>App.axaml.cs</c> の <c>TryShutdown(0)</c>) がダイアログを見せる前にプロセスを
    /// 落としていた。呼出側はすでに UI スレッド (<c>Dispatcher.UIThread.InvokeAsync</c>) から呼んでいるので、
    /// <c>Show()</c> を直接呼び TCS で Closed を待つだけで両方解決する。
    /// </summary>
    private static Task ShowAndAwaitCloseAsync(Window dialog)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => tcs.TrySetResult(true);
        dialog.Show();
        return tcs.Task;
    }
}
