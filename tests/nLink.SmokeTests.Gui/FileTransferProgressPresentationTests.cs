using NLink.App.ViewModels;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Gui")]
public sealed class FileTransferProgressPresentationTests
{
    [Theory]
    [InlineData("regular_nkn_v4_fast", 4, 710)]
    [InlineData("file_tuna_v4", 4, 710)]
    [InlineData("post_tuna_fallback_v6", 6, 100)]
    public void Progress_UsesReportingProjectionOnBothSides(string route, int protocol, long reportable)
    {
        var received = Snapshot(710, FileTransferDirection.Inbound) with
        {
            BytesTransferred = 100, RouteToken = route, ProtocolVersion = protocol,
            PeerComparableProgressBytes = reportable,
        };
        var sent = Snapshot(reportable, FileTransferDirection.Outbound) with
        {
            BytesAcceptedForTransport = 950, RouteToken = route, ProtocolVersion = protocol,
        };
        var inbound = FileTransferPanelItemViewModel.FromSnapshot(received)!;
        var outbound = FileTransferPanelItemViewModel.FromSnapshot(sent)!;
        Assert.Equal(reportable, inbound.ProgressBytes);
        Assert.Equal(inbound.ProgressFraction, outbound.ProgressFraction);
        Assert.Equal(inbound.ProgressText, outbound.ProgressText);
        Assert.Equal(100, received.BytesTransferred); // UI must not advance the protocol frontier.
        Assert.Equal(710, received.ProgressBytes); // Raw sparse-write telemetry is not the display projection.
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(0, 0)]
    [InlineData(2000, 1000)]
    public void Progress_ClampsProjectionAndDoesNotReplaceZeroWithSparseBytes(long projection, long expected)
    {
        var card = FileTransferPanelItemViewModel.FromSnapshot(Snapshot(710) with
        {
            PeerComparableProgressBytes = projection,
        })!;
        Assert.Equal(expected, card.ProgressBytes);
        Assert.Equal((double)expected / 1000, card.ProgressFraction);
        Assert.Equal(FileTransferPanelItemViewModel.FormatProgressText(expected, 1000), card.ProgressText);
    }

    [Fact]
    public void Progress_ReceiverCanLeadUntilAcknowledgmentButSenderNeverUsesQueuedBytes()
    {
        var receiver = Item(700);
        var sender = FileTransferPanelItemViewModel.FromSnapshot(Snapshot(500, FileTransferDirection.Outbound) with
        {
            BytesAcceptedForTransport = 1000,
        })!;
        Assert.Equal(700, receiver.ProgressBytes);
        Assert.Equal(500, sender.ProgressBytes);
    }

    [Fact]
    public void Animation_InterpolatesBarAndLabelWithoutPredictingProgress()
    {
        var p = new FileTransferProgressPresentation();
        p.SetTarget(Item(100), 0, true);
        p.SetTarget(Item(500), 10, true);
        Assert.Equal(100, p.DisplayedBytes);
        p.Advance(110);
        Assert.Equal(300, p.DisplayedBytes);
        Assert.Equal(.3, p.Fraction, 6);
        Assert.Equal("300 B / 1000 B", p.Text);
        p.Advance(210);
        Assert.Equal(500, p.DisplayedBytes);
        Assert.False(p.IsAnimating);
        p.Advance(20_000); // A silent/stalled transport does not create fictional progress.
        Assert.Equal(500, p.DisplayedBytes);
    }

    [Fact]
    public void Animation_RetargetsFromCurrentDisplayAndDoesNotRestartForDuplicateSamples()
    {
        var p = new FileTransferProgressPresentation();
        p.SetTarget(Item(100), 0, true);
        p.SetTarget(Item(500), 0, true);
        p.SetTarget(Item(800), 100, true);
        Assert.Equal(300, p.DisplayedBytes);
        p.SetTarget(Item(800), 200, true);
        Assert.Equal(550, p.DisplayedBytes);
        p.Advance(300);
        Assert.Equal(800, p.DisplayedBytes);
        Assert.False(p.IsAnimating);
    }

    [Fact]
    public void Animation_RouteHandoffCannotRegressPreviouslyConfirmedProgress()
    {
        var p = new FileTransferProgressPresentation();
        p.SetTarget(Item(700), 0, true);
        var handoff = Snapshot(400) with { RouteToken = "post_tuna_fallback_v6", ProtocolVersion = 6 };
        p.SetTarget(FileTransferPanelItemViewModel.FromSnapshot(handoff), 10, true);
        Assert.Equal(700, p.DisplayedBytes);
        Assert.False(p.IsAnimating);
        p.SetTarget(Item(800), 20, true);
        p.Advance(220);
        Assert.Equal(800, p.DisplayedBytes);
    }

