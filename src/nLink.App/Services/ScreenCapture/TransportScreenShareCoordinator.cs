using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.App.Services.ScreenCapture;

internal sealed class ScreenShareSenderDegradedModeChangedEventArgs : EventArgs
{
    public ScreenShareSenderDegradedModeChangedEventArgs(bool isActive)
    {
        IsActive = isActive;
    }

    public bool IsActive { get; }
}

internal enum ScreenShareSenderFreshnessMode
{
    Normal = 0,
    Reduced = 1,
    CatchUp = 2,
}

internal sealed partial class TransportScreenShareCoordinator : IAsyncDisposable
{
    private enum ScreenShareTransportPayloadizationPolicy
    {
        LegacyFragmentsOnly = 0,
        BatchWhenFits = 1,
    }

    private enum RecoveryBurstRequestDecision
    {
        None = 0,
        Start = 1,
        Suppress = 2,
        EpochTakeover = 3,
    }

    private const int ScreenShareBridgePayloadBudgetBytes = 64 * 1024;
    private const int ScreenShareFallbackBatchPayloadBudgetBytes = 60 * 1024;
    private const int StreamConfigBootstrapSendAttempts = 3;
    private const int MinAutoTuneFramesPerSecond = 2;
    private const int NormalSenderFramesPerSecond = 8;
    private const int ReducedSenderFramesPerSecond = 5;
    private const int CatchUpSenderFramesPerSecond = 3;
    private static readonly TimeSpan ScreenShareStartupWarmupDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecoveryBurstTimeoutProgressGrace = TimeSpan.FromMilliseconds(400);
    private const int ReducedToNormalHealthyRemoteAgeThresholdMs = 350;
    private const int NormalToReducedCaptureToSendThresholdMs = 250;
    private const int CatchUpModeCaptureToSendThresholdMs = 600;
    private const int CatchUpModeRemoteAgeThresholdMs = 1200;
    private const int RemoteHighFrameAgeCatchUpEntryThresholdMs = 400;
    private const int CatchUpRecoveryCaptureToSendThresholdMs = 450;
    private const int CatchUpRecoveryRemoteAgeThresholdMs = 900;
    private const int CatchUpEntryConsecutiveTicks = 2;
    private const int NormalToReducedEntryConsecutiveTicks = 2;
    private const int CatchUpRecoveryConsecutiveTicks = 3;
    private const int ReducedRecoveryConsecutiveTicks = 3;
    private const int ReducedToNormalRequiredHealthyRemoteSignals = 3;
    private const int ReducedToNormalRequiredHelperApplyCount = 3;
    private const double PromotionEncodeBudgetFraction = 0.35d;
    private const int PromotionEncodeBudgetMinMs = 40;
    private const int PromotionEncodeBudgetMaxMs = 75;
    private const int ReducedPromotionEncodeSoftSpikeResetConsecutiveEvaluations = 2;
    private const double PromotionCaptureToSendBudgetFraction = 1.25d;
    private const int PromotionCaptureToSendBudgetMinMs = 100;
    private const int PromotionCaptureToSendBudgetMaxMs = 275;
    private const double DemotionEncodePressureBudgetFraction = 0.80d;
    private const int DemotionEncodePressureBudgetMinMs = 60;
    private const int DemotionEncodePressureBudgetMaxMs = 110;
    private static readonly TimeSpan SatisfiedRecoveryProofFreshnessWindow = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan RemoteReduceFpsMinimumHold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RemoteCatchUpOnlyMinimumHold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SenderFreshnessKeyFrameRequestInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SenderPromotionBlockedLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReducedPromotionSummaryLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SoftScaleWarningInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransportProfileTransitionGraceWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecoveryLockDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecoveryOwnerPendingForcedResetDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RecoveryBurstTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan RecoveryPostAckHoldTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PostRecoveryAgeGraceWindow = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan RemoteHelperFactHealthyStallWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DisplayInfoMappingChangeDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AutoTuneInterval = TimeSpan.FromSeconds(1);
    private const long SevereRemoteStaleDropThreshold = 3;
    private const int MaxReducedPromotionJournalEntries = 24;
    private const string RecoverySendRoleOwner = "owner";
    private const string RecoverySendRoleProtectedFollower = "protected_follower";
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly Func<IScreenCaptureSource> captureSourceFactory;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync;
    private readonly Func<ReadOnlyMemory<byte>, string?, long, CancellationToken, Task>? sendPayloadWithRecoveryMetadataAsync;
    private readonly Func<ScreenShareVideoStreamConfigV1, CancellationToken, Task>? sendVideoStreamConfigAsync;
    private readonly Func<string, ScreenShareCursorStateV1, CancellationToken, Task>? sendCursorStateAsync;
    private readonly Func<bool>? cursorOverlayEnabledResolver;
    private readonly IScreenShareCursorPositionSource cursorPositionSource;
    private readonly Action<string>? flushTransportQueue;
    private readonly Func<IScreenShareTransportBackpressureProbe?>? transportBackpressureProbeResolver;
    private readonly Func<ReadOnlyMemory<byte>, long>? estimateBridgeBytes;
    private readonly Func<string, ControlDisplayInfoMessageV1, CancellationToken, Task>? sendDisplayInfoAsync;
    private readonly ScreenShareDisplayInfoProvider displayInfoProvider;
    private readonly IScreenShareClock clock;
    private readonly Action<string, long, long, long>? armRecoveryBurstTransportFallback;
    private readonly Action<long>? clearRecoveryBurstTransportFallback;
    private readonly object gate = new();
    private readonly object diagnosticRateLimitGate = new();
    private static readonly TimeSpan InFlightEnqueueDrainTimeout = TimeSpan.FromSeconds(2);

