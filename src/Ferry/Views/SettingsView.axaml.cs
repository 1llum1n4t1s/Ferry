using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ferry.ViewModels;

namespace Ferry.Views;

/// <summary>
/// 設定ビュー。保存先フォルダの選択ダイアログとロケール ComboBox を管理する。
/// </summary>
public partial class SettingsView : UserControl
{
    // OnUnloaded で対称に解除するため、購読対象の参照とハンドラを保持する
    // v1.0.38: _browseReceivePathButton は ReceiveFileSavePath 削除に伴い撤去
    private ComboBox? _localeCombo;
    private SettingsViewModel? _subscribedVm;
    private EventHandler<SelectionChangedEventArgs>? _localeChanged;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // ロケール ComboBox の初期化
        _localeCombo = this.FindControl<ComboBox>("LocaleComboBox");
        if (_localeCombo != null)
        {
            _localeCombo.ItemsSource = App.LocaleOptions;
            _localeCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayName");

            if (DataContext is SettingsViewModel vm)
            {
                // 現在のロケールを選択
                var current = App.LocaleOptions.FirstOrDefault(l => l.Key == vm.SelectedLocale);
                if (current != null) _localeCombo.SelectedItem = current;

                _localeChanged = (_, _) =>
                {
                    if (_localeCombo.SelectedItem is LocaleItem item)
                        vm.SelectedLocale = item.Key;
                };
                _localeCombo.SelectionChanged += _localeChanged;
            }
        }

        // N-1: FontSizeComboBox 関連は AccentColor / FontSize の死蔵プロパティ削除に伴い撤去済み

        // BrowseSaveDirectory イベント購読
        if (DataContext is SettingsViewModel svm)
        {
            svm.BrowseSaveDirectoryRequested += OnBrowseSaveDirectoryRequested;
            _subscribedVm = svm;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        // OnLoaded で += したものを対称に解除する。
        // 再 Load される可能性があるので、解除後はフィールドを null クリアして次回の二重購読を防ぐ
        if (_localeCombo != null && _localeChanged != null)
        {
            _localeCombo.SelectionChanged -= _localeChanged;
        }
        _localeChanged = null;

        if (_subscribedVm != null)
        {
            _subscribedVm.BrowseSaveDirectoryRequested -= OnBrowseSaveDirectoryRequested;
            _subscribedVm = null;
        }
    }

    private async void OnBrowseSaveDirectoryRequested(object? sender, EventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || DataContext is not SettingsViewModel vm) return;

        var dirs = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = App.Text("Settings.SaveDirectory"),
        });

        if (dirs.Count > 0)
        {
            var path = dirs[0].TryGetLocalPath();
            if (path != null)
                vm.SaveDirectory = path;
        }
    }

    // v1.0.38: OnBrowseReceivePathClick は ReceiveFileSavePath 削除に伴い撤去
}
