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
    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // ロケール ComboBox の初期化
        var localeCombo = this.FindControl<ComboBox>("LocaleComboBox");
        if (localeCombo != null)
        {
            localeCombo.ItemsSource = App.LocaleOptions;
            localeCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayName");

            if (DataContext is SettingsViewModel vm)
            {
                // 現在のロケールを選択
                var current = App.LocaleOptions.FirstOrDefault(l => l.Key == vm.SelectedLocale);
                if (current != null) localeCombo.SelectedItem = current;

                localeCombo.SelectionChanged += (_, _) =>
                {
                    if (localeCombo.SelectedItem is LocaleItem item)
                        vm.SelectedLocale = item.Key;
                };
            }
        }

        // フォントサイズ ComboBox の初期化
        var fontSizeCombo = this.FindControl<ComboBox>("FontSizeComboBox");
        if (fontSizeCombo != null && DataContext is SettingsViewModel settingsVm)
        {
            // 現在値に合わせて選択
            var idx = settingsVm.FontSize switch
            {
                "small" => 0,
                "large" => 2,
                _ => 1, // medium
            };
            fontSizeCombo.SelectedIndex = idx;

            fontSizeCombo.SelectionChanged += (_, _) =>
            {
                if (fontSizeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                    settingsVm.FontSize = tag;
            };
        }

        // BrowseSaveDirectory イベント購読
        if (DataContext is SettingsViewModel svm)
        {
            svm.BrowseSaveDirectoryRequested += OnBrowseSaveDirectoryRequested;
        }

        // 受信ファイル保存先の参照ボタン
        var browseReceivePath = this.FindControl<Button>("BrowseReceivePathButton");
        if (browseReceivePath != null)
        {
            browseReceivePath.Click += OnBrowseReceivePathClick;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (DataContext is SettingsViewModel vm)
        {
            vm.BrowseSaveDirectoryRequested -= OnBrowseSaveDirectoryRequested;
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

    private async void OnBrowseReceivePathClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || DataContext is not SettingsViewModel vm) return;

        var dirs = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = App.Text("Settings.ReceiveFileSavePath"),
        });

        if (dirs.Count > 0)
        {
            var path = dirs[0].TryGetLocalPath();
            if (path != null)
                vm.ReceiveFileSavePath = path;
        }
    }
}
