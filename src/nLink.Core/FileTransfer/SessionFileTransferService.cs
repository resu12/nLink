using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService : IDisposable
{
    private const string BusyReason = FileTransferResultCodes.Busy;
    private const string DeclinedReason = FileTransferResultCodes.Declined;
    private const string CanceledReason = FileTransferResultCodes.CanceledLocal;
    private const string DisconnectedErrorCode = FileTransferResultCodes.TransportDisconnected;
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
    private const string V4RuntimeNotImplementedErrorCode = FileTransferResultCodes.V4RuntimeNotImplemented;
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
    private const int PullV3HealthyTargetInFlightBytes = 2 * 1024 * 1024;
    private const int PullV3HealthyMaximumTargetInFlightBytes = 4 * 1024 * 1024;
    private const int PullV3HealthyFileOnlySoftLimitedTargetInFlightBytesDefault = 4 * 1024 * 1024;
    private const int PullV3HealthyFileOnlySoftLimitedTargetInFlightBytesMin = 512 * 1024;
    private const int PullV3HealthyFileOnlySoftLimitedTargetInFlightBytesMax = 8 * 1024 * 1024;
    private const int PullV3HealthyLimitedTargetInFlightBytes = 512 * 1024;
    private const int PullV3ScreenshareTargetInFlightBytes = 256 * 1024;
    private const int PullV3DegradedTargetInFlightBytes = 256 * 1024;
    private const int PullV3ConservativeStartupTargetInFlightBytes = 512 * 1024;
    private const int PullV3ConservativeStartupProbeTargetInFlightBytes = 1024 * 1024;
    private const int PullV3ConservativeStartupDegradedTargetInFlightBytes = 256 * 1024;
    private const int PullV3HealthyAckThresholdBytes = 256 * 1024;
    private const int PullV3HealthyAckCoalesceDelayMs = 150;
    private const int PullV3FileOnlySparseAckCoalesceDelayMs = 75;
    private const int PullV3FileOnlySparseTargetInFlightBytesDefault = 16 * 1024 * 1024;
    private const int PullV3FileOnlySparseTargetInFlightBytesMin = 1024 * 1024;
    private const int PullV3FileOnlySparseTargetInFlightBytesMax = 16 * 1024 * 1024;
    private const int PullV3FileOnlySparseGrantLowWatermarkPercentDefault = 90;
    private const int PullV3FileOnlySparseGrantLowWatermarkPercentMin = 50;
    private const int PullV3FileOnlySparseGrantLowWatermarkPercentMax = 99;
    private const int PullV3FileOnlySparseGrantCoalesceMsDefault = 25;
    private const int PullV3FileOnlySparseGrantCoalesceMsMin = 10;
    private const int PullV3FileOnlySparseGrantCoalesceMsMax = 250;
    private const int PullV3SparseAheadGrantMaxBytesCurrentDefault = 4 * 1024 * 1024;
    private const int PullV3SparseAheadGrantMaxBytesDominantDefault = 8 * 1024 * 1024;
    private const int PullV3SparseAheadGrantMaxBytesMax = 8 * 1024 * 1024;
    private const int PullV3RepairRequestChunkCount = 4;
    private const int PullV3ProactiveFrontierRepairMinGapAgeMsDefault = 500;
    private const int PullV3ProactiveFrontierRepairMinGapAgeMsMin = 100;
    private const int PullV3ProactiveFrontierRepairMinGapAgeMsMax = 2500;
    private const int PullV3ProactiveFrontierRepairRepeatMinIntervalMsDefault = 500;
    private const int PullV3ProactiveFrontierRepairRepeatMinIntervalMsMin = 100;
    private const int PullV3ProactiveFrontierRepairRepeatMinIntervalMsMax = 2500;
    private const int PullV3ProactiveFrontierRepairChunkCountDefault = 32;
    private const int PullV3ProactiveFrontierRepairChunkCountMin = 4;
    private const int PullV3ProactiveFrontierRepairChunkCountMax = 64;
    private const int PullV3ProactiveFrontierRepairMinLateDistance = 8;
    private const int PullV3ProactiveRepairGraceMsDefault = 2500;
    private const int PullV3ProactiveRepairGraceMsMin = 500;
    private const int PullV3ProactiveRepairGraceMsMax = 5000;
    private const int PullV3RepairSetScanHorizonChunks = 256;
    private const int PullV3RepairSetRepeatMinIntervalMs = 1000;
    private const int PullV3GrantLowWatermarkDivisor = 2;
    private const int PullV3HealthyDefaultChunkSizeBytes = 40 * 1024;
    private const int PullV3ConservativeStartupChunkSizeBytes = 24 * 1024;
    private const int PullV3ScreenshareDefaultChunkSizeBytes = 24 * 1024;
    private const int PullV3DegradedDefaultChunkSizeBytes = 20 * 1024;
    private const int PullV3ConservativeStartupInitialPipelineDepth = 4;
    private const int PullV3ConservativeStartupProbeProgressBytesThreshold = 512 * 1024;
    private const int PullV3ConservativeStartupProbeHoldMs = 500;
    private const int PullV3ConservativeStartupStepUpProgressBytesThreshold = 1024 * 1024;
    private const int PullV3ConservativeStartupStepUpHoldMs = 500;
    private const int PullV3ProfileAdjustmentCooldownMs = 1500;
    private const int PullV3StepUpProgressBytesThreshold = 2 * 1024 * 1024;
    private const int PullV3HealthyStepUpHoldMs = 1000;
    private const int PullV3AdverseStepDownHoldMs = 2000;
    private const int PullV3HighReorderDistanceThreshold = 8;
    private const int PullV3PressureStateSuppressionMs = 1000;
    private const int PullV3PressureStateHealthyProgressDeltaChunks = 96;
    private const int PullV3PressureStateFileOnlySparseProgressDeltaChunks = 48;
    private const int PullV3PressureStateBalancedProgressDeltaChunks = 48;
    private const int PullV3PressureStateDegradedProgressDeltaChunks = 16;
    private const int PullV3BatchMaxChunks = 4;
    private const int PullV3LimitedReorderDistanceThreshold = 16;
    private const int PullV3LimitedStepDownHoldMs = 1000;
    private const int PullV3LimitedRecoveryHoldMs = 4000;
    private const int PullV3FileOnlySparseLimitedRecoveryHoldMsDefault = 750;
    private const int PullV3FileOnlySparseLimitedRecoveryHoldMsMin = 250;
    private const int PullV3FileOnlySparseLimitedRecoveryHoldMsMax = 4000;
    private const int PullV3FileOnlySparseSoftLimitedRecoveryHoldMsDefault = 500;
    private const int PullV3FileOnlySparseSoftLimitedRecoveryHoldMsMin = 100;
    private const int PullV3FileOnlySparseSoftLimitedRecoveryHoldMsMax = 4000;
    private const int PullV3FileOnlySparseToleratedReorderThreshold = 64;
    private const int PullV3FileOnlySparseSoftLimitedReorderThresholdDefault = 512;
    private const int PullV3FileOnlySparseSoftLimitedReorderThresholdMin = 64;
    private const int PullV3FileOnlySparseSoftLimitedReorderThresholdMax = 2048;
    private const int PullV3FileOnlySparseSoftGapStallMsDefault = 1500;
    private const int PullV3FileOnlySparseSoftGapStallMsMin = 750;
    private const int PullV3FileOnlySparseSoftGapStallMsMax = 2500;
    private const int PullV3FileOnlySparseLimitedGapStallMs = 2500;
    private const int PullV3SparseAheadGrantGapStallLimitMsDefault = 2500;
    private const int PullV3SparseAheadGrantGapStallLimitMsMin = 0;
    private const int PullV3SparseAheadGrantGapStallLimitMsMax = 2500;
    private const int PullV3SparseCreditTopupBytesCurrentDefault = 256 * 1024;
    private const int PullV3SparseCreditTopupBytesDominantDefault = 128 * 1024;
    private const int PullV3SparseCreditTopupBytesMin = 0;
    private const int PullV3SparseCreditTopupBytesMax = 2 * 1024 * 1024;
    private const int PullV3SparseCreditHoldMsDefault = 1500;
    private const int PullV3SparseCreditHoldMsMin = 0;
    private const int PullV3SparseCreditHoldMsMax = 4000;
    private const int PullV3FixedFileOnlyWindowBytesDefault = 0;
    private const int PullV3FixedFileOnlyWindowBytesMin = 0;
    private const int PullV3FixedFileOnlyWindowBytesMax = 64 * 1024 * 1024;
    private const int V4DefaultChunkSizeBytes = 21 * 1024;
    private const int V4FileOnlySparseCreditWindowBytes = 64 * 1024 * 1024;
    private const int V4SenderPumpDepth = 8;
    private const int V4SenderPumpPendingBytes = 2 * 1024 * 1024;
    private const int V4RepairRepeatIntervalMs = 750;
    private const int V4RepairRequestHistoryRetentionMs = 10000;
    private const int V4RepairRedundancyEscalationStallMs = 2000;
    private const int V4RepairBurstMaxChunks = 64;
    private const int V4RepairBatchSendAttempts = 1;
    private const int V4MaxBatchSegmentsDefault = 3;
    private const int V4MaxBatchSegmentsMin = 1;
    private const int V4MaxBatchSegmentsMax = 3;
    private const int V4NormalSendQuantumChunks = 24;
    private const int V4StateCreditGrantQuantumBytes = 1 * 1024 * 1024;
    private const int V4StateProgressCreditMinChunks = 48;
    private const int V4StateProgressMaxDelayMs = 250;
    private const int V4InitialFrontierRepairChunks = 12;
    private const int V4KnownFrontierRepairChunks = V4InitialFrontierRepairChunks;
    private const int V4FrontierTailRetryChunks = V4MaxBatchSegmentsDefault;
    private const string V3FileOnlyReorderPolicyEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY";
    private const string V3ProactiveGapRepairEnvironmentVariableName = "NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR";
    private const string V3ProactiveRepairPressureModeEnvironmentVariableName = "NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE";
    private const string V3ProactiveRepairGraceMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_GRACE_MS";
    private const string V3FrontierRepairMinGapMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS";
    private const string V3FrontierRepairRepeatMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS";
    private const string V3FrontierRepairChunksEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_CHUNKS";
    private const string V3FileOnlyTargetWindowBytesEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_TARGET_WINDOW_BYTES";
    private const string V3FileOnlyGrantLowWatermarkPercentEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_GRANT_LOW_WATERMARK_PERCENT";
    private const string V3FileOnlyGrantCoalesceMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_GRANT_COALESCE_MS";
    private const string V3SparseAheadGrantMaxBytesEnvironmentVariableName = "NLINK_FILETRANSFER_V3_SPARSE_AHEAD_GRANT_MAX_BYTES";
    private const string V3SparseCreditModeEnvironmentVariableName = "NLINK_FILETRANSFER_V3_SPARSE_CREDIT_MODE";
    private const string V3SparseCreditAccountingEnvironmentVariableName = "NLINK_FILETRANSFER_V3_SPARSE_CREDIT_ACCOUNTING";
    private const string V3SparseCreditTopupBytesEnvironmentVariableName = "NLINK_FILETRANSFER_V3_SPARSE_CREDIT_TOPUP_BYTES";
    private const string V3SparseCreditHoldMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_SPARSE_CREDIT_HOLD_MS";
    private const string V3FixedFileOnlyWindowBytesEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES";
    private const string V3AsyncSenderPumpEnvironmentVariableName = "NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP";
    private const string V3ReceiverFeedbackPumpEnvironmentVariableName = "NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP";
    private const string V3CreditKeepaliveGrantsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_CREDIT_KEEPALIVE_GRANTS";
    private const string V4MaxBatchSegmentsEnvironmentVariableName = "NLINK_FILETRANSFER_V4_MAX_BATCH_SEGMENTS";
    private const string V3FileOnlySoftLimitBytesEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_LIMIT_BYTES";
    private const string V3FileOnlySoftLimitReorderThresholdEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_LIMIT_REORDER_THRESHOLD";
    private const string V3FileOnlySoftGapStallMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_GAP_STALL_MS";
    private const string V3FileOnlySoftRecoveryMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_RECOVERY_MS";
    private const string V3FileOnlyLimitedRecoveryMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_FILE_ONLY_LIMITED_RECOVERY_MS";
    private const string V3SparseAheadGapStallLimitMsEnvironmentVariableName = "NLINK_FILETRANSFER_V3_SPARSE_AHEAD_GAP_STALL_LIMIT_MS";
    private const long ReceiverBufferSoftLimitBytes = 8L * 1024L * 1024L;
    private const long ReceiverBufferSevereLimitBytes = 16L * 1024L * 1024L;
    private const long ReceiverBufferEmergencyLimitBytes = 64L * 1024L * 1024L;
    private const long ReceiverBufferExitLimitBytes = 4L * 1024L * 1024L;
    private const int ReceiverWriteBatchMaxBytes = 1024 * 1024;
    private const int ReceiverWriteBatchMaxChunks = 64;
    private const long SenderRepairCacheSeekableTargetBytes = 8L * 1024L * 1024L;
    private const long SenderRepairCacheSeekableHardLimitBytes = 16L * 1024L * 1024L;
    private const long SenderRepairCacheNonSeekableHardLimitBytes = 64L * 1024L * 1024L;
    private const int V3ReceiverFeedbackPumpQueueLimit = 64;
    private const string V3SendPipelineDepthEnvironmentVariableName = "NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH";
    private const string V3SendPipelinePendingBytesEnvironmentVariableName = "NLINK_FILETRANSFER_V3_SEND_PIPELINE_PENDING_BYTES";
    private const int V3SendPipelineMinDepth = 1;
    private const int V3SendPipelineMaxDepth = 8;
    private const int V3SendPipelineDefaultNknFileOnlyDepth = 8;
    private const int V3SendPipelineDefaultOtherDepth = 1;
    private const long V3SendPipelinePendingBytesDefaultOtherLimit = 1024L * 1024L;
    private const long V3SendPipelinePendingBytesDefaultNknFileOnlyLimit = 2L * 1024L * 1024L;
    private const long V3SendPipelinePendingBytesMinLimit = 256L * 1024L;
    private const long V3SendPipelinePendingBytesMaxLimit = 8L * 1024L * 1024L;
    private const int V3CreditKeepaliveGrantIntervalMs = 250;
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
    private bool disposed;

    internal Func<string, string, Task>? InboundDispatchBeforeWorkAsyncForTests { get; set; }

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

    internal void SetFlowControlMode(FileTransferFlowControlMode mode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? receivingContext = null;
        var startupPhasePending = false;
        var nextPolicy = FileTransferFlowControlPolicy.ForMode(mode);

        lock (gate)
        {
            if (flowControlPolicy == nextPolicy)
            {
                return;
            }

            flowControlPolicy = nextPolicy;
            if (inboundTransfer is not null &&
                !inboundTransfer.IsTerminal &&
                inboundTransfer.State is FileTransferTransferState.Receiving or FileTransferTransferState.Verifying &&
                !inboundTransfer.PullSessionActive)
            {
                receivingContext = inboundTransfer;
                startupPhasePending = !inboundTransfer.StartupPhaseCompleted;
            }
        }

        if (receivingContext is not null)
        {
            _ = SendWindowUpdateAsync(
                receivingContext,
                startupPhasePending ? WindowUpdateTrigger.StartupResend : WindowUpdateTrigger.SteadyStateResend,
                CancellationToken.None);
        }
    }

    internal void SetSessionScreenShareActive(bool active)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? receivingContext = null;
        var startupPhasePending = false;
        OutboundTransferContext? activeOutboundContext = null;
        InboundTransferContext? activeInboundPullContext = null;
        int previousInboundPipelineDepth = 0;
        int updatedInboundPipelineDepth = 0;

        lock (gate)
        {
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
                receivingContext = inboundTransfer;
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

        if (receivingContext is not null && startupPhasePending)
        {
            if (receivingContext.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3 &&
                receivingContext.PullSessionActive)
            {
                _ = SendInboundGrantWindowV3Async(receivingContext, forceGrant: true);
            }
            else
            {
                _ = SendWindowUpdateAsync(receivingContext, WindowUpdateTrigger.StartupResend, CancellationToken.None);
            }
        }
        else if (activeInboundPullContext is not null &&
                 activeInboundPullContext.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3)
        {
            _ = SendInboundGrantWindowV3Async(activeInboundPullContext, forceGrant: true);
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

            if (activeInboundPullContext.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV3)
            {
                _ = MaybeSendNextChunkRequestAsync(activeInboundPullContext, forceResendOldestOutstanding: false);
            }
        }
    }

    internal void SetSessionScreenShareDegraded(bool active)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? receivingContext = null;
        InboundTransferContext? activeInboundPullContext = null;
        int previousInboundPipelineDepth = 0;
        int updatedInboundPipelineDepth = 0;
        lock (gate)
        {
            if (sessionScreenShareDegraded == active)
            {
                return;
            }

            sessionScreenShareDegraded = active;
            if (inboundTransfer is not null &&
                !inboundTransfer.IsTerminal &&
                inboundTransfer.State is FileTransferTransferState.Receiving or FileTransferTransferState.Verifying)
            {
                receivingContext = inboundTransfer;
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

        if (receivingContext is not null)
        {
            if (receivingContext.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3 &&
                receivingContext.PullSessionActive)
            {
                _ = SendInboundGrantWindowV3Async(receivingContext, forceGrant: true);
            }
            else
            {
                _ = MaybeSendPressureStateAsync(receivingContext, CancellationToken.None);
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

            if (activeInboundPullContext.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV3)
            {
                _ = MaybeSendNextChunkRequestAsync(activeInboundPullContext, forceResendOldestOutstanding: false);
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
        transport.FileTransferStartReceived += OnFileTransferStartReceived;
        transport.FileTransferChunkReceived += OnFileTransferChunkReceived;
        transport.FileTransferWindowUpdateReceived += OnFileTransferWindowUpdateReceived;
        transport.FileTransferMissingRangeReceived += OnFileTransferMissingRangeReceived;
        transport.FileTransferPressureStateReceived += OnFileTransferPressureStateReceived;
        transport.FileTransferCancelReceived += OnFileTransferCancelReceived;
        transport.FileTransferErrorReceived += OnFileTransferErrorReceived;
        transport.FileTransferCompleteReceived += OnFileTransferCompleteReceived;

        if (transport is ISignalingTransport lifecycleTransport)
        {
            transportLifecycle = lifecycleTransport;
            lifecycleTransport.Rejected += OnTransportRejectedOrDisconnected;
            lifecycleTransport.Disconnected += OnTransportRejectedOrDisconnected;
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
            inboundTransfer = null;
            outboundTransfer = null;
        }

        inbound?.CancelLifetime();
        outbound?.CancelLifetime();
        inbound?.DisposeResources();
        outbound?.DisposeResources();
        RaiseTransferChanged(CreateSnapshot());
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
            outboundTransfer = context;
        }

        RaiseTransferChanged(CreateSnapshot());

        if (!IsV4StreamingTransport(currentTransport))
        {
            LogV4RequiredTransportIncompatible(context.TransferId, context.SessionId);
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: TransportIncompatibleErrorCode,
                statusMessage: "File transfer requires V4 streaming support from the attached transport.",
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
            PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
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
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: ClassifyOutboundFailureErrorCode(ex, InvalidStateErrorCode),
                statusMessage: ex.Message,
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
            context.NegotiatedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4;
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
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                },
                ct).ConfigureAwait(false);
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
                ct: ct).ConfigureAwait(false);
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
                ct: ct).ConfigureAwait(false);
            return CaptureCurrentInboundSnapshot();
        }

        return null;
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
                    offeredVersion: FileTransferProtocol.ProtocolVersionV4,
                    acceptedVersion: context.NegotiatedDataProtocolVersion,
                    reason: "prepared_protocol_not_v4");
                await TransitionOutboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Failed,
                    errorCode: TransportIncompatibleErrorCode,
                    statusMessage: "File transfer requires V4 data protocol.",
                    notifyPeer: false,
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!await PrepareAcceptedOutboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false))
            {
                return;
            }

            LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound);
            if (sessionScreenShareActive || sessionScreenShareDegraded)
            {
                await FailOutboundV4Async(
                    context,
                    dataSession: null,
                    V4FileOnlyRequiredErrorCode,
                    "V4 file-transfer send is currently file-only.",
                    notifyPeer: false).ConfigureAwait(false);
                return;
            }

            await RunOutboundV4SenderAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode),
                statusMessage: ex.Message,
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

    private void OnFileTransferStartReceived(object? sender, FileTransferStartReceivedEventArgs e)
        => EnqueueInboundLifecycle("start", () => HandleIncomingStartAsync(e.Message));

    private void OnFileTransferChunkReceived(object? sender, FileTransferChunkReceivedEventArgs e)
        => EnqueueInboundChunk("chunk", () => HandleIncomingChunkAsync(e.Message));

    private void OnFileTransferWindowUpdateReceived(object? sender, FileTransferWindowUpdateReceivedEventArgs e)
        => EnqueueInboundControl("window_update", () => HandleIncomingWindowUpdateAsync(e.Message));

    private void OnFileTransferMissingRangeReceived(object? sender, FileTransferMissingRangeReceivedEventArgs e)
        => EnqueueInboundControl("missing_range", () => HandleIncomingMissingRangeAsync(e.Message));

    private void OnFileTransferPressureStateReceived(object? sender, FileTransferPressureStateReceivedEventArgs e)
        => EnqueueInboundControl("pressure_state", () => HandleIncomingPressureStateAsync(e.Message));

    private void OnFileTransferCancelReceived(object? sender, FileTransferCancelReceivedEventArgs e)
        => EnqueueInboundLifecycle("cancel", () => HandleIncomingCancelAsync(e.Message));

    private void OnFileTransferErrorReceived(object? sender, FileTransferErrorReceivedEventArgs e)
        => EnqueueInboundLifecycle("error", () => HandleIncomingErrorAsync(e.Message));

    private void OnFileTransferCompleteReceived(object? sender, FileTransferCompleteReceivedEventArgs e)
        => EnqueueInboundLifecycle("complete", () => HandleIncomingCompleteAsync(e.Message));

    private void OnTransportRejectedOrDisconnected(object? sender, EventArgs e)
        => EnqueueInboundLifecycle("transport disconnect", HandleTransportRejectedOrDisconnectedAsync);

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

    private async Task HandleTransportRejectedOrDisconnectedAsync()
    {
        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        string? outboundPausedTransferId = null;
        string? outboundPausedSessionId = null;
        string? inboundPausedTransferId = null;
        string? inboundPausedSessionId = null;
        lock (gate)
        {
            outbound = outboundTransfer is { IsTerminal: false } ? outboundTransfer : null;
            inbound = inboundTransfer is { IsTerminal: false } ? inboundTransfer : null;

            if (outbound is not null &&
                (TryPauseOutboundTransportLocked(outbound, "transport_disconnected", true) || outbound.PullTransportPaused))
            {
                if (outboundPausedTransferId is null)
                {
                    outboundPausedTransferId = outbound.TransferId;
                    outboundPausedSessionId = outbound.SessionId;
                }

                outbound = null;
            }

            if (inbound is not null &&
                (TryPauseInboundTransportLocked(inbound, "transport_disconnected", true) || inbound.PullTransportPaused))
            {
                if (inboundPausedTransferId is null)
                {
                    inboundPausedTransferId = inbound.TransferId;
                    inboundPausedSessionId = inbound.SessionId;
                }

                inbound = null;
            }
        }

        if (outboundPausedTransferId is not null && outboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Outbound, outboundPausedTransferId, outboundPausedSessionId, "transport_disconnected");
        }

        if (inboundPausedTransferId is not null && inboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Inbound, inboundPausedTransferId, inboundPausedSessionId, "transport_disconnected");
        }

        if (outbound is not null)
        {
            await TransitionOutboundToTerminalAsync(
                outbound,
                FileTransferTransferState.Failed,
                errorCode: DisconnectedErrorCode,
                statusMessage: "Transport disconnected.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }

        if (inbound is not null)
        {
            await TransitionInboundToTerminalAsync(
                inbound,
                FileTransferTransferState.Failed,
                errorCode: DisconnectedErrorCode,
                statusMessage: "Transport disconnected.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
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
                reason: "offer_protocol_not_v4");
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
        var acceptedVersionIsV4 = IsNegotiableDataProtocolVersion(message.AcceptedDataProtocolVersion);
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

            if (!acceptedVersionIsV4)
            {
                // Leave SendStarted false so the transfer remains visibly rejected by negotiation,
                // not partially started.
            }
            else
            {
                context.SendStarted = true;
                context.SessionId = message.SessionId;
                context.NegotiatedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4;
                context.State = FileTransferTransferState.PreparingMetadata;
                context.StatusMessage = "Preparing file metadata.";
            }
        }

        if (!acceptedVersionIsV4)
        {
            LogLegacyNegotiationRejected(
                message.TransferId,
                message.SessionId,
                FileTransferDirection.Outbound,
                offeredVersion: FileTransferProtocol.ProtocolVersionV4,
                acceptedVersion: message.AcceptedDataProtocolVersion,
                reason: "accept_protocol_not_v4");
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: TransportIncompatibleErrorCode,
                statusMessage: "Receiver did not accept the required V4 file-transfer protocol.",
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
        bool deferUntilStart = false;
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
                        reason: "session_open_protocol_not_v4");
                    LogV4SessionOpenRejected(
                        message.TransferId,
                        message.SessionId,
                        FileTransferDirection.Inbound,
                        message.ProtocolVersion,
                        "session_open_protocol_not_v4");
                }
                context = null;
            }
            else if (message.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4)
            {
                if (context.State is not FileTransferTransferState.AwaitingMetadata and not FileTransferTransferState.Receiving)
                {
                    context = null;
                }
            }
            else if (context.State == FileTransferTransferState.AwaitingMetadata)
            {
                context.PendingSessionOpen = message;
                deferUntilStart = true;
            }
            else if (context.State is not FileTransferTransferState.AwaitingStart and not FileTransferTransferState.Receiving)
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
            if (sessionScreenShareActive || sessionScreenShareDegraded)
            {
                LogV4SessionOpenRejected(
                    message.TransferId,
                    message.SessionId,
                    FileTransferDirection.Inbound,
                    message.ProtocolVersion,
                    "file_only_required");
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
                _ = RunInboundV4SparseReceiveLoopAsync(context, message);
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

        if (deferUntilStart)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_session_open_deferred_until_start; transfer_id={message.TransferId}; session_id={message.SessionId}");
            return;
        }

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
                context.NegotiatedDataProtocolVersion = message.ProtocolVersion;
                context.PullSessionDegraded = sessionScreenShareDegraded;
                context.PullCurrentPipelineDepth = ResolveInboundMaximumPipelineDepthLocked(context);
                context.StatusMessage = "Negotiating file-transfer session.";
            }

            LogTransferInfo(
                "filetransfer_session_opened",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                reason: $"role={message.SessionRole}; protocol_version={message.ProtocolVersion}; chunk_size_bytes={message.ChunkSizeBytes}; pipeline_depth={message.InitialPipelineDepth}");
            LogPullChunkProfile(
                message.TransferId,
                message.SessionId,
                message.ChunkSizeBytes,
                context.PullCurrentPipelineDepth,
                screenshareActive: sessionScreenShareActive,
                screenshareDegraded: sessionScreenShareDegraded);
            _ = RunInboundPullReceiveLoopV3Async(context, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not open the dedicated file-transfer session.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnDataSessionAvailabilityChanged(object? sender, FileTransferDataSessionAvailabilityChangedEventArgs e)
    {
        if (sender is not IFileTransferDataSession dataSession)
        {
            return;
        }

        EnqueueInboundLifecycle(
            "filetransfer data session availability changed",
            () => HandleDataSessionAvailabilityChangedAsync(dataSession, e));
    }

    private async Task HandleDataSessionAvailabilityChangedAsync(
        IFileTransferDataSession dataSession,
        FileTransferDataSessionAvailabilityChangedEventArgs availability)
    {
        OutboundTransferContext? outboundToResume = null;
        InboundTransferContext? inboundToResume = null;
        string? outboundPausedTransferId = null;
        string? outboundPausedSessionId = null;
        string? inboundPausedTransferId = null;
        string? inboundPausedSessionId = null;
        bool outboundResumed = false;
        bool inboundResumed = false;

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer?.DataSession, dataSession) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                if (availability.IsAvailable)
                {
                    outboundResumed = TryResumeOutboundTransportLocked(outbound, availability.Reason, availability.RequiresResumeRequest);
                    if (outboundResumed)
                    {
                        outboundToResume = outbound;
                    }
                }
                else if (TryPauseOutboundTransportLocked(outbound, availability.Reason, availability.RequiresResumeRequest))
                {
                    outboundPausedTransferId = outbound.TransferId;
                    outboundPausedSessionId = outbound.SessionId;
                }
            }

            if (ReferenceEquals(inboundTransfer?.DataSession, dataSession) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                if (availability.IsAvailable)
                {
                    inboundResumed = TryResumeInboundTransportLocked(inbound, availability.Reason, availability.RequiresResumeRequest);
                    if (inboundResumed)
                    {
                        inboundToResume = inbound;
                    }
                }
                else if (TryPauseInboundTransportLocked(inbound, availability.Reason, availability.RequiresResumeRequest))
                {
                    inboundPausedTransferId = inbound.TransferId;
                    inboundPausedSessionId = inbound.SessionId;
                }
            }
        }

        if (outboundPausedTransferId is not null && outboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Outbound, outboundPausedTransferId, outboundPausedSessionId, availability.Reason);
        }

        if (inboundPausedTransferId is not null && inboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Inbound, inboundPausedTransferId, inboundPausedSessionId, availability.Reason);
        }

        if (outboundResumed && outboundToResume is not null)
        {
            LogTransportResumed(FileTransferDirection.Outbound, outboundToResume.TransferId, outboundToResume.SessionId, availability.Reason, availability.RequiresResumeRequest);
        }

        if (inboundResumed && inboundToResume is not null)
        {
            LogTransportResumed(FileTransferDirection.Inbound, inboundToResume.TransferId, inboundToResume.SessionId, availability.Reason, availability.RequiresResumeRequest);
            if (availability.RequiresResumeRequest)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_transport_rebind_started; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={availability.Reason}");
                try
                {
                    if (inboundToResume.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3)
                    {
                        await SendInboundGrantWindowV3Async(inboundToResume, forceGrant: true).ConfigureAwait(false);
                    }
                    else
                    {
                        await MaybeSendNextChunkRequestAsync(inboundToResume, forceResendOldestOutstanding: true).ConfigureAwait(false);
                    }

                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_transport_rebind_succeeded; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={availability.Reason}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_transport_rebind_failed; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={availability.Reason}; error={ex.Message}");
                    await TransitionInboundToTerminalAsync(
                        inboundToResume,
                        FileTransferTransferState.Failed,
                        errorCode: DisconnectedErrorCode,
                        statusMessage: "Transport disconnected.",
                        sendError: true,
                        errorMessage: "Transport disconnected.",
                        cancelReason: null,
                        ct: CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleIncomingStartAsync(FileTransferStartV2 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        InboundTransferContext? context;
        FileTransferSessionOpenV2? pendingSessionOpen = null;
        SessionFileTransferSnapshot? snapshot = null;
        string? terminalErrorCode = null;
        string? terminalStatus = null;
        FileTransferReceiveDestination? destination = null;

        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return;
            }

            if (context.State is not FileTransferTransferState.AwaitingMetadata and not FileTransferTransferState.AwaitingStart)
            {
                terminalErrorCode = InvalidStateErrorCode;
                terminalStatus = "Start message arrived in an invalid state.";
            }
            else if (!string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal) ||
                     !string.Equals(context.FileName, message.FileName, StringComparison.Ordinal) ||
                     context.FileSizeBytes != message.FileSizeBytes)
            {
                terminalErrorCode = InvalidStateErrorCode;
                terminalStatus = "Start metadata did not match the original offer.";
            }
            else if (message.ChunkCount <= 0 || message.ChunkSizeBytes <= 0)
            {
                terminalErrorCode = InvalidStateErrorCode;
                terminalStatus = "Start metadata was invalid.";
            }
            else if (!TryCalculateExpectedChunkCount(context.FileSizeBytes, message.ChunkSizeBytes, out var expectedChunkCount) ||
                     message.ChunkCount != expectedChunkCount)
            {
                terminalErrorCode = InvalidStateErrorCode;
                terminalStatus = "Start chunk metadata did not match the declared file size.";
            }
        }

        if (terminalErrorCode is null && context is not null && (context.WriteStream is null || context.Hash is null))
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeCts.Token);
                destination = await context.OpenWriteDestinationAsync!(context.CreateOffer(), linkedCts.Token).ConfigureAwait(false);
                ValidateWritableStream(destination.Stream);
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                terminalErrorCode = StreamOpenFailedErrorCode;
                terminalStatus = ex.Message;
            }
        }

        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                destination?.Dispose();
                return;
            }

            if (terminalErrorCode is null && destination is not null)
            {
                context.WriteDestination = destination;
                context.WriteStream = destination.Stream;
                context.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                destination = null;
            }

            if (terminalErrorCode is null && (context.WriteStream is null || context.Hash is null))
            {
                terminalErrorCode = StreamOpenFailedErrorCode;
                terminalStatus = "Could not open the destination stream.";
            }

            if (terminalErrorCode is null)
            {
                context.Sha256Base64 = message.Sha256Base64;
                context.MetadataAwaitingSinceUtc = null;
                context.ChunkCount = message.ChunkCount;
                context.ChunkSizeBytes = message.ChunkSizeBytes;
                context.NextChunkIndex = 0;
                context.BufferedBytes = 0;
                context.HighestBufferedChunkIndex = -1;
                context.LastAdvertisedGrantedUntilExclusive = 0;
                context.LastAdvertisedNextChunkIndex = 0;
                context.LastAdvertisedCreditFrontier = 0;
                context.LastWindowUpdateSentUtc = null;
                context.LastForcedWindowUpdateSentUtc = null;
                context.LastContiguousProgressUtc = DateTimeOffset.UtcNow;
                context.LastBufferedFrontierAdvanceUtc = DateTimeOffset.UtcNow;
                context.LastUsefulBulkProgressUtc = DateTimeOffset.UtcNow;
                context.LastBulkDispatchedChunkUtc = null;
                context.BulkDispatchedChunksSinceLastUsefulProgress = 0;
                context.ObsoleteChunksArrivedSinceLastUsefulProgress = 0;
                context.BulkUnhealthyDetected = false;
                context.BulkFallbackModeActive = false;
                context.ConsecutiveBulkUnhealthyDetections = 0;
                context.LastBulkUnhealthyLogUtc = null;
                context.StartupPhaseCompleted = false;
                context.SteadyStateWindowAdvertised = false;
                context.OldestGapStartChunkIndex = null;
                context.OldestGapFirstSeenUtc = null;
                context.OutstandingMissingRange = null;
                context.LastMissingRangeSentUtc = null;
                context.RecentMissingRangeSentUtc.Clear();
                context.RecentGapProgressAckSentUtc.Clear();
                context.RecentContiguousProgressChunkUtc.Clear();
                context.RecentObsoleteChunkArrivalUtc.Clear();
                context.PendingChunks.Clear();
                context.ReceiverSparseWriteActive = false;
                context.ReceiverSparseChunksWritten = null;
                context.ReceiverSparseChunksPendingWrite.Clear();
                context.ReceiverSparseBytesWritten = 0;
                context.PullReceiverSparseWriteBytesRecent = 0;
                context.PullReceiverSparseWriteBatchCountRecent = 0;
                context.PullReceiverSparseWriteDurationMsRecent = 0;
                context.PullReceiverSparseChunksWrittenRecent = 0;
                context.PullReceiverSparseContiguousChunksCommittedRecent = 0;
                context.PullV3LastSparseCreditEligibleUtc = null;
                context.PullV3LastSparseCreditBaseChunkIndex = 0;
                context.ReceiverBufferPressureActive = false;
                context.ReceiverBufferPressureSinceUtc = null;
                context.BytesTransferred = 0;
                context.ChunksTransferred = 0;
                context.State = FileTransferTransferState.Receiving;
                context.StatusMessage = "Receiving file data.";
                pendingSessionOpen = context.PendingSessionOpen;
                context.PendingSessionOpen = null;
                snapshot = CreateSnapshotLocked();
            }
        }

        destination?.Dispose();

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            LogTransferInfo(
                "start_received",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                fileName: message.FileName,
                fileSizeBytes: message.FileSizeBytes,
                reason: $"chunk_count={message.ChunkCount}; chunk_size_bytes={message.ChunkSizeBytes}");
            _ = SendWindowUpdateAsync(context, WindowUpdateTrigger.Startup, CancellationToken.None);
            _ = RunInboundWindowRefreshWatchdogAsync(context);
            _ = RunInboundGapRecoveryWatchdogAsync(context);
            if (pendingSessionOpen is not null)
            {
                _ = HandleIncomingSessionOpenAsync(pendingSessionOpen);
            }
            return;
        }

        if (context is not null && terminalErrorCode is not null)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: terminalErrorCode,
                statusMessage: terminalStatus ?? "Inbound transfer failed.",
                sendError: true,
                errorMessage: terminalStatus,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleIncomingChunkAsync(FileTransferChunkV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] chunkBytes;
        try
        {
            chunkBytes = Convert.FromBase64String(message.DataBase64);
        }
        catch (FormatException)
        {
            chunkBytes = Array.Empty<byte>();
        }

        InboundTransferContext? context;
        Stream? writeStream = null;
        IncrementalHash? hash = null;
        List<byte[]> contiguousChunkBytes = [];
        int contiguousChunkCount = 0;
        long nextBytesTransferred = 0;
        int nextChunksTransferred = 0;
        int nextChunkIndex = 0;
        bool shouldFinalize = false;
        string? failureCode = null;
        string? failureMessage = null;
        bool bufferedWithoutProgress = false;
        bool duplicateIgnored = false;
        bool shouldLogDeferredGapExtension = false;
        int deferredGapHighestBufferedChunkIndex = -1;
        int deferredGapGrantedUntilExclusive = 0;
        WindowUpdateTrigger? windowUpdateTrigger = null;
        bool shouldRequestMissingRange = false;
        bool shouldLogStartupCompleted = false;
        long startupCompletedBytesReceived = 0;
        int startupCompletedNextExpectedChunk = 0;
        int startupCompletedHighestBufferedChunk = -1;
        var now = DateTimeOffset.UtcNow;

        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return;
            }

            if (context.State != FileTransferTransferState.Receiving)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Chunk arrived in an invalid state.";
            }
            else if (message.ChunkCount != context.ChunkCount)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Chunk metadata did not match the active transfer.";
            }
            else if (context.WriteStream is null || context.Hash is null)
            {
                failureCode = StreamOpenFailedErrorCode;
                failureMessage = "Destination stream is unavailable.";
                }
                else
                {
                    context.LastBulkDispatchedChunkUtc = now;
                    context.BulkDispatchedChunksSinceLastUsefulProgress++;

                    if (chunkBytes.Length == 0)
                    {
                        failureCode = InvalidStateErrorCode;
                        failureMessage = "Chunk payload was not valid base64.";
                }
                else if (message.ChunkIndex < 0 || message.ChunkIndex >= context.ChunkCount)
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Chunk index exceeded the declared transfer bounds.";
                }
                else if (message.ChunkIndex < context.NextChunkIndex)
                {
                    duplicateIgnored = true;
                    context.ObsoleteChunksArrivedSinceLastUsefulProgress++;
                    context.RecentObsoleteChunkArrivalUtc.Enqueue(now);
                }
                else if (context.PendingChunks.ContainsKey(message.ChunkIndex))
                {
                    duplicateIgnored = true;
                }
                else if (
                    (chunkBytes.Length == 0 ||
                     chunkBytes.Length > context.ChunkSizeBytes ||
                     context.BytesTransferred + context.BufferedBytes + chunkBytes.Length > context.FileSizeBytes))
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Chunk payload exceeded the declared transfer bounds.";
                }

                if (failureCode is null && !duplicateIgnored)
                {
                    context.PendingChunks[message.ChunkIndex] = chunkBytes;
                    context.BufferedBytes += chunkBytes.Length;
                    if (message.ChunkIndex > context.HighestBufferedChunkIndex)
                    {
                        context.HighestBufferedChunkIndex = message.ChunkIndex;
                        context.LastBufferedFrontierAdvanceUtc = now;
                        RecordInboundUsefulBulkProgressLocked(context, now, clearGapState: false);
                    }
                    writeStream = context.WriteStream;
                    hash = context.Hash;
                    nextBytesTransferred = context.BytesTransferred;
                    nextChunksTransferred = context.ChunksTransferred;
                    nextChunkIndex = context.NextChunkIndex;
                    while (context.PendingChunks.TryGetValue(nextChunkIndex, out var pendingChunkBytes))
                    {
                        contiguousChunkBytes.Add(pendingChunkBytes);
                        nextBytesTransferred += pendingChunkBytes.Length;
                        nextChunksTransferred++;
                        nextChunkIndex++;
                    }

                    contiguousChunkCount = contiguousChunkBytes.Count;
                    shouldFinalize = nextChunkIndex == context.ChunkCount;
                    bufferedWithoutProgress = contiguousChunkCount == 0;
                    if (bufferedWithoutProgress)
                    {
                        UpdateOldestGapTrackingLocked(context);
                        UpdateInboundDegradedRepairModeLocked(context);
                        var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
                        var creditFrontier = GetCreditFrontierLocked(context, highestBufferedChunkIndex);
                        var rawTargetGrantedUntilExclusive = Math.Min(context.ChunkCount, creditFrontier + flowControlPolicy.GrantChunks);
                        if (ShouldDeferGrantExtensionDueToGapLocked(context, highestBufferedChunkIndex, rawTargetGrantedUntilExclusive) &&
                            ShouldLogGapDeferredLocked(context))
                        {
                            shouldLogDeferredGapExtension = true;
                            deferredGapHighestBufferedChunkIndex = highestBufferedChunkIndex;
                            deferredGapGrantedUntilExclusive = context.LastAdvertisedGrantedUntilExclusive;
                        }
                        shouldRequestMissingRange = ShouldRequestMissingRangeLocked(context);
                    }
                }
            }
        }

        if (duplicateIgnored)
        {
            return;
        }

        if (failureCode is not null && context is not null)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: failureCode,
                statusMessage: failureMessage ?? "Inbound transfer failed.",
                sendError: true,
                errorMessage: failureMessage,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (bufferedWithoutProgress)
        {
            if (shouldLogDeferredGapExtension)
            {
                LogWindowExtensionDeferredDueToGap(
                    context!.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    deferredGapHighestBufferedChunkIndex,
                    deferredGapGrantedUntilExclusive);
            }

            if (shouldRequestMissingRange)
            {
                _ = SendMissingRangeAsync(context!, CancellationToken.None);
            }

            _ = MaybeSendPressureStateAsync(context!, CancellationToken.None);

            return;
        }

        try
        {
            foreach (var contiguousChunk in contiguousChunkBytes)
            {
                await writeStream!.WriteAsync(contiguousChunk, CancellationToken.None).ConfigureAwait(false);
                hash!.AppendData(contiguousChunk);
            }
        }
        catch (Exception ex)
        {
            await TransitionInboundToTerminalAsync(
                context!,
                FileTransferTransferState.Failed,
                errorCode: StreamWriteFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not write the received chunk.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context!.IsTerminal)
            {
                var previousNextChunkIndex = context.NextChunkIndex;
                for (var index = context.NextChunkIndex; index < nextChunkIndex; index++)
                {
                    if (context.PendingChunks.Remove(index, out var pendingChunkBytes))
                    {
                        context.BufferedBytes -= pendingChunkBytes.Length;
                    }
                }

                context.BytesTransferred = nextBytesTransferred;
                context.ChunksTransferred = nextChunksTransferred;
                context.NextChunkIndex = nextChunkIndex;
                var contiguousProgressChunkCount = Math.Max(0, nextChunkIndex - previousNextChunkIndex);
                if (contiguousProgressChunkCount > 0)
                {
                    RecordInboundContiguousProgressLocked(context, now, contiguousProgressChunkCount);
                }
                RefreshHighestBufferedChunkIndexLocked(context);
                UpdateOldestGapTrackingLocked(context);
                UpdateInboundDegradedRepairModeLocked(context);
                if (context.OutstandingMissingRange is not null &&
                    nextChunkIndex >= context.OutstandingMissingRange.Value.EndChunkIndexExclusive)
                {
                    context.OutstandingMissingRange = null;
                }
                context.State = shouldFinalize
                    ? FileTransferTransferState.Verifying
                    : FileTransferTransferState.Receiving;
                context.StatusMessage = shouldFinalize
                    ? "Verifying received file."
                    : "Receiving file data.";
                var progressAdvanced = nextChunkIndex > previousNextChunkIndex;
                if (progressAdvanced)
                {
                    context.LastContiguousProgressUtc = now;
                    RecordInboundUsefulBulkProgressLocked(context, now, clearGapState: context.OldestGapStartChunkIndex is null);
                    if (!context.StartupPhaseCompleted && nextChunkIndex > 0)
                    {
                        context.StartupPhaseCompleted = true;
                        context.LastForcedWindowUpdateSentUtc = null;
                        shouldLogStartupCompleted = true;
                        startupCompletedBytesReceived = context.BytesTransferred;
                        startupCompletedNextExpectedChunk = context.NextChunkIndex;
                        startupCompletedHighestBufferedChunk = GetCurrentHighestBufferedChunkIndexLocked(context);
                    }
                }

                if (!shouldFinalize && TryGetWindowUpdateRefreshTriggerLocked(context, out var trigger))
                {
                    windowUpdateTrigger = trigger;
                }
                snapshot = CreateSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context!, FileTransferDirection.Inbound);
        }

        if (shouldLogStartupCompleted)
        {
            LogWindowStartupCompleted(
                context!.TransferId,
                context.SessionId,
                startupCompletedNextExpectedChunk,
                startupCompletedHighestBufferedChunk,
                startupCompletedBytesReceived);
        }

        if (!shouldFinalize)
        {
            _ = MaybeSendPressureStateAsync(context!, CancellationToken.None);
            if (windowUpdateTrigger is not null)
            {
                _ = SendWindowUpdateAsync(context!, windowUpdateTrigger.Value, CancellationToken.None);
            }
            return;
        }

        await FinalizeInboundTransferAsync(context!, CancellationToken.None).ConfigureAwait(false);
    }

    private Task HandleIncomingCancelAsync(FileTransferCancelV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = outboundTransfer is not null &&
                       !outboundTransfer.IsTerminal &&
                       string.Equals(outboundTransfer.TransferId, message.TransferId, StringComparison.Ordinal)
                ? outboundTransfer
                : null;
            inbound = inboundTransfer is not null &&
                      !inboundTransfer.IsTerminal &&
                      string.Equals(inboundTransfer.TransferId, message.TransferId, StringComparison.Ordinal)
                ? inboundTransfer
                : null;
        }

        if (outbound is not null)
        {
            LogTransferInfo(
                "cancel_received",
                FileTransferDirection.Outbound,
                message.TransferId,
                sessionId: message.SessionId,
                reason: NormalizeReason(message.Reason));
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
            LogTransferInfo(
                "cancel_received",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                reason: NormalizeReason(message.Reason));
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
            outbound = outboundTransfer is not null &&
                       !outboundTransfer.IsTerminal &&
                       string.Equals(outboundTransfer.TransferId, message.TransferId, StringComparison.Ordinal)
                ? outboundTransfer
                : null;
            inbound = inboundTransfer is not null &&
                      !inboundTransfer.IsTerminal &&
                      string.Equals(inboundTransfer.TransferId, message.TransferId, StringComparison.Ordinal)
                ? inboundTransfer
                : null;
        }

        if (outbound is not null)
        {
            LogTransferInfo(
                "error_received",
                FileTransferDirection.Outbound,
                message.TransferId,
                sessionId: message.SessionId,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                reason: NormalizeReason(message.Message));
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
            LogTransferInfo(
                "error_received",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                reason: NormalizeReason(message.Message));
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
                (
                    context.State == FileTransferTransferState.AwaitingCompletion ||
                    (context.PullSessionActive && context.ChunksTransferred >= context.ChunkCount && context.ChunkCount > 0) ||
                    (context.NextChunkIndexToRead >= context.ChunkCount && context.PendingRepairChunkIndices.Count == 0)
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
            context.State = FileTransferTransferState.AwaitingCompletion;
            context.StatusMessage = "Waiting for receiver verification.";
        }

        LogTransferInfo(
            "complete_received",
            FileTransferDirection.Outbound,
            message.TransferId,
            sessionId: message.SessionId,
            fileSizeBytes: message.FileSizeBytes);
        return TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Completed,
            errorCode: null,
            statusMessage: "Transfer complete.",
            notifyPeer: false,
            cancelReason: null,
            ct: CancellationToken.None);
    }


    private async Task FinalizeInboundTransferAsync(InboundTransferContext context, CancellationToken ct)
    {
        Stream? writeStream;
        IncrementalHash? hash;
        long bytesTransferred;
        string expectedHash;
        string sessionId;
        string transferId;
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
            sparseMode = context.ReceiverSparseWriteActive;
            negotiatedDataProtocolVersion = context.NegotiatedDataProtocolVersion;
            destination = context.WriteDestination;
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

        if (bytesTransferred != context.FileSizeBytes)
        {
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
            if (context.PullSessionActive && context.DataSession is not null)
            {
                if (negotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4)
                {
                    if (!await SendInboundV4CompleteAsync(
                            context,
                            sessionId,
                            transferId,
                            context.FileSizeBytes,
                            computedHash,
                            ct).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                else
                {
                    var completeFrame = new FileTransferCompleteFrameV2
                    {
                        SessionId = sessionId,
                        TransferId = transferId,
                        FileSizeBytes = context.FileSizeBytes,
                        Sha256Base64 = computedHash,
                    };
                    if (!await SendOrQueueInboundV3ReceiverFeedbackAsync(
                            context,
                            completeFrame,
                            "complete",
                            waitForSend: true).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            else
            {
                var currentTransport = GetTransportOrThrow();
                await currentTransport.SendFileTransferCompleteAsync(
                    new FileTransferCompleteV1
                    {
                        SessionId = sessionId,
                        TransferId = transferId,
                        FileSizeBytes = context.FileSizeBytes,
                        Sha256Base64 = computedHash,
                    },
                    ct).ConfigureAwait(false);
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

            return Convert.ToBase64String(hash.GetHashAndReset());
        }
        finally
        {
            stopwatch.Stop();
            ArrayPool<byte>.Shared.Return(buffer);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_receiver_sparse_hash_readback_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; read_bytes={readBytes}; expected_bytes={context.FileSizeBytes}; readback_hash_duration_ms={stopwatch.ElapsedMilliseconds}");
        }
    }

    private Task SendWindowUpdateAsync(InboundTransferContext context, WindowUpdateTrigger trigger, CancellationToken ct)
        => inboundController.SendWindowUpdateAsync(context, trigger, ct);

    private Task RunInboundWindowRefreshWatchdogAsync(InboundTransferContext context)
        => inboundController.RunInboundWindowRefreshWatchdogAsync(context);

    private Task RunInboundGapRecoveryWatchdogAsync(InboundTransferContext context)
        => inboundController.RunInboundGapRecoveryWatchdogAsync(context);

    private Task SendMissingRangeAsync(InboundTransferContext context, CancellationToken ct)
        => inboundController.SendMissingRangeAsync(context, ct);

    private bool TryGetWindowUpdateRefreshTriggerLocked(InboundTransferContext context, out WindowUpdateTrigger trigger)
        => inboundController.TryGetWindowUpdateRefreshTriggerLocked(context, out trigger);

    private bool TryGetWatchdogWindowUpdateTriggerLocked(InboundTransferContext context, out WindowUpdateTrigger trigger)
        => inboundController.TryGetWatchdogWindowUpdateTriggerLocked(context, out trigger);

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

    private Task MaybeSendPressureStateAsync(InboundTransferContext context, CancellationToken ct)
        => inboundController.MaybeSendPressureStateAsync(context, ct);

    private bool TryTransitionInboundPressureStateLocked(InboundTransferContext context, out FileTransferPressureStateV1? message)
        => inboundController.TryTransitionInboundPressureStateLocked(context, out message);

    private void RecordMissingRangeSentLocked(InboundTransferContext context)
        => inboundController.RecordMissingRangeSentLocked(context);

    private void RefreshHighestBufferedChunkIndexLocked(InboundTransferContext context)
        => inboundController.RefreshHighestBufferedChunkIndexLocked(context);

    private int GetCreditFrontierLocked(InboundTransferContext context, int highestBufferedChunkIndex)
        => inboundController.GetCreditFrontierLocked(context, highestBufferedChunkIndex);

    private int GetRawTargetGrantedUntilExclusiveLocked(InboundTransferContext context, int creditFrontier)
        => inboundController.GetRawTargetGrantedUntilExclusiveLocked(context, creditFrontier);

    private int GetTargetGrantedUntilExclusiveLocked(InboundTransferContext context, WindowUpdateTrigger trigger, int creditFrontier)
        => inboundController.GetTargetGrantedUntilExclusiveLocked(context, trigger, creditFrontier);

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

    private bool ShouldRequestMissingRangeLocked(InboundTransferContext context)
        => inboundController.ShouldRequestMissingRangeLocked(context);

    private bool TryBuildMissingRangeLocked(InboundTransferContext context, out FileTransferMissingRangeV1 message)
        => inboundController.TryBuildMissingRangeLocked(context, out message);

    private bool ShouldDeferGrantExtensionDueToGapLocked(InboundTransferContext context, int highestBufferedChunkIndex, int targetGrantedUntilExclusive)
        => inboundController.ShouldDeferGrantExtensionDueToGapLocked(context, highestBufferedChunkIndex, targetGrantedUntilExclusive);

    private bool ShouldLogGapDeferredLocked(InboundTransferContext context)
        => inboundController.ShouldLogGapDeferredLocked(context);

    private Task HandleIncomingPressureStateAsync(FileTransferPressureStateV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            var context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) ||
                !string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (message.Revision <= context.RemotePressureRevision ||
                !TryParsePressureMode(message.Mode, out var mode) ||
                !TryParsePressureReason(message.Reason, out var reason))
            {
                return Task.CompletedTask;
            }

            context.RemotePressureRevision = message.Revision;
            context.RemotePressureMode = mode;
            context.RemotePressureReason = reason;
            context.RemotePressureSuggestedSendAheadChunks = Math.Max(0, message.SuggestedSendAheadChunks);
            context.RemotePressureReceiverNextExpectedChunkIndex = Math.Max(context.RemotePressureReceiverNextExpectedChunkIndex, message.ReceiverNextExpectedChunkIndex);
            if (message.ReceiverNextExpectedChunkIndex > context.RemoteNextExpectedChunkIndex)
            {
                context.RemoteNextExpectedChunkIndex = message.ReceiverNextExpectedChunkIndex;
                PruneSentChunkCache(context, context.RemoteNextExpectedChunkIndex);
            }

            UpdateOutboundPressureDerivedStateLocked(context);
            context.SignalControlActivity();
            UpdateOutboundAcknowledgedProgressLocked(context);
            snapshot = CreateSnapshotLocked();
        }

        LogPressureStateReceived(message);
        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        return Task.CompletedTask;
    }

    private static string ClassifyOutboundFailureErrorCode(Exception ex, string fallbackErrorCode)
    {
        if (TryGetSenderCacheErrorCode(ex, out var senderCacheErrorCode))
        {
            return senderCacheErrorCode;
        }

        return IsTransportIncompatible(ex)
            ? TransportIncompatibleErrorCode
            : IsPayloadBudgetExceeded(ex)
                ? PayloadBudgetExceededErrorCode
            : fallbackErrorCode;
    }

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

    private static int GetFrameRawChunkBytes(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferChunkDataFrameV2 chunk => chunk.Data.Length,
            FileTransferChunkBatchFrameV2 batch => batch.DataSegments.Sum(static segment => segment.Length),
            _ => 0,
        };

    private static int GetFrameChunkCount(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferChunkDataFrameV2 => 1,
            FileTransferChunkBatchFrameV2 batch => batch.DataSegments.Count,
            FileTransferRepairRequestSetFrameV3 repairSet => repairSet.Ranges.Sum(static range => Math.Max(0, range.RequestedChunkCount)),
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
        if (fileSizeBytes <= 0 || chunkSizeBytes <= 0)
        {
            return false;
        }

        try
        {
            chunkCount = checked((int)((fileSizeBytes + chunkSizeBytes - 1) / chunkSizeBytes));
            return chunkCount > 0;
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

        if (context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3)
        {
            return FileTransferChunkBudget.ClampRequestedRawChunkSize(requestedChunkSize);
        }

        return FileTransferChunkBudget.ComputeLargestFittingRawChunkSize(
            requestedChunkSize,
            candidateChunkSize =>
            {
                try
                {
                    var payload = FileTransferDataFrameCodec.Serialize(
                        CreatePullChunkDataFrame(
                            context.NegotiatedDataProtocolVersion,
                            string.IsNullOrWhiteSpace(context.SessionId) ? new string('s', 32) : context.SessionId,
                            context.TransferId,
                            chunkIndex: 0,
                            chunkCount: 1,
                            chunkBytes: new byte[candidateChunkSize]));
                    return payload.Length <= FileTransferProtocol.MaxSerializedChunkPayloadBytes;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            },
            "No valid file-transfer chunk size fits within the V2 payload budget.");
    }

    private static bool IsNegotiableDataProtocolVersion(int? protocolVersion)
        => protocolVersion == FileTransferProtocol.ProtocolVersionV4;

    private static bool IsV4StreamingTransport(IFileTransferSignalingTransport? currentTransport)
        => currentTransport is IFileTransferProtocolCapabilities { SupportsFileTransferV4Streaming: true };

    private static FileTransferTransportProfileKind ResolveTransportProfileKind(IFileTransferSignalingTransport? currentTransport)
        => currentTransport is IFileTransferTransportProfileProvider transportProfileProvider
            ? transportProfileProvider.FileTransferTransportProfileKind
            : FileTransferTransportProfileKind.Default;

    private static bool UsesConservativeNknStartup(
        IFileTransferSignalingTransport? currentTransport,
        int negotiatedDataProtocolVersion)
        => negotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3 &&
           ResolveTransportProfileKind(currentTransport) == FileTransferTransportProfileKind.ConservativeNknStartup;

    private FileTransferPayloadEfficiencyProfileSelection ResolvePayloadEfficiencyProfileSelectionLocked(OutboundTransferContext context)
    {
        var requestedValue = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var hasExplicitRequest = !string.IsNullOrWhiteSpace(requestedValue);
        var requested = FileTransferPayloadEfficiencyProfile.ResolveRequestedFromEnvironment(out var reason);
        if (context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV3)
        {
            return new FileTransferPayloadEfficiencyProfileSelection(
                FileTransferPayloadEfficiencyProfile.Current,
                "non_v3_forced_current");
        }

        if (!hasExplicitRequest &&
            UsesConservativeNknStartup(transport, context.NegotiatedDataProtocolVersion) &&
            !sessionScreenShareActive &&
            !sessionScreenShareDegraded)
        {
            return new FileTransferPayloadEfficiencyProfileSelection(
                FileTransferPayloadEfficiencyProfile.Packed3x21KiB,
                "nkn_file_only_default");
        }

        if (requested.Kind == FileTransferPayloadEfficiencyProfileKind.Current)
        {
            return new FileTransferPayloadEfficiencyProfileSelection(requested, reason);
        }

        if ((sessionScreenShareActive || sessionScreenShareDegraded) &&
            !FileTransferPayloadEfficiencyProfile.AllowExperimentalProfileDuringScreenShare())
        {
            return new FileTransferPayloadEfficiencyProfileSelection(
                FileTransferPayloadEfficiencyProfile.Current,
                sessionScreenShareDegraded
                    ? "screen_share_degraded_forced_current"
                    : "screen_share_active_forced_current");
        }

        return new FileTransferPayloadEfficiencyProfileSelection(requested, reason);
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
    }

    private static void LogV4RequiredTransportIncompatible(string transferId, string sessionId)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_required_transport_incompatible; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; required_protocol_version={FileTransferProtocol.ProtocolVersionV4}");
    }

    private static void LogV4Negotiated(string transferId, string sessionId, FileTransferDirection direction)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_negotiated; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; protocol_version={FileTransferProtocol.ProtocolVersionV4}");
    }

    private static void LogV4SessionOpenRejected(
        string transferId,
        string sessionId,
        FileTransferDirection direction,
        int protocolVersion,
        string reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_session_open_rejected; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; protocol_version={protocolVersion}; reason={reason}");
    }

    private static void LogV4RuntimeNotImplemented(string transferId, string sessionId, FileTransferDirection direction)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_runtime_not_implemented; transfer_id={transferId}; session_id={FormatProtocolLogValue(sessionId)}; direction={direction}; error_code={V4RuntimeNotImplementedErrorCode}");
    }

    private static string FormatProtocolLogValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();

    private static string FormatProtocolLogValue(int? value)
        => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)";

    private static void LogWindowUpdateSent(
        FileTransferWindowUpdateV1 message,
        string phase,
        string triggerReason,
        int highestBufferedChunkIndex,
        int creditFrontier)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_update_sent; transfer_id={message.TransferId}; session_id={message.SessionId}; phase={phase}; next_expected_chunk={message.NextExpectedChunkIndex}; granted_until_exclusive={message.GrantedUntilChunkIndexExclusive}; highest_buffered_chunk={highestBufferedChunkIndex}; credit_frontier={creditFrontier}; bytes_received={message.BytesReceived}; reason={triggerReason}");
    }

    private static void LogWindowUpdateReceived(FileTransferWindowUpdateV1 message)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_update_received; transfer_id={message.TransferId}; session_id={message.SessionId}; next_expected_chunk={message.NextExpectedChunkIndex}; granted_until_exclusive={message.GrantedUntilChunkIndexExclusive}; bytes_received={message.BytesReceived}");
    }

    private static void LogPressureStateSent(FileTransferPressureStateV1 message)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=pressure_state_sent; transfer_id={message.TransferId}; session_id={message.SessionId}; revision={message.Revision}; mode={message.Mode}; suggested_send_ahead_chunks={message.SuggestedSendAheadChunks}; receiver_next_expected_chunk={message.ReceiverNextExpectedChunkIndex}; reason={message.Reason}");
    }

    private static void LogPressureStateReceived(FileTransferPressureStateV1 message)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=pressure_state_received; transfer_id={message.TransferId}; session_id={message.SessionId}; revision={message.Revision}; mode={message.Mode}; suggested_send_ahead_chunks={message.SuggestedSendAheadChunks}; receiver_next_expected_chunk={message.ReceiverNextExpectedChunkIndex}; reason={message.Reason}");
    }

    private static void LogMissingRangeSent(FileTransferMissingRangeV1 message, int nextExpectedChunkIndex, int highestBufferedChunkIndex)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=missing_range_sent; transfer_id={message.TransferId}; session_id={message.SessionId}; start_chunk={message.StartChunkIndex}; end_chunk_exclusive={message.EndChunkIndexExclusive}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}");
    }

    private static void LogMissingRangeReceived(FileTransferMissingRangeV1 message, int nextExpectedChunkIndex, int highestBufferedChunkIndex)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=missing_range_received; transfer_id={message.TransferId}; session_id={message.SessionId}; start_chunk={message.StartChunkIndex}; end_chunk_exclusive={message.EndChunkIndexExclusive}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}");
    }

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

    private static void LogBulkCatchUpOnlyEntered(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int obsoleteChunkCountRecent,
        double obsoleteChunkArrivalRatio,
        int missingRangeCountRecent,
        int contiguousProgressChunksRecent,
        FileTransferPressureReason reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=bulk_catchup_only_entered; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; obsolete_chunk_count_recent={obsoleteChunkCountRecent}; obsolete_chunk_arrival_ratio={obsoleteChunkArrivalRatio:F3}; missing_range_count_recent={missingRangeCountRecent}; contiguous_progress_chunks_recent={contiguousProgressChunksRecent}; reason={FormatPressureReason(reason)}");
    }

    private static void LogBulkCatchUpOnlyExited(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int suggestedSendAheadChunks)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=bulk_catchup_only_exited; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; suggested_send_ahead_chunks={suggestedSendAheadChunks}");
    }

    private static bool TryParsePressureMode(string? value, out FileTransferPressureMode mode)
    {
        mode = default;
        if (string.Equals(value, FileTransferProtocol.PressureModeNormal, StringComparison.OrdinalIgnoreCase))
        {
            mode = FileTransferPressureMode.Normal;
            return true;
        }

        if (string.Equals(value, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.OrdinalIgnoreCase))
        {
            mode = FileTransferPressureMode.CatchUpOnly;
            return true;
        }

        return false;
    }

    private static string FormatPressureMode(FileTransferPressureMode mode)
        => mode == FileTransferPressureMode.CatchUpOnly
            ? FileTransferProtocol.PressureModeCatchUpOnly
            : FileTransferProtocol.PressureModeNormal;

    private static bool TryParsePressureReason(string? value, out FileTransferPressureReason reason)
    {
        reason = default;
        if (string.Equals(value, FileTransferProtocol.PressureReasonGapRepair, StringComparison.OrdinalIgnoreCase))
        {
            reason = FileTransferPressureReason.GapRepair;
            return true;
        }

        if (string.Equals(value, FileTransferProtocol.PressureReasonMediaProtection, StringComparison.OrdinalIgnoreCase))
        {
            reason = FileTransferPressureReason.MediaProtection;
            return true;
        }

        if (string.Equals(value, FileTransferProtocol.PressureReasonBulkBacklog, StringComparison.OrdinalIgnoreCase))
        {
            reason = FileTransferPressureReason.BulkBacklog;
            return true;
        }

        return false;
    }

    private static string FormatPressureReason(FileTransferPressureReason reason)
        => reason switch
        {
            FileTransferPressureReason.GapRepair => FileTransferProtocol.PressureReasonGapRepair,
            FileTransferPressureReason.MediaProtection => FileTransferProtocol.PressureReasonMediaProtection,
            _ => FileTransferProtocol.PressureReasonBulkBacklog,
        };

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

    private sealed class OutboundTransferContext
    {
        private TaskCompletionSource<bool> controlSignal = CreateSignal();

        public FileTransferSendDescriptor Descriptor { get; }

        public FileTransferReadStreamFactory OpenReadStreamAsync { get; }

        public CancellationTokenSource LifetimeCts { get; } = new();

        public string SessionId { get; set; } = string.Empty;

        public string TransferId => Descriptor.TransferId!;

        public string FileName => Descriptor.FileName;

        public long FileSizeBytes => Descriptor.FileSizeBytes;

        public int ChunkSizeBytes { get; set; }

        public int ChunkCount { get; set; }

        public string? Sha256Base64 { get; set; }

        public int NegotiatedDataProtocolVersion { get; set; } = FileTransferProtocol.ProtocolVersionV4;

        public long BytesTransferred { get; set; }

        public int ChunksTransferred { get; set; }

        public long BytesAcceptedForTransport { get; set; }

        public int ChunksAcceptedForTransport { get; set; }

        public FileTransferTransferState State { get; set; } = FileTransferTransferState.Offering;

        public string? ErrorCode { get; set; }

        public string? StatusMessage { get; set; } = "Preparing transfer offer.";

        public bool SendStarted { get; set; }

        public int NextProgressMilestonePercent { get; set; } = 25;

        public int NextChunkIndexToRead { get; set; }

        public int RemoteNextExpectedChunkIndex { get; set; }

        public int RemoteGrantedUntilExclusive { get; set; }

        public DateTimeOffset LastWindowUpdateUtc { get; set; } = DateTimeOffset.UtcNow;

        public Dictionary<int, FileTransferChunkV1> SentChunkCache { get; } = new();

        public Queue<int> PendingRepairChunkIndices { get; } = new();

        public HashSet<int> PendingRepairChunkIndicesSet { get; } = [];

        public bool RepairModeActive { get; set; }

        public int? RepairRangeStartChunkIndex { get; set; }

        public int? RepairRangeEndChunkExclusive { get; set; }

        public int? DeferredRepairRangeStartChunkIndex { get; set; }

        public int? DeferredRepairRangeEndChunkExclusive { get; set; }

        public DateTimeOffset? LastRepairSendUtc { get; set; }

        public DateTimeOffset? LastRepairAckObservedUtc { get; set; }

        public DateTimeOffset? LastRepairEvidenceUtc { get; set; }

        public DateTimeOffset? LastRepairRangeRequestedUtc { get; set; }

        public int? LastRepairChunkSentIndex { get; set; }

        public bool RepairBatchInFlight { get; set; }

        public int? OutstandingRepairBatchStartChunkIndex { get; set; }

        public int? OutstandingRepairBatchEndChunkExclusive { get; set; }

        public int RepairSendCycle { get; set; }

        public bool RepairOnlyModeActive { get; set; }

        public bool RepairSingleChunkModeActive { get; set; }

        public int RemotePressureRevision { get; set; }

        public FileTransferPressureMode RemotePressureMode { get; set; } = FileTransferPressureMode.Normal;

        public FileTransferPressureReason RemotePressureReason { get; set; } = FileTransferPressureReason.BulkBacklog;

        public int RemotePressureSuggestedSendAheadChunks { get; set; } = PressureStateNormalSuggestedSendAheadChunks;

        public int RemotePressureReceiverNextExpectedChunkIndex { get; set; }

        public int CurrentRepairBatchSize { get; set; } = RepairBatchSize;

        public DateTimeOffset? LastSendAheadClampLogUtc { get; set; }

        public IFileTransferDataSession? DataSession { get; set; }

        public bool PullSessionActive { get; set; }

        public bool PullSessionDegraded { get; set; }

        public int PullCurrentPipelineDepth { get; set; }

        public SortedSet<int> RequestedButUnsent { get; } = [];

        public SortedSet<int> GrantedOutstandingChunks { get; } = [];

        public Dictionary<int, DateTimeOffset> SentAwaitingAck { get; } = new();

        public Dictionary<int, DateTimeOffset> LastChunkSentUtc { get; } = new();

        public Dictionary<int, DateTimeOffset> LastChunkResentUtc { get; } = new();

        public Dictionary<int, int> ChunkResendCountSinceAck { get; } = new();

        public Dictionary<int, byte[]> PullSentChunkCache { get; } = new();

        public bool PullSourceCanSeek { get; set; }

        public long PullSentChunkCacheBytes { get; set; }

        public bool PullSenderCachePressureActive { get; set; }

        public int PullV3GrantedUntilExclusive { get; set; }

        public DateTimeOffset? PullV3LastGrantReceivedUtc { get; set; }

        public bool PullV3ExpandedWindowActive { get; set; }

        public bool PullV3LimitedWindowActive { get; set; }

        public DateTimeOffset? PullV3CleanSinceUtc { get; set; }

        public DateTimeOffset? PullV3AdverseSinceUtc { get; set; }

        public bool PullTransportPaused { get; set; }

        public DateTimeOffset? PullTransportPausedSinceUtc { get; set; }

        public DateTimeOffset? PullTransportGraceDeadlineUtc { get; set; }

        public string? PullTransportPauseReason { get; set; }

        public bool PullTransportResumeRequestPending { get; set; }

        public Queue<DateTimeOffset> RecentPullChunkSentUtc { get; } = new();

        public int PullDuplicateRequestIgnoredCountRecent { get; set; }

        public int PullResendSuppressedCountRecent { get; set; }

        public long PullUsefulPayloadBytesRecent { get; set; }

        public DateTimeOffset? LastSenderThroughputLogUtc { get; set; }

        public long PullSenderRawBytesRecent { get; set; }

        public int PullSenderChunkFramesRecent { get; set; }

        public int PullSenderBatchFramesRecent { get; set; }

        public int PullSenderChunkCountRecent { get; set; }

        public int PullSenderSendWaitCountRecent { get; set; }

        public int PullSenderRepairSendCountRecent { get; set; }

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

        public DateTimeOffset? PullV3LastCreditStallLogUtc { get; set; }

        public List<long> PullSenderFeedInterScheduleGapMsRecent { get; } = [];

        public Queue<PullV3QueuedRepairSend> PullV3SenderPumpRepairQueue { get; } = new();

        public HashSet<int> PullV3SenderPumpRepairQueuedChunkIndices { get; } = [];

        public Queue<PullV4QueuedRepairSend> PullV4SenderPumpRepairQueue { get; } = new();

        public HashSet<int> PullV4SenderPumpRepairQueuedChunkIndices { get; } = [];

        public Dictionary<string, V4SenderRepairRequestState> PullV4SenderPumpRepairRequests { get; } = new(StringComparer.Ordinal);

        public int V4LastStateEpoch { get; set; } = -1;

        public bool V4TerminalReady { get; set; }

        public string V4SenderPumpLastWakeReason { get; set; } = "startup";

        public string V4SenderPumpLastRepairRequestKey { get; set; } = "(none)";

        public DateTimeOffset? V4SenderCreditExhaustedSinceUtc { get; set; }

        private TaskCompletionSource<bool> pullV3SenderPumpSignal = CreateSignal();

        private TaskCompletionSource<bool> pullV4SenderPumpSignal = CreateSignal();

        public Task ResetAndGetV3SenderPumpSignalTask()
        {
            pullV3SenderPumpSignal = CreateSignal();
            return pullV3SenderPumpSignal.Task;
        }

        public void SignalV3SenderPump()
        {
            pullV3SenderPumpSignal.TrySetResult(true);
        }

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
                BytesAcknowledgedByReceiver: BytesTransferred);

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
                LifetimeCts.Dispose();
            }
            catch
            {
            }

            DataSession = null;
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

    private sealed record PullV3QueuedRepairSend(
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
        bool LogRepairSetSent,
        string RepairRequestKey,
        bool LogFrontierRepairSent);

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
        bool FrontierTailRepair,
        FileTransferV4RepairDeliveryMode DeliveryMode,
        string DeliveryEscalationReason);

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

    private sealed record InboundV3ReceiverFeedbackWork(
        FileTransferDataFrameV2 Frame,
        DateTimeOffset EnqueuedUtc,
        string Reason,
        TaskCompletionSource<bool>? Completion);

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

        public int NegotiatedDataProtocolVersion { get; set; } = FileTransferProtocol.ProtocolVersionV4;

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

        public FileTransferPressureMode LocalPressureMode { get; set; } = FileTransferPressureMode.Normal;

        public FileTransferPressureReason LocalPressureReason { get; set; } = FileTransferPressureReason.BulkBacklog;

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

        public DateTimeOffset? V4LastStateSentUtc { get; set; }

        public Dictionary<string, V4ReceiverRepairRequestState> V4ReceiverRepairRequests { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset? V4FrontierStallStartedUtc { get; set; }

        public int V4FrontierStallChunkIndex { get; set; } = -1;

        public DateTimeOffset? V4FrontierStallLastSuppressedLogUtc { get; set; }

        public bool V4ReceiverRepairSchedulerStarted { get; set; }

        public bool PullV3ReceiverFeedbackPumpEnabled { get; set; }

        public List<InboundV3ReceiverFeedbackWork> PullV3ReceiverFeedbackQueue { get; } = [];

        private TaskCompletionSource<bool> pullV3ReceiverFeedbackPumpSignal = CreateSignal();

        public Task ResetAndGetV3ReceiverFeedbackPumpSignalTask()
        {
            pullV3ReceiverFeedbackPumpSignal = CreateSignal();
            return pullV3ReceiverFeedbackPumpSignal.Task;
        }

        public void SignalV3ReceiverFeedbackPump()
        {
            pullV3ReceiverFeedbackPumpSignal.TrySetResult(true);
        }

        public long PullV3ReceiverFeedbackEnqueuedRecent { get; set; }

        public long PullV3ReceiverFeedbackSentRecent { get; set; }

        public long PullV3ReceiverFeedbackCoalescedRecent { get; set; }

        public long PullV3ReceiverFeedbackFailedRecent { get; set; }

        public int PullV3ReceiverFeedbackMaxQueueDepthRecent { get; set; }

        public long PullV3ReceiverFeedbackMaxEnqueueAgeMsRecent { get; set; }

        public long PullV3ReceiverFeedbackMaxSendDurationMsRecent { get; set; }

        public DateTimeOffset? PullV3ReceiverFeedbackLastSummaryUtc { get; set; }

        public FileTransferSessionOpenV2? PendingSessionOpen { get; set; }

        public bool PullSessionDegraded { get; set; }

        public int PullCurrentPipelineDepth { get; set; }

        public int PullRequestedFrontierExclusive { get; set; }

        public int PullCommittedFrontier { get; set; }

        public DateTimeOffset? PullLastRequestSentUtc { get; set; }

        public DateTimeOffset? PullLastProgressUtc { get; set; }

        public DateTimeOffset? PullDegradedSinceUtc { get; set; }

        public DateTimeOffset? PullRecoverySinceUtc { get; set; }

        public DateTimeOffset? PullReorderPressureSinceUtc { get; set; }

        public int PullReorderPressureFrontierChunkIndex { get; set; }

        public int? PullTimeoutOldestChunkIndex { get; set; }

        public int PullTimeoutStreak { get; set; }

        public int PullHighestReceivedChunkIndex { get; set; } = -1;

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

        public Queue<DateTimeOffset> RecentPullAckSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentPullRequestSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentPullChunkSentUtc { get; } = new();

        public int PullDuplicateRequestIgnoredCountRecent { get; set; }

        public int PullResendSuppressedCountRecent { get; set; }

        public long PullUsefulPayloadBytesRecent { get; set; }

        public long PullReceiverRawBytesRecent { get; set; }

        public long PullReceiverContiguousBytesCommittedRecent { get; set; }

        public int PullReceiverWriteBatchCountRecent { get; set; }

        public long PullReceiverWriteBatchBytesRecent { get; set; }

        public long PullReceiverWriteDurationMsRecent { get; set; }

        public DateTimeOffset? LastPullControlChatterLogUtc { get; set; }

        public DateTimeOffset? PullV3GapStallSinceUtc { get; set; }

        public int PullV3GapStallStartChunkIndex { get; set; } = -1;

        public Dictionary<int, DateTimeOffset> OutstandingChunkRequests { get; } = new();

        public HashSet<int> RequestedChunks { get; } = [];

        public Dictionary<int, int> ChunkAttemptCounts { get; } = new();

        public int PullFirstChunkTimeoutCount { get; set; }

        public int PullV3GrantedUntilExclusive { get; set; }

        public DateTimeOffset? PullV3LastGrantSentUtc { get; set; }

        public DateTimeOffset? PullV3LastRepairRequestSentUtc { get; set; }

        public DateTimeOffset? PullV3LastProactiveFrontierRepairSentUtc { get; set; }

        public int PullV3LastProactiveFrontierRepairStartChunkIndex { get; set; } = -1;

        public int PullV3LastProactiveFrontierRepairRequestedChunkCount { get; set; }

        public int PullV3LastProactiveFrontierRepairHighestReceivedChunkIndex { get; set; } = -1;

        public string? PullV3LastProactiveFrontierRepairRequestKey { get; set; }

        public string? PullV3LastProactiveFrontierRepairFingerprint { get; set; }

        public int PullV3ConsecutiveProactiveFrontierRepairCount { get; set; }

        public DateTimeOffset? PullV3LastProactiveFrontierRepairSkipLogUtc { get; set; }

        public string? PullV3LastProactiveFrontierRepairSkipReason { get; set; }

        public int PullV3LastProactiveFrontierRepairSkipStartChunkIndex { get; set; } = -1;

        public Queue<DateTimeOffset> RecentPullRepairRequestSentUtc { get; } = new();

        public string? PullV3LastRepairRequestFingerprint { get; set; }

        public DateTimeOffset? PullV3LastRepairRequestFingerprintUtc { get; set; }

        public int PullV3LastRepairRequestNextChunkIndex { get; set; } = -1;

        public int PullV3LastRepairRequestHighestReceivedChunkIndex { get; set; } = -1;

        public FileTransferTransportProfileKind TransportProfileKind { get; set; } = FileTransferTransportProfileKind.Default;

        public bool PullV3ConservativeStartupActive { get; set; }

        public bool PullV3ConservativeStartupDegradedActive { get; set; }

        public bool PullV3ConservativeStartupProbeActive { get; set; }

        public DateTimeOffset? PullV3ConservativeStartupStartedUtc { get; set; }

        public DateTimeOffset? PullV3ConservativeStartupExitedUtc { get; set; }

        public string? PullV3ConservativeStartupExitReason { get; set; }

        public long PullV3ConservativeStartupExitBytes { get; set; }

        public bool PullV3FirstRepairOrTimeoutBeforeStartupExit { get; set; }

        public bool PullV3ExpandedWindowActive { get; set; }

        public bool PullV3FileOnlySoftLimitedWindowActive { get; set; }

        public bool PullV3LimitedWindowActive { get; set; }

        public DateTimeOffset? PullV3CleanSinceUtc { get; set; }

        public DateTimeOffset? PullV3AdverseSinceUtc { get; set; }

        public string? PullV3LastReorderPolicyDecision { get; set; }

        public DateTimeOffset? PullV3LastReorderPolicyDecisionLogUtc { get; set; }

        public DateTimeOffset? PullV3LastGrantWindowSummaryLogUtc { get; set; }

        public long PullV3LastGrantTargetWindowBytes { get; set; }

        public int PullV3LastGrantCreditBaseChunkIndex { get; set; }

        public DateTimeOffset? PullV3LastSparseCreditEligibleUtc { get; set; }

        public int PullV3LastSparseCreditBaseChunkIndex { get; set; }

        public bool ReceiverBufferPressureActive { get; set; }

        public DateTimeOffset? ReceiverBufferPressureSinceUtc { get; set; }

        public DateTimeOffset? LastReceiverGrantClampLogUtc { get; set; }

        public DateTimeOffset? LastPressureStateSentUtc { get; set; }

        public FileTransferPressureMode? LastPressureStateSentMode { get; set; }

        public FileTransferPressureReason? LastPressureStateSentReason { get; set; }

        public int LastPressureStateSentSuggestedSendAheadChunks { get; set; }

        public int LastPressureStateSentReceiverNextExpectedChunkIndex { get; set; }

        public string? LastPressureStateSentProfileName { get; set; }

        public bool IsTerminal => ToSnapshot().IsTerminal;

        public FileTransferIncomingOffer CreateOffer()
            => new(SessionId, TransferId, FileName, FileSizeBytes, Sha256Base64);

        public FileTransferTransferSnapshot ToSnapshot()
            => new(
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
                SavedFileName);

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

        private static TaskCompletionSource<bool> CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private int ResolvePreferredOutboundChunkSize(
        OutboundTransferContext context,
        IFileTransferSignalingTransport? currentTransport = null)
    {
        var isV3 = context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3;
        var isV4 = context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4;
        var defaultChunkSize = isV4
            ? V4DefaultChunkSizeBytes
            : isV3
            ? UsesConservativeNknStartup(currentTransport, context.NegotiatedDataProtocolVersion)
                ? PullV3ConservativeStartupChunkSizeBytes
                : PullV3HealthyDefaultChunkSizeBytes
            : PullHealthyDefaultChunkSizeBytes;
        var payloadProfileOverridesChunkSize =
            isV3 &&
            context.PayloadEfficiencyProfile.Kind != FileTransferPayloadEfficiencyProfileKind.Current &&
            context.PayloadEfficiencyProfile.PreferredChunkSizeBytes.HasValue;
        if (payloadProfileOverridesChunkSize && context.PayloadEfficiencyProfile.PreferredChunkSizeBytes is int profileChunkSizeBytes)
        {
            defaultChunkSize = profileChunkSizeBytes;
        }

        var preferredChunkSize = context.Descriptor.ChunkSizeBytes ?? defaultChunkSize;
        if (payloadProfileOverridesChunkSize && context.PayloadEfficiencyProfile.PreferredChunkSizeBytes is int experimentalChunkSizeBytes)
        {
            preferredChunkSize = experimentalChunkSizeBytes;
        }

        if (payloadProfileOverridesChunkSize)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, FileTransferProtocol.MaxChunkRawBytes);
        }
        else if (sessionScreenShareDegraded)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, isV3 ? PullV3DegradedDefaultChunkSizeBytes : PullDegradedScreenshareDefaultChunkSizeBytes);
        }
        else if (context.PullSessionDegraded)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, isV3 ? PullV3DegradedDefaultChunkSizeBytes : PullDegradedDefaultChunkSizeBytes);
        }
        else if (sessionScreenShareActive)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, isV3 ? PullV3ScreenshareDefaultChunkSizeBytes : PullScreenshareDefaultChunkSizeBytes);
        }
        else
        {
            preferredChunkSize = Math.Min(preferredChunkSize, isV4 ? V4DefaultChunkSizeBytes : isV3 ? PullV3HealthyDefaultChunkSizeBytes : PullHealthyDefaultChunkSizeBytes);
        }

        return Math.Clamp(preferredChunkSize, 1, FileTransferProtocol.MaxChunkRawBytes);
    }

    private enum WindowUpdateTrigger
    {
        Startup,
        StartupResend,
        GapProgressAck,
        LowWatermark,
        BufferedFrontier,
        SteadyStateResend,
    }

    private enum FileTransferPressureMode
    {
        Normal,
        CatchUpOnly,
    }

    private enum FileTransferPressureReason
    {
        GapRepair,
        MediaProtection,
        BulkBacklog,
    }

    private enum InboundV3ReorderPolicyDecision
    {
        Conservative,
        Normal,
        Tolerate,
        SoftLimit,
        Limit,
    }

    private sealed class SenderCacheException(string errorCode, string message) : InvalidOperationException(message)
    {
        public string ErrorCode { get; } = errorCode;
    }

    private readonly record struct FileTransferPayloadEfficiencyProfileSelection(
        FileTransferPayloadEfficiencyProfile Profile,
        string Reason);

    private sealed record PreparedV3TransportSend(
        FileTransferDataFrameV2 Frame,
        int StartChunkIndex,
        int ChunkCount,
        int RawBytes,
        bool IsBatch);

    private sealed record PendingV3TransportSend(
        PreparedV3TransportSend Prepared,
        Task SendTask,
        DateTimeOffset ScheduledUtc);

    private sealed record PreparedV4TransportSend(
        FileTransferChunkBatchFrameV4 Frame,
        int StartChunkIndex,
        int ChunkCount,
        int RawBytes);

    private sealed record PendingV4TransportSend(
        PreparedV4TransportSend Prepared,
        Task SendTask,
        DateTimeOffset ScheduledUtc,
        int SendAttempt,
        int SendAttemptCount);

    private readonly record struct MissingRange(int StartChunkIndex, int EndChunkIndexExclusive);
}
