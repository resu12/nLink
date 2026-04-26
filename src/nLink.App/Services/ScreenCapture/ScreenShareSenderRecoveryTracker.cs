using System;

namespace NLink.App.Services.ScreenCapture;

internal enum RecoveryBurstPhase
{
    Idle = 0,
    Requested = 1,
    OwnerPending = 2,
    OwnerEmittedAwaitingHelperAck = 3,
    PostAckHold = 4,
}

internal sealed class ActiveRecoveryBurst
{
    public long StreamEpoch { get; init; }

    public RecoveryBurstPhase Phase { get; set; }

    public long OwnerFrameId { get; set; } = -1;

    public long BurstToken { get; init; }

    public int ProtectedFollowerBudgetRemaining { get; set; } = 2;

    public long NextProtectedFollowerFrameId { get; set; } = -1;

    public DateTimeOffset RequestedUtc { get; init; }

    public DateTimeOffset OwnerEmittedUtc { get; set; }

    public DateTimeOffset PostAckHoldStartedUtc { get; set; }

    public bool ForcedResetIssued { get; set; }

    public ActiveRecoveryBurstSnapshot ToSnapshot()
    {
        return new ActiveRecoveryBurstSnapshot(
            StreamEpoch,
            Phase,
            OwnerFrameId,
            BurstToken,
            ProtectedFollowerBudgetRemaining,
            NextProtectedFollowerFrameId,
            RequestedUtc,
            OwnerEmittedUtc,
            PostAckHoldStartedUtc,
            ForcedResetIssued);
    }
}

internal sealed class LastCompletedRecovery
{
    public long StreamEpoch { get; init; }

    public long OwnerFrameId { get; init; } = -1;

    public long AckFrameId { get; init; } = -1;

    public string AckSource { get; init; } = string.Empty;

    public long OwnerEmitToAckMs { get; init; } = -1;

    public string CompletionKind { get; init; } = string.Empty;

    public DateTimeOffset CompletedUtc { get; init; }

    public LastCompletedRecoverySnapshot ToSnapshot()
    {
        return new LastCompletedRecoverySnapshot(
            StreamEpoch,
            OwnerFrameId,
            AckFrameId,
            AckSource,
            OwnerEmitToAckMs,
            CompletionKind,
            CompletedUtc);
    }
}

internal sealed record ActiveRecoveryBurstSnapshot(
    long StreamEpoch,
    RecoveryBurstPhase Phase,
    long OwnerFrameId,
    long BurstToken,
    int ProtectedFollowerBudgetRemaining,
    long NextProtectedFollowerFrameId,
    DateTimeOffset RequestedUtc,
    DateTimeOffset OwnerEmittedUtc,
    DateTimeOffset PostAckHoldStartedUtc,
    bool ForcedResetIssued);

internal sealed record LastCompletedRecoverySnapshot(
    long StreamEpoch,
    long OwnerFrameId,
    long AckFrameId,
    string AckSource,
    long OwnerEmitToAckMs,
    string CompletionKind,
    DateTimeOffset CompletedUtc);

internal sealed class SenderRecoveryLockState
{
    public bool Active { get; set; }

    public long StreamEpoch { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public long LastContinuitySignalSentAtUtcMs { get; set; }

    public bool TimeoutResetIssued { get; set; }

    public int TimeoutResetCount { get; set; }

    public long ClearedByAcknowledgedProofCount { get; set; }

    public long ClearedByVisibleProofCount { get; set; }

    public string LastClearReason { get; set; } = string.Empty;
}

internal sealed class SenderRecoveryBurstState
{
    public bool GapActive { get; set; }

    public long GapStreamEpoch { get; set; }

    public DateTimeOffset GapStartedUtc { get; set; }

    public ActiveRecoveryBurst? ActiveBurst { get; set; }

    public DateTimeOffset FirstHelperHeadAdvanceUtc { get; set; }

    public int ProtectedFollowerCount { get; set; }

    public int ProtectedFrameCount { get; set; }

    public long GapCount { get; set; }

    public long GapToKeyframeRequestMs { get; set; } = -1;

    public long KeyframeRequestToOwnerEmitMs { get; set; } = -1;

    public long NextBurstToken { get; set; }

