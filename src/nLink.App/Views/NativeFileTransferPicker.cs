using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.App.Views;

internal static class NativeFileTransferPicker
{
    public static async Task<FileTransferPickerSelection?> PickSingleFileAsync(UserControl owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var automationSelection = await TryCreateAutomationSelectionAsync(ct).ConfigureAwait(false);
        if (automationSelection is not null)
        {
            return automationSelection;
        }

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

    internal static async Task<FileTransferPickerSelection?> TryCreateAutomationSelectionForTestsAsync(CancellationToken ct = default)
        => await TryCreateAutomationSelectionAsync(ct).ConfigureAwait(false);

    private static async Task<FileTransferPickerSelection?> TryCreateAutomationSelectionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var nativeRequested = Environment.GetEnvironmentVariable("NLINK_APP_CANONICAL_SOURCE_V1") == "1";
        var path = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", category: "filetransfer_test_harness");
        if (string.IsNullOrWhiteSpace(path))
        {
            if (nativeRequested) throw new IOException("native_sender_source_unavailable");
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch when (!nativeRequested)
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            if (nativeRequested) throw new IOException("native_sender_source_unavailable");
            return null;
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length < 0)
        {
            return null;
        }

        var fileName = string.IsNullOrWhiteSpace(fileInfo.Name) ? "file" : fileInfo.Name.Trim();
        SessionFileTransferService.NativeSenderSelection? native = null;
        if (nativeRequested)
        {
            // The marker requests strict selection, never grants custody. A
            // missing/stale native packet must not fall back to reopening a path.
            native = SessionFileTransferService.FrontierAppLifetime.Current?
                .TrySelectNativeSender(fileName, fileInfo.Length, ct)
                ?? throw new IOException("native_sender_source_unavailable");
        }
        LocalOperationalLog.Info(
            "FileTransferPicker",
            $"event=filetransfer_live_soak_autopick_used; file_size_bytes={fileInfo.Length.ToString(CultureInfo.InvariantCulture)}; path_sha256={ComputePathSha256Hex(fullPath)}");

        await Task.CompletedTask.ConfigureAwait(false);
        if (native is not null) return new FileTransferPickerSelection(native.Descriptor, native.OpenReadStreamAsync);
        return new FileTransferPickerSelection(
            new FileTransferSendDescriptor(fileName, fileInfo.Length),
            _ => Task.FromResult<Stream>(File.OpenRead(fullPath)));
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

    private static string ComputePathSha256Hex(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed record FileTransferPickerSelection(
    FileTransferSendDescriptor Descriptor,
    FileTransferReadStreamFactory OpenReadStreamAsync);
