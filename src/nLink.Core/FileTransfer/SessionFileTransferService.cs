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
    private static readonly TimeSpan V5TransportHandoffWaitingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan V6TransportEpochProofTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan V6TransportProbeAckSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PullV4PostFallbackPeerSilenceTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PullV4PeerSilenceTimeout = TimeSpan.FromSeconds(90);
    private static readonly int[] CancelRetryDelaysMs = [250, 750, 1500, 3000, 7000, 12000, 20000, 30000];
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
    private const int V6RegularNknCheckpointSyncFailuresBeforeBridgeRecovery = 2;
    private const string V6RegularNknStateRefreshRecoveryMode = "regular_nkn_state_refresh";
    private const string V6RegularNknStateRefreshPriority = "state_refresh";
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

            context = new OutboundTransferContext(normalizedDescriptor, openReadStreamAsync);
            context.OfferedDataProtocolVersion = ResolvePreferredDataProtocolVersionForNewTransfer(currentTransport);
            outboundTransfer = context;
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
            SessionId = string.Empty,
            TransferId = context.TransferId,
            FileName = context.FileName,
            FileSizeBytes = context.FileSizeBytes,
            PreferredDataProtocolVersion = context.OfferedDataProtocolVersion,
        };

        try
        {
            var offerTransport = GetTransportOrThrow();
            await offerTransport.SendFileTransferOfferAsync(offerMessage, linkedCts.Token).ConfigureAwait(false);
            LogTransferInfo(
                "offer_sent",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes);
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
            context.NegotiatedDataProtocolVersion = context.OfferedDataProtocolVersion;
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
                !outboundTransfer.IsTerminal &&
                string.Equals(outboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                if (outboundTransfer.UserPaused || !CanUserPauseOutbound(outboundTransfer.State))
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
                outboundTransfer.V4SenderPumpLastWakeReason = "user_paused";
                outboundTransfer.ResetV6SenderPipelineCancellation();
                outboundTransfer.SignalV4SenderPump();
                pausedOutboundContext = !string.IsNullOrWhiteSpace(outboundTransfer.SessionId)
                    ? outboundTransfer
                    : null;
            }
            else if (inboundTransfer is not null &&
                     !inboundTransfer.IsTerminal &&
                     string.Equals(inboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal))
            {
                if (inboundTransfer.UserPaused || !CanUserPauseInbound(inboundTransfer.State))
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
                () => SendInboundV6ReceiverStateAsync(pausedInboundContext, "user_paused", forceSend: true),
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
                outboundTransfer.V4SenderPumpLastWakeReason = "user_resumed";
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

        resumedOutboundPumpContext?.SignalV4SenderPump();

        if (resumedInboundContext is not null)
        {
            ScheduleInboundV4PauseControlRetry(resumedInboundContext, paused: false, "user_resumed");
            _ = Task.Run(
                () => FlushInboundV6PausedProgressAsync(resumedInboundContext, "user_resumed"),
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

            if (context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4)
            {
                LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound);
                if ((sessionScreenShareActive || sessionScreenShareDegraded) && !ShouldAllowV4DuringScreenShare())
                {
                    await FailOutboundV4Async(
                        context,
                        dataSession: null,
                        V4FileOnlyRequiredErrorCode,
                        "V4 file-transfer send is currently file-only.",
                        notifyPeer: false).ConfigureAwait(false);
                    return;
                }

                LogV4MixedScreenShareEnabled(context.TransferId, context.SessionId, FileTransferDirection.Outbound);
                await RunOutboundRegularNknV4Async(context).ConfigureAwait(false);
                return;
            }

            LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound);
            if ((sessionScreenShareActive || sessionScreenShareDegraded) && !ShouldAllowV4DuringScreenShare())
            {
                await FailOutboundV4Async(
                    context,
                    dataSession: null,
                    V4FileOnlyRequiredErrorCode,
                    "V6 file-transfer send is currently file-only.",
                    notifyPeer: false).ConfigureAwait(false);
                return;
            }

            LogV4MixedScreenShareEnabled(context.TransferId, context.SessionId, FileTransferDirection.Outbound);
            var runtimeSelection = ResolveFileTransferRuntimeProfile(context);
            LogFileTransferBridgeRecoveryPolicySelected(
                context.TransferId,
                context.SessionId,
                FileTransferDirection.Outbound,
                runtimeSelection);
            if (runtimeSelection.Profile == FileTransferRuntimeProfile.PrimaryRegularNknBulkV6)
            {
                LogPrimaryRegularNknBulkV6Selected(context.TransferId, context.SessionId, FileTransferDirection.Outbound, runtimeSelection);
                await RunOutboundPrimaryRegularNknBulkV6Async(context).ConfigureAwait(false);
            }
            else
            {
                await RunOutboundV6SenderAsync(context).ConfigureAwait(false);
            }
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
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.PullSessionActive;

    private static bool ShouldDeferV6TransportDisconnectedTerminalizationLocked(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.PullSessionActive;

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

        InboundTransferContext? busyTransfer = null;
        SessionFileTransferSnapshot? snapshotToRaise = null;
        var incoming = new InboundTransferContext(message);

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (inboundTransfer is not null && !inboundTransfer.IsTerminal)
            {
                busyTransfer = inboundTransfer;
            }
            else
            {
                inboundTransfer = incoming;
                snapshotToRaise = CreateSnapshotLocked();
            }
        }

        if (snapshotToRaise is not null)
        {
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

        if (busyTransfer is not null)
        {
            await SendDeclineAsync(message.SessionId, message.TransferId, BusyReason, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleIncomingAcceptAsync(FileTransferAcceptV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        var acceptedProtocolVersion = message.AcceptedDataProtocolVersion;
        var acceptedVersionIsNegotiable = IsNegotiableDataProtocolVersion(acceptedProtocolVersion);
        var acceptedVersionMatchesOffer = false;
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
            if (!acceptedVersionIsNegotiable || !acceptedVersionMatchesOffer || acceptedProtocolVersion is null)
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

        if (!acceptedVersionIsNegotiable || !acceptedVersionMatchesOffer || acceptedProtocolVersion is null)
        {
            LogLegacyNegotiationRejected(
                message.TransferId,
                message.SessionId,
                FileTransferDirection.Outbound,
                offeredVersion: context.OfferedDataProtocolVersion,
                acceptedVersion: acceptedProtocolVersion,
                reason: acceptedVersionIsNegotiable ? "accept_protocol_mismatch" : "accept_protocol_not_supported");
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
                !IsNegotiableDataProtocolVersion(message.ProtocolVersion))
            {
                if (!IsNegotiableDataProtocolVersion(message.ProtocolVersion))
                {
                    LogLegacyNegotiationRejected(
                        message.TransferId,
                        message.SessionId,
                        FileTransferDirection.Inbound,
                        offeredVersion: message.ProtocolVersion,
                        acceptedVersion: context.NegotiatedDataProtocolVersion,
                        reason: "session_open_protocol_not_supported");
                    LogV6SessionOpenRejected(
                        message.TransferId,
                        message.SessionId,
                        FileTransferDirection.Inbound,
                        message.ProtocolVersion,
                        "session_open_protocol_not_supported");
                }
                context = null;
            }
            else if (context.State is not FileTransferTransferState.AwaitingMetadata and not FileTransferTransferState.Receiving)
            {
                context = null;
            }
        }

        if (context is null)
        {
            return;
        }

        if (message.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4)
        {
            LogV4Negotiated(message.TransferId, message.SessionId, FileTransferDirection.Inbound);
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

            LogV4MixedScreenShareEnabled(message.TransferId, message.SessionId, FileTransferDirection.Inbound);
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
                    context.NegotiatedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4;
                    context.StatusMessage = "Waiting for V4 file-transfer manifest.";
                }

                LogTransferInfo(
                    "filetransfer_session_opened",
                    FileTransferDirection.Inbound,
                    message.TransferId,
                    sessionId: message.SessionId,
                    reason: $"role={message.SessionRole}; protocol_version={message.ProtocolVersion}; chunk_size_bytes={message.ChunkSizeBytes}; pipeline_depth={message.InitialPipelineDepth}");
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

        if (message.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6)
        {
            LogV6Negotiated(message.TransferId, message.SessionId, FileTransferDirection.Inbound);
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

            LogV4MixedScreenShareEnabled(message.TransferId, message.SessionId, FileTransferDirection.Inbound);
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
                    context.NegotiatedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6;
                    context.StatusMessage = "Waiting for V6 file-transfer manifest.";
                }

                LogTransferInfo(
                    "filetransfer_session_opened",
                    FileTransferDirection.Inbound,
                    message.TransferId,
                    sessionId: message.SessionId,
                    reason: $"role={message.SessionRole}; protocol_version={message.ProtocolVersion}; chunk_size_bytes={message.ChunkSizeBytes}; pipeline_depth={message.InitialPipelineDepth}");
                StartInboundV6HeartbeatLoop(context, "session_open_received");
                var runtimeSelection = ResolveFileTransferRuntimeProfile(context);
                LogFileTransferBridgeRecoveryPolicySelected(
                    context.TransferId,
                    context.SessionId,
                    FileTransferDirection.Inbound,
                    runtimeSelection);
                if (runtimeSelection.Profile == FileTransferRuntimeProfile.PrimaryRegularNknBulkV6)
                {
                    LogPrimaryRegularNknBulkV6Selected(context.TransferId, context.SessionId, FileTransferDirection.Inbound, runtimeSelection);
                    _ = RunInboundPrimaryRegularNknBulkV6Async(context, message);
                }
                else
                {
                    _ = RunInboundV6ReceiverAsync(context, message);
                }
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
        string? inboundPausedTransferId = null;
        string? inboundPausedSessionId = null;
        bool outboundResumed = false;
        bool inboundResumed = false;
        bool outboundEpochStartedWhileUnavailable = false;
        bool inboundEpochStartedWhileUnavailable = false;

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer?.DataSession, dataSession) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                if (effectiveIsAvailable)
                {
                    outboundResumed = TryResumeOutboundTransportLocked(
                        outbound,
                        effectiveReason,
                        effectiveRequiresResumeRequest,
                        effectiveHandoffKind,
                        effectiveTargetTransport);
                    if (outboundResumed)
                    {
                        outboundToResume = outbound;
                    }
                }
                else
                {
                    if (TryPauseOutboundTransportLocked(outbound, effectiveReason, effectiveRequiresResumeRequest))
                    {
                        outboundPausedTransferId = outbound.TransferId;
                        outboundPausedSessionId = outbound.SessionId;
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
                }
            }

            if (ReferenceEquals(inboundTransfer?.DataSession, dataSession) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                if (effectiveIsAvailable)
                {
                    inboundResumed = TryResumeInboundTransportLocked(
                        inbound,
                        effectiveReason,
                        effectiveRequiresResumeRequest,
                        effectiveHandoffKind,
                        effectiveTargetTransport);
                    if (inboundResumed)
                    {
                        inboundToResume = inbound;
                    }
                }
                else
                {
                    if (TryPauseInboundTransportLocked(inbound, effectiveReason, effectiveRequiresResumeRequest))
                    {
                        inboundPausedTransferId = inbound.TransferId;
                        inboundPausedSessionId = inbound.SessionId;
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
                }
            }
        }

        if (outboundPausedTransferId is not null && outboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Outbound, outboundPausedTransferId, outboundPausedSessionId, effectiveReason);
        }

        if (inboundPausedTransferId is not null && inboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Inbound, inboundPausedTransferId, inboundPausedSessionId, effectiveReason);
        }

        if (outboundResumed && outboundToResume is not null)
        {
            LogTransportResumed(FileTransferDirection.Outbound, outboundToResume.TransferId, outboundToResume.SessionId, effectiveReason, effectiveRequiresResumeRequest);
            if (effectiveRequiresResumeRequest)
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
                    await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundToResume).ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_transport_rebind_generation_started; direction=outbound; transfer_id={outboundToResume.TransferId}; session_id={outboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={outboundToResume.PullTransportRebindGeneration}; remote_committed_chunk={outboundToResume.RemoteNextExpectedChunkIndex}; highest_sent_chunk={Math.Max(-1, outboundToResume.ChunksAcceptedForTransport - 1)}");
                }

                SignalOutboundV4SenderPump(outboundToResume);
            }
        }
        else if (outboundEpochStartedWhileUnavailable && outboundToResume is not null)
        {
            await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundToResume).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id={outboundToResume.TransferId}; session_id={outboundToResume.SessionId}; reason={FormatProtocolLogValue(effectiveReason)}; rebind_generation={outboundToResume.PullTransportRebindGeneration}; target_transport={FormatFileTransferTransportKind(effectiveTargetTransport)}");
            SignalOutboundV4SenderPump(outboundToResume);
        }

        if (inboundResumed && inboundToResume is not null)
        {
            LogTransportResumed(FileTransferDirection.Inbound, inboundToResume.TransferId, inboundToResume.SessionId, effectiveReason, effectiveRequiresResumeRequest);
            if (effectiveRequiresResumeRequest)
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
        }
        else if (inboundEpochStartedWhileUnavailable && inboundToResume is not null)
        {
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
            SignalOutboundV4SenderPump(outbound);
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
                invalidOperationException.Message.Contains("Bridge disconnected", StringComparison.OrdinalIgnoreCase))
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
        => ShouldUseFileTransferV6ForAcceleration(currentTransport)
            ? FileTransferProtocol.ProtocolVersionV6
            : FileTransferProtocol.ProtocolVersionV4;

    private static bool ShouldUseFileTransferV6ForAcceleration(IFileTransferSignalingTransport? currentTransport)
        => currentTransport is global::NLink.Core.ITransportAccelerationStatus accelerationStatus &&
           (accelerationStatus.IsTransportAccelerationActive ||
            accelerationStatus.ShouldUseFileTransferV6ForAcceleration);

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

        if (context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6)
        {
            return FileTransferRuntimeProfileSelection.Default("protocol_not_v6");
        }

        if (!IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
        {
            return FileTransferRuntimeProfileSelection.Default("not_primary_regular_nkn");
        }

        if (IsPrimaryRegularNknBulkV6ProfileEnabled(transport))
        {
            return FileTransferRuntimeProfileSelection.PrimaryRegularNknBulkV6("conservative_regular_nkn");
        }

        return FileTransferRuntimeProfileSelection.Default("transport_profile_not_conservative");
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

        if (context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6)
        {
            return FileTransferRuntimeProfileSelection.Default("protocol_not_v6");
        }

        if (!IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
        {
            return FileTransferRuntimeProfileSelection.Default("not_primary_regular_nkn");
        }

        if (IsPrimaryRegularNknBulkV6ProfileEnabled(transport))
        {
            return FileTransferRuntimeProfileSelection.PrimaryRegularNknBulkV6("conservative_regular_nkn");
        }

        return FileTransferRuntimeProfileSelection.Default("transport_profile_not_conservative");
    }

    private static void ApplyFileTransferRuntimeProfileSelectionLocked(
        OutboundTransferContext context,
        FileTransferRuntimeProfileSelection selection)
    {
        context.RuntimeProfile = selection.Profile;
        context.BridgeRecoveryPolicy = selection.BridgeRecoveryPolicy;
        context.V6RegularNknBulkSparseProfileActive = selection.UsesRegularNknSparseEngine;
    }

    private static void ApplyFileTransferRuntimeProfileSelectionLocked(
        InboundTransferContext context,
        FileTransferRuntimeProfileSelection selection)
    {
        context.RuntimeProfile = selection.Profile;
        context.BridgeRecoveryPolicy = selection.BridgeRecoveryPolicy;
        context.V6RegularNknBulkSparseProfileActive = selection.UsesRegularNknSparseEngine;
    }

    private static bool ShouldUseV6RegularNknSparseRuntime(OutboundTransferContext context)
        => context.V6RegularNknBulkSparseProfileActive;

    private static bool ShouldUseV6RegularNknSparseRuntime(InboundTransferContext context)
        => context.V6RegularNknBulkSparseProfileActive;

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
        FileTransferRuntimeProfileSelection selection)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; runtime_profile=PrimaryRegularNknBulkV6; credit_profile=v4_sparse; frame_profile=v6; recovery_profile=regular_nkn_quiet; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(selection.BridgeRecoveryPolicy)}; activation=primary_regular_nkn; selection_reason={selection.Reason}");
    }

    private static void LogFileTransferBridgeRecoveryPolicySelected(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        FileTransferRuntimeProfileSelection selection)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bridge_recovery_policy_selected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; runtime_profile={FormatFileTransferRuntimeProfile(selection.Profile)}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(selection.BridgeRecoveryPolicy)}; selection_reason={FormatProtocolLogValue(selection.Reason)}");
    }

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
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           (context.V4MixedScreenShareTransfer ||
            context.State is FileTransferTransferState.PreparingMetadata
                or FileTransferTransferState.AwaitingStart
                or FileTransferTransferState.Sending
                or FileTransferTransferState.AwaitingCompletion);

    private static bool IsActiveV4MixedInboundTransferLocked(InboundTransferContext? context)
        => context is not null &&
           !context.IsTerminal &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           (context.V4MixedScreenShareTransfer ||
            context.State is FileTransferTransferState.AwaitingMetadata
                or FileTransferTransferState.Receiving
                or FileTransferTransferState.Verifying);

    private void LogV4MixedScreenShareEnabled(string transferId, string sessionId, FileTransferDirection direction)
    {
        if (!IsV4MixedScreenShareActive())
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_mixed_enabled; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; screen_share_active={(sessionScreenShareActive ? 1 : 0)}; screen_share_degraded={(sessionScreenShareDegraded ? 1 : 0)}; screen_share_observed={(sessionScreenShareObserved ? 1 : 0)}; screen_share_policy_hint=catch_up_only; credit_window_chunks={ResolveV4StateCreditWindowChunksForCurrentMode()}; normal_batch_segments={ResolveV4MaxBatchSegments(repairSend: false)}");
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

    private static void LogV6Negotiated(string transferId, string sessionId, FileTransferDirection direction)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_negotiated; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; protocol_version={FileTransferProtocol.ProtocolVersionV6}");
    }

    private static void LogV4Negotiated(string transferId, string sessionId, FileTransferDirection direction)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_negotiated; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; protocol_version={FileTransferProtocol.ProtocolVersionV4}; activation=primary_regular_nkn; runtime_profile=regular_nkn_v4_fast");
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
        string? savedPath = null)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId ?? "(none)"}; file_name_len={(string.IsNullOrWhiteSpace(fileName) ? 0 : fileName.Trim().Length)}; file_size_bytes={(fileSizeBytes?.ToString() ?? "(none)")}; bytes_transferred={(bytesTransferred?.ToString() ?? "(none)")}; chunks_transferred={(chunksTransferred?.ToString() ?? "(none)")}; chunk_count={(chunkCount?.ToString() ?? "(none)")}; error_code={errorCode ?? "(none)"}; reason={reason ?? "(none)"}; saved_path={savedPath ?? "(none)"}");
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
        FileTransferRuntimeProfile Profile,
        FileTransferBridgeRecoveryPolicy BridgeRecoveryPolicy,
        string Reason)
    {
        public static FileTransferRuntimeProfileSelection Default(string reason)
            => new(FileTransferRuntimeProfile.Default, FileTransferBridgeRecoveryPolicy.TunaStrictRecovery, reason);

        public static FileTransferRuntimeProfileSelection PrimaryRegularNknBulkV6(string reason)
            => new(
                FileTransferRuntimeProfile.PrimaryRegularNknBulkV6,
                FileTransferBridgeRecoveryPolicy.PrimaryRegularNknQuietRecovery,
                reason);

        public bool UsesRegularNknSparseEngine =>
            Profile is FileTransferRuntimeProfile.PrimaryRegularNknBulkV6;
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
            _ => "tuna_strict",
        };

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

        public bool V6RegularNknBulkSparseProfileActive { get; set; }

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

        public bool PullTransportResumeRequestPending { get; set; }

        public int PullTransportRebindGeneration { get; set; }

        public long LastRecoveredV5TransportHandoffEpoch { get; set; }

        public long LastRecoveredV6TransportEpoch { get; set; }

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

        public bool PullPostTunaRecoveryActive { get; set; }

        public int PullPostTunaRecoveryGeneration { get; set; }

        public int PullPostTunaRecoveryFrontierChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullPostTunaRecoveryStartedUtc { get; set; }

        public int PullTransportLastSafetyReplayGeneration { get; set; }

        public int PullTransportLastSafetyReplayFrontierChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullTransportLastSafetyReplayUtc { get; set; }

        public int PullTransportSafetyReplayRearmCount { get; set; }

        public bool PullTransportFrontierOnlyRepairActive { get; set; }

        public int PullTransportFrontierOnlyRepairStartChunkIndex { get; set; } = -1;

        public DateTimeOffset? PullTransportRebindStartedUtc { get; set; }

        public TransportHandoffEpoch? V5TransportHandoff { get; set; }

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

        public string V4SenderPumpLastWakeReason { get; set; } = "startup";

        public string V4SenderPumpLastRepairRequestKey { get; set; } = "(none)";

        public DateTimeOffset? V4SenderCreditExhaustedSinceUtc { get; set; }

        private TaskCompletionSource<bool> pullV4SenderPumpSignal = CreateSignal();

        public Task ResetAndGetV4SenderPumpSignalTask()
        {
            pullV4SenderPumpSignal = CreateSignal();
            return pullV4SenderPumpSignal.Task;
        }

        public void SignalV4SenderPump()
        {
            pullV4SenderPumpSignal.TrySetResult(true);
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
                PeerPauseReason: PeerPauseReason);

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
        long CreditExhaustedTimeMsAtDecision);

    private sealed class V4SenderRepairRequestState
    {
        public bool Queued { get; set; }

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
        public InboundTransferContext(FileTransferOfferV2 offer)
        {
            ArgumentNullException.ThrowIfNull(offer);
            SessionId = offer.SessionId;
            TransferId = offer.TransferId;
            FileName = offer.FileName;
            FileSizeBytes = offer.FileSizeBytes;
            OfferedDataProtocolVersion = offer.PreferredDataProtocolVersion ?? 0;
        }

        public CancellationTokenSource LifetimeCts { get; } = new();

        public string SessionId { get; }

        public string TransferId { get; }

        public string FileName { get; }

        public long FileSizeBytes { get; }

        public string? Sha256Base64 { get; set; }

        public int OfferedDataProtocolVersion { get; }

        public int NegotiatedDataProtocolVersion { get; set; } = FileTransferProtocol.ProtocolVersionV6;

        public bool V6RegularNknBulkSparseProfileActive { get; set; }

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

        public long LastRecoveredV5TransportHandoffEpoch { get; set; }

        public long LastRecoveredV6TransportEpoch { get; set; }

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

        public long V6RegularNknCheckpointSequence { get; set; }

        public string? V6RegularNknLastCheckpointSyncRequestId { get; set; }

        public DateTimeOffset? V6FrontierStallStartedUtc { get; set; }

        public int V6FrontierStallChunkIndex { get; set; } = -1;

        public DateTimeOffset? V6FrontierStallLastDeferredLogUtc { get; set; }

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

        public TransportHandoffEpoch? V5TransportHandoff { get; set; }

        public DateTimeOffset? LastV5FrontierRepairStillMissingLogUtc { get; set; }

        public int LastV5FrontierRepairStillMissingChunkIndex { get; set; } = -1;

        public int SuppressedV5FrontierRepairStillMissingLogCount { get; set; }

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
                PeerPauseReason: PeerPauseReason);
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
        int SendAttemptCount);

    private readonly record struct MissingRange(int StartChunkIndex, int EndChunkIndexExclusive);
}
