using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

#pragma warning disable CS0067
public sealed partial class NknSignalingTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, ILocalPeerAddressSignalingTransport, IHelpRequestSignalingTransport, ISessionSecuritySignalingTransport, ISessionLivenessSignalingTransport, ISessionRecoveryStateContract, ITransportAccelerationStatus, ITransportAccelerationControl, IRemoteControlCapabilityProvider, IRemoteControlSignalingTransport, IScreenShareSignalingTransport, IScreenShareCursorOverlayCapabilityProvider, IScreenShareTransportBackpressureProbe, IScreenShareTransportPolicyController, IFileTransferSignalingTransport, IFileTransferChunkBudgetProvider, IFileTransferProtocolCapabilities, IFileTransferRouteStatus, IFileTransferTransportProfileProvider, IFileTransferV6TransportEpochObserver, IFileTransferReceiveRecoveryController, IFileTransferRegularV4ControlFeedbackPressureObserver, IFileTransferRouteCompletionObserver, IAuthoritativeConnectedAddressSource
{
    private readonly record struct FileTransferV6TransportEpochKey(
        string SessionId,
        string TransferId,
        FileTransferDirection Direction,
        long TransportEpoch);

    internal readonly record struct FileTransferV6TransportEpochDiagnostics(
        long StartedCount,
        long NormalToTunaActivationStartedCount,
        long RecoveredCount,
        long NormalToTunaActivationRecoveredCount,
        long WaitingCount,
        long TerminalCount,
        long UnresolvedCount);

    private sealed record FileTransferV6PendingHandoffIntent(
        string SessionId,
        string Reason,
        FileTransferTransportHandoffKind HandoffKind,
        FileTransferTransportKind TargetTransport,
        DateTimeOffset RecordedUtc);

    private readonly record struct FileTransferRouteHint(
        FileTransferRoute Route,
        string Token,
        int ProtocolVersion,
        string Source,
        DateTimeOffset RecordedUtc);

    private readonly record struct FileTransferPostTunaFallbackRepairProofHint(
        string SessionId,
        string TransferId,
        string ProofKind,
        string Direction,
        DateTimeOffset ObservedUtc);

    private sealed class RecoveryBurstLease
    {
        public string SessionId { get; init; } = string.Empty;

        public long StreamEpoch { get; init; }

        public long BurstToken { get; init; }

        public long OwnerFrameId { get; init; }
    }

    private static readonly TimeSpan DisposeDisconnectTimeout = TimeSpan.FromSeconds(5);
    private const int EnvelopeVersion = 1;
    private static readonly TimeSpan AckWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingJoinTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ControlInputReceiveLogWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ExpectedControlReplayDuplicateSummaryWindow = TimeSpan.FromSeconds(10);
    private const int ControlInputReceiveLogBurst = 5;
    // Transport-local abuse bounds. Hard payload-size ceilings still also exist
    // below this layer in the bridge/session envelope/screen-share codecs.
    private const int HighPriorityControlLaneCapacity = 256;
    private const int LowPriorityControlLaneCapacity = 256;
    private static readonly TimeSpan[] AckRetryDelays =
    {
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromMilliseconds(2700),
    };
    private static readonly TimeSpan[] ScreenShareStopRetryDelays =
    {
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(220),
    };
    private static readonly TimeSpan ScreenShareOutboundGateWaitBudget = TimeSpan.FromMilliseconds(25);
    private const int FileTransferMaxBridgePayloadBytes = 64 * 1024;
    internal const int FileTransferDataSessionMaxQueuedFrames = 512;
    internal const long FileTransferDataSessionMaxQueuedBytes = 32L * 1024L * 1024L;
    private const int ScreenShareLaneMaxMessages = 32;
    private const int ScreenShareLaneMaxBytes = 768 * 1024;
    private const int ScreenShareLaneCongestionDepthThreshold = 12;
    private const int ScreenShareLaneCongestionBytesThreshold = 256 * 1024;
    private const int ScreenShareLaneSevereDepthThreshold = 20;
    private const long ScreenShareControlBootstrapMaxFrameId = 7;
    private static readonly TimeSpan ScreenShareControlBootstrapKeyframeRetryDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan ScreenShareControlBootstrapFollowerRetryDelay = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan ScreenShareLaneRecentDropWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FileTransferFallbackUnprovenProbeDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FileTransferPostTunaFallbackRepairProofFreshness = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FileTransferCancelEchoMinInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan FileTransferControlReceiveStallRecoveryBroadcastCooldown = TimeSpan.FromSeconds(30);

    private readonly NknTransportOptions options;
    private readonly NknIdentity identity;
    private readonly INknClient client;
    private readonly IInviteTokenValidator inviteTokenValidator;
    private readonly IInviteValidationThrottle inviteValidationThrottle;
    private readonly ISessionHandshakeReplayCache handshakeReplayCache;
    private readonly HelpRequestAdmissionGuard helpRequestAdmissionGuard = new();
    private readonly LruMessageIdCache seenMessageIds = new(500);
    private readonly ConcurrentDictionary<string, PendingAckWait> pendingAcks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim outboundSendGate = new(1, 1);
    private readonly SemaphoreSlim screenShareMediaSendGate = new(1, 1);
    private string? outboundSendGateOwnerForDiagnostics;
    private readonly object controlOutboundQueueGate = new();
    private readonly object screenShareOutboundQueueGate = new();
    private readonly object screenShareControlFallbackGate = new();
    private readonly object controlInputReceiveLogGate = new();
    private readonly object hostReadyGate = new();
    private readonly object inboundFileTransferDispatchGate = new();
    private readonly LinkedList<QueuedControlEnvelope> highPriorityControlOutboundQueue = new();
    private readonly LinkedList<QueuedControlEnvelope> lowPriorityControlOutboundQueue = new();
    private readonly LinkedList<QueuedScreenShareEnvelope> screenShareOutboundQueue = new();
    private readonly Queue<(long UtcTicks, int DroppedFrames)> screenShareLaneRecentDropWindow = new();
    private readonly object gate = new();
    private readonly object controlSecureStateGate = new();
    private readonly object expectedControlReplayDuplicateLogGate = new();
    private readonly ScreenShareVideoFrameReassembler secureScreenShareFrameReassembler = new();
    private readonly SessionReplayWindow inboundChatReplayWindow = new();
    private readonly SessionReplayWindow inboundControlReplayWindow = new();
    private readonly SessionReplayWindow inboundLifecycleReplayWindow = new();
    private readonly SessionReplayWindow inboundScreenShareReplayWindow = new(
        windowSize: ScreenShareInboundReplayWindowSize,
        maxForwardAdvance: ScreenShareInboundReplayMaxForwardAdvance);
    private readonly SessionReplayWindow inboundFileTransferReplayWindow = new(
        windowSize: FileTransferInboundReplayWindowSize,
        maxForwardAdvance: FileTransferInboundReplayMaxForwardAdvance);
    private readonly Dictionary<string, FileTransferTransportState> fileTransferStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExpectedControlReplayDuplicateSuppressionState> expectedControlReplayDuplicateSuppressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileTransferTerminalTombstone> fileTransferTerminalTombstones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> fileTransferCancelEchoLastSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransportFileTransferDataSession> fileTransferDataSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileTransferRouteHint> fileTransferRouteHints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileTransferPostTunaFallbackRepairProofHint> fileTransferPostTunaFallbackRepairProofHints = new(StringComparer.Ordinal);
    private readonly HashSet<string> fileTransferDataSessionRemoteOpenSuppressed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileTransferV6PendingHandoffIntent> pendingFileTransferV6HandoffsBySession = new(StringComparer.Ordinal);
    private readonly object fileTransferFallbackProofGate = new();
    private readonly object fileTransferV6TransportEpochGate = new();
    private readonly Dictionary<FileTransferV6TransportEpochKey, FileTransferV6TransportEpochSnapshot> unresolvedFileTransferV6TransportEpochs = new();
    private long observedFileTransferV6TransportEpochStartedCount;
    private long observedFileTransferV6NormalToTunaActivationStartedCount;
    private long observedFileTransferV6TransportEpochRecoveredCount;
    private long observedFileTransferV6NormalToTunaActivationRecoveredCount;
    private long observedFileTransferV6TransportEpochWaitingCount;
    private long observedFileTransferV6TransportEpochTerminalCount;
    private FileTransferV6TransportEpochSnapshot? lastRecoveredFileTransferV6RegularNknEpoch;
    private long fileTransferControlReceiveStallRecoveryBroadcastLastTick;
    private long bridgeReceiveStallLivenessProofSequence;
    private readonly SortedDictionary<long, InboundFileTransferDispatchWork> pendingInboundFileTransferControlDispatch = new();
    private readonly NknLifecycleChannel lifecycleChannel;
    private readonly NknSecureControlChannel controlChannel;
    private readonly NknScreenShareChannel screenShareChannel;
    private readonly NknFileTransferChannel fileTransferChannel;
    private readonly NknEnvelopeRouter envelopeRouter;
    private readonly ControlOutboundQueue controlOutboundQueue;
    private readonly NknTunaAccelerationOptions tunaAccelerationOptions;
    private readonly INknAccelerationLane? accelerationLane;
    private const bool LocalRemoteControlSupported = true;
    private const bool LocalScreenShareCursorOverlaySupported = true;
    private const int ScreenShareInboundReplayWindowSize = 4096;
    private const long ScreenShareInboundReplayMaxForwardAdvance = 32768;
    private const int FileTransferInboundReplayWindowSize = 32768;
    private const long FileTransferInboundReplayMaxForwardAdvance = 131072;

    public bool SupportsFileTransferV6Streaming => true;

    public FileTransferTransportProfileKind FileTransferTransportProfileKind => FileTransferTransportProfileKind.ConservativeNknStartup;

    private sealed class ExpectedControlReplayDuplicateSuppressionState
    {
        public DateTimeOffset WindowStartedUtc { get; set; }

        public long SuppressedCount { get; set; }

        public long LastSequence { get; set; }

        public string LastMessageId { get; set; } = string.Empty;

        public string LastSource { get; set; } = string.Empty;
    }

    private string? currentEnvelopeCode;
    private string? remoteEndpoint;
    private string? remoteMediaEndpoint;
    private string? remoteBulkEndpoint;
    private string? lastPeerAddress;
    private SessionEcdhKeyPair? helpeeHostEcdhKeyPair;
    private SessionEcdhKeyPair? helperJoinEcdhKeyPair;
    private string? helperJoinRequestMessageId;
    private PendingJoinRequestState? pendingJoinRequest;
    private PendingInboundHandshakeState? pendingInboundHandshake;
    private PendingOutboundHandshakeState? pendingOutboundHandshake;
    private Timer? pendingInboundHandshakeTimeoutTimer;
    private long pendingInboundHandshakeTimeoutGeneration;
    private bool remoteSupportsRemoteControl;
    private bool remoteSupportsScreenShareCursorOverlay;
    private RemoteControlSessionState transportRemoteControlState = RemoteControlSessionState.Default;
    private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
    private bool fileTransferFallbackProofPending;
    private string fileTransferFallbackProofReason = "none";
    private string? fileTransferFallbackProofSessionId;
    private NknAccelerationLaneKind fileTransferFallbackProofLanes;
    private long fileTransferFallbackProofGeneration;
    private bool fileTransferFallbackProofProbeScheduled;
    private bool fileTransferFallbackBulkProofObserved;
    private bool fileTransferFallbackControlProofObserved;
    private int bridgeReceiveStallRecoveryActive;
    private SessionId? activeApprovedSessionId;
    private PeerAddress? activeApprovedHelperAddress;
    private LinkedListNode<QueuedControlEnvelope>? queuedLowPriorityMouseMoveNode;
    private LinkedListNode<QueuedControlEnvelope>? queuedLowPriorityScreenShareCursorNode;
    private bool controlOutboundDrainerActive;
    private bool screenShareOutboundDrainerActive;
    private int screenShareOutboundQueuedBytes;
    private int screenShareOutboundPeakDepthSeen;
    private long screenShareOutboundGeneration;
    private bool screenShareLaneCongestionActive;
    private bool screenShareLaneSevereCongestionActive;
    private RecoveryBurstLease? screenShareRecoveryBurstLease;
    private long screenShareRecoveryControlBootstrapRetrySkippedDueToBurstResolvedCount;
    private long screenShareRecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount;
    private long lowLaneDroppedMoves;
    private long lowLaneEnqueuedMoves;
    private int lowLaneMaxDepthSeen;
    private long controlInputReceiveLogWindowStartTicks;
    private int controlInputReceiveLogCount;
    private int controlInputReceiveLogSuppressed;
    private long screenShareOutboundBusyDrops;
    private long screenSharePayloadBytesSent;
    private long screenShareMessagesSent;
    private long highPriorityControlQueueOverflowCount;
    private long highPriorityControlRejectedCount;
    private long highPriorityControlCoalescedCount;
    private long highPriorityControlDroppedForStopCount;
    private byte[]? controlSessionSharedKey;
    private byte[]? fileTransferSessionSharedKey;
    private long nextOutboundControlSecureSequence;
    private long nextOutboundChatSecureSequence;
    private long nextOutboundLifecycleSecureSequence;
    private long nextOutboundScreenShareSecureSequence;
    private long nextOutboundFileTransferSecureSequence;
    private long nextInboundFileTransferControlDispatchOrder;
    private long nextInboundFileTransferControlDispatchToProcess = 1;
    private long inboundFileTransferDispatchGeneration;
    private bool inboundFileTransferControlDispatchActive;
    private Task inboundFileTransferLifecycleDispatchTail = Task.CompletedTask;
    private TaskCompletionSource<bool> hostReadyTcs = CreateHostReadyTcs();
    private readonly IBridgeScreenShareQueueCapability? bridgeScreenShareQueueCapability;
    private long screenShareBridgePolicyGeneration;
    private long screenShareBridgePolicyNextGeneration;
    private bool screenShareBridgeCatchUpOnlyActive;
    private bool disposed;
    internal static TimeSpan? DisposeDisconnectTimeoutOverrideForTests;