    public long StartAppliedHeadFrameId { get; set; } = -1;

    public long StartLastVisibleApplyFrameId { get; set; } = -1;

    public long BurstControlFallbackCount { get; set; }

    public long BurstTimeoutCount { get; set; }

    public long BurstCompletedCount { get; set; }

    public long BurstRestartSuppressedCount { get; set; }

    public long BurstEncoderRerequestCount { get; set; }

    public long OwnerPendingForcedResetCount { get; set; }

    public long KeyframeEmittedAfterForcedResetCount { get; set; }

    public long BurstCompletedByHelperAckCount { get; set; }

    public long BurstCompletedByTimeoutCount { get; set; }

    public long BurstCompletedByProtectedFramesCount { get; set; }

    public long BurstProfileTransitionDeferredCount { get; set; }

    public long BurstProfileTransitionTakeoverCount { get; set; }

    public long EpochTakeoverSuppressedAfterOwnerEmitCount { get; set; }

    public long BurstStaleRequestSuppressedCount { get; set; }

    public long BurstRequestSuppressedDueToHelperAckCount { get; set; }

    public long BurstStartedWhileHelperProofHealthyCount { get; set; }

    public long BurstCompletedByAppliedHeadAckCount { get; set; }

    public long BurstCompletedByLastVisibleApplyAckCount { get; set; }

    public long BurstCompletedByVisibleRecoveryFloorCount { get; set; }

    public long BurstCompletedByVisibleApplyFallbackCount { get; set; }

    public long BurstCompletedByHelperVisibleReceiptCount { get; set; }

    public long HelperProgressPastOwnerWithoutBurstAckCount { get; set; }

    public long PostAckHoldStartedCount { get; set; }

    public long PostAckHoldExpiredCount { get; set; }

    public long PostAckHoldSuppressedReopenCount { get; set; }

    public bool OwnerPendingNonKeyHeldActive { get; set; }

    public long OwnerPendingNonKeyHeldCount { get; set; }

    public long OwnerPendingNonKeyReplacedCount { get; set; }

    public bool OwnerUnackedNonKeyHeldActive { get; set; }

    public int OwnerUnackedAdmittedFollowerCount { get; set; }

    public long OwnerUnackedNonKeyHeldCount { get; set; }

    public long OwnerUnackedNonKeyReplacedCount { get; set; }

    public long SameEpochKeyframeSuppressedWhileOwnerUnackedCount { get; set; }

    public long OwnerReplacedBeforeAckCount { get; set; }

    public long HighFrameAgeSuppressedDuringOwnerAckCount { get; set; }

    public long RecoveryTimeoutWhileHelperHeadAdvancedCount { get; set; }

    public long PostAckModeGraceSuppressedHighFrameAgeCount { get; set; }

    public long BootstrapGraceSuppressedCatchUpCount { get; set; }

    public long LastEpochTakeoverSuppressedFromEpoch { get; set; }

    public long LastEpochTakeoverSuppressedToEpoch { get; set; }

    public string LastEpochTakeoverSuppressedPhase { get; set; } = string.Empty;

    public long PostRecoveryAgeGraceEpoch { get; set; }

    public DateTimeOffset PostRecoveryAgeGraceUntilUtc { get; set; }

    public long PostRecoveryAgeGraceSuppressedCount { get; set; }
}

internal sealed class SenderRecoveryReceiptState
{
    public long OwnerEmitToFirstVisibleApplyMs { get; set; } = -1;

    public long OwnerAckFrameId { get; set; } = -1;

    public long OwnerEmitToAckMs { get; set; } = -1;

    public long OwnerAckWindowMs { get; set; } = -1;

    public string AckSource { get; set; } = string.Empty;

    public long LastRemoteRecoveryReceiptStreamEpoch { get; set; }

    public long LastRemoteRecoveryReceiptOwnerFrameId { get; set; } = -1;

    public long LastRemoteRecoveryReceiptVisibleRecoveryFrameId { get; set; } = -1;

    public long LastRemoteRecoveryReceiptVisibleHeadFrameId { get; set; } = -1;

    public string LastRemoteRecoveryReceiptKind { get; set; } = string.Empty;

    public long RemoteRecoveryReceiptRejectedCount { get; set; }

