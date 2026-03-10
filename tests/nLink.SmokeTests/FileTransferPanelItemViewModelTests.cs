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
}
