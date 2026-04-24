function Get-SoakSummaryFromLog {
    $logPath = Join-Path $env:LOCALAPPDATA 'nLink\logs\nlink.log'
    if (-not (Test-Path $logPath)) {
        throw "App log not found after NKN soak: $logPath"
    }

    $captureToSend = New-Object System.Collections.Generic.List[int]
    $helperApply = New-Object System.Collections.Generic.List[int]
    $helperStaleDrops = 0
    $receiverSupersededFrames = 0
    $persistentSummaries = 0
    $sinkWriterSummaries = 0
    $normalModeSummaries = 0
    $reducedModeSummaries = 0
    $catchUpModeSummaries = 0
    $bridgeHealthAdvisorySummaries = 0
    $bridgeHealthActionableSummaries = 0
    $latestFramesQueued = -1
    $latestFramesDeferredToSendSlot = -1
    $latestFramesReplacedBeforeSendSlot = -1
    $latestFramesDroppedByQueueEvict = -1
    $latestSendSlotEmptyCount = -1
    $latestSlotCoalescingActive = -1
    $latestRawFramesDeferredToEncodeSlot = -1
    $latestRawFramesReplacedBeforeEncodeSlot = -1
    $latestRawEncodeSlotEmptyCount = -1
    $latestRawSlotCoalescingActive = -1
    $latestPromotionCaptureToSendBudgetMs = -1
    $latestSourceSupersededPendingFrames = -1
    $latestAvgFragmentsPerFrame = -1.0
    $latestAvgPayloadsPerFrame = -1.0
    $latestBatchPayloadCount = -1
    $latestLegacyPayloadCount = -1
    $latestOrdinaryNonKeyBatchedPayloadCount = -1
    $latestOrdinaryNonKeyLegacyPayloadCount = -1
    $latestKeyframeRecoveryBatchedPayloadCount = -1
    $latestEmittedDisplayableFrames = -1
    $latestEmittedNonDisplayableUnits = -1
    $latestEmittedIdrFrames = -1
    $latestEmittedPFrames = -1
    $latestDroppedBFrames = -1
    $latestDroppedMultiPictureUnits = -1
    $latestDisplayableFrameRatio = -1.0
    $latestIdrFrameRatio = -1.0
    $latestAverageEncodedFrameBytes = -1.0
    $latestTransportIpOnlyMode = -1
    $latestLastAccessUnitKind = ''
    $latestLowDelayConfigApplied = ''
    $latestHelperFramesCompleted = -1
    $latestHelperFramesEnqueuedForDecode = -1
    $latestHelperFramesDroppedBeforeDecode = -1
    $latestHelperFramesDecoded = -1
    $latestHelperFramesDroppedAfterDecode = -1
    $latestHelperFramesApplied = -1
    $latestHelperNeedMoreInputCount = -1
    $latestHelperCompletedWithoutPictureCount = -1
    $latestHelperDecodeDurationMs = -1.0
    $latestHelperApplyIntervalMs = -1.0
    $latestHelperMaxPendingEncodedDepth = -1
    $latestHelperMaxPendingDecodedDepth = -1
    $latestHelperAvgEnqueueToDecodeStartMs = -1.0
    $latestHelperAvgEnqueueToDropMs = -1.0
    $latestHelperDecodeWorkerDropQueueOverflowCount = -1
    $latestHelperDecodeWorkerDropAgeBudgetCount = -1
    $latestHelperDecodeWorkerDropGenerationCount = -1
    $latestHelperDecodeWorkerDropStoppedCount = -1
    $latestHelperReassemblerLossCount = -1
    $latestHelperEnqueueRejectCount = -1
    $latestHelperWaitingForRecoveryKeyframeRejectCount = -1
    $latestHelperRecoveryWaitRejectBeforeRunwayCount = -1
    $latestHelperRecoveryRunwayOverflowRejectCount = -1
    $latestHelperSuppressedEmitDuringRecoveryWaitCount = -1
    $latestHelperStaleSupersededRecoverySuppressedCount = -1
    $latestHelperSoftStaleCleanupCount = -1
    $latestHelperBlockedByReservedRecoveryFrameRejectCount = -1
    $latestHelperOlderEpochIgnoredDuringRecoveryLockCount = -1
    $latestHelperNewerEpochNonKeyIgnoredDuringLockCount = -1
    $latestHelperDeferredPostRecoveryCandidateReplaceCount = -1
    $latestHelperDecodeWorkerDropCount = -1
    $latestHelperPostDecodeDropCount = -1
    $latestHelperDecodeQueueOverflowCount = -1
    $latestHelperDecodeAgeBudgetCount = -1
    $latestHelperDecodeGenerationChangedCount = -1
    $latestHelperDecodeStoppedCount = -1
    $latestHelperDecodedApplyQueueOverflowCount = -1
    $latestHelperDecodedFrameReplacedBeforeApplyCount = -1
    $latestHelperStaleDroppedAfterDecodeCount = -1
    $latestHelperDroppedWaitingForRecoveryKeyframeCount = -1
    $latestHelperGapNonKeyPrunedCount = -1
    $latestHelperFutureTailQuarantinedDuringGapCount = -1
    $latestHelperFutureTailQuarantinedAfterGapCount = -1
    $latestHelperPreCandidateGapTailRejectedCount = -1
    $latestHelperRecoveryCandidatePresentCount = -1
    $latestHelperVisibleRecoveryFloorFrameId = -1
    $latestHelperStableVisibleHeadFrameId = -1
    $latestHelperAppliedHeadFrameId = -1
    $latestHelperOrderedEmitHeadFrameId = -1
    $latestHelperWinningRecoveryFrameId = -1
    $latestHelperVisibleHeadFrameId = -1
    $latestHelperSupersededRecoveryTailCleanupCount = -1
    $latestHelperLateSameEpochAfterHeadAdvancedDropCount = -1
    $latestHelperStaleRunwayWindowAbortCount = -1
    $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount = -1
    $latestHelperRunwayFollowersEmittedWithinActionableWindowCount = -1
    $latestHelperRecoveryOwnerReplacedCount = -1
    $latestHelperOlderEpochCleanupAfterEpochAdvanceCount = -1
    $latestHelperSteadyVisibleProgressActive = 0
    $latestHelperSteadyVisibleProgressActivationFrameId = -1
    $latestHelperFramesAppliedSinceLastGap = -1
    $latestRemoteHelperFactHealthyActive = 0
    $latestRemoteHelperFactHealthySource = ''
    $latestRemoteHelperFactProofFrameId = -1
    $latestRemoteHelperFactLastMessageAgeMs = -1
    $latestRemoteHelperFactHealthyClearCount = -1
    $latestRemoteHelperFactHealthyClearReason = ''
    $latestHelperLastSentStableVisibleHeadFrameId = -1
    $latestHelperPressureSendBypassedForVisibleProgressCount = -1
    $latestHelperProofKeepaliveSendCount = -1
    $latestHelperProofKeepaliveTimerDrivenSendCount = -1
    $latestHelperProofKeepaliveLastHeadFrameId = -1
    $latestHelperProofKeepaliveLastSendAgeMs = -1
    $latestHelperFirstVisibleApplyToSenderFactSendMs = -1
    $latestHelperSteadyVisibleProgressClearedCount = -1
    $latestHelperSteadyVisibleProgressClearedReason = ''
    $latestHelperLateFragmentAfterAppliedHeadCount = -1
    $latestHelperLateFragmentAfterOrderedHeadCount = -1
    $latestHelperLateFragmentAfterStableVisibleHeadCount = -1
    $latestHelperLateFragmentAfterVisibleRecoveryCount = -1
    $latestHelperPreCandidateGapTailEmittedToViewerCount = -1
    $latestHelperHighFrameAgeSuppressedDueToVisibleProgressCount = -1
    $latestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount = -1
    $latestHelperActionableHighFrameAgeCount = -1
    $latestHelperActionableLateFragmentCount = -1
    $latestRecoveryBurstActive = 0
    $latestRecoveryBurstPhase = 'idle'
    $latestRecoveryBurstStreamEpoch = -1
    $latestRecoveryOwnerFrameId = -1
    $latestRecoveryProtectedFollowerCount = -1
    $latestRecoveryGapCount = -1
    $latestRecoveryGapToKeyframeRequestMs = -1
    $latestRecoveryKeyframeRequestToOwnerEmitMs = -1
    $latestRecoveryOwnerEmitToAckMs = -1
    $latestRecoveryOwnerAckFrameId = -1
    $latestRecoveryAckSource = ''
    $latestRecoveryOwnerEmitToFirstVisibleApplyMs = -1
    $latestRecoveryBurstControlFallbackCount = -1
    $latestRecoveryBurstTimeoutCount = -1
    $latestRecoveryBurstCompletedCount = -1
    $latestRecoveryBurstRestartSuppressedCount = -1
    $latestRecoveryBurstEncoderRerequestCount = -1
    $latestRecoveryOwnerPendingForcedResetCount = -1
    $latestRecoveryKeyframeEmittedAfterForcedResetCount = -1
    $latestRecoveryBurstCompletedByHelperAckCount = -1
    $latestRecoveryBurstCompletedByAppliedHeadAckCount = -1
    $latestRecoveryBurstCompletedByLastVisibleApplyAckCount = -1
    $latestRecoveryBurstCompletedByVisibleRecoveryFloorCount = -1
    $latestRecoveryBurstCompletedByVisibleApplyFallbackCount = -1
    $latestRecoveryBurstCompletedByTimeoutCount = -1
    $latestRecoveryBurstCompletedByProtectedFramesCount = -1
    $latestRecoveryBurstProfileTransitionDeferredCount = -1
    $latestRecoveryBurstProfileTransitionTakeoverCount = -1
    $latestRecoveryBurstStaleRequestSuppressedCount = -1
    $latestRecoveryBurstRequestSuppressedDueToHelperAckCount = -1
    $latestRecoveryBurstStartedWhileHelperProofHealthyCount = -1
    $eventRecoveryBurstCompletedCount = 0
    $eventRecoveryBurstCompletedByHelperAckCount = 0
    $eventRecoveryBurstCompletedByAppliedHeadAckCount = 0
    $eventRecoveryBurstCompletedByLastVisibleApplyAckCount = 0
    $eventRecoveryBurstCompletedByVisibleRecoveryFloorCount = 0
    $eventRecoveryBurstCompletedByVisibleApplyFallbackCount = 0
    $eventRecoveryBurstCompletedByTimeoutCount = 0
    $eventRecoveryOwnerPendingForcedResetCount = 0
    $eventRecoveryKeyframeEmittedAfterForcedResetCount = 0
    $latestLastCompletedRecoveryEpoch = -1
    $latestLastCompletedRecoveryOwnerFrameId = -1
    $latestLastCompletedRecoveryAckFrameId = -1
    $latestLastCompletedRecoveryAckSource = ''
    $latestLastCompletedRecoveryOwnerEmitToAckMs = -1
    $latestLastCompletedRecoveryCompletionKind = ''
    $latestRecoveryCompletionAccountingMismatch = 0
    $latestRecoveryOwnerPendingNonKeyHeldCount = -1
    $latestRecoveryOwnerPendingNonKeyReplacedCount = -1
    $latestRecoveryOwnerUnackedNonKeyHeldCount = -1
    $latestRecoveryOwnerUnackedNonKeyReplacedCount = -1
    $latestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = -1
    $latestRecoveryOwnerReplacedBeforeAckCount = -1
    $latestRecoveryOwnerAckWindowMs = -1
    $latestHighFrameAgeSuppressedDuringOwnerAckCount = -1
    $latestRecoveryTimeoutWhileHelperHeadAdvancedCount = -1
    $latestSenderReceivedHelperProgressDuringContinuityLossCount = -1
    $latestHelperAckAfterFactSendMs = -1
    $latestPostAckModeGraceSuppressedHighFrameAgeCount = -1
    $latestBootstrapGraceSuppressedCatchUpCount = -1
    $latestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = -1
    $latestCatchUpExitWhileRemoteHighFrameAgePressureCount = -1
    $latestProtectedRecoveryFramesDispatchedCount = -1
    $latestRecoveryProtectedFrameBlockedByOrdinaryCount = -1
    $latestRecoveryPostAckHoldActive = 0
    $latestRecoveryPostAckHoldStartedCount = -1
    $latestRecoveryPostAckHoldExpiredCount = -1
    $latestRecoveryPostAckHoldSuppressedReopenCount = -1
    $latestLastAcknowledgedRecoveryOwnerFrameId = -1
    $latestLastAcknowledgedHelperHeadFrameId = -1
    $latestRemoteHelperVisibleHeadFrameId = -1
    $latestRemoteHelperVisibleRecoveryFloorFrameId = -1
    $latestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount = -1
    $latestLastAcknowledgedVisibleHelperHeadFrameId = -1
    $latestLastAcknowledgedHelperProofAgeMs = -1
    $latestPersistedReleaseFloorEpoch = -1
    $latestSatisfiedRecoveryFloorFrameId = -1
    $latestSatisfiedRecoveryFloorSource = ''
    $latestSatisfiedRecoveryFloorVisibleProofCount = -1
    $latestContinuitySignalIgnoredDueToSatisfiedFloorCount = -1
    $latestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount = -1
    $latestRecoveryLockClearedByAcknowledgedProofCount = -1
    $latestRecoveryLockClearedByVisibleProofCount = -1
    $latestRecoveryLockLastClearReason = ''
    $latestHelperProgressPastOwnerWithoutBurstAckCount = -1
    $latestPostRecoveryAgeGraceActive = 0
    $latestPostRecoveryAgeGraceSuppressedCount = -1
    $recoveryControlBootstrapRetrySkippedDueToBurstResolvedCount = 0
    $recoveryControlBootstrapRetryQueuedAfterBurstResolutionCount = 0
    $recoveryControlFallbackQueuedCount = 0
    $steadyStateControlFallbackQueuedCount = 0
    $latestBridgeMediaMessagesReceived = -1
    $bridgeMediaMessagesReceivedFromLogs = 0
    $latestMediaPlaneFramesSent = -1
    $latestMediaPlaneAttached = -1
    $recoveryBurstCompletedWithoutHelperAdvance = 0
    $recoveryAckMissedDespiteHelperProgress = 0
    $latestHelperRecoveryRunwayContiguousFollowerBufferCount = -1
    $latestHelperRecoveryRunwayContiguousFollowerApplyCount = -1
    $latestHelperRecoveryRunwayAbortCount = -1
    $latestHelperRecoveryKeyframeResyncCount = -1
    $latestHelperGapActive = -1
    $latestHelperGapExpectedFrameId = -1
    $latestHelperBufferedRecoveryKeyframeFrameId = -1
    $latestHelperFutureNonKeyBufferedCount = -1
    $latestHelperPostRecoveryVisibleGenerationResetCount = -1
    $latestHelperPostRecoveryPurgedPreRecoveryFollowerCount = -1
    $latestHelperPostRecoveryStaleDropBypassCount = -1
    $latestHelperLateFragmentAfterSuccessfulRecoveryCount = -1
    $latestHelperUnattributedLossCount = -1
    $latestHelperRecentLosses = ''
    $latestHelperVisibleApplyRatio = -1.0
    $latestHelperAvgDecodeCompleteToVisibleApplyMs = -1.0
    $latestHelperAvgUiPostApplyMs = -1.0
    $latestHelperAvgVisibleHeadLagFrames = -1.0
    $latestHelperAvgStableHeadLagFrames = -1.0
    $latestHelperLastReservedApplyHoldMs = -1
    $latestHelperLastRecoveryProgressCorridorHoldMs = -1
    $latestHelperLastRecoveryRunwayAbortHoldMs = -1
    $latestHelperLastRecoveryProgressCorridorAbortReason = 'none'
    $latestHelperGapCount = -1
    $latestHelperRecoveryKeyframeApplyCount = -1
    $latestHelperResyncCount = -1
    $latestHelperDominantReassemblerRootCause = ''
    $latestHelperDominantAdmissionRejectReason = ''
    $latestHelperPostRecoveryHighFrameAgeSuppressedTicks = -1
    $latestHelperPostRecoverySettleWindowCount = -1
    $latestHelperPostRecoverySettleWindowSuccessCount = -1
    $latestHelperPostRecoverySettleWindowTimeoutCount = -1
    $latestHelperVisibleAppliesDuringSettleCount = -1
    $latestHelperVisibleAppliesBeforePressureReenabled = -1
    $latestHelperRecoveryWindowActive = -1
    $latestHelperRecoveryWindowProgressed = -1
    $latestHelperRecoveryWindowSucceeded = -1
    $latestHelperRecoveryWindowProgressedCount = -1
    $latestHelperRecoveryWindowSuccessCount = -1
    $latestHelperActiveRecoveryWindowEpoch = -1
    $latestHelperActiveRecoveryWindowRecoveryFrameId = -1
    $latestHelperRecoveryWindowContiguousFollowerApplyCount = -1
    $latestHelperAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = -1
    $latestHelperBaselineEstablished = -1
    $latestHelperBaselineCaptureToRenderMs = -1
    $latestHelperAgeExcessMs = -1
    $latestHelperProgressStallMs = -1
    $latestHelperBaselineReseedInProgress = -1
    $latestHelperAgePressureConsecutiveCount = -1
    $latestHelperCadencePressureConsecutiveCount = -1
    $latestHelperCatchUpSuppressedDueToProgressCount = -1
    $latestHelperBaselineFrozenDueToStallCount = -1
    $latestHelperBaselineReseedAfterRecoveryCount = -1
    $latestHelperCadenceStallWindowCount = -1
    $latestHelperCadenceStallTriggerCount = -1
    $latestHelperBridgeHealthAdvisoryCount = -1
    $latestHelperBridgeHealthActionableCount = -1
    $latestHelperBridgeHealthQuarantineSuppressedCount = -1
    $latestHelperBridgeHealthActionableWithoutQueueOrDropCount = -1
    $latestHelperSessionId = ''
    $latestHelperRecoveryFollowerWindowBufferedCount = -1
    $latestHelperRecoveryFollowerWindowAppliedCount = -1
    $latestHelperRecoveryFollowerWindowTrimmedCount = -1
    $latestHelperProtectedRecoveryDeliveryCount = -1
    $latestHelperRecoveryProgressCorridorCount = -1
    $latestHelperRecoveryProgressCorridorSuccessCount = -1
    $latestHelperRecoveryProgressCorridorAbortCount = -1
    $latestHelperRecoveryProgressCorridorAppliedCount = -1
    $latestHelperRecoveryKeyframePendingVisibleApplyCount = -1
    $latestHelperStartupCorridorBufferedFollowerCount = -1
    $latestHelperStartupCorridorReleaseCount = -1
    $latestHelperStartupCorridorAbortCount = -1
    $latestHelperStartupCorridorAbortReason = ''
    $latestPromotionBlockerRateGateTicks = -1
    $latestPromotionBlockerHelperPressureTicks = -1
    $latestPromotionBlockerHelperWarmupTicks = -1
    $latestPromotionBlockerHelperApplyCountTicks = -1
    $latestPromotionBlockerBridgeHealthTicks = -1
    $latestPromotionBlockerRecoveryLockTicks = -1
    $latestPromotionBlockerQueueEvictTicks = -1
    $latestPromotionBlockerCaptureAgeTicks = -1
    $latestPromotionBlockerEncodeBudgetTicks = -1
    $latestPromotionBlockerTransitionGraceTicks = -1
    $latestPromotionEncodeSoftSpikeCount = -1
    $latestPromotionEncodeSoftSpikeResetSuppressedCount = -1
    $promotionBlockedByMissingHelperProofCount = 0
    $promotionBlockedByStaleHelperProofCount = 0
    $promotionBlockedByEncodeBudgetCount = 0
    $promotionBlockedByEncodeBudgetAloneCount = 0
    $latestHealthyTickResetReasonCounts = ''
    $latestReducedPromotionRecentEntries = ''
    $latestHelperRunId = ''
    $latestHelperListenerGeneration = -1
    $latestHealthSenderOperatingState = 'normal'
    $latestHealthSenderGuardState = 'none'
    $latestHealthHelperSessionPhase = 'no_visible_baseline'
    $latestHealthHelperRecoveryMechanism = 'none'
    $latestHealthDominantLossClass = 'benign_stale_cleanup'
    $latestHealthDominantPressureBlocker = 'none'
    $latestHealthDominantTroubleDomain = 'none'
    $latestHealthRecoveryActive = 0
    $latestHealthBaselineEstablished = 0
    $latestHealthSteadyVisibleProgressActive = 0
    $latestSummarySenderOperatingState = ''
    $latestSummarySenderGuardState = ''
    $latestSummaryDominantPressureBlocker = ''
    $latestSummaryHelperSessionPhase = ''
    $latestSummaryHelperRecoveryMechanism = ''
    $latestSummaryDominantLossClass = ''
    $latestHelperUpstreamCaptureToFrameReadyAvgMs = -1
    $latestHelperUpstreamCaptureToFrameReadyMedianMs = -1
    $latestHelperUpstreamCaptureToFrameReadyP95Ms = -1
    $latestHelperUpstreamCaptureToFrameReadyMaxMs = -1
    $latestHelperUpstreamFrameReadyToViewerAcceptAvgMs = -1
    $latestHelperUpstreamFrameReadyToViewerAcceptMedianMs = -1
    $latestHelperUpstreamFrameReadyToViewerAcceptP95Ms = -1
    $latestHelperUpstreamFrameReadyToViewerAcceptMaxMs = -1
    $latestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs = -1
    $latestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs = -1
    $latestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms = -1
    $latestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs = -1
    $latestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs = -1
    $latestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs = -1
    $latestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms = -1
    $latestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs = -1
    $latestHelperUpstreamCaptureToDecodeStartAvgMs = -1
    $latestHelperUpstreamCaptureToDecodeStartMedianMs = -1
    $latestHelperUpstreamCaptureToDecodeStartP95Ms = -1
    $latestHelperUpstreamCaptureToDecodeStartMaxMs = -1
    $latestHelperUpstreamWorstEpochByCaptureToDecodeStart = -1
    $latestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs = -1
    $latestHelperDominantUpstreamLatencyStage = 'none'
    $latestHelperReadyPathCaptureToFirstFragmentObservedAvgMs = -1
    $latestHelperReadyPathCaptureToFirstFragmentObservedMedianMs = -1
    $latestHelperReadyPathCaptureToFirstFragmentObservedP95Ms = -1
    $latestHelperReadyPathCaptureToFirstFragmentObservedMaxMs = -1
    $latestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs = -1
    $latestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs = -1
    $latestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms = -1
    $latestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs = -1
    $latestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs = -1
    $latestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs = -1
    $latestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms = -1
    $latestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs = -1
    $latestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs = -1
    $latestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs = -1
    $latestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms = -1
    $latestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs = -1
    $latestHelperDominantReadyPathStage = 'none'
    $latestHelperReceivePathCaptureToEnvelopeSendAvgMs = -1
    $latestHelperReceivePathCaptureToEnvelopeSendMedianMs = -1
    $latestHelperReceivePathCaptureToEnvelopeSendP95Ms = -1
    $latestHelperReceivePathCaptureToEnvelopeSendMaxMs = -1
    $latestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs = -1
    $latestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs = -1
    $latestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms = -1
    $latestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs = -1
    $latestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs = -1
    $latestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs = -1
    $latestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms = -1
    $latestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs = -1
    $latestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs = -1
    $latestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs = -1
    $latestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms = -1
    $latestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs = -1
    $latestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs = -1
    $latestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs = -1
    $latestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms = -1
    $latestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs = -1
    $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs = -1
    $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs = -1
    $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms = -1
    $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs = -1
    $latestHelperDominantReceivePathStage = 'none'
    $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs = -1
    $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs = -1
    $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms = -1
    $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs = -1
    $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs = -1
    $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs = -1
    $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms = -1
    $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs = -1
    $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs = -1
    $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs = -1
    $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms = -1
    $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs = -1
    $latestHelperDominantBridgeIngressStage = 'none'
    $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs = -1
    $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs = -1
    $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms = -1
    $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs = -1
    $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs = -1
    $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs = -1
    $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms = -1
    $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs = -1
    $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs = -1
    $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs = -1
    $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms = -1
    $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs = -1
    $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs = -1
    $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs = -1
    $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms = -1
    $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs = -1
    $latestHelperDominantNknReceiveStage = 'none'
    $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs = -1
    $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs = -1
    $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms = -1
    $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs = -1
    $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs = -1
    $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs = -1
    $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms = -1
    $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs = -1
    $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs = -1
    $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs = -1
    $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms = -1
    $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs = -1
    $latestHelperDominantWsReceiveStage = 'none'
    $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs = -1
    $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs = -1
    $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms = -1
    $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs = -1
    $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs = -1
    $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs = -1
    $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms = -1
    $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs = -1
    $latestHelperDominantSocketReceiveStage = 'none'
    $latestBridgeEventLoopP95Ms = -1
    $latestBridgeEventLoopMaxMs = -1
    $latestBridgeEventLoopMeanMs = -1
    $latestBridgeEventLoopSampleWindowMs = -1
    $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = -1
    $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = -1
    $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = -1
    $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = -1
    $latestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = -1
    $latestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = -1
    $latestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = -1
    $latestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = -1
    $latestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = -1
    $latestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = -1
    $latestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = -1
    $latestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = -1
    $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = -1
    $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = -1
    $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = -1
    $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = -1
    $latestBridgeMediaSendFramesSent = -1
    $latestBridgeMediaSendFailures = -1
    $latestBridgeMediaSendQueueDrops = -1
    $latestBridgeMediaSendQueueMode = 'normal'
    $latestBridgeMediaSendQueueDepth = -1
    $latestBridgeMediaSendOldestQueuedAgeMs = -1
    $latestBridgeMediaSendSampleWindowMs = -1
    $bestBridgeMediaSendFramesSent = -1
    $latestBridgeTransportHealthSelectedRpc = '(none)'
    $latestBridgeTransportHealthSelectedRpcKey = '(none)'
    $latestBridgeTransportHealthSelectedRpcStage = 'none'
    $latestBridgeTransportHealthConnectId = '(none)'
    $latestBridgeTransportHealthConnectKey = '(none)'
    $latestBridgeTransportHealthReadyEmitted = -1
    $latestBridgeTransportHealthClientReadyAgeMs = -1
    $latestBridgeTransportHealthDisconnectCountSinceLast = -1
    $latestBridgeTransportHealthConnectFailedCountSinceLast = -1
    $latestBridgeTransportHealthWsErrorCountSinceLast = -1
    $latestBridgeTransportHealthRpcFallbackAttemptCountSinceLast = -1
    $latestBridgeTransportHealthControlReady = -1
    $latestBridgeTransportHealthMediaReady = -1
    $latestBridgeTransportHealthBulkReady = -1
    $latestBridgeTransportHealthFramesSentSinceLast = -1
    $latestBridgeTransportHealthLatestDisconnectReason = '(none)'
    $latestBridgeTransportHealthSampleWindowMs = -1
    $latestBridgeTransportHealthUniqueSelectedRpcCount = 0
    $bestBridgeTransportHealthFramesSentSinceLast = -1
    $helperEpochLossLines = New-Object System.Collections.Generic.List[string]
    $helperQualitySummaryLines = New-Object System.Collections.Generic.List[string]
    $helperUpstreamLatencySummaryLines = New-Object System.Collections.Generic.List[string]
    $helperReadyPathSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperReceivePathSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperBridgeIngressSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperNknReceiveSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperWsReceiveSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperSocketReceiveSummaryLines = New-Object System.Collections.Generic.List[string]
    $bridgeEventLoopSummaryLines = New-Object System.Collections.Generic.List[string]
    $bridgeMediaSendSummaryLines = New-Object System.Collections.Generic.List[string]
    $bridgeTransportHealthSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperEpochTimelineLines = New-Object System.Collections.Generic.List[string]
    $helperReassemblerRootCauseSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperRecoveryEpochInvestigationLines = New-Object System.Collections.Generic.List[string]
    $helperReassemblerRecoveryOwnerTransitionLines = New-Object System.Collections.Generic.List[string]
    $helperReassemblerActionableLateFragmentLines = New-Object System.Collections.Generic.List[string]
    $helperReassemblerOlderEpochCleanupLines = New-Object System.Collections.Generic.List[string]
    $helperPressureSummaryLines = New-Object System.Collections.Generic.List[string]
    $healthSnapshotLines = New-Object System.Collections.Generic.List[string]
    $reducedPromotionSummaryLines = New-Object System.Collections.Generic.List[string]
    $helperEpochVisibleRatioByEpoch = @{}
    $helperEpochRecoveryLockMsByEpoch = @{}
    $helperEpochRootCauseByEpoch = @{}
    $helperEpochPressureBlockerByEpoch = @{}
    $helperPressureSummaryByEpoch = @{}
    $helperRootCauseSummaryByEpoch = @{}
    $bridgeTransportHealthSelectedRpcKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($line in [System.IO.File]::ReadAllLines($logPath)) {
        if ($line -match 'event=screenshare_freshness_summary;.*capture_to_send_age_ms=([0-9-]+).*frames_queued=([0-9-]+).*emitted_displayable_frames=([0-9-]+).*emitted_non_displayable_units=([0-9-]+).*emitted_idr_frames=([0-9-]+).*emitted_p_frames=([0-9-]+).*dropped_b_frames=([0-9-]+).*dropped_multi_picture_units=([0-9-]+).*displayable_frame_ratio=([0-9.]+).*idr_frame_ratio=([0-9.]+).*avg_encoded_frame_bytes=([0-9.]+).*transport_ip_only_mode=([0-9-]+).*last_access_unit_kind=([^;]+).*low_delay_config_applied=([^;]+).*encoder_path=([a-z_]+).*sender_freshness_mode=([a-z_]+).*avg_transport_payloads_per_frame=([0-9.]+).*batched_payloads_sent=([0-9-]+).*legacy_fragment_payloads_sent=([0-9-]+).*bridge_health_kind=([a-z_]+)') {
            [void]$captureToSend.Add([int]$matches[1])
            $latestFramesQueued = [int]$matches[2]
            $latestEmittedDisplayableFrames = [int]$matches[3]
            $latestEmittedNonDisplayableUnits = [int]$matches[4]
            $latestEmittedIdrFrames = [int]$matches[5]
            $latestEmittedPFrames = [int]$matches[6]
            $latestDroppedBFrames = [int]$matches[7]
            $latestDroppedMultiPictureUnits = [int]$matches[8]
            $latestDisplayableFrameRatio = [double]$matches[9]
            $latestIdrFrameRatio = [double]$matches[10]
            $latestAverageEncodedFrameBytes = [double]$matches[11]
            $latestTransportIpOnlyMode = [int]$matches[12]
            $latestLastAccessUnitKind = [string]$matches[13]
            $latestLowDelayConfigApplied = [string]$matches[14]
            if ([string]::Equals($matches[15], 'persistent_transform', [System.StringComparison]::OrdinalIgnoreCase)) {
                $persistentSummaries++
            }
            elseif ([string]::Equals($matches[15], 'sink_writer_fallback', [System.StringComparison]::OrdinalIgnoreCase)) {
                $sinkWriterSummaries++
            }

            switch -Regex ($matches[16]) {
                '^normal$' { $normalModeSummaries++ }
                '^reduced$' { $reducedModeSummaries++ }
                '^catch_up$' { $catchUpModeSummaries++ }
            }

            $latestAvgPayloadsPerFrame = [double]$matches[17]
            $latestBatchPayloadCount = [int]$matches[18]
            $latestLegacyPayloadCount = [int]$matches[19]

            switch -Regex ($matches[20]) {
                '^advisory$' { $bridgeHealthAdvisorySummaries++ }
                '^actionable$' { $bridgeHealthActionableSummaries++ }
            }
        }

        if ($line -match 'Bridge screenshare (first inbound traffic|traffic) \(messages=([0-9]+),') {
            $bridgeMediaMessagesReceivedFromLogs += [int]$matches[2]
            if ($bridgeMediaMessagesReceivedFromLogs -gt $latestBridgeMediaMessagesReceived) {
                $latestBridgeMediaMessagesReceived = $bridgeMediaMessagesReceivedFromLogs
            }
        }

        if ($line -like '*event=screenshare_freshness_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestFramesDeferredToSendSlot = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_deferred_to_send_slot'
            $latestFramesReplacedBeforeSendSlot = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_replaced_before_send_slot'
            $latestFramesDroppedByQueueEvict = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_dropped_by_queue_evict' -DefaultValue $latestFramesDroppedByQueueEvict
            $latestSendSlotEmptyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'send_slot_empty_count'
            $latestSlotCoalescingActive = Get-StructuredLogIntField -Pairs $pairs -Key 'slot_coalescing_active'
            $latestRawFramesDeferredToEncodeSlot = Get-StructuredLogIntField -Pairs $pairs -Key 'raw_frames_deferred_to_encode_slot'
            $latestRawFramesReplacedBeforeEncodeSlot = Get-StructuredLogIntField -Pairs $pairs -Key 'raw_frames_replaced_before_encode_slot'
            $latestRawEncodeSlotEmptyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'raw_encode_slot_empty_count'
            $latestRawSlotCoalescingActive = Get-StructuredLogIntField -Pairs $pairs -Key 'raw_slot_coalescing_active'
            $latestSummarySenderOperatingState = Get-StructuredLogStringField -Pairs $pairs -Key 'sender_operating_state' -DefaultValue $latestSummarySenderOperatingState
            $latestSummarySenderGuardState = Get-StructuredLogStringField -Pairs $pairs -Key 'sender_guard_state' -DefaultValue $latestSummarySenderGuardState
            $latestSummaryDominantPressureBlocker = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_pressure_blocker' -DefaultValue $latestSummaryDominantPressureBlocker
            $latestPromotionCaptureToSendBudgetMs = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_capture_to_send_budget_ms'
            $latestSourceSupersededPendingFrames = Get-StructuredLogIntField -Pairs $pairs -Key 'source_superseded_pending_frames'
            $latestHelperSteadyVisibleProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_steady_visible_progress_active' -DefaultValue $latestHelperSteadyVisibleProgressActive
            $stableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_stable_visible_head_frame_id' -DefaultValue $latestHelperStableVisibleHeadFrameId
            if ($stableVisibleHeadValue -match '^-?[0-9]+$') {
                $latestHelperStableVisibleHeadFrameId = [int64]$stableVisibleHeadValue
            }
            $latestHelperFramesAppliedSinceLastGap = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_frames_applied_since_last_gap' -DefaultValue $latestHelperFramesAppliedSinceLastGap
            $latestRemoteHelperFactHealthyActive = Get-StructuredLogIntField -Pairs $pairs -Key 'remote_helper_fact_healthy_active' -DefaultValue $latestRemoteHelperFactHealthyActive
            $latestRemoteHelperFactHealthySource = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_fact_healthy_source' -DefaultValue $latestRemoteHelperFactHealthySource
            $remoteHelperFactProofValue = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_fact_proof_frame_id' -DefaultValue $latestRemoteHelperFactProofFrameId
            if ($remoteHelperFactProofValue -match '^-?[0-9]+$') {
                $latestRemoteHelperFactProofFrameId = [int64]$remoteHelperFactProofValue
            }
            $latestRemoteHelperFactLastMessageAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'remote_helper_fact_last_message_age_ms' -DefaultValue $latestRemoteHelperFactLastMessageAgeMs
            $latestRemoteHelperFactHealthyClearCount = Get-StructuredLogIntField -Pairs $pairs -Key 'remote_helper_fact_healthy_clear_count' -DefaultValue $latestRemoteHelperFactHealthyClearCount
            $latestRemoteHelperFactHealthyClearReason = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_fact_healthy_clear_reason' -DefaultValue $latestRemoteHelperFactHealthyClearReason
            $latestRecoveryBurstActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_active' -DefaultValue $latestRecoveryBurstActive
            $latestRecoveryBurstPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_burst_phase' -DefaultValue $latestRecoveryBurstPhase
            $latestRecoveryBurstStreamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_stream_epoch' -DefaultValue $latestRecoveryBurstStreamEpoch
            $recoveryOwnerFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_frame_id' -DefaultValue $latestRecoveryOwnerFrameId
            if ($recoveryOwnerFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerFrameId = [int64]$recoveryOwnerFrameValue
            }
            $latestRecoveryProtectedFollowerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_protected_follower_count' -DefaultValue $latestRecoveryProtectedFollowerCount
            $latestRecoveryGapCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_gap_count' -DefaultValue $latestRecoveryGapCount
            $latestRecoveryGapToKeyframeRequestMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_gap_to_keyframe_request_ms' -DefaultValue $latestRecoveryGapToKeyframeRequestMs
            $latestRecoveryKeyframeRequestToOwnerEmitMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_request_to_owner_emit_ms' -DefaultValue $latestRecoveryKeyframeRequestToOwnerEmitMs
            $latestRecoveryOwnerAckWindowMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_ack_window_ms' -DefaultValue $latestRecoveryOwnerAckWindowMs
            $latestRecoveryOwnerEmitToAckMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_emit_to_ack_ms' -DefaultValue $latestRecoveryOwnerEmitToAckMs
            $latestRecoveryPostAckHoldActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_post_ack_hold_active' -DefaultValue $latestRecoveryPostAckHoldActive
            $latestRecoveryPostAckHoldStartedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_post_ack_hold_started_count' -DefaultValue $latestRecoveryPostAckHoldStartedCount
            $latestRecoveryPostAckHoldExpiredCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_post_ack_hold_expired_count' -DefaultValue $latestRecoveryPostAckHoldExpiredCount
            $latestRecoveryPostAckHoldSuppressedReopenCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_post_ack_hold_suppressed_reopen_count' -DefaultValue $latestRecoveryPostAckHoldSuppressedReopenCount
            $recoveryOwnerAckFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_ack_frame_id' -DefaultValue $latestRecoveryOwnerAckFrameId
            if ($recoveryOwnerAckFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerAckFrameId = [int64]$recoveryOwnerAckFrameValue
            }

            $latestRecoveryAckSource = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_ack_source' -DefaultValue $latestRecoveryAckSource
            $latestRecoveryOwnerEmitToFirstVisibleApplyMs = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_emit_to_first_visible_apply_ms' -DefaultValue $latestRecoveryOwnerEmitToFirstVisibleApplyMs
            $latestRecoveryBurstControlFallbackCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_control_fallback_count' -DefaultValue $latestRecoveryBurstControlFallbackCount
            $latestBridgeMediaMessagesReceived = [Math]::Max(
                $latestBridgeMediaMessagesReceived,
                (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_media_messages_received' -DefaultValue $latestBridgeMediaMessagesReceived))
            $latestMediaPlaneFramesSent = Get-StructuredLogIntField -Pairs $pairs -Key 'media_plane_frames_sent' -DefaultValue $latestMediaPlaneFramesSent
            $latestMediaPlaneAttached = Get-StructuredLogIntField -Pairs $pairs -Key 'media_plane_attached' -DefaultValue $latestMediaPlaneAttached
            $latestRecoveryBurstTimeoutCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_timeout_count' -DefaultValue $latestRecoveryBurstTimeoutCount
            $latestRecoveryBurstCompletedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_count' -DefaultValue $latestRecoveryBurstCompletedCount
            $latestRecoveryBurstRestartSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_restart_suppressed_count' -DefaultValue $latestRecoveryBurstRestartSuppressedCount
            $latestRecoveryBurstEncoderRerequestCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_encoder_rerequest_count' -DefaultValue $latestRecoveryBurstEncoderRerequestCount
            $latestRecoveryOwnerPendingForcedResetCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_pending_forced_reset_count' -DefaultValue $latestRecoveryOwnerPendingForcedResetCount
            $latestRecoveryKeyframeEmittedAfterForcedResetCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_emitted_after_forced_reset_count' -DefaultValue $latestRecoveryKeyframeEmittedAfterForcedResetCount
            $latestRecoveryBurstCompletedByHelperAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_helper_ack_count' -DefaultValue $latestRecoveryBurstCompletedByHelperAckCount
            $latestRecoveryBurstCompletedByAppliedHeadAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_applied_head_ack_count' -DefaultValue $latestRecoveryBurstCompletedByAppliedHeadAckCount
            $latestRecoveryBurstCompletedByLastVisibleApplyAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_last_visible_apply_ack_count' -DefaultValue $latestRecoveryBurstCompletedByLastVisibleApplyAckCount
            $latestRecoveryBurstCompletedByTimeoutCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_timeout_count' -DefaultValue $latestRecoveryBurstCompletedByTimeoutCount
            $latestRecoveryBurstCompletedByProtectedFramesCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_protected_frames_count' -DefaultValue $latestRecoveryBurstCompletedByProtectedFramesCount
            $latestRecoveryBurstProfileTransitionDeferredCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_profile_transition_deferred_count' -DefaultValue $latestRecoveryBurstProfileTransitionDeferredCount
            $latestRecoveryBurstProfileTransitionTakeoverCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_profile_transition_takeover_count' -DefaultValue $latestRecoveryBurstProfileTransitionTakeoverCount
            $latestRecoveryBurstStaleRequestSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_stale_request_suppressed_count' -DefaultValue $latestRecoveryBurstStaleRequestSuppressedCount
            $latestRecoveryBurstRequestSuppressedDueToHelperAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_request_suppressed_due_to_helper_ack_count' -DefaultValue $latestRecoveryBurstRequestSuppressedDueToHelperAckCount
            $latestRecoveryBurstStartedWhileHelperProofHealthyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_started_while_helper_proof_healthy_count' -DefaultValue $latestRecoveryBurstStartedWhileHelperProofHealthyCount
            $lastCompletedRecoveryEpochValue = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_epoch'
            if ($null -ne $lastCompletedRecoveryEpochValue) {
                if ([string]::Equals($lastCompletedRecoveryEpochValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryEpoch = -1
                }
                elseif ($lastCompletedRecoveryEpochValue -match '^-?[0-9]+$') {
                    $latestLastCompletedRecoveryEpoch = [int64]$lastCompletedRecoveryEpochValue
                }
            }

            $lastCompletedRecoveryOwnerValue = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_owner_frame_id'
            if ($null -ne $lastCompletedRecoveryOwnerValue) {
                if ([string]::Equals($lastCompletedRecoveryOwnerValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryOwnerFrameId = -1
                }
                elseif ($lastCompletedRecoveryOwnerValue -match '^-?[0-9]+$') {
                    $latestLastCompletedRecoveryOwnerFrameId = [int64]$lastCompletedRecoveryOwnerValue
                }
            }

            $lastCompletedRecoveryAckValue = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_ack_frame_id'
            if ($null -ne $lastCompletedRecoveryAckValue) {
                if ([string]::Equals($lastCompletedRecoveryAckValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryAckFrameId = -1
                }
                elseif ($lastCompletedRecoveryAckValue -match '^-?[0-9]+$') {
                    $latestLastCompletedRecoveryAckFrameId = [int64]$lastCompletedRecoveryAckValue
                }
            }

            $parsedLastCompletedRecoveryAckSource = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_ack_source'
            if ($null -ne $parsedLastCompletedRecoveryAckSource) {
                if ([string]::IsNullOrWhiteSpace($parsedLastCompletedRecoveryAckSource) -or
                    [string]::Equals($parsedLastCompletedRecoveryAckSource, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryAckSource = ''
                }
                else {
                    $latestLastCompletedRecoveryAckSource = [string]$parsedLastCompletedRecoveryAckSource
                }
            }

            $parsedLastCompletedRecoveryOwnerEmitToAckMs = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_owner_emit_to_ack_ms'
            if ($null -ne $parsedLastCompletedRecoveryOwnerEmitToAckMs -and
                $parsedLastCompletedRecoveryOwnerEmitToAckMs -match '^-?[0-9]+$') {
                $latestLastCompletedRecoveryOwnerEmitToAckMs = [int64]$parsedLastCompletedRecoveryOwnerEmitToAckMs
            }

            $parsedLastCompletedRecoveryCompletionKind = Get-StructuredLogFieldValue -Pairs $pairs -Key 'last_completed_recovery_completion_kind'
            if ($null -ne $parsedLastCompletedRecoveryCompletionKind) {
                if ([string]::IsNullOrWhiteSpace($parsedLastCompletedRecoveryCompletionKind) -or
                    [string]::Equals($parsedLastCompletedRecoveryCompletionKind, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $latestLastCompletedRecoveryCompletionKind = ''
                }
                else {
                    $latestLastCompletedRecoveryCompletionKind = [string]$parsedLastCompletedRecoveryCompletionKind
                }
            }
            $latestRecoveryCompletionAccountingMismatch = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_completion_accounting_mismatch' -DefaultValue $latestRecoveryCompletionAccountingMismatch
            $latestRecoveryOwnerPendingNonKeyHeldCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_pending_non_key_held_count' -DefaultValue $latestRecoveryOwnerPendingNonKeyHeldCount
            $latestRecoveryOwnerPendingNonKeyReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_pending_non_key_replaced_count' -DefaultValue $latestRecoveryOwnerPendingNonKeyReplacedCount
            $latestRecoveryOwnerUnackedNonKeyHeldCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_unacked_non_key_held_count' -DefaultValue $latestRecoveryOwnerUnackedNonKeyHeldCount
            $latestRecoveryOwnerUnackedNonKeyReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_unacked_non_key_replaced_count' -DefaultValue $latestRecoveryOwnerUnackedNonKeyReplacedCount
            $latestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_same_epoch_keyframe_suppressed_while_owner_unacked_count' -DefaultValue $latestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount
            $latestRecoveryOwnerReplacedBeforeAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_replaced_before_ack_count' -DefaultValue $latestRecoveryOwnerReplacedBeforeAckCount
            $latestHighFrameAgeSuppressedDuringOwnerAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_during_owner_ack_count' -DefaultValue $latestHighFrameAgeSuppressedDuringOwnerAckCount
            $latestRecoveryTimeoutWhileHelperHeadAdvancedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_timeout_while_helper_head_advanced_count' -DefaultValue $latestRecoveryTimeoutWhileHelperHeadAdvancedCount
            $latestSenderReceivedHelperProgressDuringContinuityLossCount = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_received_helper_progress_during_continuity_loss_count' -DefaultValue $latestSenderReceivedHelperProgressDuringContinuityLossCount
            $helperAckAfterFactSendValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_ack_after_fact_send_ms' -DefaultValue ''
            if ($helperAckAfterFactSendValue -match '^-?[0-9]+$') {
            $latestHelperAckAfterFactSendMs = [int64]$helperAckAfterFactSendValue
        }
        $latestPostAckModeGraceSuppressedHighFrameAgeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_ack_mode_grace_suppressed_high_frame_age_count' -DefaultValue $latestPostAckModeGraceSuppressedHighFrameAgeCount
        $latestBootstrapGraceSuppressedCatchUpCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bootstrap_grace_suppressed_catch_up_count' -DefaultValue $latestBootstrapGraceSuppressedCatchUpCount
        $latestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'catch_up_recovery_suppressed_due_to_remote_high_frame_age_count' -DefaultValue $latestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount
        $latestCatchUpExitWhileRemoteHighFrameAgePressureCount = Get-StructuredLogIntField -Pairs $pairs -Key 'catch_up_exit_while_remote_high_frame_age_pressure_count' -DefaultValue $latestCatchUpExitWhileRemoteHighFrameAgePressureCount
        $latestProtectedRecoveryFramesDispatchedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'protected_recovery_frames_dispatched_count' -DefaultValue $latestProtectedRecoveryFramesDispatchedCount
            $latestRecoveryProtectedFrameBlockedByOrdinaryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_protected_frame_blocked_by_ordinary_count' -DefaultValue $latestRecoveryProtectedFrameBlockedByOrdinaryCount
            $lastAcknowledgedRecoveryOwnerValue = Get-StructuredLogStringField -Pairs $pairs -Key 'last_acknowledged_recovery_owner_frame_id' -DefaultValue $latestLastAcknowledgedRecoveryOwnerFrameId
            if ($lastAcknowledgedRecoveryOwnerValue -match '^-?[0-9]+$') {
                $latestLastAcknowledgedRecoveryOwnerFrameId = [int64]$lastAcknowledgedRecoveryOwnerValue
            }
            $lastAcknowledgedHelperHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'last_acknowledged_helper_head_frame_id' -DefaultValue $latestLastAcknowledgedHelperHeadFrameId
            if ($lastAcknowledgedHelperHeadValue -match '^-?[0-9]+$') {
                $latestLastAcknowledgedHelperHeadFrameId = [int64]$lastAcknowledgedHelperHeadValue
            }
            $latestLastAcknowledgedHelperProofAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_acknowledged_helper_proof_age_ms' -DefaultValue $latestLastAcknowledgedHelperProofAgeMs
            $latestPersistedReleaseFloorEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'persisted_release_floor_epoch' -DefaultValue $latestPersistedReleaseFloorEpoch
            $latestSatisfiedRecoveryFloorFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'satisfied_recovery_floor_frame_id' -DefaultValue $latestSatisfiedRecoveryFloorFrameId
            $latestSatisfiedRecoveryFloorSource = Get-StructuredLogStringField -Pairs $pairs -Key 'satisfied_recovery_floor_source' -DefaultValue $latestSatisfiedRecoveryFloorSource
            $latestContinuitySignalIgnoredDueToSatisfiedFloorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'continuity_signal_ignored_due_to_satisfied_floor_count' -DefaultValue $latestContinuitySignalIgnoredDueToSatisfiedFloorCount
            $latestRecoveryLockClearedByAcknowledgedProofCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_lock_cleared_by_acknowledged_proof_count' -DefaultValue $latestRecoveryLockClearedByAcknowledgedProofCount
            $latestRecoveryLockLastClearReason = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_lock_last_clear_reason' -DefaultValue $latestRecoveryLockLastClearReason
            $latestHelperProgressPastOwnerWithoutBurstAckCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_progress_past_owner_without_burst_ack_count' -DefaultValue $latestHelperProgressPastOwnerWithoutBurstAckCount
            $latestPostRecoveryAgeGraceActive = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_age_grace_active' -DefaultValue $latestPostRecoveryAgeGraceActive
            $latestPostRecoveryAgeGraceSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_age_grace_suppressed_count' -DefaultValue $latestPostRecoveryAgeGraceSuppressedCount
            if ($latestHelperProgressPastOwnerWithoutBurstAckCount -gt 0) {
                $recoveryAckMissedDespiteHelperProgress = 1
            }
        }

        if ($line -like '*event=screenshare_health_snapshot;*') {
            [void]$healthSnapshotLines.Add($line)
            while ($healthSnapshotLines.Count -gt 24) {
                $healthSnapshotLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHealthSenderOperatingState = Get-StructuredLogStringField -Pairs $pairs -Key 'sender_operating_state' -DefaultValue $latestHealthSenderOperatingState
            $latestHealthSenderGuardState = Get-StructuredLogStringField -Pairs $pairs -Key 'sender_guard_state' -DefaultValue $latestHealthSenderGuardState
            $latestHealthHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestHealthHelperSessionPhase
            $latestHealthHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestHealthHelperRecoveryMechanism
            $latestHealthDominantLossClass = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_loss_class' -DefaultValue $latestHealthDominantLossClass
            $latestHealthDominantPressureBlocker = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_pressure_blocker' -DefaultValue $latestHealthDominantPressureBlocker
            $latestHealthDominantTroubleDomain = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_trouble_domain' -DefaultValue $latestHealthDominantTroubleDomain
            $latestHealthRecoveryActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_active' -DefaultValue $latestHealthRecoveryActive
            $latestHealthBaselineEstablished = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_established' -DefaultValue $latestHealthBaselineEstablished
            $latestHealthSteadyVisibleProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_active' -DefaultValue $latestHealthSteadyVisibleProgressActive
        }

        if ($line -like '*event=screenshare_sender_recovery_burst_started;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestRecoveryBurstStreamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch' -DefaultValue $latestRecoveryBurstStreamEpoch
            $latestRecoveryBurstActive = 1
            $latestRecoveryBurstPhase = 'requested'
            $startedGapToRequestValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_gap_to_keyframe_request_ms' -DefaultValue $latestRecoveryGapToKeyframeRequestMs
            if ($startedGapToRequestValue -match '^-?[0-9]+$') {
                $latestRecoveryGapToKeyframeRequestMs = [int64]$startedGapToRequestValue
            }
        }

        if ($line -like '*event=screenshare_visible_proof_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $remoteHelperVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_visible_head_frame_id' -DefaultValue $latestRemoteHelperVisibleHeadFrameId
            if ($remoteHelperVisibleHeadValue -match '^-?[0-9]+$') {
                $latestRemoteHelperVisibleHeadFrameId = [int64]$remoteHelperVisibleHeadValue
            }

            $remoteHelperVisibleRecoveryFloorValue = Get-StructuredLogStringField -Pairs $pairs -Key 'remote_helper_visible_recovery_floor_frame_id' -DefaultValue $latestRemoteHelperVisibleRecoveryFloorFrameId
            if ($remoteHelperVisibleRecoveryFloorValue -match '^-?[0-9]+$') {
                $latestRemoteHelperVisibleRecoveryFloorFrameId = [int64]$remoteHelperVisibleRecoveryFloorValue
            }

            $latestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'remote_helper_current_epoch_recovery_keyframe_apply_count' -DefaultValue $latestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount

            $lastAcknowledgedVisibleHelperHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'last_acknowledged_visible_helper_head_frame_id' -DefaultValue $latestLastAcknowledgedVisibleHelperHeadFrameId
            if ($lastAcknowledgedVisibleHelperHeadValue -match '^-?[0-9]+$') {
                $latestLastAcknowledgedVisibleHelperHeadFrameId = [int64]$lastAcknowledgedVisibleHelperHeadValue
            }

            $latestPersistedReleaseFloorEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'persisted_release_floor_epoch' -DefaultValue $latestPersistedReleaseFloorEpoch
            $latestSatisfiedRecoveryFloorFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'satisfied_recovery_floor_frame_id' -DefaultValue $latestSatisfiedRecoveryFloorFrameId
            $latestSatisfiedRecoveryFloorSource = Get-StructuredLogStringField -Pairs $pairs -Key 'satisfied_recovery_floor_source' -DefaultValue $latestSatisfiedRecoveryFloorSource
            $latestSatisfiedRecoveryFloorVisibleProofCount = Get-StructuredLogIntField -Pairs $pairs -Key 'satisfied_recovery_floor_visible_proof_count' -DefaultValue $latestSatisfiedRecoveryFloorVisibleProofCount
            $latestRecoveryBurstCompletedByVisibleRecoveryFloorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_visible_recovery_floor_count' -DefaultValue $latestRecoveryBurstCompletedByVisibleRecoveryFloorCount
            $latestRecoveryBurstCompletedByVisibleApplyFallbackCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_burst_completed_by_visible_apply_fallback_count' -DefaultValue $latestRecoveryBurstCompletedByVisibleApplyFallbackCount
            $latestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'continuity_signal_ignored_due_to_visible_satisfied_floor_count' -DefaultValue $latestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount
            $latestRecoveryLockClearedByVisibleProofCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_lock_cleared_by_visible_proof_count' -DefaultValue $latestRecoveryLockClearedByVisibleProofCount
            $latestRecoveryLockLastClearReason = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_lock_last_clear_reason' -DefaultValue $latestRecoveryLockLastClearReason
        }

        if ($line -like '*event=screenshare_control_fallback_queued;*' -or
            $line -like '*event=screenshare_control_bootstrap_retry_queued;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $fallbackReason = Get-StructuredLogStringField -Pairs $pairs -Key 'reason' -DefaultValue ''
            if ($fallbackReason -like 'recovery_burst_*') {
                $recoveryControlFallbackQueuedCount++
            }
            elseif (-not [string]::IsNullOrWhiteSpace($fallbackReason)) {
                $steadyStateControlFallbackQueuedCount++
            }
        }

        if ($line -like '*event=screenshare_sender_recovery_burst_owner_emitted;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestRecoveryBurstStreamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch' -DefaultValue $latestRecoveryBurstStreamEpoch
            $ownerFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_frame_id' -DefaultValue $latestRecoveryOwnerFrameId
            if ($ownerFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerFrameId = [int64]$ownerFrameValue
            }

            $ownerEmitLatencyValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_keyframe_request_to_owner_emit_ms' -DefaultValue $latestRecoveryKeyframeRequestToOwnerEmitMs
            if ($ownerEmitLatencyValue -match '^-?[0-9]+$') {
                $latestRecoveryKeyframeRequestToOwnerEmitMs = [int64]$ownerEmitLatencyValue
            }

            $latestRecoveryBurstPhase = 'owner_emitted_awaiting_helper_ack'
        }

        if ($line -like '*event=screenshare_sender_recovery_burst_completed;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestRecoveryBurstStreamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch' -DefaultValue $latestRecoveryBurstStreamEpoch
            $latestRecoveryBurstActive = 0
            $eventRecoveryBurstCompletedCount++
            $completedOwnerFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_frame_id' -DefaultValue $latestRecoveryOwnerFrameId
            if ($completedOwnerFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerFrameId = [int64]$completedOwnerFrameValue
            }

            $ownerToVisibleApplyValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_emit_to_first_visible_apply_ms' -DefaultValue $latestRecoveryOwnerEmitToFirstVisibleApplyMs
            if ($ownerToVisibleApplyValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerEmitToFirstVisibleApplyMs = [int64]$ownerToVisibleApplyValue
            }

            $ownerToAckValue = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_owner_emit_to_ack_ms' -DefaultValue $latestRecoveryOwnerEmitToAckMs
            if ($ownerToAckValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerEmitToAckMs = [int64]$ownerToAckValue
            }

            $ownerAckFrameValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_head_frame_id' -DefaultValue $latestRecoveryOwnerAckFrameId
            if ($ownerAckFrameValue -match '^-?[0-9]+$') {
                $latestRecoveryOwnerAckFrameId = [int64]$ownerAckFrameValue
            }

            $latestRecoveryAckSource = Get-StructuredLogStringField -Pairs $pairs -Key 'recovery_ack_source' -DefaultValue $latestRecoveryAckSource

            $completion = Get-StructuredLogStringField -Pairs $pairs -Key 'completion' -DefaultValue ''
            switch -Exact ($completion) {
                'helper_head_advance' {
                    $eventRecoveryBurstCompletedByHelperAckCount++
                    if ($latestRecoveryBurstStreamEpoch -gt 0) {
                        $latestLastCompletedRecoveryEpoch = $latestRecoveryBurstStreamEpoch
                    }

                    if ($latestRecoveryOwnerFrameId -ge 0) {
                        $latestLastCompletedRecoveryOwnerFrameId = $latestRecoveryOwnerFrameId
                    }

                    if ($latestRecoveryOwnerAckFrameId -ge 0) {
                        $latestLastCompletedRecoveryAckFrameId = $latestRecoveryOwnerAckFrameId
                    }

                    if (-not [string]::IsNullOrWhiteSpace($latestRecoveryAckSource) -and
                        -not [string]::Equals($latestRecoveryAckSource, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                        $latestLastCompletedRecoveryAckSource = $latestRecoveryAckSource
                    }

                    if ($latestRecoveryOwnerEmitToAckMs -ge 0) {
                        $latestLastCompletedRecoveryOwnerEmitToAckMs = $latestRecoveryOwnerEmitToAckMs
                    }

                    $latestLastCompletedRecoveryCompletionKind = 'helper_ack'

                    switch -Exact ($latestRecoveryAckSource) {
                        'applied_head' { $eventRecoveryBurstCompletedByAppliedHeadAckCount++ }
                        'last_visible_apply' { $eventRecoveryBurstCompletedByLastVisibleApplyAckCount++ }
                        'visible_recovery_floor' { $eventRecoveryBurstCompletedByVisibleRecoveryFloorCount++ }
                        'visible_apply_fallback' { $eventRecoveryBurstCompletedByVisibleApplyFallbackCount++ }
                        'helper_visible_receipt' { }
                    }

                    $latestRecoveryBurstPhase = 'completed'
                }
                'helper_visible_receipt' {
                    $eventRecoveryBurstCompletedByHelperAckCount++
                    if ($latestRecoveryBurstStreamEpoch -gt 0) {
                        $latestLastCompletedRecoveryEpoch = $latestRecoveryBurstStreamEpoch
                    }

                    if ($latestRecoveryOwnerFrameId -ge 0) {
                        $latestLastCompletedRecoveryOwnerFrameId = $latestRecoveryOwnerFrameId
                    }

                    if ($latestRecoveryOwnerAckFrameId -ge 0) {
                        $latestLastCompletedRecoveryAckFrameId = $latestRecoveryOwnerAckFrameId
                    }

                    if (-not [string]::IsNullOrWhiteSpace($latestRecoveryAckSource) -and
                        -not [string]::Equals($latestRecoveryAckSource, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                        $latestLastCompletedRecoveryAckSource = $latestRecoveryAckSource
                    }

                    if ($latestRecoveryOwnerEmitToAckMs -ge 0) {
                        $latestLastCompletedRecoveryOwnerEmitToAckMs = $latestRecoveryOwnerEmitToAckMs
                    }

                    $latestLastCompletedRecoveryCompletionKind = 'helper_ack'

                    switch -Exact ($latestRecoveryAckSource) {
                        'applied_head' { $eventRecoveryBurstCompletedByAppliedHeadAckCount++ }
                        'last_visible_apply' { $eventRecoveryBurstCompletedByLastVisibleApplyAckCount++ }
                        'visible_recovery_floor' { $eventRecoveryBurstCompletedByVisibleRecoveryFloorCount++ }
                        'visible_apply_fallback' { $eventRecoveryBurstCompletedByVisibleApplyFallbackCount++ }
                        'helper_visible_receipt' { }
                    }

                    $latestRecoveryBurstPhase = 'completed'
                }
                'timeout' {
                    $eventRecoveryBurstCompletedByTimeoutCount++
                    if ($latestRecoveryBurstStreamEpoch -gt 0) {
                        $latestLastCompletedRecoveryEpoch = $latestRecoveryBurstStreamEpoch
                    }
                    if ($latestRecoveryOwnerFrameId -ge 0) {
                        $latestLastCompletedRecoveryOwnerFrameId = $latestRecoveryOwnerFrameId
                    }
                    $latestLastCompletedRecoveryAckFrameId = -1
                    $latestLastCompletedRecoveryAckSource = ''
                    $latestLastCompletedRecoveryOwnerEmitToAckMs = -1
                    $latestLastCompletedRecoveryCompletionKind = 'timeout'
                    if ($latestHelperProgressPastOwnerWithoutBurstAckCount -gt 0) {
                        $recoveryAckMissedDespiteHelperProgress = 1
                    }
                    $latestRecoveryBurstPhase = 'timed_out'
                }
                default {
                    if (-not [string]::IsNullOrWhiteSpace($completion)) {
                        $recoveryBurstCompletedWithoutHelperAdvance = 1
                    }

                    $latestRecoveryBurstPhase = 'completed'
                }
            }
        }

        if ($line -like '*event=screenshare_sender_recovery_owner_pending_forced_reset;*') {
            $eventRecoveryOwnerPendingForcedResetCount++
        }

        if ($line -like '*event=screenshare_sender_recovery_keyframe_emitted_after_forced_reset;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $eventRecoveryKeyframeEmittedAfterForcedResetCount++
            $latestRecoveryKeyframeEmittedAfterForcedResetCount = [Math]::Max(
                $latestRecoveryKeyframeEmittedAfterForcedResetCount,
                $eventRecoveryKeyframeEmittedAfterForcedResetCount)
            $emittedLatencyMs = Get-StructuredLogIntField -Pairs $pairs -Key 'latency_ms' -DefaultValue $latestRecoveryKeyframeRequestToOwnerEmitMs
            if ($emittedLatencyMs -ge 0) {
                $latestRecoveryKeyframeRequestToOwnerEmitMs = $emittedLatencyMs
            }
        }

        if ($line -like '*event=screenshare_control_bootstrap_retry_skipped;*' -and $line -like '*skip_reason=recovery_burst_resolved*') {
            $recoveryControlBootstrapRetrySkippedDueToBurstResolvedCount++
        }

        if ($line -like '*event=screenshare_control_bootstrap_retry_queued_after_burst_resolution;*') {
            $recoveryControlBootstrapRetryQueuedAfterBurstResolutionCount++
        }

        if ($line -like '*event=screenshare_transport_batch_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestAvgFragmentsPerFrame = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_fragments_per_frame' -DefaultValue $latestAvgFragmentsPerFrame
            $latestAvgPayloadsPerFrame = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_transport_payloads_per_frame' -DefaultValue $latestAvgPayloadsPerFrame
            $latestBatchPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'batched_payloads_sent' -DefaultValue $latestBatchPayloadCount
            $latestLegacyPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'legacy_fragment_payloads_sent' -DefaultValue $latestLegacyPayloadCount
            $latestOrdinaryNonKeyBatchedPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'ordinary_non_key_batched_payloads_sent' -DefaultValue $latestOrdinaryNonKeyBatchedPayloadCount
            $latestOrdinaryNonKeyLegacyPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'ordinary_non_key_legacy_payloads_sent' -DefaultValue $latestOrdinaryNonKeyLegacyPayloadCount
            $latestKeyframeRecoveryBatchedPayloadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'keyframe_recovery_batched_payloads_sent' -DefaultValue $latestKeyframeRecoveryBatchedPayloadCount
        }

        if ($line -match 'event=screenshare_viewer_frame_applied; role=helper_remote; age_ms=([0-9-]+);.*frames_completed=([0-9-]+);.*frames_enqueued_for_decode=([0-9-]+);.*frames_dropped_before_decode=([0-9-]+);.*frames_decoded=([0-9-]+);.*frames_dropped_after_decode=([0-9-]+);.*frames_applied=([0-9-]+);.*need_more_input_count=([0-9-]+);.*completed_without_picture_count=([0-9-]+);.*avg_decode_duration_ms=([0-9.]+);.*avg_apply_interval_ms=([0-9.]+)') {
            [void]$helperApply.Add([int]$matches[1])
            $latestHelperFramesCompleted = [int]$matches[2]
            $latestHelperFramesEnqueuedForDecode = [int]$matches[3]
            $latestHelperFramesDroppedBeforeDecode = [int]$matches[4]
            $latestHelperFramesDecoded = [int]$matches[5]
            $latestHelperFramesDroppedAfterDecode = [int]$matches[6]
            $latestHelperFramesApplied = [int]$matches[7]
            $latestHelperNeedMoreInputCount = [int]$matches[8]
            $latestHelperCompletedWithoutPictureCount = [int]$matches[9]
            $latestHelperDecodeDurationMs = [double]$matches[10]
            $latestHelperApplyIntervalMs = [double]$matches[11]
        }

        if ($line -like '*event=screenshare_helper_frame_loss_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestSummaryDominantLossClass = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_loss_class' -DefaultValue $latestSummaryDominantLossClass
            $latestHelperReassemblerLossCount = Get-StructuredLogIntField -Pairs $pairs -Key 'reassembler_loss_count'
            $latestHelperEnqueueRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'enqueue_reject_count'
            $latestHelperWaitingForRecoveryKeyframeRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'waiting_for_recovery_keyframe_reject_count'
            $latestHelperRecoveryWaitRejectBeforeRunwayCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_wait_reject_before_runway_count' -DefaultValue $latestHelperWaitingForRecoveryKeyframeRejectCount
            $latestHelperRecoveryRunwayOverflowRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_overflow_reject_count'
            $latestHelperSuppressedEmitDuringRecoveryWaitCount = Get-StructuredLogIntField -Pairs $pairs -Key 'suppressed_emit_during_recovery_wait_count'
            $latestHelperSoftStaleCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'soft_stale_cleanup_count'
            $latestHelperStaleSupersededRecoverySuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_superseded_recovery_suppressed_count' -DefaultValue $latestHelperSoftStaleCleanupCount
            $latestHelperBlockedByReservedRecoveryFrameRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'blocked_by_reserved_recovery_frame_reject_count'
            $latestHelperOlderEpochIgnoredDuringRecoveryLockCount = Get-StructuredLogIntField -Pairs $pairs -Key 'older_epoch_ignored_during_recovery_lock_count'
            $latestHelperNewerEpochNonKeyIgnoredDuringLockCount = Get-StructuredLogIntField -Pairs $pairs -Key 'newer_epoch_non_key_ignored_during_lock_count'
            $latestHelperDeferredPostRecoveryCandidateReplaceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'deferred_post_recovery_candidate_replace_count'
            $latestHelperDecodeWorkerDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_worker_drop_count'
            $latestHelperPostDecodeDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_decode_drop_count'
            $latestHelperDecodeQueueOverflowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_queue_overflow_count'
            $latestHelperDecodeAgeBudgetCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_age_budget_count'
            $latestHelperDecodeGenerationChangedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_generation_changed_count'
            $latestHelperDecodeStoppedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_stopped_count'
            $latestHelperDecodedApplyQueueOverflowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decoded_apply_queue_overflow_count' -DefaultValue $latestHelperDecodedApplyQueueOverflowCount
            $latestHelperDecodedFrameReplacedBeforeApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'decoded_frame_replaced_before_apply_count' -FallbackAfterKey 'decoded_apply_queue_overflow_count' -FallbackOffset 1
            $latestHelperStaleDroppedAfterDecodeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_dropped_after_decode_count'
            $latestHelperDroppedWaitingForRecoveryKeyframeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'dropped_waiting_for_recovery_keyframe_count' -FallbackAfterKey 'decode_stopped_count' -FallbackOffset 2
            $latestHelperGapNonKeyPrunedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'gap_non_key_pruned_count'
            $latestHelperFutureTailQuarantinedDuringGapCount = Get-StructuredLogIntField -Pairs $pairs -Key 'future_tail_quarantined_during_gap_count'
            $latestHelperFutureTailQuarantinedAfterGapCount = Get-StructuredLogIntField -Pairs $pairs -Key 'future_tail_quarantined_after_gap_count' -DefaultValue $latestHelperFutureTailQuarantinedDuringGapCount
            $latestHelperPreCandidateGapTailRejectedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pre_candidate_gap_tail_rejected_count'
            $latestHelperRecoveryCandidatePresentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_candidate_present_count'
            $visibleRecoveryFloorValue = Get-StructuredLogStringField -Pairs $pairs -Key 'visible_recovery_floor_frame_id' -DefaultValue '(none)'
            $latestHelperVisibleRecoveryFloorFrameId = if ($visibleRecoveryFloorValue -match '^-?[0-9]+$') { [int64]$visibleRecoveryFloorValue } else { $latestHelperVisibleRecoveryFloorFrameId }
            $stableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'stable_visible_head_frame_id' -DefaultValue '(none)'
            $latestHelperStableVisibleHeadFrameId = if ($stableVisibleHeadValue -match '^-?[0-9]+$') { [int64]$stableVisibleHeadValue } else { $latestHelperStableVisibleHeadFrameId }
            $appliedHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'applied_head_frame_id' -DefaultValue '(none)'
            $latestHelperAppliedHeadFrameId = if ($appliedHeadValue -match '^-?[0-9]+$') { [int64]$appliedHeadValue } else { $latestHelperAppliedHeadFrameId }
            $orderedEmitHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'ordered_emit_head_frame_id' -DefaultValue '(none)'
            $latestHelperOrderedEmitHeadFrameId = if ($orderedEmitHeadValue -match '^-?[0-9]+$') { [int64]$orderedEmitHeadValue } else { $latestHelperOrderedEmitHeadFrameId }
            $winningRecoveryValue = Get-StructuredLogStringField -Pairs $pairs -Key 'winning_recovery_frame_id' -DefaultValue '(none)'
            $latestHelperWinningRecoveryFrameId = if ($winningRecoveryValue -match '^-?[0-9]+$') { [int64]$winningRecoveryValue } else { $latestHelperWinningRecoveryFrameId }
            $visibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'visible_head_frame_id' -DefaultValue '(none)'
            $latestHelperVisibleHeadFrameId = if ($visibleHeadValue -match '^-?[0-9]+$') { [int64]$visibleHeadValue } else { $latestHelperVisibleHeadFrameId }
            $latestHelperSupersededRecoveryTailCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'superseded_recovery_tail_cleanup_count' -DefaultValue $latestHelperSupersededRecoveryTailCleanupCount
            $latestHelperLateSameEpochAfterHeadAdvancedDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_same_epoch_after_head_advanced_drop_count' -DefaultValue $latestHelperLateSameEpochAfterHeadAdvancedDropCount
            $latestHelperStaleRunwayWindowAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_runway_window_abort_count' -DefaultValue $latestHelperStaleRunwayWindowAbortCount
            $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'runway_candidate_expired_after_head_advance_count' -DefaultValue $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount
            $latestHelperRunwayFollowersEmittedWithinActionableWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'runway_followers_emitted_within_actionable_window_count' -DefaultValue $latestHelperRunwayFollowersEmittedWithinActionableWindowCount
            $latestHelperRecoveryOwnerReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_replaced_count' -DefaultValue $latestHelperRecoveryOwnerReplacedCount
            $latestHelperOlderEpochCleanupAfterEpochAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'older_epoch_cleanup_after_epoch_advance_count' -DefaultValue $latestHelperOlderEpochCleanupAfterEpochAdvanceCount
            $latestHelperLateFragmentAfterAppliedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_applied_head_count' -DefaultValue $latestHelperLateFragmentAfterAppliedHeadCount
            $latestHelperLateFragmentAfterOrderedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_ordered_head_count' -DefaultValue $latestHelperLateFragmentAfterOrderedHeadCount
            $latestHelperLateFragmentAfterStableVisibleHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_stable_visible_head_count'
            $latestHelperLateFragmentAfterVisibleRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_visible_recovery_count'
            $latestHelperPreCandidateGapTailEmittedToViewerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pre_candidate_gap_tail_emitted_to_viewer_count' -DefaultValue $latestHelperPreCandidateGapTailEmittedToViewerCount
            $latestHelperActionableLateFragmentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_late_fragment_count' -DefaultValue $latestHelperActionableLateFragmentCount
            $latestHelperRecoveryRunwayContiguousFollowerBufferCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_contiguous_follower_buffer_count'
            $latestHelperRecoveryRunwayContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_contiguous_follower_apply_count'
            $latestHelperRecoveryRunwayAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_abort_count'
            $latestHelperRecoveryKeyframeResyncCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_resync_count'
            $latestHelperGapActive = Get-StructuredLogIntField -Pairs $pairs -Key 'gap_active'
            $gapExpectedValue = Get-StructuredLogStringField -Pairs $pairs -Key 'gap_expected_frame_id' -DefaultValue '(none)'
            $bufferedRecoveryKeyframeValue = Get-StructuredLogStringField -Pairs $pairs -Key 'buffered_recovery_keyframe_frame_id' -DefaultValue '(none)'
            $latestHelperGapExpectedFrameId = if ($gapExpectedValue -match '^-?[0-9]+$') { [int64]$gapExpectedValue } else { -1 }
            $latestHelperBufferedRecoveryKeyframeFrameId = if ($bufferedRecoveryKeyframeValue -match '^-?[0-9]+$') { [int64]$bufferedRecoveryKeyframeValue } else { -1 }
            $latestHelperFutureNonKeyBufferedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'future_non_key_buffered_count'
            $latestHelperRecoveryFollowerWindowBufferedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_follower_window_buffered_count'
            $latestHelperRecoveryFollowerWindowAppliedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_follower_window_applied_count'
            $latestHelperRecoveryFollowerWindowTrimmedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_follower_window_trimmed_count'
            $latestHelperProtectedRecoveryDeliveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'protected_recovery_delivery_count'
            $latestHelperRecoveryProgressCorridorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_count'
            $latestHelperRecoveryProgressCorridorSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_success_count'
            $latestHelperRecoveryProgressCorridorAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_abort_count'
            $latestHelperRecoveryProgressCorridorAppliedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_applied_count'
            $latestHelperRecoveryKeyframePendingVisibleApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_pending_visible_apply_count'
            $latestHelperStartupCorridorBufferedFollowerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_buffered_follower_count'
            $latestHelperStartupCorridorReleaseCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_release_count'
            $latestHelperStartupCorridorAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_abort_count'
            $latestHelperStartupCorridorAbortReason = Get-StructuredLogStringField -Pairs $pairs -Key 'startup_corridor_abort_reason' -DefaultValue $latestHelperStartupCorridorAbortReason
            $latestHelperDominantAdmissionRejectReason = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_helper_admission_reject_reason' -DefaultValue ''
            $latestHelperPostRecoveryVisibleGenerationResetCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_visible_generation_reset_count'
            $latestHelperPostRecoveryPurgedPreRecoveryFollowerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_purged_pre_recovery_follower_count'
            $latestHelperPostRecoveryStaleDropBypassCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_stale_drop_bypass_count'
            $latestHelperLateFragmentAfterSuccessfulRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_successful_recovery_count'
            $latestHelperUnattributedLossCount = Get-StructuredLogIntField -Pairs $pairs -Key 'unattributed_loss_count'
            $latestHelperRecentLosses = Get-StructuredLogStringField -Pairs $pairs -Key 'recent_losses'
        }

        if ($line -like '*event=screenshare_helper_quality_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestSummaryDominantLossClass = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_loss_class' -DefaultValue $latestSummaryDominantLossClass
            $latestHelperVisibleApplyRatio = Get-StructuredLogFloatField -Pairs $pairs -Key 'visible_apply_ratio'
            $latestHelperAvgDecodeCompleteToVisibleApplyMs = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_decode_complete_to_visible_apply_ms' -DefaultValue $latestHelperAvgDecodeCompleteToVisibleApplyMs
            $latestHelperAvgUiPostApplyMs = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_ui_post_apply_ms' -DefaultValue $latestHelperAvgUiPostApplyMs
            $latestHelperAvgVisibleHeadLagFrames = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_visible_head_lag_frames' -DefaultValue $latestHelperAvgVisibleHeadLagFrames
            $latestHelperAvgStableHeadLagFrames = Get-StructuredLogFloatField -Pairs $pairs -Key 'avg_stable_head_lag_frames' -DefaultValue $latestHelperAvgStableHeadLagFrames
            $latestHelperLastReservedApplyHoldMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_reserved_apply_hold_ms' -DefaultValue $latestHelperLastReservedApplyHoldMs
            $latestHelperLastRecoveryProgressCorridorHoldMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_recovery_progress_corridor_hold_ms' -DefaultValue $latestHelperLastRecoveryProgressCorridorHoldMs
            $latestHelperLastRecoveryRunwayAbortHoldMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_recovery_runway_abort_hold_ms' -DefaultValue $latestHelperLastRecoveryRunwayAbortHoldMs
            $latestHelperLastRecoveryProgressCorridorAbortReason = Get-StructuredLogStringField -Pairs $pairs -Key 'last_recovery_progress_corridor_abort_reason' -DefaultValue $latestHelperLastRecoveryProgressCorridorAbortReason
            $latestHelperGapCount = Get-StructuredLogIntField -Pairs $pairs -Key 'gap_count'
            $latestHelperRecoveryKeyframeApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_apply_count'
            $latestHelperResyncCount = Get-StructuredLogIntField -Pairs $pairs -Key 'resync_count'
            $latestHelperDominantReassemblerRootCause = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_reassembler_root_cause' -DefaultValue ''
            $latestHelperDominantAdmissionRejectReason = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_helper_admission_reject_reason' -DefaultValue $latestHelperDominantAdmissionRejectReason
            $latestHelperRecoveryWaitRejectBeforeRunwayCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_wait_reject_before_runway_count' -DefaultValue $latestHelperRecoveryWaitRejectBeforeRunwayCount
            $latestHelperRecoveryRunwayOverflowRejectCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_overflow_reject_count' -DefaultValue $latestHelperRecoveryRunwayOverflowRejectCount
            $latestHelperSuppressedEmitDuringRecoveryWaitCount = Get-StructuredLogIntField -Pairs $pairs -Key 'suppressed_emit_during_recovery_wait_count' -DefaultValue $latestHelperSuppressedEmitDuringRecoveryWaitCount
            $latestHelperSoftStaleCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'soft_stale_cleanup_count' -DefaultValue $latestHelperSoftStaleCleanupCount
            $latestHelperStaleSupersededRecoverySuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_superseded_recovery_suppressed_count' -DefaultValue $latestHelperSoftStaleCleanupCount
            $latestHelperPreCandidateGapTailEmittedToViewerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pre_candidate_gap_tail_emitted_to_viewer_count' -DefaultValue $latestHelperPreCandidateGapTailEmittedToViewerCount
            $latestHelperRecoveryCandidatePresentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_candidate_present_count' -DefaultValue $latestHelperRecoveryCandidatePresentCount
            $visibleRecoveryFloorValue = Get-StructuredLogStringField -Pairs $pairs -Key 'visible_recovery_floor_frame_id' -DefaultValue $latestHelperVisibleRecoveryFloorFrameId
            $latestHelperVisibleRecoveryFloorFrameId = if ($visibleRecoveryFloorValue -match '^-?[0-9]+$') { [int64]$visibleRecoveryFloorValue } else { $latestHelperVisibleRecoveryFloorFrameId }
            $stableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'stable_visible_head_frame_id' -DefaultValue $latestHelperStableVisibleHeadFrameId
            $latestHelperStableVisibleHeadFrameId = if ($stableVisibleHeadValue -match '^-?[0-9]+$') { [int64]$stableVisibleHeadValue } else { $latestHelperStableVisibleHeadFrameId }
            $appliedHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'applied_head_frame_id' -DefaultValue $latestHelperAppliedHeadFrameId
            $latestHelperAppliedHeadFrameId = if ($appliedHeadValue -match '^-?[0-9]+$') { [int64]$appliedHeadValue } else { $latestHelperAppliedHeadFrameId }
            $orderedEmitHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'ordered_emit_head_frame_id' -DefaultValue $latestHelperOrderedEmitHeadFrameId
            $latestHelperOrderedEmitHeadFrameId = if ($orderedEmitHeadValue -match '^-?[0-9]+$') { [int64]$orderedEmitHeadValue } else { $latestHelperOrderedEmitHeadFrameId }
            $winningRecoveryValue = Get-StructuredLogStringField -Pairs $pairs -Key 'winning_recovery_frame_id' -DefaultValue $latestHelperWinningRecoveryFrameId
            $latestHelperWinningRecoveryFrameId = if ($winningRecoveryValue -match '^-?[0-9]+$') { [int64]$winningRecoveryValue } else { $latestHelperWinningRecoveryFrameId }
            $visibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'visible_head_frame_id' -DefaultValue $latestHelperVisibleHeadFrameId
            $latestHelperVisibleHeadFrameId = if ($visibleHeadValue -match '^-?[0-9]+$') { [int64]$visibleHeadValue } else { $latestHelperVisibleHeadFrameId }
            $latestHelperSupersededRecoveryTailCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'superseded_recovery_tail_cleanup_count' -DefaultValue $latestHelperSupersededRecoveryTailCleanupCount
            $latestHelperLateSameEpochAfterHeadAdvancedDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_same_epoch_after_head_advanced_drop_count' -DefaultValue $latestHelperLateSameEpochAfterHeadAdvancedDropCount
            $latestHelperStaleRunwayWindowAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'stale_runway_window_abort_count' -DefaultValue $latestHelperStaleRunwayWindowAbortCount
            $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'runway_candidate_expired_after_head_advance_count' -DefaultValue $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount
            $latestHelperRunwayFollowersEmittedWithinActionableWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'runway_followers_emitted_within_actionable_window_count' -DefaultValue $latestHelperRunwayFollowersEmittedWithinActionableWindowCount
            $latestHelperRecoveryOwnerReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_replaced_count' -DefaultValue $latestHelperRecoveryOwnerReplacedCount
            $latestHelperOlderEpochCleanupAfterEpochAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'older_epoch_cleanup_after_epoch_advance_count' -DefaultValue $latestHelperOlderEpochCleanupAfterEpochAdvanceCount
            $latestHelperPreCandidateGapTailRejectedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pre_candidate_gap_tail_rejected_count' -DefaultValue $latestHelperPreCandidateGapTailRejectedCount
            $latestHelperLateFragmentAfterAppliedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_applied_head_count' -DefaultValue $latestHelperLateFragmentAfterAppliedHeadCount
            $latestHelperLateFragmentAfterOrderedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_ordered_head_count' -DefaultValue $latestHelperLateFragmentAfterOrderedHeadCount
            $latestHelperLateFragmentAfterStableVisibleHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_stable_visible_head_count' -DefaultValue $latestHelperLateFragmentAfterStableVisibleHeadCount
            $latestHelperLateFragmentAfterVisibleRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_visible_recovery_count' -DefaultValue $latestHelperLateFragmentAfterVisibleRecoveryCount
            $latestHelperActionableLateFragmentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_late_fragment_count' -DefaultValue $latestHelperActionableLateFragmentCount
            $latestHelperRecoveryRunwayContiguousFollowerBufferCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_contiguous_follower_buffer_count' -DefaultValue $latestHelperRecoveryRunwayContiguousFollowerBufferCount
            $latestHelperRecoveryRunwayContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_contiguous_follower_apply_count' -DefaultValue $latestHelperRecoveryRunwayContiguousFollowerApplyCount
            $latestHelperRecoveryRunwayAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_runway_abort_count' -DefaultValue $latestHelperRecoveryRunwayAbortCount
            $latestHelperProtectedRecoveryDeliveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'protected_recovery_delivery_count' -DefaultValue $latestHelperProtectedRecoveryDeliveryCount
            $latestHelperRecoveryProgressCorridorCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_count' -DefaultValue $latestHelperRecoveryProgressCorridorCount
            $latestHelperRecoveryProgressCorridorSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_success_count' -DefaultValue $latestHelperRecoveryProgressCorridorSuccessCount
            $latestHelperRecoveryProgressCorridorAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_abort_count' -DefaultValue $latestHelperRecoveryProgressCorridorAbortCount
            $latestHelperRecoveryProgressCorridorAppliedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_progress_corridor_applied_count' -DefaultValue $latestHelperRecoveryProgressCorridorAppliedCount
            $latestHelperRecoveryKeyframePendingVisibleApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_pending_visible_apply_count' -DefaultValue $latestHelperRecoveryKeyframePendingVisibleApplyCount
            $latestHelperStartupCorridorBufferedFollowerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_buffered_follower_count' -DefaultValue $latestHelperStartupCorridorBufferedFollowerCount
            $latestHelperStartupCorridorReleaseCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_release_count' -DefaultValue $latestHelperStartupCorridorReleaseCount
            $latestHelperStartupCorridorAbortCount = Get-StructuredLogIntField -Pairs $pairs -Key 'startup_corridor_abort_count' -DefaultValue $latestHelperStartupCorridorAbortCount
            $latestHelperStartupCorridorAbortReason = Get-StructuredLogStringField -Pairs $pairs -Key 'startup_corridor_abort_reason' -DefaultValue $latestHelperStartupCorridorAbortReason
            $latestHelperRecoveryWindowActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_active' -DefaultValue $latestHelperRecoveryWindowActive
            $latestHelperActiveRecoveryWindowEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_epoch' -DefaultValue $latestHelperActiveRecoveryWindowEpoch
            $latestHelperActiveRecoveryWindowRecoveryFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_recovery_frame_id' -DefaultValue $latestHelperActiveRecoveryWindowRecoveryFrameId
            $latestHelperRecoveryWindowContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_contiguous_follower_apply_count' -DefaultValue $latestHelperRecoveryWindowContiguousFollowerApplyCount
            $latestHelperLateFragmentAfterSuccessfulRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_successful_recovery_count' -DefaultValue $latestHelperLateFragmentAfterSuccessfulRecoveryCount
            [void]$helperQualitySummaryLines.Add($line)
            while ($helperQualitySummaryLines.Count -gt 8) {
                $helperQualitySummaryLines.RemoveAt(0)
            }
        }

        if ($line -match 'event=screenshare_helper_decode_worker_summary; role=helper_remote;.*max_pending_encoded_depth=([0-9-]+);.*max_pending_decoded_depth=([0-9-]+);.*avg_enqueue_to_decode_start_ms=([0-9.]+);.*avg_enqueue_to_drop_ms=([0-9.]+);.*decode_worker_drop_queue_overflow_count=([0-9-]+);.*decode_worker_drop_age_budget_count=([0-9-]+);.*decode_worker_drop_generation_count=([0-9-]+);.*decode_worker_drop_stopped_count=([0-9-]+)') {
            $latestHelperMaxPendingEncodedDepth = [int]$matches[1]
            $latestHelperMaxPendingDecodedDepth = [int]$matches[2]
            $latestHelperAvgEnqueueToDecodeStartMs = [double]$matches[3]
            $latestHelperAvgEnqueueToDropMs = [double]$matches[4]
            $latestHelperDecodeWorkerDropQueueOverflowCount = [int]$matches[5]
            $latestHelperDecodeWorkerDropAgeBudgetCount = [int]$matches[6]
            $latestHelperDecodeWorkerDropGenerationCount = [int]$matches[7]
            $latestHelperDecodeWorkerDropStoppedCount = [int]$matches[8]
        }

        if ($line -like '*event=screenshare_helper_upstream_latency_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperUpstreamCaptureToFrameReadyAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_frame_ready_avg_ms' -DefaultValue $latestHelperUpstreamCaptureToFrameReadyAvgMs
            $latestHelperUpstreamCaptureToFrameReadyMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_frame_ready_median_ms' -DefaultValue $latestHelperUpstreamCaptureToFrameReadyMedianMs
            $latestHelperUpstreamCaptureToFrameReadyP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_frame_ready_p95_ms' -DefaultValue $latestHelperUpstreamCaptureToFrameReadyP95Ms
            $latestHelperUpstreamCaptureToFrameReadyMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_frame_ready_max_ms' -DefaultValue $latestHelperUpstreamCaptureToFrameReadyMaxMs
            $latestHelperUpstreamFrameReadyToViewerAcceptAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'frame_ready_to_viewer_accept_avg_ms' -DefaultValue $latestHelperUpstreamFrameReadyToViewerAcceptAvgMs
            $latestHelperUpstreamFrameReadyToViewerAcceptMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'frame_ready_to_viewer_accept_median_ms' -DefaultValue $latestHelperUpstreamFrameReadyToViewerAcceptMedianMs
            $latestHelperUpstreamFrameReadyToViewerAcceptP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'frame_ready_to_viewer_accept_p95_ms' -DefaultValue $latestHelperUpstreamFrameReadyToViewerAcceptP95Ms
            $latestHelperUpstreamFrameReadyToViewerAcceptMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'frame_ready_to_viewer_accept_max_ms' -DefaultValue $latestHelperUpstreamFrameReadyToViewerAcceptMaxMs
            $latestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'viewer_accept_to_decode_enqueue_avg_ms' -DefaultValue $latestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs
            $latestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'viewer_accept_to_decode_enqueue_median_ms' -DefaultValue $latestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs
            $latestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'viewer_accept_to_decode_enqueue_p95_ms' -DefaultValue $latestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms
            $latestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'viewer_accept_to_decode_enqueue_max_ms' -DefaultValue $latestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs
            $latestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_enqueue_to_decode_start_avg_ms' -DefaultValue $latestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs
            $latestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_enqueue_to_decode_start_median_ms' -DefaultValue $latestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs
            $latestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_enqueue_to_decode_start_p95_ms' -DefaultValue $latestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms
            $latestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'decode_enqueue_to_decode_start_max_ms' -DefaultValue $latestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs
            $latestHelperUpstreamCaptureToDecodeStartAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_decode_start_avg_ms' -DefaultValue $latestHelperUpstreamCaptureToDecodeStartAvgMs
            $latestHelperUpstreamCaptureToDecodeStartMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_decode_start_median_ms' -DefaultValue $latestHelperUpstreamCaptureToDecodeStartMedianMs
            $latestHelperUpstreamCaptureToDecodeStartP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_decode_start_p95_ms' -DefaultValue $latestHelperUpstreamCaptureToDecodeStartP95Ms
            $latestHelperUpstreamCaptureToDecodeStartMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_decode_start_max_ms' -DefaultValue $latestHelperUpstreamCaptureToDecodeStartMaxMs
            $latestHelperUpstreamWorstEpochByCaptureToDecodeStart = Get-StructuredLogIntField -Pairs $pairs -Key 'worst_epoch_by_capture_to_decode_start' -DefaultValue $latestHelperUpstreamWorstEpochByCaptureToDecodeStart
            $latestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'worst_epoch_capture_to_decode_start_avg_ms' -DefaultValue $latestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs
            $latestHelperDominantUpstreamLatencyStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_upstream_latency_stage' -DefaultValue $latestHelperDominantUpstreamLatencyStage
            [void]$helperUpstreamLatencySummaryLines.Add($line)
            while ($helperUpstreamLatencySummaryLines.Count -gt 8) {
                $helperUpstreamLatencySummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_ready_path_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperReadyPathCaptureToFirstFragmentObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_first_fragment_observed_avg_ms' -DefaultValue $latestHelperReadyPathCaptureToFirstFragmentObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperReadyPathCaptureToFirstFragmentObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_first_fragment_observed_median_ms' -DefaultValue $latestHelperReadyPathCaptureToFirstFragmentObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperReadyPathCaptureToFirstFragmentObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_first_fragment_observed_p95_ms' -DefaultValue $latestHelperReadyPathCaptureToFirstFragmentObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperReadyPathCaptureToFirstFragmentObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_first_fragment_observed_max_ms' -DefaultValue $latestHelperReadyPathCaptureToFirstFragmentObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'first_fragment_to_last_fragment_observed_avg_ms' -DefaultValue $latestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'first_fragment_to_last_fragment_observed_median_ms' -DefaultValue $latestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'first_fragment_to_last_fragment_observed_p95_ms' -DefaultValue $latestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'first_fragment_to_last_fragment_observed_max_ms' -DefaultValue $latestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_fragment_to_assembly_complete_avg_ms' -DefaultValue $latestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_fragment_to_assembly_complete_median_ms' -DefaultValue $latestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'last_fragment_to_assembly_complete_p95_ms' -DefaultValue $latestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'last_fragment_to_assembly_complete_max_ms' -DefaultValue $latestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'assembly_complete_to_frame_emitted_avg_ms' -DefaultValue $latestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 13
            $latestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'assembly_complete_to_frame_emitted_median_ms' -DefaultValue $latestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 14
            $latestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'assembly_complete_to_frame_emitted_p95_ms' -DefaultValue $latestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 15
            $latestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'assembly_complete_to_frame_emitted_max_ms' -DefaultValue $latestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 16
            $latestHelperDominantReadyPathStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_ready_path_stage' -DefaultValue $latestHelperDominantReadyPathStage
            [void]$helperReadyPathSummaryLines.Add($line)
            while ($helperReadyPathSummaryLines.Count -gt 8) {
                $helperReadyPathSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_receive_path_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperReceivePathCaptureToEnvelopeSendAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_envelope_send_avg_ms' -DefaultValue $latestHelperReceivePathCaptureToEnvelopeSendAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperReceivePathCaptureToEnvelopeSendMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_envelope_send_median_ms' -DefaultValue $latestHelperReceivePathCaptureToEnvelopeSendMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperReceivePathCaptureToEnvelopeSendP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_envelope_send_p95_ms' -DefaultValue $latestHelperReceivePathCaptureToEnvelopeSendP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperReceivePathCaptureToEnvelopeSendMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'capture_to_envelope_send_max_ms' -DefaultValue $latestHelperReceivePathCaptureToEnvelopeSendMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_ingress_avg_ms' -DefaultValue $latestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_ingress_median_ms' -DefaultValue $latestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_ingress_p95_ms' -DefaultValue $latestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_ingress_max_ms' -DefaultValue $latestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_ingress_to_envelope_parsed_avg_ms' -DefaultValue $latestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_ingress_to_envelope_parsed_median_ms' -DefaultValue $latestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_ingress_to_envelope_parsed_p95_ms' -DefaultValue $latestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_ingress_to_envelope_parsed_max_ms' -DefaultValue $latestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_parsed_to_secure_decrypt_avg_ms' -DefaultValue $latestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 13
            $latestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_parsed_to_secure_decrypt_median_ms' -DefaultValue $latestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 14
            $latestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_parsed_to_secure_decrypt_p95_ms' -DefaultValue $latestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 15
            $latestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_parsed_to_secure_decrypt_max_ms' -DefaultValue $latestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 16
            $latestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'secure_decrypt_to_fragment_deserialize_avg_ms' -DefaultValue $latestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 17
            $latestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'secure_decrypt_to_fragment_deserialize_median_ms' -DefaultValue $latestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 18
            $latestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'secure_decrypt_to_fragment_deserialize_p95_ms' -DefaultValue $latestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 19
            $latestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'secure_decrypt_to_fragment_deserialize_max_ms' -DefaultValue $latestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 20
            $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_deserialize_to_first_fragment_observed_avg_ms' -DefaultValue $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 21
            $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_deserialize_to_first_fragment_observed_median_ms' -DefaultValue $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 22
            $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_deserialize_to_first_fragment_observed_p95_ms' -DefaultValue $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 23
            $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_deserialize_to_first_fragment_observed_max_ms' -DefaultValue $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 24
            $latestHelperDominantReceivePathStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_receive_path_stage' -DefaultValue $latestHelperDominantReceivePathStage
            [void]$helperReceivePathSummaryLines.Add($line)
            while ($helperReceivePathSummaryLines.Count -gt 8) {
                $helperReceivePathSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_bridge_ingress_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_message_observed_avg_ms' -DefaultValue $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_message_observed_median_ms' -DefaultValue $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_message_observed_p95_ms' -DefaultValue $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_bridge_message_observed_max_ms' -DefaultValue $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_message_observed_to_binary_frame_decoded_avg_ms' -DefaultValue $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_message_observed_to_binary_frame_decoded_median_ms' -DefaultValue $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_message_observed_to_binary_frame_decoded_p95_ms' -DefaultValue $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_message_observed_to_binary_frame_decoded_max_ms' -DefaultValue $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_frame_decoded_to_bridge_ingress_avg_ms' -DefaultValue $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_frame_decoded_to_bridge_ingress_median_ms' -DefaultValue $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_frame_decoded_to_bridge_ingress_p95_ms' -DefaultValue $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_frame_decoded_to_bridge_ingress_max_ms' -DefaultValue $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperDominantBridgeIngressStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_bridge_ingress_stage' -DefaultValue $latestHelperDominantBridgeIngressStage
            [void]$helperBridgeIngressSummaryLines.Add($line)
            while ($helperBridgeIngressSummaryLines.Count -gt 8) {
                $helperBridgeIngressSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_nkn_receive_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_sdk_handle_msg_entered_avg_ms' -DefaultValue $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_sdk_handle_msg_entered_median_ms' -DefaultValue $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_sdk_handle_msg_entered_p95_ms' -DefaultValue $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_sdk_handle_msg_entered_max_ms' -DefaultValue $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sdk_handle_msg_entered_to_client_message_dispatch_avg_ms' -DefaultValue $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sdk_handle_msg_entered_to_client_message_dispatch_median_ms' -DefaultValue $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sdk_handle_msg_entered_to_client_message_dispatch_p95_ms' -DefaultValue $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sdk_handle_msg_entered_to_client_message_dispatch_max_ms' -DefaultValue $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'client_message_dispatch_to_multiclient_message_dispatch_avg_ms' -DefaultValue $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'client_message_dispatch_to_multiclient_message_dispatch_median_ms' -DefaultValue $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'client_message_dispatch_to_multiclient_message_dispatch_p95_ms' -DefaultValue $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'client_message_dispatch_to_multiclient_message_dispatch_max_ms' -DefaultValue $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'multiclient_message_dispatch_to_bridge_message_observed_avg_ms' -DefaultValue $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 13
            $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'multiclient_message_dispatch_to_bridge_message_observed_median_ms' -DefaultValue $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 14
            $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'multiclient_message_dispatch_to_bridge_message_observed_p95_ms' -DefaultValue $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 15
            $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'multiclient_message_dispatch_to_bridge_message_observed_max_ms' -DefaultValue $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 16
            $latestHelperDominantNknReceiveStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_nkn_receive_stage' -DefaultValue $latestHelperDominantNknReceiveStage
            [void]$helperNknReceiveSummaryLines.Add($line)
            while ($helperNknReceiveSummaryLines.Count -gt 8) {
                $helperNknReceiveSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_ws_receive_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_ws_receiver_write_entered_avg_ms' -DefaultValue $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_ws_receiver_write_entered_median_ms' -DefaultValue $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_ws_receiver_write_entered_p95_ms' -DefaultValue $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_ws_receiver_write_entered_max_ms' -DefaultValue $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_receiver_write_entered_to_ws_message_emitted_avg_ms' -DefaultValue $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_receiver_write_entered_to_ws_message_emitted_median_ms' -DefaultValue $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_receiver_write_entered_to_ws_message_emitted_p95_ms' -DefaultValue $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_receiver_write_entered_to_ws_message_emitted_max_ms' -DefaultValue $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_message_emitted_to_sdk_handle_msg_entered_avg_ms' -DefaultValue $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 9
            $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_message_emitted_to_sdk_handle_msg_entered_median_ms' -DefaultValue $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 10
            $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_message_emitted_to_sdk_handle_msg_entered_p95_ms' -DefaultValue $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 11
            $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_message_emitted_to_sdk_handle_msg_entered_max_ms' -DefaultValue $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 12
            $latestHelperDominantWsReceiveStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_ws_receive_stage' -DefaultValue $latestHelperDominantWsReceiveStage
            [void]$helperWsReceiveSummaryLines.Add($line)
            while ($helperWsReceiveSummaryLines.Count -gt 8) {
                $helperWsReceiveSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_helper_socket_receive_summary; role=helper_remote;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestSummaryHelperSessionPhase = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_session_phase' -DefaultValue $latestSummaryHelperSessionPhase
            $latestSummaryHelperRecoveryMechanism = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_recovery_mechanism' -DefaultValue $latestSummaryHelperRecoveryMechanism
            $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_avg_ms' -DefaultValue $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 1
            $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_median_ms' -DefaultValue $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 2
            $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_p95_ms' -DefaultValue $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 3
            $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_max_ms' -DefaultValue $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 4
            $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'socket_data_event_emitted_to_ws_receiver_write_entered_avg_ms' -DefaultValue $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 5
            $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'socket_data_event_emitted_to_ws_receiver_write_entered_median_ms' -DefaultValue $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 6
            $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms' -DefaultValue $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 7
            $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'socket_data_event_emitted_to_ws_receiver_write_entered_max_ms' -DefaultValue $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs -FallbackAfterKey 'helper_recovery_mechanism' -FallbackOffset 8
            $latestHelperDominantSocketReceiveStage = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_socket_receive_stage' -DefaultValue $latestHelperDominantSocketReceiveStage
            [void]$helperSocketReceiveSummaryLines.Add($line)
            while ($helperSocketReceiveSummaryLines.Count -gt 32) {
                $helperSocketReceiveSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_bridge_event_loop_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestBridgeEventLoopP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'event_loop_p95_ms' -DefaultValue $latestBridgeEventLoopP95Ms
            $latestBridgeEventLoopMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'event_loop_max_ms' -DefaultValue $latestBridgeEventLoopMaxMs
            $latestBridgeEventLoopMeanMs = Get-StructuredLogIntField -Pairs $pairs -Key 'event_loop_mean_ms' -DefaultValue $latestBridgeEventLoopMeanMs
            $latestBridgeEventLoopSampleWindowMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sample_window_ms' -DefaultValue $latestBridgeEventLoopSampleWindowMs
            [void]$bridgeEventLoopSummaryLines.Add($line)
            while ($bridgeEventLoopSummaryLines.Count -gt 8) {
                $bridgeEventLoopSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_bridge_media_send_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_send_frame_observed_to_queue_enqueue_avg_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs -lt 0) {
                $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_ingress_avg_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_send_frame_observed_to_queue_enqueue_median_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs -lt 0) {
                $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_ingress_median_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_send_frame_observed_to_queue_enqueue_p95_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms -lt 0) {
                $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_ingress_p95_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'binary_send_frame_observed_to_queue_enqueue_max_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs -lt 0) {
                $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_ingress_max_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_enqueue_to_queue_dequeue_avg_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs -lt 0) {
                $parsedBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_queue_avg_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_enqueue_to_queue_dequeue_median_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs -lt 0) {
                $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_queue_median_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_enqueue_to_queue_dequeue_p95_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms -lt 0) {
                $parsedBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_queue_p95_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_enqueue_to_queue_dequeue_max_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs -lt 0) {
                $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_queue_max_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_dequeue_to_media_send_started_avg_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs -lt 0) {
                $parsedBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_setup_avg_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_dequeue_to_media_send_started_median_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs -lt 0) {
                $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_setup_median_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_dequeue_to_media_send_started_p95_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms -lt 0) {
                $parsedBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_setup_p95_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_dequeue_to_media_send_started_max_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs -lt 0) {
                $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_setup_max_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'media_send_started_to_media_send_resolved_avg_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs -lt 0) {
                $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_resolved_avg_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'media_send_started_to_media_send_resolved_median_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs -lt 0) {
                $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_resolved_median_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'media_send_started_to_media_send_resolved_p95_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms -lt 0) {
                $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_resolved_p95_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'media_send_started_to_media_send_resolved_max_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs -lt 0) {
                $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sender_bridge_publish_resolved_max_ms' -DefaultValue -1
            }
            $parsedBridgeMediaSendFramesSent = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_sent' -DefaultValue -1
            $parsedBridgeMediaSendFailures = Get-StructuredLogIntField -Pairs $pairs -Key 'send_failures' -DefaultValue -1
            $parsedBridgeMediaSendQueueDrops = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_drops' -DefaultValue -1
            $parsedBridgeMediaSendQueueMode = Get-StructuredLogStringField -Pairs $pairs -Key 'queue_mode' -DefaultValue 'normal'
            $parsedBridgeMediaSendQueueDepth = Get-StructuredLogIntField -Pairs $pairs -Key 'queue_depth' -DefaultValue -1
            $parsedBridgeMediaSendOldestQueuedAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'oldest_queued_age_ms' -DefaultValue -1
            $parsedBridgeMediaSendSampleWindowMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sample_window_ms' -DefaultValue -1
            if ($parsedBridgeMediaSendFramesSent -ge $bestBridgeMediaSendFramesSent) {
                $bestBridgeMediaSendFramesSent = $parsedBridgeMediaSendFramesSent
                $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs
                $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs
                $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms
                $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = $parsedBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs
                $latestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = $parsedBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs
                $latestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs
                $latestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = $parsedBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms
                $latestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = $parsedBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs
                $latestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = $parsedBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs
                $latestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs
                $latestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = $parsedBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms
                $latestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = $parsedBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs
                $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs
                $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs
                $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms
                $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = $parsedBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs
                $latestBridgeMediaSendFramesSent = $parsedBridgeMediaSendFramesSent
                $latestBridgeMediaSendFailures = $parsedBridgeMediaSendFailures
                $latestBridgeMediaSendQueueDrops = $parsedBridgeMediaSendQueueDrops
                $latestBridgeMediaSendQueueMode = $parsedBridgeMediaSendQueueMode
                $latestBridgeMediaSendQueueDepth = $parsedBridgeMediaSendQueueDepth
                $latestBridgeMediaSendOldestQueuedAgeMs = $parsedBridgeMediaSendOldestQueuedAgeMs
                $latestBridgeMediaSendSampleWindowMs = $parsedBridgeMediaSendSampleWindowMs
            }
            [void]$bridgeMediaSendSummaryLines.Add($line)
            while ($bridgeMediaSendSummaryLines.Count -gt 8) {
                $bridgeMediaSendSummaryLines.RemoveAt(0)
            }
        }

        if ($line -like '*event=screenshare_bridge_transport_health_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $parsedBridgeTransportHealthSelectedRpc = Get-StructuredLogStringField -Pairs $pairs -Key 'selected_rpc' -DefaultValue '(none)'
            $parsedBridgeTransportHealthSelectedRpcKey = Get-StructuredLogStringField -Pairs $pairs -Key 'selected_rpc_key' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthSelectedRpcKey)) {
                $parsedBridgeTransportHealthSelectedRpcKey = Get-StructuredLogStringField -Pairs $pairs -Key 'srk' -DefaultValue '(none)'
            }

            $parsedBridgeTransportHealthSelectedRpcStage = Get-StructuredLogStringField -Pairs $pairs -Key 'selected_rpc_stage' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthSelectedRpcStage)) {
                $parsedBridgeTransportHealthSelectedRpcStage = Get-StructuredLogStringField -Pairs $pairs -Key 'srs' -DefaultValue 'none'
            }

            $parsedBridgeTransportHealthConnectId = Get-StructuredLogStringField -Pairs $pairs -Key 'connect_id' -DefaultValue '(none)'
            $parsedBridgeTransportHealthConnectKey = Get-StructuredLogStringField -Pairs $pairs -Key 'connect_key' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthConnectKey)) {
                $parsedBridgeTransportHealthConnectKey = Get-StructuredLogStringField -Pairs $pairs -Key 'cky' -DefaultValue '(none)'
            }

            $parsedBridgeTransportHealthReadyEmitted = Get-StructuredLogIntField -Pairs $pairs -Key 'ready_emitted' -DefaultValue -1
            if ($parsedBridgeTransportHealthReadyEmitted -lt 0) {
                $parsedBridgeTransportHealthReadyEmitted = Get-StructuredLogIntField -Pairs $pairs -Key 'rdy' -DefaultValue -1
            }

            $parsedBridgeTransportHealthClientReadyAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'client_ready_age_ms' -DefaultValue -1
            if ($parsedBridgeTransportHealthClientReadyAgeMs -lt 0) {
                $parsedBridgeTransportHealthClientReadyAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'cra' -DefaultValue -1
            }

            $parsedBridgeTransportHealthDisconnectCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'disconnect_count_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthDisconnectCountSinceLast -lt 0) {
                $parsedBridgeTransportHealthDisconnectCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'dcc' -DefaultValue -1
            }

            $parsedBridgeTransportHealthConnectFailedCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'connect_failed_count_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthConnectFailedCountSinceLast -lt 0) {
                $parsedBridgeTransportHealthConnectFailedCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'cfc' -DefaultValue -1
            }

            $parsedBridgeTransportHealthWsErrorCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'ws_error_count_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthWsErrorCountSinceLast -lt 0) {
                $parsedBridgeTransportHealthWsErrorCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'wec' -DefaultValue -1
            }

            $parsedBridgeTransportHealthRpcFallbackAttemptCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'rpc_fallback_attempt_count_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthRpcFallbackAttemptCountSinceLast -lt 0) {
                $parsedBridgeTransportHealthRpcFallbackAttemptCountSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'rfc' -DefaultValue -1
            }

            $parsedBridgeTransportHealthControlReady = Get-StructuredLogIntField -Pairs $pairs -Key 'control_ready' -DefaultValue -1
            if ($parsedBridgeTransportHealthControlReady -lt 0) {
                $parsedBridgeTransportHealthControlReady = Get-StructuredLogIntField -Pairs $pairs -Key 'cr' -DefaultValue -1
            }

            $parsedBridgeTransportHealthMediaReady = Get-StructuredLogIntField -Pairs $pairs -Key 'media_ready' -DefaultValue -1
            if ($parsedBridgeTransportHealthMediaReady -lt 0) {
                $parsedBridgeTransportHealthMediaReady = Get-StructuredLogIntField -Pairs $pairs -Key 'mr' -DefaultValue -1
            }

            $parsedBridgeTransportHealthBulkReady = Get-StructuredLogIntField -Pairs $pairs -Key 'bulk_ready' -DefaultValue -1
            if ($parsedBridgeTransportHealthBulkReady -lt 0) {
                $parsedBridgeTransportHealthBulkReady = Get-StructuredLogIntField -Pairs $pairs -Key 'br' -DefaultValue -1
            }

            $parsedBridgeTransportHealthFramesSentSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_sent_since_last' -DefaultValue -1
            if ($parsedBridgeTransportHealthFramesSentSinceLast -lt 0) {
                $parsedBridgeTransportHealthFramesSentSinceLast = Get-StructuredLogIntField -Pairs $pairs -Key 'fss' -DefaultValue -1
            }

            $parsedBridgeTransportHealthLatestDisconnectReason = Get-StructuredLogStringField -Pairs $pairs -Key 'latest_disconnect_reason' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthLatestDisconnectReason)) {
                $parsedBridgeTransportHealthLatestDisconnectReason = Get-StructuredLogStringField -Pairs $pairs -Key 'ldr' -DefaultValue '(none)'
            }

            $parsedBridgeTransportHealthSampleWindowMs = Get-StructuredLogIntField -Pairs $pairs -Key 'sample_window_ms' -DefaultValue -1

            if ($parsedBridgeTransportHealthFramesSentSinceLast -gt 0 -and
                -not [string]::IsNullOrWhiteSpace($parsedBridgeTransportHealthSelectedRpcKey) -and
                -not [string]::Equals($parsedBridgeTransportHealthSelectedRpcKey, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
                [void]$bridgeTransportHealthSelectedRpcKeys.Add($parsedBridgeTransportHealthSelectedRpcKey)
                $latestBridgeTransportHealthUniqueSelectedRpcCount = $bridgeTransportHealthSelectedRpcKeys.Count
            }

            if ($parsedBridgeTransportHealthFramesSentSinceLast -ge $bestBridgeTransportHealthFramesSentSinceLast) {
                $bestBridgeTransportHealthFramesSentSinceLast = $parsedBridgeTransportHealthFramesSentSinceLast
                $latestBridgeTransportHealthSelectedRpc = $parsedBridgeTransportHealthSelectedRpc
                $latestBridgeTransportHealthSelectedRpcKey = $parsedBridgeTransportHealthSelectedRpcKey
                $latestBridgeTransportHealthSelectedRpcStage = $parsedBridgeTransportHealthSelectedRpcStage
                $latestBridgeTransportHealthConnectId = $parsedBridgeTransportHealthConnectId
                $latestBridgeTransportHealthConnectKey = $parsedBridgeTransportHealthConnectKey
                $latestBridgeTransportHealthReadyEmitted = $parsedBridgeTransportHealthReadyEmitted
                $latestBridgeTransportHealthClientReadyAgeMs = $parsedBridgeTransportHealthClientReadyAgeMs
                $latestBridgeTransportHealthDisconnectCountSinceLast = $parsedBridgeTransportHealthDisconnectCountSinceLast
                $latestBridgeTransportHealthConnectFailedCountSinceLast = $parsedBridgeTransportHealthConnectFailedCountSinceLast
                $latestBridgeTransportHealthWsErrorCountSinceLast = $parsedBridgeTransportHealthWsErrorCountSinceLast
                $latestBridgeTransportHealthRpcFallbackAttemptCountSinceLast = $parsedBridgeTransportHealthRpcFallbackAttemptCountSinceLast
                $latestBridgeTransportHealthControlReady = $parsedBridgeTransportHealthControlReady
                $latestBridgeTransportHealthMediaReady = $parsedBridgeTransportHealthMediaReady
                $latestBridgeTransportHealthBulkReady = $parsedBridgeTransportHealthBulkReady
                $latestBridgeTransportHealthFramesSentSinceLast = $parsedBridgeTransportHealthFramesSentSinceLast
                $latestBridgeTransportHealthLatestDisconnectReason = $parsedBridgeTransportHealthLatestDisconnectReason
                $latestBridgeTransportHealthSampleWindowMs = $parsedBridgeTransportHealthSampleWindowMs
            }

            [void]$bridgeTransportHealthSummaryLines.Add($line)
            while ($bridgeTransportHealthSummaryLines.Count -gt 32) {
                $bridgeTransportHealthSummaryLines.RemoveAt(0)
            }
        }

        if ($line -match 'event=screenshare_helper_frame_loss_epoch; role=helper_remote;') {
            [void]$helperEpochLossLines.Add($line)
            while ($helperEpochLossLines.Count -gt 16) {
                $helperEpochLossLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $streamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch'
            $framesEmitted = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_emitted'
            $framesApplied = Get-StructuredLogIntField -Pairs $pairs -Key 'frames_applied'
            if ($streamEpoch -ge 0 -and $framesEmitted -gt 0) {
                $helperEpochVisibleRatioByEpoch[[string]$streamEpoch] = [math]::Round(($framesApplied / [double]$framesEmitted), 4)
            }
        }

        if ($line -like '*event=screenshare_helper_epoch_timeline; role=helper_remote;*') {
            [void]$helperEpochTimelineLines.Add($line)
            while ($helperEpochTimelineLines.Count -gt 16) {
                $helperEpochTimelineLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $streamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch'
            $timeInRecoveryLockMs = Get-StructuredLogIntField -Pairs $pairs -Key 'time_in_recovery_lock_ms'
            if ($streamEpoch -ge 0) {
                $helperEpochRecoveryLockMsByEpoch[[string]$streamEpoch] = $timeInRecoveryLockMs
            }
        }

        if ($line -like '*event=screenshare_helper_reassembler_root_cause_summary; role=helper_remote;*') {
            [void]$helperReassemblerRootCauseSummaryLines.Add($line)
            while ($helperReassemblerRootCauseSummaryLines.Count -gt 16) {
                $helperReassemblerRootCauseSummaryLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $streamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch'
            $dominantRootCause = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_root_cause' -DefaultValue 'none'
            if ($streamEpoch -ge 0) {
                $helperEpochRootCauseByEpoch[[string]$streamEpoch] = $dominantRootCause
                $helperRootCauseSummaryByEpoch[[string]$streamEpoch] = [pscustomobject]@{
                    StreamEpoch = $streamEpoch
                    AppliedHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'applied_head_frame_id'
                    OrderedEmitHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'ordered_emit_head_frame_id'
                    WinningRecoveryFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'winning_recovery_frame_id'
                    FragmentGapBeforeAssemblyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'fragment_gap_before_assembly_count'
                    LateFragmentAfterHeadAdvancedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_head_advanced_count'
                    LateFragmentAfterAppliedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_applied_head_count'
                    LateFragmentAfterOrderedHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_ordered_head_count'
                    SupersededRecoveryTailCleanupCount = Get-StructuredLogIntField -Pairs $pairs -Key 'superseded_recovery_tail_cleanup_count'
                    RecoveryOwnerReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_owner_replaced_count'
                    OlderEpochCleanupAfterEpochAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'older_epoch_cleanup_after_epoch_advance_count'
                    LateFragmentAfterStableVisibleHeadCount = Get-StructuredLogIntField -Pairs $pairs -Key 'late_fragment_after_stable_visible_head_count'
                    ActionableLateFragmentCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_late_fragment_count'
                    FutureTailPrunedWhileGapActiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'future_tail_pruned_while_gap_active_count'
                    ProtectedHeadMissingBudgetPressureCount = Get-StructuredLogIntField -Pairs $pairs -Key 'protected_head_missing_budget_pressure_count'
                    RecoveryKeyframeSupersededOrReplacedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_keyframe_superseded_or_replaced_count'
                    OrderedEmitBlockedThenResyncedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'ordered_emit_blocked_then_resynced_count'
                    DominantRootCause = $dominantRootCause
                }
            }
        }

        if ($line -like '*event=screenshare_helper_recovery_epoch_investigation; role=helper_remote;*') {
            [void]$helperRecoveryEpochInvestigationLines.Add($line)
            while ($helperRecoveryEpochInvestigationLines.Count -gt 16) {
                $helperRecoveryEpochInvestigationLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
        }

        if ($line -like '*event=screenshare_reassembler_recovery_owner_buffered;*' -or
            $line -like '*event=screenshare_reassembler_recovery_owner_replaced;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $sessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($latestHelperSessionId) -or
                [string]::IsNullOrWhiteSpace($sessionId) -or
                [string]::Equals($sessionId, $latestHelperSessionId, [System.StringComparison]::Ordinal)) {
                [void]$helperReassemblerRecoveryOwnerTransitionLines.Add($line)
                while ($helperReassemblerRecoveryOwnerTransitionLines.Count -gt 24) {
                    $helperReassemblerRecoveryOwnerTransitionLines.RemoveAt(0)
                }
            }
        }

        if ($line -like '*event=screenshare_reassembler_actionable_late_fragment;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $sessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($latestHelperSessionId) -or
                [string]::IsNullOrWhiteSpace($sessionId) -or
                [string]::Equals($sessionId, $latestHelperSessionId, [System.StringComparison]::Ordinal)) {
                [void]$helperReassemblerActionableLateFragmentLines.Add($line)
                while ($helperReassemblerActionableLateFragmentLines.Count -gt 24) {
                    $helperReassemblerActionableLateFragmentLines.RemoveAt(0)
                }
            }
        }

        if ($line -like '*event=screenshare_reassembler_older_epoch_cleanup_after_epoch_advance;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $sessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue ''
            if ([string]::IsNullOrWhiteSpace($latestHelperSessionId) -or
                [string]::IsNullOrWhiteSpace($sessionId) -or
                [string]::Equals($sessionId, $latestHelperSessionId, [System.StringComparison]::Ordinal)) {
                [void]$helperReassemblerOlderEpochCleanupLines.Add($line)
                while ($helperReassemblerOlderEpochCleanupLines.Count -gt 24) {
                    $helperReassemblerOlderEpochCleanupLines.RemoveAt(0)
                }
            }
        }

        if ($line -like '*event=screenshare_helper_pressure_epoch_summary; role=helper_remote;*') {
            [void]$helperPressureSummaryLines.Add($line)
            while ($helperPressureSummaryLines.Count -gt 16) {
                $helperPressureSummaryLines.RemoveAt(0)
            }

            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestHelperSessionId = Get-StructuredLogStringField -Pairs $pairs -Key 'session_id' -DefaultValue $latestHelperSessionId
            $streamEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'stream_epoch'
            $dominantPressureBlocker = Get-StructuredLogStringField -Pairs $pairs -Key 'dominant_pressure_blocker' -DefaultValue 'none'
            if ($streamEpoch -ge 0) {
                $helperEpochPressureBlockerByEpoch[[string]$streamEpoch] = $dominantPressureBlocker
                $helperPressureSummaryByEpoch[[string]$streamEpoch] = [pscustomobject]@{
                    StreamEpoch = $streamEpoch
                    SteadyVisibleProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_active'
                    SteadyVisibleProgressActivationFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_activation_frame_id'
                    AppliedHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'applied_head_frame_id'
                    StableVisibleHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'stable_visible_head_frame_id'
                    LastSentStableVisibleHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'last_sent_stable_visible_head_frame_id'
                    PressureSendBypassedForVisibleProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pressure_send_bypassed_for_visible_progress_count'
                    ProofKeepaliveSendCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_send_count'
                    ProofKeepaliveTimerDrivenSendCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_timer_driven_send_count'
                    ProofKeepaliveLastHeadFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_last_head_frame_id'
                    ProofKeepaliveLastSendAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_last_send_age_ms'
                    SteadyVisibleProgressClearedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_cleared_count'
                    SteadyVisibleProgressClearedReason = Get-StructuredLogStringField -Pairs $pairs -Key 'steady_visible_progress_cleared_reason'
                    ContinuityLossTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'continuity_loss_ticks'
                    WarmupTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'warmup_ticks'
                    BeforeFirstVisibleApplyTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'before_first_visible_apply_ticks'
                    AfterVisibleRecoveryFrameTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'after_visible_recovery_frame_ticks'
                    AfterVisibleRecoveryFrameSuppressedDueToSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'after_visible_recovery_frame_suppressed_due_to_success_count'
                    SlowApplyCadenceTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'slow_apply_cadence_ticks'
                    HighFrameAgeTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_ticks'
                    HighFrameAgeSuppressedDueToVisibleProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_due_to_visible_progress_count'
                    HighFrameAgeSuppressedDueToHeadAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_due_to_head_advance_count'
                    ActionableHighFrameAgeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_high_frame_age_count'
                    PostRecoveryHighFrameAgeSuppressedTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_high_frame_age_suppressed_ticks'
                    VisibleAppliesDuringSettleCount = Get-StructuredLogIntField -Pairs $pairs -Key 'visible_applies_during_settle_count'
                    RepeatedStaleDropsTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'repeated_stale_drops_ticks'
                    BridgeHealthTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_ticks'
                    BridgeHealthAdvisoryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_advisory_count'
                    BridgeHealthActionableCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_actionable_count'
                    BridgeHealthQuarantineSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_quarantine_suppressed_count'
                    BridgeHealthActionableWithoutQueueOrDropCount = Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_became_actionable_without_queue_or_drop_count'
                    RecoveryWindowActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_active'
                    RecoveryWindowProgressed = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_progressed'
                    RecoveryWindowSucceeded = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_succeeded'
                    RecoveryWindowProgressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_progressed_count'
                    RecoveryWindowSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_success_count'
                    ActiveRecoveryWindowEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_epoch'
                    ActiveRecoveryWindowRecoveryFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_recovery_frame_id'
                    RecoveryWindowContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_contiguous_follower_apply_count'
                    BaselineEstablished = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_established'
                    BaselineCaptureToRenderMs = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_capture_to_render_ms'
                    AgeExcessMs = Get-StructuredLogIntField -Pairs $pairs -Key 'age_excess_ms'
                    ProgressStallMs = Get-StructuredLogIntField -Pairs $pairs -Key 'progress_stall_ms'
                    BaselineReseedInProgress = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_reseed_in_progress'
                    AgePressureConsecutiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'age_pressure_consecutive_count'
                    CadencePressureConsecutiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_pressure_consecutive_count'
                    CatchUpSuppressedDueToProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'catch_up_suppressed_due_to_progress_count'
                    BaselineFrozenDueToStallCount = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_frozen_due_to_stall_count'
                    BaselineReseedAfterRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_reseed_after_recovery_count'
                    CadenceStallWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_stall_window_count'
                    CadenceStallTriggerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_stall_trigger_count'
                    TimeSpentInHelperWarmupMs = Get-StructuredLogIntField -Pairs $pairs -Key 'time_spent_in_helper_warmup_ms'
                    PostRecoverySettleWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_count'
                    PostRecoverySettleWindowSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_success_count'
                    PostRecoverySettleWindowTimeoutCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_timeout_count'
                    VisibleAppliesBeforePressureReenabled = Get-StructuredLogIntField -Pairs $pairs -Key 'visible_applies_before_pressure_reenabled'
                    DominantPressureBlocker = $dominantPressureBlocker
                }

                $latestHelperPostRecoveryHighFrameAgeSuppressedTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_high_frame_age_suppressed_ticks'
                $latestHelperVisibleAppliesDuringSettleCount = Get-StructuredLogIntField -Pairs $pairs -Key 'visible_applies_during_settle_count'
                $latestHelperPostRecoverySettleWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_count'
                $latestHelperPostRecoverySettleWindowSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_success_count'
                $latestHelperPostRecoverySettleWindowTimeoutCount = Get-StructuredLogIntField -Pairs $pairs -Key 'post_recovery_settle_window_timeout_count'
                $latestHelperVisibleAppliesBeforePressureReenabled = Get-StructuredLogIntField -Pairs $pairs -Key 'visible_applies_before_pressure_reenabled'
                $latestHelperRecoveryWindowActive = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_active'
                $latestHelperRecoveryWindowProgressed = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_progressed'
                $latestHelperRecoveryWindowSucceeded = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_succeeded'
                $latestHelperSteadyVisibleProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_active' -DefaultValue $latestHelperSteadyVisibleProgressActive
                $activationValue = Get-StructuredLogStringField -Pairs $pairs -Key 'steady_visible_progress_activation_frame_id' -DefaultValue $latestHelperSteadyVisibleProgressActivationFrameId
                if ($activationValue -match '^-?[0-9]+$') {
                    $latestHelperSteadyVisibleProgressActivationFrameId = [int64]$activationValue
                }
                $appliedHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'applied_head_frame_id' -DefaultValue $latestHelperAppliedHeadFrameId
                if ($appliedHeadValue -match '^-?[0-9]+$') {
                    $latestHelperAppliedHeadFrameId = [int64]$appliedHeadValue
                }
                $stableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'stable_visible_head_frame_id' -DefaultValue $latestHelperStableVisibleHeadFrameId
                if ($stableVisibleHeadValue -match '^-?[0-9]+$') {
                    $latestHelperStableVisibleHeadFrameId = [int64]$stableVisibleHeadValue
                }
                $lastSentStableVisibleHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'last_sent_stable_visible_head_frame_id' -DefaultValue $latestHelperLastSentStableVisibleHeadFrameId
                if ($lastSentStableVisibleHeadValue -match '^-?[0-9]+$') {
                    $latestHelperLastSentStableVisibleHeadFrameId = [int64]$lastSentStableVisibleHeadValue
                }
                $latestHelperPressureSendBypassedForVisibleProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'pressure_send_bypassed_for_visible_progress_count' -DefaultValue $latestHelperPressureSendBypassedForVisibleProgressCount
                $latestHelperProofKeepaliveSendCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_send_count' -DefaultValue $latestHelperProofKeepaliveSendCount
                $latestHelperProofKeepaliveTimerDrivenSendCount = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_timer_driven_send_count' -DefaultValue $latestHelperProofKeepaliveTimerDrivenSendCount
                $helperProofKeepaliveHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_proof_keepalive_last_head_frame_id' -DefaultValue $latestHelperProofKeepaliveLastHeadFrameId
                if ($helperProofKeepaliveHeadValue -match '^-?[0-9]+$') {
                    $latestHelperProofKeepaliveLastHeadFrameId = [int64]$helperProofKeepaliveHeadValue
                }
                $latestHelperProofKeepaliveLastSendAgeMs = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_proof_keepalive_last_send_age_ms' -DefaultValue $latestHelperProofKeepaliveLastSendAgeMs
                $helperFirstVisibleApplyToSenderFactSendValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_first_visible_apply_to_sender_fact_send_ms' -DefaultValue ''
                if ($helperFirstVisibleApplyToSenderFactSendValue -match '^-?[0-9]+$') {
                    $latestHelperFirstVisibleApplyToSenderFactSendMs = [int64]$helperFirstVisibleApplyToSenderFactSendValue
                }
                $latestHelperSteadyVisibleProgressClearedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'steady_visible_progress_cleared_count' -DefaultValue $latestHelperSteadyVisibleProgressClearedCount
                $latestHelperSteadyVisibleProgressClearedReason = Get-StructuredLogStringField -Pairs $pairs -Key 'steady_visible_progress_cleared_reason' -DefaultValue $latestHelperSteadyVisibleProgressClearedReason
                $latestHelperHighFrameAgeSuppressedDueToVisibleProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_due_to_visible_progress_count' -DefaultValue $latestHelperHighFrameAgeSuppressedDueToVisibleProgressCount
                $latestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount = Get-StructuredLogIntField -Pairs $pairs -Key 'high_frame_age_suppressed_due_to_head_advance_count' -DefaultValue $latestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount
                $latestHelperActionableHighFrameAgeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'actionable_high_frame_age_count' -DefaultValue $latestHelperActionableHighFrameAgeCount
                $latestHelperBridgeHealthAdvisoryCount = [Math]::Max(
                    $latestHelperBridgeHealthAdvisoryCount,
                    (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_advisory_count' -DefaultValue $latestHelperBridgeHealthAdvisoryCount))
                $latestHelperBridgeHealthActionableCount = [Math]::Max(
                    $latestHelperBridgeHealthActionableCount,
                    (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_actionable_count' -DefaultValue $latestHelperBridgeHealthActionableCount))
                $latestHelperBridgeHealthQuarantineSuppressedCount = [Math]::Max(
                    $latestHelperBridgeHealthQuarantineSuppressedCount,
                    (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_quarantine_suppressed_count' -DefaultValue $latestHelperBridgeHealthQuarantineSuppressedCount))
                $latestHelperBridgeHealthActionableWithoutQueueOrDropCount = [Math]::Max(
                    $latestHelperBridgeHealthActionableWithoutQueueOrDropCount,
                    (Get-StructuredLogIntField -Pairs $pairs -Key 'bridge_health_became_actionable_without_queue_or_drop_count' -DefaultValue $latestHelperBridgeHealthActionableWithoutQueueOrDropCount))
                $latestHelperRecoveryWindowProgressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_progressed_count'
                $latestHelperRecoveryWindowSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_success_count'
                $latestHelperActiveRecoveryWindowEpoch = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_epoch'
                $latestHelperActiveRecoveryWindowRecoveryFrameId = Get-StructuredLogIntField -Pairs $pairs -Key 'active_recovery_window_recovery_frame_id'
                $latestHelperRecoveryWindowContiguousFollowerApplyCount = Get-StructuredLogIntField -Pairs $pairs -Key 'recovery_window_contiguous_follower_apply_count'
                $latestHelperAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = Get-StructuredLogIntField -Pairs $pairs -Key 'after_visible_recovery_frame_suppressed_due_to_success_count'
                $latestHelperBaselineEstablished = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_established'
                $latestHelperBaselineCaptureToRenderMs = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_capture_to_render_ms'
                $latestHelperAgeExcessMs = Get-StructuredLogIntField -Pairs $pairs -Key 'age_excess_ms'
                $latestHelperProgressStallMs = Get-StructuredLogIntField -Pairs $pairs -Key 'progress_stall_ms'
                $latestHelperBaselineReseedInProgress = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_reseed_in_progress'
                $latestHelperAgePressureConsecutiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'age_pressure_consecutive_count'
                $latestHelperCadencePressureConsecutiveCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_pressure_consecutive_count'
                $latestHelperCatchUpSuppressedDueToProgressCount = Get-StructuredLogIntField -Pairs $pairs -Key 'catch_up_suppressed_due_to_progress_count'
                $latestHelperBaselineFrozenDueToStallCount = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_frozen_due_to_stall_count'
                $latestHelperBaselineReseedAfterRecoveryCount = Get-StructuredLogIntField -Pairs $pairs -Key 'baseline_reseed_after_recovery_count'
                $latestHelperCadenceStallWindowCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_stall_window_count'
                $latestHelperCadenceStallTriggerCount = Get-StructuredLogIntField -Pairs $pairs -Key 'cadence_stall_trigger_count'
            }
        }

        if ($line -like '*event=screenshare_sender_promotion_blocked;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $blockers = Get-StructuredLogStringField -Pairs $pairs -Key 'blockers'
            $helperSteadyProgressActive = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_steady_visible_progress_active'
            $helperProgressProofSatisfied = Get-StructuredLogIntField -Pairs $pairs -Key 'helper_progress_proof_satisfied'
            $senderHeadValue = Get-StructuredLogStringField -Pairs $pairs -Key 'helper_stable_visible_head_frame_id' -DefaultValue '(none)'
            $senderStableVisibleHeadFrameId = if ($senderHeadValue -match '^-?[0-9]+$') { [int64]$senderHeadValue } else { -1 }
            $helperPressureBlockerActive = $blockers -match '(^|,)helper_pressure(,|$)'
            $helperWarmupBlockerActive = $blockers -match '(^|,)helper_warmup(,|$)'

            if ($blockers -match '(^|,)helper_apply_count(,|$)') {
                if ($helperSteadyProgressActive -gt 0 -and $senderStableVisibleHeadFrameId -ge 0 -and $helperProgressProofSatisfied -le 0) {
                    $promotionBlockedByStaleHelperProofCount++
                }
                else {
                    $promotionBlockedByMissingHelperProofCount++
                }
            }

            if ($blockers -match '(^|,)encode_over_budget(,|$)') {
                if ($helperProgressProofSatisfied -gt 0 -and -not $helperPressureBlockerActive -and -not $helperWarmupBlockerActive) {
                    $promotionBlockedByEncodeBudgetCount++
                }
            }
        }

        if ($line -like '*event=screenshare_reduced_promotion_summary;*') {
            $pairs = Get-StructuredLogFieldPairs -Line $line
            $latestPromotionBlockerRateGateTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_rate_gate_ticks'
            $latestPromotionBlockerHelperPressureTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_helper_pressure_ticks'
            $latestPromotionBlockerHelperWarmupTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_helper_warmup_ticks'
            $latestPromotionBlockerHelperApplyCountTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_helper_apply_count_ticks' -FallbackAfterKey 'promotion_blocker_helper_warmup_ticks' -FallbackOffset 1
            $latestPromotionBlockerBridgeHealthTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_bridge_health_ticks'
            $latestPromotionBlockerRecoveryLockTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_recovery_lock_ticks'
            $latestPromotionBlockerQueueEvictTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_queue_evict_ticks'
            $latestPromotionBlockerCaptureAgeTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_capture_age_ticks'
            $latestPromotionBlockerEncodeBudgetTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_encode_budget_ticks'
            $latestPromotionBlockerTransitionGraceTicks = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_blocker_transition_grace_ticks' -FallbackAfterKey 'promotion_blocker_encode_budget_ticks' -FallbackOffset 1
            $latestPromotionEncodeSoftSpikeCount = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_encode_soft_spike_count'
            $latestPromotionEncodeSoftSpikeResetSuppressedCount = Get-StructuredLogIntField -Pairs $pairs -Key 'promotion_encode_soft_spike_reset_suppressed_count'
            $promotionBlockedByEncodeBudgetAloneCount = Get-StructuredLogIntField -Pairs $pairs -Key 'blocked_by_encode_budget_alone' -DefaultValue $promotionBlockedByEncodeBudgetAloneCount
            $latestHealthyTickResetReasonCounts = Get-StructuredLogStringField -Pairs $pairs -Key 'healthy_tick_reset_reason_counts'
            $latestReducedPromotionRecentEntries = Get-StructuredLogStringField -Pairs $pairs -Key 'recent_entries'
            [void]$reducedPromotionSummaryLines.Add($line)
            while ($reducedPromotionSummaryLines.Count -gt 8) {
                $reducedPromotionSummaryLines.RemoveAt(0)
            }
        }

        if ($line -match 'event=screenshare_viewer_stale_frame_dropped; role=helper_remote;') {
            $helperStaleDrops++
        }

        if ($line -match 'event=screenshare_receiver_stale_frame_superseded;') {
            $receiverSupersededFrames++
        }

        if ($line -match 'event=helper_local_peer_address_ready;.*run_id=([^;]+);\s*listener_generation=(\d+)') {
            $latestHelperRunId = [string]$matches[1]
            $latestHelperListenerGeneration = [int64]$matches[2]
        }
    }

    $captureValues = @($captureToSend.ToArray())
    $helperValues = @($helperApply.ToArray())
    $worstVisibleApplyRatioEpoch = -1
    $worstVisibleApplyRatio = -1.0
    foreach ($entry in $helperEpochVisibleRatioByEpoch.GetEnumerator()) {
        $epochValue = [int]$entry.Key
        $ratioValue = [double]$entry.Value
        if ($worstVisibleApplyRatioEpoch -lt 0 -or $ratioValue -lt $worstVisibleApplyRatio -or ($ratioValue -eq $worstVisibleApplyRatio -and $epochValue -gt $worstVisibleApplyRatioEpoch)) {
            $worstVisibleApplyRatioEpoch = $epochValue
            $worstVisibleApplyRatio = $ratioValue
        }
    }

    $worstRecoveryLockEpoch = -1
    $worstRecoveryLockMs = -1
    foreach ($entry in $helperEpochRecoveryLockMsByEpoch.GetEnumerator()) {
        $epochValue = [int]$entry.Key
        $durationValue = [int64]$entry.Value
        if ($worstRecoveryLockEpoch -lt 0 -or $durationValue -gt $worstRecoveryLockMs -or ($durationValue -eq $worstRecoveryLockMs -and $epochValue -gt $worstRecoveryLockEpoch)) {
            $worstRecoveryLockEpoch = $epochValue
            $worstRecoveryLockMs = $durationValue
        }
    }

    $effectiveMediaPlaneActive = if (
        $latestMediaPlaneAttached -gt 0 -and
        $latestMediaPlaneFramesSent -gt 0 -and
        $latestBridgeMediaMessagesReceived -gt 0 -and
        $steadyStateControlFallbackQueuedCount -eq 0) { 1 } else { 0 }
    $recoveryUsedControlFallback = if ($recoveryControlFallbackQueuedCount -gt 0 -or $latestRecoveryBurstControlFallbackCount -gt 0) { 1 } else { 0 }
    $steadyStateUsedControlFallback = if ($steadyStateControlFallbackQueuedCount -gt 0) { 1 } else { 0 }

    $aggregateFragmentGapBeforeAssemblyCount = 0
    $aggregateLateFragmentAfterHeadAdvancedCount = 0
    $aggregateLateFragmentAfterAppliedHeadCount = 0
    $aggregateLateFragmentAfterOrderedHeadCount = 0
    $aggregateLateFragmentAfterStableVisibleHeadCount = 0
    $aggregateFutureTailPrunedWhileGapActiveCount = 0
    $aggregateProtectedHeadMissingBudgetPressureCount = 0
    $aggregateRecoveryKeyframeSupersededOrReplacedCount = 0
    $aggregateOrderedEmitBlockedThenResyncedCount = 0
    $aggregateRecoveryOwnerReplacedCount = 0
    $aggregateOlderEpochCleanupAfterEpochAdvanceCount = 0
    $aggregateActionableLateFragmentCount = 0
    foreach ($entry in $helperRootCauseSummaryByEpoch.Values) {
        $aggregateFragmentGapBeforeAssemblyCount += [int64]$entry.FragmentGapBeforeAssemblyCount
        $aggregateLateFragmentAfterHeadAdvancedCount += [int64]$entry.LateFragmentAfterHeadAdvancedCount
        $aggregateLateFragmentAfterAppliedHeadCount += [int64]$entry.LateFragmentAfterAppliedHeadCount
        $aggregateLateFragmentAfterOrderedHeadCount += [int64]$entry.LateFragmentAfterOrderedHeadCount
        $aggregateLateFragmentAfterStableVisibleHeadCount += [int64]$entry.LateFragmentAfterStableVisibleHeadCount
        $aggregateFutureTailPrunedWhileGapActiveCount += [int64]$entry.FutureTailPrunedWhileGapActiveCount
        $aggregateProtectedHeadMissingBudgetPressureCount += [int64]$entry.ProtectedHeadMissingBudgetPressureCount
        $aggregateRecoveryKeyframeSupersededOrReplacedCount += [int64]$entry.RecoveryKeyframeSupersededOrReplacedCount
        $aggregateOrderedEmitBlockedThenResyncedCount += [int64]$entry.OrderedEmitBlockedThenResyncedCount
        $aggregateRecoveryOwnerReplacedCount += [int64]$entry.RecoveryOwnerReplacedCount
        $aggregateOlderEpochCleanupAfterEpochAdvanceCount += [int64]$entry.OlderEpochCleanupAfterEpochAdvanceCount
        $aggregateActionableLateFragmentCount += [int64]$entry.ActionableLateFragmentCount
    }

    $dominantReassemblerRootCause = $latestHelperDominantReassemblerRootCause
    if ([string]::IsNullOrWhiteSpace($dominantReassemblerRootCause) -or [string]::Equals($dominantReassemblerRootCause, 'none', [System.StringComparison]::OrdinalIgnoreCase)) {
        $dominantReassemblerRootCause = Get-TopNamedCount -Candidates @(
            [pscustomobject]@{ Name = 'fragment_gap_before_assembly'; Count = $aggregateFragmentGapBeforeAssemblyCount },
            [pscustomobject]@{ Name = 'late_fragment_after_head_advanced'; Count = $aggregateLateFragmentAfterHeadAdvancedCount },
            [pscustomobject]@{ Name = 'future_tail_pruned_while_gap_active'; Count = $aggregateFutureTailPrunedWhileGapActiveCount },
            [pscustomobject]@{ Name = 'protected_head_missing_budget_pressure'; Count = $aggregateProtectedHeadMissingBudgetPressureCount },
            [pscustomobject]@{ Name = 'recovery_keyframe_superseded_or_replaced'; Count = $aggregateRecoveryKeyframeSupersededOrReplacedCount },
            [pscustomobject]@{ Name = 'ordered_emit_blocked_then_resynced'; Count = $aggregateOrderedEmitBlockedThenResyncedCount }
        )
    }

    $aggregateContinuityLossTicks = 0
    $aggregateWarmupTicks = 0
    $aggregateBeforeFirstVisibleApplyTicks = 0
    $aggregateAfterVisibleRecoveryFrameTicks = 0
    $aggregateSlowApplyCadenceTicks = 0
    $aggregateHighFrameAgeTicks = 0
    $aggregateHighFrameAgeSuppressedDueToVisibleProgressCount = 0
    $aggregateHighFrameAgeSuppressedDueToHeadAdvanceCount = 0
    $aggregateActionableHighFrameAgeCount = 0
    $aggregatePostRecoveryHighFrameAgeSuppressedTicks = 0
    $aggregateRepeatedStaleDropsTicks = 0
    $aggregateBridgeHealthTicks = 0
    foreach ($entry in $helperPressureSummaryByEpoch.Values) {
        $aggregateContinuityLossTicks += [int64]$entry.ContinuityLossTicks
        $aggregateWarmupTicks += [int64]$entry.WarmupTicks
        $aggregateBeforeFirstVisibleApplyTicks += [int64]$entry.BeforeFirstVisibleApplyTicks
        $aggregateAfterVisibleRecoveryFrameTicks += [int64]$entry.AfterVisibleRecoveryFrameTicks
        $aggregateSlowApplyCadenceTicks += [int64]$entry.SlowApplyCadenceTicks
        $aggregateHighFrameAgeTicks += [int64]$entry.HighFrameAgeTicks
        $aggregateHighFrameAgeSuppressedDueToVisibleProgressCount += [int64]$entry.HighFrameAgeSuppressedDueToVisibleProgressCount
        $aggregateHighFrameAgeSuppressedDueToHeadAdvanceCount += [int64]$entry.HighFrameAgeSuppressedDueToHeadAdvanceCount
        $aggregateActionableHighFrameAgeCount += [int64]$entry.ActionableHighFrameAgeCount
        $aggregatePostRecoveryHighFrameAgeSuppressedTicks += [int64]$entry.PostRecoveryHighFrameAgeSuppressedTicks
        $aggregateRepeatedStaleDropsTicks += [int64]$entry.RepeatedStaleDropsTicks
        $aggregateBridgeHealthTicks += [int64]$entry.BridgeHealthTicks
    }

    $latestPromotionEntryShowsStableProof = $false
    if (-not [string]::IsNullOrWhiteSpace($latestReducedPromotionRecentEntries) -and
        -not [string]::Equals($latestReducedPromotionRecentEntries, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
        $recentPromotionEntries = @($latestReducedPromotionRecentEntries -split '~')
        if ($recentPromotionEntries.Count -gt 0) {
            $latestPromotionEntry = $recentPromotionEntries[$recentPromotionEntries.Count - 1]
            if ($latestPromotionEntry -match '\|steady=1\|' -and $latestPromotionEntry -match '\|head=[0-9]+') {
                $latestPromotionEntryShowsStableProof = $true
            }
        }
    }

    $helperVisibleHeadRuntimeSenderMismatch = 0
    if ($latestHelperStableVisibleHeadFrameId -ge 0 -and
        $latestHelperSteadyVisibleProgressActive -gt 0 -and
        -not $latestPromotionEntryShowsStableProof -and
        (($promotionBlockedByMissingHelperProofCount + $promotionBlockedByStaleHelperProofCount) -gt 0)) {
        $helperVisibleHeadRuntimeSenderMismatch = 1
    }

    $effectiveDominantHelperAdmissionRejectReason = if (
        [string]::Equals($latestHelperDominantAdmissionRejectReason, 'waiting_for_recovery_keyframe', [System.StringComparison]::OrdinalIgnoreCase) -and
        [Math]::Max(0, $latestHelperRecoveryWaitRejectBeforeRunwayCount) -eq 0 -and
        [Math]::Max(0, $latestHelperWaitingForRecoveryKeyframeRejectCount) -eq 0 -and
        [Math]::Max(0, $latestHelperPreCandidateGapTailEmittedToViewerCount) -eq 0
    ) {
        'none'
    }
    elseif ([string]::IsNullOrWhiteSpace($latestHelperDominantAdmissionRejectReason)) {
        'none'
    }
    else {
        $latestHelperDominantAdmissionRejectReason
    }

    $dominantHelperPressureBlocker = Get-TopNamedCount -Candidates @(
        [pscustomobject]@{ Name = 'continuity_loss'; Count = $aggregateContinuityLossTicks },
        [pscustomobject]@{ Name = 'warmup'; Count = $aggregateWarmupTicks },
        [pscustomobject]@{ Name = 'before_first_visible_apply'; Count = $aggregateBeforeFirstVisibleApplyTicks },
        [pscustomobject]@{ Name = 'after_visible_recovery_frame'; Count = $aggregateAfterVisibleRecoveryFrameTicks },
        [pscustomobject]@{ Name = 'slow_apply_cadence'; Count = $aggregateSlowApplyCadenceTicks },
        [pscustomobject]@{ Name = 'high_frame_age'; Count = $aggregateHighFrameAgeTicks },
        [pscustomobject]@{ Name = 'repeated_stale_drops'; Count = $aggregateRepeatedStaleDropsTicks },
        [pscustomobject]@{ Name = 'bridge_health'; Count = $aggregateBridgeHealthTicks }
    )

    $latestOrdinaryRawLossCount = [Math]::Max(0, $latestRawFramesReplacedBeforeEncodeSlot) + [Math]::Max(0, $latestSourceSupersededPendingFrames)
    $latestOrdinarySenderLossCount = [Math]::Max(0, $latestFramesReplacedBeforeSendSlot) + [Math]::Max(0, $latestFramesDroppedByQueueEvict)
    $latestOrdinaryHelperLossCount = [Math]::Max(0, $latestHelperDecodeQueueOverflowCount) + [Math]::Max(0, $latestHelperDecodeAgeBudgetCount) + [Math]::Max(0, $latestHelperDecodedApplyQueueOverflowCount) + [Math]::Max(0, $latestHelperDecodedFrameReplacedBeforeApplyCount)
    $dominantOrdinaryFreshnessLossBoundary = Get-TopNamedCount -Candidates @(
        [pscustomobject]@{ Name = 'raw'; Count = $latestOrdinaryRawLossCount },
        [pscustomobject]@{ Name = 'sender'; Count = $latestOrdinarySenderLossCount },
        [pscustomobject]@{ Name = 'helper'; Count = $latestOrdinaryHelperLossCount }
    )

    $resolvedHealthSenderOperatingState = if ([string]::IsNullOrWhiteSpace($latestHealthSenderOperatingState)) { 'normal' } else { $latestHealthSenderOperatingState }
    $resolvedHealthSenderGuardState = if ([string]::IsNullOrWhiteSpace($latestHealthSenderGuardState)) { 'none' } else { $latestHealthSenderGuardState }
    $resolvedHealthHelperSessionPhase = if ([string]::IsNullOrWhiteSpace($latestHealthHelperSessionPhase)) { 'no_visible_baseline' } else { $latestHealthHelperSessionPhase }
    $resolvedHealthHelperRecoveryMechanism = if ([string]::IsNullOrWhiteSpace($latestHealthHelperRecoveryMechanism)) { 'none' } else { $latestHealthHelperRecoveryMechanism }
    $resolvedHealthDominantLossClass = if ([string]::IsNullOrWhiteSpace($latestHealthDominantLossClass)) { 'benign_stale_cleanup' } else { $latestHealthDominantLossClass }
    $resolvedHealthDominantPressureBlocker = if ([string]::IsNullOrWhiteSpace($latestHealthDominantPressureBlocker)) { 'none' } else { $latestHealthDominantPressureBlocker }
    $resolvedHealthDominantTroubleDomain = if ([string]::IsNullOrWhiteSpace($latestHealthDominantTroubleDomain)) { 'none' } else { $latestHealthDominantTroubleDomain }
    $resolvedHealthRecoveryActive = [Math]::Max(0, $latestHealthRecoveryActive)
    $resolvedHealthBaselineEstablished = [Math]::Max(0, $latestHealthBaselineEstablished)
    $resolvedHealthSteadyVisibleProgressActive = [Math]::Max(0, $latestHealthSteadyVisibleProgressActive)

    $needHealthFallback =
        $healthSnapshotLines.Count -eq 0 -or
        (($resolvedHealthHelperSessionPhase -eq 'no_visible_baseline') -and ($latestHelperBaselineEstablished -gt 0)) -or
        (($resolvedHealthBaselineEstablished -le 0) -and ($latestHelperBaselineEstablished -gt 0)) -or
        (($resolvedHealthSteadyVisibleProgressActive -le 0) -and ($latestHelperSteadyVisibleProgressActive -gt 0))

    if ($needHealthFallback) {
        $resolvedHealthSenderOperatingState = if ([string]::IsNullOrWhiteSpace($latestSummarySenderOperatingState)) { $resolvedHealthSenderOperatingState } else { $latestSummarySenderOperatingState }
        $resolvedHealthSenderGuardState = if ([string]::IsNullOrWhiteSpace($latestSummarySenderGuardState)) { $resolvedHealthSenderGuardState } else { $latestSummarySenderGuardState }
        $resolvedHealthDominantPressureBlocker = if ([string]::IsNullOrWhiteSpace($latestSummaryDominantPressureBlocker)) { $resolvedHealthDominantPressureBlocker } else { $latestSummaryDominantPressureBlocker }
        $resolvedHealthBaselineEstablished = [Math]::Max($resolvedHealthBaselineEstablished, [Math]::Max(0, $latestHelperBaselineEstablished))
        $resolvedHealthSteadyVisibleProgressActive = [Math]::Max($resolvedHealthSteadyVisibleProgressActive, [Math]::Max(0, $latestHelperSteadyVisibleProgressActive))
        $resolvedHealthRecoveryActive = [Math]::Max($resolvedHealthRecoveryActive, [Math]::Max(0, $latestRecoveryBurstActive))
        $resolvedHealthRecoveryActive = [Math]::Max($resolvedHealthRecoveryActive, [Math]::Max(0, $latestHelperRecoveryWindowActive))

        if (-not [string]::IsNullOrWhiteSpace($latestSummaryHelperRecoveryMechanism)) {
            $resolvedHealthHelperRecoveryMechanism = $latestSummaryHelperRecoveryMechanism
        }
        elseif ($latestHelperRecoveryProgressCorridorCount -gt 0 -or $latestHelperRecoveryWindowActive -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'recovery_corridor'
        }
        elseif ($latestHelperRecoveryKeyframePendingVisibleApplyCount -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'reserved_apply'
        }
        elseif ($latestHelperRecoveryFollowerWindowBufferedCount -gt 0 -or
                $latestHelperRecoveryFollowerWindowAppliedCount -gt 0 -or
                $latestHelperRecoveryFollowerWindowTrimmedCount -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'follower_window'
        }
        elseif ($latestHelperRecoveryRunwayContiguousFollowerBufferCount -gt 0 -or
                $latestHelperRecoveryRunwayContiguousFollowerApplyCount -gt 0 -or
                $latestHelperRecoveryRunwayAbortCount -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'runway_cleanup'
        }
        elseif ($latestHelperRecoveryWaitRejectBeforeRunwayCount -gt 0 -or
                $latestHelperSuppressedEmitDuringRecoveryWaitCount -gt 0) {
            $resolvedHealthHelperRecoveryMechanism = 'waiting_for_recovery_keyframe'
        }

        if (-not [string]::IsNullOrWhiteSpace($latestSummaryHelperSessionPhase)) {
            $resolvedHealthHelperSessionPhase = $latestSummaryHelperSessionPhase
        }
        elseif ($resolvedHealthRecoveryActive -gt 0 -or $resolvedHealthHelperRecoveryMechanism -ne 'none') {
            $resolvedHealthHelperSessionPhase = 'recovering'
        }
        elseif ($resolvedHealthBaselineEstablished -gt 0) {
            if ($latestHelperProgressStallMs -gt 0 -and $resolvedHealthSteadyVisibleProgressActive -le 0) {
                $resolvedHealthHelperSessionPhase = 'stalled'
            }
            else {
                $resolvedHealthHelperSessionPhase = 'visible_stable'
            }
        }
        else {
            $resolvedHealthHelperSessionPhase = 'no_visible_baseline'
        }

        if (-not [string]::IsNullOrWhiteSpace($latestSummaryDominantLossClass)) {
            $resolvedHealthDominantLossClass = $latestSummaryDominantLossClass
        }
        elseif ($latestHelperReassemblerLossCount -gt 0 -or
                $latestHelperLateFragmentAfterAppliedHeadCount -gt 0 -or
                $latestHelperLateFragmentAfterVisibleRecoveryCount -gt 0 -or
                $latestHelperUnattributedLossCount -gt 0 -or
                $latestHelperActionableLateFragmentCount -gt 0) {
            $resolvedHealthDominantLossClass = 'current_epoch_actionable_loss'
        }
        elseif ($latestHelperWaitingForRecoveryKeyframeRejectCount -gt 0 -or
                $latestHelperRecoveryWaitRejectBeforeRunwayCount -gt 0 -or
                $latestHelperRecoveryRunwayOverflowRejectCount -gt 0 -or
                $latestHelperSuppressedEmitDuringRecoveryWaitCount -gt 0 -or
                $latestHelperBlockedByReservedRecoveryFrameRejectCount -gt 0 -or
                $latestHelperDeferredPostRecoveryCandidateReplaceCount -gt 0 -or
                $latestHelperPreCandidateGapTailRejectedCount -gt 0 -or
                $latestHelperFutureTailQuarantinedDuringGapCount -gt 0 -or
                $latestHelperFutureTailQuarantinedAfterGapCount -gt 0) {
            $resolvedHealthDominantLossClass = 'same_epoch_recovery_suppressed'
        }
        elseif ($latestHelperOlderEpochCleanupAfterEpochAdvanceCount -gt 0) {
            $resolvedHealthDominantLossClass = 'older_epoch_cleanup'
        }
        else {
            $resolvedHealthDominantLossClass = 'benign_stale_cleanup'
        }

        if ($resolvedHealthHelperSessionPhase -eq 'recovering' -or
            $resolvedHealthHelperSessionPhase -eq 'stalled' -or
            $resolvedHealthDominantLossClass -eq 'current_epoch_actionable_loss') {
            $resolvedHealthDominantTroubleDomain = 'helper'
        }
        elseif ($resolvedHealthDominantPressureBlocker -eq 'bridge_health' -or
                $resolvedHealthDominantPressureBlocker -eq 'queue_evict' -or
                $resolvedHealthDominantPressureBlocker -eq 'rate_gate') {
            $resolvedHealthDominantTroubleDomain = 'transport'
        }
        elseif ($resolvedHealthSenderGuardState -ne 'none' -or
                $resolvedHealthSenderOperatingState -ne 'normal') {
            $resolvedHealthDominantTroubleDomain = 'sender'
        }
        else {
            $resolvedHealthDominantTroubleDomain = 'none'
        }
    }

    if ($healthSnapshotLines.Count -eq 0) {
        $syntheticHealthSnapshotLine =
            "event=screenshare_health_snapshot; sender_operating_state=$resolvedHealthSenderOperatingState; sender_guard_state=$resolvedHealthSenderGuardState; helper_session_phase=$resolvedHealthHelperSessionPhase; helper_recovery_mechanism=$resolvedHealthHelperRecoveryMechanism; dominant_loss_class=$resolvedHealthDominantLossClass; dominant_pressure_blocker=$resolvedHealthDominantPressureBlocker; dominant_trouble_domain=$resolvedHealthDominantTroubleDomain; recovery_active=$resolvedHealthRecoveryActive; baseline_established=$resolvedHealthBaselineEstablished; steady_visible_progress_active=$resolvedHealthSteadyVisibleProgressActive"
        [void]$healthSnapshotLines.Add($syntheticHealthSnapshotLine)
    }

    return [pscustomobject]@{
        CaptureSampleCount = $captureValues.Count
        CaptureAvgMs = if ($captureValues.Count -gt 0) { [math]::Round((($captureValues | Measure-Object -Average).Average), 1) } else { -1 }
        CaptureMinMs = if ($captureValues.Count -gt 0) { ($captureValues | Measure-Object -Minimum).Minimum } else { -1 }
        CaptureMaxMs = if ($captureValues.Count -gt 0) { ($captureValues | Measure-Object -Maximum).Maximum } else { -1 }
        HelperApplyCount = if ($latestHelperFramesApplied -ge 0) { $latestHelperFramesApplied } else { $helperValues.Count }
        HelperApplySampleCount = $helperValues.Count
        HelperApplyAvgMs = if ($helperValues.Count -gt 0) { [math]::Round((($helperValues | Measure-Object -Average).Average), 1) } else { -1 }
        HelperApplyMinMs = if ($helperValues.Count -gt 0) { ($helperValues | Measure-Object -Minimum).Minimum } else { -1 }
        HelperApplyMaxMs = if ($helperValues.Count -gt 0) { ($helperValues | Measure-Object -Maximum).Maximum } else { -1 }
        HelperApplyP95Ms = if ($helperValues.Count -gt 0) { Get-PercentileValue -Values $helperValues -Percentile 95 } else { -1 }
        HelperStaleDrops = $helperStaleDrops
        ReceiverSupersededFrames = $receiverSupersededFrames
        PersistentSummaryCount = $persistentSummaries
        SinkWriterSummaryCount = $sinkWriterSummaries
        NormalModeSummaryCount = $normalModeSummaries
        ReducedModeSummaryCount = $reducedModeSummaries
        CatchUpModeSummaryCount = $catchUpModeSummaries
        BridgeHealthAdvisorySummaryCount = $bridgeHealthAdvisorySummaries
        BridgeHealthActionableSummaryCount = $bridgeHealthActionableSummaries
        LatestBridgeMediaMessagesReceived = $latestBridgeMediaMessagesReceived
        LatestMediaPlaneFramesSent = $latestMediaPlaneFramesSent
        LatestMediaPlaneAttached = $latestMediaPlaneAttached
        RecoveryControlFallbackQueuedCount = $recoveryControlFallbackQueuedCount
        SteadyStateControlFallbackQueuedCount = $steadyStateControlFallbackQueuedCount
        EffectiveMediaPlaneActive = $effectiveMediaPlaneActive
        RecoveryUsedControlFallback = $recoveryUsedControlFallback
        SteadyStateUsedControlFallback = $steadyStateUsedControlFallback
        LatestFramesQueued = $latestFramesQueued
        LatestFramesDeferredToSendSlot = $latestFramesDeferredToSendSlot
        LatestFramesReplacedBeforeSendSlot = $latestFramesReplacedBeforeSendSlot
        LatestFramesDroppedByQueueEvict = $latestFramesDroppedByQueueEvict
        LatestSendSlotEmptyCount = $latestSendSlotEmptyCount
        LatestSlotCoalescingActive = $latestSlotCoalescingActive
        LatestRawFramesDeferredToEncodeSlot = $latestRawFramesDeferredToEncodeSlot
        LatestRawFramesReplacedBeforeEncodeSlot = $latestRawFramesReplacedBeforeEncodeSlot
        LatestRawEncodeSlotEmptyCount = $latestRawEncodeSlotEmptyCount
        LatestRawSlotCoalescingActive = $latestRawSlotCoalescingActive
        LatestPromotionCaptureToSendBudgetMs = $latestPromotionCaptureToSendBudgetMs
        LatestSourceSupersededPendingFrames = $latestSourceSupersededPendingFrames
        LatestAvgFragmentsPerFrame = if ($latestAvgFragmentsPerFrame -ge 0) { [math]::Round($latestAvgFragmentsPerFrame, 2) } else { -1 }
        LatestAvgPayloadsPerFrame = if ($latestAvgPayloadsPerFrame -ge 0) { [math]::Round($latestAvgPayloadsPerFrame, 2) } else { -1 }
        LatestBatchPayloadCount = $latestBatchPayloadCount
        LatestLegacyPayloadCount = $latestLegacyPayloadCount
        LatestOrdinaryNonKeyBatchedPayloadCount = $latestOrdinaryNonKeyBatchedPayloadCount
        LatestOrdinaryNonKeyLegacyPayloadCount = $latestOrdinaryNonKeyLegacyPayloadCount
        LatestKeyframeRecoveryBatchedPayloadCount = $latestKeyframeRecoveryBatchedPayloadCount
        LatestEmittedDisplayableFrames = $latestEmittedDisplayableFrames
        LatestEmittedNonDisplayableUnits = $latestEmittedNonDisplayableUnits
        LatestEmittedIdrFrames = $latestEmittedIdrFrames
        LatestEmittedPFrames = $latestEmittedPFrames
        LatestDroppedBFrames = $latestDroppedBFrames
        LatestDroppedMultiPictureUnits = $latestDroppedMultiPictureUnits
        LatestDisplayableFrameRatio = if ($latestDisplayableFrameRatio -ge 0) { [math]::Round($latestDisplayableFrameRatio, 2) } else { -1 }
        LatestIdrFrameRatio = if ($latestIdrFrameRatio -ge 0) { [math]::Round($latestIdrFrameRatio, 2) } else { -1 }
        LatestAverageEncodedFrameBytes = if ($latestAverageEncodedFrameBytes -ge 0) { [math]::Round($latestAverageEncodedFrameBytes, 1) } else { -1 }
        LatestTransportIpOnlyMode = $latestTransportIpOnlyMode
        LatestLastAccessUnitKind = $latestLastAccessUnitKind
        LatestLowDelayConfigApplied = $latestLowDelayConfigApplied
        LatestHelperFramesCompleted = $latestHelperFramesCompleted
        LatestHelperFramesEnqueuedForDecode = $latestHelperFramesEnqueuedForDecode
        LatestHelperFramesDroppedBeforeDecode = $latestHelperFramesDroppedBeforeDecode
        LatestHelperFramesDecoded = $latestHelperFramesDecoded
        LatestHelperFramesDroppedAfterDecode = $latestHelperFramesDroppedAfterDecode
        LatestHelperFramesApplied = $latestHelperFramesApplied
        LatestHelperNeedMoreInputCount = $latestHelperNeedMoreInputCount
        LatestHelperCompletedWithoutPictureCount = $latestHelperCompletedWithoutPictureCount
        LatestHelperDecodeDurationMs = if ($latestHelperDecodeDurationMs -ge 0) { [math]::Round($latestHelperDecodeDurationMs, 1) } else { -1 }
        LatestHelperApplyIntervalMs = if ($latestHelperApplyIntervalMs -ge 0) { [math]::Round($latestHelperApplyIntervalMs, 1) } else { -1 }
        LatestHelperMaxPendingEncodedDepth = $latestHelperMaxPendingEncodedDepth
        LatestHelperMaxPendingDecodedDepth = $latestHelperMaxPendingDecodedDepth
        LatestHelperAvgEnqueueToDecodeStartMs = if ($latestHelperAvgEnqueueToDecodeStartMs -ge 0) { [math]::Round($latestHelperAvgEnqueueToDecodeStartMs, 1) } else { -1 }
        LatestHelperAvgEnqueueToDropMs = if ($latestHelperAvgEnqueueToDropMs -ge 0) { [math]::Round($latestHelperAvgEnqueueToDropMs, 1) } else { -1 }
        LatestHelperDecodeWorkerDropQueueOverflowCount = $latestHelperDecodeWorkerDropQueueOverflowCount
        LatestHelperDecodeWorkerDropAgeBudgetCount = $latestHelperDecodeWorkerDropAgeBudgetCount
        LatestHelperDecodeWorkerDropGenerationCount = $latestHelperDecodeWorkerDropGenerationCount
        LatestHelperDecodeWorkerDropStoppedCount = $latestHelperDecodeWorkerDropStoppedCount
        LatestHelperReassemblerLossCount = $latestHelperReassemblerLossCount
        LatestHelperEnqueueRejectCount = $latestHelperEnqueueRejectCount
        LatestHelperWaitingForRecoveryKeyframeRejectCount = $latestHelperWaitingForRecoveryKeyframeRejectCount
        LatestHelperRecoveryWaitRejectBeforeRunwayCount = [Math]::Max(0, $latestHelperRecoveryWaitRejectBeforeRunwayCount)
        LatestHelperRecoveryRunwayOverflowRejectCount = [Math]::Max(0, $latestHelperRecoveryRunwayOverflowRejectCount)
        LatestHelperSuppressedEmitDuringRecoveryWaitCount = [Math]::Max(0, $latestHelperSuppressedEmitDuringRecoveryWaitCount)
        LatestHelperStaleSupersededRecoverySuppressedCount = [Math]::Max(0, $latestHelperStaleSupersededRecoverySuppressedCount)
        LatestHelperSoftStaleCleanupCount = [Math]::Max(0, $latestHelperSoftStaleCleanupCount)
        LatestHelperBlockedByReservedRecoveryFrameRejectCount = $latestHelperBlockedByReservedRecoveryFrameRejectCount
        LatestHelperOlderEpochIgnoredDuringRecoveryLockCount = $latestHelperOlderEpochIgnoredDuringRecoveryLockCount
        LatestHelperNewerEpochNonKeyIgnoredDuringLockCount = $latestHelperNewerEpochNonKeyIgnoredDuringLockCount
        LatestHelperDeferredPostRecoveryCandidateReplaceCount = $latestHelperDeferredPostRecoveryCandidateReplaceCount
        LatestHelperDecodeWorkerDropCount = $latestHelperDecodeWorkerDropCount
        LatestHelperPostDecodeDropCount = $latestHelperPostDecodeDropCount
        LatestHelperDecodeQueueOverflowCount = $latestHelperDecodeQueueOverflowCount
        LatestHelperDecodeAgeBudgetCount = $latestHelperDecodeAgeBudgetCount
        LatestHelperDecodeGenerationChangedCount = $latestHelperDecodeGenerationChangedCount
        LatestHelperDecodeStoppedCount = $latestHelperDecodeStoppedCount
        LatestHelperDecodedApplyQueueOverflowCount = $latestHelperDecodedApplyQueueOverflowCount
        LatestHelperDecodedFrameReplacedBeforeApplyCount = $latestHelperDecodedFrameReplacedBeforeApplyCount
        LatestOrdinaryRawLossCount = $latestOrdinaryRawLossCount
        LatestOrdinarySenderLossCount = $latestOrdinarySenderLossCount
        LatestOrdinaryHelperLossCount = $latestOrdinaryHelperLossCount
        DominantOrdinaryFreshnessLossBoundary = $dominantOrdinaryFreshnessLossBoundary
        LatestHelperStaleDroppedAfterDecodeCount = [Math]::Max(0, $latestHelperStaleDroppedAfterDecodeCount)
        LatestHelperDroppedWaitingForRecoveryKeyframeCount = $latestHelperDroppedWaitingForRecoveryKeyframeCount
        LatestHelperGapNonKeyPrunedCount = $latestHelperGapNonKeyPrunedCount
        LatestHelperFutureTailQuarantinedDuringGapCount = [Math]::Max(0, $latestHelperFutureTailQuarantinedDuringGapCount)
        LatestHelperFutureTailQuarantinedAfterGapCount = [Math]::Max(0, $latestHelperFutureTailQuarantinedAfterGapCount)
        LatestHelperPreCandidateGapTailRejectedCount = [Math]::Max(0, $latestHelperPreCandidateGapTailRejectedCount)
        LatestHelperRecoveryCandidatePresentCount = [Math]::Max(0, $latestHelperRecoveryCandidatePresentCount)
        LatestHelperVisibleRecoveryFloorFrameId = $latestHelperVisibleRecoveryFloorFrameId
        LatestHelperStableVisibleHeadFrameId = $latestHelperStableVisibleHeadFrameId
        LatestHelperAppliedHeadFrameId = $latestHelperAppliedHeadFrameId
        LatestHelperOrderedEmitHeadFrameId = $latestHelperOrderedEmitHeadFrameId
        LatestHelperWinningRecoveryFrameId = $latestHelperWinningRecoveryFrameId
        LatestHelperVisibleHeadFrameId = $latestHelperVisibleHeadFrameId
        LatestHelperSupersededRecoveryTailCleanupCount = [Math]::Max(0, $latestHelperSupersededRecoveryTailCleanupCount)
        LatestHelperLateSameEpochAfterHeadAdvancedDropCount = [Math]::Max(0, $latestHelperLateSameEpochAfterHeadAdvancedDropCount)
        LatestHelperStaleRunwayWindowAbortCount = [Math]::Max(0, $latestHelperStaleRunwayWindowAbortCount)
        LatestHelperRunwayCandidateExpiredAfterHeadAdvanceCount = [Math]::Max(0, $latestHelperRunwayCandidateExpiredAfterHeadAdvanceCount)
        LatestHelperRunwayFollowersEmittedWithinActionableWindowCount = [Math]::Max(0, $latestHelperRunwayFollowersEmittedWithinActionableWindowCount)
        LatestHelperRecoveryOwnerReplacedCount = [Math]::Max(0, $latestHelperRecoveryOwnerReplacedCount)
        LatestHelperOlderEpochCleanupAfterEpochAdvanceCount = [Math]::Max(0, $latestHelperOlderEpochCleanupAfterEpochAdvanceCount)
        LatestHelperSteadyVisibleProgressActive = [Math]::Max(0, $latestHelperSteadyVisibleProgressActive)
        LatestHelperSteadyVisibleProgressActivationFrameId = $latestHelperSteadyVisibleProgressActivationFrameId
        LatestHelperFramesAppliedSinceLastGap = [Math]::Max(0, $latestHelperFramesAppliedSinceLastGap)
        LatestRemoteHelperFactHealthyActive = [Math]::Max(0, $latestRemoteHelperFactHealthyActive)
        LatestRemoteHelperFactHealthySource = if ([string]::IsNullOrWhiteSpace($latestRemoteHelperFactHealthySource)) { 'none' } else { $latestRemoteHelperFactHealthySource }
        LatestRemoteHelperFactProofFrameId = $latestRemoteHelperFactProofFrameId
        LatestRemoteHelperFactLastMessageAgeMs = $latestRemoteHelperFactLastMessageAgeMs
        LatestRemoteHelperFactHealthyClearCount = [Math]::Max(0, $latestRemoteHelperFactHealthyClearCount)
        LatestRemoteHelperFactHealthyClearReason = if ([string]::IsNullOrWhiteSpace($latestRemoteHelperFactHealthyClearReason)) { 'none' } else { $latestRemoteHelperFactHealthyClearReason }
        LatestHelperLastSentStableVisibleHeadFrameId = $latestHelperLastSentStableVisibleHeadFrameId
        LatestHelperPressureSendBypassedForVisibleProgressCount = [Math]::Max(0, $latestHelperPressureSendBypassedForVisibleProgressCount)
        LatestHelperProofKeepaliveSendCount = [Math]::Max(0, $latestHelperProofKeepaliveSendCount)
        LatestHelperProofKeepaliveTimerDrivenSendCount = [Math]::Max(0, $latestHelperProofKeepaliveTimerDrivenSendCount)
        LatestHelperProofKeepaliveLastHeadFrameId = $latestHelperProofKeepaliveLastHeadFrameId
        LatestHelperProofKeepaliveLastSendAgeMs = $latestHelperProofKeepaliveLastSendAgeMs
        LatestHelperFirstVisibleApplyToSenderFactSendMs = $latestHelperFirstVisibleApplyToSenderFactSendMs
        LatestHelperSteadyVisibleProgressClearedCount = [Math]::Max(0, $latestHelperSteadyVisibleProgressClearedCount)
        LatestHelperSteadyVisibleProgressClearedReason = if ([string]::IsNullOrWhiteSpace($latestHelperSteadyVisibleProgressClearedReason)) { 'none' } else { $latestHelperSteadyVisibleProgressClearedReason }
        LatestHelperLateFragmentAfterAppliedHeadCount = [Math]::Max(0, $latestHelperLateFragmentAfterAppliedHeadCount)
        LatestHelperLateFragmentAfterOrderedHeadCount = [Math]::Max(0, $latestHelperLateFragmentAfterOrderedHeadCount)
        LatestHelperLateFragmentAfterStableVisibleHeadCount = [Math]::Max(0, $latestHelperLateFragmentAfterStableVisibleHeadCount)
        LatestHelperLateFragmentAfterVisibleRecoveryCount = [Math]::Max(0, $latestHelperLateFragmentAfterVisibleRecoveryCount)
        LatestHelperPreCandidateGapTailEmittedToViewerCount = [Math]::Max(0, $latestHelperPreCandidateGapTailEmittedToViewerCount)
        LatestHelperHighFrameAgeSuppressedDueToVisibleProgressCount = [Math]::Max(0, $latestHelperHighFrameAgeSuppressedDueToVisibleProgressCount)
        LatestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount = [Math]::Max(0, $latestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount)
        LatestHelperActionableHighFrameAgeCount = [Math]::Max(0, $latestHelperActionableHighFrameAgeCount)
        LatestHelperActionableLateFragmentCount = [Math]::Max(0, $latestHelperActionableLateFragmentCount)
        LatestRecoveryBurstActive = [Math]::Max(0, $latestRecoveryBurstActive)
        LatestRecoveryBurstPhase = if ([string]::IsNullOrWhiteSpace($latestRecoveryBurstPhase)) { 'idle' } else { $latestRecoveryBurstPhase }
        LatestRecoveryBurstStreamEpoch = $latestRecoveryBurstStreamEpoch
        LatestRecoveryOwnerFrameId = $latestRecoveryOwnerFrameId
        LatestRecoveryProtectedFollowerCount = [Math]::Max(0, $latestRecoveryProtectedFollowerCount)
        LatestRecoveryGapCount = [Math]::Max(0, $latestRecoveryGapCount)
        LatestRecoveryGapToKeyframeRequestMs = $latestRecoveryGapToKeyframeRequestMs
        LatestRecoveryKeyframeRequestToOwnerEmitMs = $latestRecoveryKeyframeRequestToOwnerEmitMs
        LatestRecoveryOwnerAckWindowMs = $latestRecoveryOwnerAckWindowMs
        LatestRecoveryOwnerEmitToAckMs = $latestRecoveryOwnerEmitToAckMs
        LatestRecoveryPostAckHoldActive = [Math]::Max(0, $latestRecoveryPostAckHoldActive)
        LatestRecoveryPostAckHoldStartedCount = [Math]::Max(0, $latestRecoveryPostAckHoldStartedCount)
        LatestRecoveryPostAckHoldExpiredCount = [Math]::Max(0, $latestRecoveryPostAckHoldExpiredCount)
        LatestRecoveryPostAckHoldSuppressedReopenCount = [Math]::Max(0, $latestRecoveryPostAckHoldSuppressedReopenCount)
        LatestRecoveryOwnerAckFrameId = $latestRecoveryOwnerAckFrameId
        LatestRecoveryAckSource = if ([string]::IsNullOrWhiteSpace($latestRecoveryAckSource)) { 'none' } else { $latestRecoveryAckSource }
        LatestRecoveryOwnerEmitToFirstVisibleApplyMs = $latestRecoveryOwnerEmitToFirstVisibleApplyMs
        LatestRecoveryBurstControlFallbackCount = [Math]::Max(0, $latestRecoveryBurstControlFallbackCount)
        LatestRecoveryBurstTimeoutCount = [Math]::Max(0, $latestRecoveryBurstTimeoutCount)
        LatestRecoveryBurstCompletedCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedCount), $eventRecoveryBurstCompletedCount)
        LatestRecoveryBurstRestartSuppressedCount = [Math]::Max(0, $latestRecoveryBurstRestartSuppressedCount)
        LatestRecoveryBurstEncoderRerequestCount = [Math]::Max(0, $latestRecoveryBurstEncoderRerequestCount)
        LatestRecoveryOwnerPendingForcedResetCount = [Math]::Max([Math]::Max(0, $latestRecoveryOwnerPendingForcedResetCount), $eventRecoveryOwnerPendingForcedResetCount)
        LatestRecoveryKeyframeEmittedAfterForcedResetCount = [Math]::Max([Math]::Max(0, $latestRecoveryKeyframeEmittedAfterForcedResetCount), $eventRecoveryKeyframeEmittedAfterForcedResetCount)
        LatestRecoveryBurstCompletedByHelperAckCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByHelperAckCount), $eventRecoveryBurstCompletedByHelperAckCount)
        LatestRecoveryBurstCompletedByAppliedHeadAckCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByAppliedHeadAckCount), $eventRecoveryBurstCompletedByAppliedHeadAckCount)
        LatestRecoveryBurstCompletedByLastVisibleApplyAckCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByLastVisibleApplyAckCount), $eventRecoveryBurstCompletedByLastVisibleApplyAckCount)
        LatestRecoveryBurstCompletedByVisibleRecoveryFloorCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByVisibleRecoveryFloorCount), $eventRecoveryBurstCompletedByVisibleRecoveryFloorCount)
        LatestRecoveryBurstCompletedByVisibleApplyFallbackCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByVisibleApplyFallbackCount), $eventRecoveryBurstCompletedByVisibleApplyFallbackCount)
        LatestRecoveryBurstCompletedByTimeoutCount = [Math]::Max([Math]::Max(0, $latestRecoveryBurstCompletedByTimeoutCount), $eventRecoveryBurstCompletedByTimeoutCount)
        LatestRecoveryBurstCompletedByProtectedFramesCount = [Math]::Max(0, $latestRecoveryBurstCompletedByProtectedFramesCount)
        LatestRecoveryBurstProfileTransitionDeferredCount = [Math]::Max(0, $latestRecoveryBurstProfileTransitionDeferredCount)
        LatestRecoveryBurstProfileTransitionTakeoverCount = [Math]::Max(0, $latestRecoveryBurstProfileTransitionTakeoverCount)
        LatestRecoveryBurstStaleRequestSuppressedCount = [Math]::Max(0, $latestRecoveryBurstStaleRequestSuppressedCount)
        LatestRecoveryBurstRequestSuppressedDueToHelperAckCount = [Math]::Max(0, $latestRecoveryBurstRequestSuppressedDueToHelperAckCount)
        LatestRecoveryBurstStartedWhileHelperProofHealthyCount = [Math]::Max(0, $latestRecoveryBurstStartedWhileHelperProofHealthyCount)
        LatestLastCompletedRecoveryEpoch = $latestLastCompletedRecoveryEpoch
        LatestLastCompletedRecoveryOwnerFrameId = $latestLastCompletedRecoveryOwnerFrameId
        LatestLastCompletedRecoveryAckFrameId = $latestLastCompletedRecoveryAckFrameId
        LatestLastCompletedRecoveryAckSource = if ([string]::IsNullOrWhiteSpace($latestLastCompletedRecoveryAckSource)) { 'none' } else { $latestLastCompletedRecoveryAckSource }
        LatestLastCompletedRecoveryOwnerEmitToAckMs = $latestLastCompletedRecoveryOwnerEmitToAckMs
        LatestLastCompletedRecoveryCompletionKind = if ([string]::IsNullOrWhiteSpace($latestLastCompletedRecoveryCompletionKind)) { 'none' } else { $latestLastCompletedRecoveryCompletionKind }
        LatestRecoveryCompletionAccountingMismatch = [Math]::Max(0, $latestRecoveryCompletionAccountingMismatch)
        LatestRecoveryOwnerPendingNonKeyHeldCount = [Math]::Max(0, $latestRecoveryOwnerPendingNonKeyHeldCount)
        LatestRecoveryOwnerPendingNonKeyReplacedCount = [Math]::Max(0, $latestRecoveryOwnerPendingNonKeyReplacedCount)
        LatestRecoveryOwnerUnackedNonKeyHeldCount = [Math]::Max(0, $latestRecoveryOwnerUnackedNonKeyHeldCount)
        LatestRecoveryOwnerUnackedNonKeyReplacedCount = [Math]::Max(0, $latestRecoveryOwnerUnackedNonKeyReplacedCount)
        LatestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = [Math]::Max(0, $latestRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount)
        LatestRecoveryOwnerReplacedBeforeAckCount = [Math]::Max(0, $latestRecoveryOwnerReplacedBeforeAckCount)
        LatestHighFrameAgeSuppressedDuringOwnerAckCount = [Math]::Max(0, $latestHighFrameAgeSuppressedDuringOwnerAckCount)
        LatestRecoveryTimeoutWhileHelperHeadAdvancedCount = [Math]::Max(0, $latestRecoveryTimeoutWhileHelperHeadAdvancedCount)
        LatestSenderReceivedHelperProgressDuringContinuityLossCount = [Math]::Max(0, $latestSenderReceivedHelperProgressDuringContinuityLossCount)
        LatestHelperAckAfterFactSendMs = $latestHelperAckAfterFactSendMs
        LatestPostAckModeGraceSuppressedHighFrameAgeCount = [Math]::Max(0, $latestPostAckModeGraceSuppressedHighFrameAgeCount)
        LatestBootstrapGraceSuppressedCatchUpCount = [Math]::Max(0, $latestBootstrapGraceSuppressedCatchUpCount)
        LatestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = [Math]::Max(0, $latestCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount)
        LatestCatchUpExitWhileRemoteHighFrameAgePressureCount = [Math]::Max(0, $latestCatchUpExitWhileRemoteHighFrameAgePressureCount)
        LatestProtectedRecoveryFramesDispatchedCount = [Math]::Max(0, $latestProtectedRecoveryFramesDispatchedCount)
        LatestRecoveryProtectedFrameBlockedByOrdinaryCount = [Math]::Max(0, $latestRecoveryProtectedFrameBlockedByOrdinaryCount)
        LatestLastAcknowledgedRecoveryOwnerFrameId = $latestLastAcknowledgedRecoveryOwnerFrameId
        LatestLastAcknowledgedHelperHeadFrameId = $latestLastAcknowledgedHelperHeadFrameId
        LatestRemoteHelperVisibleHeadFrameId = $latestRemoteHelperVisibleHeadFrameId
        LatestRemoteHelperVisibleRecoveryFloorFrameId = $latestRemoteHelperVisibleRecoveryFloorFrameId
        LatestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount = [Math]::Max(0, $latestRemoteHelperCurrentEpochRecoveryKeyframeApplyCount)
        LatestLastAcknowledgedVisibleHelperHeadFrameId = $latestLastAcknowledgedVisibleHelperHeadFrameId
        LatestLastAcknowledgedHelperProofAgeMs = $latestLastAcknowledgedHelperProofAgeMs
        LatestPersistedReleaseFloorEpoch = $latestPersistedReleaseFloorEpoch
        LatestSatisfiedRecoveryFloorFrameId = $latestSatisfiedRecoveryFloorFrameId
        LatestSatisfiedRecoveryFloorSource = if ([string]::IsNullOrWhiteSpace($latestSatisfiedRecoveryFloorSource)) { 'none' } else { $latestSatisfiedRecoveryFloorSource }
        LatestSatisfiedRecoveryFloorVisibleProofCount = [Math]::Max(0, $latestSatisfiedRecoveryFloorVisibleProofCount)
        LatestContinuitySignalIgnoredDueToSatisfiedFloorCount = [Math]::Max(0, $latestContinuitySignalIgnoredDueToSatisfiedFloorCount)
        LatestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount = [Math]::Max(0, $latestContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount)
        LatestRecoveryLockClearedByAcknowledgedProofCount = [Math]::Max(0, $latestRecoveryLockClearedByAcknowledgedProofCount)
        LatestRecoveryLockClearedByVisibleProofCount = [Math]::Max(0, $latestRecoveryLockClearedByVisibleProofCount)
        LatestRecoveryLockLastClearReason = if ([string]::IsNullOrWhiteSpace($latestRecoveryLockLastClearReason)) { 'none' } else { $latestRecoveryLockLastClearReason }
        LatestHelperProgressPastOwnerWithoutBurstAckCount = [Math]::Max(0, $latestHelperProgressPastOwnerWithoutBurstAckCount)
        LatestPostRecoveryAgeGraceActive = [Math]::Max(0, $latestPostRecoveryAgeGraceActive)
        LatestPostRecoveryAgeGraceSuppressedCount = [Math]::Max(0, $latestPostRecoveryAgeGraceSuppressedCount)
        RecoveryControlBootstrapRetrySkippedDueToBurstResolvedCount = [Math]::Max(0, $recoveryControlBootstrapRetrySkippedDueToBurstResolvedCount)
        RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount = [Math]::Max(0, $recoveryControlBootstrapRetryQueuedAfterBurstResolutionCount)
        RecoveryBurstCompletedWithoutHelperAdvance = [Math]::Max(0, $recoveryBurstCompletedWithoutHelperAdvance)
        RecoveryAckMissedDespiteHelperProgress = [Math]::Max(0, $recoveryAckMissedDespiteHelperProgress)
        LatestHelperRecoveryRunwayContiguousFollowerBufferCount = [Math]::Max(0, $latestHelperRecoveryRunwayContiguousFollowerBufferCount)
        LatestHelperRecoveryRunwayContiguousFollowerApplyCount = [Math]::Max(0, $latestHelperRecoveryRunwayContiguousFollowerApplyCount)
        LatestHelperRecoveryRunwayAbortCount = [Math]::Max(0, $latestHelperRecoveryRunwayAbortCount)
        LatestHelperRecoveryKeyframeResyncCount = $latestHelperRecoveryKeyframeResyncCount
        LatestHelperGapActive = $latestHelperGapActive
        LatestHelperGapExpectedFrameId = $latestHelperGapExpectedFrameId
        LatestHelperBufferedRecoveryKeyframeFrameId = $latestHelperBufferedRecoveryKeyframeFrameId
        LatestHelperFutureNonKeyBufferedCount = $latestHelperFutureNonKeyBufferedCount
        LatestHelperRecoveryFollowerWindowBufferedCount = [Math]::Max(0, $latestHelperRecoveryFollowerWindowBufferedCount)
        LatestHelperRecoveryFollowerWindowAppliedCount = [Math]::Max(0, $latestHelperRecoveryFollowerWindowAppliedCount)
        LatestHelperRecoveryFollowerWindowTrimmedCount = [Math]::Max(0, $latestHelperRecoveryFollowerWindowTrimmedCount)
        LatestHelperProtectedRecoveryDeliveryCount = [Math]::Max(0, $latestHelperProtectedRecoveryDeliveryCount)
        LatestHelperRecoveryProgressCorridorCount = [Math]::Max(0, $latestHelperRecoveryProgressCorridorCount)
        LatestHelperRecoveryProgressCorridorSuccessCount = [Math]::Max(0, $latestHelperRecoveryProgressCorridorSuccessCount)
        LatestHelperRecoveryProgressCorridorAbortCount = [Math]::Max(0, $latestHelperRecoveryProgressCorridorAbortCount)
        LatestHelperRecoveryProgressCorridorAppliedCount = [Math]::Max(0, $latestHelperRecoveryProgressCorridorAppliedCount)
        LatestHelperRecoveryKeyframePendingVisibleApplyCount = [Math]::Max(0, $latestHelperRecoveryKeyframePendingVisibleApplyCount)
        LatestHelperStartupCorridorBufferedFollowerCount = [Math]::Max(0, $latestHelperStartupCorridorBufferedFollowerCount)
        LatestHelperStartupCorridorReleaseCount = [Math]::Max(0, $latestHelperStartupCorridorReleaseCount)
        LatestHelperStartupCorridorAbortCount = [Math]::Max(0, $latestHelperStartupCorridorAbortCount)
        LatestHelperStartupCorridorAbortReason = if ([string]::IsNullOrWhiteSpace($latestHelperStartupCorridorAbortReason)) { 'none' } else { $latestHelperStartupCorridorAbortReason }
        LatestHelperPostRecoveryVisibleGenerationResetCount = [Math]::Max(0, $latestHelperPostRecoveryVisibleGenerationResetCount)
        LatestHelperPostRecoveryPurgedPreRecoveryFollowerCount = [Math]::Max(0, $latestHelperPostRecoveryPurgedPreRecoveryFollowerCount)
        LatestHelperPostRecoveryStaleDropBypassCount = [Math]::Max(0, $latestHelperPostRecoveryStaleDropBypassCount)
        LatestHelperLateFragmentAfterSuccessfulRecoveryCount = [Math]::Max(0, $latestHelperLateFragmentAfterSuccessfulRecoveryCount)
        LatestHelperUnattributedLossCount = $latestHelperUnattributedLossCount
        LatestHelperRecentLosses = $latestHelperRecentLosses
        LatestHelperVisibleApplyRatio = if ($latestHelperVisibleApplyRatio -ge 0) { [math]::Round($latestHelperVisibleApplyRatio, 2) } else { -1 }
        LatestHelperAvgDecodeCompleteToVisibleApplyMs = if ($latestHelperAvgDecodeCompleteToVisibleApplyMs -ge 0) { [math]::Round($latestHelperAvgDecodeCompleteToVisibleApplyMs, 1) } else { -1 }
        LatestHelperAvgUiPostApplyMs = if ($latestHelperAvgUiPostApplyMs -ge 0) { [math]::Round($latestHelperAvgUiPostApplyMs, 1) } else { -1 }
        LatestHelperAvgVisibleHeadLagFrames = if ($latestHelperAvgVisibleHeadLagFrames -ge 0) { [math]::Round($latestHelperAvgVisibleHeadLagFrames, 1) } else { -1 }
        LatestHelperAvgStableHeadLagFrames = if ($latestHelperAvgStableHeadLagFrames -ge 0) { [math]::Round($latestHelperAvgStableHeadLagFrames, 1) } else { -1 }
        LatestHelperLastReservedApplyHoldMs = [Math]::Max(-1, $latestHelperLastReservedApplyHoldMs)
        LatestHelperLastRecoveryProgressCorridorHoldMs = [Math]::Max(-1, $latestHelperLastRecoveryProgressCorridorHoldMs)
        LatestHelperLastRecoveryRunwayAbortHoldMs = [Math]::Max(-1, $latestHelperLastRecoveryRunwayAbortHoldMs)
        LatestHelperLastRecoveryProgressCorridorAbortReason = if ([string]::IsNullOrWhiteSpace($latestHelperLastRecoveryProgressCorridorAbortReason)) { 'none' } else { $latestHelperLastRecoveryProgressCorridorAbortReason }
        LatestHelperGapCount = $latestHelperGapCount
        LatestHelperRecoveryKeyframeApplyCount = $latestHelperRecoveryKeyframeApplyCount
        LatestHelperResyncCount = $latestHelperResyncCount
        LatestHelperDominantReassemblerRootCause = if ([string]::IsNullOrWhiteSpace($latestHelperDominantReassemblerRootCause)) { 'none' } else { $latestHelperDominantReassemblerRootCause }
        LatestHelperDominantAdmissionRejectReason = $effectiveDominantHelperAdmissionRejectReason
        LatestHealthSenderOperatingState = $resolvedHealthSenderOperatingState
        LatestHealthSenderGuardState = $resolvedHealthSenderGuardState
        LatestHealthHelperSessionPhase = $resolvedHealthHelperSessionPhase
        LatestHealthHelperRecoveryMechanism = $resolvedHealthHelperRecoveryMechanism
        LatestSummaryHelperSessionPhase = $latestSummaryHelperSessionPhase
        LatestSummaryHelperRecoveryMechanism = $latestSummaryHelperRecoveryMechanism
        LatestHealthDominantLossClass = $resolvedHealthDominantLossClass
        LatestHealthDominantPressureBlocker = $resolvedHealthDominantPressureBlocker
        LatestHealthDominantTroubleDomain = $resolvedHealthDominantTroubleDomain
        LatestHealthRecoveryActive = $resolvedHealthRecoveryActive
        LatestHealthBaselineEstablished = $resolvedHealthBaselineEstablished
        LatestHealthSteadyVisibleProgressActive = $resolvedHealthSteadyVisibleProgressActive
        LatestHelperPostRecoveryHighFrameAgeSuppressedTicks = [Math]::Max(0, $latestHelperPostRecoveryHighFrameAgeSuppressedTicks)
        LatestHelperVisibleAppliesDuringSettleCount = [Math]::Max(0, $latestHelperVisibleAppliesDuringSettleCount)
        LatestHelperPostRecoverySettleWindowCount = [Math]::Max(0, $latestHelperPostRecoverySettleWindowCount)
        LatestHelperPostRecoverySettleWindowSuccessCount = [Math]::Max(0, $latestHelperPostRecoverySettleWindowSuccessCount)
        LatestHelperPostRecoverySettleWindowTimeoutCount = [Math]::Max(0, $latestHelperPostRecoverySettleWindowTimeoutCount)
        LatestHelperVisibleAppliesBeforePressureReenabled = $latestHelperVisibleAppliesBeforePressureReenabled
        LatestHelperRecoveryWindowActive = [Math]::Max(0, $latestHelperRecoveryWindowActive)
        LatestHelperRecoveryWindowProgressed = [Math]::Max(0, $latestHelperRecoveryWindowProgressed)
        LatestHelperRecoveryWindowSucceeded = [Math]::Max(0, $latestHelperRecoveryWindowSucceeded)
        LatestHelperRecoveryWindowProgressedCount = [Math]::Max(0, $latestHelperRecoveryWindowProgressedCount)
        LatestHelperRecoveryWindowSuccessCount = [Math]::Max(0, $latestHelperRecoveryWindowSuccessCount)
        LatestHelperActiveRecoveryWindowEpoch = $latestHelperActiveRecoveryWindowEpoch
        LatestHelperActiveRecoveryWindowRecoveryFrameId = $latestHelperActiveRecoveryWindowRecoveryFrameId
        LatestHelperRecoveryWindowContiguousFollowerApplyCount = [Math]::Max(0, $latestHelperRecoveryWindowContiguousFollowerApplyCount)
        LatestHelperAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = [Math]::Max(0, $latestHelperAfterVisibleRecoveryFrameSuppressedDueToSuccessCount)
        LatestHelperRecoverySuccessCounterMismatch = if (
            (($latestHelperRecoveryWindowSuccessCount -ge 0) -and
             ($latestHelperRecoveryProgressCorridorSuccessCount -ge 0) -and
             ($latestHelperRecoveryWindowSuccessCount -ne $latestHelperRecoveryProgressCorridorSuccessCount)) -or
            (($latestHelperRecoveryWindowSuccessCount -ge 0) -and
             ($latestHelperPostRecoverySettleWindowSuccessCount -ge 0) -and
             ($latestHelperRecoveryWindowSuccessCount -ne $latestHelperPostRecoverySettleWindowSuccessCount))
        ) { 1 } else { 0 }
        LatestHelperBaselineEstablished = [Math]::Max(0, $latestHelperBaselineEstablished)
        LatestHelperBaselineCaptureToRenderMs = $latestHelperBaselineCaptureToRenderMs
        LatestHelperAgeExcessMs = $latestHelperAgeExcessMs
        LatestHelperProgressStallMs = $latestHelperProgressStallMs
        LatestHelperBaselineReseedInProgress = [Math]::Max(0, $latestHelperBaselineReseedInProgress)
        LatestHelperAgePressureConsecutiveCount = [Math]::Max(0, $latestHelperAgePressureConsecutiveCount)
        LatestHelperCadencePressureConsecutiveCount = [Math]::Max(0, $latestHelperCadencePressureConsecutiveCount)
        LatestHelperCatchUpSuppressedDueToProgressCount = [Math]::Max(0, $latestHelperCatchUpSuppressedDueToProgressCount)
        LatestHelperBaselineFrozenDueToStallCount = [Math]::Max(0, $latestHelperBaselineFrozenDueToStallCount)
        LatestHelperBaselineReseedAfterRecoveryCount = [Math]::Max(0, $latestHelperBaselineReseedAfterRecoveryCount)
        LatestHelperCadenceStallWindowCount = [Math]::Max(0, $latestHelperCadenceStallWindowCount)
        LatestHelperCadenceStallTriggerCount = [Math]::Max(0, $latestHelperCadenceStallTriggerCount)
        LatestHelperBridgeHealthAdvisoryCount = [Math]::Max(0, $latestHelperBridgeHealthAdvisoryCount)
        LatestHelperBridgeHealthActionableCount = [Math]::Max(0, $latestHelperBridgeHealthActionableCount)
        LatestHelperBridgeHealthQuarantineSuppressedCount = [Math]::Max(0, $latestHelperBridgeHealthQuarantineSuppressedCount)
        LatestHelperBridgeHealthActionableWithoutQueueOrDropCount = [Math]::Max(0, $latestHelperBridgeHealthActionableWithoutQueueOrDropCount)
        AggregateHighFrameAgeSuppressedDueToVisibleProgressCount = [Math]::Max(0, $aggregateHighFrameAgeSuppressedDueToVisibleProgressCount)
        AggregateHighFrameAgeSuppressedDueToHeadAdvanceCount = [Math]::Max(0, $aggregateHighFrameAgeSuppressedDueToHeadAdvanceCount)
        AggregateActionableHighFrameAgeCount = [Math]::Max(0, $aggregateActionableHighFrameAgeCount)
        AggregatePostRecoveryHighFrameAgeSuppressedTicks = [Math]::Max(0, $aggregatePostRecoveryHighFrameAgeSuppressedTicks)
        DominantReassemblerRootCause = $dominantReassemblerRootCause
        DominantHelperPressureBlocker = $dominantHelperPressureBlocker
        AggregateLateFragmentAfterAppliedHeadCount = [Math]::Max(0, $aggregateLateFragmentAfterAppliedHeadCount)
        AggregateLateFragmentAfterOrderedHeadCount = [Math]::Max(0, $aggregateLateFragmentAfterOrderedHeadCount)
        AggregateRecoveryOwnerReplacedCount = [Math]::Max(0, $aggregateRecoveryOwnerReplacedCount)
        AggregateOlderEpochCleanupAfterEpochAdvanceCount = [Math]::Max(0, $aggregateOlderEpochCleanupAfterEpochAdvanceCount)
        AggregateActionableLateFragmentCount = [Math]::Max(0, $aggregateActionableLateFragmentCount)
        WorstEpochByVisibleApplyRatio = $worstVisibleApplyRatioEpoch
        WorstEpochVisibleApplyRatio = if ($worstVisibleApplyRatio -ge 0) { [math]::Round($worstVisibleApplyRatio, 2) } else { -1 }
        WorstEpochByRecoveryLockTime = $worstRecoveryLockEpoch
        WorstEpochRecoveryLockTimeMs = $worstRecoveryLockMs
        LatestPromotionBlockerRateGateTicks = $latestPromotionBlockerRateGateTicks
        LatestPromotionBlockerHelperPressureTicks = $latestPromotionBlockerHelperPressureTicks
        LatestPromotionBlockerHelperWarmupTicks = $latestPromotionBlockerHelperWarmupTicks
        LatestPromotionBlockerHelperApplyCountTicks = $latestPromotionBlockerHelperApplyCountTicks
        LatestPromotionBlockerBridgeHealthTicks = $latestPromotionBlockerBridgeHealthTicks
        LatestPromotionBlockerRecoveryLockTicks = $latestPromotionBlockerRecoveryLockTicks
        LatestPromotionBlockerQueueEvictTicks = $latestPromotionBlockerQueueEvictTicks
        LatestPromotionBlockerCaptureAgeTicks = $latestPromotionBlockerCaptureAgeTicks
        LatestPromotionBlockerEncodeBudgetTicks = $latestPromotionBlockerEncodeBudgetTicks
        LatestPromotionBlockerTransitionGraceTicks = $latestPromotionBlockerTransitionGraceTicks
        LatestPromotionEncodeSoftSpikeCount = [Math]::Max(0, $latestPromotionEncodeSoftSpikeCount)
        LatestPromotionEncodeSoftSpikeResetSuppressedCount = [Math]::Max(0, $latestPromotionEncodeSoftSpikeResetSuppressedCount)
        PromotionBlockedByMissingHelperProofCount = $promotionBlockedByMissingHelperProofCount
        PromotionBlockedByStaleHelperProofCount = $promotionBlockedByStaleHelperProofCount
        PromotionBlockedByEncodeBudgetCount = $promotionBlockedByEncodeBudgetCount
        PromotionBlockedByEncodeBudgetAloneCount = $promotionBlockedByEncodeBudgetAloneCount
        HelperVisibleHeadRuntimeSenderMismatch = $helperVisibleHeadRuntimeSenderMismatch
        LatestHealthyTickResetReasonCounts = $latestHealthyTickResetReasonCounts
        LatestReducedPromotionRecentEntries = $latestReducedPromotionRecentEntries
        LatestHelperSessionId = $latestHelperSessionId
        LatestHelperRunId = $latestHelperRunId
        LatestHelperListenerGeneration = $latestHelperListenerGeneration
        LatestHelperUpstreamCaptureToFrameReadyAvgMs = $latestHelperUpstreamCaptureToFrameReadyAvgMs
        LatestHelperUpstreamCaptureToFrameReadyMedianMs = $latestHelperUpstreamCaptureToFrameReadyMedianMs
        LatestHelperUpstreamCaptureToFrameReadyP95Ms = $latestHelperUpstreamCaptureToFrameReadyP95Ms
        LatestHelperUpstreamCaptureToFrameReadyMaxMs = $latestHelperUpstreamCaptureToFrameReadyMaxMs
        LatestHelperUpstreamFrameReadyToViewerAcceptAvgMs = $latestHelperUpstreamFrameReadyToViewerAcceptAvgMs
        LatestHelperUpstreamFrameReadyToViewerAcceptMedianMs = $latestHelperUpstreamFrameReadyToViewerAcceptMedianMs
        LatestHelperUpstreamFrameReadyToViewerAcceptP95Ms = $latestHelperUpstreamFrameReadyToViewerAcceptP95Ms
        LatestHelperUpstreamFrameReadyToViewerAcceptMaxMs = $latestHelperUpstreamFrameReadyToViewerAcceptMaxMs
        LatestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs = $latestHelperUpstreamViewerAcceptToDecodeEnqueueAvgMs
        LatestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs = $latestHelperUpstreamViewerAcceptToDecodeEnqueueMedianMs
        LatestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms = $latestHelperUpstreamViewerAcceptToDecodeEnqueueP95Ms
        LatestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs = $latestHelperUpstreamViewerAcceptToDecodeEnqueueMaxMs
        LatestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs = $latestHelperUpstreamDecodeEnqueueToDecodeStartAvgMs
        LatestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs = $latestHelperUpstreamDecodeEnqueueToDecodeStartMedianMs
        LatestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms = $latestHelperUpstreamDecodeEnqueueToDecodeStartP95Ms
        LatestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs = $latestHelperUpstreamDecodeEnqueueToDecodeStartMaxMs
        LatestHelperUpstreamCaptureToDecodeStartAvgMs = $latestHelperUpstreamCaptureToDecodeStartAvgMs
        LatestHelperUpstreamCaptureToDecodeStartMedianMs = $latestHelperUpstreamCaptureToDecodeStartMedianMs
        LatestHelperUpstreamCaptureToDecodeStartP95Ms = $latestHelperUpstreamCaptureToDecodeStartP95Ms
        LatestHelperUpstreamCaptureToDecodeStartMaxMs = $latestHelperUpstreamCaptureToDecodeStartMaxMs
        LatestHelperUpstreamWorstEpochByCaptureToDecodeStart = $latestHelperUpstreamWorstEpochByCaptureToDecodeStart
        LatestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs = $latestHelperUpstreamWorstEpochCaptureToDecodeStartAvgMs
        LatestHelperDominantUpstreamLatencyStage = $latestHelperDominantUpstreamLatencyStage
        LatestHelperReadyPathCaptureToFirstFragmentObservedAvgMs = $latestHelperReadyPathCaptureToFirstFragmentObservedAvgMs
        LatestHelperReadyPathCaptureToFirstFragmentObservedMedianMs = $latestHelperReadyPathCaptureToFirstFragmentObservedMedianMs
        LatestHelperReadyPathCaptureToFirstFragmentObservedP95Ms = $latestHelperReadyPathCaptureToFirstFragmentObservedP95Ms
        LatestHelperReadyPathCaptureToFirstFragmentObservedMaxMs = $latestHelperReadyPathCaptureToFirstFragmentObservedMaxMs
        LatestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs = $latestHelperReadyPathFirstFragmentToLastFragmentObservedAvgMs
        LatestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs = $latestHelperReadyPathFirstFragmentToLastFragmentObservedMedianMs
        LatestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms = $latestHelperReadyPathFirstFragmentToLastFragmentObservedP95Ms
        LatestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs = $latestHelperReadyPathFirstFragmentToLastFragmentObservedMaxMs
        LatestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs = $latestHelperReadyPathLastFragmentToAssemblyCompleteAvgMs
        LatestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs = $latestHelperReadyPathLastFragmentToAssemblyCompleteMedianMs
        LatestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms = $latestHelperReadyPathLastFragmentToAssemblyCompleteP95Ms
        LatestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs = $latestHelperReadyPathLastFragmentToAssemblyCompleteMaxMs
        LatestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs = $latestHelperReadyPathAssemblyCompleteToFrameEmittedAvgMs
        LatestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs = $latestHelperReadyPathAssemblyCompleteToFrameEmittedMedianMs
        LatestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms = $latestHelperReadyPathAssemblyCompleteToFrameEmittedP95Ms
        LatestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs = $latestHelperReadyPathAssemblyCompleteToFrameEmittedMaxMs
        LatestHelperDominantReadyPathStage = $latestHelperDominantReadyPathStage
        LatestHelperReceivePathCaptureToEnvelopeSendAvgMs = $latestHelperReceivePathCaptureToEnvelopeSendAvgMs
        LatestHelperReceivePathCaptureToEnvelopeSendMedianMs = $latestHelperReceivePathCaptureToEnvelopeSendMedianMs
        LatestHelperReceivePathCaptureToEnvelopeSendP95Ms = $latestHelperReceivePathCaptureToEnvelopeSendP95Ms
        LatestHelperReceivePathCaptureToEnvelopeSendMaxMs = $latestHelperReceivePathCaptureToEnvelopeSendMaxMs
        LatestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs = $latestHelperReceivePathEnvelopeSendToBridgeIngressAvgMs
        LatestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs = $latestHelperReceivePathEnvelopeSendToBridgeIngressMedianMs
        LatestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms = $latestHelperReceivePathEnvelopeSendToBridgeIngressP95Ms
        LatestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs = $latestHelperReceivePathEnvelopeSendToBridgeIngressMaxMs
        LatestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs = $latestHelperReceivePathBridgeIngressToEnvelopeParsedAvgMs
        LatestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs = $latestHelperReceivePathBridgeIngressToEnvelopeParsedMedianMs
        LatestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms = $latestHelperReceivePathBridgeIngressToEnvelopeParsedP95Ms
        LatestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs = $latestHelperReceivePathBridgeIngressToEnvelopeParsedMaxMs
        LatestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs = $latestHelperReceivePathEnvelopeParsedToSecureDecryptAvgMs
        LatestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs = $latestHelperReceivePathEnvelopeParsedToSecureDecryptMedianMs
        LatestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms = $latestHelperReceivePathEnvelopeParsedToSecureDecryptP95Ms
        LatestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs = $latestHelperReceivePathEnvelopeParsedToSecureDecryptMaxMs
        LatestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs = $latestHelperReceivePathSecureDecryptToFragmentDeserializeAvgMs
        LatestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs = $latestHelperReceivePathSecureDecryptToFragmentDeserializeMedianMs
        LatestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms = $latestHelperReceivePathSecureDecryptToFragmentDeserializeP95Ms
        LatestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs = $latestHelperReceivePathSecureDecryptToFragmentDeserializeMaxMs
        LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs = $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedAvgMs
        LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs = $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMedianMs
        LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms = $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedP95Ms
        LatestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs = $latestHelperReceivePathFragmentDeserializeToFirstFragmentObservedMaxMs
        LatestHelperDominantReceivePathStage = $latestHelperDominantReceivePathStage
        LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs = $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedAvgMs
        LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs = $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMedianMs
        LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms = $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedP95Ms
        LatestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs = $latestHelperBridgeIngressEnvelopeSendToBridgeMessageObservedMaxMs
        LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs = $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedAvgMs
        LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs = $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMedianMs
        LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms = $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedP95Ms
        LatestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs = $latestHelperBridgeIngressBridgeMessageObservedToBinaryFrameDecodedMaxMs
        LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs = $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressAvgMs
        LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs = $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMedianMs
        LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms = $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressP95Ms
        LatestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs = $latestHelperBridgeIngressBinaryFrameDecodedToBridgeIngressMaxMs
        LatestHelperDominantBridgeIngressStage = $latestHelperDominantBridgeIngressStage
        LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs = $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredAvgMs
        LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs = $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMedianMs
        LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms = $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredP95Ms
        LatestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs = $latestHelperNknReceiveEnvelopeSendToSdkHandleMsgEnteredMaxMs
        LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs = $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchAvgMs
        LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs = $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMedianMs
        LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms = $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchP95Ms
        LatestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs = $latestHelperNknReceiveSdkHandleMsgEnteredToClientMessageDispatchMaxMs
        LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs = $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchAvgMs
        LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs = $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMedianMs
        LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms = $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchP95Ms
        LatestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs = $latestHelperNknReceiveClientMessageDispatchToMultiClientMessageDispatchMaxMs
        LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs = $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedAvgMs
        LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs = $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMedianMs
        LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms = $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedP95Ms
        LatestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs = $latestHelperNknReceiveMultiClientMessageDispatchToBridgeMessageObservedMaxMs
        LatestHelperDominantNknReceiveStage = $latestHelperDominantNknReceiveStage
        LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs = $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredAvgMs
        LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs = $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMedianMs
        LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms = $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredP95Ms
        LatestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs = $latestHelperWsReceiveEnvelopeSendToWsReceiverWriteEnteredMaxMs
        LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs = $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedAvgMs
        LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs = $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMedianMs
        LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms = $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedP95Ms
        LatestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs = $latestHelperWsReceiveWsReceiverWriteEnteredToWsMessageEmittedMaxMs
        LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs = $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredAvgMs
        LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs = $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMedianMs
        LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms = $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredP95Ms
        LatestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs = $latestHelperWsReceiveWsMessageEmittedToSdkHandleMsgEnteredMaxMs
        LatestHelperDominantWsReceiveStage = $latestHelperDominantWsReceiveStage
        LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs = $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedAvgMs
        LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs = $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMedianMs
        LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms = $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedP95Ms
        LatestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs = $latestHelperSocketReceiveEnvelopeSendToSocketDataEventEmittedMaxMs
        LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs = $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredAvgMs
        LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs = $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMedianMs
        LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms = $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredP95Ms
        LatestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs = $latestHelperSocketReceiveSocketDataEventEmittedToWsReceiverWriteEnteredMaxMs
        LatestHelperDominantSocketReceiveStage = $latestHelperDominantSocketReceiveStage
        LatestBridgeEventLoopP95Ms = $latestBridgeEventLoopP95Ms
        LatestBridgeEventLoopMaxMs = $latestBridgeEventLoopMaxMs
        LatestBridgeEventLoopMeanMs = $latestBridgeEventLoopMeanMs
        LatestBridgeEventLoopSampleWindowMs = $latestBridgeEventLoopSampleWindowMs
        LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs = $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueAvgMs
        LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs = $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMedianMs
        LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms = $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueP95Ms
        LatestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs = $latestBridgeMediaSendBinarySendFrameObservedToQueueEnqueueMaxMs
        LatestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs = $latestBridgeMediaSendQueueEnqueueToQueueDequeueAvgMs
        LatestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs = $latestBridgeMediaSendQueueEnqueueToQueueDequeueMedianMs
        LatestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms = $latestBridgeMediaSendQueueEnqueueToQueueDequeueP95Ms
        LatestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs = $latestBridgeMediaSendQueueEnqueueToQueueDequeueMaxMs
        LatestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs = $latestBridgeMediaSendQueueDequeueToMediaSendStartedAvgMs
        LatestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs = $latestBridgeMediaSendQueueDequeueToMediaSendStartedMedianMs
        LatestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms = $latestBridgeMediaSendQueueDequeueToMediaSendStartedP95Ms
        LatestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs = $latestBridgeMediaSendQueueDequeueToMediaSendStartedMaxMs
        LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs = $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedAvgMs
        LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs = $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMedianMs
        LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms = $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedP95Ms
        LatestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs = $latestBridgeMediaSendMediaSendStartedToMediaSendResolvedMaxMs
        LatestBridgeMediaSendFramesSent = $latestBridgeMediaSendFramesSent
        LatestBridgeMediaSendFailures = $latestBridgeMediaSendFailures
        LatestBridgeMediaSendQueueDrops = $latestBridgeMediaSendQueueDrops
        LatestBridgeMediaSendQueueMode = $latestBridgeMediaSendQueueMode
        LatestBridgeMediaSendQueueDepth = $latestBridgeMediaSendQueueDepth
        LatestBridgeMediaSendOldestQueuedAgeMs = $latestBridgeMediaSendOldestQueuedAgeMs
        LatestBridgeMediaSendSampleWindowMs = $latestBridgeMediaSendSampleWindowMs
        LatestBridgeTransportHealthSelectedRpc = $latestBridgeTransportHealthSelectedRpc
        LatestBridgeTransportHealthSelectedRpcKey = $latestBridgeTransportHealthSelectedRpcKey
        LatestBridgeTransportHealthSelectedRpcStage = $latestBridgeTransportHealthSelectedRpcStage
        LatestBridgeTransportHealthConnectId = $latestBridgeTransportHealthConnectId
        LatestBridgeTransportHealthConnectKey = $latestBridgeTransportHealthConnectKey
        LatestBridgeTransportHealthReadyEmitted = $latestBridgeTransportHealthReadyEmitted
        LatestBridgeTransportHealthClientReadyAgeMs = $latestBridgeTransportHealthClientReadyAgeMs
        LatestBridgeTransportHealthDisconnectCountSinceLast = $latestBridgeTransportHealthDisconnectCountSinceLast
        LatestBridgeTransportHealthConnectFailedCountSinceLast = $latestBridgeTransportHealthConnectFailedCountSinceLast
        LatestBridgeTransportHealthWsErrorCountSinceLast = $latestBridgeTransportHealthWsErrorCountSinceLast
        LatestBridgeTransportHealthRpcFallbackAttemptCountSinceLast = $latestBridgeTransportHealthRpcFallbackAttemptCountSinceLast
        LatestBridgeTransportHealthControlReady = $latestBridgeTransportHealthControlReady
        LatestBridgeTransportHealthMediaReady = $latestBridgeTransportHealthMediaReady
        LatestBridgeTransportHealthBulkReady = $latestBridgeTransportHealthBulkReady
        LatestBridgeTransportHealthFramesSentSinceLast = $latestBridgeTransportHealthFramesSentSinceLast
        LatestBridgeTransportHealthLatestDisconnectReason = $latestBridgeTransportHealthLatestDisconnectReason
        LatestBridgeTransportHealthSampleWindowMs = $latestBridgeTransportHealthSampleWindowMs
        LatestBridgeTransportHealthUniqueSelectedRpcCount = $latestBridgeTransportHealthUniqueSelectedRpcCount
        HelperQualitySummaryLines = @($helperQualitySummaryLines.ToArray())
        HelperUpstreamLatencySummaryLines = @($helperUpstreamLatencySummaryLines.ToArray())
        HelperReadyPathSummaryLines = @($helperReadyPathSummaryLines.ToArray())
        HelperReceivePathSummaryLines = @($helperReceivePathSummaryLines.ToArray())
        HelperBridgeIngressSummaryLines = @($helperBridgeIngressSummaryLines.ToArray())
        HelperNknReceiveSummaryLines = @($helperNknReceiveSummaryLines.ToArray())
        HelperWsReceiveSummaryLines = @($helperWsReceiveSummaryLines.ToArray())
        HelperSocketReceiveSummaryLines = @($helperSocketReceiveSummaryLines.ToArray())
        BridgeEventLoopSummaryLines = @($bridgeEventLoopSummaryLines.ToArray())
        BridgeMediaSendSummaryLines = @($bridgeMediaSendSummaryLines.ToArray())
        BridgeTransportHealthSummaryLines = @($bridgeTransportHealthSummaryLines.ToArray())
        HelperEpochLossLines = @($helperEpochLossLines.ToArray())
        HelperEpochTimelineLines = @($helperEpochTimelineLines.ToArray())
        HelperReassemblerRootCauseSummaryLines = @($helperReassemblerRootCauseSummaryLines.ToArray())
        HelperRecoveryEpochInvestigationLines = @($helperRecoveryEpochInvestigationLines.ToArray())
        HelperReassemblerRecoveryOwnerTransitionLines = @($helperReassemblerRecoveryOwnerTransitionLines.ToArray())
        HelperReassemblerActionableLateFragmentLines = @($helperReassemblerActionableLateFragmentLines.ToArray())
        HelperReassemblerOlderEpochCleanupLines = @($helperReassemblerOlderEpochCleanupLines.ToArray())
        HelperPressureSummaryLines = @($helperPressureSummaryLines.ToArray())
        HealthSnapshotLines = @($healthSnapshotLines.ToArray())
        ReducedPromotionSummaryLines = @($reducedPromotionSummaryLines.ToArray())
        LogPath = $logPath
    }
}