    public string LastRemoteRecoveryReceiptRejectReason { get; set; } = string.Empty;

    public long LastRemoteRecoveryReceiptRejectActiveStreamEpoch { get; set; }

    public long LastRemoteRecoveryReceiptRejectActiveOwnerFrameId { get; set; } = -1;

    public string LastRemoteRecoveryReceiptRejectActivePhase { get; set; } = string.Empty;

    public long HelperAckAfterFactSendMs { get; set; } = -1;

    public void ClearCurrentAckState()
    {
        OwnerAckFrameId = -1;
        OwnerEmitToAckMs = -1;
        OwnerAckWindowMs = -1;
        OwnerEmitToFirstVisibleApplyMs = -1;
        AckSource = string.Empty;
        HelperAckAfterFactSendMs = -1;
    }
}

internal sealed class SenderRecoveryOutcomeState
{
    public LastCompletedRecovery? LastCompletedRecovery { get; set; }

    public void RecordCompletedRecoveryOutcome(
        long streamEpoch,
        long ownerFrameId,
        long ackFrameId,
        string ackSource,
        long ownerEmitToAckMs,
        string completionKind,
        DateTimeOffset completedAtUtc)
    {
        LastCompletedRecovery = new LastCompletedRecovery
        {
            StreamEpoch = streamEpoch,
            OwnerFrameId = ownerFrameId,
            AckFrameId = ackFrameId,
            AckSource = string.IsNullOrWhiteSpace(ackSource) ? string.Empty : ackSource.Trim(),
            OwnerEmitToAckMs = ownerEmitToAckMs,
            CompletionKind = string.IsNullOrWhiteSpace(completionKind) ? string.Empty : completionKind.Trim(),
            CompletedUtc = completedAtUtc,
        };
    }

    public void Clear() => LastCompletedRecovery = null;
}

internal sealed class SenderRecoveryDiagnosticsState
{
    public SenderRecoveryDiagnosticsState(SenderRecoveryBurstState burst, SenderRecoveryLockState lockState, SenderRecoveryReceiptState receipt)
    {
        Burst = burst ?? throw new ArgumentNullException(nameof(burst));
        LockState = lockState ?? throw new ArgumentNullException(nameof(lockState));
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
    }

    public SenderRecoveryBurstState Burst { get; }

    public SenderRecoveryLockState LockState { get; }

    public SenderRecoveryReceiptState Receipt { get; }
}

internal sealed record SenderRecoverySnapshot(
    bool RecoveryLockActive,
    long RecoveryLockStreamEpoch,
    string RecoveryLockReason,
    bool RecoveryGapActive,
    long RecoveryGapStreamEpoch,
    ActiveRecoveryBurstSnapshot? ActiveRecoveryBurst,
    long RecoveryOwnerAckFrameId,
    long RecoveryOwnerEmitToAckMs,
    long RecoveryOwnerEmitToFirstVisibleApplyMs,
    string RecoveryAckSource,
    LastCompletedRecoverySnapshot? LastCompletedRecovery);

internal sealed class ScreenShareSenderRecoveryTracker
{
    private readonly SenderRecoveryLockState lockState = new();
    private readonly SenderRecoveryBurstState burstState = new();
    private readonly SenderRecoveryReceiptState receiptState = new();
    private readonly SenderRecoveryOutcomeState outcomeState = new();
    private readonly SenderRecoveryDiagnosticsState diagnosticsState;

    public ScreenShareSenderRecoveryTracker()
    {
        diagnosticsState = new SenderRecoveryDiagnosticsState(burstState, lockState, receiptState);
    }

    internal SenderRecoveryDiagnosticsState Diagnostics => diagnosticsState;

    public bool RecoveryLockActive
    {
        get => lockState.Active;
        private set => lockState.Active = value;
    }

    public long RecoveryLockStreamEpoch
    {
        get => lockState.StreamEpoch;
        private set => lockState.StreamEpoch = value;
    }

    public DateTimeOffset RecoveryLockStartedUtc
    {
        get => lockState.StartedUtc;
        private set => lockState.StartedUtc = value;
    }

    public string RecoveryLockReason
    {
        get => lockState.Reason;
        private set => lockState.Reason = value;
    }

