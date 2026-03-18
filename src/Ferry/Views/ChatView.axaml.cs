using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ferry.ViewModels;

namespace Ferry.Views;

/// <summary>
/// チャット画面の View。メッセージ一覧・入力欄・ファイル添付を表示する。
/// </summary>
public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();

        // レイアウトデバッグ: 各層の幅をログ出力
        var sv = this.FindControl<ScrollViewer>("MessageScrollViewer");
        if (sv != null)
        {
            sv.PropertyChanged += (_, args) =>
            {
                if (args.Property != ScrollViewer.ViewportProperty) return;
                var viewport = sv.Viewport;
                // ItemsControl を探す
                var ic = sv.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault();
                var panel = ic?.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault();
                Util.Logger.Log(
                    $"[ChatView] SV.Viewport={viewport.Width:F0}x{viewport.Height:F0} " +
                    $"IC.Bounds={ic?.Bounds.Width:F0} " +
                    $"Panel.Bounds={panel?.Bounds.Width:F0}");
            };
        }
    }

    /// <summary>Enter キーでメッセージ送信。Shift+Enter は改行。</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (DataContext is ChatViewModel vm)
            {
                vm.SendMessageCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
