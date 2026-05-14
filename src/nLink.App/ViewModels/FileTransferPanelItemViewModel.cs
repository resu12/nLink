using System;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
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
    bool ShowRiskWarning,
    string RiskWarningText,
    bool ShowAccept,
    bool ShowDecline,
    bool ShowCancel,
    bool ShowPause,
    bool ShowResume,
    bool ShowActions,
    IAsyncRelayCommand<string?>? AcceptCommand,
    IAsyncRelayCommand<string?>? DeclineCommand,
    IAsyncRelayCommand<string?>? CancelCommand,
    IAsyncRelayCommand<string?>? PauseCommand,
    IAsyncRelayCommand<string?>? ResumeCommand,
    bool IsTerminal)
{
    public static FileTransferPanelItemViewModel? FromSnapshot(
        FileTransferTransferSnapshot? snapshot,
        IAsyncRelayCommand<string?>? acceptCommand = null,
        IAsyncRelayCommand<string?>? declineCommand = null,
        IAsyncRelayCommand<string?>? cancelCommand = null,
        IAsyncRelayCommand<string?>? pauseCommand = null,
        IAsyncRelayCommand<string?>? resumeCommand = null)
    {
        if (snapshot is null)
        {
            return null;
        }

        var fileSizeText = FormatByteSize(snapshot.FileSizeBytes);
        var progressText = BuildProgressText(snapshot, fileSizeText);
        var showProgress = snapshot.State is FileTransferTransferState.AwaitingMetadata
            or FileTransferTransferState.PreparingMetadata
            or FileTransferTransferState.AwaitingStart
            or FileTransferTransferState.Sending
            or FileTransferTransferState.AwaitingCompletion
            or FileTransferTransferState.Receiving
            or FileTransferTransferState.Verifying;
        var showAccept = snapshot.Direction == FileTransferDirection.Inbound &&
                         snapshot.State == FileTransferTransferState.PendingDecision;
        var showDecline = snapshot.Direction == FileTransferDirection.Inbound &&
                          snapshot.State == FileTransferTransferState.PendingDecision;
        var fileRisk = showAccept
            ? FileTransferFileRiskClassifier.Assess(snapshot.FileName)
            : FileTransferFileRiskAssessment.None;
        var showCancel = snapshot.State is FileTransferTransferState.Offering
            or FileTransferTransferState.AwaitingAcceptance
            or FileTransferTransferState.AwaitingMetadata
            or FileTransferTransferState.PreparingMetadata
            or FileTransferTransferState.AwaitingStart
            or FileTransferTransferState.Sending
            or FileTransferTransferState.AwaitingCompletion
            or FileTransferTransferState.Receiving
            or FileTransferTransferState.Verifying;
        var canPauseResume = CanPauseResume(snapshot);
        var effectivePaused = snapshot.IsPaused || snapshot.IsPeerPaused;
        var showPause = canPauseResume && !effectivePaused;
        var showResume = canPauseResume && snapshot.IsPaused;

        return new FileTransferPanelItemViewModel(
            snapshot.TransferId,
            snapshot.Direction,
            snapshot.State,
            snapshot.FileName,
            snapshot.FileSizeBytes,
            fileSizeText,
            BuildVisibleProgressFraction(snapshot),
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
            fileRisk.IsRisky,
            fileRisk.WarningText,
            showAccept,
            showDecline,
            showCancel,
            showPause,
            showResume,
            ShowActions: showAccept || showDecline || showCancel || showPause || showResume,
            AcceptCommand: showAccept ? acceptCommand : null,
            DeclineCommand: showDecline ? declineCommand : null,
            CancelCommand: showCancel ? cancelCommand : null,
            PauseCommand: showPause ? pauseCommand : null,
            ResumeCommand: showResume ? resumeCommand : null,
            IsTerminal: snapshot.IsTerminal);
    }

    private static string BuildStatusText(FileTransferTransferSnapshot snapshot)
    {
        var mappedTerminalStatus = TryMapTerminalStatusText(snapshot);
        if (!string.IsNullOrWhiteSpace(mappedTerminalStatus))
        {
            return mappedTerminalStatus;
        }

        if (snapshot.IsPaused && !snapshot.IsTerminal)
        {
            return "Paused";
        }

        if (snapshot.IsPeerPaused && !snapshot.IsTerminal)
        {
            return "Paused by peer";
        }

        var stateText = snapshot.State switch
        {
            FileTransferTransferState.Offering => "Preparing file offer...",
            FileTransferTransferState.AwaitingAcceptance => "Waiting for receiver...",
            FileTransferTransferState.AwaitingMetadata => "Waiting for sender to prepare the file...",
            FileTransferTransferState.PreparingMetadata => "Preparing file metadata...",
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
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(stateText))
        {
            return stateText;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.StatusMessage))
        {
            return snapshot.StatusMessage!;
        }

        return "Ready";
    }

    private static bool CanPauseResume(FileTransferTransferSnapshot snapshot)
        => !snapshot.IsTerminal &&
           snapshot.Direction switch
           {
               FileTransferDirection.Outbound => snapshot.State is FileTransferTransferState.AwaitingAcceptance
                   or FileTransferTransferState.PreparingMetadata
                   or FileTransferTransferState.AwaitingStart
                   or FileTransferTransferState.Sending,
               FileTransferDirection.Inbound => snapshot.State is FileTransferTransferState.AwaitingMetadata
                   or FileTransferTransferState.AwaitingStart
                   or FileTransferTransferState.Receiving,
               _ => false,
           };

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
            FileTransferResultCodes.TransportIncompatible => "Update nLink and retry",
            FileTransferResultCodes.PeerDisconnected => "Peer disconnected",
            FileTransferResultCodes.ControlChannelStalled => "Connection stalled",
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

        if (snapshot.Direction == FileTransferDirection.Outbound)
        {
            var sent = FormatByteSize(GetVisibleProgressBytes(snapshot));
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{sent} / {fileSizeText}");
        }

        var transferred = FormatByteSize(GetVisibleProgressBytes(snapshot));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{transferred} / {fileSizeText}");
    }

    private static double BuildVisibleProgressFraction(FileTransferTransferSnapshot snapshot)
    {
        if (snapshot.FileSizeBytes <= 0)
        {
            return 0d;
        }

        return Math.Clamp((double)GetVisibleProgressBytes(snapshot) / snapshot.FileSizeBytes, 0d, 1d);
    }

    private static long GetVisibleProgressBytes(FileTransferTransferSnapshot snapshot)
    {
        if (snapshot.Direction == FileTransferDirection.Outbound)
        {
            return Math.Max(0L, snapshot.BytesAcknowledgedByReceiver ?? snapshot.BytesTransferred);
        }

        return Math.Max(0L, snapshot.BytesTransferred);
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

        var format = unitIndex >= 2 ? "0.00" : "0.#";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[Math.Max(unitIndex, 0)]}");
    }
}
