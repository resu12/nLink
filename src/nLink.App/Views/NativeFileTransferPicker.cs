using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NLink.Core.FileTransfer;

namespace NLink.App.Views;

internal static class NativeFileTransferPicker
{
    public static async Task<FileTransferPickerSelection?> PickSingleFileAsync(UserControl owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (TopLevel.GetTopLevel(owner) is not TopLevel topLevel || topLevel.StorageProvider is null)
        {
            throw new InvalidOperationException("Can't open files right now.");
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select file to send",
            AllowMultiple = false,
        });

        if (files.Count == 0)
        {
            return null;
        }

        return await CreateSelectionAsync(files[0], ct);
    }

    private static async Task<FileTransferPickerSelection> CreateSelectionAsync(IStorageFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        await using var stream = await file.OpenReadAsync();
        long fileSizeBytes;
        try
        {
            fileSizeBytes = stream.Length;
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException("The selected file could not be read.", ex);
        }

        if (fileSizeBytes < 0)
        {
            throw new InvalidOperationException("The selected file size is invalid.");
        }

        var fileName = string.IsNullOrWhiteSpace(file.Name) ? "file" : file.Name.Trim();
        return new FileTransferPickerSelection(
            new FileTransferSendDescriptor(fileName, fileSizeBytes),
            _ => file.OpenReadAsync());
    }
}

internal sealed record FileTransferPickerSelection(
    FileTransferSendDescriptor Descriptor,
    FileTransferReadStreamFactory OpenReadStreamAsync);