    public NknSignalingTransport()
    {
        options = NknTransportOptions.Load();
        identity = NknIdentityStore.LoadOrCreate(options);
        client = new RealNknClientAdapter(identity, options);
        tunaAccelerationOptions = BindTunaDialerIdentity(NknTunaAccelerationOptions.Load(), identity, options);
        accelerationLane = CreateAccelerationLane(tunaAccelerationOptions);
        inviteTokenValidator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        inviteValidationThrottle = InviteTokenServiceFactory.CreateInviteValidationThrottle();
        handshakeReplayCache = new InMemorySessionHandshakeReplayCache();
        lifecycleChannel = new NknLifecycleChannel(this);
        controlChannel = new NknSecureControlChannel(this);
        screenShareChannel = new NknScreenShareChannel(this);
        fileTransferChannel = new NknFileTransferChannel(this);
        envelopeRouter = new NknEnvelopeRouter(lifecycleChannel, controlChannel, screenShareChannel, fileTransferChannel);
        controlOutboundQueue = new ControlOutboundQueue(this);
        secureScreenShareFrameReassembler.FrameReady += OnSecureScreenShareFrameReady;
        secureScreenShareFrameReassembler.KeyframeRequested += OnSecureScreenShareKeyframeRequested;
        bridgeScreenShareQueueCapability = client as IBridgeScreenShareQueueCapability;
        SubscribeClientEvents();

        NknRuntimeDiagnostics.SetIdentity(
            address: identity.Address,
            identifier: identity.Identifier,
            keyPath: options.KeyPath,
            seedRpc: options.SeedRpc);

        Log($"Initialized | {SensitiveDataRedactor.FormatStructuredFields(" | ", ("address", identity.Address), ("identifier", identity.Identifier))}");
    }

    internal static NknSignalingTransport CreateWithTunaAcceleration(
        NknTunaAccelerationOptions tunaAccelerationOptions,
        INknTunaListenerSidecarSupervisor? listenerSupervisor)
    {
        var options = NknTransportOptions.Load();
        var identity = NknIdentityStore.LoadOrCreate(options);
        var client = new RealNknClientAdapter(identity, options);
        var effectiveOptions = BindTunaDialerIdentity(tunaAccelerationOptions, identity, options);
        var accelerationLane = CreateAccelerationLane(effectiveOptions, listenerSupervisor);
        var transport = new NknSignalingTransport(client, options, identity, effectiveOptions, accelerationLane);
        NknRuntimeDiagnostics.SetIdentity(
            address: identity.Address,
            identifier: identity.Identifier,
            keyPath: options.KeyPath,
            seedRpc: options.SeedRpc);
        Log($"Initialized | {SensitiveDataRedactor.FormatStructuredFields(" | ", ("address", identity.Address), ("identifier", identity.Identifier))}");
        return transport;
    }

    private static NknTunaAccelerationOptions BindTunaDialerIdentity(
        NknTunaAccelerationOptions? tunaOptions,
        NknIdentity identity,
        NknTransportOptions transportOptions)
    {
        var effectiveOptions = tunaOptions ?? NknTunaAccelerationOptions.Disabled;
        return effectiveOptions.Enabled
            ? effectiveOptions.WithDialerIdentity(identity.Identifier, TryReadDialerSeedBase64(transportOptions.KeyPath))
            : effectiveOptions;
    }

    private static string? TryReadDialerSeedBase64(string keyPath)
    {
        try
        {
            return NknIdentityStore.ReadSeedBase64ForConnect(keyPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_dialer_identity_seed_unavailable; error={ex.GetType().Name}");
            return null;
        }
    }

    internal NknSignalingTransport(INknClient client, NknTransportOptions options, NknIdentity identity)
        : this(client, options, identity, NknTunaAccelerationOptions.Disabled, accelerationLane: null)
    {
    }

    internal NknSignalingTransport(
        INknClient client,
        NknTransportOptions options,
        NknIdentity identity,
        NknTunaAccelerationOptions tunaAccelerationOptions,
        INknAccelerationLane? accelerationLane)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.tunaAccelerationOptions = tunaAccelerationOptions ?? NknTunaAccelerationOptions.Disabled;
        this.accelerationLane = accelerationLane;
        inviteTokenValidator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        inviteValidationThrottle = InviteTokenServiceFactory.CreateInviteValidationThrottle();
        handshakeReplayCache = new InMemorySessionHandshakeReplayCache();
        lifecycleChannel = new NknLifecycleChannel(this);
        controlChannel = new NknSecureControlChannel(this);
        screenShareChannel = new NknScreenShareChannel(this);
        fileTransferChannel = new NknFileTransferChannel(this);
        envelopeRouter = new NknEnvelopeRouter(lifecycleChannel, controlChannel, screenShareChannel, fileTransferChannel);
        controlOutboundQueue = new ControlOutboundQueue(this);
        secureScreenShareFrameReassembler.FrameReady += OnSecureScreenShareFrameReady;
        secureScreenShareFrameReassembler.KeyframeRequested += OnSecureScreenShareKeyframeRequested;
        bridgeScreenShareQueueCapability = client as IBridgeScreenShareQueueCapability;
        SubscribeClientEvents();
    }

    public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
    public event EventHandler<IncomingHelpRequestEventArgs>? IncomingHelpRequest;
    public event EventHandler<HelpRequestDecisionEventArgs>? HelpRequestDecisionReceived;

    public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;

    public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;

    public event EventHandler? Approved;

    public event EventHandler? Rejected;

    public event EventHandler? Disconnected;
    public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
    public event EventHandler<SessionLivenessProofEventArgs>? SessionLivenessProofReceived;
    public event EventHandler<TransportAccelerationStateChangedEventArgs>? TransportAccelerationStateChanged;

    public event EventHandler? RemoteSessionEnded;
    public event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;
    public event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;
    public event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;
    public event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;
    public event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
    public event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
    public event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;
    public event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
    public event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived;
    public event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived;
    public event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived;
    public event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived;
    public event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived;
    public event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived;
    public event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived;
    public event EventHandler<FileTransferPauseControlReceivedEventArgs>? FileTransferPauseControlReceived;
    public event EventHandler<FileTransferHeartbeatReceivedEventArgs>? FileTransferHeartbeatReceived;
    public event EventHandler<FileTransferTransportEpochReceivedEventArgs>? FileTransferTransportEpochReceived;
    public event EventHandler<FileTransferTransportProbeReceivedEventArgs>? FileTransferTransportProbeReceived;
    public event EventHandler<FileTransferRepairProofReceivedEventArgs>? FileTransferRepairProofReceived;

    internal event EventHandler<BridgeLifecycleEvent>? BridgeLifecycle;
    public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
    public event EventHandler? ScreenShareStopped;
    public event EventHandler<ScreenSharePressureStateReceivedEventArgs>? ScreenSharePressureStateReceived;
    public event EventHandler<ScreenShareRecoveryReceiptReceivedEventArgs>? ScreenShareRecoveryReceiptReceived;
    public event EventHandler<ScreenShareVideoStreamConfigReceivedEventArgs>? ScreenShareVideoStreamConfigReceived;
    public event EventHandler<ScreenShareVideoKeyframeRequestReceivedEventArgs>? ScreenShareVideoKeyframeRequestReceived;
    public event EventHandler<ScreenShareCursorStateReceivedEventArgs>? ScreenShareCursorStateReceived;

    public string LocalPeerAddress => string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address;
    bool IAuthoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress =>
        client is IAuthoritativeConnectedAddressSource authoritativeConnectedAddressSource &&
        authoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress;
    public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;
    public bool IsTransportAccelerationActive => IsAccelerationNegotiatedAndHealthy();
    public bool ShouldUseFileTransferV6ForAcceleration => ShouldUseFileTransferV6ForAccelerationCore();
    public bool IsFileTunaActiveForRouteSelection => IsFileTransferAccelerationNegotiatedAndHealthy();
    public bool IsPostTunaFileFallbackActiveForRouteSelection => IsFileTransferUsingRegularNknFallbackForCurrentSession();
    public bool IsDiagnosticRegularNknV6RouteEnabled => IsDiagnosticRegularNknV6RouteEnabledCore();
    public string TransportAccelerationStatusReason
    {
        get
        {
            lock (accelerationGate)
            {
                return transportAccelerationStatusReason;
            }
        }
    }

