using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

public enum SessionRuntimeRole
{
    None,
    Helpee,
    Helper,
}

public enum TransportState
{
    Idle,
    BridgeStarting,
    BridgeReady,
    TransportInitializing,
    Connecting,
    Handshake,
    Connected,
    Reconnecting,
    Failed,
    Disposed,
}

public enum SessionRuntimeState
{
    Idle,
    Waiting,
    IncomingJoinRequest,
    Connecting,
    Connected,
    Rejected,
    Failed,
    Disconnected,
}

public sealed class SessionRuntimeStateChangedEventArgs : EventArgs
{
    public SessionRuntimeStateChangedEventArgs(
        SessionRuntimeState state,
        SessionRuntimeRole role,
        string statusText)
    {
        State = state;
        Role = role;
        StatusText = statusText;
    }

    public SessionRuntimeState State { get; }

    public SessionRuntimeRole Role { get; }

    public string StatusText { get; }
}

public sealed class SessionRuntimeTransientStatusChangedEventArgs : EventArgs
{
    public SessionRuntimeTransientStatusChangedEventArgs(bool isVisible, string text, bool canCancel)
    {
        IsVisible = isVisible;
        Text = text;
        CanCancel = canCancel;
    }

    public bool IsVisible { get; }
    public string Text { get; }
    public bool CanCancel { get; }
}

public sealed class SessionRuntimeRemoteControlInputReceivedEventArgs : EventArgs
{
    public SessionRuntimeRemoteControlInputReceivedEventArgs(ControlInputMessageV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ControlInputMessageV1 Message { get; }

    public string? PeerId { get; }
}

public readonly record struct DiagnosticsSnapshot(
    string CurrentState,
    string SessionUiState,
    long AttemptNumber,
    string LastFailureCategory,
    string LastFailureMessage,
    double? LastConnectDurationMs,
    double? LastHandshakeDurationMs,
    double? LastBridgeStartDurationMs,
    string RuntimeSummary = "(unknown)",
    string AuthorizationSummary = "(unknown)",
    string LastAuthorizationDenialReason = "(none)",
    string SessionSecuritySummary = "(unknown)",
    string RemoteControlSummary = "(unknown)",
    string ScreenShareSummary = "(unknown)",
    string FileTransferSummary = "(unknown)",
    string ActiveInboundFileTransferId = "(none)",
    string ActiveInboundFileTransferState = "Idle",
    long? ActiveInboundFileTransferBytes = null,
    string ActiveOutboundFileTransferId = "(none)",
    string ActiveOutboundFileTransferState = "Idle",
    long? ActiveOutboundFileTransferBytes = null,
    string LastFileTransferFailureCode = "(none)",
    string LastFileTransferSavedPath = "(none)",
    string PersistenceSummary = "Healthy",
    string PersistenceWarning = "(none)");

internal sealed record SessionRuntimeWatchdogOptions(
    bool Enabled,
    bool AutoRetryEnabled,
    TimeSpan BridgeStartingTimeout,
    TimeSpan ConnectingTimeout,
    TimeSpan HandshakeTimeout,
    TimeSpan ReconnectingTimeout,
    TimeSpan SessionLivenessHeartbeatInterval,
    TimeSpan SessionLivenessSuspectTimeout,
    TimeSpan SessionLivenessTimeout)
{
    public static SessionRuntimeWatchdogOptions Default { get; } = new(
        Enabled: true,
        AutoRetryEnabled: false,
        BridgeStartingTimeout: TimeSpan.FromSeconds(8),
        ConnectingTimeout: TimeSpan.FromSeconds(20),
        HandshakeTimeout: SessionApprovalTimeouts.DefaultHumanDecisionTimeout,
        ReconnectingTimeout: TimeSpan.FromSeconds(8),
        SessionLivenessHeartbeatInterval: TimeSpan.FromSeconds(2),
        SessionLivenessSuspectTimeout: TimeSpan.FromSeconds(6),
        SessionLivenessTimeout: TimeSpan.FromSeconds(18));
}

internal static class SessionApprovalTimeouts
{
    public static TimeSpan DefaultHumanDecisionTimeout { get; } = TimeSpan.FromSeconds(45);
}

internal sealed record HelperListenerBootstrapSnapshot(
    PeerAddress Address,
    string RunId,
    long ListenerGeneration,
    long PublishedUtcMs,
    bool HostReady);

public sealed partial class SessionRuntime : IDisposable, ISessionRuntimeScreenShareControlContext, IHelperRemoteScreenSharePressurePublishTarget
{
    private static readonly TimeSpan DisposeOperationTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RemoteControlRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RemoteControlConsentDecisionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RemoteControlStartAwaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RemoteControlDeniedCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RemoteControlScreenChangedStatusDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RemoteControlLogRateLimitWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RemoteControlMoveInjectLogWindow = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RemoteControlAckMouseMoveMinInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RemoteControlAckStallWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RemoteControlRecentInputWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemoteControlStallRecoveryMinInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RemoteControlElevationProbeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RemoteControlSnapshotForceDownContinuousWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemoteControlSnapshotContinuousGapTolerance = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RemoteControlSnapshotRecentForcedUpWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RemoteControlScreenShareStopGracePeriod = TimeSpan.FromSeconds(8);
    private static readonly RemoteControlMouseButtonsMask RemoteControlKnownMouseButtonsMask =
        RemoteControlMouseButtonsMask.Left |
        RemoteControlMouseButtonsMask.Right |
        RemoteControlMouseButtonsMask.Middle |
        RemoteControlMouseButtonsMask.X1 |
        RemoteControlMouseButtonsMask.X2;
    private static readonly RemoteControlModifiersMask RemoteControlKnownModifiersMask =
        RemoteControlModifiersMask.Shift |
        RemoteControlModifiersMask.Ctrl |
        RemoteControlModifiersMask.Alt |
        RemoteControlModifiersMask.Meta |
        RemoteControlModifiersMask.Win;
    private const int RemoteControlInjectionQueueCapacity = 256;
    private const long RemoteControlConsentTokenTtlMs = 60_000;
    private const long RemoteControlAckMouseMoveMinSeqDelta = 8;
    private const string RemoteControlScreenChangedStatusTextHelper = "Screen changed";
    private const string RemoteControlScreenChangedStatusTextHelpee = "Screen changed; control stopped";
    private const string FileTransferAppDataDirectoryName = "nLink";
    private const string FileTransferTransfersDirectoryName = "transfers";
    private const string FileTransferIncomingDirectoryName = "incoming";
    private static readonly TimeSpan RemoteScreenShareStopFrameSuppressionWindow = TimeSpan.FromMilliseconds(750);

    private readonly Func<ISignalingTransport> createTransport;
    private readonly SessionChatService chatService = new();
    private readonly SessionFileTransferService fileTransferService = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly Dictionary<TransportState, long> transportStateEntryTimestamps = new();
    private readonly Dictionary<string, double> lastDurationMetricsMs = new(StringComparer.Ordinal);
    private readonly object watchdogGate = new();
    private readonly object explicitDisconnectGate = new();
    private readonly SessionRuntimeWatchdogOptions watchdogOptions;
    private readonly Func<TimeSpan, CancellationToken, Task> watchdogDelayAsync;
    private readonly ITransportTelemetrySink telemetrySink;
    private readonly BridgeReusePolicy bridgeReusePolicy;
    private readonly Func<TimeSpan, CancellationToken, Task> bridgeIdleDelayAsync;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly TimeSpan outboundHelpRequestDecisionTimeout;
    private readonly SessionAuthorizationGuard authorizationGuard;
    private readonly SessionClipboardGuard clipboardGuard;
    private readonly SessionFileTransferGuard fileTransferGuard;
    private readonly SessionAuthorizationCommandExecutor privilegedCommandExecutor;
    private readonly SessionRuntimeApprovalActions approvalActions;
    private readonly SessionRuntimeFileTransferHost fileTransferHost;
    private readonly SessionRuntimeRemoteControlActions remoteControlActions;
    private readonly SessionRuntimeScreenShareActions screenShareActions;
    private readonly SessionTransportLifecycle transportLifecycle;
    private readonly SessionRuntimeScreenShareControlHost screenShareControlHost;
    private readonly RetryPolicy watchdogRetryPolicy;
    private readonly TransportScreenShareCoordinator transportScreenShareCoordinator;
    private readonly IRemoteInputInjector remoteInputInjector;
    private readonly IRemoteCoordinateMapper remoteCoordinateMapper;
    private readonly bool remoteControlProcessElevated;
    private readonly object remoteControlInjectionQueueGate = new();
    private readonly object remoteControlLogRateLimitGate = new();
    private readonly object remoteControlWheelDeltaGate = new();
    private readonly Dictionary<string, long> remoteControlLogRateLimitTicks = new(StringComparer.Ordinal);
    private readonly LinkedList<RemoteControlInjectionWorkItem> remoteControlInjectionQueue = new();
    private LinkedListNode<RemoteControlInjectionWorkItem>? queuedRemoteControlInjectionMouseMoveNode;
    private LinkedListNode<RemoteControlInjectionWorkItem>? queuedRemoteControlInjectionSnapshotNode;
    private bool remoteControlInjectionExecutorActive;
    private readonly object remoteControlMouseMoveQueueGate = new();
    private readonly object fileTransferTerminalLogGate = new();
    private readonly HashSet<string> loggedFileTransferTerminalKeys = new(StringComparer.Ordinal);
    private string? preservedDevLocalPeerAddress;
    private string lastAuthorizationDenialReason = "(none)";

