using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Ferry.ViewModels;

namespace Ferry.Views;

public partial class SettingsPanel : UserControl
{
    private SettingsViewModel? _subscribedVm;

    public SettingsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // 前の ViewModel からイベントを解除（重複登録を防ぐ）
        if (_subscribedVm != null)
        {
            _subscribedVm.BrowseSaveDirectoryRequested -= OnBrowseSaveDirectoryRequested;
            _subscribedVm = null;
        }

        if (DataContext is SettingsViewModel vm)
        {
            vm.BrowseSaveDirectoryRequested += OnBrowseSaveDirectoryRequested;
            _subscribedVm = vm;
        }
    }

    private async void OnBrowseSaveDirectoryRequested(object? sender, System.EventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || DataContext is not SettingsViewModel vm)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "受信ファイルの保存先を選択",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            vm.SaveDirectory = folders[0].Path.LocalPath;
        }
    }
}