    public long RecoveryLockLastContinuitySignalSentAtUtcMs
    {
        get => lockState.LastContinuitySignalSentAtUtcMs;
        private set => lockState.LastContinuitySignalSentAtUtcMs = value;
    }

    public bool RecoveryTimeoutResetIssued
    {
        get => lockState.TimeoutResetIssued;
        private set => lockState.TimeoutResetIssued = value;
    }

    public int RecoveryTimeoutResetCount
    {
        get => lockState.TimeoutResetCount;
        private set => lockState.TimeoutResetCount = value;
    }

    public bool RecoveryGapActive
    {
        get => burstState.GapActive;
        private set => burstState.GapActive = value;
    }

    public long RecoveryGapStreamEpoch
    {
        get => burstState.GapStreamEpoch;
        private set => burstState.GapStreamEpoch = value;
    }

    public DateTimeOffset RecoveryGapStartedUtc
    {
        get => burstState.GapStartedUtc;
        private set => burstState.GapStartedUtc = value;
    }

    public ActiveRecoveryBurst? ActiveRecoveryBurst
    {
        get => burstState.ActiveBurst;
        private set => burstState.ActiveBurst = value;
    }

    public DateTimeOffset RecoveryFirstHelperHeadAdvanceUtc
    {
        get => burstState.FirstHelperHeadAdvanceUtc;
        private set => burstState.FirstHelperHeadAdvanceUtc = value;
    }

    public int RecoveryProtectedFollowerCount
    {
        get => burstState.ProtectedFollowerCount;
        private set => burstState.ProtectedFollowerCount = value;
    }

    public int RecoveryProtectedFrameCount
    {
        get => burstState.ProtectedFrameCount;
        private set => burstState.ProtectedFrameCount = value;
    }

    public long RecoveryGapCount
    {
        get => burstState.GapCount;
        private set => burstState.GapCount = value;
    }

    public long RecoveryGapToKeyframeRequestMs
    {
        get => burstState.GapToKeyframeRequestMs;
        private set => burstState.GapToKeyframeRequestMs = value;
    }

    public long RecoveryKeyframeRequestToOwnerEmitMs
    {
        get => burstState.KeyframeRequestToOwnerEmitMs;
        private set => burstState.KeyframeRequestToOwnerEmitMs = value;
    }

    public long NextRecoveryBurstToken
    {
        get => burstState.NextBurstToken;
        private set => burstState.NextBurstToken = value;
    }

    public long RecoveryOwnerEmitToFirstVisibleApplyMs
    {
        get => receiptState.OwnerEmitToFirstVisibleApplyMs;
        private set => receiptState.OwnerEmitToFirstVisibleApplyMs = value;
    }

    public long RecoveryStartAppliedHeadFrameId
    {
        get => burstState.StartAppliedHeadFrameId;
        private set => burstState.StartAppliedHeadFrameId = value;
    }

    public long RecoveryStartLastVisibleApplyFrameId
    {
        get => burstState.StartLastVisibleApplyFrameId;
        private set => burstState.StartLastVisibleApplyFrameId = value;
    }

    public long RecoveryOwnerAckFrameId
    {
        get => receiptState.OwnerAckFrameId;
        private set => receiptState.OwnerAckFrameId = value;
    }

    public long RecoveryOwnerEmitToAckMs
    {
        get => receiptState.OwnerEmitToAckMs;
        private set => receiptState.OwnerEmitToAckMs = value;
    }

    public string RecoveryAckSource
    {
        get => receiptState.AckSource;
        private set => receiptState.AckSource = value;
    }

    public long RecoveryBurstControlFallbackCount
    {
        get => burstState.BurstControlFallbackCount;
        private set => burstState.BurstControlFallbackCount = value;
    }

    public long RecoveryBurstTimeoutCount
    {
        get => burstState.BurstTimeoutCount;
        private set => burstState.BurstTimeoutCount = value;
    }

    public long RecoveryBurstCompletedCount
    {
        get => burstState.BurstCompletedCount;
        private set => burstState.BurstCompletedCount = value;
    }

    public long RecoveryBurstRestartSuppressedCount
    {
        get => burstState.BurstRestartSuppressedCount;
        private set => burstState.BurstRestartSuppressedCount = value;
    }