    private CancellationTokenSource? sessionCts;
    private ISignalingTransport? transport;
    private IncomingJoinRequestEventArgs? pendingJoinRequest;
    private volatile SessionRuntimeRole role;
    private volatile SessionRuntimeState state = SessionRuntimeState.Idle;
    private volatile TransportState transportState = TransportState.Idle;
    private volatile bool transportAccelerationActive;
    private volatile string transportAccelerationStatusReason = "inactive";
    private PeerAddress? currentHelperTargetAddress;
    private HelperConnectOrigin helperConnectOrigin;
    private bool helperShouldReturnToListenerWaiting;
    private volatile string statusText = string.Empty;
    private volatile bool hostReady;
    private volatile bool resetInProgress;
    private volatile bool startInProgress;
    private volatile bool remoteSessionEndHandling;
    private int fileTransferSessionEndInferenceStarted;
    private Task? explicitDisconnectTask;
    private volatile bool disposed;
    private long connectAttempt;
    private string sessionId = string.Empty;
    private string attemptSessionKey = string.Empty;
    private TransportFailure? lastTransportFailure;
    private TimingSpan bridgeStartTiming;
    private TimingSpan transportInitTiming;
    private TimingSpan connectTiming;
    private TimingSpan handshakeTiming;
    private TimingSpan reconnectTiming;
    private CancellationTokenSource? watchdogCts;
    private long watchdogGeneration;
    private ISignalingTransport? cachedBridgeTransport;
    private bool forceBridgeReuseOnce;
    private CancellationTokenSource? cachedBridgeIdleCts;
    private long cachedBridgeIdleGeneration;
    private long helperListenerGeneration;
    private HelperListenerBootstrapSnapshot? helperListenerBootstrapSnapshot;
    private bool transientStatusVisible;
    private string transientStatusText = string.Empty;
    private bool transientStatusCanCancel;
    private string remoteControlStatusHintText = string.Empty;
    private int quietHelpeeRehostInProgress;
    private int quietHelperListenerRestartInProgress;
    private bool activeSessionCounted;
    private bool activeConnectAttemptCounted;
    private bool lastDisconnectWasRemoteEnd;
    private int externalRecoveryInProgress;
    private bool allowTransportScreenShareAutoStart = true;
    private SessionSecurityState sessionSecurityState = SessionSecurityState.Empty;
    private volatile ApprovalRequest? pendingApprovalRequest;
    private volatile SessionGrant? currentSessionGrant;
    private string? currentHelperInviteToken;
    private ValidatedInviteV1? currentHelperInvite;
    private RemoteControlSessionState remoteControlSessionState = RemoteControlSessionState.Default;
    private RemoteControlDisplayInfoState remoteControlCoordinatorDisplayInfoState = RemoteControlDisplayInfoState.Empty;
    private bool hasPendingRemoteControlConsentPrompt;
    private PendingRemoteControlConsentToken? pendingRemoteControlConsentToken;
    private int suppressNextReducerSendControlResponse;
    private long remoteControlStopPriorityEpoch;
    private int remoteControlStopInputSuppressionLatched;
    private CancellationTokenSource? remoteControlRequestTimeoutCts;
    private CancellationTokenSource? remoteControlConsentTimeoutCts;
    private CancellationTokenSource? remoteControlDeniedCooldownCts;
    private CancellationTokenSource? remoteControlScreenChangedStatusCts;
    private CancellationTokenSource? remoteControlScreenShareStopGraceCts;
    private long remoteControlInputSequence;
    private long lastRemoteControlInjectedSeq;
    private long lastRemoteControlAckSentSeq;
    private long lastRemoteControlAckSentTick;
    private long remoteControlAckSentCount;
    private long helperRemoteControlLastAckSeq;
    private long helperRemoteControlLastAckAdvanceTick;
    private long helperRemoteControlLastInputSentTick;
    private long helperRemoteControlAckStallDetectedCount;
    private long helperRemoteControlStallRecoveryLastTick;
    private long helperRemoteControlStallRecoverySentCount;
    private double remoteControlWheelDeltaCarryX;
    private double remoteControlWheelDeltaCarryY;
    private ControlDisplayInfoMessageV1? latestRemoteControlDisplayInfo;
    private ControlInputMessageV1? queuedRemoteControlMouseMove;
    private bool remoteControlMouseMoveSenderActive;
    private bool hasRemoteControlRevisionMismatchCache;
    private string? lastRemoteControlRevisionMismatchDisplayId;
    private long lastRemoteControlRevisionMismatchIncomingRevision;
    private long lastRemoteControlRevisionMismatchExpectedRevision;
    private long remoteControlDebugMappingClampCount;
    private long remoteControlDebugQueueDropCount;
    private long remoteControlDebugInjectionSuppressedCount;
    private long remoteControlDebugQueueFlushCount;
    private long remoteControlDebugLastMappedNxBits;
    private long remoteControlDebugLastMappedNyBits;
    private int remoteControlDebugLastMappedPx;
    private int remoteControlDebugLastMappedPy;
    private long remoteControlDebugLastMappedVersion;
    private int remoteControlForceNextMoveInjectionLog;
    private long remoteControlElevationProbeNextTick;
    private int remoteControlElevationWarningVisible;
    private string remoteControlElevationWarningText = string.Empty;
    private RemoteControlMouseButtonsMask remoteControlAppliedMouseButtonsMask = RemoteControlMouseButtonsMask.None;
    private RemoteControlModifiersMask remoteControlAppliedModifiersMask = RemoteControlModifiersMask.None;
    private long remoteControlSnapshotReceivedCount;
    private long remoteControlSnapshotAppliedCount;
    private long remoteControlSnapshotUnstuckButtonsCount;
    private long remoteControlSnapshotUnstuckModifiersCount;
    private long remoteControlSnapshotLastReceivedSeq;
    private int remoteControlSnapshotLastReceivedModifiersMask;
    private int remoteControlSnapshotLastReceivedMouseButtonsMask;
    private long remoteControlSnapshotLastAppliedSeq;
    private int remoteControlSnapshotLastAppliedModifiersMask;
    private int remoteControlSnapshotLastAppliedMouseButtonsMask;
    private long remoteControlSnapshotLastReceivedTick;
    private long remoteControlSnapshotContinuousStartTick;
    private long remoteControlSnapshotForcedUpLeftTick;
    private long remoteControlSnapshotForcedUpRightTick;
    private long remoteControlSnapshotForcedUpMiddleTick;
    private long remoteControlSnapshotForcedUpX1Tick;
    private long remoteControlSnapshotForcedUpX2Tick;
    private DateTimeOffset remoteScreenShareFramesSuppressedUntilUtc;
    private long remoteScreenShareSuppressFramesCapturedBeforeOrAtUtcMs;
    private long lastScreenShareStopSuppressedLogTick;
    private long helperRemoteScreenShareAcceptedFrames;
    private long helperRemoteScreenShareLastAcceptedEpoch;
    private int helperRemoteScreenShareSawConfig;
    private readonly object helperRemoteScreenSharePressureGate = new();
    private readonly long[] helperRemoteRecentAppliedFrameAgesMs = new long[3];
    private int helperRemoteRecentAppliedFrameCount;
    private int helperRemoteRecentAppliedFrameIndex;
    private long helperRemoteLastAppliedFrameAgeMs = -1;
    private DateTimeOffset helperRemoteLastAppliedFrameUtc;
    private long helperRemoteLastApplyCadenceMs = -1;
    private long helperRemoteApplyCadenceObserved;
    private long helperRemoteTotalApplyCadenceMs;
    private long helperRemoteViewerStaleDropCount;
    private long helperRemoteViewerSoftStaleDropCount;
    private int helperRemoteConsecutiveVeryHighAppliedFrames;
    private int helperRemoteConsecutiveStaleDropWindows;
    private long helperRemoteCurrentPressureEpoch;
    private DateTimeOffset helperRemoteCurrentPressureEpochStartedUtc;
    private DateTimeOffset helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc;
    private bool helperRemoteCurrentPressureEpochFirstApplySeen;
    private DateTimeOffset helperRemoteCurrentPressureEpochFirstVisibleApplyUtc;
    private int helperRemoteCurrentPressureEpochApplyCount;
    private long helperRemoteCurrentPressureEpochRecoveryKeyframeApplyCountLocal;
    private long helperRemoteCurrentPressureEpochNeedMoreInputCount;
    private long helperRemoteCurrentPressureEpochStaleDropCount;
    private long helperRemoteCurrentPressureEpochSoftStaleDropCount;
    private long helperRemoteCurrentPressureEpochLastVisibleApplyFrameId = -1;
    private long helperRemoteCurrentPressureEpochContinuityLossTicks;
    private long helperRemoteCurrentPressureEpochWarmupTicks;
    private long helperRemoteCurrentPressureEpochBeforeFirstVisibleApplyTicks;
    private long helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameTicks;
    private long helperRemoteCurrentPressureEpochSlowApplyCadenceTicks;
    private long helperRemoteCurrentPressureEpochHighFrameAgeTicks;
    private long helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToVisibleProgressCount;
    private long helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToHeadAdvanceCount;
    private long helperRemoteCurrentPressureEpochActionableHighFrameAgeCount;
    private long helperRemoteCurrentPressureEpochPostRecoveryAgeGraceSuppressedCount;
    private long helperRemoteCurrentPressureEpochPostRecoveryHighFrameAgeSuppressedTicks;
    private long helperRemoteCurrentPressureEpochRepeatedStaleDropsTicks;
    private long helperRemoteCurrentPressureEpochBridgeHealthTicks;
    private long helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount;
    private long helperRemoteCurrentPressureEpochBridgeHealthActionableCount;
    private long helperRemoteCurrentPressureEpochBridgeHealthQuarantineSuppressedCount;
    private int helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount;
    private long helperRemoteCurrentPressureEpochBridgeHealthActionableWithoutQueueOrDropCount;
    private long helperRemoteCurrentPressureEpochVisibleAppliesBeforePressureReenabled = -1;
    private long helperRemoteCurrentPressureEpochVisibleAppliesDuringSettleCount;
    private bool helperRemoteCurrentPressureEpochBaselineEstablished;
    private double helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs;
    private long helperRemoteCurrentPressureEpochBaselineSampleCount;
    private bool helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply;
    private long helperRemoteCurrentPressureEpochBaselineFrozenDueToStallCount;
    private long helperRemoteCurrentPressureEpochBaselineReseedAfterRecoveryCount;
    private bool helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending;
    private int helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies;
    private long helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs;
    private DateTimeOffset helperRemoteCurrentPressureEpochBaselineReseedStartedUtc;
    private long helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = -1;
    private long helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId = -1;
    private long helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId = -1;
    private int helperRemoteCurrentPressureEpochAgePressureConsecutiveCount;
    private int helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount;
    private long helperRemoteCurrentPressureEpochCatchUpSuppressedDueToProgressCount;
    private DateTimeOffset helperRemoteCurrentPressureEpochCadenceStallStartedUtc;
    private bool helperRemoteCurrentPressureEpochCadenceStallTriggered;
    private long helperRemoteCurrentPressureEpochCadenceStallWindowCount;
    private long helperRemoteCurrentPressureEpochCadenceStallTriggerCount;
    private DateTimeOffset helperRemoteCurrentPressureEpochWarmupStartedUtc;
    private DateTimeOffset helperRemoteCurrentPressureEpochWarmupEndedUtc;
    private bool helperRemoteContinuityRecoveryActive;
    private long helperRemoteContinuityRecoveryEpoch;
    private DateTimeOffset helperRemoteContinuityRecoveryStartedUtc;
    private bool helperRemoteContinuityRecoveryTimeoutSent;
    private long helperRemotePostRecoveryAgeGraceEpoch;
    private DateTimeOffset helperRemotePostRecoveryAgeGraceUntilUtc;
    private bool helperRemotePostRecoveryHealthySignalSent;
    private string helperRemoteRecoveryWindowAbortReason = string.Empty;
    private long helperRemoteSteadyProgressEpoch;
    private bool helperRemoteSteadyVisibleProgressActive;
    private long helperRemoteSteadyProgressActivationFrameId = -1;
    private long helperRemoteSteadyProgressVisibleHeadFrameId = -1;
    private long helperRemoteSteadyProgressStableVisibleHeadFrameId = -1;
    private long helperRemoteSteadyProgressFramesAppliedSinceLastGap;
    private long helperRemoteSteadyVisibleProgressClearedCount;
    private string helperRemoteSteadyVisibleProgressClearedReason = string.Empty;
    private long helperRemotePostRecoveryHealthyLatchCount;
    private long helperRemotePostRecoveryHealthyLatchClearCount;
    private string helperRemotePostRecoveryHealthyLatchClearReason = string.Empty;
    private DateTimeOffset helperRemotePostRecoveryHealthyLastHeadAdvanceUtc;
    private long helperRemoteLastSentSteadyProgressEpoch;
    private bool helperRemoteLastSentSteadyVisibleProgressActive;
    private long helperRemoteLastSentStableVisibleHeadFrameId = -1;
    private long helperRemoteLastSentVisibleHeadFrameId = -1;
    private long helperRemoteLastSentFramesAppliedSinceLastGap;
    private long helperRemoteLastSentVisibleApplyFrameId = -1;
    private long helperRemoteLastSentAppliedHeadFrameId = -1;
    private long helperRemotePressureSendBypassedForVisibleProgressCount;
    private long helperRemoteProofKeepaliveSendCount;
    private long helperRemoteProofKeepaliveTimerDrivenSendCount;
    private long helperRemoteLastProofKeepaliveHeadFrameId = -1;
    private DateTimeOffset helperRemoteLastProofKeepaliveSentUtc;
    private long helperRemoteActiveRecoveryReceiptOwnerEpoch;
    private long helperRemoteActiveRecoveryReceiptOwnerFrameId = -1;
    private long helperRemotePublishedRecoveryReceiptEpoch;
    private long helperRemotePublishedRecoveryReceiptOwnerFrameId = -1;
    private long helperRemotePublishedRecoveryReceiptVisibleRecoveryFrameId = -1;
    private long helperRemotePublishedRecoveryReceiptVisibleHeadFrameId = -1;
    private string helperRemotePublishedRecoveryReceiptKind = string.Empty;
    private DateTimeOffset helperRemotePublishedRecoveryReceiptUtc;
    private bool helperRemotePublishedRecoveryReceiptRetrySent;
    private long helperRemoteRecoveryReceiptRetryGeneration;
    private long helperRemoteLastFirstVisibleApplyToPressureSendMs = -1;
    private DateTimeOffset helperRemoteLastRecoveryKeyframeRequestUtc;
    private long helperRemoteLastRecoveryKeyframeRequestEpoch;
    private long helperRemoteTransportRebindGeneration;
    private long helperRemoteTransportRebindRecoveredGeneration;
    private string helperRemoteTransportRebindSessionId = string.Empty;
    private ScreenSharePressureMode lastSentScreenSharePressureMode = ScreenSharePressureMode.Normal;
    private string lastSentScreenSharePressureReason = ScreenSharePressureProtocol.PressureReasonHealthy;
    private long lastSentScreenSharePressureAgeMs;
    private long lastSentScreenSharePressureStaleDrops;
    private DateTimeOffset lastSentScreenSharePressureUtc;
    private DateTimeOffset lastSentScreenSharePressureModeEnteredUtc;
    private long lastObservedRemoteScreenShareStaleDrops;
    private int healthyScreenSharePressureIntervals;
    private readonly object sessionFlowGate = new();
    private SessionFlowState sessionFlowState = SessionFlowState.Initial;
    private SessionFlowSnapshot currentFlowSnapshot = new(
        SessionFlowPhase.NoSession,
        SessionUiPhase.Idle,
        SessionRuntimeRole.None,
        SessionRuntimeState.Idle,
        TransportState.Idle,
        SessionFlowEndOrigin.None,
        LocalEndInProgress: false,
        HasPendingRequest: false,
        HasPendingApproval: false,
        ApprovalActive: false,
        ApprovedCapabilities: CapabilityGrant.None,
        ShouldSuppressConnectedControls: false,
        TerminalKind: SessionTerminalKind.None,
        TerminalStatusText: string.Empty,
        FailureTitle: string.Empty,
        FailureMessage: string.Empty,
        FailureActionText: string.Empty,
        ShouldShowPeerEndedNotice: false,
        ShouldClearConversationUi: false,
        StatusText: string.Empty,
        FailureReason: string.Empty,
        SessionId: null,
        HelperIdentity: null,
        RemoteEndpoint: null);

