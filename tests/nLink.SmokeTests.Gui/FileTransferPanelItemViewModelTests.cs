using CommunityToolkit.Mvvm.Input;
using NLink.App.ViewModels;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Gui")]
public sealed class FileTransferPanelItemViewModelTests
{
    [Fact]
    public void InboundPendingDecision_ShowsOfferActions_WithoutProgress()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.PendingDecision,
                FileName: "report.pdf",
                FileSizeBytes: 2048,
                Sha256Base64: null,
                BytesTransferred: 0,
                ChunksTransferred: 0,
                ChunkCount: 0,
                ChunkSizeBytes: 0,
                ErrorCode: null,
                StatusMessage: null));

        Assert.NotNull(item);
        Assert.True(item!.ShowAccept);
        Assert.True(item.ShowDecline);
        Assert.False(item.ShowCancel);
        Assert.False(item.ShowPause);
        Assert.False(item.ShowResume);
        Assert.True(item.ShowActions);
        Assert.False(item.ShowProgress);
        Assert.False(item.ShowRiskWarning);
        Assert.Equal(string.Empty, item.RiskWarningText);
        Assert.False(item.IsTerminal);
    }

    [Theory]
    [InlineData("installer.exe", FileTransferFileRiskLevel.ExecutableOrScript, "run commands")]
    [InlineData("payload.zip", FileTransferFileRiskLevel.Archive, "Archives")]
    public void InboundPendingDecision_ShowsRiskWarning_ForRiskyFileNames(
        string fileName,
        FileTransferFileRiskLevel expectedLevel,
        string expectedWarningFragment)
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.PendingDecision,
                FileName: fileName,
                FileSizeBytes: 2048,
                Sha256Base64: null,
                BytesTransferred: 0,
                ChunksTransferred: 0,
                ChunkCount: 0,
                ChunkSizeBytes: 0,
                ErrorCode: null,
                StatusMessage: null));

        var risk = FileTransferFileRiskClassifier.Assess(fileName);
        Assert.Equal(expectedLevel, risk.Level);
        Assert.NotNull(item);
        Assert.True(item!.ShowAccept);
        Assert.True(item.ShowDecline);
        Assert.True(item.ShowRiskWarning);
        Assert.Contains(expectedWarningFragment, item.RiskWarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.PendingDecision, "report.pdf")]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.AwaitingAcceptance, "installer.exe")]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Receiving, "installer.exe")]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Completed, "installer.exe")]
    public void FileRiskWarning_ShowsOnlyForPendingInboundRiskyOffers(
        FileTransferDirection direction,
        FileTransferTransferState state,
        string fileName)
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: direction,
                State: state,
                FileName: fileName,
                FileSizeBytes: 2048,
                Sha256Base64: null,
                BytesTransferred: state == FileTransferTransferState.Completed ? 2048 : 0,
                ChunksTransferred: 0,
                ChunkCount: 0,
                ChunkSizeBytes: 0,
                ErrorCode: null,
                StatusMessage: null));

        Assert.NotNull(item);
        Assert.False(item!.ShowRiskWarning);
        Assert.Equal(string.Empty, item.RiskWarningText);
    }

    [Fact]
    public void InboundPendingDecision_ExposesItemActionCommands()
    {
        var acceptCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);
        var declineCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);
        var cancelCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);

        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.PendingDecision,
                FileName: "report.pdf",
                FileSizeBytes: 2048,
                Sha256Base64: null,
                BytesTransferred: 0,
                ChunksTransferred: 0,
                ChunkCount: 0,
                ChunkSizeBytes: 0,
                ErrorCode: null,
                StatusMessage: null),
            acceptCommand,
            declineCommand,
            cancelCommand);

        Assert.NotNull(item);
        Assert.True(item!.ShowAccept);
        Assert.True(item.ShowDecline);
        Assert.Same(acceptCommand, item.AcceptCommand);
        Assert.Same(declineCommand, item.DeclineCommand);
        Assert.Null(item.CancelCommand);
        Assert.Null(item.PauseCommand);
        Assert.Null(item.ResumeCommand);
    }

    [Fact]
    public void ActiveTransfer_ShowsProgress_AndCancel()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Outbound,
                State: FileTransferTransferState.Sending,
                FileName: "archive.bin",
                FileSizeBytes: 4096,
                Sha256Base64: null,
                BytesTransferred: 1024,
                ChunksTransferred: 1,
                ChunkCount: 4,
                ChunkSizeBytes: 1024,
                ErrorCode: null,
                StatusMessage: null));

        Assert.NotNull(item);
        Assert.False(item!.ShowAccept);
        Assert.False(item.ShowDecline);
        Assert.True(item.ShowCancel);
        Assert.True(item.ShowPause);
        Assert.False(item.ShowResume);
        Assert.True(item.ShowActions);
        Assert.True(item.ShowProgress);
        Assert.False(item.IsTerminal);
    }

    [Fact]
    public void ActiveInboundTransfer_ShowsPauseCommand()
    {
        var pauseCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);
        var resumeCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);

        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Receiving,
                FileName: "archive.bin",
                FileSizeBytes: 4096,
                Sha256Base64: null,
                BytesTransferred: 1024,
                ChunksTransferred: 1,
                ChunkCount: 4,
                ChunkSizeBytes: 1024,
                ErrorCode: null,
                StatusMessage: null),
            pauseCommand: pauseCommand,
            resumeCommand: resumeCommand);

        Assert.NotNull(item);
        Assert.True(item!.ShowPause);
        Assert.False(item.ShowResume);
        Assert.True(item.ShowCancel);
        Assert.Same(pauseCommand, item.PauseCommand);
        Assert.Null(item.ResumeCommand);
    }

    [Fact]
    public void PausedTransfer_ShowsResumeCommand_AndCancel()
    {
        var pauseCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);
        var resumeCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);

        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Outbound,
                State: FileTransferTransferState.Sending,
                FileName: "archive.bin",
                FileSizeBytes: 4096,
                Sha256Base64: null,
                BytesTransferred: 1024,
                ChunksTransferred: 1,
                ChunkCount: 4,
                ChunkSizeBytes: 1024,
                ErrorCode: null,
                StatusMessage: "Transfer paused.",
                IsPaused: true,
                PauseReason: "ui_pause"),
            pauseCommand: pauseCommand,
            resumeCommand: resumeCommand);

        Assert.NotNull(item);
        Assert.False(item!.ShowPause);
        Assert.True(item.ShowResume);
        Assert.True(item.ShowCancel);
        Assert.Equal("Paused", item.StatusText);
        Assert.Null(item.PauseCommand);
        Assert.Same(resumeCommand, item.ResumeCommand);
    }

    [Fact]
    public void PeerPausedTransfer_ShowsPeerPausedStatus_WithoutPauseOrResume()
    {
        var pauseCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);
        var resumeCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);

        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Receiving,
                FileName: "archive.bin",
                FileSizeBytes: 4096,
                Sha256Base64: null,
                BytesTransferred: 1024,
                ChunksTransferred: 1,
                ChunkCount: 4,
                ChunkSizeBytes: 1024,
                ErrorCode: null,
                StatusMessage: "Peer paused transfer.",
                IsPeerPaused: true,
                PeerPauseReason: "sender_pause"),
            pauseCommand: pauseCommand,
            resumeCommand: resumeCommand);

        Assert.NotNull(item);
        Assert.False(item!.ShowPause);
        Assert.False(item.ShowResume);
        Assert.True(item.ShowCancel);
        Assert.True(item.ShowActions);
        Assert.Equal("Paused by peer", item.StatusText);
        Assert.Null(item.PauseCommand);
        Assert.Null(item.ResumeCommand);
    }

    [Fact]
    public void TerminalTransfer_HidesProgress_AndActions()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Completed,
                FileName: "done.txt",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 128,
                ChunksTransferred: 1,
                ChunkCount: 1,
                ChunkSizeBytes: 128,
                ErrorCode: null,
                StatusMessage: null,
                SavedFilePath: @"C:\temp\done.txt",
                SavedDirectoryPath: @"C:\temp",
                SavedFileName: "done.txt"));

        Assert.NotNull(item);
        Assert.False(item!.ShowAccept);
        Assert.False(item.ShowDecline);
        Assert.False(item.ShowCancel);
        Assert.False(item.ShowPause);
        Assert.False(item.ShowResume);
        Assert.False(item.ShowActions);
        Assert.False(item.ShowProgress);
        Assert.True(item.IsTerminal);
        Assert.True(item.ShowSavedLocation);
        Assert.Equal(@"Saved to C:\temp", item.SavedLocationText);
    }

    [Fact]
    public void TerminalTransfer_DoesNotExposeOfferActionCommands()
    {
        var acceptCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);
        var declineCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);
        var cancelCommand = new AsyncRelayCommand<string?>(_ => Task.CompletedTask);

        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Completed,
                FileName: "done.txt",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 128,
                ChunksTransferred: 1,
                ChunkCount: 1,
                ChunkSizeBytes: 128,
                ErrorCode: null,
                StatusMessage: null),
            acceptCommand,
            declineCommand,
            cancelCommand);

        Assert.NotNull(item);
        Assert.False(item!.ShowAccept);
        Assert.False(item.ShowDecline);
        Assert.False(item.ShowCancel);
        Assert.False(item.ShowPause);
        Assert.False(item.ShowResume);
        Assert.Null(item.AcceptCommand);
        Assert.Null(item.DeclineCommand);
        Assert.Null(item.CancelCommand);
        Assert.Null(item.PauseCommand);
        Assert.Null(item.ResumeCommand);
    }

    [Fact]
    public void FailedTransfer_MapsKnownErrorCodeToConciseStatus()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Failed,
                FileName: "bad.bin",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 128,
                ChunksTransferred: 1,
                ChunkCount: 1,
                ChunkSizeBytes: 128,
                ErrorCode: FileTransferResultCodes.IntegrityMismatch,
                StatusMessage: "File hash verification failed."));

        Assert.NotNull(item);
        Assert.Equal("Integrity check failed", item!.StatusText);
    }

    [Fact]
    public void CanceledTransfer_MapsRemoteCancelToPeerStatus()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Outbound,
                State: FileTransferTransferState.Canceled,
                FileName: "bad.bin",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 64,
                ChunksTransferred: 1,
                ChunkCount: 2,
                ChunkSizeBytes: 64,
                ErrorCode: FileTransferResultCodes.CanceledRemote,
                StatusMessage: "canceled_local"));

        Assert.NotNull(item);
        Assert.Equal("Canceled by peer", item!.StatusText);
    }

    [Fact]
    public void CanceledTransfer_MapsLocalCancelToLocalStatus()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Outbound,
                State: FileTransferTransferState.Canceled,
                FileName: "bad.bin",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 64,
                ChunksTransferred: 1,
                ChunkCount: 2,
                ChunkSizeBytes: 64,
                ErrorCode: FileTransferResultCodes.CanceledLocal,
                StatusMessage: "receiver_canceled"));

        Assert.NotNull(item);
        Assert.Equal("Canceled", item!.StatusText);
    }

    [Fact]
    public void DeclinedTransfer_MapsBusyReasonToBusyStatus()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Outbound,
                State: FileTransferTransferState.Declined,
                FileName: "bad.bin",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 0,
                ChunksTransferred: 0,
                ChunkCount: 0,
                ChunkSizeBytes: 0,
                ErrorCode: FileTransferResultCodes.Busy,
                StatusMessage: "busy"));

        Assert.NotNull(item);
        Assert.Equal("Peer is busy", item!.StatusText);
    }

    [Fact]
    public void FailedTransfer_MapsFinalizeFailureToSaveStatus()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Failed,
                FileName: "bad.bin",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 128,
                ChunksTransferred: 1,
                ChunkCount: 1,
                ChunkSizeBytes: 128,
                ErrorCode: FileTransferResultCodes.FinalizeFailed,
                StatusMessage: "File-transfer destination finalization failed."));

        Assert.NotNull(item);
        Assert.Equal("Couldn't save file", item!.StatusText);
    }

    [Fact]
    public void FailedTransfer_MapsPayloadBudgetExceededToTransferPayloadTooLarge()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Outbound,
                State: FileTransferTransferState.Failed,
                FileName: "too-large.bin",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 0,
                ChunksTransferred: 0,
                ChunkCount: 1,
                ChunkSizeBytes: 128,
                ErrorCode: FileTransferResultCodes.PayloadBudgetExceeded,
                StatusMessage: "Bridge payload too large for 'send' (max 65536 bytes)."));

        Assert.NotNull(item);
        Assert.Equal("Transfer payload too large", item!.StatusText);
    }

    [Fact]
    public void FailedTransfer_MapsTransportIncompatibleToUpgradeStatus()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Outbound,
                State: FileTransferTransferState.Failed,
                FileName: "bad.bin",
                FileSizeBytes: 128,
                Sha256Base64: null,
                BytesTransferred: 0,
                ChunksTransferred: 0,
                ChunkCount: 1,
                ChunkSizeBytes: 128,
                ErrorCode: FileTransferResultCodes.TransportIncompatible,
                StatusMessage: "bridge_protocol_outdated_bulk_missing"));

        Assert.NotNull(item);
        Assert.Equal("Update nLink and retry", item!.StatusText);
    }

    [Fact]
    public void OutboundTransfer_ShowsReceiverAcknowledgedProgressWhenTransportRunsAhead()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Outbound,
                State: FileTransferTransferState.Sending,
                FileName: "archive.bin",
                FileSizeBytes: 57_400_000,
                Sha256Base64: null,
                BytesTransferred: 11_800_000,
                ChunksTransferred: 2881,
                ChunkCount: 14_014,
                ChunkSizeBytes: 4096,
                ErrorCode: null,
                StatusMessage: "Sending file data.",
                BytesAcceptedForTransport: 13_200_000,
                BytesAcknowledgedByReceiver: 11_800_000));

        Assert.NotNull(item);
        Assert.Equal("11.3 MB / 54.7 MB", item!.ProgressText);
        Assert.Equal(11_800_000d / 57_400_000d, item.ProgressFraction, 6);
    }

    [Fact]
    public void LargeTransfer_ShowsGbProgressWithTensOfMbPrecision()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-large",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Receiving,
                FileName: "disk-image.bin",
                FileSizeBytes: 5_583_457_484,
                Sha256Base64: null,
                BytesTransferred: 2_842_594_713,
                ChunksTransferred: 43_741,
                ChunkCount: 85_800,
                ChunkSizeBytes: 65_536,
                ErrorCode: null,
                StatusMessage: null));

        Assert.NotNull(item);
        Assert.Equal("2.65 GB / 5.20 GB", item!.ProgressText);
        Assert.Equal("5.20 GB", item.FileSizeText);
    }

    [Fact]
    public void InboundSparseTransfer_ShowsCommittedProgressWhenSparseWritesRunAhead()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-sparse",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Receiving,
                FileName: "movie.mkv",
                FileSizeBytes: 134_217_728,
                Sha256Base64: null,
                BytesTransferred: 10_485_760,
                ChunksTransferred: 160,
                ChunkCount: 2048,
                ChunkSizeBytes: 65_536,
                ErrorCode: null,
                StatusMessage: "Receiving V6 file data.",
                BytesAcceptedForTransport: 74_448_896));

        Assert.NotNull(item);
        Assert.Equal("10 MB / 128 MB", item!.ProgressText);
        Assert.Equal(10_485_760d / 134_217_728d, item.ProgressFraction, 6);
    }

    [Fact]
    public void ActiveTransfer_PrefersConciseStateText_OverVerboseRuntimeStatus()
    {
        var item = FileTransferPanelItemViewModel.FromSnapshot(
            new FileTransferTransferSnapshot(
                SessionId: "session-a",
                TransferId: "transfer-a",
                Direction: FileTransferDirection.Inbound,
                State: FileTransferTransferState.Receiving,
                FileName: "archive.bin",
                FileSizeBytes: 4096,
                Sha256Base64: null,
                BytesTransferred: 1024,
                ChunksTransferred: 1,
                ChunkCount: 4,
                ChunkSizeBytes: 1024,
                ErrorCode: null,
                StatusMessage: "Receiving requested chunks."));

        Assert.NotNull(item);
        Assert.Equal("Receiving...", item!.StatusText);
    }
}
