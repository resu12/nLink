using System;
using System.Globalization;
using NLink.Core.FileTransfer;

namespace NLink.App.ViewModels;

public sealed record FileTransferPanelItemViewModel(
    string TransferId,
    FileTransferDirection Direction,
    FileTransferTransferState State,
    string FileName,
    long FileSizeBytes,
    string FileSizeText,
    double ProgressFraction,
    string ProgressText,
    string StatusText,
    string? SavedFilePath,
    string? SavedDirectoryPath,
    string? SavedFileName,
    string? SavedLocationText,
    bool ShowSavedLocation,
    bool ShowProgress,
    bool ShowAccept,
    bool ShowDecline,
    bool ShowCancel,
    bool ShowActions,
    bool IsTerminal)
{
    public static FileTransferPanelItemViewModel? FromSnapshot(FileTransferTransferSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var fileSizeText = FormatByteSize(snapshot.FileSizeBytes);
        var progressText = BuildProgressText(snapshot, fileSizeText);
        var showProgress = snapshot.State is FileTransferTransferState.AwaitingStart
            or FileTransferTransferState.Sending
            or FileTransferTransferState.AwaitingCompletion
            or FileTransferTransferState.Receiving
            or FileTransferTransferState.Verifying;
        var showAccept = snapshot.Direction == FileTransferDirection.Inbound &&
                         snapshot.State == FileTransferTransferState.PendingDecision;
        var showDecline = snapshot.Direction == FileTransferDirection.Inbound &&
                          snapshot.State == FileTransferTransferState.PendingDecision;
        var showCancel = snapshot.State is FileTransferTransferState.Offering
            or FileTransferTransferState.AwaitingAcceptance
            or FileTransferTransferState.AwaitingStart
            or FileTransferTransferState.Sending
            or FileTransferTransferState.AwaitingCompletion
            or FileTransferTransferState.Receiving
            or FileTransferTransferState.Verifying;

        return new FileTransferPanelItemViewModel(
            snapshot.TransferId,
            snapshot.Direction,
            snapshot.State,
            snapshot.FileName,
            snapshot.FileSizeBytes,
            fileSizeText,
            snapshot.ProgressFraction,
            progressText,
            BuildStatusText(snapshot),
            snapshot.SavedFilePath,
            snapshot.SavedDirectoryPath,
            snapshot.SavedFileName,
            BuildSavedLocationText(snapshot),
            ShowSavedLocation: snapshot.Direction == FileTransferDirection.Inbound &&
                               snapshot.State == FileTransferTransferState.Completed &&
                               !string.IsNullOrWhiteSpace(snapshot.SavedDirectoryPath),
            showProgress,
            showAccept,
            showDecline,
            showCancel,
            ShowActions: showAccept || showDecline || showCancel,
            IsTerminal: snapshot.IsTerminal);
    }

    private static string BuildStatusText(FileTransferTransferSnapshot snapshot)
    {
        var mappedTerminalStatus = TryMapTerminalStatusText(snapshot);
        if (!string.IsNullOrWhiteSpace(mappedTerminalStatus))
        {
            return mappedTerminalStatus;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.StatusMessage))
        {
            return snapshot.StatusMessage!;
        }

        return snapshot.State switch
        {
            FileTransferTransferState.Offering => "Preparing file offer...",
            FileTransferTransferState.AwaitingAcceptance => "Waiting for receiver...",
            FileTransferTransferState.PendingDecision => "Incoming file offer",
            FileTransferTransferState.AwaitingStart => "Preparing to receive...",
            FileTransferTransferState.Sending => "Sending...",
            FileTransferTransferState.AwaitingCompletion => "Waiting for completion...",
            FileTransferTransferState.Receiving => "Receiving...",
            FileTransferTransferState.Verifying => "Verifying file...",
            FileTransferTransferState.Completed => "Transfer complete",
            FileTransferTransferState.Declined => "Transfer declined",
            FileTransferTransferState.Canceled => "Transfer canceled",
            FileTransferTransferState.Failed => "Transfer failed",
            _ => "Ready",
        };
    }

    private static string? TryMapTerminalStatusText(FileTransferTransferSnapshot snapshot)
    {
        if (snapshot.State == FileTransferTransferState.Declined &&
            string.Equals(snapshot.ErrorCode, FileTransferResultCodes.Busy, StringComparison.Ordinal))
        {
            return "Peer is busy";
        }

        if (snapshot.State == FileTransferTransferState.Canceled)
        {
            return snapshot.ErrorCode switch
            {
                FileTransferResultCodes.CanceledRemote => "Canceled by peer",
                FileTransferResultCodes.CanceledLocal => "Canceled",
                _ => snapshot.StatusMessage,
            };
        }

        if (snapshot.State != FileTransferTransferState.Failed)
        {
            return null;
        }

        return snapshot.ErrorCode switch
        {
            FileTransferResultCodes.InvalidState => "Transfer failed",
            FileTransferResultCodes.SessionMismatch => "Session mismatch",
            FileTransferResultCodes.IntegrityMismatch => "Integrity check failed",
            FileTransferResultCodes.SizeMismatch => "File size mismatch",
            FileTransferResultCodes.WriteOpenFailed => "Couldn't open destination",
            FileTransferResultCodes.WriteFailed => "Write failed",
            FileTransferResultCodes.FinalizeFailed => "Couldn't save file",
            FileTransferResultCodes.PayloadBudgetExceeded => "Transfer payload too large",
            FileTransferResultCodes.ReadFailed => "Couldn't read file",
            FileTransferResultCodes.TransportDisconnected => "Connection lost",
            FileTransferResultCodes.TransportDetached => "Transfer stopped",
            _ => snapshot.StatusMessage,
        };
    }

    private static string BuildProgressText(FileTransferTransferSnapshot snapshot, string fileSizeText)
    {
        if (snapshot.FileSizeBytes <= 0)
        {
            return fileSizeText;
        }

        var transferred = FormatByteSize(snapshot.BytesTransferred);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{transferred} / {fileSizeText}");
    }

    private static string? BuildSavedLocationText(FileTransferTransferSnapshot snapshot)
    {
        if (snapshot.Direction != FileTransferDirection.Inbound ||
            snapshot.State != FileTransferTransferState.Completed ||
            string.IsNullOrWhiteSpace(snapshot.SavedDirectoryPath))
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Saved to {snapshot.SavedDirectoryPath}");
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        var value = (double)bytes;
        var units = new[] { "KB", "MB", "GB", "TB" };
        var unitIndex = -1;
        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value:0.#} {units[Math.Max(unitIndex, 0)]}");
    }
}