    public long RecoveryBurstEncoderRerequestCount
    {
        get => burstState.BurstEncoderRerequestCount;
        private set => burstState.BurstEncoderRerequestCount = value;
    }

    public long RecoveryOwnerPendingForcedResetCount
    {
        get => burstState.OwnerPendingForcedResetCount;
        private set => burstState.OwnerPendingForcedResetCount = value;
    }

    public long RecoveryKeyframeEmittedAfterForcedResetCount
    {
        get => burstState.KeyframeEmittedAfterForcedResetCount;
        private set => burstState.KeyframeEmittedAfterForcedResetCount = value;
    }

    public long RecoveryBurstCompletedByHelperAckCount
    {
        get => burstState.BurstCompletedByHelperAckCount;
        private set => burstState.BurstCompletedByHelperAckCount = value;
    }

    public long RecoveryBurstCompletedByTimeoutCount
    {
        get => burstState.BurstCompletedByTimeoutCount;
        private set => burstState.BurstCompletedByTimeoutCount = value;
    }

    public long RecoveryBurstCompletedByProtectedFramesCount
    {
        get => burstState.BurstCompletedByProtectedFramesCount;
        private set => burstState.BurstCompletedByProtectedFramesCount = value;
    }

    public long RecoveryBurstProfileTransitionDeferredCount
    {
        get => burstState.BurstProfileTransitionDeferredCount;
        private set => burstState.BurstProfileTransitionDeferredCount = value;
    }

    public long RecoveryBurstProfileTransitionTakeoverCount
    {
        get => burstState.BurstProfileTransitionTakeoverCount;
        private set => burstState.BurstProfileTransitionTakeoverCount = value;
    }

    public long RecoveryEpochTakeoverSuppressedAfterOwnerEmitCount
    {
        get => burstState.EpochTakeoverSuppressedAfterOwnerEmitCount;
        private set => burstState.EpochTakeoverSuppressedAfterOwnerEmitCount = value;
    }

    public long RecoveryBurstStaleRequestSuppressedCount
    {
        get => burstState.BurstStaleRequestSuppressedCount;
        private set => burstState.BurstStaleRequestSuppressedCount = value;
    }

    public long RecoveryBurstRequestSuppressedDueToHelperAckCount
    {
        get => burstState.BurstRequestSuppressedDueToHelperAckCount;
        private set => burstState.BurstRequestSuppressedDueToHelperAckCount = value;
    }

    public long RecoveryBurstStartedWhileHelperProofHealthyCount
    {
        get => burstState.BurstStartedWhileHelperProofHealthyCount;
        private set => burstState.BurstStartedWhileHelperProofHealthyCount = value;
    }

    public long RecoveryBurstCompletedByAppliedHeadAckCount
    {
        get => burstState.BurstCompletedByAppliedHeadAckCount;
        private set => burstState.BurstCompletedByAppliedHeadAckCount = value;
    }

    public long RecoveryBurstCompletedByLastVisibleApplyAckCount
    {
        get => burstState.BurstCompletedByLastVisibleApplyAckCount;
        private set => burstState.BurstCompletedByLastVisibleApplyAckCount = value;
    }

    public long RecoveryBurstCompletedByVisibleRecoveryFloorCount
    {
        get => burstState.BurstCompletedByVisibleRecoveryFloorCount;
        private set => burstState.BurstCompletedByVisibleRecoveryFloorCount = value;
    }

    public long RecoveryBurstCompletedByVisibleApplyFallbackCount
    {
        get => burstState.BurstCompletedByVisibleApplyFallbackCount;
        private set => burstState.BurstCompletedByVisibleApplyFallbackCount = value;
    }

    public long RecoveryBurstCompletedByHelperVisibleReceiptCount
    {
        get => burstState.BurstCompletedByHelperVisibleReceiptCount;
        private set => burstState.BurstCompletedByHelperVisibleReceiptCount = value;
    }

    public long HelperProgressPastOwnerWithoutBurstAckCount
    {
        get => burstState.HelperProgressPastOwnerWithoutBurstAckCount;
        private set => burstState.HelperProgressPastOwnerWithoutBurstAckCount = value;
    }

