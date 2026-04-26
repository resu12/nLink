using System;
using System.Globalization;
using System.Threading;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    private readonly ScreenShareSenderRecoveryTracker recoveryTracker = new();
    private void SetRecoveryLockState(Action<SenderRecoveryLockState> update) => recoveryTracker.UpdateLockState(update);
    private void SetRecoveryBurstState(Action<SenderRecoveryBurstState> update) => recoveryTracker.UpdateBurstState(update);
    private void SetRecoveryReceiptState(Action<SenderRecoveryReceiptState> update) => recoveryTracker.UpdateReceiptState(update);
    private void SetRecoveryOutcomeState(Action<SenderRecoveryOutcomeState> update) => recoveryTracker.UpdateOutcomeState(update);

    private bool recoveryLockActive
    {
        get => recoveryTracker.RecoveryLockActive;
        set => SetRecoveryLockState(state => state.Active = value);
    }

    private long recoveryLockStreamEpoch
    {
        get => recoveryTracker.RecoveryLockStreamEpoch;
        set => SetRecoveryLockState(state => state.StreamEpoch = value);
    }

    private DateTimeOffset recoveryLockStartedUtc
    {
        get => recoveryTracker.RecoveryLockStartedUtc;
        set => SetRecoveryLockState(state => state.StartedUtc = value);
    }

    private string recoveryLockReason
    {
        get => recoveryTracker.RecoveryLockReason;
        set => SetRecoveryLockState(state => state.Reason = value);
    }

    private long recoveryLockLastContinuitySignalSentAtUtcMs
    {
        get => recoveryTracker.RecoveryLockLastContinuitySignalSentAtUtcMs;
        set => SetRecoveryLockState(state => state.LastContinuitySignalSentAtUtcMs = value);
    }

    private bool recoveryTimeoutResetIssued
    {
        get => recoveryTracker.RecoveryTimeoutResetIssued;
        set => SetRecoveryLockState(state => state.TimeoutResetIssued = value);
    }

    private int recoveryTimeoutResetCount
    {
        get => recoveryTracker.RecoveryTimeoutResetCount;
        set => SetRecoveryLockState(state => state.TimeoutResetCount = value);
    }

    private bool recoveryGapActive
    {
        get => recoveryTracker.RecoveryGapActive;
        set => SetRecoveryBurstState(state => state.GapActive = value);
    }

    private long recoveryGapStreamEpoch
    {
        get => recoveryTracker.RecoveryGapStreamEpoch;
        set => SetRecoveryBurstState(state => state.GapStreamEpoch = value);
    }

    private DateTimeOffset recoveryGapStartedUtc
    {
        get => recoveryTracker.RecoveryGapStartedUtc;
        set => SetRecoveryBurstState(state => state.GapStartedUtc = value);
    }

    private ActiveRecoveryBurst? activeRecoveryBurst
    {
        get => recoveryTracker.ActiveRecoveryBurst;
        set => recoveryTracker.SetActiveRecoveryBurst(value);
    }

    private DateTimeOffset recoveryFirstHelperHeadAdvanceUtc
    {
        get => recoveryTracker.RecoveryFirstHelperHeadAdvanceUtc;
        set => SetRecoveryBurstState(state => state.FirstHelperHeadAdvanceUtc = value);
    }

    private int recoveryProtectedFollowerCount
    {
        get => recoveryTracker.RecoveryProtectedFollowerCount;
        set => SetRecoveryBurstState(state => state.ProtectedFollowerCount = value);
    }

    private int recoveryProtectedFrameCount
    {
        get => recoveryTracker.RecoveryProtectedFrameCount;
        set => SetRecoveryBurstState(state => state.ProtectedFrameCount = value);
    }

    private long recoveryGapCount
    {
        get => recoveryTracker.RecoveryGapCount;
        set => SetRecoveryBurstState(state => state.GapCount = value);
    }

    private long recoveryGapToKeyframeRequestMs
    {
        get => recoveryTracker.RecoveryGapToKeyframeRequestMs;
        set => SetRecoveryBurstState(state => state.GapToKeyframeRequestMs = value);
    }

    private long recoveryKeyframeRequestToOwnerEmitMs
    {
        get => recoveryTracker.RecoveryKeyframeRequestToOwnerEmitMs;
        set => SetRecoveryBurstState(state => state.KeyframeRequestToOwnerEmitMs = value);
    }

    private long nextRecoveryBurstToken
    {
        get => recoveryTracker.NextRecoveryBurstToken;
        set => SetRecoveryBurstState(state => state.NextBurstToken = value);
    }

    private long recoveryOwnerEmitToFirstVisibleApplyMs
    {
        get => recoveryTracker.RecoveryOwnerEmitToFirstVisibleApplyMs;
        set => SetRecoveryReceiptState(state => state.OwnerEmitToFirstVisibleApplyMs = value);
    }

    private long recoveryStartAppliedHeadFrameId
    {
        get => recoveryTracker.RecoveryStartAppliedHeadFrameId;
        set => SetRecoveryBurstState(state => state.StartAppliedHeadFrameId = value);
    }

    private long recoveryStartLastVisibleApplyFrameId
    {
        get => recoveryTracker.RecoveryStartLastVisibleApplyFrameId;
        set => SetRecoveryBurstState(state => state.StartLastVisibleApplyFrameId = value);
    }

    private long recoveryOwnerAckFrameId
    {
        get => recoveryTracker.RecoveryOwnerAckFrameId;
        set => SetRecoveryReceiptState(state => state.OwnerAckFrameId = value);
    }

    private long recoveryOwnerEmitToAckMs
    {
        get => recoveryTracker.RecoveryOwnerEmitToAckMs;
        set => SetRecoveryReceiptState(state => state.OwnerEmitToAckMs = value);
    }

    private string recoveryAckSource
    {
        get => recoveryTracker.RecoveryAckSource;
        set => SetRecoveryReceiptState(state => state.AckSource = value);
    }

    private long recoveryBurstControlFallbackCount
    {
        get => recoveryTracker.RecoveryBurstControlFallbackCount;
        set => SetRecoveryBurstState(state => state.BurstControlFallbackCount = value);
    }

    private long recoveryBurstTimeoutCount
    {
        get => recoveryTracker.RecoveryBurstTimeoutCount;
        set => SetRecoveryBurstState(state => state.BurstTimeoutCount = value);
    }

    private long recoveryBurstCompletedCount
    {
        get => recoveryTracker.RecoveryBurstCompletedCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedCount = value);
    }

    private long recoveryBurstRestartSuppressedCount
    {
        get => recoveryTracker.RecoveryBurstRestartSuppressedCount;
        set => SetRecoveryBurstState(state => state.BurstRestartSuppressedCount = value);
    }

    private long recoveryBurstEncoderRerequestCount
    {
        get => recoveryTracker.RecoveryBurstEncoderRerequestCount;
        set => SetRecoveryBurstState(state => state.BurstEncoderRerequestCount = value);
    }

    private long recoveryOwnerPendingForcedResetCount
    {
        get => recoveryTracker.RecoveryOwnerPendingForcedResetCount;
        set => SetRecoveryBurstState(state => state.OwnerPendingForcedResetCount = value);
    }

    private long recoveryKeyframeEmittedAfterForcedResetCount
    {
        get => recoveryTracker.RecoveryKeyframeEmittedAfterForcedResetCount;
        set => SetRecoveryBurstState(state => state.KeyframeEmittedAfterForcedResetCount = value);
    }

    private long recoveryBurstCompletedByHelperAckCount
    {
        get => recoveryTracker.RecoveryBurstCompletedByHelperAckCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedByHelperAckCount = value);
    }

    private long recoveryBurstCompletedByTimeoutCount
    {
        get => recoveryTracker.RecoveryBurstCompletedByTimeoutCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedByTimeoutCount = value);
    }

    private long recoveryBurstCompletedByProtectedFramesCount
    {
        get => recoveryTracker.RecoveryBurstCompletedByProtectedFramesCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedByProtectedFramesCount = value);
    }

    private long recoveryBurstProfileTransitionDeferredCount
    {
        get => recoveryTracker.RecoveryBurstProfileTransitionDeferredCount;
        set => SetRecoveryBurstState(state => state.BurstProfileTransitionDeferredCount = value);
    }

    private long recoveryBurstProfileTransitionTakeoverCount
    {
        get => recoveryTracker.RecoveryBurstProfileTransitionTakeoverCount;
        set => SetRecoveryBurstState(state => state.BurstProfileTransitionTakeoverCount = value);
    }

    private long recoveryEpochTakeoverSuppressedAfterOwnerEmitCount
    {
        get => recoveryTracker.RecoveryEpochTakeoverSuppressedAfterOwnerEmitCount;
        set => SetRecoveryBurstState(state => state.EpochTakeoverSuppressedAfterOwnerEmitCount = value);
    }

    private long recoveryBurstStaleRequestSuppressedCount
    {
        get => recoveryTracker.RecoveryBurstStaleRequestSuppressedCount;
        set => SetRecoveryBurstState(state => state.BurstStaleRequestSuppressedCount = value);
    }

    private long recoveryBurstRequestSuppressedDueToHelperAckCount
    {
        get => recoveryTracker.RecoveryBurstRequestSuppressedDueToHelperAckCount;
        set => SetRecoveryBurstState(state => state.BurstRequestSuppressedDueToHelperAckCount = value);
    }

    private long recoveryBurstStartedWhileHelperProofHealthyCount
    {
        get => recoveryTracker.RecoveryBurstStartedWhileHelperProofHealthyCount;
        set => SetRecoveryBurstState(state => state.BurstStartedWhileHelperProofHealthyCount = value);
    }

    private long recoveryBurstCompletedByAppliedHeadAckCount
    {
        get => recoveryTracker.RecoveryBurstCompletedByAppliedHeadAckCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedByAppliedHeadAckCount = value);
    }

    private long recoveryBurstCompletedByLastVisibleApplyAckCount
    {
        get => recoveryTracker.RecoveryBurstCompletedByLastVisibleApplyAckCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedByLastVisibleApplyAckCount = value);
    }

    private long recoveryBurstCompletedByVisibleRecoveryFloorCount
    {
        get => recoveryTracker.RecoveryBurstCompletedByVisibleRecoveryFloorCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedByVisibleRecoveryFloorCount = value);
    }

    private long recoveryBurstCompletedByVisibleApplyFallbackCount
    {
        get => recoveryTracker.RecoveryBurstCompletedByVisibleApplyFallbackCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedByVisibleApplyFallbackCount = value);
    }

    private long recoveryBurstCompletedByHelperVisibleReceiptCount
    {
        get => recoveryTracker.RecoveryBurstCompletedByHelperVisibleReceiptCount;
        set => SetRecoveryBurstState(state => state.BurstCompletedByHelperVisibleReceiptCount = value);
    }

    private long helperProgressPastOwnerWithoutBurstAckCount
    {
        get => recoveryTracker.HelperProgressPastOwnerWithoutBurstAckCount;
        set => SetRecoveryBurstState(state => state.HelperProgressPastOwnerWithoutBurstAckCount = value);
    }

    private LastCompletedRecoverySnapshot? lastCompletedRecovery
    {
        get => recoveryTracker.GetLastCompletedRecoverySnapshot();
    }

    private long recoveryPostAckHoldStartedCount
    {
        get => recoveryTracker.RecoveryPostAckHoldStartedCount;
        set => SetRecoveryBurstState(state => state.PostAckHoldStartedCount = value);
    }

    private long recoveryPostAckHoldExpiredCount
    {
        get => recoveryTracker.RecoveryPostAckHoldExpiredCount;
        set => SetRecoveryBurstState(state => state.PostAckHoldExpiredCount = value);
    }

    private long recoveryPostAckHoldSuppressedReopenCount
    {
        get => recoveryTracker.RecoveryPostAckHoldSuppressedReopenCount;
        set => SetRecoveryBurstState(state => state.PostAckHoldSuppressedReopenCount = value);
    }

    private bool recoveryOwnerPendingNonKeyHeldActive
    {
        get => recoveryTracker.RecoveryOwnerPendingNonKeyHeldActive;
        set => SetRecoveryBurstState(state => state.OwnerPendingNonKeyHeldActive = value);
    }

    private long recoveryOwnerPendingNonKeyHeldCount
    {
        get => recoveryTracker.RecoveryOwnerPendingNonKeyHeldCount;
        set => SetRecoveryBurstState(state => state.OwnerPendingNonKeyHeldCount = value);
    }

    private long recoveryOwnerPendingNonKeyReplacedCount
    {
        get => recoveryTracker.RecoveryOwnerPendingNonKeyReplacedCount;
        set => SetRecoveryBurstState(state => state.OwnerPendingNonKeyReplacedCount = value);
    }

    private bool recoveryOwnerUnackedNonKeyHeldActive
    {
        get => recoveryTracker.RecoveryOwnerUnackedNonKeyHeldActive;
        set => SetRecoveryBurstState(state => state.OwnerUnackedNonKeyHeldActive = value);
    }

    private int recoveryOwnerUnackedAdmittedFollowerCount
    {
        get => recoveryTracker.RecoveryOwnerUnackedAdmittedFollowerCount;
        set => SetRecoveryBurstState(state => state.OwnerUnackedAdmittedFollowerCount = value);
    }

    private long recoveryOwnerUnackedNonKeyHeldCount
    {
        get => recoveryTracker.RecoveryOwnerUnackedNonKeyHeldCount;
        set => SetRecoveryBurstState(state => state.OwnerUnackedNonKeyHeldCount = value);
    }

    private long recoveryOwnerUnackedNonKeyReplacedCount
    {
        get => recoveryTracker.RecoveryOwnerUnackedNonKeyReplacedCount;
        set => SetRecoveryBurstState(state => state.OwnerUnackedNonKeyReplacedCount = value);
    }

    private long recoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount
    {
        get => recoveryTracker.RecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount;
        set => SetRecoveryBurstState(state => state.SameEpochKeyframeSuppressedWhileOwnerUnackedCount = value);
    }

    private long recoveryOwnerReplacedBeforeAckCount
    {
        get => recoveryTracker.RecoveryOwnerReplacedBeforeAckCount;
        set => SetRecoveryBurstState(state => state.OwnerReplacedBeforeAckCount = value);
    }

    private long recoveryOwnerAckWindowMs
    {
        get => recoveryTracker.RecoveryOwnerAckWindowMs;
        set => SetRecoveryReceiptState(state => state.OwnerAckWindowMs = value);
    }

    private long highFrameAgeSuppressedDuringOwnerAckCount
    {
        get => recoveryTracker.HighFrameAgeSuppressedDuringOwnerAckCount;
        set => SetRecoveryBurstState(state => state.HighFrameAgeSuppressedDuringOwnerAckCount = value);
    }

    private long recoveryTimeoutWhileHelperHeadAdvancedCount
    {
        get => recoveryTracker.RecoveryTimeoutWhileHelperHeadAdvancedCount;
        set => SetRecoveryBurstState(state => state.RecoveryTimeoutWhileHelperHeadAdvancedCount = value);
    }

    private long postAckModeGraceSuppressedHighFrameAgeCount
    {
        get => recoveryTracker.PostAckModeGraceSuppressedHighFrameAgeCount;
        set => SetRecoveryBurstState(state => state.PostAckModeGraceSuppressedHighFrameAgeCount = value);
    }

    private long bootstrapGraceSuppressedCatchUpCount
    {
        get => recoveryTracker.BootstrapGraceSuppressedCatchUpCount;
        set => SetRecoveryBurstState(state => state.BootstrapGraceSuppressedCatchUpCount = value);
    }

    private long recoveryLockClearedByAcknowledgedProofCount
    {
        get => recoveryTracker.RecoveryLockClearedByAcknowledgedProofCount;
        set => SetRecoveryLockState(state => state.ClearedByAcknowledgedProofCount = value);
    }

    private long recoveryLockClearedByVisibleProofCount
    {
        get => recoveryTracker.RecoveryLockClearedByVisibleProofCount;
        set => SetRecoveryLockState(state => state.ClearedByVisibleProofCount = value);
    }

    private string recoveryLockLastClearReason
    {
        get => recoveryTracker.RecoveryLockLastClearReason;
        set => SetRecoveryLockState(state => state.LastClearReason = value);
    }

    private long lastRemoteRecoveryReceiptStreamEpoch
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptStreamEpoch;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptStreamEpoch = value);
    }

    private long lastRemoteRecoveryReceiptOwnerFrameId
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptOwnerFrameId;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptOwnerFrameId = value);
    }

    private long lastRemoteRecoveryReceiptVisibleRecoveryFrameId
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptVisibleRecoveryFrameId;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptVisibleRecoveryFrameId = value);
    }

    private long lastRemoteRecoveryReceiptVisibleHeadFrameId
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptVisibleHeadFrameId;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptVisibleHeadFrameId = value);
    }

    private string lastRemoteRecoveryReceiptKind
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptKind;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptKind = value);
    }

    private long remoteRecoveryReceiptRejectedCount
    {
        get => recoveryTracker.RemoteRecoveryReceiptRejectedCount;
        set => SetRecoveryReceiptState(state => state.RemoteRecoveryReceiptRejectedCount = value);
    }

    private string lastRemoteRecoveryReceiptRejectReason
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptRejectReason;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptRejectReason = value);
    }

    private long lastRemoteRecoveryReceiptRejectActiveStreamEpoch
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptRejectActiveStreamEpoch;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptRejectActiveStreamEpoch = value);
    }

    private long lastRemoteRecoveryReceiptRejectActiveOwnerFrameId
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptRejectActiveOwnerFrameId;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptRejectActiveOwnerFrameId = value);
    }

    private string lastRemoteRecoveryReceiptRejectActivePhase
    {
        get => recoveryTracker.LastRemoteRecoveryReceiptRejectActivePhase;
        set => SetRecoveryReceiptState(state => state.LastRemoteRecoveryReceiptRejectActivePhase = value);
    }

    private long lastRecoveryEpochTakeoverSuppressedFromEpoch
    {
        get => recoveryTracker.LastRecoveryEpochTakeoverSuppressedFromEpoch;
        set => SetRecoveryBurstState(state => state.LastEpochTakeoverSuppressedFromEpoch = value);
    }

    private long lastRecoveryEpochTakeoverSuppressedToEpoch
    {
        get => recoveryTracker.LastRecoveryEpochTakeoverSuppressedToEpoch;
        set => SetRecoveryBurstState(state => state.LastEpochTakeoverSuppressedToEpoch = value);
    }

    private string lastRecoveryEpochTakeoverSuppressedPhase
    {
        get => recoveryTracker.LastRecoveryEpochTakeoverSuppressedPhase;
        set => SetRecoveryBurstState(state => state.LastEpochTakeoverSuppressedPhase = value);
    }

    private long helperAckAfterFactSendMs
    {
        get => recoveryTracker.HelperAckAfterFactSendMs;
        set => SetRecoveryReceiptState(state => state.HelperAckAfterFactSendMs = value);
    }

    private long postRecoveryAgeGraceEpoch
    {
        get => recoveryTracker.PostRecoveryAgeGraceEpoch;
        set => SetRecoveryBurstState(state => state.PostRecoveryAgeGraceEpoch = value);
    }

    private DateTimeOffset postRecoveryAgeGraceUntilUtc
    {
        get => recoveryTracker.PostRecoveryAgeGraceUntilUtc;
        set => SetRecoveryBurstState(state => state.PostRecoveryAgeGraceUntilUtc = value);
    }


    private bool HasActiveRecoveryBurst_NoLock()
    {
        return activeRecoveryBurst is not null;
    }

    private RecoveryBurstPhase GetActiveRecoveryBurstPhase_NoLock()
    {
        return activeRecoveryBurst?.Phase ?? RecoveryBurstPhase.Idle;
    }

    private long GetActiveRecoveryBurstStreamEpoch_NoLock()
    {
        return activeRecoveryBurst?.StreamEpoch ?? 0;
    }

    private long GetActiveRecoveryBurstToken_NoLock()
    {
        return activeRecoveryBurst?.BurstToken ?? 0;
    }

    private long GetActiveRecoveryOwnerFrameId_NoLock()
    {
        return activeRecoveryBurst?.OwnerFrameId ?? -1;
    }

    private bool IsRecoveryBurstActiveForEpoch_NoLock(long streamEpoch)
    {
        return streamEpoch > 0 &&
               activeRecoveryBurst is { StreamEpoch: var activeStreamEpoch } &&
               activeStreamEpoch == streamEpoch;
    }

    private bool TryGetRecoverySendMetadata_NoLock(
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        out string? sendRole,
        out long burstToken,
        out bool armLease)
    {
        sendRole = null;
        burstToken = 0;
        armLease = false;

        if (!IsRecoveryBurstActiveForEpoch_NoLock(streamEpoch) ||
            activeRecoveryBurst is null)
        {
            return false;
        }

        if (activeRecoveryBurst.OwnerFrameId < 0)
        {
            if (!isKeyFrame)
            {
                return false;
            }

            sendRole = RecoverySendRoleOwner;
            burstToken = activeRecoveryBurst.BurstToken;
            armLease = true;
            return true;
        }

        return false;
    }

    private bool HelperProofLooksHealthyForEpoch_NoLock(long streamEpoch, DateTimeOffset nowUtc)
    {
        if (streamEpoch <= 0 ||
            helperCurrentEpochStateStreamEpoch != streamEpoch ||
            !remoteHelperFactHealthyActive ||
            Math.Max(remoteHelperFactProofFrameId, GetLatestHelperVisibleProgressFrameId_NoLock()) < 0 ||
            helperLatestVisibleProgressEpoch != streamEpoch ||
            helperLatestVisibleProgressUtc == default)
        {
            return false;
        }

        return nowUtc - helperLatestVisibleProgressUtc <= SatisfiedRecoveryProofFreshnessWindow;
    }

    private bool HasPersistedAcknowledgedHelperProof_NoLock(
        long streamEpoch,
        DateTimeOffset nowUtc,
        out long proofFrameId,
        out long proofAgeMs)
    {
        proofFrameId = -1;
        proofAgeMs = -1;
        if (streamEpoch <= 0 ||
            acknowledgedHelperProofEpoch != streamEpoch ||
            acknowledgedHelperHeadFrameId < 0 ||
            acknowledgedHelperProofUtc == default)
        {
            return false;
        }

        proofFrameId = acknowledgedHelperHeadFrameId;
        proofAgeMs = nowUtc >= acknowledgedHelperProofUtc
            ? Math.Max(0, (long)(nowUtc - acknowledgedHelperProofUtc).TotalMilliseconds)
            : 0;
        return true;
    }

    private bool HasPersistedAcknowledgedVisibleHelperProof_NoLock(
        long streamEpoch,
        DateTimeOffset nowUtc,
        out long proofFrameId,
        out long proofAgeMs)
    {
        proofFrameId = -1;
        proofAgeMs = -1;
        if (streamEpoch <= 0 ||
            acknowledgedVisibleHelperProofEpoch != streamEpoch ||
            acknowledgedVisibleHelperHeadFrameId < 0 ||
            acknowledgedVisibleHelperProofUtc == default)
        {
            return false;
        }

        proofFrameId = acknowledgedVisibleHelperHeadFrameId;
        proofAgeMs = nowUtc >= acknowledgedVisibleHelperProofUtc
            ? Math.Max(0, (long)(nowUtc - acknowledgedVisibleHelperProofUtc).TotalMilliseconds)
            : 0;
        return true;
    }

    private static bool TryGetSatisfiedRecoveryReleaseFloor(
        LastCompletedRecovery completedRecovery,
        out long releaseFloorFrameId,
        out string releaseFloorSource)
    {
        releaseFloorFrameId = -1;
        releaseFloorSource = string.Empty;

        if (!string.Equals(completedRecovery.CompletionKind, "helper_ack", StringComparison.Ordinal))
        {
            return false;
        }

        if (completedRecovery.AckFrameId >= 0)
        {
            releaseFloorFrameId = completedRecovery.AckFrameId;
            releaseFloorSource = "helper_ack_frame";
            return true;
        }

        if (completedRecovery.OwnerFrameId >= 0)
        {
            releaseFloorFrameId = completedRecovery.OwnerFrameId;
            releaseFloorSource = "owner_frame_fallback";
            return true;
        }

        return false;
    }

    private static bool TryGetSatisfiedRecoveryReleaseFloor(
        LastCompletedRecoverySnapshot completedRecovery,
        out long releaseFloorFrameId,
        out string releaseFloorSource)
    {
        releaseFloorFrameId = -1;
        releaseFloorSource = string.Empty;

        if (!string.Equals(completedRecovery.CompletionKind, "helper_ack", StringComparison.Ordinal))
        {
            return false;
        }

        if (completedRecovery.AckFrameId >= 0)
        {
            releaseFloorFrameId = completedRecovery.AckFrameId;
            releaseFloorSource = "helper_ack_frame";
            return true;
        }

        if (completedRecovery.OwnerFrameId >= 0)
        {
            releaseFloorFrameId = completedRecovery.OwnerFrameId;
            releaseFloorSource = "owner_frame_fallback";
            return true;
        }

        return false;
    }

    private bool HasSatisfiedRecoveryFloor_NoLock(
        long streamEpoch,
        DateTimeOffset nowUtc,
        out long satisfiedFloorFrameId,
        out long proofAgeMs)
    {
        satisfiedFloorFrameId = -1;
        proofAgeMs = -1;
        if (streamEpoch <= 0 ||
            satisfiedRecoveryFloorEpoch != streamEpoch ||
            satisfiedRecoveryFloorFrameId < 0 ||
            satisfiedRecoveryFloorUtc == default)
        {
            return false;
        }

        satisfiedFloorFrameId = satisfiedRecoveryFloorFrameId;
        proofAgeMs = nowUtc >= satisfiedRecoveryFloorUtc
            ? Math.Max(0, (long)(nowUtc - satisfiedRecoveryFloorUtc).TotalMilliseconds)
            : 0;
        return true;
    }

    private bool HasFreshSatisfiedRecoveryFloor_NoLock(long streamEpoch, DateTimeOffset nowUtc, out long proofAgeMs)
    {
        return HasSatisfiedRecoveryFloorSatisfiedByAcknowledgedProof_NoLock(
            streamEpoch,
            nowUtc,
            out _,
            out _,
            out proofAgeMs);
    }

    private bool HasSatisfiedRecoveryFloorSatisfiedByAcknowledgedProof_NoLock(
        long streamEpoch,
        DateTimeOffset nowUtc,
        out long satisfiedFloorFrameId,
        out long acknowledgedProofHeadFrameId,
        out long proofAgeMs)
    {
        satisfiedFloorFrameId = -1;
        acknowledgedProofHeadFrameId = -1;
        proofAgeMs = -1;
        if (!HasSatisfiedRecoveryFloor_NoLock(streamEpoch, nowUtc, out satisfiedFloorFrameId, out proofAgeMs))
        {
            return false;
        }

        if (!HasPersistedAcknowledgedVisibleHelperProof_NoLock(streamEpoch, nowUtc, out acknowledgedProofHeadFrameId, out _))
        {
            return false;
        }

        return acknowledgedProofHeadFrameId >= satisfiedFloorFrameId;
    }

    private long GetLatestHelperVisibleProgressFrameId_NoLock()
    {
        return Math.Max(
            helperVisibleRecoveryFloorFrameId,
            helperVisibleHeadFrameId >= 0
                ? helperVisibleHeadFrameId
                : helperLastVisibleApplyFrameId);
    }

    private static bool HasCurrentHelperVisibleOrApplyEvidence(
        int currentEpochApplyCount,
        long lastVisibleApplyFrameId,
        long visibleHeadFrameId,
        long visibleRecoveryFloorFrameId,
        long framesAppliedSinceLastGap)
    {
        return currentEpochApplyCount > 0 ||
               lastVisibleApplyFrameId >= 0 ||
               visibleHeadFrameId >= 0 ||
               visibleRecoveryFloorFrameId >= 0 ||
               framesAppliedSinceLastGap > 0;
    }

    private bool IsReceiptCompletedEpochPromotionSafe_NoLock(long currentStreamEpoch)
    {
        if (currentStreamEpoch <= 0 ||
            lastCompletedRecovery is not { } completedRecovery ||
            completedRecovery.StreamEpoch != currentStreamEpoch ||
            !string.Equals(completedRecovery.AckSource, "helper_visible_receipt", StringComparison.Ordinal) ||
            !HasCurrentHelperVisibleOrApplyEvidence(
                helperCurrentEpochApplyCount,
                helperLastVisibleApplyFrameId,
                helperVisibleHeadFrameId,
                helperVisibleRecoveryFloorFrameId,
                helperFramesAppliedSinceLastGap))
        {
            return false;
        }

        if (activeRecoveryBurst is { } activeBurst &&
            activeBurst.StreamEpoch > currentStreamEpoch)
        {
            return false;
        }

        return true;
    }

    private long ComputeRemoteHelperDerivedFramesAppliedSinceLastGap_NoLock(
        long currentStreamEpoch,
        long inboundFramesAppliedSinceLastGap)
    {
        var derivedFramesAppliedSinceLastGap = Math.Max(0, inboundFramesAppliedSinceLastGap);
        derivedFramesAppliedSinceLastGap = Math.Max(
            derivedFramesAppliedSinceLastGap,
            Math.Max(0, helperCurrentEpochApplyCount));

        var latestHelperVisibleProgressFrameId = GetLatestHelperVisibleProgressFrameId_NoLock();
        if (latestHelperVisibleProgressFrameId >= 0)
        {
            derivedFramesAppliedSinceLastGap = Math.Max(derivedFramesAppliedSinceLastGap, 1);
        }

        if (currentStreamEpoch > 0 &&
            lastCompletedRecovery is { } completedRecovery &&
            completedRecovery.StreamEpoch == currentStreamEpoch &&
            completedRecovery.OwnerFrameId >= 0 &&
            latestHelperVisibleProgressFrameId >= completedRecovery.OwnerFrameId)
        {
            derivedFramesAppliedSinceLastGap = Math.Max(
                derivedFramesAppliedSinceLastGap,
                latestHelperVisibleProgressFrameId - completedRecovery.OwnerFrameId + 1);
        }

        return derivedFramesAppliedSinceLastGap;
    }

    private bool TryDeriveRemoteHelperFactHealthyState_NoLock(
        long currentStreamEpoch,
        bool inboundSteadyVisibleProgressActive,
        long inboundFramesAppliedSinceLastGap,
        DateTimeOffset nowUtc,
        out string source,
        out long proofFrameId,
        out long derivedFramesAppliedSinceLastGap)
    {
        source = string.Empty;
        proofFrameId = -1;
        derivedFramesAppliedSinceLastGap = 0;

        if (currentStreamEpoch <= 0 ||
            helperCurrentEpochStateStreamEpoch != currentStreamEpoch)
        {
            return false;
        }

        proofFrameId = GetLatestHelperVisibleProgressFrameId_NoLock();
        derivedFramesAppliedSinceLastGap = ComputeRemoteHelperDerivedFramesAppliedSinceLastGap_NoLock(
            currentStreamEpoch,
            inboundFramesAppliedSinceLastGap);
        if (proofFrameId < 0)
        {
            return false;
        }

        if (inboundSteadyVisibleProgressActive)
        {
            source = "steady_visible_progress";
            return true;
        }

        if (inboundFramesAppliedSinceLastGap >= 4)
        {
            source = "frames_applied_since_last_gap";
            return true;
        }

        if (HasFreshSatisfiedRecoveryFloor_NoLock(currentStreamEpoch, nowUtc, out _))
        {
            source = "recovery_floor";
            return true;
        }

        return false;
    }

    private static long GetLatestRemoteHelperVisibleProofHeadFrameId(
        long? lastVisibleApplyFrameId,
        long? visibleHeadFrameId,
        long? visibleRecoveryFloorFrameId = null)
    {
        return Math.Max(
            visibleRecoveryFloorFrameId ?? -1,
            (visibleHeadFrameId ?? -1) >= 0
                ? visibleHeadFrameId!.Value
                : (lastVisibleApplyFrameId ?? -1));
    }

    private static long GetLatestRemoteHelperAppliedProofHeadFrameId(
        long? lastVisibleApplyFrameId,
        long? appliedHeadFrameId,
        long? stableVisibleHeadFrameId)
    {
        return Math.Max(
            Math.Max(lastVisibleApplyFrameId ?? -1, appliedHeadFrameId ?? -1),
            stableVisibleHeadFrameId ?? -1);
    }

    private static bool HasInboundRemoteHelperProofRefresh(
        long currentStreamEpoch,
        bool inboundSteadyVisibleProgressActive,
        long inboundFramesAppliedSinceLastGap,
        long inboundProofHeadFrameId)
    {
        return currentStreamEpoch > 0 &&
               inboundProofHeadFrameId >= 0 &&
               (inboundSteadyVisibleProgressActive ||
                inboundFramesAppliedSinceLastGap >= 4);
    }

    private void SetRemoteHelperFactHealthyState_NoLock(
        string source,
        long proofFrameId,
        long framesAppliedSinceLastGap)
    {
        remoteHelperFactHealthyActive = true;
        remoteHelperFactHealthySource = string.IsNullOrWhiteSpace(source)
            ? "unknown"
            : source.Trim();
        remoteHelperFactProofFrameId = Math.Max(remoteHelperFactProofFrameId, proofFrameId);
        helperSteadyVisibleProgressActive = true;
        helperFramesAppliedSinceLastGap = Math.Max(
            helperFramesAppliedSinceLastGap,
            Math.Max(1, framesAppliedSinceLastGap));
        helperCurrentEpochWarmupActive = false;
    }

    private void ClearRemoteHelperFactHealthyState_NoLock(string reason, bool resetSenderProofState)
    {
        if (remoteHelperFactHealthyActive &&
            remoteHelperFactHealthyClearCount < long.MaxValue)
        {
            remoteHelperFactHealthyClearCount++;
        }

        remoteHelperFactHealthyActive = false;
        remoteHelperFactHealthySource = string.Empty;
        remoteHelperFactProofFrameId = -1;
        remoteHelperFactHealthyClearReason = string.IsNullOrWhiteSpace(reason)
            ? "none"
            : reason.Trim();
        if (resetSenderProofState)
        {
            helperSteadyVisibleProgressActive = false;
            helperFramesAppliedSinceLastGap = 0;
        }
    }

    private void RefreshRemoteHelperFactHealthyState_NoLock(
        long currentStreamEpoch,
        bool continuityRecoverySignal,
        bool inboundSteadyVisibleProgressActive,
        long inboundFramesAppliedSinceLastGap,
        DateTimeOffset nowUtc)
    {
        if (currentStreamEpoch <= 0 ||
            helperCurrentEpochStateStreamEpoch != currentStreamEpoch)
        {
            ClearPersistedHelperProof_NoLock();
            if (remoteHelperFactHealthyActive)
            {
                ClearRemoteHelperFactHealthyState_NoLock("stream_epoch_reset", resetSenderProofState: true);
            }

            return;
        }

        if (remoteHelperFactHealthyActive &&
            lastHelperProgressFactReceivedEpoch == currentStreamEpoch &&
            lastHelperProgressFactReceivedUtc != default &&
            nowUtc - lastHelperProgressFactReceivedUtc > RemoteHelperFactHealthyStallWindow)
        {
            ClearRemoteHelperFactHealthyState_NoLock("no_progress_stall", resetSenderProofState: false);
        }

        if (TryDeriveRemoteHelperFactHealthyState_NoLock(
                currentStreamEpoch,
                inboundSteadyVisibleProgressActive,
                inboundFramesAppliedSinceLastGap,
                nowUtc,
                out var source,
                out var proofFrameId,
                out var derivedFramesAppliedSinceLastGap))
        {
            SetRemoteHelperFactHealthyState_NoLock(
                source,
                proofFrameId,
                derivedFramesAppliedSinceLastGap);
            return;
        }

        if (continuityRecoverySignal &&
            remoteHelperFactHealthyActive)
        {
            ClearRemoteHelperFactHealthyState_NoLock("hard_recovery", resetSenderProofState: false);
        }
    }

    private bool TryResolveRecoveryAckFromHelperProgress_NoLock(
        long streamEpoch,
        long recoveryAckTargetFrameId,
        out long ackFrameId,
        out string ackSource)
    {
        ackFrameId = -1;
        ackSource = string.Empty;
        if (streamEpoch <= 0 ||
            recoveryAckTargetFrameId < 0 ||
            helperCurrentEpochStateStreamEpoch != streamEpoch)
        {
            return false;
        }

        if (helperVisibleRecoveryFloorFrameId >= recoveryAckTargetFrameId)
        {
            ackFrameId = helperVisibleRecoveryFloorFrameId;
            ackSource = "visible_recovery_floor";
            return true;
        }

        var visibleApplyFallbackHead = GetLatestHelperVisibleProgressFrameId_NoLock();
        if (helperCurrentEpochRecoveryKeyframeApplyCount > 0 &&
            visibleApplyFallbackHead >= recoveryAckTargetFrameId &&
            (remoteHelperFactHealthyActive ||
             helperSteadyVisibleProgressActive ||
             helperFramesAppliedSinceLastGap >= 4))
        {
            ackFrameId = visibleApplyFallbackHead;
            ackSource = "visible_apply_fallback";
            return true;
        }

        return false;
    }

    private void RefreshAcknowledgedRecoveryProof_NoLock(
        long streamEpoch,
        long visibleHeadFrameId,
        long visibleRecoveryFloorFrameId,
        long stableVisibleHeadFrameId,
        long appliedHeadFrameId,
        long lastVisibleApplyFrameId,
        DateTimeOffset nowUtc)
    {
        if (streamEpoch <= 0)
        {
            return;
        }

        var appliedProofHead = Math.Max(
            stableVisibleHeadFrameId,
            Math.Max(appliedHeadFrameId, lastVisibleApplyFrameId));
        var visibleProofHead = GetLatestRemoteHelperVisibleProofHeadFrameId(
            lastVisibleApplyFrameId,
            visibleHeadFrameId,
            visibleRecoveryFloorFrameId);
        if (appliedProofHead < 0 && visibleProofHead < 0)
        {
            return;
        }

        if (appliedProofHead >= 0)
        {
            if (acknowledgedHelperProofEpoch != streamEpoch)
            {
                acknowledgedHelperProofEpoch = streamEpoch;
                acknowledgedHelperHeadFrameId = appliedProofHead;
            }
            else
            {
                acknowledgedHelperHeadFrameId = Math.Max(acknowledgedHelperHeadFrameId, appliedProofHead);
            }

            acknowledgedHelperProofUtc = nowUtc;
        }

        if (visibleProofHead >= 0)
        {
            if (acknowledgedVisibleHelperProofEpoch != streamEpoch)
            {
                acknowledgedVisibleHelperProofEpoch = streamEpoch;
                acknowledgedVisibleHelperHeadFrameId = visibleProofHead;
            }
            else
            {
                acknowledgedVisibleHelperHeadFrameId = Math.Max(acknowledgedVisibleHelperHeadFrameId, visibleProofHead);
            }

            acknowledgedVisibleHelperProofUtc = nowUtc;
        }

        if (lastCompletedRecovery is { } completedRecovery &&
            completedRecovery.StreamEpoch == streamEpoch &&
            TryGetSatisfiedRecoveryReleaseFloor(
                completedRecovery,
                out var releaseFloorFrameId,
                out var releaseFloorSource) &&
            acknowledgedVisibleHelperHeadFrameId >= releaseFloorFrameId)
        {
            var shouldUpdateSatisfiedFloor =
                satisfiedRecoveryFloorEpoch != streamEpoch ||
                releaseFloorFrameId > satisfiedRecoveryFloorFrameId ||
                (releaseFloorFrameId == satisfiedRecoveryFloorFrameId &&
                 !string.Equals(satisfiedRecoveryFloorSource, releaseFloorSource, StringComparison.Ordinal));

            satisfiedRecoveryFloorEpoch = streamEpoch;
            if (shouldUpdateSatisfiedFloor)
            {
                satisfiedRecoveryFloorFrameId = releaseFloorFrameId;
                satisfiedRecoveryFloorSource = releaseFloorSource;
                if (satisfiedRecoveryFloorVisibleProofCount < long.MaxValue)
                {
                    satisfiedRecoveryFloorVisibleProofCount++;
                }
            }

            satisfiedRecoveryFloorUtc = nowUtc;
        }
    }

    private void ClearAcknowledgedHelperProof_NoLock()
    {
        acknowledgedHelperProofEpoch = 0;
        acknowledgedHelperHeadFrameId = -1;
        acknowledgedHelperProofUtc = default;
        acknowledgedVisibleHelperProofEpoch = 0;
        acknowledgedVisibleHelperHeadFrameId = -1;
        acknowledgedVisibleHelperProofUtc = default;
    }

    private void ClearSatisfiedRecoveryFloor_NoLock()
    {
        satisfiedRecoveryFloorEpoch = 0;
        satisfiedRecoveryFloorFrameId = -1;
        satisfiedRecoveryFloorUtc = default;
        satisfiedRecoveryFloorSource = string.Empty;
    }

    private void ClearPersistedHelperProof_NoLock()
    {
        ClearSatisfiedRecoveryFloor_NoLock();
        ClearAcknowledgedHelperProof_NoLock();
    }

    private bool ClearRecoveryLock_NoLock(
        string reason,
        DateTimeOffset nowUtc,
        out long clearedEpoch,
        out long clearedDurationMs)
    {
        clearedEpoch = 0;
        clearedDurationMs = 0;
        if (!recoveryLockActive)
        {
            return false;
        }

        clearedEpoch = recoveryLockStreamEpoch;
        clearedDurationMs = GetRecoveryLockDurationMs_NoLock(nowUtc);
        recoveryLockActive = false;
        recoveryLockStreamEpoch = 0;
        recoveryLockStartedUtc = default;
        recoveryLockReason = string.Empty;
        recoveryLockLastContinuitySignalSentAtUtcMs = 0;
        recoveryTimeoutResetIssued = false;
        recoveryLockLastClearReason = string.IsNullOrWhiteSpace(reason)
            ? "none"
            : reason.Trim();
        if ((string.Equals(recoveryLockLastClearReason, "acknowledged_helper_proof", StringComparison.Ordinal) ||
             string.Equals(recoveryLockLastClearReason, "acknowledged_visible_helper_proof", StringComparison.Ordinal)) &&
            recoveryLockClearedByAcknowledgedProofCount < long.MaxValue)
        {
            recoveryLockClearedByAcknowledgedProofCount++;
        }

        if (string.Equals(recoveryLockLastClearReason, "acknowledged_visible_helper_proof", StringComparison.Ordinal) &&
            recoveryLockClearedByVisibleProofCount < long.MaxValue)
        {
            recoveryLockClearedByVisibleProofCount++;
        }

        return true;
    }

    private bool TryClearRecoveryLockFromAcknowledgedProof_NoLock(
        long streamEpoch,
        DateTimeOffset nowUtc,
        out long clearedEpoch,
        out long clearedDurationMs)
    {
        clearedEpoch = 0;
        clearedDurationMs = 0;
        if (!IsRecoveryLockEligibleForTrustedVisibleProof_NoLock(streamEpoch))
        {
            return false;
        }

        if (!HasSatisfiedRecoveryFloorSatisfiedByAcknowledgedProof_NoLock(
                streamEpoch,
                nowUtc,
                out _,
                out _,
                out var proofAgeMs))
        {
            return false;
        }

        if (proofAgeMs > (long)SatisfiedRecoveryProofFreshnessWindow.TotalMilliseconds)
        {
            return false;
        }

        return ClearRecoveryLock_NoLock(
            "acknowledged_visible_helper_proof",
            nowUtc,
            out clearedEpoch,
            out clearedDurationMs);
    }

    private bool IsRecoveryLockEligibleForTrustedVisibleProof_NoLock(long streamEpoch)
    {
        if (!recoveryLockActive || streamEpoch <= 0)
        {
            return false;
        }

        if (recoveryLockStreamEpoch == streamEpoch)
        {
            return true;
        }

        return recoveryLockStreamEpoch > 0 &&
               recoveryLockStreamEpoch < streamEpoch &&
               activeRecoveryBurst is null &&
               remoteHelperFactHealthyActive &&
               helperSteadyVisibleProgressActive &&
               helperLatestVisibleProgressEpoch == streamEpoch &&
               helperLatestVisibleProgressUtc != default &&
               helperCurrentEpochNeedMoreInputCount <= 0 &&
               helperCurrentEpochStaleDrops <= 0 &&
               helperFramesAppliedSinceLastGap >= 8 &&
               GetLatestHelperVisibleProgressFrameId_NoLock() >= 0;
    }

    private bool TrySatisfyRecoveryFloorFromTrustedContinuityProof_NoLock(
        long streamEpoch,
        DateTimeOffset nowUtc,
        bool continuityRecoverySignal,
        long recentStaleDrops)
    {
        if ((!continuityRecoverySignal &&
            !remoteHelperFactHealthyActive &&
             !helperSteadyVisibleProgressActive) ||
            !IsRecoveryLockEligibleForTrustedVisibleProof_NoLock(streamEpoch) ||
            activeRecoveryBurst is not null ||
            recentStaleDrops > 0 ||
            helperCurrentEpochNeedMoreInputCount > 0)
        {
            return false;
        }

        var visibleProofHead = Math.Max(
            helperVisibleHeadFrameId,
            Math.Max(helperLastVisibleApplyFrameId, helperStableVisibleHeadFrameId));
        var releaseFloorFrameId = -1L;
        var releaseFloorSource = string.Empty;
        if (helperVisibleRecoveryFloorFrameId >= 0 &&
            visibleProofHead >= helperVisibleRecoveryFloorFrameId)
        {
            releaseFloorFrameId = helperVisibleRecoveryFloorFrameId;
            releaseFloorSource = "visible_recovery_floor";
        }
        else if (string.Equals(remoteHelperFactHealthySource, "steady_visible_progress", StringComparison.Ordinal) &&
                 helperSteadyVisibleProgressActive &&
                 helperStableVisibleHeadFrameId >= 0 &&
                 helperFramesAppliedSinceLastGap >= 8 &&
                 helperCurrentEpochApplyCount >= 8)
        {
            releaseFloorFrameId = helperStableVisibleHeadFrameId;
            releaseFloorSource = "stable_visible_head";
        }

        if (releaseFloorFrameId < 0 ||
            !(remoteHelperFactHealthyActive ||
             helperSteadyVisibleProgressActive ||
              helperFramesAppliedSinceLastGap >= 8))
        {
            return false;
        }

        var shouldUpdateSatisfiedFloor =
            satisfiedRecoveryFloorEpoch != streamEpoch ||
            releaseFloorFrameId > satisfiedRecoveryFloorFrameId ||
            (releaseFloorFrameId == satisfiedRecoveryFloorFrameId &&
             !string.Equals(satisfiedRecoveryFloorSource, releaseFloorSource, StringComparison.Ordinal));

        satisfiedRecoveryFloorEpoch = streamEpoch;
        if (shouldUpdateSatisfiedFloor)
        {
            satisfiedRecoveryFloorFrameId = releaseFloorFrameId;
            satisfiedRecoveryFloorSource = releaseFloorSource;
            if (satisfiedRecoveryFloorVisibleProofCount < long.MaxValue)
            {
                satisfiedRecoveryFloorVisibleProofCount++;
            }
        }

        satisfiedRecoveryFloorUtc = nowUtc;
        postRecoveryAgeGraceEpoch = streamEpoch;
        postRecoveryAgeGraceUntilUtc = nowUtc + PostRecoveryAgeGraceWindow;
        return true;
    }

    private void RecordCompletedRecoveryOutcome_NoLock(
        long streamEpoch,
        long ownerFrameId,
        long ackFrameId,
        string ackSource,
        long ownerEmitToAckMs,
        string completionKind,
        DateTimeOffset completedAtUtc)
    {
        recoveryTracker.RecordCompletedRecoveryOutcome(
            streamEpoch,
            ownerFrameId,
            ackFrameId,
            ackSource,
            ownerEmitToAckMs,
            completionKind,
            completedAtUtc);
    }

    private void ClearCurrentRecoveryAckState_NoLock()
    {
        recoveryTracker.ClearCurrentRecoveryAckState();
    }

    private void ClearLastCompletedRecoveryOutcome_NoLock()
    {
        recoveryTracker.ClearLastCompletedRecoveryOutcome();
        ClearPersistedHelperProof_NoLock();
    }

    private static long ComputeRecoveryCompletionAccountingMismatch(LastCompletedRecoverySnapshot? completedRecovery)
    {
        return ScreenShareSenderRecoveryTracker.ComputeRecoveryCompletionAccountingMismatch(completedRecovery);
    }

    private bool IsRecoveryPostAckHoldActive_NoLock(long streamEpoch)
    {
        return activeRecoveryBurst is { } recoveryBurst &&
               recoveryBurst.StreamEpoch == streamEpoch &&
               streamEpoch > 0 &&
               recoveryBurst.Phase == RecoveryBurstPhase.PostAckHold;
    }

    private long ClearActiveRecoveryBurstAfterCompletion_NoLock()
    {
        if (activeRecoveryBurst is not { } recoveryBurst)
        {
            return 0;
        }

        var clearedTransportBurstToken = recoveryBurst.BurstToken;
        StopRecoveryOwnerPendingTimer_NoLock();
        activeRecoveryBurst = null;
        recoveryGapActive = false;
        ClearRecoveryOwnerPendingHold_NoLock();
        return clearedTransportBurstToken;
    }

    private void StartRecoveryPostAckHold_NoLock(DateTimeOffset startedUtc)
    {
        if (activeRecoveryBurst is null)
        {
            return;
        }

        activeRecoveryBurst.Phase = RecoveryBurstPhase.PostAckHold;
        activeRecoveryBurst.PostAckHoldStartedUtc = startedUtc;
        StopRecoveryOwnerPendingTimer_NoLock();
        recoveryPostAckHoldStartedCount++;
    }

    private bool IsHelperVisibleProgressFreshForEpoch_NoLock(long streamEpoch, DateTimeOffset nowUtc)
    {
        return streamEpoch > 0 &&
               helperLatestVisibleProgressEpoch == streamEpoch &&
               helperLatestVisibleProgressUtc != default &&
               nowUtc - helperLatestVisibleProgressUtc <= TimeSpan.FromMilliseconds(400);
    }

    private bool IsPostAckModeGraceActive_NoLock(long streamEpoch, DateTimeOffset nowUtc)
    {
        return IsRecoveryPostAckHoldActive_NoLock(streamEpoch) &&
               IsHelperVisibleProgressFreshForEpoch_NoLock(streamEpoch, nowUtc);
    }

    private bool IsBeforeFirstVisibleApplyBootstrapGraceActive_NoLock(long streamEpoch, DateTimeOffset nowUtc)
    {
        return streamEpoch > 0 &&
               startupWarmupUntilUtc > nowUtc &&
               helperCurrentEpochStateStreamEpoch == streamEpoch &&
               GetLatestHelperVisibleProgressFrameId_NoLock() < 0 &&
               !recoveryGapActive;
    }

    private bool TryCompleteRecoveryBurstFromLatestHelperProgress(
        DateTimeOffset nowUtc,
        out long completedStreamEpoch,
        out long completedOwnerFrameId,
        out long completedAckFrameId,
        out string completedAckSource,
        out long ownerEmitToFirstVisibleApplyMsValue,
        out long ownerEmitToAckMsValue,
        out long clearedTransportBurstToken)
    {
        lock (gate)
        {
            return TryCompleteRecoveryBurstFromLatestHelperProgress_NoLock(
                nowUtc,
                out completedStreamEpoch,
                out completedOwnerFrameId,
                out completedAckFrameId,
                out completedAckSource,
                out ownerEmitToFirstVisibleApplyMsValue,
                out ownerEmitToAckMsValue,
                out clearedTransportBurstToken);
        }
    }

    private bool TryCompleteRecoveryBurstFromLatestHelperProgress_NoLock(
        DateTimeOffset nowUtc,
        out long completedStreamEpoch,
        out long completedOwnerFrameId,
        out long completedAckFrameId,
        out string completedAckSource,
        out long ownerEmitToFirstVisibleApplyMsValue,
        out long ownerEmitToAckMsValue,
        out long clearedTransportBurstToken)
    {
        completedStreamEpoch = 0;
        completedOwnerFrameId = -1;
        completedAckFrameId = -1;
        completedAckSource = string.Empty;
        ownerEmitToFirstVisibleApplyMsValue = -1;
        ownerEmitToAckMsValue = -1;
        clearedTransportBurstToken = 0;

        if (activeRecoveryBurst is not { } recoveryBurst ||
            recoveryBurst.StreamEpoch <= 0 ||
            recoveryBurst.OwnerFrameId < 0 ||
            recoveryBurst.Phase != RecoveryBurstPhase.OwnerEmittedAwaitingHelperAck ||
            !TryResolveRecoveryAckFromHelperProgress_NoLock(
                recoveryBurst.StreamEpoch,
                recoveryBurst.OwnerFrameId,
                out var resolvedAckFrameId,
                out var resolvedAckSource))
        {
            return false;
        }

        if (recoveryFirstHelperHeadAdvanceUtc == default)
        {
            recoveryFirstHelperHeadAdvanceUtc = nowUtc;
        }

        recoveryOwnerEmitToAckMs = recoveryBurst.OwnerEmittedUtc == default
            ? -1
            : Math.Max(0, (long)(recoveryFirstHelperHeadAdvanceUtc - recoveryBurst.OwnerEmittedUtc).TotalMilliseconds);
        recoveryOwnerAckWindowMs = recoveryOwnerEmitToAckMs;
        recoveryOwnerEmitToFirstVisibleApplyMs = recoveryOwnerEmitToAckMs;
        recoveryOwnerAckFrameId = resolvedAckFrameId;
        recoveryAckSource = resolvedAckSource;
        recoveryBurstCompletedCount++;
        recoveryBurstCompletedByHelperAckCount++;
        if (string.Equals(resolvedAckSource, "visible_recovery_floor", StringComparison.Ordinal))
        {
            recoveryBurstCompletedByVisibleRecoveryFloorCount++;
        }
        else if (string.Equals(resolvedAckSource, "visible_apply_fallback", StringComparison.Ordinal))
        {
            recoveryBurstCompletedByVisibleApplyFallbackCount++;
        }

        RecordCompletedRecoveryOutcome_NoLock(
            recoveryBurst.StreamEpoch,
            recoveryBurst.OwnerFrameId,
            resolvedAckFrameId,
            resolvedAckSource,
            recoveryOwnerEmitToAckMs,
            "helper_ack",
            recoveryFirstHelperHeadAdvanceUtc);
        satisfiedRecoveryFloorEpoch = recoveryBurst.StreamEpoch;
        if (resolvedAckFrameId > satisfiedRecoveryFloorFrameId ||
            !string.Equals(satisfiedRecoveryFloorSource, resolvedAckSource, StringComparison.Ordinal))
        {
            satisfiedRecoveryFloorFrameId = resolvedAckFrameId;
            satisfiedRecoveryFloorSource = resolvedAckSource;
            if (satisfiedRecoveryFloorVisibleProofCount < long.MaxValue)
            {
                satisfiedRecoveryFloorVisibleProofCount++;
            }
        }

        satisfiedRecoveryFloorUtc = nowUtc;
        postRecoveryAgeGraceEpoch = recoveryBurst.StreamEpoch;
        postRecoveryAgeGraceUntilUtc = recoveryFirstHelperHeadAdvanceUtc + PostRecoveryAgeGraceWindow;
        clearedTransportBurstToken = ClearActiveRecoveryBurstAfterCompletion_NoLock();

        completedStreamEpoch = recoveryBurst.StreamEpoch;
        completedOwnerFrameId = recoveryBurst.OwnerFrameId;
        completedAckFrameId = resolvedAckFrameId;
        completedAckSource = resolvedAckSource;
        ownerEmitToFirstVisibleApplyMsValue = recoveryOwnerEmitToFirstVisibleApplyMs;
        ownerEmitToAckMsValue = recoveryOwnerEmitToAckMs;
        return true;
    }

    private bool TryExpireRecoveryPostAckHold_NoLock(
        DateTimeOffset nowUtc,
        out long expiredStreamEpoch,
        out long expiredOwnerFrameId,
        out long clearedTransportBurstToken)
    {
        expiredStreamEpoch = 0;
        expiredOwnerFrameId = -1;
        clearedTransportBurstToken = 0;
        if (activeRecoveryBurst is not { } recoveryBurst ||
            recoveryBurst.Phase != RecoveryBurstPhase.PostAckHold ||
            recoveryBurst.PostAckHoldStartedUtc == default ||
            nowUtc - recoveryBurst.PostAckHoldStartedUtc < RecoveryPostAckHoldTimeout)
        {
            return false;
        }

        expiredStreamEpoch = recoveryBurst.StreamEpoch;
        expiredOwnerFrameId = recoveryBurst.OwnerFrameId;
        clearedTransportBurstToken = recoveryBurst.BurstToken;
        recoveryPostAckHoldExpiredCount++;
        StopRecoveryOwnerPendingTimer_NoLock();
        activeRecoveryBurst = null;
        recoveryGapActive = false;
        ClearRecoveryOwnerPendingHold_NoLock();
        return true;
    }

    private void ClearRecoveryOwnerPendingHold_NoLock()
    {
        recoveryOwnerPendingNonKeyHeldActive = false;
        recoveryOwnerUnackedNonKeyHeldActive = false;
        recoveryOwnerUnackedAdmittedFollowerCount = 0;
    }

    private bool ShouldSuppressPreOwnerSameEpochFrameAtSendTime_NoLock(long streamEpoch, bool isKeyFrame)
    {
        return activeRecoveryBurst is { } recoveryBurst &&
               recoveryBurst.StreamEpoch == streamEpoch &&
               streamEpoch > 0 &&
               !isKeyFrame &&
               recoveryBurst.OwnerFrameId < 0 &&
               (recoveryBurst.Phase == RecoveryBurstPhase.Requested ||
                recoveryBurst.Phase == RecoveryBurstPhase.OwnerPending);
    }

    private bool ShouldHoldPreOwnerSameEpochNonKeyFrame_NoLock(long streamEpoch, bool isKeyFrame)
    {
        return activeRecoveryBurst is { } recoveryBurst &&
               recoveryBurst.StreamEpoch == streamEpoch &&
               streamEpoch > 0 &&
               !isKeyFrame &&
               recoveryBurst.OwnerFrameId < 0 &&
               (recoveryBurst.Phase == RecoveryBurstPhase.Requested ||
                recoveryBurst.Phase == RecoveryBurstPhase.OwnerPending);
    }

    private void RecordHeldPreOwnerSameEpochNonKeyFrame_NoLock()
    {
        if (!recoveryOwnerPendingNonKeyHeldActive)
        {
            recoveryOwnerPendingNonKeyHeldActive = true;
            recoveryOwnerPendingNonKeyHeldCount++;
            return;
        }

        recoveryOwnerPendingNonKeyReplacedCount++;
    }

    private bool IsRecoveryOwnerAwaitingHelperAck_NoLock(long streamEpoch)
    {
        return activeRecoveryBurst is { } recoveryBurst &&
               recoveryBurst.StreamEpoch == streamEpoch &&
               streamEpoch > 0 &&
               recoveryBurst.OwnerFrameId >= 0 &&
               recoveryBurst.Phase == RecoveryBurstPhase.OwnerEmittedAwaitingHelperAck;
    }

    private bool ShouldHoldSameEpochFrameWhileOwnerAwaitingHelperAck_NoLock(long streamEpoch, bool isKeyFrame)
    {
        if (!IsRecoveryOwnerAwaitingHelperAck_NoLock(streamEpoch) ||
            activeRecoveryBurst is not { } recoveryBurst)
        {
            return false;
        }

        if (isKeyFrame)
        {
            recoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount++;
            return true;
        }

        RecordHeldSameEpochNonKeyFrameWhileOwnerAwaitingHelperAck_NoLock();
        return true;
    }

    private void RecordHeldSameEpochNonKeyFrameWhileOwnerAwaitingHelperAck_NoLock()
    {
        if (!recoveryOwnerUnackedNonKeyHeldActive)
        {
            recoveryOwnerUnackedNonKeyHeldActive = true;
            recoveryOwnerUnackedNonKeyHeldCount++;
            return;
        }

        recoveryOwnerUnackedNonKeyReplacedCount++;
    }

    private bool ShouldSuppressFrameWhileOwnerAwaitingHelperAck_NoLock(long streamEpoch, long frameId, bool isKeyFrame)
    {
        var recoveryOwnerFrameId = GetActiveRecoveryOwnerFrameId_NoLock();
        if (!IsRecoveryOwnerAwaitingHelperAck_NoLock(streamEpoch) ||
            activeRecoveryBurst is not { } recoveryBurst ||
            frameId < 0 ||
            recoveryOwnerFrameId < 0)
        {
            return false;
        }

        if (frameId <= recoveryOwnerFrameId)
        {
            return false;
        }

        if (isKeyFrame)
        {
            recoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount++;
            return true;
        }

        if (frameId > GetRecoveryAckTargetFrameId_NoLock(recoveryBurst))
        {
            RecordHeldSameEpochNonKeyFrameWhileOwnerAwaitingHelperAck_NoLock();
            return true;
        }

        return false;
    }

    private bool ShouldHoldSameEpochFrameDuringRecoveryPostAckHold_NoLock(long streamEpoch, bool isKeyFrame)
    {
        if (!IsRecoveryPostAckHoldActive_NoLock(streamEpoch))
        {
            return false;
        }

        if (isKeyFrame)
        {
            recoveryPostAckHoldSuppressedReopenCount++;
        }

        return true;
    }

    private bool IsPostRecoveryAgeGraceActive_NoLock(long streamEpoch, DateTimeOffset nowUtc)
    {
        if (streamEpoch <= 0 ||
            postRecoveryAgeGraceEpoch != streamEpoch ||
            postRecoveryAgeGraceUntilUtc == default)
        {
            return false;
        }

        if (nowUtc > postRecoveryAgeGraceUntilUtc)
        {
            postRecoveryAgeGraceEpoch = 0;
            postRecoveryAgeGraceUntilUtc = default;
            return false;
        }

        return true;
    }

    private RecoveryBurstRequestDecision EvaluateRecoveryBurstRequest_NoLock(
        long streamEpoch,
        string reason,
        DateTimeOffset nowUtc,
        out long gapToRequestMsValue,
        out long recoveryBurstTransportDisarmToken,
        out bool suppressedDueToHelperAck,
        out bool startedWhileHelperProofHealthy)
    {
        gapToRequestMsValue = -1;
        recoveryBurstTransportDisarmToken = 0;
        suppressedDueToHelperAck = false;
        startedWhileHelperProofHealthy = false;
        if (streamEpoch <= 0)
        {
            return RecoveryBurstRequestDecision.None;
        }

        if (IsRecoveryPostAckHoldActive_NoLock(streamEpoch))
        {
            recoveryPostAckHoldSuppressedReopenCount++;
            recoveryBurstRestartSuppressedCount++;
            recoveryBurstStaleRequestSuppressedCount++;
            recoveryBurstRequestSuppressedDueToHelperAckCount++;
            suppressedDueToHelperAck = true;
            return RecoveryBurstRequestDecision.Suppress;
        }

        if (activeRecoveryBurst is { } recoveryBurst)
        {
            if (recoveryBurst.StreamEpoch == streamEpoch)
            {
                if (recoveryBurst.OwnerFrameId < 0)
                {
                    recoveryBurstRestartSuppressedCount++;
                    return RecoveryBurstRequestDecision.Suppress;
                }

                recoveryBurstRestartSuppressedCount++;
                return RecoveryBurstRequestDecision.Suppress;
            }

            if (streamEpoch > recoveryBurst.StreamEpoch)
            {
                if (ShouldFreezeRecoveryBurstIdentity_NoLock(recoveryBurst))
                {
                    RecordRecoveryEpochTakeoverSuppressed_NoLock(
                        recoveryBurst.StreamEpoch,
                        streamEpoch,
                        recoveryBurst.Phase);
                    recoveryBurstRestartSuppressedCount++;
                    return RecoveryBurstRequestDecision.Suppress;
                }

                if (recoveryBurst.OwnerFrameId >= 0 &&
                    recoveryBurst.BurstToken > 0)
                {
                    recoveryBurstTransportDisarmToken = recoveryBurst.BurstToken;
                    recoveryOwnerReplacedBeforeAckCount++;
                }

                recoveryBurstProfileTransitionTakeoverCount++;
                activeRecoveryBurst = new ActiveRecoveryBurst
                {
                    StreamEpoch = streamEpoch,
                    Phase = RecoveryBurstPhase.Requested,
                    OwnerFrameId = -1,
                    BurstToken = ++nextRecoveryBurstToken,
                    ProtectedFollowerBudgetRemaining = 0,
                    NextProtectedFollowerFrameId = -1,
                    RequestedUtc = nowUtc,
                    OwnerEmittedUtc = default,
                    PostAckHoldStartedUtc = default,
                    ForcedResetIssued = false,
                };
                recoveryFirstHelperHeadAdvanceUtc = default;
                recoveryProtectedFollowerCount = 0;
                recoveryProtectedFrameCount = 0;
                recoveryStartAppliedHeadFrameId = helperAppliedHeadFrameId;
                recoveryStartLastVisibleApplyFrameId = helperLastVisibleApplyFrameId;
                recoveryOwnerAckFrameId = -1;
                recoveryOwnerEmitToAckMs = -1;
                recoveryOwnerAckWindowMs = -1;
                recoveryAckSource = string.Empty;
                ClearRecoveryOwnerPendingHold_NoLock();
                if (recoveryGapActive &&
                    recoveryGapStreamEpoch == streamEpoch &&
                    recoveryGapStartedUtc != default)
                {
                    gapToRequestMsValue = Math.Max(0, (long)(nowUtc - recoveryGapStartedUtc).TotalMilliseconds);
                    recoveryGapToKeyframeRequestMs = gapToRequestMsValue;
                }

                return RecoveryBurstRequestDecision.EpochTakeover;
            }

            recoveryBurstRestartSuppressedCount++;
            return RecoveryBurstRequestDecision.Suppress;
        }

        if (recoveryGapActive &&
            recoveryGapStreamEpoch == streamEpoch &&
            recoveryGapStartedUtc != default)
        {
            gapToRequestMsValue = Math.Max(0, (long)(nowUtc - recoveryGapStartedUtc).TotalMilliseconds);
            recoveryGapToKeyframeRequestMs = gapToRequestMsValue;
        }

        startedWhileHelperProofHealthy = HelperProofLooksHealthyForEpoch_NoLock(streamEpoch, nowUtc);
        if (startedWhileHelperProofHealthy)
        {
            recoveryBurstStartedWhileHelperProofHealthyCount++;
        }

        activeRecoveryBurst = new ActiveRecoveryBurst
        {
            StreamEpoch = streamEpoch,
            Phase = RecoveryBurstPhase.Requested,
            OwnerFrameId = -1,
            BurstToken = ++nextRecoveryBurstToken,
            ProtectedFollowerBudgetRemaining = 0,
            NextProtectedFollowerFrameId = -1,
            RequestedUtc = nowUtc,
            OwnerEmittedUtc = default,
            PostAckHoldStartedUtc = default,
            ForcedResetIssued = false,
        };
        recoveryFirstHelperHeadAdvanceUtc = default;
        recoveryProtectedFollowerCount = 0;
        recoveryProtectedFrameCount = 0;
        recoveryStartAppliedHeadFrameId = helperAppliedHeadFrameId;
        recoveryStartLastVisibleApplyFrameId = helperLastVisibleApplyFrameId;
        recoveryOwnerAckFrameId = -1;
        recoveryOwnerEmitToAckMs = -1;
        recoveryOwnerAckWindowMs = -1;
        recoveryAckSource = string.Empty;
        ClearRecoveryOwnerPendingHold_NoLock();
        return RecoveryBurstRequestDecision.Start;
    }

    private void MarkRecoveryBurstKeyframeRequestIssued_NoLock(long streamEpoch)
    {
        if (activeRecoveryBurst is null ||
            activeRecoveryBurst.StreamEpoch != streamEpoch)
        {
            return;
        }

        if (activeRecoveryBurst.OwnerFrameId < 0)
        {
            activeRecoveryBurst.Phase = RecoveryBurstPhase.OwnerPending;
            StopRecoveryOwnerPendingTimer_NoLock();
        }
    }

    private void OnRecoveryOwnerPendingTimerTick()
    {
        if (Interlocked.Exchange(ref recoveryOwnerPendingTimerInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            TryForceOwnerPendingRecoveryReset();
        }
        finally
        {
            Interlocked.Exchange(ref recoveryOwnerPendingTimerInFlight, 0);
        }
    }

    private void TryForceOwnerPendingRecoveryReset()
    {
        IScreenCaptureSource? currentCaptureSource;
        ScreenShareFrameSendPipeline? currentPipeline;
        string currentSessionId;
        ScreenShareTransportTuningLevel currentTransportTuningLevel;
        DateTimeOffset requestedUtc;
        long burstToken;
        long resetEpoch = 0;
        var nowUtc = clock.UtcNow;
        const string reason = "recovery_owner_pending_forced_reset";

        lock (gate)
        {
            if (disposed ||
                activeRecoveryBurst is not { } recoveryBurst ||
                recoveryBurst.Phase != RecoveryBurstPhase.OwnerPending ||
                recoveryBurst.OwnerFrameId >= 0 ||
                recoveryBurst.ForcedResetIssued ||
                recoveryBurst.RequestedUtc == default ||
                nowUtc - recoveryBurst.RequestedUtc < RecoveryOwnerPendingForcedResetDelay)
            {
                return;
            }

            recoveryBurst.ForcedResetIssued = true;
            recoveryOwnerPendingForcedResetCount++;
            StopRecoveryOwnerPendingTimer_NoLock();
            currentCaptureSource = captureSource;
            currentPipeline = sendPipeline;
            currentSessionId = sessionId;
            currentTransportTuningLevel = transportTuningLevel;
            requestedUtc = recoveryBurst.RequestedUtc;
            burstToken = recoveryBurst.BurstToken;
        }

        var droppedQueuedFrames = 0;
        var droppedPendingRawFrames = 0;
        if (currentPipeline is not null)
        {
            droppedQueuedFrames = currentPipeline.FlushPendingFrames();
            currentPipeline.ResetPacingWindow();
        }

        flushTransportQueue?.Invoke(reason);
        droppedPendingRawFrames = PurgeSenderRawBacklog(currentCaptureSource);

        if (currentCaptureSource is IScreenCaptureTransportRecoveryResetSource resetSource)
        {
            resetEpoch = resetSource.ForceTransportRecoveryReset(currentTransportTuningLevel);
        }
        else
        {
            resetEpoch = GetCaptureFreshnessMetricsSnapshot(currentCaptureSource).CurrentStreamEpoch;
        }

        if (currentCaptureSource is IScreenCaptureAdaptiveTuning adaptiveCaptureSource &&
            captureFpsHint > 0)
        {
            adaptiveCaptureSource.SetCaptureFrameRateHint(captureFpsHint);
        }

        if (currentCaptureSource is IScreenCaptureKeyFrameRequestSource keyFrameRequestSource)
        {
            keyFrameRequestSource.RequestKeyFrame(reason);
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_keyframe_requested; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; reason={reason}");
        }

        lock (gate)
        {
            if (disposed ||
                activeRecoveryBurst is not { } recoveryBurst ||
                recoveryBurst.BurstToken != burstToken)
            {
                return;
            }

            ResetHelperCurrentEpochState_NoLock(resetEpoch);
            activeRecoveryBurst = new ActiveRecoveryBurst
            {
                StreamEpoch = resetEpoch > 0 ? resetEpoch : recoveryBurst.StreamEpoch,
                Phase = RecoveryBurstPhase.OwnerPending,
                OwnerFrameId = -1,
                BurstToken = burstToken,
                ProtectedFollowerBudgetRemaining = 0,
                NextProtectedFollowerFrameId = -1,
                RequestedUtc = requestedUtc == default ? nowUtc : requestedUtc,
                OwnerEmittedUtc = default,
                PostAckHoldStartedUtc = default,
                ForcedResetIssued = true,
            };
            recoveryFirstHelperHeadAdvanceUtc = default;
            recoveryProtectedFollowerCount = 0;
            recoveryProtectedFrameCount = 0;
            recoveryStartAppliedHeadFrameId = helperAppliedHeadFrameId;
            recoveryStartLastVisibleApplyFrameId = helperLastVisibleApplyFrameId;
            recoveryOwnerAckFrameId = -1;
            recoveryOwnerEmitToAckMs = -1;
            recoveryOwnerAckWindowMs = -1;
            recoveryOwnerEmitToFirstVisibleApplyMs = -1;
            recoveryAckSource = string.Empty;
            ClearRecoveryOwnerPendingHold_NoLock();
        }

        if (droppedQueuedFrames > 0)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_frame_dropped_backlog; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; dropped_count={droppedQueuedFrames}; reason={reason}");
        }

        if (droppedPendingRawFrames > 0)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_raw_backlog_purged; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; dropped_count={droppedPendingRawFrames}; reason={reason}");
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_sender_recovery_owner_pending_forced_reset; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, resetEpoch)}; burst_token={Math.Max(0, burstToken)}; requested_delay_ms={Math.Max(0, (long)(nowUtc - requestedUtc).TotalMilliseconds)}; transport_tuning_level={currentTransportTuningLevel}");
    }

    private void HandleRecoveryBurstFrameSent(
        string currentSessionId,
        long frameStreamEpoch,
        long frameId,
        bool isKeyFrame,
        DateTimeOffset sentUtc)
    {
        var logOwnerEmitted = false;
        var logForcedResetOwnerEmitted = false;
        long ownerEmitLatencyMs = -1;

        lock (gate)
        {
            if (activeRecoveryBurst is null ||
                activeRecoveryBurst.StreamEpoch != frameStreamEpoch)
            {
                return;
            }

            if (activeRecoveryBurst.OwnerFrameId < 0)
            {
                if (!isKeyFrame)
                {
                    return;
                }

                activeRecoveryBurst.OwnerFrameId = frameId;
                activeRecoveryBurst.OwnerEmittedUtc = sentUtc;
                activeRecoveryBurst.PostAckHoldStartedUtc = default;
                recoveryProtectedFrameCount = 1;
                recoveryProtectedFollowerCount = 0;
                recoveryOwnerUnackedAdmittedFollowerCount = 0;
                activeRecoveryBurst.Phase = RecoveryBurstPhase.OwnerEmittedAwaitingHelperAck;
                recoveryBurstControlFallbackCount++;
                if (activeRecoveryBurst.ForcedResetIssued)
                {
                    recoveryKeyframeEmittedAfterForcedResetCount++;
                    logForcedResetOwnerEmitted = true;
                }

                StopRecoveryOwnerPendingTimer_NoLock();
                ClearRecoveryOwnerPendingHold_NoLock();
                if (activeRecoveryBurst.RequestedUtc != default)
                {
                    recoveryKeyframeRequestToOwnerEmitMs = Math.Max(
                        0,
                        (long)(sentUtc - activeRecoveryBurst.RequestedUtc).TotalMilliseconds);
                    ownerEmitLatencyMs = recoveryKeyframeRequestToOwnerEmitMs;
                }

                logOwnerEmitted = true;
            }
        }

        if (logOwnerEmitted)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_recovery_burst_owner_emitted; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, frameStreamEpoch)}; recovery_owner_frame_id={frameId}; recovery_keyframe_request_to_owner_emit_ms={(ownerEmitLatencyMs >= 0 ? ownerEmitLatencyMs.ToString(CultureInfo.InvariantCulture) : "(none)")}");
        }

        if (logForcedResetOwnerEmitted)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_recovery_keyframe_emitted_after_forced_reset; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, frameStreamEpoch)}; recovery_owner_frame_id={frameId}; latency_ms={(ownerEmitLatencyMs >= 0 ? ownerEmitLatencyMs.ToString(CultureInfo.InvariantCulture) : "(none)")}");
        }
    }

    private bool TryTimeoutRecoveryBurst(
        DateTimeOffset nowUtc,
        out long timedOutStreamEpoch,
        out long timedOutOwnerFrameId,
        out long timedOutBurstToken,
        out string completionKind,
        out string completionAckSource)
    {
        timedOutStreamEpoch = 0;
        timedOutOwnerFrameId = -1;
        timedOutBurstToken = 0;
        completionKind = "timeout";
        completionAckSource = string.Empty;
        lock (gate)
        {
            if (activeRecoveryBurst is null ||
                activeRecoveryBurst.Phase == RecoveryBurstPhase.PostAckHold)
            {
                return false;
            }

            var timeoutAnchorUtc =
                activeRecoveryBurst.OwnerEmittedUtc != default
                    ? activeRecoveryBurst.OwnerEmittedUtc
                    : activeRecoveryBurst.RequestedUtc;
            if (timeoutAnchorUtc == default ||
                nowUtc - timeoutAnchorUtc < RecoveryBurstTimeout)
            {
                return false;
            }

            timedOutStreamEpoch = activeRecoveryBurst.StreamEpoch;
            timedOutOwnerFrameId = activeRecoveryBurst.OwnerFrameId;
            timedOutBurstToken = activeRecoveryBurst.BurstToken;
            recoveryBurstTimeoutCount++;
            recoveryBurstCompletedCount++;
            recoveryBurstCompletedByTimeoutCount++;
            recoveryOwnerAckFrameId = -1;
            recoveryOwnerEmitToAckMs = -1;
            recoveryOwnerAckWindowMs = -1;
            recoveryOwnerEmitToFirstVisibleApplyMs = -1;
            recoveryAckSource = string.Empty;
            helperAckAfterFactSendMs = -1;
            recoveryFirstHelperHeadAdvanceUtc = default;
            RecordCompletedRecoveryOutcome_NoLock(
                timedOutStreamEpoch,
                timedOutOwnerFrameId,
                ackFrameId: -1,
                ackSource: string.Empty,
                ownerEmitToAckMs: -1,
                completionKind: "timeout",
                completedAtUtc: nowUtc);
            ClearRecoveryLock_NoLock(
                "timeout",
                nowUtc,
                out _,
                out _);
            ClearActiveRecoveryBurstAfterCompletion_NoLock();
            return true;
        }
    }

    internal void SetRemoteRecoveryReceipt(ScreenShareRecoveryReceiptV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var nowUtc = clock.UtcNow;
        var normalizedReceiptKind = string.IsNullOrWhiteSpace(message.ReceiptKind)
            ? string.Empty
            : message.ReceiptKind.Trim();
        string currentSessionId;
        bool completedRecoveryBurstFromReceipt = false;
        bool clearedRecoveryLock = false;
        long recoveryLockLogEpoch = 0;
        long recoveryLockDurationMs = 0;
        string recoveryLockClearReason = string.Empty;
        long completedOwnerFrameId = -1;
        long completedAckFrameId = -1;
        long ownerEmitToAckMsValue = -1;
        long ownerEmitToFirstVisibleApplyMsValue = -1;
        long clearedTransportBurstToken = 0;
        bool ignoredReceipt = false;
        string ignoredReceiptReason = string.Empty;
        long ignoredReceiptActiveStreamEpoch = 0;
        long ignoredReceiptActiveOwnerFrameId = -1;
        string ignoredReceiptActivePhase = string.Empty;

        lock (gate)
        {
            currentSessionId = sessionId;
            lastRemoteRecoveryReceiptStreamEpoch = Math.Max(0, message.StreamEpoch);
            lastRemoteRecoveryReceiptOwnerFrameId = message.OwnerFrameId;
            lastRemoteRecoveryReceiptVisibleRecoveryFrameId = message.VisibleRecoveryFrameId;
            lastRemoteRecoveryReceiptVisibleHeadFrameId = message.VisibleHeadFrameId;
            lastRemoteRecoveryReceiptKind = normalizedReceiptKind;

            if (message.StreamEpoch <= 0 ||
                message.OwnerFrameId < 0 ||
                message.VisibleRecoveryFrameId < message.OwnerFrameId ||
                message.VisibleHeadFrameId < message.VisibleRecoveryFrameId)
            {
                ignoredReceipt = true;
                ignoredReceiptReason = "invalid_message";
            }
            else if (activeRecoveryBurst is not { } recoveryBurst)
            {
                ignoredReceipt = true;
                ignoredReceiptReason = "no_active_burst";
            }
            else
            {
                ignoredReceiptActiveStreamEpoch = recoveryBurst.StreamEpoch;
                ignoredReceiptActiveOwnerFrameId = recoveryBurst.OwnerFrameId;
                ignoredReceiptActivePhase = FormatRecoveryBurstPhase(recoveryBurst.Phase);

                if (recoveryBurst.StreamEpoch != message.StreamEpoch)
                {
                    ignoredReceipt = true;
                    ignoredReceiptReason = "wrong_stream_epoch";
                }
                else if (recoveryBurst.OwnerFrameId != message.OwnerFrameId)
                {
                    ignoredReceipt = true;
                    ignoredReceiptReason = "wrong_owner_frame";
                }
                else if (recoveryBurst.Phase != RecoveryBurstPhase.OwnerEmittedAwaitingHelperAck &&
                         recoveryBurst.Phase != RecoveryBurstPhase.PostAckHold)
                {
                    ignoredReceipt = true;
                    ignoredReceiptReason = "wrong_phase";
                }
                else if (recoveryBurst.Phase == RecoveryBurstPhase.PostAckHold &&
                         lastCompletedRecovery is { } completedRecovery &&
                         completedRecovery.StreamEpoch == recoveryBurst.StreamEpoch &&
                         completedRecovery.OwnerFrameId == recoveryBurst.OwnerFrameId)
                {
                    ignoredReceipt = true;
                    ignoredReceiptReason = "already_completed";
                }
                else if (recoveryBurst.OwnerEmittedUtc == default)
                {
                    ignoredReceipt = true;
                    ignoredReceiptReason = "owner_not_emitted";
                }
                else
                {
                    if (recoveryFirstHelperHeadAdvanceUtc == default)
                    {
                        recoveryFirstHelperHeadAdvanceUtc = nowUtc;
                    }

                    helperVisibleHeadFrameId = Math.Max(helperVisibleHeadFrameId, message.VisibleHeadFrameId);
                    helperVisibleRecoveryFloorFrameId = Math.Max(helperVisibleRecoveryFloorFrameId, message.VisibleRecoveryFrameId);
                    helperLastVisibleApplyFrameId = Math.Max(helperLastVisibleApplyFrameId, message.VisibleRecoveryFrameId);
                    helperCurrentEpochStateStreamEpoch = Math.Max(helperCurrentEpochStateStreamEpoch, message.StreamEpoch);
                    helperLatestVisibleProgressEpoch = message.StreamEpoch;
                    helperLatestVisibleProgressUtc = nowUtc;
                    lastHelperProgressFactReceivedEpoch = message.StreamEpoch;
                    lastHelperProgressFactReceivedUtc = nowUtc;
                    acknowledgedVisibleHelperProofEpoch = message.StreamEpoch;
                    acknowledgedVisibleHelperHeadFrameId = Math.Max(acknowledgedVisibleHelperHeadFrameId, message.VisibleHeadFrameId);
                    acknowledgedVisibleHelperProofUtc = nowUtc;
                    acknowledgedHelperProofEpoch = message.StreamEpoch;
                    acknowledgedHelperHeadFrameId = Math.Max(acknowledgedHelperHeadFrameId, message.VisibleRecoveryFrameId);
                    acknowledgedHelperProofUtc = nowUtc;
                    satisfiedRecoveryFloorEpoch = message.StreamEpoch;
                    satisfiedRecoveryFloorFrameId = Math.Max(satisfiedRecoveryFloorFrameId, message.VisibleRecoveryFrameId);
                    satisfiedRecoveryFloorUtc = nowUtc;
                    satisfiedRecoveryFloorSource = "helper_visible_receipt";
                    if (satisfiedRecoveryFloorVisibleProofCount < long.MaxValue)
                    {
                        satisfiedRecoveryFloorVisibleProofCount++;
                    }

                    recoveryOwnerEmitToAckMs = Math.Max(
                        0,
                        (long)(nowUtc - recoveryBurst.OwnerEmittedUtc).TotalMilliseconds);
                    recoveryOwnerAckWindowMs = recoveryOwnerEmitToAckMs;
                    recoveryOwnerEmitToFirstVisibleApplyMs = recoveryOwnerEmitToAckMs;
                    recoveryOwnerAckFrameId = message.VisibleRecoveryFrameId;
                    recoveryAckSource = "helper_visible_receipt";
                    recoveryBurstCompletedCount++;
                    recoveryBurstCompletedByHelperAckCount++;
                    recoveryBurstCompletedByHelperVisibleReceiptCount++;

                    RecordCompletedRecoveryOutcome_NoLock(
                        recoveryBurst.StreamEpoch,
                        recoveryBurst.OwnerFrameId,
                        message.VisibleRecoveryFrameId,
                        "helper_visible_receipt",
                        recoveryOwnerEmitToAckMs,
                        "helper_ack",
                        nowUtc);
                    postRecoveryAgeGraceEpoch = recoveryBurst.StreamEpoch;
                    postRecoveryAgeGraceUntilUtc = nowUtc + PostRecoveryAgeGraceWindow;
                    helperAckAfterFactSendMs = 0;
                    if (ClearRecoveryLock_NoLock(
                            "helper_visible_receipt",
                            nowUtc,
                            out recoveryLockLogEpoch,
                            out recoveryLockDurationMs))
                    {
                        clearedRecoveryLock = true;
                        recoveryLockClearReason = recoveryLockLastClearReason;
                    }

                    completedRecoveryBurstFromReceipt = true;
                    completedOwnerFrameId = recoveryBurst.OwnerFrameId;
                    completedAckFrameId = message.VisibleRecoveryFrameId;
                    ownerEmitToAckMsValue = recoveryOwnerEmitToAckMs;
                    ownerEmitToFirstVisibleApplyMsValue = recoveryOwnerEmitToFirstVisibleApplyMs;
                    StartRecoveryPostAckHold_NoLock(nowUtc);
                }
            }

            if (ignoredReceipt)
            {
                if (remoteRecoveryReceiptRejectedCount < long.MaxValue)
                {
                    remoteRecoveryReceiptRejectedCount++;
                }

                lastRemoteRecoveryReceiptRejectReason = ignoredReceiptReason;
                lastRemoteRecoveryReceiptRejectActiveStreamEpoch = ignoredReceiptActiveStreamEpoch;
                lastRemoteRecoveryReceiptRejectActiveOwnerFrameId = ignoredReceiptActiveOwnerFrameId;
                lastRemoteRecoveryReceiptRejectActivePhase = ignoredReceiptActivePhase;
            }
        }

        if (clearedTransportBurstToken > 0)
        {
            clearRecoveryBurstTransportFallback?.Invoke(clearedTransportBurstToken);
        }

        if (clearedRecoveryLock)
        {
            if (!string.IsNullOrWhiteSpace(currentSessionId) && recoveryLockLogEpoch > 0)
            {
                ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                    currentSessionId,
                    recoveryLockLogEpoch,
                    "recovery_lock_cleared");
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_recovery_lock_cleared; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, recoveryLockLogEpoch)}; reason={(string.IsNullOrWhiteSpace(recoveryLockClearReason) ? "helper_visible_receipt" : recoveryLockClearReason)}; lock_duration_ms={recoveryLockDurationMs}; current_epoch_need_more_input_count=unavailable; last_clean_frame_id=unavailable; triggered_profile_change=0");
        }

        if (completedRecoveryBurstFromReceipt)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_recovery_burst_completed; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, message.StreamEpoch)}; recovery_owner_frame_id={completedOwnerFrameId}; completion=helper_visible_receipt; helper_head_frame_id={completedAckFrameId}; recovery_ack_source=helper_visible_receipt; receipt_kind={(string.IsNullOrWhiteSpace(normalizedReceiptKind) ? "(none)" : normalizedReceiptKind)}; recovery_owner_emit_to_ack_ms={(ownerEmitToAckMsValue >= 0 ? ownerEmitToAckMsValue.ToString(CultureInfo.InvariantCulture) : "(none)")}; recovery_owner_emit_to_first_visible_apply_ms={(ownerEmitToFirstVisibleApplyMsValue >= 0 ? ownerEmitToFirstVisibleApplyMsValue.ToString(CultureInfo.InvariantCulture) : "(none)")}");
        }
        else if (ignoredReceipt)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_recovery_receipt_ignored; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, message.StreamEpoch)}; owner_frame_id={message.OwnerFrameId}; visible_recovery_frame_id={message.VisibleRecoveryFrameId}; visible_head_frame_id={message.VisibleHeadFrameId}; receipt_kind={(string.IsNullOrWhiteSpace(normalizedReceiptKind) ? "(none)" : normalizedReceiptKind)}; reason={(string.IsNullOrWhiteSpace(ignoredReceiptReason) ? "(none)" : ignoredReceiptReason)}; active_recovery_stream_epoch={(ignoredReceiptActiveStreamEpoch > 0 ? ignoredReceiptActiveStreamEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}; active_recovery_owner_frame_id={(ignoredReceiptActiveOwnerFrameId >= 0 ? ignoredReceiptActiveOwnerFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; active_recovery_phase={(string.IsNullOrWhiteSpace(ignoredReceiptActivePhase) ? "(none)" : ignoredReceiptActivePhase)}");
        }
    }

    private bool TryExpireRecoveryPostAckHold(
        DateTimeOffset nowUtc,
        out long expiredStreamEpoch,
        out long expiredOwnerFrameId,
        out long clearedTransportBurstToken)
    {
        lock (gate)
        {
            return TryExpireRecoveryPostAckHold_NoLock(
                nowUtc,
                out expiredStreamEpoch,
                out expiredOwnerFrameId,
                out clearedTransportBurstToken);
        }
    }

    internal void SetRemotePressureState(
        ScreenShareRemotePressureMode mode,
        string? reason,
        long observedFrameAgeMs,
        long recentStaleDrops,
        long sentAtUtcMs = 0,
        bool? currentEpochWarmupActive = null,
        int? currentEpochApplyCount = null,
        long? currentEpochNeedMoreInputCount = null,
        long? lastVisibleApplyFrameId = null,
        long? visibleHeadFrameId = null,
        long? appliedHeadFrameId = null,
        bool? steadyVisibleProgressActive = null,
        long? stableVisibleHeadFrameId = null,
        long? framesAppliedSinceLastGap = null,
        long? visibleRecoveryFloorFrameId = null,
        long? currentEpochRecoveryKeyframeApplyCount = null)
    {
        IScreenCaptureSource? currentCaptureSource;
        ScreenShareFrameSendPipeline? currentPipeline;
        string currentSessionId;
        ScreenShareRemotePressureMode previousMode;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? ScreenSharePressureProtocol.PressureReasonHealthy
            : reason.Trim();
        var nowUtc = clock.UtcNow;
        var continuityRecoverySignal = string.Equals(
            normalizedReason,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            StringComparison.Ordinal);
        var continuityRecoveryTimeoutSignal =
            continuityRecoverySignal &&
            mode == ScreenShareRemotePressureMode.CatchUpOnly;
        var inboundSteadyVisibleProgressActive = steadyVisibleProgressActive == true;
        var inboundFramesAppliedSinceLastGap = Math.Max(0L, framesAppliedSinceLastGap ?? -1L);

        lock (gate)
        {
            currentCaptureSource = captureSource;
            currentPipeline = sendPipeline;
            currentSessionId = sessionId;
        }

        long recoveryBurstTransportClearToken = 0;
        var currentStreamEpoch = GetCaptureFreshnessMetricsSnapshot(currentCaptureSource).CurrentStreamEpoch;
        RefreshHelperCurrentEpochState(currentStreamEpoch);
        if (continuityRecoverySignal &&
            currentStreamEpoch > 0)
        {
            lock (gate)
            {
                if (!recoveryGapActive || recoveryGapStreamEpoch != currentStreamEpoch)
                {
                    recoveryGapActive = true;
                    recoveryGapStreamEpoch = currentStreamEpoch;
                    recoveryGapStartedUtc = clock.UtcNow;
                    recoveryGapCount++;
                    if (activeRecoveryBurst is { } recoveryBurst &&
                        recoveryBurst.StreamEpoch != currentStreamEpoch)
                    {
                        if (recoveryBurst.OwnerFrameId >= 0 &&
                            recoveryBurst.BurstToken > 0)
                        {
                            recoveryBurstTransportClearToken = recoveryBurst.BurstToken;
                            recoveryOwnerReplacedBeforeAckCount++;
                        }

                        StopRecoveryOwnerPendingTimer_NoLock();
                        activeRecoveryBurst = null;
                        recoveryFirstHelperHeadAdvanceUtc = default;
                        recoveryProtectedFollowerCount = 0;
                        recoveryProtectedFrameCount = 0;
                        ClearRecoveryOwnerPendingHold_NoLock();
                    }

                }
            }
        }

        var localBackpressureProbe = transportBackpressureProbeResolver?.Invoke();
        var localHealthIssues = localBackpressureProbe?.ScreenShareTransportRecentHealthIssueCount ?? 0;
        var localHealthSevere = localBackpressureProbe?.IsScreenShareTransportHealthSeverelyDegraded == true;
        var localLaneCongestion = localBackpressureProbe?.IsScreenShareTransportCongested == true;
        var localLaneSevereCongestion = localBackpressureProbe?.IsScreenShareTransportSeverelyCongested == true;
        var localLaneQueueDepth = Math.Max(0, localBackpressureProbe?.ScreenShareTransportQueueDepth ?? 0);
        var localLaneRecentDrops = Math.Max(0L, localBackpressureProbe?.ScreenShareTransportRecentDropCount ?? 0);
        var localHealthActionable = HasActionableBridgeHealth(
            localHealthIssues,
            localHealthSevere,
            localLaneCongestion,
            localLaneSevereCongestion,
            localLaneQueueDepth,
            localLaneRecentDrops,
            mode);
        if (mode == ScreenShareRemotePressureMode.None &&
            localHealthActionable &&
            !continuityRecoverySignal)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_pressure_state_ignored; mode=none; reason={normalizedReason}; local_bridge_health_kind={FormatBridgeHealthKind(localHealthIssues > 0 || localHealthSevere, localHealthActionable)}; recent_health_issues={localHealthIssues}; queue_depth={localLaneQueueDepth}; recent_drops={localLaneRecentDrops}");
            return;
        }

        bool startedRecoveryLock = false;
        bool clearedRecoveryLock = false;
        long recoveryLockLogEpoch = 0;
        long recoveryLockDurationMs = 0;
        bool recoveryLockTriggeredProfileChange = false;
        string recoveryLockClearReason = string.Empty;
        bool shouldStartRecoveryTimeoutReset = false;
        bool ignoredStaleRecoveryMessage = false;
        long ignoredRecoveryLockSentAtUtcMs = 0;
        bool suppressedHighFrameAgeDueToPostRecoveryGrace = false;
        bool suppressedHighFrameAgeDuringOwnerAck = false;
        bool completedRecoveryBurstFromVisibleProof = false;
        long completedRecoveryBurstStreamEpoch = 0;
        long completedRecoveryOwnerFrameId = -1;
        long completedRecoveryAckFrameId = -1;
        string completedRecoveryAckSource = string.Empty;
        long completedRecoveryOwnerEmitToFirstVisibleApplyMs = -1;
        long completedRecoveryOwnerEmitToAckMs = -1;
        bool satisfiedRecoveryFloorFromTrustedContinuityProof = false;

        void TryCompleteVisibleProofRecovery_NoLock()
        {
            if (!completedRecoveryBurstFromVisibleProof &&
                TryCompleteRecoveryBurstFromLatestHelperProgress_NoLock(
                    nowUtc,
                    out completedRecoveryBurstStreamEpoch,
                    out completedRecoveryOwnerFrameId,
                    out completedRecoveryAckFrameId,
                    out completedRecoveryAckSource,
                    out completedRecoveryOwnerEmitToFirstVisibleApplyMs,
                    out completedRecoveryOwnerEmitToAckMs,
                    out var completedRecoveryTransportBurstToken))
            {
                completedRecoveryBurstFromVisibleProof = true;
                if (completedRecoveryTransportBurstToken > 0)
                {
                    recoveryBurstTransportClearToken = completedRecoveryTransportBurstToken;
                }
            }

            satisfiedRecoveryFloorFromTrustedContinuityProof =
                TrySatisfyRecoveryFloorFromTrustedContinuityProof_NoLock(
                    currentStreamEpoch,
                    nowUtc,
                    continuityRecoverySignal,
                    recentStaleDrops) ||
                satisfiedRecoveryFloorFromTrustedContinuityProof;

            if (!clearedRecoveryLock &&
                TryClearRecoveryLockFromAcknowledgedProof_NoLock(
                    currentStreamEpoch,
                    nowUtc,
                    out recoveryLockLogEpoch,
                    out recoveryLockDurationMs))
            {
                clearedRecoveryLock = true;
                recoveryLockClearReason = recoveryLockLastClearReason;
            }
        }

        lock (gate)
        {
            previousMode = remotePressureMode;
            if (continuityRecoverySignal)
            {
                var effectiveSentAtUtcMs = sentAtUtcMs > 0
                    ? sentAtUtcMs
                    : nowUtc.ToUnixTimeMilliseconds();
                var lockableContinuitySignal = currentStreamEpoch > 0;
                var postReceiptHoldActive =
                    activeRecoveryBurst is { } currentRecoveryBurst &&
                    currentRecoveryBurst.StreamEpoch == currentStreamEpoch &&
                    currentRecoveryBurst.Phase == RecoveryBurstPhase.PostAckHold &&
                    lastCompletedRecovery is { } completedRecoveryForReceiptHold &&
                    completedRecoveryForReceiptHold.StreamEpoch == currentStreamEpoch &&
                    completedRecoveryForReceiptHold.OwnerFrameId == currentRecoveryBurst.OwnerFrameId &&
                    string.Equals(completedRecoveryForReceiptHold.AckSource, "helper_visible_receipt", StringComparison.Ordinal);
                if (lockableContinuitySignal &&
                    !recoveryLockActive &&
                    !postReceiptHoldActive)
                {
                    startedRecoveryLock = true;
                    recoveryLockStartedUtc = nowUtc;
                }

                if (lockableContinuitySignal &&
                    !postReceiptHoldActive)
                {
                    recoveryLockActive = true;
                    recoveryLockReason = normalizedReason;
                    recoveryLockStreamEpoch = currentStreamEpoch > 0
                        ? currentStreamEpoch
                        : recoveryLockStreamEpoch;
                    recoveryLockLastContinuitySignalSentAtUtcMs = Math.Max(
                        recoveryLockLastContinuitySignalSentAtUtcMs,
                        effectiveSentAtUtcMs);
                    recoveryLockLogEpoch = recoveryLockStreamEpoch;
                    recoveryLockDurationMs = recoveryLockStartedUtc == default
                        ? 0
                        : Math.Max(0, (long)(nowUtc - recoveryLockStartedUtc).TotalMilliseconds);
                }

                remotePressureMode = ScreenShareRemotePressureMode.None;
                remotePressureReason = normalizedReason;
                remotePressureObservedFrameAgeMs = 0;
                remotePressureRecentStaleDrops = Math.Max(0, recentStaleDrops);
                remotePressureAppliedUtc = null;
                if (currentEpochWarmupActive.HasValue)
                {
                    helperCurrentEpochWarmupActive = currentEpochWarmupActive.Value;
                }

                if (currentEpochApplyCount.HasValue)
                {
                    helperCurrentEpochApplyCount = Math.Max(0, currentEpochApplyCount.Value);
                }

                if (currentEpochNeedMoreInputCount.HasValue)
                {
                    helperCurrentEpochNeedMoreInputCount = Math.Max(0, currentEpochNeedMoreInputCount.Value);
                }

                var helperVisibleProgressAdvanced = false;
                var helperVisibleRecoveryFloorAdvanced = false;
                if (visibleHeadFrameId is { } validVisibleHeadFrameId)
                {
                    if (validVisibleHeadFrameId > helperVisibleHeadFrameId)
                    {
                        helperVisibleProgressAdvanced = true;
                    }

                    helperVisibleHeadFrameId = Math.Max(
                        helperVisibleHeadFrameId,
                        Math.Max(-1, validVisibleHeadFrameId));
                }

                if (visibleRecoveryFloorFrameId is { } validVisibleRecoveryFloorFrameId)
                {
                    if (validVisibleRecoveryFloorFrameId > helperVisibleRecoveryFloorFrameId)
                    {
                        helperVisibleRecoveryFloorAdvanced = true;
                    }

                    helperVisibleRecoveryFloorFrameId = Math.Max(
                        helperVisibleRecoveryFloorFrameId,
                        Math.Max(-1, validVisibleRecoveryFloorFrameId));
                }

                if (lastVisibleApplyFrameId is { } validLastVisibleApplyFrameId)
                {
                    if (validLastVisibleApplyFrameId > helperLastVisibleApplyFrameId)
                    {
                        helperVisibleProgressAdvanced = true;
                    }

                    helperLastVisibleApplyFrameId = Math.Max(
                        helperLastVisibleApplyFrameId,
                        Math.Max(-1, validLastVisibleApplyFrameId));
                }

                if (appliedHeadFrameId is { } validAppliedHeadFrameId)
                {
                    helperAppliedHeadFrameId = Math.Max(
                        helperAppliedHeadFrameId,
                        Math.Max(-1, validAppliedHeadFrameId));
                }

                if (stableVisibleHeadFrameId is { } validStableVisibleHeadFrameId)
                {
                    helperStableVisibleHeadFrameId = Math.Max(
                        helperStableVisibleHeadFrameId,
                        Math.Max(-1, validStableVisibleHeadFrameId));
                }

                if (currentEpochRecoveryKeyframeApplyCount is { } validCurrentEpochRecoveryKeyframeApplyCount)
                {
                    helperCurrentEpochRecoveryKeyframeApplyCount = Math.Max(
                        helperCurrentEpochRecoveryKeyframeApplyCount,
                        Math.Max(0L, validCurrentEpochRecoveryKeyframeApplyCount));
                }

                var inboundProofHeadFrameId = GetLatestRemoteHelperVisibleProofHeadFrameId(
                    lastVisibleApplyFrameId,
                    visibleHeadFrameId,
                    visibleRecoveryFloorFrameId);
                var inboundHelperProofRefresh = HasInboundRemoteHelperProofRefresh(
                    currentStreamEpoch,
                    inboundSteadyVisibleProgressActive,
                    inboundFramesAppliedSinceLastGap,
                    inboundProofHeadFrameId);

                if ((helperVisibleProgressAdvanced ||
                     helperVisibleRecoveryFloorAdvanced ||
                     inboundProofHeadFrameId >= 0) &&
                    currentStreamEpoch > 0)
                {
                    if (helperVisibleProgressAdvanced || helperVisibleRecoveryFloorAdvanced)
                    {
                        helperLatestVisibleProgressEpoch = currentStreamEpoch;
                        helperLatestVisibleProgressUtc = nowUtc;
                        senderReceivedHelperProgressDuringContinuityLossCount++;
                    }

                    lastHelperProgressFactReceivedEpoch = currentStreamEpoch;
                    lastHelperProgressFactReceivedUtc = nowUtc;
                    RefreshAcknowledgedRecoveryProof_NoLock(
                        currentStreamEpoch,
                        helperVisibleHeadFrameId,
                        helperVisibleRecoveryFloorFrameId,
                        helperStableVisibleHeadFrameId,
                        helperAppliedHeadFrameId,
                        helperLastVisibleApplyFrameId,
                        nowUtc);
                }
                else if (inboundHelperProofRefresh)
                {
                    lastHelperProgressFactReceivedEpoch = currentStreamEpoch;
                    lastHelperProgressFactReceivedUtc = nowUtc;
                }

                if ((currentEpochApplyCount.HasValue && currentEpochApplyCount.Value > 0) ||
                    helperVisibleProgressAdvanced ||
                    helperVisibleRecoveryFloorAdvanced ||
                    helperLastVisibleApplyFrameId >= 0 ||
                    helperAppliedHeadFrameId >= 0 ||
                    helperStableVisibleHeadFrameId >= 0)
                {
                    helperCurrentEpochWarmupActive = false;
                }

                RefreshRemoteHelperFactHealthyState_NoLock(
                    currentStreamEpoch,
                    continuityRecoverySignal: true,
                    inboundSteadyVisibleProgressActive,
                    inboundFramesAppliedSinceLastGap,
                    nowUtc);
                TryCompleteVisibleProofRecovery_NoLock();
                var continuitySignalIgnoredDueToSatisfiedFloor = HasSatisfiedRecoveryFloorSatisfiedByAcknowledgedProof_NoLock(
                    currentStreamEpoch,
                    nowUtc,
                    out _,
                    out _,
                    out _);
                var hasPersistedHelperProof = HasPersistedAcknowledgedVisibleHelperProof_NoLock(
                    currentStreamEpoch,
                    nowUtc,
                    out _,
                    out _);
                if (continuitySignalIgnoredDueToSatisfiedFloor &&
                    continuitySignalIgnoredDueToSatisfiedFloorCount < long.MaxValue)
                {
                    continuitySignalIgnoredDueToSatisfiedFloorCount++;
                }

                if (continuitySignalIgnoredDueToSatisfiedFloor &&
                    continuitySignalIgnoredDueToVisibleSatisfiedFloorCount < long.MaxValue)
                {
                    continuitySignalIgnoredDueToVisibleSatisfiedFloorCount++;
                }

                if (!remoteHelperFactHealthyActive &&
                    !hasPersistedHelperProof)
                {
                    helperSteadyVisibleProgressActive = false;
                    helperFramesAppliedSinceLastGap = 0;
                }

                helperReducedModeEntryStableVisibleHeadFrameId = -1;
                helperReducedModeEntryStreamEpoch = 0;
                if (!completedRecoveryBurstFromVisibleProof &&
                    !satisfiedRecoveryFloorFromTrustedContinuityProof)
                {
                    postRecoveryAgeGraceEpoch = 0;
                    postRecoveryAgeGraceUntilUtc = default;
                }

                if (currentStreamEpoch > 0 &&
                    lastCompletedRecovery is { } completedRecovery &&
                    completedRecovery.StreamEpoch > 0 &&
                    currentStreamEpoch > completedRecovery.StreamEpoch)
                {
                    ClearLastCompletedRecoveryOutcome_NoLock();
                }

                if (!remoteHelperFactHealthyActive &&
                    !hasPersistedHelperProof)
                {
                    helperCurrentEpochHealthySignalCount = 0;
                }

                helperCurrentEpochStaleDrops = remotePressureRecentStaleDrops;
                if (stableVisibleHeadFrameId is { } validStableVisibleHeadFrameIdForContinuityFacts)
                {
                    helperStableVisibleHeadFrameId = Math.Max(
                        helperStableVisibleHeadFrameId,
                        Math.Max(-1, validStableVisibleHeadFrameIdForContinuityFacts));
                }
                if (continuityRecoveryTimeoutSignal &&
                    !recoveryTimeoutResetIssued)
                {
                    shouldStartRecoveryTimeoutReset = false;
                }

            }
            else
            {
                if (recoveryLockActive &&
                    sentAtUtcMs > 0 &&
                    recoveryLockLastContinuitySignalSentAtUtcMs > 0 &&
                    sentAtUtcMs < recoveryLockLastContinuitySignalSentAtUtcMs)
                {
                    ignoredStaleRecoveryMessage = true;
                    ignoredRecoveryLockSentAtUtcMs = recoveryLockLastContinuitySignalSentAtUtcMs;
                }
                else
                {
                    if (currentEpochWarmupActive.HasValue)
                    {
                        helperCurrentEpochWarmupActive = currentEpochWarmupActive.Value;
                    }

                    if (currentEpochApplyCount.HasValue)
                    {
                        helperCurrentEpochApplyCount = Math.Max(0, currentEpochApplyCount.Value);
                    }

                    if (currentEpochNeedMoreInputCount.HasValue)
                    {
                        helperCurrentEpochNeedMoreInputCount = Math.Max(0, currentEpochNeedMoreInputCount.Value);
                    }

                    var helperVisibleProgressAdvanced = false;
                    var helperVisibleRecoveryFloorAdvanced = false;
                    if (visibleHeadFrameId is { } validVisibleHeadFrameId)
                    {
                        if (validVisibleHeadFrameId > helperVisibleHeadFrameId)
                        {
                            helperVisibleProgressAdvanced = true;
                        }

                        helperVisibleHeadFrameId = Math.Max(
                            helperVisibleHeadFrameId,
                            Math.Max(-1, validVisibleHeadFrameId));
                    }

                    if (visibleRecoveryFloorFrameId is { } validVisibleRecoveryFloorFrameId)
                    {
                        if (validVisibleRecoveryFloorFrameId > helperVisibleRecoveryFloorFrameId)
                        {
                            helperVisibleRecoveryFloorAdvanced = true;
                        }

                        helperVisibleRecoveryFloorFrameId = Math.Max(
                            helperVisibleRecoveryFloorFrameId,
                            Math.Max(-1, validVisibleRecoveryFloorFrameId));
                    }

                    if (lastVisibleApplyFrameId is { } validLastVisibleApplyFrameId)
                    {
                        if (validLastVisibleApplyFrameId > helperLastVisibleApplyFrameId)
                        {
                            helperVisibleProgressAdvanced = true;
                        }

                        helperLastVisibleApplyFrameId = Math.Max(
                            helperLastVisibleApplyFrameId,
                            Math.Max(-1, validLastVisibleApplyFrameId));
                    }

                    if (appliedHeadFrameId is { } validAppliedHeadFrameId)
                    {
                        helperAppliedHeadFrameId = Math.Max(
                            helperAppliedHeadFrameId,
                            Math.Max(-1, validAppliedHeadFrameId));
                    }

                    if (stableVisibleHeadFrameId is { } validStableVisibleHeadFrameIdForFacts)
                    {
                        helperStableVisibleHeadFrameId = Math.Max(
                            helperStableVisibleHeadFrameId,
                            Math.Max(-1, validStableVisibleHeadFrameIdForFacts));
                    }

                    if (currentEpochRecoveryKeyframeApplyCount is { } validCurrentEpochRecoveryKeyframeApplyCount)
                    {
                        helperCurrentEpochRecoveryKeyframeApplyCount = Math.Max(
                            helperCurrentEpochRecoveryKeyframeApplyCount,
                            Math.Max(0L, validCurrentEpochRecoveryKeyframeApplyCount));
                    }

                    var inboundProofHeadFrameId = GetLatestRemoteHelperVisibleProofHeadFrameId(
                        lastVisibleApplyFrameId,
                        visibleHeadFrameId,
                        visibleRecoveryFloorFrameId);
                    var inboundHelperProofRefresh = HasInboundRemoteHelperProofRefresh(
                        currentStreamEpoch,
                        inboundSteadyVisibleProgressActive,
                        inboundFramesAppliedSinceLastGap,
                        inboundProofHeadFrameId);

                    if ((helperVisibleProgressAdvanced || helperVisibleRecoveryFloorAdvanced) &&
                        currentStreamEpoch > 0)
                    {
                        helperLatestVisibleProgressEpoch = currentStreamEpoch;
                        helperLatestVisibleProgressUtc = nowUtc;
                        if (continuityRecoverySignal)
                        {
                            senderReceivedHelperProgressDuringContinuityLossCount++;
                        }

                        lastHelperProgressFactReceivedEpoch = currentStreamEpoch;
                        lastHelperProgressFactReceivedUtc = nowUtc;
                        RefreshAcknowledgedRecoveryProof_NoLock(
                            currentStreamEpoch,
                            helperVisibleHeadFrameId,
                            helperVisibleRecoveryFloorFrameId,
                            helperStableVisibleHeadFrameId,
                            helperAppliedHeadFrameId,
                            helperLastVisibleApplyFrameId,
                            nowUtc);
                    }
                    else if (inboundHelperProofRefresh)
                    {
                        lastHelperProgressFactReceivedEpoch = currentStreamEpoch;
                        lastHelperProgressFactReceivedUtc = nowUtc;
                    }

                    if ((currentEpochApplyCount.HasValue && currentEpochApplyCount.Value > 0) ||
                        helperVisibleProgressAdvanced ||
                        helperVisibleRecoveryFloorAdvanced ||
                        helperLastVisibleApplyFrameId >= 0 ||
                        helperAppliedHeadFrameId >= 0 ||
                        helperStableVisibleHeadFrameId >= 0)
                    {
                        helperCurrentEpochWarmupActive = false;
                    }

                    RefreshRemoteHelperFactHealthyState_NoLock(
                        currentStreamEpoch,
                        continuityRecoverySignal: false,
                        inboundSteadyVisibleProgressActive,
                        inboundFramesAppliedSinceLastGap,
                        nowUtc);
                    TryCompleteVisibleProofRecovery_NoLock();
                    var hasPersistedHelperProof = HasPersistedAcknowledgedVisibleHelperProof_NoLock(
                        currentStreamEpoch,
                        nowUtc,
                        out _,
                        out _);
                    var effectiveMode = mode;
                    var effectiveReason = normalizedReason;
                    var ownerAckWindowActive =
                        IsRecoveryOwnerAwaitingHelperAck_NoLock(currentStreamEpoch);
                    var helperVisibleProgressFresh =
                        currentStreamEpoch > 0 &&
                        helperLatestVisibleProgressEpoch == currentStreamEpoch &&
                        helperLatestVisibleProgressUtc != default &&
                        nowUtc - helperLatestVisibleProgressUtc <= TimeSpan.FromMilliseconds(400);
                    if (mode == ScreenShareRemotePressureMode.ReduceFps &&
                        string.Equals(normalizedReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) &&
                        ownerAckWindowActive &&
                        helperVisibleProgressFresh)
                    {
                        suppressedHighFrameAgeDuringOwnerAck = true;
                        highFrameAgeSuppressedDuringOwnerAckCount++;
                        effectiveMode = ScreenShareRemotePressureMode.None;
                        effectiveReason = ScreenSharePressureProtocol.PressureReasonHealthy;
                    }

                    if (mode == ScreenShareRemotePressureMode.ReduceFps &&
                        string.Equals(normalizedReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) &&
                        !suppressedHighFrameAgeDuringOwnerAck &&
                        IsPostRecoveryAgeGraceActive_NoLock(currentStreamEpoch, nowUtc) &&
                        activeRecoveryBurst is null)
                    {
                        var latestVisibleProgressFrameId = GetLatestHelperVisibleProgressFrameId_NoLock();
                        var completedRecoveryGraceApplies =
                            lastCompletedRecovery is { } completedRecoveryForGrace &&
                            completedRecoveryForGrace.OwnerFrameId >= 0 &&
                            latestVisibleProgressFrameId >= completedRecoveryForGrace.OwnerFrameId;
                        var satisfiedFloorGraceApplies =
                            HasFreshSatisfiedRecoveryFloor_NoLock(currentStreamEpoch, nowUtc, out _) &&
                            latestVisibleProgressFrameId >= satisfiedRecoveryFloorFrameId;
                        if (completedRecoveryGraceApplies ||
                            satisfiedFloorGraceApplies)
                        {
                            suppressedHighFrameAgeDueToPostRecoveryGrace = true;
                            postRecoveryAgeGraceSuppressedCount++;
                            effectiveMode = ScreenShareRemotePressureMode.None;
                            effectiveReason = ScreenSharePressureProtocol.PressureReasonHealthy;
                        }
                    }

                    remotePressureMode = effectiveMode;
                    remotePressureReason = effectiveReason;
                    remotePressureObservedFrameAgeMs = Math.Max(0, observedFrameAgeMs);
                    remotePressureRecentStaleDrops = Math.Max(0, recentStaleDrops);
                    remotePressureAppliedUtc = effectiveMode == ScreenShareRemotePressureMode.None ? null : nowUtc;

                    var isHealthyPressure =
                        effectiveMode == ScreenShareRemotePressureMode.None &&
                        string.Equals(effectiveReason, ScreenSharePressureProtocol.PressureReasonHealthy, StringComparison.Ordinal);
                    if (isHealthyPressure)
                    {
                        if (inboundSteadyVisibleProgressActive)
                        {
                            helperSteadyVisibleProgressActive = true;
                        }

                        if (stableVisibleHeadFrameId is { } validStableVisibleHeadFrameId)
                        {
                            helperStableVisibleHeadFrameId = Math.Max(
                                helperStableVisibleHeadFrameId,
                                Math.Max(-1, validStableVisibleHeadFrameId));
                        }

                        if (framesAppliedSinceLastGap is { } validFramesAppliedSinceLastGap)
                        {
                            helperFramesAppliedSinceLastGap = Math.Max(
                                helperFramesAppliedSinceLastGap,
                                Math.Max(0, validFramesAppliedSinceLastGap));
                        }

                        RefreshAcknowledgedRecoveryProof_NoLock(
                            currentStreamEpoch,
                            helperVisibleHeadFrameId,
                            helperVisibleRecoveryFloorFrameId,
                            helperStableVisibleHeadFrameId,
                            helperAppliedHeadFrameId,
                            helperLastVisibleApplyFrameId,
                            nowUtc);
                        TryCompleteVisibleProofRecovery_NoLock();
                    }
                    else
                    {
                        if (!remoteHelperFactHealthyActive &&
                            !hasPersistedHelperProof)
                        {
                            helperSteadyVisibleProgressActive = false;
                            helperFramesAppliedSinceLastGap = 0;
                            helperReducedModeEntryStableVisibleHeadFrameId = -1;
                            helperReducedModeEntryStreamEpoch = 0;
                            postRecoveryAgeGraceEpoch = 0;
                            postRecoveryAgeGraceUntilUtc = default;
                            ClearSatisfiedRecoveryFloor_NoLock();
                        }
                    }

                    if (isHealthyPressure &&
                        remotePressureRecentStaleDrops == 0)
                    {
                        if (!currentEpochWarmupActive.HasValue)
                        {
                            helperCurrentEpochWarmupActive = false;
                        }

                        if (helperCurrentEpochHealthySignalCount < int.MaxValue)
                        {
                            helperCurrentEpochHealthySignalCount++;
                        }

                        helperCurrentEpochStaleDrops = 0;
                    }
                    else
                    {
                        if (!remoteHelperFactHealthyActive &&
                            !hasPersistedHelperProof)
                        {
                            helperCurrentEpochHealthySignalCount = 0;
                        }

                        helperCurrentEpochStaleDrops = remotePressureRecentStaleDrops;
                    }
                }
            }
        }

        if (recoveryBurstTransportClearToken > 0)
        {
            clearRecoveryBurstTransportFallback?.Invoke(recoveryBurstTransportClearToken);
        }

        if (ignoredStaleRecoveryMessage)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_pressure_state_ignored; mode={FormatRemotePressureMode(mode)}; reason={normalizedReason}; ignore_reason=stale_recovery_message; incoming_sent_at_utc_ms={sentAtUtcMs}; recovery_lock_sent_at_utc_ms={ignoredRecoveryLockSentAtUtcMs}");
            return;
        }

        if (startedRecoveryLock)
        {
            if (!string.IsNullOrWhiteSpace(currentSessionId) && recoveryLockLogEpoch > 0)
            {
                ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                    currentSessionId,
                    recoveryLockLogEpoch,
                    "recovery_lock_started");
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_recovery_lock_started; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, recoveryLockLogEpoch)}; reason={normalizedReason}; lock_duration_ms=0; current_epoch_need_more_input_count=unavailable; last_clean_frame_id=unavailable; triggered_profile_change=0");
        }

        if (clearedRecoveryLock)
        {
            if (!string.IsNullOrWhiteSpace(currentSessionId) && recoveryLockLogEpoch > 0)
            {
                ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                    currentSessionId,
                    recoveryLockLogEpoch,
                    "recovery_lock_cleared");
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_recovery_lock_cleared; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, recoveryLockLogEpoch)}; reason={(string.IsNullOrWhiteSpace(recoveryLockClearReason) ? normalizedReason : recoveryLockClearReason)}; lock_duration_ms={recoveryLockDurationMs}; current_epoch_need_more_input_count=unavailable; last_clean_frame_id=unavailable; triggered_profile_change={(recoveryLockTriggeredProfileChange ? 1 : 0)}");
        }

        if (completedRecoveryBurstFromVisibleProof)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_recovery_burst_completed; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, completedRecoveryBurstStreamEpoch)}; recovery_owner_frame_id={completedRecoveryOwnerFrameId}; completion=helper_head_advance; helper_head_frame_id={completedRecoveryAckFrameId}; recovery_ack_source={(string.IsNullOrWhiteSpace(completedRecoveryAckSource) ? "(none)" : completedRecoveryAckSource)}; recovery_owner_emit_to_ack_ms={(completedRecoveryOwnerEmitToAckMs >= 0 ? completedRecoveryOwnerEmitToAckMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; recovery_owner_emit_to_first_visible_apply_ms={(completedRecoveryOwnerEmitToFirstVisibleApplyMs >= 0 ? completedRecoveryOwnerEmitToFirstVisibleApplyMs.ToString(CultureInfo.InvariantCulture) : "(none)")}");
        }

        if (suppressedHighFrameAgeDueToPostRecoveryGrace)
        {
            var recoveryOwnerFrameIdForLog = lastCompletedRecovery?.OwnerFrameId ?? -1;
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_post_recovery_age_grace_suppressed; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, currentStreamEpoch)}; recovery_owner_frame_id={(recoveryOwnerFrameIdForLog >= 0 ? recoveryOwnerFrameIdForLog.ToString(CultureInfo.InvariantCulture) : "(none)")}"); 
        }

        if (suppressedHighFrameAgeDuringOwnerAck)
        {
            var recoveryOwnerFrameIdForLog = -1L;
            lock (gate)
            {
                recoveryOwnerFrameIdForLog = GetActiveRecoveryOwnerFrameId_NoLock();
            }
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_owner_ack_window_age_suppressed; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, currentStreamEpoch)}; recovery_owner_frame_id={(recoveryOwnerFrameIdForLog >= 0 ? recoveryOwnerFrameIdForLog.ToString(CultureInfo.InvariantCulture) : "(none)")}"); 
        }

        if (!continuityRecoverySignal)
        {
            RecordTransitionRemoteApplySignal(
                currentSessionId,
                normalizedReason,
                observedFrameAgeMs,
                recentStaleDrops);
        }

        if (currentPipeline is not null &&
            mode == ScreenShareRemotePressureMode.CatchUpOnly &&
            !continuityRecoverySignal)
        {
            var droppedQueuedFrames = currentPipeline.FlushPendingFrames();
            if (droppedQueuedFrames > 0)
            {
                LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_frame_dropped_backlog; session_id={currentSessionId}; dropped_count={droppedQueuedFrames}; reason=remote_pressure");
            }
        }

        if (mode == ScreenShareRemotePressureMode.CatchUpOnly &&
            previousMode != ScreenShareRemotePressureMode.CatchUpOnly &&
            !continuityRecoverySignal)
        {
            RequestKeyFrame("remote_catch_up_only");
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_pressure_state_applied; mode={FormatRemotePressureMode(continuityRecoverySignal ? ScreenShareRemotePressureMode.None : mode)}; reason={normalizedReason}; observed_frame_age_ms={(continuityRecoverySignal ? 0 : Math.Max(0, observedFrameAgeMs))}; recent_stale_drops={Math.Max(0, recentStaleDrops)}");

        if (shouldStartRecoveryTimeoutReset)
        {
            TryStartRecoveryTimeoutReset(currentPipeline, currentCaptureSource, currentSessionId, normalizedReason);
        }

        OnAutoTuneTimerTick();
    }

    private void TryStartRecoveryTimeoutReset(
        ScreenShareFrameSendPipeline? currentPipeline,
        IScreenCaptureSource? currentCaptureSource,
        string currentSessionId,
        string reason)
    {
        var sessionIdForLog = string.IsNullOrWhiteSpace(currentSessionId)
            ? "(none)"
            : currentSessionId;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_recovery_timeout_reset_started; session_id={sessionIdForLog}; stream_epoch={Math.Max(0, recoveryLockStreamEpoch)}; reason={reason}; lock_duration_ms={GetRecoveryLockDurationMs(clock.UtcNow)}; current_epoch_need_more_input_count=unavailable; last_clean_frame_id=unavailable; triggered_profile_change=0");

        try
        {
            var nextHint = ResolveSenderTargetFramesPerSecond(
                ScreenShareSenderFreshnessMode.Reduced,
                configuredCap: Math.Min(FeatureFlags.ScreenShareMaxFps, FeatureFlags.ScreenShareTransportMaxFps),
                inStartupWarmup: false);
            var previousMode = ScreenShareSenderFreshnessMode.Normal;
            var previousTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
            var degradedStateChanged = false;
            var profileChanged = false;
            long resetEpoch = 0;
            var droppedQueuedFrames = 0;
            var droppedPendingRawFrames = 0;

            lock (gate)
            {
                previousMode = senderFreshnessMode;
                previousTransportTuningLevel = transportTuningLevel;
                degradedStateChanged = IsSenderFreshnessDegraded(previousMode) != IsSenderFreshnessDegraded(ScreenShareSenderFreshnessMode.Reduced);
                profileChanged = previousTransportTuningLevel != ScreenShareTransportTuningLevel.BandwidthReduced;
                senderFreshnessMode = ScreenShareSenderFreshnessMode.Reduced;
                captureFpsHint = nextHint;
                transportTuningLevel = ScreenShareTransportTuningLevel.BandwidthReduced;
            }

            if (currentPipeline is not null)
            {
                droppedQueuedFrames = currentPipeline.FlushPendingFrames();
                currentPipeline.ResetPacingWindow();
                currentPipeline.SetMaxFramesPerSecond(nextHint);
            }

            flushTransportQueue?.Invoke("recovery_timeout_reset");

            droppedPendingRawFrames = PurgeSenderRawBacklog(currentCaptureSource);
            if (currentCaptureSource is IScreenCaptureAdaptiveTuning adaptiveCaptureSource)
            {
                adaptiveCaptureSource.SetCaptureFrameRateHint(nextHint);
            }

            if (currentCaptureSource is IScreenCaptureTransportRecoveryResetSource resetSource)
            {
                resetEpoch = resetSource.ForceTransportRecoveryReset(ScreenShareTransportTuningLevel.BandwidthReduced);
            }
            else if (currentCaptureSource is IScreenCaptureAdaptiveTuning fallbackTuningSource)
            {
                fallbackTuningSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.BandwidthReduced);
                resetEpoch = GetCaptureFreshnessMetricsSnapshot(currentCaptureSource).CurrentStreamEpoch;
            }

            lock (gate)
            {
                if (resetEpoch > 0)
                {
                    recoveryLockStreamEpoch = resetEpoch;
                    ResetHelperCurrentEpochState_NoLock(resetEpoch);
                }
            }

            if (profileChanged && resetEpoch > 0)
            {
                StartTransportProfileTransition(
                    currentSessionId,
                    previousTransportTuningLevel,
                    ScreenShareTransportTuningLevel.BandwidthReduced,
                    resetEpoch);
            }

            if (degradedStateChanged)
            {
                SenderDegradedModeChanged?.Invoke(
                    this,
                    new ScreenShareSenderDegradedModeChangedEventArgs(IsSenderFreshnessDegraded(ScreenShareSenderFreshnessMode.Reduced)));
            }

            if (droppedQueuedFrames > 0)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_sender_frame_dropped_backlog; session_id={sessionIdForLog}; dropped_count={droppedQueuedFrames}; reason=recovery_timeout_reset");
            }

            if (droppedPendingRawFrames > 0)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_sender_raw_backlog_purged; session_id={sessionIdForLog}; dropped_count={droppedPendingRawFrames}; reason=recovery_timeout_reset");
            }

            RequestKeyFrame("recovery_timeout_reset");
            lock (gate)
            {
                recoveryTimeoutResetCount++;
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_recovery_timeout_reset_completed; session_id={sessionIdForLog}; stream_epoch={Math.Max(0, resetEpoch > 0 ? resetEpoch : recoveryLockStreamEpoch)}; reason={reason}; lock_duration_ms={GetRecoveryLockDurationMs(clock.UtcNow)}; current_epoch_need_more_input_count=unavailable; last_clean_frame_id=unavailable; triggered_profile_change={(profileChanged ? 1 : 0)}");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "ScreenShareTransport",
                $"event=screenshare_recovery_timeout_reset_failed; session_id={sessionIdForLog}; stream_epoch={Math.Max(0, recoveryLockStreamEpoch)}; reason={reason}; lock_duration_ms={GetRecoveryLockDurationMs(clock.UtcNow)}; current_epoch_need_more_input_count=unavailable; last_clean_frame_id=unavailable; triggered_profile_change=0; failure={ex.GetType().Name}");
            LogDebug($"Recovery timeout reset failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private long postRecoveryAgeGraceSuppressedCount
    {
        get => recoveryTracker.PostRecoveryAgeGraceSuppressedCount;
        set => SetRecoveryBurstState(state => state.PostRecoveryAgeGraceSuppressedCount = value);
    }
}
