using Avalonia.Controls;
using Ferry.ViewModels;

namespace Ferry.Views;

public partial class AddMemberPanel : UserControl
{
    public AddMemberPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// DataContext が設定されたタイミングで自動的にペアリングセッションを開始する。
    /// TabControl 内では OnAttachedToVisualTree 時点で DataContext がまだ null のため、
    /// DataContextChanged イベントを使用する。
    /// </summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ConnectionViewModel vm && vm.QrCodeImage == null)
        {
            vm.StartSessionCommand.Execute(null);
        }
    }
}