    public LastCompletedRecovery? LastCompletedRecovery
    {
        get => outcomeState.LastCompletedRecovery;
        private set => outcomeState.LastCompletedRecovery = value;
    }

    public long RecoveryPostAckHoldStartedCount
    {
        get => burstState.PostAckHoldStartedCount;
        private set => burstState.PostAckHoldStartedCount = value;
    }

    public long RecoveryPostAckHoldExpiredCount
    {
        get => burstState.PostAckHoldExpiredCount;
        private set => burstState.PostAckHoldExpiredCount = value;
    }

    public long RecoveryPostAckHoldSuppressedReopenCount
    {
        get => burstState.PostAckHoldSuppressedReopenCount;
        private set => burstState.PostAckHoldSuppressedReopenCount = value;
    }

    public bool RecoveryOwnerPendingNonKeyHeldActive
    {
        get => burstState.OwnerPendingNonKeyHeldActive;
        private set => burstState.OwnerPendingNonKeyHeldActive = value;
    }

    public long RecoveryOwnerPendingNonKeyHeldCount
    {
        get => burstState.OwnerPendingNonKeyHeldCount;
        private set => burstState.OwnerPendingNonKeyHeldCount = value;
    }

    public long RecoveryOwnerPendingNonKeyReplacedCount
    {
        get => burstState.OwnerPendingNonKeyReplacedCount;
        private set => burstState.OwnerPendingNonKeyReplacedCount = value;
    }

    public bool RecoveryOwnerUnackedNonKeyHeldActive
    {
        get => burstState.OwnerUnackedNonKeyHeldActive;
        private set => burstState.OwnerUnackedNonKeyHeldActive = value;
    }

    public int RecoveryOwnerUnackedAdmittedFollowerCount
    {
        get => burstState.OwnerUnackedAdmittedFollowerCount;
        private set => burstState.OwnerUnackedAdmittedFollowerCount = value;
    }

    public long RecoveryOwnerUnackedNonKeyHeldCount
    {
        get => burstState.OwnerUnackedNonKeyHeldCount;
        private set => burstState.OwnerUnackedNonKeyHeldCount = value;
    }

    public long RecoveryOwnerUnackedNonKeyReplacedCount
    {
        get => burstState.OwnerUnackedNonKeyReplacedCount;
        private set => burstState.OwnerUnackedNonKeyReplacedCount = value;
    }

    public long RecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount
    {
        get => burstState.SameEpochKeyframeSuppressedWhileOwnerUnackedCount;
        private set => burstState.SameEpochKeyframeSuppressedWhileOwnerUnackedCount = value;
    }

    public long RecoveryOwnerReplacedBeforeAckCount
    {
        get => burstState.OwnerReplacedBeforeAckCount;
        private set => burstState.OwnerReplacedBeforeAckCount = value;
    }

    public long RecoveryOwnerAckWindowMs
    {
        get => receiptState.OwnerAckWindowMs;
        private set => receiptState.OwnerAckWindowMs = value;
    }

    public long HighFrameAgeSuppressedDuringOwnerAckCount
    {
        get => burstState.HighFrameAgeSuppressedDuringOwnerAckCount;
        private set => burstState.HighFrameAgeSuppressedDuringOwnerAckCount = value;
    }

    public long RecoveryTimeoutWhileHelperHeadAdvancedCount
    {
        get => burstState.RecoveryTimeoutWhileHelperHeadAdvancedCount;
        private set => burstState.RecoveryTimeoutWhileHelperHeadAdvancedCount = value;
    }

    public long PostAckModeGraceSuppressedHighFrameAgeCount
    {
        get => burstState.PostAckModeGraceSuppressedHighFrameAgeCount;
        private set => burstState.PostAckModeGraceSuppressedHighFrameAgeCount = value;
    }

    public long BootstrapGraceSuppressedCatchUpCount
    {
        get => burstState.BootstrapGraceSuppressedCatchUpCount;
        private set => burstState.BootstrapGraceSuppressedCatchUpCount = value;
    }

    public long RecoveryLockClearedByAcknowledgedProofCount
    {
        get => lockState.ClearedByAcknowledgedProofCount;
        private set => lockState.ClearedByAcknowledgedProofCount = value;
    }

