using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Ferry.ViewModels;

namespace Ferry.Views;

public partial class TransferPanel : UserControl
{
    public TransferPanel()
    {
        InitializeComponent();

        // ドラッグ＆ドロップのイベント登録
        var dropArea = this.FindControl<Border>("DropArea");
        if (dropArea is not null)
        {
            dropArea.AddHandler(DragDrop.DropEvent, OnDrop);
            dropArea.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not TransferViewModel vm)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;

        var filePaths = files
            .OfType<IStorageFile>()
            .Select(f => f.Path.LocalPath)
            .Where(p => System.IO.File.Exists(p))
            .ToArray();

        if (filePaths.Length > 0 && vm.SendFilesCommand.CanExecute(filePaths))
        {
            vm.SendFilesCommand.Execute(filePaths);
        }
    }
}