    private IScreenCaptureSource? captureSource;
    private ScreenShareFrameSendPipeline? sendPipeline;
    private string sessionId = string.Empty;
    private string lastActiveSessionId = string.Empty;
    private bool capturedCursorEnabledForTransport = true;
    private ScreenShareDisplayInfoSnapshot? lastSentDisplayInfo;
    private DisplayInfoMappingKey? lastSentDisplayInfoMapping;
    private long lastSentDisplayInfoRevision;
    private ScreenShareDisplayInfoSnapshot? pendingDisplayInfo;
    private DisplayInfoMappingKey? pendingDisplayInfoMapping;
    private DateTimeOffset pendingDisplayInfoNotBeforeUtc;
    private string lastDisplayInfoIssue = string.Empty;
    private long lifecycleGeneration;
    private long lastDisplayInfoSuppressedLogTick;
    private int inFlightEnqueues;
    private TaskCompletionSource<bool>? inFlightDrainedTcs;
    private Timer? autoTuneTimer;
    private Timer? recoveryOwnerPendingTimer;
    private Timer? cursorTelemetryTimer;
    private int autoTuneTickInFlight;
    private int recoveryOwnerPendingTimerInFlight;
    private int cursorTelemetryTickInFlight;
    private int captureFpsHint;
    private int captureToSendCatchUpPressureTicks;
    private int remoteObservedCatchUpPressureTicks;
    private int normalToReducedPressureTicks;
    private int catchUpRecoveryLowPressureTicks;
    private int reducedRecoveryLowPressureTicks;
    private int preferFreshestPendingFrameOnly;
    private ScreenShareTransportTuningLevel transportTuningLevel = ScreenShareTransportTuningLevel.Normal;
    private bool fileTransferDegradedHintActive;
    private bool fileTransferCatchUpOnlyHintActive;
    private ScreenShareSenderFreshnessMode senderFreshnessMode;
    private DateTimeOffset startupWarmupUntilUtc;
    private ScreenShareRemotePressureMode remotePressureMode;
    private string remotePressureReason = "healthy";
    private long remotePressureObservedFrameAgeMs;
    private long remotePressureRecentStaleDrops;
    private long lastAutoTuneRateGateDrops;
    private long lastAutoTuneQueueEvictDrops;
    private long lastAutoTuneSourceSupersededPendingFrames;
    private readonly List<ReducedPromotionEvaluationEntry> reducedPromotionJournal = new();
    private readonly Dictionary<string, long> healthyTickResetReasonCounts = new(StringComparer.Ordinal);
    private long promotionBlockerRateGateTicks;
    private long promotionBlockerHelperPressureTicks;
    private long promotionBlockerHelperWarmupTicks;
    private long promotionBlockerHelperApplyCountTicks;
    private long promotionBlockerBridgeHealthTicks;
    private long promotionBlockerRecoveryLockTicks;
    private long promotionBlockerQueueEvictTicks;
    private long promotionBlockerCaptureAgeTicks;
    private long promotionBlockerEncodeBudgetTicks;
    private long promotionBlockerTransitionGraceTicks;
    private long promotionEncodeSoftSpikeCount;
    private long promotionEncodeSoftSpikeResetSuppressedCount;
    private long promotionBlockedByEncodeBudgetAloneCount;
    private int reducedPromotionEncodeSoftSpikeConsecutiveCount;
    private int remoteHighFrameAgeCatchUpEntryConsecutiveTicks;
    private long senderCatchUpEnteredDueToRemoteHighFrameAgeCount;
    private long remoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount;
    private long remoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount;
    private long remoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount;
    private long remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount;
    private long remoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount;
    private string lastRemoteHighFrameAgeCatchUpSuppressionReason = string.Empty;
    private long catchUpRecoverySuppressedDueToRemoteHighFrameAgeCount;
    private long catchUpExitWhileRemoteHighFrameAgePressureCount;
    private long recoveryLockAllowedSameTuningModeChangeCount;
    private string lastRecoveryLockAllowedSameTuningModeChange = string.Empty;
    private long displayInfoSendCount;
    private long cursorOverlayStateSeq;
    private long cursorOverlayUpdatesSentCount;
    private long cursorOverlaySendFailureCount;
    private long cursorOverlayMappingFailureCount;
    private string cursorOverlayDeliveryMode = "captured_video";
    private string cursorOverlayLastStatus = "not_started";
    private ScreenShareCursorStateV1? lastCursorStateSent;
    private DateTimeOffset lastCursorStateSentUtc;
    private long encodedFramesSent;
    private long transportPayloadsSent;
    private long batchedPayloadsSent;
    private long legacyFragmentPayloadsSent;
    private long ordinaryNonKeyBatchedPayloadsSent;
    private long ordinaryNonKeyLegacyPayloadsSent;
    private long keyframeOrRecoveryBatchedPayloadsSent;
    private long serializedChunkBytesSent;
    private long bridgeBytesSent;
    private DateTimeOffset? lastSenderFreshnessKeyFrameRequestedUtc;
    private DateTimeOffset? remotePressureAppliedUtc;
    private bool lastLocalLaneCongestionActive;
    private bool lastLocalLaneSevereCongestionActive;
    private bool lastLocalLaneRecentDropActive;
    private DateTimeOffset lastFreshnessSummaryUtc;
    private DateTimeOffset lastSenderPromotionBlockedLogUtc;
    private DateTimeOffset lastReducedPromotionSummaryLogUtc;
    private DateTimeOffset lastSoftScaleWarningUtc;
    private bool transitionActive;
    private long transitionStreamEpoch;
    private DateTimeOffset transitionStartedUtc;
    private bool transitionFirstRemoteApplySeen;
    private int transitionRemoteApplyCount;
    private long helperCurrentEpochStateStreamEpoch;
    private bool helperCurrentEpochWarmupActive = true;
    private int helperCurrentEpochApplyCount;
    private long helperCurrentEpochNeedMoreInputCount;
    private int helperCurrentEpochHealthySignalCount;
    private long helperCurrentEpochStaleDrops;
    private bool helperSteadyVisibleProgressActive;
    private long helperVisibleHeadFrameId = -1;
    private long helperVisibleRecoveryFloorFrameId = -1;
    private long helperLastVisibleApplyFrameId = -1;
    private long helperAppliedHeadFrameId = -1;
    private long helperStableVisibleHeadFrameId = -1;
    private long helperCurrentEpochRecoveryKeyframeApplyCount;
    private long helperFramesAppliedSinceLastGap;
    private long helperLatestVisibleProgressEpoch;
    private DateTimeOffset helperLatestVisibleProgressUtc;
    private long senderReceivedHelperProgressDuringContinuityLossCount;
    private long lastHelperProgressFactReceivedEpoch;
    private DateTimeOffset lastHelperProgressFactReceivedUtc;
    private bool remoteHelperFactHealthyActive;
    private string remoteHelperFactHealthySource = string.Empty;
    private long remoteHelperFactProofFrameId = -1;
    private long remoteHelperFactHealthyClearCount;
    private string remoteHelperFactHealthyClearReason = string.Empty;
    private long acknowledgedHelperProofEpoch;
    private long acknowledgedHelperHeadFrameId = -1;
    private DateTimeOffset acknowledgedHelperProofUtc;
    private long acknowledgedVisibleHelperProofEpoch;
    private long acknowledgedVisibleHelperHeadFrameId = -1;
    private DateTimeOffset acknowledgedVisibleHelperProofUtc;
    private long satisfiedRecoveryFloorEpoch;
    private long satisfiedRecoveryFloorFrameId = -1;
    private DateTimeOffset satisfiedRecoveryFloorUtc;
    private string satisfiedRecoveryFloorSource = string.Empty;
    private long satisfiedRecoveryFloorVisibleProofCount;
    private long continuitySignalIgnoredDueToSatisfiedFloorCount;
    private long continuitySignalIgnoredDueToVisibleSatisfiedFloorCount;
    private long postReceiptBlockerSuppressedCount;
    private string lastPostReceiptBlockerSuppressedSet = string.Empty;
    private long helperReducedModeEntryStableVisibleHeadFrameId = -1;
    private long helperReducedModeEntryStreamEpoch;
    private ScreenShareTransportTuningLevel transitionFromTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
    private ScreenShareTransportTuningLevel transitionToTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
    private ScreenShareMetrics lastMetricsSnapshot = new();
    private ScreenShareVideoStreamConfigV1? bootstrapStreamConfig;
    private long bootstrapStreamConfigEpoch;
    private int bootstrapStreamConfigSendCount;
    private bool disposed;
#if DEBUG
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif

    public TransportScreenShareCoordinator(
        Func<IScreenCaptureSource> captureSourceFactory,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync,
        IScreenShareClock? clock = null,
        Func<string, ControlDisplayInfoMessageV1, CancellationToken, Task>? sendDisplayInfoAsync = null,
        ScreenShareDisplayInfoProvider? displayInfoProvider = null,
        Func<ReadOnlyMemory<byte>, long>? estimateBridgeBytes = null,
        Func<IScreenShareTransportBackpressureProbe?>? transportBackpressureProbeResolver = null,
        Func<ScreenShareVideoStreamConfigV1, CancellationToken, Task>? sendVideoStreamConfigAsync = null,
        Func<string, ScreenShareCursorStateV1, CancellationToken, Task>? sendCursorStateAsync = null,
        Func<bool>? cursorOverlayEnabledResolver = null,
        IScreenShareCursorPositionSource? cursorPositionSource = null,
        Func<ReadOnlyMemory<byte>, string?, long, CancellationToken, Task>? sendPayloadWithRecoveryMetadataAsync = null,
        Action<string>? flushTransportQueue = null,
        Action<string, long, long, long>? armRecoveryBurstTransportFallback = null,
        Action<long>? clearRecoveryBurstTransportFallback = null)
    {
        this.captureSourceFactory = captureSourceFactory ?? throw new ArgumentNullException(nameof(captureSourceFactory));
        this.sendPayloadAsync = sendPayloadAsync ?? throw new ArgumentNullException(nameof(sendPayloadAsync));
        this.sendDisplayInfoAsync = sendDisplayInfoAsync;
        this.displayInfoProvider = displayInfoProvider ?? new ScreenShareDisplayInfoProvider();
        this.clock = clock ?? SystemScreenShareClock.Instance;
        this.estimateBridgeBytes = estimateBridgeBytes;
        this.transportBackpressureProbeResolver = transportBackpressureProbeResolver;
        this.sendVideoStreamConfigAsync = sendVideoStreamConfigAsync;
        this.sendCursorStateAsync = sendCursorStateAsync;
        this.cursorOverlayEnabledResolver = cursorOverlayEnabledResolver;
        this.cursorPositionSource = cursorPositionSource ?? new WindowsScreenShareCursorPositionSource();
        this.sendPayloadWithRecoveryMetadataAsync = sendPayloadWithRecoveryMetadataAsync;
        this.flushTransportQueue = flushTransportQueue;
        this.armRecoveryBurstTransportFallback = armRecoveryBurstTransportFallback;
        this.clearRecoveryBurstTransportFallback = clearRecoveryBurstTransportFallback;
    }

    internal event EventHandler<ScreenShareSenderDegradedModeChangedEventArgs>? SenderDegradedModeChanged;

    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return captureSource is not null && sendPipeline is not null;
            }
        }
    }

}
