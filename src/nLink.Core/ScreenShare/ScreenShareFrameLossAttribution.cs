using System.Collections.ObjectModel;
using NLink.Core.Logging;

namespace NLink.Core.ScreenShare;

internal enum ScreenShareFrameLossBucket
{
    None = 0,
    ReassemblerStaleSuperseded,
    AssemblyEvicted,
    ReadyFrameSkippedReplaced,
    ViewerRejectedBeforeEnqueue,
    DecodeWorkerDroppedBeforeDecode,
    DecodedFrameReplacedBeforeApply,
    DroppedWaitingForRecoveryKeyframe,
    DecodeFailed,
    StaleDroppedAfterDecode,
}

internal enum ScreenShareReassemblerRootCauseBucket
{
    None = 0,
    FragmentGapBeforeAssembly,
    LateFragmentAfterHeadAdvanced,
    FutureTailPrunedWhileGapActive,
    ProtectedHeadMissingBudgetPressure,
    RecoveryKeyframeSupersededOrReplaced,
    OrderedEmitBlockedThenResynced,
}

internal readonly record struct ScreenShareFrameLossBreadcrumb(
    long StreamEpoch,
    long FrameId,
    string Stage,
    string Reason,
    long RelatedFrameId);

internal readonly record struct ScreenShareEpochContinuityEventSnapshot(
    string EventName,
    long FrameId,
    long RelatedFrameId,
    long OccurredUtcMs);

internal readonly record struct ScreenShareReassemblerLossBurstSnapshot(
    string RootCause,
    long ExpectedNextFrameId,
    long ReceivedFrameIdStart,
    long ReceivedFrameIdEnd,
    int FutureNonKeyBufferedCount,
    long BufferedRecoveryKeyframeFrameId,
    long LossCount);

internal sealed record ScreenShareEpochDiagnosticsSnapshot(
    long StreamEpoch,
    long LastAppliedFrameId,
    long VisibleHeadFrameId,
    long AppliedHeadFrameId,
    long OrderedEmitHeadFrameId,
    long WinningRecoveryFrameId,
    long GapCount,
    long RecoveryKeyframeApplyCount,
    long ResyncCount,
    long FramesAppliedSinceLastGap,
    long TimeToFirstApplyMs,
    long TimeFromGapToKeyframeRequestMs,
    long TimeFromGapToRecoveryKeyframeAppliedMs,
    long TimeInRecoveryLockMs,
    long RecoveryCandidatePresentCount,
    long VisibleRecoveryFloorFrameId,
    long StableVisibleHeadFrameId,
    long FragmentGapBeforeAssemblyCount,
    long LateFragmentAfterHeadAdvancedCount,
    long LateFragmentAfterAppliedHeadCount,
    long LateFragmentAfterOrderedHeadCount,
    long SupersededRecoveryTailCleanupCount,
    long RecoveryOwnerReplacedCount,
    long OlderEpochCleanupAfterEpochAdvanceCount,
    long LateFragmentAfterStableVisibleHeadCount,
    long LateFragmentAfterVisibleRecoveryCount,
    long LateFragmentAfterSuccessfulRecoveryCount,
    long SuppressedEmitDuringRecoveryWaitCount,
    long FutureTailPrunedWhileGapActiveCount,
    long ProtectedHeadMissingBudgetPressureCount,
    long RecoveryKeyframeSupersededOrReplacedCount,
    long OrderedEmitBlockedThenResyncedCount,
    string DominantReassemblerRootCause,
    IReadOnlyList<ScreenShareEpochContinuityEventSnapshot> TimelineEvents,
    IReadOnlyList<ScreenShareReassemblerLossBurstSnapshot> TopLossBursts);

internal sealed record ScreenShareFrameLossEpochSnapshot(
    long StreamEpoch,
    long FragmentSeenFrames,
    long FramesAssembled,
    long FramesReady,
    long FramesEmitted,
    long ViewerAcceptedFrames,
    long DecodeEnqueuedFrames,
    long FramesDecoded,
    long FramesApplied,
    long ReassemblerStaleSupersededLossCount,
    long AssemblyEvictedLossCount,
    long ReadyFrameSkippedReplacedLossCount,
    long ViewerRejectedBeforeEnqueueCount,
    long WaitingForRecoveryKeyframeRejectCount,
    long RecoveryRunwayOverflowRejectCount,
    long SuppressedEmitDuringRecoveryWaitCount,
    long BlockedByReservedRecoveryFrameRejectCount,
    long OlderEpochIgnoredDuringRecoveryLockCount,
    long NewerEpochNonKeyIgnoredDuringLockCount,
    long DeferredPostRecoveryCandidateReplaceCount,
    long DecodeWorkerDroppedBeforeDecodeCount,
    long DecodeQueueOverflowCount,
    long DecodeAgeBudgetCount,
    long DecodeGenerationChangedCount,
    long DecodeStoppedCount,
    long DecodedApplyQueueOverflowCount,
    long DecodedFrameReplacedBeforeApplyCount,
    long DecodedStaleAfterRecoveryCount,
    long DecodedBlockedByReservedRecoveryFrameCount,
    long DecodedNewerEpochIgnoredDuringLockCount,
    long DroppedWaitingForRecoveryKeyframeCount,
    long DecodeFailedLossCount,
    long StaleDroppedAfterDecodeCount,
    long GapNonKeyPrunedCount,
    long FutureTailQuarantinedDuringGapCount,
    long FutureTailQuarantinedAfterGapCount,
    long PreCandidateGapTailRejectedCount,
    long RecoveryCandidatePresentCount,
    long VisibleRecoveryFloorFrameId,
    long StableVisibleHeadFrameId,
    long AppliedHeadFrameId,
    long OrderedEmitHeadFrameId,
    long WinningRecoveryFrameId,
    long SupersededRecoveryTailCleanupCount,
    long LateSameEpochAfterHeadAdvancedDropCount,
    long StaleRunwayWindowAbortCount,
    long RunwayCandidateExpiredAfterHeadAdvanceCount,
    long RunwayFollowersEmittedWithinActionableWindowCount,
    long LateFragmentAfterAppliedHeadCount,
    long LateFragmentAfterOrderedHeadCount,
    long LateFragmentAfterStableVisibleHeadCount,
    long LateFragmentAfterVisibleRecoveryCount,
    long UnattributedLossCount,
    long RecoveryOwnerReplacedCount,
    long OlderEpochCleanupAfterEpochAdvanceCount,
    string DominantHelperAdmissionRejectReason,
    long LastAppliedFrameId,
    long LastCleanFrameId,
    IReadOnlyList<ScreenShareFrameLossBreadcrumb> RecentLosses);

internal sealed record ScreenShareFrameLossSessionSnapshot(
    string SessionId,
    long FragmentSeenFrames,
    long FramesAssembled,
    long FramesReady,
    long FramesEmitted,
    long ViewerAcceptedFrames,
    long DecodeEnqueuedFrames,
    long FramesDecoded,
    long FramesApplied,
    long ReassemblerStaleSupersededLossCount,
    long AssemblyEvictedLossCount,
    long ReadyFrameSkippedReplacedLossCount,
    long ViewerRejectedBeforeEnqueueCount,
    long WaitingForRecoveryKeyframeRejectCount,
    long RecoveryRunwayOverflowRejectCount,
    long SuppressedEmitDuringRecoveryWaitCount,
    long BlockedByReservedRecoveryFrameRejectCount,
    long OlderEpochIgnoredDuringRecoveryLockCount,
    long NewerEpochNonKeyIgnoredDuringLockCount,
    long DeferredPostRecoveryCandidateReplaceCount,
    long DecodeWorkerDroppedBeforeDecodeCount,
    long DecodeQueueOverflowCount,
    long DecodeAgeBudgetCount,
    long DecodeGenerationChangedCount,
    long DecodeStoppedCount,
    long DecodedApplyQueueOverflowCount,
    long DecodedFrameReplacedBeforeApplyCount,
    long DecodedStaleAfterRecoveryCount,
    long DecodedBlockedByReservedRecoveryFrameCount,
    long DecodedNewerEpochIgnoredDuringLockCount,
    long DroppedWaitingForRecoveryKeyframeCount,
    long DecodeFailedLossCount,
    long StaleDroppedAfterDecodeCount,
    long ReassemblerLossCount,
    long EnqueueRejectCount,
    long DecodeWorkerDropCount,
    long PostDecodeDropCount,
    long GapNonKeyPrunedCount,
    long FutureTailQuarantinedDuringGapCount,
    long FutureTailQuarantinedAfterGapCount,
    long PreCandidateGapTailRejectedCount,
    long RecoveryKeyframeResyncCount,
    long RecoveryCandidatePresentCount,
    long VisibleRecoveryFloorFrameId,
    long StableVisibleHeadFrameId,
    long AppliedHeadFrameId,
    long OrderedEmitHeadFrameId,
    long WinningRecoveryFrameId,
    long FragmentGapBeforeAssemblyCount,
    long LateFragmentAfterHeadAdvancedCount,
    long LateFragmentAfterAppliedHeadCount,
    long LateFragmentAfterOrderedHeadCount,
    long SupersededRecoveryTailCleanupCount,
    long LateSameEpochAfterHeadAdvancedDropCount,
    long StaleRunwayWindowAbortCount,
    long RunwayCandidateExpiredAfterHeadAdvanceCount,
    long RunwayFollowersEmittedWithinActionableWindowCount,
    long RecoveryOwnerReplacedCount,
    long OlderEpochCleanupAfterEpochAdvanceCount,
    long LateFragmentAfterStableVisibleHeadCount,
    long LateFragmentAfterVisibleRecoveryCount,
    long LateFragmentAfterSuccessfulRecoveryCount,
    long FutureTailPrunedWhileGapActiveCount,
    long ProtectedHeadMissingBudgetPressureCount,
    long RecoveryKeyframeSupersededOrReplacedCount,
    long OrderedEmitBlockedThenResyncedCount,
    string DominantReassemblerRootCause,
    string DominantHelperAdmissionRejectReason,
    long UnattributedLossCount,
    bool GapActive,
    long GapExpectedFrameId,
    long BufferedRecoveryKeyframeFrameId,
    int FutureNonKeyBufferedCount,
    long LastAppliedFrameId,
    long LastCleanFrameId,
    IReadOnlyList<ScreenShareFrameLossBreadcrumb> RecentLosses,
    IReadOnlyList<ScreenShareFrameLossEpochSnapshot> EpochSnapshots,
    IReadOnlyList<ScreenShareEpochDiagnosticsSnapshot> EpochDiagnostics)
{
    public static ScreenShareFrameLossSessionSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        FragmentSeenFrames: 0,
        FramesAssembled: 0,
        FramesReady: 0,
        FramesEmitted: 0,
        ViewerAcceptedFrames: 0,
        DecodeEnqueuedFrames: 0,
        FramesDecoded: 0,
        FramesApplied: 0,
        ReassemblerStaleSupersededLossCount: 0,
        AssemblyEvictedLossCount: 0,
        ReadyFrameSkippedReplacedLossCount: 0,
        ViewerRejectedBeforeEnqueueCount: 0,
        WaitingForRecoveryKeyframeRejectCount: 0,
        RecoveryRunwayOverflowRejectCount: 0,
        SuppressedEmitDuringRecoveryWaitCount: 0,
        BlockedByReservedRecoveryFrameRejectCount: 0,
        OlderEpochIgnoredDuringRecoveryLockCount: 0,
        NewerEpochNonKeyIgnoredDuringLockCount: 0,
        DeferredPostRecoveryCandidateReplaceCount: 0,
        DecodeWorkerDroppedBeforeDecodeCount: 0,
        DecodeQueueOverflowCount: 0,
        DecodeAgeBudgetCount: 0,
        DecodeGenerationChangedCount: 0,
        DecodeStoppedCount: 0,
        DecodedApplyQueueOverflowCount: 0,
        DecodedFrameReplacedBeforeApplyCount: 0,
        DecodedStaleAfterRecoveryCount: 0,
        DecodedBlockedByReservedRecoveryFrameCount: 0,
        DecodedNewerEpochIgnoredDuringLockCount: 0,
        DroppedWaitingForRecoveryKeyframeCount: 0,
        DecodeFailedLossCount: 0,
        StaleDroppedAfterDecodeCount: 0,
        ReassemblerLossCount: 0,
        EnqueueRejectCount: 0,
        DecodeWorkerDropCount: 0,
        PostDecodeDropCount: 0,
        GapNonKeyPrunedCount: 0,
        FutureTailQuarantinedDuringGapCount: 0,
        FutureTailQuarantinedAfterGapCount: 0,
        PreCandidateGapTailRejectedCount: 0,
        RecoveryKeyframeResyncCount: 0,
        RecoveryCandidatePresentCount: 0,
        VisibleRecoveryFloorFrameId: -1,
        StableVisibleHeadFrameId: -1,
        AppliedHeadFrameId: -1,
        OrderedEmitHeadFrameId: -1,
        WinningRecoveryFrameId: -1,
        FragmentGapBeforeAssemblyCount: 0,
        LateFragmentAfterHeadAdvancedCount: 0,
        LateFragmentAfterAppliedHeadCount: 0,
        LateFragmentAfterOrderedHeadCount: 0,
        SupersededRecoveryTailCleanupCount: 0,
        LateSameEpochAfterHeadAdvancedDropCount: 0,
        StaleRunwayWindowAbortCount: 0,
        RunwayCandidateExpiredAfterHeadAdvanceCount: 0,
        RunwayFollowersEmittedWithinActionableWindowCount: 0,
        RecoveryOwnerReplacedCount: 0,
        OlderEpochCleanupAfterEpochAdvanceCount: 0,
        LateFragmentAfterStableVisibleHeadCount: 0,
        LateFragmentAfterVisibleRecoveryCount: 0,
        LateFragmentAfterSuccessfulRecoveryCount: 0,
        FutureTailPrunedWhileGapActiveCount: 0,
        ProtectedHeadMissingBudgetPressureCount: 0,
        RecoveryKeyframeSupersededOrReplacedCount: 0,
        OrderedEmitBlockedThenResyncedCount: 0,
        DominantReassemblerRootCause: "none",
        DominantHelperAdmissionRejectReason: "none",
        UnattributedLossCount: 0,
        GapActive: false,
        GapExpectedFrameId: -1,
        BufferedRecoveryKeyframeFrameId: -1,
        FutureNonKeyBufferedCount: 0,
        LastAppliedFrameId: -1,
        LastCleanFrameId: -1,
        RecentLosses: Array.Empty<ScreenShareFrameLossBreadcrumb>(),
        EpochSnapshots: Array.Empty<ScreenShareFrameLossEpochSnapshot>(),
        EpochDiagnostics: Array.Empty<ScreenShareEpochDiagnosticsSnapshot>());
}

internal sealed record ScreenShareHelperUpstreamLatencyEpochSnapshot(
    long StreamEpoch,
    long CaptureToFrameReadyAvgMs,
    long CaptureToFrameReadyMedianMs,
    long CaptureToFrameReadyP95Ms,
    long CaptureToFrameReadyMaxMs,
    long FrameReadyToViewerAcceptAvgMs,
    long FrameReadyToViewerAcceptMedianMs,
    long FrameReadyToViewerAcceptP95Ms,
    long FrameReadyToViewerAcceptMaxMs,
    long ViewerAcceptToDecodeEnqueueAvgMs,
    long ViewerAcceptToDecodeEnqueueMedianMs,
    long ViewerAcceptToDecodeEnqueueP95Ms,
    long ViewerAcceptToDecodeEnqueueMaxMs,
    long DecodeEnqueueToDecodeStartAvgMs,
    long DecodeEnqueueToDecodeStartMedianMs,
    long DecodeEnqueueToDecodeStartP95Ms,
    long DecodeEnqueueToDecodeStartMaxMs,
    long CaptureToDecodeStartAvgMs,
    long CaptureToDecodeStartMedianMs,
    long CaptureToDecodeStartP95Ms,
    long CaptureToDecodeStartMaxMs);

internal sealed record ScreenShareHelperUpstreamLatencySessionSnapshot(
    string SessionId,
    long CaptureToFrameReadyAvgMs,
    long CaptureToFrameReadyMedianMs,
    long CaptureToFrameReadyP95Ms,
    long CaptureToFrameReadyMaxMs,
    long FrameReadyToViewerAcceptAvgMs,
    long FrameReadyToViewerAcceptMedianMs,
    long FrameReadyToViewerAcceptP95Ms,
    long FrameReadyToViewerAcceptMaxMs,
    long ViewerAcceptToDecodeEnqueueAvgMs,
    long ViewerAcceptToDecodeEnqueueMedianMs,
    long ViewerAcceptToDecodeEnqueueP95Ms,
    long ViewerAcceptToDecodeEnqueueMaxMs,
    long DecodeEnqueueToDecodeStartAvgMs,
    long DecodeEnqueueToDecodeStartMedianMs,
    long DecodeEnqueueToDecodeStartP95Ms,
    long DecodeEnqueueToDecodeStartMaxMs,
    long CaptureToDecodeStartAvgMs,
    long CaptureToDecodeStartMedianMs,
    long CaptureToDecodeStartP95Ms,
    long CaptureToDecodeStartMaxMs,
    long WorstEpochByCaptureToDecodeStart,
    long WorstEpochCaptureToDecodeStartAvgMs,
    string DominantUpstreamLatencyStage,
    IReadOnlyList<ScreenShareHelperUpstreamLatencyEpochSnapshot> EpochSnapshots)
{
    public static ScreenShareHelperUpstreamLatencySessionSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        CaptureToFrameReadyAvgMs: 0,
        CaptureToFrameReadyMedianMs: 0,
        CaptureToFrameReadyP95Ms: 0,
        CaptureToFrameReadyMaxMs: 0,
        FrameReadyToViewerAcceptAvgMs: 0,
        FrameReadyToViewerAcceptMedianMs: 0,
        FrameReadyToViewerAcceptP95Ms: 0,
        FrameReadyToViewerAcceptMaxMs: 0,
        ViewerAcceptToDecodeEnqueueAvgMs: 0,
        ViewerAcceptToDecodeEnqueueMedianMs: 0,
        ViewerAcceptToDecodeEnqueueP95Ms: 0,
        ViewerAcceptToDecodeEnqueueMaxMs: 0,
        DecodeEnqueueToDecodeStartAvgMs: 0,
        DecodeEnqueueToDecodeStartMedianMs: 0,
        DecodeEnqueueToDecodeStartP95Ms: 0,
        DecodeEnqueueToDecodeStartMaxMs: 0,
        CaptureToDecodeStartAvgMs: 0,
        CaptureToDecodeStartMedianMs: 0,
        CaptureToDecodeStartP95Ms: 0,
        CaptureToDecodeStartMaxMs: 0,
        WorstEpochByCaptureToDecodeStart: -1,
        WorstEpochCaptureToDecodeStartAvgMs: 0,
        DominantUpstreamLatencyStage: "none",
        EpochSnapshots: Array.Empty<ScreenShareHelperUpstreamLatencyEpochSnapshot>());
}

internal sealed record ScreenShareHelperReadyPathEpochSnapshot(
    long StreamEpoch,
    long CaptureToFirstFragmentObservedAvgMs,
    long CaptureToFirstFragmentObservedMedianMs,
    long CaptureToFirstFragmentObservedP95Ms,
    long CaptureToFirstFragmentObservedMaxMs,
    long FirstFragmentToLastFragmentObservedAvgMs,
    long FirstFragmentToLastFragmentObservedMedianMs,
    long FirstFragmentToLastFragmentObservedP95Ms,
    long FirstFragmentToLastFragmentObservedMaxMs,
    long LastFragmentToAssemblyCompleteAvgMs,
    long LastFragmentToAssemblyCompleteMedianMs,
    long LastFragmentToAssemblyCompleteP95Ms,
    long LastFragmentToAssemblyCompleteMaxMs,
    long AssemblyCompleteToFrameEmittedAvgMs,
    long AssemblyCompleteToFrameEmittedMedianMs,
    long AssemblyCompleteToFrameEmittedP95Ms,
    long AssemblyCompleteToFrameEmittedMaxMs);

internal sealed record ScreenShareHelperReadyPathSessionSnapshot(
    string SessionId,
    long CaptureToFirstFragmentObservedAvgMs,
    long CaptureToFirstFragmentObservedMedianMs,
    long CaptureToFirstFragmentObservedP95Ms,
    long CaptureToFirstFragmentObservedMaxMs,
    long FirstFragmentToLastFragmentObservedAvgMs,
    long FirstFragmentToLastFragmentObservedMedianMs,
    long FirstFragmentToLastFragmentObservedP95Ms,
    long FirstFragmentToLastFragmentObservedMaxMs,
    long LastFragmentToAssemblyCompleteAvgMs,
    long LastFragmentToAssemblyCompleteMedianMs,
    long LastFragmentToAssemblyCompleteP95Ms,
    long LastFragmentToAssemblyCompleteMaxMs,
    long AssemblyCompleteToFrameEmittedAvgMs,
    long AssemblyCompleteToFrameEmittedMedianMs,
    long AssemblyCompleteToFrameEmittedP95Ms,
    long AssemblyCompleteToFrameEmittedMaxMs,
    string DominantReadyPathStage,
    IReadOnlyList<ScreenShareHelperReadyPathEpochSnapshot> EpochSnapshots)
{
    public static ScreenShareHelperReadyPathSessionSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        CaptureToFirstFragmentObservedAvgMs: 0,
        CaptureToFirstFragmentObservedMedianMs: 0,
        CaptureToFirstFragmentObservedP95Ms: 0,
        CaptureToFirstFragmentObservedMaxMs: 0,
        FirstFragmentToLastFragmentObservedAvgMs: 0,
        FirstFragmentToLastFragmentObservedMedianMs: 0,
        FirstFragmentToLastFragmentObservedP95Ms: 0,
        FirstFragmentToLastFragmentObservedMaxMs: 0,
        LastFragmentToAssemblyCompleteAvgMs: 0,
        LastFragmentToAssemblyCompleteMedianMs: 0,
        LastFragmentToAssemblyCompleteP95Ms: 0,
        LastFragmentToAssemblyCompleteMaxMs: 0,
        AssemblyCompleteToFrameEmittedAvgMs: 0,
        AssemblyCompleteToFrameEmittedMedianMs: 0,
        AssemblyCompleteToFrameEmittedP95Ms: 0,
        AssemblyCompleteToFrameEmittedMaxMs: 0,
        DominantReadyPathStage: "none",
        EpochSnapshots: Array.Empty<ScreenShareHelperReadyPathEpochSnapshot>());
}

internal sealed record ScreenShareHelperReceivePathEpochSnapshot(
    long StreamEpoch,
    long CaptureToEnvelopeSendAvgMs,
    long CaptureToEnvelopeSendMedianMs,
    long CaptureToEnvelopeSendP95Ms,
    long CaptureToEnvelopeSendMaxMs,
    long EnvelopeSendToBridgeIngressAvgMs,
    long EnvelopeSendToBridgeIngressMedianMs,
    long EnvelopeSendToBridgeIngressP95Ms,
    long EnvelopeSendToBridgeIngressMaxMs,
    long BridgeIngressToEnvelopeParsedAvgMs,
    long BridgeIngressToEnvelopeParsedMedianMs,
    long BridgeIngressToEnvelopeParsedP95Ms,
    long BridgeIngressToEnvelopeParsedMaxMs,
    long EnvelopeParsedToSecureDecryptAvgMs,
    long EnvelopeParsedToSecureDecryptMedianMs,
    long EnvelopeParsedToSecureDecryptP95Ms,
    long EnvelopeParsedToSecureDecryptMaxMs,
    long SecureDecryptToFragmentDeserializeAvgMs,
    long SecureDecryptToFragmentDeserializeMedianMs,
    long SecureDecryptToFragmentDeserializeP95Ms,
    long SecureDecryptToFragmentDeserializeMaxMs,
    long FragmentDeserializeToFirstFragmentObservedAvgMs,
    long FragmentDeserializeToFirstFragmentObservedMedianMs,
    long FragmentDeserializeToFirstFragmentObservedP95Ms,
    long FragmentDeserializeToFirstFragmentObservedMaxMs);