    public SessionRuntime(Func<ISignalingTransport> createTransport)
        : this(createTransport, SessionRuntimeWatchdogOptions.Default, DefaultWatchdogDelayAsync, TransportTelemetry.Noop, BridgeReusePolicy.Default, null, null, null)
    {
    }

    internal SessionRuntime(
        Func<ISignalingTransport> createTransport,
        SessionRuntimeWatchdogOptions? watchdogOptions,
        Func<TimeSpan, CancellationToken, Task>? watchdogDelayAsync = null,
        ITransportTelemetrySink? telemetrySink = null,
        BridgeReusePolicy? bridgeReusePolicy = null,
        Func<TimeSpan, CancellationToken, Task>? bridgeIdleDelayAsync = null,
        IRemoteInputInjector? remoteInputInjector = null,
        IRemoteCoordinateMapper? remoteCoordinateMapper = null,
        Func<DateTimeOffset>? nowProvider = null,
        Func<IScreenCaptureSource>? transportScreenCaptureSourceFactory = null,
        TimeSpan? outboundHelpRequestDecisionTimeout = null)
    {
        this.createTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));
        this.watchdogOptions = watchdogOptions ?? SessionRuntimeWatchdogOptions.Default;
        this.watchdogDelayAsync = watchdogDelayAsync ?? DefaultWatchdogDelayAsync;
        this.telemetrySink = telemetrySink ?? TransportTelemetry.Noop;
        this.bridgeReusePolicy = bridgeReusePolicy ?? BridgeReusePolicy.Default;
        this.bridgeIdleDelayAsync = bridgeIdleDelayAsync ?? DefaultWatchdogDelayAsync;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        this.outboundHelpRequestDecisionTimeout = outboundHelpRequestDecisionTimeout ?? SessionApprovalTimeouts.DefaultHumanDecisionTimeout;
        authorizationGuard = new SessionAuthorizationGuard(this.nowProvider);
        clipboardGuard = new SessionClipboardGuard(this.nowProvider);
        fileTransferGuard = new SessionFileTransferGuard(this.nowProvider);
        privilegedCommandExecutor = new SessionAuthorizationCommandExecutor(this);
        approvalActions = new SessionRuntimeApprovalActions(this);
        fileTransferHost = new SessionRuntimeFileTransferHost(this);
        remoteControlActions = new SessionRuntimeRemoteControlActions(this);
        screenShareActions = new SessionRuntimeScreenShareActions(this);
        transportLifecycle = new SessionTransportLifecycle(this);
        screenShareControlHost = new SessionRuntimeScreenShareControlHost(
            this,
            new HelperRemoteScreenSharePressurePublisher(this));
        this.remoteInputInjector = remoteInputInjector ?? RemoteInputInjectorFactory.CreateDefault();
        this.remoteCoordinateMapper = remoteCoordinateMapper ?? new DefaultRemoteCoordinateMapper();
        remoteControlProcessElevated = WindowsInputIntegrityProbe.IsCurrentProcessElevated();
        watchdogRetryPolicy = new RetryPolicy(
            new RetryPolicyOptions(
                MaxAttempts: 3,
                InitialDelay: TimeSpan.FromMilliseconds(200),
                MaxDelay: TimeSpan.FromSeconds(1),
                JitterRatio: 0.10));
        watchdogRetryPolicy.EventEmitted += OnWatchdogRetryPolicyEvent;
        transportStateEntryTimestamps[transportState] = Stopwatch.GetTimestamp();
        transportScreenShareCoordinator = new TransportScreenShareCoordinator(
            transportScreenCaptureSourceFactory ?? ScreenCaptureFactory.CreateForTransport,
            screenShareActions.SendPayloadAsync,
            sendPayloadWithRecoveryMetadataAsync: screenShareActions.SendPayloadWithRecoveryMetadataAsync,
            sendDisplayInfoAsync: SendRemoteControlDisplayInfoAsync,
            transportBackpressureProbeResolver: () => transport as IScreenShareTransportBackpressureProbe,
            sendVideoStreamConfigAsync: screenShareActions.SendVideoStreamConfigAsync,
            sendCursorStateAsync: screenShareActions.SendCursorStateAsync,
            cursorOverlayEnabledResolver: ShouldUsePassiveScreenShareCursorOverlayForTransport,
            flushTransportQueue: reason =>
            {
                if (transport is IScreenShareTransportPolicyController policyController)
                {
                    policyController.FlushScreenShareTransportQueue(reason);
                }
            },
            armRecoveryBurstTransportFallback: (sessionId, streamEpoch, burstToken, ownerFrameId) =>
            {
                if (transport is NknSignalingTransport nknTransport)
                {
                    nknTransport.ArmRecoveryBurstControlFallback(sessionId, streamEpoch, burstToken, ownerFrameId);
                }
            },
            clearRecoveryBurstTransportFallback: burstToken =>
            {
                if (transport is NknSignalingTransport nknTransport)
                {
                    nknTransport.ResolveRecoveryBurstControlFallback(burstToken);
                }
            });
        transportScreenShareCoordinator.SenderDegradedModeChanged += OnScreenShareSenderDegradedModeChanged;