    public long RecoveryLockClearedByVisibleProofCount
    {
        get => lockState.ClearedByVisibleProofCount;
        private set => lockState.ClearedByVisibleProofCount = value;
    }

    public string RecoveryLockLastClearReason
    {
        get => lockState.LastClearReason;
        private set => lockState.LastClearReason = value;
    }

    public long LastRemoteRecoveryReceiptStreamEpoch
    {
        get => receiptState.LastRemoteRecoveryReceiptStreamEpoch;
        private set => receiptState.LastRemoteRecoveryReceiptStreamEpoch = value;
    }

    public long LastRemoteRecoveryReceiptOwnerFrameId
    {
        get => receiptState.LastRemoteRecoveryReceiptOwnerFrameId;
        private set => receiptState.LastRemoteRecoveryReceiptOwnerFrameId = value;
    }

    public long LastRemoteRecoveryReceiptVisibleRecoveryFrameId
    {
        get => receiptState.LastRemoteRecoveryReceiptVisibleRecoveryFrameId;
        private set => receiptState.LastRemoteRecoveryReceiptVisibleRecoveryFrameId = value;
    }

    public long LastRemoteRecoveryReceiptVisibleHeadFrameId
    {
        get => receiptState.LastRemoteRecoveryReceiptVisibleHeadFrameId;
        private set => receiptState.LastRemoteRecoveryReceiptVisibleHeadFrameId = value;
    }

    public string LastRemoteRecoveryReceiptKind
    {
        get => receiptState.LastRemoteRecoveryReceiptKind;
        private set => receiptState.LastRemoteRecoveryReceiptKind = value;
    }

    public long RemoteRecoveryReceiptRejectedCount
    {
        get => receiptState.RemoteRecoveryReceiptRejectedCount;
        private set => receiptState.RemoteRecoveryReceiptRejectedCount = value;
    }

    public string LastRemoteRecoveryReceiptRejectReason
    {
        get => receiptState.LastRemoteRecoveryReceiptRejectReason;
        private set => receiptState.LastRemoteRecoveryReceiptRejectReason = value;
    }

    public long LastRemoteRecoveryReceiptRejectActiveStreamEpoch
    {
        get => receiptState.LastRemoteRecoveryReceiptRejectActiveStreamEpoch;
        private set => receiptState.LastRemoteRecoveryReceiptRejectActiveStreamEpoch = value;
    }

    public long LastRemoteRecoveryReceiptRejectActiveOwnerFrameId
    {
        get => receiptState.LastRemoteRecoveryReceiptRejectActiveOwnerFrameId;
        private set => receiptState.LastRemoteRecoveryReceiptRejectActiveOwnerFrameId = value;
    }

    public string LastRemoteRecoveryReceiptRejectActivePhase
    {
        get => receiptState.LastRemoteRecoveryReceiptRejectActivePhase;
        private set => receiptState.LastRemoteRecoveryReceiptRejectActivePhase = value;
    }

    public long LastRecoveryEpochTakeoverSuppressedFromEpoch
    {
        get => burstState.LastEpochTakeoverSuppressedFromEpoch;
        private set => burstState.LastEpochTakeoverSuppressedFromEpoch = value;
    }

    public long LastRecoveryEpochTakeoverSuppressedToEpoch
    {
        get => burstState.LastEpochTakeoverSuppressedToEpoch;
        private set => burstState.LastEpochTakeoverSuppressedToEpoch = value;
    }

    public string LastRecoveryEpochTakeoverSuppressedPhase
    {
        get => burstState.LastEpochTakeoverSuppressedPhase;
        private set => burstState.LastEpochTakeoverSuppressedPhase = value;
    }

    public long HelperAckAfterFactSendMs
    {
        get => receiptState.HelperAckAfterFactSendMs;
        private set => receiptState.HelperAckAfterFactSendMs = value;
    }

    public long PostRecoveryAgeGraceEpoch
    {
        get => burstState.PostRecoveryAgeGraceEpoch;
        private set => burstState.PostRecoveryAgeGraceEpoch = value;
    }

    public DateTimeOffset PostRecoveryAgeGraceUntilUtc
    {
        get => burstState.PostRecoveryAgeGraceUntilUtc;
        private set => burstState.PostRecoveryAgeGraceUntilUtc = value;
    }