internal sealed record ScreenShareHelperReceivePathSessionSnapshot(
    string SessionId,
    long CaptureToEnvelopeSendAvgMs,
    long CaptureToEnvelopeSendMedianMs,
    long CaptureToEnvelopeSendP95Ms,
    long CaptureToEnvelopeSendMaxMs,
    long EnvelopeSendToBridgeIngressAvgMs,
    long EnvelopeSendToBridgeIngressMedianMs,
    long EnvelopeSendToBridgeIngressP95Ms,
    long EnvelopeSendToBridgeIngressMaxMs,
    long BridgeIngressToEnvelopeParsedAvgMs,
    long BridgeIngressToEnvelopeParsedMedianMs,
    long BridgeIngressToEnvelopeParsedP95Ms,
    long BridgeIngressToEnvelopeParsedMaxMs,
    long EnvelopeParsedToSecureDecryptAvgMs,
    long EnvelopeParsedToSecureDecryptMedianMs,
    long EnvelopeParsedToSecureDecryptP95Ms,
    long EnvelopeParsedToSecureDecryptMaxMs,
    long SecureDecryptToFragmentDeserializeAvgMs,
    long SecureDecryptToFragmentDeserializeMedianMs,
    long SecureDecryptToFragmentDeserializeP95Ms,
    long SecureDecryptToFragmentDeserializeMaxMs,
    long FragmentDeserializeToFirstFragmentObservedAvgMs,
    long FragmentDeserializeToFirstFragmentObservedMedianMs,
    long FragmentDeserializeToFirstFragmentObservedP95Ms,
    long FragmentDeserializeToFirstFragmentObservedMaxMs,
    string DominantReceivePathStage,
    IReadOnlyList<ScreenShareHelperReceivePathEpochSnapshot> EpochSnapshots)
{
    public static ScreenShareHelperReceivePathSessionSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        CaptureToEnvelopeSendAvgMs: 0,
        CaptureToEnvelopeSendMedianMs: 0,
        CaptureToEnvelopeSendP95Ms: 0,
        CaptureToEnvelopeSendMaxMs: 0,
        EnvelopeSendToBridgeIngressAvgMs: 0,
        EnvelopeSendToBridgeIngressMedianMs: 0,
        EnvelopeSendToBridgeIngressP95Ms: 0,
        EnvelopeSendToBridgeIngressMaxMs: 0,
        BridgeIngressToEnvelopeParsedAvgMs: 0,
        BridgeIngressToEnvelopeParsedMedianMs: 0,
        BridgeIngressToEnvelopeParsedP95Ms: 0,
        BridgeIngressToEnvelopeParsedMaxMs: 0,
        EnvelopeParsedToSecureDecryptAvgMs: 0,
        EnvelopeParsedToSecureDecryptMedianMs: 0,
        EnvelopeParsedToSecureDecryptP95Ms: 0,
        EnvelopeParsedToSecureDecryptMaxMs: 0,
        SecureDecryptToFragmentDeserializeAvgMs: 0,
        SecureDecryptToFragmentDeserializeMedianMs: 0,
        SecureDecryptToFragmentDeserializeP95Ms: 0,
        SecureDecryptToFragmentDeserializeMaxMs: 0,
        FragmentDeserializeToFirstFragmentObservedAvgMs: 0,
        FragmentDeserializeToFirstFragmentObservedMedianMs: 0,
        FragmentDeserializeToFirstFragmentObservedP95Ms: 0,
        FragmentDeserializeToFirstFragmentObservedMaxMs: 0,
        DominantReceivePathStage: "none",
        EpochSnapshots: Array.Empty<ScreenShareHelperReceivePathEpochSnapshot>());
}

internal sealed record ScreenShareHelperBridgeIngressEpochSnapshot(
    long StreamEpoch,
    long EnvelopeSendToBridgeMessageObservedAvgMs,
    long EnvelopeSendToBridgeMessageObservedMedianMs,
    long EnvelopeSendToBridgeMessageObservedP95Ms,
    long EnvelopeSendToBridgeMessageObservedMaxMs,
    long BridgeMessageObservedToBinaryFrameDecodedAvgMs,
    long BridgeMessageObservedToBinaryFrameDecodedMedianMs,
    long BridgeMessageObservedToBinaryFrameDecodedP95Ms,
    long BridgeMessageObservedToBinaryFrameDecodedMaxMs,
    long BinaryFrameDecodedToBridgeIngressAvgMs,
    long BinaryFrameDecodedToBridgeIngressMedianMs,
    long BinaryFrameDecodedToBridgeIngressP95Ms,
    long BinaryFrameDecodedToBridgeIngressMaxMs);

internal sealed record ScreenShareHelperBridgeIngressSessionSnapshot(
    string SessionId,
    long EnvelopeSendToBridgeMessageObservedAvgMs,
    long EnvelopeSendToBridgeMessageObservedMedianMs,
    long EnvelopeSendToBridgeMessageObservedP95Ms,
    long EnvelopeSendToBridgeMessageObservedMaxMs,
    long BridgeMessageObservedToBinaryFrameDecodedAvgMs,
    long BridgeMessageObservedToBinaryFrameDecodedMedianMs,
    long BridgeMessageObservedToBinaryFrameDecodedP95Ms,
    long BridgeMessageObservedToBinaryFrameDecodedMaxMs,
    long BinaryFrameDecodedToBridgeIngressAvgMs,
    long BinaryFrameDecodedToBridgeIngressMedianMs,
    long BinaryFrameDecodedToBridgeIngressP95Ms,
    long BinaryFrameDecodedToBridgeIngressMaxMs,
    string DominantBridgeIngressStage,
    IReadOnlyList<ScreenShareHelperBridgeIngressEpochSnapshot> EpochSnapshots)
{
    public static ScreenShareHelperBridgeIngressSessionSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        EnvelopeSendToBridgeMessageObservedAvgMs: 0,
        EnvelopeSendToBridgeMessageObservedMedianMs: 0,
        EnvelopeSendToBridgeMessageObservedP95Ms: 0,
        EnvelopeSendToBridgeMessageObservedMaxMs: 0,
        BridgeMessageObservedToBinaryFrameDecodedAvgMs: 0,
        BridgeMessageObservedToBinaryFrameDecodedMedianMs: 0,
        BridgeMessageObservedToBinaryFrameDecodedP95Ms: 0,
        BridgeMessageObservedToBinaryFrameDecodedMaxMs: 0,
        BinaryFrameDecodedToBridgeIngressAvgMs: 0,
        BinaryFrameDecodedToBridgeIngressMedianMs: 0,
        BinaryFrameDecodedToBridgeIngressP95Ms: 0,
        BinaryFrameDecodedToBridgeIngressMaxMs: 0,
        DominantBridgeIngressStage: "none",
        EpochSnapshots: Array.Empty<ScreenShareHelperBridgeIngressEpochSnapshot>());
}

internal sealed record ScreenShareHelperNknReceiveEpochSnapshot(
    long StreamEpoch,
    long EnvelopeSendToSdkHandleMsgEnteredAvgMs,
    long EnvelopeSendToSdkHandleMsgEnteredMedianMs,
    long EnvelopeSendToSdkHandleMsgEnteredP95Ms,
    long EnvelopeSendToSdkHandleMsgEnteredMaxMs,
    long SdkHandleMsgEnteredToClientMessageDispatchAvgMs,
    long SdkHandleMsgEnteredToClientMessageDispatchMedianMs,
    long SdkHandleMsgEnteredToClientMessageDispatchP95Ms,
    long SdkHandleMsgEnteredToClientMessageDispatchMaxMs,
    long ClientMessageDispatchToMultiClientMessageDispatchAvgMs,
    long ClientMessageDispatchToMultiClientMessageDispatchMedianMs,
    long ClientMessageDispatchToMultiClientMessageDispatchP95Ms,
    long ClientMessageDispatchToMultiClientMessageDispatchMaxMs,
    long MultiClientMessageDispatchToBridgeMessageObservedAvgMs,
    long MultiClientMessageDispatchToBridgeMessageObservedMedianMs,
    long MultiClientMessageDispatchToBridgeMessageObservedP95Ms,
    long MultiClientMessageDispatchToBridgeMessageObservedMaxMs);

internal sealed record ScreenShareHelperNknReceiveSessionSnapshot(
    string SessionId,
    long EnvelopeSendToSdkHandleMsgEnteredAvgMs,
    long EnvelopeSendToSdkHandleMsgEnteredMedianMs,
    long EnvelopeSendToSdkHandleMsgEnteredP95Ms,
    long EnvelopeSendToSdkHandleMsgEnteredMaxMs,
    long SdkHandleMsgEnteredToClientMessageDispatchAvgMs,
    long SdkHandleMsgEnteredToClientMessageDispatchMedianMs,
    long SdkHandleMsgEnteredToClientMessageDispatchP95Ms,
    long SdkHandleMsgEnteredToClientMessageDispatchMaxMs,
    long ClientMessageDispatchToMultiClientMessageDispatchAvgMs,
    long ClientMessageDispatchToMultiClientMessageDispatchMedianMs,
    long ClientMessageDispatchToMultiClientMessageDispatchP95Ms,
    long ClientMessageDispatchToMultiClientMessageDispatchMaxMs,
    long MultiClientMessageDispatchToBridgeMessageObservedAvgMs,
    long MultiClientMessageDispatchToBridgeMessageObservedMedianMs,
    long MultiClientMessageDispatchToBridgeMessageObservedP95Ms,
    long MultiClientMessageDispatchToBridgeMessageObservedMaxMs,
    string DominantNknReceiveStage,
    IReadOnlyList<ScreenShareHelperNknReceiveEpochSnapshot> EpochSnapshots)
{
    public static ScreenShareHelperNknReceiveSessionSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        EnvelopeSendToSdkHandleMsgEnteredAvgMs: 0,
        EnvelopeSendToSdkHandleMsgEnteredMedianMs: 0,
        EnvelopeSendToSdkHandleMsgEnteredP95Ms: 0,
        EnvelopeSendToSdkHandleMsgEnteredMaxMs: 0,
        SdkHandleMsgEnteredToClientMessageDispatchAvgMs: 0,
        SdkHandleMsgEnteredToClientMessageDispatchMedianMs: 0,
        SdkHandleMsgEnteredToClientMessageDispatchP95Ms: 0,
        SdkHandleMsgEnteredToClientMessageDispatchMaxMs: 0,
        ClientMessageDispatchToMultiClientMessageDispatchAvgMs: 0,
        ClientMessageDispatchToMultiClientMessageDispatchMedianMs: 0,
        ClientMessageDispatchToMultiClientMessageDispatchP95Ms: 0,
        ClientMessageDispatchToMultiClientMessageDispatchMaxMs: 0,
        MultiClientMessageDispatchToBridgeMessageObservedAvgMs: 0,
        MultiClientMessageDispatchToBridgeMessageObservedMedianMs: 0,
        MultiClientMessageDispatchToBridgeMessageObservedP95Ms: 0,
        MultiClientMessageDispatchToBridgeMessageObservedMaxMs: 0,
        DominantNknReceiveStage: "none",
        EpochSnapshots: Array.Empty<ScreenShareHelperNknReceiveEpochSnapshot>());
}

internal sealed record ScreenShareHelperWsReceiveEpochSnapshot(
    long StreamEpoch,
    long EnvelopeSendToWsReceiverWriteEnteredAvgMs,
    long EnvelopeSendToWsReceiverWriteEnteredMedianMs,
    long EnvelopeSendToWsReceiverWriteEnteredP95Ms,
    long EnvelopeSendToWsReceiverWriteEnteredMaxMs,
    long WsReceiverWriteEnteredToWsMessageEmittedAvgMs,
    long WsReceiverWriteEnteredToWsMessageEmittedMedianMs,
    long WsReceiverWriteEnteredToWsMessageEmittedP95Ms,
    long WsReceiverWriteEnteredToWsMessageEmittedMaxMs,
    long WsMessageEmittedToSdkHandleMsgEnteredAvgMs,
    long WsMessageEmittedToSdkHandleMsgEnteredMedianMs,
    long WsMessageEmittedToSdkHandleMsgEnteredP95Ms,
    long WsMessageEmittedToSdkHandleMsgEnteredMaxMs);

internal sealed record ScreenShareHelperWsReceiveSessionSnapshot(
    string SessionId,
    long EnvelopeSendToWsReceiverWriteEnteredAvgMs,
    long EnvelopeSendToWsReceiverWriteEnteredMedianMs,
    long EnvelopeSendToWsReceiverWriteEnteredP95Ms,
    long EnvelopeSendToWsReceiverWriteEnteredMaxMs,
    long WsReceiverWriteEnteredToWsMessageEmittedAvgMs,
    long WsReceiverWriteEnteredToWsMessageEmittedMedianMs,
    long WsReceiverWriteEnteredToWsMessageEmittedP95Ms,
    long WsReceiverWriteEnteredToWsMessageEmittedMaxMs,
    long WsMessageEmittedToSdkHandleMsgEnteredAvgMs,
    long WsMessageEmittedToSdkHandleMsgEnteredMedianMs,
    long WsMessageEmittedToSdkHandleMsgEnteredP95Ms,
    long WsMessageEmittedToSdkHandleMsgEnteredMaxMs,
    string DominantWsReceiveStage,
    IReadOnlyList<ScreenShareHelperWsReceiveEpochSnapshot> EpochSnapshots)
{
    public static ScreenShareHelperWsReceiveSessionSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        EnvelopeSendToWsReceiverWriteEnteredAvgMs: 0,
        EnvelopeSendToWsReceiverWriteEnteredMedianMs: 0,
        EnvelopeSendToWsReceiverWriteEnteredP95Ms: 0,
        EnvelopeSendToWsReceiverWriteEnteredMaxMs: 0,
        WsReceiverWriteEnteredToWsMessageEmittedAvgMs: 0,
        WsReceiverWriteEnteredToWsMessageEmittedMedianMs: 0,
        WsReceiverWriteEnteredToWsMessageEmittedP95Ms: 0,
        WsReceiverWriteEnteredToWsMessageEmittedMaxMs: 0,
        WsMessageEmittedToSdkHandleMsgEnteredAvgMs: 0,
        WsMessageEmittedToSdkHandleMsgEnteredMedianMs: 0,
        WsMessageEmittedToSdkHandleMsgEnteredP95Ms: 0,
        WsMessageEmittedToSdkHandleMsgEnteredMaxMs: 0,
        DominantWsReceiveStage: "none",
        EpochSnapshots: Array.Empty<ScreenShareHelperWsReceiveEpochSnapshot>());
}

internal sealed record ScreenShareHelperSocketReceiveEpochSnapshot(
    long StreamEpoch,
    long EnvelopeSendToSocketDataEventEmittedAvgMs,
    long EnvelopeSendToSocketDataEventEmittedMedianMs,
    long EnvelopeSendToSocketDataEventEmittedP95Ms,
    long EnvelopeSendToSocketDataEventEmittedMaxMs,
    long SocketDataEventEmittedToWsReceiverWriteEnteredAvgMs,
    long SocketDataEventEmittedToWsReceiverWriteEnteredMedianMs,
    long SocketDataEventEmittedToWsReceiverWriteEnteredP95Ms,
    long SocketDataEventEmittedToWsReceiverWriteEnteredMaxMs);

internal sealed record ScreenShareHelperSocketReceiveSessionSnapshot(
    string SessionId,
    long EnvelopeSendToSocketDataEventEmittedAvgMs,
    long EnvelopeSendToSocketDataEventEmittedMedianMs,
    long EnvelopeSendToSocketDataEventEmittedP95Ms,
    long EnvelopeSendToSocketDataEventEmittedMaxMs,
    long SocketDataEventEmittedToWsReceiverWriteEnteredAvgMs,
    long SocketDataEventEmittedToWsReceiverWriteEnteredMedianMs,
    long SocketDataEventEmittedToWsReceiverWriteEnteredP95Ms,
    long SocketDataEventEmittedToWsReceiverWriteEnteredMaxMs,
    string DominantSocketReceiveStage,
    IReadOnlyList<ScreenShareHelperSocketReceiveEpochSnapshot> EpochSnapshots)
{
    public static ScreenShareHelperSocketReceiveSessionSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        EnvelopeSendToSocketDataEventEmittedAvgMs: 0,
        EnvelopeSendToSocketDataEventEmittedMedianMs: 0,
        EnvelopeSendToSocketDataEventEmittedP95Ms: 0,
        EnvelopeSendToSocketDataEventEmittedMaxMs: 0,
        SocketDataEventEmittedToWsReceiverWriteEnteredAvgMs: 0,
        SocketDataEventEmittedToWsReceiverWriteEnteredMedianMs: 0,
        SocketDataEventEmittedToWsReceiverWriteEnteredP95Ms: 0,
        SocketDataEventEmittedToWsReceiverWriteEnteredMaxMs: 0,
        DominantSocketReceiveStage: "none",
        EpochSnapshots: Array.Empty<ScreenShareHelperSocketReceiveEpochSnapshot>());
}

internal static class ScreenShareFrameLossAttributionRegistry
{
    private const int MaxTrackedSessions = 8;
    private const int MaxTrackedFramesPerSession = 1024;
    private const int MaxRecentLossesPerSession = 96;
    private const long UnattributedInFlightAgeThresholdMs = 1500;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SessionState> Sessions = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> SessionOrder = new();

    internal static void ResetAllForTests()
    {
        lock (Gate)
        {
            Sessions.Clear();
            SessionOrder.Clear();
        }
    }

    internal static void ObserveFragmentSeen(string sessionId, long streamEpoch, long frameId, bool isKeyFrame)
        => ObserveStage(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.FragmentSeen);

