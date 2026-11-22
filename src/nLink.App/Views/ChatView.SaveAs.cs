using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NLink.App.ViewModels;
using NLink.Core.SessionSecurity;

namespace NLink.App.Views;

public partial class ChatView
{
    private bool choosingDestination;
    internal Func<string, Task<string?>>? SavePickerForTests { get; set; }

    private async void SaveAsFileTransferButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (choosingDestination || sender is not Button { DataContext: FileTransferPanelItemViewModel item } ||
            DataContext is not IChatFileTransferActions receiver || !StillPending())
            return;

        choosingDestination = true;
        try
        {
            SetFileActionHint(null);
            // Never pass a peer-supplied path to the OS dialog as a suggested filename.
            var check = SessionFileTransferGuard.ValidateSafeFileName(item.FileName, out var suggestedName);
            if (!check.IsAllowed)
            {
                SetFileActionHint("This offer has an invalid filename.", true);
                return;
            }
            var path = await (SavePickerForTests?.Invoke(suggestedName) ?? PickReceivePathAsync(suggestedName));
            if (path is null || !StillPending())
                return;
            if (await receiver.AcceptIncomingAsAsync(item.SessionId, item.TransferId, item.InboundOfferToken, path) is null && StillPending())
                SetFileActionHint("This offer can't be accepted right now.", true);
        }
        catch (IOException ex)
        {
            if (StillPending()) SetFileActionHint(ex.Message, true);
        }
        catch (Exception)
        {
            if (StillPending()) SetFileActionHint("Couldn't use that destination. Choose another file.", true);
        }
        finally
        {
            choosingDestination = false;
        }

        bool StillPending() =>
            this.IsAttachedToVisualTree() && DataContext == receiver &&
            receiver.InboundFileTransfer is { ShowAccept: true } current &&
            current.TransferId == item.TransferId && current.SessionId == item.SessionId &&
            item.InboundOfferToken != Guid.Empty && current.InboundOfferToken == item.InboundOfferToken &&
            receiver.AcceptIncomingFileCommand.CanExecute(item.TransferId);
    }

    private async Task<string?> PickReceivePathAsync(string suggestedName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
            throw new IOException("The save dialog is unavailable.");
        using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save incoming file as",
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = false, // An occupied name is rejected, never replaced.
        });
        return file is null ? null : file.TryGetLocalPath() ?? throw new IOException("Choose a filesystem destination.");
    }
}
