using System;
using System.Buffers.Binary;
using System.IO;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class ChatView
{
    private static readonly DataFormat<byte[]> ShellIdList = DataFormat.CreateBytesPlatformFormat("Shell IDList Array");
    private const string UnsupportedDrop = "Drop one local file from Explorer. Folders and multiple items aren't supported.";
    private const string SendUnavailable = "Sending files isn't available right now.";
    private IDisposable? hideFileActionHint;
    private bool preparingFileDrop;

    private async void ChatFileDrag(object? sender, DragEventArgs e)
    {
        var dropping = e.RoutedEvent == DragDrop.DropEvent;
        var allowsCopy = (e.DragEffects & DragDropEffects.Copy) != 0;
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
        var target = DataContext as IChatFileTransferActions;
        try
        {
            if (preparingFileDrop || target is not { ShowSendFileAction: true, CanSendFileAction: true })
            {
                SetFileActionHint(SendUnavailable, dropping);
                return;
            }

            if (!allowsCopy || SingleLocalFile(e.DataTransfer) is not { } file)
            {
                SetFileActionHint(UnsupportedDrop, dropping);
                return;
            }

            if (!dropping)
            {
                e.DragEffects = DragDropEffects.Copy;
                SetFileActionHint(null);
                return;
            }

            if (!target.TryAuthorizeFileSelection() || target.FileSendSessionId is not { } sessionId)
            {
                SetFileActionHint(SendUnavailable, true);
                return;
            }

            e.DragEffects = DragDropEffects.Copy;
            SetFileActionHint(null);
            preparingFileDrop = true;
            try
            {
                var selection = await NativeFileTransferPicker.CreateSelectionAsync(file);
                if (!this.IsAttachedToVisualTree() || DataContext != target || target.FileSendSessionId != sessionId)
                    return;
                if (!target.TryAuthorizeFileSelection())
                {
                    SetFileActionHint(SendUnavailable, true);
                    return;
                }
                await target.StartSendFileAsync(selection.Descriptor, selection.OpenReadStreamAsync);
            }
            finally
            {
                preparingFileDrop = false;
            }
        }
        catch (Exception)
        {
            // OS drag data or the file can disappear while the user is dropping it.
            if (DataContext == target)
                SetFileActionHint("Couldn't offer the dropped file.", dropping);
        }
    }

    private static IStorageFile? SingleLocalFile(IDataTransfer data)
    {
        var items = data.Items;
        // Win32 may omit unresolvable paths. Require both the resolved file and
        // Explorer's formats item with the original selection count; never infer it.
        if (items.Count != 2 || items[0].TryGetFile() is not IStorageFile file ||
            !items[1].Contains(DataFormat.File) || !items[1].Contains(ShellIdList) ||
            items[1].TryGetValue(ShellIdList) is not { Length: >= 12 } ids ||
            BinaryPrimitives.ReadUInt32LittleEndian(ids) != 1 || items[1].TryGetFile() is not null)
            return null;

        if (file.Path is not { IsAbsoluteUri: true, IsFile: true, IsUnc: false } uri ||
            uri.Host.Length != 0 || file.TryGetLocalPath() is not { } path || !Path.IsPathFullyQualified(path))
            return null;
        if (OperatingSystem.IsWindows() &&
            (Path.GetPathRoot(path) is not { Length: 3 } root ||
             new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram)))
            return null;
        return file;
    }

    private void SetFileActionHint(string? message, bool briefly = false)
    {
        hideFileActionHint?.Dispose();
        hideFileActionHint = null;
        FileActionHint.Text = message;
        FileActionHint.IsVisible = message is not null;
        if (briefly)
            hideFileActionHint = DispatcherTimer.RunOnce(() => SetFileActionHint(null), TimeSpan.FromSeconds(3));
    }
}