    internal static void ObserveAcceptedFragment(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, long observedUtcMs = 0)
    {
        if (!TryNormalize(sessionId, streamEpoch, frameId, out var normalizedSessionId))
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var frame = GetOrCreateFrameState(session, streamEpoch, frameId, isKeyFrame);
            frame.IsKeyFrame |= isKeyFrame;
            var acceptedUtcMs = observedUtcMs > 0 ? observedUtcMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (frame.FirstFragmentObservedUtcMs <= 0)
            {
                frame.FirstFragmentObservedUtcMs = acceptedUtcMs;
            }

            frame.LastFragmentObservedUtcMs = acceptedUtcMs;
            frame.LastUpdatedUtcMs = acceptedUtcMs;
        }
    }

    internal static void ObserveInboundReceivePath(
        string sessionId,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        long capturedTsUtcMs = 0,
        long envelopeSendUtcMs = 0,
        long socketDataEventEmittedUtcMs = 0,
        long wsReceiverWriteEnteredUtcMs = 0,
        long wsMessageEmittedUtcMs = 0,
        long sdkHandleMsgEnteredUtcMs = 0,
        long clientMessageDispatchUtcMs = 0,
        long multiClientMessageDispatchUtcMs = 0,
        long bridgeMessageObservedUtcMs = 0,
        long binaryFrameDecodedUtcMs = 0,
        long bridgeIngressObservedUtcMs = 0,
        long envelopeParsedUtcMs = 0,
        long secureDecryptCompletedUtcMs = 0,
        long fragmentEnvelopeDeserializedUtcMs = 0)
    {
        if (!TryNormalize(sessionId, streamEpoch, frameId, out var normalizedSessionId))
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var frame = GetOrCreateFrameState(session, streamEpoch, frameId, isKeyFrame);
            frame.IsKeyFrame |= isKeyFrame;
            if (capturedTsUtcMs > 0 && frame.CapturedTsUtcMs <= 0)
            {
                frame.CapturedTsUtcMs = capturedTsUtcMs;
            }

            if (envelopeSendUtcMs > 0 && frame.EnvelopeSendUtcMs <= 0)
            {
                frame.EnvelopeSendUtcMs = envelopeSendUtcMs;
            }

            if (socketDataEventEmittedUtcMs > 0 && frame.SocketDataEventEmittedUtcMs <= 0)
            {
                frame.SocketDataEventEmittedUtcMs = socketDataEventEmittedUtcMs;
            }

            if (wsReceiverWriteEnteredUtcMs > 0 && frame.WsReceiverWriteEnteredUtcMs <= 0)
            {
                frame.WsReceiverWriteEnteredUtcMs = wsReceiverWriteEnteredUtcMs;
            }

            if (wsMessageEmittedUtcMs > 0 && frame.WsMessageEmittedUtcMs <= 0)
            {
                frame.WsMessageEmittedUtcMs = wsMessageEmittedUtcMs;
            }

            if (sdkHandleMsgEnteredUtcMs > 0 && frame.SdkHandleMsgEnteredUtcMs <= 0)
            {
                frame.SdkHandleMsgEnteredUtcMs = sdkHandleMsgEnteredUtcMs;
            }

            if (clientMessageDispatchUtcMs > 0 && frame.ClientMessageDispatchUtcMs <= 0)
            {
                frame.ClientMessageDispatchUtcMs = clientMessageDispatchUtcMs;
            }

            if (multiClientMessageDispatchUtcMs > 0 && frame.MultiClientMessageDispatchUtcMs <= 0)
            {
                frame.MultiClientMessageDispatchUtcMs = multiClientMessageDispatchUtcMs;
            }

            if (bridgeMessageObservedUtcMs > 0 && frame.BridgeMessageObservedUtcMs <= 0)
            {
                frame.BridgeMessageObservedUtcMs = bridgeMessageObservedUtcMs;
            }

            if (binaryFrameDecodedUtcMs > 0 && frame.BinaryFrameDecodedUtcMs <= 0)
            {
                frame.BinaryFrameDecodedUtcMs = binaryFrameDecodedUtcMs;
            }

            if (bridgeIngressObservedUtcMs > 0 && frame.BridgeIngressObservedUtcMs <= 0)
            {
                frame.BridgeIngressObservedUtcMs = bridgeIngressObservedUtcMs;
            }

            if (envelopeParsedUtcMs > 0 && frame.EnvelopeParsedUtcMs <= 0)
            {
                frame.EnvelopeParsedUtcMs = envelopeParsedUtcMs;
            }

            if (secureDecryptCompletedUtcMs > 0 && frame.SecureDecryptCompletedUtcMs <= 0)
            {
                frame.SecureDecryptCompletedUtcMs = secureDecryptCompletedUtcMs;
            }

            if (fragmentEnvelopeDeserializedUtcMs > 0 && frame.FragmentEnvelopeDeserializedUtcMs <= 0)
            {
                frame.FragmentEnvelopeDeserializedUtcMs = fragmentEnvelopeDeserializedUtcMs;
            }

            var lastObservedUtcMs = new[]
            {
                fragmentEnvelopeDeserializedUtcMs,
                secureDecryptCompletedUtcMs,
                envelopeParsedUtcMs,
                bridgeIngressObservedUtcMs,
                binaryFrameDecodedUtcMs,
                bridgeMessageObservedUtcMs,
                multiClientMessageDispatchUtcMs,
                clientMessageDispatchUtcMs,
                sdkHandleMsgEnteredUtcMs,
                wsMessageEmittedUtcMs,
                wsReceiverWriteEnteredUtcMs,
                envelopeSendUtcMs,
                capturedTsUtcMs,
            }.Max();

            if (lastObservedUtcMs > 0)
            {
                frame.LastUpdatedUtcMs = Math.Max(frame.LastUpdatedUtcMs, lastObservedUtcMs);
            }
        }
    }

    internal static void ObserveFrameAssembled(string sessionId, long streamEpoch, long frameId, bool isKeyFrame)
        => ObserveStage(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.Assembled);

    internal static void ObserveFrameReady(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, long capturedTsUtcMs = 0, long frameReadyObservedUtcMs = 0)
        => ObserveStage(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.Ready, frameReadyObservedUtcMs, capturedTsUtcMs);

    internal static void ObserveFrameEmitted(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, long emittedUtcMs = 0)
    {
        ObserveStage(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.Emitted, emittedUtcMs);

        if (!TryNormalize(sessionId, streamEpoch, frameId, out var normalizedSessionId))
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.OrderedEmitHeadFrameId = Math.Max(epochDiagnostics.OrderedEmitHeadFrameId, frameId);
        }
    }

    internal static void ObserveViewerAccepted(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, long viewerAcceptedUtcMs = 0)
        => ObserveStage(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.ViewerAccepted, viewerAcceptedUtcMs);

    internal static void ObserveRunwayFollowerEmittedWithinActionableWindow(string sessionId, long streamEpoch, long frameId)
    {
        if (!TryNormalize(sessionId, streamEpoch, frameId, out var normalizedSessionId))
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.RunwayFollowersEmittedWithinActionableWindowCount++;
        }
    }

    internal static void ObserveStaleRunwayWindowAbort(string sessionId, long streamEpoch, long recoveryOwnerFrameId, long blockedByFrameId)
    {
        if (!TryNormalize(sessionId, streamEpoch, Math.Max(0, recoveryOwnerFrameId), out var normalizedSessionId))
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.StaleRunwayWindowAbortCount++;
            NoteTimelineEvent(
                epochDiagnostics,
                "stale_runway_window_abort",
                recoveryOwnerFrameId,
                blockedByFrameId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    internal static void ObserveViewerRejectedBeforeEnqueue(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, string reason)
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.ViewerRejectedBeforeEnqueue, ScreenShareFrameLossBucket.ViewerRejectedBeforeEnqueue, reason);

    internal static void ObserveDecodeEnqueued(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, long decodeEnqueuedUtcMs = 0)
        => ObserveStage(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.DecodeEnqueued, decodeEnqueuedUtcMs);

    internal static void ObserveDecodeStarted(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, long decodeStartedUtcMs = 0)
        => ObserveStage(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.DecodeStarted, decodeStartedUtcMs);

    internal static void ObserveDecodeWorkerDroppedBeforeDecode(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, string reason)
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.DecodeWorkerDroppedBeforeDecode, ScreenShareFrameLossBucket.DecodeWorkerDroppedBeforeDecode, reason);

    internal static void ObserveDecodedFrameReplacedBeforeApply(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, string reason)
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.DecodedFrameReplacedBeforeApply, ScreenShareFrameLossBucket.DecodedFrameReplacedBeforeApply, reason);

    internal static void ObserveDecodeSucceeded(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, long decodeCompletedUtcMs = 0)
        => ObserveStage(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.Decoded, decodeCompletedUtcMs);

    internal static void ObserveDecodeFailed(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, string reason)
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.DecodeFailed, ScreenShareFrameLossBucket.DecodeFailed, reason);

    internal static void ObserveFrameApplied(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, long appliedUtcMs = 0)
    {
        if (!TryNormalize(sessionId, streamEpoch, frameId, out var normalizedSessionId))
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var frame = GetOrCreateFrameState(session, streamEpoch, frameId, isKeyFrame);
            frame.IsKeyFrame |= isKeyFrame;
            ApplyStage(frame, FrameLifecycleStage.Applied);
            frame.Applied = true;
            var observedUtcMs = appliedUtcMs > 0 ? appliedUtcMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            frame.AppliedUtcMs = observedUtcMs;
            frame.LastUpdatedUtcMs = observedUtcMs;
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.VisibleApplyCount++;
            epochDiagnostics.LastAppliedFrameId = frameId;
            epochDiagnostics.AppliedHeadFrameId = Math.Max(epochDiagnostics.AppliedHeadFrameId, frameId);
            if (epochDiagnostics.FirstCleanFrameAppliedUtcMs <= 0)
            {
                epochDiagnostics.FirstCleanFrameAppliedUtcMs = observedUtcMs;
                epochDiagnostics.FirstCleanFrameAppliedFrameId = frameId;
                NoteTimelineEvent(epochDiagnostics, "first_clean_frame_applied", frameId, -1, observedUtcMs);
            }

            if (epochDiagnostics.GapCount == 0 &&
                epochDiagnostics.VisibleApplyCount >= 4)
            {
                epochDiagnostics.StableVisibleHeadFrameId = Math.Max(
                    epochDiagnostics.StableVisibleHeadFrameId,
                    frameId);
            }
            else if (epochDiagnostics.StableVisibleHeadFrameId >= 0)
            {
                epochDiagnostics.StableVisibleHeadFrameId = Math.Max(
                    epochDiagnostics.StableVisibleHeadFrameId,
                    frameId);
            }
        }
    }

    internal static void ObserveDroppedWaitingForRecoveryKeyframe(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, string reason)
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.DroppedWaitingForRecoveryKeyframe, ScreenShareFrameLossBucket.DroppedWaitingForRecoveryKeyframe, reason);

    internal static void ObserveStaleDroppedAfterDecode(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, string reason)
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.StaleDroppedAfterDecode, ScreenShareFrameLossBucket.StaleDroppedAfterDecode, reason);

    internal static void ObserveReassemblerStaleSuperseded(
        string sessionId,
        long streamEpoch,
        long frameId,
        long supersededByFrameId,
        bool isKeyFrame = false,
        string reason = "stale_frame_superseded")
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.ReassemblerLoss, ScreenShareFrameLossBucket.ReassemblerStaleSuperseded, reason, supersededByFrameId);

    internal static void ObserveOlderEpochCleanupAfterEpochAdvance(
        string sessionId,
        long streamEpoch,
        long frameId,
        long sessionCurrentStreamEpoch,
        string source)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            streamEpoch <= 0 ||
            frameId < 0 ||
            sessionCurrentStreamEpoch <= streamEpoch)
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.OlderEpochCleanupAfterEpochAdvanceCount++;
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_reassembler_older_epoch_cleanup_after_epoch_advance; session_id={normalizedSessionId}; stream_epoch={streamEpoch}; session_current_stream_epoch={FormatFrameIdForLog(sessionCurrentStreamEpoch)}; frame_id={frameId}; source={NormalizeDiagnosticValue(source, "incoming_fragment")}");
        }
    }

    internal static void ObserveAssemblyEvicted(string sessionId, long streamEpoch, long frameId, string reason, bool isKeyFrame = false)
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.ReassemblerLoss, ScreenShareFrameLossBucket.AssemblyEvicted, reason);

    internal static void ObserveReadyFrameSkippedReplaced(
        string sessionId,
        long streamEpoch,
        long frameId,
        long replacedByFrameId,
        bool isKeyFrame = false,
        string reason = "ready_frame_skipped_replaced")
        => ObserveLoss(sessionId, streamEpoch, frameId, isKeyFrame, FrameLifecycleStage.ReassemblerLoss, ScreenShareFrameLossBucket.ReadyFrameSkippedReplaced, reason, replacedByFrameId);

    internal static void ObserveReassemblerGapState(
        string sessionId,
        long streamEpoch,
        bool gapActive,
        long gapExpectedFrameId,
        long bufferedRecoveryKeyframeFrameId,
        int futureNonKeyBufferedCount)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var previousBufferedRecoveryKeyframeFrameId = session.BufferedRecoveryKeyframeFrameId;
            session.GapStateStreamEpoch = streamEpoch > 0 ? streamEpoch : session.GapStateStreamEpoch;
            session.GapActive = gapActive;
            session.GapExpectedFrameId = gapExpectedFrameId >= 0 ? gapExpectedFrameId : -1;
            session.BufferedRecoveryKeyframeFrameId = bufferedRecoveryKeyframeFrameId >= 0 ? bufferedRecoveryKeyframeFrameId : -1;
            session.FutureNonKeyBufferedCount = Math.Max(0, futureNonKeyBufferedCount);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.FutureNonKeyBufferedCount = session.FutureNonKeyBufferedCount;
            epochDiagnostics.BufferedRecoveryKeyframeFrameId = session.BufferedRecoveryKeyframeFrameId;
            if (gapActive && gapExpectedFrameId >= 0)
            {
                epochDiagnostics.GapExpectedFrameId = gapExpectedFrameId;
            }

            if (gapActive &&
                bufferedRecoveryKeyframeFrameId >= 0 &&
                bufferedRecoveryKeyframeFrameId != previousBufferedRecoveryKeyframeFrameId)
            {
                epochDiagnostics.RecoveryCandidatePresentCount++;
                epochDiagnostics.FirstRecoveryKeyframeBufferedUtcMs = epochDiagnostics.FirstRecoveryKeyframeBufferedUtcMs <= 0
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    : epochDiagnostics.FirstRecoveryKeyframeBufferedUtcMs;
                NoteTimelineEvent(
                    epochDiagnostics,
                    "recovery_keyframe_buffered",
                    bufferedRecoveryKeyframeFrameId,
                    gapExpectedFrameId,
                    epochDiagnostics.FirstRecoveryKeyframeBufferedUtcMs);
            }
        }
    }

    internal static bool HasBufferedRecoveryKeyframeCandidate(string sessionId, long streamEpoch)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0)
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return false;
        }

        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session))
            {
                return false;
            }

            if (session.GapStateStreamEpoch == streamEpoch && session.BufferedRecoveryKeyframeFrameId >= 0)
            {
                return true;
            }

            return session.EpochDiagnostics.TryGetValue(streamEpoch, out var epochDiagnostics) &&
                   epochDiagnostics.BufferedRecoveryKeyframeFrameId >= 0;
        }
    }

    internal static long GetVisibleRecoveryFloorFrameId(string sessionId, long streamEpoch)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0)
        {
            return -1;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return -1;
        }

        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session) ||
                !session.EpochDiagnostics.TryGetValue(streamEpoch, out var epochDiagnostics))
            {
                return -1;
            }

            return epochDiagnostics.VisibleRecoveryFloorFrameId;
        }
    }

    internal static long GetStableVisibleHeadFrameId(string sessionId, long streamEpoch)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0)
        {
            return -1;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return -1;
        }

        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session) ||
                !session.EpochDiagnostics.TryGetValue(streamEpoch, out var epochDiagnostics))
            {
                return -1;
            }

            return epochDiagnostics.StableVisibleHeadFrameId;
        }
    }

    internal static long GetAppliedHeadFrameId(string sessionId, long streamEpoch)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0)
        {
            return -1;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return -1;
        }

        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session) ||
                !session.EpochDiagnostics.TryGetValue(streamEpoch, out var epochDiagnostics))
            {
                return -1;
            }

            return epochDiagnostics.AppliedHeadFrameId;
        }
    }

    internal static long GetOrderedEmitHeadFrameId(string sessionId, long streamEpoch)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0)
        {
            return -1;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return -1;
        }

        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session) ||
                !session.EpochDiagnostics.TryGetValue(streamEpoch, out var epochDiagnostics))
            {
                return -1;
            }

            return epochDiagnostics.OrderedEmitHeadFrameId;
        }
    }

    internal static void ObserveVisibleRecoveryFloor(string sessionId, long streamEpoch, long frameId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0 || frameId < 0)
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.VisibleRecoveryFloorFrameId = Math.Max(epochDiagnostics.VisibleRecoveryFloorFrameId, frameId);
        }
    }

    internal static void ObserveRecoveryOwner(
        string sessionId,
        long streamEpoch,
        long winningRecoveryFrameId,
        long orderedEmitHeadFrameId,
        bool replaced)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0 || winningRecoveryFrameId < 0)
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.WinningRecoveryFrameId = Math.Max(epochDiagnostics.WinningRecoveryFrameId, winningRecoveryFrameId);
            epochDiagnostics.OrderedEmitHeadFrameId = Math.Max(epochDiagnostics.OrderedEmitHeadFrameId, orderedEmitHeadFrameId);
            if (replaced)
            {
                epochDiagnostics.RecoveryOwnerReplacedCount++;
            }
        }
    }

    internal static void ObserveRecoveryKeyframeResync(string sessionId, long streamEpoch, long frameId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            session.GapStateStreamEpoch = streamEpoch > 0 ? streamEpoch : session.GapStateStreamEpoch;
            session.RecoveryKeyframeResyncCount++;
            EnqueueRecentLoss(
                session,
                new ScreenShareFrameLossBreadcrumb(
                    streamEpoch,
                    frameId,
                    "ReassemblerResync",
                    "recovery_keyframe_resync",
                    -1));
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.ResyncCount++;
            NoteTimelineEvent(epochDiagnostics, "resync_triggered", frameId, -1);
        }
    }

    internal static void ObserveEpochContinuityEvent(
        string sessionId,
        long streamEpoch,
        string eventName,
        long frameId = -1,
        long relatedFrameId = -1)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            streamEpoch <= 0 ||
            string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            NoteTimelineEvent(epochDiagnostics, eventName.Trim(), frameId, relatedFrameId);
        }
    }

    internal static void ObserveReassemblerRootCause(
        string sessionId,
        long streamEpoch,
        long frameId,
        ScreenShareReassemblerRootCauseBucket rootCause,
        long expectedNextFrameId,
        long receivedFrameId,
        int futureNonKeyBufferedCount,
        long bufferedRecoveryKeyframeFrameId,
        string reasonSource = "",
        long sessionCurrentStreamEpoch = -1,
        bool gapActive = false,
        long gapExpectedFrameId = -1,
        long currentWinningRecoveryFrameId = -1,
        long currentOrderedEmitHeadFrameId = -1)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            streamEpoch <= 0 ||
            frameId < 0 ||
            rootCause == ScreenShareReassemblerRootCauseBucket.None)
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            var orderedEmitHeadFrameId = epochDiagnostics.OrderedEmitHeadFrameId;
            if (rootCause == ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced &&
                orderedEmitHeadFrameId >= 0 &&
                frameId <= orderedEmitHeadFrameId)
            {
                epochDiagnostics.LateFragmentAfterOrderedHeadCount++;
                return;
            }

            var appliedHeadFrameId = epochDiagnostics.AppliedHeadFrameId;
            if (rootCause == ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced &&
                appliedHeadFrameId >= 0 &&
                frameId <= appliedHeadFrameId)
            {
                epochDiagnostics.LateFragmentAfterAppliedHeadCount++;
                return;
            }

            var stableVisibleHeadFrameId = epochDiagnostics.StableVisibleHeadFrameId;
            if (rootCause == ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced &&
                stableVisibleHeadFrameId >= 0 &&
                frameId <= stableVisibleHeadFrameId)
            {
                epochDiagnostics.LateFragmentAfterStableVisibleHeadCount++;
                return;
            }

            var visibleRecoveryFloorFrameId = epochDiagnostics.VisibleRecoveryFloorFrameId;
            if (rootCause == ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced &&
                visibleRecoveryFloorFrameId >= 0 &&
                frameId <= visibleRecoveryFloorFrameId)
            {
                epochDiagnostics.LateFragmentAfterVisibleRecoveryCount++;
                return;
            }

            if (rootCause == ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced &&
                epochDiagnostics.LastSuccessfulRecoveryWindowContiguousFrameId >= 0 &&
                frameId <= epochDiagnostics.LastSuccessfulRecoveryWindowContiguousFrameId)
            {
                epochDiagnostics.LateFragmentAfterSuccessfulRecoveryCount++;
                return;
            }

            if (rootCause == ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced)
            {
                LocalOperationalLog.Info(
                    "ScreenShare",
                    $"event=screenshare_reassembler_actionable_late_fragment; session_id={normalizedSessionId}; stream_epoch={streamEpoch}; session_current_stream_epoch={FormatFrameIdForLog(sessionCurrentStreamEpoch)}; frame_id={frameId}; reason_source={NormalizeDiagnosticValue(reasonSource, "late_fragment_after_head_advanced")}; expected_next_frame_id={FormatFrameIdForLog(expectedNextFrameId)}; received_frame_id={FormatFrameIdForLog(receivedFrameId)}; gap_active={(gapActive ? 1 : 0)}; gap_expected_frame_id={FormatFrameIdForLog(gapExpectedFrameId)}; current_winning_recovery_frame_id={FormatFrameIdForLog(currentWinningRecoveryFrameId)}; current_ordered_emit_head_frame_id={FormatFrameIdForLog(currentOrderedEmitHeadFrameId)}; ordered_emit_head_frame_id={FormatFrameIdForLog(epochDiagnostics.OrderedEmitHeadFrameId)}; applied_head_frame_id={FormatFrameIdForLog(epochDiagnostics.AppliedHeadFrameId)}; stable_visible_head_frame_id={FormatFrameIdForLog(epochDiagnostics.StableVisibleHeadFrameId)}; visible_recovery_floor_frame_id={FormatFrameIdForLog(epochDiagnostics.VisibleRecoveryFloorFrameId)}; buffered_recovery_keyframe_frame_id={FormatFrameIdForLog(bufferedRecoveryKeyframeFrameId)}; future_non_key_buffered_count={Math.Max(0, futureNonKeyBufferedCount)}");
            }

            IncrementRootCause(epochDiagnostics, rootCause);

            var burstKey = new LossBurstKey(rootCause, expectedNextFrameId);
            if (!epochDiagnostics.LossBursts.TryGetValue(burstKey, out var burst))
            {
                burst = new LossBurstState(rootCause, expectedNextFrameId);
                epochDiagnostics.LossBursts.Add(burstKey, burst);
            }

            burst.Observe(
                receivedFrameId >= 0 ? receivedFrameId : frameId,
                Math.Max(0, futureNonKeyBufferedCount),
                bufferedRecoveryKeyframeFrameId);
        }
    }

    internal static void ObserveRecoveryWindowSucceeded(
        string sessionId,
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0 || recoveryFrameId < 0)
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            epochDiagnostics.LastSuccessfulRecoveryWindowFrameId = recoveryFrameId;
            epochDiagnostics.LastSuccessfulRecoveryWindowContiguousFrameId = Math.Max(recoveryFrameId, lastContiguousFrameId);
            epochDiagnostics.StableVisibleHeadFrameId = Math.Max(
                epochDiagnostics.StableVisibleHeadFrameId,
                epochDiagnostics.LastSuccessfulRecoveryWindowContiguousFrameId);
        }
    }

    internal static ScreenShareFrameLossSessionSnapshot GetSnapshot(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ScreenShareFrameLossSessionSnapshot.Empty;
        }

        lock (Gate)
        {
            if (!Sessions.TryGetValue(sessionId.Trim(), out var session))
            {
                return ScreenShareFrameLossSessionSnapshot.Empty;
            }

            TouchSession(sessionId.Trim(), session);
            return BuildSnapshot(session);
        }
    }

    internal static string FormatRecentLosses(IReadOnlyList<ScreenShareFrameLossBreadcrumb>? breadcrumbs)
    {
        if (breadcrumbs is null || breadcrumbs.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            "|",
            breadcrumbs.Select(static breadcrumb =>
                $"{breadcrumb.StreamEpoch}:{breadcrumb.FrameId}@{breadcrumb.Stage}/{breadcrumb.Reason}{(breadcrumb.RelatedFrameId >= 0 ? $">{breadcrumb.RelatedFrameId}" : string.Empty)}"));
    }

    private static string FormatFrameIdForLog(long frameId)
        => frameId >= 0 ? frameId.ToString() : "(none)";

    private static string NormalizeDiagnosticValue(string value, string defaultValue)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();

    internal static long GetActionableLateFragmentCount(ScreenShareFrameLossSessionSnapshot snapshot)
        => Math.Max(0L, snapshot.LateFragmentAfterHeadAdvancedCount);

    internal static long GetActionableLateFragmentCount(ScreenShareEpochDiagnosticsSnapshot snapshot)
        => Math.Max(0L, snapshot.LateFragmentAfterHeadAdvancedCount);

    internal static ScreenShareHelperUpstreamLatencySessionSnapshot GetHelperUpstreamLatencySnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ScreenShareHelperUpstreamLatencySessionSnapshot.Empty;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session))
            {
                return ScreenShareHelperUpstreamLatencySessionSnapshot.Empty;
            }

            var captureToFrameReady = new List<long>();
            var frameReadyToViewerAccept = new List<long>();
            var viewerAcceptToDecodeEnqueue = new List<long>();
            var decodeEnqueueToDecodeStart = new List<long>();
            var captureToDecodeStart = new List<long>();
            var epochSnapshots = new List<ScreenShareHelperUpstreamLatencyEpochSnapshot>();
            long worstEpoch = -1;
            long worstEpochCaptureToDecodeStartAvgMs = 0;

            foreach (var epochGroup in session.Frames.Values.GroupBy(static frame => frame.StreamEpoch).OrderBy(static group => group.Key))
            {
                var epochCaptureToFrameReady = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.CapturedTsUtcMs, frame.FrameReadyObservedUtcMs)));
                var epochFrameReadyToViewerAccept = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.FrameReadyObservedUtcMs, frame.ViewerAcceptedUtcMs)));
                var epochViewerAcceptToDecodeEnqueue = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.ViewerAcceptedUtcMs, frame.DecodeEnqueuedUtcMs)));
                var epochDecodeEnqueueToDecodeStart = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.DecodeEnqueuedUtcMs, frame.DecodeStartedUtcMs)));
                var epochCaptureToDecodeStart = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.CapturedTsUtcMs, frame.DecodeStartedUtcMs)));

                captureToFrameReady.AddRange(epochCaptureToFrameReady);
                frameReadyToViewerAccept.AddRange(epochFrameReadyToViewerAccept);
                viewerAcceptToDecodeEnqueue.AddRange(epochViewerAcceptToDecodeEnqueue);
                decodeEnqueueToDecodeStart.AddRange(epochDecodeEnqueueToDecodeStart);
                captureToDecodeStart.AddRange(epochCaptureToDecodeStart);

                var epochCaptureToFrameReadySummary = ComputeLatencySummary(epochCaptureToFrameReady);
                var epochFrameReadyToViewerAcceptSummary = ComputeLatencySummary(epochFrameReadyToViewerAccept);
                var epochViewerAcceptToDecodeEnqueueSummary = ComputeLatencySummary(epochViewerAcceptToDecodeEnqueue);
                var epochDecodeEnqueueToDecodeStartSummary = ComputeLatencySummary(epochDecodeEnqueueToDecodeStart);
                var epochCaptureToDecodeStartSummary = ComputeLatencySummary(epochCaptureToDecodeStart);
                epochSnapshots.Add(
                    new ScreenShareHelperUpstreamLatencyEpochSnapshot(
                        epochGroup.Key,
                        epochCaptureToFrameReadySummary.AvgMs,
                        epochCaptureToFrameReadySummary.MedianMs,
                        epochCaptureToFrameReadySummary.P95Ms,
                        epochCaptureToFrameReadySummary.MaxMs,
                        epochFrameReadyToViewerAcceptSummary.AvgMs,
                        epochFrameReadyToViewerAcceptSummary.MedianMs,
                        epochFrameReadyToViewerAcceptSummary.P95Ms,
                        epochFrameReadyToViewerAcceptSummary.MaxMs,
                        epochViewerAcceptToDecodeEnqueueSummary.AvgMs,
                        epochViewerAcceptToDecodeEnqueueSummary.MedianMs,
                        epochViewerAcceptToDecodeEnqueueSummary.P95Ms,
                        epochViewerAcceptToDecodeEnqueueSummary.MaxMs,
                        epochDecodeEnqueueToDecodeStartSummary.AvgMs,
                        epochDecodeEnqueueToDecodeStartSummary.MedianMs,
                        epochDecodeEnqueueToDecodeStartSummary.P95Ms,
                        epochDecodeEnqueueToDecodeStartSummary.MaxMs,
                        epochCaptureToDecodeStartSummary.AvgMs,
                        epochCaptureToDecodeStartSummary.MedianMs,
                        epochCaptureToDecodeStartSummary.P95Ms,
                        epochCaptureToDecodeStartSummary.MaxMs));
                if (epochCaptureToDecodeStartSummary.AvgMs > worstEpochCaptureToDecodeStartAvgMs)
                {
                    worstEpoch = epochGroup.Key;
                    worstEpochCaptureToDecodeStartAvgMs = epochCaptureToDecodeStartSummary.AvgMs;
                }
            }

            var captureToFrameReadySummary = ComputeLatencySummary(captureToFrameReady);
            var frameReadyToViewerAcceptSummary = ComputeLatencySummary(frameReadyToViewerAccept);
            var viewerAcceptToDecodeEnqueueSummary = ComputeLatencySummary(viewerAcceptToDecodeEnqueue);
            var decodeEnqueueToDecodeStartSummary = ComputeLatencySummary(decodeEnqueueToDecodeStart);
            var captureToDecodeStartSummary = ComputeLatencySummary(captureToDecodeStart);

            return new ScreenShareHelperUpstreamLatencySessionSnapshot(
                SessionId: normalizedSessionId,
                CaptureToFrameReadyAvgMs: captureToFrameReadySummary.AvgMs,
                CaptureToFrameReadyMedianMs: captureToFrameReadySummary.MedianMs,
                CaptureToFrameReadyP95Ms: captureToFrameReadySummary.P95Ms,
                CaptureToFrameReadyMaxMs: captureToFrameReadySummary.MaxMs,
                FrameReadyToViewerAcceptAvgMs: frameReadyToViewerAcceptSummary.AvgMs,
                FrameReadyToViewerAcceptMedianMs: frameReadyToViewerAcceptSummary.MedianMs,
                FrameReadyToViewerAcceptP95Ms: frameReadyToViewerAcceptSummary.P95Ms,
                FrameReadyToViewerAcceptMaxMs: frameReadyToViewerAcceptSummary.MaxMs,
                ViewerAcceptToDecodeEnqueueAvgMs: viewerAcceptToDecodeEnqueueSummary.AvgMs,
                ViewerAcceptToDecodeEnqueueMedianMs: viewerAcceptToDecodeEnqueueSummary.MedianMs,
                ViewerAcceptToDecodeEnqueueP95Ms: viewerAcceptToDecodeEnqueueSummary.P95Ms,
                ViewerAcceptToDecodeEnqueueMaxMs: viewerAcceptToDecodeEnqueueSummary.MaxMs,
                DecodeEnqueueToDecodeStartAvgMs: decodeEnqueueToDecodeStartSummary.AvgMs,
                DecodeEnqueueToDecodeStartMedianMs: decodeEnqueueToDecodeStartSummary.MedianMs,
                DecodeEnqueueToDecodeStartP95Ms: decodeEnqueueToDecodeStartSummary.P95Ms,
                DecodeEnqueueToDecodeStartMaxMs: decodeEnqueueToDecodeStartSummary.MaxMs,
                CaptureToDecodeStartAvgMs: captureToDecodeStartSummary.AvgMs,
                CaptureToDecodeStartMedianMs: captureToDecodeStartSummary.MedianMs,
                CaptureToDecodeStartP95Ms: captureToDecodeStartSummary.P95Ms,
                CaptureToDecodeStartMaxMs: captureToDecodeStartSummary.MaxMs,
                WorstEpochByCaptureToDecodeStart: worstEpoch,
                WorstEpochCaptureToDecodeStartAvgMs: worstEpochCaptureToDecodeStartAvgMs,
                DominantUpstreamLatencyStage: DetermineDominantUpstreamLatencyStage(
                    captureToFrameReadySummary,
                    frameReadyToViewerAcceptSummary,
                    viewerAcceptToDecodeEnqueueSummary,
                    decodeEnqueueToDecodeStartSummary,
                    captureToDecodeStartSummary),
                EpochSnapshots: epochSnapshots);
        }
    }

    internal static ScreenShareHelperReadyPathSessionSnapshot GetHelperReadyPathSnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ScreenShareHelperReadyPathSessionSnapshot.Empty;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session))
            {
                return ScreenShareHelperReadyPathSessionSnapshot.Empty;
            }

            var captureToFirstFragmentObserved = new List<long>();
            var firstFragmentToLastFragmentObserved = new List<long>();
            var lastFragmentToAssemblyComplete = new List<long>();
            var assemblyCompleteToFrameEmitted = new List<long>();
            var epochSnapshots = new List<ScreenShareHelperReadyPathEpochSnapshot>();

            foreach (var epochGroup in session.Frames.Values.GroupBy(static frame => frame.StreamEpoch).OrderBy(static group => group.Key))
            {
                var epochCaptureToFirstFragmentObserved = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.CapturedTsUtcMs, frame.FirstFragmentObservedUtcMs)));
                var epochFirstFragmentToLastFragmentObserved = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.FirstFragmentObservedUtcMs, frame.LastFragmentObservedUtcMs)));
                var epochLastFragmentToAssemblyComplete = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.LastFragmentObservedUtcMs, frame.FrameReadyObservedUtcMs)));
                var epochAssemblyCompleteToFrameEmitted = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.FrameReadyObservedUtcMs, frame.EmittedUtcMs)));

                captureToFirstFragmentObserved.AddRange(epochCaptureToFirstFragmentObserved);
                firstFragmentToLastFragmentObserved.AddRange(epochFirstFragmentToLastFragmentObserved);
                lastFragmentToAssemblyComplete.AddRange(epochLastFragmentToAssemblyComplete);
                assemblyCompleteToFrameEmitted.AddRange(epochAssemblyCompleteToFrameEmitted);

                var epochCaptureToFirstFragmentObservedSummary = ComputeLatencySummary(epochCaptureToFirstFragmentObserved);
                var epochFirstFragmentToLastFragmentObservedSummary = ComputeLatencySummary(epochFirstFragmentToLastFragmentObserved);
                var epochLastFragmentToAssemblyCompleteSummary = ComputeLatencySummary(epochLastFragmentToAssemblyComplete);
                var epochAssemblyCompleteToFrameEmittedSummary = ComputeLatencySummary(epochAssemblyCompleteToFrameEmitted);
                epochSnapshots.Add(
                    new ScreenShareHelperReadyPathEpochSnapshot(
                        epochGroup.Key,
                        epochCaptureToFirstFragmentObservedSummary.AvgMs,
                        epochCaptureToFirstFragmentObservedSummary.MedianMs,
                        epochCaptureToFirstFragmentObservedSummary.P95Ms,
                        epochCaptureToFirstFragmentObservedSummary.MaxMs,
                        epochFirstFragmentToLastFragmentObservedSummary.AvgMs,
                        epochFirstFragmentToLastFragmentObservedSummary.MedianMs,
                        epochFirstFragmentToLastFragmentObservedSummary.P95Ms,
                        epochFirstFragmentToLastFragmentObservedSummary.MaxMs,
                        epochLastFragmentToAssemblyCompleteSummary.AvgMs,
                        epochLastFragmentToAssemblyCompleteSummary.MedianMs,
                        epochLastFragmentToAssemblyCompleteSummary.P95Ms,
                        epochLastFragmentToAssemblyCompleteSummary.MaxMs,
                        epochAssemblyCompleteToFrameEmittedSummary.AvgMs,
                        epochAssemblyCompleteToFrameEmittedSummary.MedianMs,
                        epochAssemblyCompleteToFrameEmittedSummary.P95Ms,
                        epochAssemblyCompleteToFrameEmittedSummary.MaxMs));
            }

            var captureToFirstFragmentObservedSummary = ComputeLatencySummary(captureToFirstFragmentObserved);
            var firstFragmentToLastFragmentObservedSummary = ComputeLatencySummary(firstFragmentToLastFragmentObserved);
            var lastFragmentToAssemblyCompleteSummary = ComputeLatencySummary(lastFragmentToAssemblyComplete);
            var assemblyCompleteToFrameEmittedSummary = ComputeLatencySummary(assemblyCompleteToFrameEmitted);

            return new ScreenShareHelperReadyPathSessionSnapshot(
                SessionId: normalizedSessionId,
                CaptureToFirstFragmentObservedAvgMs: captureToFirstFragmentObservedSummary.AvgMs,
                CaptureToFirstFragmentObservedMedianMs: captureToFirstFragmentObservedSummary.MedianMs,
                CaptureToFirstFragmentObservedP95Ms: captureToFirstFragmentObservedSummary.P95Ms,
                CaptureToFirstFragmentObservedMaxMs: captureToFirstFragmentObservedSummary.MaxMs,
                FirstFragmentToLastFragmentObservedAvgMs: firstFragmentToLastFragmentObservedSummary.AvgMs,
                FirstFragmentToLastFragmentObservedMedianMs: firstFragmentToLastFragmentObservedSummary.MedianMs,
                FirstFragmentToLastFragmentObservedP95Ms: firstFragmentToLastFragmentObservedSummary.P95Ms,
                FirstFragmentToLastFragmentObservedMaxMs: firstFragmentToLastFragmentObservedSummary.MaxMs,
                LastFragmentToAssemblyCompleteAvgMs: lastFragmentToAssemblyCompleteSummary.AvgMs,
                LastFragmentToAssemblyCompleteMedianMs: lastFragmentToAssemblyCompleteSummary.MedianMs,
                LastFragmentToAssemblyCompleteP95Ms: lastFragmentToAssemblyCompleteSummary.P95Ms,
                LastFragmentToAssemblyCompleteMaxMs: lastFragmentToAssemblyCompleteSummary.MaxMs,
                AssemblyCompleteToFrameEmittedAvgMs: assemblyCompleteToFrameEmittedSummary.AvgMs,
                AssemblyCompleteToFrameEmittedMedianMs: assemblyCompleteToFrameEmittedSummary.MedianMs,
                AssemblyCompleteToFrameEmittedP95Ms: assemblyCompleteToFrameEmittedSummary.P95Ms,
                AssemblyCompleteToFrameEmittedMaxMs: assemblyCompleteToFrameEmittedSummary.MaxMs,
                DominantReadyPathStage: DetermineDominantReadyPathStage(
                    captureToFirstFragmentObservedSummary,
                    firstFragmentToLastFragmentObservedSummary,
                    lastFragmentToAssemblyCompleteSummary,
                    assemblyCompleteToFrameEmittedSummary),
                EpochSnapshots: epochSnapshots);
        }
    }

    internal static ScreenShareHelperReceivePathSessionSnapshot GetHelperReceivePathSnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ScreenShareHelperReceivePathSessionSnapshot.Empty;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session))
            {
                return ScreenShareHelperReceivePathSessionSnapshot.Empty;
            }

            var captureToEnvelopeSend = new List<long>();
            var envelopeSendToBridgeIngress = new List<long>();
            var bridgeIngressToEnvelopeParsed = new List<long>();
            var envelopeParsedToSecureDecrypt = new List<long>();
            var secureDecryptToFragmentDeserialize = new List<long>();
            var fragmentDeserializeToFirstFragmentObserved = new List<long>();
            var epochSnapshots = new List<ScreenShareHelperReceivePathEpochSnapshot>();

            foreach (var epochGroup in session.Frames.Values.GroupBy(static frame => frame.StreamEpoch).OrderBy(static group => group.Key))
            {
                var epochCaptureToEnvelopeSend = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.CapturedTsUtcMs, frame.EnvelopeSendUtcMs)));
                var epochEnvelopeSendToBridgeIngress = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.EnvelopeSendUtcMs, frame.BridgeIngressObservedUtcMs)));
                var epochBridgeIngressToEnvelopeParsed = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.BridgeIngressObservedUtcMs, frame.EnvelopeParsedUtcMs)));
                var epochEnvelopeParsedToSecureDecrypt = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.EnvelopeParsedUtcMs, frame.SecureDecryptCompletedUtcMs)));
                var epochSecureDecryptToFragmentDeserialize = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.SecureDecryptCompletedUtcMs, frame.FragmentEnvelopeDeserializedUtcMs)));
                var epochFragmentDeserializeToFirstFragmentObserved = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.FragmentEnvelopeDeserializedUtcMs, frame.FirstFragmentObservedUtcMs)));

                captureToEnvelopeSend.AddRange(epochCaptureToEnvelopeSend);
                envelopeSendToBridgeIngress.AddRange(epochEnvelopeSendToBridgeIngress);
                bridgeIngressToEnvelopeParsed.AddRange(epochBridgeIngressToEnvelopeParsed);
                envelopeParsedToSecureDecrypt.AddRange(epochEnvelopeParsedToSecureDecrypt);
                secureDecryptToFragmentDeserialize.AddRange(epochSecureDecryptToFragmentDeserialize);
                fragmentDeserializeToFirstFragmentObserved.AddRange(epochFragmentDeserializeToFirstFragmentObserved);

                var epochCaptureToEnvelopeSendSummary = ComputeLatencySummary(epochCaptureToEnvelopeSend);
                var epochEnvelopeSendToBridgeIngressSummary = ComputeLatencySummary(epochEnvelopeSendToBridgeIngress);
                var epochBridgeIngressToEnvelopeParsedSummary = ComputeLatencySummary(epochBridgeIngressToEnvelopeParsed);
                var epochEnvelopeParsedToSecureDecryptSummary = ComputeLatencySummary(epochEnvelopeParsedToSecureDecrypt);
                var epochSecureDecryptToFragmentDeserializeSummary = ComputeLatencySummary(epochSecureDecryptToFragmentDeserialize);
                var epochFragmentDeserializeToFirstFragmentObservedSummary = ComputeLatencySummary(epochFragmentDeserializeToFirstFragmentObserved);
                epochSnapshots.Add(
                    new ScreenShareHelperReceivePathEpochSnapshot(
                        epochGroup.Key,
                        epochCaptureToEnvelopeSendSummary.AvgMs,
                        epochCaptureToEnvelopeSendSummary.MedianMs,
                        epochCaptureToEnvelopeSendSummary.P95Ms,
                        epochCaptureToEnvelopeSendSummary.MaxMs,
                        epochEnvelopeSendToBridgeIngressSummary.AvgMs,
                        epochEnvelopeSendToBridgeIngressSummary.MedianMs,
                        epochEnvelopeSendToBridgeIngressSummary.P95Ms,
                        epochEnvelopeSendToBridgeIngressSummary.MaxMs,
                        epochBridgeIngressToEnvelopeParsedSummary.AvgMs,
                        epochBridgeIngressToEnvelopeParsedSummary.MedianMs,
                        epochBridgeIngressToEnvelopeParsedSummary.P95Ms,
                        epochBridgeIngressToEnvelopeParsedSummary.MaxMs,
                        epochEnvelopeParsedToSecureDecryptSummary.AvgMs,
                        epochEnvelopeParsedToSecureDecryptSummary.MedianMs,
                        epochEnvelopeParsedToSecureDecryptSummary.P95Ms,
                        epochEnvelopeParsedToSecureDecryptSummary.MaxMs,
                        epochSecureDecryptToFragmentDeserializeSummary.AvgMs,
                        epochSecureDecryptToFragmentDeserializeSummary.MedianMs,
                        epochSecureDecryptToFragmentDeserializeSummary.P95Ms,
                        epochSecureDecryptToFragmentDeserializeSummary.MaxMs,
                        epochFragmentDeserializeToFirstFragmentObservedSummary.AvgMs,
                        epochFragmentDeserializeToFirstFragmentObservedSummary.MedianMs,
                        epochFragmentDeserializeToFirstFragmentObservedSummary.P95Ms,
                        epochFragmentDeserializeToFirstFragmentObservedSummary.MaxMs));
            }

            var captureToEnvelopeSendSummary = ComputeLatencySummary(captureToEnvelopeSend);
            var envelopeSendToBridgeIngressSummary = ComputeLatencySummary(envelopeSendToBridgeIngress);
            var bridgeIngressToEnvelopeParsedSummary = ComputeLatencySummary(bridgeIngressToEnvelopeParsed);
            var envelopeParsedToSecureDecryptSummary = ComputeLatencySummary(envelopeParsedToSecureDecrypt);
            var secureDecryptToFragmentDeserializeSummary = ComputeLatencySummary(secureDecryptToFragmentDeserialize);
            var fragmentDeserializeToFirstFragmentObservedSummary = ComputeLatencySummary(fragmentDeserializeToFirstFragmentObserved);

            return new ScreenShareHelperReceivePathSessionSnapshot(
                SessionId: normalizedSessionId,
                CaptureToEnvelopeSendAvgMs: captureToEnvelopeSendSummary.AvgMs,
                CaptureToEnvelopeSendMedianMs: captureToEnvelopeSendSummary.MedianMs,
                CaptureToEnvelopeSendP95Ms: captureToEnvelopeSendSummary.P95Ms,
                CaptureToEnvelopeSendMaxMs: captureToEnvelopeSendSummary.MaxMs,
                EnvelopeSendToBridgeIngressAvgMs: envelopeSendToBridgeIngressSummary.AvgMs,
                EnvelopeSendToBridgeIngressMedianMs: envelopeSendToBridgeIngressSummary.MedianMs,
                EnvelopeSendToBridgeIngressP95Ms: envelopeSendToBridgeIngressSummary.P95Ms,
                EnvelopeSendToBridgeIngressMaxMs: envelopeSendToBridgeIngressSummary.MaxMs,
                BridgeIngressToEnvelopeParsedAvgMs: bridgeIngressToEnvelopeParsedSummary.AvgMs,
                BridgeIngressToEnvelopeParsedMedianMs: bridgeIngressToEnvelopeParsedSummary.MedianMs,
                BridgeIngressToEnvelopeParsedP95Ms: bridgeIngressToEnvelopeParsedSummary.P95Ms,
                BridgeIngressToEnvelopeParsedMaxMs: bridgeIngressToEnvelopeParsedSummary.MaxMs,
                EnvelopeParsedToSecureDecryptAvgMs: envelopeParsedToSecureDecryptSummary.AvgMs,
                EnvelopeParsedToSecureDecryptMedianMs: envelopeParsedToSecureDecryptSummary.MedianMs,
                EnvelopeParsedToSecureDecryptP95Ms: envelopeParsedToSecureDecryptSummary.P95Ms,
                EnvelopeParsedToSecureDecryptMaxMs: envelopeParsedToSecureDecryptSummary.MaxMs,
                SecureDecryptToFragmentDeserializeAvgMs: secureDecryptToFragmentDeserializeSummary.AvgMs,
                SecureDecryptToFragmentDeserializeMedianMs: secureDecryptToFragmentDeserializeSummary.MedianMs,
                SecureDecryptToFragmentDeserializeP95Ms: secureDecryptToFragmentDeserializeSummary.P95Ms,
                SecureDecryptToFragmentDeserializeMaxMs: secureDecryptToFragmentDeserializeSummary.MaxMs,
                FragmentDeserializeToFirstFragmentObservedAvgMs: fragmentDeserializeToFirstFragmentObservedSummary.AvgMs,
                FragmentDeserializeToFirstFragmentObservedMedianMs: fragmentDeserializeToFirstFragmentObservedSummary.MedianMs,
                FragmentDeserializeToFirstFragmentObservedP95Ms: fragmentDeserializeToFirstFragmentObservedSummary.P95Ms,
                FragmentDeserializeToFirstFragmentObservedMaxMs: fragmentDeserializeToFirstFragmentObservedSummary.MaxMs,
                DominantReceivePathStage: DetermineDominantReceivePathStage(
                    captureToEnvelopeSendSummary,
                    envelopeSendToBridgeIngressSummary,
                    bridgeIngressToEnvelopeParsedSummary,
                    envelopeParsedToSecureDecryptSummary,
                    secureDecryptToFragmentDeserializeSummary,
                    fragmentDeserializeToFirstFragmentObservedSummary),
                EpochSnapshots: epochSnapshots);
        }
    }

    internal static ScreenShareHelperBridgeIngressSessionSnapshot GetHelperBridgeIngressSnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ScreenShareHelperBridgeIngressSessionSnapshot.Empty;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session))
            {
                return ScreenShareHelperBridgeIngressSessionSnapshot.Empty;
            }

            var envelopeSendToBridgeMessageObserved = new List<long>();
            var bridgeMessageObservedToBinaryFrameDecoded = new List<long>();
            var binaryFrameDecodedToBridgeIngress = new List<long>();
            var epochSnapshots = new List<ScreenShareHelperBridgeIngressEpochSnapshot>();

            foreach (var epochGroup in session.Frames.Values.GroupBy(static frame => frame.StreamEpoch).OrderBy(static group => group.Key))
            {
                var epochEnvelopeSendToBridgeMessageObserved = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.EnvelopeSendUtcMs, frame.BridgeMessageObservedUtcMs)));
                var epochBridgeMessageObservedToBinaryFrameDecoded = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.BridgeMessageObservedUtcMs, frame.BinaryFrameDecodedUtcMs)));
                var epochBinaryFrameDecodedToBridgeIngress = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.BinaryFrameDecodedUtcMs, frame.BridgeIngressObservedUtcMs)));

                envelopeSendToBridgeMessageObserved.AddRange(epochEnvelopeSendToBridgeMessageObserved);
                bridgeMessageObservedToBinaryFrameDecoded.AddRange(epochBridgeMessageObservedToBinaryFrameDecoded);
                binaryFrameDecodedToBridgeIngress.AddRange(epochBinaryFrameDecodedToBridgeIngress);

                var epochEnvelopeSendToBridgeMessageObservedSummary = ComputeLatencySummary(epochEnvelopeSendToBridgeMessageObserved);
                var epochBridgeMessageObservedToBinaryFrameDecodedSummary = ComputeLatencySummary(epochBridgeMessageObservedToBinaryFrameDecoded);
                var epochBinaryFrameDecodedToBridgeIngressSummary = ComputeLatencySummary(epochBinaryFrameDecodedToBridgeIngress);
                epochSnapshots.Add(
                    new ScreenShareHelperBridgeIngressEpochSnapshot(
                        epochGroup.Key,
                        epochEnvelopeSendToBridgeMessageObservedSummary.AvgMs,
                        epochEnvelopeSendToBridgeMessageObservedSummary.MedianMs,
                        epochEnvelopeSendToBridgeMessageObservedSummary.P95Ms,
                        epochEnvelopeSendToBridgeMessageObservedSummary.MaxMs,
                        epochBridgeMessageObservedToBinaryFrameDecodedSummary.AvgMs,
                        epochBridgeMessageObservedToBinaryFrameDecodedSummary.MedianMs,
                        epochBridgeMessageObservedToBinaryFrameDecodedSummary.P95Ms,
                        epochBridgeMessageObservedToBinaryFrameDecodedSummary.MaxMs,
                        epochBinaryFrameDecodedToBridgeIngressSummary.AvgMs,
                        epochBinaryFrameDecodedToBridgeIngressSummary.MedianMs,
                        epochBinaryFrameDecodedToBridgeIngressSummary.P95Ms,
                        epochBinaryFrameDecodedToBridgeIngressSummary.MaxMs));
            }

            var envelopeSendToBridgeMessageObservedSummary = ComputeLatencySummary(envelopeSendToBridgeMessageObserved);
            var bridgeMessageObservedToBinaryFrameDecodedSummary = ComputeLatencySummary(bridgeMessageObservedToBinaryFrameDecoded);
            var binaryFrameDecodedToBridgeIngressSummary = ComputeLatencySummary(binaryFrameDecodedToBridgeIngress);

            return new ScreenShareHelperBridgeIngressSessionSnapshot(
                SessionId: normalizedSessionId,
                EnvelopeSendToBridgeMessageObservedAvgMs: envelopeSendToBridgeMessageObservedSummary.AvgMs,
                EnvelopeSendToBridgeMessageObservedMedianMs: envelopeSendToBridgeMessageObservedSummary.MedianMs,
                EnvelopeSendToBridgeMessageObservedP95Ms: envelopeSendToBridgeMessageObservedSummary.P95Ms,
                EnvelopeSendToBridgeMessageObservedMaxMs: envelopeSendToBridgeMessageObservedSummary.MaxMs,
                BridgeMessageObservedToBinaryFrameDecodedAvgMs: bridgeMessageObservedToBinaryFrameDecodedSummary.AvgMs,
                BridgeMessageObservedToBinaryFrameDecodedMedianMs: bridgeMessageObservedToBinaryFrameDecodedSummary.MedianMs,
                BridgeMessageObservedToBinaryFrameDecodedP95Ms: bridgeMessageObservedToBinaryFrameDecodedSummary.P95Ms,
                BridgeMessageObservedToBinaryFrameDecodedMaxMs: bridgeMessageObservedToBinaryFrameDecodedSummary.MaxMs,
                BinaryFrameDecodedToBridgeIngressAvgMs: binaryFrameDecodedToBridgeIngressSummary.AvgMs,
                BinaryFrameDecodedToBridgeIngressMedianMs: binaryFrameDecodedToBridgeIngressSummary.MedianMs,
                BinaryFrameDecodedToBridgeIngressP95Ms: binaryFrameDecodedToBridgeIngressSummary.P95Ms,
                BinaryFrameDecodedToBridgeIngressMaxMs: binaryFrameDecodedToBridgeIngressSummary.MaxMs,
                DominantBridgeIngressStage: DetermineDominantBridgeIngressStage(
                    envelopeSendToBridgeMessageObservedSummary,
                    bridgeMessageObservedToBinaryFrameDecodedSummary,
                    binaryFrameDecodedToBridgeIngressSummary),
                EpochSnapshots: epochSnapshots);
        }
    }

    internal static ScreenShareHelperNknReceiveSessionSnapshot GetHelperNknReceiveSnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ScreenShareHelperNknReceiveSessionSnapshot.Empty;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session))
            {
                return ScreenShareHelperNknReceiveSessionSnapshot.Empty;
            }

            var envelopeSendToSdkHandleMsgEntered = new List<long>();
            var sdkHandleMsgEnteredToClientMessageDispatch = new List<long>();
            var clientMessageDispatchToMultiClientMessageDispatch = new List<long>();
            var multiClientMessageDispatchToBridgeMessageObserved = new List<long>();
            var epochSnapshots = new List<ScreenShareHelperNknReceiveEpochSnapshot>();

            foreach (var epochGroup in session.Frames.Values.GroupBy(static frame => frame.StreamEpoch).OrderBy(static group => group.Key))
            {
                var epochEnvelopeSendToSdkHandleMsgEntered = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.EnvelopeSendUtcMs, frame.SdkHandleMsgEnteredUtcMs)));
                var epochSdkHandleMsgEnteredToClientMessageDispatch = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.SdkHandleMsgEnteredUtcMs, frame.ClientMessageDispatchUtcMs)));
                var epochClientMessageDispatchToMultiClientMessageDispatch = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.ClientMessageDispatchUtcMs, frame.MultiClientMessageDispatchUtcMs)));
                var epochMultiClientMessageDispatchToBridgeMessageObserved = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.MultiClientMessageDispatchUtcMs, frame.BridgeMessageObservedUtcMs)));

                envelopeSendToSdkHandleMsgEntered.AddRange(epochEnvelopeSendToSdkHandleMsgEntered);
                sdkHandleMsgEnteredToClientMessageDispatch.AddRange(epochSdkHandleMsgEnteredToClientMessageDispatch);
                clientMessageDispatchToMultiClientMessageDispatch.AddRange(epochClientMessageDispatchToMultiClientMessageDispatch);
                multiClientMessageDispatchToBridgeMessageObserved.AddRange(epochMultiClientMessageDispatchToBridgeMessageObserved);

                var epochEnvelopeSendToSdkHandleMsgEnteredSummary = ComputeLatencySummary(epochEnvelopeSendToSdkHandleMsgEntered);
                var epochSdkHandleMsgEnteredToClientMessageDispatchSummary = ComputeLatencySummary(epochSdkHandleMsgEnteredToClientMessageDispatch);
                var epochClientMessageDispatchToMultiClientMessageDispatchSummary = ComputeLatencySummary(epochClientMessageDispatchToMultiClientMessageDispatch);
                var epochMultiClientMessageDispatchToBridgeMessageObservedSummary = ComputeLatencySummary(epochMultiClientMessageDispatchToBridgeMessageObserved);
                epochSnapshots.Add(
                    new ScreenShareHelperNknReceiveEpochSnapshot(
                        epochGroup.Key,
                        epochEnvelopeSendToSdkHandleMsgEnteredSummary.AvgMs,
                        epochEnvelopeSendToSdkHandleMsgEnteredSummary.MedianMs,
                        epochEnvelopeSendToSdkHandleMsgEnteredSummary.P95Ms,
                        epochEnvelopeSendToSdkHandleMsgEnteredSummary.MaxMs,
                        epochSdkHandleMsgEnteredToClientMessageDispatchSummary.AvgMs,
                        epochSdkHandleMsgEnteredToClientMessageDispatchSummary.MedianMs,
                        epochSdkHandleMsgEnteredToClientMessageDispatchSummary.P95Ms,
                        epochSdkHandleMsgEnteredToClientMessageDispatchSummary.MaxMs,
                        epochClientMessageDispatchToMultiClientMessageDispatchSummary.AvgMs,
                        epochClientMessageDispatchToMultiClientMessageDispatchSummary.MedianMs,
                        epochClientMessageDispatchToMultiClientMessageDispatchSummary.P95Ms,
                        epochClientMessageDispatchToMultiClientMessageDispatchSummary.MaxMs,
                        epochMultiClientMessageDispatchToBridgeMessageObservedSummary.AvgMs,
                        epochMultiClientMessageDispatchToBridgeMessageObservedSummary.MedianMs,
                        epochMultiClientMessageDispatchToBridgeMessageObservedSummary.P95Ms,
                        epochMultiClientMessageDispatchToBridgeMessageObservedSummary.MaxMs));
            }

            var envelopeSendToSdkHandleMsgEnteredSummary = ComputeLatencySummary(envelopeSendToSdkHandleMsgEntered);
            var sdkHandleMsgEnteredToClientMessageDispatchSummary = ComputeLatencySummary(sdkHandleMsgEnteredToClientMessageDispatch);
            var clientMessageDispatchToMultiClientMessageDispatchSummary = ComputeLatencySummary(clientMessageDispatchToMultiClientMessageDispatch);
            var multiClientMessageDispatchToBridgeMessageObservedSummary = ComputeLatencySummary(multiClientMessageDispatchToBridgeMessageObserved);

            return new ScreenShareHelperNknReceiveSessionSnapshot(
                SessionId: normalizedSessionId,
                EnvelopeSendToSdkHandleMsgEnteredAvgMs: envelopeSendToSdkHandleMsgEnteredSummary.AvgMs,
                EnvelopeSendToSdkHandleMsgEnteredMedianMs: envelopeSendToSdkHandleMsgEnteredSummary.MedianMs,
                EnvelopeSendToSdkHandleMsgEnteredP95Ms: envelopeSendToSdkHandleMsgEnteredSummary.P95Ms,
                EnvelopeSendToSdkHandleMsgEnteredMaxMs: envelopeSendToSdkHandleMsgEnteredSummary.MaxMs,
                SdkHandleMsgEnteredToClientMessageDispatchAvgMs: sdkHandleMsgEnteredToClientMessageDispatchSummary.AvgMs,
                SdkHandleMsgEnteredToClientMessageDispatchMedianMs: sdkHandleMsgEnteredToClientMessageDispatchSummary.MedianMs,
                SdkHandleMsgEnteredToClientMessageDispatchP95Ms: sdkHandleMsgEnteredToClientMessageDispatchSummary.P95Ms,
                SdkHandleMsgEnteredToClientMessageDispatchMaxMs: sdkHandleMsgEnteredToClientMessageDispatchSummary.MaxMs,
                ClientMessageDispatchToMultiClientMessageDispatchAvgMs: clientMessageDispatchToMultiClientMessageDispatchSummary.AvgMs,
                ClientMessageDispatchToMultiClientMessageDispatchMedianMs: clientMessageDispatchToMultiClientMessageDispatchSummary.MedianMs,
                ClientMessageDispatchToMultiClientMessageDispatchP95Ms: clientMessageDispatchToMultiClientMessageDispatchSummary.P95Ms,
                ClientMessageDispatchToMultiClientMessageDispatchMaxMs: clientMessageDispatchToMultiClientMessageDispatchSummary.MaxMs,
                MultiClientMessageDispatchToBridgeMessageObservedAvgMs: multiClientMessageDispatchToBridgeMessageObservedSummary.AvgMs,
                MultiClientMessageDispatchToBridgeMessageObservedMedianMs: multiClientMessageDispatchToBridgeMessageObservedSummary.MedianMs,
                MultiClientMessageDispatchToBridgeMessageObservedP95Ms: multiClientMessageDispatchToBridgeMessageObservedSummary.P95Ms,
                MultiClientMessageDispatchToBridgeMessageObservedMaxMs: multiClientMessageDispatchToBridgeMessageObservedSummary.MaxMs,
                DominantNknReceiveStage: DetermineDominantNknReceiveStage(
                    envelopeSendToSdkHandleMsgEnteredSummary,
                    sdkHandleMsgEnteredToClientMessageDispatchSummary,
                    clientMessageDispatchToMultiClientMessageDispatchSummary,
                    multiClientMessageDispatchToBridgeMessageObservedSummary),
                EpochSnapshots: epochSnapshots);
        }
    }

    internal static ScreenShareHelperWsReceiveSessionSnapshot GetHelperWsReceiveSnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ScreenShareHelperWsReceiveSessionSnapshot.Empty;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session))
            {
                return ScreenShareHelperWsReceiveSessionSnapshot.Empty;
            }

            var envelopeSendToWsReceiverWriteEntered = new List<long>();
            var wsReceiverWriteEnteredToWsMessageEmitted = new List<long>();
            var wsMessageEmittedToSdkHandleMsgEntered = new List<long>();
            var epochSnapshots = new List<ScreenShareHelperWsReceiveEpochSnapshot>();

            foreach (var epochGroup in session.Frames.Values.GroupBy(static frame => frame.StreamEpoch).OrderBy(static group => group.Key))
            {
                var epochEnvelopeSendToWsReceiverWriteEntered = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.EnvelopeSendUtcMs, frame.WsReceiverWriteEnteredUtcMs)));
                var epochWsReceiverWriteEnteredToWsMessageEmitted = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.WsReceiverWriteEnteredUtcMs, frame.WsMessageEmittedUtcMs)));
                var epochWsMessageEmittedToSdkHandleMsgEntered = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.WsMessageEmittedUtcMs, frame.SdkHandleMsgEnteredUtcMs)));

                envelopeSendToWsReceiverWriteEntered.AddRange(epochEnvelopeSendToWsReceiverWriteEntered);
                wsReceiverWriteEnteredToWsMessageEmitted.AddRange(epochWsReceiverWriteEnteredToWsMessageEmitted);
                wsMessageEmittedToSdkHandleMsgEntered.AddRange(epochWsMessageEmittedToSdkHandleMsgEntered);

                var epochEnvelopeSendToWsReceiverWriteEnteredSummary = ComputeLatencySummary(epochEnvelopeSendToWsReceiverWriteEntered);
                var epochWsReceiverWriteEnteredToWsMessageEmittedSummary = ComputeLatencySummary(epochWsReceiverWriteEnteredToWsMessageEmitted);
                var epochWsMessageEmittedToSdkHandleMsgEnteredSummary = ComputeLatencySummary(epochWsMessageEmittedToSdkHandleMsgEntered);
                epochSnapshots.Add(
                    new ScreenShareHelperWsReceiveEpochSnapshot(
                        epochGroup.Key,
                        epochEnvelopeSendToWsReceiverWriteEnteredSummary.AvgMs,
                        epochEnvelopeSendToWsReceiverWriteEnteredSummary.MedianMs,
                        epochEnvelopeSendToWsReceiverWriteEnteredSummary.P95Ms,
                        epochEnvelopeSendToWsReceiverWriteEnteredSummary.MaxMs,
                        epochWsReceiverWriteEnteredToWsMessageEmittedSummary.AvgMs,
                        epochWsReceiverWriteEnteredToWsMessageEmittedSummary.MedianMs,
                        epochWsReceiverWriteEnteredToWsMessageEmittedSummary.P95Ms,
                        epochWsReceiverWriteEnteredToWsMessageEmittedSummary.MaxMs,
                        epochWsMessageEmittedToSdkHandleMsgEnteredSummary.AvgMs,
                        epochWsMessageEmittedToSdkHandleMsgEnteredSummary.MedianMs,
                        epochWsMessageEmittedToSdkHandleMsgEnteredSummary.P95Ms,
                        epochWsMessageEmittedToSdkHandleMsgEnteredSummary.MaxMs));
            }

            var envelopeSendToWsReceiverWriteEnteredSummary = ComputeLatencySummary(envelopeSendToWsReceiverWriteEntered);
            var wsReceiverWriteEnteredToWsMessageEmittedSummary = ComputeLatencySummary(wsReceiverWriteEnteredToWsMessageEmitted);
            var wsMessageEmittedToSdkHandleMsgEnteredSummary = ComputeLatencySummary(wsMessageEmittedToSdkHandleMsgEntered);

            return new ScreenShareHelperWsReceiveSessionSnapshot(
                SessionId: normalizedSessionId,
                EnvelopeSendToWsReceiverWriteEnteredAvgMs: envelopeSendToWsReceiverWriteEnteredSummary.AvgMs,
                EnvelopeSendToWsReceiverWriteEnteredMedianMs: envelopeSendToWsReceiverWriteEnteredSummary.MedianMs,
                EnvelopeSendToWsReceiverWriteEnteredP95Ms: envelopeSendToWsReceiverWriteEnteredSummary.P95Ms,
                EnvelopeSendToWsReceiverWriteEnteredMaxMs: envelopeSendToWsReceiverWriteEnteredSummary.MaxMs,
                WsReceiverWriteEnteredToWsMessageEmittedAvgMs: wsReceiverWriteEnteredToWsMessageEmittedSummary.AvgMs,
                WsReceiverWriteEnteredToWsMessageEmittedMedianMs: wsReceiverWriteEnteredToWsMessageEmittedSummary.MedianMs,
                WsReceiverWriteEnteredToWsMessageEmittedP95Ms: wsReceiverWriteEnteredToWsMessageEmittedSummary.P95Ms,
                WsReceiverWriteEnteredToWsMessageEmittedMaxMs: wsReceiverWriteEnteredToWsMessageEmittedSummary.MaxMs,
                WsMessageEmittedToSdkHandleMsgEnteredAvgMs: wsMessageEmittedToSdkHandleMsgEnteredSummary.AvgMs,
                WsMessageEmittedToSdkHandleMsgEnteredMedianMs: wsMessageEmittedToSdkHandleMsgEnteredSummary.MedianMs,
                WsMessageEmittedToSdkHandleMsgEnteredP95Ms: wsMessageEmittedToSdkHandleMsgEnteredSummary.P95Ms,
                WsMessageEmittedToSdkHandleMsgEnteredMaxMs: wsMessageEmittedToSdkHandleMsgEnteredSummary.MaxMs,
                DominantWsReceiveStage: DetermineDominantWsReceiveStage(
                    envelopeSendToWsReceiverWriteEnteredSummary,
                    wsReceiverWriteEnteredToWsMessageEmittedSummary,
                    wsMessageEmittedToSdkHandleMsgEnteredSummary),
                EpochSnapshots: epochSnapshots);
        }
    }

    internal static ScreenShareHelperSocketReceiveSessionSnapshot GetHelperSocketReceiveSnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ScreenShareHelperSocketReceiveSessionSnapshot.Empty;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (Gate)
        {
            if (!Sessions.TryGetValue(normalizedSessionId, out var session))
            {
                return ScreenShareHelperSocketReceiveSessionSnapshot.Empty;
            }

            var envelopeSendToSocketDataEventEmitted = new List<long>();
            var socketDataEventEmittedToWsReceiverWriteEntered = new List<long>();
            var epochSnapshots = new List<ScreenShareHelperSocketReceiveEpochSnapshot>();

            foreach (var epochGroup in session.Frames.Values.GroupBy(static frame => frame.StreamEpoch).OrderBy(static group => group.Key))
            {
                var epochEnvelopeSendToSocketDataEventEmitted = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.EnvelopeSendUtcMs, frame.SocketDataEventEmittedUtcMs)));
                var epochSocketDataEventEmittedToWsReceiverWriteEntered = CollectNonNegativeDurations(epochGroup.Select(static frame => ComputeDuration(frame.SocketDataEventEmittedUtcMs, frame.WsReceiverWriteEnteredUtcMs)));

                envelopeSendToSocketDataEventEmitted.AddRange(epochEnvelopeSendToSocketDataEventEmitted);
                socketDataEventEmittedToWsReceiverWriteEntered.AddRange(epochSocketDataEventEmittedToWsReceiverWriteEntered);

                var epochEnvelopeSendToSocketDataEventEmittedSummary = ComputeLatencySummary(epochEnvelopeSendToSocketDataEventEmitted);
                var epochSocketDataEventEmittedToWsReceiverWriteEnteredSummary = ComputeLatencySummary(epochSocketDataEventEmittedToWsReceiverWriteEntered);
                epochSnapshots.Add(
                    new ScreenShareHelperSocketReceiveEpochSnapshot(
                        epochGroup.Key,
                        epochEnvelopeSendToSocketDataEventEmittedSummary.AvgMs,
                        epochEnvelopeSendToSocketDataEventEmittedSummary.MedianMs,
                        epochEnvelopeSendToSocketDataEventEmittedSummary.P95Ms,
                        epochEnvelopeSendToSocketDataEventEmittedSummary.MaxMs,
                        epochSocketDataEventEmittedToWsReceiverWriteEnteredSummary.AvgMs,
                        epochSocketDataEventEmittedToWsReceiverWriteEnteredSummary.MedianMs,
                        epochSocketDataEventEmittedToWsReceiverWriteEnteredSummary.P95Ms,
                        epochSocketDataEventEmittedToWsReceiverWriteEnteredSummary.MaxMs));
            }

            var envelopeSendToSocketDataEventEmittedSummary = ComputeLatencySummary(envelopeSendToSocketDataEventEmitted);
            var socketDataEventEmittedToWsReceiverWriteEnteredSummary = ComputeLatencySummary(socketDataEventEmittedToWsReceiverWriteEntered);

            return new ScreenShareHelperSocketReceiveSessionSnapshot(
                SessionId: normalizedSessionId,
                EnvelopeSendToSocketDataEventEmittedAvgMs: envelopeSendToSocketDataEventEmittedSummary.AvgMs,
                EnvelopeSendToSocketDataEventEmittedMedianMs: envelopeSendToSocketDataEventEmittedSummary.MedianMs,
                EnvelopeSendToSocketDataEventEmittedP95Ms: envelopeSendToSocketDataEventEmittedSummary.P95Ms,
                EnvelopeSendToSocketDataEventEmittedMaxMs: envelopeSendToSocketDataEventEmittedSummary.MaxMs,
                SocketDataEventEmittedToWsReceiverWriteEnteredAvgMs: socketDataEventEmittedToWsReceiverWriteEnteredSummary.AvgMs,
                SocketDataEventEmittedToWsReceiverWriteEnteredMedianMs: socketDataEventEmittedToWsReceiverWriteEnteredSummary.MedianMs,
                SocketDataEventEmittedToWsReceiverWriteEnteredP95Ms: socketDataEventEmittedToWsReceiverWriteEnteredSummary.P95Ms,
                SocketDataEventEmittedToWsReceiverWriteEnteredMaxMs: socketDataEventEmittedToWsReceiverWriteEnteredSummary.MaxMs,
                DominantSocketReceiveStage: DetermineDominantSocketReceiveStage(
                    envelopeSendToSocketDataEventEmittedSummary,
                    socketDataEventEmittedToWsReceiverWriteEnteredSummary),
                EpochSnapshots: epochSnapshots);
        }
    }

    private static void ObserveStage(string sessionId, long streamEpoch, long frameId, bool isKeyFrame, FrameLifecycleStage stage, long occurredUtcMs = 0, long capturedTsUtcMs = 0)
    {
        if (!TryNormalize(sessionId, streamEpoch, frameId, out var normalizedSessionId))
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var frame = GetOrCreateFrameState(session, streamEpoch, frameId, isKeyFrame);
            frame.IsKeyFrame |= isKeyFrame;
            ApplyStage(frame, stage);
            if (capturedTsUtcMs > 0 && frame.CapturedTsUtcMs <= 0)
            {
                frame.CapturedTsUtcMs = capturedTsUtcMs;
            }

            var observedUtcMs = occurredUtcMs > 0 ? occurredUtcMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            switch (stage)
            {
                case FrameLifecycleStage.Ready when frame.FrameReadyObservedUtcMs <= 0:
                    frame.FrameReadyObservedUtcMs = observedUtcMs;
                    break;
                case FrameLifecycleStage.Emitted when frame.EmittedUtcMs <= 0:
                    frame.EmittedUtcMs = observedUtcMs;
                    break;
                case FrameLifecycleStage.ViewerAccepted when frame.ViewerAcceptedUtcMs <= 0:
                    frame.ViewerAcceptedUtcMs = observedUtcMs;
                    break;
                case FrameLifecycleStage.DecodeEnqueued when frame.DecodeEnqueuedUtcMs <= 0:
                    frame.DecodeEnqueuedUtcMs = observedUtcMs;
                    break;
                case FrameLifecycleStage.DecodeStarted when frame.DecodeStartedUtcMs <= 0:
                    frame.DecodeStartedUtcMs = observedUtcMs;
                    break;
                case FrameLifecycleStage.Decoded when frame.DecodeCompletedUtcMs <= 0:
                    frame.DecodeCompletedUtcMs = observedUtcMs;
                    break;
                case FrameLifecycleStage.Applied when frame.AppliedUtcMs <= 0:
                    frame.AppliedUtcMs = observedUtcMs;
                    break;
            }

            frame.LastUpdatedUtcMs = observedUtcMs;
            var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
            switch (stage)
            {
                case FrameLifecycleStage.FragmentSeen when epochDiagnostics.FirstFragmentSeenUtcMs <= 0:
                    epochDiagnostics.FirstFragmentSeenUtcMs = observedUtcMs;
                    epochDiagnostics.FirstFragmentSeenFrameId = frameId;
                    NoteTimelineEvent(epochDiagnostics, "first_fragment_seen", frameId, -1, observedUtcMs);
                    break;
                case FrameLifecycleStage.Assembled when epochDiagnostics.FirstFrameAssembledUtcMs <= 0:
                    epochDiagnostics.FirstFrameAssembledUtcMs = observedUtcMs;
                    epochDiagnostics.FirstFrameAssembledFrameId = frameId;
                    NoteTimelineEvent(epochDiagnostics, "first_frame_assembled", frameId, -1, observedUtcMs);
                    break;
                case FrameLifecycleStage.Emitted when epochDiagnostics.FirstFrameEmittedUtcMs <= 0:
                    epochDiagnostics.FirstFrameEmittedUtcMs = observedUtcMs;
                    epochDiagnostics.FirstFrameEmittedFrameId = frameId;
                    NoteTimelineEvent(epochDiagnostics, "first_frame_emitted", frameId, -1, observedUtcMs);
                    break;
            }
        }
    }

    private static void ObserveLoss(
        string sessionId,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        FrameLifecycleStage stage,
        ScreenShareFrameLossBucket bucket,
        string reason,
        long relatedFrameId = -1)
    {
        if (!TryNormalize(sessionId, streamEpoch, frameId, out var normalizedSessionId))
        {
            return;
        }

        lock (Gate)
        {
            var session = GetOrCreateSessionState(normalizedSessionId);
            var frame = GetOrCreateFrameState(session, streamEpoch, frameId, isKeyFrame);
            var sanitizedReason = Sanitize(reason);
            frame.IsKeyFrame |= isKeyFrame;
            ApplyStage(frame, stage);
            frame.LastUpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (frame.LossBucket == ScreenShareFrameLossBucket.None)
            {
                frame.LossBucket = bucket;
                frame.LossReason = sanitizedReason;
                frame.RelatedFrameId = relatedFrameId;
                EnqueueRecentLoss(
                    session,
                    new ScreenShareFrameLossBreadcrumb(
                        streamEpoch,
                        frameId,
                        stage.ToString(),
                        frame.LossReason,
                        relatedFrameId));
            }

            if (string.Equals(sanitizedReason, "superseded_recovery_tail_cleanup", StringComparison.Ordinal))
            {
                var epochDiagnostics = GetOrCreateEpochDiagnosticsState(session, streamEpoch);
                epochDiagnostics.SupersededRecoveryTailCleanupCount++;
            }
        }
    }

    private static bool TryNormalize(string sessionId, long streamEpoch, long frameId, out string normalizedSessionId)
    {
        normalizedSessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0 || frameId < 0)
        {
            return false;
        }

        normalizedSessionId = sessionId.Trim();
        return normalizedSessionId.Length > 0;
    }

    private static SessionState GetOrCreateSessionState(string sessionId)
    {
        if (Sessions.TryGetValue(sessionId, out var session))
        {
            TouchSession(sessionId, session);
            return session;
        }

        session = new SessionState(sessionId);
        session.OrderNode = SessionOrder.AddLast(sessionId);
        Sessions.Add(sessionId, session);

        while (Sessions.Count > MaxTrackedSessions)
        {
            var oldestNode = SessionOrder.First;
            if (oldestNode is null)
            {
                break;
            }

            SessionOrder.RemoveFirst();
            Sessions.Remove(oldestNode.Value);
        }

        return session;
    }

    private static void TouchSession(string sessionId, SessionState session)
    {
        if (session.OrderNode is null)
        {
            session.OrderNode = SessionOrder.AddLast(sessionId);
            return;
        }

        if (!ReferenceEquals(SessionOrder.Last, session.OrderNode))
        {
            SessionOrder.Remove(session.OrderNode);
            SessionOrder.AddLast(session.OrderNode);
        }
    }

    private static FrameState GetOrCreateFrameState(SessionState session, long streamEpoch, long frameId, bool isKeyFrame)
    {
        var key = new FrameKey(streamEpoch, frameId);
        if (session.Frames.TryGetValue(key, out var frame))
        {
            return frame;
        }

        frame = new FrameState(streamEpoch, frameId, isKeyFrame);
        session.Frames.Add(key, frame);
        TrimTrackedFrames(session);
        return frame;
    }

    private static void TrimTrackedFrames(SessionState session)
    {
        while (session.Frames.Count > MaxTrackedFramesPerSession)
        {
            FrameKey oldestKey = default;
            var foundOldest = false;
            foreach (var key in session.Frames.Keys)
            {
                if (!foundOldest ||
                    key.StreamEpoch < oldestKey.StreamEpoch ||
                    key.StreamEpoch == oldestKey.StreamEpoch && key.FrameId < oldestKey.FrameId)
                {
                    oldestKey = key;
                    foundOldest = true;
                }
            }

            if (!foundOldest)
            {
                break;
            }

            session.Frames.Remove(oldestKey);
        }
    }

    private static void EnqueueRecentLoss(SessionState session, ScreenShareFrameLossBreadcrumb breadcrumb)
    {
        session.RecentLosses.AddLast(breadcrumb);
        while (session.RecentLosses.Count > MaxRecentLossesPerSession)
        {
            session.RecentLosses.RemoveFirst();
        }
    }

    private static ScreenShareFrameLossSessionSnapshot BuildSnapshot(SessionState session)
    {
        var epochBuckets = new Dictionary<long, EpochAccumulator>();
        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long fragmentSeenFrames = 0;
        long framesAssembled = 0;
        long framesReady = 0;
        long framesEmitted = 0;
        long viewerAcceptedFrames = 0;
        long decodeEnqueuedFrames = 0;
        long framesDecoded = 0;
        long framesApplied = 0;
        long reassemblerStaleSupersededLossCount = 0;
        long assemblyEvictedLossCount = 0;
        long readyFrameSkippedReplacedLossCount = 0;
        long viewerRejectedBeforeEnqueueCount = 0;
        long waitingForRecoveryKeyframeRejectCount = 0;
        long recoveryRunwayOverflowRejectCount = 0;
        long suppressedEmitDuringRecoveryWaitCount = 0;
        long blockedByReservedRecoveryFrameRejectCount = 0;
        long olderEpochIgnoredDuringRecoveryLockCount = 0;
        long newerEpochNonKeyIgnoredDuringLockCount = 0;
        long deferredPostRecoveryCandidateReplaceCount = 0;
        long decodeWorkerDroppedBeforeDecodeCount = 0;
        long decodeQueueOverflowCount = 0;
        long decodeAgeBudgetCount = 0;
        long decodeGenerationChangedCount = 0;
        long decodeStoppedCount = 0;
        long decodedApplyQueueOverflowCount = 0;
        long decodedFrameReplacedBeforeApplyCount = 0;
        long decodedStaleAfterRecoveryCount = 0;
        long decodedBlockedByReservedRecoveryFrameCount = 0;
        long decodedNewerEpochIgnoredDuringLockCount = 0;
        long droppedWaitingForRecoveryKeyframeCount = 0;
        long decodeFailedLossCount = 0;
        long staleDroppedAfterDecodeCount = 0;
        long gapNonKeyPrunedCount = 0;
        long futureTailQuarantinedDuringGapCount = 0;
        long futureTailQuarantinedAfterGapCount = 0;
        long preCandidateGapTailRejectedCount = 0;
        long fragmentGapBeforeAssemblyCount = 0;
        long lateFragmentAfterHeadAdvancedCount = 0;
        long lateFragmentAfterAppliedHeadCount = 0;
        long lateFragmentAfterOrderedHeadCount = 0;
        long supersededRecoveryTailCleanupCount = 0;
        long lateSameEpochAfterHeadAdvancedDropCount = 0;
        long staleRunwayWindowAbortCount = 0;
        long runwayCandidateExpiredAfterHeadAdvanceCount = 0;
        long runwayFollowersEmittedWithinActionableWindowCount = 0;
        long recoveryOwnerReplacedCount = 0;
        long olderEpochCleanupAfterEpochAdvanceCount = 0;
        long lateFragmentAfterStableVisibleHeadCount = 0;
        long lateFragmentAfterVisibleRecoveryCount = 0;
        long lateFragmentAfterSuccessfulRecoveryCount = 0;
        long recoveryCandidatePresentCount = 0;
        long visibleRecoveryFloorFrameId = -1;
        long stableVisibleHeadFrameId = -1;
        long appliedHeadFrameId = -1;
        long orderedEmitHeadFrameId = -1;
        long winningRecoveryFrameId = -1;
        long futureTailPrunedWhileGapActiveCount = 0;
        long protectedHeadMissingBudgetPressureCount = 0;
        long recoveryKeyframeSupersededOrReplacedCount = 0;
        long orderedEmitBlockedThenResyncedCount = 0;
        long unattributedLossCount = 0;
        long lastAppliedFrameId = -1;
        long lastCleanFrameId = -1;

        foreach (var frame in session.Frames.Values)
        {
            var epoch = GetOrCreateEpoch(epochBuckets, frame.StreamEpoch);

            if (frame.FragmentSeen)
            {
                fragmentSeenFrames++;
                epoch.FragmentSeenFrames++;
            }

            if (frame.Assembled)
            {
                framesAssembled++;
                epoch.FramesAssembled++;
            }

            if (frame.Ready)
            {
                framesReady++;
                epoch.FramesReady++;
            }

            if (frame.Emitted)
            {
                framesEmitted++;
                epoch.FramesEmitted++;
            }

            if (frame.ViewerAccepted)
            {
                viewerAcceptedFrames++;
                epoch.ViewerAcceptedFrames++;
            }

            if (frame.DecodeEnqueued)
            {
                decodeEnqueuedFrames++;
                epoch.DecodeEnqueuedFrames++;
            }

            if (frame.Decoded)
            {
                framesDecoded++;
                epoch.FramesDecoded++;
            }

            if (frame.Applied)
            {
                framesApplied++;
                epoch.FramesApplied++;
                lastAppliedFrameId = Math.Max(lastAppliedFrameId, frame.FrameId);
                lastCleanFrameId = Math.Max(lastCleanFrameId, frame.FrameId);
                epoch.LastAppliedFrameId = Math.Max(epoch.LastAppliedFrameId, frame.FrameId);
                epoch.LastCleanFrameId = Math.Max(epoch.LastCleanFrameId, frame.FrameId);
                continue;
            }

            switch (frame.LossBucket)
            {
                case ScreenShareFrameLossBucket.ReassemblerStaleSuperseded:
                    reassemblerStaleSupersededLossCount++;
                    epoch.ReassemblerStaleSupersededLossCount++;
                    break;
                case ScreenShareFrameLossBucket.AssemblyEvicted:
                    assemblyEvictedLossCount++;
                    epoch.AssemblyEvictedLossCount++;
                    break;
                case ScreenShareFrameLossBucket.ReadyFrameSkippedReplaced:
                    readyFrameSkippedReplacedLossCount++;
                    epoch.ReadyFrameSkippedReplacedLossCount++;
                    break;
                case ScreenShareFrameLossBucket.ViewerRejectedBeforeEnqueue:
                    viewerRejectedBeforeEnqueueCount++;
                    epoch.ViewerRejectedBeforeEnqueueCount++;
                    break;
                case ScreenShareFrameLossBucket.DecodeWorkerDroppedBeforeDecode:
                    decodeWorkerDroppedBeforeDecodeCount++;
                    epoch.DecodeWorkerDroppedBeforeDecodeCount++;
                    break;
                case ScreenShareFrameLossBucket.DecodedFrameReplacedBeforeApply:
                    if (string.Equals(frame.LossReason, "decoded_apply_queue_overflow", StringComparison.Ordinal))
                    {
                        decodedApplyQueueOverflowCount++;
                        epoch.DecodedApplyQueueOverflowCount++;
                    }
                    else
                    {
                        decodedFrameReplacedBeforeApplyCount++;
                        epoch.DecodedFrameReplacedBeforeApplyCount++;
                    }
                    break;
                case ScreenShareFrameLossBucket.DroppedWaitingForRecoveryKeyframe:
                    droppedWaitingForRecoveryKeyframeCount++;
                    epoch.DroppedWaitingForRecoveryKeyframeCount++;
                    break;
                case ScreenShareFrameLossBucket.DecodeFailed:
                    decodeFailedLossCount++;
                    epoch.DecodeFailedLossCount++;
                    break;
                case ScreenShareFrameLossBucket.StaleDroppedAfterDecode:
                    staleDroppedAfterDecodeCount++;
                    epoch.StaleDroppedAfterDecodeCount++;
                    break;
                case ScreenShareFrameLossBucket.None:
                    if (frame.FragmentSeen &&
                        frame.LastUpdatedUtcMs > 0 &&
                        nowUtcMs - frame.LastUpdatedUtcMs >= UnattributedInFlightAgeThresholdMs)
                    {
                        unattributedLossCount++;
                        epoch.UnattributedLossCount++;
                    }
                    break;
            }

            if (string.Equals(frame.LossReason, "gap_non_key_pruned", StringComparison.Ordinal))
            {
                gapNonKeyPrunedCount++;
                epoch.GapNonKeyPrunedCount++;
            }
            else if (string.Equals(frame.LossReason, "future_tail_quarantined_during_gap", StringComparison.Ordinal))
            {
                futureTailQuarantinedDuringGapCount++;
                epoch.FutureTailQuarantinedDuringGapCount++;
            }
            else if (string.Equals(frame.LossReason, "future_tail_quarantined_after_gap", StringComparison.Ordinal) ||
                     string.Equals(frame.LossReason, "recovery_keyframe_buffered_tail_rejected", StringComparison.Ordinal) ||
                     string.Equals(frame.LossReason, "recovery_follower_window_trimmed", StringComparison.Ordinal) ||
                     string.Equals(frame.LossReason, "recovery_runway_overflow", StringComparison.Ordinal))
            {
                futureTailQuarantinedAfterGapCount++;
                epoch.FutureTailQuarantinedAfterGapCount++;
            }
            else if (string.Equals(frame.LossReason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal))
            {
                preCandidateGapTailRejectedCount++;
                epoch.PreCandidateGapTailRejectedCount++;
            }
            else if (string.Equals(frame.LossReason, "superseded_recovery_tail_cleanup", StringComparison.Ordinal))
            {
                supersededRecoveryTailCleanupCount++;
                epoch.SupersededRecoveryTailCleanupCount++;
            }
            else if (string.Equals(frame.LossReason, "late_same_epoch_after_head_advanced_drop", StringComparison.Ordinal))
            {
                lateSameEpochAfterHeadAdvancedDropCount++;
                epoch.LateSameEpochAfterHeadAdvancedDropCount++;
            }
            else if (string.Equals(frame.LossReason, "runway_candidate_expired_after_head_advance", StringComparison.Ordinal))
            {
                runwayCandidateExpiredAfterHeadAdvanceCount++;
                epoch.RunwayCandidateExpiredAfterHeadAdvanceCount++;
            }
            else if (string.Equals(frame.LossReason, "late_fragment_after_stable_visible_head", StringComparison.Ordinal))
            {
                lateFragmentAfterStableVisibleHeadCount++;
                epoch.LateFragmentAfterStableVisibleHeadCount++;
            }
            else if (string.Equals(frame.LossReason, "late_fragment_after_applied_head", StringComparison.Ordinal))
            {
                epoch.LateFragmentAfterAppliedHeadCount++;
            }
            else if (string.Equals(frame.LossReason, "late_fragment_after_ordered_head", StringComparison.Ordinal))
            {
                epoch.LateFragmentAfterOrderedHeadCount++;
            }

            switch (frame.LossReason)
            {
                case "waiting_for_recovery_keyframe":
                    waitingForRecoveryKeyframeRejectCount++;
                    epoch.WaitingForRecoveryKeyframeRejectCount++;
                    break;
                case "recovery_runway_overflow":
                    recoveryRunwayOverflowRejectCount++;
                    epoch.RecoveryRunwayOverflowRejectCount++;
                    break;
                case "suppressed_emit_during_recovery_wait":
                    suppressedEmitDuringRecoveryWaitCount++;
                    epoch.SuppressedEmitDuringRecoveryWaitCount++;
                    break;
                case "blocked_by_reserved_recovery_frame":
                    blockedByReservedRecoveryFrameRejectCount++;
                    epoch.BlockedByReservedRecoveryFrameRejectCount++;
                    break;
                case "older_epoch_ignored_during_recovery_lock":
                    olderEpochIgnoredDuringRecoveryLockCount++;
                    epoch.OlderEpochIgnoredDuringRecoveryLockCount++;
                    break;
                case "newer_epoch_non_key_ignored_during_lock":
                    newerEpochNonKeyIgnoredDuringLockCount++;
                    epoch.NewerEpochNonKeyIgnoredDuringLockCount++;
                    break;
                case "deferred_post_recovery_candidate_replaced":
                    deferredPostRecoveryCandidateReplaceCount++;
                    epoch.DeferredPostRecoveryCandidateReplaceCount++;
                    break;
                case "decode_queue_overflow":
                    decodeQueueOverflowCount++;
                    epoch.DecodeQueueOverflowCount++;
                    break;
                case "decode_age_budget":
                    decodeAgeBudgetCount++;
                    epoch.DecodeAgeBudgetCount++;
                    break;
                case "decode_generation_changed":
                    decodeGenerationChangedCount++;
                    epoch.DecodeGenerationChangedCount++;
                    break;
                case "decode_stopped":
                    decodeStoppedCount++;
                    epoch.DecodeStoppedCount++;
                    break;
                case "decoded_stale_after_recovery":
                    decodedStaleAfterRecoveryCount++;
                    epoch.DecodedStaleAfterRecoveryCount++;
                    break;
                case "decoded_blocked_by_reserved_recovery_frame":
                    decodedBlockedByReservedRecoveryFrameCount++;
                    epoch.DecodedBlockedByReservedRecoveryFrameCount++;
                    break;
                case "decoded_newer_epoch_ignored_during_lock":
                    decodedNewerEpochIgnoredDuringLockCount++;
                    epoch.DecodedNewerEpochIgnoredDuringLockCount++;
                    break;
            }
        }

        foreach (var diagnostics in session.EpochDiagnostics.Values)
        {
            var epoch = GetOrCreateEpoch(epochBuckets, diagnostics.StreamEpoch);
            epoch.RecoveryCandidatePresentCount = diagnostics.RecoveryCandidatePresentCount;
            epoch.VisibleRecoveryFloorFrameId = diagnostics.VisibleRecoveryFloorFrameId;
            epoch.StableVisibleHeadFrameId = diagnostics.StableVisibleHeadFrameId;
            epoch.AppliedHeadFrameId = diagnostics.AppliedHeadFrameId;
            epoch.OrderedEmitHeadFrameId = diagnostics.OrderedEmitHeadFrameId;
            epoch.WinningRecoveryFrameId = diagnostics.WinningRecoveryFrameId;
            epoch.SupersededRecoveryTailCleanupCount = Math.Max(
                epoch.SupersededRecoveryTailCleanupCount,
                diagnostics.SupersededRecoveryTailCleanupCount);
            epoch.LateFragmentAfterAppliedHeadCount = diagnostics.LateFragmentAfterAppliedHeadCount;
            epoch.LateFragmentAfterOrderedHeadCount = diagnostics.LateFragmentAfterOrderedHeadCount;
            epoch.LateFragmentAfterStableVisibleHeadCount = diagnostics.LateFragmentAfterStableVisibleHeadCount;
            epoch.LateFragmentAfterVisibleRecoveryCount = diagnostics.LateFragmentAfterVisibleRecoveryCount;
            epoch.RecoveryOwnerReplacedCount = diagnostics.RecoveryOwnerReplacedCount;
            epoch.OlderEpochCleanupAfterEpochAdvanceCount = diagnostics.OlderEpochCleanupAfterEpochAdvanceCount;
            epoch.StaleRunwayWindowAbortCount = diagnostics.StaleRunwayWindowAbortCount;
            epoch.RunwayFollowersEmittedWithinActionableWindowCount = diagnostics.RunwayFollowersEmittedWithinActionableWindowCount;
            recoveryCandidatePresentCount += diagnostics.RecoveryCandidatePresentCount;
            visibleRecoveryFloorFrameId = Math.Max(visibleRecoveryFloorFrameId, diagnostics.VisibleRecoveryFloorFrameId);
            stableVisibleHeadFrameId = Math.Max(stableVisibleHeadFrameId, diagnostics.StableVisibleHeadFrameId);
            appliedHeadFrameId = Math.Max(appliedHeadFrameId, diagnostics.AppliedHeadFrameId);
            orderedEmitHeadFrameId = Math.Max(orderedEmitHeadFrameId, diagnostics.OrderedEmitHeadFrameId);
            winningRecoveryFrameId = Math.Max(winningRecoveryFrameId, diagnostics.WinningRecoveryFrameId);
            staleRunwayWindowAbortCount += diagnostics.StaleRunwayWindowAbortCount;
            runwayFollowersEmittedWithinActionableWindowCount += diagnostics.RunwayFollowersEmittedWithinActionableWindowCount;
            fragmentGapBeforeAssemblyCount += diagnostics.FragmentGapBeforeAssemblyCount;
            lateFragmentAfterHeadAdvancedCount += diagnostics.LateFragmentAfterHeadAdvancedCount;
            lateFragmentAfterAppliedHeadCount += diagnostics.LateFragmentAfterAppliedHeadCount;
            lateFragmentAfterOrderedHeadCount += diagnostics.LateFragmentAfterOrderedHeadCount;
            recoveryOwnerReplacedCount += diagnostics.RecoveryOwnerReplacedCount;
            olderEpochCleanupAfterEpochAdvanceCount += diagnostics.OlderEpochCleanupAfterEpochAdvanceCount;
            lateFragmentAfterStableVisibleHeadCount += diagnostics.LateFragmentAfterStableVisibleHeadCount;
            lateFragmentAfterVisibleRecoveryCount += diagnostics.LateFragmentAfterVisibleRecoveryCount;
            lateFragmentAfterSuccessfulRecoveryCount += diagnostics.LateFragmentAfterSuccessfulRecoveryCount;
            futureTailPrunedWhileGapActiveCount += diagnostics.FutureTailPrunedWhileGapActiveCount;
            protectedHeadMissingBudgetPressureCount += diagnostics.ProtectedHeadMissingBudgetPressureCount;
            recoveryKeyframeSupersededOrReplacedCount += diagnostics.RecoveryKeyframeSupersededOrReplacedCount;
            orderedEmitBlockedThenResyncedCount += diagnostics.OrderedEmitBlockedThenResyncedCount;
        }

        var epochSnapshots = epochBuckets.Values
            .OrderBy(epoch => epoch.StreamEpoch)
            .Select(epoch => new ScreenShareFrameLossEpochSnapshot(
                StreamEpoch: epoch.StreamEpoch,
                FragmentSeenFrames: epoch.FragmentSeenFrames,
                FramesAssembled: epoch.FramesAssembled,
                FramesReady: epoch.FramesReady,
                FramesEmitted: epoch.FramesEmitted,
                ViewerAcceptedFrames: epoch.ViewerAcceptedFrames,
                DecodeEnqueuedFrames: epoch.DecodeEnqueuedFrames,
                FramesDecoded: epoch.FramesDecoded,
                FramesApplied: epoch.FramesApplied,
                ReassemblerStaleSupersededLossCount: epoch.ReassemblerStaleSupersededLossCount,
                AssemblyEvictedLossCount: epoch.AssemblyEvictedLossCount,
                ReadyFrameSkippedReplacedLossCount: epoch.ReadyFrameSkippedReplacedLossCount,
                ViewerRejectedBeforeEnqueueCount: epoch.ViewerRejectedBeforeEnqueueCount,
                WaitingForRecoveryKeyframeRejectCount: epoch.WaitingForRecoveryKeyframeRejectCount,
                RecoveryRunwayOverflowRejectCount: epoch.RecoveryRunwayOverflowRejectCount,
                SuppressedEmitDuringRecoveryWaitCount: epoch.SuppressedEmitDuringRecoveryWaitCount,
                BlockedByReservedRecoveryFrameRejectCount: epoch.BlockedByReservedRecoveryFrameRejectCount,
                OlderEpochIgnoredDuringRecoveryLockCount: epoch.OlderEpochIgnoredDuringRecoveryLockCount,
                NewerEpochNonKeyIgnoredDuringLockCount: epoch.NewerEpochNonKeyIgnoredDuringLockCount,
                DeferredPostRecoveryCandidateReplaceCount: epoch.DeferredPostRecoveryCandidateReplaceCount,
                DecodeWorkerDroppedBeforeDecodeCount: epoch.DecodeWorkerDroppedBeforeDecodeCount,
                DecodeQueueOverflowCount: epoch.DecodeQueueOverflowCount,
                DecodeAgeBudgetCount: epoch.DecodeAgeBudgetCount,
                DecodeGenerationChangedCount: epoch.DecodeGenerationChangedCount,
                DecodeStoppedCount: epoch.DecodeStoppedCount,
                DecodedApplyQueueOverflowCount: epoch.DecodedApplyQueueOverflowCount,
                DecodedFrameReplacedBeforeApplyCount: epoch.DecodedFrameReplacedBeforeApplyCount,
                DecodedStaleAfterRecoveryCount: epoch.DecodedStaleAfterRecoveryCount,
                DecodedBlockedByReservedRecoveryFrameCount: epoch.DecodedBlockedByReservedRecoveryFrameCount,
                DecodedNewerEpochIgnoredDuringLockCount: epoch.DecodedNewerEpochIgnoredDuringLockCount,
                DroppedWaitingForRecoveryKeyframeCount: epoch.DroppedWaitingForRecoveryKeyframeCount,
                DecodeFailedLossCount: epoch.DecodeFailedLossCount,
                StaleDroppedAfterDecodeCount: epoch.StaleDroppedAfterDecodeCount,
                GapNonKeyPrunedCount: epoch.GapNonKeyPrunedCount,
                FutureTailQuarantinedDuringGapCount: epoch.FutureTailQuarantinedDuringGapCount,
                FutureTailQuarantinedAfterGapCount: epoch.FutureTailQuarantinedAfterGapCount,
                PreCandidateGapTailRejectedCount: epoch.PreCandidateGapTailRejectedCount,
                RecoveryCandidatePresentCount: epoch.RecoveryCandidatePresentCount,
                VisibleRecoveryFloorFrameId: epoch.VisibleRecoveryFloorFrameId,
                StableVisibleHeadFrameId: epoch.StableVisibleHeadFrameId,
                AppliedHeadFrameId: epoch.AppliedHeadFrameId,
                OrderedEmitHeadFrameId: epoch.OrderedEmitHeadFrameId,
                WinningRecoveryFrameId: epoch.WinningRecoveryFrameId,
                SupersededRecoveryTailCleanupCount: epoch.SupersededRecoveryTailCleanupCount,
                LateSameEpochAfterHeadAdvancedDropCount: epoch.LateSameEpochAfterHeadAdvancedDropCount,
                StaleRunwayWindowAbortCount: epoch.StaleRunwayWindowAbortCount,
                RunwayCandidateExpiredAfterHeadAdvanceCount: epoch.RunwayCandidateExpiredAfterHeadAdvanceCount,
                RunwayFollowersEmittedWithinActionableWindowCount: epoch.RunwayFollowersEmittedWithinActionableWindowCount,
                LateFragmentAfterAppliedHeadCount: epoch.LateFragmentAfterAppliedHeadCount,
                LateFragmentAfterOrderedHeadCount: epoch.LateFragmentAfterOrderedHeadCount,
                LateFragmentAfterStableVisibleHeadCount: epoch.LateFragmentAfterStableVisibleHeadCount,
                LateFragmentAfterVisibleRecoveryCount: epoch.LateFragmentAfterVisibleRecoveryCount,
                UnattributedLossCount: epoch.UnattributedLossCount,
                RecoveryOwnerReplacedCount: epoch.RecoveryOwnerReplacedCount,
                OlderEpochCleanupAfterEpochAdvanceCount: epoch.OlderEpochCleanupAfterEpochAdvanceCount,
                DominantHelperAdmissionRejectReason: FormatHelperAdmissionRejectReason(GetDominantHelperAdmissionRejectReason(epoch)),
                LastAppliedFrameId: epoch.LastAppliedFrameId,
                LastCleanFrameId: epoch.LastCleanFrameId,
                RecentLosses: new ReadOnlyCollection<ScreenShareFrameLossBreadcrumb>(
                    session.RecentLosses.Where(loss => loss.StreamEpoch == epoch.StreamEpoch).ToList())))
            .ToArray();

        var epochDiagnostics = session.EpochDiagnostics.Values
            .OrderBy(epoch => epoch.StreamEpoch)
            .Select(epoch => new ScreenShareEpochDiagnosticsSnapshot(
                StreamEpoch: epoch.StreamEpoch,
                LastAppliedFrameId: epoch.LastAppliedFrameId,
                VisibleHeadFrameId: epoch.LastAppliedFrameId,
                AppliedHeadFrameId: epoch.AppliedHeadFrameId,
                OrderedEmitHeadFrameId: epoch.OrderedEmitHeadFrameId,
                WinningRecoveryFrameId: epoch.WinningRecoveryFrameId,
                GapCount: epoch.GapCount,
                RecoveryKeyframeApplyCount: epoch.RecoveryKeyframeApplyCount,
                ResyncCount: epoch.ResyncCount,
                FramesAppliedSinceLastGap: Math.Max(0, epoch.VisibleApplyCount - epoch.VisibleApplyCountAtLastGap),
                TimeToFirstApplyMs: ComputeDuration(epoch.FirstFragmentSeenUtcMs, epoch.FirstCleanFrameAppliedUtcMs),
                TimeFromGapToKeyframeRequestMs: ComputeDuration(epoch.FirstGapDetectedUtcMs, epoch.FirstKeyframeRequestedUtcMs),
                TimeFromGapToRecoveryKeyframeAppliedMs: ComputeDuration(epoch.FirstGapDetectedUtcMs, epoch.FirstRecoveryKeyframeAppliedUtcMs),
                TimeInRecoveryLockMs: ComputeDuration(epoch.FirstRecoveryLockStartedUtcMs, epoch.FirstRecoveryLockClearedUtcMs),
                RecoveryCandidatePresentCount: epoch.RecoveryCandidatePresentCount,
                VisibleRecoveryFloorFrameId: epoch.VisibleRecoveryFloorFrameId,
                StableVisibleHeadFrameId: epoch.StableVisibleHeadFrameId,
                FragmentGapBeforeAssemblyCount: epoch.FragmentGapBeforeAssemblyCount,
                LateFragmentAfterHeadAdvancedCount: epoch.LateFragmentAfterHeadAdvancedCount,
                LateFragmentAfterAppliedHeadCount: epoch.LateFragmentAfterAppliedHeadCount,
                LateFragmentAfterOrderedHeadCount: epoch.LateFragmentAfterOrderedHeadCount,
                SupersededRecoveryTailCleanupCount: epoch.SupersededRecoveryTailCleanupCount,
                RecoveryOwnerReplacedCount: epoch.RecoveryOwnerReplacedCount,
                OlderEpochCleanupAfterEpochAdvanceCount: epoch.OlderEpochCleanupAfterEpochAdvanceCount,
                LateFragmentAfterStableVisibleHeadCount: epoch.LateFragmentAfterStableVisibleHeadCount,
                LateFragmentAfterVisibleRecoveryCount: epoch.LateFragmentAfterVisibleRecoveryCount,
                LateFragmentAfterSuccessfulRecoveryCount: epoch.LateFragmentAfterSuccessfulRecoveryCount,
                SuppressedEmitDuringRecoveryWaitCount: epoch.SuppressedEmitDuringRecoveryWaitCount,
                FutureTailPrunedWhileGapActiveCount: epoch.FutureTailPrunedWhileGapActiveCount,
                ProtectedHeadMissingBudgetPressureCount: epoch.ProtectedHeadMissingBudgetPressureCount,
                RecoveryKeyframeSupersededOrReplacedCount: epoch.RecoveryKeyframeSupersededOrReplacedCount,
                OrderedEmitBlockedThenResyncedCount: epoch.OrderedEmitBlockedThenResyncedCount,
                DominantReassemblerRootCause: FormatRootCause(GetDominantRootCause(epoch)),
                TimelineEvents: new ReadOnlyCollection<ScreenShareEpochContinuityEventSnapshot>(
                    epoch.TimelineEvents
                        .OrderBy(eventSnapshot => eventSnapshot.OccurredUtcMs)
                        .Select(static eventSnapshot => new ScreenShareEpochContinuityEventSnapshot(
                            eventSnapshot.EventName,
                            eventSnapshot.FrameId,
                            eventSnapshot.RelatedFrameId,
                            eventSnapshot.OccurredUtcMs))
                        .ToList()),
                TopLossBursts: new ReadOnlyCollection<ScreenShareReassemblerLossBurstSnapshot>(
                    epoch.LossBursts.Values
                        .OrderByDescending(static burst => burst.LossCount)
                        .ThenByDescending(static burst => burst.ReceivedFrameIdEnd)
                        .Take(10)
                        .Select(static burst => new ScreenShareReassemblerLossBurstSnapshot(
                            RootCause: FormatRootCause(burst.RootCause),
                            ExpectedNextFrameId: burst.ExpectedNextFrameId,
                            ReceivedFrameIdStart: burst.ReceivedFrameIdStart,
                            ReceivedFrameIdEnd: burst.ReceivedFrameIdEnd,
                            FutureNonKeyBufferedCount: burst.FutureNonKeyBufferedCount,
                            BufferedRecoveryKeyframeFrameId: burst.BufferedRecoveryKeyframeFrameId,
                            LossCount: burst.LossCount))
                        .ToList())))
            .ToArray();

        return new ScreenShareFrameLossSessionSnapshot(
            SessionId: session.SessionId,
            FragmentSeenFrames: fragmentSeenFrames,
            FramesAssembled: framesAssembled,
            FramesReady: framesReady,
            FramesEmitted: framesEmitted,
            ViewerAcceptedFrames: viewerAcceptedFrames,
            DecodeEnqueuedFrames: decodeEnqueuedFrames,
            FramesDecoded: framesDecoded,
            FramesApplied: framesApplied,
            ReassemblerStaleSupersededLossCount: reassemblerStaleSupersededLossCount,
            AssemblyEvictedLossCount: assemblyEvictedLossCount,
            ReadyFrameSkippedReplacedLossCount: readyFrameSkippedReplacedLossCount,
            ViewerRejectedBeforeEnqueueCount: viewerRejectedBeforeEnqueueCount,
            WaitingForRecoveryKeyframeRejectCount: waitingForRecoveryKeyframeRejectCount,
            RecoveryRunwayOverflowRejectCount: recoveryRunwayOverflowRejectCount,
            SuppressedEmitDuringRecoveryWaitCount: suppressedEmitDuringRecoveryWaitCount,
            BlockedByReservedRecoveryFrameRejectCount: blockedByReservedRecoveryFrameRejectCount,
            OlderEpochIgnoredDuringRecoveryLockCount: olderEpochIgnoredDuringRecoveryLockCount,
            NewerEpochNonKeyIgnoredDuringLockCount: newerEpochNonKeyIgnoredDuringLockCount,
            DeferredPostRecoveryCandidateReplaceCount: deferredPostRecoveryCandidateReplaceCount,
            DecodeWorkerDroppedBeforeDecodeCount: decodeWorkerDroppedBeforeDecodeCount,
            DecodeQueueOverflowCount: decodeQueueOverflowCount,
            DecodeAgeBudgetCount: decodeAgeBudgetCount,
            DecodeGenerationChangedCount: decodeGenerationChangedCount,
            DecodeStoppedCount: decodeStoppedCount,
            DecodedApplyQueueOverflowCount: decodedApplyQueueOverflowCount,
            DecodedFrameReplacedBeforeApplyCount: decodedFrameReplacedBeforeApplyCount,
            DecodedStaleAfterRecoveryCount: decodedStaleAfterRecoveryCount,
            DecodedBlockedByReservedRecoveryFrameCount: decodedBlockedByReservedRecoveryFrameCount,
            DecodedNewerEpochIgnoredDuringLockCount: decodedNewerEpochIgnoredDuringLockCount,
            DroppedWaitingForRecoveryKeyframeCount: droppedWaitingForRecoveryKeyframeCount,
            DecodeFailedLossCount: decodeFailedLossCount,
            StaleDroppedAfterDecodeCount: staleDroppedAfterDecodeCount,
            ReassemblerLossCount: reassemblerStaleSupersededLossCount + assemblyEvictedLossCount + readyFrameSkippedReplacedLossCount,
            EnqueueRejectCount: viewerRejectedBeforeEnqueueCount,
            DecodeWorkerDropCount: decodeWorkerDroppedBeforeDecodeCount,
            PostDecodeDropCount: decodedApplyQueueOverflowCount + decodedFrameReplacedBeforeApplyCount + staleDroppedAfterDecodeCount,
            GapNonKeyPrunedCount: gapNonKeyPrunedCount,
            FutureTailQuarantinedDuringGapCount: futureTailQuarantinedDuringGapCount,
            FutureTailQuarantinedAfterGapCount: futureTailQuarantinedAfterGapCount,
            PreCandidateGapTailRejectedCount: preCandidateGapTailRejectedCount,
            RecoveryKeyframeResyncCount: session.RecoveryKeyframeResyncCount,
            RecoveryCandidatePresentCount: recoveryCandidatePresentCount,
            VisibleRecoveryFloorFrameId: visibleRecoveryFloorFrameId,
            StableVisibleHeadFrameId: stableVisibleHeadFrameId,
            AppliedHeadFrameId: appliedHeadFrameId,
            OrderedEmitHeadFrameId: orderedEmitHeadFrameId,
            WinningRecoveryFrameId: winningRecoveryFrameId,
            FragmentGapBeforeAssemblyCount: fragmentGapBeforeAssemblyCount,
            LateFragmentAfterHeadAdvancedCount: lateFragmentAfterHeadAdvancedCount,
            LateFragmentAfterAppliedHeadCount: lateFragmentAfterAppliedHeadCount,
            LateFragmentAfterOrderedHeadCount: lateFragmentAfterOrderedHeadCount,
            SupersededRecoveryTailCleanupCount: supersededRecoveryTailCleanupCount,
            LateSameEpochAfterHeadAdvancedDropCount: lateSameEpochAfterHeadAdvancedDropCount,
            StaleRunwayWindowAbortCount: staleRunwayWindowAbortCount,
            RunwayCandidateExpiredAfterHeadAdvanceCount: runwayCandidateExpiredAfterHeadAdvanceCount,
            RunwayFollowersEmittedWithinActionableWindowCount: runwayFollowersEmittedWithinActionableWindowCount,
            RecoveryOwnerReplacedCount: recoveryOwnerReplacedCount,
            OlderEpochCleanupAfterEpochAdvanceCount: olderEpochCleanupAfterEpochAdvanceCount,
            LateFragmentAfterStableVisibleHeadCount: lateFragmentAfterStableVisibleHeadCount,
            LateFragmentAfterVisibleRecoveryCount: lateFragmentAfterVisibleRecoveryCount,
            LateFragmentAfterSuccessfulRecoveryCount: lateFragmentAfterSuccessfulRecoveryCount,
            FutureTailPrunedWhileGapActiveCount: futureTailPrunedWhileGapActiveCount,
            ProtectedHeadMissingBudgetPressureCount: protectedHeadMissingBudgetPressureCount,
            RecoveryKeyframeSupersededOrReplacedCount: recoveryKeyframeSupersededOrReplacedCount,
            OrderedEmitBlockedThenResyncedCount: orderedEmitBlockedThenResyncedCount,
            DominantReassemblerRootCause: FormatRootCause(GetDominantRootCause(
                fragmentGapBeforeAssemblyCount,
                lateFragmentAfterHeadAdvancedCount,
                futureTailPrunedWhileGapActiveCount,
                protectedHeadMissingBudgetPressureCount,
                recoveryKeyframeSupersededOrReplacedCount,
                orderedEmitBlockedThenResyncedCount)),
            DominantHelperAdmissionRejectReason: FormatHelperAdmissionRejectReason(GetDominantHelperAdmissionRejectReason(
                waitingForRecoveryKeyframeRejectCount,
                recoveryRunwayOverflowRejectCount,
                blockedByReservedRecoveryFrameRejectCount,
                olderEpochIgnoredDuringRecoveryLockCount,
                newerEpochNonKeyIgnoredDuringLockCount,
                deferredPostRecoveryCandidateReplaceCount)),
            UnattributedLossCount: unattributedLossCount,
            GapActive: session.GapActive,
            GapExpectedFrameId: session.GapExpectedFrameId,
            BufferedRecoveryKeyframeFrameId: session.BufferedRecoveryKeyframeFrameId,
            FutureNonKeyBufferedCount: session.FutureNonKeyBufferedCount,
            LastAppliedFrameId: lastAppliedFrameId,
            LastCleanFrameId: lastCleanFrameId,
            RecentLosses: new ReadOnlyCollection<ScreenShareFrameLossBreadcrumb>(session.RecentLosses.ToList()),
            EpochSnapshots: epochSnapshots,
            EpochDiagnostics: epochDiagnostics);
    }

    private static EpochAccumulator GetOrCreateEpoch(Dictionary<long, EpochAccumulator> epochs, long streamEpoch)
    {
        if (epochs.TryGetValue(streamEpoch, out var epoch))
        {
            return epoch;
        }

        epoch = new EpochAccumulator(streamEpoch);
        epochs.Add(streamEpoch, epoch);
        return epoch;
    }

    private static EpochDiagnosticsState GetOrCreateEpochDiagnosticsState(SessionState session, long streamEpoch)
    {
        if (session.EpochDiagnostics.TryGetValue(streamEpoch, out var epochDiagnostics))
        {
            return epochDiagnostics;
        }

        epochDiagnostics = new EpochDiagnosticsState(streamEpoch);
        session.EpochDiagnostics.Add(streamEpoch, epochDiagnostics);
        return epochDiagnostics;
    }

    private static void NoteTimelineEvent(
        EpochDiagnosticsState epochDiagnostics,
        string eventName,
        long frameId,
        long relatedFrameId,
        long? occurredUtcMs = null)
    {
        var timestampUtcMs = occurredUtcMs.GetValueOrDefault(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        epochDiagnostics.TimelineEvents.Add(
            new TimelineEventState(
                Sanitize(eventName),
                frameId,
                relatedFrameId,
                timestampUtcMs));
        while (epochDiagnostics.TimelineEvents.Count > 32)
        {
            epochDiagnostics.TimelineEvents.RemoveAt(0);
        }

        switch (eventName)
        {
            case "gap_detected":
                epochDiagnostics.GapCount++;
                epochDiagnostics.VisibleApplyCountAtLastGap = epochDiagnostics.VisibleApplyCount;
                if (epochDiagnostics.FirstGapDetectedUtcMs <= 0)
                {
                    epochDiagnostics.FirstGapDetectedUtcMs = timestampUtcMs;
                }
                break;
            case "keyframe_requested":
                if (epochDiagnostics.FirstKeyframeRequestedUtcMs <= 0)
                {
                    epochDiagnostics.FirstKeyframeRequestedUtcMs = timestampUtcMs;
                }
                break;
            case "recovery_lock_started":
                if (epochDiagnostics.FirstRecoveryLockStartedUtcMs <= 0)
                {
                    epochDiagnostics.FirstRecoveryLockStartedUtcMs = timestampUtcMs;
                }
                break;
            case "recovery_lock_cleared":
                if (epochDiagnostics.FirstRecoveryLockClearedUtcMs <= 0)
                {
                    epochDiagnostics.FirstRecoveryLockClearedUtcMs = timestampUtcMs;
                }
                break;
            case "recovery_keyframe_applied":
                epochDiagnostics.RecoveryKeyframeApplyCount++;
                if (epochDiagnostics.FirstRecoveryKeyframeAppliedUtcMs <= 0)
                {
                    epochDiagnostics.FirstRecoveryKeyframeAppliedUtcMs = timestampUtcMs;
                }
                break;
            case "resync_triggered":
                if (epochDiagnostics.FirstResyncTriggeredUtcMs <= 0)
                {
                    epochDiagnostics.FirstResyncTriggeredUtcMs = timestampUtcMs;
                }
                break;
        }
    }

    private static void IncrementRootCause(EpochDiagnosticsState diagnostics, ScreenShareReassemblerRootCauseBucket rootCause)
    {
        switch (rootCause)
        {
            case ScreenShareReassemblerRootCauseBucket.FragmentGapBeforeAssembly:
                diagnostics.FragmentGapBeforeAssemblyCount++;
                break;
            case ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced:
                diagnostics.LateFragmentAfterHeadAdvancedCount++;
                break;
            case ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive:
                diagnostics.FutureTailPrunedWhileGapActiveCount++;
                break;
            case ScreenShareReassemblerRootCauseBucket.ProtectedHeadMissingBudgetPressure:
                diagnostics.ProtectedHeadMissingBudgetPressureCount++;
                break;
            case ScreenShareReassemblerRootCauseBucket.RecoveryKeyframeSupersededOrReplaced:
                diagnostics.RecoveryKeyframeSupersededOrReplacedCount++;
                break;
            case ScreenShareReassemblerRootCauseBucket.OrderedEmitBlockedThenResynced:
                diagnostics.OrderedEmitBlockedThenResyncedCount++;
                break;
        }
    }

    private static ScreenShareReassemblerRootCauseBucket GetDominantRootCause(EpochDiagnosticsState diagnostics)
        => GetDominantRootCause(
            diagnostics.FragmentGapBeforeAssemblyCount,
            diagnostics.LateFragmentAfterHeadAdvancedCount,
            diagnostics.FutureTailPrunedWhileGapActiveCount,
            diagnostics.ProtectedHeadMissingBudgetPressureCount,
            diagnostics.RecoveryKeyframeSupersededOrReplacedCount,
            diagnostics.OrderedEmitBlockedThenResyncedCount);

    private static ScreenShareReassemblerRootCauseBucket GetDominantRootCause(
        long fragmentGapBeforeAssemblyCount,
        long lateFragmentAfterHeadAdvancedCount,
        long futureTailPrunedWhileGapActiveCount,
        long protectedHeadMissingBudgetPressureCount,
        long recoveryKeyframeSupersededOrReplacedCount,
        long orderedEmitBlockedThenResyncedCount)
    {
        var values = new[]
        {
            (Bucket: ScreenShareReassemblerRootCauseBucket.FragmentGapBeforeAssembly, Count: fragmentGapBeforeAssemblyCount),
            (Bucket: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced, Count: lateFragmentAfterHeadAdvancedCount),
            (Bucket: ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive, Count: futureTailPrunedWhileGapActiveCount),
            (Bucket: ScreenShareReassemblerRootCauseBucket.ProtectedHeadMissingBudgetPressure, Count: protectedHeadMissingBudgetPressureCount),
            (Bucket: ScreenShareReassemblerRootCauseBucket.RecoveryKeyframeSupersededOrReplaced, Count: recoveryKeyframeSupersededOrReplacedCount),
            (Bucket: ScreenShareReassemblerRootCauseBucket.OrderedEmitBlockedThenResynced, Count: orderedEmitBlockedThenResyncedCount),
        };

        var best = values
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => (int)item.Bucket)
            .FirstOrDefault();

        return best.Count > 0 ? best.Bucket : ScreenShareReassemblerRootCauseBucket.None;
    }

    private static string FormatRootCause(ScreenShareReassemblerRootCauseBucket rootCause)
    {
        return rootCause switch
        {
            ScreenShareReassemblerRootCauseBucket.FragmentGapBeforeAssembly => "fragment_gap_before_assembly",
            ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced => "late_fragment_after_head_advanced",
            ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive => "future_tail_pruned_while_gap_active",
            ScreenShareReassemblerRootCauseBucket.ProtectedHeadMissingBudgetPressure => "protected_head_missing_budget_pressure",
            ScreenShareReassemblerRootCauseBucket.RecoveryKeyframeSupersededOrReplaced => "recovery_keyframe_superseded_or_replaced",
            ScreenShareReassemblerRootCauseBucket.OrderedEmitBlockedThenResynced => "ordered_emit_blocked_then_resynced",
            _ => "none",
        };
    }

    private static string FormatHelperAdmissionRejectReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "none" : reason.Trim();
    }

    private static string GetDominantHelperAdmissionRejectReason(EpochAccumulator epoch)
        => GetDominantHelperAdmissionRejectReason(
            epoch.WaitingForRecoveryKeyframeRejectCount,
            epoch.RecoveryRunwayOverflowRejectCount,
            epoch.BlockedByReservedRecoveryFrameRejectCount,
            epoch.OlderEpochIgnoredDuringRecoveryLockCount,
            epoch.NewerEpochNonKeyIgnoredDuringLockCount,
            epoch.DeferredPostRecoveryCandidateReplaceCount);

    private static string GetDominantHelperAdmissionRejectReason(
        long waitingForRecoveryKeyframeRejectCount,
        long recoveryRunwayOverflowRejectCount,
        long blockedByReservedRecoveryFrameRejectCount,
        long olderEpochIgnoredDuringRecoveryLockCount,
        long newerEpochNonKeyIgnoredDuringLockCount,
        long deferredPostRecoveryCandidateReplaceCount)
    {
        var best = new[]
        {
            (Reason: "waiting_for_recovery_keyframe", Count: waitingForRecoveryKeyframeRejectCount),
            (Reason: "recovery_runway_overflow", Count: recoveryRunwayOverflowRejectCount),
            (Reason: "blocked_by_reserved_recovery_frame", Count: blockedByReservedRecoveryFrameRejectCount),
            (Reason: "older_epoch_ignored_during_recovery_lock", Count: olderEpochIgnoredDuringRecoveryLockCount),
            (Reason: "newer_epoch_non_key_ignored_during_lock", Count: newerEpochNonKeyIgnoredDuringLockCount),
            (Reason: "deferred_post_recovery_candidate_replaced", Count: deferredPostRecoveryCandidateReplaceCount),
        }
        .OrderByDescending(static candidate => candidate.Count)
        .ThenBy(static candidate => candidate.Reason, StringComparer.Ordinal)
        .FirstOrDefault();

        return best.Count > 0 ? best.Reason : "none";
    }

    private static long ComputeDuration(long startUtcMs, long endUtcMs)
    {
        return startUtcMs > 0 && endUtcMs > 0 && endUtcMs >= startUtcMs
            ? endUtcMs - startUtcMs
            : -1;
    }

    private static List<long> CollectNonNegativeDurations(IEnumerable<long> values)
        => values.Where(static value => value >= 0).ToList();

    private static LatencySummary ComputeLatencySummary(IReadOnlyCollection<long> values)
    {
        if (values.Count == 0)
        {
            return LatencySummary.Empty;
        }

        var ordered = values.OrderBy(static value => value).ToArray();
        long total = 0;
        foreach (var value in ordered)
        {
            total += value;
        }

        return new LatencySummary(
            AvgMs: ordered.Length > 0 ? total / ordered.Length : 0,
            MedianMs: GetMedianLong(ordered),
            P95Ms: GetPercentileLong(ordered, 0.95),
            MaxMs: ordered[^1]);
    }

    private static long GetMedianLong(IReadOnlyList<long> ordered)
    {
        if (ordered.Count == 0)
        {
            return 0;
        }

        var middle = ordered.Count / 2;
        if ((ordered.Count % 2) == 1)
        {
            return ordered[middle];
        }

        return (ordered[middle - 1] + ordered[middle]) / 2;
    }

    private static long GetPercentileLong(IReadOnlyList<long> ordered, double percentile)
    {
        if (ordered.Count == 0)
        {
            return 0;
        }

        var clamped = Math.Min(1d, Math.Max(0d, percentile));
        var index = (int)Math.Ceiling(clamped * ordered.Count) - 1;
        if (index < 0)
        {
            index = 0;
        }
        else if (index >= ordered.Count)
        {
            index = ordered.Count - 1;
        }

        return ordered[index];
    }

    private static string DetermineDominantUpstreamLatencyStage(
        LatencySummary captureToFrameReady,
        LatencySummary frameReadyToViewerAccept,
        LatencySummary viewerAcceptToDecodeEnqueue,
        LatencySummary decodeEnqueueToDecodeStart,
        LatencySummary captureToDecodeStart)
    {
        _ = captureToDecodeStart;
        var candidates = new[]
        {
            (Stage: "capture_to_frame_ready", AvgMs: captureToFrameReady.AvgMs, MaxMs: captureToFrameReady.MaxMs, Order: 0),
            (Stage: "frame_ready_to_viewer_accept", AvgMs: frameReadyToViewerAccept.AvgMs, MaxMs: frameReadyToViewerAccept.MaxMs, Order: 1),
            (Stage: "viewer_accept_to_decode_enqueue", AvgMs: viewerAcceptToDecodeEnqueue.AvgMs, MaxMs: viewerAcceptToDecodeEnqueue.MaxMs, Order: 2),
            (Stage: "decode_enqueue_to_decode_start", AvgMs: decodeEnqueueToDecodeStart.AvgMs, MaxMs: decodeEnqueueToDecodeStart.MaxMs, Order: 3),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.AvgMs)
            .ThenByDescending(static candidate => candidate.MaxMs)
            .ThenBy(static candidate => candidate.Order)
            .FirstOrDefault();

        return best.AvgMs > 0 ? best.Stage : "none";
    }

    private static string DetermineDominantReadyPathStage(
        LatencySummary captureToFirstFragmentObserved,
        LatencySummary firstFragmentToLastFragmentObserved,
        LatencySummary lastFragmentToAssemblyComplete,
        LatencySummary assemblyCompleteToFrameEmitted)
    {
        var candidates = new[]
        {
            (Stage: "capture_to_first_fragment_observed", AvgMs: captureToFirstFragmentObserved.AvgMs, MaxMs: captureToFirstFragmentObserved.MaxMs, Order: 0),
            (Stage: "first_fragment_to_last_fragment_observed", AvgMs: firstFragmentToLastFragmentObserved.AvgMs, MaxMs: firstFragmentToLastFragmentObserved.MaxMs, Order: 1),
            (Stage: "last_fragment_to_assembly_complete", AvgMs: lastFragmentToAssemblyComplete.AvgMs, MaxMs: lastFragmentToAssemblyComplete.MaxMs, Order: 2),
            (Stage: "assembly_complete_to_frame_emitted", AvgMs: assemblyCompleteToFrameEmitted.AvgMs, MaxMs: assemblyCompleteToFrameEmitted.MaxMs, Order: 3),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.AvgMs)
            .ThenByDescending(static candidate => candidate.MaxMs)
            .ThenBy(static candidate => candidate.Order)
            .FirstOrDefault();

        return best.AvgMs > 0 ? best.Stage : "none";
    }

    private static string DetermineDominantReceivePathStage(
        LatencySummary captureToEnvelopeSend,
        LatencySummary envelopeSendToBridgeIngress,
        LatencySummary bridgeIngressToEnvelopeParsed,
        LatencySummary envelopeParsedToSecureDecrypt,
        LatencySummary secureDecryptToFragmentDeserialize,
        LatencySummary fragmentDeserializeToFirstFragmentObserved)
    {
        var candidates = new[]
        {
            (Stage: "capture_to_envelope_send", AvgMs: captureToEnvelopeSend.AvgMs, MaxMs: captureToEnvelopeSend.MaxMs, Order: 0),
            (Stage: "envelope_send_to_bridge_ingress", AvgMs: envelopeSendToBridgeIngress.AvgMs, MaxMs: envelopeSendToBridgeIngress.MaxMs, Order: 1),
            (Stage: "bridge_ingress_to_envelope_parsed", AvgMs: bridgeIngressToEnvelopeParsed.AvgMs, MaxMs: bridgeIngressToEnvelopeParsed.MaxMs, Order: 2),
            (Stage: "envelope_parsed_to_secure_decrypt", AvgMs: envelopeParsedToSecureDecrypt.AvgMs, MaxMs: envelopeParsedToSecureDecrypt.MaxMs, Order: 3),
            (Stage: "secure_decrypt_to_fragment_deserialize", AvgMs: secureDecryptToFragmentDeserialize.AvgMs, MaxMs: secureDecryptToFragmentDeserialize.MaxMs, Order: 4),
            (Stage: "fragment_deserialize_to_first_fragment_observed", AvgMs: fragmentDeserializeToFirstFragmentObserved.AvgMs, MaxMs: fragmentDeserializeToFirstFragmentObserved.MaxMs, Order: 5),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.AvgMs)
            .ThenByDescending(static candidate => candidate.MaxMs)
            .ThenBy(static candidate => candidate.Order)
            .FirstOrDefault();

        return best.AvgMs > 0 ? best.Stage : "none";
    }

    private static string DetermineDominantBridgeIngressStage(
        LatencySummary envelopeSendToBridgeMessageObserved,
        LatencySummary bridgeMessageObservedToBinaryFrameDecoded,
        LatencySummary binaryFrameDecodedToBridgeIngress)
    {
        var candidates = new[]
        {
            (Stage: "envelope_send_to_bridge_message_observed", AvgMs: envelopeSendToBridgeMessageObserved.AvgMs, MaxMs: envelopeSendToBridgeMessageObserved.MaxMs, Order: 0),
            (Stage: "bridge_message_observed_to_binary_frame_decoded", AvgMs: bridgeMessageObservedToBinaryFrameDecoded.AvgMs, MaxMs: bridgeMessageObservedToBinaryFrameDecoded.MaxMs, Order: 1),
            (Stage: "binary_frame_decoded_to_bridge_ingress", AvgMs: binaryFrameDecodedToBridgeIngress.AvgMs, MaxMs: binaryFrameDecodedToBridgeIngress.MaxMs, Order: 2),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.AvgMs)
            .ThenByDescending(static candidate => candidate.MaxMs)
            .ThenBy(static candidate => candidate.Order)
            .FirstOrDefault();

        return best.AvgMs > 0 ? best.Stage : "none";
    }

    private static string DetermineDominantNknReceiveStage(
        LatencySummary envelopeSendToSdkHandleMsgEntered,
        LatencySummary sdkHandleMsgEnteredToClientMessageDispatch,
        LatencySummary clientMessageDispatchToMultiClientMessageDispatch,
        LatencySummary multiClientMessageDispatchToBridgeMessageObserved)
    {
        var candidates = new[]
        {
            (Stage: "envelope_send_to_sdk_handle_msg_entered", AvgMs: envelopeSendToSdkHandleMsgEntered.AvgMs, MaxMs: envelopeSendToSdkHandleMsgEntered.MaxMs, Order: 0),
            (Stage: "sdk_handle_msg_entered_to_client_message_dispatch", AvgMs: sdkHandleMsgEnteredToClientMessageDispatch.AvgMs, MaxMs: sdkHandleMsgEnteredToClientMessageDispatch.MaxMs, Order: 1),
            (Stage: "client_message_dispatch_to_multiclient_message_dispatch", AvgMs: clientMessageDispatchToMultiClientMessageDispatch.AvgMs, MaxMs: clientMessageDispatchToMultiClientMessageDispatch.MaxMs, Order: 2),
            (Stage: "multiclient_message_dispatch_to_bridge_message_observed", AvgMs: multiClientMessageDispatchToBridgeMessageObserved.AvgMs, MaxMs: multiClientMessageDispatchToBridgeMessageObserved.MaxMs, Order: 3),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.AvgMs)
            .ThenByDescending(static candidate => candidate.MaxMs)
            .ThenBy(static candidate => candidate.Order)
            .FirstOrDefault();

        return best.AvgMs > 0 ? best.Stage : "none";
    }

    private static string DetermineDominantWsReceiveStage(
        LatencySummary envelopeSendToWsReceiverWriteEntered,
        LatencySummary wsReceiverWriteEnteredToWsMessageEmitted,
        LatencySummary wsMessageEmittedToSdkHandleMsgEntered)
    {
        var candidates = new[]
        {
            (Stage: "envelope_send_to_ws_receiver_write_entered", AvgMs: envelopeSendToWsReceiverWriteEntered.AvgMs, MaxMs: envelopeSendToWsReceiverWriteEntered.MaxMs, Order: 0),
            (Stage: "ws_receiver_write_entered_to_ws_message_emitted", AvgMs: wsReceiverWriteEnteredToWsMessageEmitted.AvgMs, MaxMs: wsReceiverWriteEnteredToWsMessageEmitted.MaxMs, Order: 1),
            (Stage: "ws_message_emitted_to_sdk_handle_msg_entered", AvgMs: wsMessageEmittedToSdkHandleMsgEntered.AvgMs, MaxMs: wsMessageEmittedToSdkHandleMsgEntered.MaxMs, Order: 2),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.AvgMs)
            .ThenByDescending(static candidate => candidate.MaxMs)
            .ThenBy(static candidate => candidate.Order)
            .FirstOrDefault();

        return best.AvgMs > 0 ? best.Stage : "none";
    }

    private static string DetermineDominantSocketReceiveStage(
        LatencySummary envelopeSendToSocketDataEventEmitted,
        LatencySummary socketDataEventEmittedToWsReceiverWriteEntered)
    {
        var candidates = new[]
        {
            (Stage: "envelope_send_to_socket_data_event_emitted", AvgMs: envelopeSendToSocketDataEventEmitted.AvgMs, MaxMs: envelopeSendToSocketDataEventEmitted.MaxMs, Order: 0),
            (Stage: "socket_data_event_emitted_to_ws_receiver_write_entered", AvgMs: socketDataEventEmittedToWsReceiverWriteEntered.AvgMs, MaxMs: socketDataEventEmittedToWsReceiverWriteEntered.MaxMs, Order: 1),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.AvgMs)
            .ThenByDescending(static candidate => candidate.MaxMs)
            .ThenBy(static candidate => candidate.Order)
            .FirstOrDefault();

        return best.AvgMs > 0 ? best.Stage : "none";
    }

    private static void ApplyStage(FrameState frame, FrameLifecycleStage stage)
    {
        frame.LastStage = stage;

        switch (stage)
        {
            case FrameLifecycleStage.FragmentSeen:
                frame.FragmentSeen = true;
                break;
            case FrameLifecycleStage.Assembled:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                break;
            case FrameLifecycleStage.Ready:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                break;
            case FrameLifecycleStage.Emitted:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                break;
            case FrameLifecycleStage.ViewerAccepted:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                frame.ViewerAccepted = true;
                break;
            case FrameLifecycleStage.ViewerRejectedBeforeEnqueue:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                break;
            case FrameLifecycleStage.DecodeEnqueued:
            case FrameLifecycleStage.DecodeWorkerDroppedBeforeDecode:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                frame.ViewerAccepted = true;
                frame.DecodeEnqueued = true;
                break;
            case FrameLifecycleStage.DecodeStarted:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                frame.ViewerAccepted = true;
                frame.DecodeEnqueued = true;
                break;
            case FrameLifecycleStage.Decoded:
            case FrameLifecycleStage.DecodedFrameReplacedBeforeApply:
            case FrameLifecycleStage.StaleDroppedAfterDecode:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                frame.ViewerAccepted = true;
                frame.DecodeEnqueued = true;
                frame.Decoded = true;
                break;
            case FrameLifecycleStage.DecodeFailed:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                frame.ViewerAccepted = true;
                frame.DecodeEnqueued = true;
                break;
            case FrameLifecycleStage.DroppedWaitingForRecoveryKeyframe:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                break;
            case FrameLifecycleStage.Applied:
                frame.FragmentSeen = true;
                frame.Assembled = true;
                frame.Ready = true;
                frame.Emitted = true;
                frame.ViewerAccepted = true;
                frame.DecodeEnqueued = true;
                frame.Decoded = true;
                break;
        }
    }

    private static string Sanitize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "(none)"
            : value.Replace(';', ',').Trim();
    }

    private enum FrameLifecycleStage
    {
        None = 0,
        FragmentSeen,
        Assembled,
        Ready,
        Emitted,
        ViewerAccepted,
        ViewerRejectedBeforeEnqueue,
        DecodeEnqueued,
        DecodeStarted,
        DecodeWorkerDroppedBeforeDecode,
        Decoded,
        DecodedFrameReplacedBeforeApply,
        DecodeFailed,
        DroppedWaitingForRecoveryKeyframe,
        StaleDroppedAfterDecode,
        Applied,
        ReassemblerLoss,
    }

    private readonly record struct FrameKey(long StreamEpoch, long FrameId);

    private sealed class SessionState
    {
        public SessionState(string sessionId)
        {
            SessionId = sessionId;
        }

        public string SessionId { get; }
        public Dictionary<FrameKey, FrameState> Frames { get; } = new();
        public Dictionary<long, EpochDiagnosticsState> EpochDiagnostics { get; } = new();
        public LinkedList<ScreenShareFrameLossBreadcrumb> RecentLosses { get; } = new();
        public LinkedListNode<string>? OrderNode { get; set; }
        public long GapStateStreamEpoch { get; set; }
        public bool GapActive { get; set; }
        public long GapExpectedFrameId { get; set; } = -1;
        public long BufferedRecoveryKeyframeFrameId { get; set; } = -1;
        public int FutureNonKeyBufferedCount { get; set; }
        public long RecoveryKeyframeResyncCount { get; set; }
    }

    private sealed class FrameState
    {
        public FrameState(long streamEpoch, long frameId, bool isKeyFrame)
        {
            StreamEpoch = streamEpoch;
            FrameId = frameId;
            IsKeyFrame = isKeyFrame;
        }

        public long StreamEpoch { get; }
        public long FrameId { get; }
        public bool IsKeyFrame { get; set; }
        public FrameLifecycleStage LastStage { get; set; }
        public bool FragmentSeen { get; set; }
        public bool Assembled { get; set; }
        public bool Ready { get; set; }
        public bool Emitted { get; set; }
        public bool ViewerAccepted { get; set; }
        public bool DecodeEnqueued { get; set; }
        public bool Decoded { get; set; }
        public ScreenShareFrameLossBucket LossBucket { get; set; }
        public string LossReason { get; set; } = string.Empty;
        public long RelatedFrameId { get; set; } = -1;
        public bool Applied { get; set; }
        public long CapturedTsUtcMs { get; set; }
        public long EnvelopeSendUtcMs { get; set; }
        public long SocketDataEventEmittedUtcMs { get; set; }
        public long WsReceiverWriteEnteredUtcMs { get; set; }
        public long WsMessageEmittedUtcMs { get; set; }
        public long SdkHandleMsgEnteredUtcMs { get; set; }
        public long ClientMessageDispatchUtcMs { get; set; }
        public long MultiClientMessageDispatchUtcMs { get; set; }
        public long BridgeMessageObservedUtcMs { get; set; }
        public long BinaryFrameDecodedUtcMs { get; set; }
        public long BridgeIngressObservedUtcMs { get; set; }
        public long EnvelopeParsedUtcMs { get; set; }
        public long SecureDecryptCompletedUtcMs { get; set; }
        public long FragmentEnvelopeDeserializedUtcMs { get; set; }
        public long FirstFragmentObservedUtcMs { get; set; }
        public long LastFragmentObservedUtcMs { get; set; }
        public long FrameReadyObservedUtcMs { get; set; }
        public long EmittedUtcMs { get; set; }
        public long ViewerAcceptedUtcMs { get; set; }
        public long DecodeEnqueuedUtcMs { get; set; }
        public long DecodeStartedUtcMs { get; set; }
        public long DecodeCompletedUtcMs { get; set; }
        public long AppliedUtcMs { get; set; }
        public long LastUpdatedUtcMs { get; set; }
    }

    private sealed class EpochAccumulator
    {
        public EpochAccumulator(long streamEpoch)
        {
            StreamEpoch = streamEpoch;
        }

        public long StreamEpoch { get; }
        public long FragmentSeenFrames { get; set; }
        public long FramesAssembled { get; set; }
        public long FramesReady { get; set; }
        public long FramesEmitted { get; set; }
        public long ViewerAcceptedFrames { get; set; }
        public long DecodeEnqueuedFrames { get; set; }
        public long FramesDecoded { get; set; }
        public long FramesApplied { get; set; }
        public long ReassemblerStaleSupersededLossCount { get; set; }
        public long AssemblyEvictedLossCount { get; set; }
        public long ReadyFrameSkippedReplacedLossCount { get; set; }
        public long ViewerRejectedBeforeEnqueueCount { get; set; }
        public long WaitingForRecoveryKeyframeRejectCount { get; set; }
        public long RecoveryRunwayOverflowRejectCount { get; set; }
        public long SuppressedEmitDuringRecoveryWaitCount { get; set; }
        public long BlockedByReservedRecoveryFrameRejectCount { get; set; }
        public long OlderEpochIgnoredDuringRecoveryLockCount { get; set; }
        public long NewerEpochNonKeyIgnoredDuringLockCount { get; set; }
        public long DeferredPostRecoveryCandidateReplaceCount { get; set; }
        public long DecodeWorkerDroppedBeforeDecodeCount { get; set; }
        public long DecodeQueueOverflowCount { get; set; }
        public long DecodeAgeBudgetCount { get; set; }
        public long DecodeGenerationChangedCount { get; set; }
        public long DecodeStoppedCount { get; set; }
        public long DecodedApplyQueueOverflowCount { get; set; }
        public long DecodedFrameReplacedBeforeApplyCount { get; set; }
        public long DecodedStaleAfterRecoveryCount { get; set; }
        public long DecodedBlockedByReservedRecoveryFrameCount { get; set; }
        public long DecodedNewerEpochIgnoredDuringLockCount { get; set; }
        public long DroppedWaitingForRecoveryKeyframeCount { get; set; }
        public long DecodeFailedLossCount { get; set; }
        public long StaleDroppedAfterDecodeCount { get; set; }
        public long GapNonKeyPrunedCount { get; set; }
        public long FutureTailQuarantinedDuringGapCount { get; set; }
        public long FutureTailQuarantinedAfterGapCount { get; set; }
        public long PreCandidateGapTailRejectedCount { get; set; }
        public long RecoveryCandidatePresentCount { get; set; }
        public long VisibleRecoveryFloorFrameId { get; set; } = -1;
        public long StableVisibleHeadFrameId { get; set; } = -1;
        public long AppliedHeadFrameId { get; set; } = -1;
        public long OrderedEmitHeadFrameId { get; set; } = -1;
        public long WinningRecoveryFrameId { get; set; } = -1;
        public long SupersededRecoveryTailCleanupCount { get; set; }
        public long LateSameEpochAfterHeadAdvancedDropCount { get; set; }
        public long StaleRunwayWindowAbortCount { get; set; }
        public long RunwayCandidateExpiredAfterHeadAdvanceCount { get; set; }
        public long RunwayFollowersEmittedWithinActionableWindowCount { get; set; }
        public long LateFragmentAfterAppliedHeadCount { get; set; }
        public long LateFragmentAfterOrderedHeadCount { get; set; }
        public long LateFragmentAfterStableVisibleHeadCount { get; set; }
        public long LateFragmentAfterVisibleRecoveryCount { get; set; }
        public long UnattributedLossCount { get; set; }
        public long RecoveryOwnerReplacedCount { get; set; }
        public long OlderEpochCleanupAfterEpochAdvanceCount { get; set; }
        public long LastAppliedFrameId { get; set; } = -1;
        public long LastCleanFrameId { get; set; } = -1;
    }

    private readonly record struct LossBurstKey(
        ScreenShareReassemblerRootCauseBucket RootCause,
        long ExpectedNextFrameId);

    private readonly record struct LatencySummary(
        long AvgMs,
        long MedianMs,
        long P95Ms,
        long MaxMs)
    {
        public static LatencySummary Empty { get; } = new(0, 0, 0, 0);
    }

    private sealed class TimelineEventState
    {
        public TimelineEventState(string eventName, long frameId, long relatedFrameId, long occurredUtcMs)
        {
            EventName = eventName;
            FrameId = frameId;
            RelatedFrameId = relatedFrameId;
            OccurredUtcMs = occurredUtcMs;
        }

        public string EventName { get; }
        public long FrameId { get; }
        public long RelatedFrameId { get; }
        public long OccurredUtcMs { get; }
    }

    private sealed class LossBurstState
    {
        public LossBurstState(ScreenShareReassemblerRootCauseBucket rootCause, long expectedNextFrameId)
        {
            RootCause = rootCause;
            ExpectedNextFrameId = expectedNextFrameId;
        }

        public ScreenShareReassemblerRootCauseBucket RootCause { get; }
        public long ExpectedNextFrameId { get; }
        public long ReceivedFrameIdStart { get; private set; } = -1;
        public long ReceivedFrameIdEnd { get; private set; } = -1;
        public int FutureNonKeyBufferedCount { get; private set; }
        public long BufferedRecoveryKeyframeFrameId { get; private set; } = -1;
        public long LossCount { get; private set; }

        public void Observe(long receivedFrameId, int futureNonKeyBufferedCount, long bufferedRecoveryKeyframeFrameId)
        {
            if (ReceivedFrameIdStart < 0 || receivedFrameId < ReceivedFrameIdStart)
            {
                ReceivedFrameIdStart = receivedFrameId;
            }

            if (receivedFrameId > ReceivedFrameIdEnd)
            {
                ReceivedFrameIdEnd = receivedFrameId;
            }

            FutureNonKeyBufferedCount = Math.Max(FutureNonKeyBufferedCount, Math.Max(0, futureNonKeyBufferedCount));
            BufferedRecoveryKeyframeFrameId = bufferedRecoveryKeyframeFrameId >= 0
                ? bufferedRecoveryKeyframeFrameId
                : BufferedRecoveryKeyframeFrameId;
            LossCount++;
        }
    }

    private sealed class EpochDiagnosticsState
    {
        public EpochDiagnosticsState(long streamEpoch)
        {
            StreamEpoch = streamEpoch;
        }

        public long StreamEpoch { get; }
        public long LastAppliedFrameId { get; set; } = -1;
        public long VisibleApplyCount { get; set; }
        public long VisibleApplyCountAtLastGap { get; set; }
        public long GapCount { get; set; }
        public long RecoveryKeyframeApplyCount { get; set; }
        public long ResyncCount { get; set; }
        public long GapExpectedFrameId { get; set; } = -1;
        public long BufferedRecoveryKeyframeFrameId { get; set; } = -1;
        public int FutureNonKeyBufferedCount { get; set; }
        public long FirstFragmentSeenUtcMs { get; set; }
        public long FirstFragmentSeenFrameId { get; set; } = -1;
        public long FirstFrameAssembledUtcMs { get; set; }
        public long FirstFrameAssembledFrameId { get; set; } = -1;
        public long FirstFrameEmittedUtcMs { get; set; }
        public long FirstFrameEmittedFrameId { get; set; } = -1;
        public long FirstCleanFrameAppliedUtcMs { get; set; }
        public long FirstCleanFrameAppliedFrameId { get; set; } = -1;
        public long FirstGapDetectedUtcMs { get; set; }
        public long FirstKeyframeRequestedUtcMs { get; set; }
        public long FirstRecoveryLockStartedUtcMs { get; set; }
        public long FirstRecoveryLockClearedUtcMs { get; set; }
        public long FirstRecoveryKeyframeBufferedUtcMs { get; set; }
        public long FirstRecoveryKeyframeAppliedUtcMs { get; set; }
        public long FirstResyncTriggeredUtcMs { get; set; }
        public long RecoveryCandidatePresentCount { get; set; }
        public long VisibleRecoveryFloorFrameId { get; set; } = -1;
        public long StableVisibleHeadFrameId { get; set; } = -1;
        public long AppliedHeadFrameId { get; set; } = -1;
        public long OrderedEmitHeadFrameId { get; set; } = -1;
        public long WinningRecoveryFrameId { get; set; } = -1;
        public long LastSuccessfulRecoveryWindowFrameId { get; set; } = -1;
        public long LastSuccessfulRecoveryWindowContiguousFrameId { get; set; } = -1;
        public long FragmentGapBeforeAssemblyCount { get; set; }
        public long LateFragmentAfterHeadAdvancedCount { get; set; }
        public long LateFragmentAfterAppliedHeadCount { get; set; }
        public long LateFragmentAfterOrderedHeadCount { get; set; }
        public long SupersededRecoveryTailCleanupCount { get; set; }
        public long RecoveryOwnerReplacedCount { get; set; }
        public long OlderEpochCleanupAfterEpochAdvanceCount { get; set; }
        public long LateFragmentAfterStableVisibleHeadCount { get; set; }
        public long LateFragmentAfterVisibleRecoveryCount { get; set; }
        public long LateFragmentAfterSuccessfulRecoveryCount { get; set; }
        public long StaleRunwayWindowAbortCount { get; set; }
        public long RunwayFollowersEmittedWithinActionableWindowCount { get; set; }
        public long SuppressedEmitDuringRecoveryWaitCount { get; set; }
        public long FutureTailPrunedWhileGapActiveCount { get; set; }
        public long ProtectedHeadMissingBudgetPressureCount { get; set; }
        public long RecoveryKeyframeSupersededOrReplacedCount { get; set; }
        public long OrderedEmitBlockedThenResyncedCount { get; set; }
        public List<TimelineEventState> TimelineEvents { get; } = new();
        public Dictionary<LossBurstKey, LossBurstState> LossBursts { get; } = new();
    }
}