    [Fact]
    public void Animation_HandoffRetainsOnlyComparableProgressNeverRawSparseWrites()
    {
        var p = new FileTransferProgressPresentation();
        var snapshot = Snapshot(900) with
        {
            BytesTransferred = 100, PeerComparableProgressBytes = 100,
            RouteToken = "file_tuna_v4", ProtocolVersion = 4,
        };
        p.SetTarget(FileTransferPanelItemViewModel.FromSnapshot(snapshot), 0, true);
        snapshot = snapshot with { RouteToken = "post_tuna_fallback_v6", ProtocolVersion = 6 };
        p.SetTarget(FileTransferPanelItemViewModel.FromSnapshot(snapshot), 10, true);
        p.Advance(1000);
        Assert.Equal(100, p.DisplayedBytes);
        snapshot = snapshot with { BytesTransferred = 400, PeerComparableProgressBytes = 400 };
        p.SetTarget(FileTransferPanelItemViewModel.FromSnapshot(snapshot), 1100, true);
        p.Advance(1300);
        Assert.Equal(400, p.DisplayedBytes);
        Assert.Equal("400 B / 1000 B", p.Text);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("transfer")]
    [InlineData("direction")]
    [InlineData("size")]
    public void Animation_ReplacementResetsInsteadOfInheritingPreviousProgress(string changed)
    {
        var p = new FileTransferProgressPresentation();
        p.SetTarget(Item(700), 0, true);
        var next = Snapshot(20);
        next = changed switch
        {
            "session" => next with { SessionId = "next-session" },
            "transfer" => next with { TransferId = "next-transfer" },
            "direction" => next with { Direction = FileTransferDirection.Outbound },
            _ => next with { FileSizeBytes = 2000 },
        };
        p.SetTarget(FileTransferPanelItemViewModel.FromSnapshot(next), 10, true);
        Assert.Equal(20, p.DisplayedBytes);
        Assert.False(p.IsAnimating);
    }

    [Theory]
    [InlineData(FileTransferTransferState.Receiving, true, false, "Paused")]
    [InlineData(FileTransferTransferState.Receiving, false, true, "Paused by peer")]
    [InlineData(FileTransferTransferState.Verifying, false, false, "Verifying file...")]
    [InlineData(FileTransferTransferState.AwaitingCompletion, false, false, "Waiting for completion...")]
    [InlineData(FileTransferTransferState.Completed, false, false, "Transfer complete")]
    [InlineData(FileTransferTransferState.Canceled, false, false, "Transfer canceled")]
    [InlineData(FileTransferTransferState.Failed, false, false, "Transfer failed")]
    public void Animation_StateChangesStopImmediatelyWithoutInventingCompletion(
        FileTransferTransferState state, bool paused, bool peerPaused, string status)
    {
        var p = new FileTransferProgressPresentation();
        p.SetTarget(Item(100), 0, true);
        p.SetTarget(Item(500), 0, true);
        var snapshot = Snapshot(state == FileTransferTransferState.Completed ? 1000 : 500) with
        {
            State = state, IsPaused = paused, IsPeerPaused = peerPaused,
        };
        var item = FileTransferPanelItemViewModel.FromSnapshot(snapshot)!;
        p.SetTarget(item, 50, true);
        Assert.False(p.IsAnimating);
        Assert.Equal(item.ProgressBytes, p.DisplayedBytes);
        Assert.Equal(status, item.StatusText);
        Assert.Equal(state is not (FileTransferTransferState.Completed or FileTransferTransferState.Failed or FileTransferTransferState.Canceled), item.ShowProgress);
        p.Advance(10_000);
        Assert.Equal(item.ProgressBytes, p.DisplayedBytes);
    }

    [Fact]
    public void Animation_DetachAndClearStopWorkAndClampInvalidByteCounts()
    {
        var p = new FileTransferProgressPresentation();
        p.SetTarget(Item(-50), 0, true);
        Assert.Equal(0, p.DisplayedBytes);
        p.SetTarget(Item(5000), 1, true);
        p.SetTarget(Item(5000), 2, false);
        Assert.Equal(1000, p.DisplayedBytes);
        Assert.False(p.IsAnimating);
        p.SetTarget(null, 3, false);
        Assert.Equal(0, p.DisplayedBytes);
        Assert.Equal(0, p.Fraction);
        p.SetTarget(Item(10), 4, true);
        Assert.Equal(10, p.DisplayedBytes);
    }

    internal static FileTransferPanelItemViewModel Item(long bytes)
        => FileTransferPanelItemViewModel.FromSnapshot(Snapshot(bytes))!;

    private static FileTransferTransferSnapshot Snapshot(long bytes, FileTransferDirection direction = FileTransferDirection.Inbound)
        => new("session", "transfer", direction,
            direction == FileTransferDirection.Inbound ? FileTransferTransferState.Receiving : FileTransferTransferState.Sending,
            "progress.bin", 1000, null, bytes, 0, 10, 100, null, null,
            BytesAcceptedForTransport: bytes, BytesAcknowledgedByReceiver: bytes);
}
