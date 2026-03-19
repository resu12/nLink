using NLink.App.ViewModels;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

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
        Assert.True(item.ShowActions);
        Assert.False(item.ShowProgress);
        Assert.False(item.IsTerminal);
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
        Assert.True(item.ShowActions);
        Assert.True(item.ShowProgress);
        Assert.False(item.IsTerminal);
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
        Assert.False(item.ShowActions);
        Assert.False(item.ShowProgress);
        Assert.True(item.IsTerminal);
        Assert.True(item.ShowSavedLocation);
        Assert.Equal(@"Saved to C:\temp", item.SavedLocationText);
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
    public void OutboundTransfer_ShowsSentAndConfirmedProgressText()
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
        Assert.Equal("12.6 MB sent / 11.3 MB confirmed / 54.7 MB", item!.ProgressText);
        Assert.Equal(13_200_000d / 57_400_000d, item.ProgressFraction, 6);
    }
}
