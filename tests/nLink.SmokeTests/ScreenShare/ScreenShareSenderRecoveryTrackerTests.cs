using NLink.App.Services.ScreenCapture;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareSenderRecoveryTrackerTests
{
    [Fact]
    public void RecordCompletedRecoveryOutcome_HelperVisibleReceipt_KeepsAccountingCoherent()
    {
        var tracker = new ScreenShareSenderRecoveryTracker();

        tracker.RecordCompletedRecoveryOutcome(
            streamEpoch: 12,
            ownerFrameId: 40,
            ackFrameId: 41,
            ackSource: "helper_visible_receipt",
            ownerEmitToAckMs: 87,
            completionKind: "helper_ack",
            completedAtUtc: new DateTimeOffset(2026, 4, 22, 8, 0, 0, TimeSpan.Zero));

        var completed = Assert.IsType<LastCompletedRecoverySnapshot>(tracker.GetLastCompletedRecoverySnapshot());
        Assert.Equal(12, completed.StreamEpoch);
        Assert.Equal("helper_visible_receipt", completed.AckSource);
        Assert.Equal(0, ScreenShareSenderRecoveryTracker.ComputeRecoveryCompletionAccountingMismatch(completed));
    }

    [Fact]
    public void ClearLastCompletedRecoveryOutcome_ClearsSnapshotAndLiveAckState()
    {
        var tracker = new ScreenShareSenderRecoveryTracker();
        tracker.UpdateReceiptState(state =>
        {
            state.OwnerAckFrameId = 55;
            state.OwnerEmitToAckMs = 99;
            state.OwnerAckWindowMs = 120;
            state.OwnerEmitToFirstVisibleApplyMs = 140;
            state.AckSource = "helper_visible_receipt";
            state.HelperAckAfterFactSendMs = 15;
        });

        tracker.RecordCompletedRecoveryOutcome(
            streamEpoch: 7,
            ownerFrameId: 20,
            ackFrameId: 22,
            ackSource: "helper_visible_receipt",
            ownerEmitToAckMs: 50,
            completionKind: "helper_ack",
            completedAtUtc: DateTimeOffset.UtcNow);

        tracker.ClearLastCompletedRecoveryOutcome();

        Assert.Null(tracker.GetLastCompletedRecoverySnapshot());
        Assert.Equal(-1, tracker.RecoveryOwnerAckFrameId);
        Assert.Equal(-1, tracker.RecoveryOwnerEmitToAckMs);
        Assert.Equal(-1, tracker.RecoveryOwnerAckWindowMs);
        Assert.Equal(-1, tracker.RecoveryOwnerEmitToFirstVisibleApplyMs);
        Assert.Equal(string.Empty, tracker.RecoveryAckSource);
        Assert.Equal(-1, tracker.HelperAckAfterFactSendMs);
    }

    [Fact]
    public void ComputeRecoveryCompletionAccountingMismatch_TimeoutWithoutAck_IsZero()
    {
        var completed = new LastCompletedRecovery
        {
            StreamEpoch = 9,
            OwnerFrameId = 33,
            AckFrameId = -1,
            AckSource = string.Empty,
            OwnerEmitToAckMs = -1,
            CompletionKind = "timeout",
            CompletedUtc = DateTimeOffset.UtcNow,
        };

        Assert.Equal(0, ScreenShareSenderRecoveryTracker.ComputeRecoveryCompletionAccountingMismatch(completed));
    }

    [Fact]
    public void GetSnapshot_ReflectsGroupedRecoveryState()
    {
        var tracker = new ScreenShareSenderRecoveryTracker();
        tracker.UpdateLockState(state =>
        {
            state.Active = true;
            state.StreamEpoch = 21;
        });
        tracker.UpdateReceiptState(state =>
        {
            state.AckSource = "helper_visible_receipt";
            state.OwnerAckFrameId = 88;
        });
        tracker.SetActiveRecoveryBurst(new ActiveRecoveryBurst
        {
            StreamEpoch = 21,
            BurstToken = 3,
            Phase = RecoveryBurstPhase.OwnerEmittedAwaitingHelperAck,
            OwnerFrameId = 87,
        });

        var snapshot = tracker.GetSnapshot();

        Assert.True(snapshot.RecoveryLockActive);
        Assert.Equal(21, snapshot.RecoveryLockStreamEpoch);
        Assert.Equal("helper_visible_receipt", snapshot.RecoveryAckSource);
        Assert.Equal(88, snapshot.RecoveryOwnerAckFrameId);
        Assert.NotNull(snapshot.ActiveRecoveryBurst);
        Assert.Equal(87, snapshot.ActiveRecoveryBurst!.OwnerFrameId);

        tracker.UpdateActiveRecoveryBurst(state => state.OwnerFrameId = 111);
        Assert.Equal(87, snapshot.ActiveRecoveryBurst.OwnerFrameId);
    }

    [Fact]
    public void GetLastCompletedRecoverySnapshot_ReturnsImmutableCompletedOutcome()
    {
        var tracker = new ScreenShareSenderRecoveryTracker();
        tracker.RecordCompletedRecoveryOutcome(
            streamEpoch: 5,
            ownerFrameId: 12,
            ackFrameId: 14,
            ackSource: "helper_visible_receipt",
            ownerEmitToAckMs: 32,
            completionKind: "helper_ack",
            completedAtUtc: DateTimeOffset.UtcNow);

        var snapshot = tracker.GetLastCompletedRecoverySnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(0, ScreenShareSenderRecoveryTracker.ComputeRecoveryCompletionAccountingMismatch(snapshot));

        tracker.RecordCompletedRecoveryOutcome(
            streamEpoch: 7,
            ownerFrameId: 99,
            ackFrameId: -1,
            ackSource: string.Empty,
            ownerEmitToAckMs: -1,
            completionKind: "timeout",
            completedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(5, snapshot!.StreamEpoch);
        Assert.Equal(12, snapshot.OwnerFrameId);
    }
}