    public long PostRecoveryAgeGraceSuppressedCount
    {
        get => burstState.PostRecoveryAgeGraceSuppressedCount;
        private set => burstState.PostRecoveryAgeGraceSuppressedCount = value;
    }

    public void ClearCurrentRecoveryAckState() => receiptState.ClearCurrentAckState();

    public void RecordCompletedRecoveryOutcome(
        long streamEpoch,
        long ownerFrameId,
        long ackFrameId,
        string ackSource,
        long ownerEmitToAckMs,
        string completionKind,
        DateTimeOffset completedAtUtc)
    {
        outcomeState.RecordCompletedRecoveryOutcome(
            streamEpoch,
            ownerFrameId,
            ackFrameId,
            ackSource,
            ownerEmitToAckMs,
            completionKind,
            completedAtUtc);
    }

    public void ClearLastCompletedRecoveryOutcome()
    {
        outcomeState.Clear();
        receiptState.ClearCurrentAckState();
    }

    public void UpdateLockState(Action<SenderRecoveryLockState> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        update(lockState);
    }

    public void UpdateBurstState(Action<SenderRecoveryBurstState> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        update(burstState);
    }

    public void UpdateReceiptState(Action<SenderRecoveryReceiptState> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        update(receiptState);
    }

    public void UpdateOutcomeState(Action<SenderRecoveryOutcomeState> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        update(outcomeState);
    }

    public void SetActiveRecoveryBurst(ActiveRecoveryBurst? activeBurst)
    {
        burstState.ActiveBurst = activeBurst;
    }

    public void UpdateActiveRecoveryBurst(Action<ActiveRecoveryBurst> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (burstState.ActiveBurst is not { } activeBurst)
        {
            return;
        }

        update(activeBurst);
    }

    public ActiveRecoveryBurstSnapshot? GetActiveRecoveryBurstSnapshot()
    {
        return burstState.ActiveBurst?.ToSnapshot();
    }

    public LastCompletedRecoverySnapshot? GetLastCompletedRecoverySnapshot()
    {
        return outcomeState.LastCompletedRecovery?.ToSnapshot();
    }

    public SenderRecoverySnapshot GetSnapshot()
    {
        return new SenderRecoverySnapshot(
            RecoveryLockActive,
            RecoveryLockStreamEpoch,
            RecoveryLockReason,
            RecoveryGapActive,
            RecoveryGapStreamEpoch,
            GetActiveRecoveryBurstSnapshot(),
            RecoveryOwnerAckFrameId,
            RecoveryOwnerEmitToAckMs,
            RecoveryOwnerEmitToFirstVisibleApplyMs,
            RecoveryAckSource,
            GetLastCompletedRecoverySnapshot());
    }

    public static long ComputeRecoveryCompletionAccountingMismatch(LastCompletedRecovery? completedRecovery)
    {
        if (completedRecovery is null)
        {
            return 0;
        }

        var hasAckFrame = completedRecovery.AckFrameId >= 0;
        var hasAckTiming = completedRecovery.OwnerEmitToAckMs >= 0;
        var hasAckSource = !string.IsNullOrWhiteSpace(completedRecovery.AckSource);
        return string.Equals(completedRecovery.CompletionKind, "timeout", StringComparison.Ordinal)
            ? ((hasAckFrame || hasAckTiming || hasAckSource) ? 1L : 0L)
            : ((!hasAckFrame || !hasAckTiming || !hasAckSource) ? 1L : 0L);
    }

    public static long ComputeRecoveryCompletionAccountingMismatch(LastCompletedRecoverySnapshot? completedRecovery)
    {
        if (completedRecovery is null)
        {
            return 0;
        }

        var hasAckFrame = completedRecovery.AckFrameId >= 0;
        var hasAckTiming = completedRecovery.OwnerEmitToAckMs >= 0;
        var hasAckSource = !string.IsNullOrWhiteSpace(completedRecovery.AckSource);
        return string.Equals(completedRecovery.CompletionKind, "timeout", StringComparison.Ordinal)
            ? ((hasAckFrame || hasAckTiming || hasAckSource) ? 1L : 0L)
            : ((!hasAckFrame || !hasAckTiming || !hasAckSource) ? 1L : 0L);
    }
}