    private void RaiseSessionLivenessProof(
        string? sessionId,
        long generation,
        long sequence,
        string proofKind,
        string lane)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        var currentSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(currentSessionId) ||
            !string.Equals(normalizedSessionId, currentSessionId, StringComparison.Ordinal))
        {
            return;
        }

        var normalizedProofKind = string.IsNullOrWhiteSpace(proofKind) ? "unknown" : proofKind.Trim();
        var normalizedLane = string.IsNullOrWhiteSpace(lane) ? "unknown" : lane.Trim();
        var observedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=session_liveness_proof_received; transport=nkn; session_id={SanitizeLogToken(normalizedSessionId)}; generation={generation}; sequence={sequence}; proof_kind={normalizedProofKind}; lane={normalizedLane}; observed_utc_ms={observedUtcMs}");
        SessionLivenessProofReceived?.Invoke(
            this,
            new SessionLivenessProofEventArgs(
                normalizedSessionId,
                generation,
                sequence,
                observedUtcMs,
                normalizedProofKind,
                normalizedLane));
    }

    private static bool IsDiagnosticRegularNknV6RouteEnabledCore()
    {
        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(
            "NLINK_FILETRANSFER_DIAGNOSTIC_REGULAR_NKN_V6",
            category: "filetransfer_diagnostic");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiagnosticFileTunaV4RouteEnabledCore()
    {
        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(
            "NLINK_FILETRANSFER_DIAGNOSTIC_FILE_TUNA_V4",
            category: "filetransfer_diagnostic");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanSendSessionEnd => !disposed && !string.IsNullOrWhiteSpace(currentEnvelopeCode) && !string.IsNullOrWhiteSpace(remoteEndpoint);
    public bool CanSendPendingJoinCancel
    {
        get
        {
            lock (gate)
            {
                return !disposed &&
                       pendingOutboundHandshake?.HelpeeEcdhPublicKey is not null &&
                       helperJoinEcdhKeyPair is not null &&
                       !string.IsNullOrWhiteSpace(helperJoinRequestMessageId) &&
                       !string.IsNullOrWhiteSpace(currentEnvelopeCode) &&
                       !string.IsNullOrWhiteSpace(remoteEndpoint);
            }
        }
    }
    public bool LocalSupportsRemoteControl => LocalRemoteControlSupported;
    public bool RemoteSupportsRemoteControl => remoteSupportsRemoteControl;
    public bool SessionSupportsRemoteControl => LocalSupportsRemoteControl && RemoteSupportsRemoteControl;
    public bool LocalSupportsScreenShareCursorOverlay => LocalScreenShareCursorOverlaySupported;
    public bool RemoteSupportsScreenShareCursorOverlay => remoteSupportsScreenShareCursorOverlay;
    public bool SessionSupportsScreenShareCursorOverlay => LocalSupportsScreenShareCursorOverlay && RemoteSupportsScreenShareCursorOverlay;
    internal long LowLaneDroppedMoves => Interlocked.Read(ref lowLaneDroppedMoves);
    internal long LowLaneEnqueuedMoves => Interlocked.Read(ref lowLaneEnqueuedMoves);
    internal int LowLaneMaxDepthSeen => Volatile.Read(ref lowLaneMaxDepthSeen);
    internal long ScreenShareOutboundBusyDrops => Interlocked.Read(ref screenShareOutboundBusyDrops);
    internal long ScreenSharePayloadBytesSent => Interlocked.Read(ref screenSharePayloadBytesSent);
    internal long ScreenShareMessagesSent => Interlocked.Read(ref screenShareMessagesSent);
    internal long NextOutboundFileTransferSecureSequence => Interlocked.Read(ref nextOutboundFileTransferSecureSequence);
    internal long HighPriorityControlQueueOverflowCount => Interlocked.Read(ref highPriorityControlQueueOverflowCount);
    internal long HighPriorityControlRejectedCount => Interlocked.Read(ref highPriorityControlRejectedCount);
    internal long HighPriorityControlCoalescedCount => Interlocked.Read(ref highPriorityControlCoalescedCount);
    internal long HighPriorityControlDroppedForStopCount => Interlocked.Read(ref highPriorityControlDroppedForStopCount);
    public bool IsScreenShareTransportCongested
    {
        get
        {
            lock (screenShareOutboundQueueGate)
            {
                return ComputeEffectiveScreenShareLaneStateUnsafe().IsCongested;
            }
        }
    }

    public bool IsScreenShareTransportSeverelyCongested
    {
        get
        {
            lock (screenShareOutboundQueueGate)
            {
                return ComputeEffectiveScreenShareLaneStateUnsafe().IsSevere;
            }
        }
    }

    public int ScreenShareTransportQueueDepth
    {
        get
        {
            lock (screenShareOutboundQueueGate)
            {
                return ComputeEffectiveScreenShareLaneStateUnsafe().QueueDepth;
            }
        }
    }

    public int ScreenShareTransportQueuedBytes
    {
        get
        {
            lock (screenShareOutboundQueueGate)
            {
                return ComputeEffectiveScreenShareLaneStateUnsafe().QueuedBytes;
            }
        }
    }

    public long ScreenShareTransportOldestQueuedAgeMs
    {
        get
        {
            lock (screenShareOutboundQueueGate)
            {
                return ComputeEffectiveScreenShareLaneStateUnsafe().OldestQueuedAgeMs;
            }
        }
    }

    public long ScreenShareTransportRecentDropCount
    {
        get
        {
            lock (screenShareOutboundQueueGate)
            {
                return ComputeEffectiveScreenShareLaneStateUnsafe().RecentDropCount;
            }
        }
    }

    public long ScreenShareTransportRecentHealthIssueCount
    {
        get
        {
            lock (screenShareOutboundQueueGate)
            {
                return ComputeEffectiveScreenShareLaneStateUnsafe().RecentHealthIssueCount;
            }
        }
    }

    public bool IsScreenShareTransportHealthSeverelyDegraded
    {
        get
        {
            lock (screenShareOutboundQueueGate)
            {
                return ComputeEffectiveScreenShareLaneStateUnsafe().IsHealthSevere;
            }
        }
    }

    public Task WaitUntilHostReadyAsync(CancellationToken ct)
    {
        Task readyTask;
        lock (hostReadyGate)
        {
            readyTask = hostReadyTcs.Task;
        }

        return readyTask.WaitAsync(ct);
    }

    private enum ControlOutboundLane
    {
        High = 0,
        Low = 1,
    }

    public async Task<bool> TryPingBridgeHealthAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (client is not RealNknClientAdapter realClient)
        {
            return false;
        }

        try
        {
            await realClient.PingBridgeAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"TryPingBridgeHealthAsync failed ({ex.GetType().Name})");
            return false;
        }
    }

    internal Task PrepareForReuseAsync()
    {
        ThrowIfDisposed();

        CancelPendingAcks();
        CancelPendingInboundHandshakeTimeout();
        ResetSessionTracking();
        seenMessageIds.Clear();
        currentEnvelopeCode = null;
        lastPeerAddress = null;

        Log("Prepared for reuse");
        return Task.CompletedTask;
    }

    private static TaskCompletionSource<bool> CreateHostReadyTcs()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ResetHostReady()
    {
        lock (hostReadyGate)
        {
            hostReadyTcs = CreateHostReadyTcs();
        }
    }

    private void TrySetHostReady()
    {
        lock (hostReadyGate)
        {
            hostReadyTcs.TrySetResult(true);
        }
    }

    private void TryCancelHostReady()
    {
        lock (hostReadyGate)
        {
            hostReadyTcs.TrySetCanceled();
        }
    }

    private void TryFailHostReady(Exception ex)
    {
        lock (hostReadyGate)
        {
            hostReadyTcs.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        TryCancelHostReady();

        // Avoid treating normal cleanup as a disconnection.
        client.MessageReceived -= OnClientMessageReceived;
        client.Disconnected -= OnClientDisconnected;
        if (client is RealNknClientAdapter realClient)
        {
            realClient.BridgeLifecycle -= OnBridgeLifecycle;
        }
        if (accelerationLane is not null)
        {
            accelerationLane.MessageReceived -= OnAccelerationMessageReceived;
            accelerationLane.StateChanged -= OnAccelerationStateChanged;
        }

        CompleteTunaFallbackProof("dispose");
        try
        {
            CleanupAsync()
                .WaitAsync(DisposeDisconnectTimeoutOverrideForTests ?? DisposeDisconnectTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            Log(
                $"event=transport_dispose_disconnect_timeout; timeout_ms={(DisposeDisconnectTimeoutOverrideForTests ?? DisposeDisconnectTimeout).TotalMilliseconds:F0}; forcing_client_dispose=1");
        }
        catch (Exception ex)
        {
            Log($"event=transport_dispose_disconnect_failed; ex={ex.GetType().Name}; forcing_client_dispose=1");
        }

        DisposeEphemeralKeyState();
        accelerationLane?.Dispose();
        client.Dispose();
        Log("Disposed");
    }

    public async Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await SendScreenSharePayloadAsync(payload, recoverySendRole: null, recoveryBurstToken: 0, ct).ConfigureAwait(false);
    }

    internal async Task SendScreenSharePayloadAsync(
        ReadOnlyMemory<byte> payload,
        string? recoverySendRole,
        long recoveryBurstToken,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryParseScreenSharePayload(payload.Span, out var messageType, out var messageSessionId))
        {
            LogScreenShareRejected("send", "payload_invalid", sessionId: null);
            return;
        }

        if (!TryValidateScreenShareSession(messageType, messageSessionId) ||
            !IsScreenShareAuthorizedForDispatch(messageType, messageSessionId))
        {
            return;
        }

        var isStopPayload = string.Equals(messageType, "stop", StringComparison.Ordinal);
        var destination = isStopPayload ? remoteEndpoint : remoteMediaEndpoint;
        if (string.IsNullOrWhiteSpace(destination))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_no_remote_endpoint");
            Log($"SendScreenSharePayloadAsync failed (payload_len={payload.Length}, reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_session_context_unavailable");
            Log($"SendScreenSharePayloadAsync failed (payload_len={payload.Length}, reason=no_session_context)");
            throw new InvalidOperationException("Session context is not known yet.");
        }

        if (isStopPayload)
        {
            ResetScreenShareControlFallbackState();
            var securePayload = CreateSecureScreenSharePayload(MsgType.ScreenShareStop, payload.ToArray());
            var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareStop, securePayload, replyTo: null);
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_stop_send_requested; session_id={messageSessionId}; payload_len={payload.Length}");
            await SendScreenShareStopEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            SendScreenShareStopEnvelopeRetriesFireAndForget(destination, envelope, messageSessionId);
        }
        else
        {
            if (!screenShareBridgeCatchUpOnlyActive)
            {
                await EnsureScreenShareBridgeSessionStartedAsync(ct).ConfigureAwait(false);
            }

            var securePayload = CreateSecureScreenSharePayload(MsgType.ScreenShareFrame, payload.ToArray());
            var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareFrame, securePayload, replyTo: null);
            var transportPayload = EnvelopeCodec.Serialize(envelope);
            var redundancyMetadata = TryCreateQueuedScreenShareEnvelopeMetadata(
                transportPayload,
                recoverySendRole,
                recoveryBurstToken);

            if (!string.IsNullOrWhiteSpace(remoteEndpoint) &&
                TrySelectScreenShareControlFallback(reasonMetadata: redundancyMetadata, out var fallbackReason, out var selectedRecoveryBurstToken))
            {
                var controlFallbackQueued = await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_control_fallback_{(controlFallbackQueued ? "queued" : "rejected")}; session_id={redundancyMetadata!.SessionId}; stream_epoch={redundancyMetadata.StreamEpoch}; frame_id={redundancyMetadata.FrameId}; is_keyframe={(redundancyMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; transport_payload_bytes={transportPayload.Length}");

                if (controlFallbackQueued &&
                    ShouldScheduleScreenShareControlBootstrapRetry(redundancyMetadata, fallbackReason, out var retryDelay))
                {
                    ScheduleScreenShareControlBootstrapRetry(
                        payload.ToArray(),
                        redundancyMetadata,
                        fallbackReason,
                        retryDelay,
                        selectedRecoveryBurstToken);
                }
            }

            await SendScreenShareMediaEnvelopeDirectAsync(destination, transportPayload, ct).ConfigureAwait(false);
        }

        Log($"SendScreenSharePayloadAsync sent screenshare payload (payload_len={payload.Length})");
    }

    public async Task SendScreenSharePressureStateAsync(ScreenSharePressureStateV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        var normalizedMessage = EnsureScreenSharePressureStateSessionId(message);
        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_pressure_state_no_remote_endpoint");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_pressure_state_session_context_unavailable");
            throw new InvalidOperationException("Session context is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ScreenSharePressureState,
            requestId: null,
            ScreenSharePressureStateCodec.Serialize(normalizedMessage));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenSharePressureState, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
            remoteEndpoint,
            envelope,
            ResolveControlOutboundLane(MsgType.ScreenSharePressureState),
            ct).ConfigureAwait(false);
    }

    public async Task SendScreenShareVideoStreamConfigAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        var normalizedMessage = EnsureScreenShareVideoStreamConfigSessionId(message);
        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_video_stream_config_no_remote_endpoint");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_video_stream_config_session_context_unavailable");
            throw new InvalidOperationException("Session context is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ScreenShareVideoStreamConfig,
            requestId: null,
            ScreenShareVideoPayloadCodec.SerializeStreamConfig(normalizedMessage));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareVideoStreamConfig, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
            remoteEndpoint,
            envelope,
            ResolveControlOutboundLane(MsgType.ScreenShareVideoStreamConfig),
            ct).ConfigureAwait(false);
    }

    public async Task SendScreenShareVideoKeyframeRequestAsync(ScreenShareVideoKeyframeRequestV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        var normalizedMessage = EnsureScreenShareVideoKeyframeRequestSessionId(message);
        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_video_keyframe_request_no_remote_endpoint");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_video_keyframe_request_session_context_unavailable");
            throw new InvalidOperationException("Session context is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ScreenShareVideoKeyframeRequest,
            requestId: null,
            ScreenShareVideoKeyframeRequestCodec.Serialize(normalizedMessage));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareVideoKeyframeRequest, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
            remoteEndpoint,
            envelope,
            ResolveControlOutboundLane(MsgType.ScreenShareVideoKeyframeRequest),
            ct).ConfigureAwait(false);
    }

    public async Task SendScreenShareCursorStateAsync(ScreenShareCursorStateV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        if (!SessionSupportsScreenShareCursorOverlay)
        {
            return;
        }

        var normalizedMessage = EnsureScreenShareCursorStateSessionId(message);
        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_cursor_state_no_remote_endpoint");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_cursor_state_session_context_unavailable");
            throw new InvalidOperationException("Session context is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ScreenShareCursorState,
            requestId: null,
            ScreenShareCursorStateCodec.Serialize(normalizedMessage));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareCursorState, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
            remoteEndpoint,
            envelope,
            ResolveControlOutboundLane(MsgType.ScreenShareCursorState),
            ct,
            isLowPriorityScreenShareCursorState: true).ConfigureAwait(false);
    }

    public async Task SendScreenShareRecoveryReceiptAsync(ScreenShareRecoveryReceiptV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        var normalizedMessage = EnsureScreenShareRecoveryReceiptSessionId(message);
        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_recovery_receipt_no_remote_endpoint");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_recovery_receipt_session_context_unavailable");
            throw new InvalidOperationException("Session context is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ScreenShareRecoveryReceipt,
            requestId: null,
            ScreenShareRecoveryReceiptCodec.Serialize(normalizedMessage));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareRecoveryReceipt, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
            remoteEndpoint,
            envelope,
            ResolveControlOutboundLane(MsgType.ScreenShareRecoveryReceipt),
            ct).ConfigureAwait(false);
    }

    public Task SetScreenShareTransportCatchUpOnlyAsync(bool active, CancellationToken ct)
    {
        return ApplyScreenShareBridgePolicyAsync(
            active ? BridgeScreenShareQueueMode.CatchUpOnly : BridgeScreenShareQueueMode.Normal,
            flushQueued: true,
            reason: active ? "sender_degraded_entered" : "sender_degraded_exited",
            ct);
    }

    public void FlushScreenShareTransportQueue(string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "transport_queue_flush" : reason.Trim();
        ResetScreenShareControlFallbackState();
        RequestBridgeScreenShareQueueFlush(normalizedReason);
    }

    private async Task SendScreenShareMediaEnvelopeDirectAsync(string destination, byte[] payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(payload);

        NknRuntimeDiagnostics.SetOutboundLaneQueueDepth("screenshare", 0, 0);
        if (!await screenShareMediaSendGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            NknRuntimeDiagnostics.IncrementOutboundLaneWaitCount("screenshare");
            await screenShareMediaSendGate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            NknRuntimeDiagnostics.SetOutboundLaneInFlight("screenshare", 1);
            if (await TrySendAcceleratedEnvelopeAsync(MsgType.ScreenShareFrame, NknBridgeChannel.Media, payload, ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref screenShareMessagesSent);
                Interlocked.Add(ref screenSharePayloadBytesSent, payload.Length);
                NknRuntimeDiagnostics.IncrementScreenShareMessagesSent();
                NknRuntimeDiagnostics.AddScreenSharePayloadBytesSent(payload.Length);
                NknRuntimeDiagnostics.AddOutboundLaneSent("screenshare", payload.Length);
                NknRuntimeDiagnostics.IncrementMediaPlaneFramesSent();
                return;
            }

            await client.SendMediaAsync(destination, payload, ct).ConfigureAwait(false);
            RecordTunaFallbackNknFrameSent(MsgType.ScreenShareFrame, NknBridgeChannel.Media, payload.Length);
            Interlocked.Increment(ref screenShareMessagesSent);
            Interlocked.Add(ref screenSharePayloadBytesSent, payload.Length);
            NknRuntimeDiagnostics.IncrementScreenShareMessagesSent();
            NknRuntimeDiagnostics.AddScreenSharePayloadBytesSent(payload.Length);
            NknRuntimeDiagnostics.AddOutboundLaneSent("screenshare", payload.Length);
            NknRuntimeDiagnostics.IncrementMediaPlaneFramesSent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            NknRuntimeDiagnostics.IncrementMediaPlaneSendFailures();
            throw;
        }
        finally
        {
            NknRuntimeDiagnostics.SetOutboundLaneInFlight("screenshare", 0);
            NknRuntimeDiagnostics.SetOutboundLaneQueueDepth("screenshare", 0, 0);
            screenShareMediaSendGate.Release();
        }
    }

    private Task QueueScreenShareEnvelopeAsync(
        string destination,
        byte[] payload,
        string? sessionId,
        QueuedScreenShareEnvelopeMetadata? metadata,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var shouldStartDrainer = false;
        var laneRejected = 0;
        var freshnessPrunedEnvelopes = 0;
        var freshnessPrunedFrames = 0;
        long? retainedStreamEpoch = null;
        long? retainedFrameId = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<TaskCompletionSource>? supersededCompletions = null;
        lock (screenShareOutboundQueueGate)
        {
            var generation = Volatile.Read(ref screenShareOutboundGeneration);
            screenShareOutboundQueue.AddLast(new QueuedScreenShareEnvelope(
                destination,
                payload,
                string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim(),
                generation,
                completion,
                metadata));
            screenShareOutboundQueuedBytes += payload.Length;

            if (metadata is not null &&
                (screenShareBridgeCatchUpOnlyActive ||
                 ComputeScreenShareLaneCongestionUnsafe() ||
                 ComputeScreenShareLaneSevereCongestionUnsafe()))
            {
                PruneQueuedScreenShareVideoUnsafe(
                    metadata,
                    out freshnessPrunedEnvelopes,
                    out freshnessPrunedFrames,
                    out retainedStreamEpoch,
                    out retainedFrameId,
                    ref supersededCompletions);
            }

            while (screenShareOutboundQueue.Count > ScreenShareLaneMaxMessages ||
                   screenShareOutboundQueuedBytes > ScreenShareLaneMaxBytes)
            {
                var dropped = screenShareOutboundQueue.First;
                if (dropped is null)
                {
                    break;
                }

                screenShareOutboundQueue.RemoveFirst();
                screenShareOutboundQueuedBytes = Math.Max(0, screenShareOutboundQueuedBytes - dropped.Value.Payload.Length);
                dropped.Value.Completion.TrySetException(new ScreenShareSendSupersededException("Queued media envelope dropped due to lane overflow."));
                laneRejected++;
            }

            if (laneRejected > 0)
            {
                RecordScreenShareLaneFrameDropsUnsafe(laneRejected);
            }

            if (screenShareOutboundQueue.Count > screenShareOutboundPeakDepthSeen)
            {
                screenShareOutboundPeakDepthSeen = screenShareOutboundQueue.Count;
            }

            NknRuntimeDiagnostics.SetOutboundLaneQueueDepth("screenshare", screenShareOutboundQueue.Count, screenShareOutboundPeakDepthSeen);
            UpdateScreenShareLaneCongestionStateUnsafe();
            if (!screenShareOutboundDrainerActive)
            {
                screenShareOutboundDrainerActive = true;
                shouldStartDrainer = true;
            }
        }

        if (supersededCompletions is not null)
        {
            foreach (var supersededCompletion in supersededCompletions)
            {
                supersededCompletion.TrySetException(new ScreenShareSendSupersededException("Queued media envelope was superseded by a newer video frame."));
            }
        }

        for (var i = 0; i < laneRejected; i++)
        {
            NknRuntimeDiagnostics.IncrementOutboundLaneRejected("screenshare");
            NknRuntimeDiagnostics.IncrementScreenShareLaneCongestionHit();
        }

        for (var i = 0; i < freshnessPrunedEnvelopes; i++)
        {
            NknRuntimeDiagnostics.IncrementOutboundLaneRejected("screenshare");
            NknRuntimeDiagnostics.IncrementScreenShareLaneCongestionHit();
        }

        if (laneRejected > 0)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_lane_drop; reason=lane_overflow_oldest_frame; dropped_count={laneRejected}; queued_bytes={Volatile.Read(ref screenShareOutboundQueuedBytes)}; queue_depth={ScreenShareTransportQueueDepth}");
        }

        if (freshnessPrunedEnvelopes > 0)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_transport_stale_video_purged; dropped_envelopes={freshnessPrunedEnvelopes}; dropped_frames={freshnessPrunedFrames}; retained_stream_epoch={retainedStreamEpoch?.ToString() ?? "(none)"}; retained_frame_id={retainedFrameId?.ToString() ?? "(none)"}; queue_depth={ScreenShareTransportQueueDepth}; queued_bytes={Volatile.Read(ref screenShareOutboundQueuedBytes)}; mode={(screenShareBridgeCatchUpOnlyActive ? "catch_up_only" : "lane_behind")}");
        }

        if (shouldStartDrainer)
        {
            _ = Task.Run(ProcessScreenShareOutboundQueueAsync, CancellationToken.None);
        }

        return completion.Task.WaitAsync(ct);
    }

    private async Task ProcessScreenShareOutboundQueueAsync()
    {
        while (true)
        {
            QueuedScreenShareEnvelope? next = null;
            lock (screenShareOutboundQueueGate)
            {
                if (screenShareOutboundQueue.First is not null)
                {
                    next = screenShareOutboundQueue.First.Value;
                    screenShareOutboundQueue.RemoveFirst();
                    screenShareOutboundQueuedBytes = Math.Max(0, screenShareOutboundQueuedBytes - next.Payload.Length);
                    NknRuntimeDiagnostics.SetOutboundLaneQueueDepth("screenshare", screenShareOutboundQueue.Count, screenShareOutboundPeakDepthSeen);
                    UpdateScreenShareLaneCongestionStateUnsafe();
                }
                else
                {
                    screenShareOutboundDrainerActive = false;
                    NknRuntimeDiagnostics.SetOutboundLaneInFlight("screenshare", 0);
                    UpdateScreenShareLaneCongestionStateUnsafe();
                    return;
                }
            }

            try
            {
                var currentGeneration = Volatile.Read(ref screenShareOutboundGeneration);
                if (next.Generation != currentGeneration ||
                    string.IsNullOrWhiteSpace(next.SessionId) ||
                    !string.Equals(currentSessionSecurityState.SessionId?.Value, next.SessionId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(remoteMediaEndpoint) ||
                    !string.Equals(remoteMediaEndpoint, next.Destination, StringComparison.Ordinal))
                {
                    next.Completion.TrySetException(new ScreenShareSendSupersededException("Queued media envelope was superseded by a newer screenshare state."));
                    continue;
                }

                NknRuntimeDiagnostics.SetOutboundLaneInFlight("screenshare", 1);
                if (await TrySendAcceleratedEnvelopeAsync(MsgType.ScreenShareFrame, NknBridgeChannel.Media, next.Payload, CancellationToken.None).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref screenShareMessagesSent);
                    Interlocked.Add(ref screenSharePayloadBytesSent, next.Payload.Length);
                    NknRuntimeDiagnostics.IncrementScreenShareMessagesSent();
                    NknRuntimeDiagnostics.AddScreenSharePayloadBytesSent(next.Payload.Length);
                    NknRuntimeDiagnostics.AddOutboundLaneSent("screenshare", next.Payload.Length);
                    next.Completion.TrySetResult();
                    continue;
                }

                await client.SendMediaAsync(next.Destination, next.Payload, CancellationToken.None).ConfigureAwait(false);
                RecordTunaFallbackNknFrameSent(MsgType.ScreenShareFrame, NknBridgeChannel.Media, next.Payload.Length);
                Interlocked.Increment(ref screenShareMessagesSent);
                Interlocked.Add(ref screenSharePayloadBytesSent, next.Payload.Length);
                NknRuntimeDiagnostics.IncrementScreenShareMessagesSent();
                NknRuntimeDiagnostics.AddScreenSharePayloadBytesSent(next.Payload.Length);
                NknRuntimeDiagnostics.AddOutboundLaneSent("screenshare", next.Payload.Length);
                next.Completion.TrySetResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                NknRuntimeDiagnostics.SetLastError(ex);
                Log($"ProcessScreenShareOutboundQueueAsync send failed ({ex.GetType().Name})");
                next?.Completion.TrySetException(ex);
            }
            finally
            {
                NknRuntimeDiagnostics.SetOutboundLaneInFlight("screenshare", 0);
            }
        }
    }

    private bool ComputeScreenShareLaneCongestionUnsafe()
    {
        return screenShareOutboundQueue.Count >= ScreenShareLaneCongestionDepthThreshold ||
               screenShareOutboundQueuedBytes >= ScreenShareLaneCongestionBytesThreshold;
    }

    private bool ComputeScreenShareLaneSevereCongestionUnsafe()
    {
        return screenShareOutboundQueue.Count >= ScreenShareLaneSevereDepthThreshold ||
               HasRecentScreenShareLaneDropUnsafe();
    }

    private bool HasRecentScreenShareLaneDropUnsafe()
    {
        return GetRecentScreenShareLaneDropCountUnsafe() > 0;
    }

    private void UpdateScreenShareLaneCongestionStateUnsafe()
    {
        var isCongested = ComputeScreenShareLaneCongestionUnsafe();
        var isSeverelyCongested = ComputeScreenShareLaneSevereCongestionUnsafe();
        if (isCongested != screenShareLaneCongestionActive)
        {
            screenShareLaneCongestionActive = isCongested;
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_lane_congestion_{(isCongested ? "entered" : "exited")}; queue_depth={screenShareOutboundQueue.Count}; queued_bytes={screenShareOutboundQueuedBytes}; severe={isSeverelyCongested}");
        }

        if (isSeverelyCongested != screenShareLaneSevereCongestionActive)
        {
            screenShareLaneSevereCongestionActive = isSeverelyCongested;
            if (isSeverelyCongested)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_lane_state; state=severe_congestion; queue_depth={screenShareOutboundQueue.Count}; queued_bytes={screenShareOutboundQueuedBytes}; recent_drop={(HasRecentScreenShareLaneDropUnsafe() ? 1 : 0)}");
            }
        }
    }

    private EffectiveScreenShareLaneState ComputeEffectiveScreenShareLaneStateUnsafe()
    {
        var bridgeState = bridgeScreenShareQueueCapability?.CurrentScreenShareQueueState;
        var bridgeHealthState = bridgeScreenShareQueueCapability?.CurrentScreenShareHealthState;
        if (bridgeState is null)
        {
            return new EffectiveScreenShareLaneState(
                false,
                false,
                0,
                0,
                0,
                0,
                bridgeHealthState?.RecentIssueCount ?? 0,
                bridgeHealthState?.IsSevere == true);
        }

        return new EffectiveScreenShareLaneState(
            IsCongested: bridgeState.Value.IsCongested,
            IsSevere: bridgeState.Value.IsSevere,
            QueueDepth: bridgeState.Value.QueueDepth,
            QueuedBytes: bridgeState.Value.QueuedBytes,
            OldestQueuedAgeMs: Math.Max(0, bridgeState.Value.OldestQueuedAgeMs),
            RecentDropCount: Math.Max(0, bridgeState.Value.DroppedSinceLast),
            RecentHealthIssueCount: bridgeHealthState?.RecentIssueCount ?? 0,
            IsHealthSevere: bridgeHealthState?.IsSevere == true);
    }

    private void PruneQueuedScreenShareVideoUnsafe(
        QueuedScreenShareEnvelopeMetadata newestMetadata,
        out int droppedEnvelopes,
        out int droppedFrames,
        out long? retainedStreamEpoch,
        out long? retainedFrameId,
        ref List<TaskCompletionSource>? supersededCompletions)
    {
        droppedEnvelopes = 0;
        droppedFrames = 0;
        retainedStreamEpoch = newestMetadata.StreamEpoch;
        retainedFrameId = newestMetadata.FrameId;

        if (screenShareOutboundQueue.Count <= 1)
        {
            return;
        }

        HashSet<(long StreamEpoch, long FrameId)>? droppedFrameKeys = null;
        var node = screenShareOutboundQueue.First;
        while (node is not null)
        {
            var next = node.Next;
            var queued = node.Value;
            var metadata = queued.Metadata;
            if (metadata is not null &&
                IsScreenShareVideoFrameSuperseded(metadata, newestMetadata))
            {
                screenShareOutboundQueue.Remove(node);
                screenShareOutboundQueuedBytes = Math.Max(0, screenShareOutboundQueuedBytes - queued.Payload.Length);
                supersededCompletions ??= new List<TaskCompletionSource>();
                supersededCompletions.Add(queued.Completion);
                droppedEnvelopes++;
                droppedFrameKeys ??= new HashSet<(long StreamEpoch, long FrameId)>();
                droppedFrameKeys.Add((metadata.StreamEpoch, metadata.FrameId));
            }

            node = next;
        }

        if (droppedFrameKeys is not null && droppedFrameKeys.Count > 0)
        {
            droppedFrames = droppedFrameKeys.Count;
            RecordScreenShareLaneFrameDropsUnsafe(droppedFrames);
        }
    }

    private QueuedScreenShareEnvelopeMetadata? TryCreateQueuedScreenShareEnvelopeMetadata(
        byte[] payload,
        string? recoverySendRole,
        long recoveryBurstToken)
    {
        if (!EnvelopeCodec.TryDeserialize(payload, out var envelope) ||
            envelope.Type != MsgType.ScreenShareFrame)
        {
            return null;
        }

        byte[] key;
        try
        {
            key = GetControlSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        SessionSecureEnvelopePayload securePayload;
        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                envelope.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.ScreenShare,
                    MessageType: "screenshare_frame"));
        }
        catch
        {
            return null;
        }

        if (ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(securePayload.Plaintext, out var fragments, out _)
            && fragments.Length > 0)
        {
            var fragment = fragments[0];
            return new QueuedScreenShareEnvelopeMetadata(
                fragment.SessionId,
                fragment.StreamEpoch,
                fragment.FrameId,
                fragment.CapturedTsUtcMs,
                fragment.IsKeyFrame,
                string.IsNullOrWhiteSpace(recoverySendRole) ? null : recoverySendRole.Trim(),
                recoveryBurstToken > 0 ? recoveryBurstToken : 0);
        }

        return null;
    }

    internal void ArmRecoveryBurstControlFallback(string sessionId, long streamEpoch, long burstToken, long ownerFrameId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            streamEpoch <= 0 ||
            burstToken <= 0 ||
            ownerFrameId < 0)
        {
            return;
        }

        lock (screenShareControlFallbackGate)
        {
            // An unresolved recovery burst becomes the sole control-fallback owner for its epoch.
            screenShareRecoveryBurstLease = new RecoveryBurstLease
            {
                SessionId = sessionId.Trim(),
                StreamEpoch = streamEpoch,
                BurstToken = burstToken,
                OwnerFrameId = ownerFrameId,
            };
        }
    }

    internal void ResolveRecoveryBurstControlFallback(long burstToken)
    {
        lock (screenShareControlFallbackGate)
        {
            if (burstToken > 0 &&
                screenShareRecoveryBurstLease is { BurstToken: var activeBurstToken } &&
                activeBurstToken > 0 &&
                activeBurstToken != burstToken)
            {
                return;
            }

            ClearScreenShareRecoveryControlFallbackStateUnsafe();
        }
    }

    private bool TrySelectScreenShareControlFallback(
        QueuedScreenShareEnvelopeMetadata? reasonMetadata,
        out string reason,
        out long recoveryBurstToken)
    {
        reason = "none";
        recoveryBurstToken = 0;
        if (reasonMetadata is null)
        {
            return false;
        }

        lock (screenShareControlFallbackGate)
        {
            return TrySelectRecoveryBurstControlFallbackUnsafe(reasonMetadata, out reason, out recoveryBurstToken);
        }
    }

    private bool TrySelectRecoveryBurstControlFallbackUnsafe(
        QueuedScreenShareEnvelopeMetadata reasonMetadata,
        out string reason,
        out long recoveryBurstToken)
    {
        reason = "none";
        recoveryBurstToken = 0;

        if (screenShareRecoveryBurstLease is not { } recoveryBurstLease)
        {
            return false;
        }

        if (!string.Equals(recoveryBurstLease.SessionId, reasonMetadata.SessionId, StringComparison.Ordinal) ||
            recoveryBurstLease.StreamEpoch != reasonMetadata.StreamEpoch)
        {
            if (!string.Equals(recoveryBurstLease.SessionId, reasonMetadata.SessionId, StringComparison.Ordinal) ||
                (recoveryBurstLease.StreamEpoch >= 0 &&
                 reasonMetadata.StreamEpoch > recoveryBurstLease.StreamEpoch))
            {
                ClearScreenShareRecoveryControlFallbackStateUnsafe();
            }

            return false;
        }

        if (reasonMetadata.RecoveryBurstToken <= 0 ||
            reasonMetadata.RecoveryBurstToken != recoveryBurstLease.BurstToken)
        {
            return false;
        }

        recoveryBurstToken = recoveryBurstLease.BurstToken;
        if (reasonMetadata.IsKeyFrame &&
            reasonMetadata.FrameId == recoveryBurstLease.OwnerFrameId &&
            string.Equals(reasonMetadata.RecoverySendRole, "owner", StringComparison.Ordinal))
        {
            reason = "recovery_burst_owner";
            return true;
        }

        if (!reasonMetadata.IsKeyFrame &&
            reasonMetadata.FrameId > recoveryBurstLease.OwnerFrameId &&
            string.Equals(reasonMetadata.RecoverySendRole, "protected_follower", StringComparison.Ordinal))
        {
            reason = "recovery_burst_follower";
            return true;
        }

        return false;
    }

    private void ResetScreenShareControlFallbackState()
    {
        lock (screenShareControlFallbackGate)
        {
            ClearScreenShareRecoveryControlFallbackStateUnsafe();
        }
    }

    private void ClearScreenShareRecoveryControlFallbackStateUnsafe()
    {
        screenShareRecoveryBurstLease = null;
    }

    private bool ShouldScheduleScreenShareControlBootstrapRetry(
        QueuedScreenShareEnvelopeMetadata? reasonMetadata,
        string fallbackReason,
        out TimeSpan retryDelay)
    {
        retryDelay = TimeSpan.Zero;
        if (reasonMetadata is null)
        {
            return false;
        }

        if (reasonMetadata.IsKeyFrame)
        {
            retryDelay = ScreenShareControlBootstrapKeyframeRetryDelay;
            return string.Equals(fallbackReason, "recovery_burst_owner", StringComparison.Ordinal);
        }

        if (string.Equals(fallbackReason, "recovery_burst_follower", StringComparison.Ordinal))
        {
            retryDelay = ScreenShareControlBootstrapFollowerRetryDelay;
            return true;
        }

        return false;
    }

    private void ScheduleScreenShareControlBootstrapRetry(
        byte[] rawPayload,
        QueuedScreenShareEnvelopeMetadata reasonMetadata,
        string fallbackReason,
        TimeSpan retryDelay,
        long recoveryBurstToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(retryDelay, CancellationToken.None).ConfigureAwait(false);
                    await QueueScreenShareControlBootstrapRetryAsync(rawPayload, reasonMetadata, fallbackReason, retryDelay, recoveryBurstToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    Log($"ScreenShare control bootstrap retry failed (frame_id={reasonMetadata.FrameId}, reason={fallbackReason}, ex={ex.GetType().Name})");
                }
            });
    }

    private async Task QueueScreenShareControlBootstrapRetryAsync(
        byte[] rawPayload,
        QueuedScreenShareEnvelopeMetadata reasonMetadata,
        string fallbackReason,
        TimeSpan retryDelay,
        long recoveryBurstToken)
    {
        if (disposed || rawPayload.Length == 0)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_retry_skipped; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; skip_reason={(disposed ? "transport_disposed" : "empty_payload")}");
            return;
        }

        if (!ValidateRecoveryBurstControlRetry(reasonMetadata, fallbackReason, recoveryBurstToken, out var recoveryRetrySkipReason))
        {
            if (string.Equals(recoveryRetrySkipReason, "recovery_burst_resolved", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref screenShareRecoveryControlBootstrapRetrySkippedDueToBurstResolvedCount);
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_retry_skipped; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; skip_reason={recoveryRetrySkipReason}");
            return;
        }

        if (!TryParseScreenSharePayload(rawPayload, out var messageType, out var messageSessionId) ||
            !string.Equals(messageType, "frame", StringComparison.Ordinal) ||
            !string.Equals(messageSessionId, reasonMetadata.SessionId, StringComparison.Ordinal))
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_retry_skipped; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; skip_reason=payload_parse_or_session_mismatch");
            return;
        }

        if (!TryValidateScreenShareSession(messageType, messageSessionId))
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_retry_skipped; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; skip_reason=session_validation_failed");
            return;
        }

        if (!IsScreenShareAuthorizedForDispatch(messageType, messageSessionId))
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_retry_skipped; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; skip_reason=authorization_failed");
            return;
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_retry_skipped; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; skip_reason=remote_endpoint_unavailable");
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_retry_skipped; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; skip_reason=session_context_unavailable");
            return;
        }

        var securePayload = CreateSecureScreenSharePayload(MsgType.ScreenShareFrame, rawPayload);
        var retryEnvelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareFrame, securePayload, replyTo: null);
        var retryTransportPayload = EnvelopeCodec.Serialize(retryEnvelope);
        if (!ValidateRecoveryBurstControlRetry(reasonMetadata, fallbackReason, recoveryBurstToken, out recoveryRetrySkipReason))
        {
            if (string.Equals(recoveryRetrySkipReason, "recovery_burst_resolved", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref screenShareRecoveryControlBootstrapRetrySkippedDueToBurstResolvedCount);
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_retry_skipped; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; skip_reason={recoveryRetrySkipReason}");
            return;
        }

        var queued = await QueueControlEnvelopeAsync(remoteEndpoint, retryEnvelope, ControlOutboundLane.High, CancellationToken.None).ConfigureAwait(false);
        if (queued &&
            !ValidateRecoveryBurstControlRetry(reasonMetadata, fallbackReason, recoveryBurstToken, out recoveryRetrySkipReason))
        {
            if (string.Equals(recoveryRetrySkipReason, "recovery_burst_resolved", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref screenShareRecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount);
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_control_bootstrap_retry_queued_after_burst_resolution; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; retry_delay_ms={retryDelay.TotalMilliseconds:F0}; transport_payload_bytes={retryTransportPayload.Length}");
            }
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_control_bootstrap_retry_{(queued ? "queued" : "rejected")}; session_id={reasonMetadata.SessionId}; stream_epoch={reasonMetadata.StreamEpoch}; frame_id={reasonMetadata.FrameId}; is_keyframe={(reasonMetadata.IsKeyFrame ? 1 : 0)}; reason={fallbackReason}; retry_delay_ms={retryDelay.TotalMilliseconds:F0}; transport_payload_bytes={retryTransportPayload.Length}");
    }

    private bool ValidateRecoveryBurstControlRetry(
        QueuedScreenShareEnvelopeMetadata reasonMetadata,
        string fallbackReason,
        long recoveryBurstToken,
        out string skipReason)
    {
        skipReason = "none";
        if (recoveryBurstToken <= 0 ||
            (!string.Equals(fallbackReason, "recovery_burst_owner", StringComparison.Ordinal) &&
             !string.Equals(fallbackReason, "recovery_burst_follower", StringComparison.Ordinal)))
        {
            return true;
        }

        lock (screenShareControlFallbackGate)
        {
            if (screenShareRecoveryBurstLease is not { } recoveryBurstLease ||
                recoveryBurstLease.BurstToken != recoveryBurstToken ||
                !string.Equals(recoveryBurstLease.SessionId, reasonMetadata.SessionId, StringComparison.Ordinal) ||
                recoveryBurstLease.StreamEpoch != reasonMetadata.StreamEpoch ||
                recoveryBurstLease.OwnerFrameId < 0 ||
                reasonMetadata.RecoveryBurstToken != recoveryBurstToken)
            {
                skipReason = "recovery_burst_resolved";
                return false;
            }
        }

        return true;
    }

    private static bool IsScreenShareVideoFrameSuperseded(
        QueuedScreenShareEnvelopeMetadata queued,
        QueuedScreenShareEnvelopeMetadata newest)
    {
        if (!string.Equals(queued.SessionId, newest.SessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (queued.StreamEpoch != newest.StreamEpoch)
        {
            return queued.StreamEpoch < newest.StreamEpoch;
        }

        return queued.FrameId < newest.FrameId;
    }

    private void RecordScreenShareLaneFrameDropsUnsafe(int droppedFrames)
    {
        if (droppedFrames <= 0)
        {
            return;
        }

        var utcNowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        screenShareLaneRecentDropWindow.Enqueue((utcNowTicks, droppedFrames));
        PruneScreenShareLaneRecentDropWindowUnsafe(utcNowTicks);
    }

    private long GetRecentScreenShareLaneDropCountUnsafe()
    {
        var utcNowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        PruneScreenShareLaneRecentDropWindowUnsafe(utcNowTicks);
        long total = 0;
        foreach (var sample in screenShareLaneRecentDropWindow)
        {
            total += sample.DroppedFrames;
        }

        return total;
    }

    private void PruneScreenShareLaneRecentDropWindowUnsafe(long utcNowTicks)
    {
        while (screenShareLaneRecentDropWindow.Count > 0)
        {
            var next = screenShareLaneRecentDropWindow.Peek();
            var elapsedTicks = utcNowTicks - next.UtcTicks;
            if (elapsedTicks >= 0 && elapsedTicks < ScreenShareLaneRecentDropWindow.Ticks)
            {
                break;
            }

            screenShareLaneRecentDropWindow.Dequeue();
        }
    }

    private async Task EnsureScreenShareBridgeSessionStartedAsync(CancellationToken ct)
    {
        var capability = bridgeScreenShareQueueCapability;
        if (screenShareBridgePolicyGeneration != 0 || capability is null)
        {
            return;
        }

        if (!capability.IsBridgeProcessRunning)
        {
            return;
        }

        await ApplyScreenShareBridgePolicyAsync(
            BridgeScreenShareQueueMode.Normal,
            flushQueued: false,
            reason: "screenshare_started",
            ct).ConfigureAwait(false);
    }

    private async Task ApplyScreenShareBridgePolicyAsync(
        BridgeScreenShareQueueMode mode,
        bool flushQueued,
        string reason,
        CancellationToken ct)
    {
        var capability = bridgeScreenShareQueueCapability;
        if (capability is null)
        {
            return;
        }

        if (!capability.IsBridgeProcessRunning)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_bridge_policy_skipped; mode={(mode == BridgeScreenShareQueueMode.CatchUpOnly ? "catch_up_only" : "normal")}; flush_queued={(flushQueued ? 1 : 0)}; reason={reason}; bridge_running=0");
            return;
        }

        var generation = Interlocked.Increment(ref screenShareBridgePolicyNextGeneration);
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_bridge_policy_apply_requested; mode={(mode == BridgeScreenShareQueueMode.CatchUpOnly ? "catch_up_only" : "normal")}; generation={generation}; flush_queued={(flushQueued ? 1 : 0)}; reason={reason}");
        await capability.SetScreenSharePolicyAsync(mode, generation, flushQueued, ct).ConfigureAwait(false);
        Interlocked.Exchange(ref screenShareBridgePolicyGeneration, generation);
        screenShareBridgeCatchUpOnlyActive = mode == BridgeScreenShareQueueMode.CatchUpOnly;
    }

    private void ResetScreenShareBridgePolicyState()
    {
        screenShareBridgeCatchUpOnlyActive = false;
        Interlocked.Exchange(ref screenShareBridgePolicyGeneration, 0);
        Interlocked.Exchange(ref screenShareBridgePolicyNextGeneration, 0);
    }

    private readonly record struct EffectiveScreenShareLaneState(
        bool IsCongested,
        bool IsSevere,
        int QueueDepth,
        int QueuedBytes,
        long OldestQueuedAgeMs,
        long RecentDropCount,
        long RecentHealthIssueCount,
        bool IsHealthSevere);

    private void ClearScreenShareOutboundQueue(string reason)
    {
        int dropped = 0;
        List<TaskCompletionSource>? canceledCompletions = null;
        lock (screenShareOutboundQueueGate)
        {
            dropped = screenShareOutboundQueue.Count;
            if (dropped > 0)
            {
                canceledCompletions = new List<TaskCompletionSource>(dropped);
                foreach (var queued in screenShareOutboundQueue)
                {
                    canceledCompletions.Add(queued.Completion);
                }

                screenShareOutboundQueue.Clear();
            }

            screenShareOutboundQueuedBytes = 0;
            screenShareOutboundDrainerActive = false;
            screenShareOutboundPeakDepthSeen = 0;
            Interlocked.Increment(ref screenShareOutboundGeneration);
            NknRuntimeDiagnostics.SetOutboundLaneQueueDepth("screenshare", 0, 0);
            NknRuntimeDiagnostics.SetOutboundLaneInFlight("screenshare", 0);
            UpdateScreenShareLaneCongestionStateUnsafe();
        }

        ResetScreenShareControlFallbackState();
        RequestBridgeScreenShareQueueFlush(reason);

        if (canceledCompletions is not null)
        {
            foreach (var completion in canceledCompletions)
            {
                completion.TrySetException(new ScreenShareSendSupersededException("Queued media envelope was cleared before send."));
            }
        }

        for (var i = 0; i < dropped; i++)
        {
            NknRuntimeDiagnostics.IncrementOutboundLaneRejected("screenshare");
        }

        if (dropped > 0)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_lane_cleared; reason={reason}; dropped_count={dropped}");
        }

        if (!disposed && bridgeScreenShareQueueCapability is not null)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await ApplyScreenShareBridgePolicyAsync(
                            BridgeScreenShareQueueMode.Normal,
                            flushQueued: true,
                            reason,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                    {
                        NknRuntimeDiagnostics.SetLastError(ex);
                    }
                },
                CancellationToken.None);
        }
        else
        {
            ResetScreenShareBridgePolicyState();
        }
    }

    private void RequestBridgeScreenShareQueueFlush(string reason)
    {
        var capability = bridgeScreenShareQueueCapability;
        if (capability is null || !capability.IsBridgeProcessRunning || disposed)
        {
            return;
        }

        var mode = screenShareBridgeCatchUpOnlyActive
            ? BridgeScreenShareQueueMode.CatchUpOnly
            : BridgeScreenShareQueueMode.Normal;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await ApplyScreenShareBridgePolicyAsync(mode, flushQueued: true, reason, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    Log($"FlushScreenShareTransportQueue bridge flush failed (reason={reason}, ex={ex.GetType().Name})");
                }
            },
            CancellationToken.None);
    }

    private async Task<bool> SendScreenShareTransportPayloadAsync(
        string destination,
        ReadOnlyMemory<byte> payload,
        bool waitForOutboundGate,
        CancellationToken ct)
    {
        if (waitForOutboundGate)
        {
            await outboundSendGate.WaitAsync(ct).ConfigureAwait(false);
        }
        else if (!await TryAcquireScreenShareOutboundGateAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await client.SendAsync(destination, payload.ToArray(), ct).ConfigureAwait(false);
            Interlocked.Increment(ref screenShareMessagesSent);
            Interlocked.Add(ref screenSharePayloadBytesSent, payload.Length);
            NknRuntimeDiagnostics.IncrementScreenShareMessagesSent();
            NknRuntimeDiagnostics.AddScreenSharePayloadBytesSent(payload.Length);
            return true;
        }
        finally
        {
            outboundSendGate.Release();
        }
    }

    private async Task<bool> TryAcquireScreenShareOutboundGateAsync(CancellationToken ct)
    {
        if (await outboundSendGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return true;
        }

        return await outboundSendGate.WaitAsync(ScreenShareOutboundGateWaitBudget, ct).ConfigureAwait(false);
    }

    private async Task SendScreenShareStopEnvelopeAsync(string destination, Envelope envelope, CancellationToken ct)
    {
        FlushLowPriorityControlOutboundQueue("screenshare_stop");
        var payload = EnvelopeCodec.Serialize(envelope);
        await SendScreenShareTransportPayloadAsync(destination, payload, waitForOutboundGate: true, ct).ConfigureAwait(false);
    }

    private void SendScreenShareStopEnvelopeRetriesFireAndForget(string destination, Envelope envelope, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delay in ScreenShareStopRetryDelays)
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);

                    if (disposed ||
                        !string.Equals(currentSessionSecurityState.SessionId?.Value, sessionId, StringComparison.Ordinal) ||
                        !string.Equals(remoteEndpoint, destination, StringComparison.Ordinal))
                    {
                        return;
                    }

                    await SendScreenShareStopEnvelopeAsync(destination, envelope, CancellationToken.None).ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_stop_envelope_resend_dispatched; session_id={sessionId ?? "(none)"}; msg_id={envelope.MessageId}; delay_ms={delay.TotalMilliseconds:F0}");
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                NknRuntimeDiagnostics.SetLastError(ex);
                LocalOperationalLog.Warn(
                    "ScreenShareTransport",
                    $"event=screenshare_stop_envelope_resend_failed; session_id={sessionId ?? "(none)"}; msg_id={envelope.MessageId}; ex={ex.GetType().Name}");
                Log($"SendScreenSharePayloadAsync stop envelope resend failed (msg_id={envelope.MessageId}, ex={ex.GetType().Name})");
            }
        });
    }


    public async Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlrequest_no_session_context");
            Log("SendControlRequestAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlrequest_no_remote_endpoint");
            Log("SendControlRequestAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlRequest,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlRequest, payload, replyTo: null);
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        Log($"SendControlRequestAsync sent ControlRequest (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length})");
    }

    public async Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlresponse_no_session_context");
            Log("SendControlResponseAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlresponse_no_remote_endpoint");
            Log("SendControlResponseAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlResponse,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlResponse, payload, replyTo: null);
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        Log($"SendControlResponseAsync sent ControlResponse (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length}, decision={message.Decision})");
    }

    public async Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlstart_no_session_context");
            Log("SendControlStartAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlstart_no_remote_endpoint");
            Log("SendControlStartAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlStart,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlStart, payload, replyTo: null);
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        Log($"SendControlStartAsync sent ControlStart (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length})");
    }

    public async Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlstop_no_session_context");
            Log("SendControlStopAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlstop_no_remote_endpoint");
            Log("SendControlStopAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlStop,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlStop, payload, replyTo: null);
        FlushLowPriorityControlOutboundQueue("control_stop");
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        Log($"SendControlStopAsync sent ControlStop (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length}, has_reason={message.Reason is not null})");
    }

    public async Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlinput_no_session_context");
            Log("SendControlInputAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlinput_no_remote_endpoint");
            Log("SendControlInputAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlInput,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlInput, payload, replyTo: null);
        var isMouseMove = IsLowPriorityControlInput(message);
        await QueueControlEnvelopeAsync(
                remoteEndpoint,
                envelope,
                ResolveControlOutboundLane(MsgType.ControlInput, isLowPriorityMouseMove: isMouseMove),
                ct,
                isLowPriorityMouseMove: isMouseMove)
            .ConfigureAwait(false);
        Log($"SendControlInputAsync sent ControlInput (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length}, kind={message.Kind}, seq={message.Seq})");
    }

    public async Task SendControlAckAsync(ControlInputAckV1 ack, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ack);
        ThrowIfDisposed();
        ack = EnsureControlSessionId(ack);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlack_no_session_context");
            Log("SendControlAckAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlack_no_remote_endpoint");
            Log("SendControlAckAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlAck,
            ack.RequestId,
            RemoteControlPayloadCodec.Serialize(ack));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlAck, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
                remoteEndpoint,
                envelope,
                ResolveControlOutboundLane(MsgType.ControlAck),
                ct)
            .ConfigureAwait(false);
        Log($"SendControlAckAsync sent ControlAck (msg_id={envelope.MessageId}, request_id_len={ack.RequestId.Length}, ack_seq={ack.AckSeq})");
    }

    public async Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controldisplayinfo_no_session_context");
            Log("SendControlDisplayInfoAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controldisplayinfo_no_remote_endpoint");
            Log("SendControlDisplayInfoAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlDisplayInfo,
            requestId: null,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlDisplayInfo, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
                remoteEndpoint,
                envelope,
                ResolveControlOutboundLane(MsgType.ControlDisplayInfo),
                ct)
            .ConfigureAwait(false);
        Log($"SendControlDisplayInfoAsync sent ControlDisplayInfo (msg_id={envelope.MessageId}, display_id_len={message.DisplayId.Length}, revision={message.Revision}, frame={message.FrameWidth}x{message.FrameHeight})");
    }

    public async Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();
        snapshot = EnsureControlSessionId(snapshot);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlstatesnapshot_no_session_context");
            Log("SendControlStateSnapshotAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlstatesnapshot_no_remote_endpoint");
            Log("SendControlStateSnapshotAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlStateSnapshot,
            snapshot.RequestId,
            RemoteControlPayloadCodec.Serialize(snapshot));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlStateSnapshot, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
                remoteEndpoint,
                envelope,
                ResolveControlOutboundLane(MsgType.ControlStateSnapshot),
                ct)
            .ConfigureAwait(false);
        Log($"SendControlStateSnapshotAsync sent ControlStateSnapshot (msg_id={envelope.MessageId}, request_id_len={snapshot.RequestId.Length}, seq={snapshot.Seq}, buttons_mask={snapshot.MouseButtonsMask}, modifiers_mask={snapshot.ModifiersMask})");
    }


    private Task<bool> QueueControlEnvelopeAsync(
        string destination,
        Envelope envelope,
        ControlOutboundLane lane,
        CancellationToken ct,
        bool isLowPriorityMouseMove = false,
        bool isLowPriorityScreenShareCursorState = false)
        => controlOutboundQueue.QueueEnvelopeAsync(
            destination,
            envelope,
            lane,
            ct,
            isLowPriorityMouseMove,
            isLowPriorityScreenShareCursorState);

    private void FlushLowPriorityControlOutboundQueue(string reason)
        => controlOutboundQueue.FlushLowPriority(reason);

    private void FlushAllControlOutboundQueues(string reason)
        => controlOutboundQueue.FlushAll(reason);

    private static bool IsLowPriorityControlInput(ControlInputMessageV1 message)
    {
        var kind = message.Kind;
        if (string.IsNullOrWhiteSpace(kind))
        {
            return false;
        }

        // Keep clicks, wheel and keyboard in high lane for responsiveness.
        return string.Equals(kind.Trim(), "mouse_move", StringComparison.Ordinal);
    }

    private void SubscribeClientEvents()
    {
        client.MessageReceived += OnClientMessageReceived;
        client.Disconnected += OnClientDisconnected;
        if (client is RealNknClientAdapter realClient)
        {
            realClient.BridgeLifecycle += OnBridgeLifecycle;
        }
        if (accelerationLane is not null)
        {
            accelerationLane.MessageReceived += OnAccelerationMessageReceived;
            accelerationLane.StateChanged += OnAccelerationStateChanged;
        }
    }

    private void OnBridgeLifecycle(object? sender, BridgeLifecycleEvent e)
    {
        if (e.Kind == BridgeLifecycleEventKind.QueueCleared)
        {
            HandleRuntimeUnlockOfferQueueCleared(e);
        }

        if (e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryStarted)
        {
            Volatile.Write(ref bridgeReceiveStallRecoveryActive, 1);
            var recoveryReason = string.IsNullOrWhiteSpace(e.ExitReasonText) ? "receive_stall_recovery" : e.ExitReasonText;
            var sessionId = currentSessionSecurityState.SessionId?.Value;
            MarkFileTransferTunaActivationBridgeRecoveryStarted(recoveryReason);
            InterruptRuntimeUnlockOfferForBridgeRecovery(
                "offer_interrupted_by_bridge_recovery",
                recoveryReason,
                "receive_stall_recovery_started");
            var sessionLivenessReceiveRecovery = IsSessionLivenessReceiveRecoveryReason(recoveryReason);
            var protectedActivePostTunaFallbackRepair =
                ShouldProtectPostTunaFallbackAvailabilityDuringRuntimeUnlockRecovery(
                    sessionId,
                    recoveryReason,
                    "receive_stall_recovery_started",
                    out _);
            if (!protectedActivePostTunaFallbackRepair &&
                ShouldSuppressFileTransferTransportRecoveredForTunaActivationPause("receive_stall_recovery_started", out _))
            {
                return;
            }

            if (protectedActivePostTunaFallbackRepair)
            {
                // Runtime-unlock offer recovery still retires/retries the Tuna offer, but it must
                // not publish transport unavailability into an actively repairing fallback V6 route.
            }
            else if (sessionLivenessReceiveRecovery)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_session_liveness_receive_recovery_availability_preserved; session_id={SanitizeLogToken(sessionId ?? "none")}; transfer_id=(unknown); direction=unknown; reason={SanitizeLogToken(recoveryReason)}; trigger=receive_stall_recovery_started");
            }
            else if (ShouldUseFileTransferV6EpochForRegularNknRecovery(sessionId))
            {
                var handoffKind = FileTransferTransportHandoffKind.RegularNknRecovery;
                var markProofPending = true;
                if (TryGetUnresolvedFileTransferV6TransportEpochForCurrentSession(out var unresolvedEpoch) &&
                    unresolvedEpoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
                    unresolvedEpoch.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback)
                {
                    handoffKind = FileTransferTransportHandoffKind.TunaToNormalFallback;
                    markProofPending = false;
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=filetransfer_receive_stall_recovery_preserved_tuna_fallback_epoch; session_id={SanitizeLogToken(unresolvedEpoch.SessionId)}; transfer_id={SanitizeLogToken(unresolvedEpoch.TransferId)}; direction={unresolvedEpoch.Direction.ToString().ToLowerInvariant()}; transport_epoch={unresolvedEpoch.TransportEpoch}; reason={SanitizeLogToken(recoveryReason)}");
                }

                if (markProofPending)
                {
                    MarkFileTransferFallbackNknProofPending(
                        reason: recoveryReason,
                        sessionId: sessionId,
                        lanes: NknAccelerationLaneKind.File);
                }

                var deferResumeUntilRecoveryCompletes = ShouldDeferPostTunaFallbackReceiveStallRecoveryStart(
                    recoveryReason,
                    sessionId,
                    handoffKind);
                if (deferResumeUntilRecoveryCompletes)
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=filetransfer_post_tuna_fallback_receive_recovery_start_handoff_deferred; session_id={SanitizeLogToken(sessionId ?? "none")}; reason={SanitizeLogToken(recoveryReason)}; handoff_kind={handoffKind.ToString().ToLowerInvariant()}; trigger=receive_stall_recovery_started");
                }

                SetFileTransferDataSessionsAvailability(
                    isAvailable: false,
                    reason: "receive_stall_recovery",
                    requiresResumeRequest: !deferResumeUntilRecoveryCompletes,
                    handoffKind: deferResumeUntilRecoveryCompletes ? FileTransferTransportHandoffKind.None : handoffKind,
                    targetTransport: FileTransferTransportKind.RegularNkn);
            }
            else
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_regular_nkn_receive_recovery_no_epoch; session_id={SanitizeLogToken(sessionId ?? "none")}; reason={SanitizeLogToken(recoveryReason)}; trigger=bridge_receive_stall_recovery_started");
                SetFileTransferDataSessionsAvailability(
                    isAvailable: false,
                    reason: "receive_stall_recovery",
                    requiresResumeRequest: false,
                    handoffKind: FileTransferTransportHandoffKind.None,
                    targetTransport: FileTransferTransportKind.RegularNkn);
            }
        }

        if (e.Kind == BridgeLifecycleEventKind.Ready)
        {
            if (ShouldDeferBridgeProcessReadyUntilReceiveStallRecoveryCompletes(e))
            {
                var sessionId = SanitizeLogToken(currentSessionSecurityState.SessionId?.Value ?? "none");
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=filetransfer_bridge_ready_deferred_until_receive_stall_recovery_completed; session_id={sessionId}; proof_pending={(IsFileTransferFallbackNknProofPending() ? 1 : 0)}; start_mode={e.StartMode?.ToString().ToLowerInvariant() ?? "none"}");
            }
            else
            {
                HandleFileTransferBridgeRecovered(
                    runtimeUnlockTrigger: "bridge_ready",
                    recoveredReason: "bridge_ready",
                    pendingLogEvent: "filetransfer_fallback_nkn_ready_unproven",
                    pendingLogReason: "bridge_ready_waiting_for_receive_proof",
                    probeTrigger: "bridge_ready_unproven");
            }
        }

        if (e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryCompleted)
        {
            MarkFileTransferTunaActivationBridgeRecoverySettled("receive_stall_recovery_completed");
            Volatile.Write(ref bridgeReceiveStallRecoveryActive, 0);
            HandleFileTransferBridgeRecovered(
                runtimeUnlockTrigger: "receive_stall_recovery_completed",
                recoveredReason: "receive_stall_recovery_completed",
                pendingLogEvent: "filetransfer_fallback_nkn_recovery_completed_unproven",
                pendingLogReason: "receive_stall_recovery_completed_waiting_for_receive_proof",
                probeTrigger: "receive_stall_recovery_completed_unproven");
        }

        if (e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed)
        {
            RaiseBridgeReceiveStallSessionLivenessProof(e);
            MarkFileTransferTunaActivationBridgeRecoverySettled("receive_resumed");
            Volatile.Write(ref bridgeReceiveStallRecoveryActive, 0);
            HandleFileTransferBridgeRecovered(
                runtimeUnlockTrigger: "receive_resumed",
                recoveredReason: "receive_resumed",
                pendingLogEvent: "filetransfer_fallback_nkn_receive_resumed_unproven",
                pendingLogReason: "waiting_for_file_transfer_bulk_receive_proof",
                probeTrigger: "receive_resumed_unproven");
        }

        if (e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted)
        {
            Volatile.Write(ref bridgeReceiveStallRecoveryActive, 0);
            MarkFileTransferTunaActivationBridgeRecoverySettled("receive_stall_recovery_exhausted");
            var sessionId = SanitizeLogToken(currentSessionSecurityState.SessionId?.Value ?? "none");
            var reason = string.IsNullOrWhiteSpace(e.ExitReasonText)
                ? "control_receive_stalled_max_restarts"
                : SanitizeLogToken(e.ExitReasonText);
            InterruptRuntimeUnlockOfferForBridgeRecovery(
                "offer_interrupted_by_bridge_recovery",
                reason,
                "receive_stall_recovery_exhausted");
            if (HasActiveFileTransferDataSessionsForRecovery())
            {
                if (ShouldSuppressFileTransferControlReceiveStallRecoveryBroadcast(
                        reason,
                        out var suppressReason,
                        out var cooldownRemainingMs))
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=filetransfer_control_receive_stall_recovery_broadcast_suppressed; session_id={sessionId}; reason={reason}; suppress_reason={suppressReason}; cooldown_remaining_ms={cooldownRemainingMs}");
                    return;
                }

                MarkFileTransferControlReceiveStallRecoveryBroadcasted();
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=filetransfer_control_receive_stall_recovery_broadcast; session_id={sessionId}; reason={reason}; action=regular_nkn_recovery_epoch");
                MarkFileTransferFallbackNknProofPending(
                    reason: reason,
                    sessionId: currentSessionSecurityState.SessionId?.Value,
                    lanes: NknAccelerationLaneKind.File);
                SetFileTransferDataSessionsAvailability(
                    isAvailable: true,
                    reason: reason,
                    requiresResumeRequest: true,
                    handoffKind: FileTransferTransportHandoffKind.RegularNknRecovery,
                    targetTransport: FileTransferTransportKind.RegularNkn);
                ScheduleFileTransferFallbackNknProbeIfPending("receive_stall_recovery_exhausted");
                if (IsRuntimeUnlockActivationRecoveryFailure(reason))
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=filetransfer_activation_recovery_exhausted_session_disconnect_suppressed; session_id={sessionId}; reason={reason}; active_file_transfer_session=1");
                    BridgeLifecycle?.Invoke(
                        this,
                        e with
                        {
                            Kind = BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred,
                            ExitReasonText = $"reason=runtime_unlock_recovery_exhausted:stall={reason}:connect=core_filetransfer_request"
                        });
                    return;
                }

                BridgeLifecycle?.Invoke(this, e);
                return;
            }

            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=filetransfer_control_receive_stall_terminal_broadcast; session_id={sessionId}; reason={reason}");
            SetFileTransferDataSessionsAvailability(
                isAvailable: false,
                reason: reason,
                requiresResumeRequest: false,
                handoffKind: FileTransferTransportHandoffKind.RegularNknRecovery,
                targetTransport: FileTransferTransportKind.RegularNkn);
        }

        BridgeLifecycle?.Invoke(this, e);
    }

    private bool ShouldDeferBridgeProcessReadyUntilReceiveStallRecoveryCompletes(BridgeLifecycleEvent e)
    {
        if (Volatile.Read(ref bridgeReceiveStallRecoveryActive) == 0)
        {
            return false;
        }

        // Ready with a start mode is emitted by the local bridge hello/pong path.
        // During receive-stall recovery that only proves the child process is alive;
        // NKN control/bulk clients may still be reconnecting and cannot yet carry
        // post-Tuna fallback checkpoint/frontier proof.
        return e.StartMode.HasValue;
    }

    private void HandleFileTransferBridgeRecovered(
        string runtimeUnlockTrigger,
        string recoveredReason,
        string pendingLogEvent,
        string pendingLogReason,
        string probeTrigger)
    {
        ScheduleRuntimeUnlockRetryAfterRecoveryIfArmed(runtimeUnlockTrigger);
        if (IsFileTransferFallbackNknProofPending())
        {
            var sessionId = SanitizeLogToken(currentSessionSecurityState.SessionId?.Value ?? "none");
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event={pendingLogEvent}; session_id={sessionId}; reason={pendingLogReason}");
            SetFileTransferDataSessionsAvailability(
                isAvailable: false,
                reason: "transport_recovered_unproven",
                requiresResumeRequest: true,
                handoffKind: FileTransferTransportHandoffKind.RegularNknRecovery,
                targetTransport: FileTransferTransportKind.RegularNkn);
            ScheduleFileTransferFallbackNknProbeIfPending(probeTrigger);
            return;
        }

        if (!ShouldSuppressFileTransferTransportRecoveredForTunaActivationPause(recoveredReason, out _))
        {
            SetFileTransferDataSessionsAvailability(
                isAvailable: true,
                reason: "transport_recovered",
                requiresResumeRequest: false,
                handoffKind: FileTransferTransportHandoffKind.None,
                targetTransport: FileTransferTransportKind.RegularNkn);
        }
    }

    private void RaiseBridgeReceiveStallSessionLivenessProof(BridgeLifecycleEvent e)
    {
        if (e.TotalMessagesReceivedSinceLast <= 0)
        {
            return;
        }

        var sessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var lane =
            e.ControlMessagesReceivedSinceLast > 0 ? "bridge_control" :
            e.BulkMessagesReceivedSinceLast > 0 ? "bridge_bulk" :
            e.MediaMessagesReceivedSinceLast > 0 ? "bridge_media" :
            "bridge";
        RaiseSessionLivenessProof(
            sessionId,
            generation: 0,
            Interlocked.Increment(ref bridgeReceiveStallLivenessProofSequence),
            "bridge_receive_stall_recovery_receive_resumed",
            lane);
    }

    private static bool IsRuntimeUnlockActivationRecoveryFailure(string reason)
        => !string.IsNullOrWhiteSpace(reason) &&
           reason.Contains("tuna_activation_offer_send_timeout", StringComparison.OrdinalIgnoreCase);

    private static bool IsPostTunaFallbackReceiveStallRecoveryReason(string? reason)
        => !string.IsNullOrWhiteSpace(reason) &&
           reason.Trim().StartsWith("post_tuna_fallback", StringComparison.OrdinalIgnoreCase);

    private bool ShouldDeferPostTunaFallbackReceiveStallRecoveryStart(
        string? reason,
        string? sessionId,
        FileTransferTransportHandoffKind handoffKind)
    {
        if (IsPostTunaFallbackReceiveStallRecoveryReason(reason) ||
            handoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback ||
            IsFileTransferFallbackNknProofPending())
        {
            return true;
        }

        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? currentSessionSecurityState.SessionId?.Value
            : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return false;
        }

        lock (accelerationGate)
        {
            return TryGetCurrentTunaFallbackProofStateUnsafe(normalizedSessionId, out var state) &&
                   (state.Lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File &&
                   state.FileState is TunaFallbackLaneState.Pending or
                       TunaFallbackLaneState.WaitingForRegularNkn or
                       TunaFallbackLaneState.MediaReady;
        }
    }

    private void OnSecureScreenShareFrameReady(object? sender, ScreenShareVideoFrameReadyEventArgs e)
    {
        if (!TryValidateScreenShareSession("frame", e.SessionId) ||
            !IsScreenShareAuthorizedForDispatch("frame", e.SessionId))
        {
            return;
        }

        try
        {
            MarkScreenTunaHandoffFrameApplied(e);
            var metrics = secureScreenShareFrameReassembler.GetMetricsSnapshot();
            NknRuntimeDiagnostics.SetMediaPlaneFramesDroppedForFreshness(metrics.FramesDropped);
            ScreenShareFrameCompleted?.Invoke(
                this,
                new ScreenShareFrameCompletedEventArgs(
                    e.FrameId,
                    e.Width,
                    e.Height,
                    e.Encoding,
                    e.EncodedFrameBytes,
                    e.CapturedTsUtcMs,
                    ChunksDroppedOlderFrame: metrics.FramesDropped,
                    AssembliesExpired: secureScreenShareFrameReassembler.AssembliesExpired,
                    SessionId: e.SessionId,
                    IsKeyFrame: e.IsKeyFrame,
                    StreamEpoch: e.StreamEpoch,
                    StreamConfig: e.StreamConfig,
                    RecoveryDeliveryClass: e.RecoveryDeliveryClass,
                    FrameReadyObservedUtcMs: e.FrameReadyObservedUtcMs));
        }
        catch (Exception ex)
        {
            Log($"ScreenShareFrameCompleted dispatch failed (source=secure_envelope, ex={ex.GetType().Name})");
        }
    }

    private void OnSecureScreenShareKeyframeRequested(object? sender, ScreenShareVideoKeyframeRequestV1 e)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await SendScreenShareVideoKeyframeRequestAsync(e, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"ScreenShare keyframe request send failed (reason={e.Reason}, ex={ex.GetType().Name})");
                }
            });
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        ResetScreenShareBridgePolicyState();
        SetFileTransferDataSessionsAvailability(
            isAvailable: false,
            reason: "transport_disconnected",
            requiresResumeRequest: true,
            handoffKind: FileTransferTransportHandoffKind.RegularNknRecovery,
            targetTransport: FileTransferTransportKind.RegularNkn);
        NknRuntimeDiagnostics.SetLastError("nkn_client_disconnected");
        UpdateSessionSecurityState(currentSessionSecurityState.Invalidate("transport_disconnected"));
        Log("Client disconnected");
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private string? ResolveExpectedRemotePeerAddressForCurrentSession()
    {
        var localAddress = LocalPeerAddress;

        if (!string.IsNullOrWhiteSpace(remoteEndpoint) &&
            !AddressesLikelySamePeer(remoteEndpoint, localAddress))
        {
            return remoteEndpoint;
        }

        if (currentSessionSecurityState.HelperAddress is PeerAddress helperAddress &&
            !AddressesLikelySamePeer(helperAddress.Value, localAddress))
        {
            return helperAddress.Value;
        }

        if (currentSessionSecurityState.HelpeeAddress is PeerAddress helpeeAddress &&
            !AddressesLikelySamePeer(helpeeAddress.Value, localAddress))
        {
            return helpeeAddress.Value;
        }

        return null;
    }

    private string? ResolveExpectedRemoteMediaPeerAddressForCurrentSession()
    {
        return string.IsNullOrWhiteSpace(remoteMediaEndpoint)
            ? ResolveExpectedRemotePeerAddressForCurrentSession()
            : remoteMediaEndpoint;
    }

    private string? ResolveExpectedRemoteBulkPeerAddressForCurrentSession()
    {
        return string.IsNullOrWhiteSpace(remoteBulkEndpoint)
            ? ResolveExpectedRemotePeerAddressForCurrentSession()
            : remoteBulkEndpoint;
    }

    private PeerAddress? TryResolveExpectedRemotePeerAddressForLifecycle()
    {
        var expected = ResolveExpectedRemotePeerAddressForCurrentSession();
        return PeerAddress.TryParse(expected, out var peerAddress) ? peerAddress : null;
    }

    private bool TryValidateScreenShareSession(string messageType, string? messageSessionId)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            failureReason = "session_unavailable";
        }
        else if (!string.Equals(normalizedMessageSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else
        {
            return true;
        }

        LogScreenShareRejected(messageType, failureReason, normalizedMessageSessionId);
        return false;
    }

    private bool IsScreenShareAuthorizedForDispatch(string messageType, string? messageSessionId)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        string failureReason;

        if (!currentSessionSecurityState.InviteValidated)
        {
            failureReason = "invite_not_validated";
        }
        else if (!currentSessionSecurityState.HandshakeCompleted ||
                 currentSessionSecurityState.HandshakeState != SessionHandshakeState.Verified)
        {
            failureReason = "handshake_not_verified";
        }
        else if (!currentSessionSecurityState.HasCapability(CapabilityGrant.ScreenShare, nowUtc))
        {
            failureReason = currentSessionSecurityState.ApprovalGranted ? "capability_missing" : "approval_missing";
        }
        else
        {
            return true;
        }

        LogScreenShareRejected(messageType, failureReason, messageSessionId);
        return false;
    }

    private void LogScreenShareRejected(string messageType, string reason, string? sessionId)
    {
        NknRuntimeDiagnostics.SetLastError($"screenshare_{reason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"screenshare_{reason}");
        if (string.Equals(messageType, "frame", StringComparison.Ordinal))
        {
            NknRuntimeDiagnostics.IncrementMediaPlanePolicyRejectCount();
            NknRuntimeDiagnostics.SetLastMediaPlaneRejectReason(reason);
        }

        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=screen_share_message_rejected; message_type={messageType}; reason={reason}; session_id={sessionId ?? "(none)"}; expected_session_id={currentSessionSecurityState.SessionId?.Value ?? "(none)"}; helper_identity={currentSessionSecurityState.HelperAddress?.Value ?? "(none)"}");
        Log($"Screen share message rejected (type={messageType}, reason={reason}, session_id={sessionId ?? "(none)"})");
    }

    private static bool TryParseScreenSharePayload(ReadOnlySpan<byte> payload, out string messageType, out string? messageSessionId)
    {
        if (ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(payload, out var fragments, out _) &&
            fragments.Length > 0)
        {
            messageType = "frame";
            messageSessionId = fragments[0].SessionId;
            return true;
        }

        if (ScreenSharePayloadCodec.TryDeserializeStop(payload, out var stop))
        {
            messageType = "stop";
            messageSessionId = stop.SessionId;
            return true;
        }

        messageType = "payload";
        messageSessionId = null;
        return false;
    }

    private ControlRequestMessageV1 EnsureControlSessionId(ControlRequestMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlResponseMessageV1 EnsureControlSessionId(ControlResponseMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlStartMessageV1 EnsureControlSessionId(ControlStartMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlStopMessageV1 EnsureControlSessionId(ControlStopMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlInputMessageV1 EnsureControlSessionId(ControlInputMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlInputAckV1 EnsureControlSessionId(ControlInputAckV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlStateSnapshotV1 EnsureControlSessionId(ControlStateSnapshotV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlDisplayInfoMessageV1 EnsureControlSessionId(ControlDisplayInfoMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ScreenSharePressureStateV1 EnsureScreenSharePressureStateSessionId(ScreenSharePressureStateV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ScreenShareVideoStreamConfigV1 EnsureScreenShareVideoStreamConfigSessionId(ScreenShareVideoStreamConfigV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ScreenShareVideoKeyframeRequestV1 EnsureScreenShareVideoKeyframeRequestSessionId(ScreenShareVideoKeyframeRequestV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ScreenShareRecoveryReceiptV1 EnsureScreenShareRecoveryReceiptSessionId(ScreenShareRecoveryReceiptV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ScreenShareCursorStateV1 EnsureScreenShareCursorStateSessionId(ScreenShareCursorStateV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };


    private string ResolveControlSessionId(string? current)
    {
        return string.IsNullOrWhiteSpace(current)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : current.Trim();
    }

    private sealed record QueuedScreenShareEnvelope(
        string Destination,
        byte[] Payload,
        string? SessionId,
        long Generation,
        TaskCompletionSource Completion,
        QueuedScreenShareEnvelopeMetadata? Metadata);

    private sealed record QueuedScreenShareEnvelopeMetadata(
        string SessionId,
        long StreamEpoch,
        long FrameId,
        long CapturedTsUtcMs,
        bool IsKeyFrame,
        string? RecoverySendRole,
        long RecoveryBurstToken);

    private byte[] CreateSecureChatPayload(byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetControlSessionSharedKeyOrThrow();
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.Chat,
            MessageType: MapSecureChatMessageType(),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundChatSecureSequence),
            RequestId: null);
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private bool TryDecryptChatPayload(
        string? source,
        Envelope env,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default!;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError("chat_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("chat_session_unavailable");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedSender) || !PeerAddress.TryParse(expectedSender, out var senderIdentity))
        {
            NknRuntimeDiagnostics.SetLastError("chat_expected_sender_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("chat_expected_sender_unavailable");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason=expected_sender_unavailable)");
            return false;
        }

        byte[] key;
        try
        {
            key = GetControlSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            NknRuntimeDiagnostics.SetLastError("chat_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("chat_session_key_unavailable");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                env.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.Chat,
                    MessageType: MapSecureChatMessageType(),
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError("chat_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("chat_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=chat_message_rejected; reason=secure_envelope_invalid; session_id={sessionId.Value}; source={source ?? "(none)"}; expected_source={expectedSender}; msg_id={env.MessageId}; ex={ex.GetType().Name}");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = inboundChatReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"chat_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"chat_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=chat_message_rejected; reason={replayReason}; session_id={securePayload.Metadata.SessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        RaiseSessionLivenessProof(
            securePayload.Metadata.SessionId.Value,
            generation: 0,
            securePayload.Metadata.Sequence,
            "chat_message",
            "control");
        return true;
    }
}
#pragma warning restore CS0067
