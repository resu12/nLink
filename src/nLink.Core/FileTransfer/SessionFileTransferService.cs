using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using NLink.Core.Configuration;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService : IDisposable
{
    private readonly record struct FallbackControlPlaneExchangeOwnerSnapshot(
        string SessionId,
        string TransferId,
        FileTransferDirection Direction,
        string RouteToken,
        int ProtocolVersion,
        int LiveRouteEpoch,
        int LegGeneration,
        int BridgeRecoveryGeneration,
        long TransportEpoch,
        string? CheckpointRequestId);

    private const string BusyReason = FileTransferResultCodes.Busy;
    private const string DeclinedReason = FileTransferResultCodes.Declined;
    private const string CanceledReason = FileTransferResultCodes.CanceledLocal;
    private const string DisconnectedErrorCode = FileTransferResultCodes.PeerDisconnected;
    private const string ControlChannelStalledErrorCode = FileTransferResultCodes.ControlChannelStalled;
    private const string DetachedErrorCode = FileTransferResultCodes.TransportDetached;
    private const string InvalidStateErrorCode = FileTransferResultCodes.InvalidState;
    private const string SessionMismatchErrorCode = FileTransferResultCodes.SessionMismatch;
    private const string FileSizeMismatchErrorCode = FileTransferResultCodes.SizeMismatch;
    private const string HashMismatchErrorCode = FileTransferResultCodes.IntegrityMismatch;
    private const string StreamOpenFailedErrorCode = FileTransferResultCodes.WriteOpenFailed;
    private const string StreamReadFailedErrorCode = FileTransferResultCodes.ReadFailed;
    private const string TransportIncompatibleErrorCode = FileTransferResultCodes.TransportIncompatible;
    private const string WindowTimeoutErrorCode = FileTransferResultCodes.WindowTimeout;
    private const string PayloadBudgetExceededErrorCode = FileTransferResultCodes.PayloadBudgetExceeded;
    private const string StreamWriteFailedErrorCode = FileTransferResultCodes.WriteFailed;
    private const string FinalizeFailedErrorCode = FileTransferResultCodes.FinalizeFailed;
    private const string PullSessionStalledErrorCode = FileTransferResultCodes.PullSessionStalled;
    private const string ReceiverBufferExhaustedErrorCode = FileTransferResultCodes.ReceiverBufferExhausted;
    private const string ReceiverFeedbackQueueExhaustedErrorCode = FileTransferResultCodes.ReceiverFeedbackQueueExhausted;
    private const string SenderCacheExhaustedErrorCode = FileTransferResultCodes.SenderCacheExhausted;
    private const string SenderRepairUnavailableErrorCode = FileTransferResultCodes.SenderRepairUnavailable;
    private const string V4FileOnlyRequiredErrorCode = FileTransferResultCodes.V4FileOnlyRequired;
    private const string V4SparseDestinationRequiredErrorCode = FileTransferResultCodes.V4SparseDestinationRequired;
    private const int WindowUpdateWatchdogDelayMs = 500;
    private const int MissingRangeWatchdogDelayMs = 100;
    private const int MissingRangeTriggerDelayMs = 100;
    private const int MissingRangeCooldownMs = 250;
    private const int RepeatedMissingRangeMinIntervalMs = 500;
    private const int MissingRangeMaxChunks = 8;
    private const int RepairResendIntervalMs = 150;
    private const int RepairAckTimeoutMs = 750;
    private const int LifecyclePrioritySendTimeoutMs = 1000;
    private const int OfferSendTimeoutMs = 5000;
    private static readonly int[] OfferRetryDelaysMs = [1000, 3000, 7000, 15000, 30000];
    private const int OfferRetrySteadyStateDelayMs = 30000;
    private const int V6HeartbeatIntervalMs = 5000;
    private const int V6PeerLivenessTimeoutMs = 20000;
    private const int V6RegularNknPeerLivenessRepairGraceMultiplier = 6;
    private const int RepairBatchSize = 2;
    private const int DegradedRepairGrantChunks = 32;
    private const int DegradedRepairLowWatermarkChunks = 8;
    private const int DegradedRepairGapPersistenceMs = 500;
    private const int DegradedRepairMissingRangeBurstThreshold = 3;
    private const int DegradedRepairMissingRangeBurstWindowMs = 1000;
    private const int DegradedRepairRecoveryHoldMs = 1000;
    private const int BulkFallbackGrantChunks = 8;
    private const int BulkFallbackLowWatermarkChunks = 2;
    private const int BulkUnhealthyDetectionWindowMs = 1000;
    private const int BulkUnhealthyDetectionConfirmations = 2;
    private const int BulkFallbackRecoveryHoldMs = 2000;
    private const int PressureStateRecoveryHoldMs = 2000;
    private const int PressureStateNormalSuggestedSendAheadChunks = 32;
    private const int PressureStateCatchUpOnlySuggestedSendAheadChunks = 2;
    private const int PressureStateCatchUpOnlySuggestedSendAheadWhileScreenshareChunks = 1;
    private const int PressureStateObsoleteChunkThreshold = 4;
    private const double PressureStateObsoleteChunkRatioThreshold = 0.25;
    private const int PressureStateBulkDispatchThreshold = 4;
    private const int PressureStateNoProgressWindowMs = 1500;
    private const int PressuredWatchdogResendMinIntervalMs = 1000;
    private const int RepairChurnDetectionWindowMs = 2000;
    private const int RepairChurnMissingRangeThreshold = 3;
    private const int RepairChurnMaxHealthyProgressChunks = 6;
    private const int LocalSendAheadClampChunks = 32;
    private const int LocalSendAheadClampDegradedChunks = 8;
    private const int LocalSendAheadClampDegradedWhileScreenshareChunks = 4;
    private const int ScreenShareActiveStartupGrantCapChunks = 32;
    private const int PullHealthyPipelineDepth = 6;
    private const int PullHealthyMinimumPipelineDepth = 2;
    private const int PullHealthyLowWatermarkChunks = 1;
    private const int PullHealthyTargetInFlightBytes = 144 * 1024;
    private const int PullHealthyLowWatermarkBytes = 24 * 1024;
    private const int PullHealthyMaximumPipelineDepthCap = 6;
    private const int PullScreensharePipelineDepth = 3;
    private const int PullScreenshareLowWatermarkChunks = 2;
    private const int PullDegradedPipelineDepth = 4;
    private const int PullDegradedLowWatermarkChunks = 1;
    private const int PullDegradedScreensharePipelineDepth = 1;
    private const int PullDegradedScreenshareLowWatermarkChunks = 0;
    private const int PullHealthyDefaultChunkSizeBytes = 24576;
    private const int PullScreenshareDefaultChunkSizeBytes = 24576;
    private const int PullDegradedDefaultChunkSizeBytes = 8192;
    private const int PullDegradedScreenshareDefaultChunkSizeBytes = 2048;
    private const int PullSessionHealthyRequestTimeoutMs = 3000;
    private const int PullSessionDegradedRequestTimeoutMs = 5000;
    private const int PullSessionHealthyRetryResendGateMs = 1000;
    private const int PullSessionDegradedRetryResendGateMs = 1500;
    private const int PullSessionReceivePollDelayMs = 250;
    private const int PullSessionTransportRecoveryGraceMs = 15000;
    private const int TunaActivationSendCancellationRecoveryWindowMs = 10000;
    private const int PullSessionRecoveryHoldMs = 5000;
    private const int PullHealthyReorderStepDownHoldMs = 1500;
    private const int PullSessionFirstChunkStallTimeouts = 3;
    private const int PullSessionDegradedEntryTimeoutStreakThreshold = 2;
    private const int PullSessionHealthyAckCoalesceDelayMs = 1000;
    private const int PullSessionScreenshareAckCoalesceDelayMs = 250;
    private const int PullSessionDegradedAckCoalesceDelayMs = 500;
    private const int PullControlChatterWindowMs = 2000;
    private const int PullLateArrivalDistanceThreshold = 4;
    private const int PullGapFocusBufferedThreshold = 3;
    private const int PullTimeoutOutstandingStepDownThreshold = 4;
    private const int PullProfileAdjustmentCooldownMs = 500;
    private const int PullHealthyBundledRawBytesCap = FileTransferChunkBudget.MaxRawChunkBytes;
    private const int PullV4HealthyTargetInFlightBytes = 2 * 1024 * 1024;
    private const int PullV4HealthyMaximumTargetInFlightBytes = 4 * 1024 * 1024;
    private const int PullV4HealthyFileOnlySoftLimitedTargetInFlightBytesDefault = 4 * 1024 * 1024;
    private const int PullV4HealthyFileOnlySoftLimitedTargetInFlightBytesMin = 512 * 1024;
    private const int PullV4HealthyFileOnlySoftLimitedTargetInFlightBytesMax = 8 * 1024 * 1024;
    private const int PullV4HealthyLimitedTargetInFlightBytes = 512 * 1024;
    private const int PullV4ScreenshareTargetInFlightBytes = 256 * 1024;
    private const int PullV4DegradedTargetInFlightBytes = 256 * 1024;
    private const int PullV4ConservativeStartupTargetInFlightBytes = 512 * 1024;
    private const int PullV4ConservativeStartupProbeTargetInFlightBytes = 1024 * 1024;
    private const int PullV4ConservativeStartupDegradedTargetInFlightBytes = 256 * 1024;
    private const int PullV4HealthyAckThresholdBytes = 256 * 1024;
    private const int PullV4HealthyAckCoalesceDelayMs = 150;
    private const int PullV4FileOnlySparseAckCoalesceDelayMs = 75;
    private const int PullV4FileOnlySparseTargetInFlightBytesDefault = 16 * 1024 * 1024;
    private const int PullV4FileOnlySparseTargetInFlightBytesMin = 1024 * 1024;
    private const int PullV4FileOnlySparseTargetInFlightBytesMax = 16 * 1024 * 1024;
    private const int PullV4FileOnlySparseGrantLowWatermarkPercentDefault = 90;
    private const int PullV4FileOnlySparseGrantLowWatermarkPercentMin = 50;
    private const int PullV4FileOnlySparseGrantLowWatermarkPercentMax = 99;
    private const int PullV4FileOnlySparseGrantCoalesceMsDefault = 25;
    private const int PullV4FileOnlySparseGrantCoalesceMsMin = 10;
    private const int PullV4FileOnlySparseGrantCoalesceMsMax = 250;
    private const int PullV4SparseAheadGrantMaxBytesCurrentDefault = 4 * 1024 * 1024;
    private const int PullV4SparseAheadGrantMaxBytesDominantDefault = 8 * 1024 * 1024;
    private const int PullV4SparseAheadGrantMaxBytesMax = 8 * 1024 * 1024;
    private const int PullV4RepairRequestChunkCount = 4;
    private const int PullV4ProactiveFrontierRepairMinGapAgeMsDefault = 500;
    private const int PullV4ProactiveFrontierRepairMinGapAgeMsMin = 100;
    private const int PullV4ProactiveFrontierRepairMinGapAgeMsMax = 2500;
    private const int PullV4ProactiveFrontierRepairRepeatMinIntervalMsDefault = 500;
    private const int PullV4ProactiveFrontierRepairRepeatMinIntervalMsMin = 100;
    private const int PullV4ProactiveFrontierRepairRepeatMinIntervalMsMax = 2500;
    private const int PullV4ProactiveFrontierRepairChunkCountDefault = 32;
    private const int PullV4ProactiveFrontierRepairChunkCountMin = 4;
    private const int PullV4ProactiveFrontierRepairChunkCountMax = 64;
    private const int PullV4ProactiveFrontierRepairMinLateDistance = 8;
    private const int PullV4ProactiveRepairGraceMsDefault = 2500;
    private const int PullV4ProactiveRepairGraceMsMin = 500;
    private const int PullV4ProactiveRepairGraceMsMax = 5000;
    private const int PullV4RepairSetScanHorizonChunks = 256;
    private const int PullV4RepairSetRepeatMinIntervalMs = 1000;
    private static readonly int[] PullTransportRebindRetryDelaysMs = [500, 1500, 3500, 7000, 12000];
    private const int PullTransportRebindSafetyReplayMaxChunks = 64;
    private const int PullTransportRebindSafetyReplayMaxBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan PullTransportRebindSafetyReplayRearmCooldown = TimeSpan.FromSeconds(5);
    private const int PullTransportRebindFrontierOnlyStableAdvanceChunks = 64;
    private static readonly TimeSpan V6TransportHandoffWaitingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan V6TransportEpochProofTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan V6TransportProbeAckSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PullV4PostFallbackPeerSilenceTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PullV4PeerSilenceTimeout = TimeSpan.FromSeconds(90);
    private static readonly int[] CancelRetryDelaysMs = [0, 250, 750, 1500, 3000, 7000, 12000, 20000, 30000];
    private static readonly int[] CancelDataFrameRetryDelaysMs = [250, 750, 1500, 3000, 7000, 12000];
    private static readonly int[] PauseControlRetryDelaysMs = [0, 250, 500, 750, 1000, 1500, 2000, 3000, 4000, 5000, 6000];
    private const int CancelDataFrameBestEffortTimeoutMs = 750;
    private const int PullV4GrantLowWatermarkDivisor = 2;
    private const int PullV4HealthyDefaultChunkSizeBytes = 40 * 1024;
    private const int PullV4ConservativeStartupChunkSizeBytes = 24 * 1024;
    private const int PullV4ScreenshareDefaultChunkSizeBytes = 24 * 1024;
    private const int PullV4DegradedDefaultChunkSizeBytes = 20 * 1024;
    private const int PullV4ConservativeStartupInitialPipelineDepth = 4;
    private const int PullV4ConservativeStartupProbeProgressBytesThreshold = 512 * 1024;
    private const int PullV4ConservativeStartupProbeHoldMs = 500;
    private const int PullV4ConservativeStartupStepUpProgressBytesThreshold = 1024 * 1024;
    private const int PullV4ConservativeStartupStepUpHoldMs = 500;
    private const int PullV4ProfileAdjustmentCooldownMs = 1500;
    private const int PullV4StepUpProgressBytesThreshold = 2 * 1024 * 1024;
    private const int PullV4HealthyStepUpHoldMs = 1000;
    private const int PullV4AdverseStepDownHoldMs = 2000;
    private const int PullV4HighReorderDistanceThreshold = 8;
    private const int PullV4PressureStateSuppressionMs = 1000;
    private const int PullV4PressureStateHealthyProgressDeltaChunks = 96;
    private const int PullV4PressureStateFileOnlySparseProgressDeltaChunks = 48;
    private const int PullV4PressureStateBalancedProgressDeltaChunks = 48;
    private const int PullV4PressureStateDegradedProgressDeltaChunks = 16;
    private const int PullV4BatchMaxChunks = 4;
    private const int PullV4LimitedReorderDistanceThreshold = 16;
    private const int PullV4LimitedStepDownHoldMs = 1000;
    private const int PullV4LimitedRecoveryHoldMs = 4000;
    private const int PullV4FileOnlySparseLimitedRecoveryHoldMsDefault = 750;
    private const int PullV4FileOnlySparseLimitedRecoveryHoldMsMin = 250;
    private const int PullV4FileOnlySparseLimitedRecoveryHoldMsMax = 4000;
    private const int PullV4FileOnlySparseSoftLimitedRecoveryHoldMsDefault = 500;
    private const int PullV4FileOnlySparseSoftLimitedRecoveryHoldMsMin = 100;
    private const int PullV4FileOnlySparseSoftLimitedRecoveryHoldMsMax = 4000;
    private const int PullV4FileOnlySparseToleratedReorderThreshold = 64;
    private const int PullV4FileOnlySparseSoftLimitedReorderThresholdDefault = 512;
    private const int PullV4RegularNknControlFeedbackPressureMinCreditExhaustedMs = 30_000;
    private const int PullV4RegularNknControlFeedbackPressureMinFrontierLagChunks = 64;
    private const int PullV4FileOnlySparseSoftLimitedReorderThresholdMin = 64;
    private const int PullV4FileOnlySparseSoftLimitedReorderThresholdMax = 2048;
    private const int PullV4FileOnlySparseSoftGapStallMsDefault = 1500;
    private const int PullV4FileOnlySparseSoftGapStallMsMin = 750;
    private const int PullV4FileOnlySparseSoftGapStallMsMax = 2500;
    private const int PullV4FileOnlySparseLimitedGapStallMs = 2500;
    private const int PullV4SparseAheadGrantGapStallLimitMsDefault = 2500;
    private const int PullV4SparseAheadGrantGapStallLimitMsMin = 0;
    private const int PullV4SparseAheadGrantGapStallLimitMsMax = 2500;
    private const int PullV4SparseCreditTopupBytesCurrentDefault = 256 * 1024;
    private const int PullV4SparseCreditTopupBytesDominantDefault = 128 * 1024;
    private const int PullV4SparseCreditTopupBytesMin = 0;
    private const int PullV4SparseCreditTopupBytesMax = 2 * 1024 * 1024;
    private const int PullV4SparseCreditHoldMsDefault = 1500;
    private const int PullV4SparseCreditHoldMsMin = 0;
    private const int PullV4SparseCreditHoldMsMax = 4000;
    private const int PullV4FixedFileOnlyWindowBytesDefault = 0;
    private const int PullV4FixedFileOnlyWindowBytesMin = 0;
    private const int PullV4FixedFileOnlyWindowBytesMax = 64 * 1024 * 1024;
    private const int V4DefaultChunkSizeBytes = 21 * 1024;
    private const int V4FileOnlySparseCreditWindowBytes = 64 * 1024 * 1024;
    private const int V6RegularNknBulkSparseCreditWindowBytes = V4FileOnlySparseCreditWindowBytes;
    private const int V4SenderPumpDepth = 8;
    private const int V4SenderPumpPendingBytes = 2 * 1024 * 1024;
    private const int V4RepairRepeatIntervalMs = 750;
    private const int V4FileOnlyFrontierRepairRepeatIntervalMs = 250;
    private const int V4RegularNknFileOnlyFrontierRepairRepeatIntervalMs = 750;
    private const int V6RegularNknSparseRuntimeFrontierRepairRepeatIntervalMs = V4FileOnlyFrontierRepairRepeatIntervalMs;
    private const int V6RegularNknSparseRuntimeSenderFrontierRepairRepeatIntervalMs = V4RepairRepeatIntervalMs;
    private const int V4RepairRequestHistoryRetentionMs = 10000;
    private const int V4RepairRedundancyEscalationStallMs = 2000;
    private const int V4FileOnlyFirstRepairCreditStallEscalationMs = 1000;
    private const int V4RepairBurstMaxChunks = 64;
    private const int V6RegularNknSparseRuntimeRepairBurstMaxChunks = V4RepairBurstMaxChunks;
    private const int V4RepairBatchSendAttempts = 1;
    private const int V4MaxBatchSegmentsDefault = 3;
    private const int V4MaxBatchSegmentsMin = 1;
    private const int V4MaxBatchSegmentsMax = 3;
    private const int V4MixedScreenShareNormalBatchSegments = 2;
    private const int V4MixedScreenShareDegradedBatchSegments = 2;
    private const int V4MixedScreenShareCreditWindowChunks = 96;
    private const int V4MixedScreenShareDegradedCreditWindowChunks = 24;
    private const int V4NormalSendQuantumChunks = 24;
    private const int V4StateCreditGrantQuantumBytes = 1 * 1024 * 1024;
    private const int V4StateProgressCreditMinChunks = 48;
    private const int V4StateProgressMaxDelayMs = 250;
    private const int V4TerminalReadyStateBestEffortTimeoutMs = 250;
    private const int V4KnownFrontierRepairChunks = V4MaxBatchSegmentsDefault;
    private const int V4FileOnlyInitialFrontierRepairChunks = 12;
    private const int V4MixedInitialFrontierRepairChunks = 12;
    private const string V6RegularNknFrontierRepairTransactionRequestPrefix = "regular-nkn-frontier:";
    private const string V6RegularNknFrontierRepairTransactionPriority = "frontier";
    private const string V6RegularNknFrontierRepairTransactionRecoveryMode = "regular_nkn_frontier_stall_control_bulk";
    private const int V4PostFallbackEmergencyFrontierRepairChunks = 1;
    private const int V4PostFallbackFrontierBackfillStep1Chunks = 3;
    private const int V4PostFallbackFrontierBackfillStep2Chunks = 12;
    private const int V4PostFallbackFrontierBackfillStep3Chunks = 32;
    private const int V4PostFallbackFrontierBackfillStep1AfterCommittedChunks = 1;
    private const int V4PostFallbackFrontierBackfillStep2AfterCommittedChunks = 4;
    private const int V4PostFallbackFrontierBackfillStep3AfterCommittedChunks = 8;
    private const int V4FrontierTailRetryChunks = V4MaxBatchSegmentsDefault;
    private const int V4FileOnlyFrontierTailRetryChunks = 12;
    private const int V6ReceiverRequestWindowChunks = 1536;
    private const int V6RegularNknReceiverRequestWindowChunks = 512;
    private const int V6RecoveredRegularNknReceiverRequestWindowChunks = 512;
    private const int V6RecoveredRegularNknFrontierStalledReceiverRequestWindowChunks = 384;
    private const int V6FrontierStalledReceiverRequestWindowChunks = 256;
    private const int V6FrontierRequestChunks = 12;
    private const int V6PostTunaFallbackFrontierRescueExactChunks = V4PostFallbackEmergencyFrontierRepairChunks;
    private const int V6PostTunaFallbackFrontierRescueStep1Chunks = V4PostFallbackFrontierBackfillStep1Chunks;
    private const int V6PostTunaFallbackFrontierRescueStep2Chunks = V4PostFallbackFrontierBackfillStep2Chunks;
    private const int V6PostTunaFallbackFrontierRescueStep3Chunks = V4PostFallbackFrontierBackfillStep3Chunks;
    private const int V6PostTunaFallbackSenderSelfRepairChunks = V4PostFallbackFrontierBackfillStep3Chunks;
    private const int V6PostTunaFallbackFrontierRescueRequestsBeforeWiden = 2;
    private const int V6PostTunaFallbackNormalSendAheadFreezeChunks = V4PostFallbackFrontierBackfillStep3Chunks;
    private const int V6EpochFrontierRequestChunks = 1;
    private const int V6SparseSeekableRollingAheadChunks = 2048;
    private const int V6SparseSeekableRequestBudgetChunks = 1536;
    private const int V6SparseSeekableFrontierStalledRollingAheadChunks = 256;
    private const int V6SparseSeekableFrontierStalledRequestBudgetChunks = 256;
    private const int V6RegularNknNormalSendAheadLimitChunks = 512;
    private const int V6RegularNknNormalRefillLowWatermarkChunks = 256;
    private const int V6RegularNknNearFrontierNormalResendBypassChunks = 24;
    private const int V6RegularNknFrontierRepairBurstChunks = 12;
    private const int V6RegularNknFrontierRepairScanHorizonChunks = 768;
    private const int V6RegularNknFrontierPressureNormalSendAheadLimitChunks = 128;
    private const int V6RegularNknFrontierPressureNormalRefillLowWatermarkChunks = 96;
    private const int V6RegularNknFrontierPressureReleaseAdvanceChunks = 512;
    private const int V6RegularNknDegradedNormalSendAheadLimitChunks = 512;
    private const int V6RegularNknDegradedNormalRefillLowWatermarkChunks = 384;
    private const int V6RegularNknDegradedReleaseAdvanceChunks = 256;
    private const int V6RegularNknDegradedNoProgressReceiverStateThreshold = 4;
    private const int V6RegularNknDegradedNoProgressGraceMs = 3500;
    private const int V6TunaNormalSendAheadLimitChunks = 1536;
    private const int V6FrontierStalledPriorityBurstChunks = 16;
    private const int V6RegularNknInferredFrontierRepairBurstChunks = 4;
    private const int V6RegularNknInferredFrontierRepairStallMs = 5000;
    private const int V6RegularNknInferredFrontierRepairCooldownMs = 5000;
    private const int V6RecoveredRegularNknFrontierPriorityBurstChunks = 24;
    private const int V6NormalReceiverStateResendGateMs = 3500;
    // Regular NKN can acknowledge send success well before the peer receives a
    // chunk. Keep normal-window resends behind the send timeout; explicit
    // frontier repair still handles true holes quickly.
    private const int V6RegularNknNormalReceiverStateResendGateMs = 16000;
    private const int V6RegularNknFrontierStallObservationMs = 1000;
    private const int V6RecoveredFrontierResendGateMs = 1500;
    private const int V6EpochFrontierResendGateMs = 1500;
    private const int V6TunaRedundantDataProbeDelayMs = 10000;
    private const int V6SenderRequestFeedbackStallRecoveryMs = 12000;
    private const int V6SenderRequestFeedbackStallRecoveryCooldownMs = 15000;
    private const int V6SenderRequestFeedbackStallRecoverySuppressedLogIntervalMs = 5000;
    private const int V6SenderFeedbackStaleNormalBacklogChunks = 256;
    private const int V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryMs = 30000;
    private const int V6RegularNknSparseRuntimeStateRefreshCooldownMs = 5000;
    private const int V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs = 7500;
    private const int V6PostTunaFallbackControlPlanePeerCopyTimeoutMs = 2500;
    private const int V6RegularNknCheckpointSyncFailuresBeforeBridgeRecovery = 2;
    private const int V6PostTunaFallbackStaleInflightRecoveryMinStateRefreshFailures = 3;
    private const int V6PostTunaFallbackStaleCreditRecoveryMinStateRefreshFailures = 3;
    private const int V6PostTunaFallbackTailReconciliationMinStateRefreshAttempts = 3;
    private const int V6PostTunaFallbackCheckpointSendFailurePreserveLimit = 3;
    private const int V6PostTunaFallbackTailCheckpointSendFailurePreserveLimit = 3;
    private const int V6PostTunaFallbackLiveCheckpointTailStallThreshold = 3;
    private const int V6PostTunaFallbackLiveCheckpointTailGapChunks = 128;
    private const int V6PostTunaFallbackTailQuiescenceRepeatLimit = 3;
    private const string V6RegularNknStateRefreshRecoveryMode = "regular_nkn_state_refresh";
    private const string V6RegularNknStateRefreshPriority = "state_refresh";
    private const string V6RegularNknStateRefreshStaleInflightPriority = "state_refresh_stale_inflight";
    private const string V6RegularNknStateRefreshStaleCreditPriority = "state_refresh_stale_credit";
    private const string V6RegularNknStateRefreshTailReconciliationPriority = "state_refresh_tail_reconciliation";
    private const string V6RegularNknCheckpointSyncRecoveryMode = "regular_nkn_checkpoint_sync";
    private const string V6RegularNknCheckpointSyncPriority = "checkpoint_sync";
    private const string V6RegularNknCheckpointSyncRequestPrefix = "v6-regular-nkn-checkpoint-sync:";
    private const long V6TunaRedundantDataMinimumBytesAfterProof = 10L * 1024L * 1024L;
    private const int V6FileOnlySenderPipelineDepth = 24;
    private const int V6RegularNknSenderPipelineDepth = 4;
    private const int V6RegularNknRedundantSenderPipelineDepth = 2;
    private const int V6RegularNknRedundantNormalBatchLimit = 2;
    private const int V6RegularNknFallbackSenderPipelineDepth = 10;
    private const int V6EpochPriorityPipelineBypassDepth = 2;
    private const int V6SenderTransportSendTimeoutMs = 5000;
    private const int V6RegularNknTransportSendTimeoutMs = 7500;
    private const int V6RegularNknRedundantTransportSendTimeoutMs = 15000;
    private const int V6RegularNknSparseRuntimeV4TransportSendTimeoutMs = V6RegularNknTransportSendTimeoutMs;
    private const int V6ReceiverStateRetryIntervalMs = 750;
    private const int V6FrontierRequestRetryIntervalMs = 500;
    private const int V6RegularNknFrontierRequestRetryIntervalMs = 750;
    private const int V6FrontierRequestStallGraceMs = 750;
    private const int V6RegularNknFrontierRequestStallGraceMs = 750;
    private const int V6RegularNknFrontierRequestProgressGraceMs = 1000;
    private const int V6RegularNknFrontierControlBulkEscalationMs = V6RegularNknFrontierRequestStallGraceMs;
    private const int V6ReceiverStateProgressMinCommittedChunks = 16;
    private const int V6ReceiverStateProgressMaxIntervalMs = 500;
    private const int V6ReceiverStateDeferredDiagnosticCoalesceMs = 10000;
    private const string V4MaxBatchSegmentsEnvironmentVariableName = "NLINK_FILETRANSFER_V4_MAX_BATCH_SEGMENTS";
    private const string V4MixedScreenShareEnvironmentVariableName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
    private const string V4FileOnlyFastRepairEnvironmentVariableName = "NLINK_FILETRANSFER_V4_FILE_ONLY_FAST_REPAIR";
    private const long ReceiverBufferSoftLimitBytes = 8L * 1024L * 1024L;
    private const long ReceiverBufferSevereLimitBytes = 16L * 1024L * 1024L;
    private const long ReceiverBufferEmergencyLimitBytes = 64L * 1024L * 1024L;
    private const long ReceiverBufferExitLimitBytes = 4L * 1024L * 1024L;
    private const int ReceiverWriteBatchMaxBytes = 1024 * 1024;
    private const int ReceiverWriteBatchMaxChunks = 64;
    private const long SenderRepairCacheSeekableTargetBytes = 8L * 1024L * 1024L;
    private const long SenderRepairCacheSeekableHardLimitBytes = 16L * 1024L * 1024L;
    private const long SenderRepairCacheNonSeekableHardLimitBytes = 64L * 1024L * 1024L;
    private const int SenderRepairCachePressureWarnMinIntervalMs = 10000;
    private const int SenderRepairCachePressureWarnMinAcceptedChunkDelta = 512;
    private const int InboundMetadataTimeoutMs = 30000;
    private static readonly TimeSpan OutboundWindowTimeout = TimeSpan.FromSeconds(15);

    private readonly object gate = new();
    private readonly object inboundLifecycleDispatchGate = new();
    private readonly object inboundControlDispatchGate = new();
    private readonly object inboundChunkDispatchGate = new();
    private readonly Func<string> transferIdFactory;
    private readonly InboundTransferController inboundController;
    private IFileTransferSignalingTransport? transport;
    private ISignalingTransport? transportLifecycle;
    private OutboundTransferContext? outboundTransfer;
    private InboundTransferContext? inboundTransfer;
    private Task inboundLifecycleTail = Task.CompletedTask;
    private Task inboundControlTail = Task.CompletedTask;
    private Task inboundChunkTail = Task.CompletedTask;
    private FileTransferFlowControlPolicy flowControlPolicy = FileTransferFlowControlPolicy.ForMode(FileTransferFlowControlMode.Background);
    private bool sessionScreenShareActive;
    private bool sessionScreenShareDegraded;
    private bool sessionScreenShareObserved;
    private bool disposed;

    internal Func<string, string, Task>? InboundDispatchBeforeWorkAsyncForTests { get; set; }

    internal static TimeSpan? V4PeerSilenceTimeoutOverrideForTests { get; set; }
    internal static TimeSpan? V6HeartbeatIntervalOverrideForTests { get; set; }
    internal static TimeSpan? V6PeerLivenessTimeoutOverrideForTests { get; set; }
    internal static TimeSpan? V6TransportEpochProofTimeoutOverrideForTests { get; set; }
    internal static TimeSpan? V6TransportProbeAckSendTimeoutOverrideForTests { get; set; }
    internal static TimeSpan? V6TunaRedundantDataProbeDelayOverrideForTests { get; set; }
    internal static long? V6TunaRedundantDataMinimumBytesAfterProofOverrideForTests { get; set; }
    internal static TimeSpan? V6SenderTransportSendTimeoutOverrideForTests { get; set; }
    internal static TimeSpan? V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests { get; set; }
    internal static TimeSpan? V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests { get; set; }
    internal static TimeSpan? V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests { get; set; }
    internal static TimeSpan? V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests { get; set; }
    internal static TimeSpan? V6RegularNknNormalReceiverStateResendGateOverrideForTests { get; set; }
    internal static TimeSpan? V6RegularNknFrontierRequestProgressGraceOverrideForTests { get; set; }
    internal static TimeSpan? V6RegularNknInferredFrontierRepairStallOverrideForTests { get; set; }
    internal static TimeSpan? V6RegularNknInferredFrontierRepairCooldownOverrideForTests { get; set; }
    internal static TimeSpan? OfferSendTimeoutOverrideForTests { get; set; }
    public SessionFileTransferService(Func<string>? transferIdFactory = null)
    {
        this.transferIdFactory = transferIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        inboundController = new InboundTransferController(this);
    }

    public event EventHandler<SessionFileTransferSnapshotChangedEventArgs>? TransferChanged;

    public SessionFileTransferSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return CreateSnapshotLocked();
            }
        }
    }

    public bool IsTransferDegraded
    {
        get
        {
            lock (gate)
            {
                return IsTransferDegradedLocked();
            }
        }
    }

    public bool IsCatchUpOnlyPressureActive
    {
        get
        {
            lock (gate)
            {
                return IsCatchUpOnlyPressureActiveLocked();
            }
        }
    }

    public bool IsV4MixedScreenShareTransferActive
    {
        get
        {
            lock (gate)
            {
                return IsV4MixedScreenShareTransferActiveLocked();
            }
        }
    }

    internal void SetFlowControlMode(FileTransferFlowControlMode mode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var nextPolicy = FileTransferFlowControlPolicy.ForMode(mode);

        lock (gate)
        {
            if (flowControlPolicy == nextPolicy)
            {
                return;
            }

            flowControlPolicy = nextPolicy;
        }
    }

    internal void SetSessionScreenShareActive(bool active)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var startupPhasePending = false;
        OutboundTransferContext? activeOutboundContext = null;
        InboundTransferContext? activeInboundPullContext = null;
        int previousInboundPipelineDepth = 0;
        int updatedInboundPipelineDepth = 0;

        lock (gate)
        {
            if (active)
            {
                sessionScreenShareObserved = true;
            }

            if (sessionScreenShareActive == active)
            {
                return;
            }

            sessionScreenShareActive = active;
            if (outboundTransfer is not null && !outboundTransfer.IsTerminal)
            {
                activeOutboundContext = outboundTransfer;
                UpdateOutboundPressureDerivedStateLocked(outboundTransfer);
                outboundTransfer.PullCurrentPipelineDepth = ResolveOutboundInitialPipelineDepth(outboundTransfer);
            }
            if (inboundTransfer is not null &&
                !inboundTransfer.IsTerminal &&
                inboundTransfer.State is FileTransferTransferState.Receiving or FileTransferTransferState.Verifying)
            {
                startupPhasePending = !inboundTransfer.StartupPhaseCompleted;
                previousInboundPipelineDepth = inboundTransfer.PullCurrentPipelineDepth;
                inboundTransfer.PullCurrentPipelineDepth = Math.Min(
                    inboundTransfer.PullCurrentPipelineDepth > 0 ? inboundTransfer.PullCurrentPipelineDepth : ResolveInboundMaximumPipelineDepthLocked(inboundTransfer),
                    ResolveInboundMaximumPipelineDepthLocked(inboundTransfer));
                updatedInboundPipelineDepth = inboundTransfer.PullCurrentPipelineDepth;
                if (inboundTransfer.PullSessionActive)
                {
                    activeInboundPullContext = inboundTransfer;
                    inboundTransfer.PullRecoverySinceUtc = null;
                }
            }
        }

        if (activeOutboundContext is not null)
        {
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, activeOutboundContext) && !activeOutboundContext.IsTerminal)
                {
                    activeOutboundContext.SignalControlActivity();
                }
            }
        }

        if (activeInboundPullContext is not null)
        {
            LogPullProfileClampForScreenshare(
                activeInboundPullContext.TransferId,
                activeInboundPullContext.SessionId,
                active ? "active" : "inactive",
                activeInboundPullContext.ChunkSizeBytes,
                updatedInboundPipelineDepth);

            if (updatedInboundPipelineDepth != previousInboundPipelineDepth)
            {
                LogPullPipelineChanged(
                    activeInboundPullContext.TransferId,
                    activeInboundPullContext.SessionId,
                    FileTransferDirection.Inbound,
                    updatedInboundPipelineDepth,
                    activeInboundPullContext.PullSessionDegraded || sessionScreenShareDegraded);
            }

            _ = startupPhasePending;
        }
    }

    internal void SetSessionScreenShareDegraded(bool active)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? activeInboundPullContext = null;
        int previousInboundPipelineDepth = 0;
        int updatedInboundPipelineDepth = 0;
        lock (gate)
        {
            if (active)
            {
                sessionScreenShareObserved = true;
            }

            if (sessionScreenShareDegraded == active)
            {
                return;
            }

            sessionScreenShareDegraded = active;
            if (inboundTransfer is not null &&
                !inboundTransfer.IsTerminal &&
                inboundTransfer.State is FileTransferTransferState.Receiving or FileTransferTransferState.Verifying)
            {
                inboundTransfer.PullSessionDegraded = active || inboundTransfer.PullSessionDegraded;
                previousInboundPipelineDepth = inboundTransfer.PullCurrentPipelineDepth;
                inboundTransfer.PullCurrentPipelineDepth = Math.Min(
                    inboundTransfer.PullCurrentPipelineDepth > 0 ? inboundTransfer.PullCurrentPipelineDepth : ResolveInboundMaximumPipelineDepthLocked(inboundTransfer),
                    ResolveInboundMaximumPipelineDepthLocked(inboundTransfer));
                updatedInboundPipelineDepth = inboundTransfer.PullCurrentPipelineDepth;
                if (inboundTransfer.PullSessionActive)
                {
                    activeInboundPullContext = inboundTransfer;
                    inboundTransfer.PullRecoverySinceUtc = null;
                }
            }
            if (outboundTransfer is not null && !outboundTransfer.IsTerminal)
            {
                outboundTransfer.PullSessionDegraded = active;
            }
        }

        if (activeInboundPullContext is not null)
        {
            if (active)
            {
                LogPullProfileClampForScreenshare(
                    activeInboundPullContext.TransferId,
                    activeInboundPullContext.SessionId,
                    "degraded",
                    activeInboundPullContext.ChunkSizeBytes,
                    updatedInboundPipelineDepth);
            }
            else
            {
                LogPullProfileRecoveredAfterScreenshare(
                    activeInboundPullContext.TransferId,
                    activeInboundPullContext.SessionId,
                    activeInboundPullContext.ChunkSizeBytes,
                    updatedInboundPipelineDepth);
            }

            if (updatedInboundPipelineDepth != previousInboundPipelineDepth)
            {
                LogPullPipelineChanged(
                    activeInboundPullContext.TransferId,
                    activeInboundPullContext.SessionId,
                    FileTransferDirection.Inbound,
                    updatedInboundPipelineDepth,
                    activeInboundPullContext.PullSessionDegraded || sessionScreenShareDegraded);
            }

        }
    }

    public void AttachTransport(IFileTransferSignalingTransport transport)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(transport);

        if (ReferenceEquals(this.transport, transport))
        {
            return;
        }

        DetachTransportCore(markActiveTransfersFailed: true, failureCode: DetachedErrorCode, failureMessage: "Transport was detached.");

        this.transport = transport;
        transport.FileTransferOfferReceived += OnFileTransferOfferReceived;
        transport.FileTransferAcceptReceived += OnFileTransferAcceptReceived;
        transport.FileTransferDeclineReceived += OnFileTransferDeclineReceived;
        transport.FileTransferSessionOpenReceived += OnFileTransferSessionOpenReceived;
        transport.FileTransferCancelReceived += OnFileTransferCancelReceived;
        transport.FileTransferErrorReceived += OnFileTransferErrorReceived;
        transport.FileTransferCompleteReceived += OnFileTransferCompleteReceived;
        transport.FileTransferPauseControlReceived += OnFileTransferPauseControlReceived;
        transport.FileTransferHeartbeatReceived += OnFileTransferHeartbeatReceived;
        transport.FileTransferTransportEpochReceived += OnFileTransferTransportEpochReceived;
        transport.FileTransferTransportProbeReceived += OnFileTransferTransportProbeReceived;
        transport.FileTransferRepairProofReceived += OnFileTransferRepairProofReceived;

        if (transport is ISignalingTransport lifecycleTransport)
        {
            transportLifecycle = lifecycleTransport;
            lifecycleTransport.Rejected += OnTransportRejected;
            lifecycleTransport.Disconnected += OnTransportDisconnected;
        }

        RaiseTransferChanged(CreateSnapshot());
    }

    public void DetachTransport()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        DetachTransportCore(markActiveTransfersFailed: true, failureCode: DetachedErrorCode, failureMessage: "Transport was detached.");
    }

    public void ResetSessionState()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? inbound;
        OutboundTransferContext? outbound;
        lock (gate)
        {
            inbound = inboundTransfer;
            outbound = outboundTransfer;
            if (outbound?.V6TransportEpoch is { } outboundEpoch)
            {
                TerminalizeV6TransportEpochLocked(FileTransferDirection.Outbound, outbound.TransferId, outbound.SessionId, outboundEpoch, "reset_session_state");
                outbound.V6TransportEpoch = null;
            }

            if (inbound?.V6TransportEpoch is { } inboundEpoch)
            {
                TerminalizeV6TransportEpochLocked(FileTransferDirection.Inbound, inbound.TransferId, inbound.SessionId, inboundEpoch, "reset_session_state");
                inbound.V6TransportEpoch = null;
                inbound.V6ReceiverTransportEpoch = 0;
            }

            inboundTransfer = null;
            outboundTransfer = null;
            sessionScreenShareObserved = sessionScreenShareActive || sessionScreenShareDegraded;
        }

        inbound?.CancelLifetime();
        outbound?.CancelLifetime();
        inbound?.DisposeResources();
        outbound?.DisposeResources();
        RaiseTransferChanged(CreateSnapshot());
    }

    public async Task<int> CancelActiveTransfersForSessionEndAsync(string? reason, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var normalizedReason = NormalizeReason(reason) ?? "session_end";
        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = outboundTransfer is { IsTerminal: false } ? outboundTransfer : null;
            inbound = inboundTransfer is { IsTerminal: false } ? inboundTransfer : null;
        }

        var cancelCount = 0;
        if (outbound is null && inbound is null)
        {
            return cancelCount;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_session_end_cancel_started; reason={FormatProtocolLogValue(normalizedReason)}; outbound_active={(outbound is null ? 0 : 1)}; inbound_active={(inbound is null ? 0 : 1)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_terminal_broadcast_started; reason={FormatProtocolLogValue(normalizedReason)}; outbound_active={(outbound is null ? 0 : 1)}; inbound_active={(inbound is null ? 0 : 1)}");

        if (outbound is not null)
        {
            cancelCount++;
            try
            {
                await TransitionOutboundToTerminalAsync(
                        outbound,
                        FileTransferTransferState.Canceled,
                        errorCode: FileTransferResultCodes.CanceledLocal,
                        statusMessage: "Transfer canceled because the session ended.",
                        notifyPeer: true,
                        cancelReason: normalizedReason,
                        ct: CancellationToken.None)
                    .ConfigureAwait(false);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_terminalized_by_session_end; direction=outbound; transfer_id={outbound.TransferId}; session_id={outbound.SessionId}; reason={FormatProtocolLogValue(normalizedReason)}; terminal_state=canceled");
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_session_end_cancel_failed; direction=outbound; transfer_id={outbound.TransferId}; session_id={outbound.SessionId}; reason={FormatProtocolLogValue(normalizedReason)}; error={ex.GetType().Name}");
            }
        }

        if (inbound is not null)
        {
            cancelCount++;
            try
            {
                await TransitionInboundToTerminalAsync(
                        inbound,
                        FileTransferTransferState.Canceled,
                        errorCode: FileTransferResultCodes.CanceledLocal,
                        statusMessage: "Transfer canceled because the session ended.",
                        sendError: false,
                        errorMessage: null,
                        cancelReason: normalizedReason,
                        ct: CancellationToken.None)
                    .ConfigureAwait(false);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_terminalized_by_session_end; direction=inbound; transfer_id={inbound.TransferId}; session_id={inbound.SessionId}; reason={FormatProtocolLogValue(normalizedReason)}; terminal_state=canceled");
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_session_end_cancel_failed; direction=inbound; transfer_id={inbound.TransferId}; session_id={inbound.SessionId}; reason={FormatProtocolLogValue(normalizedReason)}; error={ex.GetType().Name}");
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_session_end_cancel_completed; reason={FormatProtocolLogValue(normalizedReason)}; transfer_count={cancelCount}");
        return cancelCount;
    }

    public async Task<int> BroadcastActiveTransferSessionEndNoticeAsync(string? reason, string source, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();

        var normalizedReason = NormalizeReason(reason) ?? "session_end";
        var normalizedSource = NormalizeReason(source) ?? "session_end_notice";
        ActiveTransferSessionEndNotice? outboundNotice = null;
        ActiveTransferSessionEndNotice? inboundNotice = null;
        lock (gate)
        {
            if (outboundTransfer is { IsTerminal: false } outbound)
            {
                outboundNotice = new ActiveTransferSessionEndNotice(
                    FileTransferDirection.Outbound,
                    outbound.SessionId,
                    outbound.TransferId,
                    outbound.DataSession,
                    outbound.NegotiatedDataProtocolVersion,
                    outbound.V6TransportEpoch?.EpochId ?? outbound.LastRecoveredV6TransportEpoch);
            }

            if (inboundTransfer is { IsTerminal: false } inbound)
            {
                inboundNotice = new ActiveTransferSessionEndNotice(
                    FileTransferDirection.Inbound,
                    inbound.SessionId,
                    inbound.TransferId,
                    inbound.DataSession,
                    inbound.NegotiatedDataProtocolVersion,
                    inbound.V6TransportEpoch?.EpochId ?? inbound.V6ReceiverTransportEpoch);
            }
        }

        var noticeCount = 0;
        if (outboundNotice is null && inboundNotice is null)
        {
            return noticeCount;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_session_end_notice_started; reason={FormatProtocolLogValue(normalizedReason)}; source={FormatProtocolLogValue(normalizedSource)}; outbound_active={(outboundNotice is null ? 0 : 1)}; inbound_active={(inboundNotice is null ? 0 : 1)}");

        if (outboundNotice is { } outboundSessionEndNotice)
        {
            noticeCount++;
            await SendActiveTransferSessionEndNoticeAsync(outboundSessionEndNotice, normalizedReason, normalizedSource).ConfigureAwait(false);
        }

        if (inboundNotice is { } inboundSessionEndNotice)
        {
            noticeCount++;
            await SendActiveTransferSessionEndNoticeAsync(inboundSessionEndNotice, normalizedReason, normalizedSource).ConfigureAwait(false);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_session_end_notice_completed; reason={FormatProtocolLogValue(normalizedReason)}; source={FormatProtocolLogValue(normalizedSource)}; transfer_count={noticeCount}");
        return noticeCount;
    }

    private async Task SendActiveTransferSessionEndNoticeAsync(
        ActiveTransferSessionEndNotice notice,
        string reason,
        string source)
    {
        try
        {
            var controlCancelTask = SendCancelAsync(notice.SessionId, notice.TransferId, reason, CancellationToken.None);
            Task? dataCancelTask = null;
            if (notice.DataSession is not null)
            {
                dataCancelTask = TrySendCancelDataFrameAsync(
                    notice.DataSession,
                    notice.SessionId,
                    notice.TransferId,
                    reason,
                    notice.Direction,
                    notice.ProtocolVersion,
                    notice.TransportEpoch,
                    source,
                    CancellationToken.None);
            }

            if (dataCancelTask is not null)
            {
                await dataCancelTask.ConfigureAwait(false);
            }

            await controlCancelTask.ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_session_end_notice_sent; direction={notice.Direction.ToString().ToLowerInvariant()}; transfer_id={notice.TransferId}; session_id={notice.SessionId}; reason={FormatProtocolLogValue(reason)}; source={FormatProtocolLogValue(source)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_session_end_notice_failed; direction={notice.Direction.ToString().ToLowerInvariant()}; transfer_id={notice.TransferId}; session_id={notice.SessionId}; reason={FormatProtocolLogValue(reason)}; source={FormatProtocolLogValue(source)}; error={ex.GetType().Name}");
        }
    }

    public Task<int> TerminalizeActiveTransfersForPeerSessionEndAsync(string? reason, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return TerminalizeActiveTransfersForPeerTeardownAsync(
            reason,
            eventName: "filetransfer_peer_session_end_terminalization",
            terminalizedEventName: "filetransfer_terminalized_by_peer_session_end",
            terminalState: FileTransferTransferState.Canceled,
            errorCode: FileTransferResultCodes.CanceledRemote,
            statusMessage: "Transfer canceled because the peer ended the session.",
            logTerminalState: "canceled",
            ct);
    }

    public Task<int> TerminalizeActiveTransfersForPeerDisconnectedAsync(string? reason, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return TerminalizeActiveTransfersForPeerTeardownAsync(
            reason,
            eventName: "filetransfer_peer_disconnect_terminalization",
            terminalizedEventName: "filetransfer_terminalized_by_peer_disconnect",
            terminalState: FileTransferTransferState.Failed,
            errorCode: FileTransferResultCodes.PeerDisconnected,
            statusMessage: "Peer disconnected.",
            logTerminalState: "failed",
            ct);
    }

    private async Task<int> TerminalizeActiveTransfersForPeerTeardownAsync(
        string? reason,
        string eventName,
        string terminalizedEventName,
        FileTransferTransferState terminalState,
        string errorCode,
        string statusMessage,
        string logTerminalState,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedReason = NormalizeReason(reason) ?? "peer_teardown";
        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        OutboundTransferContext? terminalOutbound;
        InboundTransferContext? terminalInbound;
        lock (gate)
        {
            outbound = outboundTransfer is { IsTerminal: false } ? outboundTransfer : null;
            inbound = inboundTransfer is { IsTerminal: false } ? inboundTransfer : null;
            terminalOutbound = outboundTransfer is { IsTerminal: true } ? outboundTransfer : null;
            terminalInbound = inboundTransfer is { IsTerminal: true } ? inboundTransfer : null;
        }

        var terminalizedCount = 0;
        if (terminalOutbound is not null)
        {
            LogTerminalTeardownSkippedTerminalWins(
                FileTransferDirection.Outbound,
                terminalOutbound.TransferId,
                terminalOutbound.SessionId,
                terminalOutbound.State,
                terminalOutbound.ErrorCode,
                terminalState,
                errorCode,
                eventName,
                normalizedReason);
        }

        if (terminalInbound is not null)
        {
            LogTerminalTeardownSkippedTerminalWins(
                FileTransferDirection.Inbound,
                terminalInbound.TransferId,
                terminalInbound.SessionId,
                terminalInbound.State,
                terminalInbound.ErrorCode,
                terminalState,
                errorCode,
                eventName,
                normalizedReason);
        }

        if (outbound is null && inbound is null)
        {
            return terminalizedCount;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}_started; reason={FormatProtocolLogValue(normalizedReason)}; outbound_active={(outbound is null ? 0 : 1)}; inbound_active={(inbound is null ? 0 : 1)}");

        if (outbound is not null)
        {
            terminalizedCount++;
            try
            {
                await TransitionOutboundToTerminalAsync(
                        outbound,
                        terminalState,
                        errorCode: errorCode,
                        statusMessage: statusMessage,
                        notifyPeer: false,
                        cancelReason: null,
                        ct: CancellationToken.None)
                    .ConfigureAwait(false);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event={terminalizedEventName}; direction=outbound; transfer_id={outbound.TransferId}; session_id={outbound.SessionId}; reason={FormatProtocolLogValue(normalizedReason)}; terminal_state={logTerminalState}");
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event={eventName}_failed; direction=outbound; transfer_id={outbound.TransferId}; session_id={outbound.SessionId}; reason={FormatProtocolLogValue(normalizedReason)}; error={ex.GetType().Name}");
            }
        }

        if (inbound is not null)
        {
            terminalizedCount++;
            try
            {
                await TransitionInboundToTerminalAsync(
                        inbound,
                        terminalState,
                        errorCode: errorCode,
                        statusMessage: statusMessage,
                        sendError: false,
                        errorMessage: null,
                        cancelReason: null,
                        ct: CancellationToken.None)
                    .ConfigureAwait(false);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event={terminalizedEventName}; direction=inbound; transfer_id={inbound.TransferId}; session_id={inbound.SessionId}; reason={FormatProtocolLogValue(normalizedReason)}; terminal_state={logTerminalState}");
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event={eventName}_failed; direction=inbound; transfer_id={inbound.TransferId}; session_id={inbound.SessionId}; reason={FormatProtocolLogValue(normalizedReason)}; error={ex.GetType().Name}");
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}_completed; reason={FormatProtocolLogValue(normalizedReason)}; transfer_count={terminalizedCount}");
        return terminalizedCount;
    }

    private static void LogTerminalTeardownSkippedTerminalWins(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferTransferState currentState,
        string? currentErrorCode,
        FileTransferTransferState attemptedState,
        string? attemptedErrorCode,
        string source,
        string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_terminal_teardown_skipped_terminal_wins; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; current_state={currentState.ToString().ToLowerInvariant()}; current_error_code={FormatProtocolLogValue(currentErrorCode ?? "(none)")}; attempted_state={attemptedState.ToString().ToLowerInvariant()}; attempted_error_code={FormatProtocolLogValue(NormalizeErrorCode(attemptedErrorCode) ?? "(none)")}; source={FormatProtocolLogValue(source)}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogTerminalStaleUpdateRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferTransferState currentState,
        string? currentErrorCode,
        FileTransferTransferState attemptedState,
        string? attemptedErrorCode,
        string source,
        string reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_terminal_stale_update_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; current_state={currentState.ToString().ToLowerInvariant()}; current_error_code={FormatProtocolLogValue(currentErrorCode ?? "(none)")}; attempted_state={attemptedState.ToString().ToLowerInvariant()}; attempted_error_code={FormatProtocolLogValue(NormalizeErrorCode(attemptedErrorCode) ?? "(none)")}; source={FormatProtocolLogValue(source)}; reason={FormatProtocolLogValue(reason)}");
    }

    public async Task<FileTransferTransferSnapshot?> TryStartSendAsync(
        FileTransferSendDescriptor descriptor,
        FileTransferReadStreamFactory openReadStreamAsync,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(openReadStreamAsync);

        var normalizedDescriptor = NormalizeSendDescriptor(descriptor, transferIdFactory);
        OutboundTransferContext context;
        IFileTransferSignalingTransport currentTransport;
        FileTransferRouteResolverInput routeInput;
        FileTransferInitialTunaReadinessEvidence? initialTunaEvidence;

        lock (gate)
        {
            if (transport is null)
            {
                return null;
            }

            currentTransport = transport;
            if (outboundTransfer is not null && !outboundTransfer.IsTerminal)
            {
                return null;
            }

            var transportRouteInput = FileTransferRouteResolverInput.FromTransport(currentTransport);
            routeInput = CreateInitialRouteResolverInputForNewTransfer(currentTransport);
            initialTunaEvidence = CaptureInitialTunaReadinessEvidence(
                currentTransport,
                ResolveCurrentFileTransferSessionId(currentTransport),
                normalizedDescriptor.TransferId ?? string.Empty,
                FileTransferDirection.Outbound);
            var routeSelection = ResolveInitialOutboundRouteSelection(
                routeInput,
                transportRouteInput,
                initialTunaEvidence);
            context = new OutboundTransferContext(normalizedDescriptor, openReadStreamAsync)
            {
                SessionId = ResolveCurrentFileTransferSessionId(currentTransport),
                RouteSelection = routeSelection,
                OfferedDataProtocolVersion = routeSelection.ProtocolVersion,
                NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion,
            };
            ApplyFileTransferRuntimeProfileSelectionLocked(
                context,
                FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection));
            if (EnsureOutboundTransferLegLocked(
                    context,
                    "new_transfer",
                    initialTunaEvidence,
                    ResolveFileTransferRouteForNewTransfer(routeInput)) is null)
            {
                return null;
            }

            routeInput = CreateRouteInputForPublishedSelection(context.RouteSelection, routeInput.TransportProfileKind);
            outboundTransfer = context;
        }

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                TryPublishRouteSelectedIfAuthorizedLocked(
                    context,
                    routeInput,
                    "new_transfer",
                    "new_transfer");
            }
        }
        RaiseTransferChanged(CreateSnapshot());

        if (!IsV6StreamingTransport(currentTransport))
        {
            LogV6RequiredTransportIncompatible(context.TransferId, context.SessionId);
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: TransportIncompatibleErrorCode,
                statusMessage: "File transfer requires V6 streaming support from the attached transport.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.LifetimeCts.Token);

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return CaptureCurrentOutboundSnapshot();
            }

            context.State = FileTransferTransferState.AwaitingAcceptance;
            context.StatusMessage = "Waiting for receiver response.";
        }

        RaiseTransferChanged(CreateSnapshot());

        var offerMessage = new FileTransferOfferV2
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            FileName = context.FileName,
            FileSizeBytes = context.FileSizeBytes,
            PreferredDataProtocolVersion = context.OfferedDataProtocolVersion,
            FileTransferRoute = context.RouteSelection.TelemetryToken,
        };

        StartOutboundOfferRetryLoop(context, offerMessage);

        try
        {
            var offerTransport = GetTransportOrThrow();
            if (await TrySendOutboundOfferWithTimeoutAsync(
                    context,
                    offerTransport,
                    offerMessage,
                    attempt: 1,
                    source: "initial",
                    linkedCts.Token)
                .ConfigureAwait(false))
            {
                LogTransferInfo(
                    "offer_sent",
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    sessionId: context.SessionId,
                    fileName: context.FileName,
                    fileSizeBytes: context.FileSizeBytes);
            }

            return CaptureCurrentOutboundSnapshot();
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledLocal,
                statusMessage: "Transfer offer canceled.",
                notifyPeer: false,
                cancelReason: CanceledReason,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }
        catch (Exception ex)
        {
            var errorCode = ClassifyOutboundFailureErrorCode(ex, InvalidStateErrorCode);
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: errorCode,
                statusMessage: ClassifyOutboundFailureStatusMessage(ex, errorCode),
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }
    }

    private static string ResolveCurrentFileTransferSessionId(IFileTransferSignalingTransport? currentTransport)
    {
        if (currentTransport is not IFileTransferSessionContextProvider sessionContextProvider)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(sessionContextProvider.CurrentFileTransferSessionId)
            ? string.Empty
            : sessionContextProvider.CurrentFileTransferSessionId.Trim();
    }

    private static TimeSpan GetOfferSendTimeout()
    {
        var timeout = OfferSendTimeoutOverrideForTests ?? TimeSpan.FromMilliseconds(OfferSendTimeoutMs);
        return timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(OfferSendTimeoutMs) : timeout;
    }

    private async Task<bool> TrySendOutboundOfferWithTimeoutAsync(
        OutboundTransferContext context,
        IFileTransferSignalingTransport currentTransport,
        FileTransferOfferV2 offerMessage,
        int attempt,
        string source,
        CancellationToken ct)
    {
        var timeout = GetOfferSendTimeout();
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_offer_send_started; transfer_id={context.TransferId}; session_id={context.SessionId}; attempt={attempt}; source={FormatProtocolLogValue(source)}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.RouteSelection.ProtocolVersion}; timeout_ms={(long)timeout.TotalMilliseconds}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await currentTransport.SendFileTransferOfferAsync(offerMessage, timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_offer_send_timed_out; transfer_id={context.TransferId}; session_id={context.SessionId}; attempt={attempt}; source={FormatProtocolLogValue(source)}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.RouteSelection.ProtocolVersion}; timeout_ms={(long)timeout.TotalMilliseconds}");
            return false;
        }
    }

    private void StartOutboundOfferRetryLoop(OutboundTransferContext context, FileTransferOfferV2 offerMessage)
    {
        _ = Task.Run(() => RunOutboundOfferRetryLoopAsync(context, offerMessage));
    }

    private async Task RunOutboundOfferRetryLoopAsync(OutboundTransferContext context, FileTransferOfferV2 offerMessage)
    {
        var attempt = 1;
        var retryIndex = 0;

        while (true)
        {
            var delayMs = retryIndex < OfferRetryDelaysMs.Length
                ? OfferRetryDelaysMs[retryIndex++]
                : OfferRetrySteadyStateDelayMs;

            try
            {
                await Task.Delay(delayMs, context.LifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
                return;
            }

            IFileTransferSignalingTransport? currentTransport;
            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) ||
                    context.IsTerminal ||
                    context.SendStarted ||
                    context.State != FileTransferTransferState.AwaitingAcceptance)
                {
                    return;
                }

                currentTransport = transport;
            }

            if (currentTransport is null)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_offer_retry_deferred; transfer_id={context.TransferId}; session_id={context.SessionId}; attempt={attempt + 1}; reason=transport_unavailable");
                continue;
            }

            try
            {
                attempt++;
                if (await TrySendOutboundOfferWithTimeoutAsync(
                        context,
                        currentTransport,
                        offerMessage,
                        attempt,
                        source: "retry",
                        context.LifetimeCts.Token)
                    .ConfigureAwait(false))
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_offer_retry_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; attempt={attempt}; delay_ms={delayMs}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.RouteSelection.ProtocolVersion}");
                }
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_offer_retry_send_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; attempt={attempt}; error={ex.GetType().Name}");
            }
        }
    }

    public async Task<FileTransferTransferSnapshot?> AcceptIncomingTransferAsync(
        string transferId,
        FileTransferWriteStreamFactory openWriteStreamAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(openWriteStreamAsync);

        return await AcceptIncomingTransferAsync(
                transferId,
                async (offer, linkedCt) =>
                {
                    var stream = await openWriteStreamAsync(offer, linkedCt).ConfigureAwait(false);
                    return FileTransferReceiveDestination.FromStream(stream);
                },
                ct)
            .ConfigureAwait(false);
    }

    public async Task<FileTransferTransferSnapshot?> AcceptIncomingTransferAsync(
        string transferId,
        FileTransferWriteDestinationFactory openWriteDestinationAsync,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(openWriteDestinationAsync);

        InboundTransferContext? context;

        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.PendingDecision ||
                context.AcceptInProgress ||
                !string.Equals(context.TransferId, NormalizeTransferId(transferId), StringComparison.Ordinal))
            {
                return null;
            }

            context.AcceptInProgress = true;
        }

        SessionFileTransferSnapshot snapshot;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.PendingDecision)
            {
                context.AcceptInProgress = false;
                return CaptureCurrentInboundSnapshot();
            }

            context.OpenWriteDestinationAsync = openWriteDestinationAsync;
            context.AcceptInProgress = false;
            context.NegotiatedDataProtocolVersion = context.RouteSelection.ProtocolVersion;
            context.MetadataAwaitingSinceUtc = DateTimeOffset.UtcNow;
            context.State = FileTransferTransferState.AwaitingMetadata;
            context.StatusMessage = "Waiting for sender to prepare the file.";
            snapshot = CreateSnapshotLocked();
        }

        RaiseTransferChanged(snapshot);
        _ = RunInboundAwaitingMetadataTimeoutAsync(context);

        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    AcceptedDataProtocolVersion = context.NegotiatedDataProtocolVersion,
                    FileTransferRoute = context.RouteSelection.TelemetryToken,
                },
                ct).ConfigureAwait(false);
            if (context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6)
            {
                StartInboundV6HeartbeatLoop(context, "accept_sent");
            }
            return CaptureCurrentInboundSnapshot();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not send the accept response.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentInboundSnapshot();
        }
    }

    public async Task<FileTransferTransferSnapshot?> DeclineIncomingTransferAsync(
        string transferId,
        string? reason,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? context;
        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.PendingDecision ||
                !string.Equals(context.TransferId, NormalizeTransferId(transferId), StringComparison.Ordinal))
            {
                return null;
            }
        }

        await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Declined,
                errorCode: NormalizeReason(reason) == FileTransferResultCodes.Busy ? FileTransferResultCodes.Busy : null,
                statusMessage: NormalizeReason(reason) ?? "Transfer declined.",
                sendError: false,
                errorMessage: null,
            cancelReason: NormalizeReason(reason) ?? DeclinedReason,
            ct: ct).ConfigureAwait(false);
        return CaptureCurrentInboundSnapshot();
    }

    public async Task<FileTransferTransferSnapshot?> CancelTransferAsync(
        string transferId,
        string? reason,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalizedTransferId = NormalizeTransferId(transferId);

        OutboundTransferContext? outboundContext;
        InboundTransferContext? inboundContext;
        lock (gate)
        {
            if (outboundTransfer is not null &&
                outboundTransfer.IsTerminal &&
                string.Equals(outboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                LogTerminalStaleUpdateRejected(
                    FileTransferDirection.Outbound,
                    outboundTransfer.TransferId,
                    outboundTransfer.SessionId,
                    outboundTransfer.State,
                    outboundTransfer.ErrorCode,
                    FileTransferTransferState.Canceled,
                    FileTransferResultCodes.CanceledLocal,
                    "user_cancel",
                    NormalizeReason(reason) ?? "user_requested");
                return null;
            }

            if (inboundTransfer is not null &&
                inboundTransfer.IsTerminal &&
                string.Equals(inboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                LogTerminalStaleUpdateRejected(
                    FileTransferDirection.Inbound,
                    inboundTransfer.TransferId,
                    inboundTransfer.SessionId,
                    inboundTransfer.State,
                    inboundTransfer.ErrorCode,
                    FileTransferTransferState.Canceled,
                    FileTransferResultCodes.CanceledLocal,
                    "user_cancel",
                    NormalizeReason(reason) ?? "user_requested");
                return null;
            }

            outboundContext = outboundTransfer is not null &&
                              !outboundTransfer.IsTerminal &&
                              string.Equals(outboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal)
                ? outboundTransfer
                : null;
            inboundContext = inboundTransfer is not null &&
                             !inboundTransfer.IsTerminal &&
                             string.Equals(inboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal)
                ? inboundTransfer
                : null;
        }

        if (outboundContext is not null)
        {
            await TransitionOutboundToTerminalAsync(
                outboundContext,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledLocal,
                statusMessage: NormalizeReason(reason) ?? "Transfer canceled.",
                notifyPeer: true,
                cancelReason: NormalizeReason(reason) ?? CanceledReason,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }

        if (inboundContext is not null)
        {
            await TransitionInboundToTerminalAsync(
                inboundContext,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledLocal,
                statusMessage: NormalizeReason(reason) ?? "Transfer canceled.",
                sendError: false,
                errorMessage: null,
                cancelReason: NormalizeReason(reason) ?? CanceledReason,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentInboundSnapshot();
        }

        return null;
    }

    public async Task<FileTransferTransferSnapshot?> PauseTransferAsync(
        string transferId,
        string? reason,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var normalizedTransferId = NormalizeTransferId(transferId);
        var normalizedReason = NormalizeReason(reason) ?? "user_requested";
        SessionFileTransferSnapshot? snapshot = null;
        FileTransferTransferSnapshot? result = null;
        string? sessionId = null;
        FileTransferDirection direction = default;
        FileTransferTransferState state = default;
        bool transportPaused = false;
        OutboundTransferContext? pausedOutboundContext = null;
        InboundTransferContext? pausedInboundContext = null;

        lock (gate)
        {
            if (outboundTransfer is not null &&
                outboundTransfer.IsTerminal &&
                string.Equals(outboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                LogTerminalStaleUpdateRejected(
                    FileTransferDirection.Outbound,
                    outboundTransfer.TransferId,
                    outboundTransfer.SessionId,
                    outboundTransfer.State,
                    outboundTransfer.ErrorCode,
                    outboundTransfer.State,
                    outboundTransfer.ErrorCode,
                    "user_pause",
                    normalizedReason);
                return null;
            }

            if (inboundTransfer is not null &&
                inboundTransfer.IsTerminal &&
                string.Equals(inboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                LogTerminalStaleUpdateRejected(
                    FileTransferDirection.Inbound,
                    inboundTransfer.TransferId,
                    inboundTransfer.SessionId,
                    inboundTransfer.State,
                    inboundTransfer.ErrorCode,
                    inboundTransfer.State,
                    inboundTransfer.ErrorCode,
                    "user_pause",
                    normalizedReason);
                return null;
            }

            if (outboundTransfer is not null &&
                !outboundTransfer.IsTerminal &&
                string.Equals(outboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                if (outboundTransfer.UserPaused ||
                    IsTunaActivationNegotiationTransportPauseReason(outboundTransfer.PullTransportPauseReason) ||
                    !CanUserPauseOutbound(outboundTransfer.State))
                {
                    return null;
                }

                outboundTransfer.UserPaused = true;
                outboundTransfer.UserPauseReason = normalizedReason;
                outboundTransfer.UserPausedSinceUtc = DateTimeOffset.UtcNow;
                outboundTransfer.StatusMessage = "Transfer paused.";
                snapshot = CreateSnapshotLocked();
                result = outboundTransfer.ToSnapshot();
                sessionId = outboundTransfer.SessionId;
                direction = FileTransferDirection.Outbound;
                state = outboundTransfer.State;
                transportPaused = outboundTransfer.PullTransportPaused;
                outboundTransfer.SparseSenderPumpLastWakeReason = "user_paused";
                outboundTransfer.ResetV6SenderPipelineCancellation();
                outboundTransfer.SignalSparseSenderPump();
                pausedOutboundContext = !string.IsNullOrWhiteSpace(outboundTransfer.SessionId)
                    ? outboundTransfer
                    : null;
            }
            else if (inboundTransfer is not null &&
                     !inboundTransfer.IsTerminal &&
                     string.Equals(inboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                if (inboundTransfer.UserPaused ||
                    IsTunaActivationNegotiationTransportPauseReason(inboundTransfer.PullTransportPauseReason) ||
                    !CanUserPauseInbound(inboundTransfer.State))
                {
                    return null;
                }

                inboundTransfer.UserPaused = true;
                inboundTransfer.UserPauseReason = normalizedReason;
                inboundTransfer.UserPausedSinceUtc = DateTimeOffset.UtcNow;
                inboundTransfer.StatusMessage = "Transfer paused.";
                snapshot = CreateSnapshotLocked();
                result = inboundTransfer.ToSnapshot();
                sessionId = inboundTransfer.SessionId;
                direction = FileTransferDirection.Inbound;
                state = inboundTransfer.State;
                transportPaused = inboundTransfer.PullTransportPaused;
                pausedInboundContext = !string.IsNullOrWhiteSpace(inboundTransfer.SessionId)
                    ? inboundTransfer
                    : null;
            }
        }

        if (snapshot is null || result is null)
        {
            return null;
        }

        RaiseTransferChanged(snapshot);
        LogUserPauseResume("filetransfer_user_paused", normalizedTransferId, sessionId, direction, state, paused: true, normalizedReason, transportPaused);
        if (pausedOutboundContext is not null)
        {
            ScheduleOutboundV4PauseControlRetry(pausedOutboundContext, paused: true, "user_paused");
            _ = Task.Run(
                () => SendOutboundV4PauseStateAsync(pausedOutboundContext, "user_paused"),
                CancellationToken.None);
        }

        if (pausedInboundContext is not null)
        {
            ScheduleInboundV4PauseControlRetry(pausedInboundContext, paused: true, "user_paused");
            _ = Task.Run(
                () => SendInboundPauseProgressStateAsync(pausedInboundContext, "user_paused"),
                CancellationToken.None);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    public async Task<FileTransferTransferSnapshot?> ResumeTransferAsync(
        string transferId,
        string? reason,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var normalizedTransferId = NormalizeTransferId(transferId);
        var normalizedReason = NormalizeReason(reason);
        SessionFileTransferSnapshot? snapshot = null;
        FileTransferTransferSnapshot? result = null;
        string? sessionId = null;
        FileTransferDirection direction = default;
        FileTransferTransferState state = default;
        bool transportPaused = false;
        OutboundTransferContext? resumedOutboundContext = null;
        OutboundTransferContext? resumedOutboundPumpContext = null;
        InboundTransferContext? resumedInboundContext = null;

        lock (gate)
        {
            if (outboundTransfer is not null &&
                outboundTransfer.IsTerminal &&
                string.Equals(outboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                LogTerminalStaleUpdateRejected(
                    FileTransferDirection.Outbound,
                    outboundTransfer.TransferId,
                    outboundTransfer.SessionId,
                    outboundTransfer.State,
                    outboundTransfer.ErrorCode,
                    outboundTransfer.State,
                    outboundTransfer.ErrorCode,
                    "user_resume",
                    normalizedReason ?? "user_requested");
                return null;
            }

            if (inboundTransfer is not null &&
                inboundTransfer.IsTerminal &&
                string.Equals(inboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                LogTerminalStaleUpdateRejected(
                    FileTransferDirection.Inbound,
                    inboundTransfer.TransferId,
                    inboundTransfer.SessionId,
                    inboundTransfer.State,
                    inboundTransfer.ErrorCode,
                    inboundTransfer.State,
                    inboundTransfer.ErrorCode,
                    "user_resume",
                    normalizedReason ?? "user_requested");
                return null;
            }

            if (outboundTransfer is not null &&
                !outboundTransfer.IsTerminal &&
                string.Equals(outboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                if (!outboundTransfer.UserPaused || !CanUserPauseOutbound(outboundTransfer.State))
                {
                    return null;
                }

                outboundTransfer.UserPaused = false;
                outboundTransfer.UserPauseReason = normalizedReason;
                outboundTransfer.UserPausedSinceUtc = null;
                outboundTransfer.StatusMessage = outboundTransfer.PeerPaused
                    ? "Peer paused transfer."
                    : GetOutboundResumeStatusMessage(outboundTransfer.State);
                snapshot = CreateSnapshotLocked();
                result = outboundTransfer.ToSnapshot();
                sessionId = outboundTransfer.SessionId;
                direction = FileTransferDirection.Outbound;
                state = outboundTransfer.State;
                transportPaused = outboundTransfer.PullTransportPaused;
                ResetOutboundV4AcceptedForUserResumeLocked(outboundTransfer);
                outboundTransfer.SparseSenderPumpLastWakeReason = "user_resumed";
                resumedOutboundPumpContext = outboundTransfer;
                resumedOutboundContext = !string.IsNullOrWhiteSpace(outboundTransfer.SessionId)
                    ? outboundTransfer
                    : null;
            }
            else if (inboundTransfer is not null &&
                     !inboundTransfer.IsTerminal &&
                     string.Equals(inboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                if (!inboundTransfer.UserPaused || !CanUserPauseInbound(inboundTransfer.State))
                {
                    return null;
                }

                inboundTransfer.UserPaused = false;
                inboundTransfer.UserPauseReason = normalizedReason;
                inboundTransfer.UserPausedSinceUtc = null;
                inboundTransfer.StatusMessage = inboundTransfer.PeerPaused
                    ? "Peer paused transfer."
                    : GetInboundResumeStatusMessage(inboundTransfer.State);
                snapshot = CreateSnapshotLocked();
                result = inboundTransfer.ToSnapshot();
                sessionId = inboundTransfer.SessionId;
                direction = FileTransferDirection.Inbound;
                state = inboundTransfer.State;
                transportPaused = inboundTransfer.PullTransportPaused;
                resumedInboundContext = !string.IsNullOrWhiteSpace(inboundTransfer.SessionId)
                    ? inboundTransfer
                    : null;
            }
        }

        if (snapshot is null || result is null)
        {
            return null;
        }

        RaiseTransferChanged(snapshot);
        LogUserPauseResume("filetransfer_user_resumed", normalizedTransferId, sessionId, direction, state, paused: false, normalizedReason, transportPaused);
        if (resumedOutboundContext is not null)
        {
            ScheduleOutboundV4PauseControlRetry(resumedOutboundContext, paused: false, "user_resumed");
            _ = Task.Run(
                () => SendOutboundV4PauseStateAsync(resumedOutboundContext, "user_resumed"),
                CancellationToken.None);
        }

        resumedOutboundPumpContext?.SignalSparseSenderPump();

        if (resumedInboundContext is not null)
        {
            ScheduleInboundV4PauseControlRetry(resumedInboundContext, paused: false, "user_resumed");
            _ = Task.Run(
                () => FlushInboundPausedProgressAsync(resumedInboundContext, "user_resumed"),
                CancellationToken.None);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    private static bool CanUserPauseOutbound(FileTransferTransferState state)
        => state is FileTransferTransferState.AwaitingAcceptance
            or FileTransferTransferState.PreparingMetadata
            or FileTransferTransferState.AwaitingStart
            or FileTransferTransferState.Sending;

    private static bool CanUserPauseInbound(FileTransferTransferState state)
        => state is FileTransferTransferState.AwaitingMetadata
            or FileTransferTransferState.AwaitingStart
            or FileTransferTransferState.Receiving;

    private static string GetOutboundResumeStatusMessage(FileTransferTransferState state)
        => state switch
        {
            FileTransferTransferState.AwaitingAcceptance => "Waiting for receiver response.",
            FileTransferTransferState.PreparingMetadata => "Preparing file metadata.",
            FileTransferTransferState.AwaitingStart => "Starting V6 file transfer.",
            FileTransferTransferState.Sending => "Sending file data.",
            _ => "Transfer resumed.",
        };

    private static string GetInboundResumeStatusMessage(FileTransferTransferState state)
        => state switch
        {
            FileTransferTransferState.AwaitingMetadata => "Waiting for sender to prepare the file.",
            FileTransferTransferState.AwaitingStart => "Preparing to receive.",
            FileTransferTransferState.Receiving => "Receiving V6 file data.",
            _ => "Transfer resumed.",
        };

    private static void LogUserPauseResume(
        string eventName,
        string transferId,
        string? sessionId,
        FileTransferDirection direction,
        FileTransferTransferState state,
        bool paused,
        string? reason,
        bool transportPaused)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; transfer_id={transferId}; session_id={sessionId ?? "(none)"}; direction={direction}; state={state}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason ?? "(none)")}; transport_paused={(transportPaused ? 1 : 0)}");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DetachTransportCore(markActiveTransfersFailed: false, failureCode: DetachedErrorCode, failureMessage: "Service disposed.");

        InboundTransferContext? inbound;
        OutboundTransferContext? outbound;
        lock (gate)
        {
            inbound = inboundTransfer;
            outbound = outboundTransfer;
            inboundTransfer = null;
            outboundTransfer = null;
        }

        inbound?.DisposeResources();
        outbound?.DisposeResources();
    }

    private async Task<bool> PrepareAcceptedOutboundTransferAsync(OutboundTransferContext context, CancellationToken ct)
    {
        using var stream = await context.OpenReadStreamAsync(ct).ConfigureAwait(false);
        ValidateReadableStream(stream);

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(context.ChunkSizeBytes, 4096, 64 * 1024));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytesReadTotal = 0;
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, bytesRead);
                bytesReadTotal += bytesRead;
                if (bytesReadTotal > context.FileSizeBytes)
                {
                    throw new InvalidOperationException("Source stream length exceeded the declared file size.");
                }
            }

            if (bytesReadTotal != context.FileSizeBytes)
            {
                throw new InvalidOperationException("Source stream length did not match the declared file size.");
            }

            var hashBytes = hash.GetHashAndReset();
            SessionFileTransferSnapshot snapshot;
            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    return false;
                }

                var payloadProfileSelection = ResolvePayloadEfficiencyProfileSelectionLocked(context);
                context.PayloadEfficiencyProfile = payloadProfileSelection.Profile;
                context.PayloadEfficiencyProfileSelectionReason = payloadProfileSelection.Reason;
                context.ChunkSizeBytes = ResolveSafeOutboundChunkSize(context, transport);
                context.Sha256Base64 = Convert.ToBase64String(hashBytes);
                context.ChunkCount = checked((int)((context.FileSizeBytes + context.ChunkSizeBytes - 1) / context.ChunkSizeBytes));
                context.State = FileTransferTransferState.AwaitingStart;
                context.StatusMessage = "Starting file transfer.";
                snapshot = CreateSnapshotLocked();
            }

            LogTransferInfo(
                "metadata_prepared",
                FileTransferDirection.Outbound,
                context.TransferId,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes,
                reason: $"chunk_count={context.ChunkCount}; chunk_size_bytes={context.ChunkSizeBytes}");
            LogPullChunkProfile(
                context.TransferId,
                context.SessionId,
                context.ChunkSizeBytes,
                pipelineDepth: ResolveOutboundInitialPipelineDepth(context),
                screenshareActive: sessionScreenShareActive,
                screenshareDegraded: sessionScreenShareDegraded);
            LogPayloadEfficiencyProfileSelected(context);
            RaiseTransferChanged(snapshot);
            return true;
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task PrepareAndStartAcceptedOutboundTransferAsync(OutboundTransferContext context)
    {
        try
        {
            if (!IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion))
            {
                LogLegacyNegotiationRejected(
                    context.TransferId,
                    context.SessionId,
                    FileTransferDirection.Outbound,
                    offeredVersion: context.OfferedDataProtocolVersion,
                    acceptedVersion: context.NegotiatedDataProtocolVersion,
                    reason: "prepared_protocol_not_supported");
                await TransitionOutboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: TransportIncompatibleErrorCode,
                    statusMessage: "File transfer requires a supported data protocol.",
                    notifyPeer: false,
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!await PrepareAcceptedOutboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false))
            {
                return;
            }

            if (context.NegotiatedDataProtocolVersion != context.RouteSelection.ProtocolVersion)
            {
                LogLegacyNegotiationRejected(
                    context.TransferId,
                    context.SessionId,
                    FileTransferDirection.Outbound,
                    offeredVersion: context.OfferedDataProtocolVersion,
                    acceptedVersion: context.NegotiatedDataProtocolVersion,
                    reason: "prepared_route_protocol_mismatch");
                await TransitionOutboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: TransportIncompatibleErrorCode,
                    statusMessage: "File transfer route does not match the negotiated protocol.",
                    notifyPeer: false,
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var routeRuntime = context.RouteRuntime;
            if (routeRuntime.UsesV4SparsePump)
            {
                LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, context.RouteSelection);
                if ((sessionScreenShareActive || sessionScreenShareDegraded) && !ShouldAllowV4DuringScreenShare())
                {
                    await FailOutboundV4Async(
                        context,
                        dataSession: null,
                        runtimeOwner: null,
                        V4FileOnlyRequiredErrorCode,
                        "V4 file-transfer send is currently file-only.",
                        notifyPeer: false).ConfigureAwait(false);
                    return;
                }

                LogV4MixedScreenShareEnabled(context.TransferId, context.SessionId, FileTransferDirection.Outbound, context.RouteSelection);
                await RunOutboundRegularNknV4Async(context).ConfigureAwait(false);
                return;
            }

            if (routeRuntime.UsesV6RequestPump)
            {
                LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, context.RouteSelection);
                if ((sessionScreenShareActive || sessionScreenShareDegraded) && !ShouldAllowV4DuringScreenShare())
                {
                    await FailOutboundV4Async(
                        context,
                        dataSession: null,
                        runtimeOwner: null,
                        V4FileOnlyRequiredErrorCode,
                        "V6 file-transfer send is currently file-only.",
                        notifyPeer: false).ConfigureAwait(false);
                    return;
                }

                LogV4MixedScreenShareEnabled(context.TransferId, context.SessionId, FileTransferDirection.Outbound, context.RouteSelection);
                var v6RuntimeSelection = ResolveFileTransferRuntimeProfile(context);
                LogFileTransferBridgeRecoveryPolicySelected(
                    context.TransferId,
                    context.SessionId,
                    FileTransferDirection.Outbound,
                    v6RuntimeSelection,
                    context.RouteSelection);
                await RunOutboundV6SenderAsync(context).ConfigureAwait(false);
                return;
            }

            if (routeRuntime.UsesV6SparsePump)
            {
                LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, context.RouteSelection);
                if ((sessionScreenShareActive || sessionScreenShareDegraded) && !ShouldAllowV4DuringScreenShare())
                {
                    await FailOutboundV4Async(
                        context,
                        dataSession: null,
                        runtimeOwner: null,
                        V4FileOnlyRequiredErrorCode,
                        "V6 file-transfer send is currently file-only.",
                        notifyPeer: false).ConfigureAwait(false);
                    return;
                }

                LogV4MixedScreenShareEnabled(context.TransferId, context.SessionId, FileTransferDirection.Outbound, context.RouteSelection);
                var diagnosticRuntimeSelection = ResolveFileTransferRuntimeProfile(context);
                LogFileTransferBridgeRecoveryPolicySelected(
                    context.TransferId,
                    context.SessionId,
                    FileTransferDirection.Outbound,
                    diagnosticRuntimeSelection,
                    context.RouteSelection);
                LogPrimaryRegularNknBulkV6Selected(context.TransferId, context.SessionId, FileTransferDirection.Outbound, diagnosticRuntimeSelection, context.RouteSelection);
                await RunOutboundPrimaryRegularNknBulkV6Async(context).ConfigureAwait(false);
                return;
            }

            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: TransportIncompatibleErrorCode,
                statusMessage: "Unsupported file-transfer route.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var errorCode = ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode);
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: errorCode,
                statusMessage: ClassifyOutboundFailureStatusMessage(ex, errorCode),
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunInboundAwaitingMetadataTimeoutAsync(InboundTransferContext context)
    {
        try
        {
            await Task.Delay(InboundMetadataTimeoutMs, context.LifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool shouldFail;
        lock (gate)
        {
            shouldFail = ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                context.State == FileTransferTransferState.AwaitingMetadata;
        }

        if (!shouldFail)
        {
            return;
        }

        await TransitionInboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: FileTransferResultCodes.MetadataNotProvided,
            statusMessage: "Sender did not provide file metadata.",
            sendError: true,
            errorMessage: "Sender could not prepare the file.",
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
    }

    private void OnFileTransferOfferReceived(object? sender, FileTransferOfferReceivedEventArgs e)
        => EnqueueInboundLifecycle("offer", () => HandleIncomingOfferAsync(e.Message));

    private void OnFileTransferAcceptReceived(object? sender, FileTransferAcceptReceivedEventArgs e)
        => EnqueueInboundLifecycle("accept", () => HandleIncomingAcceptAsync(e.Message));

    private void OnFileTransferDeclineReceived(object? sender, FileTransferDeclineReceivedEventArgs e)
        => EnqueueInboundLifecycle("decline", () => HandleIncomingDeclineAsync(e.Message));

    private void OnFileTransferSessionOpenReceived(object? sender, FileTransferSessionOpenReceivedEventArgs e)
        => EnqueueInboundLifecycle("session_open", () => HandleIncomingSessionOpenAsync(e.Message));

    private void OnFileTransferCancelReceived(object? sender, FileTransferCancelReceivedEventArgs e)
        => RunHardPriorityInboundLifecycle("cancel", () => HandleIncomingCancelAsync(e.Message));

    private void OnFileTransferErrorReceived(object? sender, FileTransferErrorReceivedEventArgs e)
        => RunHardPriorityInboundLifecycle("error", () => HandleIncomingErrorAsync(e.Message));

    private void OnFileTransferCompleteReceived(object? sender, FileTransferCompleteReceivedEventArgs e)
        => RunHardPriorityInboundLifecycle("complete", () => HandleIncomingCompleteAsync(e.Message));

    private void OnFileTransferPauseControlReceived(object? sender, FileTransferPauseControlReceivedEventArgs e)
        => RunHardPriorityInboundLifecycle("pause_control", () => HandleIncomingPauseControlAsync(e.Message));

    private void OnFileTransferHeartbeatReceived(object? sender, FileTransferHeartbeatReceivedEventArgs e)
        => RunHardPriorityInboundLifecycle("heartbeat", () => HandleIncomingHeartbeatAsync(e.Message));

    private void OnFileTransferTransportEpochReceived(object? sender, FileTransferTransportEpochReceivedEventArgs e)
        => RunHardPriorityInboundLifecycle("transport_epoch", () => HandleIncomingTransportEpochAsync(e.Message));

    private void OnFileTransferTransportProbeReceived(object? sender, FileTransferTransportProbeReceivedEventArgs e)
        => RunHardPriorityInboundLifecycle("transport_probe", () => HandleIncomingTransportProbeAckAsync(e.Message));

    private void OnFileTransferRepairProofReceived(object? sender, FileTransferRepairProofReceivedEventArgs e)
        => RunHardPriorityInboundLifecycle("repair_proof", () => HandleIncomingRepairProofAsync(e.Message));

    private void OnTransportRejected(object? sender, EventArgs e)
        => RunHardPriorityInboundLifecycle("transport_rejected", HandleTransportRejectedAsync);

    private void OnTransportDisconnected(object? sender, EventArgs e)
        => RunHardPriorityInboundLifecycle("transport_disconnect", HandleTransportDisconnectedAsync);

    private void RunHardPriorityInboundLifecycle(string operation, Func<Task> work)
    {
        _ = Task.Run(
            () => RunInboundDispatchAsync("lifecycle_priority", operation, work),
            CancellationToken.None);
    }

    private void EnqueueInboundLifecycle(string operation, Func<Task> work)
    {
        lock (inboundLifecycleDispatchGate)
        {
            inboundLifecycleTail = inboundLifecycleTail
                .ContinueWith(
                    static (_, state) => ((InboundDispatchWork)state!).Service.RunInboundDispatchAsync(
                        ((InboundDispatchWork)state!).Lane,
                        ((InboundDispatchWork)state!).Operation,
                        ((InboundDispatchWork)state!).Work),
                    new InboundDispatchWork(this, "lifecycle", operation, work),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private void EnqueueInboundControl(string operation, Func<Task> work)
    {
        Task lifecycleBarrier;
        lock (inboundLifecycleDispatchGate)
        {
            lifecycleBarrier = inboundLifecycleTail;
        }

        lock (inboundControlDispatchGate)
        {
            inboundControlTail = Task.WhenAll(inboundControlTail, lifecycleBarrier)
                .ContinueWith(
                    static async (_, state) =>
                    {
                        var dispatch = (InboundDispatchWork)state!;
                        await dispatch.Service.RunInboundDispatchAsync(dispatch.Lane, dispatch.Operation, dispatch.Work).ConfigureAwait(false);
                    },
                    new InboundDispatchWork(this, "control", operation, work),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private void EnqueueInboundChunk(string operation, Func<Task> work)
    {
        Task lifecycleBarrier;
        lock (inboundLifecycleDispatchGate)
        {
            lifecycleBarrier = inboundLifecycleTail;
        }

        lock (inboundChunkDispatchGate)
        {
            inboundChunkTail = Task.WhenAll(inboundChunkTail, lifecycleBarrier)
                .ContinueWith(
                    static async (_, state) =>
                    {
                        var dispatch = (InboundDispatchWork)state!;
                        await dispatch.Service.RunInboundDispatchAsync(dispatch.Lane, dispatch.Operation, dispatch.Work).ConfigureAwait(false);
                    },
                    new InboundDispatchWork(this, "chunk", operation, work),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task RunInboundDispatchAsync(string lane, string operation, Func<Task> work)
    {
        try
        {
            if (InboundDispatchBeforeWorkAsyncForTests is not null)
            {
                await InboundDispatchBeforeWorkAsyncForTests(lane, operation).ConfigureAwait(false);
            }

            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Warn($"{lane} {operation} handler failed: {ex.Message}");
        }
    }

    private Task HandleTransportRejectedAsync()
        => HandleTransportPeerDownAsync("transport_rejected", deferActiveV6Session: false);

    private Task HandleTransportDisconnectedAsync()
        => HandleTransportPeerDownAsync("transport_disconnected", deferActiveV6Session: true);

    private async Task HandleTransportPeerDownAsync(string reason, bool deferActiveV6Session)
    {
        OutboundTransferContext? outboundToFail;
        InboundTransferContext? inboundToFail;
        OutboundTransferContext? outboundDeferred;
        InboundTransferContext? inboundDeferred;
        lock (gate)
        {
            outboundToFail = null;
            inboundToFail = null;
            outboundDeferred = null;
            inboundDeferred = null;

            if (outboundTransfer is { IsTerminal: false } outbound)
            {
                if (deferActiveV6Session && ShouldDeferV6TransportDisconnectedTerminalizationLocked(outbound))
                {
                    outboundDeferred = outbound;
                }
                else
                {
                    outboundToFail = outbound;
                }
            }

            if (inboundTransfer is { IsTerminal: false } inbound)
            {
                if (deferActiveV6Session && ShouldDeferV6TransportDisconnectedTerminalizationLocked(inbound))
                {
                    inboundDeferred = inbound;
                }
                else
                {
                    inboundToFail = inbound;
                }
            }
        }

        if (outboundDeferred is not null)
        {
            LogV6PeerDisconnectDeferred(FileTransferDirection.Outbound, outboundDeferred, reason);
        }

        if (inboundDeferred is not null)
        {
            LogV6PeerDisconnectDeferred(FileTransferDirection.Inbound, inboundDeferred, reason);
        }

        if (outboundToFail is not null)
        {
            await TransitionOutboundToTerminalAsync(
                outboundToFail,
                FileTransferTransferState.Failed,
                errorCode: DisconnectedErrorCode,
                statusMessage: "Peer disconnected.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_terminalized_by_peer_down; direction=outbound; transfer_id={outboundToFail.TransferId}; session_id={outboundToFail.SessionId}; reason={FormatProtocolLogValue(reason)}; terminal_state=failed");
        }

        if (inboundToFail is not null)
        {
            await TransitionInboundToTerminalAsync(
                inboundToFail,
                FileTransferTransferState.Failed,
                errorCode: DisconnectedErrorCode,
                statusMessage: "Peer disconnected.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_terminalized_by_peer_down; direction=inbound; transfer_id={inboundToFail.TransferId}; session_id={inboundToFail.SessionId}; reason={FormatProtocolLogValue(reason)}; terminal_state=failed");
        }
    }

    private static bool ShouldDeferV6TransportDisconnectedTerminalizationLocked(OutboundTransferContext context)
    {
        if (context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6)
        {
            return false;
        }

        return context.PullSessionActive ||
               ShouldDeferPostTunaFallbackTransportDisconnectedTerminalizationLocked(
                   context.RouteSelection,
                   context.RouteRuntime,
                   context.CurrentTransferLeg);
    }

    private static bool ShouldDeferV6TransportDisconnectedTerminalizationLocked(InboundTransferContext context)
    {
        if (context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6)
        {
            return false;
        }

        return context.PullSessionActive ||
               ShouldDeferPostTunaFallbackTransportDisconnectedTerminalizationLocked(
                   context.RouteSelection,
                   context.RouteRuntime,
                   context.CurrentTransferLeg);
    }

    private static bool ShouldDeferPostTunaFallbackTransportDisconnectedTerminalizationLocked(
        FileTransferRouteSelection routeSelection,
        FileTransferRouteRuntimeDescriptor routeRuntime,
        FileTransferLeg? currentLeg)
    {
        if (routeSelection.Route != FileTransferRoute.PostTunaFallbackV6 &&
            !routeRuntime.UsesPostTunaFallbackV6Runtime)
        {
            return false;
        }

        return currentLeg is null ||
               currentLeg.State is not FileTransferLegState.Frozen and not FileTransferLegState.Terminal;
    }

    private static void LogV6PeerDisconnectDeferred(FileTransferDirection direction, OutboundTransferContext context, string reason)
        => LogV6PeerDisconnectDeferred(direction, context.TransferId, context.SessionId, context.V6TransportEpoch, context.PullTransportPauseReason, reason);

    private static void LogV6PeerDisconnectDeferred(FileTransferDirection direction, InboundTransferContext context, string reason)
        => LogV6PeerDisconnectDeferred(direction, context.TransferId, context.SessionId, context.V6TransportEpoch, context.PullTransportPauseReason, reason);

    private static void LogV6PeerDisconnectDeferred(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        V6TransportEpoch? epoch,
        string? pauseReason,
        string reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_peer_disconnect_deferred_for_epoch; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason)}; transport_epoch={epoch?.EpochId ?? 0}; epoch_state={FormatProtocolLogValue(epoch is null ? null : FormatV6TransportEpochState(epoch.State))}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch?.Kind ?? FileTransferTransportHandoffKind.None)}; target_transport={FormatFileTransferTransportKind(epoch?.TargetTransport ?? FileTransferTransportKind.Unknown)}; pause_reason={FormatProtocolLogValue(pauseReason)}");
    }

    private async Task HandleIncomingOfferAsync(FileTransferOfferV2 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!IsNegotiableDataProtocolVersion(message.PreferredDataProtocolVersion))
        {
            LogLegacyNegotiationRejected(
                message.TransferId,
                message.SessionId,
                FileTransferDirection.Inbound,
                offeredVersion: message.PreferredDataProtocolVersion,
                acceptedVersion: null,
                reason: "offer_protocol_not_supported");
            await SendDeclineAsync(message.SessionId, message.TransferId, TransportIncompatibleErrorCode, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        IFileTransferSignalingTransport? currentTransport;
        lock (gate)
        {
            currentTransport = transport;
        }

        var routeInput = CreateInitialRouteResolverInputForNewTransfer(currentTransport);
        var initialTunaEvidence = CaptureInitialTunaReadinessEvidence(
            currentTransport,
            message.SessionId,
            message.TransferId,
            FileTransferDirection.Inbound);
        if (!TryResolveIncomingOfferRouteSelection(message, routeInput, out var routeSelection, out var routeRejectReason))
        {
            LogLegacyNegotiationRejected(
                message.TransferId,
                message.SessionId,
                FileTransferDirection.Inbound,
                offeredVersion: message.PreferredDataProtocolVersion,
                acceptedVersion: null,
                reason: routeRejectReason);
            await SendDeclineAsync(message.SessionId, message.TransferId, TransportIncompatibleErrorCode, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        InboundTransferContext? busyTransfer = null;
        InboundTransferContext? duplicateTransfer = null;
        FileTransferTransferState duplicateState = default;
        SessionFileTransferSnapshot? snapshotToRaise = null;
        var incoming = new InboundTransferContext(message, routeSelection);
        ApplyFileTransferRuntimeProfileSelectionLocked(
            incoming,
            FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection));
        if (EnsureInboundTransferLegLocked(incoming, "offer_received", initialTunaEvidence) is null)
        {
            LogLegacyNegotiationRejected(
                message.TransferId,
                message.SessionId,
                FileTransferDirection.Inbound,
                offeredVersion: message.PreferredDataProtocolVersion,
                acceptedVersion: null,
                reason: "offer_route_not_active");
            await SendDeclineAsync(message.SessionId, message.TransferId, TransportIncompatibleErrorCode, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        routeInput = CreateRouteInputForPublishedSelection(incoming.RouteSelection, routeInput.TransportProfileKind);

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (inboundTransfer is not null && !inboundTransfer.IsTerminal)
            {
                if (string.Equals(inboundTransfer.TransferId, incoming.TransferId, StringComparison.Ordinal))
                {
                    duplicateTransfer = inboundTransfer;
                    duplicateState = inboundTransfer.State;
                }
                else
                {
                    busyTransfer = inboundTransfer;
                }
            }
            else
            {
                inboundTransfer = incoming;
                snapshotToRaise = CreateSnapshotLocked();
            }
        }

        if (snapshotToRaise is not null)
        {
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, incoming) && !incoming.IsTerminal)
                {
                    TryPublishRouteSelectedIfAuthorizedLocked(
                        incoming,
                        routeInput,
                        "offer_received",
                        "offer_received");
                }
            }
            LogTransferInfo(
                "offer_received",
                FileTransferDirection.Inbound,
                incoming.TransferId,
                sessionId: incoming.SessionId,
                fileName: incoming.FileName,
                fileSizeBytes: incoming.FileSizeBytes);
            RaiseTransferChanged(snapshotToRaise);
            return;
        }

        if (duplicateTransfer is not null)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_offer_duplicate_ignored; direction=inbound; transfer_id={duplicateTransfer.TransferId}; session_id={duplicateTransfer.SessionId}; state={FormatProtocolLogValue(duplicateState.ToString())}; route={duplicateTransfer.RouteSelection.TelemetryToken}; protocol_version={duplicateTransfer.RouteSelection.ProtocolVersion}");
            if (duplicateState != FileTransferTransferState.PendingDecision)
            {
                await TryResendAcceptForDuplicateOfferAsync(duplicateTransfer, "duplicate_offer").ConfigureAwait(false);
            }

            return;
        }

        if (busyTransfer is not null)
        {
            await SendDeclineAsync(message.SessionId, message.TransferId, BusyReason, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task TryResendAcceptForDuplicateOfferAsync(InboundTransferContext context, string reason)
    {
        FileTransferAcceptV1? acceptMessage = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.State == FileTransferTransferState.PendingDecision)
            {
                return;
            }

            var acceptedProtocolVersion = context.NegotiatedDataProtocolVersion > 0
                ? context.NegotiatedDataProtocolVersion
                : context.RouteSelection.ProtocolVersion;
            acceptMessage = new FileTransferAcceptV1
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                AcceptedDataProtocolVersion = acceptedProtocolVersion,
                FileTransferRoute = context.RouteSelection.TelemetryToken,
            };
        }

        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferAcceptAsync(acceptMessage, CancellationToken.None).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_accept_redundant_sent; direction=inbound; transfer_id={acceptMessage.TransferId}; session_id={acceptMessage.SessionId}; reason={FormatProtocolLogValue(reason)}; route={FormatProtocolLogValue(acceptMessage.FileTransferRoute)}; protocol_version={acceptMessage.AcceptedDataProtocolVersion}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_accept_redundant_send_failed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; error={ex.GetType().Name}");
        }
    }

    private async Task HandleIncomingAcceptAsync(FileTransferAcceptV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        var acceptedProtocolVersion = message.AcceptedDataProtocolVersion;
        var acceptedVersionIsNegotiable = IsNegotiableDataProtocolVersion(acceptedProtocolVersion);
        var acceptedVersionMatchesOffer = false;
        var acceptedRouteMatchesOffer = false;
        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.AwaitingAcceptance ||
                context.SendStarted ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return;
            }

            acceptedVersionMatchesOffer = acceptedProtocolVersion == context.OfferedDataProtocolVersion;
            acceptedRouteMatchesOffer =
                acceptedProtocolVersion is { } normalizedAcceptedProtocolVersion &&
                IsRouteTokenCompatibleWithSelection(
                    message.FileTransferRoute,
                    normalizedAcceptedProtocolVersion,
                    context.RouteSelection);
            if (!acceptedVersionIsNegotiable || !acceptedVersionMatchesOffer || !acceptedRouteMatchesOffer || acceptedProtocolVersion is null)
            {
                // Leave SendStarted false so the transfer remains visibly rejected by negotiation,
                // not partially started.
            }
            else
            {
                context.SendStarted = true;
                context.SessionId = message.SessionId;
                context.NegotiatedDataProtocolVersion = acceptedProtocolVersion.Value;
                context.State = FileTransferTransferState.PreparingMetadata;
                context.StatusMessage = "Preparing file metadata.";
            }
        }

        if (!acceptedVersionIsNegotiable || !acceptedVersionMatchesOffer || !acceptedRouteMatchesOffer || acceptedProtocolVersion is null)
        {
            LogLegacyNegotiationRejected(
                message.TransferId,
                message.SessionId,
                FileTransferDirection.Outbound,
                offeredVersion: context.OfferedDataProtocolVersion,
                acceptedVersion: acceptedProtocolVersion,
                reason: !acceptedVersionIsNegotiable
                    ? "accept_protocol_not_supported"
                    : acceptedVersionMatchesOffer
                        ? "accept_route_mismatch"
                        : "accept_protocol_mismatch");
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: TransportIncompatibleErrorCode,
                statusMessage: "Receiver did not accept the offered file-transfer protocol.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        LogTransferInfo(
            "accept_received",
            FileTransferDirection.Outbound,
            message.TransferId,
            sessionId: message.SessionId);
        if (context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6)
        {
            StartOutboundV6HeartbeatLoop(context, "accept_received");
        }
        RaiseTransferChanged(CreateSnapshot());
        _ = PrepareAndStartAcceptedOutboundTransferAsync(context);
    }

    private Task HandleIncomingDeclineAsync(FileTransferDeclineV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }
        }

        LogTransferInfo(
            "decline_received",
            FileTransferDirection.Outbound,
            message.TransferId,
            sessionId: message.SessionId,
            reason: NormalizeReason(message.Reason));
        return TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Declined,
            errorCode: NormalizeReason(message.Reason) == FileTransferResultCodes.Busy ? FileTransferResultCodes.Busy : null,
                statusMessage: NormalizeReason(message.Reason) ?? "Transfer declined.",
                notifyPeer: false,
                cancelReason: null,
            ct: CancellationToken.None);
    }

    private async Task HandleIncomingSessionOpenAsync(FileTransferSessionOpenV2 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        InboundTransferContext? context;
        InboundTransferContext? rejectedContext = null;
        string? sessionOpenRejectReason = null;
        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal) ||
                !string.Equals(message.SessionRole, FileTransferProtocol.SessionRoleSender, StringComparison.Ordinal) ||
                message.ProtocolVersion != context.NegotiatedDataProtocolVersion ||
                !IsNegotiableDataProtocolVersion(message.ProtocolVersion) ||
                !IsRouteTokenCompatibleWithSelection(message.FileTransferRoute, message.ProtocolVersion, context.RouteSelection))
            {
                rejectedContext = context;
                sessionOpenRejectReason = !IsNegotiableDataProtocolVersion(message.ProtocolVersion)
                    ? "session_open_protocol_not_supported"
                    : message.ProtocolVersion != context.NegotiatedDataProtocolVersion
                        ? "session_open_protocol_mismatch"
                        : "session_open_route_mismatch";
                context = null;
            }
            else if (context.State is not FileTransferTransferState.AwaitingMetadata and not FileTransferTransferState.Receiving)
            {
                context = null;
            }
        }

        if (rejectedContext is not null)
        {
            LogLegacyNegotiationRejected(
                message.TransferId,
                message.SessionId,
                FileTransferDirection.Inbound,
                offeredVersion: message.ProtocolVersion,
                acceptedVersion: rejectedContext.NegotiatedDataProtocolVersion,
                reason: sessionOpenRejectReason ?? "session_open_route_mismatch");
            LogV6SessionOpenRejected(
                message.TransferId,
                message.SessionId,
                FileTransferDirection.Inbound,
                message.ProtocolVersion,
                sessionOpenRejectReason ?? "session_open_route_mismatch");
            await TransitionInboundToTerminalAsync(
                rejectedContext,
                FileTransferTransferState.Failed,
                errorCode: TransportIncompatibleErrorCode,
                statusMessage: "Sender opened a file-transfer route that does not match the accepted route.",
                sendError: true,
                errorMessage: "Sender opened a file-transfer route that does not match the accepted route.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (context is null)
        {
            return;
        }

        if (context.RouteRuntime.UsesV4SparsePump)
        {
            LogV4Negotiated(message.TransferId, message.SessionId, FileTransferDirection.Inbound, context.RouteSelection);
            if ((sessionScreenShareActive || sessionScreenShareDegraded) && !ShouldAllowV4DuringScreenShare())
            {
                await TransitionInboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: V4FileOnlyRequiredErrorCode,
                    statusMessage: "V4 file-transfer receive is currently file-only.",
                    sendError: true,
                    errorMessage: "V4 file-transfer receive is currently file-only.",
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
                return;
            }

            LogV4MixedScreenShareEnabled(message.TransferId, message.SessionId, FileTransferDirection.Inbound, context.RouteSelection);
            try
            {
                var session = await GetTransportOrThrow()
                    .OpenFileTransferDataSessionAsync(message.SessionId, message.TransferId, context.LifetimeCts.Token)
                    .ConfigureAwait(false);

                lock (gate)
                {
                    if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                    {
                        session.Dispose();
                        return;
                    }

                    ReplaceInboundDataSessionLocked(context, session);
                    context.PullSessionActive = true;
                    context.NegotiatedDataProtocolVersion = context.RouteSelection.ProtocolVersion;
                    context.StatusMessage = "Waiting for V4 file-transfer manifest.";
                }

                LogTransferInfo(
                    "filetransfer_session_opened",
                    FileTransferDirection.Inbound,
                    message.TransferId,
                    sessionId: message.SessionId,
                    reason: FormatFileTransferSessionOpenReason(message.SessionRole, message.ProtocolVersion, message.ChunkSizeBytes, message.InitialPipelineDepth, context.RouteSelection),
                    routeSelection: context.RouteSelection);
                _ = RunInboundRegularNknV4Async(context, message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await TransitionInboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: InvalidStateErrorCode,
                    statusMessage: ex.Message,
                    sendError: true,
                    errorMessage: "Could not open the dedicated V4 file-transfer session.",
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        if (context.RouteRuntime.UsesV6RequestPump)
        {
            LogV6Negotiated(message.TransferId, message.SessionId, FileTransferDirection.Inbound, context.RouteSelection);
            if ((sessionScreenShareActive || sessionScreenShareDegraded) && !ShouldAllowV4DuringScreenShare())
            {
                LogV6SessionOpenRejected(
                    message.TransferId,
                    message.SessionId,
                    FileTransferDirection.Inbound,
                    message.ProtocolVersion,
                    "file_only_required");
                await TransitionInboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: V4FileOnlyRequiredErrorCode,
                    statusMessage: "V6 file-transfer receive is currently file-only.",
                    sendError: true,
                    errorMessage: "V6 file-transfer receive is currently file-only.",
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
                return;
            }

            LogV4MixedScreenShareEnabled(message.TransferId, message.SessionId, FileTransferDirection.Inbound, context.RouteSelection);
            try
            {
                var session = await GetTransportOrThrow()
                    .OpenFileTransferDataSessionAsync(message.SessionId, message.TransferId, context.LifetimeCts.Token)
                    .ConfigureAwait(false);

                lock (gate)
                {
                    if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                    {
                        session.Dispose();
                        return;
                    }

                    ReplaceInboundDataSessionLocked(context, session);
                    context.PullSessionActive = true;
                    context.NegotiatedDataProtocolVersion = context.RouteSelection.ProtocolVersion;
                    context.StatusMessage = "Waiting for V6 file-transfer manifest.";
                }

                LogTransferInfo(
                    "filetransfer_session_opened",
                    FileTransferDirection.Inbound,
                    message.TransferId,
                    sessionId: message.SessionId,
                    reason: FormatFileTransferSessionOpenReason(message.SessionRole, message.ProtocolVersion, message.ChunkSizeBytes, message.InitialPipelineDepth, context.RouteSelection),
                    routeSelection: context.RouteSelection);
                StartInboundV6HeartbeatLoop(context, "session_open_received");
                var runtimeSelection = ResolveFileTransferRuntimeProfile(context);
                LogFileTransferBridgeRecoveryPolicySelected(
                    context.TransferId,
                    context.SessionId,
                    FileTransferDirection.Inbound,
                    runtimeSelection,
                    context.RouteSelection);
                _ = RunInboundV6ReceiverAsync(context, message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await TransitionInboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: InvalidStateErrorCode,
                    statusMessage: ex.Message,
                    sendError: true,
                    errorMessage: "Could not open the dedicated V6 file-transfer session.",
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        if (context.RouteRuntime.UsesV6SparsePump)
        {
            LogV6Negotiated(message.TransferId, message.SessionId, FileTransferDirection.Inbound, context.RouteSelection);
            if ((sessionScreenShareActive || sessionScreenShareDegraded) && !ShouldAllowV4DuringScreenShare())
            {
                LogV6SessionOpenRejected(
                    message.TransferId,
                    message.SessionId,
                    FileTransferDirection.Inbound,
                    message.ProtocolVersion,
                    "file_only_required");
                await TransitionInboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: V4FileOnlyRequiredErrorCode,
                    statusMessage: "V6 file-transfer receive is currently file-only.",
                    sendError: true,
                    errorMessage: "V6 file-transfer receive is currently file-only.",
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
                return;
            }

            LogV4MixedScreenShareEnabled(message.TransferId, message.SessionId, FileTransferDirection.Inbound, context.RouteSelection);
            try
            {
                var session = await GetTransportOrThrow()
                    .OpenFileTransferDataSessionAsync(message.SessionId, message.TransferId, context.LifetimeCts.Token)
                    .ConfigureAwait(false);

                lock (gate)
                {
                    if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                    {
                        session.Dispose();
                        return;
                    }

                    ReplaceInboundDataSessionLocked(context, session);
                    context.PullSessionActive = true;
                    context.NegotiatedDataProtocolVersion = context.RouteSelection.ProtocolVersion;
                    context.StatusMessage = "Waiting for V6 file-transfer manifest.";
                }

                LogTransferInfo(
                    "filetransfer_session_opened",
                    FileTransferDirection.Inbound,
                    message.TransferId,
                    sessionId: message.SessionId,
                    reason: FormatFileTransferSessionOpenReason(message.SessionRole, message.ProtocolVersion, message.ChunkSizeBytes, message.InitialPipelineDepth, context.RouteSelection),
                    routeSelection: context.RouteSelection);
                StartInboundV6HeartbeatLoop(context, "session_open_received");
                var runtimeSelection = ResolveFileTransferRuntimeProfile(context);
                LogFileTransferBridgeRecoveryPolicySelected(
                    context.TransferId,
                    context.SessionId,
                    FileTransferDirection.Inbound,
                    runtimeSelection,
                    context.RouteSelection);
                LogPrimaryRegularNknBulkV6Selected(context.TransferId, context.SessionId, FileTransferDirection.Inbound, runtimeSelection, context.RouteSelection);
                _ = RunInboundPrimaryRegularNknBulkV6Async(context, message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await TransitionInboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: InvalidStateErrorCode,
                    statusMessage: ex.Message,
                    sendError: true,
                    errorMessage: "Could not open the dedicated V6 file-transfer session.",
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }
    }

    private void OnDataSessionAvailabilityChanged(object? sender, FileTransferDataSessionAvailabilityChangedEventArgs e)
    {
        if (sender is not IFileTransferDataSession dataSession)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_session_availability_observed; session_id={dataSession.SessionId}; transfer_id={dataSession.TransferId}; is_available={(e.IsAvailable ? 1 : 0)}; reason={FormatProtocolLogValue(e.Reason)}; requires_resume_request={(e.RequiresResumeRequest ? 1 : 0)}; handoff_kind={FormatFileTransferTransportHandoffKind(e.HandoffKind)}; target_transport={FormatFileTransferTransportKind(e.TargetTransport)}");

        RunHardPriorityInboundLifecycle(
            "data_session_availability",
            () => HandleDataSessionAvailabilityChangedAsync(dataSession, e));
    }

    private async Task HandleDataSessionAvailabilityChangedAsync(
        IFileTransferDataSession dataSession,
        FileTransferDataSessionAvailabilityChangedEventArgs availability)
    {
        if (dataSession.IsAvailable != availability.IsAvailable)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_availability_stale_ignored; session_id={dataSession.SessionId}; transfer_id={dataSession.TransferId}; observed_available={(availability.IsAvailable ? 1 : 0)}; current_available={(dataSession.IsAvailable ? 1 : 0)}; reason={FormatProtocolLogValue(availability.Reason)}; requires_resume_request={(availability.RequiresResumeRequest ? 1 : 0)}; handoff_kind={FormatFileTransferTransportHandoffKind(availability.HandoffKind)}; target_transport={FormatFileTransferTransportKind(availability.TargetTransport)}");
            return;
        }

        var effectiveReason = availability.Reason;
        var effectiveIsAvailable = availability.IsAvailable;
        var effectiveRequiresResumeRequest = availability.RequiresResumeRequest;
        var effectiveHandoffKind = availability.HandoffKind;
        var effectiveTargetTransport = availability.TargetTransport;
        if (!availability.IsAvailable &&
            IsTerminalControlChannelStallReason(availability.Reason))
        {
            if (ShouldRecoverV6TransferFromControlChannelStall(dataSession, availability.Reason))
            {
                effectiveIsAvailable = true;
                effectiveRequiresResumeRequest = true;
                effectiveHandoffKind = FileTransferTransportHandoffKind.RegularNknRecovery;
                effectiveTargetTransport = FileTransferTransportKind.RegularNkn;
            }
            else
            {
                await TerminalizeTransfersForControlChannelStallAsync(dataSession, availability.Reason).ConfigureAwait(false);
                return;
            }
        }

        OutboundTransferContext? outboundToResume = null;
        InboundTransferContext? inboundToResume = null;
        string? outboundPausedTransferId = null;
        string? outboundPausedSessionId = null;
        OutboundTransferContext? outboundPaused = null;
        string? inboundPausedTransferId = null;
        string? inboundPausedSessionId = null;
        InboundTransferContext? inboundPaused = null;
        bool outboundResumed = false;
        bool inboundResumed = false;
        bool outboundActivationPauseStarted = false;
        bool inboundActivationPauseStarted = false;
        bool outboundActivationPauseResumed = false;
        bool inboundActivationPauseResumed = false;
        bool outboundNormalToTunaActivationResumed = false;
        bool inboundNormalToTunaActivationResumed = false;
        bool outboundEpochStartedWhileUnavailable = false;
        bool inboundEpochStartedWhileUnavailable = false;

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer?.DataSession, dataSession) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                if (effectiveIsAvailable)
                {
                    var wasActivationPause =
                        outbound.PullTransportPaused &&
                        IsTunaActivationNegotiationTransportPauseReason(outbound.PullTransportPauseReason);
                    var wasNormalToTunaActivation =
                        effectiveHandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                        effectiveTargetTransport == FileTransferTransportKind.Tuna;
                    outboundResumed = TryResumeOutboundTransportLocked(
                        outbound,
                        effectiveReason,
                        effectiveRequiresResumeRequest,
                        effectiveHandoffKind,
                        effectiveTargetTransport);
                    if (outboundResumed)
                    {
                        outboundToResume = outbound;
                        outboundActivationPauseResumed = wasActivationPause;
                        outboundNormalToTunaActivationResumed =
                            wasNormalToTunaActivation &&
                            outbound.RouteRuntime.UsesFileTunaV4Runtime &&
                            outbound.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4;
                    }
                }
                else
                {
                    if (TryPauseOutboundTransportLocked(outbound, effectiveReason, effectiveRequiresResumeRequest))
                    {
                        outboundPausedTransferId = outbound.TransferId;
                        outboundPausedSessionId = outbound.SessionId;
                        outboundPaused = outbound;
                        outboundActivationPauseStarted = IsTunaActivationNegotiationTransportPauseReason(effectiveReason);
                    }

                    if (TryStartOutboundV6TransportEpochWhileUnavailableLocked(
                            outbound,
                            effectiveReason,
                            effectiveRequiresResumeRequest,
                            effectiveHandoffKind,
                            effectiveTargetTransport))
                    {
                        outboundEpochStartedWhileUnavailable = true;
                        outboundToResume = outbound;
                    }
                    else if (!outbound.PullTransportPaused)
                    {
                        outboundPausedTransferId = null;
                        outboundPausedSessionId = null;
                        outboundPaused = null;
                        outboundActivationPauseStarted = false;
                    }
                }
            }

            if (ReferenceEquals(inboundTransfer?.DataSession, dataSession) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                if (effectiveIsAvailable)
                {
                    var wasActivationPause =
                        inbound.PullTransportPaused &&
                        IsTunaActivationNegotiationTransportPauseReason(inbound.PullTransportPauseReason);
                    var wasNormalToTunaActivation =
                        effectiveHandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                        effectiveTargetTransport == FileTransferTransportKind.Tuna;
                    inboundResumed = TryResumeInboundTransportLocked(
                        inbound,
                        effectiveReason,
                        effectiveRequiresResumeRequest,
                        effectiveHandoffKind,
                        effectiveTargetTransport);
                    if (inboundResumed)
                    {
                        inboundToResume = inbound;
                        inboundActivationPauseResumed = wasActivationPause;
                        inboundNormalToTunaActivationResumed =
                            wasNormalToTunaActivation &&
                            inbound.RouteRuntime.UsesFileTunaV4Runtime &&
                            inbound.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4;
                    }
                }
                else
                {
                    if (TryPauseInboundTransportLocked(inbound, effectiveReason, effectiveRequiresResumeRequest))
                    {
                        inboundPausedTransferId = inbound.TransferId;
                        inboundPausedSessionId = inbound.SessionId;
                        inboundPaused = inbound;
                        inboundActivationPauseStarted = IsTunaActivationNegotiationTransportPauseReason(effectiveReason);
                    }

                    if (TryStartInboundV6TransportEpochWhileUnavailableLocked(
                            inbound,
                            effectiveReason,
                            effectiveRequiresResumeRequest,
                            effectiveHandoffKind,
                            effectiveTargetTransport))
                    {
                        inboundEpochStartedWhileUnavailable = true;
                        inboundToResume = inbound;
                    }
                    else if (!inbound.PullTransportPaused)
                    {
                        inboundPausedTransferId = null;
                        inboundPausedSessionId = null;
                        inboundPaused = null;
                        inboundActivationPauseStarted = false;
                    }
                }
            }
        }

        if (outboundPausedTransferId is not null && outboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Outbound, outboundPausedTransferId, outboundPausedSessionId, effectiveReason);
            if (outboundPaused is not null)
            {
                if (outboundActivationPauseStarted)
                {
                    ScheduleOutboundV4TransportPauseControlRetry(
                        outboundPaused,
                        paused: true,
                        effectiveReason,
                        localTunaActivationBarrier: true);
                }

                SignalOutboundSparseSenderPump(outboundPaused);
            }
        }

        if (inboundPausedTransferId is not null && inboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Inbound, inboundPausedTransferId, inboundPausedSessionId, effectiveReason);
            if (inboundActivationPauseStarted && inboundPaused is not null)
            {
                ScheduleInboundV4TransportPauseControlRetry(
                    inboundPaused,
                    paused: true,
                    effectiveReason,
                    localTunaActivationBarrier: true);
            }
        }

        if (outboundResumed && outboundToResume is not null)
        {
            LogTransportResumed(FileTransferDirection.Outbound, outboundToResume.TransferId, outboundToResume.SessionId, effectiveReason, effectiveRequiresResumeRequest);
            if (outboundActivationPauseResumed)
            {
                ScheduleOutboundV4TransportPauseControlRetry(
                    outboundToResume,
                    paused: false,
                    effectiveReason,
                    localTunaActivationBarrier: true);
            }

            if (effectiveRequiresResumeRequest &&
                !outboundActivationPauseResumed &&
                !outboundNormalToTunaActivationResumed)
            {
                IFileTransferDataSession? checkpointDataSession = null;
                FileTransferFrontierRequestFrameV6? checkpointRequest = null;
                var primaryRegularNknBulkV6Rebind = false;
                lock (gate)
                {
                    primaryRegularNknBulkV6Rebind =
                        ReferenceEquals(outboundTransfer, outboundToResume) &&
                        !outboundToResume.IsTerminal &&
                        IsPrimaryRegularNknBulkV6ContextLocked(outboundToResume);
                    if (primaryRegularNknBulkV6Rebind)
                    {
                        checkpointDataSession = outboundToResume.DataSession;
                        checkpointRequest = CreateOutboundPrimaryRegularNknBulkV6CheckpointSyncRequestLocked(
                            outboundToResume,
                            DateTimeOffset.UtcNow,
                            TimeSpan.Zero,
                            Math.Max(0, outboundToResume.ChunksAcceptedForTransport - outboundToResume.RemoteNextExpectedChunkIndex),
                            Math.Max(0, Math.Min(outboundToResume.RemoteGrantedUntilExclusive, outboundToResume.ChunkCount) - outboundToResume.ChunksAcceptedForTransport),
                            Math.Min(outboundToResume.RemoteGrantedUntilExclusive, outboundToResume.ChunkCount),
                            Math.Max(0, outboundToResume.PullSenderPipelineCurrentInFlightFrames),
                            "after_rebind");
                    }
                }

                if (primaryRegularNknBulkV6Rebind && checkpointDataSession is not null && checkpointRequest is not null)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_primary_regular_nkn_bulk_v6_rebind_started; direction=outbound; transfer_id={outboundToResume.TransferId}; session_id={outboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={outboundToResume.PullTransportRebindGeneration}; remote_committed_chunk={outboundToResume.RemoteNextExpectedChunkIndex}; highest_sent_chunk={Math.Max(-1, outboundToResume.ChunksAcceptedForTransport - 1)}");
                    QueueOutboundPrimaryRegularNknBulkV6CheckpointSync(
                        outboundToResume,
                        checkpointDataSession,
                        checkpointRequest);
                }
                else
                {
                    if (outboundToResume.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6)
                    {
                        StartOutboundV6HeartbeatLoop(outboundToResume, "transport_rebind");
                    }

                    await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundToResume).ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_transport_rebind_generation_started; direction=outbound; transfer_id={outboundToResume.TransferId}; session_id={outboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={outboundToResume.PullTransportRebindGeneration}; remote_committed_chunk={outboundToResume.RemoteNextExpectedChunkIndex}; highest_sent_chunk={Math.Max(-1, outboundToResume.ChunksAcceptedForTransport - 1)}");
                }

                SignalOutboundSparseSenderPump(outboundToResume);
            }
            else if (outboundNormalToTunaActivationResumed)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_transport_rebind_skipped_for_tuna_activation; direction=outbound; transfer_id={outboundToResume.TransferId}; session_id={outboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; route={FormatProtocolLogValue(outboundToResume.RouteSelection.TelemetryToken)}; protocol_version={outboundToResume.NegotiatedDataProtocolVersion}");
                SignalOutboundSparseSenderPump(outboundToResume);
            }
            else if (outboundActivationPauseResumed)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_tuna_activation_local_barrier_released; direction=outbound; transfer_id={outboundToResume.TransferId}; session_id={outboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; route={FormatProtocolLogValue(outboundToResume.RouteSelection.TelemetryToken)}; protocol_version={outboundToResume.NegotiatedDataProtocolVersion}");
                SignalOutboundSparseSenderPump(outboundToResume);
            }

            QueueDeferredOutboundPostTunaFallbackStateRefreshAfterResume(outboundToResume, effectiveReason);
        }
        else if (outboundEpochStartedWhileUnavailable && outboundToResume is not null)
        {
            if (outboundToResume.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6)
            {
                StartOutboundV6HeartbeatLoop(outboundToResume, "transport_epoch_started_while_unavailable");
            }

            await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundToResume).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id={outboundToResume.TransferId}; session_id={outboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={outboundToResume.PullTransportRebindGeneration}; target_transport={FormatFileTransferTransportKind(effectiveTargetTransport)}");
            SignalOutboundSparseSenderPump(outboundToResume);
        }

        if (inboundResumed && inboundToResume is not null)
        {
            LogTransportResumed(FileTransferDirection.Inbound, inboundToResume.TransferId, inboundToResume.SessionId, effectiveReason, effectiveRequiresResumeRequest);
            if (inboundActivationPauseResumed)
            {
                ScheduleInboundV4TransportPauseControlRetry(
                    inboundToResume,
                    paused: false,
                    effectiveReason,
                    localTunaActivationBarrier: true);
            }

            if (effectiveRequiresResumeRequest &&
                !inboundActivationPauseResumed &&
                !inboundNormalToTunaActivationResumed)
            {
                var primaryRegularNknBulkV6Rebind = false;
                lock (gate)
                {
                    primaryRegularNknBulkV6Rebind =
                        ReferenceEquals(inboundTransfer, inboundToResume) &&
                        !inboundToResume.IsTerminal &&
                        IsPrimaryRegularNknBulkV6ContextLocked(inboundToResume);
                }

                if (primaryRegularNknBulkV6Rebind)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_primary_regular_nkn_bulk_v6_rebind_started; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={inboundToResume.PullTransportRebindGeneration}; committed_chunk={inboundToResume.NextChunkIndex}; highest_received_chunk={inboundToResume.PullHighestReceivedChunkIndex}; missing_range_count={(inboundToResume.NextChunkIndex <= inboundToResume.PullHighestReceivedChunkIndex ? 1 : 0)}");
                    var sent = await SendInboundV4StateAsync(
                        inboundToResume,
                        V6RegularNknCheckpointSyncRecoveryMode,
                        terminalReady: false,
                        forceSend: true).ConfigureAwait(false);
                    if (sent)
                    {
                        LogPrimaryRegularNknBulkV6State(inboundToResume, PrimaryRegularNknBulkV6State.RebindConfirmed, effectiveReason);
                    }

                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_primary_regular_nkn_bulk_v6_rebind_checkpoint_confirmed; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={inboundToResume.PullTransportRebindGeneration}; checkpoint_sent={(sent ? 1 : 0)}; committed_chunk={inboundToResume.NextChunkIndex}; highest_received_chunk={inboundToResume.PullHighestReceivedChunkIndex}");
                }
                else
                {
                    if (inboundToResume.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6)
                    {
                        StartInboundV6HeartbeatLoop(inboundToResume, "transport_rebind");
                    }

                    await AnnounceAndProbeInboundV6TransportEpochAsync(inboundToResume).ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_transport_rebind_generation_started; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={inboundToResume.PullTransportRebindGeneration}; committed_chunk={inboundToResume.NextChunkIndex}; highest_received_chunk={inboundToResume.PullHighestReceivedChunkIndex}; missing_range_count={(inboundToResume.NextChunkIndex <= inboundToResume.PullHighestReceivedChunkIndex ? 1 : 0)}");
                    try
                    {
                        var sent = await MaybeSendTransportRebindStateAsync(inboundToResume).ConfigureAwait(false);

                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_transport_rebind_state_forced; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={inboundToResume.PullTransportRebindGeneration}; state_sent={(sent ? 1 : 0)}; committed_chunk={inboundToResume.NextChunkIndex}; highest_received_chunk={inboundToResume.PullHighestReceivedChunkIndex}");
                        ScheduleInboundTransportRebindRetries(inboundToResume, effectiveReason, inboundToResume.PullTransportRebindGeneration);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LocalOperationalLog.Warn(
                            "FileTransferService",
                            $"event=filetransfer_transport_rebind_failed; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={inboundToResume.PullTransportRebindGeneration}; error={FormatProtocolLogValue(ex.Message)}");
                    }
                }
            }
            else if (inboundNormalToTunaActivationResumed)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_transport_rebind_skipped_for_tuna_activation; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; route={FormatProtocolLogValue(inboundToResume.RouteSelection.TelemetryToken)}; protocol_version={inboundToResume.NegotiatedDataProtocolVersion}");
            }
            else if (inboundActivationPauseResumed)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_tuna_activation_local_barrier_released; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; route={FormatProtocolLogValue(inboundToResume.RouteSelection.TelemetryToken)}; protocol_version={inboundToResume.NegotiatedDataProtocolVersion}");
            }
        }
        else if (inboundEpochStartedWhileUnavailable && inboundToResume is not null)
        {
            if (inboundToResume.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6)
            {
                StartInboundV6HeartbeatLoop(inboundToResume, "transport_epoch_started_while_unavailable");
            }

            await AnnounceAndProbeInboundV6TransportEpochAsync(inboundToResume).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_transport_epoch_started_while_unavailable; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={inboundToResume.PullTransportRebindGeneration}; target_transport={FormatFileTransferTransportKind(effectiveTargetTransport)}");
        }
    }

    private bool ShouldRecoverV6TransferFromControlChannelStall(
        IFileTransferDataSession dataSession,
        string reason)
    {
        OutboundTransferContext? outbound = null;
        InboundTransferContext? inbound = null;
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer?.DataSession, dataSession) &&
                outboundTransfer is { IsTerminal: false, NegotiatedDataProtocolVersion: >= FileTransferProtocol.ProtocolVersionV6 } outboundCandidate)
            {
                outbound = outboundCandidate;
            }

            if (ReferenceEquals(inboundTransfer?.DataSession, dataSession) &&
                inboundTransfer is { IsTerminal: false, NegotiatedDataProtocolVersion: >= FileTransferProtocol.ProtocolVersionV6 } inboundCandidate)
            {
                inbound = inboundCandidate;
            }
        }

        if (outbound is null && inbound is null)
        {
            return false;
        }

        if (outbound is not null)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_control_channel_stalled_recovery; direction=outbound; transfer_id={outbound.TransferId}; session_id={outbound.SessionId}; reason={FormatProtocolLogValue(reason)}; action=regular_nkn_recovery_epoch");
        }

        if (inbound is not null)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_control_channel_stalled_recovery; direction=inbound; transfer_id={inbound.TransferId}; session_id={inbound.SessionId}; reason={FormatProtocolLogValue(reason)}; action=regular_nkn_recovery_epoch");
        }

        return true;
    }

    private async Task TerminalizeTransfersForControlChannelStallAsync(
        IFileTransferDataSession dataSession,
        string reason)
    {
        OutboundTransferContext? outbound = null;
        InboundTransferContext? inbound = null;
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer?.DataSession, dataSession) &&
                outboundTransfer is { IsTerminal: false } outboundCandidate)
            {
                outbound = outboundCandidate;
            }

            if (ReferenceEquals(inboundTransfer?.DataSession, dataSession) &&
                inboundTransfer is { IsTerminal: false } inboundCandidate)
            {
                inbound = inboundCandidate;
            }
        }

        if (outbound is not null)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_control_channel_stalled_terminalized; direction=outbound; transfer_id={outbound.TransferId}; session_id={outbound.SessionId}; reason={FormatProtocolLogValue(reason)}");
            await TransitionOutboundToTerminalAsync(
                outbound,
                FileTransferTransferState.Failed,
                errorCode: ControlChannelStalledErrorCode,
                statusMessage: "Connection control channel stalled.",
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }

        if (inbound is not null)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_control_channel_stalled_terminalized; direction=inbound; transfer_id={inbound.TransferId}; session_id={inbound.SessionId}; reason={FormatProtocolLogValue(reason)}");
            await TransitionInboundToTerminalAsync(
                inbound,
                FileTransferTransferState.Failed,
                errorCode: ControlChannelStalledErrorCode,
                statusMessage: "Connection control channel stalled.",
                sendError: true,
                errorMessage: "Connection control channel stalled.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool IsTerminalControlChannelStallReason(string? reason)
        => !string.IsNullOrWhiteSpace(reason) &&
           reason.Contains("control_receive_stalled", StringComparison.OrdinalIgnoreCase) &&
           reason.Contains("max_restarts", StringComparison.OrdinalIgnoreCase);

    private Task HandleIncomingCancelAsync(FileTransferCancelV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = IsOutboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId)
                ? outboundTransfer
                : null;
            inbound = IsInboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId)
                ? inboundTransfer
                : null;
        }

        if (outbound is not null)
        {
            TouchOutboundV6PeerLiveness(outbound, "cancel");
            LogTransferInfo(
                "cancel_received",
                FileTransferDirection.Outbound,
                message.TransferId,
                sessionId: message.SessionId,
                reason: NormalizeReason(message.Reason));
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_received; kind=cancel; transfer_id={message.TransferId}; session_id={message.SessionId}; direction=outbound; reason={FormatProtocolLogValue(NormalizeReason(message.Reason) ?? CanceledReason)}");
            return TransitionOutboundToTerminalAsync(
                outbound,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledRemote,
                statusMessage: NormalizeReason(message.Reason) ?? "Transfer canceled by peer.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        if (inbound is not null)
        {
            TouchInboundV6PeerLiveness(inbound, "cancel");
            LogTransferInfo(
                "cancel_received",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                reason: NormalizeReason(message.Reason));
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_received; kind=cancel; transfer_id={message.TransferId}; session_id={message.SessionId}; direction=inbound; reason={FormatProtocolLogValue(NormalizeReason(message.Reason) ?? CanceledReason)}");
            return TransitionInboundToTerminalAsync(
                inbound,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledRemote,
                statusMessage: NormalizeReason(message.Reason) ?? "Transfer canceled by peer.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private Task HandleIncomingErrorAsync(FileTransferErrorV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = IsOutboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId)
                ? outboundTransfer
                : null;
            inbound = IsInboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId)
                ? inboundTransfer
                : null;
        }

        if (outbound is not null)
        {
            TouchOutboundV6PeerLiveness(outbound, "error");
            LogTransferInfo(
                "error_received",
                FileTransferDirection.Outbound,
                message.TransferId,
                sessionId: message.SessionId,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                reason: NormalizeReason(message.Message));
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_received; kind=error; transfer_id={message.TransferId}; session_id={message.SessionId}; direction=outbound; error_code={NormalizeErrorCode(message.ErrorCode) ?? InvalidStateErrorCode}; reason={FormatProtocolLogValue(NormalizeReason(message.Message) ?? "(none)")}");
            return TransitionOutboundToTerminalAsync(
                outbound,
                FileTransferTransferState.Failed,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                statusMessage: NormalizeReason(message.Message) ?? "Transfer failed on peer.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        if (inbound is not null)
        {
            TouchInboundV6PeerLiveness(inbound, "error");
            LogTransferInfo(
                "error_received",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                reason: NormalizeReason(message.Message));
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_received; kind=error; transfer_id={message.TransferId}; session_id={message.SessionId}; direction=inbound; error_code={NormalizeErrorCode(message.ErrorCode) ?? InvalidStateErrorCode}; reason={FormatProtocolLogValue(NormalizeReason(message.Message) ?? "(none)")}");
            return TransitionInboundToTerminalAsync(
                inbound,
                FileTransferTransferState.Failed,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                statusMessage: NormalizeReason(message.Message) ?? "Transfer failed on peer.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private async Task<bool> TryHandleOutboundLifecycleErrorDataFrameAsync(
        OutboundTransferContext context,
        FileTransferErrorFrameV4 error)
    {
        var errorCode = NormalizeErrorCode(error.ErrorCode);
        if (!IsPeerDisconnectedErrorCode(errorCode))
        {
            return false;
        }

        TouchOutboundV6PeerLiveness(context, "error_data_frame");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_lifecycle_priority_received; kind=error; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=outbound; error_code={errorCode ?? InvalidStateErrorCode}; reason={FormatProtocolLogValue(NormalizeReason(error.Message) ?? "(none)")}; path=redundant_data_frame");
        await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: errorCode,
                statusMessage: NormalizeReason(error.Message) ?? "Peer disconnected.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryHandleInboundLifecycleErrorDataFrameAsync(
        InboundTransferContext context,
        FileTransferErrorFrameV4 error)
    {
        var errorCode = NormalizeErrorCode(error.ErrorCode);
        if (!IsPeerDisconnectedErrorCode(errorCode))
        {
            return false;
        }

        TouchInboundV6PeerLiveness(context, "error_data_frame");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_lifecycle_priority_received; kind=error; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=inbound; error_code={errorCode ?? InvalidStateErrorCode}; reason={FormatProtocolLogValue(NormalizeReason(error.Message) ?? "(none)")}; path=redundant_data_frame");
        await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: errorCode,
                statusMessage: NormalizeReason(error.Message) ?? "Peer disconnected.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None)
            .ConfigureAwait(false);
        return true;
    }

    private Task HandleIncomingCompleteAsync(FileTransferCompleteV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        bool completionEligible;
        lock (gate)
        {
            context = outboundTransfer;
            completionEligible = context is not null &&
                !context.IsTerminal &&
                string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) &&
                string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal) &&
                (
                    context.State == FileTransferTransferState.AwaitingCompletion ||
                    context.PullSessionActive
                ) &&
                context.FileSizeBytes == message.FileSizeBytes &&
                string.Equals(context.Sha256Base64, message.Sha256Base64, StringComparison.Ordinal);

            if (context is null ||
                !completionEligible)
            {
                return Task.CompletedTask;
            }

            context.BytesTransferred = context.FileSizeBytes;
            context.ChunksTransferred = context.ChunkCount;
            context.BytesAcceptedForTransport = context.FileSizeBytes;
            context.ChunksAcceptedForTransport = context.ChunkCount;
            context.BytesAcknowledgedByReceiver = context.FileSizeBytes;
            context.State = FileTransferTransferState.AwaitingCompletion;
            context.StatusMessage = "Waiting for receiver verification.";
        }

        LogTransferInfo(
            "complete_received",
            FileTransferDirection.Outbound,
            message.TransferId,
            sessionId: message.SessionId,
            fileSizeBytes: message.FileSizeBytes);
        TouchOutboundV6PeerLiveness(context, "complete");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_lifecycle_priority_received; kind=complete; transfer_id={message.TransferId}; session_id={message.SessionId}; direction=outbound; file_size_bytes={message.FileSizeBytes}");
        return TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Completed,
            errorCode: null,
            statusMessage: "Transfer complete.",
            notifyPeer: false,
            cancelReason: null,
            ct: CancellationToken.None);
    }

    private async Task<bool> TryHandleOutboundLifecycleCompleteDataFrameAsync(
        OutboundTransferContext context,
        FileTransferCompleteFrameV4 complete)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(complete);

        bool completionEligible;
        lock (gate)
        {
            completionEligible = ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                string.Equals(context.TransferId, complete.TransferId, StringComparison.Ordinal) &&
                string.Equals(context.SessionId, complete.SessionId, StringComparison.Ordinal) &&
                (
                    context.State == FileTransferTransferState.AwaitingCompletion ||
                    context.PullSessionActive
                ) &&
                context.FileSizeBytes == complete.FileSizeBytes &&
                string.Equals(context.Sha256Base64, complete.Sha256Base64, StringComparison.Ordinal);

            if (!completionEligible)
            {
                return false;
            }

            context.BytesTransferred = context.FileSizeBytes;
            context.ChunksTransferred = context.ChunkCount;
            context.BytesAcceptedForTransport = context.FileSizeBytes;
            context.ChunksAcceptedForTransport = context.ChunkCount;
            context.BytesAcknowledgedByReceiver = context.FileSizeBytes;
            context.State = FileTransferTransferState.AwaitingCompletion;
            context.StatusMessage = "Waiting for receiver verification.";
        }

        LogTransferInfo(
            "complete_received",
            FileTransferDirection.Outbound,
            complete.TransferId,
            sessionId: complete.SessionId,
            fileSizeBytes: complete.FileSizeBytes);
        TouchOutboundV6PeerLiveness(context, "complete_data_frame");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_lifecycle_priority_received; kind=complete; transfer_id={complete.TransferId}; session_id={complete.SessionId}; direction=outbound; file_size_bytes={complete.FileSizeBytes}; path=redundant_data_frame; protocol_version={(complete is FileTransferCompleteFrameV6 ? FileTransferProtocol.ProtocolVersionV6 : FileTransferProtocol.ProtocolVersionV4)}");
        await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Completed,
                errorCode: null,
                statusMessage: "Transfer complete.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None)
            .ConfigureAwait(false);
        return true;
    }

    private Task HandleIncomingPauseControlAsync(FileTransferPauseControlV6 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = IsOutboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId)
                ? outboundTransfer
                : null;
            inbound = IsInboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId)
                ? inboundTransfer
                : null;
        }

        var frame = new FileTransferPauseControlFrameV6
        {
            SessionId = message.SessionId,
            TransferId = message.TransferId,
            Epoch = message.Epoch,
            Paused = message.Paused,
            Reason = message.Reason,
            TransportEpoch = message.TransportEpoch,
            BatchId = message.BatchId,
            RepairRequestId = message.RepairRequestId,
            Priority = message.Priority,
            RecoveryMode = message.RecoveryMode,
        };

        if (outbound is not null)
        {
            TouchOutboundV6PeerLiveness(outbound, "pause_control");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_received; kind=pause_control; transfer_id={message.TransferId}; session_id={message.SessionId}; direction=outbound; paused={(message.Paused ? 1 : 0)}; reason={FormatProtocolLogValue(NormalizeReason(message.Reason) ?? "(none)")}");
            ApplyOutboundV4PauseControl(outbound, frame);
            SignalOutboundSparseSenderPump(outbound);
            return Task.CompletedTask;
        }

        if (inbound is not null)
        {
            TouchInboundV6PeerLiveness(inbound, "pause_control");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_received; kind=pause_control; transfer_id={message.TransferId}; session_id={message.SessionId}; direction=inbound; paused={(message.Paused ? 1 : 0)}; reason={FormatProtocolLogValue(NormalizeReason(message.Reason) ?? "(none)")}");
            if (ApplyInboundV4PauseControl(inbound, frame))
            {
                _ = Task.Run(
                    () => FlushInboundV6PausedProgressAsync(inbound, "peer_resumed"),
                    CancellationToken.None);
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleIncomingHeartbeatAsync(FileTransferHeartbeatV6 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = IsOutboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId)
                ? outboundTransfer
                : null;
            inbound = IsInboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId)
                ? inboundTransfer
                : null;
        }

        if (outbound is not null)
        {
            TouchOutboundV6PeerLiveness(outbound, "heartbeat");
        }

        if (inbound is not null)
        {
            TouchInboundV6PeerLiveness(inbound, "heartbeat");
        }

        return Task.CompletedTask;
    }

    private bool IsOutboundLifecycleMessageMatchLocked(string sessionId, string transferId)
        => outboundTransfer is not null &&
           !outboundTransfer.IsTerminal &&
           string.Equals(outboundTransfer.TransferId, transferId, StringComparison.Ordinal) &&
           string.Equals(outboundTransfer.SessionId, sessionId, StringComparison.Ordinal);

    private bool IsInboundLifecycleMessageMatchLocked(string sessionId, string transferId)
        => inboundTransfer is not null &&
           !inboundTransfer.IsTerminal &&
           string.Equals(inboundTransfer.TransferId, transferId, StringComparison.Ordinal) &&
           string.Equals(inboundTransfer.SessionId, sessionId, StringComparison.Ordinal);


    private async Task FinalizeInboundTransferAsync(InboundTransferContext context, CancellationToken ct)
    {
        Stream? writeStream;
        IncrementalHash? hash;
        long bytesTransferred;
        string expectedHash;
        string sessionId;
        string transferId;
        long fileSizeBytes;
        bool sparseMode;
        int negotiatedDataProtocolVersion;
        FileTransferReceiveDestination? destination;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            writeStream = context.WriteStream;
            hash = context.Hash;
            bytesTransferred = context.BytesTransferred;
            expectedHash = context.Sha256Base64 ?? string.Empty;
            sessionId = context.SessionId;
            transferId = context.TransferId;
            fileSizeBytes = context.FileSizeBytes;
            sparseMode = context.ReceiverSparseWriteActive;
            negotiatedDataProtocolVersion = context.NegotiatedDataProtocolVersion;
            destination = context.WriteDestination;
        }

        var isV4 = negotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 || sparseMode;
        if (isV4)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_finalize_started; transfer_id={transferId}; session_id={sessionId}; data_protocol_version={negotiatedDataProtocolVersion}; sparse_mode={(sparseMode ? 1 : 0)}; bytes_transferred={bytesTransferred}; file_size={fileSizeBytes}");
        }

        if (writeStream is null || hash is null)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamOpenFailedErrorCode,
                statusMessage: "Destination stream became unavailable.",
                sendError: true,
                errorMessage: "Destination stream became unavailable.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (bytesTransferred != fileSizeBytes)
        {
            if (isV4)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_v4_finalize_size_mismatch; transfer_id={transferId}; session_id={sessionId}; bytes_transferred={bytesTransferred}; file_size={fileSizeBytes}; delta_bytes={bytesTransferred - fileSizeBytes}");
            }

            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: FileSizeMismatchErrorCode,
                statusMessage: "Received bytes did not match the declared file size.",
                sendError: true,
                errorMessage: "Received bytes did not match the declared file size.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string computedHash;
        try
        {
            await writeStream.FlushAsync(ct).ConfigureAwait(false);
            computedHash = sparseMode
                ? await ComputeSparseReceiveHashAsync(context, writeStream, ct).ConfigureAwait(false)
                : Convert.ToBase64String(hash.GetHashAndReset());
        }
        catch (Exception ex)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamWriteFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not finalize the destination stream.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(computedHash, expectedHash, StringComparison.Ordinal))
        {
            LogTransferInfo(
                "integrity_verify_failed",
                FileTransferDirection.Inbound,
                transferId,
                sessionId: sessionId,
                errorCode: HashMismatchErrorCode,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes);
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: HashMismatchErrorCode,
                statusMessage: "File hash verification failed.",
                sendError: true,
                errorMessage: "File hash verification failed.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        LogTransferInfo(
            "integrity_verify_passed",
            FileTransferDirection.Inbound,
            transferId,
            sessionId: sessionId,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes);

        try
        {
            if (destination is not null)
            {
                await destination.FinalizeAsync(ct).ConfigureAwait(false);
            }
            LogTransferInfo(
                "temp_finalize_succeeded",
                FileTransferDirection.Inbound,
                transferId,
                sessionId: sessionId,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes,
                savedPath: destination?.FinalPath);
        }
        catch (Exception ex)
        {
            LogTransferInfo(
                "temp_finalize_failed",
                FileTransferDirection.Inbound,
                transferId,
                sessionId: sessionId,
                errorCode: FinalizeFailedErrorCode,
                reason: ex.GetType().Name,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes,
                savedPath: destination?.FinalPath);
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: FinalizeFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not finalize the received file.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                context.SavedFilePath = destination?.FinalPath;
                context.SavedDirectoryPath = destination?.DirectoryPath;
                context.SavedFileName = destination?.SafeFileName ?? context.FileName;
            }
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
            using var linkedCompletionCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferCompleteAsync(
                new FileTransferCompleteV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = context.FileSizeBytes,
                    Sha256Base64 = computedHash,
                },
                linkedCompletionCts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_sent; kind=complete; transfer_id={transferId}; session_id={sessionId}; path=control; file_size_bytes={context.FileSizeBytes}");

            if (ShouldUseV6SparseCreditEnvelope(context))
            {
                using var dataFrameTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
                await SendInboundV4CompleteAsync(
                        context,
                        sessionId,
                        transferId,
                        context.FileSizeBytes,
                        computedHash,
                        dataFrameTimeout.Token,
                        failOnSendFailure: false)
                    .ConfigureAwait(false);
            }

            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Completed,
                errorCode: null,
                statusMessage: "Transfer complete.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not send the completion acknowledgment.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeSparseReceiveHashAsync(InboundTransferContext context, Stream stream, CancellationToken ct)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("Sparse receive destination is not readable and seekable.");
        }

        var stopwatch = Stopwatch.StartNew();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiverWriteBatchMaxBytes);
        long remaining = context.FileSizeBytes;
        long readBytes = 0;
        var isV4 = context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 ||
            context.ReceiverSparseWriteActive;
        var completed = false;
        if (isV4)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_sparse_hash_started; transfer_id={context.TransferId}; session_id={context.SessionId}; file_size={context.FileSizeBytes}");
        }

        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, requested), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Sparse receive destination ended before the declared file size.");
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
                readBytes += read;
            }

            var computedHash = Convert.ToBase64String(hash.GetHashAndReset());
            completed = true;
            return computedHash;
        }
        finally
        {
            stopwatch.Stop();
            ArrayPool<byte>.Shared.Return(buffer);
            if (isV4 && completed)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_sparse_hash_completed; transfer_id={context.TransferId}; session_id={context.SessionId}; duration_ms={stopwatch.ElapsedMilliseconds}; read_bytes={readBytes}; file_size={context.FileSizeBytes}");
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_receiver_sparse_hash_readback_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; read_bytes={readBytes}; expected_bytes={context.FileSizeBytes}; readback_hash_duration_ms={stopwatch.ElapsedMilliseconds}");
        }
    }

    private void UpdateOldestGapTrackingLocked(InboundTransferContext context)
        => inboundController.UpdateOldestGapTrackingLocked(context);

    private void UpdateInboundDegradedRepairModeLocked(InboundTransferContext context)
        => inboundController.UpdateInboundDegradedRepairModeLocked(context);

    private void RecordInboundUsefulBulkProgressLocked(InboundTransferContext context, DateTimeOffset now, bool clearGapState)
        => inboundController.RecordInboundUsefulBulkProgressLocked(context, now, clearGapState);

    private void RecordInboundContiguousProgressLocked(InboundTransferContext context, DateTimeOffset now, int contiguousProgressChunkCount)
        => inboundController.RecordInboundContiguousProgressLocked(context, now, contiguousProgressChunkCount);

    private void UpdateInboundBulkHealthLocked(InboundTransferContext context)
        => inboundController.UpdateInboundBulkHealthLocked(context);

    private void RefreshHighestBufferedChunkIndexLocked(InboundTransferContext context)
        => inboundController.RefreshHighestBufferedChunkIndexLocked(context);

    private int GetCreditFrontierLocked(InboundTransferContext context, int highestBufferedChunkIndex)
        => inboundController.GetCreditFrontierLocked(context, highestBufferedChunkIndex);

    private int GetRawTargetGrantedUntilExclusiveLocked(InboundTransferContext context, int creditFrontier)
        => inboundController.GetRawTargetGrantedUntilExclusiveLocked(context, creditFrontier);

    private DateTimeOffset MaxDateTimeOffset(params DateTimeOffset?[] values)
        => inboundController.MaxDateTimeOffset(values);

    private int GetCurrentHighestBufferedChunkIndexLocked(InboundTransferContext context)
        => inboundController.GetCurrentHighestBufferedChunkIndexLocked(context);

    private int GetEffectiveGrantChunksLocked(InboundTransferContext context)
        => inboundController.GetEffectiveGrantChunksLocked(context);

    private int GetEffectiveStartupGrantChunksLocked()
        => inboundController.GetEffectiveStartupGrantChunksLocked();

    private int GetEffectiveLowWatermarkChunksLocked(InboundTransferContext context)
        => inboundController.GetEffectiveLowWatermarkChunksLocked(context);

    private bool ShouldDeferGrantExtensionDueToGapLocked(InboundTransferContext context, int highestBufferedChunkIndex, int targetGrantedUntilExclusive)
        => inboundController.ShouldDeferGrantExtensionDueToGapLocked(context, highestBufferedChunkIndex, targetGrantedUntilExclusive);

    private bool ShouldLogGapDeferredLocked(InboundTransferContext context)
        => inboundController.ShouldLogGapDeferredLocked(context);

    private static string ClassifyOutboundFailureErrorCode(Exception ex, string fallbackErrorCode)
    {
        if (TryGetSenderCacheErrorCode(ex, out var senderCacheErrorCode))
        {
            return senderCacheErrorCode;
        }

        return IsTransportIncompatible(ex)
            ? TransportIncompatibleErrorCode
            : IsTransportDisconnected(ex)
                ? DisconnectedErrorCode
            : IsPayloadBudgetExceeded(ex)
                ? PayloadBudgetExceededErrorCode
                : fallbackErrorCode;
    }

    private static string ClassifyOutboundFailureStatusMessage(Exception ex, string errorCode)
        => string.Equals(errorCode, DisconnectedErrorCode, StringComparison.Ordinal)
            ? "Peer disconnected."
            : ex.Message;

    private static bool TryGetSenderCacheErrorCode(Exception ex, out string errorCode)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SenderCacheException senderCacheException)
            {
                errorCode = senderCacheException.ErrorCode;
                return true;
            }
        }

        errorCode = string.Empty;
        return false;
    }

    private static bool IsTransportIncompatible(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is not InvalidOperationException invalidOperationException)
            {
                continue;
            }

            if (invalidOperationException.Message.Contains(
                    "bridge_protocol_outdated_bulk_missing",
                    StringComparison.OrdinalIgnoreCase) ||
                invalidOperationException.Message.Contains(
                    "Installed bridge does not support",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransportDisconnected(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is ObjectDisposedException)
            {
                return true;
            }

            if (current is InvalidOperationException invalidOperationException &&
                (invalidOperationException.Message.Contains("Bridge disconnected", StringComparison.OrdinalIgnoreCase) ||
                 invalidOperationException.Message.Contains("NKN bridge is not running", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPayloadBudgetExceeded(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is not InvalidOperationException invalidOperationException)
            {
                continue;
            }

            if (invalidOperationException.Message.Contains(
                    "payload exceeded safe budget",
                    StringComparison.OrdinalIgnoreCase) ||
                invalidOperationException.Message.Contains(
                    "Bridge payload too large",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetFrameRawChunkBytes(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => batch.DataSegments.Sum(static segment => segment.Length),
            _ => 0,
        };

    private static int GetFrameChunkCount(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => batch.DataSegments.Count,
            _ => 0,
        };

    private static string NormalizeRequiredBounded(string? value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(paramName, $"Value exceeds {maxLength} characters.");
        }

        return normalized;
    }

    private static string? NormalizeReason(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > FileTransferProtocol.MaxErrorCodeLength
            ? normalized[..FileTransferProtocol.MaxErrorCodeLength]
            : normalized;
    }

    private static bool IsPeerDisconnectedErrorCode(string? value)
        => string.Equals(
            NormalizeErrorCode(value),
            FileTransferResultCodes.PeerDisconnected,
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateReadableStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("Source stream must be readable.");
        }

        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }
    }

    private static void ValidateWritableStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new InvalidOperationException("Destination stream must be writable.");
        }
    }

    private static bool TryCalculateExpectedChunkCount(long fileSizeBytes, int chunkSizeBytes, out int chunkCount)
    {
        chunkCount = 0;
        if (fileSizeBytes <= 0 ||
            chunkSizeBytes <= 0 ||
            chunkSizeBytes > FileTransferProtocol.MaxChunkRawBytes)
        {
            return false;
        }

        try
        {
            chunkCount = checked((int)((fileSizeBytes + chunkSizeBytes - 1) / chunkSizeBytes));
            return chunkCount is > 0 and <= FileTransferProtocol.MaxChunkCountV4;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private int ResolveSafeOutboundChunkSize(
        OutboundTransferContext context,
        IFileTransferSignalingTransport? currentTransport)
    {
        var requestedChunkSize = ResolvePreferredOutboundChunkSize(context, currentTransport);
        if (currentTransport is IFileTransferChunkBudgetProvider chunkBudgetProvider)
        {
            return chunkBudgetProvider.ResolveSafeOutboundChunkSize(
                new FileTransferChunkBudgetRequest(
                    context.TransferId,
                    context.FileSizeBytes,
                    requestedChunkSize,
                    context.NegotiatedDataProtocolVersion));
        }

        return FileTransferChunkBudget.ComputeLargestFittingRawChunkSize(
            requestedChunkSize,
            candidateChunkSize =>
            {
                try
                {
                    var estimateFrame = context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4
                        ? new FileTransferChunkBatchFrameV4
                        {
                            SessionId = string.IsNullOrWhiteSpace(context.SessionId) ? new string('s', 32) : context.SessionId,
                            TransferId = context.TransferId,
                            StartChunkIndex = 0,
                            ChunkCount = 1,
                            DataSegments = [new byte[candidateChunkSize]],
                        }
                        : new FileTransferChunkBatchFrameV6
                        {
                            SessionId = string.IsNullOrWhiteSpace(context.SessionId) ? new string('s', 32) : context.SessionId,
                            TransferId = context.TransferId,
                            StartChunkIndex = 0,
                            ChunkCount = 1,
                            DataSegments = [new byte[candidateChunkSize]],
                        };
                    var payload = context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4
                        ? FileTransferDataFrameCodec.SerializeLegacyV4(estimateFrame)
                        : FileTransferDataFrameCodec.Serialize(estimateFrame);
                    return payload.Length <= FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV4;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            },
            "No valid file-transfer chunk size fits within the payload budget.");
    }

    private static bool IsNegotiableDataProtocolVersion(int? protocolVersion)
        => protocolVersion is FileTransferProtocol.ProtocolVersionV4
            or FileTransferProtocol.ProtocolVersionV6;

    private static int ResolvePreferredDataProtocolVersionForNewTransfer(IFileTransferSignalingTransport? currentTransport)
        => ResolveFileTransferRouteForNewTransfer(CreateInitialRouteResolverInputForNewTransfer(currentTransport)).ProtocolVersion;

    private static FileTransferRouteSelection ResolveFileTransferRouteForNewTransfer(FileTransferRouteResolverInput input)
        => FileTransferRouteResolver.Resolve(input);

    private static FileTransferRouteResolverInput CreateInitialRouteResolverInputForNewTransfer(
        IFileTransferSignalingTransport? currentTransport)
    {
        var input = FileTransferRouteResolverInput.FromTransport(currentTransport);
        return input with { IsFileTunaActive = false };
    }

    private static FileTransferRouteSelection ResolveInitialOutboundRouteSelection(
        FileTransferRouteResolverInput baseRouteInput,
        FileTransferRouteResolverInput transportRouteInput,
        FileTransferInitialTunaReadinessEvidence? initialTunaEvidence)
    {
        if (!baseRouteInput.IsPostTunaFileFallbackActive &&
            (initialTunaEvidence is not null || transportRouteInput.IsFileTunaActive))
        {
            return FileTransferRouteResolver.Resolve(new FileTransferRouteResolverInput(
                IsFileTunaActive: true,
                IsPostTunaFileFallbackActive: false,
                IsDiagnosticRegularNknV6RouteEnabled: false,
                HandoffKind: FileTransferTransportHandoffKind.None,
                TransportProfileKind: baseRouteInput.TransportProfileKind));
        }

        return ResolveFileTransferRouteForNewTransfer(baseRouteInput);
    }

    private static FileTransferRouteResolverInput CreateRouteInputForPublishedSelection(
        FileTransferRouteSelection routeSelection,
        FileTransferTransportProfileKind transportProfileKind)
        => new(
            IsFileTunaActive: routeSelection.Route == FileTransferRoute.FileTunaV4,
            IsPostTunaFileFallbackActive: routeSelection.Route == FileTransferRoute.PostTunaFallbackV6,
            IsDiagnosticRegularNknV6RouteEnabled: routeSelection.Route == FileTransferRoute.DiagnosticRegularNknV6,
            HandoffKind: routeSelection.HandoffKind,
            TransportProfileKind: transportProfileKind);

    private static FileTransferInitialTunaReadinessEvidence? CaptureInitialTunaReadinessEvidence(
        IFileTransferSignalingTransport? currentTransport,
        string sessionId,
        string transferId,
        FileTransferDirection direction)
        => currentTransport is IFileTransferInitialTunaReadinessProvider provider &&
           provider.TryGetInitialTunaReadinessEvidence(sessionId, transferId, direction, out var evidence)
            ? evidence
            : null;

    private static bool TryResolveIncomingOfferRouteSelection(
        FileTransferOfferV2 message,
        FileTransferRouteResolverInput routeInput,
        out FileTransferRouteSelection routeSelection,
        out string reason)
    {
        routeSelection = default;
        reason = "offer_route_invalid";
        if (message.PreferredDataProtocolVersion is not { } preferredProtocolVersion ||
            !IsNegotiableDataProtocolVersion(preferredProtocolVersion))
        {
            reason = "offer_protocol_not_supported";
            return false;
        }

        var localSelection = ResolveFileTransferRouteForNewTransfer(routeInput);
        if (!string.IsNullOrWhiteSpace(message.FileTransferRoute))
        {
            if (!TryResolveRouteSelectionFromToken(message.FileTransferRoute, preferredProtocolVersion, out var explicitSelection))
            {
                reason = "offer_route_invalid_or_protocol_mismatch";
                return false;
            }

            if (explicitSelection.Route != localSelection.Route &&
                explicitSelection.Route != FileTransferRoute.FileTunaV4)
            {
                reason = "offer_route_not_active";
                return false;
            }

            routeSelection = explicitSelection;
            reason = "offer_route_explicit";
            return true;
        }

        if (localSelection.ProtocolVersion != preferredProtocolVersion)
        {
            reason = "offer_route_context_mismatch";
            return false;
        }

        routeSelection = localSelection;
        reason = "offer_route_inferred";
        return true;
    }

    private static bool TryResolveRouteSelectionFromToken(
        string? routeToken,
        int protocolVersion,
        out FileTransferRouteSelection routeSelection)
    {
        routeSelection = default;
        if (!FileTransferRouteResolver.TryNormalizeTelemetryToken(routeToken, protocolVersion, out var normalizedToken) ||
            string.IsNullOrWhiteSpace(normalizedToken) ||
            !FileTransferRouteResolver.TryParseTelemetryToken(normalizedToken, out var route))
        {
            return false;
        }

        routeSelection = FileTransferRouteResolver.Resolve(route);
        return true;
    }

    private static bool IsRouteTokenCompatibleWithSelection(
        string? routeToken,
        int protocolVersion,
        FileTransferRouteSelection selection)
    {
        if (selection.ProtocolVersion != protocolVersion)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(routeToken))
        {
            return true;
        }

        return FileTransferRouteResolver.TryNormalizeTelemetryToken(routeToken, protocolVersion, out var normalizedToken) &&
               string.Equals(normalizedToken, selection.TelemetryToken, StringComparison.Ordinal);
    }

    private static bool IsV6StreamingTransport(IFileTransferSignalingTransport? currentTransport)
        => currentTransport is IFileTransferProtocolCapabilities { SupportsFileTransferV6Streaming: true };

    private FileTransferRuntimeProfileSelection ResolveFileTransferRuntimeProfile(OutboundTransferContext context)
    {
        lock (gate)
        {
            var selection = ResolveFileTransferRuntimeProfileLocked(context);
            ApplyFileTransferRuntimeProfileSelectionLocked(context, selection);
            return selection;
        }
    }

    private FileTransferRuntimeProfileSelection ResolveFileTransferRuntimeProfile(InboundTransferContext context)
    {
        lock (gate)
        {
            var selection = ResolveFileTransferRuntimeProfileLocked(context);
            ApplyFileTransferRuntimeProfileSelectionLocked(context, selection);
            return selection;
        }
    }

    private FileTransferRuntimeProfileSelection ResolveFileTransferRuntimeProfileLocked(
        OutboundTransferContext context)
    {
        if (!ReferenceEquals(outboundTransfer, context))
        {
            return FileTransferRuntimeProfileSelection.Default("not_current_outbound");
        }

        if (context.IsTerminal)
        {
            return FileTransferRuntimeProfileSelection.Default("terminal");
        }

        return FileTransferRuntimeProfileSelection.FromRouteSelection(context.RouteSelection);
    }

    private FileTransferRuntimeProfileSelection ResolveFileTransferRuntimeProfileLocked(
        InboundTransferContext context)
    {
        if (!ReferenceEquals(inboundTransfer, context))
        {
            return FileTransferRuntimeProfileSelection.Default("not_current_inbound");
        }

        if (context.IsTerminal)
        {
            return FileTransferRuntimeProfileSelection.Default("terminal");
        }

        return FileTransferRuntimeProfileSelection.FromRouteSelection(context.RouteSelection);
    }

    private static void ApplyFileTransferRuntimeProfileSelectionLocked(
        OutboundTransferContext context,
        FileTransferRuntimeProfileSelection selection)
    {
        context.RouteRuntime = selection.RouteRuntime;
        context.RuntimeProfile = selection.Profile;
        context.BridgeRecoveryPolicy = selection.BridgeRecoveryPolicy;
    }

    private static void ApplyFileTransferRuntimeProfileSelectionLocked(
        InboundTransferContext context,
        FileTransferRuntimeProfileSelection selection)
    {
        context.RouteRuntime = selection.RouteRuntime;
        context.RuntimeProfile = selection.Profile;
        context.BridgeRecoveryPolicy = selection.BridgeRecoveryPolicy;
    }

    private static string FormatFileTransferLegState(FileTransferLegState state)
        => FileTransferCoordinator.FormatLegState(state);

    private static FileTransferLeg? EnsureOutboundTransferLegLocked(
        OutboundTransferContext context,
        string reason,
        FileTransferInitialTunaReadinessEvidence? initialTunaReadinessEvidence = null,
        FileTransferRouteSelection? initialRouteFallbackSelection = null)
        => context.CurrentTransferLeg ?? StartOutboundTransferLegLocked(
            context,
            context.RouteSelection,
            reason,
            FileTransferLegState.Active,
            canSendData: true,
            freezeCurrent: false,
            initialTunaReadinessEvidence: initialTunaReadinessEvidence,
            allowInitialRouteFallback: initialRouteFallbackSelection.HasValue,
            initialRouteFallbackSelection: initialRouteFallbackSelection);

    private static FileTransferLeg? EnsureInboundTransferLegLocked(
        InboundTransferContext context,
        string reason,
        FileTransferInitialTunaReadinessEvidence? initialTunaReadinessEvidence = null)
        => context.CurrentTransferLeg ?? StartInboundTransferLegLocked(
            context,
            context.RouteSelection,
            reason,
            FileTransferLegState.Active,
            canSendData: true,
            freezeCurrent: false,
            initialTunaReadinessEvidence: initialTunaReadinessEvidence);

    private static FileTransferCoordinatorState CreateCoordinatorStateLocked(OutboundTransferContext context)
        => new(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.IsTerminal,
            context.RouteSelection,
            context.CurrentLiveRouteEpoch,
            context.CurrentTransferLeg,
            context.LastLiveRouteEpochId,
            context.LastTransferLegGeneration,
            context.LastRuntimeOwnerGeneration,
            context.TerminalGeneration,
            context.CurrentRuntimeOwner,
            context.TransferLegHistory,
            context.RuntimeUnlockFallbackSurvivalState,
            context.RuntimeUnlockAdmissionToken,
            context.LastRuntimeUnlockAdmissionGeneration,
            context.RuntimeUnlockRouteCommitToken,
            context.LastRuntimeUnlockRouteCommitGeneration,
            context.CurrentFallbackControlPlaneToken,
            context.LastFallbackControlPlaneTokenGeneration,
            context.CurrentFallbackCheckpointLease,
            context.LastFallbackCheckpointLeaseGeneration,
            context.CurrentFallbackControlPlanePressureToken,
            context.LastFallbackControlPlanePressureGeneration,
            context.LastFallbackTailYieldDecision,
            context.LastFallbackTailYieldGeneration,
            context.CurrentRepairReplayDecision,
            context.LastRepairReplayGeneration,
            context.CurrentTransportEpochDecision,
            context.LastTransportEpochDecisionGeneration,
            context.CurrentBridgeRecoveryDecision,
            context.LastBridgeRecoveryDecisionGeneration,
            context.CurrentRecoveryLivenessLease,
            context.LastRecoveryLivenessLeaseGeneration,
            context.CurrentRoutePublicationToken,
            context.LastRoutePublicationGeneration);

    private static void ApplyCoordinatorDecisionToContextLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorDecision decision)
    {
        context.RouteSelection = decision.State.RouteSelection;
        context.RouteRuntime = decision.State.RouteRuntime;
        context.NegotiatedDataProtocolVersion = decision.State.RouteSelection.ProtocolVersion;
        context.CurrentLiveRouteEpoch = decision.State.CurrentLiveRouteEpoch;
        context.LastLiveRouteEpochId = decision.State.LastLiveRouteEpochId;
        context.CurrentTransferLeg = decision.State.CurrentLeg;
        context.LastTransferLegGeneration = decision.State.LastTransferLegGeneration;
        context.LastRuntimeOwnerGeneration = decision.State.LastRuntimeOwnerGeneration;
        context.TerminalGeneration = decision.State.TerminalGeneration;
        context.CurrentRuntimeOwner = decision.State.CurrentRuntimeOwner;
        context.RuntimeUnlockFallbackSurvivalState = decision.State.RuntimeUnlockFallbackSurvivalState;
        context.RuntimeUnlockAdmissionToken = decision.State.CurrentRuntimeUnlockAdmission;
        context.LastRuntimeUnlockAdmissionGeneration = decision.State.LastRuntimeUnlockAdmissionGeneration;
        context.RuntimeUnlockRouteCommitToken = decision.State.CurrentRouteCommit;
        context.LastRuntimeUnlockRouteCommitGeneration = decision.State.LastRouteCommitGeneration;
        context.CurrentFallbackControlPlaneToken = decision.State.CurrentFallbackControlPlaneToken;
        context.LastFallbackControlPlaneTokenGeneration = decision.State.LastFallbackControlPlaneTokenGeneration;
        context.CurrentFallbackCheckpointLease = decision.State.CurrentFallbackCheckpointLease;
        context.LastFallbackCheckpointLeaseGeneration = decision.State.LastFallbackCheckpointLeaseGeneration;
        context.CurrentFallbackControlPlanePressureToken = decision.State.CurrentFallbackControlPlanePressureToken;
        context.LastFallbackControlPlanePressureGeneration = decision.State.LastFallbackControlPlanePressureGeneration;
        context.LastFallbackTailYieldDecision = decision.State.LastFallbackTailYieldDecision;
        context.LastFallbackTailYieldGeneration = decision.State.LastFallbackTailYieldGeneration;
        context.CurrentRepairReplayDecision = decision.State.CurrentRepairReplayDecision;
        context.LastRepairReplayGeneration = decision.State.LastRepairReplayGeneration;
        context.CurrentTransportEpochDecision = decision.State.CurrentTransportEpochDecision;
        context.LastTransportEpochDecisionGeneration = decision.State.LastTransportEpochDecisionGeneration;
        context.CurrentBridgeRecoveryDecision = decision.State.CurrentBridgeRecoveryDecision;
        context.LastBridgeRecoveryDecisionGeneration = decision.State.LastBridgeRecoveryDecisionGeneration;
        context.CurrentRecoveryLivenessLease = decision.State.CurrentRecoveryLivenessLease;
        context.LastRecoveryLivenessLeaseGeneration = decision.State.LastRecoveryLivenessLeaseGeneration;
        context.CurrentRoutePublicationToken = decision.State.CurrentRoutePublicationToken;
        context.LastRoutePublicationGeneration = decision.State.LastRoutePublicationGeneration;
        var legHistory = decision.State.LegHistory.ToList();
        context.TransferLegHistory.Clear();
        context.TransferLegHistory.AddRange(legHistory);
    }

    private static void ApplyCoordinatorDecisionToContextLocked(
        InboundTransferContext context,
        FileTransferCoordinatorDecision decision)
    {
        context.RouteSelection = decision.State.RouteSelection;
        context.RouteRuntime = decision.State.RouteRuntime;
        context.NegotiatedDataProtocolVersion = decision.State.RouteSelection.ProtocolVersion;
        context.CurrentLiveRouteEpoch = decision.State.CurrentLiveRouteEpoch;
        context.LastLiveRouteEpochId = decision.State.LastLiveRouteEpochId;
        context.CurrentTransferLeg = decision.State.CurrentLeg;
        context.LastTransferLegGeneration = decision.State.LastTransferLegGeneration;
        context.LastRuntimeOwnerGeneration = decision.State.LastRuntimeOwnerGeneration;
        context.TerminalGeneration = decision.State.TerminalGeneration;
        context.CurrentRuntimeOwner = decision.State.CurrentRuntimeOwner;
        context.RuntimeUnlockFallbackSurvivalState = decision.State.RuntimeUnlockFallbackSurvivalState;
        context.RuntimeUnlockAdmissionToken = decision.State.CurrentRuntimeUnlockAdmission;
        context.LastRuntimeUnlockAdmissionGeneration = decision.State.LastRuntimeUnlockAdmissionGeneration;
        context.RuntimeUnlockRouteCommitToken = decision.State.CurrentRouteCommit;
        context.LastRuntimeUnlockRouteCommitGeneration = decision.State.LastRouteCommitGeneration;
        context.CurrentFallbackControlPlaneToken = decision.State.CurrentFallbackControlPlaneToken;
        context.LastFallbackControlPlaneTokenGeneration = decision.State.LastFallbackControlPlaneTokenGeneration;
        context.CurrentFallbackCheckpointLease = decision.State.CurrentFallbackCheckpointLease;
        context.LastFallbackCheckpointLeaseGeneration = decision.State.LastFallbackCheckpointLeaseGeneration;
        context.CurrentFallbackControlPlanePressureToken = decision.State.CurrentFallbackControlPlanePressureToken;
        context.LastFallbackControlPlanePressureGeneration = decision.State.LastFallbackControlPlanePressureGeneration;
        context.LastFallbackTailYieldDecision = decision.State.LastFallbackTailYieldDecision;
        context.LastFallbackTailYieldGeneration = decision.State.LastFallbackTailYieldGeneration;
        context.CurrentRepairReplayDecision = decision.State.CurrentRepairReplayDecision;
        context.LastRepairReplayGeneration = decision.State.LastRepairReplayGeneration;
        context.CurrentTransportEpochDecision = decision.State.CurrentTransportEpochDecision;
        context.LastTransportEpochDecisionGeneration = decision.State.LastTransportEpochDecisionGeneration;
        context.CurrentBridgeRecoveryDecision = decision.State.CurrentBridgeRecoveryDecision;
        context.LastBridgeRecoveryDecisionGeneration = decision.State.LastBridgeRecoveryDecisionGeneration;
        context.CurrentRecoveryLivenessLease = decision.State.CurrentRecoveryLivenessLease;
        context.LastRecoveryLivenessLeaseGeneration = decision.State.LastRecoveryLivenessLeaseGeneration;
        context.CurrentRoutePublicationToken = decision.State.CurrentRoutePublicationToken;
        context.LastRoutePublicationGeneration = decision.State.LastRoutePublicationGeneration;
        var legHistory = decision.State.LegHistory.ToList();
        context.TransferLegHistory.Clear();
        context.TransferLegHistory.AddRange(legHistory);
    }

    private static void ApplyCoordinatorActionDecisionToContextLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorActionDecision decision)
    {
        context.CurrentRepairReplayDecision = decision.State.CurrentRepairReplayDecision;
        context.LastRepairReplayGeneration = decision.State.LastRepairReplayGeneration;
        context.CurrentTransportEpochDecision = decision.State.CurrentTransportEpochDecision;
        context.LastTransportEpochDecisionGeneration = decision.State.LastTransportEpochDecisionGeneration;
        context.CurrentBridgeRecoveryDecision = decision.State.CurrentBridgeRecoveryDecision;
        context.LastBridgeRecoveryDecisionGeneration = decision.State.LastBridgeRecoveryDecisionGeneration;
        context.CurrentRecoveryLivenessLease = decision.State.CurrentRecoveryLivenessLease;
        context.LastRecoveryLivenessLeaseGeneration = decision.State.LastRecoveryLivenessLeaseGeneration;
        context.CurrentRoutePublicationToken = decision.State.CurrentRoutePublicationToken;
        context.LastRoutePublicationGeneration = decision.State.LastRoutePublicationGeneration;
    }

    private static void ApplyCoordinatorActionDecisionToContextLocked(
        InboundTransferContext context,
        FileTransferCoordinatorActionDecision decision)
    {
        context.CurrentRepairReplayDecision = decision.State.CurrentRepairReplayDecision;
        context.LastRepairReplayGeneration = decision.State.LastRepairReplayGeneration;
        context.CurrentTransportEpochDecision = decision.State.CurrentTransportEpochDecision;
        context.LastTransportEpochDecisionGeneration = decision.State.LastTransportEpochDecisionGeneration;
        context.CurrentBridgeRecoveryDecision = decision.State.CurrentBridgeRecoveryDecision;
        context.LastBridgeRecoveryDecisionGeneration = decision.State.LastBridgeRecoveryDecisionGeneration;
        context.CurrentRecoveryLivenessLease = decision.State.CurrentRecoveryLivenessLease;
        context.LastRecoveryLivenessLeaseGeneration = decision.State.LastRecoveryLivenessLeaseGeneration;
        context.CurrentRoutePublicationToken = decision.State.CurrentRoutePublicationToken;
        context.LastRoutePublicationGeneration = decision.State.LastRoutePublicationGeneration;
    }

    private static FileTransferCoordinatorState CreateCoordinatorStateLocked(InboundTransferContext context)
        => new(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.IsTerminal,
            context.RouteSelection,
            context.CurrentLiveRouteEpoch,
            context.CurrentTransferLeg,
            context.LastLiveRouteEpochId,
            context.LastTransferLegGeneration,
            context.LastRuntimeOwnerGeneration,
            context.TerminalGeneration,
            context.CurrentRuntimeOwner,
            context.TransferLegHistory,
            context.RuntimeUnlockFallbackSurvivalState,
            context.RuntimeUnlockAdmissionToken,
            context.LastRuntimeUnlockAdmissionGeneration,
            context.RuntimeUnlockRouteCommitToken,
            context.LastRuntimeUnlockRouteCommitGeneration,
            context.CurrentFallbackControlPlaneToken,
            context.LastFallbackControlPlaneTokenGeneration,
            context.CurrentFallbackCheckpointLease,
            context.LastFallbackCheckpointLeaseGeneration,
            context.CurrentFallbackControlPlanePressureToken,
            context.LastFallbackControlPlanePressureGeneration,
            context.LastFallbackTailYieldDecision,
            context.LastFallbackTailYieldGeneration,
            context.CurrentRepairReplayDecision,
            context.LastRepairReplayGeneration,
            context.CurrentTransportEpochDecision,
            context.LastTransportEpochDecisionGeneration,
            context.CurrentBridgeRecoveryDecision,
            context.LastBridgeRecoveryDecisionGeneration,
            context.CurrentRecoveryLivenessLease,
            context.LastRecoveryLivenessLeaseGeneration,
            context.CurrentRoutePublicationToken,
            context.LastRoutePublicationGeneration);

    private static bool TryCaptureRuntimeOwnerLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        out RuntimeOwnerToken runtimeOwner)
    {
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            runtimeOwner = default;
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            return false;
        }

        runtimeOwner = owner;
        return TryAuthorizeRuntimeOwnerActionLocked(
            context,
            owner,
            actionKind,
            source,
            attemptedAction,
            out _);
    }

    private static bool TryCaptureRuntimeOwnerLocked(
        InboundTransferContext context,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        out RuntimeOwnerToken runtimeOwner)
    {
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            runtimeOwner = default;
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            return false;
        }

        runtimeOwner = owner;
        return TryAuthorizeRuntimeOwnerActionLocked(
            context,
            owner,
            actionKind,
            source,
            attemptedAction,
            out _);
    }

    private static bool TryAuthorizeRuntimeOwnerActionLocked(
        OutboundTransferContext context,
        RuntimeOwnerToken? token,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        out string rejectionReason)
    {
        if (token is not { } owner)
        {
            rejectionReason = "runtime_owner_missing";
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                actionKind,
                source,
                attemptedAction,
                RuntimeOwner: owner));
        rejectionReason = decision.Accepted ? "current" : decision.RejectionReason;
        if (!decision.Accepted)
        {
            RejectStaleRuntimeOwnerUpdateLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                owner,
                source,
                attemptedAction,
                decision.RejectionReason);
        }

        return decision.Accepted;
    }

    private static bool TryAuthorizeRuntimeOwnerActionLocked(
        InboundTransferContext context,
        RuntimeOwnerToken? token,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        out string rejectionReason)
    {
        if (token is not { } owner)
        {
            rejectionReason = "runtime_owner_missing";
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                actionKind,
                source,
                attemptedAction,
                RuntimeOwner: owner));
        rejectionReason = decision.Accepted ? "current" : decision.RejectionReason;
        if (!decision.Accepted)
        {
            RejectStaleRuntimeOwnerUpdateLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                owner,
                source,
                attemptedAction,
                decision.RejectionReason);
        }

        return decision.Accepted;
    }

    private static bool TryAuthorizeRuntimeOwnerLegActionLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        FileTransferLeg? leg,
        string? checkpointRequestId,
        long? transportEpochId,
        int? bridgeRecoveryGeneration,
        int? checkpointGeneration,
        FallbackControlPlaneToken? fallbackControlPlaneToken,
        FallbackCheckpointLease? fallbackCheckpointLease,
        FallbackControlPlanePressureToken? fallbackControlPlanePressureToken,
        FileTransferControlPlaneKind? controlPlaneKind,
        string? controlPlanePurpose,
        bool? controlPlanePeerVisible,
        bool? controlPlaneAcceptedAny,
        FallbackTailYieldKind? fallbackTailYieldKind,
        out string rejectionReason)
    {
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            rejectionReason = "runtime_owner_missing";
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                actionKind,
                source,
                attemptedAction,
                RuntimeOwner: owner,
                Route: leg?.RouteSelection.Route,
                ProtocolVersion: leg?.ProtocolVersion,
                LiveRouteEpoch: leg?.LiveRouteEpochId,
                LegGeneration: leg?.Generation,
                TransportEpochId: transportEpochId,
                BridgeRecoveryGeneration: bridgeRecoveryGeneration,
                CheckpointRequestId: checkpointRequestId,
                FallbackControlPlaneToken: fallbackControlPlaneToken,
                FallbackCheckpointLease: fallbackCheckpointLease,
                FallbackControlPlanePressureToken: fallbackControlPlanePressureToken,
                ControlPlaneKind: controlPlaneKind,
                ControlPlanePurpose: controlPlanePurpose,
                ControlPlanePeerVisible: controlPlanePeerVisible,
                ControlPlaneAcceptedAny: controlPlaneAcceptedAny,
                CheckpointGeneration: checkpointGeneration,
                FallbackTailYieldKind: fallbackTailYieldKind));
        rejectionReason = decision.Accepted ? "current" : decision.RejectionReason;
        if (!decision.Accepted)
        {
            RejectStaleRuntimeOwnerUpdateLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                owner,
                source,
                attemptedAction,
                decision.RejectionReason);
        }

        return decision.Accepted;
    }

    private static bool TryAuthorizeRuntimeOwnerLegActionLocked(
        InboundTransferContext context,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        FileTransferLeg? leg,
        string? checkpointRequestId,
        long? transportEpochId,
        int? bridgeRecoveryGeneration,
        int? checkpointGeneration,
        FallbackControlPlaneToken? fallbackControlPlaneToken,
        FallbackCheckpointLease? fallbackCheckpointLease,
        FallbackControlPlanePressureToken? fallbackControlPlanePressureToken,
        FileTransferControlPlaneKind? controlPlaneKind,
        string? controlPlanePurpose,
        bool? controlPlanePeerVisible,
        bool? controlPlaneAcceptedAny,
        FallbackTailYieldKind? fallbackTailYieldKind,
        out string rejectionReason)
    {
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            rejectionReason = "runtime_owner_missing";
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                actionKind,
                source,
                attemptedAction,
                RuntimeOwner: owner,
                Route: leg?.RouteSelection.Route,
                ProtocolVersion: leg?.ProtocolVersion,
                LiveRouteEpoch: leg?.LiveRouteEpochId,
                LegGeneration: leg?.Generation,
                TransportEpochId: transportEpochId,
                BridgeRecoveryGeneration: bridgeRecoveryGeneration,
                CheckpointRequestId: checkpointRequestId,
                FallbackControlPlaneToken: fallbackControlPlaneToken,
                FallbackCheckpointLease: fallbackCheckpointLease,
                FallbackControlPlanePressureToken: fallbackControlPlanePressureToken,
                ControlPlaneKind: controlPlaneKind,
                ControlPlanePurpose: controlPlanePurpose,
                ControlPlanePeerVisible: controlPlanePeerVisible,
                ControlPlaneAcceptedAny: controlPlaneAcceptedAny,
                CheckpointGeneration: checkpointGeneration,
                FallbackTailYieldKind: fallbackTailYieldKind));
        rejectionReason = decision.Accepted ? "current" : decision.RejectionReason;
        if (!decision.Accepted)
        {
            RejectStaleRuntimeOwnerUpdateLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                owner,
                source,
                attemptedAction,
                decision.RejectionReason);
        }

        return decision.Accepted;
    }

    private static bool TryAuthorizeRoutePublicationLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken,
        out RoutePublicationToken routePublicationToken,
        out string rejectionReason)
    {
        var decision = AuthorizeRoutePublication(
            CreateCoordinatorStateLocked(context),
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.RouteSelection,
            context.CurrentRuntimeOwner,
            context.CurrentLiveRouteEpoch,
            context.CurrentTransferLeg,
            context.TerminalGeneration,
            actionKind,
            source,
            reason,
            routeCommitToken);
        rejectionReason = decision.Accepted ? "accepted" : decision.RejectionReason;
        if (!decision.Accepted || decision.RoutePublicationToken is not { } token || token.IsDefault)
        {
            routePublicationToken = default;
            LogFileTransferRoutePublicationRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                actionKind,
                source,
                reason,
                decision.Accepted ? "route_publication_token_missing" : decision.RejectionReason,
                context.CurrentRuntimeOwner,
                routeCommitToken?.RuntimeOwner ?? context.CurrentRuntimeOwner,
                context.RouteSelection,
                context.CurrentLiveRouteEpoch,
                context.CurrentTransferLeg,
                routeCommitToken,
                decision);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        routePublicationToken = token;
        return true;
    }

    private static bool TryAuthorizeRoutePublicationLocked(
        InboundTransferContext context,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken,
        out RoutePublicationToken routePublicationToken,
        out string rejectionReason)
    {
        var decision = AuthorizeRoutePublication(
            CreateCoordinatorStateLocked(context),
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.RouteSelection,
            context.CurrentRuntimeOwner,
            context.CurrentLiveRouteEpoch,
            context.CurrentTransferLeg,
            context.TerminalGeneration,
            actionKind,
            source,
            reason,
            routeCommitToken);
        rejectionReason = decision.Accepted ? "accepted" : decision.RejectionReason;
        if (!decision.Accepted || decision.RoutePublicationToken is not { } token || token.IsDefault)
        {
            routePublicationToken = default;
            LogFileTransferRoutePublicationRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                actionKind,
                source,
                reason,
                decision.Accepted ? "route_publication_token_missing" : decision.RejectionReason,
                context.CurrentRuntimeOwner,
                routeCommitToken?.RuntimeOwner ?? context.CurrentRuntimeOwner,
                context.RouteSelection,
                context.CurrentLiveRouteEpoch,
                context.CurrentTransferLeg,
                routeCommitToken,
                decision);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        routePublicationToken = token;
        return true;
    }

    private static FileTransferCoordinatorActionDecision AuthorizeRoutePublication(
        FileTransferCoordinatorState state,
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRouteSelection routeSelection,
        RuntimeOwnerToken? currentRuntimeOwner,
        LiveRouteEpoch? liveRouteEpoch,
        FileTransferLeg? currentLeg,
        int terminalGeneration,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken)
    {
        var commitToken = routeCommitToken;
        var runtimeOwner = commitToken?.RuntimeOwner ?? currentRuntimeOwner;
        var targetRoute = commitToken?.TargetRoute ?? routeSelection.Route;
        var targetProtocolVersion = commitToken?.TargetProtocolVersion ?? routeSelection.ProtocolVersion;
        var targetTransport = commitToken?.TargetTransport ?? ResolveRoutePublicationTargetTransport(targetRoute);
        var request = new FileTransferCoordinatorActionRequest(
            actionKind,
            source,
            reason,
            RuntimeOwner: runtimeOwner,
            Route: commitToken?.Route ?? routeSelection.Route,
            ProtocolVersion: commitToken?.ProtocolVersion ?? routeSelection.ProtocolVersion,
            LiveRouteEpoch: commitToken?.LiveRouteEpoch ?? liveRouteEpoch?.EpochId ?? 0,
            LegGeneration: commitToken?.LegGeneration ?? currentLeg?.Generation ?? 0,
            RuntimeUnlockAdmissionToken: commitToken?.RuntimeUnlockAdmission,
            RouteCommitToken: commitToken,
            RuntimeUnlockTransactionGeneration: commitToken?.TransactionGeneration,
            RuntimeUnlockOfferGeneration: commitToken?.OfferGeneration,
            RuntimeUnlockTunaPathLeaseGeneration: commitToken?.TunaPathLeaseGeneration,
            RuntimeUnlockTunaPathLeaseListenerRunId: commitToken?.TunaPathLeaseListenerRunId,
            RuntimeUnlockProbeId: commitToken?.SamePathTunaProbeAckId,
            RuntimeUnlockProbeAcked: commitToken is not null ? true : null,
            RuntimeUnlockSamePathTunaProbeAckId: commitToken?.SamePathTunaProbeAckId,
            RouteCommitGeneration: commitToken?.CommitGeneration,
            TargetRoute: targetRoute,
            TargetProtocolVersion: targetProtocolVersion,
            TargetTransport: targetTransport,
            RuntimeGeneration: commitToken?.RuntimeGeneration ?? runtimeOwner?.RuntimeGeneration ?? 0,
            TerminalGeneration: commitToken?.TerminalGeneration ?? terminalGeneration);
        return FileTransferCoordinator.AuthorizeAction(state, request);
    }

    private bool TryPublishRouteSelectedIfAuthorizedLocked(
        OutboundTransferContext context,
        FileTransferRouteResolverInput routeInput,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogFileTransferRouteSelected(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.RouteSelection,
            routeInput,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishRouteSelectedIfAuthorizedLocked(
        InboundTransferContext context,
        FileTransferRouteResolverInput routeInput,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogFileTransferRouteSelected(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.RouteSelection,
            routeInput,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishRouteHintIfAuthorizedLocked(
        OutboundTransferContext context,
        string source,
        string reason,
        string observerSource,
        RuntimeUnlockAdmissionDecision? runtimeUnlockAdmission = null,
        Func<RuntimeUnlockAdmissionDecision>? runtimeUnlockAdmissionFactory = null,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        runtimeUnlockAdmission ??= runtimeUnlockAdmissionFactory?.Invoke();
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.RouteSelection,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            observerSource,
            context.CurrentTransferLeg,
            runtimeUnlockAdmission,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishRouteHintIfAuthorizedLocked(
        InboundTransferContext context,
        string source,
        string reason,
        string observerSource,
        RuntimeUnlockAdmissionDecision? runtimeUnlockAdmission = null,
        Func<RuntimeUnlockAdmissionDecision>? runtimeUnlockAdmissionFactory = null,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        runtimeUnlockAdmission ??= runtimeUnlockAdmissionFactory?.Invoke();
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.RouteSelection,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            observerSource,
            context.CurrentTransferLeg,
            runtimeUnlockAdmission,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishRouteTransitionedIfAuthorizedLocked(
        OutboundTransferContext context,
        FileTransferRouteSelection previousRouteSelection,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            context.RouteSelection,
            reason,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishRouteTransitionedIfAuthorizedLocked(
        InboundTransferContext context,
        FileTransferRouteSelection previousRouteSelection,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            context.RouteSelection,
            reason,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishLiveRouteEpochStartedIfAuthorizedLocked(
        OutboundTransferContext context,
        FileTransferRouteSelection previousRouteSelection,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (context.CurrentLiveRouteEpoch is not { } liveRouteEpoch ||
            !TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogLiveRouteEpochStarted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishLiveRouteEpochStartedIfAuthorizedLocked(
        InboundTransferContext context,
        FileTransferRouteSelection previousRouteSelection,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (context.CurrentLiveRouteEpoch is not { } liveRouteEpoch ||
            !TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogLiveRouteEpochStarted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(
        OutboundTransferContext context,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogLiveRouteEpochRecovered(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.CurrentLiveRouteEpoch,
            reason,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(
        InboundTransferContext context,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogLiveRouteEpochRecovered(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.CurrentLiveRouteEpoch,
            reason,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishRuntimeStartedIfAuthorizedLocked(
        OutboundTransferContext context,
        string runtimeRole,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogFileTransferRuntimeStarted(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Outbound,
            runtimeRole,
            context.RouteSelection,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            token,
            rejectionReason);
        return true;
    }

    private bool TryPublishRuntimeStartedIfAuthorizedLocked(
        InboundTransferContext context,
        string runtimeRole,
        string source,
        string reason,
        RouteCommitToken? routeCommitToken = null)
    {
        var actionKind = routeCommitToken is null
            ? FileTransferCoordinatorActionKind.RoutePublication
            : FileTransferCoordinatorActionKind.RouteCommitPublication;
        if (!TryAuthorizeRoutePublicationLocked(context, actionKind, source, reason, routeCommitToken, out var token, out var rejectionReason))
        {
            return false;
        }

        LogFileTransferRuntimeStarted(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeRole,
            context.RouteSelection,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            token,
            rejectionReason);
        return true;
    }

    private static bool TryAuthorizeRepairReplayLocked(
        OutboundTransferContext context,
        RuntimeOwnerToken? token,
        RepairReplayKind repairKind,
        string source,
        string attemptedAction,
        FileTransferLeg? leg,
        long? transportEpochId,
        int? bridgeRecoveryGeneration,
        int? recoveryDecisionGeneration,
        out RepairReplayDecision? repairDecision)
    {
        repairDecision = null;
        if (token is not { } owner)
        {
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RepairReplay,
                source,
                attemptedAction,
                "runtime_owner_missing",
                context.CurrentRuntimeOwner,
                null,
                repairKind,
                null,
                leg,
                transportEpochId,
                bridgeRecoveryGeneration,
                recoveryDecisionGeneration,
                null,
                null,
                null);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                source,
                attemptedAction,
                RuntimeOwner: owner,
                Route: leg?.RouteSelection.Route ?? owner.Route,
                ProtocolVersion: leg?.ProtocolVersion ?? owner.ProtocolVersion,
                LiveRouteEpoch: leg?.LiveRouteEpochId ?? owner.LiveRouteEpoch,
                LegGeneration: leg?.Generation,
                TransportEpochId: transportEpochId,
                BridgeRecoveryGeneration: bridgeRecoveryGeneration,
                RepairReplayKind: repairKind,
                RecoveryDecisionGeneration: recoveryDecisionGeneration));
        if (!decision.Accepted || decision.RepairReplayDecision is null)
        {
            if (IsRecoveryOwnerAuthorityRejection(decision.Accepted ? "repair_replay_decision_missing" : decision.RejectionReason))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentRuntimeOwner,
                    owner,
                    source,
                    attemptedAction,
                    decision.Accepted ? "repair_replay_decision_missing" : decision.RejectionReason);
            }

            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RepairReplay,
                source,
                attemptedAction,
                decision.Accepted ? "repair_replay_decision_missing" : decision.RejectionReason,
                context.CurrentRuntimeOwner,
                owner,
                repairKind,
                null,
                leg,
                transportEpochId,
                bridgeRecoveryGeneration,
                recoveryDecisionGeneration,
                null,
                null,
                null);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        repairDecision = decision.RepairReplayDecision;
        return true;
    }

    private static bool TryAuthorizeRepairReplayLocked(
        InboundTransferContext context,
        RuntimeOwnerToken? token,
        RepairReplayKind repairKind,
        string source,
        string attemptedAction,
        FileTransferLeg? leg,
        long? transportEpochId,
        int? bridgeRecoveryGeneration,
        int? recoveryDecisionGeneration,
        out RepairReplayDecision? repairDecision)
    {
        repairDecision = null;
        if (token is not { } owner)
        {
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RepairReplay,
                source,
                attemptedAction,
                "runtime_owner_missing",
                context.CurrentRuntimeOwner,
                null,
                repairKind,
                null,
                leg,
                transportEpochId,
                bridgeRecoveryGeneration,
                recoveryDecisionGeneration,
                null,
                null,
                null);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                source,
                attemptedAction,
                RuntimeOwner: owner,
                Route: leg?.RouteSelection.Route ?? owner.Route,
                ProtocolVersion: leg?.ProtocolVersion ?? owner.ProtocolVersion,
                LiveRouteEpoch: leg?.LiveRouteEpochId ?? owner.LiveRouteEpoch,
                LegGeneration: leg?.Generation,
                TransportEpochId: transportEpochId,
                BridgeRecoveryGeneration: bridgeRecoveryGeneration,
                RepairReplayKind: repairKind,
                RecoveryDecisionGeneration: recoveryDecisionGeneration));
        if (!decision.Accepted || decision.RepairReplayDecision is null)
        {
            if (IsRecoveryOwnerAuthorityRejection(decision.Accepted ? "repair_replay_decision_missing" : decision.RejectionReason))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentRuntimeOwner,
                    owner,
                    source,
                    attemptedAction,
                    decision.Accepted ? "repair_replay_decision_missing" : decision.RejectionReason);
            }

            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RepairReplay,
                source,
                attemptedAction,
                decision.Accepted ? "repair_replay_decision_missing" : decision.RejectionReason,
                context.CurrentRuntimeOwner,
                owner,
                repairKind,
                null,
                leg,
                transportEpochId,
                bridgeRecoveryGeneration,
                recoveryDecisionGeneration,
                null,
                null,
                null);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        repairDecision = decision.RepairReplayDecision;
        return true;
    }

    private static bool TryAuthorizeTransportEpochActionLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorActionKind actionKind,
        TransportEpochActionKind transportEpochActionKind,
        string source,
        string attemptedAction,
        FileTransferLeg? leg,
        long? transportEpochId,
        int? bridgeRecoveryGeneration,
        int? recoveryDecisionGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport,
        out TransportEpochDecision? transportDecision)
    {
        transportDecision = null;
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                actionKind,
                source,
                attemptedAction,
                "runtime_owner_missing",
                context.CurrentRuntimeOwner,
                null,
                null,
                transportEpochActionKind,
                leg,
                transportEpochId,
                bridgeRecoveryGeneration,
                recoveryDecisionGeneration,
                targetRoute,
                targetProtocolVersion,
                targetTransport);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                actionKind,
                source,
                attemptedAction,
                RuntimeOwner: owner,
                Route: leg?.RouteSelection.Route ?? owner.Route,
                ProtocolVersion: leg?.ProtocolVersion ?? owner.ProtocolVersion,
                LiveRouteEpoch: leg?.LiveRouteEpochId ?? owner.LiveRouteEpoch,
                LegGeneration: leg?.Generation,
                TransportEpochId: transportEpochId,
                BridgeRecoveryGeneration: bridgeRecoveryGeneration,
                TransportEpochActionKind: transportEpochActionKind,
                RecoveryDecisionGeneration: recoveryDecisionGeneration,
                TargetRoute: targetRoute,
                TargetProtocolVersion: targetProtocolVersion,
                TargetTransport: targetTransport));
        if (!decision.Accepted || decision.TransportEpochDecision is null)
        {
            var reason = decision.Accepted ? "transport_epoch_decision_missing" : decision.RejectionReason;
            if (IsRecoveryOwnerAuthorityRejection(reason))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentRuntimeOwner,
                    owner,
                    source,
                    attemptedAction,
                    reason);
            }

            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                actionKind,
                source,
                attemptedAction,
                reason,
                context.CurrentRuntimeOwner,
                owner,
                null,
                transportEpochActionKind,
                leg,
                transportEpochId,
                bridgeRecoveryGeneration,
                recoveryDecisionGeneration,
                targetRoute,
                targetProtocolVersion,
                targetTransport);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        transportDecision = decision.TransportEpochDecision;
        return true;
    }

    private static bool TryAuthorizeTransportEpochActionLocked(
        InboundTransferContext context,
        FileTransferCoordinatorActionKind actionKind,
        TransportEpochActionKind transportEpochActionKind,
        string source,
        string attemptedAction,
        FileTransferLeg? leg,
        long? transportEpochId,
        int? bridgeRecoveryGeneration,
        int? recoveryDecisionGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport,
        out TransportEpochDecision? transportDecision)
    {
        transportDecision = null;
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                actionKind,
                source,
                attemptedAction,
                "runtime_owner_missing",
                context.CurrentRuntimeOwner,
                null,
                null,
                transportEpochActionKind,
                leg,
                transportEpochId,
                bridgeRecoveryGeneration,
                recoveryDecisionGeneration,
                targetRoute,
                targetProtocolVersion,
                targetTransport);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                actionKind,
                source,
                attemptedAction,
                RuntimeOwner: owner,
                Route: leg?.RouteSelection.Route ?? owner.Route,
                ProtocolVersion: leg?.ProtocolVersion ?? owner.ProtocolVersion,
                LiveRouteEpoch: leg?.LiveRouteEpochId ?? owner.LiveRouteEpoch,
                LegGeneration: leg?.Generation,
                TransportEpochId: transportEpochId,
                BridgeRecoveryGeneration: bridgeRecoveryGeneration,
                TransportEpochActionKind: transportEpochActionKind,
                RecoveryDecisionGeneration: recoveryDecisionGeneration,
                TargetRoute: targetRoute,
                TargetProtocolVersion: targetProtocolVersion,
                TargetTransport: targetTransport));
        if (!decision.Accepted || decision.TransportEpochDecision is null)
        {
            var reason = decision.Accepted ? "transport_epoch_decision_missing" : decision.RejectionReason;
            if (IsRecoveryOwnerAuthorityRejection(reason))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentRuntimeOwner,
                    owner,
                    source,
                    attemptedAction,
                    reason);
            }

            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                actionKind,
                source,
                attemptedAction,
                reason,
                context.CurrentRuntimeOwner,
                owner,
                null,
                transportEpochActionKind,
                leg,
                transportEpochId,
                bridgeRecoveryGeneration,
                recoveryDecisionGeneration,
                targetRoute,
                targetProtocolVersion,
                targetTransport);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        transportDecision = decision.TransportEpochDecision;
        return true;
    }

    private static bool TryAuthorizeBridgeRecoveryLocked(
        OutboundTransferContext context,
        FileTransferReceiveRecoveryRequest request,
        string source,
        string attemptedAction,
        out FileTransferReceiveRecoveryRequest authorizedRequest)
    {
        authorizedRequest = request;
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                source,
                attemptedAction,
                "runtime_owner_missing",
                context.CurrentRuntimeOwner,
                null,
                null,
                null,
                context.CurrentTransferLeg,
                request.TransportEpoch > 0 ? request.TransportEpoch : context.CurrentTransferLeg?.TransportEpochId,
                request.BridgeRecoveryGeneration > 0 ? request.BridgeRecoveryGeneration : context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                null,
                null,
                null,
                BridgeRecoveryKind.BridgeReceiveStallRecovery);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            CreateBridgeRecoveryActionRequest(
                request,
                owner,
                context.CurrentTransferLeg,
                source,
                attemptedAction));
        if (!decision.Accepted || decision.BridgeRecoveryDecision is null)
        {
            var reason = decision.Accepted ? "bridge_recovery_decision_missing" : decision.RejectionReason;
            if (IsRecoveryOwnerAuthorityRejection(reason))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentRuntimeOwner,
                    owner,
                    source,
                    attemptedAction,
                    reason);
            }

            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                source,
                attemptedAction,
                reason,
                context.CurrentRuntimeOwner,
                owner,
                null,
                null,
                context.CurrentTransferLeg,
                request.TransportEpoch > 0 ? request.TransportEpoch : context.CurrentTransferLeg?.TransportEpochId,
                request.BridgeRecoveryGeneration > 0 ? request.BridgeRecoveryGeneration : context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                decision.State.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration,
                null,
                null,
                null,
                BridgeRecoveryKind.BridgeReceiveStallRecovery);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        authorizedRequest = AttachBridgeRecoveryAuthority(request, decision.BridgeRecoveryDecision.Value);
        LogFileTransferBridgeRecoveryAuthorityGranted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
            source,
            attemptedAction,
            decision.BridgeRecoveryDecision.Value,
            authorizedRequest);
        return true;
    }

    private static bool TryAuthorizeBridgeRecoveryLocked(
        InboundTransferContext context,
        FileTransferReceiveRecoveryRequest request,
        string source,
        string attemptedAction,
        out FileTransferReceiveRecoveryRequest authorizedRequest)
    {
        authorizedRequest = request;
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                source,
                attemptedAction,
                "runtime_owner_missing",
                context.CurrentRuntimeOwner,
                null,
                null,
                null,
                context.CurrentTransferLeg,
                request.TransportEpoch > 0 ? request.TransportEpoch : context.CurrentTransferLeg?.TransportEpochId,
                request.BridgeRecoveryGeneration > 0 ? request.BridgeRecoveryGeneration : context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                null,
                null,
                null,
                BridgeRecoveryKind.BridgeReceiveStallRecovery);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            CreateBridgeRecoveryActionRequest(
                request,
                owner,
                context.CurrentTransferLeg,
                source,
                attemptedAction));
        if (!decision.Accepted || decision.BridgeRecoveryDecision is null)
        {
            var reason = decision.Accepted ? "bridge_recovery_decision_missing" : decision.RejectionReason;
            if (IsRecoveryOwnerAuthorityRejection(reason))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentRuntimeOwner,
                    owner,
                    source,
                    attemptedAction,
                    reason);
            }

            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                source,
                attemptedAction,
                reason,
                context.CurrentRuntimeOwner,
                owner,
                null,
                null,
                context.CurrentTransferLeg,
                request.TransportEpoch > 0 ? request.TransportEpoch : context.CurrentTransferLeg?.TransportEpochId,
                request.BridgeRecoveryGeneration > 0 ? request.BridgeRecoveryGeneration : context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                decision.State.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration,
                null,
                null,
                null,
                BridgeRecoveryKind.BridgeReceiveStallRecovery);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        authorizedRequest = AttachBridgeRecoveryAuthority(request, decision.BridgeRecoveryDecision.Value);
        LogFileTransferBridgeRecoveryAuthorityGranted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
            source,
            attemptedAction,
            decision.BridgeRecoveryDecision.Value,
            authorizedRequest);
        return true;
    }

    private static FileTransferCoordinatorActionRequest CreateBridgeRecoveryActionRequest(
        FileTransferReceiveRecoveryRequest request,
        RuntimeOwnerToken owner,
        FileTransferLeg? leg,
        string source,
        string attemptedAction)
    {
        var route = TryResolveRequestRoute(request.RouteToken, request.ProtocolVersion, out var routeSelection)
            ? routeSelection.Route
            : leg?.RouteSelection.Route ?? owner.Route;
        var protocolVersion = request.ProtocolVersion > 0
            ? request.ProtocolVersion
            : leg?.ProtocolVersion ?? owner.ProtocolVersion;
        var liveRouteEpoch = request.LiveRouteEpoch > 0
            ? request.LiveRouteEpoch
            : leg?.LiveRouteEpochId ?? owner.LiveRouteEpoch;
        var legGeneration = request.TransferLegGeneration > 0
            ? request.TransferLegGeneration
            : leg?.Generation;
        var transportEpoch = request.TransportEpoch > 0
            ? request.TransportEpoch
            : leg?.TransportEpochId;
        var bridgeRecoveryGeneration = request.BridgeRecoveryGeneration > 0
            ? request.BridgeRecoveryGeneration
            : leg?.BridgeRecoveryGeneration;

        return new FileTransferCoordinatorActionRequest(
            FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
            source,
            attemptedAction,
            RuntimeOwner: owner,
            Route: route,
            ProtocolVersion: protocolVersion,
            LiveRouteEpoch: liveRouteEpoch,
            LegGeneration: legGeneration,
            TransportEpochId: transportEpoch,
            BridgeRecoveryGeneration: bridgeRecoveryGeneration,
            CheckpointRequestId: request.CheckpointRequestId,
            BridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery);
    }

    private static FileTransferReceiveRecoveryRequest AttachBridgeRecoveryAuthority(
        FileTransferReceiveRecoveryRequest request,
        BridgeRecoveryDecision decision)
    {
        var selection = FileTransferRouteResolver.Resolve(decision.CurrentRoute);
        var authority = new FileTransferBridgeRecoveryAuthority(
            request.SessionId,
            request.TransferId,
            request.Direction,
            selection.TelemetryToken,
            decision.CurrentProtocolVersion,
            decision.CurrentLiveRouteEpoch,
            decision.CurrentRuntimeGeneration,
            decision.CurrentTerminalGeneration,
            decision.CurrentLegGeneration,
            decision.TransportEpoch,
            decision.BridgeRecoveryGeneration,
            decision.BridgeDecisionGeneration,
            decision.RecoveryKind.ToString(),
            decision.Reason);

        return request with
        {
            RouteToken = authority.RouteToken,
            ProtocolVersion = authority.ProtocolVersion,
            LiveRouteEpoch = authority.LiveRouteEpoch,
            TransferLegGeneration = authority.TransferLegGeneration,
            TransportEpoch = authority.TransportEpoch,
            BridgeRecoveryGeneration = authority.BridgeRecoveryGeneration,
            AuthorityReason = string.IsNullOrWhiteSpace(request.AuthorityReason)
                ? authority.Reason
                : request.AuthorityReason,
            BridgeRecoveryAuthority = authority,
        };
    }

    private static bool TryResolveRequestRoute(
        string? routeToken,
        int protocolVersion,
        out FileTransferRouteSelection selection)
    {
        selection = default;
        return !string.IsNullOrWhiteSpace(routeToken) &&
               FileTransferRouteResolver.TryNormalizeTelemetryToken(routeToken, protocolVersion, out var normalizedToken) &&
               FileTransferRouteResolver.TryParseTelemetryToken(normalizedToken, out var route) &&
               (selection = FileTransferRouteResolver.Resolve(route)).ProtocolVersion == protocolVersion;
    }

    private static bool TryAuthorizeRecoveryLivenessLeaseLocked(
        OutboundTransferContext context,
        string source,
        string attemptedAction,
        string reason,
        DateTimeOffset livenessDeferralDeadlineUtc,
        string? attemptedRouteToken,
        int? attemptedProtocolVersion,
        int? attemptedLiveRouteEpoch,
        int? attemptedLegGeneration,
        long? attemptedTransportEpoch,
        int? attemptedBridgeRecoveryGeneration,
        out FileTransferRecoveryLivenessDeferralGrant? grant)
    {
        grant = null;
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                source,
                attemptedAction,
                "runtime_owner_missing",
                context.CurrentRuntimeOwner,
                null,
                null,
                null,
                context.CurrentTransferLeg,
                attemptedTransportEpoch ?? context.CurrentTransferLeg?.TransportEpochId,
                attemptedBridgeRecoveryGeneration ?? context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                null,
                null,
                null);
            return false;
        }

        var decision = AuthorizeRecoveryLivenessLease(
            CreateCoordinatorStateLocked(context),
            owner,
            context.CurrentTransferLeg,
            source,
            attemptedAction,
            reason,
            attemptedRouteToken,
            attemptedProtocolVersion,
            attemptedLiveRouteEpoch,
            attemptedLegGeneration,
            attemptedTransportEpoch,
            attemptedBridgeRecoveryGeneration);
        if (!decision.Accepted || decision.RecoveryLivenessLease is null)
        {
            var rejection = decision.Accepted ? "recovery_liveness_lease_missing" : decision.RejectionReason;
            if (IsRecoveryOwnerAuthorityRejection(rejection))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentRuntimeOwner,
                    owner,
                    source,
                    attemptedAction,
                    rejection);
            }

            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                source,
                attemptedAction,
                rejection,
                context.CurrentRuntimeOwner,
                owner,
                null,
                null,
                context.CurrentTransferLeg,
                attemptedTransportEpoch ?? context.CurrentTransferLeg?.TransportEpochId,
                attemptedBridgeRecoveryGeneration ?? context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                decision.State.CurrentRecoveryLivenessLease?.LeaseGeneration,
                null,
                null,
                null);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        grant = CreateRecoveryLivenessDeferralGrant(decision.RecoveryLivenessLease.Value, livenessDeferralDeadlineUtc);
        LogFileTransferRecoveryLivenessLeaseGranted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            source,
            attemptedAction,
            decision.RecoveryLivenessLease.Value,
            livenessDeferralDeadlineUtc);
        return true;
    }

    private static bool TryAuthorizeRecoveryLivenessLeaseLocked(
        InboundTransferContext context,
        string source,
        string attemptedAction,
        string reason,
        DateTimeOffset livenessDeferralDeadlineUtc,
        string? attemptedRouteToken,
        int? attemptedProtocolVersion,
        int? attemptedLiveRouteEpoch,
        int? attemptedLegGeneration,
        long? attemptedTransportEpoch,
        int? attemptedBridgeRecoveryGeneration,
        out FileTransferRecoveryLivenessDeferralGrant? grant)
    {
        grant = null;
        if (context.CurrentRuntimeOwner is not { } owner)
        {
            RejectMissingRuntimeOwnerActionLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                source,
                attemptedAction);
            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                source,
                attemptedAction,
                "runtime_owner_missing",
                context.CurrentRuntimeOwner,
                null,
                null,
                null,
                context.CurrentTransferLeg,
                attemptedTransportEpoch ?? context.CurrentTransferLeg?.TransportEpochId,
                attemptedBridgeRecoveryGeneration ?? context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                null,
                null,
                null);
            return false;
        }

        var decision = AuthorizeRecoveryLivenessLease(
            CreateCoordinatorStateLocked(context),
            owner,
            context.CurrentTransferLeg,
            source,
            attemptedAction,
            reason,
            attemptedRouteToken,
            attemptedProtocolVersion,
            attemptedLiveRouteEpoch,
            attemptedLegGeneration,
            attemptedTransportEpoch,
            attemptedBridgeRecoveryGeneration);
        if (!decision.Accepted || decision.RecoveryLivenessLease is null)
        {
            var rejection = decision.Accepted ? "recovery_liveness_lease_missing" : decision.RejectionReason;
            if (IsRecoveryOwnerAuthorityRejection(rejection))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentRuntimeOwner,
                    owner,
                    source,
                    attemptedAction,
                    rejection);
            }

            LogFileTransferRecoveryAuthorityRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                source,
                attemptedAction,
                rejection,
                context.CurrentRuntimeOwner,
                owner,
                null,
                null,
                context.CurrentTransferLeg,
                attemptedTransportEpoch ?? context.CurrentTransferLeg?.TransportEpochId,
                attemptedBridgeRecoveryGeneration ?? context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                decision.State.CurrentRecoveryLivenessLease?.LeaseGeneration,
                null,
                null,
                null);
            return false;
        }

        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        grant = CreateRecoveryLivenessDeferralGrant(decision.RecoveryLivenessLease.Value, livenessDeferralDeadlineUtc);
        LogFileTransferRecoveryLivenessLeaseGranted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            source,
            attemptedAction,
            decision.RecoveryLivenessLease.Value,
            livenessDeferralDeadlineUtc);
        return true;
    }

    private static FileTransferCoordinatorActionDecision AuthorizeRecoveryLivenessLease(
        FileTransferCoordinatorState state,
        RuntimeOwnerToken owner,
        FileTransferLeg? leg,
        string source,
        string attemptedAction,
        string reason,
        string? attemptedRouteToken,
        int? attemptedProtocolVersion,
        int? attemptedLiveRouteEpoch,
        int? attemptedLegGeneration,
        long? attemptedTransportEpoch,
        int? attemptedBridgeRecoveryGeneration)
    {
        var route = TryResolveRequestRoute(attemptedRouteToken, attemptedProtocolVersion ?? 0, out var routeSelection)
            ? routeSelection.Route
            : leg?.RouteSelection.Route ?? owner.Route;
        return FileTransferCoordinator.AuthorizeAction(
            state,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                source,
                reason,
                RuntimeOwner: owner,
                Route: route,
                ProtocolVersion: attemptedProtocolVersion ?? leg?.ProtocolVersion ?? owner.ProtocolVersion,
                LiveRouteEpoch: attemptedLiveRouteEpoch ?? leg?.LiveRouteEpochId ?? owner.LiveRouteEpoch,
                LegGeneration: attemptedLegGeneration ?? leg?.Generation,
                TransportEpochId: attemptedTransportEpoch ?? leg?.TransportEpochId,
                BridgeRecoveryGeneration: attemptedBridgeRecoveryGeneration ?? leg?.BridgeRecoveryGeneration));
    }

    private static FileTransferRecoveryLivenessDeferralGrant CreateRecoveryLivenessDeferralGrant(
        RecoveryLivenessLease lease,
        DateTimeOffset livenessDeferralDeadlineUtc)
        => new(
            lease.SessionId,
            lease.TransferId,
            lease.Direction,
            lease.RouteToken,
            lease.ProtocolVersion,
            lease.LiveRouteEpoch,
            lease.RuntimeGeneration,
            lease.TerminalGeneration,
            lease.LegGeneration,
            lease.TransportEpoch,
            lease.BridgeRecoveryGeneration,
            lease.LeaseGeneration,
            lease.State.ToString(),
            lease.Reason,
            livenessDeferralDeadlineUtc);

    private static bool TryApplyOutboundCoordinatorLegMutationLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorActionKind actionKind,
        string attemptedAction,
        FileTransferLeg? attemptedLeg,
        string? checkpointRequestId,
        long transportEpoch,
        out FileTransferCoordinatorDecision decision)
    {
        decision = FileTransferCoordinator.Apply(coordinatorEvent, CreateCoordinatorStateLocked(context));
        if (!string.IsNullOrWhiteSpace(decision.MutationRejectedReason))
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                actionKind,
                attemptedAction,
                decision,
                attemptedLeg,
                checkpointRequestId,
                transportEpoch,
                coordinatorEvent.Reason);
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, decision);
        return true;
    }

    private static bool TryApplyInboundCoordinatorLegMutationLocked(
        InboundTransferContext context,
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorActionKind actionKind,
        string attemptedAction,
        FileTransferLeg? attemptedLeg,
        string? checkpointRequestId,
        long transportEpoch,
        out FileTransferCoordinatorDecision decision)
    {
        decision = FileTransferCoordinator.Apply(coordinatorEvent, CreateCoordinatorStateLocked(context));
        if (!string.IsNullOrWhiteSpace(decision.MutationRejectedReason))
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                actionKind,
                attemptedAction,
                decision,
                attemptedLeg,
                checkpointRequestId,
                transportEpoch,
                coordinatorEvent.Reason);
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, decision);
        return true;
    }

    private bool TryAuthorizeRuntimeOwnerActionForDirectionLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        RuntimeOwnerToken token,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        out string rejectionReason)
    {
        rejectionReason = "transfer_not_current";
        if (direction == FileTransferDirection.Outbound &&
            IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
            outboundTransfer is { } outbound)
        {
            return TryAuthorizeRuntimeOwnerActionLocked(
                outbound,
                token,
                actionKind,
                source,
                attemptedAction,
                out rejectionReason);
        }

        if (direction == FileTransferDirection.Inbound &&
            IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
            inboundTransfer is { } inbound)
        {
            return TryAuthorizeRuntimeOwnerActionLocked(
                inbound,
                token,
                actionKind,
                source,
                attemptedAction,
                out rejectionReason);
        }

        RejectStaleRuntimeOwnerUpdateLocked(
            direction,
            transferId,
            sessionId,
            currentOwner: null,
            token,
            source,
            attemptedAction,
            rejectionReason);
        return false;
    }

    private bool TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        RuntimeOwnerToken? runtimeOwner,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        RuntimeUnlockAdmissionToken? admissionToken,
        RouteCommitToken? routeCommitToken,
        long? transactionGeneration,
        long? offerGeneration,
        long? tunaPathLeaseGeneration,
        string? tunaPathLeaseListenerRunId,
        string? probeId,
        bool? probeAcked,
        string? samePathTunaProbeAckId,
        int? routeCommitGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport,
        out string rejectionReason)
    {
        rejectionReason = "transfer_not_current";
        if (direction == FileTransferDirection.Outbound &&
            IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
            outboundTransfer is { } outbound)
        {
            return TryAuthorizeRuntimeUnlockCallbackActionLocked(
                outbound,
                runtimeOwner,
                actionKind,
                source,
                attemptedAction,
                admissionToken,
                routeCommitToken,
                transactionGeneration,
                offerGeneration,
                tunaPathLeaseGeneration,
                tunaPathLeaseListenerRunId,
                probeId,
                probeAcked,
                samePathTunaProbeAckId,
                routeCommitGeneration,
                targetRoute,
                targetProtocolVersion,
                targetTransport,
                out rejectionReason);
        }

        if (direction == FileTransferDirection.Inbound &&
            IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
            inboundTransfer is { } inbound)
        {
            return TryAuthorizeRuntimeUnlockCallbackActionLocked(
                inbound,
                runtimeOwner,
                actionKind,
                source,
                attemptedAction,
                admissionToken,
                routeCommitToken,
                transactionGeneration,
                offerGeneration,
                tunaPathLeaseGeneration,
                tunaPathLeaseListenerRunId,
                probeId,
                probeAcked,
                samePathTunaProbeAckId,
                routeCommitGeneration,
                targetRoute,
                targetProtocolVersion,
                targetTransport,
                out rejectionReason);
        }

        LogRuntimeUnlockCallbackRejected(
            direction,
            transferId,
            sessionId,
            actionKind,
            source,
            attemptedAction,
            rejectionReason,
            currentOwner: null,
            attemptedOwner: runtimeOwner ?? admissionToken?.RuntimeOwner ?? routeCommitToken?.RuntimeOwner,
            admissionToken,
            routeCommitToken,
            transactionGeneration,
            offerGeneration,
            tunaPathLeaseGeneration,
            tunaPathLeaseListenerRunId,
            probeId,
            probeAcked,
            samePathTunaProbeAckId,
            routeCommitGeneration,
            targetRoute,
            targetProtocolVersion,
            targetTransport,
            currentRoute: null,
            currentProtocolVersion: 0,
            currentLiveRouteEpoch: 0,
            currentRuntimeGeneration: 0,
            currentTerminalGeneration: 0,
            currentLegGeneration: 0);
        return false;
    }

    private static bool TryAuthorizeRuntimeUnlockCallbackActionLocked(
        OutboundTransferContext context,
        RuntimeOwnerToken? runtimeOwner,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        RuntimeUnlockAdmissionToken? admissionToken,
        RouteCommitToken? routeCommitToken,
        long? transactionGeneration,
        long? offerGeneration,
        long? tunaPathLeaseGeneration,
        string? tunaPathLeaseListenerRunId,
        string? probeId,
        bool? probeAcked,
        string? samePathTunaProbeAckId,
        int? routeCommitGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport,
        out string rejectionReason)
        => TryAuthorizeRuntimeUnlockCallbackActionLocked(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            CreateCoordinatorStateLocked(context),
            context.CurrentRuntimeOwner,
            runtimeOwner,
            actionKind,
            source,
            attemptedAction,
            admissionToken ?? context.RuntimeUnlockAdmissionToken,
            routeCommitToken ?? context.RuntimeUnlockRouteCommitToken,
            transactionGeneration,
            offerGeneration,
            tunaPathLeaseGeneration,
            tunaPathLeaseListenerRunId,
            probeId,
            probeAcked,
            samePathTunaProbeAckId,
            routeCommitGeneration,
            targetRoute,
            targetProtocolVersion,
            targetTransport,
            out rejectionReason);

    private static bool TryAuthorizeRuntimeUnlockCallbackActionLocked(
        InboundTransferContext context,
        RuntimeOwnerToken? runtimeOwner,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        RuntimeUnlockAdmissionToken? admissionToken,
        RouteCommitToken? routeCommitToken,
        long? transactionGeneration,
        long? offerGeneration,
        long? tunaPathLeaseGeneration,
        string? tunaPathLeaseListenerRunId,
        string? probeId,
        bool? probeAcked,
        string? samePathTunaProbeAckId,
        int? routeCommitGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport,
        out string rejectionReason)
        => TryAuthorizeRuntimeUnlockCallbackActionLocked(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            CreateCoordinatorStateLocked(context),
            context.CurrentRuntimeOwner,
            runtimeOwner,
            actionKind,
            source,
            attemptedAction,
            admissionToken ?? context.RuntimeUnlockAdmissionToken,
            routeCommitToken ?? context.RuntimeUnlockRouteCommitToken,
            transactionGeneration,
            offerGeneration,
            tunaPathLeaseGeneration,
            tunaPathLeaseListenerRunId,
            probeId,
            probeAcked,
            samePathTunaProbeAckId,
            routeCommitGeneration,
            targetRoute,
            targetProtocolVersion,
            targetTransport,
            out rejectionReason);

    private static bool TryAuthorizeRuntimeUnlockCallbackActionLocked(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferCoordinatorState coordinatorState,
        RuntimeOwnerToken? currentOwner,
        RuntimeOwnerToken? runtimeOwner,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        RuntimeUnlockAdmissionToken? admissionToken,
        RouteCommitToken? routeCommitToken,
        long? transactionGeneration,
        long? offerGeneration,
        long? tunaPathLeaseGeneration,
        string? tunaPathLeaseListenerRunId,
        string? probeId,
        bool? probeAcked,
        string? samePathTunaProbeAckId,
        int? routeCommitGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport,
        out string rejectionReason)
    {
        var attemptedOwner = runtimeOwner ??
            admissionToken?.RuntimeOwner ??
            routeCommitToken?.RuntimeOwner ??
            currentOwner;
        var decision = FileTransferCoordinator.AuthorizeAction(
            coordinatorState,
            new FileTransferCoordinatorActionRequest(
                actionKind,
                source,
                attemptedAction,
                RuntimeOwner: attemptedOwner,
                RuntimeUnlockAdmissionToken: admissionToken,
                RouteCommitToken: routeCommitToken,
                RuntimeUnlockTransactionGeneration: transactionGeneration,
                RuntimeUnlockOfferGeneration: offerGeneration,
                RuntimeUnlockTunaPathLeaseGeneration: tunaPathLeaseGeneration,
                RuntimeUnlockTunaPathLeaseListenerRunId: tunaPathLeaseListenerRunId,
                RuntimeUnlockProbeId: probeId,
                RuntimeUnlockProbeAcked: probeAcked,
                RuntimeUnlockSamePathTunaProbeAckId: samePathTunaProbeAckId,
                RouteCommitGeneration: routeCommitGeneration,
                TargetRoute: targetRoute,
                TargetProtocolVersion: targetProtocolVersion,
                TargetTransport: targetTransport));
        rejectionReason = decision.Accepted ? "current" : decision.RejectionReason;
        if (!decision.Accepted)
        {
            if (IsRuntimeUnlockCallbackStaleOwnerOrTokenRejection(decision.RejectionReason))
            {
                RejectStaleRuntimeOwnerUpdateLocked(
                    direction,
                    transferId,
                    sessionId,
                    currentOwner,
                    attemptedOwner,
                    source,
                    attemptedAction,
                    decision.RejectionReason);
            }

            LogRuntimeUnlockCallbackRejected(
                direction,
                transferId,
                sessionId,
                actionKind,
                source,
                attemptedAction,
                decision.RejectionReason,
                currentOwner,
                attemptedOwner,
                admissionToken,
                routeCommitToken,
                transactionGeneration,
                offerGeneration,
                tunaPathLeaseGeneration,
                tunaPathLeaseListenerRunId,
                probeId,
                probeAcked,
                samePathTunaProbeAckId,
                routeCommitGeneration,
                targetRoute,
                targetProtocolVersion,
                targetTransport,
                decision.CurrentRoute,
                decision.CurrentProtocolVersion,
                decision.CurrentLiveRouteEpoch,
                decision.CurrentRuntimeGeneration,
                decision.CurrentTerminalGeneration,
                decision.CurrentLegGeneration);
        }

        return decision.Accepted;
    }

    private static bool IsRuntimeUnlockCallbackStaleOwnerOrTokenRejection(string reason)
        => reason is "runtime_owner_missing" or
                     "runtime_generation_mismatch" or
                     "terminal_generation_mismatch" or
                     "route_or_protocol_mismatch" or
                     "live_route_epoch_mismatch" or
                     "session_transfer_or_direction_mismatch" or
                     "runtime_unlock_admission_missing" or
                     "runtime_unlock_admission_generation_mismatch" or
                     "route_commit_token_missing" or
                     "route_commit_generation_mismatch";

    private static bool IsRecoveryOwnerAuthorityRejection(string reason)
        => reason is "runtime_owner_missing" or
                     "runtime_generation_mismatch" or
                     "terminal_generation_mismatch" or
                     "route_or_protocol_mismatch" or
                     "live_route_epoch_mismatch" or
                     "session_transfer_or_direction_mismatch";

    private static void RejectMissingRuntimeOwnerActionLocked(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        RuntimeOwnerToken? currentOwner,
        string source,
        string attemptedAction)
        => RejectStaleRuntimeOwnerUpdateLocked(
            direction,
            transferId,
            sessionId,
            currentOwner,
            attemptedOwner: null,
            source,
            attemptedAction,
            "runtime_owner_missing");

    private static void RejectStaleRuntimeOwnerUpdateLocked(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        RuntimeOwnerToken? currentOwner,
        RuntimeOwnerToken? attemptedOwner,
        string source,
        string attemptedAction,
        string reason)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_runtime_owner_stale_update_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; source={FormatProtocolLogValue(source)}; attempted_action={FormatProtocolLogValue(attemptedAction)}; reason={FormatProtocolLogValue(reason)}; runtime_generation={attemptedOwner?.RuntimeGeneration ?? 0}; current_runtime_generation={currentOwner?.RuntimeGeneration ?? 0}; terminal_generation={attemptedOwner?.TerminalGeneration ?? 0}; current_terminal_generation={currentOwner?.TerminalGeneration ?? 0}; route={FormatProtocolLogValue(attemptedOwner?.RouteToken ?? "(none)")}; current_route={FormatProtocolLogValue(currentOwner?.RouteToken ?? "(none)")}; protocol_version={attemptedOwner?.ProtocolVersion ?? 0}; current_protocol_version={currentOwner?.ProtocolVersion ?? 0}; live_route_epoch={attemptedOwner?.LiveRouteEpoch ?? 0}; current_live_route_epoch={currentOwner?.LiveRouteEpoch ?? 0}; runtime_family={FormatProtocolLogValue(attemptedOwner?.RuntimeFamily ?? "(none)")}; current_runtime_family={FormatProtocolLogValue(currentOwner?.RuntimeFamily ?? "(none)")}");

    private static void LogRuntimeUnlockCallbackRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        string rejectionReason,
        RuntimeOwnerToken? currentOwner,
        RuntimeOwnerToken? attemptedOwner,
        RuntimeUnlockAdmissionToken? admissionToken,
        RouteCommitToken? routeCommitToken,
        long? transactionGeneration,
        long? offerGeneration,
        long? tunaPathLeaseGeneration,
        string? tunaPathLeaseListenerRunId,
        string? probeId,
        bool? probeAcked,
        string? samePathTunaProbeAckId,
        int? routeCommitGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport,
        FileTransferRoute? currentRoute,
        int currentProtocolVersion,
        int currentLiveRouteEpoch,
        int currentRuntimeGeneration,
        int currentTerminalGeneration,
        int currentLegGeneration)
    {
        var attemptedRoute = targetRoute is { } explicitTargetRoute
            ? FormatRuntimeUnlockCallbackRouteToken(explicitTargetRoute)
            : routeCommitToken is { } commitToken
                ? FormatRuntimeUnlockCallbackRouteToken(commitToken.TargetRoute)
                : attemptedOwner?.RouteToken ?? admissionToken?.RouteToken ?? "(none)";
        var attemptedProtocolVersion = targetProtocolVersion ??
            routeCommitToken?.TargetProtocolVersion ??
            attemptedOwner?.ProtocolVersion ??
            admissionToken?.ProtocolVersion ??
            0;
        var attemptedLiveRouteEpoch = attemptedOwner?.LiveRouteEpoch ??
            admissionToken?.LiveRouteEpoch ??
            routeCommitToken?.LiveRouteEpoch ??
            0;
        var attemptedRuntimeGeneration = attemptedOwner?.RuntimeGeneration ??
            admissionToken?.RuntimeGeneration ??
            routeCommitToken?.RuntimeGeneration ??
            0;
        var attemptedTerminalGeneration = attemptedOwner?.TerminalGeneration ??
            admissionToken?.TerminalGeneration ??
            routeCommitToken?.TerminalGeneration ??
            0;
        var attemptedLegGeneration = admissionToken?.LegGeneration ??
            routeCommitToken?.LegGeneration ??
            0;

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_callback_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; action_kind={FormatProtocolLogValue(actionKind.ToString())}; source={FormatProtocolLogValue(source)}; attempted_action={FormatProtocolLogValue(attemptedAction)}; rejection_reason={FormatProtocolLogValue(rejectionReason)}; reason={FormatProtocolLogValue(attemptedAction)}; current_route={FormatProtocolLogValue(currentRoute is { } route ? FormatRuntimeUnlockCallbackRouteToken(route) : currentOwner?.RouteToken ?? "(none)")}; current_protocol_version={currentProtocolVersion}; current_live_route_epoch={currentLiveRouteEpoch}; current_runtime_generation={currentRuntimeGeneration}; current_terminal_generation={currentTerminalGeneration}; current_leg_generation={currentLegGeneration}; attempted_route={FormatProtocolLogValue(attemptedRoute)}; attempted_protocol_version={attemptedProtocolVersion}; attempted_live_route_epoch={attemptedLiveRouteEpoch}; attempted_runtime_generation={attemptedRuntimeGeneration}; attempted_terminal_generation={attemptedTerminalGeneration}; attempted_leg_generation={attemptedLegGeneration}; transaction_generation={transactionGeneration ?? 0}; offer_generation={offerGeneration ?? 0}; tuna_path_lease_generation={tunaPathLeaseGeneration ?? 0}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(tunaPathLeaseListenerRunId ?? "(none)")}; probe_id={FormatProtocolLogValue(probeId ?? "(none)")}; probe_acked={(probeAcked == true ? 1 : 0)}; same_path_tuna_probe_ack_id={FormatProtocolLogValue(samePathTunaProbeAckId ?? "(none)")}; route_commit_generation={routeCommitGeneration ?? routeCommitToken?.CommitGeneration ?? 0}; target_route={FormatProtocolLogValue(targetRoute is { } routeTarget ? FormatRuntimeUnlockCallbackRouteToken(routeTarget) : routeCommitToken is { } token ? FormatRuntimeUnlockCallbackRouteToken(token.TargetRoute) : "(none)")}; target_protocol_version={targetProtocolVersion ?? routeCommitToken?.TargetProtocolVersion ?? 0}; target_transport={FormatProtocolLogValue(targetTransport is { } transportKind ? FormatFileTransferTransportKind(transportKind) : routeCommitToken is { } routeToken ? FormatFileTransferTransportKind(routeToken.TargetTransport) : "(none)")}; admission_generation={admissionToken?.AdmissionGeneration ?? 0}; route_commit_token_generation={routeCommitToken?.CommitGeneration ?? 0}");
    }

    private static void LogFileTransferRoutePublicationRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string reason,
        string rejectionReason,
        RuntimeOwnerToken? currentOwner,
        RuntimeOwnerToken? attemptedOwner,
        FileTransferRouteSelection routeSelection,
        LiveRouteEpoch? liveRouteEpoch,
        FileTransferLeg? currentLeg,
        RouteCommitToken? routeCommitToken,
        FileTransferCoordinatorActionDecision decision)
    {
        var targetRoute = routeCommitToken?.TargetRoute ?? routeSelection.Route;
        var targetProtocolVersion = routeCommitToken?.TargetProtocolVersion ?? routeSelection.ProtocolVersion;
        var targetTransport = routeCommitToken?.TargetTransport ?? ResolveRoutePublicationTargetTransport(targetRoute);
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_route_publication_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; action_kind={FormatProtocolLogValue(actionKind.ToString())}; source={FormatProtocolLogValue(source)}; reason={FormatProtocolLogValue(reason)}; rejection_reason={FormatProtocolLogValue(rejectionReason)}; current_route={FormatProtocolLogValue(FormatRuntimeUnlockCallbackRouteToken(decision.CurrentRoute))}; current_protocol_version={decision.CurrentProtocolVersion}; current_live_route_epoch={decision.CurrentLiveRouteEpoch}; current_runtime_generation={decision.CurrentRuntimeGeneration}; current_terminal_generation={decision.CurrentTerminalGeneration}; current_leg_generation={decision.CurrentLegGeneration}; attempted_route={FormatProtocolLogValue(routeCommitToken?.RouteToken ?? routeSelection.TelemetryToken)}; attempted_protocol_version={routeCommitToken?.ProtocolVersion ?? routeSelection.ProtocolVersion}; attempted_live_route_epoch={routeCommitToken?.LiveRouteEpoch ?? liveRouteEpoch?.EpochId ?? 0}; attempted_runtime_generation={routeCommitToken?.RuntimeGeneration ?? attemptedOwner?.RuntimeGeneration ?? 0}; attempted_terminal_generation={routeCommitToken?.TerminalGeneration ?? attemptedOwner?.TerminalGeneration ?? 0}; attempted_leg_generation={routeCommitToken?.LegGeneration ?? currentLeg?.Generation ?? 0}; route_commit_generation={routeCommitToken?.CommitGeneration ?? 0}; route_publication_generation={decision.State.LastRoutePublicationGeneration}; target_route={FormatProtocolLogValue(FormatRuntimeUnlockCallbackRouteToken(targetRoute))}; target_protocol_version={targetProtocolVersion}; target_transport={FormatProtocolLogValue(FormatFileTransferTransportKind(targetTransport))}; runtime_generation={attemptedOwner?.RuntimeGeneration ?? 0}; terminal_generation={attemptedOwner?.TerminalGeneration ?? 0}; leg_generation={currentLeg?.Generation ?? 0}; coordinator_decision_reason={FormatProtocolLogValue(rejectionReason)}; current_owner_runtime_generation={currentOwner?.RuntimeGeneration ?? 0}");
    }

    private static string FormatRuntimeUnlockCallbackRouteToken(FileTransferRoute route)
        => FileTransferRouteResolver.Resolve(route).TelemetryToken;

    private static void LogFileTransferRecoveryAuthorityRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        string rejectionReason,
        RuntimeOwnerToken? currentOwner,
        RuntimeOwnerToken? attemptedOwner,
        RepairReplayKind? repairReplayKind,
        TransportEpochActionKind? transportEpochActionKind,
        FileTransferLeg? attemptedLeg,
        long? transportEpoch,
        int? bridgeRecoveryGeneration,
        int? recoveryDecisionGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport,
        BridgeRecoveryKind? bridgeRecoveryKind = null,
        int? recoveryLivenessLeaseGeneration = null)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_recovery_authority_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; action_kind={FormatProtocolLogValue(actionKind.ToString())}; source={FormatProtocolLogValue(source)}; attempted_action={FormatProtocolLogValue(attemptedAction)}; rejection_reason={FormatProtocolLogValue(rejectionReason)}; reason={FormatProtocolLogValue(rejectionReason)}; repair_replay_kind={FormatProtocolLogValue(repairReplayKind?.ToString() ?? "(none)")}; transport_epoch_action_kind={FormatProtocolLogValue(transportEpochActionKind?.ToString() ?? "(none)")}; bridge_recovery_kind={FormatProtocolLogValue(bridgeRecoveryKind?.ToString() ?? "(none)")}; current_route={FormatProtocolLogValue(currentOwner?.RouteToken ?? "(none)")}; current_protocol_version={currentOwner?.ProtocolVersion ?? 0}; current_live_route_epoch={currentOwner?.LiveRouteEpoch ?? 0}; current_runtime_generation={currentOwner?.RuntimeGeneration ?? 0}; current_terminal_generation={currentOwner?.TerminalGeneration ?? 0}; attempted_route={FormatProtocolLogValue(attemptedLeg?.RouteSelection.TelemetryToken ?? attemptedOwner?.RouteToken ?? "(none)")}; attempted_protocol_version={attemptedLeg?.ProtocolVersion ?? attemptedOwner?.ProtocolVersion ?? 0}; attempted_live_route_epoch={attemptedLeg?.LiveRouteEpochId ?? attemptedOwner?.LiveRouteEpoch ?? 0}; attempted_runtime_generation={attemptedOwner?.RuntimeGeneration ?? 0}; attempted_terminal_generation={attemptedOwner?.TerminalGeneration ?? 0}; attempted_leg_generation={attemptedLeg?.Generation ?? 0}; attempted_transport_epoch={transportEpoch ?? attemptedLeg?.TransportEpochId ?? 0}; attempted_bridge_recovery_generation={bridgeRecoveryGeneration ?? attemptedLeg?.BridgeRecoveryGeneration ?? 0}; recovery_decision_generation={recoveryDecisionGeneration ?? 0}; recovery_liveness_lease_generation={recoveryLivenessLeaseGeneration ?? 0}; target_route={FormatProtocolLogValue(targetRoute is { } route ? FileTransferRouteResolver.Resolve(route).TelemetryToken : "(none)")}; target_protocol_version={targetProtocolVersion ?? 0}; target_transport={FormatProtocolLogValue(targetTransport is { } transport ? FormatFileTransferTransportKind(transport) : "(none)")}");

    private static void LogFileTransferBridgeRecoveryAuthorityGranted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferCoordinatorActionKind actionKind,
        string source,
        string attemptedAction,
        BridgeRecoveryDecision decision,
        FileTransferReceiveRecoveryRequest request)
    {
        var routeToken = FileTransferRouteResolver.Resolve(decision.CurrentRoute).TelemetryToken;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bridge_recovery_authority_granted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; action_kind={FormatProtocolLogValue(actionKind.ToString())}; source={FormatProtocolLogValue(source)}; attempted_action={FormatProtocolLogValue(attemptedAction)}; reason={FormatProtocolLogValue(decision.Reason)}; route={FormatProtocolLogValue(routeToken)}; protocol_version={decision.CurrentProtocolVersion}; live_route_epoch={decision.CurrentLiveRouteEpoch}; runtime_generation={decision.CurrentRuntimeGeneration}; terminal_generation={decision.CurrentTerminalGeneration}; leg_generation={decision.CurrentLegGeneration}; transport_epoch={decision.TransportEpoch}; bridge_recovery_generation={decision.BridgeRecoveryGeneration}; bridge_decision_generation={decision.BridgeDecisionGeneration}; bridge_recovery_kind={FormatProtocolLogValue(decision.RecoveryKind.ToString())}; checkpoint_request_id={FormatProtocolLogValue(request.CheckpointRequestId ?? "(none)")}");
    }

    private static void LogFileTransferRecoveryLivenessLeaseGranted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string source,
        string attemptedAction,
        RecoveryLivenessLease lease,
        DateTimeOffset livenessDeferralDeadlineUtc)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_recovery_liveness_lease_granted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; action_kind={FormatProtocolLogValue(FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin.ToString())}; source={FormatProtocolLogValue(source)}; attempted_action={FormatProtocolLogValue(attemptedAction)}; reason={FormatProtocolLogValue(lease.Reason)}; route={FormatProtocolLogValue(lease.RouteToken)}; protocol_version={lease.ProtocolVersion}; live_route_epoch={lease.LiveRouteEpoch}; runtime_generation={lease.RuntimeGeneration}; terminal_generation={lease.TerminalGeneration}; leg_generation={lease.LegGeneration}; transport_epoch={lease.TransportEpoch}; bridge_recovery_generation={lease.BridgeRecoveryGeneration}; recovery_liveness_lease_generation={lease.LeaseGeneration}; liveness_deferral_deadline_utc_ms={livenessDeferralDeadlineUtc.ToUnixTimeMilliseconds()}");

    private static void LogFileTransferCoordinatorLegMutationRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferCoordinatorActionKind actionKind,
        string attemptedAction,
        FileTransferCoordinatorDecision decision,
        FileTransferLeg? attemptedLeg,
        string? checkpointRequestId,
        long transportEpoch,
        string reason)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_coordinator_leg_mutation_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; action_kind={FormatProtocolLogValue(actionKind.ToString())}; attempted_action={FormatProtocolLogValue(attemptedAction)}; rejection_reason={FormatProtocolLogValue(decision.MutationRejectedReason ?? "mutation_rejected")}; current_route={FormatProtocolLogValue(decision.State.RouteSelection.TelemetryToken)}; current_protocol_version={decision.State.RouteSelection.ProtocolVersion}; current_live_route_epoch={decision.State.CurrentLiveRouteEpoch?.EpochId ?? 0}; current_runtime_generation={decision.State.CurrentRuntimeOwner?.RuntimeGeneration ?? 0}; current_terminal_generation={decision.State.TerminalGeneration}; current_leg_generation={decision.State.CurrentLeg?.Generation ?? 0}; attempted_route={FormatProtocolLogValue(attemptedLeg?.RouteSelection.TelemetryToken ?? "(none)")}; attempted_protocol_version={attemptedLeg?.ProtocolVersion ?? 0}; attempted_live_route_epoch={attemptedLeg?.LiveRouteEpochId ?? 0}; attempted_leg_generation={attemptedLeg?.Generation ?? 0}; attempted_transport_epoch={transportEpoch}; attempted_checkpoint_request_id={FormatProtocolLogValue(checkpointRequestId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");

    private static void LogInitialTunaRouteSelectionDecision(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        InitialTunaRouteSelectionDecision? decision)
    {
        if (decision is not { } initialTuna)
        {
            return;
        }

        var evidence = initialTuna.Evidence;
        var eventName = initialTuna.Accepted
            ? "filetransfer_initial_tuna_route_selection_granted"
            : "filetransfer_initial_tuna_route_selection_denied";
        var message =
            $"event={eventName}; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; requested_route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(initialTuna.RequestedRoute).TelemetryToken)}; requested_protocol_version={initialTuna.RequestedProtocolVersion}; requested_transport={FormatProtocolLogValue(FormatFileTransferTransportKind(initialTuna.RequestedTransport))}; selected_route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(initialTuna.SelectedRoute).TelemetryToken)}; selected_protocol_version={initialTuna.SelectedProtocolVersion}; fallback_applied={(initialTuna.FallbackApplied ? 1 : 0)}; coordinator_reason={FormatProtocolLogValue(initialTuna.Reason)}; evidence_session_id={FormatProtocolLogValue(evidence?.SessionId ?? "(none)")}; evidence_transfer_id={FormatProtocolLogValue(evidence?.TransferId ?? "(none)")}; lease_generation={evidence?.LeaseGeneration ?? 0}; listener_run_id={FormatProtocolLogValue(evidence?.ListenerRunId ?? "(none)")}; lease_current={(evidence?.LeaseCurrent == true ? 1 : 0)}; listener_ready={(evidence?.ListenerReady == true ? 1 : 0)}; offer_generation={evidence?.OfferGeneration ?? 0}; peer_visible_proof={(evidence?.PeerVisibleProof == true ? 1 : 0)}; answer_received={(evidence?.AnswerReceived == true ? 1 : 0)}; answer_ack_received={(evidence?.AnswerAckReceived == true ? 1 : 0)}; tuna_negotiated_observed={(evidence?.TunaNegotiatedObserved == true ? 1 : 0)}";
        if (initialTuna.Accepted)
        {
            LocalOperationalLog.Info("FileTransferService", message);
        }
        else
        {
            LocalOperationalLog.Warn("FileTransferService", message);
        }
    }

    private static void RetireRuntimeOwnedWorkersLocked(
        OutboundTransferContext context,
        RuntimeOwnerToken? retiredOwner,
        string reason)
    {
        if (retiredOwner is null)
        {
            return;
        }

        ClearOutboundRuntimeUnlockFallbackSeparationStateLocked(context);
        context.PullV4PeerSilenceDeferralUtc = null;
        context.V6EpochLivenessDeferralUtc = null;
        context.V6PeerLivenessRecoveryDeferredUtc = null;
        context.V6LastReceiveRecoveryRequestedUtc = null;
        context.V6LastFeedbackStallRecoverySuppressedUtc = null;
        context.PullV4LastRegularNknControlFeedbackPressureObservedUtc = null;
        context.PullRegularNknV4LastPeerSilenceSafetyReplayUtc = null;
        context.PullRegularNknV4LastPeerSilenceSafetyReplayFrontierChunkIndex = -1;
        context.PullRegularNknV4LastPeerSilenceSafetyReplayEndChunkIndex = -1;
        context.PullRegularNknV4PeerSilenceSafetyReplayCount = 0;
        context.PullTransportLastSafetyReplayGeneration = 0;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = -1;
        context.PullTransportLastSafetyReplayEndChunkIndex = -1;
        context.PullTransportLastSafetyReplayUtc = null;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        ClearOutboundFallbackTailQuiescenceDiagnosticLocked(context);
        context.PullTransportResumeRequestPending = false;
        context.V6RegularNknStateRefreshSendInFlight = 0;
        context.V6RegularNknStateRefreshActiveSendGeneration = 0;
        context.V6RegularNknStateRefreshActiveRequestId = null;
        context.V6RegularNknStateRefreshActivePriority = null;
        context.V6RegularNknStateRefreshActiveStartedUtc = null;
        context.V6RegularNknStateRefreshActiveReusedPendingCheckpoint = false;
        context.V6RegularNknCheckpointSyncSendInFlight = 0;
        ClearOutboundV6RegularNknDeferredStateRefreshLocked(context);
        ClearOutboundFallbackCheckpointRepairAuthorityLocked(context);
        context.V6RegularNknFallbackCheckpointDeliveryRecoveryPending = false;
        context.V6RegularNknFallbackCheckpointDeliveryRecoveryStartedUtc = null;
        context.V6RegularNknFallbackCheckpointDeliveryRecoveryReason = null;
        context.V6RegularNknFallbackCheckpointDeliveryRecoveryRequestId = null;
        context.PullV4SenderPumpRepairQueue.Clear();
        context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
        foreach (var repairState in context.PullV4SenderPumpRepairRequests.Values)
        {
            repairState.Queued = false;
            repairState.QueuedUtc = null;
            repairState.InFlight = false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_owner_workers_retired; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; retired_runtime_generation={retiredOwner.Value.RuntimeGeneration}; current_runtime_generation={context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0}; retired_route={FormatProtocolLogValue(retiredOwner.Value.RouteToken)}; current_route={FormatProtocolLogValue(context.CurrentRuntimeOwner?.RouteToken ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void RetireRuntimeOwnedWorkersLocked(
        InboundTransferContext context,
        RuntimeOwnerToken? retiredOwner,
        string reason)
    {
        if (retiredOwner is null)
        {
            return;
        }

        ClearInboundRuntimeUnlockFallbackSeparationStateLocked(context);
        context.V6EpochLivenessDeferralUtc = null;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindRecoveredLogged = false;
        ClearInboundV6PostTunaFallbackProofReplayLocked(context);
        context.V6PostTunaFallbackProofReplayReceiverStatePending = false;
        context.V6PostTunaFallbackProofReplayFrontierRequestPending = false;
        context.V6LastReceiverStateSentUtc = null;
        context.V6LastFrontierRequestSentUtc = null;
        context.V6LastFrontierRequestChunkIndex = -1;
        context.V6LastFrontierRequestId = null;

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_owner_workers_retired; direction=inbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; retired_runtime_generation={retiredOwner.Value.RuntimeGeneration}; current_runtime_generation={context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0}; retired_route={FormatProtocolLogValue(retiredOwner.Value.RouteToken)}; current_route={FormatProtocolLogValue(context.CurrentRuntimeOwner?.RouteToken ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");
    }

    private static FileTransferLeg? StartOutboundTransferLegLocked(
        OutboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason,
        FileTransferLegState state,
        bool canSendData,
        bool freezeCurrent = true,
        long? transportEpochOverride = null,
        int? bridgeRecoveryGenerationOverride = null,
        FileTransferInitialTunaReadinessEvidence? initialTunaReadinessEvidence = null,
        bool allowInitialRouteFallback = false,
        FileTransferRouteSelection? initialRouteFallbackSelection = null)
    {
        var retiredLeg = context.CurrentTransferLeg;
        var committedChunk = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var transportEpoch = transportEpochOverride ?? (routeSelection.Route == FileTransferRoute.PostTunaFallbackV6
            ? ResolveOutboundFallbackCurrentTransportEpochLocked(context)
            : context.V6TransportEpoch?.EpochId ?? Math.Max(0, context.PullTransportRebindGeneration));
        var bridgeRecoveryGeneration = bridgeRecoveryGenerationOverride ?? Math.Max(0, context.PullTransportRebindGeneration);
        var retiredOwner = context.CurrentRuntimeOwner;
        var decision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                routeSelection,
                FileTransferTransportHandoffKind.None,
                routeSelection.Route == FileTransferRoute.FileTunaV4
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.Unknown,
                reason,
                state,
                canSendData,
                committedChunk,
                Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                transportEpoch,
                bridgeRecoveryGeneration,
                FreezeCurrentLeg: freezeCurrent,
                InitialTunaReadinessEvidence: initialTunaReadinessEvidence,
                AllowInitialRouteFallback: allowInitialRouteFallback,
                InitialRouteFallbackSelection: initialRouteFallbackSelection),
            CreateCoordinatorStateLocked(context));
        LogInitialTunaRouteSelectionDecision(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            decision.InitialTunaRouteSelectionDecision);
        if (decision.StartedLeg is not { } leg)
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.StartTransferLeg,
                "start_transfer_leg",
                decision,
                retiredLeg,
                null,
                transportEpoch,
                reason);
            return null;
        }

        ApplyCoordinatorDecisionToContextLocked(context, decision);
        LogTransferLegFrozenLocked(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            decision.FrozenLeg,
            reason);
        if (retiredOwner is { } owner &&
            context.CurrentRuntimeOwner is { } currentOwner &&
            owner.RuntimeGeneration != currentOwner.RuntimeGeneration)
        {
            RetireOutboundFallbackControlPlaneExchangeLocked(
                context,
                retiredLeg,
                leg.Generation,
                leg.CheckpointRequestId,
                reason);
            RetireRuntimeOwnedWorkersLocked(context, owner, reason);
        }

        LogFileTransferLegStarted(FileTransferDirection.Outbound, context.TransferId, context.SessionId, leg, reason);
        return leg;
    }

    private static FileTransferLeg? StartInboundTransferLegLocked(
        InboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason,
        FileTransferLegState state,
        bool canSendData,
        bool freezeCurrent = true,
        long? transportEpochOverride = null,
        int? bridgeRecoveryGenerationOverride = null,
        FileTransferInitialTunaReadinessEvidence? initialTunaReadinessEvidence = null)
    {
        var retiredLeg = context.CurrentTransferLeg;
        var committedChunk = Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var transportEpoch = transportEpochOverride ?? context.V6TransportEpoch?.EpochId ?? Math.Max(0, context.PullTransportRebindGeneration);
        var bridgeRecoveryGeneration = bridgeRecoveryGenerationOverride ?? Math.Max(0, context.PullTransportRebindGeneration);
        var retiredOwner = context.CurrentRuntimeOwner;
        var decision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                routeSelection,
                FileTransferTransportHandoffKind.None,
                routeSelection.Route == FileTransferRoute.FileTunaV4
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.Unknown,
                reason,
                state,
                canSendData,
                committedChunk,
                context.PullHighestReceivedChunkIndex,
                transportEpoch,
                bridgeRecoveryGeneration,
                FreezeCurrentLeg: freezeCurrent,
                InitialTunaReadinessEvidence: initialTunaReadinessEvidence),
            CreateCoordinatorStateLocked(context));
        LogInitialTunaRouteSelectionDecision(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            decision.InitialTunaRouteSelectionDecision);
        if (decision.StartedLeg is not { } leg)
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.StartTransferLeg,
                "start_transfer_leg",
                decision,
                retiredLeg,
                null,
                transportEpoch,
                reason);
            return null;
        }

        ApplyCoordinatorDecisionToContextLocked(context, decision);
        LogTransferLegFrozenLocked(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            decision.FrozenLeg,
            reason);
        if (retiredOwner is { } owner &&
            context.CurrentRuntimeOwner is { } currentOwner &&
            owner.RuntimeGeneration != currentOwner.RuntimeGeneration)
        {
            RetireInboundFallbackControlPlaneExchangeLocked(
                context,
                retiredLeg,
                leg.Generation,
                leg.CheckpointRequestId,
                reason);
            RetireRuntimeOwnedWorkersLocked(context, owner, reason);
        }

        LogFileTransferLegStarted(FileTransferDirection.Inbound, context.TransferId, context.SessionId, leg, reason);
        return leg;
    }

    private static void LogTransferLegFrozenLocked(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? frozenLeg,
        string reason)
    {
        if (frozenLeg is null)
        {
            return;
        }
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_leg_frozen; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(frozenLeg.LegId)}; leg_generation={frozenLeg.Generation}; route={frozenLeg.RouteSelection.TelemetryToken}; protocol_version={frozenLeg.ProtocolVersion}; live_route_epoch={frozenLeg.LiveRouteEpochId}; transport_epoch={frozenLeg.TransportEpochId}; reason={FormatProtocolLogValue(reason)}; proven_committed_chunk={frozenLeg.ProvenCommittedChunkIndex}; proven_highest_observed_chunk={frozenLeg.ProvenHighestObservedChunkIndex}");
        if (frozenLeg.RouteSelection.Route is FileTransferRoute.FileTunaV4 or FileTransferRoute.PostTunaFallbackV6)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_clean_fallback_leg_frozen; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(frozenLeg.LegId)}; leg_generation={frozenLeg.Generation}; route={frozenLeg.RouteSelection.TelemetryToken}; protocol_version={frozenLeg.ProtocolVersion}; live_route_epoch={frozenLeg.LiveRouteEpochId}; transport_epoch={frozenLeg.TransportEpochId}; reason={FormatProtocolLogValue(reason)}; proven_committed_chunk={frozenLeg.ProvenCommittedChunkIndex}; proven_highest_observed_chunk={frozenLeg.ProvenHighestObservedChunkIndex}");
        }
    }

    private static void LogFileTransferLegStarted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_leg_started; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; state={FormatFileTransferLegState(leg.State)}; reason={FormatProtocolLogValue(reason)}; start_committed_chunk={leg.StartCommittedChunkIndex}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; can_send_data={(leg.CanSendData ? 1 : 0)}");
        if (leg.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_clean_fallback_leg_started; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; state={FormatFileTransferLegState(leg.State)}; reason={FormatProtocolLogValue(reason)}; start_committed_chunk={leg.StartCommittedChunkIndex}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; can_send_data={(leg.CanSendData ? 1 : 0)}");
        }
    }

    private static void LogFileTransferFallbackCheckpointRequested(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        string requestId,
        string reason,
        int requestedChunkIndex)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_requested; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(requestId)}; checkpoint_generation={leg.CheckpointGeneration}; requested_chunk_index={requestedChunkIndex}; reason={FormatProtocolLogValue(reason)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_clean_fallback_checkpoint_requested; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(requestId)}; checkpoint_generation={leg.CheckpointGeneration}; requested_chunk_index={requestedChunkIndex}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogFileTransferFallbackCheckpointAccepted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        string? requestId,
        string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_accepted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(none)")}; proven_committed_chunk={leg.ProvenCommittedChunkIndex}; proven_highest_observed_chunk={leg.ProvenHighestObservedChunkIndex}; state={FormatFileTransferLegState(leg.State)}; reason={FormatProtocolLogValue(reason)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_clean_fallback_checkpoint_accepted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(none)")}; proven_committed_chunk={leg.ProvenCommittedChunkIndex}; proven_highest_observed_chunk={leg.ProvenHighestObservedChunkIndex}; state={FormatFileTransferLegState(leg.State)}; reason={FormatProtocolLogValue(reason)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_clean_fallback_resume_committed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(none)")}; proven_committed_chunk={leg.ProvenCommittedChunkIndex}; proven_highest_observed_chunk={leg.ProvenHighestObservedChunkIndex}; resume_chunk_index={leg.ProvenCommittedChunkIndex}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogFileTransferFallbackCheckpointRetired(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        string? retiredRequestId,
        string reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_retired; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; retired_checkpoint_request_id={FormatProtocolLogValue(retiredRequestId ?? "(none)")}; state={FormatFileTransferLegState(leg.State)}; reason={FormatProtocolLogValue(reason)}");
        LogFileTransferFallbackCheckpointExchangeRetired(
            direction,
            transferId,
            sessionId,
            leg,
            retiredRequestId,
            leg.Generation,
            leg.CheckpointRequestId,
            reason);
    }

    private static void LogFileTransferFallbackCheckpointExchangeRetired(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? retiredLeg,
        string? retiredRequestId,
        int currentLegGeneration,
        string? currentCheckpointRequestId,
        string reason)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_exchange_retired; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; retired_leg_id={FormatProtocolLogValue(retiredLeg?.LegId ?? "(none)")}; retired_leg_generation={retiredLeg?.Generation ?? 0}; current_leg_generation={currentLegGeneration}; route={FormatProtocolLogValue(retiredLeg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={retiredLeg?.ProtocolVersion ?? 0}; live_route_epoch={retiredLeg?.LiveRouteEpochId ?? 0}; bridge_recovery_generation={retiredLeg?.BridgeRecoveryGeneration ?? 0}; transport_epoch={retiredLeg?.TransportEpochId ?? 0}; retired_checkpoint_request_id={FormatProtocolLogValue(retiredRequestId ?? "(none)")}; current_checkpoint_request_id={FormatProtocolLogValue(currentCheckpointRequestId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");

    private static FallbackControlPlaneExchangeOwnerSnapshot? CaptureFallbackControlPlaneExchangeOwner(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferLeg? leg)
        => IsCurrentPostTunaFallbackLeg(leg)
            ? new FallbackControlPlaneExchangeOwnerSnapshot(
                sessionId,
                transferId,
                direction,
                leg!.RouteSelection.TelemetryToken,
                leg.ProtocolVersion,
                leg.LiveRouteEpochId,
                leg.Generation,
                leg.BridgeRecoveryGeneration,
                leg.TransportEpochId,
                leg.CheckpointRequestId)
            : null;

    private static void RetireOutboundFallbackControlPlaneExchangeLocked(
        OutboundTransferContext context,
        FileTransferLeg? retiredLeg,
        int currentLegGeneration,
        string? currentCheckpointRequestId,
        string reason)
    {
        var retiredOwner = CaptureFallbackControlPlaneExchangeOwner(
            FileTransferDirection.Outbound,
            context.SessionId,
            context.TransferId,
            retiredLeg);
        if (retiredOwner is null)
        {
            return;
        }

        var retiredRequestId = retiredOwner.Value.CheckpointRequestId;
        if (!string.IsNullOrWhiteSpace(retiredRequestId))
        {
            ClearOutboundFallbackCheckpointRetryStateLocked(context, retiredRequestId, reason);
            RememberRetiredFallbackCheckpointRequest(
                context.RetiredFallbackCheckpointRequestKeys,
                retiredLeg!,
                retiredOwner.Value.TransportEpoch,
                retiredRequestId);
            retiredLeg!.CheckpointRequestId = null;
            retiredLeg.CheckpointPriority = null;
            retiredLeg.CheckpointRequestedUtc = null;
        }

        ClearOutboundFallbackCheckpointDeliveryRecoveryPendingLocked(context, reason);
        ClearOutboundFallbackCheckpointRepairAuthorityLocked(context);
        ClearOutboundFallbackTailQuiescenceDiagnosticLocked(context);
        LogFileTransferFallbackCheckpointExchangeRetired(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            retiredLeg,
            retiredRequestId,
            currentLegGeneration,
            currentCheckpointRequestId,
            reason);
    }

    private static void RetireInboundFallbackControlPlaneExchangeLocked(
        InboundTransferContext context,
        FileTransferLeg? retiredLeg,
        int currentLegGeneration,
        string? currentCheckpointRequestId,
        string reason)
    {
        var retiredOwner = CaptureFallbackControlPlaneExchangeOwner(
            FileTransferDirection.Inbound,
            context.SessionId,
            context.TransferId,
            retiredLeg);
        if (retiredOwner is null)
        {
            return;
        }

        var retiredRequestId = retiredOwner.Value.CheckpointRequestId;
        if (!string.IsNullOrWhiteSpace(retiredRequestId))
        {
            RememberRetiredFallbackCheckpointRequest(
                context.RetiredFallbackCheckpointRequestKeys,
                retiredLeg!,
                retiredOwner.Value.TransportEpoch,
                retiredRequestId);
            retiredLeg!.CheckpointRequestId = null;
            retiredLeg.CheckpointPriority = null;
            retiredLeg.CheckpointRequestedUtc = null;
        }

        ClearInboundV6PostTunaFallbackProofReplayLocked(context);
        context.V6PostTunaFallbackProofReplayReceiverStatePending = false;
        context.V6PostTunaFallbackProofReplayFrontierRequestPending = false;
        context.V6LastReceiverStateSentUtc = null;
        context.V6LastFrontierRequestSentUtc = null;
        context.V6LastFrontierRequestChunkIndex = -1;
        context.V6LastFrontierRequestId = null;
        LogFileTransferFallbackCheckpointExchangeRetired(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            retiredLeg,
            retiredRequestId,
            currentLegGeneration,
            currentCheckpointRequestId,
            reason);
    }

    private static void AdoptOutboundFallbackLegTransportEpochLocked(
        OutboundTransferContext context,
        long transportEpoch,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg) ||
            transportEpoch <= 0 ||
            leg!.TransportEpochId == transportEpoch)
        {
            return;
        }

        var previousTransportEpoch = leg.TransportEpochId;
        var previousBridgeRecoveryGeneration = leg.BridgeRecoveryGeneration;
        var nextBridgeRecoveryGeneration = context.PullTransportRebindGeneration > 0
            ? Math.Max(leg.BridgeRecoveryGeneration, context.PullTransportRebindGeneration)
            : leg.BridgeRecoveryGeneration;
        var bridgeRecoveryGenerationAdvances = nextBridgeRecoveryGeneration != previousBridgeRecoveryGeneration;
        var retiredRequestId = leg.CheckpointRequestId;
        if (!string.IsNullOrWhiteSpace(retiredRequestId))
        {
            RetireOutboundFallbackControlPlaneExchangeLocked(
                context,
                leg,
                leg.Generation,
                null,
                bridgeRecoveryGenerationAdvances
                    ? "bridge_recovery_generation_advanced"
                    : "transport_epoch_adopted");
        }

        leg.TransportEpochId = transportEpoch;
        if (!string.IsNullOrWhiteSpace(retiredRequestId))
        {
            leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = true;
        }

        leg.BridgeRecoveryGeneration = nextBridgeRecoveryGeneration;

        LogFileTransferFallbackLegTransportEpochAdopted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            leg,
            previousTransportEpoch,
            retiredRequestId,
            reason);
    }

    private static long ResolveOutboundFallbackCurrentTransportEpochLocked(
        OutboundTransferContext context,
        long minimumEpoch = 0)
    {
        var transportEpoch = Math.Max(0, minimumEpoch);
        if (context.V6TransportEpoch is { EpochId: > 0 } v6TransportEpoch)
        {
            transportEpoch = Math.Max(transportEpoch, v6TransportEpoch.EpochId);
        }

        if (context.V6TransportHandoff is { EpochId: > 0 } handoff)
        {
            transportEpoch = Math.Max(transportEpoch, handoff.EpochId);
        }

        if (context.PullTransportRebindGeneration > 0)
        {
            transportEpoch = Math.Max(transportEpoch, context.PullTransportRebindGeneration);
        }

        if (IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
            context.CurrentTransferLeg!.TransportEpochId > 0)
        {
            transportEpoch = Math.Max(transportEpoch, context.CurrentTransferLeg.TransportEpochId);
        }

        return transportEpoch;
    }

    private static void AdoptInboundFallbackLegTransportEpochLocked(
        InboundTransferContext context,
        long transportEpoch,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg) ||
            transportEpoch <= 0 ||
            leg!.TransportEpochId == transportEpoch)
        {
            return;
        }

        var previousTransportEpoch = leg.TransportEpochId;
        var previousBridgeRecoveryGeneration = leg.BridgeRecoveryGeneration;
        var nextBridgeRecoveryGeneration = context.PullTransportRebindGeneration > 0
            ? Math.Max(leg.BridgeRecoveryGeneration, context.PullTransportRebindGeneration)
            : leg.BridgeRecoveryGeneration;
        var bridgeRecoveryGenerationAdvances = nextBridgeRecoveryGeneration != previousBridgeRecoveryGeneration;
        var retiredRequestId = leg.CheckpointRequestId;
        if (!string.IsNullOrWhiteSpace(retiredRequestId))
        {
            RetireInboundFallbackControlPlaneExchangeLocked(
                context,
                leg,
                leg.Generation,
                null,
                bridgeRecoveryGenerationAdvances
                    ? "bridge_recovery_generation_advanced"
                    : "transport_epoch_adopted");
        }

        leg.TransportEpochId = transportEpoch;
        if (!string.IsNullOrWhiteSpace(retiredRequestId))
        {
            leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = true;
        }

        leg.BridgeRecoveryGeneration = nextBridgeRecoveryGeneration;

        LogFileTransferFallbackLegTransportEpochAdopted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            leg,
            previousTransportEpoch,
            retiredRequestId,
            reason);
    }

    private static void LogFileTransferFallbackLegTransportEpochAdopted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        long previousTransportEpoch,
        string? retiredRequestId,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_leg_transport_epoch_adopted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; previous_transport_epoch={previousTransportEpoch}; transport_epoch={leg.TransportEpochId}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; retired_checkpoint_request_id={FormatProtocolLogValue(retiredRequestId ?? "(none)")}; current_checkpoint_request_id={FormatProtocolLogValue(leg.CheckpointRequestId ?? "(none)")}; state={FormatFileTransferLegState(leg.State)}; can_send_data={(leg.CanSendData ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackCheckpointOwnerViolation(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        FileTransferFrontierRequestFrameV6 request,
        string reason,
        string source)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_owner_violation; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; current_checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; request_transport_epoch={request.TransportEpoch}; request_checkpoint_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; request_priority={FormatProtocolLogValue(request.Priority ?? "(none)")}; source={FormatProtocolLogValue(source)}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackCheckpointReissueSuppressed(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason,
        string source,
        long proofTransportEpoch,
        string? proofRequestId)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_reissue_suppressed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; source={FormatProtocolLogValue(source)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; proof_transport_epoch={proofTransportEpoch}; proof_checkpoint_request_id={FormatProtocolLogValue(proofRequestId ?? "(none)")}");

    private static void LogFileTransferFallbackStaleProofIgnored(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason,
        long transportEpoch,
        string? requestId,
        HashSet<string>? suppressionKeys = null)
    {
        var suppressionKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{direction}|{transferId}|{sessionId}|{leg?.Generation ?? 0}|{transportEpoch}|{requestId ?? "(none)"}|{reason}");
        if (suppressionKeys is not null && !suppressionKeys.Add(suppressionKey))
        {
            var replaySuppressionKey = string.Create(CultureInfo.InvariantCulture, $"replay|{suppressionKey}");
            if (suppressionKeys.Add(replaySuppressionKey))
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_fallback_stale_proof_replay_suppressed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={transportEpoch}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(none)")}; expected_checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");
            }

            return;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_stale_proof_ignored; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={transportEpoch}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(none)")}; expected_checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_clean_fallback_stale_proof_ignored; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={transportEpoch}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(none)")}; expected_checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogFileTransferFallbackLegAuthoritySupersededByRouteHint(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? supersededLeg,
        FileTransferRouteSelection supersedingRoute,
        string source)
    {
        if (!IsCurrentPostTunaFallbackLeg(supersededLeg))
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_leg_authority_superseded_by_route_hint; direction={direction.ToString().ToLowerInvariant()}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={transferId}; leg_id={FormatProtocolLogValue(supersededLeg!.LegId)}; leg_generation={supersededLeg.Generation}; route={supersededLeg.RouteSelection.TelemetryToken}; protocol_version={supersededLeg.ProtocolVersion}; live_route_epoch={supersededLeg.LiveRouteEpochId}; transport_epoch={supersededLeg.TransportEpochId}; bridge_recovery_generation={supersededLeg.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(supersededLeg.CheckpointRequestId ?? "(none)")}; authority_reason=live_route_tuna_reactivated; superseded_by_route={supersedingRoute.TelemetryToken}; superseded_by_protocol_version={supersedingRoute.ProtocolVersion}; source={FormatProtocolLogValue(source)}");
    }

    private static void LogFileTransferFallbackStaleTransportEpochCleared(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        long checkpointTransportEpoch,
        long clearedTransportEpoch,
        string clearedTransportEpochState,
        long clearedHandoffEpoch,
        string clearedHandoffState,
        string reason)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_stale_transport_epoch_cleared; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_transport_epoch={checkpointTransportEpoch}; cleared_transport_epoch={clearedTransportEpoch}; cleared_transport_epoch_state={FormatProtocolLogValue(clearedTransportEpochState)}; cleared_handoff_epoch={clearedHandoffEpoch}; cleared_handoff_state={FormatProtocolLogValue(clearedHandoffState)}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTransportEpochAdopted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        long previousTransportEpoch,
        FileTransferReceiverStateFrameV6 state,
        string reason)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_transport_epoch_adopted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; previous_transport_epoch={previousTransportEpoch}; transport_epoch={state.TransportEpoch}; proven_committed_chunk={leg.ProvenCommittedChunkIndex}; receiver_committed_chunk={state.ContiguousCommittedChunkIndex}; proven_highest_observed_chunk={leg.ProvenHighestObservedChunkIndex}; receiver_highest_observed_chunk={state.DurableReceivedHighestChunkIndex}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailReconciliationAccepted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        FileTransferReceiverStateFrameV6 state,
        string? checkpointRequestId,
        string? priority,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_reconciliation_accepted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(checkpointRequestId ?? "(none)")}; proven_committed_chunk={leg.ProvenCommittedChunkIndex}; proven_highest_observed_chunk={leg.ProvenHighestObservedChunkIndex}; receiver_committed_chunk={state.ContiguousCommittedChunkIndex}; receiver_highest_observed_chunk={state.DurableReceivedHighestChunkIndex}; receiver_credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; priority={FormatProtocolLogValue(priority ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailQuiescentYieldGranted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        int committedChunk,
        int highestObservedChunk,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_quiescent_yield_granted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; committed_chunk={committedChunk}; highest_observed_chunk={highestObservedChunk}; state={FormatProtocolLogValue(FileTransferCoordinator.FormatLegState(leg.State))}; can_send_data={(leg.CanSendData ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailYieldProofSent(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        FileTransferFallbackTailYieldFrameV6? frame,
        FileTransferControlPlaneDeliveryResult? result,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_yield_proof_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? frame?.FallbackLegGeneration ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? FileTransferRouteResolver.PostTunaFallbackV6Token)}; protocol_version={leg?.ProtocolVersion ?? FileTransferProtocol.ProtocolVersionV6}; live_route_epoch={leg?.LiveRouteEpochId ?? frame?.LiveRouteEpoch ?? 0}; transport_epoch={frame?.TransportEpoch ?? leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? frame?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? frame?.RepairRequestId ?? "(none)")}; owner_key={FormatProtocolLogValue(frame?.OwnerKey ?? "(none)")}; yield_generation={frame?.YieldGeneration ?? 0}; committed_chunk={frame?.CommittedChunkIndex ?? 0}; highest_observed_chunk={frame?.HighestObservedChunkIndex ?? -1}; peer_visible_any={(result?.PeerVisibleAny == true ? 1 : 0)}; accepted_any={(result?.AcceptedAny == true ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailYieldProofReceived(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        FileTransferFallbackTailYieldFrameV6 frame)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_yield_proof_received; direction={direction.ToString().ToLowerInvariant()}; peer_direction={FormatProtocolLogValue(frame.Direction)}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? FileTransferRouteResolver.PostTunaFallbackV6Token)}; protocol_version={leg?.ProtocolVersion ?? FileTransferProtocol.ProtocolVersionV6}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; owner_key={FormatProtocolLogValue(frame.OwnerKey)}; yield_generation={frame.YieldGeneration}; committed_chunk={frame.CommittedChunkIndex}; highest_observed_chunk={frame.HighestObservedChunkIndex}");

    private static void LogFileTransferFallbackTailPairedYieldGranted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string ownerKey,
        long yieldGeneration,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_paired_yield_granted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? FileTransferRouteResolver.PostTunaFallbackV6Token)}; protocol_version={leg?.ProtocolVersion ?? FileTransferProtocol.ProtocolVersionV6}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; owner_key={FormatProtocolLogValue(ownerKey)}; yield_generation={yieldGeneration}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailOwnerGenerationMismatch(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        FileTransferFallbackTailYieldFrameV6 frame,
        string expectedOwnerKey)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_owner_generation_mismatch; direction={direction.ToString().ToLowerInvariant()}; peer_direction={FormatProtocolLogValue(frame.Direction)}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(leg.CheckpointRequestId ?? "(none)")}; peer_live_route_epoch={frame.LiveRouteEpoch}; peer_leg_generation={frame.FallbackLegGeneration}; peer_transport_epoch={frame.TransportEpoch}; peer_bridge_recovery_generation={frame.BridgeRecoveryGeneration}; peer_checkpoint_request_id={FormatProtocolLogValue(frame.RepairRequestId ?? "(none)")}; expected_owner_key={FormatProtocolLogValue(expectedOwnerKey)}; peer_owner_key={FormatProtocolLogValue(frame.OwnerKey)}; yield_generation={frame.YieldGeneration}");

    private static void LogFileTransferFallbackTailYieldProofStaleIgnored(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        FileTransferFallbackTailYieldFrameV6? frame,
        string reason,
        HashSet<string> suppressionKeys)
    {
        var key = string.Join(
            "|",
            direction.ToString(),
            transferId,
            sessionId,
            frame?.OwnerKey ?? "(none)",
            frame?.YieldGeneration.ToString(CultureInfo.InvariantCulture) ?? "0",
            reason);
        if (!suppressionKeys.Add(key))
        {
            return;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_yield_proof_stale_ignored; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? FileTransferRouteResolver.PostTunaFallbackV6Token)}; protocol_version={leg?.ProtocolVersion ?? FileTransferProtocol.ProtocolVersionV6}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; peer_owner_key={FormatProtocolLogValue(frame?.OwnerKey ?? "(none)")}; yield_generation={frame?.YieldGeneration ?? 0}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogFileTransferFallbackTailTerminalProofConsumed(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        int committedChunk,
        int chunkCount,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_terminal_proof_consumed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; committed_chunk={committedChunk}; chunk_count={chunkCount}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailProgressCoalesced(
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        string tuple,
        int repeatCount,
        string reason,
        int suppressedRepeatCount = 0)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_progress_coalesced; direction=outbound; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; repeat_count={repeatCount}; suppressed_repeat_count={suppressedRepeatCount}; tuple={FormatProtocolLogValue(tuple)}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailRetiredOwnerUpdateSuppressed(
        OutboundTransferContext context,
        FileTransferLeg? leg,
        string ownerKey,
        string source,
        string attemptedAction,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_retired_owner_update_suppressed; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? context.RouteSelection.TelemetryToken)}; protocol_version={leg?.ProtocolVersion ?? context.NegotiatedDataProtocolVersion}; live_route_epoch={leg?.LiveRouteEpochId ?? context.CurrentLiveRouteEpoch?.EpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; source={FormatProtocolLogValue(source)}; attempted_action={FormatProtocolLogValue(attemptedAction)}; owner_key={FormatProtocolLogValue(ownerKey)}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailRetiredOwnerUpdateSuppressed(
        InboundTransferContext context,
        FileTransferLeg? leg,
        string ownerKey,
        string source,
        string attemptedAction,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_retired_owner_update_suppressed; direction=inbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? context.RouteSelection.TelemetryToken)}; protocol_version={leg?.ProtocolVersion ?? context.NegotiatedDataProtocolVersion}; live_route_epoch={leg?.LiveRouteEpochId ?? context.CurrentLiveRouteEpoch?.EpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; source={FormatProtocolLogValue(source)}; attempted_action={FormatProtocolLogValue(attemptedAction)}; owner_key={FormatProtocolLogValue(ownerKey)}; reason={FormatProtocolLogValue(reason)}");

    private static string BuildFallbackChurnTuple(
        string sessionId,
        string transferId,
        string routeToken,
        int protocolVersion,
        int liveRouteEpoch,
        int legGeneration,
        long transportEpoch,
        int committedChunk,
        int highestChunk,
        int frontierGap,
        int missingRangeCount,
        string? checkpointRequestId)
        => string.Join(
            "|",
            sessionId,
            transferId,
            routeToken,
            protocolVersion.ToString(CultureInfo.InvariantCulture),
            liveRouteEpoch.ToString(CultureInfo.InvariantCulture),
            legGeneration.ToString(CultureInfo.InvariantCulture),
            transportEpoch.ToString(CultureInfo.InvariantCulture),
            committedChunk.ToString(CultureInfo.InvariantCulture),
            highestChunk.ToString(CultureInfo.InvariantCulture),
            frontierGap.ToString(CultureInfo.InvariantCulture),
            missingRangeCount.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(checkpointRequestId) ? "(none)" : checkpointRequestId);

    private static string BuildFallbackOwnerKey(
        string sessionId,
        string transferId,
        FileTransferLeg leg)
        => string.Join(
            "|",
            sessionId,
            transferId,
            leg.RouteSelection.TelemetryToken,
            leg.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            leg.LiveRouteEpochId.ToString(CultureInfo.InvariantCulture),
            leg.Generation.ToString(CultureInfo.InvariantCulture),
            leg.BridgeRecoveryGeneration.ToString(CultureInfo.InvariantCulture),
            leg.TransportEpochId.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(leg.CheckpointRequestId) ? "(none)" : leg.CheckpointRequestId);

    private static bool HasPairedFallbackTailYieldForCurrentOwner(OutboundTransferContext context)
        => HasPairedFallbackTailYieldForCurrentOwner(
            context.SessionId,
            context.TransferId,
            context.CurrentTransferLeg,
            context.PairedFallbackTailYieldGranted,
            context.LocalFallbackTailYieldOwnerKey,
            context.PeerFallbackTailYieldOwnerKey);

    private static bool HasPairedFallbackTailYieldForCurrentOwner(InboundTransferContext context)
        => HasPairedFallbackTailYieldForCurrentOwner(
            context.SessionId,
            context.TransferId,
            context.CurrentTransferLeg,
            context.PairedFallbackTailYieldGranted,
            context.LocalFallbackTailYieldOwnerKey,
            context.PeerFallbackTailYieldOwnerKey);

    private static bool HasPairedFallbackTailYieldForCurrentOwner(
        string sessionId,
        string transferId,
        FileTransferLeg? leg,
        bool paired,
        string? localOwnerKey,
        string? peerOwnerKey)
    {
        if (!paired || !IsCurrentPostTunaFallbackLeg(leg))
        {
            return false;
        }

        var ownerKey = BuildFallbackOwnerKey(sessionId, transferId, leg!);
        return string.Equals(localOwnerKey, ownerKey, StringComparison.Ordinal) &&
               string.Equals(peerOwnerKey, ownerKey, StringComparison.Ordinal);
    }

    private static FileTransferFallbackTailYieldFrameV6 CreateFallbackTailYieldFrame(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferLeg leg,
        string ownerKey,
        long yieldGeneration,
        int committedChunk,
        int highestObservedChunk)
        => new()
        {
            SessionId = sessionId,
            TransferId = transferId,
            TransportEpoch = leg.TransportEpochId,
            RepairRequestId = leg.CheckpointRequestId,
            Priority = "fallback_tail_yield",
            RecoveryMode = "fallback_tail_yield",
            OwnerKey = ownerKey,
            Direction = direction.ToString().ToLowerInvariant(),
            YieldGeneration = yieldGeneration,
            LiveRouteEpoch = leg.LiveRouteEpochId,
            FallbackLegGeneration = leg.Generation,
            BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
            CommittedChunkIndex = committedChunk,
            HighestObservedChunkIndex = highestObservedChunk,
            SentUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private void RecordOutboundFallbackTailLocalYieldLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        int committedChunk,
        int highestObservedChunk,
        string reason)
    {
        var ownerKey = BuildFallbackOwnerKey(context.SessionId, context.TransferId, leg);
        if (!string.Equals(context.LocalFallbackTailYieldOwnerKey, ownerKey, StringComparison.Ordinal))
        {
            context.FallbackTailYieldGeneration++;
            context.LocalFallbackTailYieldOwnerKey = ownerKey;
            context.LocalFallbackTailYieldPeerVisible = false;
            context.PeerFallbackTailYieldOwnerKey = null;
            context.PeerFallbackTailYieldGranted = false;
            context.PairedFallbackTailYieldGranted = false;
            context.FallbackTailYieldMismatchReason = null;
        }
        else if (context.FallbackTailYieldGeneration <= 0)
        {
            context.FallbackTailYieldGeneration = 1;
        }

        context.LocalFallbackTailYieldGranted = true;
        var frame = CreateFallbackTailYieldFrame(
            FileTransferDirection.Outbound,
            context.SessionId,
            context.TransferId,
            leg,
            ownerKey,
            context.FallbackTailYieldGeneration,
            committedChunk,
            highestObservedChunk);
        var request = CreateOutboundControlPlaneDeliveryRequestLocked(
            context,
            FileTransferControlPlaneKind.FallbackTailYield,
            frame,
            "fallback_tail_paired_yield",
            ignoreCallerCancellation: true,
            peerCopyAttempts: 2) with
        {
            CopyTimeout = TimeSpan.FromMilliseconds(V6PostTunaFallbackControlPlanePeerCopyTimeoutMs),
        };
        QueueOutboundFallbackTailYieldProofSendLocked(
            context,
            request,
            ownerKey,
            context.FallbackTailYieldGeneration,
            reason);
    }

    private void RecordInboundFallbackTailLocalYieldLocked(
        InboundTransferContext context,
        FileTransferLeg leg,
        int committedChunk,
        int highestObservedChunk,
        string reason)
    {
        var ownerKey = BuildFallbackOwnerKey(context.SessionId, context.TransferId, leg);
        if (!string.Equals(context.LocalFallbackTailYieldOwnerKey, ownerKey, StringComparison.Ordinal))
        {
            context.FallbackTailYieldGeneration++;
            context.LocalFallbackTailYieldOwnerKey = ownerKey;
            context.LocalFallbackTailYieldPeerVisible = false;
            context.PeerFallbackTailYieldOwnerKey = null;
            context.PeerFallbackTailYieldGranted = false;
            context.PairedFallbackTailYieldGranted = false;
            context.FallbackTailYieldMismatchReason = null;
        }
        else if (context.FallbackTailYieldGeneration <= 0)
        {
            context.FallbackTailYieldGeneration = 1;
        }

        context.LocalFallbackTailYieldGranted = true;
        var frame = CreateFallbackTailYieldFrame(
            FileTransferDirection.Inbound,
            context.SessionId,
            context.TransferId,
            leg,
            ownerKey,
            context.FallbackTailYieldGeneration,
            committedChunk,
            highestObservedChunk);
        var request = CreateInboundControlPlaneDeliveryRequestLocked(
            context,
            FileTransferControlPlaneKind.FallbackTailYield,
            frame,
            "fallback_tail_paired_yield",
            ignoreCallerCancellation: true,
            peerCopyAttempts: 2) with
        {
            CopyTimeout = TimeSpan.FromMilliseconds(V6PostTunaFallbackControlPlanePeerCopyTimeoutMs),
        };
        QueueInboundFallbackTailYieldProofSendLocked(
            context,
            request,
            ownerKey,
            context.FallbackTailYieldGeneration,
            reason);
    }

    private void QueueOutboundFallbackTailYieldProofSendLocked(
        OutboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        string ownerKey,
        long yieldGeneration,
        string reason)
    {
        if (context.DataSession is not { } dataSession)
        {
            LogFileTransferFallbackTailYieldProofStaleIgnored(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                request.Frame as FileTransferFallbackTailYieldFrameV6,
                "data_session_unavailable",
                context.FallbackTailYieldProofSuppressionKeys);
            return;
        }

        _ = SendOutboundFallbackTailYieldProofAsync(
            context,
            dataSession,
            request,
            ownerKey,
            yieldGeneration,
            reason);
    }

    private void QueueInboundFallbackTailYieldProofSendLocked(
        InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        string ownerKey,
        long yieldGeneration,
        string reason)
    {
        if (context.DataSession is not { } dataSession)
        {
            LogFileTransferFallbackTailYieldProofStaleIgnored(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                request.Frame as FileTransferFallbackTailYieldFrameV6,
                "data_session_unavailable",
                context.FallbackTailYieldProofSuppressionKeys);
            return;
        }

        _ = SendInboundFallbackTailYieldProofAsync(
            context,
            dataSession,
            request,
            ownerKey,
            yieldGeneration,
            reason);
    }

    private async Task SendOutboundFallbackTailYieldProofAsync(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferControlPlaneDeliveryRequest request,
        string ownerKey,
        long yieldGeneration,
        string reason)
    {
        FileTransferControlPlaneDeliveryResult? result = null;
        FallbackControlPlaneToken? controlPlaneToken;
        try
        {
            lock (gate)
            {
                if (!TryAuthorizeOutboundFallbackControlPlaneSendLocked(context, request, out controlPlaneToken))
                {
                    return;
                }
            }

            result = await SendFileTransferControlPlaneOrDataSessionCoreAsync(
                    dataSession,
                    request,
                    context.LifetimeCts.Token)
                .ConfigureAwait(false);

            if (result is not null)
            {
                lock (gate)
                {
                    if (!TryCompleteOutboundFallbackControlPlaneSendLocked(context, request, controlPlaneToken, result))
                    {
                        LogFileTransferControlPlaneDeliveryResult(request, result);
                        return;
                    }
                }

                LogFileTransferControlPlaneDeliveryResult(request, result);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFileTransferFallbackTailYieldProofStaleIgnored(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                request.Frame as FileTransferFallbackTailYieldFrameV6,
                ex.GetType().Name,
                context.FallbackTailYieldProofSuppressionKeys);
            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !string.Equals(context.LocalFallbackTailYieldOwnerKey, ownerKey, StringComparison.Ordinal) ||
                context.FallbackTailYieldGeneration != yieldGeneration)
            {
                LogFileTransferFallbackTailYieldProofStaleIgnored(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentTransferLeg,
                    request.Frame as FileTransferFallbackTailYieldFrameV6,
                    "yield_owner_retired",
                    context.FallbackTailYieldProofSuppressionKeys);
                return;
            }

            context.LocalFallbackTailYieldPeerVisible = result?.PeerVisibleAny == true;
            LogFileTransferFallbackTailYieldProofSent(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                request.Frame as FileTransferFallbackTailYieldFrameV6,
                result,
                reason);
            TryGrantPairedOutboundFallbackTailYieldLocked(context, "fallback_tail_paired_yield");
        }
    }

    private async Task SendInboundFallbackTailYieldProofAsync(
        InboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferControlPlaneDeliveryRequest request,
        string ownerKey,
        long yieldGeneration,
        string reason)
    {
        FileTransferControlPlaneDeliveryResult? result = null;
        FallbackControlPlaneToken? controlPlaneToken;
        try
        {
            lock (gate)
            {
                if (!TryAuthorizeInboundFallbackControlPlaneSendLocked(context, request, out controlPlaneToken))
                {
                    return;
                }
            }

            result = await SendFileTransferControlPlaneOrDataSessionCoreAsync(
                    dataSession,
                    request,
                    context.LifetimeCts.Token)
                .ConfigureAwait(false);

            if (result is not null)
            {
                lock (gate)
                {
                    if (!TryCompleteInboundFallbackControlPlaneSendLocked(context, request, controlPlaneToken, result))
                    {
                        LogFileTransferControlPlaneDeliveryResult(request, result);
                        return;
                    }
                }

                LogFileTransferControlPlaneDeliveryResult(request, result);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFileTransferFallbackTailYieldProofStaleIgnored(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                request.Frame as FileTransferFallbackTailYieldFrameV6,
                ex.GetType().Name,
                context.FallbackTailYieldProofSuppressionKeys);
            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !string.Equals(context.LocalFallbackTailYieldOwnerKey, ownerKey, StringComparison.Ordinal) ||
                context.FallbackTailYieldGeneration != yieldGeneration)
            {
                LogFileTransferFallbackTailYieldProofStaleIgnored(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.CurrentTransferLeg,
                    request.Frame as FileTransferFallbackTailYieldFrameV6,
                    "yield_owner_retired",
                    context.FallbackTailYieldProofSuppressionKeys);
                return;
            }

            context.LocalFallbackTailYieldPeerVisible = result?.PeerVisibleAny == true;
            LogFileTransferFallbackTailYieldProofSent(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                request.Frame as FileTransferFallbackTailYieldFrameV6,
                result,
                reason);
            TryGrantPairedInboundFallbackTailYieldLocked(context, "fallback_tail_paired_yield");
        }
    }

    private void ApplyOutboundFallbackTailYieldProof(OutboundTransferContext context, FileTransferFallbackTailYieldFrameV6 frame)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (!TryAcceptFallbackTailYieldProofLocked(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.RouteSelection,
                    context.NegotiatedDataProtocolVersion,
                    context.CurrentLiveRouteEpoch,
                    context.CurrentTransferLeg,
                    frame,
                    context.FallbackTailYieldProofSuppressionKeys,
                    out var reason))
            {
                context.FallbackTailYieldMismatchReason = reason;
                return;
            }

            var currentLeg = context.CurrentTransferLeg;
            if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                    context,
                    FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                    "fallback_tail_peer_yield",
                    "fallback_tail_peer_yield",
                    currentLeg,
                    currentLeg?.CheckpointRequestId,
                    frame.TransportEpoch,
                    frame.BridgeRecoveryGeneration,
                    currentLeg?.CheckpointGeneration,
                    null,
                    context.CurrentFallbackCheckpointLease,
                    null,
                    null,
                    null,
                    null,
                    null,
                    FallbackTailYieldKind.Quiescent,
                    out _))
            {
                LogFileTransferFallbackTailYieldProofStaleIgnored(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    currentLeg,
                    frame,
                    "coordinator_authority_rejected",
                    context.FallbackTailYieldProofSuppressionKeys);
                return;
            }

            context.PeerFallbackTailYieldGranted = true;
            context.PeerFallbackTailYieldOwnerKey = frame.OwnerKey;
            LogFileTransferFallbackTailYieldProofReceived(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                frame);
            TryGrantPairedOutboundFallbackTailYieldLocked(context, "fallback_tail_peer_yield");
        }
    }

    private void ApplyInboundFallbackTailYieldProof(InboundTransferContext context, FileTransferFallbackTailYieldFrameV6 frame)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (!TryAcceptFallbackTailYieldProofLocked(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.RouteSelection,
                    context.NegotiatedDataProtocolVersion,
                    context.CurrentLiveRouteEpoch,
                    context.CurrentTransferLeg,
                    frame,
                    context.FallbackTailYieldProofSuppressionKeys,
                    out var reason))
            {
                context.FallbackTailYieldMismatchReason = reason;
                return;
            }

            var currentLeg = context.CurrentTransferLeg;
            if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                    context,
                    FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                    "fallback_tail_peer_yield",
                    "fallback_tail_peer_yield",
                    currentLeg,
                    currentLeg?.CheckpointRequestId,
                    frame.TransportEpoch,
                    frame.BridgeRecoveryGeneration,
                    currentLeg?.CheckpointGeneration,
                    null,
                    context.CurrentFallbackCheckpointLease,
                    null,
                    null,
                    null,
                    null,
                    null,
                    FallbackTailYieldKind.Quiescent,
                    out _))
            {
                LogFileTransferFallbackTailYieldProofStaleIgnored(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    currentLeg,
                    frame,
                    "coordinator_authority_rejected",
                    context.FallbackTailYieldProofSuppressionKeys);
                return;
            }

            context.PeerFallbackTailYieldGranted = true;
            context.PeerFallbackTailYieldOwnerKey = frame.OwnerKey;
            LogFileTransferFallbackTailYieldProofReceived(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                frame);
            TryGrantPairedInboundFallbackTailYieldLocked(context, "fallback_tail_peer_yield");
        }
    }

    private static bool TryAcceptFallbackTailYieldProofLocked(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRouteSelection routeSelection,
        int protocolVersion,
        LiveRouteEpoch? liveRouteEpoch,
        FileTransferLeg? leg,
        FileTransferFallbackTailYieldFrameV6 frame,
        HashSet<string> suppressionKeys,
        out string reason)
    {
        reason = "current";
        if (!string.Equals(frame.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(frame.TransferId, transferId, StringComparison.Ordinal))
        {
            reason = "session_or_transfer_mismatch";
            LogFileTransferFallbackTailYieldProofStaleIgnored(
                direction,
                transferId,
                sessionId,
                leg,
                frame,
                reason,
                suppressionKeys);
            return false;
        }

        if (routeSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            protocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !IsCurrentPostTunaFallbackLeg(leg))
        {
            reason = "fallback_owner_not_current";
            LogFileTransferFallbackTailYieldProofStaleIgnored(
                direction,
                transferId,
                sessionId,
                leg,
                frame,
                reason,
                suppressionKeys);
            return false;
        }

        var currentLeg = leg!;
        var expectedOwnerKey = BuildFallbackOwnerKey(sessionId, transferId, currentLeg);
        var expectedLiveRouteEpoch = liveRouteEpoch?.EpochId ?? currentLeg.LiveRouteEpochId;
        var expectedPeerDirection = direction == FileTransferDirection.Outbound
            ? "inbound"
            : "outbound";
        if (!string.Equals(frame.OwnerKey, expectedOwnerKey, StringComparison.Ordinal) ||
            !string.Equals(frame.Direction, expectedPeerDirection, StringComparison.Ordinal) ||
            frame.LiveRouteEpoch != expectedLiveRouteEpoch ||
            frame.LiveRouteEpoch != currentLeg.LiveRouteEpochId ||
            frame.FallbackLegGeneration != currentLeg.Generation ||
            frame.BridgeRecoveryGeneration != currentLeg.BridgeRecoveryGeneration ||
            frame.TransportEpoch != currentLeg.TransportEpochId)
        {
            reason = "fallback_tail_owner_generation_mismatch";
            LogFileTransferFallbackTailOwnerGenerationMismatch(
                direction,
                transferId,
                sessionId,
                currentLeg,
                frame,
                expectedOwnerKey);
            return false;
        }

        return true;
    }

    private void TryGrantPairedOutboundFallbackTailYieldLocked(OutboundTransferContext context, string reason)
    {
        if (context.PairedFallbackTailYieldGranted ||
            !context.LocalFallbackTailYieldGranted ||
            !context.LocalFallbackTailYieldPeerVisible ||
            !context.PeerFallbackTailYieldGranted ||
            string.IsNullOrWhiteSpace(context.LocalFallbackTailYieldOwnerKey) ||
            !string.Equals(context.LocalFallbackTailYieldOwnerKey, context.PeerFallbackTailYieldOwnerKey, StringComparison.Ordinal))
        {
            return;
        }

        context.PairedFallbackTailYieldGranted = true;
        LogFileTransferFallbackTailPairedYieldGranted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            context.LocalFallbackTailYieldOwnerKey,
            context.FallbackTailYieldGeneration,
            reason);
        TryPublishRouteHintIfAuthorizedLocked(
            context,
            "fallback_tail_paired_yield_route_hint",
            "fallback_tail_paired_yield",
            "fallback_tail_paired_yield",
            runtimeUnlockAdmissionFactory: () => RequestRuntimeUnlockAdmissionLocked(context, "route_hint", "fallback_tail_paired_yield"));
        TryResumeOutboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
            context,
            "fallback_tail_quiescent");
    }

    private void TryGrantPairedInboundFallbackTailYieldLocked(InboundTransferContext context, string reason)
    {
        if (context.PairedFallbackTailYieldGranted ||
            !context.LocalFallbackTailYieldGranted ||
            !context.LocalFallbackTailYieldPeerVisible ||
            !context.PeerFallbackTailYieldGranted ||
            string.IsNullOrWhiteSpace(context.LocalFallbackTailYieldOwnerKey) ||
            !string.Equals(context.LocalFallbackTailYieldOwnerKey, context.PeerFallbackTailYieldOwnerKey, StringComparison.Ordinal))
        {
            return;
        }

        context.PairedFallbackTailYieldGranted = true;
        LogFileTransferFallbackTailPairedYieldGranted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            context.LocalFallbackTailYieldOwnerKey,
            context.FallbackTailYieldGeneration,
            reason);
        TryPublishRouteHintIfAuthorizedLocked(
            context,
            "fallback_tail_paired_yield_route_hint",
            "fallback_tail_paired_yield",
            "fallback_tail_paired_yield",
            runtimeUnlockAdmissionFactory: () => RequestRuntimeUnlockAdmissionLocked(context, "route_hint", "fallback_tail_paired_yield"));
        TryResumeInboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
            context,
            "fallback_tail_quiescent");
    }

    private static void ClearOutboundFallbackTailQuiescenceDiagnosticLocked(OutboundTransferContext context)
    {
        context.FallbackTailQuiescenceTuple = null;
        context.FallbackTailQuiescenceTupleRepeatCount = 0;
        context.FallbackTailQuiescenceTupleFirstSeenUtc = null;
        context.FallbackTailQuiescenceTupleLastDiagnosticUtc = null;
        context.FallbackTailQuiescenceSuppressedDiagnosticCount = 0;
        context.FallbackTailQuiescenceTimedOut = false;
    }

    private static void LogOutboundFallbackTailTerminalProofConsumedOnceLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        int committedChunk,
        string reason)
    {
        if (context.FallbackTailTerminalProofConsumedLogged)
        {
            return;
        }

        context.FallbackTailTerminalProofConsumedLogged = true;
        LogFileTransferFallbackTailTerminalProofConsumed(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            leg,
            committedChunk,
            context.ChunkCount,
            reason);
    }

    private static void LogInboundFallbackTailTerminalProofConsumedOnceLocked(
        InboundTransferContext context,
        FileTransferLeg leg,
        int committedChunk,
        string reason)
    {
        if (context.FallbackTailTerminalProofConsumedLogged)
        {
            return;
        }

        context.FallbackTailTerminalProofConsumedLogged = true;
        LogFileTransferFallbackTailTerminalProofConsumed(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            leg,
            committedChunk,
            context.ChunkCount,
            reason);
    }

    private void ConsumeOutboundFallbackTailTerminalProofBeforeTerminalLocked(
        OutboundTransferContext context,
        FileTransferTransferState terminalState,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (terminalState != FileTransferTransferState.Completed ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !IsCurrentPostTunaFallbackLeg(leg) ||
            context.ChunkCount <= 0)
        {
            return;
        }

        LogOutboundFallbackTailTerminalProofConsumedOnceLocked(
            context,
            leg!,
            context.ChunkCount,
            reason);

        var ownerKey = BuildFallbackOwnerKey(context.SessionId, context.TransferId, leg!);
        if (!string.Equals(context.RetiredFallbackTailOwnerKey, ownerKey, StringComparison.Ordinal))
        {
            ClearOutboundFallbackTailRecoveryOwnershipAfterYieldLocked(
                context,
                leg!,
                "fallback_tail_terminal_completed");
        }

        ClearOutboundFallbackTailQuiescenceDiagnosticLocked(context);
        ClearPostTunaFallbackControlPlanePressure(context.TransferId, "fallback_tail_terminal_completed");
    }

    private void ConsumeInboundFallbackTailTerminalProofBeforeTerminalLocked(
        InboundTransferContext context,
        FileTransferTransferState terminalState,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (terminalState != FileTransferTransferState.Completed ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !IsCurrentPostTunaFallbackLeg(leg) ||
            context.ChunkCount <= 0)
        {
            return;
        }

        LogInboundFallbackTailTerminalProofConsumedOnceLocked(
            context,
            leg!,
            context.ChunkCount,
            reason);

        var ownerKey = BuildFallbackOwnerKey(context.SessionId, context.TransferId, leg!);
        if (!string.Equals(context.RetiredFallbackTailOwnerKey, ownerKey, StringComparison.Ordinal))
        {
            ClearInboundFallbackTailRecoveryOwnershipAfterYieldLocked(
                context,
                leg!,
                "fallback_tail_terminal_completed");
        }

        ClearPostTunaFallbackControlPlanePressure(context.TransferId, "fallback_tail_terminal_completed");
    }

    private static void ObserveOutboundFallbackTailTupleLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        string tuple,
        string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(context.FallbackTailQuiescenceTuple, tuple, StringComparison.Ordinal))
        {
            context.FallbackTailQuiescenceTupleRepeatCount++;
            var shouldLog =
                context.FallbackTailQuiescenceTupleLastDiagnosticUtc is not { } lastDiagnostic ||
                now - lastDiagnostic >= TimeSpan.FromMilliseconds(V6ReceiverStateDeferredDiagnosticCoalesceMs);
            if (shouldLog)
            {
                var suppressedCount = context.FallbackTailQuiescenceSuppressedDiagnosticCount;
                context.FallbackTailQuiescenceTupleLastDiagnosticUtc = now;
                context.FallbackTailQuiescenceSuppressedDiagnosticCount = 0;
                LogFileTransferFallbackTailProgressCoalesced(
                    context.TransferId,
                    context.SessionId,
                    leg,
                    tuple,
                    context.FallbackTailQuiescenceTupleRepeatCount,
                    reason,
                    suppressedCount);
            }
            else
            {
                context.FallbackTailQuiescenceSuppressedDiagnosticCount++;
            }

            return;
        }

        context.FallbackTailQuiescenceTuple = tuple;
        context.FallbackTailQuiescenceTupleRepeatCount = 1;
        context.FallbackTailQuiescenceTupleFirstSeenUtc = now;
        context.FallbackTailQuiescenceTupleLastDiagnosticUtc = null;
        context.FallbackTailQuiescenceSuppressedDiagnosticCount = 0;
        context.FallbackTailQuiescenceTimedOut = false;
    }

    private static bool TrySuppressRetiredFallbackTailOwnerUpdateLocked(
        OutboundTransferContext context,
        FileTransferLeg? leg,
        string source,
        string attemptedAction,
        string reason)
    {
        if (leg is null ||
            string.IsNullOrWhiteSpace(context.RetiredFallbackTailOwnerKey))
        {
            return false;
        }

        var ownerKey = BuildFallbackOwnerKey(context.SessionId, context.TransferId, leg);
        if (!string.Equals(ownerKey, context.RetiredFallbackTailOwnerKey, StringComparison.Ordinal))
        {
            return false;
        }

        var suppressionKey = string.Join("|", source, attemptedAction, reason, ownerKey);
        if (context.RetiredFallbackTailOwnerSuppressionKeys.Add(suppressionKey))
        {
            LogFileTransferFallbackTailRetiredOwnerUpdateSuppressed(
                context,
                leg,
                ownerKey,
                source,
                attemptedAction,
                reason);
        }

        return true;
    }

    private static bool TrySuppressRetiredFallbackTailOwnerUpdateLocked(
        InboundTransferContext context,
        FileTransferLeg? leg,
        string source,
        string attemptedAction,
        string reason)
    {
        if (leg is null ||
            string.IsNullOrWhiteSpace(context.RetiredFallbackTailOwnerKey))
        {
            return false;
        }

        var ownerKey = BuildFallbackOwnerKey(context.SessionId, context.TransferId, leg);
        if (!string.Equals(ownerKey, context.RetiredFallbackTailOwnerKey, StringComparison.Ordinal))
        {
            return false;
        }

        var suppressionKey = string.Join("|", source, attemptedAction, reason, ownerKey);
        if (context.RetiredFallbackTailOwnerSuppressionKeys.Add(suppressionKey))
        {
            LogFileTransferFallbackTailRetiredOwnerUpdateSuppressed(
                context,
                leg,
                ownerKey,
                source,
                attemptedAction,
                reason);
        }

        return true;
    }

    private static bool IsPostYieldFallbackTailOwnerRetiredAndYieldedLocked(
        OutboundTransferContext context,
        FileTransferLeg? leg)
        => !string.IsNullOrWhiteSpace(context.RetiredFallbackTailOwnerKey) &&
           context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           IsCurrentPostTunaFallbackLeg(leg) &&
           leg!.State == FileTransferLegState.Active &&
           leg.CanSendData &&
           string.IsNullOrWhiteSpace(leg.CheckpointRequestId) &&
           !context.PullPostTunaRecoveryActive &&
           !context.PullTransportFrontierOnlyRepairActive &&
           !context.V6RegularNknFallbackCheckpointDeliveryRecoveryPending &&
           context.V6RegularNknStateRefreshSendInFlight == 0 &&
           context.V6RegularNknDeferredStateRefreshRequest is null &&
           !IsV6TransportHandoffBlockingTail(context.V6TransportHandoff);

    private static bool IsPostYieldFallbackTailOwnerRetiredAndYieldedLocked(
        InboundTransferContext context,
        FileTransferLeg? leg)
        => !string.IsNullOrWhiteSpace(context.RetiredFallbackTailOwnerKey) &&
           context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           IsCurrentPostTunaFallbackLeg(leg) &&
           leg!.State == FileTransferLegState.Active &&
           leg.CanSendData &&
           string.IsNullOrWhiteSpace(leg.CheckpointRequestId) &&
           !context.PullPostTunaRecoveryActive &&
           !context.V6PostTunaFallbackProofReplayReceiverStatePending &&
           !context.V6PostTunaFallbackProofReplayFrontierRequestPending &&
           !IsV6TransportHandoffBlockingTail(context.V6TransportHandoff);

    private static bool TrySuppressPostYieldFallbackRecoveryRestartLocked(
        OutboundTransferContext context,
        string source,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (!IsPostYieldFallbackTailOwnerRetiredAndYieldedLocked(context, leg))
        {
            return false;
        }

        var ownerKey = BuildFallbackOwnerKey(context.SessionId, context.TransferId, leg!);
        var suppressionKey = string.Join(
            "|",
            source,
            "start_post_tuna_recovery",
            reason,
            ownerKey,
            context.RetiredFallbackTailOwnerKey);
        if (context.RetiredFallbackTailOwnerSuppressionKeys.Add(suppressionKey))
        {
            LogFileTransferFallbackTailRetiredOwnerUpdateSuppressed(
                context,
                leg,
                ownerKey,
                source,
                "start_post_tuna_recovery",
                "post_yield_recovery_restart");
        }

        return true;
    }

    private static bool TrySuppressPostYieldFallbackRecoveryRestartLocked(
        InboundTransferContext context,
        string source,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (!IsPostYieldFallbackTailOwnerRetiredAndYieldedLocked(context, leg))
        {
            return false;
        }

        var ownerKey = BuildFallbackOwnerKey(context.SessionId, context.TransferId, leg!);
        var suppressionKey = string.Join(
            "|",
            source,
            "start_post_tuna_recovery",
            reason,
            ownerKey,
            context.RetiredFallbackTailOwnerKey);
        if (context.RetiredFallbackTailOwnerSuppressionKeys.Add(suppressionKey))
        {
            LogFileTransferFallbackTailRetiredOwnerUpdateSuppressed(
                context,
                leg,
                ownerKey,
                source,
                "start_post_tuna_recovery",
                "post_yield_recovery_restart");
        }

        return true;
    }

    private static void LogFileTransferFallbackTailQuiescenceTimeout(
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        string tuple,
        int repeatCount,
        string reason)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_quiescence_timeout; direction=outbound; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; repeat_count={repeatCount}; tuple={FormatProtocolLogValue(tuple)}; state={FormatProtocolLogValue(FileTransferCoordinator.FormatLegState(leg.State))}; reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferFallbackTailFrontierRepairCleared(
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        int previousStartChunkIndex,
        int committedChunk,
        int highestObservedChunk,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_frontier_repair_cleared; direction=outbound; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; previous_frontier_only_start_chunk_index={previousStartChunkIndex}; committed_chunk={committedChunk}; highest_observed_chunk={highestObservedChunk}; reason={FormatProtocolLogValue(reason)}");

    private static void ClearOutboundFallbackTailRecoveryOwnershipAfterYieldLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        string reason)
    {
        var previousHandoffEpoch = context.V6TransportHandoff?.EpochId ?? 0;
        var previousHandoffState = context.V6TransportHandoff is { } handoff
            ? FormatV6TransportHandoffState(handoff.State)
            : "none";
        var discardedRepairFrameCount = context.PullV4SenderPumpRepairQueue.Count;
        var discardedRepairChunkCount = context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count);

        if (leg.TransportEpochId > 0)
        {
            context.LastRecoveredV6TransportHandoffEpoch = Math.Max(
                context.LastRecoveredV6TransportHandoffEpoch,
                leg.TransportEpochId);
        }

        context.V6TransportHandoff = null;
        context.PullPostTunaRecoveryActive = false;
        context.PullPostTunaRecoveryFrontierChunkIndex = -1;
        context.PullTransportRebindGeneration = 0;
        context.PullTransportLastSafetyReplayGeneration = 0;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = -1;
        context.PullTransportLastSafetyReplayEndChunkIndex = -1;
        context.PullTransportLastSafetyReplayUtc = null;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        context.PullTransportResumeRequestPending = false;
        context.V6RegularNknStateRefreshSendInFlight = 0;
        context.V6RegularNknStateRefreshActiveSendGeneration = 0;
        context.V6RegularNknStateRefreshActiveRequestId = null;
        context.V6RegularNknStateRefreshActivePriority = null;
        context.V6RegularNknStateRefreshActiveStartedUtc = null;
        context.V6RegularNknStateRefreshActiveReusedPendingCheckpoint = false;
        context.V6RegularNknCheckpointSyncSendInFlight = 0;
        ClearOutboundV6RegularNknDeferredStateRefreshLocked(context);
        ClearOutboundFallbackCheckpointDeliveryRecoveryPendingLocked(context, reason);
        ClearOutboundFallbackCheckpointRepairAuthorityLocked(context);
        context.PullV4SenderPumpRepairQueue.Clear();
        context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
        foreach (var repairState in context.PullV4SenderPumpRepairRequests.Values)
        {
            repairState.Queued = false;
            repairState.QueuedUtc = null;
            repairState.InFlight = false;
        }

        context.RetiredFallbackTailOwnerKey = BuildFallbackOwnerKey(
            context.SessionId,
            context.TransferId,
            leg);
        context.RetiredFallbackTailOwnerSuppressionKeys.Clear();

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; previous_handoff_epoch={previousHandoffEpoch}; previous_handoff_state={FormatProtocolLogValue(previousHandoffState)}; discarded_repair_frame_count={discardedRepairFrameCount}; discarded_repair_chunk_count={discardedRepairChunkCount}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void ClearInboundFallbackTailRecoveryOwnershipAfterYieldLocked(
        InboundTransferContext context,
        FileTransferLeg leg,
        string reason)
    {
        var previousHandoffEpoch = context.V6TransportHandoff?.EpochId ?? 0;
        var previousHandoffState = context.V6TransportHandoff is { } handoff
            ? FormatV6TransportHandoffState(handoff.State)
            : "none";

        if (leg.TransportEpochId > 0)
        {
            context.LastRecoveredV6TransportHandoffEpoch = Math.Max(
                context.LastRecoveredV6TransportHandoffEpoch,
                leg.TransportEpochId);
        }

        context.V6TransportHandoff = null;
        context.PullPostTunaRecoveryActive = false;
        context.PullPostTunaRecoveryFrontierChunkIndex = -1;
        context.PullTransportRebindGeneration = 0;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindStableProgressSamples = 0;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        ClearInboundV6PostTunaFallbackProofReplayLocked(context);
        ClearInboundPostTunaFallbackPressureDiagnosticLocked(context);
        ClearInboundPrimaryRegularNknFrontierRepairTransactionLocked(context);

        context.RetiredFallbackTailOwnerKey = BuildFallbackOwnerKey(
            context.SessionId,
            context.TransferId,
            leg);
        context.RetiredFallbackTailOwnerSuppressionKeys.Clear();

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=inbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; previous_handoff_epoch={previousHandoffEpoch}; previous_handoff_state={FormatProtocolLogValue(previousHandoffState)}; discarded_repair_frame_count=0; discarded_repair_chunk_count=0; reason={FormatProtocolLogValue(reason)}");
    }

    private static bool IsCurrentPostTunaFallbackLegCheckpointPending(FileTransferLeg? leg)
        => FileTransferCoordinator.IsCurrentPostTunaFallbackLegCheckpointPending(leg);

    private static bool IsCurrentPostTunaFallbackTailReconciliationCheckpointPending(FileTransferLeg? leg)
        => IsCurrentPostTunaFallbackLegCheckpointPending(leg) &&
           string.Equals(leg!.CheckpointPriority, V6RegularNknStateRefreshTailReconciliationPriority, StringComparison.Ordinal);

    private static bool IsCurrentPostTunaFallbackLeg(FileTransferLeg? leg)
        => FileTransferCoordinator.IsCurrentPostTunaFallbackLeg(leg);

    private static bool TryAcceptOutboundFallbackCheckpointWithCoordinatorLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        long transportEpoch,
        int committed,
        int highestObserved,
        string? checkpointRequestId,
        string reason)
    {
        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackCheckpointAccept,
                reason,
                "fallback_checkpoint_accept",
                leg,
                checkpointRequestId,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                leg.CheckpointGeneration,
                null,
                context.CurrentFallbackCheckpointLease,
                null,
                null,
                null,
                null,
                null,
                null,
                out _))
        {
            return false;
        }

        return TryApplyOutboundCoordinatorLegMutationLocked(
            context,
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointAccepted,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                reason,
                FileTransferLegState.RecoveryActive,
                CanSendData: true,
                committed,
                highestObserved,
                transportEpoch,
                leg.BridgeRecoveryGeneration,
                CheckpointRequestId: checkpointRequestId,
                FallbackCheckpointLease: context.CurrentFallbackCheckpointLease),
            FileTransferCoordinatorActionKind.FallbackCheckpointAccept,
            "fallback_checkpoint_accept",
            leg,
            checkpointRequestId,
            transportEpoch,
            out _);
    }

    private static bool TryAcceptInboundFallbackCheckpointWithCoordinatorLocked(
        InboundTransferContext context,
        FileTransferLeg leg,
        long transportEpoch,
        int committed,
        int highestObserved,
        string? checkpointRequestId,
        string reason)
    {
        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackCheckpointAccept,
                reason,
                "fallback_checkpoint_accept",
                leg,
                checkpointRequestId,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                leg.CheckpointGeneration,
                null,
                context.CurrentFallbackCheckpointLease,
                null,
                null,
                null,
                null,
                null,
                null,
                out _))
        {
            return false;
        }

        return TryApplyInboundCoordinatorLegMutationLocked(
            context,
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointAccepted,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                reason,
                FileTransferLegState.RecoveryActive,
                CanSendData: true,
                committed,
                highestObserved,
                transportEpoch,
                leg.BridgeRecoveryGeneration,
                CheckpointRequestId: checkpointRequestId,
                FallbackCheckpointLease: context.CurrentFallbackCheckpointLease),
            FileTransferCoordinatorActionKind.FallbackCheckpointAccept,
            "fallback_checkpoint_accept",
            leg,
            checkpointRequestId,
            transportEpoch,
            out _);
    }

    private static bool TryMarkOutboundFallbackTailQuiescentWithCoordinatorLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        long transportEpoch,
        int committed,
        int highestObserved,
        string reason)
    {
        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                reason,
                "fallback_tail_quiescent",
                leg,
                null,
                transportEpoch,
                leg.BridgeRecoveryGeneration,
                leg.CheckpointGeneration,
                null,
                context.CurrentFallbackCheckpointLease,
                null,
                null,
                null,
                null,
                null,
                FallbackTailYieldKind.Quiescent,
                out _))
        {
            return false;
        }

        return TryApplyOutboundCoordinatorLegMutationLocked(
            context,
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                reason,
                FileTransferLegState.Active,
                CanSendData: true,
                committed,
                highestObserved,
                transportEpoch,
                leg.BridgeRecoveryGeneration,
                FallbackCheckpointLease: context.CurrentFallbackCheckpointLease,
                FallbackTailYieldKind: FallbackTailYieldKind.Quiescent),
            FileTransferCoordinatorActionKind.FallbackTailQuiescent,
            "fallback_tail_quiescent",
            leg,
            null,
            transportEpoch,
            out _);
    }

    private static bool TryMarkInboundFallbackTailQuiescentWithCoordinatorLocked(
        InboundTransferContext context,
        FileTransferLeg leg,
        long transportEpoch,
        int committed,
        int highestObserved,
        string reason)
    {
        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                reason,
                "fallback_tail_quiescent",
                leg,
                null,
                transportEpoch,
                leg.BridgeRecoveryGeneration,
                leg.CheckpointGeneration,
                null,
                context.CurrentFallbackCheckpointLease,
                null,
                null,
                null,
                null,
                null,
                FallbackTailYieldKind.Quiescent,
                out _))
        {
            return false;
        }

        return TryApplyInboundCoordinatorLegMutationLocked(
            context,
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                reason,
                FileTransferLegState.Active,
                CanSendData: true,
                committed,
                highestObserved,
                transportEpoch,
                leg.BridgeRecoveryGeneration,
                FallbackCheckpointLease: context.CurrentFallbackCheckpointLease,
                FallbackTailYieldKind: FallbackTailYieldKind.Quiescent),
            FileTransferCoordinatorActionKind.FallbackTailQuiescent,
            "fallback_tail_quiescent",
            leg,
            null,
            transportEpoch,
            out _);
    }

    private static bool TryMarkOutboundFallbackTailFailedWithCoordinatorLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        string reason)
    {
        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackTailFailed,
                reason,
                "fallback_tail_failed",
                leg,
                null,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                leg.CheckpointGeneration,
                null,
                context.CurrentFallbackCheckpointLease,
                null,
                null,
                null,
                null,
                null,
                FallbackTailYieldKind.Failed,
                out _))
        {
            return false;
        }

        return TryApplyOutboundCoordinatorLegMutationLocked(
            context,
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailFailed,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                reason,
                FileTransferLegState.RecoveryActive,
                CanSendData: false,
                Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                FallbackCheckpointLease: context.CurrentFallbackCheckpointLease,
                FallbackTailYieldKind: FallbackTailYieldKind.Failed),
            FileTransferCoordinatorActionKind.FallbackTailFailed,
            "fallback_tail_failed",
            leg,
            null,
            leg.TransportEpochId,
            out _);
    }

    private static bool TryAcceptInboundOwnerlessFallbackCheckpointFromCurrentProgressLocked(
        InboundTransferContext context,
        FileTransferLeg leg,
        long transportEpoch,
        int committed,
        int highestObserved,
        string reason)
    {
        if (leg.State is not (FileTransferLegState.CheckpointPending or FileTransferLegState.BridgeRestartPending) ||
            leg.CanSendData ||
            !string.IsNullOrWhiteSpace(leg.CheckpointRequestId))
        {
            return false;
        }

        var effectiveTransportEpoch = transportEpoch > 0 ? transportEpoch : leg.TransportEpochId;
        if (effectiveTransportEpoch <= 0 ||
            (leg.TransportEpochId > 0 && leg.TransportEpochId != effectiveTransportEpoch))
        {
            return false;
        }

        var startCommitted = Math.Max(0, leg.StartCommittedChunkIndex);
        if (committed <= startCommitted)
        {
            return false;
        }

        LogFileTransferFallbackStaleProofIgnored(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            leg,
            "ownerless_current_progress_evidence_only",
            effectiveTransportEpoch,
            null,
            context.FallbackStaleProofSuppressionKeys);
        return false;
    }

    private bool TryMarkOutboundFallbackTailQuiescentOrTerminalLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        bool stateRequestsFrontierGap,
        string reason,
        out bool terminalProofConsumed)
    {
        terminalProofConsumed = false;
        var leg = context.CurrentTransferLeg;
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !IsCurrentPostTunaFallbackLeg(leg) ||
            leg!.State != FileTransferLegState.RecoveryActive ||
            !leg.CanSendData ||
            !string.IsNullOrWhiteSpace(leg.CheckpointRequestId) ||
            context.IsTerminal)
        {
            return false;
        }

        var currentLiveRouteEpoch = context.CurrentLiveRouteEpoch?.EpochId ?? 0;
        if (leg.LiveRouteEpochId > 0 &&
            currentLiveRouteEpoch > 0 &&
            leg.LiveRouteEpochId != currentLiveRouteEpoch)
        {
            return false;
        }

        var committed = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var highestObserved = Math.Max(-1, state.DurableReceivedHighestChunkIndex);
        var checkpointId = leg.CheckpointRequestId ?? "(none)";
        var tuple = BuildFallbackChurnTuple(
            context.SessionId,
            context.TransferId,
            leg.RouteSelection.TelemetryToken,
            leg.ProtocolVersion,
            leg.LiveRouteEpochId,
            leg.Generation,
            leg.TransportEpochId,
            committed,
            highestObserved,
            stateRequestsFrontierGap ? 1 : 0,
            state.MissingRanges.Count,
            checkpointId);
        ObserveOutboundFallbackTailTupleLocked(context, leg, tuple, reason);

        if (state.TerminalReady && committed >= context.ChunkCount && context.ChunkCount > 0)
        {
            terminalProofConsumed = true;
            LogOutboundFallbackTailTerminalProofConsumedOnceLocked(context, leg, committed, reason);
            return false;
        }

        var stateCarriesFallbackRecoveryWork =
            !string.IsNullOrWhiteSpace(state.RepairRequestId) ||
            !string.IsNullOrWhiteSpace(state.RecoveryMode);

        var movingFrontierRepairSatisfied = false;
        if (context.PullTransportFrontierOnlyRepairActive)
        {
            var previousStart = context.PullTransportFrontierOnlyRepairStartChunkIndex;
            var frontierRepairSatisfied =
                previousStart < 0 ||
                committed > previousStart ||
                (!stateRequestsFrontierGap && highestObserved >= previousStart);
            if (frontierRepairSatisfied)
            {
                movingFrontierRepairSatisfied = true;
                context.PullTransportFrontierOnlyRepairActive = false;
                context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
                LogFileTransferFallbackTailFrontierRepairCleared(
                    context.TransferId,
                    context.SessionId,
                    leg,
                    previousStart,
                    committed,
                    highestObserved,
                    reason);
            }
        }

        var explicitFrontierRepairSatisfied =
            movingFrontierRepairSatisfied &&
            (string.Equals(state.Priority, "frontier", StringComparison.OrdinalIgnoreCase) ||
             (state.RepairRequestId?.StartsWith("v6-frontier:", StringComparison.OrdinalIgnoreCase) ?? false));
        var explicitFallbackRepairWorkPending =
            stateCarriesFallbackRecoveryWork &&
            state.MissingRanges.Count > 0 &&
            !explicitFrontierRepairSatisfied;
        var stateRecoveryWorkStillBlocking =
            explicitFallbackRepairWorkPending ||
            (stateCarriesFallbackRecoveryWork && !movingFrontierRepairSatisfied);
        if (!stateRecoveryWorkStillBlocking &&
            (!stateRequestsFrontierGap || movingFrontierRepairSatisfied) &&
            context.V6TransportHandoff is { State: V6TransportHandoffState.BackfillRepair })
        {
            CompleteOutboundV6TransportHandoffLocked(
                context,
                "fallback_tail_backfill_quiescent",
                committed,
                highestObserved);
        }
        else if (!stateRecoveryWorkStillBlocking &&
                 movingFrontierRepairSatisfied &&
                 IsV6TransportHandoffBlockingTail(context.V6TransportHandoff))
        {
            CompleteOutboundV6TransportHandoffLocked(
                context,
                "fallback_tail_moving_frontier_quiescent",
                committed,
                highestObserved);
        }

        var pendingOwnerWork =
            context.V6RegularNknFallbackCheckpointDeliveryRecoveryPending ||
            context.V6RegularNknStateRefreshSendInFlight != 0 ||
            context.V6RegularNknDeferredStateRefreshRequest is not null ||
            context.PullTransportFrontierOnlyRepairActive ||
            IsV6TransportHandoffBlockingTail(context.V6TransportHandoff);
        if (pendingOwnerWork || stateRecoveryWorkStillBlocking)
        {
            if (context.FallbackTailQuiescenceTupleRepeatCount >= V6PostTunaFallbackTailQuiescenceRepeatLimit)
            {
                if (TryMarkOutboundFallbackTailFailedWithCoordinatorLocked(context, leg, reason) &&
                    !context.FallbackTailQuiescenceTimedOut)
                {
                    context.FallbackTailQuiescenceTimedOut = true;
                    LogFileTransferFallbackTailQuiescenceTimeout(
                        context.TransferId,
                        context.SessionId,
                        leg,
                        tuple,
                        context.FallbackTailQuiescenceTupleRepeatCount,
                        reason);
                }
            }

            return false;
        }

        if (!TryMarkOutboundFallbackTailQuiescentWithCoordinatorLocked(
                context,
                leg,
                state.TransportEpoch,
                committed,
                highestObserved,
                reason))
        {
            return false;
        }

        ClearOutboundFallbackTailRecoveryOwnershipAfterYieldLocked(
            context,
            leg,
            "fallback_tail_quiescent");
        ClearOutboundFallbackTailQuiescenceDiagnosticLocked(context);
        MaybeClearOutboundV6RegularNknFrontierPressureLocked(
            context,
            state,
            "fallback_tail_quiescent",
            forceClear: true);
        ClearPostTunaFallbackControlPlanePressure(context.TransferId, "fallback_tail_quiescent");
        TryPublishRouteHintIfAuthorizedLocked(
            context,
            "fallback_tail_quiescent_route_hint",
            "fallback_tail_quiescent",
            "fallback_tail_quiescent",
            runtimeUnlockAdmissionFactory: () => RequestRuntimeUnlockAdmissionLocked(context, "route_hint", "fallback_tail_quiescent"));
        LogFileTransferFallbackTailQuiescentYieldGranted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            leg,
            committed,
            highestObserved,
            reason);
        RecordOutboundFallbackTailLocalYieldLocked(
            context,
            leg,
            committed,
            highestObserved,
            "fallback_tail_quiescent");
        return true;
    }

    private static bool TryMarkOutboundFallbackTailRepairTimeoutLocked(
        OutboundTransferContext context,
        int frontierChunkIndex,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !IsCurrentPostTunaFallbackLeg(leg) ||
            leg!.State is not FileTransferLegState.RecoveryActive and not FileTransferLegState.BridgeRestartPending ||
            context.IsTerminal)
        {
            return false;
        }

        if (TrySuppressRetiredFallbackTailOwnerUpdateLocked(
                context,
                leg,
                "fallback_tail_repair_timeout",
                "mark_tail_timeout",
                reason))
        {
            return false;
        }

        var tuple = string.Join(
            "|",
            "repair",
            context.SessionId,
            context.TransferId,
            leg.RouteSelection.TelemetryToken,
            leg.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            leg.LiveRouteEpochId.ToString(CultureInfo.InvariantCulture),
            leg.Generation,
            leg.TransportEpochId.ToString(CultureInfo.InvariantCulture),
            Math.Max(0, frontierChunkIndex),
            Math.Max(-1, context.ChunksAcceptedForTransport - 1),
            Math.Max(0, context.RemoteGrantedUntilExclusive),
            leg.CheckpointRequestId ?? "(none)");
        ObserveOutboundFallbackTailTupleLocked(context, leg, tuple, reason);

        if (context.FallbackTailQuiescenceTupleRepeatCount < V6PostTunaFallbackTailQuiescenceRepeatLimit ||
            context.FallbackTailQuiescenceTimedOut)
        {
            return context.FallbackTailQuiescenceTimedOut;
        }

        if (!TryMarkOutboundFallbackTailFailedWithCoordinatorLocked(context, leg, reason))
        {
            return false;
        }

        context.FallbackTailQuiescenceTimedOut = true;
        LogFileTransferFallbackTailQuiescenceTimeout(
            context.TransferId,
            context.SessionId,
            leg,
            tuple,
            context.FallbackTailQuiescenceTupleRepeatCount,
            reason);
        return true;
    }

    private bool TryMarkInboundFallbackTailQuiescentLocked(
        InboundTransferContext context,
        long transportEpoch,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !IsCurrentPostTunaFallbackLeg(leg) ||
            context.IsTerminal)
        {
            return false;
        }

        var currentLeg = leg!;
        if (TrySuppressRetiredFallbackTailOwnerUpdateLocked(
                context,
                currentLeg,
                "fallback_tail_quiescent",
                "mark_tail_quiescent",
                reason))
        {
            return false;
        }

        var currentLiveRouteEpoch = context.CurrentLiveRouteEpoch?.EpochId ?? 0;
        if (currentLeg.LiveRouteEpochId > 0 &&
            currentLiveRouteEpoch > 0 &&
            currentLeg.LiveRouteEpochId != currentLiveRouteEpoch)
        {
            return false;
        }

        var committed = Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var highestObserved = Math.Max(-1, context.PullHighestReceivedChunkIndex);
        if (committed >= context.ChunkCount && context.ChunkCount > 0)
        {
            LogInboundFallbackTailTerminalProofConsumedOnceLocked(context, currentLeg, committed, reason);
            return false;
        }

        TryAcceptInboundOwnerlessFallbackCheckpointFromCurrentProgressLocked(
            context,
            currentLeg,
            transportEpoch,
            committed,
            highestObserved,
            reason);

        if (currentLeg.State != FileTransferLegState.RecoveryActive ||
            !currentLeg.CanSendData ||
            !string.IsNullOrWhiteSpace(currentLeg.CheckpointRequestId))
        {
            return false;
        }

        var proofReplayPending =
            context.V6PostTunaFallbackProofReplayReceiverStatePending ||
            context.V6PostTunaFallbackProofReplayFrontierRequestPending;
        if (proofReplayPending &&
            context.V6PostTunaFallbackProofReplayStartChunkIndex >= 0 &&
            committed > context.V6PostTunaFallbackProofReplayStartChunkIndex)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_post_tuna_fallback_proof_replay_completed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; replay_generation={context.V6PostTunaFallbackProofReplayLoopGeneration}; reason=progress_observed_quiescence_check; start_chunk_index={context.V6PostTunaFallbackProofReplayStartChunkIndex}; current_chunk_index={committed}");
            ClearInboundV6PostTunaFallbackProofReplayLocked(context);
            proofReplayPending = false;
        }

        if (proofReplayPending)
        {
            return false;
        }

        if (IsV6TransportHandoffBlockingTail(context.V6TransportHandoff))
        {
            CompleteInboundV6TransportHandoffLocked(
                context,
                "fallback_tail_moving_frontier_quiescent",
                committed,
                highestObserved);
        }

        if (IsV6TransportHandoffBlockingTail(context.V6TransportHandoff))
        {
            return false;
        }

        if (!TryMarkInboundFallbackTailQuiescentWithCoordinatorLocked(
                context,
                currentLeg,
                transportEpoch,
                committed,
                highestObserved,
                reason))
        {
            return false;
        }

        ClearInboundFallbackTailRecoveryOwnershipAfterYieldLocked(
            context,
            currentLeg,
            "fallback_tail_quiescent");
        LogFileTransferFallbackTailQuiescentYieldGranted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            currentLeg.ProvenCommittedChunkIndex,
            currentLeg.ProvenHighestObservedChunkIndex,
            reason);
        ClearPostTunaFallbackControlPlanePressure(context.TransferId, "fallback_tail_quiescent");
        TryPublishRouteHintIfAuthorizedLocked(
            context,
            "fallback_tail_quiescent_route_hint",
            "fallback_tail_quiescent",
            "fallback_tail_quiescent",
            runtimeUnlockAdmissionFactory: () => RequestRuntimeUnlockAdmissionLocked(context, "route_hint", "fallback_tail_quiescent"));
        RecordInboundFallbackTailLocalYieldLocked(
            context,
            currentLeg,
            currentLeg.ProvenCommittedChunkIndex,
            currentLeg.ProvenHighestObservedChunkIndex,
            "fallback_tail_quiescent");
        return true;
    }

    private bool TryConsumeInboundFallbackTailReceiverStateProofLocked(
        InboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        FileTransferControlPlaneDeliveryRequest request,
        FileTransferControlPlaneDeliveryResult? result,
        string reason)
    {
        if (result is not { PeerVisibleAny: true } ||
            request.Kind is not (FileTransferControlPlaneKind.ReceiverState or FileTransferControlPlaneKind.FallbackCheckpointProof) ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            context.IsTerminal ||
            !string.Equals(state.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(state.TransferId, context.TransferId, StringComparison.Ordinal))
        {
            return false;
        }

        var leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return false;
        }

        var currentLeg = leg!;
        if (currentLeg.TransportEpochId > 0 &&
            state.TransportEpoch > 0 &&
            currentLeg.TransportEpochId != state.TransportEpoch)
        {
            return false;
        }

        context.V6PostTunaFallbackProofReplayReceiverStatePending = false;
        if (state.MissingRanges.Count == 0)
        {
            context.V6PostTunaFallbackProofReplayFrontierRequestPending = false;
        }

        if (!context.V6PostTunaFallbackProofReplayReceiverStatePending &&
            !context.V6PostTunaFallbackProofReplayFrontierRequestPending)
        {
            ClearInboundV6PostTunaFallbackProofReplayLocked(context);
        }

        return TryMarkInboundFallbackTailQuiescentLocked(
            context,
            state.TransportEpoch > 0 ? state.TransportEpoch : request.TransportEpoch,
            reason);
    }

    private static FileTransferReceiveRecoveryRequest AttachFallbackLegAuthority(
        OutboundTransferContext context,
        FileTransferReceiveRecoveryRequest request,
        string authorityReason,
        string? checkpointRequestId = null,
        long? transportEpoch = null)
    {
        var leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return request;
        }

        return request with
        {
            RouteToken = leg!.RouteSelection.TelemetryToken,
            ProtocolVersion = leg.ProtocolVersion,
            LiveRouteEpoch = leg.LiveRouteEpochId,
            TransferLegGeneration = leg.Generation,
            BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
            TransportEpoch = transportEpoch ?? leg.TransportEpochId,
            CheckpointRequestId = checkpointRequestId ?? leg.CheckpointRequestId,
            AuthorityReason = authorityReason,
        };
    }

    private static FileTransferReceiveRecoveryRequest AttachFallbackLegAuthority(
        InboundTransferContext context,
        FileTransferReceiveRecoveryRequest request,
        string authorityReason,
        string? checkpointRequestId = null,
        long? transportEpoch = null)
    {
        var leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return request;
        }

        return request with
        {
            RouteToken = leg!.RouteSelection.TelemetryToken,
            ProtocolVersion = leg.ProtocolVersion,
            LiveRouteEpoch = leg.LiveRouteEpochId,
            TransferLegGeneration = leg.Generation,
            BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
            TransportEpoch = transportEpoch ?? leg.TransportEpochId,
            CheckpointRequestId = checkpointRequestId ?? leg.CheckpointRequestId,
            AuthorityReason = authorityReason,
        };
    }

    private static string FormatReceiveRecoveryAuthorityFields(FileTransferReceiveRecoveryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RouteToken) &&
            request.ProtocolVersion <= 0 &&
            request.TransferLegGeneration <= 0 &&
            request.TransportEpoch <= 0 &&
            string.IsNullOrWhiteSpace(request.CheckpointRequestId))
        {
            return string.Empty;
        }

        return
            $"; route={FormatProtocolLogValue(request.RouteToken ?? "(none)")}" +
            $"; protocol_version={request.ProtocolVersion}" +
            $"; live_route_epoch={request.LiveRouteEpoch}" +
            $"; leg_generation={request.TransferLegGeneration}" +
            $"; bridge_recovery_generation={request.BridgeRecoveryGeneration}" +
            $"; transport_epoch={request.TransportEpoch}" +
            $"; checkpoint_request_id={FormatProtocolLogValue(request.CheckpointRequestId ?? "(none)")}" +
            $"; authority_reason={FormatProtocolLogValue(request.AuthorityReason ?? "(none)")}" +
            $"; bridge_decision_generation={request.BridgeRecoveryAuthority?.BridgeDecisionGeneration ?? 0}";
    }

    private async Task<FileTransferControlPlaneDeliveryResult?> SendFileTransferControlPlaneOrDataSessionCoreAsync(
        IFileTransferDataSession dataSession,
        FileTransferControlPlaneDeliveryRequest request,
        CancellationToken ct)
    {
        if (transport is not IFileTransferControlPlaneDeliveryTransport controlPlaneTransport)
        {
            await dataSession.SendAsync(request.Frame, ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_control_plane_delivery_unavailable; kind={FormatFileTransferControlPlaneKind(request.Kind)}; transfer_id={request.Frame.TransferId}; session_id={request.Frame.SessionId}; frame_type={FormatProtocolLogValue(request.Frame.Type)}; reason={FormatProtocolLogValue(request.Reason)}; fallback=data_session");
            return null;
        }

        return await controlPlaneTransport.SendFileTransferControlPlaneFrameAsync(request, ct).ConfigureAwait(false);
    }

    private async Task SendFileTransferControlPlaneOrDataSessionAsync(
        IFileTransferDataSession dataSession,
        FileTransferControlPlaneDeliveryRequest request,
        CancellationToken ct)
    {
        var result = await SendFileTransferControlPlaneOrDataSessionCoreAsync(dataSession, request, ct).ConfigureAwait(false);
        if (result is not null)
        {
            LogFileTransferControlPlaneDeliveryResult(request, result);
        }
    }

    private static void LogFileTransferControlPlaneDeliveryResult(
        FileTransferControlPlaneDeliveryRequest request,
        FileTransferControlPlaneDeliveryResult result)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_control_plane_delivery_observed; direction={FormatFileTransferDirection(request.Direction)}; kind={FormatFileTransferControlPlaneKind(request.Kind)}; transfer_id={request.Frame.TransferId}; session_id={request.Frame.SessionId}; route={FormatProtocolLogValue(request.RouteToken ?? "(none)")}; protocol_version={request.ProtocolVersion}; live_route_epoch={request.LiveRouteEpoch}; leg_generation={request.TransferLegGeneration}; bridge_recovery_generation={request.BridgeRecoveryGeneration}; transport_epoch={request.TransportEpoch}; checkpoint_request_id={FormatProtocolLogValue(request.CheckpointRequestId ?? "(none)")}; reason={FormatProtocolLogValue(request.Reason)}; frame_type={FormatProtocolLogValue(request.Frame.Type)}; control_queue={(result.ControlQueue ? 1 : 0)}; control_copy={(result.ControlCopy ? 1 : 0)}; bulk_copy={(result.BulkCopy ? 1 : 0)}; peer_visible_any={(result.PeerVisibleAny ? 1 : 0)}; accepted_any={(result.AcceptedAny ? 1 : 0)}; local_only_rejected={(result.ControlQueue && !result.PeerVisibleAny && request.PeerVisibleRequired ? 1 : 0)}");
        if (!result.AcceptedAny)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_control_plane_delivery_failed; direction={FormatFileTransferDirection(request.Direction)}; kind={FormatFileTransferControlPlaneKind(request.Kind)}; transfer_id={request.Frame.TransferId}; session_id={request.Frame.SessionId}; route={FormatProtocolLogValue(request.RouteToken ?? "(none)")}; protocol_version={request.ProtocolVersion}; live_route_epoch={request.LiveRouteEpoch}; leg_generation={request.TransferLegGeneration}; bridge_recovery_generation={request.BridgeRecoveryGeneration}; transport_epoch={request.TransportEpoch}; checkpoint_request_id={FormatProtocolLogValue(request.CheckpointRequestId ?? "(none)")}; reason={FormatProtocolLogValue(request.Reason)}; frame_type={FormatProtocolLogValue(request.Frame.Type)}; control_queue_error={FormatProtocolLogValue(result.ControlQueueErrorName)}; control_copy_error={FormatProtocolLogValue(result.ControlCopyErrorName)}; bulk_copy_error={FormatProtocolLogValue(result.BulkCopyErrorName)}");
            throw new InvalidOperationException($"File-transfer control-plane delivery failed for {request.Kind}.");
        }
    }

    private bool TryAuthorizeOutboundFallbackControlPlaneSendLocked(
        OutboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        out FallbackControlPlaneToken? token)
    {
        token = null;
        if (!IsFallbackControlPlaneGenerationBound(request))
        {
            return true;
        }

        if (!TryValidateOutboundFallbackControlPlaneSendLocked(context, request, out var reason, out var currentLeg))
        {
            LogFileTransferControlPlaneSendRetiredStaleGeneration(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                request,
                currentLeg,
                reason,
                context.RetiredFallbackControlPlaneSendKeys);
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, reason);
            ClearPostTunaFallbackControlPlanePressure(context.TransferId, reason);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                "fallback_control_plane_send",
                request.Reason,
                RuntimeOwner: context.CurrentRuntimeOwner,
                Route: FileTransferRoute.PostTunaFallbackV6,
                ProtocolVersion: request.ProtocolVersion,
                LiveRouteEpoch: request.LiveRouteEpoch,
                LegGeneration: request.TransferLegGeneration,
                TransportEpochId: request.TransportEpoch,
                BridgeRecoveryGeneration: request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason,
                CheckpointGeneration: currentLeg?.CheckpointGeneration));
        if (!decision.Accepted || decision.FallbackControlPlaneToken is not { } authorizedToken)
        {
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(
                context,
                request,
                decision.Accepted ? "fallback_control_plane_token_missing" : decision.RejectionReason);
            return false;
        }

        var applyDecision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                request.Reason,
                FileTransferLegState.Active,
                CanSendData: true,
                currentLeg?.ProvenCommittedChunkIndex ?? 0,
                currentLeg?.ProvenHighestObservedChunkIndex ?? -1,
                request.TransportEpoch,
                request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                CheckpointGeneration: currentLeg?.CheckpointGeneration ?? 0,
                FallbackControlPlaneToken: authorizedToken,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason),
            CreateCoordinatorStateLocked(context));
        if (!string.IsNullOrWhiteSpace(applyDecision.MutationRejectedReason))
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                FormatFileTransferControlPlaneKind(request.Kind),
                applyDecision,
                currentLeg,
                request.CheckpointRequestId,
                request.TransportEpoch,
                request.Reason);
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, applyDecision);
        token = applyDecision.FallbackControlPlaneToken ?? authorizedToken;
        return token is not null;
    }

    private bool TryAuthorizeInboundFallbackControlPlaneSendLocked(
        InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        out FallbackControlPlaneToken? token)
    {
        token = null;
        if (!IsFallbackControlPlaneGenerationBound(request))
        {
            return true;
        }

        if (!TryValidateInboundFallbackControlPlaneSendLocked(context, request, out var reason, out var currentLeg))
        {
            LogFileTransferControlPlaneSendRetiredStaleGeneration(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                request,
                currentLeg,
                reason,
                context.RetiredFallbackControlPlaneSendKeys);
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, reason);
            ClearPostTunaFallbackControlPlanePressure(context.TransferId, reason);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                "fallback_control_plane_send",
                request.Reason,
                RuntimeOwner: context.CurrentRuntimeOwner,
                Route: FileTransferRoute.PostTunaFallbackV6,
                ProtocolVersion: request.ProtocolVersion,
                LiveRouteEpoch: request.LiveRouteEpoch,
                LegGeneration: request.TransferLegGeneration,
                TransportEpochId: request.TransportEpoch,
                BridgeRecoveryGeneration: request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason,
                CheckpointGeneration: currentLeg?.CheckpointGeneration));
        if (!decision.Accepted || decision.FallbackControlPlaneToken is not { } authorizedToken)
        {
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(
                context,
                request,
                decision.Accepted ? "fallback_control_plane_token_missing" : decision.RejectionReason);
            return false;
        }

        var applyDecision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                request.Reason,
                FileTransferLegState.Active,
                CanSendData: true,
                currentLeg?.ProvenCommittedChunkIndex ?? 0,
                currentLeg?.ProvenHighestObservedChunkIndex ?? -1,
                request.TransportEpoch,
                request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                CheckpointGeneration: currentLeg?.CheckpointGeneration ?? 0,
                FallbackControlPlaneToken: authorizedToken,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason),
            CreateCoordinatorStateLocked(context));
        if (!string.IsNullOrWhiteSpace(applyDecision.MutationRejectedReason))
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                FormatFileTransferControlPlaneKind(request.Kind),
                applyDecision,
                currentLeg,
                request.CheckpointRequestId,
                request.TransportEpoch,
                request.Reason);
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, applyDecision);
        token = applyDecision.FallbackControlPlaneToken ?? authorizedToken;
        return token is not null;
    }

    private bool TryCompleteOutboundFallbackControlPlaneSendLocked(
        OutboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        FallbackControlPlaneToken? token,
        FileTransferControlPlaneDeliveryResult result)
    {
        if (!IsFallbackControlPlaneGenerationBound(request))
        {
            return true;
        }

        if (token is not { } controlPlaneToken)
        {
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, "fallback_control_plane_token_missing");
            return false;
        }

        if (!TryValidateOutboundFallbackControlPlaneSendLocked(context, request, out var reason, out var currentLeg))
        {
            LogFileTransferControlPlaneSendRetiredStaleGeneration(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                request,
                currentLeg,
                reason,
                context.RetiredFallbackControlPlaneSendKeys);
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, reason);
            ClearPostTunaFallbackControlPlanePressure(context.TransferId, reason);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSendCompletion,
                "fallback_control_plane_send_completion",
                request.Reason,
                RuntimeOwner: context.CurrentRuntimeOwner,
                Route: FileTransferRoute.PostTunaFallbackV6,
                ProtocolVersion: request.ProtocolVersion,
                LiveRouteEpoch: request.LiveRouteEpoch,
                LegGeneration: request.TransferLegGeneration,
                TransportEpochId: request.TransportEpoch,
                BridgeRecoveryGeneration: request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                FallbackControlPlaneToken: controlPlaneToken,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason,
                ControlPlanePeerVisible: result.PeerVisibleAny,
                ControlPlaneAcceptedAny: result.AcceptedAny,
                CheckpointGeneration: currentLeg?.CheckpointGeneration));
        if (!decision.Accepted)
        {
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, decision.RejectionReason);
            return false;
        }

        var applyDecision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                request.Reason,
                FileTransferLegState.Active,
                CanSendData: true,
                currentLeg?.ProvenCommittedChunkIndex ?? 0,
                currentLeg?.ProvenHighestObservedChunkIndex ?? -1,
                request.TransportEpoch,
                request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                CheckpointGeneration: currentLeg?.CheckpointGeneration ?? 0,
                FallbackControlPlaneToken: controlPlaneToken,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason,
                ControlPlanePeerVisible: result.PeerVisibleAny,
                ControlPlaneAcceptedAny: result.AcceptedAny),
            CreateCoordinatorStateLocked(context));
        if (!string.IsNullOrWhiteSpace(applyDecision.MutationRejectedReason))
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.ControlPlaneSendCompletion,
                FormatFileTransferControlPlaneKind(request.Kind),
                applyDecision,
                currentLeg,
                request.CheckpointRequestId,
                request.TransportEpoch,
                request.Reason);
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, applyDecision);
        return true;
    }

    private bool TryCompleteInboundFallbackControlPlaneSendLocked(
        InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        FallbackControlPlaneToken? token,
        FileTransferControlPlaneDeliveryResult result)
    {
        if (!IsFallbackControlPlaneGenerationBound(request))
        {
            return true;
        }

        if (token is not { } controlPlaneToken)
        {
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, "fallback_control_plane_token_missing");
            return false;
        }

        if (!TryValidateInboundFallbackControlPlaneSendLocked(context, request, out var reason, out var currentLeg))
        {
            LogFileTransferControlPlaneSendRetiredStaleGeneration(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                request,
                currentLeg,
                reason,
                context.RetiredFallbackControlPlaneSendKeys);
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, reason);
            ClearPostTunaFallbackControlPlanePressure(context.TransferId, reason);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSendCompletion,
                "fallback_control_plane_send_completion",
                request.Reason,
                RuntimeOwner: context.CurrentRuntimeOwner,
                Route: FileTransferRoute.PostTunaFallbackV6,
                ProtocolVersion: request.ProtocolVersion,
                LiveRouteEpoch: request.LiveRouteEpoch,
                LegGeneration: request.TransferLegGeneration,
                TransportEpochId: request.TransportEpoch,
                BridgeRecoveryGeneration: request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                FallbackControlPlaneToken: controlPlaneToken,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason,
                ControlPlanePeerVisible: result.PeerVisibleAny,
                ControlPlaneAcceptedAny: result.AcceptedAny,
                CheckpointGeneration: currentLeg?.CheckpointGeneration));
        if (!decision.Accepted)
        {
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, decision.RejectionReason);
            return false;
        }

        var applyDecision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                request.Reason,
                FileTransferLegState.Active,
                CanSendData: true,
                currentLeg?.ProvenCommittedChunkIndex ?? 0,
                currentLeg?.ProvenHighestObservedChunkIndex ?? -1,
                request.TransportEpoch,
                request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                CheckpointGeneration: currentLeg?.CheckpointGeneration ?? 0,
                FallbackControlPlaneToken: controlPlaneToken,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason,
                ControlPlanePeerVisible: result.PeerVisibleAny,
                ControlPlaneAcceptedAny: result.AcceptedAny),
            CreateCoordinatorStateLocked(context));
        if (!string.IsNullOrWhiteSpace(applyDecision.MutationRejectedReason))
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.ControlPlaneSendCompletion,
                FormatFileTransferControlPlaneKind(request.Kind),
                applyDecision,
                currentLeg,
                request.CheckpointRequestId,
                request.TransportEpoch,
                request.Reason);
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, applyDecision);
        return true;
    }

    private async Task<bool> SendOutboundFallbackControlPlaneOrDataSessionAsync(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferControlPlaneDeliveryRequest request,
        CancellationToken ct)
    {
        FallbackControlPlaneToken? controlPlaneToken;
        lock (gate)
        {
            if (!TryAuthorizeOutboundFallbackControlPlaneSendLocked(context, request, out controlPlaneToken))
            {
                return false;
            }
        }

        var result = await SendFileTransferControlPlaneOrDataSessionCoreAsync(dataSession, request, ct).ConfigureAwait(false);
        if (result is null)
        {
            return true;
        }

        lock (gate)
        {
            if (!TryCompleteOutboundFallbackControlPlaneSendLocked(context, request, controlPlaneToken, result))
            {
                LogFileTransferControlPlaneDeliveryResult(request, result);
                return false;
            }
        }

        LogFileTransferControlPlaneDeliveryResult(request, result);
        return true;
    }

    private async Task<(bool Sent, FileTransferControlPlaneDeliveryResult? Result)> SendInboundFallbackControlPlaneOrDataSessionAsync(
        InboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferControlPlaneDeliveryRequest request,
        CancellationToken ct)
    {
        FallbackControlPlaneToken? controlPlaneToken;
        lock (gate)
        {
            if (!TryAuthorizeInboundFallbackControlPlaneSendLocked(context, request, out controlPlaneToken))
            {
                return (false, null);
            }
        }

        try
        {
            var result = await SendFileTransferControlPlaneOrDataSessionCoreAsync(dataSession, request, ct).ConfigureAwait(false);
            if (result is not null)
            {
                lock (gate)
                {
                    if (!TryCompleteInboundFallbackControlPlaneSendLocked(context, request, controlPlaneToken, result))
                    {
                        LogFileTransferControlPlaneDeliveryResult(request, result);
                        return (false, null);
                    }
                }

                LogFileTransferControlPlaneDeliveryResult(request, result);
            }

            MaybeObserveInboundPostTunaFallbackControlPlanePressure(context, request);
            return (true, result);
        }
        catch (Exception ex) when (request.Kind == FileTransferControlPlaneKind.ReceiverState &&
                                   TryDeferInboundV6ControlSendFailureForRecovery(
                                       context,
                                       "receiver_state",
                                       request.Reason,
                                       ex,
                                       requireRecoverableFailure: false))
        {
            return (false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException &&
                                   IsRegularNknV4CriticalReceiverStateControlPlaneRequest(request))
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_regular_v4_receiver_state_control_plane_delivery_failed; direction=inbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; route={FormatProtocolLogValue(request.RouteToken ?? "(none)")}; protocol_version={request.ProtocolVersion}; reason={FormatProtocolLogValue(request.Reason)}; epoch={(request.Frame is FileTransferStateFrameV4 state ? state.Epoch : 0)}; missing_range_count={(request.Frame is FileTransferStateFrameV4 stateFrame ? stateFrame.MissingRanges.Count : 0)}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");
            return (false, null);
        }
    }

    private void MaybeObserveInboundPostTunaFallbackControlPlanePressure(
        InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request)
    {
        if (!string.Equals(request.RouteToken, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal) ||
            request.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            request.TransferLegGeneration <= 0 ||
            request.Kind is not (FileTransferControlPlaneKind.ReceiverState or
                FileTransferControlPlaneKind.FrontierRequest or
                FileTransferControlPlaneKind.FallbackCheckpointProof))
        {
            return;
        }

        IFileTransferPostTunaFallbackControlPlanePressureObserver? observer;
        var clearProgressPressure = IsInboundPostTunaFallbackProgressOnlyControlPlane(request);
        var shouldObservePressure = true;
        lock (gate)
        {
            if (!TryValidateInboundFallbackControlPlaneSendLocked(
                    context,
                    request,
                    out _,
                    out var currentLeg))
            {
                return;
            }

            observer = transport as IFileTransferPostTunaFallbackControlPlanePressureObserver;
            if (clearProgressPressure)
            {
                if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                        context,
                        FileTransferCoordinatorActionKind.FallbackControlPlanePressure,
                        "fallback_control_plane_pressure_clear",
                        request.Reason,
                        currentLeg,
                        request.CheckpointRequestId,
                        request.TransportEpoch,
                        request.BridgeRecoveryGeneration,
                        currentLeg?.CheckpointGeneration,
                        null,
                        null,
                        null,
                        request.Kind,
                        request.Reason,
                        null,
                        null,
                        null,
                        out _))
                {
                    return;
                }

                ClearInboundPostTunaFallbackPressureDiagnosticLocked(context);
            }
            else
            {
                if (!TryApplyInboundFallbackControlPlanePressureLocked(context, request))
                {
                    return;
                }

                shouldObservePressure = ShouldObserveInboundPostTunaFallbackControlPlanePressureLocked(
                    context,
                    request,
                    DateTimeOffset.UtcNow);
            }
        }

        if (clearProgressPressure)
        {
            observer?.ClearPostTunaFallbackControlPlanePressure(
                context.TransferId,
                "receiver_state_progress");
            return;
        }

        if (!shouldObservePressure)
        {
            return;
        }

        observer?.ObservePostTunaFallbackControlPlanePressure(
            new FileTransferPostTunaFallbackControlPlanePressure(
                context.SessionId,
                context.TransferId,
                request.RouteToken ?? FileTransferRouteResolver.PostTunaFallbackV6Token,
                request.ProtocolVersion,
                request.LiveRouteEpoch,
                request.TransferLegGeneration,
                request.BridgeRecoveryGeneration,
                request.TransportEpoch,
                request.CheckpointRequestId,
                FormatFileTransferControlPlaneKind(request.Kind),
                request.Reason));
    }

    private bool TryApplyInboundFallbackControlPlanePressureLocked(
        InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request)
    {
        if (!IsFallbackControlPlaneGenerationBound(request))
        {
            return false;
        }

        if (!TryValidateInboundFallbackControlPlaneSendLocked(context, request, out var reason, out var currentLeg))
        {
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(context, request, reason);
            return false;
        }

        var decision = FileTransferCoordinator.AuthorizeAction(
            CreateCoordinatorStateLocked(context),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.FallbackControlPlanePressure,
                "fallback_control_plane_pressure",
                request.Reason,
                RuntimeOwner: context.CurrentRuntimeOwner,
                Route: FileTransferRoute.PostTunaFallbackV6,
                ProtocolVersion: request.ProtocolVersion,
                LiveRouteEpoch: request.LiveRouteEpoch,
                LegGeneration: request.TransferLegGeneration,
                TransportEpochId: request.TransportEpoch,
                BridgeRecoveryGeneration: request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason,
                CheckpointGeneration: currentLeg?.CheckpointGeneration));
        if (!decision.Accepted || decision.FallbackControlPlanePressureToken is not { } pressureToken)
        {
            LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(
                context,
                request,
                decision.Accepted ? "fallback_control_plane_pressure_token_missing" : decision.RejectionReason);
            return false;
        }

        var applyDecision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlanePressureUpdated,
                context.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                request.Reason,
                FileTransferLegState.Active,
                CanSendData: true,
                currentLeg?.ProvenCommittedChunkIndex ?? 0,
                currentLeg?.ProvenHighestObservedChunkIndex ?? -1,
                request.TransportEpoch,
                request.BridgeRecoveryGeneration,
                CheckpointRequestId: request.CheckpointRequestId,
                CheckpointGeneration: currentLeg?.CheckpointGeneration ?? 0,
                FallbackControlPlanePressureToken: pressureToken,
                ControlPlaneKind: request.Kind,
                ControlPlanePurpose: request.Reason),
            CreateCoordinatorStateLocked(context));
        if (!string.IsNullOrWhiteSpace(applyDecision.MutationRejectedReason))
        {
            LogFileTransferCoordinatorLegMutationRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.FallbackControlPlanePressure,
                FormatFileTransferControlPlaneKind(request.Kind),
                applyDecision,
                currentLeg,
                request.CheckpointRequestId,
                request.TransportEpoch,
                request.Reason);
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, applyDecision);
        return true;
    }

    private static bool IsInboundPostTunaFallbackProgressOnlyControlPlane(
        FileTransferControlPlaneDeliveryRequest request)
        => request.Kind is (FileTransferControlPlaneKind.ReceiverState or FileTransferControlPlaneKind.FrontierRequest) &&
           string.Equals(request.Reason, "chunk_batch_committed", StringComparison.Ordinal);

    private static void ClearInboundPostTunaFallbackPressureDiagnosticLocked(InboundTransferContext context)
    {
        context.PostTunaFallbackControlPlanePressureTuple = null;
        context.PostTunaFallbackControlPlanePressureDiagnosticUtc = null;
        context.PostTunaFallbackControlPlanePressureSuppressedCount = 0;
    }

    private static bool ShouldObserveInboundPostTunaFallbackControlPlanePressureLocked(
        InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        DateTimeOffset now)
    {
        var leg = context.CurrentTransferLeg;
        var (committed, highest, missingRangeCount) = request.Frame switch
        {
            FileTransferReceiverStateFrameV6 state => (
                state.ContiguousCommittedChunkIndex,
                state.DurableReceivedHighestChunkIndex,
                state.MissingRanges.Count),
            FileTransferFrontierRequestFrameV6 frontier => (
                context.NextChunkIndex,
                context.PullHighestReceivedChunkIndex,
                frontier.MissingRanges.Count),
            _ => (
                context.NextChunkIndex,
                context.PullHighestReceivedChunkIndex,
                0),
        };
        var tuple = BuildFallbackChurnTuple(
            context.SessionId,
            context.TransferId,
            request.RouteToken ?? FileTransferRouteResolver.PostTunaFallbackV6Token,
            request.ProtocolVersion,
            request.LiveRouteEpoch,
            request.TransferLegGeneration,
            request.TransportEpoch,
            committed,
            highest,
            missingRangeCount > 0 ? 1 : 0,
            missingRangeCount,
            request.CheckpointRequestId ?? leg?.CheckpointRequestId);
        if (string.Equals(context.PostTunaFallbackControlPlanePressureTuple, tuple, StringComparison.Ordinal) &&
            context.PostTunaFallbackControlPlanePressureDiagnosticUtc is { } lastObserved &&
            now - lastObserved < TimeSpan.FromMilliseconds(V6ReceiverStateDeferredDiagnosticCoalesceMs))
        {
            context.PostTunaFallbackControlPlanePressureSuppressedCount++;
            return false;
        }

        context.PostTunaFallbackControlPlanePressureTuple = tuple;
        context.PostTunaFallbackControlPlanePressureDiagnosticUtc = now;
        context.PostTunaFallbackControlPlanePressureSuppressedCount = 0;
        return true;
    }

    private void ClearPostTunaFallbackControlPlanePressure(string transferId, string reason)
    {
        if (transport is IFileTransferPostTunaFallbackControlPlanePressureObserver observer)
        {
            observer.ClearPostTunaFallbackControlPlanePressure(transferId, reason);
        }
    }

    private bool TryValidateOutboundFallbackControlPlaneSendLocked(
        OutboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        out string reason,
        out FileTransferLeg? currentLeg)
    {
        currentLeg = context.CurrentTransferLeg;
        if (!IsFallbackControlPlaneGenerationBound(request))
        {
            reason = "not_fallback_generation_bound";
            return true;
        }

        if (!ReferenceEquals(outboundTransfer, context))
        {
            reason = "transfer_context_superseded";
            return false;
        }

        return TryValidateFallbackControlPlaneSendLocked(
            context.SessionId,
            context.TransferId,
            context.IsTerminal,
            context.RouteSelection,
            context.NegotiatedDataProtocolVersion,
            context.CurrentLiveRouteEpoch,
            currentLeg,
            request,
            out reason);
    }

    private bool TryValidateInboundFallbackControlPlaneSendLocked(
        InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        out string reason,
        out FileTransferLeg? currentLeg)
    {
        currentLeg = context.CurrentTransferLeg;
        if (!IsFallbackControlPlaneGenerationBound(request))
        {
            reason = "not_fallback_generation_bound";
            return true;
        }

        if (!ReferenceEquals(inboundTransfer, context))
        {
            reason = "transfer_context_superseded";
            return false;
        }

        return TryValidateFallbackControlPlaneSendLocked(
            context.SessionId,
            context.TransferId,
            context.IsTerminal,
            context.RouteSelection,
            context.NegotiatedDataProtocolVersion,
            context.CurrentLiveRouteEpoch,
            currentLeg,
            request,
            out reason);
    }

    private static bool TryValidateFallbackControlPlaneSendLocked(
        string sessionId,
        string transferId,
        bool isTerminal,
        FileTransferRouteSelection routeSelection,
        int protocolVersion,
        LiveRouteEpoch? liveRouteEpoch,
        FileTransferLeg? currentLeg,
        FileTransferControlPlaneDeliveryRequest request,
        out string reason)
    {
        if (isTerminal)
        {
            reason = "transfer_terminal";
            return false;
        }

        if (!string.Equals(request.Frame.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(request.Frame.TransferId, transferId, StringComparison.Ordinal))
        {
            reason = "session_or_transfer_mismatch";
            return false;
        }

        if (routeSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            protocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !string.Equals(request.RouteToken, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal) ||
            request.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
        {
            reason = "route_or_protocol_mismatch";
            return false;
        }

        if (!IsCurrentPostTunaFallbackLeg(currentLeg))
        {
            reason = "fallback_leg_not_current";
            return false;
        }

        var leg = currentLeg!;
        if (request.TransferLegGeneration != leg.Generation)
        {
            reason = "leg_generation_mismatch";
            return false;
        }

        if (request.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration)
        {
            reason = "bridge_recovery_generation_mismatch";
            return false;
        }

        var expectedLiveRouteEpoch = liveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId;
        if (request.LiveRouteEpoch != expectedLiveRouteEpoch || request.LiveRouteEpoch != leg.LiveRouteEpochId)
        {
            reason = "live_route_epoch_mismatch";
            return false;
        }

        if (request.TransportEpoch != leg.TransportEpochId)
        {
            reason = "transport_epoch_mismatch";
            return false;
        }

        if (RequiresFallbackCheckpointRequestId(request.Kind))
        {
            if (string.IsNullOrWhiteSpace(request.CheckpointRequestId) ||
                string.Equals(request.CheckpointRequestId, "(none)", StringComparison.Ordinal))
            {
                reason = "checkpoint_request_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(leg.CheckpointRequestId))
            {
                reason = "checkpoint_not_pending";
                return false;
            }

            if (!string.Equals(leg.CheckpointRequestId, request.CheckpointRequestId, StringComparison.Ordinal))
            {
                reason = "checkpoint_request_mismatch";
                return false;
            }
        }
        reason = "current";
        return true;
    }

    private static bool IsFallbackControlPlaneGenerationBound(FileTransferControlPlaneDeliveryRequest request)
        => string.Equals(request.RouteToken, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal) &&
           request.Kind is FileTransferControlPlaneKind.FallbackCheckpointRequest
               or FileTransferControlPlaneKind.FallbackCheckpointProof
               or FileTransferControlPlaneKind.ReceiverState
               or FileTransferControlPlaneKind.FrontierRequest
               or FileTransferControlPlaneKind.FallbackTailYield;

    private static bool RequiresFallbackCheckpointRequestId(FileTransferControlPlaneKind kind)
        => kind is FileTransferControlPlaneKind.FallbackCheckpointRequest
            or FileTransferControlPlaneKind.FallbackCheckpointProof;

    private static void LogFileTransferControlPlaneSendRetiredStaleGeneration(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferControlPlaneDeliveryRequest request,
        FileTransferLeg? currentLeg,
        string reason,
        HashSet<string> suppressionKeys)
    {
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{direction}|{transferId}|{sessionId}|{FormatFileTransferControlPlaneKind(request.Kind)}|{request.TransferLegGeneration}|{request.BridgeRecoveryGeneration}|{request.TransportEpoch}|{request.CheckpointRequestId ?? "(none)"}|{reason}");
        if (!suppressionKeys.Add(key))
        {
            return;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_control_plane_send_retired_stale_generation; direction={direction.ToString().ToLowerInvariant()}; kind={FormatFileTransferControlPlaneKind(request.Kind)}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; route={FormatProtocolLogValue(request.RouteToken ?? "(none)")}; protocol_version={request.ProtocolVersion}; live_route_epoch={request.LiveRouteEpoch}; leg_generation={request.TransferLegGeneration}; current_leg_generation={currentLeg?.Generation ?? 0}; bridge_recovery_generation={request.BridgeRecoveryGeneration}; current_bridge_recovery_generation={currentLeg?.BridgeRecoveryGeneration ?? 0}; transport_epoch={request.TransportEpoch}; current_transport_epoch={currentLeg?.TransportEpochId ?? 0}; checkpoint_request_id={FormatProtocolLogValue(request.CheckpointRequestId ?? "(none)")}; current_checkpoint_request_id={FormatProtocolLogValue(currentLeg?.CheckpointRequestId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}; frame_type={FormatProtocolLogValue(request.Frame.Type)}");

        if (RequiresFallbackCheckpointRequestId(request.Kind))
        {
            LogFileTransferFallbackCheckpointExchangeRetired(
                direction,
                transferId,
                sessionId,
                currentLeg,
                request.CheckpointRequestId,
                currentLeg?.Generation ?? 0,
                currentLeg?.CheckpointRequestId,
                reason);
        }
    }

    private static void LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(
        OutboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        string reason)
    {
        var attemptedOwner = RuntimeOwnerTokenForControlPlaneRequest(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.TerminalGeneration,
            request);
        RejectStaleRuntimeOwnerUpdateLocked(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.CurrentRuntimeOwner,
            attemptedOwner,
            "fallback_control_plane_send",
            FormatFileTransferControlPlaneKind(request.Kind),
            reason);
    }

    private static void LogRuntimeOwnerRejectedForFallbackControlPlaneLocked(
        InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request,
        string reason)
    {
        var attemptedOwner = RuntimeOwnerTokenForControlPlaneRequest(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.TerminalGeneration,
            request);
        RejectStaleRuntimeOwnerUpdateLocked(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.CurrentRuntimeOwner,
            attemptedOwner,
            "fallback_control_plane_send",
            FormatFileTransferControlPlaneKind(request.Kind),
            reason);
    }

    private static RuntimeOwnerToken RuntimeOwnerTokenForControlPlaneRequest(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        int terminalGeneration,
        FileTransferControlPlaneDeliveryRequest request)
    {
        var route = request.RouteToken switch
        {
            FileTransferRouteResolver.RegularNknV4FastToken => FileTransferRoute.RegularNknV4Fast,
            FileTransferRouteResolver.FileTunaV4Token => FileTransferRoute.FileTunaV4,
            FileTransferRouteResolver.PostTunaFallbackV6Token => FileTransferRoute.PostTunaFallbackV6,
            _ => FileTransferRoute.RegularNknV4Fast,
        };
        var routeToken = string.IsNullOrWhiteSpace(request.RouteToken)
            ? "(none)"
            : request.RouteToken!;
        return new RuntimeOwnerToken(
            sessionId,
            transferId,
            direction,
            route,
            routeToken,
            request.ProtocolVersion,
            request.LiveRouteEpoch,
            Math.Max(0, request.TransferLegGeneration),
            terminalGeneration,
            route switch
            {
                FileTransferRoute.RegularNknV4Fast => "regular_v4",
                FileTransferRoute.FileTunaV4 => "file_tuna_v4",
                FileTransferRoute.PostTunaFallbackV6 => "post_tuna_fallback_v6",
                _ => "unknown",
            },
            "fallback_control_plane_request");
    }

    private FileTransferControlPlaneDeliveryRequest CreateOutboundControlPlaneDeliveryRequestLocked(
        OutboundTransferContext context,
        FileTransferControlPlaneKind kind,
        FileTransferDataFrame frame,
        string reason,
        bool ignoreCallerCancellation = false,
        int peerCopyAttempts = 1)
    {
        var leg = context.CurrentTransferLeg;
        var durableFallbackCheckpointExchange = RequiresFallbackCheckpointRequestId(kind);
        var request = new FileTransferControlPlaneDeliveryRequest(kind, frame, reason)
        {
            Direction = FileTransferDirection.Outbound,
            RouteToken = context.RouteSelection.TelemetryToken,
            ProtocolVersion = context.NegotiatedDataProtocolVersion,
            LiveRouteEpoch = leg?.LiveRouteEpochId ?? context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            TransferLegGeneration = leg?.Generation ?? 0,
            BridgeRecoveryGeneration = leg?.BridgeRecoveryGeneration ?? 0,
            TransportEpoch = frame switch
            {
                FileTransferFrontierRequestFrameV6 frontier => frontier.TransportEpoch,
                FileTransferReceiverStateFrameV6 state => state.TransportEpoch,
                FileTransferFallbackTailYieldFrameV6 yield => yield.TransportEpoch,
                FileTransferCancelFrameV6 cancel => cancel.TransportEpoch,
                _ => leg?.TransportEpochId ?? 0,
            },
            CheckpointRequestId = ResolveFallbackControlPlaneCheckpointRequestId(kind, frame, leg),
            PeerVisibleRequired = true,
            IgnoreCallerCancellation = ignoreCallerCancellation || durableFallbackCheckpointExchange,
            PeerCopyAttempts = durableFallbackCheckpointExchange
                ? Math.Max(2, peerCopyAttempts)
                : peerCopyAttempts,
            CopyTimeout = durableFallbackCheckpointExchange
                ? TimeSpan.FromMilliseconds(V6PostTunaFallbackControlPlanePeerCopyTimeoutMs)
                : null,
        };
        return request with
        {
            PreTransportDeliveryValidator = () =>
            {
                lock (gate)
                {
                    return TryValidateOutboundFallbackControlPlaneSendLocked(context, request, out _, out _);
                }
            },
        };
    }

    private FileTransferControlPlaneDeliveryRequest CreateInboundControlPlaneDeliveryRequestLocked(
        InboundTransferContext context,
        FileTransferControlPlaneKind kind,
        FileTransferDataFrame frame,
        string reason,
        bool ignoreCallerCancellation = false,
        int peerCopyAttempts = 1)
    {
        var leg = context.CurrentTransferLeg;
        var durableFallbackCheckpointExchange = RequiresFallbackCheckpointRequestId(kind);
        var request = new FileTransferControlPlaneDeliveryRequest(kind, frame, reason)
        {
            Direction = FileTransferDirection.Inbound,
            RouteToken = context.RouteSelection.TelemetryToken,
            ProtocolVersion = context.NegotiatedDataProtocolVersion,
            LiveRouteEpoch = leg?.LiveRouteEpochId ?? context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            TransferLegGeneration = leg?.Generation ?? 0,
            BridgeRecoveryGeneration = leg?.BridgeRecoveryGeneration ?? 0,
            TransportEpoch = frame switch
            {
                FileTransferFrontierRequestFrameV6 frontier => frontier.TransportEpoch,
                FileTransferReceiverStateFrameV6 state => state.TransportEpoch,
                FileTransferFallbackTailYieldFrameV6 yield => yield.TransportEpoch,
                FileTransferCancelFrameV6 cancel => cancel.TransportEpoch,
                _ => leg?.TransportEpochId ?? context.V6ReceiverTransportEpoch,
            },
            CheckpointRequestId = ResolveFallbackControlPlaneCheckpointRequestId(kind, frame, leg),
            PeerVisibleRequired = true,
            IgnoreCallerCancellation = ignoreCallerCancellation || durableFallbackCheckpointExchange,
            PeerCopyAttempts = durableFallbackCheckpointExchange
                ? Math.Max(2, peerCopyAttempts)
                : peerCopyAttempts,
            CopyTimeout = durableFallbackCheckpointExchange
                ? TimeSpan.FromMilliseconds(V6PostTunaFallbackControlPlanePeerCopyTimeoutMs)
                : null,
        };
        return request with
        {
            PreTransportDeliveryValidator = () =>
            {
                lock (gate)
                {
                    return TryValidateInboundFallbackControlPlaneSendLocked(context, request, out _, out _);
                }
            },
        };
    }

    private static bool ShouldUseFallbackControlPlaneForOutboundFrame(
        OutboundTransferContext context,
        FileTransferDataFrame frame)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
           frame is FileTransferFrontierRequestFrameV6;

    private static bool ShouldUseFallbackControlPlaneForInboundFrame(
        InboundTransferContext context,
        FileTransferDataFrame frame)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
           frame is FileTransferReceiverStateFrameV6 or FileTransferFrontierRequestFrameV6;

    private static bool ShouldUseRegularV4ControlPlaneForInboundFrame(
        InboundTransferContext context,
        FileTransferDataFrame frame,
        string reason)
        => string.Equals(reason, "frontier_stall_repair_due", StringComparison.Ordinal) &&
           context.RouteRuntime.UsesRegularNknV4FastRuntime &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           !context.PullPostTunaRecoveryActive &&
           context.V6TransportHandoff is null &&
           frame is FileTransferStateFrameV4 state &&
           state.MissingRanges.Count > 0;

    private static bool IsRegularNknV4CriticalReceiverStateControlPlaneRequest(
        FileTransferControlPlaneDeliveryRequest request)
        => request.Kind == FileTransferControlPlaneKind.ReceiverState &&
           string.Equals(request.RouteToken, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal) &&
           request.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           string.Equals(request.Reason, "frontier_stall_repair_due", StringComparison.Ordinal) &&
           request.Frame is FileTransferStateFrameV4 state &&
           state.MissingRanges.Count > 0;

    private static string? ResolveFallbackControlPlaneCheckpointRequestId(
        FileTransferControlPlaneKind kind,
        FileTransferDataFrame frame,
        FileTransferLeg? leg)
        => kind switch
        {
            FileTransferControlPlaneKind.FallbackCheckpointRequest =>
                frame is FileTransferFrontierRequestFrameV6 frontier
                    ? frontier.RepairRequestId
                    : leg?.CheckpointRequestId,
            FileTransferControlPlaneKind.FallbackCheckpointProof =>
                frame is FileTransferReceiverStateFrameV6 state
                    ? state.RepairRequestId
                    : leg?.CheckpointRequestId,
            _ => null,
        };

    private static string FormatFileTransferControlPlaneKind(FileTransferControlPlaneKind kind)
        => kind switch
        {
            FileTransferControlPlaneKind.RouteEpoch => "route_epoch",
            FileTransferControlPlaneKind.FallbackCheckpointRequest => "fallback_checkpoint_request",
            FileTransferControlPlaneKind.FallbackCheckpointProof => "fallback_checkpoint_proof",
            FileTransferControlPlaneKind.ReceiverState => "receiver_state",
            FileTransferControlPlaneKind.FrontierRequest => "frontier_request",
            FileTransferControlPlaneKind.RuntimeUnlockOffer => "runtime_unlock_offer",
            FileTransferControlPlaneKind.RuntimeUnlockAnswer => "runtime_unlock_answer",
            FileTransferControlPlaneKind.RuntimeUnlockAnswerAck => "runtime_unlock_answer_ack",
            FileTransferControlPlaneKind.TransferCancel => "transfer_cancel",
            FileTransferControlPlaneKind.SessionEnd => "session_end",
            FileTransferControlPlaneKind.LivenessProof => "liveness_proof",
            FileTransferControlPlaneKind.FallbackTailYield => "fallback_tail_yield",
            _ => "unknown",
        };

    private static string FormatFileTransferDirection(FileTransferDirection? direction)
        => direction is { } value
            ? value.ToString().ToLowerInvariant()
            : "(none)";

    private static void LogFileTransferFallbackLegAuthoritySendBlocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_leg_authority_send_blocked; direction=outbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; reason={FormatProtocolLogValue(reason)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(leg.CheckpointRequestId ?? "(none)")}; proven_committed_chunk={leg.ProvenCommittedChunkIndex}; state={FormatFileTransferLegState(leg.State)}; can_send_data={(leg.CanSendData ? 1 : 0)}");

    private static void LogFileTransferFallbackLegAuthorityStarted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        string? requestId,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_leg_authority_started; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(none)")}; authority_reason={FormatProtocolLogValue(reason)}; state={FormatFileTransferLegState(leg.State)}; can_send_data={(leg.CanSendData ? 1 : 0)}");

    private static void LogFileTransferFallbackLegAuthorityCheckpointAccepted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg leg,
        string? requestId,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(none)")}; proven_committed_chunk={leg.ProvenCommittedChunkIndex}; proven_highest_observed_chunk={leg.ProvenHighestObservedChunkIndex}; reason={FormatProtocolLogValue(reason)}");

    private static bool MarkOutboundFallbackCheckpointRequestedLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason)
    {
        if (context.CurrentTransferLeg is null &&
            context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
            context.PullPostTunaRecoveryActive)
        {
            var generation = Math.Max(1, context.PullTransportRebindGeneration);
            var transportEpoch = ResolveOutboundFallbackCurrentTransportEpochLocked(context, generation);
            var leg = StartOutboundTransferLegLocked(
                context,
                context.RouteSelection,
                reason,
                FileTransferLegState.CheckpointPending,
                canSendData: false,
                freezeCurrent: false,
                transportEpochOverride: transportEpoch,
                bridgeRecoveryGenerationOverride: generation);
            if (leg is null)
            {
                return false;
            }

            context.PullPostTunaRecoveryGeneration = Math.Max(context.PullPostTunaRecoveryGeneration, generation);
            context.PullPostTunaRecoveryFrontierChunkIndex = Math.Clamp(
                context.RemoteNextExpectedChunkIndex,
                0,
                Math.Max(0, context.ChunkCount - 1));
            context.PullPostTunaRecoveryStartedUtc ??= DateTimeOffset.UtcNow;
        }

        if (!TryBeginOutboundFallbackCheckpointExchangeLocked(context, request, reason))
        {
            return false;
        }

        var currentLeg = context.CurrentTransferLeg!;
        var requestedChunkIndex = request.MissingRanges.Count > 0 ? request.MissingRanges[0].StartChunkIndex : -1;
        LogFileTransferFallbackLegAuthorityStarted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            request.RepairRequestId,
            reason);
        LogFileTransferFallbackCheckpointRequested(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            request.RepairRequestId ?? "(none)",
            reason,
            requestedChunkIndex);
        return true;
    }

    private static bool MarkInboundFallbackCheckpointRequestedLocked(
        InboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason)
    {
        if (context.CurrentTransferLeg is null &&
            context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            return true;
        }

        StartInboundFallbackCheckpointRecoveryLegIfNeededLocked(context, request, reason);

        if (!TryBeginInboundFallbackCheckpointExchangeLocked(context, request, reason))
        {
            return false;
        }

        var currentLeg = context.CurrentTransferLeg!;
        var requestedChunkIndex = request.MissingRanges.Count > 0 ? request.MissingRanges[0].StartChunkIndex : -1;
        LogFileTransferFallbackLegAuthorityStarted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            request.RepairRequestId,
            reason);
        LogFileTransferFallbackCheckpointRequested(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            request.RepairRequestId ?? "(none)",
            reason,
            requestedChunkIndex);
        return true;
    }

    private static void StartInboundFallbackCheckpointRecoveryLegIfNeededLocked(
        InboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason)
    {
        var currentLeg = context.CurrentTransferLeg;
        if (TrySuppressPostYieldFallbackRecoveryRestartLocked(
                context,
                "fallback_checkpoint_recovery_leg",
                reason))
        {
            return;
        }

        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            !IsCurrentPostTunaFallbackLeg(currentLeg) ||
            string.IsNullOrWhiteSpace(request.RepairRequestId) ||
            request.TransportEpoch <= 0 ||
            !string.IsNullOrWhiteSpace(currentLeg!.CheckpointRequestId) ||
            IsRetiredFallbackCheckpointRequest(
                currentLeg,
                request.TransportEpoch,
                request.RepairRequestId,
                context.RetiredFallbackCheckpointRequestKeys))
        {
            return;
        }

        if (currentLeg.TransportEpochId > 0 &&
            request.TransportEpoch < currentLeg.TransportEpochId)
        {
            return;
        }

        var requestAdvancesTransportEpoch = currentLeg.TransportEpochId > 0 &&
                                           request.TransportEpoch > currentLeg.TransportEpochId;
        if (!requestAdvancesTransportEpoch &&
            !currentLeg.CanSendData &&
            currentLeg.State is FileTransferLegState.CheckpointPending or FileTransferLegState.BridgeRestartPending)
        {
            return;
        }

        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.StartTransferLeg,
                reason,
                "fallback_checkpoint_recovery_leg_start",
                leg: currentLeg,
                checkpointRequestId: null,
                transportEpochId: currentLeg.TransportEpochId,
                bridgeRecoveryGeneration: currentLeg.BridgeRecoveryGeneration,
                checkpointGeneration: currentLeg.CheckpointGeneration,
                fallbackControlPlaneToken: null,
                fallbackCheckpointLease: null,
                fallbackControlPlanePressureToken: null,
                controlPlaneKind: null,
                controlPlanePurpose: null,
                controlPlanePeerVisible: null,
                controlPlaneAcceptedAny: null,
                fallbackTailYieldKind: null,
                out _))
        {
            return;
        }

        var previousLegGeneration = currentLeg.Generation;
        var previousTransportEpoch = currentLeg.TransportEpochId;
        var frontier = Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount - 1));
        var recoveryGeneration = Math.Max(
            context.PullPostTunaRecoveryGeneration + 1,
            Math.Max(
                1,
                Math.Max(
                    context.PullTransportRebindGeneration,
                    request.TransportEpoch > int.MaxValue ? int.MaxValue : (int)request.TransportEpoch)));

        var leg = StartInboundTransferLegLocked(
            context,
            context.RouteSelection,
            reason,
            FileTransferLegState.CheckpointPending,
            canSendData: false,
            transportEpochOverride: request.TransportEpoch,
            bridgeRecoveryGenerationOverride: recoveryGeneration);
        if (leg is null)
        {
            return;
        }

        context.V6ReceiverTransportEpoch = request.TransportEpoch;
        ResetInboundPostTunaFallbackLegOwnedStateLocked(context, reason, recoveryGeneration, frontier);
        context.PullPostTunaRecoveryActive = true;
        context.PullPostTunaRecoveryGeneration = recoveryGeneration;
        context.PullPostTunaRecoveryFrontierChunkIndex = frontier;
        context.PullPostTunaRecoveryStartedUtc = DateTimeOffset.UtcNow;

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_recovery_leg_started; direction=inbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; previous_leg_generation={previousLegGeneration}; leg_generation={leg.Generation}; previous_transport_epoch={previousTransportEpoch}; transport_epoch={leg.TransportEpochId}; bridge_recovery_generation={leg.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(request.RepairRequestId)}; reason={FormatProtocolLogValue(reason)}");
    }

    private static bool TryBeginOutboundFallbackCheckpointExchangeLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string source)
    {
        var leg = context.CurrentTransferLeg;
        if (!TryValidateFallbackCheckpointBeginLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                leg,
                request,
                source,
                context.RetiredFallbackCheckpointRequestKeys,
                context.FallbackStaleProofSuppressionKeys,
                out var idempotent))
        {
            return false;
        }

        if (idempotent && context.CurrentFallbackCheckpointLease is not null)
        {
            return true;
        }

        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackCheckpointBegin,
                source,
                "fallback_checkpoint_begin",
                leg,
                request.RepairRequestId,
                request.TransportEpoch,
                leg?.BridgeRecoveryGeneration,
                leg?.CheckpointGeneration,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                out _))
        {
            return false;
        }

        if (!TryApplyOutboundCoordinatorLegMutationLocked(
                context,
                new FileTransferCoordinatorEvent(
                    FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                    context.RouteSelection,
                    FileTransferTransportHandoffKind.None,
                    FileTransferTransportKind.Unknown,
                    source,
                    FileTransferLegState.CheckpointPending,
                    CanSendData: false,
                    Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                    Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    request.TransportEpoch,
                    leg!.BridgeRecoveryGeneration,
                    CheckpointRequestId: request.RepairRequestId,
                    CheckpointPriority: request.Priority),
                FileTransferCoordinatorActionKind.FallbackCheckpointBegin,
                "fallback_checkpoint_begin",
                leg,
                request.RepairRequestId,
                request.TransportEpoch,
                out _))
        {
            return false;
        }

        ClearOutboundFallbackCheckpointRepairAuthorityLocked(context);
        return true;
    }

    private static bool TryBeginInboundFallbackCheckpointExchangeLocked(
        InboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string source)
    {
        var leg = context.CurrentTransferLeg;
        if (leg is null &&
            context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            return true;
        }

        if (!TryValidateFallbackCheckpointBeginLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                leg,
                request,
                source,
                context.RetiredFallbackCheckpointRequestKeys,
                context.FallbackStaleProofSuppressionKeys,
                out var idempotent))
        {
            return false;
        }

        if (idempotent && context.CurrentFallbackCheckpointLease is not null)
        {
            return true;
        }

        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackCheckpointBegin,
                source,
                "fallback_checkpoint_begin",
                leg,
                request.RepairRequestId,
                request.TransportEpoch,
                leg?.BridgeRecoveryGeneration,
                leg?.CheckpointGeneration,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                out _))
        {
            return false;
        }

        if (!TryApplyInboundCoordinatorLegMutationLocked(
                context,
                new FileTransferCoordinatorEvent(
                    FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                    context.RouteSelection,
                    FileTransferTransportHandoffKind.None,
                    FileTransferTransportKind.Unknown,
                    source,
                    FileTransferLegState.CheckpointPending,
                    CanSendData: false,
                    Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                    Math.Max(-1, context.PullHighestReceivedChunkIndex),
                    request.TransportEpoch,
                    leg!.BridgeRecoveryGeneration,
                    CheckpointRequestId: request.RepairRequestId,
                    CheckpointPriority: request.Priority),
                FileTransferCoordinatorActionKind.FallbackCheckpointBegin,
                "fallback_checkpoint_begin",
                leg,
                request.RepairRequestId,
                request.TransportEpoch,
                out _))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateFallbackCheckpointBeginLocked(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        FileTransferFrontierRequestFrameV6 request,
        string source,
        HashSet<string> retiredCheckpointRequestKeys,
        HashSet<string> staleProofSuppressionKeys,
        out bool idempotent)
    {
        idempotent = false;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            LogFileTransferFallbackCheckpointOwnerViolation(
                direction,
                transferId,
                sessionId,
                leg,
                request,
                "fallback_leg_not_current",
                source);
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RepairRequestId))
        {
            LogFileTransferFallbackCheckpointOwnerViolation(
                direction,
                transferId,
                sessionId,
                leg,
                request,
                "checkpoint_request_missing",
                source);
            return false;
        }

        if (request.TransportEpoch <= 0)
        {
            LogFileTransferFallbackCheckpointOwnerViolation(
                direction,
                transferId,
                sessionId,
                leg,
                request,
                "transport_epoch_missing",
                source);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(leg!.CheckpointRequestId))
        {
            if (string.Equals(leg.CheckpointRequestId, request.RepairRequestId, StringComparison.Ordinal))
            {
                idempotent = true;
                return true;
            }

            LogFileTransferFallbackCheckpointOwnerViolation(
                direction,
                transferId,
                sessionId,
                leg,
                request,
                "checkpoint_owner_mismatch",
                source);
            return false;
        }

        if (IsRetiredFallbackCheckpointRequest(leg, request.TransportEpoch, request.RepairRequestId, retiredCheckpointRequestKeys))
        {
            LogFileTransferFallbackCheckpointExchangeRetired(
                direction,
                transferId,
                sessionId,
                leg,
                request.RepairRequestId,
                leg.Generation,
                leg.CheckpointRequestId,
                "retired_checkpoint_request_replay");
            return false;
        }

        if (leg.TransportEpochId > 0 &&
            request.TransportEpoch != leg.TransportEpochId)
        {
            LogFileTransferFallbackStaleProofIgnored(
                direction,
                transferId,
                sessionId,
                leg,
                "checkpoint_not_pending",
                request.TransportEpoch,
                request.RepairRequestId,
                staleProofSuppressionKeys);
            return false;
        }

        return true;
    }

    private static bool IsRetiredFallbackCheckpointRequest(
        FileTransferLeg leg,
        long transportEpoch,
        string? requestId,
        HashSet<string> retiredCheckpointRequestKeys)
        => retiredCheckpointRequestKeys.Contains(CreateFallbackCheckpointRequestOwnerKey(leg, transportEpoch, requestId));

    private static void RememberRetiredFallbackCheckpointRequest(
        HashSet<string> retiredCheckpointRequestKeys,
        FileTransferLeg leg,
        long transportEpoch,
        string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) ||
            transportEpoch <= 0)
        {
            return;
        }

        retiredCheckpointRequestKeys.Add(CreateFallbackCheckpointRequestOwnerKey(leg, transportEpoch, requestId));
    }

    private static string CreateFallbackCheckpointRequestOwnerKey(
        FileTransferLeg leg,
        long transportEpoch,
        string? requestId)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{leg.Generation}|{leg.BridgeRecoveryGeneration}|{leg.LiveRouteEpochId}|{transportEpoch}|{requestId ?? "(none)"}");

    private static bool TryRetireOutboundFallbackCheckpointRequestLocked(
        OutboundTransferContext context,
        string? requestId,
        string reason)
        => RetireOutboundFallbackCheckpointExchangeLocked(context, requestId, reason);

    private static bool RetireOutboundFallbackCheckpointExchangeLocked(
        OutboundTransferContext context,
        string? requestId,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg) ||
            string.IsNullOrWhiteSpace(leg!.CheckpointRequestId))
        {
            ClearOutboundFallbackCheckpointRetryStateLocked(context, requestId, reason);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requestId) &&
            !string.Equals(leg.CheckpointRequestId, requestId, StringComparison.Ordinal))
        {
            LogFileTransferFallbackStaleProofIgnored(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                leg,
                "checkpoint_retire_request_mismatch",
                leg.TransportEpochId,
                requestId,
                context.FallbackStaleProofSuppressionKeys);
            return false;
        }

        var retiredRequestId = leg.CheckpointRequestId;
        var retiredTransportEpoch = leg.TransportEpochId;
        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackCheckpointRetire,
                reason,
                "fallback_checkpoint_retire",
                leg,
                retiredRequestId,
                retiredTransportEpoch,
                leg.BridgeRecoveryGeneration,
                leg.CheckpointGeneration,
                null,
                context.CurrentFallbackCheckpointLease,
                null,
                null,
                null,
                null,
                null,
                null,
                out _))
        {
            return false;
        }

        if (!TryApplyOutboundCoordinatorLegMutationLocked(
                context,
                new FileTransferCoordinatorEvent(
                    FileTransferCoordinatorEventKind.FallbackCheckpointRetired,
                    context.RouteSelection,
                    FileTransferTransportHandoffKind.None,
                    FileTransferTransportKind.Unknown,
                    reason,
                    FileTransferLegState.RecoveryActive,
                    CanSendData: true,
                    Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                    Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    retiredTransportEpoch,
                    leg.BridgeRecoveryGeneration,
                    CheckpointRequestId: retiredRequestId,
                    FallbackCheckpointLease: context.CurrentFallbackCheckpointLease),
                FileTransferCoordinatorActionKind.FallbackCheckpointRetire,
                "fallback_checkpoint_retire",
                leg,
                retiredRequestId,
                retiredTransportEpoch,
                out _))
        {
            return false;
        }

        ClearOutboundFallbackCheckpointRetryStateLocked(context, retiredRequestId, reason);
        RememberRetiredFallbackCheckpointRequest(context.RetiredFallbackCheckpointRequestKeys, leg, retiredTransportEpoch, retiredRequestId);
        LogFileTransferFallbackCheckpointRetired(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            leg,
            retiredRequestId,
            reason);
        return true;
    }

    private static bool IsCurrentOutboundFallbackCheckpointOwnerLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        out string reason,
        out FileTransferLeg? leg)
    {
        leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            reason = "fallback_leg_not_current";
            return false;
        }

        if (string.IsNullOrWhiteSpace(leg!.CheckpointRequestId))
        {
            reason = "checkpoint_not_pending";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RepairRequestId))
        {
            reason = "checkpoint_request_missing";
            return false;
        }

        if (!string.Equals(leg.CheckpointRequestId, request.RepairRequestId, StringComparison.Ordinal))
        {
            reason = "checkpoint_request_mismatch";
            return false;
        }

        if (leg.TransportEpochId > 0 &&
            request.TransportEpoch != leg.TransportEpochId)
        {
            reason = "transport_epoch_mismatch";
            return false;
        }

        reason = "current";
        return true;
    }

    private static void ClearOutboundFallbackCheckpointRetryStateLocked(
        OutboundTransferContext context,
        string? requestId,
        string reason)
    {
        if (ShouldClearCheckpointRequest(context.V6RegularNknDeferredStateRefreshRequest?.RepairRequestId, requestId))
        {
            ClearOutboundV6RegularNknDeferredStateRefreshLocked(context);
        }

        if (ShouldClearCheckpointRequest(context.V6RegularNknStateRefreshActiveRequestId, requestId))
        {
            if (context.V6RegularNknStateRefreshActiveSendGeneration > 0)
            {
                context.V6RegularNknStateRefreshRetiredSendGeneration = Math.Max(
                    context.V6RegularNknStateRefreshRetiredSendGeneration,
                    context.V6RegularNknStateRefreshActiveSendGeneration);
                context.V6RegularNknStateRefreshRetiredRequestId = context.V6RegularNknStateRefreshActiveRequestId;
                context.V6RegularNknStateRefreshRetiredSendCount++;
            }

            context.V6RegularNknStateRefreshSendInFlight = 0;
            context.V6RegularNknStateRefreshActiveSendGeneration = 0;
            context.V6RegularNknStateRefreshActiveRequestId = null;
            context.V6RegularNknStateRefreshActivePriority = null;
            context.V6RegularNknStateRefreshActiveStartedUtc = null;
            context.V6RegularNknStateRefreshActiveReusedPendingCheckpoint = false;
        }

        if (ShouldClearCheckpointRequest(context.LastOutboundFallbackCheckpointRepairRequestId, requestId))
        {
            ClearOutboundFallbackCheckpointRepairAuthorityLocked(context);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_retry_state_cleared; direction=outbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; checkpoint_request_id={FormatProtocolLogValue(requestId ?? "(any)")}; reason={FormatProtocolLogValue(reason)}");
    }

    private static bool ShouldClearCheckpointRequest(string? currentRequestId, string? retiredRequestId)
        => string.IsNullOrWhiteSpace(retiredRequestId) ||
           string.IsNullOrWhiteSpace(currentRequestId) ||
           string.Equals(currentRequestId, retiredRequestId, StringComparison.Ordinal);

    private static void ClearOutboundFallbackCheckpointRepairAuthorityLocked(OutboundTransferContext context)
    {
        context.LastOutboundFallbackCheckpointRepairRequestId = null;
        context.LastOutboundFallbackCheckpointRepairTransportEpoch = 0;
        context.LastOutboundFallbackCheckpointRepairLegGeneration = 0;
        context.LastOutboundFallbackCheckpointRepairLiveRouteEpoch = 0;
        context.LastOutboundFallbackCheckpointRepairAcceptedUtc = null;
    }

    private static void MarkOutboundFallbackCheckpointRepairAuthorityAcceptedLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        string? requestId,
        long transportEpoch)
    {
        context.LastOutboundFallbackCheckpointRepairRequestId = requestId;
        context.LastOutboundFallbackCheckpointRepairTransportEpoch = transportEpoch;
        context.LastOutboundFallbackCheckpointRepairLegGeneration = leg.Generation;
        context.LastOutboundFallbackCheckpointRepairLiveRouteEpoch = leg.LiveRouteEpochId;
        context.LastOutboundFallbackCheckpointRepairAcceptedUtc = DateTimeOffset.UtcNow;
    }

    private static void ClearStaleOutboundFallbackTransportEpochAfterCheckpointLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        long checkpointTransportEpoch,
        string reason)
    {
        if (checkpointTransportEpoch <= 0)
        {
            return;
        }

        var transportEpoch = context.V6TransportEpoch;
        var handoff = context.V6TransportHandoff;
        var clearTransportEpoch = transportEpoch is { EpochId: > 0 } &&
                                  transportEpoch.EpochId < checkpointTransportEpoch;
        var clearHandoff = handoff is { EpochId: > 0 } &&
                           handoff.EpochId < checkpointTransportEpoch;
        if (!clearTransportEpoch && !clearHandoff)
        {
            return;
        }

        var clearedTransportEpoch = clearTransportEpoch ? transportEpoch!.EpochId : 0;
        var clearedTransportEpochState = clearTransportEpoch
            ? FormatV6TransportEpochState(transportEpoch!.State)
            : "none";
        var clearedHandoffEpoch = clearHandoff ? handoff!.EpochId : 0;
        var clearedHandoffState = clearHandoff
            ? FormatV6TransportHandoffState(handoff!.State)
            : "none";

        if (clearTransportEpoch)
        {
            context.V6TransportEpoch = null;
        }

        if (clearHandoff)
        {
            context.V6TransportHandoff = null;
        }

        LogFileTransferFallbackStaleTransportEpochCleared(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            leg,
            checkpointTransportEpoch,
            clearedTransportEpoch,
            clearedTransportEpochState,
            clearedHandoffEpoch,
            clearedHandoffState,
            reason);
    }

    private static void ClearStaleInboundFallbackTransportEpochAfterCheckpointLocked(
        InboundTransferContext context,
        FileTransferLeg leg,
        long checkpointTransportEpoch,
        string reason)
    {
        if (checkpointTransportEpoch <= 0)
        {
            return;
        }

        var transportEpoch = context.V6TransportEpoch;
        var handoff = context.V6TransportHandoff;
        var clearTransportEpoch = transportEpoch is { EpochId: > 0 } &&
                                  transportEpoch.EpochId < checkpointTransportEpoch;
        var clearHandoff = handoff is { EpochId: > 0 } &&
                           handoff.EpochId < checkpointTransportEpoch;
        if (!clearTransportEpoch && !clearHandoff)
        {
            return;
        }

        var clearedTransportEpoch = clearTransportEpoch ? transportEpoch!.EpochId : 0;
        var clearedTransportEpochState = clearTransportEpoch
            ? FormatV6TransportEpochState(transportEpoch!.State)
            : "none";
        var clearedHandoffEpoch = clearHandoff ? handoff!.EpochId : 0;
        var clearedHandoffState = clearHandoff
            ? FormatV6TransportHandoffState(handoff!.State)
            : "none";

        if (clearTransportEpoch)
        {
            context.V6TransportEpoch = null;
        }

        if (clearHandoff)
        {
            context.V6TransportHandoff = null;
        }

        LogFileTransferFallbackStaleTransportEpochCleared(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            leg,
            checkpointTransportEpoch,
            clearedTransportEpoch,
            clearedTransportEpochState,
            clearedHandoffEpoch,
            clearedHandoffState,
            reason);
    }

    private static bool TryAcceptOutboundFallbackCheckpointLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        string reason)
        => TryAcceptOutboundFallbackCheckpointCoreLocked(
            context,
            state,
            reason,
            out _);

    private static bool TryAcceptOutboundFallbackCheckpointAndPrepareReissueLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        string reason,
        out FileTransferFrontierRequestFrameV6? checkpointReissueRequest)
        => TryAcceptOutboundFallbackCheckpointCoreLocked(
            context,
            state,
            reason,
            out checkpointReissueRequest);

    private static bool TryAcceptOutboundFallbackCheckpointCoreLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        string reason,
        out FileTransferFrontierRequestFrameV6? checkpointReissueRequest)
    {
        checkpointReissueRequest = null;
        var leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return true;
        }

        if (context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
        {
            LogFileTransferFallbackStaleProofIgnored(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                leg,
                "route_or_protocol_mismatch",
                state.TransportEpoch,
                state.RepairRequestId,
                context.FallbackStaleProofSuppressionKeys);
            return false;
        }

        leg = context.CurrentTransferLeg;
        var plainTailReconciliationProof = false;
        if (!IsPostTunaFallbackCheckpointProofReceiverState(state))
        {
            plainTailReconciliationProof =
                IsCurrentOutboundFallbackTailReconciliationPlainReceiverStateProofLocked(context, leg!, state);
            if (!plainTailReconciliationProof)
            {
                if (TryAcceptOutboundFallbackCheckpointSupersededByCurrentPeerStateLocked(context, leg!, state, reason))
                {
                    return true;
                }

                TrackOutboundPostTunaFallbackLiveCheckpointLocked(context, leg!, state);
                return true;
            }
        }

        if (!FileTransferCoordinator.IsCurrentPostTunaFallbackLegAwaitingCheckpoint(leg))
        {
            if (IsAcceptedOutboundFallbackCheckpointProofDuplicate(context, leg, state))
            {
                TrackOutboundPostTunaFallbackLiveCheckpointLocked(context, leg!, state);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(state.RepairRequestId))
            {
                LogFileTransferFallbackStaleProofIgnored(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    leg,
                    "checkpoint_not_pending",
                    state.TransportEpoch,
                    state.RepairRequestId,
                    context.FallbackStaleProofSuppressionKeys);
                LogFileTransferFallbackCheckpointReissueSuppressed(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    leg,
                    "checkpoint_not_pending",
                    reason,
                    state.TransportEpoch,
                    state.RepairRequestId);
                return true;
            }

            TrackOutboundPostTunaFallbackLiveCheckpointLocked(context, leg!, state);
            return true;
        }

        if (!plainTailReconciliationProof &&
            !FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
                leg,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                state.TransportEpoch,
                state.RepairRequestId,
                out var checkpointRejectionReason))
        {
            LogFileTransferFallbackStaleProofIgnored(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                leg,
                checkpointRejectionReason,
                state.TransportEpoch,
                state.RepairRequestId,
                context.FallbackStaleProofSuppressionKeys);
            TryCreateOutboundFallbackCheckpointReissueRequestLocked(
                context,
                leg!,
                state,
                checkpointRejectionReason,
                out checkpointReissueRequest);
            return true;
        }

        var currentLeg = leg!;
        var acceptedCheckpointRequestId = state.RepairRequestId ?? currentLeg.CheckpointRequestId;
        var acceptedCheckpointPriority = state.Priority ?? currentLeg.CheckpointPriority;
        var acceptedReason = plainTailReconciliationProof
            ? "tail_reconciliation_plain_receiver_state"
            : reason;
        var tailReconciliationCheckpoint =
            plainTailReconciliationProof ||
            IsPostTunaFallbackTailReconciliationStateRefresh(state) ||
            string.Equals(currentLeg.CheckpointPriority, V6RegularNknStateRefreshTailReconciliationPriority, StringComparison.Ordinal);

        var acceptedCommitted = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        if (!TryAcceptOutboundFallbackCheckpointWithCoordinatorLocked(
                context,
                currentLeg,
                state.TransportEpoch,
                acceptedCommitted,
                state.DurableReceivedHighestChunkIndex,
                acceptedCheckpointRequestId,
                acceptedReason))
        {
            return false;
        }

        RememberRetiredFallbackCheckpointRequest(context.RetiredFallbackCheckpointRequestKeys, currentLeg, state.TransportEpoch, acceptedCheckpointRequestId);
        ClearOutboundFallbackCheckpointRetryStateLocked(context, acceptedCheckpointRequestId, "checkpoint_accepted");
        ClearOutboundFallbackCheckpointDeliveryRecoveryPendingLocked(context, "checkpoint_accepted");
        MarkOutboundFallbackCheckpointRepairAuthorityAcceptedLocked(
            context,
            currentLeg,
            acceptedCheckpointRequestId,
            state.TransportEpoch);
        ClearStaleOutboundFallbackTransportEpochAfterCheckpointLocked(context, currentLeg, state.TransportEpoch, reason);
        LogFileTransferFallbackCheckpointAccepted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            acceptedCheckpointRequestId,
            acceptedReason);
        LogFileTransferFallbackLegAuthorityCheckpointAccepted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            acceptedCheckpointRequestId,
            acceptedReason);
        MarkLiveRouteEpochRecoveredFromFallbackCheckpointIfCurrent(context, currentLeg);
        if (tailReconciliationCheckpoint)
        {
            LogFileTransferFallbackTailReconciliationAccepted(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                currentLeg,
                state,
                acceptedCheckpointRequestId,
                acceptedCheckpointPriority,
                acceptedReason);
        }

        TrackOutboundPostTunaFallbackLiveCheckpointLocked(context, currentLeg, state);
        return true;
    }

    private bool TryMarkOutboundFallbackTailQuiescentAfterCheckpointAcceptedLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        FileTransferReceiverStateFrameV6 state,
        string reason)
    {
        if (!context.RuntimeUnlockWaitingForFallbackSurvival ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !ReferenceEquals(context.CurrentTransferLeg, leg) ||
            !IsCurrentPostTunaFallbackLeg(leg) ||
            leg.State != FileTransferLegState.RecoveryActive ||
            !leg.CanSendData ||
            !string.IsNullOrWhiteSpace(leg.CheckpointRequestId) ||
            context.IsTerminal)
        {
            return false;
        }

        var currentLiveRouteEpoch = context.CurrentLiveRouteEpoch?.EpochId ?? 0;
        if (leg.LiveRouteEpochId > 0 &&
            currentLiveRouteEpoch > 0 &&
            leg.LiveRouteEpochId != currentLiveRouteEpoch)
        {
            return false;
        }

        var committed = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var highestObserved = Math.Max(-1, state.DurableReceivedHighestChunkIndex);
        if (state.TerminalReady && committed >= context.ChunkCount && context.ChunkCount > 0)
        {
            LogOutboundFallbackTailTerminalProofConsumedOnceLocked(context, leg, committed, reason);
            return false;
        }

        if (state.MissingRanges.Any(range => range.StartChunkIndex > committed) &&
            (!string.IsNullOrWhiteSpace(state.RepairRequestId) ||
             !string.IsNullOrWhiteSpace(state.RecoveryMode)))
        {
            return false;
        }

        if (!TryMarkOutboundFallbackTailQuiescentWithCoordinatorLocked(
                context,
                leg,
                state.TransportEpoch,
                committed,
                highestObserved,
                reason))
        {
            return false;
        }

        ClearOutboundFallbackTailRecoveryOwnershipAfterYieldLocked(
            context,
            leg,
            "fallback_tail_quiescent");
        ClearOutboundFallbackTailQuiescenceDiagnosticLocked(context);
        MaybeClearOutboundV6RegularNknFrontierPressureLocked(
            context,
            state,
            "fallback_tail_quiescent",
            forceClear: true);
        ClearPostTunaFallbackControlPlanePressure(context.TransferId, "fallback_tail_quiescent");
        TryPublishRouteHintIfAuthorizedLocked(
            context,
            "fallback_tail_quiescent_route_hint",
            "fallback_tail_quiescent",
            "fallback_tail_quiescent",
            runtimeUnlockAdmissionFactory: () => RequestRuntimeUnlockAdmissionLocked(context, "route_hint", "fallback_tail_quiescent"));
        LogFileTransferFallbackTailQuiescentYieldGranted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            leg,
            committed,
            highestObserved,
            reason);
        RecordOutboundFallbackTailLocalYieldLocked(
            context,
            leg,
            committed,
            highestObserved,
            "fallback_tail_quiescent");
        return true;
    }

    private static bool TryAcceptOutboundFallbackCheckpointSupersededByCurrentPeerStateLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        FileTransferReceiverStateFrameV6 state,
        string source)
    {
        if (!FileTransferCoordinator.IsCurrentPostTunaFallbackLegAwaitingCheckpoint(leg) ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !context.PullPostTunaRecoveryActive ||
            !string.IsNullOrWhiteSpace(state.RepairRequestId) ||
            !string.Equals(state.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(state.TransferId, context.TransferId, StringComparison.Ordinal))
        {
            return false;
        }

        var currentLiveRouteEpoch = context.CurrentLiveRouteEpoch?.EpochId ?? 0;
        if (leg.LiveRouteEpochId > 0 &&
            currentLiveRouteEpoch > 0 &&
            currentLiveRouteEpoch != leg.LiveRouteEpochId)
        {
            return false;
        }

        if (leg.TransportEpochId <= 0 ||
            state.TransportEpoch <= 0 ||
            state.TransportEpoch < leg.TransportEpochId)
        {
            return false;
        }

        if (state.TransportEpoch == leg.TransportEpochId &&
            !string.IsNullOrWhiteSpace(state.RecoveryMode))
        {
            return false;
        }

        if (context.V4LastStateEpoch >= 0 &&
            state.Epoch < context.V4LastStateEpoch)
        {
            return false;
        }

        var maxChunk = Math.Max(0, context.ChunkCount);
        var committed = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, maxChunk);
        var requiredCommitted = Math.Max(
            Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, maxChunk),
            Math.Clamp(leg.ProvenCommittedChunkIndex, 0, maxChunk));
        if (committed < requiredCommitted)
        {
            return false;
        }

        if (committed > 0 &&
            state.DurableReceivedHighestChunkIndex < committed - 1)
        {
            return false;
        }

        var retiredCheckpointRequestId = leg.CheckpointRequestId;
        var retiredTransportEpoch = leg.TransportEpochId;
        const string acceptedReason = "peer_current_state_superseded_checkpoint";
        if (!TryAcceptOutboundFallbackCheckpointWithCoordinatorLocked(
                context,
                leg,
                state.TransportEpoch,
                committed,
                state.DurableReceivedHighestChunkIndex,
                retiredCheckpointRequestId,
                acceptedReason))
        {
            return false;
        }

        RememberRetiredFallbackCheckpointRequest(
            context.RetiredFallbackCheckpointRequestKeys,
            leg,
            retiredTransportEpoch,
            retiredCheckpointRequestId);
        ClearOutboundFallbackCheckpointRetryStateLocked(context, retiredCheckpointRequestId, acceptedReason);
        ClearOutboundFallbackCheckpointDeliveryRecoveryPendingLocked(context, acceptedReason);
        MarkOutboundFallbackCheckpointRepairAuthorityAcceptedLocked(
            context,
            leg,
            retiredCheckpointRequestId,
            state.TransportEpoch);
        ClearStaleOutboundFallbackTransportEpochAfterCheckpointLocked(context, leg, state.TransportEpoch, acceptedReason);

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_superseded_by_current_peer_state; direction=outbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; source={FormatProtocolLogValue(source)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; retired_transport_epoch={retiredTransportEpoch}; accepted_transport_epoch={state.TransportEpoch}; retired_checkpoint_request_id={FormatProtocolLogValue(retiredCheckpointRequestId)}; receiver_committed_chunk={committed}; receiver_highest_observed_chunk={state.DurableReceivedHighestChunkIndex}; required_committed_chunk={requiredCommitted}");
        LogFileTransferFallbackCheckpointAccepted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            leg,
            retiredCheckpointRequestId,
            acceptedReason);
        LogFileTransferFallbackLegAuthorityCheckpointAccepted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            leg,
            retiredCheckpointRequestId,
            acceptedReason);
        MarkLiveRouteEpochRecoveredFromFallbackCheckpointIfCurrent(context, leg);
        TrackOutboundPostTunaFallbackLiveCheckpointLocked(context, leg, state);
        return true;
    }

    private static bool IsCurrentOutboundFallbackTailReconciliationPlainReceiverStateProofLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        FileTransferReceiverStateFrameV6 state)
    {
        if (!IsCurrentPostTunaFallbackTailReconciliationCheckpointPending(leg) ||
            !string.IsNullOrWhiteSpace(state.RepairRequestId) ||
            !string.Equals(state.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(state.TransferId, context.TransferId, StringComparison.Ordinal))
        {
            return false;
        }

        var currentLiveRouteEpoch = context.CurrentLiveRouteEpoch?.EpochId ?? 0;
        if (leg.LiveRouteEpochId > 0 &&
            currentLiveRouteEpoch > 0 &&
            currentLiveRouteEpoch != leg.LiveRouteEpochId)
        {
            return false;
        }

        if (leg.TransportEpochId <= 0 ||
            state.TransportEpoch <= 0 ||
            state.TransportEpoch != leg.TransportEpochId)
        {
            return false;
        }

        if (context.V4LastStateEpoch >= 0 &&
            state.Epoch < context.V4LastStateEpoch)
        {
            return false;
        }

        var committed = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var requiredCommitted = Math.Max(
            Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount)),
            Math.Clamp(leg.ProvenCommittedChunkIndex, 0, Math.Max(0, context.ChunkCount)));
        if (committed < requiredCommitted)
        {
            return false;
        }

        return committed <= 0 ||
               state.DurableReceivedHighestChunkIndex >= committed - 1;
    }

    private static bool TryCreateOutboundFallbackCheckpointReissueRequestLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        FileTransferReceiverStateFrameV6 state,
        string checkpointRejectionReason,
        out FileTransferFrontierRequestFrameV6? checkpointReissueRequest)
    {
        checkpointReissueRequest = null;
        if (!IsCurrentPostTunaFallbackLegCheckpointPending(leg) ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            !context.PullPostTunaRecoveryActive ||
            string.IsNullOrWhiteSpace(leg.CheckpointRequestId) ||
            string.Equals(checkpointRejectionReason, "checkpoint_not_pending", StringComparison.Ordinal) ||
            !IsOutboundFallbackCheckpointReissueReason(checkpointRejectionReason) ||
            !string.Equals(state.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(state.TransferId, context.TransferId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                context,
                FileTransferCoordinatorActionKind.FallbackCheckpointReissue,
                "fallback_checkpoint_reissue",
                checkpointRejectionReason,
                leg,
                leg.CheckpointRequestId,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                leg.CheckpointGeneration,
                null,
                context.CurrentFallbackCheckpointLease,
                null,
                null,
                null,
                null,
                null,
                null,
                out _))
        {
            return false;
        }

        if (context.V6RegularNknStateRefreshSendInFlight > 0 &&
            string.Equals(
                context.V6RegularNknStateRefreshActiveRequestId,
                leg.CheckpointRequestId,
                StringComparison.Ordinal))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_fallback_checkpoint_reissue_suppressed; direction=outbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; reason=current_checkpoint_send_in_flight; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(leg.CheckpointRequestId)}; stale_proof_reason={FormatProtocolLogValue(checkpointRejectionReason)}; proof_transport_epoch={state.TransportEpoch}; proof_checkpoint_request_id={FormatProtocolLogValue(state.RepairRequestId ?? "(none)")}");
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (context.V6RegularNknLastStateRefreshRequestedUtc is { } lastRefresh &&
            now - lastRefresh < CurrentV6RegularNknSparseRuntimeStateRefreshCooldown)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_fallback_checkpoint_reissue_suppressed; direction=outbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; reason=reissue_cooldown; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(leg.CheckpointRequestId)}; stale_proof_reason={FormatProtocolLogValue(checkpointRejectionReason)}; proof_transport_epoch={state.TransportEpoch}; proof_checkpoint_request_id={FormatProtocolLogValue(state.RepairRequestId ?? "(none)")}; last_refresh_age_ms={(long)Math.Max(0, (now - lastRefresh).TotalMilliseconds)}; cooldown_ms={(long)CurrentV6RegularNknSparseRuntimeStateRefreshCooldown.TotalMilliseconds}");
            return false;
        }

        var refreshHintChunkIndex = context.ChunkCount > 0
            ? Math.Min(Math.Max(0, context.RemoteNextExpectedChunkIndex), context.ChunkCount - 1)
            : 0;
        context.V6RegularNknLastStateRefreshRequestedUtc = now;
        context.V6EpochLivenessDeferralCount++;
        context.V6EpochLivenessDeferralUtc = now;
        context.PullTransportResumeRequestPending = true;
        context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_checkpoint_reissue";
        context.V6SenderPumpLastWakeReason = "post_tuna_fallback_checkpoint_reissue";
        checkpointReissueRequest = new FileTransferFrontierRequestFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            TransportEpoch = leg.TransportEpochId,
            RepairRequestId = leg.CheckpointRequestId,
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = refreshHintChunkIndex,
                    ChunkCount = 1,
                },
            ],
            Priority = leg.CheckpointPriority ?? V6RegularNknStateRefreshPriority,
            RecoveryMode = V6RegularNknStateRefreshRecoveryMode,
        };

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_checkpoint_reissue_requested; direction=outbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; route={leg.RouteSelection.TelemetryToken}; protocol_version={leg.ProtocolVersion}; live_route_epoch={leg.LiveRouteEpochId}; transport_epoch={leg.TransportEpochId}; checkpoint_request_id={FormatProtocolLogValue(leg.CheckpointRequestId)}; priority={FormatProtocolLogValue(checkpointReissueRequest.Priority ?? "(none)")}; stale_proof_reason={FormatProtocolLogValue(checkpointRejectionReason)}; proof_transport_epoch={state.TransportEpoch}; proof_checkpoint_request_id={FormatProtocolLogValue(state.RepairRequestId ?? "(none)")}; receiver_committed_chunk={state.ContiguousCommittedChunkIndex}; receiver_highest_observed_chunk={state.DurableReceivedHighestChunkIndex}; refresh_hint_chunk_index={refreshHintChunkIndex}");
        return true;
    }

    private static bool IsOutboundFallbackCheckpointReissueReason(string checkpointRejectionReason)
        => string.Equals(checkpointRejectionReason, "transport_epoch_mismatch", StringComparison.Ordinal) ||
           string.Equals(checkpointRejectionReason, "checkpoint_request_missing_proof", StringComparison.Ordinal) ||
           string.Equals(checkpointRejectionReason, "checkpoint_request_mismatch", StringComparison.Ordinal);

    private static bool IsPostTunaFallbackCheckpointProofReceiverState(FileTransferReceiverStateFrameV6 state)
        => string.Equals(state.RecoveryMode, V6RegularNknStateRefreshRecoveryMode, StringComparison.Ordinal) ||
           string.Equals(state.Priority, V6RegularNknStateRefreshPriority, StringComparison.Ordinal) ||
           string.Equals(state.Priority, V6RegularNknStateRefreshStaleInflightPriority, StringComparison.Ordinal) ||
           string.Equals(state.Priority, V6RegularNknStateRefreshStaleCreditPriority, StringComparison.Ordinal) ||
           string.Equals(state.Priority, V6RegularNknStateRefreshTailReconciliationPriority, StringComparison.Ordinal);

    private static bool IsAcceptedOutboundFallbackCheckpointProofDuplicate(
        OutboundTransferContext context,
        FileTransferLeg? leg,
        FileTransferReceiverStateFrameV6 state)
        => IsCurrentPostTunaFallbackLeg(leg) &&
           !string.IsNullOrWhiteSpace(state.RepairRequestId) &&
           string.Equals(context.LastOutboundFallbackCheckpointRepairRequestId, state.RepairRequestId, StringComparison.Ordinal) &&
           context.LastOutboundFallbackCheckpointRepairTransportEpoch == state.TransportEpoch &&
           context.LastOutboundFallbackCheckpointRepairLegGeneration == leg!.Generation &&
           context.LastOutboundFallbackCheckpointRepairAcceptedUtc is not null;

    private static void TrackOutboundPostTunaFallbackLiveCheckpointLocked(
        OutboundTransferContext context,
        FileTransferLeg leg,
        FileTransferReceiverStateFrameV6 state)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
            !context.PullPostTunaRecoveryActive ||
            !IsCurrentPostTunaFallbackLeg(leg) ||
            leg.State != FileTransferLegState.RecoveryActive ||
            !leg.CanSendData ||
            !IsV6TransportHandoffBlockingTail(context.V6TransportHandoff))
        {
            context.PostTunaFallbackLiveCheckpointStallCommittedChunkIndex = -1;
            context.PostTunaFallbackLiveCheckpointStallHighestObservedChunkIndex = -1;
            context.PostTunaFallbackLiveCheckpointStallCount = 0;
            return;
        }

        var committed = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var highestObserved = Math.Max(-1, state.DurableReceivedHighestChunkIndex);
        if (highestObserved - committed < V6PostTunaFallbackLiveCheckpointTailGapChunks)
        {
            context.PostTunaFallbackLiveCheckpointStallCommittedChunkIndex = committed;
            context.PostTunaFallbackLiveCheckpointStallHighestObservedChunkIndex = highestObserved;
            context.PostTunaFallbackLiveCheckpointStallCount = 0;
            return;
        }

        if (context.PostTunaFallbackLiveCheckpointStallCommittedChunkIndex == committed &&
            highestObserved >= context.PostTunaFallbackLiveCheckpointStallHighestObservedChunkIndex)
        {
            context.PostTunaFallbackLiveCheckpointStallCount++;
        }
        else
        {
            context.PostTunaFallbackLiveCheckpointStallCommittedChunkIndex = committed;
            context.PostTunaFallbackLiveCheckpointStallCount = 1;
        }

        context.PostTunaFallbackLiveCheckpointStallHighestObservedChunkIndex = Math.Max(
            context.PostTunaFallbackLiveCheckpointStallHighestObservedChunkIndex,
            highestObserved);
    }

    private void MarkInboundFallbackCheckpointAcceptedLocked(
        InboundTransferContext context,
        string? requestId,
        long transportEpoch,
        string reason)
    {
        var leg = context.CurrentTransferLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return;
        }

        if (context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
        {
            LogFileTransferFallbackStaleProofIgnored(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                leg,
                "route_or_protocol_mismatch",
                transportEpoch,
                requestId,
                context.FallbackStaleProofSuppressionKeys);
            return;
        }

        if (!FileTransferCoordinator.IsCurrentPostTunaFallbackLegAwaitingCheckpoint(leg))
        {
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                LogFileTransferFallbackStaleProofIgnored(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    leg,
                    "checkpoint_not_pending",
                    transportEpoch,
                    requestId,
                    context.FallbackStaleProofSuppressionKeys);
                LogFileTransferFallbackCheckpointReissueSuppressed(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    leg,
                    "checkpoint_not_pending",
                    reason,
                    transportEpoch,
                    requestId);
            }

            return;
        }

        if (!string.Equals(reason, V6RegularNknStateRefreshRecoveryMode, StringComparison.Ordinal))
        {
            LogFileTransferFallbackStaleProofIgnored(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                leg,
                "recovery_mode_mismatch",
                transportEpoch,
                requestId,
                context.FallbackStaleProofSuppressionKeys);
            return;
        }

        if (!FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
                leg,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                transportEpoch,
                requestId,
                out var checkpointRejectionReason))
        {
            LogFileTransferFallbackStaleProofIgnored(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                leg,
                checkpointRejectionReason,
                transportEpoch,
                requestId,
                context.FallbackStaleProofSuppressionKeys);
            return;
        }

        var currentLeg = leg!;
        if (!TryAcceptInboundFallbackCheckpointWithCoordinatorLocked(
                context,
                currentLeg,
                transportEpoch,
                Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                context.PullHighestReceivedChunkIndex,
                requestId,
                reason))
        {
            return;
        }

        RememberRetiredFallbackCheckpointRequest(context.RetiredFallbackCheckpointRequestKeys, currentLeg, transportEpoch, requestId);
        ClearStaleInboundFallbackTransportEpochAfterCheckpointLocked(context, currentLeg, transportEpoch, reason);
        LogFileTransferFallbackCheckpointAccepted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            requestId,
            reason);
        LogFileTransferFallbackLegAuthorityCheckpointAccepted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            currentLeg,
            requestId,
            reason);
        MarkLiveRouteEpochRecoveredFromFallbackCheckpointIfCurrent(context, currentLeg);
    }

    private static bool ShouldUseV6RegularNknSparseRuntime(OutboundTransferContext context)
        => context.RouteRuntime.UsesV6SparsePump;

    private static bool ShouldUseV6RegularNknSparseRuntime(InboundTransferContext context)
        => context.RouteRuntime.UsesV6SparsePump;

    private static bool IsPrimaryRegularNknBulkV6ProfileEnabled(IFileTransferSignalingTransport? currentTransport)
        => ResolveTransportProfileKind(currentTransport) == FileTransferTransportProfileKind.ConservativeNknStartup;

    private Task RunOutboundPrimaryRegularNknBulkV6Async(OutboundTransferContext context)
        => RunOutboundSparseCreditSenderAsync(context, FileTransferSparseCreditRuntimeKind.PrimaryRegularNknBulkV6);

    private Task RunInboundPrimaryRegularNknBulkV6Async(
        InboundTransferContext context,
        FileTransferSessionOpenV2 sessionOpen)
        => RunInboundSparseCreditReceiveLoopAsync(context, sessionOpen, FileTransferSparseCreditRuntimeKind.PrimaryRegularNknBulkV6);

    private Task RunOutboundRegularNknV4Async(OutboundTransferContext context)
        => RunOutboundSparseCreditSenderAsync(context, FileTransferSparseCreditRuntimeKind.PrimaryRegularNknV4);

    private Task RunInboundRegularNknV4Async(
        InboundTransferContext context,
        FileTransferSessionOpenV2 sessionOpen)
        => RunInboundSparseCreditReceiveLoopAsync(context, sessionOpen, FileTransferSparseCreditRuntimeKind.PrimaryRegularNknV4);

    private static void LogPrimaryRegularNknBulkV6Selected(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        FileTransferRuntimeProfileSelection selection,
        FileTransferRouteSelection routeSelection)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; route={routeSelection.TelemetryToken}; runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; credit_profile=v4_sparse; frame_profile=v6; frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; recovery_profile=regular_nkn_quiet; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(selection.BridgeRecoveryPolicy)}; liveness_terminal_policy={FormatFileTransferRouteLivenessTerminalPolicy(routeSelection.LivenessTerminalPolicy)}; activation=primary_regular_nkn; selection_reason={FormatProtocolLogValue(selection.Reason)}");
    }

    private static void LogFileTransferBridgeRecoveryPolicySelected(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        FileTransferRuntimeProfileSelection selection,
        FileTransferRouteSelection routeSelection,
        int liveRouteEpochId = 0)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bridge_recovery_policy_selected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; route={routeSelection.TelemetryToken}; protocol_version={routeSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; handoff_kind={FormatFileTransferTransportHandoffKind(routeSelection.HandoffKind)}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(selection.BridgeRecoveryPolicy)}; liveness_terminal_policy={FormatFileTransferRouteLivenessTerminalPolicy(routeSelection.LivenessTerminalPolicy)}; selection_reason={FormatProtocolLogValue(selection.Reason)}{FormatLiveRouteEpochSuffix(liveRouteEpochId)}");
    }

    private static void LogFileTransferRouteSelected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRouteSelection routeSelection,
        FileTransferRouteResolverInput routeInput,
        int liveRouteEpochId = 0,
        RoutePublicationToken? routePublicationToken = null,
        string? coordinatorDecisionReason = null)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_route_selected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; route={routeSelection.TelemetryToken}; protocol_version={routeSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; handoff_kind={FormatFileTransferTransportHandoffKind(routeSelection.HandoffKind)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(routeSelection.BridgeRecoveryPolicy)}; liveness_terminal_policy={FormatFileTransferRouteLivenessTerminalPolicy(routeSelection.LivenessTerminalPolicy)}; selection_reason={FormatProtocolLogValue(routeSelection.SelectionReason)}; file_tuna_active={(routeInput.IsFileTunaActive ? 1 : 0)}; post_tuna_fallback_active={(routeInput.IsPostTunaFileFallbackActive ? 1 : 0)}; diagnostic_regular_nkn_v6={(routeInput.IsDiagnosticRegularNknV6RouteEnabled ? 1 : 0)}; transport_profile={FormatFileTransferTransportProfileKind(routeInput.TransportProfileKind)}{FormatPublicationLiveRouteEpochSuffix(routePublicationToken, liveRouteEpochId)}{FormatRoutePublicationAuthoritySuffix(routePublicationToken, coordinatorDecisionReason)}");
    }

    private void NotifyFileTransferRouteHintObserver(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRouteSelection routeSelection,
        int liveRouteEpochId,
        string source,
        FileTransferLeg? currentLeg = null,
        RuntimeUnlockAdmissionDecision? runtimeUnlockAdmission = null,
        RoutePublicationToken? routePublicationToken = null,
        string? coordinatorDecisionReason = null)
    {
        if (transport is not IFileTransferRouteHintObserver observer)
        {
            return;
        }

        try
        {
            observer.ObserveFileTransferRouteHint(new FileTransferRouteHintNotification(
                sessionId,
                transferId,
                routeSelection.TelemetryToken,
                routeSelection.ProtocolVersion,
                source,
                runtimeUnlockAdmission?.Granted ?? false,
                runtimeUnlockAdmission?.Reason ?? "unknown",
                currentLeg is null
                    ? "unknown"
                    : FileTransferCoordinator.FormatLegState(currentLeg.State),
                currentLeg?.CanSendData ?? false,
                currentLeg?.Generation ?? 0,
                currentLeg?.CheckpointRequestId,
                CreateRoutePublicationAuthority(
                    routePublicationToken,
                    coordinatorDecisionReason)));
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_route_hint_observer_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; route={routeSelection.TelemetryToken}; protocol_version={routeSelection.ProtocolVersion}; live_route_epoch={liveRouteEpochId}; source={FormatProtocolLogValue(source)}; error={FormatProtocolLogValue(ex.GetType().Name)}{FormatRoutePublicationAuthoritySuffix(routePublicationToken, coordinatorDecisionReason)}");
        }
    }

    private static void LogFileTransferRouteTransitioned(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRouteSelection previousRouteSelection,
        FileTransferRouteSelection routeSelection,
        string reason,
        RoutePublicationToken? routePublicationToken = null,
        string? coordinatorDecisionReason = null)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_route_transitioned; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; route={routeSelection.TelemetryToken}; protocol_version={routeSelection.ProtocolVersion}; previous_route={previousRouteSelection.TelemetryToken}; new_route={routeSelection.TelemetryToken}; previous_protocol_version={previousRouteSelection.ProtocolVersion}; new_protocol_version={routeSelection.ProtocolVersion}; previous_runtime_profile={FormatFileTransferRouteRuntimeProfile(previousRouteSelection.RuntimeProfile)}; new_runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; previous_frame_family={FormatFileTransferFrameFamily(previousRouteSelection.FrameFamily)}; new_frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; handoff_kind={FormatFileTransferTransportHandoffKind(routeSelection.HandoffKind)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(routeSelection.BridgeRecoveryPolicy)}; selection_reason={FormatProtocolLogValue(routeSelection.SelectionReason)}; reason={FormatProtocolLogValue(reason)}{FormatPublicationLiveRouteEpochSuffix(routePublicationToken, 0)}{FormatRoutePublicationAuthoritySuffix(routePublicationToken, coordinatorDecisionReason)}");
    }

    private static string FormatLiveRouteEpochSuffix(int liveRouteEpochId)
        => liveRouteEpochId > 0
            ? $"; live_route_epoch={liveRouteEpochId}"
            : string.Empty;

    private static string FormatPublicationLiveRouteEpochSuffix(
        RoutePublicationToken? routePublicationToken,
        int liveRouteEpochId)
        => routePublicationToken is { } token && !token.IsDefault
            ? $"; live_route_epoch={token.LiveRouteEpoch}"
            : FormatLiveRouteEpochSuffix(liveRouteEpochId);

    private static string FormatRoutePublicationAuthoritySuffix(
        RoutePublicationToken? routePublicationToken,
        string? coordinatorDecisionReason)
    {
        if (routePublicationToken is not { } token || token.IsDefault)
        {
            return string.Empty;
        }

        var targetRoute = FileTransferRouteResolver.Resolve(token.TargetRoute).TelemetryToken;
        var routeCommitGeneration = token.RouteCommitGeneration ?? 0;
        return $"; route_publication_generation={token.PublicationGeneration}; runtime_generation={token.RuntimeGeneration}; terminal_generation={token.TerminalGeneration}; leg_generation={token.LegGeneration}; route_commit_generation={routeCommitGeneration}; target_route={targetRoute}; target_protocol_version={token.TargetProtocolVersion}; target_transport={FormatFileTransferTransportKind(token.TargetTransport)}; route_publication_action_kind={FormatProtocolLogValue(token.ActionKind.ToString())}; coordinator_decision_reason={FormatProtocolLogValue(coordinatorDecisionReason ?? token.Reason)}";
    }

    private static FileTransferRoutePublicationAuthority? CreateRoutePublicationAuthority(
        RoutePublicationToken? routePublicationToken,
        string? coordinatorDecisionReason)
    {
        if (routePublicationToken is not { } token || token.IsDefault)
        {
            return null;
        }

        return new FileTransferRoutePublicationAuthority(
            token.SessionId,
            token.TransferId,
            token.Direction,
            token.RouteToken,
            token.ProtocolVersion,
            token.LiveRouteEpoch,
            token.RuntimeGeneration,
            token.TerminalGeneration,
            token.LegGeneration,
            token.PublicationGeneration,
            token.RouteCommitGeneration,
            FileTransferRouteResolver.Resolve(token.TargetRoute).TelemetryToken,
            token.TargetProtocolVersion,
            token.TargetTransport,
            token.ActionKind.ToString(),
            coordinatorDecisionReason ?? token.Reason);
    }

    private static FileTransferTransportKind ResolveRoutePublicationTargetTransport(FileTransferRoute route)
        => route == FileTransferRoute.FileTunaV4
            ? FileTransferTransportKind.Tuna
            : FileTransferTransportKind.RegularNkn;

    private static LiveRouteEpoch StartLiveRouteEpoch(
        int previousEpochId,
        FileTransferRouteSelection routeSelection,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        string reason)
        => FileTransferCoordinator.StartLiveRouteEpoch(
            previousEpochId,
            routeSelection,
            handoffKind,
            targetTransport,
            NormalizeReason(reason) ?? "live_route_transition");

    private static void LogLiveRouteEpochStarted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        LiveRouteEpoch epoch,
        FileTransferRouteSelection previousRouteSelection,
        RoutePublicationToken? routePublicationToken = null,
        string? coordinatorDecisionReason = null)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_live_route_epoch_started; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; live_route_epoch={epoch.EpochId}; previous_route={previousRouteSelection.TelemetryToken}; route={epoch.RouteSelection.TelemetryToken}; protocol_version={epoch.RouteSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(epoch.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(epoch.RouteSelection.FrameFamily)}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.HandoffKind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; state={FormatProtocolLogValue(epoch.State)}; reason={FormatProtocolLogValue(epoch.Reason)}{FormatRoutePublicationAuthoritySuffix(routePublicationToken, coordinatorDecisionReason)}");
    }

    private static void LogLiveRouteEpochRecovered(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        LiveRouteEpoch? epoch,
        string reason,
        RoutePublicationToken? routePublicationToken = null,
        string? coordinatorDecisionReason = null)
    {
        if (epoch is null)
        {
            return;
        }

        epoch.State = "recovered";
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_live_route_epoch_recovered; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; live_route_epoch={epoch.EpochId}; route={epoch.RouteSelection.TelemetryToken}; protocol_version={epoch.RouteSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(epoch.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(epoch.RouteSelection.FrameFamily)}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.HandoffKind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}{FormatRoutePublicationAuthoritySuffix(routePublicationToken, coordinatorDecisionReason)}");
    }

    private static void LogLiveRouteEpochTerminal(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        LiveRouteEpoch? epoch,
        FileTransferTransferState terminalState,
        string reason)
    {
        if (epoch is null)
        {
            return;
        }

        epoch.State = "terminal";
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_live_route_epoch_terminal; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; live_route_epoch={epoch.EpochId}; route={epoch.RouteSelection.TelemetryToken}; protocol_version={epoch.RouteSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(epoch.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(epoch.RouteSelection.FrameFamily)}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.HandoffKind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; terminal_state={terminalState.ToString().ToLowerInvariant()}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogLiveRouteEpochRecoveredBeforeTerminalIfCompleted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        LiveRouteEpoch? epoch,
        FileTransferTransferState terminalState,
        string reason)
    {
        if (epoch is null ||
            terminalState != FileTransferTransferState.Completed ||
            epoch.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            epoch.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            epoch.HandoffKind != FileTransferTransportHandoffKind.TunaToNormalFallback ||
            epoch.TargetTransport != FileTransferTransportKind.RegularNkn ||
            string.Equals(epoch.State, "recovered", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(epoch.State, "terminal", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
    }

    private static bool MarkLiveRouteEpochRecoveredFromFallbackCheckpointIfCurrent(
        OutboundTransferContext context,
        FileTransferLeg leg)
        // Legacy static checkpoint seams cannot mint route-publication authority.
        => false;

    private static bool MarkLiveRouteEpochRecoveredFromFallbackCheckpointIfCurrent(
        InboundTransferContext context,
        FileTransferLeg leg)
        // Legacy static checkpoint seams cannot mint route-publication authority.
        => false;

    private FileTransferBridgeRecoveryPolicy ResolveReceiveRecoveryPolicyForRequestLocked(FileTransferReceiveRecoveryRequest request)
    {
        if (request.Direction == FileTransferDirection.Outbound &&
            outboundTransfer is { } outbound &&
            ReferenceEquals(outboundTransfer, outbound) &&
            string.Equals(outbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
            string.Equals(outbound.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            return outbound.BridgeRecoveryPolicy;
        }

        if (request.Direction == FileTransferDirection.Inbound &&
            inboundTransfer is { } inbound &&
            ReferenceEquals(inboundTransfer, inbound) &&
            string.Equals(inbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
            string.Equals(inbound.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            return inbound.BridgeRecoveryPolicy;
        }

        return FileTransferBridgeRecoveryPolicy.TunaStrictRecovery;
    }

    private static bool IsV4MixedScreenShareEnabled()
    {
        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(V4MixedScreenShareEnvironmentVariableName, category: "filetransfer_tuning");
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        value = value.Trim();
        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldAllowV4DuringScreenShare()
        => IsV4MixedScreenShareEnabled();

    private bool IsV4MixedScreenShareActive()
        => ShouldAllowV4DuringScreenShare() && (sessionScreenShareActive || sessionScreenShareDegraded || sessionScreenShareObserved);

    private bool IsV4MixedScreenShareTransferActiveLocked()
    {
        if (!IsV4MixedScreenShareActive())
        {
            return false;
        }

        return IsActiveV4MixedOutboundTransferLocked(outboundTransfer) ||
               IsActiveV4MixedInboundTransferLocked(inboundTransfer);
    }

    private static bool IsActiveV4MixedOutboundTransferLocked(OutboundTransferContext? context)
        => context is not null &&
           !context.IsTerminal &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
           (context.V4MixedScreenShareTransfer ||
            context.State is FileTransferTransferState.PreparingMetadata
                or FileTransferTransferState.AwaitingStart
                or FileTransferTransferState.Sending
                or FileTransferTransferState.AwaitingCompletion);

    private static bool IsActiveV4MixedInboundTransferLocked(InboundTransferContext? context)
        => context is not null &&
           !context.IsTerminal &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
           (context.V4MixedScreenShareTransfer ||
            context.State is FileTransferTransferState.AwaitingMetadata
                or FileTransferTransferState.Receiving
                or FileTransferTransferState.Verifying);

    private void LogV4MixedScreenShareEnabled(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        FileTransferRouteSelection routeSelection)
    {
        if (routeSelection.FrameFamily != FileTransferFrameFamily.V4)
        {
            return;
        }

        if (!IsV4MixedScreenShareActive())
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_mixed_screenshare_enabled; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; screen_share_active={(sessionScreenShareActive ? 1 : 0)}; screen_share_degraded={(sessionScreenShareDegraded ? 1 : 0)}; screen_share_observed={(sessionScreenShareObserved ? 1 : 0)}; screen_share_policy_hint=catch_up_only; credit_window_chunks={ResolveV4StateCreditWindowChunksForCurrentMode()}; normal_batch_segments={ResolveV4MaxBatchSegments(repairSend: false)}");
    }

    private static FileTransferTransportProfileKind ResolveTransportProfileKind(IFileTransferSignalingTransport? currentTransport)
        => currentTransport is IFileTransferTransportProfileProvider transportProfileProvider
            ? transportProfileProvider.FileTransferTransportProfileKind
            : FileTransferTransportProfileKind.Default;

    private static bool UsesConservativeNknStartup(
        IFileTransferSignalingTransport? currentTransport,
        int negotiatedDataProtocolVersion)
        => false;

    private FileTransferPayloadEfficiencyProfileSelection ResolvePayloadEfficiencyProfileSelectionLocked(OutboundTransferContext context)
    {
        return new FileTransferPayloadEfficiencyProfileSelection(
            FileTransferPayloadEfficiencyProfile.Current,
            "v4_only_forced_current");
    }

    private static void LogPayloadEfficiencyProfileSelected(OutboundTransferContext context)
    {
        var profile = context.PayloadEfficiencyProfile;
        LocalOperationalLog.Info(
            "FileTransferService",
            string.Format(
                CultureInfo.InvariantCulture,
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id={0}; session_id={1}; profile={2}; chunk_size={3}; chunk_size_bytes={3}; max_batch_chunks={4}; target_raw_batch_bytes={5}; reason={6}",
                context.TransferId,
                context.SessionId,
                profile.Name,
                context.ChunkSizeBytes,
                profile.MaxBatchChunkCount,
                profile.TargetBatchRawBytes,
                context.PayloadEfficiencyProfileSelectionReason));
    }

    private static void Warn(string message)
    {
        LocalOperationalLog.Warn("FileTransferService", $"event=warning; message={message}");
    }

    private static void LogLegacyNegotiationRejected(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        int? offeredVersion,
        int? acceptedVersion,
        string reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_legacy_negotiation_rejected; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; offered_version={FormatProtocolLogValue(offeredVersion)}; accepted_version={FormatProtocolLogValue(acceptedVersion)}; reason={reason}");
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_required_transport_incompatible; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; required_protocol_version={FileTransferProtocol.ProtocolVersionV6}; offered_version={FormatProtocolLogValue(offeredVersion)}; accepted_version={FormatProtocolLogValue(acceptedVersion)}; reason={reason}");
    }

    private static void LogV6RequiredTransportIncompatible(string transferId, string sessionId)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_required_transport_incompatible; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; required_protocol_version={FileTransferProtocol.ProtocolVersionV6}");
    }

    private static void LogV6Negotiated(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        FileTransferRouteSelection routeSelection,
        int liveRouteEpochId = 0)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_negotiated; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; route={routeSelection.TelemetryToken}; runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(routeSelection.BridgeRecoveryPolicy)}; selection_reason={FormatProtocolLogValue(routeSelection.SelectionReason)}{FormatLiveRouteEpochSuffix(liveRouteEpochId)}");
        LogProtocolNegotiated(transferId, sessionId, direction, routeSelection, liveRouteEpochId);
    }

    private static void LogV4Negotiated(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        FileTransferRouteSelection routeSelection,
        int liveRouteEpochId = 0)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_negotiated; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; protocol_version={FileTransferProtocol.ProtocolVersionV4}; route={routeSelection.TelemetryToken}; activation=primary_regular_nkn; runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(routeSelection.BridgeRecoveryPolicy)}; selection_reason={FormatProtocolLogValue(routeSelection.SelectionReason)}{FormatLiveRouteEpochSuffix(liveRouteEpochId)}");
        LogProtocolNegotiated(transferId, sessionId, direction, routeSelection, liveRouteEpochId);
    }

    private static void LogProtocolNegotiated(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        FileTransferRouteSelection routeSelection,
        int liveRouteEpochId = 0)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_protocol_negotiated; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; route={routeSelection.TelemetryToken}; protocol_version={routeSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(routeSelection.BridgeRecoveryPolicy)}; selection_reason={FormatProtocolLogValue(routeSelection.SelectionReason)}{FormatLiveRouteEpochSuffix(liveRouteEpochId)}");
    }

    private static void LogFileTransferRuntimeStarted(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        string runtimeRole,
        FileTransferRouteSelection routeSelection,
        int liveRouteEpochId = 0,
        RoutePublicationToken? routePublicationToken = null,
        string? coordinatorDecisionReason = null)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_started; direction={direction.ToString().ToLowerInvariant()}; role={FormatProtocolLogValue(runtimeRole)}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; route={routeSelection.TelemetryToken}; protocol_version={routeSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; handoff_kind={FormatFileTransferTransportHandoffKind(routeSelection.HandoffKind)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(routeSelection.BridgeRecoveryPolicy)}; liveness_terminal_policy={FormatFileTransferRouteLivenessTerminalPolicy(routeSelection.LivenessTerminalPolicy)}; selection_reason={FormatProtocolLogValue(routeSelection.SelectionReason)}{FormatPublicationLiveRouteEpochSuffix(routePublicationToken, liveRouteEpochId)}{FormatRoutePublicationAuthoritySuffix(routePublicationToken, coordinatorDecisionReason)}");
    }

    private static void LogV6SessionOpenRejected(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        int protocolVersion,
        string reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_session_open_rejected; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; protocol_version={protocolVersion}; reason={reason}");
    }

    private static string FormatProtocolLogValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();

    private static string FormatProtocolLogValue(int? value)
        => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)";

    private static string FormatFileTransferRouteRuntimeProfile(FileTransferRouteRuntimeProfile profile)
        => profile switch
        {
            FileTransferRouteRuntimeProfile.PrimaryRegularNknBulkV6 => "primary_regular_nkn_bulk_v6",
            FileTransferRouteRuntimeProfile.DefaultV6 => "default_v6",
            FileTransferRouteRuntimeProfile.FileTunaV4Fast => "file_tuna_v4_fast",
            _ => "regular_nkn_v4_fast",
        };

    private static string FormatFileTransferFrameFamily(FileTransferFrameFamily frameFamily)
        => frameFamily switch
        {
            FileTransferFrameFamily.V6 => "v6",
            _ => "v4",
        };

    private static string FormatFileTransferRouteBridgeRecoveryPolicy(FileTransferRouteBridgeRecoveryPolicy policy)
        => policy switch
        {
            FileTransferRouteBridgeRecoveryPolicy.PrimaryRegularNknQuietRecovery => "primary_regular_nkn_quiet",
            FileTransferRouteBridgeRecoveryPolicy.PostTunaFallbackStrictRecovery => "post_tuna_fallback_strict",
            FileTransferRouteBridgeRecoveryPolicy.RegularNknV4Fast => "regular_nkn_v4_fast",
            _ => "tuna_strict",
        };

    private static string FormatFileTransferRouteLivenessTerminalPolicy(FileTransferRouteLivenessTerminalPolicy policy)
        => policy switch
        {
            FileTransferRouteLivenessTerminalPolicy.PostTunaFallbackV6Repair => "post_tuna_fallback_v6_repair",
            FileTransferRouteLivenessTerminalPolicy.DiagnosticRegularNknV6 => "diagnostic_regular_nkn_v6",
            FileTransferRouteLivenessTerminalPolicy.FileTunaV4Fast => "file_tuna_v4_fast",
            _ => "regular_nkn_v4_fast",
        };

    private static string FormatFileTransferTransportProfileKind(FileTransferTransportProfileKind profileKind)
        => profileKind switch
        {
            FileTransferTransportProfileKind.ConservativeNknStartup => "conservative_nkn_startup",
            _ => "default",
        };

    private static string FormatFileTransferSessionOpenReason(
        string sessionRole,
        int protocolVersion,
        int chunkSizeBytes,
        int pipelineDepth,
        FileTransferRouteSelection routeSelection)
        => $"role={sessionRole}; protocol_version={protocolVersion}; route={routeSelection.TelemetryToken}; runtime_profile={FormatFileTransferRouteRuntimeProfile(routeSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(routeSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(routeSelection.BridgeRecoveryPolicy)}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}";

    private static void LogRepairChunkEvent(
        string eventName,
        string transferId,
        string sessionId,
        int chunkIndex,
        int rangeStartChunkIndex,
        int rangeEndChunkExclusive,
        int remoteNextExpectedChunkIndex,
        int remoteGrantedUntilExclusive,
        int batchSize,
        int pendingBatchCount)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; transfer_id={transferId}; session_id={sessionId}; chunk_index={chunkIndex}; range_start={rangeStartChunkIndex}; range_end_exclusive={rangeEndChunkExclusive}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; batch_size={batchSize}; pending_batch_count={pendingBatchCount}");
    }

    private static void LogRepairModeBatchWait(
        string transferId,
        string sessionId,
        int rangeStartChunkIndex,
        int rangeEndChunkExclusive,
        int remoteNextExpectedChunkIndex,
        int remoteGrantedUntilExclusive,
        int pendingBatchCount)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=repair_mode_batch_wait; transfer_id={transferId}; session_id={sessionId}; range_start={rangeStartChunkIndex}; range_end_exclusive={rangeEndChunkExclusive}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; pending_batch_count={pendingBatchCount}");
    }

    private static void LogRepairOnlyModeEvent(
        string eventName,
        string transferId,
        string sessionId,
        int nextChunkToRead,
        int remoteNextExpectedChunkIndex,
        int remoteGrantedUntilExclusive)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; transfer_id={transferId}; session_id={sessionId}; next_chunk_to_read={nextChunkToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}");
    }

    private static void LogWindowExtensionDeferredDueToGap(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_extension_deferred_due_to_gap; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}");
    }

    private static void LogWindowStartupCompleted(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        long bytesReceived)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_startup_completed; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; bytes_received={bytesReceived}");
    }

    private static void LogBulkUnhealthyDetected(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive,
        long bulkDispatchedSinceProgress,
        long obsoleteChunksSinceProgress,
        int obsoleteChunkCountRecent,
        int missingRangeCountRecent,
        int gapProgressAckCountRecent,
        int contiguousProgressChunksRecent)
    {
        var obsoleteChunkArrivalRatio = bulkDispatchedSinceProgress <= 0
            ? 0D
            : (double)obsoleteChunksSinceProgress / bulkDispatchedSinceProgress;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_bulk_unhealthy_detected; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}; bulk_dispatched_since_progress={bulkDispatchedSinceProgress}; obsolete_chunks_since_progress={obsoleteChunksSinceProgress}; obsolete_chunk_count_recent={obsoleteChunkCountRecent}; obsolete_chunk_arrival_ratio={obsoleteChunkArrivalRatio:F3}; missing_range_count_recent={missingRangeCountRecent}; gap_progress_ack_count_recent={gapProgressAckCountRecent}; contiguous_progress_chunks_recent={contiguousProgressChunksRecent}");
    }

    private static void LogBulkHealthyResumed(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bulk_healthy_resumed; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}");
    }

    private static void LogBulkFallbackEntered(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_bulk_fallback_entered; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}; grant_chunks={BulkFallbackGrantChunks}; low_watermark_chunks={BulkFallbackLowWatermarkChunks}");
    }

    private static void LogBulkFallbackExited(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bulk_fallback_exited; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}");
    }

    private static void LogTransferInfo(
        string eventName,
        FileTransferDirection direction,
        string transferId,
        string? sessionId = null,
        string? errorCode = null,
        string? reason = null,
        string? fileName = null,
        long? fileSizeBytes = null,
        long? bytesTransferred = null,
        int? chunksTransferred = null,
        int? chunkCount = null,
        string? savedPath = null,
        FileTransferRouteSelection? routeSelection = null)
    {
        var routeFields = routeSelection is { } selection
            ? $"; route={selection.TelemetryToken}; protocol_version={selection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(selection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(selection.FrameFamily)}; handoff_kind={FormatFileTransferTransportHandoffKind(selection.HandoffKind)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(selection.BridgeRecoveryPolicy)}; liveness_terminal_policy={FormatFileTransferRouteLivenessTerminalPolicy(selection.LivenessTerminalPolicy)}; selection_reason={FormatProtocolLogValue(selection.SelectionReason)}"
            : string.Empty;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId ?? "(none)"}; file_name_len={(string.IsNullOrWhiteSpace(fileName) ? 0 : fileName.Trim().Length)}; file_size_bytes={(fileSizeBytes?.ToString() ?? "(none)")}; bytes_transferred={(bytesTransferred?.ToString() ?? "(none)")}; chunks_transferred={(chunksTransferred?.ToString() ?? "(none)")}; chunk_count={(chunkCount?.ToString() ?? "(none)")}; error_code={errorCode ?? "(none)"}; reason={reason ?? "(none)"}; saved_path={savedPath ?? "(none)"}{routeFields}");
    }

    private static void LogTransportPaused(FileTransferDirection direction, string transferId, string sessionId, string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_paused; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; reason={reason}");

    private static void LogTransportResumed(FileTransferDirection direction, string transferId, string sessionId, string reason, bool requiresResumeRequest)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_resumed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; reason={reason}; requires_resume_request={(requiresResumeRequest ? "yes" : "no")}");

    private static void MaybeLogProgressMilestone(OutboundTransferContext context, FileTransferDirection direction)
    {
        var nextProgressMilestonePercent = context.NextProgressMilestonePercent;
        MaybeLogProgressMilestone(
            direction,
            context.TransferId,
            context.SessionId,
            context.FileName,
            context.FileSizeBytes,
            context.BytesTransferred,
            context.ChunksTransferred,
            context.ChunkCount,
            ref nextProgressMilestonePercent);
        context.NextProgressMilestonePercent = nextProgressMilestonePercent;
    }

    private static void MaybeLogProgressMilestone(InboundTransferContext context, FileTransferDirection direction)
    {
        var nextProgressMilestonePercent = context.NextProgressMilestonePercent;
        MaybeLogProgressMilestone(
            direction,
            context.TransferId,
            context.SessionId,
            context.FileName,
            context.FileSizeBytes,
            context.BytesTransferred,
            context.ChunksTransferred,
            context.ChunkCount,
            ref nextProgressMilestonePercent);
        context.NextProgressMilestonePercent = nextProgressMilestonePercent;
    }

    private static void MaybeLogProgressMilestone(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string fileName,
        long fileSizeBytes,
        long bytesTransferred,
        int chunksTransferred,
        int chunkCount,
        ref int nextProgressMilestonePercent)
    {
        if (fileSizeBytes <= 0 || bytesTransferred <= 0 || nextProgressMilestonePercent > 100)
        {
            return;
        }

        var percentComplete = (int)Math.Floor((double)bytesTransferred * 100d / fileSizeBytes);
        if (percentComplete < nextProgressMilestonePercent)
        {
            return;
        }

        LogTransferInfo(
            "progress_milestone",
            direction,
            transferId,
            sessionId: sessionId,
            fileName: fileName,
            fileSizeBytes: fileSizeBytes,
            bytesTransferred: bytesTransferred,
            chunksTransferred: chunksTransferred,
            chunkCount: chunkCount,
            reason: $"percent={Math.Min(percentComplete, 100)}");

        while (nextProgressMilestonePercent <= percentComplete)
        {
            nextProgressMilestonePercent += 25;
        }
    }

    private enum FileTransferRuntimeProfile
    {
        Default,
        PrimaryRegularNknBulkV6,
    }

    private enum FileTransferBridgeRecoveryPolicy
    {
        RegularNknV4Fast,
        TunaStrictRecovery,
        PostTunaFallbackStrictRecovery,
        PrimaryRegularNknQuietRecovery,
    }

    private enum FileTransferSparseCreditRuntimeKind
    {
        PrimaryRegularNknV4,
        PrimaryRegularNknBulkV6,
    }

    private enum PrimaryRegularNknBulkV6State
    {
        Opening,
        ManifestExchange,
        AwaitingManifest,
        CreditGranted,
        SendingBulk,
        ReceivingBulk,
        AwaitingReceiverState,
        StateRefreshRequested,
        CheckpointSyncRequested,
        Rebinding,
        RebindConfirmed,
        Finalizing,
        Completed,
        Failed,
        Cancelled,
    }

    private readonly record struct FileTransferRuntimeProfileSelection(
        FileTransferRouteRuntimeDescriptor RouteRuntime,
        FileTransferRuntimeProfile Profile,
        FileTransferBridgeRecoveryPolicy BridgeRecoveryPolicy,
        string Reason)
    {
        public static FileTransferRuntimeProfileSelection Default(string reason)
            => FromRouteRuntime(
                FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast).RuntimeDescriptor,
                reason);

        public static FileTransferRuntimeProfileSelection RegularNknV4Fast(string reason)
            => FromRouteRuntime(
                FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast).RuntimeDescriptor,
                reason);

        public static FileTransferRuntimeProfileSelection FileTunaV4Fast(string reason)
            => FromRouteRuntime(
                FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4).RuntimeDescriptor,
                reason);

        public static FileTransferRuntimeProfileSelection PostTunaFallback(string reason)
            => FromRouteRuntime(
                FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6).RuntimeDescriptor,
                reason);

        public static FileTransferRuntimeProfileSelection PrimaryRegularNknBulkV6(string reason)
            => FromRouteRuntime(
                FileTransferRouteResolver.Resolve(FileTransferRoute.DiagnosticRegularNknV6).RuntimeDescriptor,
                reason);

        public static FileTransferRuntimeProfileSelection FromRouteSelection(FileTransferRouteSelection routeSelection)
            => FromRouteRuntime(routeSelection.RuntimeDescriptor, routeSelection.SelectionReason);

        private static FileTransferRuntimeProfileSelection FromRouteRuntime(
            FileTransferRouteRuntimeDescriptor routeRuntime,
            string reason)
            => new(
                routeRuntime,
                routeRuntime.UsesV6SparsePump
                    ? FileTransferRuntimeProfile.PrimaryRegularNknBulkV6
                    : FileTransferRuntimeProfile.Default,
                routeRuntime.BridgeRecoveryPolicy switch
                {
                    FileTransferRouteBridgeRecoveryPolicy.RegularNknV4Fast =>
                        FileTransferBridgeRecoveryPolicy.RegularNknV4Fast,
                    FileTransferRouteBridgeRecoveryPolicy.PostTunaFallbackStrictRecovery =>
                        FileTransferBridgeRecoveryPolicy.PostTunaFallbackStrictRecovery,
                    FileTransferRouteBridgeRecoveryPolicy.PrimaryRegularNknQuietRecovery =>
                        FileTransferBridgeRecoveryPolicy.PrimaryRegularNknQuietRecovery,
                    _ => FileTransferBridgeRecoveryPolicy.TunaStrictRecovery,
                },
                reason);

    }

    private static string FormatFileTransferRuntimeProfile(FileTransferRuntimeProfile profile)
        => profile switch
        {
            FileTransferRuntimeProfile.PrimaryRegularNknBulkV6 => "PrimaryRegularNknBulkV6",
            _ => "Default",
        };

    private static string FormatFileTransferBridgeRecoveryPolicy(FileTransferBridgeRecoveryPolicy policy)
        => policy switch
        {
            FileTransferBridgeRecoveryPolicy.PrimaryRegularNknQuietRecovery => "primary_regular_nkn_quiet",
            FileTransferBridgeRecoveryPolicy.PostTunaFallbackStrictRecovery => "post_tuna_fallback_strict",
            FileTransferBridgeRecoveryPolicy.RegularNknV4Fast => "regular_nkn_v4_fast",
            _ => "tuna_strict",
        };

    private sealed record ActiveTransferSessionEndNotice(
        FileTransferDirection Direction,
        string SessionId,
        string TransferId,
        IFileTransferDataSession? DataSession,
        int ProtocolVersion,
        long TransportEpoch);

    private sealed record RuntimeUnlockPeerPreCommitProbeState(
        string SessionId,
        string TransferId,
        long TransactionGeneration,
        long OfferGeneration,
        long TunaPathLeaseGeneration,
        string ProbeId,
        DateTimeOffset CreatedUtc);

    private sealed class OutboundTransferContext
    {
        private TaskCompletionSource<bool> controlSignal = CreateSignal();

        private readonly List<CancellationTokenSource> retiredV6SenderPipelineCts = [];

        public FileTransferSendDescriptor Descriptor { get; }

        public FileTransferReadStreamFactory OpenReadStreamAsync { get; }

        public CancellationTokenSource LifetimeCts { get; } = new();

        public CancellationTokenSource V6SenderPipelineCts { get; private set; } = new();

        public long V6SenderPipelineGeneration { get; private set; }

        public string SessionId { get; set; } = string.Empty;

        public string TransferId => Descriptor.TransferId!;

        public string FileName => Descriptor.FileName;

        public long FileSizeBytes => Descriptor.FileSizeBytes;

        public int ChunkSizeBytes { get; set; }

        public int ChunkCount { get; set; }

        public string? Sha256Base64 { get; set; }

        public int OfferedDataProtocolVersion { get; set; } = FileTransferProtocol.ProtocolVersionV6;

        public int NegotiatedDataProtocolVersion { get; set; } = FileTransferProtocol.ProtocolVersionV6;

        public FileTransferRouteSelection RouteSelection { get; set; } =
            FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);

        public FileTransferRouteRuntimeDescriptor RouteRuntime { get; set; } =
            FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast).RuntimeDescriptor;

        public LiveRouteEpoch? CurrentLiveRouteEpoch { get; set; }

        public int LastLiveRouteEpochId { get; set; }

        public FileTransferLeg? CurrentTransferLeg { get; set; }

        public List<FileTransferLeg> TransferLegHistory { get; } = [];

        public int LastTransferLegGeneration { get; set; }

        public int LastRuntimeOwnerGeneration { get; set; }

        public int TerminalGeneration { get; set; }

        public RuntimeOwnerToken? CurrentRuntimeOwner { get; set; }

        public RuntimeUnlockFallbackSurvivalState RuntimeUnlockFallbackSurvivalState { get; set; }

        public RuntimeUnlockAdmissionToken? RuntimeUnlockAdmissionToken { get; set; }

        public int LastRuntimeUnlockAdmissionGeneration { get; set; }

        public RouteCommitToken? RuntimeUnlockRouteCommitToken { get; set; }

        public int LastRuntimeUnlockRouteCommitGeneration { get; set; }

        public RoutePublicationToken? CurrentRoutePublicationToken { get; set; }

        public int LastRoutePublicationGeneration { get; set; }

        public bool RuntimeUnlockWaitingForFallbackSurvival { get; set; }

        public string? RuntimeUnlockWaitingForFallbackSurvivalReason { get; set; }

        public DateTimeOffset? RuntimeUnlockWaitingForFallbackSurvivalUtc { get; set; }

        public bool RuntimeUnlockActivationWindowGranted { get; set; }

        public string? RuntimeUnlockActivationWindowReason { get; set; }

        public DateTimeOffset? RuntimeUnlockActivationWindowGrantedUtc { get; set; }

        public RuntimeUnlockPeerPreCommitProbeState? RuntimeUnlockPendingPeerPreCommitProbe { get; set; }

        public FileTransferRuntimeProfile RuntimeProfile { get; set; }

        public FileTransferBridgeRecoveryPolicy BridgeRecoveryPolicy { get; set; } =
            FileTransferBridgeRecoveryPolicy.TunaStrictRecovery;

        public long BytesTransferred { get; set; }

        public int ChunksTransferred { get; set; }

        public long BytesAcceptedForTransport { get; set; }

        public int ChunksAcceptedForTransport { get; set; }

        public long BytesAcknowledgedByReceiver { get; set; }

        public FileTransferTransferState State { get; set; } = FileTransferTransferState.Offering;

        public string? ErrorCode { get; set; }

        public string? StatusMessage { get; set; } = "Preparing transfer offer.";

        public bool SendStarted { get; set; }

        public int NextProgressMilestonePercent { get; set; } = 25;

        public int RemoteNextExpectedChunkIndex { get; set; }

        public int RemoteGrantedUntilExclusive { get; set; }

        public int CurrentRepairBatchSize { get; set; } = RepairBatchSize;

        public DateTimeOffset? LastSendAheadClampLogUtc { get; set; }

        public IFileTransferDataSession? DataSession { get; set; }

        public bool PullSessionActive { get; set; }

        public bool PullSessionDegraded { get; set; }

        public int PullCurrentPipelineDepth { get; set; }

        public SortedSet<int> RequestedButUnsent { get; } = [];

        public SortedSet<int> GrantedOutstandingChunks { get; } = [];

        public Dictionary<int, DateTimeOffset> SentAwaitingAck { get; } = new();

        public Dictionary<int, DateTimeOffset> V6ChunkSendsInFlight { get; } = new();

        public Dictionary<int, DateTimeOffset> LastChunkSentUtc { get; } = new();

        public Dictionary<int, DateTimeOffset> LastChunkResentUtc { get; } = new();

        public Dictionary<int, int> ChunkResendCountSinceAck { get; } = new();

        public Dictionary<int, byte[]> PullSentChunkCache { get; } = new();

        public bool PullSourceCanSeek { get; set; }

        public long PullSentChunkCacheBytes { get; set; }

        public bool PullSenderCachePressureActive { get; set; }

        public bool PullSenderCachePressureEnterLogged { get; set; }

        public DateTimeOffset? PullSenderCachePressureLastWarnUtc { get; set; }

        public int PullSenderCachePressureLastWarnAcceptedChunks { get; set; }

        public int PullSenderCachePressureSuppressedCount { get; set; }

        public int PullV4GrantedUntilExclusive { get; set; }

        public DateTimeOffset? PullV4LastGrantReceivedUtc { get; set; }

        public DateTimeOffset? PullV4LastPeerFrameReceivedUtc { get; set; }

        public DateTimeOffset? PullV4PeerSilenceDeferralUtc { get; set; }

        public int PullV4PeerSilenceDeferralCount { get; set; }

        public bool V6HeartbeatLoopStarted { get; set; }

        public long V6HeartbeatSequence { get; set; }

        public DateTimeOffset? V6LastPeerLivenessUtc { get; set; }

        public int V6EpochLivenessDeferralCount { get; set; }

        public DateTimeOffset? V6EpochLivenessDeferralUtc { get; set; }

        public int V6PeerLivenessRecoveryDeferralCount { get; set; }

        public DateTimeOffset? V6PeerLivenessRecoveryDeferredUtc { get; set; }

        public DateTimeOffset? V6LastReceiveRecoveryRequestedUtc { get; set; }

        public DateTimeOffset? V6LastFeedbackStallRecoverySuppressedUtc { get; set; }

        public DateTimeOffset? V6RegularNknLastStateRefreshRequestedUtc { get; set; }

        public long V6RegularNknStateRefreshSequence { get; set; }

        public int V6RegularNknStateRefreshSendInFlight { get; set; }

        public long V6RegularNknStateRefreshSendGeneration { get; set; }

        public long V6RegularNknStateRefreshActiveSendGeneration { get; set; }

        public string? V6RegularNknStateRefreshActiveRequestId { get; set; }

        public string? V6RegularNknStateRefreshActivePriority { get; set; }

        public DateTimeOffset? V6RegularNknStateRefreshActiveStartedUtc { get; set; }

        public bool V6RegularNknStateRefreshActiveReusedPendingCheckpoint { get; set; }

        public long V6RegularNknStateRefreshRetiredSendGeneration { get; set; }

        public string? V6RegularNknStateRefreshRetiredRequestId { get; set; }

        public int V6RegularNknStateRefreshRetiredSendCount { get; set; }

        public int V6RegularNknStateRefreshFailureCount { get; set; }

        public bool V6RegularNknFallbackCheckpointDeliveryRecoveryPending { get; set; }

        public DateTimeOffset? V6RegularNknFallbackCheckpointDeliveryRecoveryStartedUtc { get; set; }

        public string? V6RegularNknFallbackCheckpointDeliveryRecoveryReason { get; set; }

        public string? V6RegularNknFallbackCheckpointDeliveryRecoveryRequestId { get; set; }

        public int PostTunaFallbackLiveCheckpointStallCommittedChunkIndex { get; set; } = -1;

        public int PostTunaFallbackLiveCheckpointStallHighestObservedChunkIndex { get; set; } = -1;

        public int PostTunaFallbackLiveCheckpointStallCount { get; set; }

        public string? FallbackTailQuiescenceTuple { get; set; }

        public int FallbackTailQuiescenceTupleRepeatCount { get; set; }

        public DateTimeOffset? FallbackTailQuiescenceTupleFirstSeenUtc { get; set; }

        public DateTimeOffset? FallbackTailQuiescenceTupleLastDiagnosticUtc { get; set; }

        public int FallbackTailQuiescenceSuppressedDiagnosticCount { get; set; }

        public bool FallbackTailQuiescenceTimedOut { get; set; }

        public bool FallbackTailTerminalProofConsumedLogged { get; set; }

        public string? RetiredFallbackTailOwnerKey { get; set; }

        public string? LocalFallbackTailYieldOwnerKey { get; set; }

        public string? PeerFallbackTailYieldOwnerKey { get; set; }

        public long FallbackTailYieldGeneration { get; set; }

        public bool LocalFallbackTailYieldGranted { get; set; }

        public bool LocalFallbackTailYieldPeerVisible { get; set; }

        public bool PeerFallbackTailYieldGranted { get; set; }

        public bool PairedFallbackTailYieldGranted { get; set; }

        public string? FallbackTailYieldMismatchReason { get; set; }

        public HashSet<string> FallbackTailYieldProofSuppressionKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RetiredFallbackTailOwnerSuppressionKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FallbackStaleProofSuppressionKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RetiredFallbackControlPlaneSendKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RetiredFallbackCheckpointRequestKeys { get; } = new(StringComparer.Ordinal);

        public FallbackControlPlaneToken? CurrentFallbackControlPlaneToken { get; set; }

        public int LastFallbackControlPlaneTokenGeneration { get; set; }

        public FallbackCheckpointLease? CurrentFallbackCheckpointLease { get; set; }

        public int LastFallbackCheckpointLeaseGeneration { get; set; }

        public FallbackControlPlanePressureToken? CurrentFallbackControlPlanePressureToken { get; set; }

        public int LastFallbackControlPlanePressureGeneration { get; set; }

        public FallbackTailYieldDecision? LastFallbackTailYieldDecision { get; set; }

        public int LastFallbackTailYieldGeneration { get; set; }

        public RepairReplayDecision? CurrentRepairReplayDecision { get; set; }

        public int LastRepairReplayGeneration { get; set; }

        public TransportEpochDecision? CurrentTransportEpochDecision { get; set; }

        public int LastTransportEpochDecisionGeneration { get; set; }

        public BridgeRecoveryDecision? CurrentBridgeRecoveryDecision { get; set; }

        public int LastBridgeRecoveryDecisionGeneration { get; set; }

        public RecoveryLivenessLease? CurrentRecoveryLivenessLease { get; set; }

        public int LastRecoveryLivenessLeaseGeneration { get; set; }

        public string? LastOutboundFallbackCheckpointRepairRequestId { get; set; }

        public long LastOutboundFallbackCheckpointRepairTransportEpoch { get; set; }

        public int LastOutboundFallbackCheckpointRepairLegGeneration { get; set; }

        public int LastOutboundFallbackCheckpointRepairLiveRouteEpoch { get; set; }

        public DateTimeOffset? LastOutboundFallbackCheckpointRepairAcceptedUtc { get; set; }

        public FileTransferFrontierRequestFrameV6? V6RegularNknDeferredStateRefreshRequest { get; set; }

        public string? V6RegularNknDeferredStateRefreshReason { get; set; }

        public DateTimeOffset? V6RegularNknDeferredStateRefreshCreatedUtc { get; set; }

        public int V6RegularNknDeferredStateRefreshReplaceCount { get; set; }

        public DateTimeOffset? V6RegularNknLastCheckpointSyncRequestedUtc { get; set; }

        public long V6RegularNknCheckpointSyncSequence { get; set; }

        public int V6RegularNknCheckpointSyncSendInFlight { get; set; }

        public int V6RegularNknCheckpointSyncFailureCount { get; set; }

        public bool PullV4ExpandedWindowActive { get; set; }

        public bool PullV4LimitedWindowActive { get; set; }

        public DateTimeOffset? PullV4CleanSinceUtc { get; set; }

        public DateTimeOffset? PullV4AdverseSinceUtc { get; set; }

        public bool PullTransportPaused { get; set; }

        public DateTimeOffset? PullTransportPausedSinceUtc { get; set; }

        public DateTimeOffset? PullTransportGraceDeadlineUtc { get; set; }

        public string? PullTransportPauseReason { get; set; }

        public string? PullTransportLastPauseReason { get; set; }

        public string? PullTransportLastResumeReason { get; set; }

        public DateTimeOffset? PullTransportLastResumeUtc { get; set; }

        public bool PullTransportResumeRequestPending { get; set; }

        public int PullTransportRebindGeneration { get; set; }

        public int TunaActivationBarrierStateSendSuppressedCount { get; set; }

        public long LastRecoveredV6TransportHandoffEpoch { get; set; }

        public long LastRecoveredV6TransportEpoch { get; set; }

        public int LastRecoveredV6TransportLiveRouteEpochId { get; set; }

        public FileTransferTransportHandoffKind LastRecoveredV6TransportEpochKind { get; set; }

        public FileTransferTransportKind LastRecoveredV6TransportTargetTransport { get; set; }

        public V6TransportEpoch? V6TransportEpoch { get; set; }

        public long V6TransportEpochReplayLoopEpochId { get; set; }

        public int V6LastReceiverStateEpoch { get; set; } = -1;

        public DateTimeOffset? V6LastReceiverFeedbackReceivedUtc { get; set; }

        public int V6NextSequentialSourceChunkIndex { get; set; }

        public SortedSet<int> V6PriorityRequestedChunks { get; } = [];

        public SortedSet<int> V6NormalRequestedChunks { get; } = [];

        public Dictionary<int, V6OutboundChunkRequestMetadata> V6RequestedChunkMetadataByChunkIndex { get; } = new();

        public HashSet<string> V6AppliedFrontierRequestIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> V6PendingEpochRepairRequestIds { get; } = new(StringComparer.Ordinal);

        public string? V6CurrentNormalRequestKey { get; set; }

        public string V6SenderPumpLastWakeReason { get; set; } = "startup";

        public int V6RegularNknFrontierPressureStartChunkIndex { get; set; } = -1;

        public int V6RegularNknFrontierPressureUntilChunkIndex { get; set; } = -1;

        public DateTimeOffset? V6RegularNknFrontierPressureEnteredUtc { get; set; }

        public int V6RegularNknDegradedNoProgressReceiverStateCount { get; set; }

        public int V6RegularNknDegradedObservedChunkIndex { get; set; } = -1;

        public DateTimeOffset? V6RegularNknDegradedObservedUtc { get; set; }

        public int V6RegularNknDegradedStartChunkIndex { get; set; } = -1;

        public int V6RegularNknDegradedUntilChunkIndex { get; set; } = -1;

        public DateTimeOffset? V6RegularNknDegradedEnteredUtc { get; set; }

        public string? V6RegularNknDegradedReason { get; set; }

        public int V6RegularNknInferredFrontierObservedChunkIndex { get; set; } = -1;

        public DateTimeOffset? V6RegularNknInferredFrontierObservedUtc { get; set; }

        public int V6LastInferredRegularNknFrontierRepairChunkIndex { get; set; } = -1;

        public int V6LastInferredRegularNknFrontierRepairReceiverStateEpoch { get; set; } = -1;

        public DateTimeOffset? V6LastInferredRegularNknFrontierRepairUtc { get; set; }

        public string? V6LastInferredRegularNknFrontierRepairRequestId { get; set; }

        public DateTimeOffset? V6LastInferredRegularNknFrontierRepairSuppressedLogUtc { get; set; }

        public string? V6LastInferredRegularNknFrontierRepairSuppressedReason { get; set; }

        public bool V6UseRegularNknRedundantData { get; set; }

        public long V6TunaRedundantDataEpochId { get; set; }

        public long V6TunaRedundantDataSatisfiedEpochId { get; set; }

        public DateTimeOffset? V6TunaRedundantDataProbeStartedUtc { get; set; }

        public long V6TunaRedundantDataProbeStartedBytes { get; set; }

        public long V6RegularNknRedundantDataEpochId { get; set; }

        public long V6RegularNknRedundantDataDisabledEpochId { get; set; }

        public int V6RegularNknRedundantDataBatchCount { get; set; }

        public long V6PostTunaFallbackSenderSelfRepairSequence { get; set; }

        public int V6PostTunaFallbackSenderSelfRepairChunkIndex { get; set; } = -1;

        public DateTimeOffset? V6PostTunaFallbackSenderSelfRepairUtc { get; set; }

        public int V6PostTunaFallbackNormalSendAheadFreezeChunkIndex { get; set; } = -1;

        public DateTimeOffset? V6PostTunaFallbackNormalSendAheadFreezeUtc { get; set; }

        public bool PullPostTunaRecoveryActive { get; set; }

        public int PullPostTunaRecoveryGeneration { get; set; }

        public int PullPostTunaRecoveryFrontierChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullPostTunaRecoveryStartedUtc { get; set; }

        public int PullTransportLastSafetyReplayGeneration { get; set; }

        public int PullTransportLastSafetyReplayFrontierChunkIndex { get; set; } = -1;

        public int PullTransportLastSafetyReplayEndChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullTransportLastSafetyReplayUtc { get; set; }

        public int PullTransportSafetyReplayRearmCount { get; set; }

        public bool PullTransportFrontierOnlyRepairActive { get; set; }

        public int PullTransportFrontierOnlyRepairStartChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullTransportRebindStartedUtc { get; set; }

        public TransportHandoffEpoch? V6TransportHandoff { get; set; }

        public bool UserPaused { get; set; }

        public string? UserPauseReason { get; set; }

        public DateTimeOffset? UserPausedSinceUtc { get; set; }

        public bool PeerPaused { get; set; }

        public string? PeerPauseReason { get; set; }

        public DateTimeOffset? PeerPausedSinceUtc { get; set; }

        public Queue<DateTimeOffset> RecentPullChunkSentUtc { get; } = new();

        public int PullDuplicateRequestIgnoredCountRecent { get; set; }

        public int PullResendSuppressedCountRecent { get; set; }

        public long PullUsefulPayloadBytesRecent { get; set; }

        public DateTimeOffset? LastSenderThroughputLogUtc { get; set; }

        public DateTimeOffset? PullV4LastRegularNknControlFeedbackPressureObservedUtc { get; set; }

        public DateTimeOffset? PullRegularNknV4LastPeerSilenceSafetyReplayUtc { get; set; }

        public int PullRegularNknV4LastPeerSilenceSafetyReplayFrontierChunkIndex { get; set; } = -1;

        public int PullRegularNknV4LastPeerSilenceSafetyReplayEndChunkIndex { get; set; } = -1;

        public int PullRegularNknV4PeerSilenceSafetyReplayCount { get; set; }

        public int PullFileTunaV4PostTunaReactivationGeneration { get; set; }

        public DateTimeOffset? PullFileTunaV4PostTunaReactivationStartedUtc { get; set; }

        public DateTimeOffset? PullFileTunaV4PostTunaLastFrontierReplayUtc { get; set; }

        public int PullFileTunaV4PostTunaLastFrontierReplayChunkIndex { get; set; } = -1;

        public int PullFileTunaV4PostTunaLastFrontierReplayEndChunkIndex { get; set; } = -1;

        public int PullFileTunaV4PostTunaFrontierReplayCount { get; set; }

        public long PullSenderRawBytesRecent { get; set; }

        public long PullSenderRawBytesTotal { get; set; }

        public long PullSenderNormalRawBytesTotal { get; set; }

        public long PullSenderRepairRawBytesTotal { get; set; }

        public int PullSenderChunkFramesRecent { get; set; }

        public int PullSenderBatchFramesRecent { get; set; }

        public int PullSenderBatchFramesTotal { get; set; }

        public int PullSenderNormalBatchFramesTotal { get; set; }

        public int PullSenderRepairBatchFramesTotal { get; set; }

        public int PullSenderChunkCountRecent { get; set; }

        public int PullSenderChunkCountTotal { get; set; }

        public int PullSenderNormalChunkCountTotal { get; set; }

        public int PullSenderRepairChunkCountTotal { get; set; }

        public int PullSenderSendWaitCountRecent { get; set; }

        public int PullSenderSendWaitCountTotal { get; set; }

        public int PullSenderRepairSendCountRecent { get; set; }

        public int PullSenderPipelineFailedFramesTotal { get; set; }

        public int PullSenderCacheHitCountRecent { get; set; }

        public int PullSenderCacheMissCountRecent { get; set; }

        public int PullSenderSourceRereadCountRecent { get; set; }

        public int PullSenderCacheEvictionCountRecent { get; set; }

        public int PullSenderRepairChunkSkippedCountRecent { get; set; }

        public int PullSenderPipelineConfiguredDepthRecent { get; set; }

        public int PullSenderPipelineEffectiveDepthRecent { get; set; }

        public int PullSenderPipelineCurrentInFlightFrames { get; set; }

        public long PullSenderPipelineCurrentInFlightBytes { get; set; }

        public int PullSenderPipelineMaxInFlightFramesRecent { get; set; }

        public long PullSenderPipelineMaxInFlightBytesRecent { get; set; }

        public int PullSenderPipelineScheduledFramesRecent { get; set; }

        public int PullSenderPipelineCompletedFramesRecent { get; set; }

        public int PullSenderPipelineFailedFramesRecent { get; set; }

        public int PullSenderV4NormalScheduledFramesRecent { get; set; }

        public int PullSenderV4RepairScheduledFramesRecent { get; set; }

        public long PullSenderPipelineFifoWaitMsRecent { get; set; }

        public long PullSenderPipelineMaxFifoWaitMsRecent { get; set; }

        public long PullSenderPipelineMaxAcceptedProgressLagBytesRecent { get; set; }

        public int PullSenderFeedChunkFramesPreparedRecent { get; set; }

        public int PullSenderFeedBatchFramesPreparedRecent { get; set; }

        public int PullSenderFeedChunkCountPreparedRecent { get; set; }

        public long PullSenderFeedRawBytesPreparedRecent { get; set; }

        public long PullSenderFeedReadDurationMsRecent { get; set; }

        public long PullSenderFeedBatchPrepareDurationMsRecent { get; set; }

        public long PullSenderFeedScheduleDurationMsRecent { get; set; }

        public long PullSenderFeedCreditWaitMsRecent { get; set; }

        public long PullSenderFeedPipelineSlotWaitMsRecent { get; set; }

        public int PullSenderFeedSourceReadErrorCountRecent { get; set; }

        public DateTimeOffset? PullSenderFeedLastScheduleUtc { get; set; }

        public DateTimeOffset? PullSenderFeedCreditWaitStartedUtc { get; set; }

        public DateTimeOffset? PullV4LastCreditStallLogUtc { get; set; }

        public List<long> PullSenderFeedInterScheduleGapMsRecent { get; } = [];

        public Queue<PullV4QueuedRepairSend> PullV4SenderPumpRepairQueue { get; } = new();

        public HashSet<int> PullV4SenderPumpRepairQueuedChunkIndices { get; } = [];

        public Dictionary<string, V4SenderRepairRequestState> PullV4SenderPumpRepairRequests { get; } = new(StringComparer.Ordinal);

        public int V4LastStateEpoch { get; set; } = -1;

        public int PullV4StateReceivedCountTotal { get; set; }

        public int PullV4StateAppliedCountTotal { get; set; }

        public int PullV4StateDuplicateCountTotal { get; set; }

        public int PullV4StateStaleCountTotal { get; set; }

        public int V4PauseControlEpoch { get; set; }

        public int PeerV4LastPauseControlEpoch { get; set; } = -1;

        public bool V4TerminalReady { get; set; }

        public bool V4MixedScreenShareTransfer { get; set; }

        public string SparseSenderPumpLastWakeReason { get; set; } = "startup";

        public string V4SenderPumpLastRepairRequestKey { get; set; } = "(none)";

        public DateTimeOffset? V4SenderCreditExhaustedSinceUtc { get; set; }

        private TaskCompletionSource<bool> pullSparseSenderPumpSignal = CreateSignal();

        public Task ResetAndGetSparseSenderPumpSignalTask()
        {
            pullSparseSenderPumpSignal = CreateSignal();
            return pullSparseSenderPumpSignal.Task;
        }

        public void SignalSparseSenderPump()
        {
            pullSparseSenderPumpSignal.TrySetResult(true);
        }

        public FileTransferPayloadEfficiencyProfile PayloadEfficiencyProfile { get; set; } = FileTransferPayloadEfficiencyProfile.Current;

        public string PayloadEfficiencyProfileSelectionReason { get; set; } = "current_default";

        public bool IsTerminal => ToSnapshot().IsTerminal;

        public FileTransferTransferSnapshot ToSnapshot()
            => new(
                SessionId,
                TransferId,
                FileTransferDirection.Outbound,
                State,
                FileName,
                FileSizeBytes,
                Sha256Base64,
                BytesTransferred,
                ChunksTransferred,
                ChunkCount,
                ChunkSizeBytes,
                ErrorCode,
                StatusMessage,
                BytesAcceptedForTransport: BytesAcceptedForTransport,
                BytesAcknowledgedByReceiver: Math.Max(BytesAcknowledgedByReceiver, BytesTransferred),
                IsPaused: UserPaused,
                PauseReason: UserPauseReason,
                IsPeerPaused: PeerPaused,
                PeerPauseReason: PeerPauseReason,
                RouteToken: RouteSelection.TelemetryToken,
                ProtocolVersion: NegotiatedDataProtocolVersion);

        public OutboundTransferContext(FileTransferSendDescriptor descriptor, FileTransferReadStreamFactory openReadStreamAsync)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            OpenReadStreamAsync = openReadStreamAsync ?? throw new ArgumentNullException(nameof(openReadStreamAsync));
            ChunkSizeBytes = descriptor.ChunkSizeBytes ?? FileTransferProtocol.MaxChunkRawBytes;
        }

        public void CancelLifetime()
        {
            try
            {
                V6SenderPipelineCts.Cancel();
                LifetimeCts.Cancel();
            }
            catch
            {
            }
        }

        public long ResetV6SenderPipelineCancellation()
        {
            var previous = V6SenderPipelineCts;
            V6SenderPipelineCts = new CancellationTokenSource();
            V6SenderPipelineGeneration++;
            try
            {
                previous.Cancel();
            }
            catch
            {
            }

            retiredV6SenderPipelineCts.Add(previous);
            return V6SenderPipelineGeneration;
        }

        public void DisposeResources()
        {
            try
            {
                DataSession?.Dispose();
            }
            catch
            {
            }

            try
            {
                V6SenderPipelineCts.Cancel();
                V6SenderPipelineCts.Dispose();
                foreach (var retired in retiredV6SenderPipelineCts)
                {
                    retired.Dispose();
                }

                retiredV6SenderPipelineCts.Clear();
            }
            catch
            {
            }

            try
            {
                LifetimeCts.Dispose();
            }
            catch
            {
            }

            DataSession = null;
        }

        public IFileTransferDataSession? DetachDataSession()
        {
            var dataSession = DataSession;
            DataSession = null;
            return dataSession;
        }

        public Task ResetAndGetControlSignalTask()
        {
            controlSignal = CreateSignal();
            return controlSignal.Task;
        }

        public void SignalControlActivity()
        {
            controlSignal.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record InboundDispatchWork(
        SessionFileTransferService Service,
        string Lane,
        string Operation,
        Func<Task> Work);

    private sealed record PullV4QueuedRepairSend(
        List<int> ChunkIndices,
        int RangeCount,
        int RequestedChunkCount,
        int FirstStartChunkIndex,
        int LastEndChunkExclusive,
        int RemoteNextExpectedChunkIndex,
        int ChunksAcceptedForTransport,
        int SkippedObsoleteCount,
        int SkippedFutureCount,
        int SkippedOutOfBoundsCount,
        string RepairRequestKey,
        string? ProtocolRepairRequestId,
        string? ProtocolPriority,
        string? ProtocolRecoveryMode,
        bool FrontierTailRepair,
        bool EmergencyCreditRepair,
        FileTransferV4RepairDeliveryMode DeliveryMode,
        string DeliveryEscalationReason,
        long CreditExhaustedTimeMsAtDecision,
        RuntimeOwnerToken? RuntimeOwner = null);

    private sealed class V4SenderRepairRequestState
    {
        public bool Queued { get; set; }

        public DateTimeOffset? QueuedUtc { get; set; }

        public bool InFlight { get; set; }

        public DateTimeOffset? LastSentUtc { get; set; }

        public int SentCount { get; set; }

        public int LastSentRemoteFrontierChunkIndex { get; set; } = -1;

        public int SuppressedCount { get; set; }
    }

    private sealed class V4ReceiverRepairRequestState
    {
        public required string RepairRequestKey { get; init; }

        public required DateTimeOffset FirstSeenUtc { get; init; }

        public DateTimeOffset? LastRequestedUtc { get; set; }

        public DateTimeOffset? LastSuppressedLogUtc { get; set; }

        public int AttemptCount { get; set; }

        public int FirstStartChunkIndex { get; init; }

        public int LastEndChunkExclusive { get; init; }

        public int RequestedChunkCount { get; init; }

        public required IReadOnlyList<FileTransferRangeV4> Ranges { get; init; }

        public bool Filled { get; set; }

        public bool FrontierTailRepair { get; init; }
    }

    private enum V6ReceiveDestinationMode
    {
        Unknown = 0,
        SparseSeekable = 1,
        ContiguousOnly = 2,
    }

    private readonly record struct V6OutboundChunkRequestMetadata(
        string RequestKey,
        bool Priority,
        long TransportEpoch,
        string? RepairRequestId,
        string? PriorityName,
        string? RecoveryMode,
        bool ForceRegularNknBulk = false,
        bool RequiresExplicitFrontierRequest = false,
        bool AllowNormalRefillBypass = false);

    private sealed class InboundTransferContext
    {
        public InboundTransferContext(FileTransferOfferV2 offer, FileTransferRouteSelection routeSelection)
        {
            ArgumentNullException.ThrowIfNull(offer);
            SessionId = offer.SessionId;
            TransferId = offer.TransferId;
            FileName = offer.FileName;
            FileSizeBytes = offer.FileSizeBytes;
            OfferedDataProtocolVersion = offer.PreferredDataProtocolVersion ?? 0;
            RouteSelection = routeSelection;
            RouteRuntime = routeSelection.RuntimeDescriptor;
            NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion;
        }

        public CancellationTokenSource LifetimeCts { get; } = new();

        public string SessionId { get; }

        public string TransferId { get; }

        public string FileName { get; }

        public long FileSizeBytes { get; }

        public string? Sha256Base64 { get; set; }

        public int OfferedDataProtocolVersion { get; }

        public int NegotiatedDataProtocolVersion { get; set; } = FileTransferProtocol.ProtocolVersionV6;

        public FileTransferRouteSelection RouteSelection { get; set; }

        public FileTransferRouteRuntimeDescriptor RouteRuntime { get; set; }

        public LiveRouteEpoch? CurrentLiveRouteEpoch { get; set; }

        public int LastLiveRouteEpochId { get; set; }

        public FileTransferLeg? CurrentTransferLeg { get; set; }

        public List<FileTransferLeg> TransferLegHistory { get; } = [];

        public int LastTransferLegGeneration { get; set; }

        public int LastRuntimeOwnerGeneration { get; set; }

        public int TerminalGeneration { get; set; }

        public RuntimeOwnerToken? CurrentRuntimeOwner { get; set; }

        public RuntimeUnlockFallbackSurvivalState RuntimeUnlockFallbackSurvivalState { get; set; }

        public RuntimeUnlockAdmissionToken? RuntimeUnlockAdmissionToken { get; set; }

        public int LastRuntimeUnlockAdmissionGeneration { get; set; }

        public RouteCommitToken? RuntimeUnlockRouteCommitToken { get; set; }

        public int LastRuntimeUnlockRouteCommitGeneration { get; set; }

        public RoutePublicationToken? CurrentRoutePublicationToken { get; set; }

        public int LastRoutePublicationGeneration { get; set; }

        public bool RuntimeUnlockWaitingForFallbackSurvival { get; set; }

        public string? RuntimeUnlockWaitingForFallbackSurvivalReason { get; set; }

        public DateTimeOffset? RuntimeUnlockWaitingForFallbackSurvivalUtc { get; set; }

        public bool RuntimeUnlockActivationWindowGranted { get; set; }

        public string? RuntimeUnlockActivationWindowReason { get; set; }

        public DateTimeOffset? RuntimeUnlockActivationWindowGrantedUtc { get; set; }

        public RuntimeUnlockPeerPreCommitProbeState? RuntimeUnlockPendingPeerPreCommitProbe { get; set; }

        public FileTransferRuntimeProfile RuntimeProfile { get; set; }

        public FileTransferBridgeRecoveryPolicy BridgeRecoveryPolicy { get; set; } =
            FileTransferBridgeRecoveryPolicy.TunaStrictRecovery;

        public FileTransferTransferState State { get; set; } = FileTransferTransferState.PendingDecision;

        public long BytesTransferred { get; set; }

        public int ChunksTransferred { get; set; }

        public int ChunkCount { get; set; }

        public int ChunkSizeBytes { get; set; }

        public int NextChunkIndex { get; set; }

        public long BufferedBytes { get; set; }

        public int HighestBufferedChunkIndex { get; set; } = -1;

        public int LastAdvertisedGrantedUntilExclusive { get; set; }

        public int LastAdvertisedNextChunkIndex { get; set; }

        public int LastAdvertisedCreditFrontier { get; set; }

        public DateTimeOffset? LastWindowUpdateSentUtc { get; set; }

        public DateTimeOffset? LastForcedWindowUpdateSentUtc { get; set; }

        public DateTimeOffset? LastContiguousProgressUtc { get; set; }

        public DateTimeOffset? LastBufferedFrontierAdvanceUtc { get; set; }

        public bool StartupPhaseCompleted { get; set; }

        public bool SteadyStateWindowAdvertised { get; set; }

        public bool DegradedRepairModeActive { get; set; }

        public bool BulkFallbackModeActive { get; set; }

        public bool BulkUnhealthyDetected { get; set; }

        public int LocalPressureRevision { get; set; }

        public int LocalPressureSuggestedSendAheadChunks { get; set; } = PressureStateNormalSuggestedSendAheadChunks;

        public int LocalPressureReceiverNextExpectedChunkIndex { get; set; }

        public DateTimeOffset? PressureRecoverySinceUtc { get; set; }

        public int ConsecutiveBulkUnhealthyDetections { get; set; }

        public long BulkDispatchedChunksSinceLastUsefulProgress { get; set; }

        public long ObsoleteChunksArrivedSinceLastUsefulProgress { get; set; }

        public DateTimeOffset? LastBulkDispatchedChunkUtc { get; set; }

        public DateTimeOffset? LastUsefulBulkProgressUtc { get; set; }

        public DateTimeOffset? LastBulkUnhealthyLogUtc { get; set; }

        public int? OldestGapStartChunkIndex { get; set; }

        public DateTimeOffset? OldestGapFirstSeenUtc { get; set; }

        public DateTimeOffset? GapFreeSinceUtc { get; set; }

        public MissingRange? OutstandingMissingRange { get; set; }

        public DateTimeOffset? LastMissingRangeSentUtc { get; set; }

        public int LastMissingRangeHighestBufferedChunkIndex { get; set; } = -1;

        public Queue<DateTimeOffset> RecentMissingRangeSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentGapProgressAckSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentContiguousProgressChunkUtc { get; } = new();

        public Queue<DateTimeOffset> RecentObsoleteChunkArrivalUtc { get; } = new();

        public DateTimeOffset? LastGapExtensionDeferredLogUtc { get; set; }

        public string? ErrorCode { get; set; }

        public string? StatusMessage { get; set; } = "Incoming transfer offer pending.";

        public string? SavedFilePath { get; set; }

        public string? SavedDirectoryPath { get; set; }

        public string? SavedFileName { get; set; }

        public bool AcceptInProgress { get; set; }

        public DateTimeOffset? MetadataAwaitingSinceUtc { get; set; }

        public int NextProgressMilestonePercent { get; set; } = 25;

        public FileTransferWriteDestinationFactory? OpenWriteDestinationAsync { get; set; }

        public FileTransferReceiveDestination? WriteDestination { get; set; }

        public Stream? WriteStream { get; set; }

        public SemaphoreSlim ReceiverSparseWriteGate { get; } = new(1, 1);

        public IncrementalHash? Hash { get; set; }

        public SortedDictionary<int, byte[]> PendingChunks { get; } = new();

        public bool ReceiverSparseWriteActive { get; set; }

        public BitArray? ReceiverSparseChunksWritten { get; set; }

        public HashSet<int> ReceiverSparseChunksPendingWrite { get; } = [];

        public long ReceiverSparseBytesWritten { get; set; }

        public long PullReceiverSparseWriteBytesRecent { get; set; }

        public int PullReceiverSparseWriteBatchCountRecent { get; set; }

        public long PullReceiverSparseWriteDurationMsRecent { get; set; }

        public int PullReceiverSparseWriteBatchCountTotal { get; set; }

        public long PullReceiverSparseWriteDurationMsTotal { get; set; }

        public int PullReceiverSparseChunksWrittenRecent { get; set; }

        public int PullReceiverSparseContiguousChunksCommittedRecent { get; set; }

        public IFileTransferDataSession? DataSession { get; set; }

        public bool PullSessionActive { get; set; }

        public bool PullManifestReceived { get; set; }

        public int V4StateEpoch { get; set; }

        public int V4CreditUntilChunkIndexExclusive { get; set; }

        public int V4LastStateCreditUntilChunkIndexExclusive { get; set; }

        public int V4LastStateContiguousCommittedChunkIndex { get; set; }

        public int V4LastStateDurableHighestChunkIndex { get; set; } = -1;

        public long V4LastStateBytesCommitted { get; set; }

        public bool V4MixedScreenShareTransfer { get; set; }

        public DateTimeOffset? V4LastStateSentUtc { get; set; }

        public long V6RegularNknFrontierRepairTransactionSequence { get; set; }

        public string? V6RegularNknFrontierRepairTransactionId { get; set; }

        public int V6RegularNknFrontierRepairTransactionStartChunkIndex { get; set; } = -1;

        public int V6RegularNknFrontierRepairTransactionChunkCount { get; set; }

        public DateTimeOffset? V6RegularNknFrontierRepairTransactionStartedUtc { get; set; }

        public DateTimeOffset? V6RegularNknFrontierRepairTransactionLastObservedUtc { get; set; }

        public int V6RegularNknFrontierRepairTransactionObservedCount { get; set; }

        public Dictionary<string, V4ReceiverRepairRequestState> V4ReceiverRepairRequests { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset? V4FrontierStallStartedUtc { get; set; }

        public int V4FrontierStallChunkIndex { get; set; } = -1;

        public DateTimeOffset? V4FrontierStallLastSuppressedLogUtc { get; set; }

        public int V4FrontierStallSuppressedCountTotal { get; set; }

        public int V4LastRegularFrontierRepairStartChunkIndex { get; set; } = -1;

        public int V4LastRegularFrontierRepairChunkCount { get; set; }

        public int V4LastRegularFrontierRepairCommittedChunkIndex { get; set; } = -1;

        public int V4LastRegularFrontierRepairDurableHighestChunkIndex { get; set; } = -1;

        public int V4LastRegularFrontierRepairCreditUntilChunkIndexExclusive { get; set; }

        public int V4RegularFrontierRepairSpamCoalescedCountTotal { get; set; }

        public bool V4ReceiverRepairSchedulerStarted { get; set; }

        public FileTransferSessionOpenV2? PendingSessionOpen { get; set; }

        public bool PullSessionDegraded { get; set; }

        public int PullCurrentPipelineDepth { get; set; }

        public int PullRequestedFrontierExclusive { get; set; }

        public int PullCommittedFrontier { get; set; }

        public DateTimeOffset? PullLastRequestSentUtc { get; set; }

        public DateTimeOffset? PullLastProgressUtc { get; set; }

        public DateTimeOffset? PullLastCommittedProgressUtc { get; set; }

        public DateTimeOffset? PullDegradedSinceUtc { get; set; }

        public DateTimeOffset? PullRecoverySinceUtc { get; set; }

        public DateTimeOffset? PullReorderPressureSinceUtc { get; set; }

        public int PullReorderPressureFrontierChunkIndex { get; set; }

        public int? PullTimeoutOldestChunkIndex { get; set; }

        public int PullTimeoutStreak { get; set; }

        public int PullHighestReceivedChunkIndex { get; set; } = -1;

        public int V6SparseAcceptWindowEndExclusive { get; set; }

        public int PullLateArrivalDistance { get; set; }

        public bool PullGapFocusActive { get; set; }

        public int PullCurrentPipelineStep { get; set; }

        public int PullCurrentChunkSizeStep { get; set; }

        public DateTimeOffset? PullLastProfileAdjustmentUtc { get; set; }

        public DateTimeOffset? PullLastAckSentUtc { get; set; }

        public int PullLastAckSentChunkIndex { get; set; }

        public int PullAckDebtChunks { get; set; }

        public long PullAckDebtBytes { get; set; }

        public bool PullTransportPaused { get; set; }

        public DateTimeOffset? PullTransportPausedSinceUtc { get; set; }

        public DateTimeOffset? PullTransportGraceDeadlineUtc { get; set; }

        public string? PullTransportPauseReason { get; set; }

        public bool PullTransportResumeRequestPending { get; set; }

        public int PullTransportRebindGeneration { get; set; }

        public int TunaActivationBarrierStateSendSuppressedCount { get; set; }

        public long LastRecoveredV6TransportHandoffEpoch { get; set; }

        public long LastRecoveredV6TransportEpoch { get; set; }

        public int LastRecoveredV6TransportLiveRouteEpochId { get; set; }

        public FileTransferTransportHandoffKind LastRecoveredV6TransportEpochKind { get; set; }

        public FileTransferTransportKind LastRecoveredV6TransportTargetTransport { get; set; }

        public V6ReceiveDestinationMode V6DestinationMode { get; set; } = V6ReceiveDestinationMode.Unknown;

        public int V6ReceiverStateEpoch { get; set; }

        public long V6ReceiverTransportEpoch { get; set; }

        public V6TransportEpoch? V6TransportEpoch { get; set; }

        public long V6TransportEpochReplayLoopEpochId { get; set; }

        public long V6FrontierRequestSequence { get; set; }

        public DateTimeOffset? V6LastReceiverStateSentUtc { get; set; }

        public DateTimeOffset? V6LastFrontierRequestSentUtc { get; set; }

        public int V6LastFrontierRequestChunkIndex { get; set; } = -1;

        public string? V6LastFrontierRequestId { get; set; }

        public int V6PostTunaFallbackFrontierRescueChunkIndex { get; set; } = -1;

        public int V6PostTunaFallbackFrontierRescueStep { get; set; }

        public int V6PostTunaFallbackFrontierRescueRequestCount { get; set; }

        public DateTimeOffset? V6PostTunaFallbackFrontierRescueStartedUtc { get; set; }

        public HashSet<string> FallbackStaleProofSuppressionKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RetiredFallbackControlPlaneSendKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RetiredFallbackCheckpointRequestKeys { get; } = new(StringComparer.Ordinal);

        public FallbackControlPlaneToken? CurrentFallbackControlPlaneToken { get; set; }

        public int LastFallbackControlPlaneTokenGeneration { get; set; }

        public FallbackCheckpointLease? CurrentFallbackCheckpointLease { get; set; }

        public int LastFallbackCheckpointLeaseGeneration { get; set; }

        public FallbackControlPlanePressureToken? CurrentFallbackControlPlanePressureToken { get; set; }

        public int LastFallbackControlPlanePressureGeneration { get; set; }

        public FallbackTailYieldDecision? LastFallbackTailYieldDecision { get; set; }

        public int LastFallbackTailYieldGeneration { get; set; }

        public RepairReplayDecision? CurrentRepairReplayDecision { get; set; }

        public int LastRepairReplayGeneration { get; set; }

        public TransportEpochDecision? CurrentTransportEpochDecision { get; set; }

        public int LastTransportEpochDecisionGeneration { get; set; }

        public BridgeRecoveryDecision? CurrentBridgeRecoveryDecision { get; set; }

        public int LastBridgeRecoveryDecisionGeneration { get; set; }

        public RecoveryLivenessLease? CurrentRecoveryLivenessLease { get; set; }

        public int LastRecoveryLivenessLeaseGeneration { get; set; }

        public int V6PostTunaFallbackProofReplayGeneration { get; set; }

        public int V6PostTunaFallbackProofReplayLoopGeneration { get; set; }

        public bool V6PostTunaFallbackProofReplayReceiverStatePending { get; set; }

        public bool V6PostTunaFallbackProofReplayFrontierRequestPending { get; set; }

        public int V6PostTunaFallbackProofReplayStartChunkIndex { get; set; } = -1;

        public long V6PostTunaFallbackProofReplayTransportEpoch { get; set; }

        public string? V6PostTunaFallbackProofReplayReason { get; set; }

        public DateTimeOffset? V6PostTunaFallbackProofReplayArmedUtc { get; set; }

        public long V6RegularNknCheckpointSequence { get; set; }

        public string? V6RegularNknLastCheckpointSyncRequestId { get; set; }

        public DateTimeOffset? V6FrontierStallStartedUtc { get; set; }

        public int V6FrontierStallChunkIndex { get; set; } = -1;

        public DateTimeOffset? V6FrontierStallLastDeferredLogUtc { get; set; }

        public string? V6LastReceiverStateDeferredDiagnosticTuple { get; set; }

        public DateTimeOffset? V6LastReceiverStateDeferredDiagnosticUtc { get; set; }

        public int V6ReceiverStateDeferredDiagnosticSuppressedCount { get; set; }

        public string? V6LastReceiverStateCoalescedDiagnosticTuple { get; set; }

        public DateTimeOffset? V6LastReceiverStateCoalescedDiagnosticUtc { get; set; }

        public int V6ReceiverStateCoalescedDiagnosticSuppressedCount { get; set; }

        public string? V6LastFrontierProgressForcedDiagnosticTuple { get; set; }

        public DateTimeOffset? V6LastFrontierProgressForcedDiagnosticUtc { get; set; }

        public int V6FrontierProgressForcedDiagnosticSuppressedCount { get; set; }

        public string? PostTunaFallbackControlPlanePressureTuple { get; set; }

        public DateTimeOffset? PostTunaFallbackControlPlanePressureDiagnosticUtc { get; set; }

        public int PostTunaFallbackControlPlanePressureSuppressedCount { get; set; }

        public string? RetiredFallbackTailOwnerKey { get; set; }

        public string? LocalFallbackTailYieldOwnerKey { get; set; }

        public string? PeerFallbackTailYieldOwnerKey { get; set; }

        public long FallbackTailYieldGeneration { get; set; }

        public bool LocalFallbackTailYieldGranted { get; set; }

        public bool LocalFallbackTailYieldPeerVisible { get; set; }

        public bool PeerFallbackTailYieldGranted { get; set; }

        public bool PairedFallbackTailYieldGranted { get; set; }

        public string? FallbackTailYieldMismatchReason { get; set; }

        public HashSet<string> FallbackTailYieldProofSuppressionKeys { get; } = new(StringComparer.Ordinal);

        public bool FallbackTailTerminalProofConsumedLogged { get; set; }

        public HashSet<string> RetiredFallbackTailOwnerSuppressionKeys { get; } = new(StringComparer.Ordinal);

        public bool PullPostTunaRecoveryActive { get; set; }

        public int PullPostTunaRecoveryGeneration { get; set; }

        public int PullPostTunaRecoveryFrontierChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullPostTunaRecoveryStartedUtc { get; set; }

        public long PullTransportRebindStartedBytesTransferred { get; set; }

        public int PullTransportRebindStartedNextChunkIndex { get; set; }

        public int PullTransportRebindStartedHighestReceivedChunkIndex { get; set; }

        public DateTimeOffset? PullTransportRebindStartedUtc { get; set; }

        public bool PullTransportRebindRecoveredLogged { get; set; }

        public int PullTransportRebindStableProgressSamples { get; set; }

        public int PullTransportRebindLastObservedNextChunkIndex { get; set; }

        public int PullTransportRebindLastObservedHighestReceivedChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullTransportRebindLastFrontierRepairLoopLogUtc { get; set; }

        public int PullTransportRebindFrontierRepairCommittedChunks { get; set; }

        public int PullTransportRebindFrontierRepairWindowChunks { get; set; } = V4PostFallbackEmergencyFrontierRepairChunks;

        public int PullTransportRebindFrontierRepairLastCommittedChunkIndex { get; set; } = -1;

        public TransportHandoffEpoch? V6TransportHandoff { get; set; }

        public DateTimeOffset? LastV6FrontierRepairStillMissingLogUtc { get; set; }

        public int LastV6FrontierRepairStillMissingChunkIndex { get; set; } = -1;

        public int SuppressedV6FrontierRepairStillMissingLogCount { get; set; }

        public bool UserPaused { get; set; }

        public string? UserPauseReason { get; set; }

        public DateTimeOffset? UserPausedSinceUtc { get; set; }

        public bool PeerPaused { get; set; }

        public string? PeerPauseReason { get; set; }

        public DateTimeOffset? PeerPausedSinceUtc { get; set; }

        public int PeerV4LastStateEpoch { get; set; } = -1;

        public int PullV4StateSentCountTotal { get; set; }

        public int PullV4RepairRequestCountTotal { get; set; }

        public int PullV4RepairRequestedChunkCountTotal { get; set; }

        public int PullV4RepairSuppressedCountTotal { get; set; }

        public int PullV4FrontierTailRepairRequestCountTotal { get; set; }

        public int V4PauseControlEpoch { get; set; }

        public int PeerV4LastPauseControlEpoch { get; set; } = -1;

        public Queue<DateTimeOffset> RecentPullAckSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentPullRequestSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentPullChunkSentUtc { get; } = new();

        public int PullDuplicateRequestIgnoredCountRecent { get; set; }

        public int PullResendSuppressedCountRecent { get; set; }

        public long PullUsefulPayloadBytesRecent { get; set; }

        public long PullReceiverRawBytesRecent { get; set; }

        public long PullReceiverRawBatchBytesTotal { get; set; }

        public int PullReceiverRawBatchFramesTotal { get; set; }

        public long PullReceiverAcceptedRawBytesTotal { get; set; }

        public long PullReceiverDuplicateOrStaleRawBytesTotal { get; set; }

        public int PullReceiverChunkCountTotal { get; set; }

        public int PullReceiverAcceptedChunkCountTotal { get; set; }

        public int PullReceiverDuplicateOrStaleChunkCountTotal { get; set; }

        public int PullReceiverRepairOverlapChunkCountTotal { get; set; }

        public int PullReceiverRepairAcceptedChunkCountTotal { get; set; }

        public int PullReceiverRepairDuplicateOrStaleChunkCountTotal { get; set; }

        public long PullReceiverRepairDuplicateOrStaleRawBytesTotal { get; set; }

        public long PullReceiverContiguousBytesCommittedRecent { get; set; }

        public int PullReceiverWriteBatchCountRecent { get; set; }

        public long PullReceiverWriteBatchBytesRecent { get; set; }

        public long PullReceiverWriteDurationMsRecent { get; set; }

        public DateTimeOffset? LastPullControlChatterLogUtc { get; set; }

        public DateTimeOffset? PullV4GapStallSinceUtc { get; set; }

        public int PullV4GapStallStartChunkIndex { get; set; } = -1;

        public Dictionary<int, DateTimeOffset> OutstandingChunkRequests { get; } = new();

        public HashSet<int> RequestedChunks { get; } = [];

        public Dictionary<int, int> ChunkAttemptCounts { get; } = new();

        public int PullFirstChunkTimeoutCount { get; set; }

        public int PullV4GrantedUntilExclusive { get; set; }

        public DateTimeOffset? PullV4LastGrantSentUtc { get; set; }

        public DateTimeOffset? PullV4LastRepairRequestSentUtc { get; set; }

        public DateTimeOffset? PullV4LastProactiveFrontierRepairSentUtc { get; set; }

        public int PullV4LastProactiveFrontierRepairStartChunkIndex { get; set; } = -1;

        public int PullV4LastProactiveFrontierRepairRequestedChunkCount { get; set; }

        public int PullV4LastProactiveFrontierRepairHighestReceivedChunkIndex { get; set; } = -1;

        public string? PullV4LastProactiveFrontierRepairRequestKey { get; set; }

        public string? PullV4LastProactiveFrontierRepairFingerprint { get; set; }

        public int PullV4ConsecutiveProactiveFrontierRepairCount { get; set; }

        public DateTimeOffset? PullV4LastProactiveFrontierRepairSkipLogUtc { get; set; }

        public string? PullV4LastProactiveFrontierRepairSkipReason { get; set; }

        public int PullV4LastProactiveFrontierRepairSkipStartChunkIndex { get; set; } = -1;

        public Queue<DateTimeOffset> RecentPullRepairRequestSentUtc { get; } = new();

        public string? PullV4LastRepairRequestFingerprint { get; set; }

        public DateTimeOffset? PullV4LastRepairRequestFingerprintUtc { get; set; }

        public int PullV4LastRepairRequestNextChunkIndex { get; set; } = -1;

        public int PullV4LastRepairRequestHighestReceivedChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullV4LastPeerFrameReceivedUtc { get; set; }

        public DateTimeOffset? PullV4PeerSilenceDeferralUtc { get; set; }

        public int PullV4PeerSilenceDeferralCount { get; set; }

        public bool V6HeartbeatLoopStarted { get; set; }

        public long V6HeartbeatSequence { get; set; }

        public DateTimeOffset? V6LastPeerLivenessUtc { get; set; }

        public int V6EpochLivenessDeferralCount { get; set; }

        public DateTimeOffset? V6EpochLivenessDeferralUtc { get; set; }

        public int V6LastReceiverStateCommittedChunkIndex { get; set; } = -1;

        public FileTransferTransportProfileKind TransportProfileKind { get; set; } = FileTransferTransportProfileKind.Default;

        public bool PullV4ConservativeStartupActive { get; set; }

        public bool PullV4ConservativeStartupDegradedActive { get; set; }

        public bool PullV4ConservativeStartupProbeActive { get; set; }

        public DateTimeOffset? PullV4ConservativeStartupStartedUtc { get; set; }

        public DateTimeOffset? PullV4ConservativeStartupExitedUtc { get; set; }

        public string? PullV4ConservativeStartupExitReason { get; set; }

        public long PullV4ConservativeStartupExitBytes { get; set; }

        public bool PullV4FirstRepairOrTimeoutBeforeStartupExit { get; set; }

        public bool PullV4ExpandedWindowActive { get; set; }

        public bool PullV4FileOnlySoftLimitedWindowActive { get; set; }

        public bool PullV4LimitedWindowActive { get; set; }

        public DateTimeOffset? PullV4CleanSinceUtc { get; set; }

        public DateTimeOffset? PullV4AdverseSinceUtc { get; set; }

        public string? PullV4LastReorderPolicyDecision { get; set; }

        public DateTimeOffset? PullV4LastReorderPolicyDecisionLogUtc { get; set; }

        public DateTimeOffset? PullV4LastGrantWindowSummaryLogUtc { get; set; }

        public long PullV4LastGrantTargetWindowBytes { get; set; }

        public int PullV4LastGrantCreditBaseChunkIndex { get; set; }

        public DateTimeOffset? PullV4LastSparseCreditEligibleUtc { get; set; }

        public int PullV4LastSparseCreditBaseChunkIndex { get; set; }

        public bool ReceiverBufferPressureActive { get; set; }

        public DateTimeOffset? ReceiverBufferPressureSinceUtc { get; set; }

        public DateTimeOffset? LastReceiverGrantClampLogUtc { get; set; }

        public DateTimeOffset? LastPressureStateSentUtc { get; set; }

        public int LastPressureStateSentSuggestedSendAheadChunks { get; set; }

        public int LastPressureStateSentReceiverNextExpectedChunkIndex { get; set; }

        public string? LastPressureStateSentProfileName { get; set; }

        public bool IsTerminal => ToSnapshot().IsTerminal;

        public FileTransferIncomingOffer CreateOffer()
            => new(SessionId, TransferId, FileName, FileSizeBytes, Sha256Base64);

        public FileTransferTransferSnapshot ToSnapshot()
        {
            var bytesAcceptedForTransport = ReceiverSparseWriteActive
                ? Math.Max(BytesTransferred, ReceiverSparseBytesWritten)
                : BytesTransferred;

            return new(
                SessionId,
                TransferId,
                FileTransferDirection.Inbound,
                State,
                FileName,
                FileSizeBytes,
                Sha256Base64,
                BytesTransferred,
                ChunksTransferred,
                ChunkCount,
                ChunkSizeBytes,
                ErrorCode,
                StatusMessage,
                SavedFilePath,
                SavedDirectoryPath,
                SavedFileName,
                BytesAcceptedForTransport: bytesAcceptedForTransport,
                IsPaused: UserPaused,
                PauseReason: UserPauseReason,
                IsPeerPaused: PeerPaused,
                PeerPauseReason: PeerPauseReason,
                RouteToken: RouteSelection.TelemetryToken,
                ProtocolVersion: NegotiatedDataProtocolVersion);
        }

        public void CancelLifetime()
        {
            try
            {
                LifetimeCts.Cancel();
            }
            catch
            {
            }
        }

        public void DisposeResources()
        {
            try
            {
                DataSession?.Dispose();
            }
            catch
            {
            }

            try
            {
                WriteDestination?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (WriteDestination is null)
                {
                    WriteStream?.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                Hash?.Dispose();
            }
            catch
            {
            }

            try
            {
                LifetimeCts.Dispose();
            }
            catch
            {
            }

            WriteDestination = null;
            WriteStream = null;
            Hash = null;
            DataSession = null;
        }

        public IFileTransferDataSession? DetachDataSession()
        {
            var dataSession = DataSession;
            DataSession = null;
            return dataSession;
        }

        private static TaskCompletionSource<bool> CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private int ResolvePreferredOutboundChunkSize(
        OutboundTransferContext context,
        IFileTransferSignalingTransport? currentTransport = null)
    {
        var defaultChunkSize = V4DefaultChunkSizeBytes;
        var preferredChunkSize = context.Descriptor.ChunkSizeBytes ?? defaultChunkSize;
        var v4MixedScreenShareTransfer =
            ShouldAllowV4DuringScreenShare() &&
            (sessionScreenShareActive || sessionScreenShareDegraded || sessionScreenShareObserved);
        if (v4MixedScreenShareTransfer)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, V4DefaultChunkSizeBytes);
        }
        else if (sessionScreenShareDegraded)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, V4DefaultChunkSizeBytes);
        }
        else if (context.PullSessionDegraded)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, V4DefaultChunkSizeBytes);
        }
        else if (sessionScreenShareActive)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, V4DefaultChunkSizeBytes);
        }
        else
        {
            preferredChunkSize = Math.Min(preferredChunkSize, V4DefaultChunkSizeBytes);
        }

        return Math.Clamp(preferredChunkSize, 1, FileTransferProtocol.MaxChunkRawBytes);
    }

    private sealed class SenderCacheException(string errorCode, string message) : InvalidOperationException(message)
    {
        public string ErrorCode { get; } = errorCode;
    }

    private readonly record struct FileTransferPayloadEfficiencyProfileSelection(
        FileTransferPayloadEfficiencyProfile Profile,
        string Reason);

    private sealed record PreparedV4TransportSend(
        FileTransferChunkBatchFrameV4 Frame,
        int StartChunkIndex,
        int ChunkCount,
        int RawBytes);

    private sealed record PendingV4TransportSend(
        PreparedV4TransportSend Prepared,
        Task SendTask,
        CancellationTokenSource? SendCts,
        DateTimeOffset ScheduledUtc,
        int SendAttempt,
        int SendAttemptCount,
        bool ScheduledRegularNknV4FastRuntime);

    private readonly record struct MissingRange(int StartChunkIndex, int EndChunkIndexExclusive);
}