        chatService.MessageReceived += OnChatMessageReceived;
        chatService.MessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        chatService.StateChanged += OnChatStateChanged;
        fileTransferService.TransferChanged += OnFileTransferChanged;
    }

    private static Task DefaultWatchdogDelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    private void MarkActiveSession()
    {
        if (activeSessionCounted)
        {
            return;
        }

        ActiveRuntimeCounters.IncSessions();
        activeSessionCounted = true;
    }

    private void ClearActiveSession()
    {
        fileTransferService.ResetSessionState();
        lock (fileTransferTerminalLogGate)
        {
            loggedFileTransferTerminalKeys.Clear();
        }

        if (!activeSessionCounted)
        {
            return;
        }

        ActiveRuntimeCounters.DecSessions();
        activeSessionCounted = false;
    }

    private void MarkActiveConnectAttempt()
    {
        if (activeConnectAttemptCounted)
        {
            return;
        }

        ActiveRuntimeCounters.IncConnectAttempts();
        activeConnectAttemptCounted = true;
    }

    private void ClearActiveConnectAttempt()
    {
        if (!activeConnectAttemptCounted)
        {
            return;
        }

        ActiveRuntimeCounters.DecConnectAttempts();
        activeConnectAttemptCounted = false;
    }

    private void RunCountedBackgroundTask(
        Func<Task> body,
        bool countAsTransportTask = true,
        [CallerMemberName] string operationName = "")
    {
        if (countAsTransportTask)
        {
            ActiveRuntimeCounters.IncTransportTasks();
        }

        _ = BackgroundTaskRunner.Run(
            body,
            source: "SessionRuntime",
            operationName: operationName,
            onFinally: countAsTransportTask ? ActiveRuntimeCounters.DecTransportTasks : null,
            contextProvider: () =>
                $"role={role}; session_state={state}; transport_state={transportState}; disposed={disposed}; resetting={resetInProgress}");
    }

    private void PublishSessionFlowEvent(SessionFlowEvent flowEvent)
    {
        SessionFlowSnapshot? nextSnapshot = null;
        lock (sessionFlowGate)
        {
            sessionFlowState = SessionFlowReducer.Reduce(sessionFlowState, flowEvent);
            var activeGrant = currentSessionGrant;
            var projectedSnapshot = SessionFlowProjector.Project(new SessionFlowProjectionInput(
                sessionFlowState,
                role,
                state,
                transportState,
                statusText,
                disposed,
                transientStatusVisible,
                IsPassiveHelperListenerState(),
                helperShouldReturnToListenerWaiting,
                HasPendingHelpRequest || HasPendingOutboundHelpRequest,
                pendingApprovalRequest is not null || state == SessionRuntimeState.IncomingJoinRequest,
                EvaluateApprovalActive(),
                activeGrant?.Capabilities ?? sessionSecurityState.ApprovedCapabilities,
                activeGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value,
                activeGrant?.HelperIdentity.Value ?? sessionSecurityState.HelperAddress?.Value,
                ResolveCurrentRemoteEndpoint(),
                sessionSecurityState.VerificationCode,
                helperConnectOrigin,
                lastTransportFailure));
            if (Equals(currentFlowSnapshot, projectedSnapshot))
            {
                return;
            }

            currentFlowSnapshot = projectedSnapshot;
            nextSnapshot = projectedSnapshot;
        }

        if (nextSnapshot is not null)
        {
            FlowSnapshotChanged?.Invoke(this, new SessionFlowSnapshotChangedEventArgs(nextSnapshot));
        }
    }

    private bool EvaluateApprovalActive()
    {
        EnsureApprovalGrantActive();
        return sessionSecurityState.IsApprovalActive(nowProvider());
    }

    private string? ResolveCurrentRemoteEndpoint()
    {
        return currentHelperTargetAddress?.Value;
    }

    public event EventHandler<SessionRuntimeStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionRuntimeTransientStatusChangedEventArgs>? TransientStatusChanged;
    public event EventHandler<SessionFlowSnapshotChangedEventArgs>? FlowSnapshotChanged;
    public event EventHandler? RemoteControlStateChanged;
    public event EventHandler<SessionRuntimeRemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;

    public event EventHandler? IncomingJoinRequestAvailable;

    public event EventHandler? Approved;

    public event EventHandler? Rejected;

    public event EventHandler? Disconnected;
    public event EventHandler? RemoteSessionEnded;

    internal event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
    internal event EventHandler? ScreenShareStopped;
    internal event EventHandler<ScreenShareCursorStateReceivedEventArgs>? ScreenShareCursorStateReceived;

    public event EventHandler<ChatMessageEventArgs>? ChatMessageReceived;

    public event EventHandler? ChatMessageReceivedBeforeApproved;

    public event EventHandler? ChatStateChanged;
    public event EventHandler? SessionSecurityStateChanged;
    public event EventHandler? TransportAccelerationStateChanged;
    public event EventHandler<SessionFileTransferSnapshotChangedEventArgs>? FileTransferChanged;
    public event EventHandler? HelperListenerBootstrapSnapshotChanged;

    public SessionRuntimeState State => state;
    public TransportState TransportLifecycleState => transportState;
    public bool IsTransportAccelerationActive => transportAccelerationActive;
    public string TransportAccelerationStatusReason => transportAccelerationStatusReason;

    public SessionRuntimeRole Role => role;

    public string StatusText => statusText;
    public SessionFlowSnapshot FlowSnapshot
    {
        get
        {
            lock (sessionFlowGate)
            {
                return currentFlowSnapshot;
            }
        }
    }
    public bool IsTransientStatusVisible => transientStatusVisible;
    public string TransientStatusText => transientStatusText;
    public bool CanCancelTransientStatus => transientStatusCanCancel;
    public TransportFailure? LastTransportFailure => lastTransportFailure;
    public PeerAddress? CurrentLocalPeerAddress =>
        role == SessionRuntimeRole.Helpee && !hostReady
            ? null
            : transport is IAuthoritativeConnectedAddressSource authoritativeConnectedAddressSource &&
              !authoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress
                ? null
            : transport is ILocalPeerAddressSignalingTransport localAddressTransport &&
              PeerAddress.TryParse(localAddressTransport.LocalPeerAddress, out var peerAddress)
                ? peerAddress
                : null;
    public PeerAddress? CurrentInvitePeerAddress => CurrentLocalPeerAddress;
    internal HelperListenerBootstrapSnapshot? CurrentHelperListenerBootstrapSnapshot => helperListenerBootstrapSnapshot;

    public bool HasPendingJoinRequest => pendingJoinRequest is not null;
    public ApprovalRequest? PendingApprovalRequest => pendingApprovalRequest;
    public SessionSecurityState SecurityState => sessionSecurityState;
    public SessionGrant? CurrentSessionGrant
    {
        get
        {
            EnsureApprovalGrantActive();
            return currentSessionGrant;
        }
    }

    public RemoteControlSessionState RemoteControlSessionState => remoteControlSessionState;
    public ControlState ControlState => remoteControlSessionState.ControlState;
    public string? ControllerPeerId => remoteControlSessionState.ControllerPeerId;
    public string? CurrentControlRequestId => remoteControlSessionState.CurrentControlRequestId;
    public string? ConsentToken => remoteControlSessionState.ConsentToken;
    public bool LocalSupportsRemoteControl => remoteControlSessionState.SupportsRemoteControl;
    public bool RemoteSupportsRemoteControl => remoteControlSessionState.PeerSupportsRemoteControl;
    public bool SessionSupportsRemoteControl => remoteControlSessionState.SessionSupportsRemoteControl;
    public bool RemoteControlAvailable => remoteControlSessionState.RemoteControlAvailable && CanPerform(SessionCapability.RemoteControl);
    public bool HasPendingRemoteControlConsentPrompt => hasPendingRemoteControlConsentPrompt;
    public string RemoteControlStatusHintText => remoteControlStatusHintText;
    public bool RemoteControlMappingAvailable => IsUsableRemoteControlDisplayInfo(latestRemoteControlDisplayInfo);
    public string? RemoteControlMappingDisplayId =>
        IsUsableRemoteControlDisplayInfo(latestRemoteControlDisplayInfo) ? latestRemoteControlDisplayInfo!.DisplayId : null;
    public long? RemoteControlMappingRevision =>
        IsUsableRemoteControlDisplayInfo(latestRemoteControlDisplayInfo) ? latestRemoteControlDisplayInfo!.Revision : null;
    public bool RemoteControlInjectionSupported => remoteInputInjector.IsSupported;
    public bool RemoteControlAdminRestartRequired => Volatile.Read(ref remoteControlElevationWarningVisible) != 0;
    public string RemoteControlAdminWarningText => remoteControlElevationWarningText;
    public bool RemoteControlProcessElevated => remoteControlProcessElevated;
    public int RemoteControlInjectionQueueDepth
    {
        get
        {
            lock (remoteControlInjectionQueueGate)
            {
                return remoteControlInjectionQueue.Count;
            }
        }
    }

    public int RemoteControlOutgoingMouseMoveQueueDepth
    {
        get
        {
            lock (remoteControlMouseMoveQueueGate)
            {
                return queuedRemoteControlMouseMove is null ? 0 : 1;
            }
        }
    }

    private bool IsPassiveHelperListenerState()
    {
        return role == SessionRuntimeRole.Helper &&
               state == SessionRuntimeState.Waiting &&
               currentHelperTargetAddress is null;
    }

    public long RemoteControlDebugMappingClampCount => Interlocked.Read(ref remoteControlDebugMappingClampCount);
    public long RemoteControlDebugQueueDropCount => Interlocked.Read(ref remoteControlDebugQueueDropCount);
    public long RemoteControlDebugInjectionSuppressedCount => Interlocked.Read(ref remoteControlDebugInjectionSuppressedCount);
    public long RemoteControlDebugQueueFlushCount => Interlocked.Read(ref remoteControlDebugQueueFlushCount);

    public bool CanSendChat => chatService.CanSend && CanPerform(SessionCapability.Chat);
    public bool LastDisconnectWasRemoteEnd => lastDisconnectWasRemoteEnd;
    public SessionFileTransferSnapshot FileTransferSnapshot => fileTransferService.Snapshot;

    public bool CanPerform(SessionCapability capability)
    {
        return EvaluateCapabilityAuthorization(capability).IsAuthorized;
    }

    public bool IsCapabilityGranted(CapabilityGrant capability)
    {
        EnsureApprovalGrantActive();
        var effectiveSecurityState = BuildEffectiveSecurityStateForAuthorization();
        if (transport is not ISessionSecuritySignalingTransport)
        {
            return false;
        }

        if (capability == CapabilityGrant.None)
        {
            return currentSessionGrant is not null &&
                   effectiveSecurityState.SessionId is not null &&
                   effectiveSecurityState.HelperAddress is not null;
        }

        var remaining = capability;
        foreach (var sessionCapability in Enum.GetValues<SessionCapability>())
        {
            var mappedGrant = SessionAuthorizationService.ToCapabilityGrant(sessionCapability);
            if ((capability & mappedGrant) != mappedGrant)
            {
                continue;
            }

            if (!CanPerform(sessionCapability))
            {
                return false;
            }

            remaining &= ~mappedGrant;
        }

        return remaining == CapabilityGrant.None;
    }

    private bool RequireCapability(SessionCapability capability)
    {
        return EvaluateCapabilityAuthorization(capability).IsAuthorized;
    }

    private bool RequireCapability(SessionCapability capability, string operation)
    {
        var authorization = EvaluateCapabilityAuthorization(capability);
        if (authorization.IsAuthorized)
        {
            return true;
        }

        LogAuthorizationDenied(operation, capability, authorization.Failure);
        return false;
    }

    internal bool TryAuthorizePrivilegedAction(SessionPrivilegedAction action)
    {
        return action.Kind switch
        {
            SessionPrivilegedActionKind.ApprovalGrant or SessionPrivilegedActionKind.ApprovalDeny => true,
            SessionPrivilegedActionKind.FileTransferStartSend or
            SessionPrivilegedActionKind.FileTransferAcceptIncoming or
            SessionPrivilegedActionKind.FileTransferDeclineIncoming => TryAuthorizeFileTransferSend(),
            SessionPrivilegedActionKind.FileTransferCancel => TryAuthorizeFileTransferAction(action.Operation),
            SessionPrivilegedActionKind.FileTransferPause or
            SessionPrivilegedActionKind.FileTransferResume => TryAuthorizeFileTransferAction(action.Operation),
            SessionPrivilegedActionKind.ClipboardSync or SessionPrivilegedActionKind.ClipboardApply => TryAuthorizeClipboardSync(),
            SessionPrivilegedActionKind.ScreenShareDispatch => RequireCapability(SessionCapability.ScreenShare, action.Operation),
            SessionPrivilegedActionKind.ChatSend => RequireCapability(SessionCapability.Chat, action.Operation),
            SessionPrivilegedActionKind.RemoteControlRequest or
            SessionPrivilegedActionKind.RemoteControlRespond or
            SessionPrivilegedActionKind.RemoteControlStop or
            SessionPrivilegedActionKind.RemoteControlInputSend or
            SessionPrivilegedActionKind.RemoteControlSnapshotSend => RequireCapability(SessionCapability.RemoteControl, action.Operation),
            _ => false,
        };
    }

    private SessionAuthorizationResult EvaluateCapabilityAuthorization(SessionCapability capability)
    {
        EnsureApprovalGrantActive();
        var effectiveSecurityState = BuildEffectiveSecurityStateForAuthorization();
        return authorizationGuard.Evaluate(
            hasSecurityTransport: transport is ISessionSecuritySignalingTransport,
            securityState: effectiveSecurityState,
            grant: currentSessionGrant,
            capability: capability);
    }

    private SessionSecurityState BuildEffectiveSecurityStateForAuthorization()
    {
        if (currentSessionGrant is null)
        {
            return sessionSecurityState;
        }

        if (sessionSecurityState.SessionId == currentSessionGrant.SessionId &&
            sessionSecurityState.HelperAddress == currentSessionGrant.HelperIdentity &&
            sessionSecurityState.InviteValidated &&
            sessionSecurityState.HandshakeCompleted &&
            sessionSecurityState.HandshakeState == SessionHandshakeState.Verified &&
            sessionSecurityState.ApprovalGranted)
        {
            return sessionSecurityState;
        }

        return (SessionSecurityState.Empty with
        {
            SessionId = currentSessionGrant.SessionId,
            HelpeeAddress = sessionSecurityState.HelpeeAddress ?? CurrentLocalPeerAddress,
            HelperAddress = currentSessionGrant.HelperIdentity,
            InviteValidated = true,
            HandshakeCompleted = true,
            HandshakeState = SessionHandshakeState.Verified,
        }).WithApproval(currentSessionGrant);
    }

    private void LogAuthorizationDenied(
        string operation,
        SessionCapability capability,
        SessionAuthorizationFailure failure)
    {
        var reason = MapAuthorizationFailureReason(failure);
        lastAuthorizationDenialReason = reason;
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=authorization_denied; operation={operation}; capability={capability}; reason={reason}; session_present={FormatYesNo(sessionSecurityState.SessionId is not null)}; helper_identity_present={FormatYesNo(sessionSecurityState.HelperAddress is not null)}");
    }

    private static string MapAuthorizationFailureReason(SessionAuthorizationFailure failure)
    {
        return failure switch
        {
            SessionAuthorizationFailure.SecurityTransportRequired => "authorization_transport_required",
            SessionAuthorizationFailure.InviteNotValidated => "authorization_invite_not_validated",
            SessionAuthorizationFailure.HandshakeIncomplete => "authorization_handshake_incomplete",
            SessionAuthorizationFailure.ApprovalMissing => "authorization_approval_missing",
            SessionAuthorizationFailure.SessionIdMissing => "authorization_session_missing",
            SessionAuthorizationFailure.HelperIdentityMissing => "authorization_helper_identity_missing",
            SessionAuthorizationFailure.SessionMismatch => "authorization_session_mismatch",
            SessionAuthorizationFailure.HelperIdentityMismatch => "authorization_helper_identity_mismatch",
            SessionAuthorizationFailure.Expired => "authorization_expired",
            SessionAuthorizationFailure.CapabilityMissing => "authorization_capability_missing",
            _ => "authorization_denied",
        };
    }

    private bool RequireRemoteControlAuxiliaryCapability(
        string operation,
        string action,
        string? requestId = null,
        string? controllerPeerId = null,
        string? rateLimitKey = null,
        TimeSpan? rateLimitWindow = null)
    {
        if (RequireCapability(SessionCapability.RemoteControl, operation))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(rateLimitKey) ||
            ShouldEmitRemoteControlRateLimitedLog(rateLimitKey, rateLimitWindow ?? RemoteControlLogRateLimitWindow))
        {
            LogRemoteControlViolation(action, "capability_not_granted", requestId, controllerPeerId);
        }

        return false;
    }

    private void LogApprovalGranted(ApprovalDecision decision)
    {
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=approval_granted; session_id={decision.SessionId.Value}; helper_identity={decision.HelperIdentity.Value}; capabilities={decision.ApprovedCapabilities}; expires_at_utc={decision.ExpiresAtUtc:O}");
    }

    private void LogApprovalDenied(string reason, ApprovalRequest? approvalRequest)
    {
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=approval_denied; reason={reason}; session_id={approvalRequest?.SessionId.Value ?? "(none)"}; helper_identity={approvalRequest?.HelperIdentity.Value ?? "(none)"}; requested_capabilities={approvalRequest?.RequestedCapabilities.ToString() ?? "(none)"}");
    }

    private static string NormalizeIncomingRejectReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "local_reject" : reason.Trim();
    }

    private void LogApprovalInvalidated(string reason)
    {
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=approval_invalidated; reason={reason}; session_id={sessionSecurityState.SessionId?.Value ?? "(none)"}; helper_identity={sessionSecurityState.HelperAddress?.Value ?? "(none)"}");
    }

    private void LogScreenShareRejected(string operation, string messageType, string reason, string? messageSessionId)
    {
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=screen_share_rejected; operation={operation}; message_type={messageType}; reason={reason}; session_id={messageSessionId ?? "(none)"}; expected_session_id={ResolveAuthorizedSessionIdForScreenShare() ?? "(none)"}; helper_identity={sessionSecurityState.HelperAddress?.Value ?? "(none)"}");
    }

    private void LogFileTransferRejected(
        string operation,
        FileTransferAccessResult result,
        FileTransferDescriptor? descriptor = null)
    {
        var authReason = MapAuthorizationFailureReason(result.AuthorizationFailure);
        lastAuthorizationDenialReason = authReason;
        var fileNameLength = descriptor is null
            ? "(none)"
            : descriptor.FileName.Length.ToString(CultureInfo.InvariantCulture);
        var fileSizeBytes = descriptor is null
            ? "(none)"
            : descriptor.FileSizeBytes.ToString(CultureInfo.InvariantCulture);
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=file_transfer_rejected; operation={operation}; reason={result.Failure}; auth_reason={authReason}; session_present={FormatYesNo((descriptor?.SessionId ?? sessionSecurityState.SessionId) is not null)}; helper_identity_present={FormatYesNo((descriptor?.HelperIdentity ?? sessionSecurityState.HelperAddress) is not null)}; file_name_len={fileNameLength}; file_size_bytes={fileSizeBytes}");
    }

    private void LogClipboardRejected(
        string operation,
        ClipboardAccessResult result,
        ClipboardTransferDescriptor? descriptor = null)
    {
        var authReason = MapAuthorizationFailureReason(result.AuthorizationFailure);
        lastAuthorizationDenialReason = authReason;
        var textLength = descriptor is null
            ? "(none)"
            : descriptor.TextLength.ToString(CultureInfo.InvariantCulture);
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=clipboard_rejected; operation={operation}; reason={result.Failure}; auth_reason={authReason}; session_present={FormatYesNo((descriptor?.SessionId ?? sessionSecurityState.SessionId) is not null)}; helper_identity_present={FormatYesNo((descriptor?.HelperIdentity ?? sessionSecurityState.HelperAddress) is not null)}; text_length={textLength}");
    }

    internal bool TryAuthorizeClipboardSync()
    {
        EnsureApprovalGrantActive();
        var effectiveSecurityState = BuildEffectiveSecurityStateForAuthorization();
        var result = clipboardGuard.AuthorizeSync(
            hasSecurityTransport: transport is ISessionSecuritySignalingTransport,
            securityState: effectiveSecurityState,
            grant: currentSessionGrant);
        if (result.IsAllowed)
        {
            return true;
        }

        LogClipboardRejected("clipboard_sync", result);
        return false;
    }

    internal ClipboardAccessResult ValidateInboundClipboardTransfer(
        ClipboardTransferDescriptor descriptor,
        int maxTextLength = ClipboardTransferDefaults.DefaultMaxTextLength)
    {
        EnsureApprovalGrantActive();
        var effectiveSecurityState = BuildEffectiveSecurityStateForAuthorization();
        var result = clipboardGuard.ValidateTransfer(
            hasSecurityTransport: transport is ISessionSecuritySignalingTransport,
            securityState: effectiveSecurityState,
            grant: currentSessionGrant,
            descriptor,
            maxTextLength);
        if (!result.IsAllowed)
        {
            LogClipboardRejected("clipboard_receive", result, descriptor);
        }

        return result;
    }

    internal bool TryAuthorizeFileTransferSend()
        => TryAuthorizeFileTransferAction("file_transfer_send");

    private bool TryAuthorizeFileTransferAction(string operation)
    {
        EnsureApprovalGrantActive();
        var effectiveSecurityState = BuildEffectiveSecurityStateForAuthorization();
        var result = fileTransferGuard.AuthorizeSend(
            hasSecurityTransport: transport is ISessionSecuritySignalingTransport,
            securityState: effectiveSecurityState,
            grant: currentSessionGrant);
        if (result.IsAllowed)
        {
            return true;
        }

        LogFileTransferRejected(operation, result);
        return false;
    }

    private bool TryAuthorizeExistingFileTransferControl(string transferId, string operation)
    {
        EnsureApprovalGrantActive();

        var snapshot = fileTransferService.Snapshot;
        var activeTransfer =
            IsActiveFileTransferSnapshot(snapshot.Outbound, transferId) ? snapshot.Outbound :
            IsActiveFileTransferSnapshot(snapshot.Inbound, transferId) ? snapshot.Inbound :
            null;
        if (activeTransfer is null)
        {
            return false;
        }

        if (currentSessionGrant is SessionGrant grant)
        {
            if ((grant.Capabilities & CapabilityGrant.FileTransfer) != CapabilityGrant.FileTransfer ||
                !string.Equals(activeTransfer.SessionId, grant.SessionId.Value, StringComparison.Ordinal))
            {
                return false;
            }

            LogExistingFileTransferControlAuthorized(operation, activeTransfer, "active_grant");
            return true;
        }

        if (state != SessionRuntimeState.Connected ||
            sessionSecurityState.SessionId is not SessionId sessionId ||
            sessionSecurityState.HelperAddress is null ||
            !string.Equals(activeTransfer.SessionId, sessionId.Value, StringComparison.Ordinal))
        {
            return false;
        }

        LogExistingFileTransferControlAuthorized(operation, activeTransfer, "active_transfer_session_binding");
        return true;
    }

    private static bool IsActiveFileTransferSnapshot(FileTransferTransferSnapshot? snapshot, string transferId)
        => snapshot is not null &&
           !snapshot.IsTerminal &&
           string.Equals(snapshot.TransferId, transferId, StringComparison.Ordinal);

    private static void LogExistingFileTransferControlAuthorized(
        string operation,
        FileTransferTransferSnapshot snapshot,
        string reason)
    {
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=file_transfer_existing_control_authorized; operation={operation}; reason={reason}; session_id={snapshot.SessionId}; transfer_id={snapshot.TransferId}; direction={snapshot.Direction}; state={snapshot.State}");
    }

    internal FileTransferAccessResult ValidateInboundFileTransferMetadata(
        FileTransferDescriptor descriptor,
        FileTransferStoragePolicy storagePolicy)
    {
        EnsureApprovalGrantActive();
        var effectiveSecurityState = BuildEffectiveSecurityStateForAuthorization();
        var result = fileTransferGuard.ValidateReceiveMetadata(
            hasSecurityTransport: transport is ISessionSecuritySignalingTransport,
            securityState: effectiveSecurityState,
            grant: currentSessionGrant,
            descriptor,
            storagePolicy);
        if (!result.IsAllowed)
        {
            LogFileTransferRejected("file_transfer_metadata_receive", result, descriptor);
        }

        return result;
    }

    internal FileTransferAccessResult ValidateInboundFileTransferChunk(
        FileTransferChunkDescriptor descriptor,
        FileTransferStoragePolicy storagePolicy)
    {
        EnsureApprovalGrantActive();
        var effectiveSecurityState = BuildEffectiveSecurityStateForAuthorization();
        var result = fileTransferGuard.ValidateChunk(
            hasSecurityTransport: transport is ISessionSecuritySignalingTransport,
            securityState: effectiveSecurityState,
            grant: currentSessionGrant,
            descriptor,
            storagePolicy);
        if (!result.IsAllowed)
        {
            LogFileTransferRejected(
                "file_transfer_chunk_receive",
                result,
                new FileTransferDescriptor(
                    descriptor.SessionId,
                    descriptor.HelperIdentity,
                    descriptor.FileName,
                    descriptor.FileSizeBytes));
        }

        return result;
    }

    internal FileTransferWriteOpenResult OpenAuthorizedInboundFileWriteStream(
        FileTransferDescriptor descriptor,
        FileTransferStoragePolicy storagePolicy)
    {
        EnsureApprovalGrantActive();
        var effectiveSecurityState = BuildEffectiveSecurityStateForAuthorization();
        var result = fileTransferGuard.OpenReceiveWriteStream(
            hasSecurityTransport: transport is ISessionSecuritySignalingTransport,
            securityState: effectiveSecurityState,
            grant: currentSessionGrant,
            descriptor,
            storagePolicy);
        if (!result.IsAllowed)
        {
            LogFileTransferRejected("file_transfer_write_open", result.Access, descriptor);
        }

        return result;
    }

    public async Task CancelTransientAsync()
    {
        bool shouldReset;
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            shouldReset = !disposed && transportState is TransportState.BridgeStarting
                or TransportState.BridgeReady
                or TransportState.TransportInitializing
                or TransportState.Connecting
                or TransportState.Handshake
                or TransportState.Reconnecting;
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (!shouldReset)
        {
            return;
        }

        await ResetAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!disposed && state != SessionRuntimeState.Connected)
            {
                SetState(SessionRuntimeState.Idle, string.Empty);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public bool HasSessionKey => chatService.HasSessionKey;

    public bool IsApproved => chatService.IsApproved;

    internal long GetTransportStateEntryTimestamp(TransportState state)
    {
        return transportStateEntryTimestamps.TryGetValue(state, out var ts) ? ts : 0L;
    }

    internal double? GetLastDurationMetricMilliseconds(string metricName)
    {
        return lastDurationMetricsMs.TryGetValue(metricName, out var value) ? value : null;
    }

    internal long GetConnectAttemptForTests() => connectAttempt;

    internal string GetSessionIdForTests() => sessionId;

    internal TransportFailureCategory? GetLastFailureCategoryForTests() => lastTransportFailure?.Category;

    internal bool CanAutoStartTransportScreenShareForTests => allowTransportScreenShareAutoStart;
    internal bool IsTransportScreenShareActive => transportScreenShareCoordinator.IsActive;
    internal bool IsTransportScreenShareActiveForTests => transportScreenShareCoordinator.IsActive;
    internal bool IsRemoteInputInjectionSupportedForTests => remoteInputInjector.IsSupported;
    internal bool HasCachedBridgeTransportForTests() => cachedBridgeTransport is not null;
    internal void SetRoleForTests(SessionRuntimeRole value) => role = value;
    internal bool IsDisposedForFileTransferHost => disposed;

    internal void AttachFileTransferRuntimeTransport(IFileTransferSignalingTransport transport)
    {
        fileTransferService.AttachTransport(transport);
        SyncFileTransferFlowControlMode();
    }

    internal void DetachFileTransferRuntimeTransport()
    {
        fileTransferService.DetachTransport();
    }

    internal void RunFileTransferBackgroundTask(Func<Task> body)
    {
        RunCountedBackgroundTask(body, countAsTransportTask: false);
    }

    internal Task<FileTransferReceiveDestination> OpenInboundFileTransferDestinationAsync(FileTransferIncomingOffer offer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ct.ThrowIfCancellationRequested();
        EnsureApprovalGrantActive();

        if (sessionSecurityState.HelperAddress is not PeerAddress helperIdentity)
        {
            throw new InvalidOperationException("File-transfer helper identity is unavailable.");
        }

        var descriptor = new FileTransferDescriptor(
            new SessionId(offer.SessionId),
            helperIdentity,
            offer.FileName,
            offer.FileSizeBytes);
        var storagePolicy = CreateInboundFileTransferStoragePolicy(offer);
        var result = OpenAuthorizedInboundFileWriteStream(descriptor, storagePolicy);
        if (!result.IsAllowed || result.Handle is null)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Access.Message)
                    ? "Could not open the inbound file-transfer destination."
                    : result.Access.Message);
        }

        var handle = result.Handle;
        var plan = result.Plan
            ?? throw new InvalidOperationException("Inbound file-transfer write plan is unavailable.");

        LocalOperationalLog.Info(
            "Session",
            $"event=file_transfer_destination_opened; role={role}; session_id={offer.SessionId}; transfer_id={offer.TransferId}; file_name_len={offer.FileName.Length}; final_path={plan.FinalPath}; temp_path={plan.TempPath}");

        return Task.FromResult(
            new FileTransferReceiveDestination(
                handle.Stream,
                finalizeAsync: finalizeCt => handle.FinalizeAsync(finalizeCt),
                dispose: handle.Dispose,
                disposeAsync: () => handle.DisposeAsync(),
                finalPath: plan.FinalPath,
                safeFileName: plan.SafeFileName));
    }

    internal void LogRuntimeFileTransferSnapshotCore(SessionFileTransferSnapshot snapshot)
    {
        if (snapshot.Inbound is { IsTerminal: true } inbound)
        {
            var terminalKey = string.Create(
                CultureInfo.InvariantCulture,
                $"inbound|{inbound.SessionId}|{inbound.TransferId}|{inbound.State}|{inbound.ErrorCode ?? "(none)"}|{inbound.SavedFilePath ?? "(none)"}");
            bool shouldLog;
            lock (fileTransferTerminalLogGate)
            {
                shouldLog = loggedFileTransferTerminalKeys.Add(terminalKey);
            }

            if (shouldLog)
            {
                LocalOperationalLog.Info(
                    "Session",
                    $"event=file_transfer_inbound_terminal; role={role}; session_id={inbound.SessionId}; transfer_id={inbound.TransferId}; state={inbound.State}; error_code={inbound.ErrorCode ?? "(none)"}; saved_path={inbound.SavedFilePath ?? "(none)"}");
            }
        }

        if (snapshot.Outbound is { IsTerminal: true } outbound)
        {
            var terminalKey = string.Create(
                CultureInfo.InvariantCulture,
                $"outbound|{outbound.SessionId}|{outbound.TransferId}|{outbound.State}|{outbound.ErrorCode ?? "(none)"}");
            bool shouldLog;
            lock (fileTransferTerminalLogGate)
            {
                shouldLog = loggedFileTransferTerminalKeys.Add(terminalKey);
            }

            if (shouldLog)
            {
                LocalOperationalLog.Info(
                    "Session",
                    $"event=file_transfer_outbound_terminal; role={role}; session_id={outbound.SessionId}; transfer_id={outbound.TransferId}; state={outbound.State}; error_code={outbound.ErrorCode ?? "(none)"}");
            }
        }
    }

    public DiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var fileTransferSnapshot = fileTransferService.Snapshot;
        var persistenceSnapshot = PersistenceDiagnostics.Snapshot();
        return new DiagnosticsSnapshot(
            CurrentState: transportState.ToString(),
            SessionUiState: state.ToString(),
            AttemptNumber: connectAttempt,
            LastFailureCategory: lastTransportFailure?.Category.ToString() ?? "(none)",
            LastFailureMessage: string.IsNullOrWhiteSpace(lastTransportFailure?.Message) ? "(none)" : lastTransportFailure!.Message,
            LastConnectDurationMs: GetLastDurationMetricMilliseconds("connect_duration_ms"),
            LastHandshakeDurationMs: GetLastDurationMetricMilliseconds("handshake_duration_ms"),
            LastBridgeStartDurationMs: GetLastDurationMetricMilliseconds("bridge_start_duration_ms"),
            RuntimeSummary: BuildRuntimeSummary(),
            AuthorizationSummary: BuildAuthorizationSummary(),
            LastAuthorizationDenialReason: NormalizeDiagnosticsText(lastAuthorizationDenialReason),
            SessionSecuritySummary: BuildSessionSecuritySummary(),
            RemoteControlSummary: BuildRemoteControlSummary(),
            ScreenShareSummary: BuildScreenShareSummary(),
            FileTransferSummary: BuildFileTransferSummary(fileTransferSnapshot),
            ActiveInboundFileTransferId: NormalizeDiagnosticsText(fileTransferSnapshot.Inbound?.TransferId),
            ActiveInboundFileTransferState: fileTransferSnapshot.InboundState.ToString(),
            ActiveInboundFileTransferBytes: fileTransferSnapshot.Inbound?.BytesTransferred,
            ActiveOutboundFileTransferId: NormalizeDiagnosticsText(fileTransferSnapshot.Outbound?.TransferId),
            ActiveOutboundFileTransferState: fileTransferSnapshot.OutboundState.ToString(),
            ActiveOutboundFileTransferBytes: fileTransferSnapshot.Outbound?.BytesTransferred,
            LastFileTransferFailureCode: NormalizeDiagnosticsText(GetLastFileTransferFailureCode(fileTransferSnapshot)),
            LastFileTransferSavedPath: NormalizeDiagnosticsText(GetLastFileTransferSavedPath(fileTransferSnapshot)),
            PersistenceSummary: persistenceSnapshot.Summary,
            PersistenceWarning: persistenceSnapshot.LastWarning);
    }

    internal ScreenShareLiveDiagnosticsSnapshot GetScreenShareLiveDiagnosticsSnapshot()
    {
        var snapshot = transportScreenShareCoordinator.GetLiveDiagnosticsSnapshot();
        return snapshot with
        {
            RemoteViewerActive = screenShareControlHost.RemoteScreenShareActive,
        };
    }

    public void NotifyLocalEndRequested()
    {
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.LocalEndRequested,
            Role,
            State,
            TransportLifecycleState,
            "local_end_requested"));
    }

    internal void RefreshSessionFlowProjection()
    {
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.None,
            Role,
            State,
            TransportLifecycleState,
            statusText));
    }

    private string BuildAuthorizationSummary()
        => $"chat={DescribeCapabilityAuthorization(SessionCapability.Chat)}; " +
           $"file_transfer={DescribeCapabilityAuthorization(SessionCapability.FileTransfer)}; " +
           $"screenshare={DescribeCapabilityAuthorization(SessionCapability.ScreenShare)}; " +
           $"remote_control={DescribeCapabilityAuthorization(SessionCapability.RemoteControl)}; " +
           $"clipboard={DescribeCapabilityAuthorization(SessionCapability.Clipboard)}; " +
           $"last_denial={NormalizeDiagnosticsText(lastAuthorizationDenialReason)}";

    private string BuildRuntimeSummary()
    {
        SessionFlowSnapshot snapshot;
        lock (sessionFlowGate)
        {
            snapshot = currentFlowSnapshot;
        }

        var lastTerminalReason = string.IsNullOrWhiteSpace(snapshot.FailureReason)
            ? "(none)"
            : snapshot.FailureReason.Trim();
        return $"role={role}; " +
               $"runtime_state={state}; " +
               $"transport_state={transportState}; " +
               $"terminal={snapshot.TerminalKind}; " +
               $"last_terminal_reason={lastTerminalReason}; " +
               $"pending_request={FormatYesNo(HasPendingHelpRequest || HasPendingOutboundHelpRequest)}; " +
               $"pending_approval={FormatYesNo(pendingApprovalRequest is not null || state == SessionRuntimeState.IncomingJoinRequest)}";
    }

    private string BuildSessionSecuritySummary()
    {
        var approvalActive = sessionSecurityState.IsApprovalActive(nowProvider());
        return $"invite_validated={FormatYesNo(sessionSecurityState.InviteValidated)}; " +
               $"handshake={sessionSecurityState.HandshakeState}; " +
               $"approval_granted={FormatYesNo(sessionSecurityState.ApprovalGranted)}; " +
               $"approval_active={FormatYesNo(approvalActive)}; " +
               $"capabilities={FormatCapabilities(sessionSecurityState.ApprovedCapabilities)}";
    }

    private string BuildRemoteControlSummary()
    {
        var statusHint = string.IsNullOrWhiteSpace(remoteControlStatusHintText) ? "(none)" : remoteControlStatusHintText;
        return $"state={remoteControlSessionState.ControlState}; " +
               $"available={FormatYesNo(RemoteControlAvailable)}; " +
               $"local_support={FormatYesNo(LocalSupportsRemoteControl)}; " +
               $"remote_support={FormatYesNo(RemoteSupportsRemoteControl)}; " +
               $"session_support={FormatYesNo(SessionSupportsRemoteControl)}; " +
               $"status_hint={statusHint}";
    }

    private string BuildScreenShareSummary()
    {
        var authorized = EvaluateCapabilityAuthorization(SessionCapability.ScreenShare).IsAuthorized;
        return $"active={FormatYesNo(IsSessionScreenShareActive())}; " +
               $"authorized={FormatYesNo(authorized)}; " +
               $"auto_start={FormatYesNo(allowTransportScreenShareAutoStart)}; " +
               $"host_ready={FormatYesNo(hostReady)}";
    }

    private bool IsSessionScreenShareActive()
        => transportScreenShareCoordinator.IsActive || screenShareControlHost.RemoteScreenShareActive;

    private FileTransferFlowControlMode ResolveFileTransferFlowControlMode()
    {
        if (remoteControlSessionState.ControlState == ControlState.Requesting || hasPendingRemoteControlConsentPrompt)
        {
            return FileTransferFlowControlMode.InteractiveCritical;
        }

        if (remoteControlSessionState.ControlState == ControlState.Active || IsSessionScreenShareActive())
        {
            return FileTransferFlowControlMode.Interactive;
        }

        return FileTransferFlowControlMode.Background;
    }

    private void SyncFileTransferFlowControlMode()
    {
        var screenShareActive = IsSessionScreenShareActive();
        fileTransferService.SetSessionScreenShareActive(screenShareActive);
        fileTransferService.SetSessionScreenShareDegraded(
            screenShareActive &&
            !string.Equals(transportScreenShareCoordinator.GetMetricsSnapshot().FreshnessMode, "normal", StringComparison.Ordinal));
        fileTransferService.SetFlowControlMode(ResolveFileTransferFlowControlMode());
        var mixedV4TransferActive = screenShareActive && fileTransferService.IsV4MixedScreenShareTransferActive;
        transportScreenShareCoordinator.SetFileTransferDegradedHint(
            screenShareActive && (fileTransferService.IsTransferDegraded || mixedV4TransferActive));
        transportScreenShareCoordinator.SetFileTransferCatchUpOnlyHint(
            screenShareActive && (fileTransferService.IsCatchUpOnlyPressureActive || mixedV4TransferActive));
    }

    private string BuildFileTransferSummary(SessionFileTransferSnapshot snapshot)
    {
        var authorized = EvaluateCapabilityAuthorization(SessionCapability.FileTransfer).IsAuthorized;
        return $"authorized={FormatYesNo(authorized)}; " +
               $"inbound_state={snapshot.InboundState}; " +
               $"inbound_transfer_id={NormalizeDiagnosticsText(snapshot.Inbound?.TransferId)}; " +
               $"inbound_bytes={snapshot.Inbound?.BytesTransferred.ToString() ?? "(none)"}; " +
               $"outbound_state={snapshot.OutboundState}; " +
               $"outbound_transfer_id={NormalizeDiagnosticsText(snapshot.Outbound?.TransferId)}; " +
               $"outbound_bytes={snapshot.Outbound?.BytesTransferred.ToString() ?? "(none)"}; " +
               $"last_failure_code={NormalizeDiagnosticsText(GetLastFileTransferFailureCode(snapshot))}; " +
               $"last_saved_path={NormalizeDiagnosticsText(GetLastFileTransferSavedPath(snapshot))}";
    }

    private static string? GetLastFileTransferFailureCode(SessionFileTransferSnapshot snapshot)
        => snapshot.Inbound?.ErrorCode ??
           snapshot.Outbound?.ErrorCode;

    private static string? GetLastFileTransferSavedPath(SessionFileTransferSnapshot snapshot)
        => snapshot.Inbound?.SavedFilePath;

    private static string NormalizeDiagnosticsText(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();

    private string DescribeCapabilityAuthorization(SessionCapability capability)
    {
        var authorization = EvaluateCapabilityAuthorization(capability);
        return authorization.IsAuthorized ? "yes" : $"no ({authorization.Failure})";
    }

    private static string FormatYesNo(bool value) => value ? "yes" : "no";

    private static string FormatCapabilities(CapabilityGrant capabilities)
        => capabilities == CapabilityGrant.None ? "none" : capabilities.ToString();

    internal async Task HandleExternalRecoveryAsync(ExternalRecoveryTrigger triggers, CancellationToken ct)
    {
        if (triggers == ExternalRecoveryTrigger.None)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(disposed, this);

        if (Interlocked.CompareExchange(ref externalRecoveryInProgress, 1, 0) != 0)
        {
            LocalOperationalLog.Info("Network", $"event=external_recovery_skipped; reason=inflight; triggers={triggers}");
            return;
        }

        try
        {
            SessionRuntimeRole roleSnapshot;
            PeerAddress? targetAddressSnapshot;
            string? inviteTokenSnapshot;
            ValidatedInviteV1? inviteSnapshot;
            SessionRuntimeState uiStateSnapshot;
            TransportState transportStateSnapshot;
            ISignalingTransport? activeTransportSnapshot;
            ISignalingTransport? cachedTransportSnapshot;
            bool connectInFlight;

            await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (disposed)
                {
                    return;
                }

                roleSnapshot = role;
                targetAddressSnapshot = currentHelperTargetAddress;
                inviteTokenSnapshot = currentHelperInviteToken;
                inviteSnapshot = currentHelperInvite;
                uiStateSnapshot = state;
                transportStateSnapshot = transportState;
                activeTransportSnapshot = transport;
                cachedTransportSnapshot = cachedBridgeTransport;
                connectInFlight = activeConnectAttemptCounted || transportState is TransportState.BridgeStarting
                    or TransportState.BridgeReady
                    or TransportState.TransportInitializing
                    or TransportState.Connecting
                    or TransportState.Handshake
                    or TransportState.Reconnecting;
            }
            finally
            {
                lifecycleGate.Release();
            }

            var pingRequested = triggers is not ExternalRecoveryTrigger.None;
            var pingSucceeded = false;
            if (pingRequested)
            {
                pingSucceeded = await TryPingBridgeForExternalRecoveryAsync(
                    activeTransportSnapshot,
                    cachedTransportSnapshot,
                    ct).ConfigureAwait(false);
            }

            if (connectInFlight)
            {
                LocalOperationalLog.Info(
                    "Network",
                    $"event=external_recovery_skipped; reason=connect_inflight; triggers={triggers}; state={transportStateSnapshot}");
                return;
            }

            var hasReconnectTarget = roleSnapshot switch
            {
                SessionRuntimeRole.Helpee => true,
                SessionRuntimeRole.Helper => inviteSnapshot is null && targetAddressSnapshot is not null,
                _ => false,
            };

            if (roleSnapshot == SessionRuntimeRole.Helper &&
                inviteSnapshot is not null &&
                uiStateSnapshot is SessionRuntimeState.Failed or SessionRuntimeState.Disconnected)
            {
                LocalOperationalLog.Info(
                    "Network",
                    $"event=external_recovery_noop; reason=invite_reauth_required; triggers={triggers}; ping_ok={pingSucceeded}; ui_state={uiStateSnapshot}; transport_state={transportStateSnapshot}; session_id={inviteSnapshot.SessionId.Value}");
                return;
            }

            var shouldReconnect =
                hasReconnectTarget &&
                (
                    !pingSucceeded ||
                    uiStateSnapshot is SessionRuntimeState.Failed or SessionRuntimeState.Disconnected
                );

            if (!shouldReconnect)
            {
                LocalOperationalLog.Info(
                    "Network",
                    $"event=external_recovery_noop; triggers={triggers}; ping_ok={pingSucceeded}; ui_state={uiStateSnapshot}; transport_state={transportStateSnapshot}");
                return;
            }

            LocalOperationalLog.Warn(
                "Network",
                $"event=external_recovery_reconnect; triggers={triggers}; role={roleSnapshot}; ui_state={uiStateSnapshot}; transport_state={transportStateSnapshot}");

            await ResetAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);

            if (roleSnapshot == SessionRuntimeRole.Helpee)
            {
                await StartHelpeeAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await StartHelperAsync(targetAddressSnapshot!.Value, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref externalRecoveryInProgress, 0);
        }
    }

    public void SetReliabilityAttempt(SessionReliabilityAttempt? attempt)
    {
        chatService.SetReliabilityAttempt(attempt);
    }

    public async Task StartHelpeeAsync(CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync(uiCt);
        try
        {
            ThrowIfStartInProgress();
            startInProgress = true;

            try
            {
                await ResetCoreAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
                BeginConnectAttempt(SessionRuntimeRole.Helpee, "address_native");
                TransitionTo(TransportState.TransportInitializing, "start_helpee");

                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(uiCt);
                var nextTransport = AcquireTransportForNewSession(out var reusedCachedBridge);
                EnsureSessionSecurityTransport(nextTransport);
                sessionCts = linkedCts;
                transport = nextTransport;
                role = SessionRuntimeRole.Helpee;
                hostReady = false;
                currentHelperTargetAddress = null;
                pendingJoinRequest = null;
                RefreshRemoteControlCapabilitiesFromTransport();
                PublishSessionFlowEvent(new SessionFlowEvent(
                    SessionFlowEventKind.StartHelpee,
                    role,
                    state,
                    transportState,
                    "start_helpee"));
                SessionTimeline.Record("Started");
                SessionTimeline.Record("Hosting");

                WireTransport(nextTransport);
                chatService.AttachTransport(nextTransport);
                AttachFileTransferTransport(nextTransport);
                if (nextTransport is NknSignalingTransport)
                {
                    TransitionTo(TransportState.BridgeStarting, "nkn_bridge_starting");
                    if (reusedCachedBridge)
                    {
                        EmitSyntheticWarmBridgeLifecycle();
                    }
                }
                else
                {
                    TransitionTo(TransportState.Connecting, "host_start");
                }

                SetState(SessionRuntimeState.Waiting, "Waiting for helper…");

                _ = RunHostAsync(nextTransport, linkedCts.Token);
                if (nextTransport is IHostReadySignalingTransport hostReadyTransport)
                {
                    await hostReadyTransport.WaitUntilHostReadyAsync(linkedCts.Token).ConfigureAwait(false);
                }

                hostReady = true;
                SetState(SessionRuntimeState.Waiting, "Waiting for helper…");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HandleSynchronousStartFailure(ex, "host_start_sync_failed");
                throw;
            }
        }
        finally
        {
            startInProgress = false;
            lifecycleGate.Release();
        }
    }

    internal Task StartHelperAsync(PeerAddress targetAddress, CancellationToken uiCt)
    {
        return StartHelperCoreAsync(
            targetAddress,
            invite: null,
            inviteToken: null,
            HelperConnectOrigin.DirectInvite,
            uiCt);
    }

    public Task StartHelperAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken uiCt)
    {
        if (string.IsNullOrWhiteSpace(inviteToken))
        {
            throw new ArgumentException("Invite token is required.", nameof(inviteToken));
        }

        ArgumentNullException.ThrowIfNull(invite);
        return StartHelperCoreAsync(
            invite.TargetAddress,
            invite,
            inviteToken.Trim(),
            HelperConnectOrigin.DirectInvite,
            uiCt);
    }

    private sealed class HelperListenerHandoffFallbackException : Exception
    {
        public HelperListenerHandoffFallbackException(string reason, Exception innerException)
            : base(reason, innerException)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    private async Task StartHelperCoreAsync(
        PeerAddress targetAddress,
        ValidatedInviteV1? invite,
        string? inviteToken,
        HelperConnectOrigin connectOrigin,
        CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (invite is not null && connectOrigin == HelperConnectOrigin.IncomingHelpRequest)
        {
            LocalOperationalLog.Info(
                "Session",
                $"event=helper_listener_handoff_started; mode=warm; target={targetAddress.Value}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");

            try
            {
                await StartHelperCoreAttemptAsync(
                        targetAddress,
                        invite,
                        inviteToken,
                        connectOrigin,
                        uiCt,
                        discardCachedBridgeTransportBeforeInviteJoin: false,
                        handoffMode: "warm")
                    .ConfigureAwait(false);
                return;
            }
            catch (HelperListenerHandoffFallbackException ex)
            {
                LocalOperationalLog.Warn(
                    "Session",
                    $"event=helper_listener_handoff_fallback; reason={ex.Reason}; ex={ex.InnerException?.GetType().Name ?? ex.GetType().Name}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");

                await StartHelperCoreAttemptAsync(
                        targetAddress,
                        invite,
                        inviteToken,
                        connectOrigin,
                        uiCt,
                        discardCachedBridgeTransportBeforeInviteJoin: true,
                        handoffMode: "cold")
                    .ConfigureAwait(false);
                return;
            }
        }

        await StartHelperCoreAttemptAsync(
                targetAddress,
                invite,
                inviteToken,
                connectOrigin,
                uiCt,
                discardCachedBridgeTransportBeforeInviteJoin: invite is not null,
                handoffMode: null)
            .ConfigureAwait(false);
    }

    private async Task StartHelperCoreAttemptAsync(
        PeerAddress targetAddress,
        ValidatedInviteV1? invite,
        string? inviteToken,
        HelperConnectOrigin connectOrigin,
        CancellationToken uiCt,
        bool discardCachedBridgeTransportBeforeInviteJoin,
        string? handoffMode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        CancellationTokenSource? linkedCts = null;
        ISignalingTransport? nextTransport = null;
        var joinRequestSent = false;
        var reusedCachedBridge = false;

        await lifecycleGate.WaitAsync(uiCt);
        try
        {
            ThrowIfStartInProgress();
            startInProgress = true;

            forceBridgeReuseOnce =
                invite is not null &&
                connectOrigin == HelperConnectOrigin.IncomingHelpRequest &&
                string.Equals(handoffMode, "warm", StringComparison.Ordinal);

            await ResetCoreAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
            BeginConnectAttempt(
                SessionRuntimeRole.Helper,
                invite is null ? $"addr:{targetAddress.Value}" : $"invite:{invite.SessionId.Value}");
            TransitionTo(
                TransportState.TransportInitializing,
                invite is null ? "start_helper_address" : "start_helper_invite");

            if (discardCachedBridgeTransportBeforeInviteJoin)
            {
                DiscardCachedBridgeTransport();
            }

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(uiCt);
            nextTransport = AcquireTransportForNewSession(out reusedCachedBridge);
            if (invite is null && nextTransport is NknSignalingTransport)
            {
                linkedCts.Dispose();
                linkedCts = null;
                nextTransport.Dispose();
                throw new InvalidOperationException("Invite-targeted helper connect is required for NKN sessions.");
            }

            EnsureSessionSecurityTransport(nextTransport);
            sessionCts = linkedCts;
            transport = nextTransport;
            role = SessionRuntimeRole.Helper;
            helperConnectOrigin = connectOrigin;
            helperShouldReturnToListenerWaiting =
                connectOrigin is HelperConnectOrigin.Listener or HelperConnectOrigin.IncomingHelpRequest;
            hostReady = false;
            currentHelperTargetAddress = targetAddress;
            currentHelperInviteToken = inviteToken;
            currentHelperInvite = invite;
            pendingJoinRequest = null;
            RefreshRemoteControlCapabilitiesFromTransport();
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.StartHelperConnect,
                role,
                state,
                transportState,
                invite is null ? "start_helper_address" : "start_helper_invite"));
            SessionTimeline.Record("Started");
            SessionTimeline.Record("Joining");

            WireTransport(nextTransport);
            chatService.AttachTransport(nextTransport);
            AttachFileTransferTransport(nextTransport);
            if (nextTransport is NknSignalingTransport)
            {
                TransitionTo(TransportState.BridgeStarting, "nkn_bridge_starting");
                if (reusedCachedBridge)
                {
                    EmitSyntheticWarmBridgeLifecycle();
                }
            }
            else
            {
                TransitionTo(TransportState.Connecting, "join_start");
            }

            SetState(SessionRuntimeState.Connecting, "Connecting…");
        }
        finally
        {
            if (nextTransport is null)
            {
                forceBridgeReuseOnce = false;
            }

            startInProgress = false;
            lifecycleGate.Release();
        }

        try
        {
            if (invite is not null)
            {
                if (nextTransport is not IInviteTargetSignalingTransport inviteTargetTransport)
                {
                    throw new NotSupportedException("This transport does not support invite-targeted helper connect.");
                }

                await inviteTargetTransport.JoinByInviteAsync(inviteToken!, invite, linkedCts.Token).ConfigureAwait(false);
            }
            else if (nextTransport is IAddressTargetSignalingTransport addressTargetTransport)
            {
                await addressTargetTransport.JoinByAddressAsync(targetAddress.Value, linkedCts.Token).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException("This transport does not support address-targeted helper connect.");
            }

            joinRequestSent = true;
            if (!string.IsNullOrWhiteSpace(handoffMode))
            {
                var helperAddress =
                    (CurrentLocalPeerAddress?.Value ??
                     (nextTransport as ILocalPeerAddressSignalingTransport)?.LocalPeerAddress ??
                     string.Empty).Trim();
                LocalOperationalLog.Info(
                    "Session",
                    $"event=helper_listener_handoff_completed; mode={handoffMode}; reused_cached_bridge={(reusedCachedBridge ? 1 : 0)}; helper_address={(string.IsNullOrWhiteSpace(helperAddress) ? "(none)" : helperAddress)}; target={targetAddress.Value}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
            }
        }
        catch (OperationCanceledException) when (uiCt.IsCancellationRequested || linkedCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            invite is not null &&
            connectOrigin == HelperConnectOrigin.IncomingHelpRequest &&
            string.Equals(handoffMode, "warm", StringComparison.Ordinal) &&
            !joinRequestSent)
        {
            throw new HelperListenerHandoffFallbackException("pre_join_handshake_failure", ex);
        }

        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            if (disposed ||
                linkedCts.IsCancellationRequested ||
                !ReferenceEquals(transport, nextTransport) ||
                !ReferenceEquals(sessionCts, linkedCts))
            {
                return;
            }

            try
            {
                if (transportState != TransportState.Handshake &&
                    IsTransportTransitionAllowed(transportState, TransportState.Handshake))
                {
                    TransitionTo(TransportState.Handshake, "join_request_sent");
                }
            }
            catch (InvalidOperationException) when (
                transportState is TransportState.Handshake or
                TransportState.Connected or
                TransportState.Failed or
                TransportState.Reconnecting or
                TransportState.Idle or
                TransportState.Disposed)
            {
                LocalOperationalLog.Info(
                    "Session",
                    $"event=transport_state_transition_superseded; attempted=Handshake; reason=join_request_sent; current={transportState}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task ApproveAsync(CancellationToken uiCt)
    {
        ApprovalRequest? approvalRequest;
        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            approvalRequest = pendingApprovalRequest;
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (approvalRequest is null)
        {
            return;
        }

        await ApproveAsync(approvalRequest.RequestedCapabilities, uiCt).ConfigureAwait(false);
    }

    public Task ApproveAsync(CapabilityGrant approvedCapabilities, CancellationToken uiCt)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ApprovalGrant, "approval_grant"),
            ct => approvalActions.ApproveAsync(approvedCapabilities, ct),
            uiCt);
    }

    internal async Task ApproveCoreAsync(CapabilityGrant approvedCapabilities, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        IncomingJoinRequestEventArgs? request;
        ApprovalDecision? decision = null;
        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            request = pendingJoinRequest;
            pendingJoinRequest = null;
            if (request is null)
            {
                return;
            }

            if (pendingApprovalRequest is not ApprovalRequest approvalRequest)
            {
                return;
            }

            if (approvedCapabilities == CapabilityGrant.None)
            {
                throw new ArgumentOutOfRangeException(nameof(approvedCapabilities), "Approval must grant at least one capability.");
            }

            decision = approvalRequest.CreateDecision(
                approvedCapabilities,
                nowProvider().Add(SessionSecurityDefaults.GrantLifetime),
                nowProvider());
            pendingApprovalRequest = null;
            LogApprovalGranted(decision);
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.LocalApprovalStarted,
                role,
                state,
                transportState,
                "local_approve"));
            TransitionTo(TransportState.Handshake, "local_approve");
            SetState(SessionRuntimeState.Connecting, "Connecting…");
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await request.ApproveAsync(decision, uiCt).ConfigureAwait(false);
        }
        catch
        {
            // UI state is already optimistic; transport disconnect will reconcile if needed.
        }
    }

    public Task RejectAsync(CancellationToken uiCt)
    {
        return RejectAsync(reason: null, uiCt);
    }

    public Task RejectAsync(string? reason, CancellationToken uiCt)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ApprovalDeny, "approval_deny"),
            ct => approvalActions.RejectAsync(reason, ct),
            uiCt);
    }

    internal async Task RejectCoreAsync(string? reason, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        IncomingJoinRequestEventArgs? request;
        ApprovalRequest? approvalRequest;
        var normalizedReason = NormalizeIncomingRejectReason(reason);
        var isApprovalTimeout = string.Equals(normalizedReason, "approval_timeout", StringComparison.Ordinal);
        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            request = pendingJoinRequest;
            approvalRequest = pendingApprovalRequest;
            pendingJoinRequest = null;
            pendingApprovalRequest = null;
            if (request is null)
            {
                return;
            }

            LogApprovalDenied(normalizedReason, approvalRequest);
            TransitionTo(TransportState.Failed, normalizedReason);
            SetState(
                SessionRuntimeState.Rejected,
                isApprovalTimeout
                    ? UserErrorMapper.HelperApprovalTimeout()
                    : "Permission was declined.");
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await request.RejectWithReasonAsync(normalizedReason, uiCt).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }
    }

    public Task<ChatMessageRecord?> TrySendChatTextAsync(string text, CancellationToken uiCt)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ChatSend, "chat_send"),
            _ => TrySendChatTextCoreAsync(text, uiCt),
            deniedValue: null,
            uiCt);
    }

    internal Task<ChatMessageRecord?> TrySendChatTextCoreAsync(string text, CancellationToken uiCt)
    {
        return chatService.TrySendTextAsync(text, uiCt);
    }

    public Task<FileTransferTransferSnapshot?> StartSendAsync(
        FileTransferSendDescriptor descriptor,
        FileTransferReadStreamFactory openReadStreamAsync,
        CancellationToken uiCt = default)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.FileTransferStartSend, "file_transfer_send"),
            ct => fileTransferHost.StartSendAsync(descriptor, openReadStreamAsync, ct),
            deniedValue: null,
            uiCt);
    }

    internal Task<FileTransferTransferSnapshot?> StartSendCoreAsync(
        FileTransferSendDescriptor descriptor,
        FileTransferReadStreamFactory openReadStreamAsync,
        CancellationToken uiCt)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(openReadStreamAsync);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (transport is not IFileTransferSignalingTransport)
        {
            return Task.FromResult<FileTransferTransferSnapshot?>(null);
        }

        LocalOperationalLog.Info(
            "Session",
            $"event=file_transfer_send_requested; role={role}; session_id={sessionSecurityState.SessionId?.Value ?? "(none)"}; file_name_len={descriptor.FileName?.Length ?? 0}; file_size_bytes={descriptor.FileSizeBytes}; transfer_id={descriptor.TransferId ?? "(auto)"}");
        return fileTransferService.TryStartSendAsync(descriptor, openReadStreamAsync, uiCt);
    }

    public Task<FileTransferTransferSnapshot?> AcceptIncomingAsync(string transferId, CancellationToken uiCt = default)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.FileTransferAcceptIncoming, "file_transfer_accept"),
            ct => fileTransferHost.AcceptIncomingAsync(transferId, ct),
            deniedValue: null,
            uiCt);
    }

    internal Task<FileTransferTransferSnapshot?> AcceptIncomingCoreAsync(string transferId, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LocalOperationalLog.Info(
            "Session",
            $"event=file_transfer_accept_requested; role={role}; session_id={sessionSecurityState.SessionId?.Value ?? "(none)"}; transfer_id={transferId}");
        return fileTransferService.AcceptIncomingTransferAsync(transferId, fileTransferHost.OpenInboundWriteStreamAsync, uiCt);
    }

    public Task<FileTransferTransferSnapshot?> DeclineIncomingAsync(
        string transferId,
        string? reason = null,
        CancellationToken uiCt = default)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.FileTransferDeclineIncoming, "file_transfer_decline"),
            ct => fileTransferHost.DeclineIncomingAsync(transferId, reason, ct),
            deniedValue: null,
            uiCt);
    }

    internal Task<FileTransferTransferSnapshot?> DeclineIncomingCoreAsync(
        string transferId,
        string? reason,
        CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LocalOperationalLog.Info(
            "Session",
            $"event=file_transfer_decline_requested; role={role}; session_id={sessionSecurityState.SessionId?.Value ?? "(none)"}; transfer_id={transferId}; reason={reason ?? "(none)"}");
        return fileTransferService.DeclineIncomingTransferAsync(transferId, reason, uiCt);
    }

    public Task<FileTransferTransferSnapshot?> CancelTransferAsync(
        string transferId,
        string? reason = null,
        CancellationToken uiCt = default)
    {
        if (TryAuthorizeExistingFileTransferControl(transferId, "file_transfer_cancel"))
        {
            return fileTransferHost.CancelTransferAsync(transferId, reason, uiCt);
        }

        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.FileTransferCancel, "file_transfer_cancel"),
            ct => fileTransferHost.CancelTransferAsync(transferId, reason, ct),
            deniedValue: null,
            uiCt);
    }

    internal Task<FileTransferTransferSnapshot?> CancelTransferCoreAsync(
        string transferId,
        string? reason,
        CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LocalOperationalLog.Info(
            "Session",
            $"event=file_transfer_cancel_requested; role={role}; session_id={sessionSecurityState.SessionId?.Value ?? "(none)"}; transfer_id={transferId}; reason={reason ?? "(none)"}");
        return fileTransferService.CancelTransferAsync(transferId, reason, uiCt);
    }

    public Task<FileTransferTransferSnapshot?> PauseTransferAsync(
        string transferId,
        string? reason = null,
        CancellationToken uiCt = default)
    {
        if (TryAuthorizeExistingFileTransferControl(transferId, "file_transfer_pause"))
        {
            return fileTransferHost.PauseTransferAsync(transferId, reason, uiCt);
        }

        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.FileTransferPause, "file_transfer_pause"),
            ct => fileTransferHost.PauseTransferAsync(transferId, reason, ct),
            deniedValue: null,
            uiCt);
    }

    internal async Task<FileTransferTransferSnapshot?> PauseTransferCoreAsync(
        string transferId,
        string? reason,
        CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LogRuntimeFileTransferPauseResume("file_transfer_pause_requested", transferId, reason);
        var result = await fileTransferService.PauseTransferAsync(transferId, reason, uiCt).ConfigureAwait(false);
        if (result is null)
        {
            LogRuntimeFileTransferPauseResume("file_transfer_pause_ignored", transferId, reason, "not_active_or_not_eligible");
        }

        return result;
    }

    public Task<FileTransferTransferSnapshot?> ResumeTransferAsync(
        string transferId,
        string? reason = null,
        CancellationToken uiCt = default)
    {
        if (TryAuthorizeExistingFileTransferControl(transferId, "file_transfer_resume"))
        {
            return fileTransferHost.ResumeTransferAsync(transferId, reason, uiCt);
        }

        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.FileTransferResume, "file_transfer_resume"),
            ct => fileTransferHost.ResumeTransferAsync(transferId, reason, ct),
            deniedValue: null,
            uiCt);
    }

    internal async Task<FileTransferTransferSnapshot?> ResumeTransferCoreAsync(
        string transferId,
        string? reason,
        CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LogRuntimeFileTransferPauseResume("file_transfer_resume_requested", transferId, reason);
        var result = await fileTransferService.ResumeTransferAsync(transferId, reason, uiCt).ConfigureAwait(false);
        if (result is null)
        {
            LogRuntimeFileTransferPauseResume("file_transfer_resume_ignored", transferId, reason, "not_active_or_not_eligible");
        }

        return result;
    }

    private void LogRuntimeFileTransferPauseResume(
        string eventName,
        string transferId,
        string? reason,
        string? ignoredReason = null)
    {
        var snapshot = fileTransferService.Snapshot;
        var inbound = snapshot.Inbound;
        var outbound = snapshot.Outbound;
        var payload =
            $"event={eventName}; role={role}; session_id={sessionSecurityState.SessionId?.Value ?? "(none)"}; " +
            $"transfer_id={transferId}; reason={reason ?? "(none)"}; runtime_state={State}; " +
            $"inbound_state={inbound?.State.ToString() ?? "(none)"}; inbound_paused={(inbound?.IsPaused == true ? 1 : 0)}; " +
            $"outbound_state={outbound?.State.ToString() ?? "(none)"}; outbound_paused={(outbound?.IsPaused == true ? 1 : 0)}";
        if (!string.IsNullOrWhiteSpace(ignoredReason))
        {
            payload += $"; ignored_reason={ignoredReason}";
        }

        LocalOperationalLog.Info("Session", payload);
    }

    public Task SendChatAsync(ReadOnlyMemory<byte> payload, CancellationToken uiCt)
    {
        return privilegedCommandExecutor.ExecuteRequiredAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ChatSend, "chat_send"),
            "Chat capability is not authorized for the current session.",
            ct => SendChatCoreAsync(payload, ct),
            uiCt);
    }

    internal Task SendChatCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var currentTransport = transport ?? throw new InvalidOperationException("No active session.");
        return currentTransport.SendChatMessageAsync(payload, uiCt);
    }

    internal Task SendScreenSharePayloadCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ScreenShareDispatch, "screen_share_stream"),
            token => SendScreenSharePayloadAuthorizedCoreAsync(payload, token),
            ct);
    }

    internal Task SendScreenSharePayloadCoreAsync(
        ReadOnlyMemory<byte> payload,
        string? recoverySendRole,
        long recoveryBurstToken,
        CancellationToken ct)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ScreenShareDispatch, "screen_share_stream"),
            token => SendScreenSharePayloadAuthorizedCoreAsync(payload, recoverySendRole, recoveryBurstToken, token),
            ct);
    }

    internal Task SendScreenShareVideoStreamConfigCoreAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ScreenShareDispatch, "screen_share_stream_config"),
            token => SendScreenShareVideoStreamConfigAuthorizedCoreAsync(message, token),
            ct);
    }

    internal Task SendScreenShareCursorStateCoreAsync(string sessionId, ScreenShareCursorStateV1 message, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(message);
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ScreenShareDispatch, "screen_share_cursor_state"),
            token => SendScreenShareCursorStateAuthorizedCoreAsync(sessionId, message, token),
            ct);
    }

    private async Task SendScreenSharePayloadAuthorizedCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await SendScreenSharePayloadAuthorizedCoreAsync(
                payload,
                recoverySendRole: null,
                recoveryBurstToken: 0,
                ct)
            .ConfigureAwait(false);
    }

    private async Task SendScreenSharePayloadAuthorizedCoreAsync(
        ReadOnlyMemory<byte> payload,
        string? recoverySendRole,
        long recoveryBurstToken,
        CancellationToken ct)
    {
        if (!TryValidateScreenSharePayload(payload.Span, "screen_share_stream"))
        {
            return;
        }

        if (!FeatureFlags.EnableScreenShareTransport)
        {
            return;
        }

        if (transport is not IScreenShareSignalingTransport screenShareTransport)
        {
            return;
        }

        if (transport is NknSignalingTransport nknTransport)
        {
            await nknTransport.SendScreenSharePayloadAsync(payload, recoverySendRole, recoveryBurstToken, ct).ConfigureAwait(false);
            return;
        }

        await screenShareTransport.SendScreenSharePayloadAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task SendScreenShareVideoStreamConfigAuthorizedCoreAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
    {
        if (!TryValidateScreenShareSession(message.SessionId, "screen_share_stream_config", "stream_config"))
        {
            return;
        }

        if (!FeatureFlags.EnableScreenShareTransport)
        {
            return;
        }

        if (transport is not IScreenShareSignalingTransport screenShareTransport)
        {
            return;
        }

        await screenShareTransport.SendScreenShareVideoStreamConfigAsync(message, ct).ConfigureAwait(false);
    }

    private async Task SendScreenShareCursorStateAuthorizedCoreAsync(string sessionId, ScreenShareCursorStateV1 message, CancellationToken ct)
    {
        if (!TryValidateScreenShareSession(sessionId, "screen_share_cursor_state", "cursor_state") ||
            !TryValidateScreenShareSession(message.SessionId, "screen_share_cursor_state", "cursor_state"))
        {
            return;
        }

        if (!FeatureFlags.EnableScreenShareTransport ||
            !ShouldUsePassiveScreenShareCursorOverlayForTransport() ||
            transport is not IScreenShareSignalingTransport screenShareTransport)
        {
            return;
        }

        await screenShareTransport.SendScreenShareCursorStateAsync(message, ct).ConfigureAwait(false);
    }

    private Task StopTransportScreenShareAsync(bool notifyRemoteStop, string reason, CancellationToken ct)
    {
        return transportScreenShareCoordinator.StopAsync(notifyRemoteStop, reason, ct);
    }

    internal async Task StartTransportScreenShareAsync(CancellationToken ct = default)
    {
        var transportSessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value ?? sessionId;
        if (disposed ||
            role != SessionRuntimeRole.Helpee ||
            state != SessionRuntimeState.Connected ||
            !RequireCapability(SessionCapability.ScreenShare, "screen_share_start") ||
            !FeatureFlags.EnableScreenShareTransport ||
            !FeatureFlags.EnableScreenShareCapture ||
            string.IsNullOrWhiteSpace(transportSessionId))
        {
            return;
        }

        SyncTransportScreenShareCursorCaptureForRemoteControl("screen_share_start");
        await transportScreenShareCoordinator.StartAsync(transportSessionId, sessionCts?.Token ?? ct).ConfigureAwait(false);
        SyncFileTransferFlowControlMode();
    }

    internal async Task StopTransportScreenShareAsync(string reason, CancellationToken ct = default)
    {
        await transportScreenShareCoordinator.StopAsync(sendStopMessage: true, reason, ct).ConfigureAwait(false);
        SyncFileTransferFlowControlMode();
    }

    private static string SanitizeStatusForLog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(none)";
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }

    private static bool ShouldPreserveMappedFailureStatusText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return !string.Equals(text, "Connecting…", StringComparison.Ordinal) &&
               !string.Equals(text, "Connected", StringComparison.Ordinal) &&
               !string.Equals(text, "Waiting for helper…", StringComparison.Ordinal) &&
               !string.Equals(text, "Helper on this PC wants to connect. Click Allow.", StringComparison.Ordinal);
    }

    private readonly record struct RemoteControlInjectionWorkItem(
        ControlInputMessageV1? Message,
        ControlStateSnapshotV1? Snapshot,
        string? PeerId,
        long StopEpochSnapshot);

    private sealed class PendingRemoteControlConsentToken
    {
        public PendingRemoteControlConsentToken(
            string requestId,
            string controllerPeerId,
            byte[] tokenHash,
            DateTimeOffset expiresAtUtc)
        {
            RequestId = requestId;
            ControllerPeerId = controllerPeerId;
            TokenHash = tokenHash;
            ExpiresAtUtc = expiresAtUtc;
            IsUsed = false;
        }

        public string RequestId { get; }
        public string ControllerPeerId { get; }
        public byte[] TokenHash { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public bool IsUsed { get; set; }
    }
}

