using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core;
using NLink.Core.Configuration;
using NLink.Core.Logging;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;

namespace NLink.Infra.Nkn;

internal sealed class RealNknClientAdapter : INknClient, IBridgeProcessRunner, IAuthoritativeConnectedAddressSource, IBridgeScreenShareQueueCapability
{
    private const int MaxPayloadBytes = BridgeBinaryProtocol.MaxPayloadBytes;
    private const int BridgeProtocolVersion = BridgeBinaryProtocol.ProtocolVersion;
    private static readonly TimeSpan CommandAckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScreenShareBridgeLogInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BridgeTrafficLogInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReceiveStallRecoveryCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReceiveStallActiveFileTransferExtendedRecoveryCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReceiveStallActiveFileTransferUnprovenRecoveryCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ActiveFileTransferRecoveryTombstoneTtl = TimeSpan.FromSeconds(90);
    private const int ReceiveStallActiveFileTransferHardRestartMinAttempt = 2;
    private const int ReceiveStallRequiredConsecutiveWindows = 3;
    private const int ReceiveStallFastRequiredConsecutiveWindows = 2;
    private const int ReceiveStallControlAgeThresholdMs = 8_000;
    private const int ReceiveStallBulkAgeThresholdMs = 6_000;
    private const int ReceiveStallControlOnlyActiveFileTransferGraceMs = 12_000;
    private const int ReceiveStallControlOnlyActiveFileTransferGraceWindows = 3;
    private const int ReceiveStallMaxRecoveriesPerSession = 4;
    private const int ReceiveStallMaxActiveFileTransferRecoveriesPerSession = 16;
    private const int FileTransferBulkPolicyLowThroughputWindowsToPromote = 2;
    private const int FileTransferBulkPolicyMinDemandFrames = 8;
    private const int FileTransferBulkPolicyPromotedConcurrency = 4;
    private const int FileTransferBulkPolicyMaxConcurrency = 8;
    private const string FileTransferBulkPolicyModeSingle = "single";
    private const string FileTransferBulkPolicyModeRoundRobin = "round_robin";
    private static readonly string[] RequiredBridgeChannels = ["control", "media", "bulk"];
    internal const string BridgeProtocolOutdatedBulkMissingCode = "bridge_protocol_outdated_bulk_missing";
    private static readonly RetryPolicyOptions UnexpectedExitRestartRetryOptions = new(
        MaxAttempts: 5,
        InitialDelay: TimeSpan.FromSeconds(1),
        MaxDelay: TimeSpan.FromSeconds(16),
        JitterRatio: 0d);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IdentityUsageLeases = new(StringComparer.OrdinalIgnoreCase);

    private readonly object gate = new();
    private readonly NknIdentity identity;
    private readonly NknTransportOptions options;
    private readonly BridgeSupervisor bridgeSupervisor;
    private readonly BridgeProtocolClient protocolClient;
    private readonly BridgeProtocolEventRouter protocolEventRouter;
    private readonly ConnectAttemptCoordinator connectAttempts = new();
    private TimeSpan? connectReadyTimeoutOverrideForTests;
    private CancellationTokenSource? pingLoopCts;
    private Task? pingLoopTask;
    private string address;
    private string mediaAddress;
    private string bulkAddress;
    private int disconnectedRaised;
    private int unexpectedRestartLoopActive;
    private bool helloCompleted;
    private bool shuttingDown;
    private bool disposed;
    private string reliabilityModeHint = "Helper";
    private long screenShareBridgeMessageCountSinceLastLog;
    private long screenShareBridgePayloadBytesSinceLastLog;
    private long lastScreenShareBridgeSummaryLogTick;
    private long bridgeSendCountSinceLastLog;
    private long bridgeSendPayloadBytesSinceLastLog;
    private long lastBridgeSendSummaryLogTick;
    private long bridgeMessageCountSinceLastLog;
    private long bridgeMessagePayloadBytesSinceLastLog;
    private long lastBridgeMessageSummaryLogTick;
    private long bulkBridgeMessageCountSinceLastLog;
    private long bulkBridgeMessagePayloadBytesSinceLastLog;
    private long lastBulkBridgeMessageSummaryLogTick;
    private readonly InboundDeliveryCounters controlInboundDeliveryCounters = new();
    private readonly InboundDeliveryCounters mediaInboundDeliveryCounters = new();
    private readonly InboundDeliveryCounters bulkInboundDeliveryCounters = new();
    private string[] supportedBridgeChannels = [];
    private int? negotiatedBridgeProtocol;
    private string? bridgeAppVersion;
    private BridgeBundleIdentity? bridgeBundleIdentity;
    private int disposeStarted;
    private SemaphoreSlim? heldIdentityUsageLease;
    private readonly object screenShareQueueStateGate = new();
    private readonly object screenShareHealthGate = new();
    private BridgeScreenShareQueueState screenShareQueueState = new(
        QueueDepth: 0,
        QueuedBytes: 0,
        OldestQueuedAgeMs: 0,
        InFlight: false,
        DroppedSinceLast: 0,
        IsCongested: false,
        IsSevere: false,
        Mode: BridgeScreenShareQueueMode.Normal);
    private readonly Queue<DateTimeOffset> recentScreenShareHealthIssuesUtc = new();
    private TaskCompletionSource<long> screenShareQueueStateChangedTcs = CreateQueueStateChangedTcs();
    private long screenShareQueueStateVersion;
    private long lastScreenShareQueueWaitLogTick;
    private int receiveStallConsecutiveWindows;
    private int receiveStallBulkConsecutiveWindows;
    private int receiveStallControlConsecutiveWindows;
    private int receiveStallRecoveryInProgress;
    private int receiveStallRecoveryCount;
    private long receiveStallLastRecoveryStartedTick;
    private long receiveStallLastRecoveryCompletedTick;
    private int receiveStallRecoveryAwaitingReceiveProof;
    private int receiveStallRecoveryRequiresControlProof;
    private int receiveStallRecoveryRequiresBulkProof;
    private long receiveStallRecoveryLastUnprovenLogTick;
    private int receiveStallRecoveryConnectActive;
    private int activeFileTransferDataSessions;
    private readonly ConcurrentDictionary<string, byte> activeFileTransferRuntimeTransfers = new(StringComparer.Ordinal);
    private long activeFileTransferRecoveryTombstoneExpiresTick;
    private readonly object fileTransferBulkPolicyGate = new();
    private string? fileTransferBulkPolicyBaselineMode;
    private int fileTransferBulkPolicyBaselineConcurrency;
    private int fileTransferBulkPolicyLowThroughputWindows;
    private int fileTransferBulkPolicyAdaptiveActive;
    private int fileTransferBulkPolicyChangeInFlight;
    private long fileTransferBulkPolicyLastChangeTick;
    private bool suppressBridgeDisconnectDuringReceiveStallRecovery;
    private readonly object bulkQueueStateGate = new();
    private BridgeBulkQueueState bulkQueueState = new(
        QueueDepth: 0,
        QueuedBytes: 0,
        OldestQueuedAgeMs: 0,
        InFlight: false,
        InFlightCount: 0,
        InFlightBytes: 0,
        ConfiguredConcurrency: 1,
        EffectiveConcurrency: 1,
        ClearedSinceLast: 0,
        IsCongested: false,
        IsSevere: false);
    private TaskCompletionSource<long> bulkQueueStateChangedTcs = CreateQueueStateChangedTcs();
    private long bulkQueueStateVersion;
    private long lastBulkQueueWaitLogTick;
    private const int ScreenShareHealthSevereThreshold = 3;
    private static readonly TimeSpan ScreenShareHealthIssueWindow = TimeSpan.FromSeconds(8);

    private readonly record struct BridgeMediaTimingMetadata(
        long BridgeMessageObservedUtcMs,
        long SocketDataEventEmittedUtcMs,
        long WsReceiverWriteEnteredUtcMs,
        long WsMessageEmittedUtcMs,
        long SdkHandleMsgEnteredUtcMs,
        long ClientMessageDispatchUtcMs,
        long MultiClientMessageDispatchUtcMs);

    private sealed class InboundDeliveryCounters
    {
        public long MessageCount;
        public long PayloadBytes;
        public long SubscriberPresentCount;
        public long SubscriberMissingCount;
        public long HandlerFailureCount;
        public long TopicCount;
        public long SourceMatchesLocalControlCount;
        public long SourceMatchesLocalMediaCount;
        public long SourceMatchesLocalBulkCount;
        public long SourceMatchesAnyLocalCount;
        public long LastSummaryLogTick;
        public int LastSourceLength;
        public string LastSourceHash = "(none)";
    }

    internal event EventHandler<BridgeScreenShareQueueStateChangedEventArgs>? ScreenShareQueueStateChanged;

    public RealNknClientAdapter(NknIdentity identity, NknTransportOptions options)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        address = identity.Address;
        mediaAddress = BuildFallbackMediaAddress(identity.Identifier, identity.Address);
        bulkAddress = BuildFallbackBulkAddress(identity.Identifier, identity.Address);
        bridgeSupervisor = new BridgeSupervisor(
            callbacks: new BridgeSupervisorCallbacks
            {
                Log = Log,
                SignalDisconnected = SignalDisconnected,
                OnUnexpectedExitDetected = reason => _ = Task.Run(() => HandleUnexpectedProcessExitAsync(reason), CancellationToken.None),
                RecordBridgeFailure = RecordBridgeFailure,
                EmitBridgeLifecycle = EmitBridgeLifecycle,
                GetBridgeBundleIdentity = GetBridgeBundleIdentity,
            },
            resolveNodePath: ResolveNodeExecutablePath,
            resolveBridgePath: ResolveBridgeScriptPath,
            onStdoutJsonLineAsync: (line, _) =>
            {
                protocolClient!.HandleStdoutJsonLine(line);
                return Task.CompletedTask;
            },
            onStdoutBinaryFrameAsync: (frame, _) =>
            {
                HandleBinaryBridgeFrame(frame);
                return Task.CompletedTask;
            },
            onStderrLineAsync: (line, _, _, _) =>
            {
                if (ShouldSuppressBridgeStderrDuringShutdown(line))
                {
                    return Task.CompletedTask;
                }

                Log(BuildBridgeDiagnosticLogMessage("bridge stderr", line));
                RecordScreenShareTransportHealthIssueFromBridgeLine(line);
                return Task.CompletedTask;
            },
            getCleanupReasonPrefix: () => "bridge",
            isDisposed: () => disposed,
            isShuttingDown: () =>
            {
                lock (gate)
                {
                    return shuttingDown;
                }
            },
            getReliabilityModeHint: () =>
            {
                lock (gate)
                {
                    return reliabilityModeHint;
                }
            },
            getCurrentUptimeMs: () => bridgeSupervisor is null ? null : GetCurrentBridgeUptimeMs());

        protocolEventRouter = new BridgeProtocolEventRouter(
            identity.Address,
            BuildFallbackMediaAddress(identity.Identifier, identity.Address),
            BuildFallbackBulkAddress(identity.Identifier, identity.Address),
            connectAttempts,
            getCurrentPid: () => bridgeSupervisor.CurrentPid,
            setConnectedAddresses: (controlAddr, mediaAddr, bulkAddr) =>
            {
                lock (gate)
                {
                    address = controlAddr;
                    mediaAddress = string.IsNullOrWhiteSpace(mediaAddr)
                        ? BuildFallbackMediaAddress(identity.Identifier, controlAddr)
                        : mediaAddr;
                    bulkAddress = string.IsNullOrWhiteSpace(bulkAddr)
                        ? BuildFallbackBulkAddress(identity.Identifier, controlAddr)
                        : bulkAddr;
                }
            },
            log: Log);

        protocolClient = new BridgeProtocolClient(
            getWriter: () => bridgeSupervisor.GetActiveIoOrThrow().Writer,
            log: Log,
            onReady: root => protocolEventRouter.HandleReady(root),
            onRpcProgress: (eventName, root) => protocolEventRouter.HandleRpcProgress(eventName, root),
            onMessage: HandleMessage,
            onDisconnected: HandleBridgeDisconnected,
            onHelloOk: root => protocolEventRouter.HandleHelloOk(root),
            onPong: root => protocolEventRouter.HandlePong(root),
            onScreenShareQueueState: HandleScreenShareQueueState,
            onBridgeEventLoopSummary: HandleBridgeEventLoopSummary,
            onBridgeControlSendSummary: HandleBridgeControlSendSummary,
            onBridgeMediaSendSummary: HandleBridgeMediaSendSummary,
            onBridgeTransportHealthSummary: HandleBridgeTransportHealthSummary,
            onUnmatchedBridgeError: reason => SignalDisconnected("bridge_error:" + reason),
            onBulkQueueState: HandleBulkQueueState,
            onBridgeBulkSendSummary: HandleBridgeBulkSendSummary);
    }

    public string Address
    {
        get
        {
            lock (gate)
            {
                return address;
            }
        }
    }

    public string MediaAddress
    {
        get
        {
            lock (gate)
            {
                return mediaAddress;
            }
        }
    }

    public string BulkAddress
    {
        get
        {
            lock (gate)
            {
                return bulkAddress;
            }
        }
    }

    internal bool SupportsBulkBridgeChannel
    {
        get
        {
            lock (gate)
            {
                return supportedBridgeChannels.Any(value => string.Equals(value, "bulk", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public event EventHandler<NknIncomingMessage>? MessageReceived;

    public event EventHandler? Disconnected;
    internal event EventHandler<BridgeLifecycleEvent>? BridgeLifecycle;

    BridgeScreenShareQueueState IBridgeScreenShareQueueCapability.CurrentScreenShareQueueState
    {
        get
        {
            lock (screenShareQueueStateGate)
            {
                return screenShareQueueState;
            }
        }
    }

    BridgeScreenShareHealthState IBridgeScreenShareQueueCapability.CurrentScreenShareHealthState
    {
        get
        {
            lock (screenShareHealthGate)
            {
                PruneScreenShareHealthIssuesUnsafe(DateTimeOffset.UtcNow);
                return BuildCurrentScreenShareHealthStateUnsafe(DateTimeOffset.UtcNow);
            }
        }
    }

    bool IBridgeScreenShareQueueCapability.IsBridgeProcessRunning => bridgeSupervisor.IsProcessRunning;

    bool IBridgeProcessRunner.WasForcedKillRequested => bridgeSupervisor.WasForcedKillRequested;

    internal BridgeProcessDebugState GetDebugStateForTests()
    {
        return bridgeSupervisor.GetDebugStateForTests();
    }

    internal static bool TryCleanupTrackedNodeProcessForTests(int pid, long startTimeUtcFileTime)
    {
        return BridgeSupervisor.TryCleanupTrackedNodeProcessByPidForTests(pid, startTimeUtcFileTime);
    }

    internal void SetReliabilityModeHint(string mode)
    {
        lock (gate)
        {
            reliabilityModeHint = string.Equals(mode, "Helpee", StringComparison.OrdinalIgnoreCase) ? "Helpee" : "Helper";
        }
    }

    internal void RegisterActiveFileTransferDataSession(string transferId)
    {
        var activeCount = Interlocked.Increment(ref activeFileTransferDataSessions);
        Volatile.Write(ref activeFileTransferRecoveryTombstoneExpiresTick, 0);
        NknRuntimeDiagnostics.SetActiveFileTransferTombstones(0);
        Log($"event=filetransfer_v4_receive_liveness_summary; reason=data_session_opened; transfer_id={SanitizeLogToken(transferId)}; active_file_transfer_sessions={activeCount}");
    }

    internal void RegisterActiveFileTransferRuntime(string transferId)
    {
        var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? "(unknown)" : transferId.Trim();
        if (!activeFileTransferRuntimeTransfers.TryAdd(normalizedTransferId, 0))
        {
            return;
        }

        Volatile.Write(ref activeFileTransferRecoveryTombstoneExpiresTick, 0);
        NknRuntimeDiagnostics.SetActiveFileTransferTombstones(0);
        Log(
            "event=filetransfer_active_runtime_registered; " +
            $"transfer_id={SanitizeLogToken(normalizedTransferId)}; active_file_transfer_runtime_sessions={activeFileTransferRuntimeTransfers.Count}; active_file_transfer_data_sessions={Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions))}");
    }

    internal void UnregisterActiveFileTransferRuntime(string transferId)
    {
        var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? "(unknown)" : transferId.Trim();
        if (!activeFileTransferRuntimeTransfers.TryRemove(normalizedTransferId, out _))
        {
            return;
        }

        if (activeFileTransferRuntimeTransfers.IsEmpty && Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions)) == 0)
        {
            Volatile.Write(ref activeFileTransferRecoveryTombstoneExpiresTick, 0);
            NknRuntimeDiagnostics.SetActiveFileTransferTombstones(0);
        }

        Log(
            "event=filetransfer_active_runtime_unregistered; " +
            $"transfer_id={SanitizeLogToken(normalizedTransferId)}; active_file_transfer_runtime_sessions={activeFileTransferRuntimeTransfers.Count}; active_file_transfer_data_sessions={Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions))}");
        MaybeResetFileTransferBulkPolicyIfIdle("runtime_unregistered");
    }

    internal void ClearActiveFileTransferRuntimeTransfers(string reason)
    {
        if (activeFileTransferRuntimeTransfers.IsEmpty)
        {
            return;
        }

        var clearedCount = activeFileTransferRuntimeTransfers.Count;
        activeFileTransferRuntimeTransfers.Clear();
        if (Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions)) == 0)
        {
            Volatile.Write(ref activeFileTransferRecoveryTombstoneExpiresTick, 0);
            NknRuntimeDiagnostics.SetActiveFileTransferTombstones(0);
        }

        Log(
            "event=filetransfer_active_runtime_cleared; " +
            $"reason={SanitizeLogToken(reason)}; cleared_count={clearedCount}; active_file_transfer_data_sessions={Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions))}");
        MaybeResetFileTransferBulkPolicyIfIdle("runtime_cleared");
    }

    internal bool RequestFileTransferReceiveStallRecovery(string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "core_filetransfer_receive_recovery"
            : SanitizeLogToken(reason);
        if (!options.ReceiveStallRecoveryEnabled)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_request_ignored; reason=recovery_disabled; " +
                $"requested_reason={normalizedReason}");
            return false;
        }

        if (disposed || shuttingDown)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_request_ignored; reason=adapter_stopping; " +
                $"requested_reason={normalizedReason}");
            return false;
        }

        var activeDataSessionCount = Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions));
        var activeRuntimeCount = activeFileTransferRuntimeTransfers.Count;
        if (activeDataSessionCount <= 0 && activeRuntimeCount <= 0)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_request_ignored; reason=no_active_file_transfer; " +
                $"requested_reason={normalizedReason}; active_file_transfer_sessions={activeDataSessionCount}; active_file_transfer_runtime_sessions={activeRuntimeCount}");
            return false;
        }

        if (Interlocked.CompareExchange(ref receiveStallRecoveryInProgress, 1, 0) != 0)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_request_ignored; reason=recovery_already_in_progress; " +
                $"requested_reason={normalizedReason}; active_file_transfer_sessions={activeDataSessionCount}; active_file_transfer_runtime_sessions={activeRuntimeCount}");
            return false;
        }

        var nowTick = Stopwatch.GetTimestamp();
        var lastRecoveryTick = Volatile.Read(ref receiveStallLastRecoveryStartedTick);
        var requestCooldown = Volatile.Read(ref receiveStallRecoveryAwaitingReceiveProof) != 0
            ? ReceiveStallActiveFileTransferUnprovenRecoveryCooldown
            : ReceiveStallActiveFileTransferExtendedRecoveryCooldown;
        if (lastRecoveryTick > 0 &&
            Stopwatch.GetElapsedTime(lastRecoveryTick, nowTick) < requestCooldown)
        {
            Interlocked.Exchange(ref receiveStallRecoveryInProgress, 0);
            var cooldownRemainingMs = Math.Max(
                0,
                (long)(requestCooldown - Stopwatch.GetElapsedTime(lastRecoveryTick, nowTick)).TotalMilliseconds);
            Log(
                "event=nkn_bridge_receive_stall_recovery_request_ignored; reason=active_filetransfer_cooldown; " +
                $"requested_reason={normalizedReason}; cooldown_remaining_ms={cooldownRemainingMs}; active_file_transfer_sessions={activeDataSessionCount}; active_file_transfer_runtime_sessions={activeRuntimeCount}");
            return false;
        }

        Volatile.Write(ref receiveStallLastRecoveryStartedTick, nowTick);
        var attempt = Interlocked.Increment(ref receiveStallRecoveryCount);
        Volatile.Write(ref receiveStallRecoveryRequiresControlProof, 1);
        Volatile.Write(ref receiveStallRecoveryRequiresBulkProof, 0);
        Log(
            "event=nkn_bridge_receive_stall_recovery_requested; reason=core_filetransfer_request; " +
            $"requested_reason={normalizedReason}; attempt={attempt}; active_file_transfer_sessions={activeDataSessionCount}; active_file_transfer_runtime_sessions={activeRuntimeCount}");
        _ = Task.Run(
            () => RecoverFromReceiveStallAsync(
                "core_filetransfer_request",
                normalizedReason,
                attempt,
                0,
                0,
                -1,
                -1,
                -1),
            CancellationToken.None);
        return true;
    }

    internal void UnregisterActiveFileTransferDataSession(string transferId)
    {
        var activeCount = Interlocked.Decrement(ref activeFileTransferDataSessions);
        if (activeCount < 0)
        {
            activeCount = 0;
            Interlocked.Exchange(ref activeFileTransferDataSessions, 0);
        }

        if (activeCount == 0 && activeFileTransferRuntimeTransfers.IsEmpty)
        {
            var expiresTick = AddStopwatchDuration(Stopwatch.GetTimestamp(), ActiveFileTransferRecoveryTombstoneTtl);
            Volatile.Write(ref activeFileTransferRecoveryTombstoneExpiresTick, expiresTick);
            NknRuntimeDiagnostics.SetActiveFileTransferTombstones(1);
            Log(
                "event=filetransfer_active_recovery_tombstone_started; " +
                $"transfer_id={SanitizeLogToken(transferId)}; ttl_ms={(long)ActiveFileTransferRecoveryTombstoneTtl.TotalMilliseconds}");
        }
        else
        {
            Volatile.Write(ref activeFileTransferRecoveryTombstoneExpiresTick, 0);
            NknRuntimeDiagnostics.SetActiveFileTransferTombstones(0);
        }

        Log($"event=filetransfer_v4_receive_liveness_summary; reason=data_session_closed; transfer_id={SanitizeLogToken(transferId)}; active_file_transfer_sessions={activeCount}");
        MaybeResetFileTransferBulkPolicyIfIdle("data_session_closed");
    }

    private int GetActiveFileTransferSessionCountForPingRecovery()
    {
        var activeCount = Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions));
        if (activeCount > 0)
        {
            return activeCount;
        }

        var activeRuntimeCount = activeFileTransferRuntimeTransfers.Count;
        if (activeRuntimeCount > 0)
        {
            Log(
                "event=filetransfer_active_runtime_used; " +
                $"reason=bridge_ping_timeout_recovery; active_file_transfer_runtime_sessions={activeRuntimeCount}");
            return activeRuntimeCount;
        }

        if (!IsActiveFileTransferRecoveryTombstoneFresh())
        {
            return 0;
        }

        var recoveryKnown =
            Volatile.Read(ref receiveStallRecoveryAwaitingReceiveProof) != 0 ||
            Volatile.Read(ref receiveStallRecoveryInProgress) != 0 ||
            Volatile.Read(ref receiveStallRecoveryCount) > 0;
        if (!recoveryKnown)
        {
            return 0;
        }

        Log(
            "event=filetransfer_active_recovery_tombstone_used; " +
            $"reason=bridge_ping_timeout_recovery; ttl_ms={(long)ActiveFileTransferRecoveryTombstoneTtl.TotalMilliseconds}");
        return 1;
    }

    private bool IsActiveFileTransferRecoveryTombstoneFresh()
    {
        var expiresTick = Volatile.Read(ref activeFileTransferRecoveryTombstoneExpiresTick);
        if (expiresTick <= 0)
        {
            return false;
        }

        if (Stopwatch.GetTimestamp() <= expiresTick)
        {
            return true;
        }

        if (Interlocked.CompareExchange(ref activeFileTransferRecoveryTombstoneExpiresTick, 0, expiresTick) == expiresTick)
        {
            NknRuntimeDiagnostics.SetActiveFileTransferTombstones(0);
            Log("event=filetransfer_active_recovery_tombstone_expired");
        }

        return false;
    }

    private static long AddStopwatchDuration(long startTick, TimeSpan duration)
    {
        var deltaTicks = Math.Max(1L, (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency));
        return startTick + deltaTicks;
    }

    internal void SetConnectReadyTimeoutForTests(TimeSpan timeout)
    {
        connectReadyTimeoutOverrideForTests = timeout <= TimeSpan.Zero ? null : timeout;
    }

    internal void HandleStdoutJsonLineForTests(string line)
    {
        protocolClient.HandleStdoutJsonLine(line);
    }

    internal BridgeScreenShareQueueState GetScreenShareQueueStateForTests()
    {
        lock (screenShareQueueStateGate)
        {
            return screenShareQueueState;
        }
    }

    internal BridgeBulkQueueState GetBulkQueueStateForTests()
    {
        lock (bulkQueueStateGate)
        {
            return bulkQueueState;
        }
    }

    internal void HandleBinaryBridgeFrameForTests(BridgeBinaryFrame frame)
    {
        HandleBinaryBridgeFrame(frame);
    }

    internal async Task StartBridgeAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        try
        {
            await EnsureProcessStartedAsync(ct);
            await EnsureHelloHandshakeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var existing = NknRuntimeDiagnostics.Snapshot().LastError;
            if (string.IsNullOrWhiteSpace(existing) ||
                !existing.StartsWith("NKN_START_FAILED:", StringComparison.Ordinal))
            {
                SetNknStartFailed("bridge_start", ex.Message);
            }
            throw;
        }
    }

    internal Task PingBridgeAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        return SendBridgePingAndWaitPongAsync(PingTimeout, ct);
    }

    internal bool IsBridgeProcessRunning
    {
        get
        {
            return bridgeSupervisor.IsProcessRunning;
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        await AcquireIdentityUsageLeaseAsync(ct).ConfigureAwait(false);
        var connectTask = connectAttempts.GetOrCreateConnectTask(
            bridgeSupervisor.IsProcessRunning,
            sequence => ConnectCoreAsync(sequence, ct));
        await connectTask.WaitAsync(ct);
    }

    private async Task ConnectCoreAsync(long sequence, CancellationToken ct)
    {
        TaskCompletionSource<BridgeReadyInfo> readyWait;
        string connectId = Guid.NewGuid().ToString("N");

        try
        {
            try
            {
                await EnsureProcessStartedAsync(ct);
                await EnsureHelloHandshakeAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var existing = NknRuntimeDiagnostics.Snapshot().LastError;
                if (string.IsNullOrWhiteSpace(existing) ||
                    !existing.StartsWith("NKN_START_FAILED:", StringComparison.Ordinal))
                {
                    SetNknStartFailed("bridge_start", ex.Message);
                }
                throw;
            }

            readyWait = connectAttempts.RegisterPendingReady(connectId);

            var seedBase64 = NknIdentityStore.ReadSeedBase64ForConnect(options.KeyPath);

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["identifier"] = identity.Identifier,
                ["seedBase64"] = seedBase64,
                ["seedRpc"] = string.IsNullOrWhiteSpace(options.SeedRpc) ? null : options.SeedRpc,
                ["connectId"] = connectId,
            };

            if (options.ShouldSendSubClientTopology)
            {
                payload["numSubClients"] = options.NumSubClients;
                payload["mediaNumSubClients"] = options.MediaNumSubClients;
                payload["bulkNumSubClients"] = options.BulkNumSubClients;
                payload["bulkSendConcurrency"] = options.BulkSendConcurrency;
            }

            if (options.PreflightRpcEnabled)
            {
                payload["preflightRpcEnabled"] = true;
                payload["preflightTimeoutMs"] = options.PreflightTimeoutMs;
                payload["preflightConcurrency"] = options.PreflightConcurrency;
                payload["preflightCacheTtlMs"] = options.PreflightCacheTtlMs;
            }

            if (Volatile.Read(ref receiveStallRecoveryConnectActive) != 0)
            {
                payload["fallbackDelayMs"] = options.ReceiveStallRecoveryFallbackDelayMs;
            }

            await SendCommandAndWaitAckAsync("connect", payload, ct, timeoutOverride: CommandAckTimeout);

            BridgeReadyInfo readyInfo;
            try
            {
                readyInfo = await readyWait.Task.WaitAsync(connectReadyTimeoutOverrideForTests ?? ConnectReadyTimeout, ct);
            }
            catch (TimeoutException ex)
            {
                var progressSuffix = BuildLastProgressSummaryForDiagnostics();
                NknRuntimeDiagnostics.SetLastError($"bridge_connect_ready_timeout{progressSuffix}");
                RecordBridgeFailure("bridge_connect_ready_timeout", $"The local helper process did not become ready.{progressSuffix}");
                throw new TimeoutException("Timed out waiting for NKN bridge ready(address) after connect.", ex);
            }

            ValidateBridgeCapabilitiesOrThrow(readyInfo);

            lock (gate)
            {
                address = string.IsNullOrWhiteSpace(readyInfo.ControlAddress) ? identity.Address : readyInfo.ControlAddress;
                supportedBridgeChannels = readyInfo.SupportedChannels.Length == 0
                    ? []
                    : readyInfo.SupportedChannels
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Select(static value => value.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                negotiatedBridgeProtocol = readyInfo.Protocol;
                bridgeAppVersion = string.IsNullOrWhiteSpace(readyInfo.BridgeAppVersion) ? null : readyInfo.BridgeAppVersion;
                if (string.IsNullOrWhiteSpace(mediaAddress))
                {
                    mediaAddress = BuildFallbackMediaAddress(identity.Identifier, address);
                }

                if (string.IsNullOrWhiteSpace(bulkAddress))
                {
                    bulkAddress = BuildFallbackBulkAddress(identity.Identifier, address);
                }
            }

            NknRuntimeDiagnostics.SetMediaPlaneAttached(
                supportedBridgeChannels.Any(value => string.Equals(value, "media", StringComparison.OrdinalIgnoreCase)));

            StartPingLoopIfNeeded();
            var channelsSummary = supportedBridgeChannels.Length == 0
                ? "(none)"
                : string.Join(",", supportedBridgeChannels.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));
            Log(
                "Connected bridge " +
                $"(control_address_len={Address.Length}, media_address_len={MediaAddress.Length}, bulk_address_len={BulkAddress.Length}, " +
                $"channels={channelsSummary}, protocol={(negotiatedBridgeProtocol?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)")}, " +
                $"bridge_app_version={(bridgeAppVersion ?? "(none)")})");
        }
        finally
        {
            connectAttempts.CompleteAttempt(sequence, connectId);
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (disposed)
            {
                return;
            }

            lock (gate)
            {
                if (!bridgeSupervisor.IsProcessRunning)
                {
                    return;
                }

                shuttingDown = true;
            }

            try
            {
                await StopPingLoopAsync();
                await bridgeSupervisor.RequestShutdownAndCleanupAsync(
                    sendShutdownAsync: shutdownCt => SendCommandAndWaitAckAsync(
                        "shutdown",
                        payload: null,
                        shutdownCt,
                        timeoutOverride: CommandAckTimeout),
                    CancellationToken.None,
                    shutdownReason: "disconnect").ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    shuttingDown = false;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref receiveStallRecoveryAwaitingReceiveProof, 0);
            Interlocked.Exchange(ref receiveStallConsecutiveWindows, 0);
            NknRuntimeDiagnostics.SetMediaPlaneAttached(false);
            ReleaseIdentityUsageLease();
        }
    }

    bool IAuthoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress => connectAttempts.WasConnected();

    public Task SubscribeAsync(string topic, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        Log($"Bridge subscribe (topic_len={topic.Length})");
        return SendCommandAndWaitAckAsync("subscribe", new Dictionary<string, object?> { ["topic"] = topic }, ct, timeoutOverride: CommandAckTimeout);
    }

    public Task UnsubscribeAsync(string topic)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            return Task.CompletedTask;
        }

        Log($"Bridge unsubscribe (topic_len={topic.Length})");
        return SendCommandAndWaitAckAsync(
            "unsubscribe",
            new Dictionary<string, object?> { ["topic"] = topic },
            CancellationToken.None,
            timeoutOverride: CommandAckTimeout);
    }

    public Task PublishAsync(string topic, byte[] payload, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);
        EnsurePayloadWithinLimit(payload, "publish");

        Log($"Bridge publish (topic_len={topic.Length}, payload_len={payload.Length})");
        return SendCommandAndWaitAckAsync(
            "publish",
            new Dictionary<string, object?>
            {
                ["topic"] = topic,
                ["payloadBase64"] = Convert.ToBase64String(payload),
            },
            ct,
            timeoutOverride: CommandAckTimeout);
    }

    public Task SendAsync(string destination, byte[] payload, CancellationToken ct)
    {
        return SendCoreAsync(destination, payload, NknBridgeChannel.Control, ct);
    }

    public async Task SendMediaAsync(string destination, byte[] payload, CancellationToken ct)
    {
        await WaitWhileScreenShareQueueSeverelyCongestedAsync(ct).ConfigureAwait(false);
        await SendCoreAsync(destination, payload, NknBridgeChannel.Media, ct).ConfigureAwait(false);
    }

    public Task SendBulkAsync(string destination, byte[] payload, CancellationToken ct)
    {
        return SendBulkCoreAsync(destination, payload, ct);
    }

    private async Task SendBulkCoreAsync(string destination, byte[] payload, CancellationToken ct)
    {
        await WaitWhileBulkQueueSeverelyCongestedAsync(ct).ConfigureAwait(false);
        await SendCoreAsync(destination, payload, NknBridgeChannel.Bulk, ct).ConfigureAwait(false);
    }

    public Task SetScreenSharePolicyAsync(BridgeScreenShareQueueMode mode, long generation, bool flushQueued, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!bridgeSupervisor.IsProcessRunning)
        {
            return Task.CompletedTask;
        }

        var normalizedGeneration = Math.Max(0, generation);
        return SendCommandAndWaitAckAsync(
            "setScreenSharePolicy",
            new Dictionary<string, object?>
            {
                ["mode"] = mode == BridgeScreenShareQueueMode.CatchUpOnly ? "catch_up_only" : "normal",
                ["generation"] = normalizedGeneration,
                ["flushQueued"] = flushQueued,
            },
            ct,
            timeoutOverride: CommandAckTimeout);
    }

    private Task SetBulkSendPolicyAsync(string mode, int concurrency, string reason, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!bridgeSupervisor.IsProcessRunning)
        {
            return Task.CompletedTask;
        }

        var normalizedMode = NormalizeFileTransferBulkSendMode(mode);
        var normalizedConcurrency = Math.Clamp(concurrency, 1, FileTransferBulkPolicyMaxConcurrency);
        return SendCommandAndWaitAckAsync(
            "setBulkSendPolicy",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = normalizedMode,
                ["concurrency"] = normalizedConcurrency,
                ["reason"] = SanitizeLogToken(reason),
            },
            ct,
            timeoutOverride: CommandAckTimeout);
    }

    private async Task SendCoreAsync(string destination, byte[] payload, NknBridgeChannel channel, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(payload);
        EnsurePayloadWithinLimit(payload, "send");
        EnsureChannelSupported(channel);

        MaybeLogBridgeSendSummary(payload.Length, destination.Length);
        var isScreenSharePayload = channel == NknBridgeChannel.Media;
        var serializedBytes = BridgeBinaryProtocol.MeasureSendFrameBytes(destination, payload);
        await bridgeSupervisor.GetActiveIoOrThrow().Writer.WriteSendFrameAsync(destination, payload, channel, ct).ConfigureAwait(false);

        if (isScreenSharePayload)
        {
            NknRuntimeDiagnostics.IncrementBridgeMediaMessagesSent();
            NknRuntimeDiagnostics.AddBridgeMediaBytesSent(serializedBytes);
            NknRuntimeDiagnostics.AddScreenShareBridgeBytesSent(serializedBytes);
        }
        else if (channel == NknBridgeChannel.Control)
        {
            NknRuntimeDiagnostics.IncrementBridgeControlMessagesSent();
            NknRuntimeDiagnostics.AddBridgeControlBytesSent(serializedBytes);
        }
        else if (channel == NknBridgeChannel.Bulk)
        {
            NknRuntimeDiagnostics.AddOutboundLaneSent("file_transfer", payload.Length);
        }
    }

    private static TaskCompletionSource<long> CreateQueueStateChangedTcs()
    {
        return new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task WaitWhileScreenShareQueueSeverelyCongestedAsync(CancellationToken ct)
    {
        while (true)
        {
            BridgeScreenShareQueueState state;
            Task waitTask;
            lock (screenShareQueueStateGate)
            {
                state = screenShareQueueState;
                if (!state.IsSevere)
                {
                    return;
                }

                waitTask = screenShareQueueStateChangedTcs.Task;
            }

            NknRuntimeDiagnostics.IncrementOutboundLaneWaitCount("screenshare");
            MaybeLogScreenShareQueueWait(state);
            await waitTask.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task WaitWhileBulkQueueSeverelyCongestedAsync(CancellationToken ct)
    {
        while (true)
        {
            BridgeBulkQueueState state;
            Task waitTask;
            lock (bulkQueueStateGate)
            {
                state = bulkQueueState;
                if (!state.IsSevere)
                {
                    return;
                }

                waitTask = bulkQueueStateChangedTcs.Task;
            }

            NknRuntimeDiagnostics.IncrementOutboundLaneWaitCount("file_transfer");
            MaybeLogBulkQueueWait(state);
            await waitTask.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void MaybeLogScreenShareQueueWait(BridgeScreenShareQueueState state)
    {
        var nowTick = Stopwatch.GetTimestamp();
        var previousTick = Volatile.Read(ref lastScreenShareQueueWaitLogTick);
        if (previousTick != 0 &&
            Stopwatch.GetElapsedTime(previousTick, nowTick) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastScreenShareQueueWaitLogTick, nowTick, previousTick) != previousTick)
        {
            return;
        }

        Log(
            $"event=screenshare_bridge_queue_waiting; queue_depth={state.QueueDepth}; queued_bytes={state.QueuedBytes}; oldest_queued_age_ms={state.OldestQueuedAgeMs}; mode={FormatBridgeScreenShareQueueMode(state.Mode)}");
    }

    private void MaybeLogBulkQueueWait(BridgeBulkQueueState state)
    {
        var nowTick = Stopwatch.GetTimestamp();
        var previousTick = Volatile.Read(ref lastBulkQueueWaitLogTick);
        if (previousTick != 0 &&
            Stopwatch.GetElapsedTime(previousTick, nowTick) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastBulkQueueWaitLogTick, nowTick, previousTick) != previousTick)
        {
            return;
        }

        Log(
            $"event=nkn_bridge_bulk_queue_waiting; queue_depth={state.QueueDepth}; queued_bytes={state.QueuedBytes}; oldest_queued_age_ms={state.OldestQueuedAgeMs}; in_flight={state.InFlightCount}; in_flight_bytes={state.InFlightBytes}; configured_concurrency={state.ConfiguredConcurrency}; effective_concurrency={state.EffectiveConcurrency}");
    }

    private void HandleScreenShareQueueState(JsonElement root)
    {
        var queueDepth = TryGetInt32(root, "queueDepth", out var queueDepthValue) ? Math.Max(0, queueDepthValue) : 0;
        var queuedBytes = TryGetInt32(root, "queuedBytes", out var queuedBytesValue) ? Math.Max(0, queuedBytesValue) : 0;
        var oldestQueuedAgeMs = TryGetInt64(root, "oldestQueuedAgeMs", out var oldestQueuedAgeValue) ? Math.Max(0, oldestQueuedAgeValue) : 0;
        var droppedSinceLast = TryGetInt64(root, "droppedSinceLast", out var droppedSinceLastValue) ? Math.Max(0, droppedSinceLastValue) : 0;
        var inFlight = TryGetBool(root, "inFlight", out var inFlightValue) && inFlightValue;
        var isCongested = TryGetBool(root, "congested", out var congestedValue) && congestedValue;
        var isSevere = TryGetBool(root, "severe", out var severeValue) && severeValue;
        var mode = TryGetString(root, "mode", out var modeValue) &&
                   string.Equals(modeValue, "catch_up_only", StringComparison.OrdinalIgnoreCase)
            ? BridgeScreenShareQueueMode.CatchUpOnly
            : BridgeScreenShareQueueMode.Normal;

        SetScreenShareQueueState(new BridgeScreenShareQueueState(
            queueDepth,
            queuedBytes,
            oldestQueuedAgeMs,
            inFlight,
            droppedSinceLast,
            isCongested,
            isSevere,
            mode));
    }

    private void HandleBulkQueueState(JsonElement root)
    {
        var queueDepth = TryGetInt32(root, "queueDepth", out var queueDepthValue) ? Math.Max(0, queueDepthValue) : 0;
        var queuedBytes = TryGetInt64(root, "queuedBytes", out var queuedBytesValue) ? Math.Max(0, queuedBytesValue) : 0;
        var oldestQueuedAgeMs = TryGetInt64(root, "oldestQueuedAgeMs", out var oldestQueuedAgeValue) ? Math.Max(0, oldestQueuedAgeValue) : 0;
        var clearedSinceLast = TryGetInt64(root, "clearedSinceLast", out var clearedSinceLastValue) ? Math.Max(0, clearedSinceLastValue) : 0;
        var inFlightCount = TryGetInt64OrBool(root, "inFlight", out var inFlightValue) ? (int)Math.Min(int.MaxValue, Math.Max(0, inFlightValue)) : 0;
        var inFlight = inFlightCount > 0;
        var inFlightBytes = TryGetInt64(root, "inFlightBytes", out var inFlightBytesValue) ? Math.Max(0, inFlightBytesValue) : 0;
        var configuredConcurrency = TryGetInt32(root, "configuredConcurrency", out var configuredConcurrencyValue) ? Math.Max(0, configuredConcurrencyValue) : 1;
        var effectiveConcurrency = TryGetInt32(root, "effectiveConcurrency", out var effectiveConcurrencyValue) ? Math.Max(0, effectiveConcurrencyValue) : Math.Max(1, configuredConcurrency);
        var isCongested = TryGetBool(root, "congested", out var congestedValue) && congestedValue;
        var isSevere = TryGetBool(root, "severe", out var severeValue) && severeValue;

        SetBulkQueueState(new BridgeBulkQueueState(
            queueDepth,
            queuedBytes,
            oldestQueuedAgeMs,
            inFlight,
            inFlightCount,
            inFlightBytes,
            configuredConcurrency,
            effectiveConcurrency,
            clearedSinceLast,
            isCongested,
            isSevere));
    }

    private void HandleBridgeEventLoopSummary(JsonElement root)
    {
        var p95Ms = TryGetInt64(root, "event_loop_p95_ms", out var p95Value) ? Math.Max(0, p95Value) : 0;
        var maxMs = TryGetInt64(root, "event_loop_max_ms", out var maxValue) ? Math.Max(0, maxValue) : 0;
        var meanMs = TryGetInt64(root, "event_loop_mean_ms", out var meanValue) ? Math.Max(0, meanValue) : 0;
        var sampleWindowMs = TryGetInt64(root, "sample_window_ms", out var sampleWindowValue) ? Math.Max(0, sampleWindowValue) : 0;

        Log(
            $"event=screenshare_bridge_event_loop_summary; event_loop_p95_ms={p95Ms}; event_loop_max_ms={maxMs}; event_loop_mean_ms={meanMs}; sample_window_ms={sampleWindowMs}");
    }

    private void HandleBridgeControlSendSummary(JsonElement root)
    {
        var ingressP95Ms = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_p95_ms", out var ingressP95Value) ? ingressP95Value : -1;
        var ingressMaxMs = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_max_ms", out var ingressMaxValue) ? ingressMaxValue : -1;
        var queueP95Ms = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_p95_ms", out var queueP95Value) ? queueP95Value : -1;
        var queueMaxMs = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_max_ms", out var queueMaxValue) ? queueMaxValue : -1;
        var sendP95Ms = TryGetInt64(root, "send_p95_ms", out var sendP95Value) ? sendP95Value : -1;
        var sendMaxMs = TryGetInt64(root, "send_max_ms", out var sendMaxValue) ? sendMaxValue : -1;
        var framesSent = TryGetInt64(root, "frames_sent", out var framesSentValue) ? Math.Max(0, framesSentValue) : 0;
        var payloadBytesSent = TryGetInt64(root, "payload_bytes_sent", out var payloadBytesSentValue) ? Math.Max(0, payloadBytesSentValue) : 0;
        var payloadBytesPerSecond = TryGetInt64(root, "payload_bytes_per_second", out var payloadBytesPerSecondValue) ? Math.Max(0, payloadBytesPerSecondValue) : 0;
        var sendFailures = TryGetInt64(root, "send_failures", out var sendFailuresValue) ? Math.Max(0, sendFailuresValue) : 0;
        var queueClears = TryGetInt64(root, "queue_clears", out var queueClearsValue) ? Math.Max(0, queueClearsValue) : 0;
        var queueDepth = TryGetInt64(root, "queue_depth", out var queueDepthValue) ? Math.Max(0, queueDepthValue) : 0;
        var queuedBytes = TryGetInt64(root, "queued_bytes", out var queuedBytesValue) ? Math.Max(0, queuedBytesValue) : 0;
        var oldestQueuedAgeMs = TryGetInt64(root, "oldest_queued_age_ms", out var oldestQueuedAgeValue) ? Math.Max(0, oldestQueuedAgeValue) : 0;
        var inFlight = TryGetInt64(root, "in_flight", out var inFlightValue) ? Math.Max(0, inFlightValue) : 0;
        var sendTimeoutMs = TryGetInt64(root, "send_timeout_ms", out var sendTimeoutValue) ? Math.Max(0, sendTimeoutValue) : 0;
        var sampleWindowMs = TryGetInt64(root, "sample_window_ms", out var sampleWindowValue) ? Math.Max(0, sampleWindowValue) : 0;

        Log(
            "event=nkn_bridge_control_send_summary; " +
            $"binary_send_frame_observed_to_queue_enqueue_p95_ms={ingressP95Ms}; " +
            $"binary_send_frame_observed_to_queue_enqueue_max_ms={ingressMaxMs}; " +
            $"queue_enqueue_to_queue_dequeue_p95_ms={queueP95Ms}; " +
            $"queue_enqueue_to_queue_dequeue_max_ms={queueMaxMs}; " +
            $"send_p95_ms={sendP95Ms}; send_max_ms={sendMaxMs}; frames_sent={framesSent}; " +
            $"payload_bytes_sent={payloadBytesSent}; payload_bytes_per_second={payloadBytesPerSecond}; " +
            $"send_failures={sendFailures}; queue_clears={queueClears}; queue_depth={queueDepth}; queued_bytes={queuedBytes}; " +
            $"oldest_queued_age_ms={oldestQueuedAgeMs}; in_flight={inFlight}; send_timeout_ms={sendTimeoutMs}; sample_window_ms={sampleWindowMs}");
    }

    private void HandleBridgeMediaSendSummary(JsonElement root)
    {
        var binarySendFrameObservedToQueueEnqueueAvgMs = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_avg_ms", out var ingressAvgValue) ? ingressAvgValue : -1;
        var binarySendFrameObservedToQueueEnqueueMedianMs = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_median_ms", out var ingressMedianValue) ? ingressMedianValue : -1;
        var binarySendFrameObservedToQueueEnqueueP95Ms = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_p95_ms", out var ingressP95Value) ? ingressP95Value : -1;
        var binarySendFrameObservedToQueueEnqueueMaxMs = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_max_ms", out var ingressMaxValue) ? ingressMaxValue : -1;
        var queueEnqueueToQueueDequeueAvgMs = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_avg_ms", out var queueAvgValue) ? queueAvgValue : -1;
        var queueEnqueueToQueueDequeueMedianMs = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_median_ms", out var queueMedianValue) ? queueMedianValue : -1;
        var queueEnqueueToQueueDequeueP95Ms = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_p95_ms", out var queueP95Value) ? queueP95Value : -1;
        var queueEnqueueToQueueDequeueMaxMs = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_max_ms", out var queueMaxValue) ? queueMaxValue : -1;
        var queueDequeueToMediaSendStartedAvgMs = TryGetInt64(root, "queue_dequeue_to_media_send_started_avg_ms", out var startAvgValue) ? startAvgValue : -1;
        var queueDequeueToMediaSendStartedMedianMs = TryGetInt64(root, "queue_dequeue_to_media_send_started_median_ms", out var startMedianValue) ? startMedianValue : -1;
        var queueDequeueToMediaSendStartedP95Ms = TryGetInt64(root, "queue_dequeue_to_media_send_started_p95_ms", out var startP95Value) ? startP95Value : -1;
        var queueDequeueToMediaSendStartedMaxMs = TryGetInt64(root, "queue_dequeue_to_media_send_started_max_ms", out var startMaxValue) ? startMaxValue : -1;
        var mediaSendStartedToMediaSendResolvedAvgMs = TryGetInt64(root, "media_send_started_to_media_send_resolved_avg_ms", out var resolvedAvgValue) ? resolvedAvgValue : -1;
        var mediaSendStartedToMediaSendResolvedMedianMs = TryGetInt64(root, "media_send_started_to_media_send_resolved_median_ms", out var resolvedMedianValue) ? resolvedMedianValue : -1;
        var mediaSendStartedToMediaSendResolvedP95Ms = TryGetInt64(root, "media_send_started_to_media_send_resolved_p95_ms", out var resolvedP95Value) ? resolvedP95Value : -1;
        var mediaSendStartedToMediaSendResolvedMaxMs = TryGetInt64(root, "media_send_started_to_media_send_resolved_max_ms", out var resolvedMaxValue) ? resolvedMaxValue : -1;
        var framesSent = TryGetInt64(root, "frames_sent", out var framesSentValue) ? Math.Max(0, framesSentValue) : 0;
        var sendFailures = TryGetInt64(root, "send_failures", out var sendFailuresValue) ? Math.Max(0, sendFailuresValue) : 0;
        var queueDrops = TryGetInt64(root, "queue_drops", out var queueDropsValue) ? Math.Max(0, queueDropsValue) : 0;
        var queueDepth = TryGetInt64(root, "queue_depth", out var queueDepthValue) ? Math.Max(0, queueDepthValue) : 0;
        var oldestQueuedAgeMs = TryGetInt64(root, "oldest_queued_age_ms", out var oldestQueuedAgeValue) ? Math.Max(0, oldestQueuedAgeValue) : 0;
        var sampleWindowMs = TryGetInt64(root, "sample_window_ms", out var sampleWindowValue) ? Math.Max(0, sampleWindowValue) : 0;
        var queueMode = TryGetString(root, "queue_mode", out var queueModeValue) ? queueModeValue : "normal";

        Log(
            "event=screenshare_bridge_media_send_summary; " +
            $"binary_send_frame_observed_to_queue_enqueue_avg_ms={binarySendFrameObservedToQueueEnqueueAvgMs}; " +
            $"binary_send_frame_observed_to_queue_enqueue_median_ms={binarySendFrameObservedToQueueEnqueueMedianMs}; " +
            $"binary_send_frame_observed_to_queue_enqueue_p95_ms={binarySendFrameObservedToQueueEnqueueP95Ms}; " +
            $"binary_send_frame_observed_to_queue_enqueue_max_ms={binarySendFrameObservedToQueueEnqueueMaxMs}; " +
            $"sender_bridge_ingress_avg_ms={binarySendFrameObservedToQueueEnqueueAvgMs}; " +
            $"sender_bridge_ingress_median_ms={binarySendFrameObservedToQueueEnqueueMedianMs}; " +
            $"sender_bridge_ingress_p95_ms={binarySendFrameObservedToQueueEnqueueP95Ms}; " +
            $"sender_bridge_ingress_max_ms={binarySendFrameObservedToQueueEnqueueMaxMs}; " +
            $"queue_enqueue_to_queue_dequeue_avg_ms={queueEnqueueToQueueDequeueAvgMs}; " +
            $"queue_enqueue_to_queue_dequeue_median_ms={queueEnqueueToQueueDequeueMedianMs}; " +
            $"queue_enqueue_to_queue_dequeue_p95_ms={queueEnqueueToQueueDequeueP95Ms}; " +
            $"queue_enqueue_to_queue_dequeue_max_ms={queueEnqueueToQueueDequeueMaxMs}; " +
            $"sender_bridge_queue_avg_ms={queueEnqueueToQueueDequeueAvgMs}; " +
            $"sender_bridge_queue_median_ms={queueEnqueueToQueueDequeueMedianMs}; " +
            $"sender_bridge_queue_p95_ms={queueEnqueueToQueueDequeueP95Ms}; " +
            $"sender_bridge_queue_max_ms={queueEnqueueToQueueDequeueMaxMs}; " +
            $"queue_dequeue_to_media_send_started_avg_ms={queueDequeueToMediaSendStartedAvgMs}; " +
            $"queue_dequeue_to_media_send_started_median_ms={queueDequeueToMediaSendStartedMedianMs}; " +
            $"queue_dequeue_to_media_send_started_p95_ms={queueDequeueToMediaSendStartedP95Ms}; " +
            $"queue_dequeue_to_media_send_started_max_ms={queueDequeueToMediaSendStartedMaxMs}; " +
            $"sender_bridge_publish_setup_avg_ms={queueDequeueToMediaSendStartedAvgMs}; " +
            $"sender_bridge_publish_setup_median_ms={queueDequeueToMediaSendStartedMedianMs}; " +
            $"sender_bridge_publish_setup_p95_ms={queueDequeueToMediaSendStartedP95Ms}; " +
            $"sender_bridge_publish_setup_max_ms={queueDequeueToMediaSendStartedMaxMs}; " +
            $"media_send_started_to_media_send_resolved_avg_ms={mediaSendStartedToMediaSendResolvedAvgMs}; " +
            $"media_send_started_to_media_send_resolved_median_ms={mediaSendStartedToMediaSendResolvedMedianMs}; " +
            $"media_send_started_to_media_send_resolved_p95_ms={mediaSendStartedToMediaSendResolvedP95Ms}; " +
            $"media_send_started_to_media_send_resolved_max_ms={mediaSendStartedToMediaSendResolvedMaxMs}; " +
            $"sender_bridge_publish_resolved_avg_ms={mediaSendStartedToMediaSendResolvedAvgMs}; " +
            $"sender_bridge_publish_resolved_median_ms={mediaSendStartedToMediaSendResolvedMedianMs}; " +
            $"sender_bridge_publish_resolved_p95_ms={mediaSendStartedToMediaSendResolvedP95Ms}; " +
            $"sender_bridge_publish_resolved_max_ms={mediaSendStartedToMediaSendResolvedMaxMs}; " +
            $"frames_sent={framesSent}; send_failures={sendFailures}; queue_drops={queueDrops}; queue_mode={queueMode}; queue_depth={queueDepth}; oldest_queued_age_ms={oldestQueuedAgeMs}; sample_window_ms={sampleWindowMs}");
    }

    private void HandleBridgeBulkSendSummary(JsonElement root)
    {
        var binarySendFrameObservedToQueueEnqueueAvgMs = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_avg_ms", out var ingressAvgValue) ? ingressAvgValue : -1;
        var binarySendFrameObservedToQueueEnqueueMedianMs = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_median_ms", out var ingressMedianValue) ? ingressMedianValue : -1;
        var binarySendFrameObservedToQueueEnqueueP95Ms = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_p95_ms", out var ingressP95Value) ? ingressP95Value : -1;
        var binarySendFrameObservedToQueueEnqueueMaxMs = TryGetInt64(root, "binary_send_frame_observed_to_queue_enqueue_max_ms", out var ingressMaxValue) ? ingressMaxValue : -1;
        var queueEnqueueToQueueDequeueAvgMs = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_avg_ms", out var queueAvgValue) ? queueAvgValue : -1;
        var queueEnqueueToQueueDequeueMedianMs = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_median_ms", out var queueMedianValue) ? queueMedianValue : -1;
        var queueEnqueueToQueueDequeueP95Ms = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_p95_ms", out var queueP95Value) ? queueP95Value : -1;
        var queueEnqueueToQueueDequeueMaxMs = TryGetInt64(root, "queue_enqueue_to_queue_dequeue_max_ms", out var queueMaxValue) ? queueMaxValue : -1;
        var queueDequeueToBulkSendStartedAvgMs = TryGetInt64(root, "queue_dequeue_to_bulk_send_started_avg_ms", out var startAvgValue) ? startAvgValue : -1;
        var queueDequeueToBulkSendStartedMedianMs = TryGetInt64(root, "queue_dequeue_to_bulk_send_started_median_ms", out var startMedianValue) ? startMedianValue : -1;
        var queueDequeueToBulkSendStartedP95Ms = TryGetInt64(root, "queue_dequeue_to_bulk_send_started_p95_ms", out var startP95Value) ? startP95Value : -1;
        var queueDequeueToBulkSendStartedMaxMs = TryGetInt64(root, "queue_dequeue_to_bulk_send_started_max_ms", out var startMaxValue) ? startMaxValue : -1;
        var bulkSendStartedToBulkSendResolvedAvgMs = TryGetInt64(root, "bulk_send_started_to_bulk_send_resolved_avg_ms", out var resolvedAvgValue) ? resolvedAvgValue : -1;
        var bulkSendStartedToBulkSendResolvedMedianMs = TryGetInt64(root, "bulk_send_started_to_bulk_send_resolved_median_ms", out var resolvedMedianValue) ? resolvedMedianValue : -1;
        var bulkSendStartedToBulkSendResolvedP95Ms = TryGetInt64(root, "bulk_send_started_to_bulk_send_resolved_p95_ms", out var resolvedP95Value) ? resolvedP95Value : -1;
        var bulkSendStartedToBulkSendResolvedMaxMs = TryGetInt64(root, "bulk_send_started_to_bulk_send_resolved_max_ms", out var resolvedMaxValue) ? resolvedMaxValue : -1;
        var sendP95Ms = TryGetInt64(root, "send_p95_ms", out var sendP95Value) ? sendP95Value : bulkSendStartedToBulkSendResolvedP95Ms;
        var sendMaxMs = TryGetInt64(root, "send_max_ms", out var sendMaxValue) ? sendMaxValue : bulkSendStartedToBulkSendResolvedMaxMs;
        var framesSent = TryGetInt64(root, "frames_sent", out var framesSentValue) ? Math.Max(0, framesSentValue) : 0;
        var framesEnqueued = TryGetInt64(root, "frames_enqueued", out var framesEnqueuedValue) ? Math.Max(0, framesEnqueuedValue) : 0;
        var payloadBytesSent = TryGetInt64(root, "payload_bytes_sent", out var payloadBytesSentValue) ? Math.Max(0, payloadBytesSentValue) : 0;
        var payloadBytesPerSecond = TryGetInt64(root, "payload_bytes_per_second", out var payloadBytesPerSecondValue) ? Math.Max(0, payloadBytesPerSecondValue) : 0;
        var payloadBytesEnqueued = TryGetInt64(root, "payload_bytes_enqueued", out var payloadBytesEnqueuedValue) ? Math.Max(0, payloadBytesEnqueuedValue) : 0;
        var payloadBytesEnqueuedPerSecond = TryGetInt64(root, "payload_bytes_enqueued_per_second", out var payloadBytesEnqueuedPerSecondValue) ? Math.Max(0, payloadBytesEnqueuedPerSecondValue) : 0;
        var interEnqueueGapP95Ms = TryGetInt64(root, "inter_enqueue_gap_p95_ms", out var interEnqueueP95Value) ? interEnqueueP95Value : -1;
        var interEnqueueGapMaxMs = TryGetInt64(root, "inter_enqueue_gap_max_ms", out var interEnqueueMaxValue) ? interEnqueueMaxValue : -1;
        var sendFailures = TryGetInt64(root, "send_failures", out var sendFailuresValue) ? Math.Max(0, sendFailuresValue) : 0;
        var queueClears = TryGetInt64(root, "queue_clears", out var queueClearsValue) ? Math.Max(0, queueClearsValue) : 0;
        var queueDepth = TryGetInt64(root, "queue_depth", out var queueDepthValue) ? Math.Max(0, queueDepthValue) : 0;
        var queuedBytes = TryGetInt64(root, "queued_bytes", out var queuedBytesValue) ? Math.Max(0, queuedBytesValue) : 0;
        var oldestQueuedAgeMs = TryGetInt64(root, "oldest_queued_age_ms", out var oldestQueuedAgeValue) ? Math.Max(0, oldestQueuedAgeValue) : 0;
        var inFlight = TryGetInt64(root, "in_flight", out var inFlightValue) ? Math.Max(0, inFlightValue) : 0;
        var inFlightBytes = TryGetInt64(root, "in_flight_bytes", out var inFlightBytesValue) ? Math.Max(0, inFlightBytesValue) : 0;
        var configuredConcurrency = TryGetInt64(root, "configured_concurrency", out var configuredConcurrencyValue) ? Math.Max(0, configuredConcurrencyValue) : 0;
        var effectiveConcurrency = TryGetInt64(root, "effective_concurrency", out var effectiveConcurrencyValue) ? Math.Max(0, effectiveConcurrencyValue) : 0;
        var inFlightMax = TryGetInt64(root, "in_flight_max", out var inFlightMaxValue) ? Math.Max(0, inFlightMaxValue) : inFlight;
        var inFlightBytesMax = TryGetInt64(root, "in_flight_bytes_max", out var inFlightBytesMaxValue) ? Math.Max(0, inFlightBytesMaxValue) : inFlightBytes;
        var workerUtilizationPercent = TryGetInt64(root, "worker_utilization_percent", out var workerUtilizationValue) ? Math.Clamp(workerUtilizationValue, 0, 100) : 0;
        var workerIdleSlotSamples = TryGetInt64(root, "worker_idle_slot_samples", out var workerIdleSlotSamplesValue) ? Math.Max(0, workerIdleSlotSamplesValue) : 0;
        var workerSaturationPercent = TryGetInt64(root, "worker_saturation_percent", out var workerSaturationValue) ? Math.Clamp(workerSaturationValue, 0, 100) : 0;
        var drainWakeCount = TryGetInt64(root, "drain_wake_count", out var drainWakeCountValue) ? Math.Max(0, drainWakeCountValue) : 0;
        var sendMode = TryGetString(root, "send_mode", out var sendModeValue) ? SanitizeLogToken(sendModeValue) : "fanout";
        var sendModeFanoutFrames = TryGetInt64(root, "send_mode_fanout_frames", out var sendModeFanoutFramesValue) ? Math.Max(0, sendModeFanoutFramesValue) : 0;
        var sendModeRoundRobinFrames = TryGetInt64(root, "send_mode_round_robin_frames", out var sendModeRoundRobinFramesValue) ? Math.Max(0, sendModeRoundRobinFramesValue) : 0;
        var sendModeSingleFrames = TryGetInt64(root, "send_mode_single_frames", out var sendModeSingleFramesValue) ? Math.Max(0, sendModeSingleFramesValue) : 0;
        var sendModeRedundant2Frames = TryGetInt64(root, "send_mode_redundant2_frames", out var sendModeRedundant2FramesValue) ? Math.Max(0, sendModeRedundant2FramesValue) : 0;
        var sendModeFallbackFrames = TryGetInt64(root, "send_mode_fallback_frames", out var sendModeFallbackFramesValue) ? Math.Max(0, sendModeFallbackFramesValue) : 0;
        var sampleWindowMs = TryGetInt64(root, "sample_window_ms", out var sampleWindowValue) ? Math.Max(0, sampleWindowValue) : 0;

        Log(
            "event=nkn_bridge_bulk_send_summary; " +
            $"binary_send_frame_observed_to_queue_enqueue_avg_ms={binarySendFrameObservedToQueueEnqueueAvgMs}; " +
            $"binary_send_frame_observed_to_queue_enqueue_median_ms={binarySendFrameObservedToQueueEnqueueMedianMs}; " +
            $"binary_send_frame_observed_to_queue_enqueue_p95_ms={binarySendFrameObservedToQueueEnqueueP95Ms}; " +
            $"binary_send_frame_observed_to_queue_enqueue_max_ms={binarySendFrameObservedToQueueEnqueueMaxMs}; " +
            $"queue_enqueue_to_queue_dequeue_avg_ms={queueEnqueueToQueueDequeueAvgMs}; " +
            $"queue_enqueue_to_queue_dequeue_median_ms={queueEnqueueToQueueDequeueMedianMs}; " +
            $"queue_enqueue_to_queue_dequeue_p95_ms={queueEnqueueToQueueDequeueP95Ms}; " +
            $"queue_enqueue_to_queue_dequeue_max_ms={queueEnqueueToQueueDequeueMaxMs}; " +
            $"queue_dequeue_to_bulk_send_started_avg_ms={queueDequeueToBulkSendStartedAvgMs}; " +
            $"queue_dequeue_to_bulk_send_started_median_ms={queueDequeueToBulkSendStartedMedianMs}; " +
            $"queue_dequeue_to_bulk_send_started_p95_ms={queueDequeueToBulkSendStartedP95Ms}; " +
            $"queue_dequeue_to_bulk_send_started_max_ms={queueDequeueToBulkSendStartedMaxMs}; " +
            $"bulk_send_started_to_bulk_send_resolved_avg_ms={bulkSendStartedToBulkSendResolvedAvgMs}; " +
            $"bulk_send_started_to_bulk_send_resolved_median_ms={bulkSendStartedToBulkSendResolvedMedianMs}; " +
            $"bulk_send_started_to_bulk_send_resolved_p95_ms={bulkSendStartedToBulkSendResolvedP95Ms}; " +
            $"bulk_send_started_to_bulk_send_resolved_max_ms={bulkSendStartedToBulkSendResolvedMaxMs}; " +
            $"send_p95_ms={sendP95Ms}; send_max_ms={sendMaxMs}; frames_sent={framesSent}; frames_enqueued={framesEnqueued}; payload_bytes_sent={payloadBytesSent}; payload_bytes_per_second={payloadBytesPerSecond}; payload_bytes_enqueued={payloadBytesEnqueued}; payload_bytes_enqueued_per_second={payloadBytesEnqueuedPerSecond}; inter_enqueue_gap_p95_ms={interEnqueueGapP95Ms}; inter_enqueue_gap_max_ms={interEnqueueGapMaxMs}; send_failures={sendFailures}; queue_clears={queueClears}; queue_depth={queueDepth}; queued_bytes={queuedBytes}; oldest_queued_age_ms={oldestQueuedAgeMs}; in_flight={inFlight}; in_flight_bytes={inFlightBytes}; configured_concurrency={configuredConcurrency}; effective_concurrency={effectiveConcurrency}; in_flight_max={inFlightMax}; in_flight_bytes_max={inFlightBytesMax}; worker_utilization_percent={workerUtilizationPercent}; worker_idle_slot_samples={workerIdleSlotSamples}; worker_saturation_percent={workerSaturationPercent}; drain_wake_count={drainWakeCount}; send_mode={sendMode}; send_mode_fanout_frames={sendModeFanoutFrames}; send_mode_round_robin_frames={sendModeRoundRobinFrames}; send_mode_single_frames={sendModeSingleFrames}; send_mode_redundant2_frames={sendModeRedundant2Frames}; send_mode_fallback_frames={sendModeFallbackFrames}; sample_window_ms={sampleWindowMs}");

        EvaluateFileTransferBulkPolicy(
            sendMode,
            configuredConcurrency,
            effectiveConcurrency,
            framesEnqueued,
            payloadBytesPerSecond,
            payloadBytesEnqueuedPerSecond,
            sendFailures,
            queueClears);
    }

    private void EvaluateFileTransferBulkPolicy(
        string sendMode,
        long configuredConcurrency,
        long effectiveConcurrency,
        long framesEnqueued,
        long payloadBytesPerSecond,
        long payloadBytesEnqueuedPerSecond,
        long sendFailures,
        long queueClears)
    {
        if (!options.FileTransferAutoBulkAdaptationEnabled)
        {
            ResetFileTransferBulkPolicyWindowCount();
            return;
        }

        var normalizedMode = NormalizeFileTransferBulkSendMode(sendMode);
        var observedConcurrency = (int)Math.Clamp(
            effectiveConcurrency > 0 ? effectiveConcurrency : configuredConcurrency > 0 ? configuredConcurrency : options.BulkSendConcurrency,
            1,
            FileTransferBulkPolicyMaxConcurrency);
        CaptureFileTransferBulkPolicyBaseline(normalizedMode, observedConcurrency);

        if (!IsFileTransferActive())
        {
            ResetFileTransferBulkPolicyWindowCount();
            MaybeResetFileTransferBulkPolicyIfIdle("bulk_summary_idle");
            return;
        }

        var targetBytesPerSecond = Math.Max(1, options.FileTransferBulkTargetBytesPerSecond);
        var bridgeHadDemand =
            framesEnqueued >= FileTransferBulkPolicyMinDemandFrames &&
            payloadBytesEnqueuedPerSecond >= targetBytesPerSecond;
        var bridgeBelowTarget = payloadBytesPerSecond < targetBytesPerSecond;
        var transportDegraded = sendFailures > 0 || queueClears > 0;
        if ((!bridgeHadDemand || !bridgeBelowTarget) && !transportDegraded)
        {
            ResetFileTransferBulkPolicyWindowCount();
            return;
        }

        if (!string.Equals(normalizedMode, FileTransferBulkPolicyModeSingle, StringComparison.Ordinal))
        {
            ResetFileTransferBulkPolicyWindowCount();
            return;
        }

        var lowWindowCount = transportDegraded
            ? FileTransferBulkPolicyLowThroughputWindowsToPromote
            : IncrementFileTransferBulkPolicyWindowCount();
        if (lowWindowCount < FileTransferBulkPolicyLowThroughputWindowsToPromote)
        {
            return;
        }

        var promotedConcurrency = Math.Clamp(
            Math.Max(Math.Max(options.BulkSendConcurrency, observedConcurrency), FileTransferBulkPolicyPromotedConcurrency),
            1,
            FileTransferBulkPolicyMaxConcurrency);
        RequestFileTransferBulkPolicyChange(
            FileTransferBulkPolicyModeRoundRobin,
            promotedConcurrency,
            transportDegraded ? "bridge_bulk_send_degraded" : "bridge_bulk_capacity_below_target",
            adaptiveActiveAfterSuccess: true,
            payloadBytesPerSecond,
            payloadBytesEnqueuedPerSecond,
            targetBytesPerSecond,
            framesEnqueued,
            sendFailures,
            queueClears);
    }

    private bool IsFileTransferActive()
        => Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions)) > 0 ||
           !activeFileTransferRuntimeTransfers.IsEmpty;

    private void CaptureFileTransferBulkPolicyBaseline(string mode, int concurrency)
    {
        if (Volatile.Read(ref fileTransferBulkPolicyAdaptiveActive) != 0)
        {
            return;
        }

        lock (fileTransferBulkPolicyGate)
        {
            if (fileTransferBulkPolicyBaselineMode is not null)
            {
                return;
            }

            fileTransferBulkPolicyBaselineMode = mode;
            fileTransferBulkPolicyBaselineConcurrency = Math.Clamp(concurrency, 1, FileTransferBulkPolicyMaxConcurrency);
        }
    }

    private int IncrementFileTransferBulkPolicyWindowCount()
    {
        lock (fileTransferBulkPolicyGate)
        {
            fileTransferBulkPolicyLowThroughputWindows++;
            return fileTransferBulkPolicyLowThroughputWindows;
        }
    }

    private void ResetFileTransferBulkPolicyWindowCount()
    {
        lock (fileTransferBulkPolicyGate)
        {
            fileTransferBulkPolicyLowThroughputWindows = 0;
        }
    }

    private void MaybeResetFileTransferBulkPolicyIfIdle(string reason)
    {
        if (IsFileTransferActive())
        {
            return;
        }

        ResetFileTransferBulkPolicyWindowCount();
        if (Volatile.Read(ref fileTransferBulkPolicyAdaptiveActive) == 0)
        {
            return;
        }

        string resetMode;
        int resetConcurrency;
        lock (fileTransferBulkPolicyGate)
        {
            resetMode = fileTransferBulkPolicyBaselineMode ?? FileTransferBulkPolicyModeSingle;
            resetConcurrency = fileTransferBulkPolicyBaselineConcurrency > 0
                ? fileTransferBulkPolicyBaselineConcurrency
                : Math.Clamp(options.BulkSendConcurrency, 1, FileTransferBulkPolicyMaxConcurrency);
        }

        RequestFileTransferBulkPolicyChange(
            resetMode,
            resetConcurrency,
            reason,
            adaptiveActiveAfterSuccess: false,
            payloadBytesPerSecond: 0,
            payloadBytesEnqueuedPerSecond: 0,
            targetBytesPerSecond: Math.Max(1, options.FileTransferBulkTargetBytesPerSecond),
            framesEnqueued: 0,
            sendFailures: 0,
            queueClears: 0,
            ignoreCooldown: true);
    }

    private void RequestFileTransferBulkPolicyChange(
        string mode,
        int concurrency,
        string reason,
        bool adaptiveActiveAfterSuccess,
        long payloadBytesPerSecond,
        long payloadBytesEnqueuedPerSecond,
        long targetBytesPerSecond,
        long framesEnqueued,
        long sendFailures,
        long queueClears,
        bool ignoreCooldown = false)
    {
        if (disposed || shuttingDown || !bridgeSupervisor.IsProcessRunning)
        {
            return;
        }

        if (Volatile.Read(ref fileTransferBulkPolicyChangeInFlight) != 0)
        {
            return;
        }

        if (adaptiveActiveAfterSuccess && Volatile.Read(ref fileTransferBulkPolicyAdaptiveActive) != 0)
        {
            return;
        }

        var nowTick = Stopwatch.GetTimestamp();
        var lastChangeTick = Volatile.Read(ref fileTransferBulkPolicyLastChangeTick);
        var cooldown = TimeSpan.FromMilliseconds(Math.Max(0, options.FileTransferBulkAdaptationCooldownMs));
        if (!ignoreCooldown && lastChangeTick > 0 && Stopwatch.GetElapsedTime(lastChangeTick, nowTick) < cooldown)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref fileTransferBulkPolicyChangeInFlight, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref fileTransferBulkPolicyLastChangeTick, nowTick);
        var normalizedMode = NormalizeFileTransferBulkSendMode(mode);
        var normalizedConcurrency = Math.Clamp(concurrency, 1, FileTransferBulkPolicyMaxConcurrency);
        Log(
            "event=nkn_bridge_bulk_send_policy_adaptation_requested; " +
            $"reason={SanitizeLogToken(reason)}; mode={normalizedMode}; concurrency={normalizedConcurrency}; " +
            $"adaptive_active_after_success={(adaptiveActiveAfterSuccess ? 1 : 0)}; payload_bytes_per_second={payloadBytesPerSecond}; payload_bytes_enqueued_per_second={payloadBytesEnqueuedPerSecond}; " +
            $"target_bytes_per_second={targetBytesPerSecond}; frames_enqueued={framesEnqueued}; send_failures={sendFailures}; queue_clears={queueClears}");

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await SetBulkSendPolicyAsync(normalizedMode, normalizedConcurrency, reason, CancellationToken.None).ConfigureAwait(false);
                    Volatile.Write(ref fileTransferBulkPolicyAdaptiveActive, adaptiveActiveAfterSuccess ? 1 : 0);
                    if (!adaptiveActiveAfterSuccess)
                    {
                        ResetFileTransferBulkPolicyWindowCount();
                    }

                    Log(
                        "event=nkn_bridge_bulk_send_policy_adaptation_applied; " +
                        $"reason={SanitizeLogToken(reason)}; mode={normalizedMode}; concurrency={normalizedConcurrency}; adaptive_active={(adaptiveActiveAfterSuccess ? 1 : 0)}");
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or InvalidOperationException or TimeoutException or IOException)
                {
                    Log(
                        "event=nkn_bridge_bulk_send_policy_adaptation_failed; " +
                        $"reason={SanitizeLogToken(reason)}; mode={normalizedMode}; concurrency={normalizedConcurrency}; error={SanitizeLogToken(ex.GetType().Name)}");
                }
                finally
                {
                    Interlocked.Exchange(ref fileTransferBulkPolicyChangeInFlight, 0);
                }
            },
            CancellationToken.None);
    }

    private static string NormalizeFileTransferBulkSendMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return FileTransferBulkPolicyModeSingle;
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized switch
        {
            FileTransferBulkPolicyModeSingle => FileTransferBulkPolicyModeSingle,
            FileTransferBulkPolicyModeRoundRobin => FileTransferBulkPolicyModeRoundRobin,
            "redundant2" => "redundant2",
            "fanout" => "fanout",
            _ => FileTransferBulkPolicyModeSingle,
        };
    }

    private void HandleBridgeTransportHealthSummary(JsonElement root)
    {
        var selectedRpc = TryGetString(root, "selected_rpc", out var selectedRpcValue) ? selectedRpcValue : "(none)";
        var selectedRpcKey = TryGetString(root, "selected_rpc_key", out var selectedRpcKeyValue) ? selectedRpcKeyValue : "(none)";
        var selectedRpcStage = TryGetString(root, "selected_rpc_stage", out var selectedRpcStageValue) ? selectedRpcStageValue : "none";
        var connectId = TryGetString(root, "connect_id", out var connectIdValue) ? connectIdValue : "(none)";
        var connectKey = TryGetString(root, "connect_key", out var connectKeyValue) ? connectKeyValue : "(none)";
        var readyEmitted = TryGetInt64(root, "ready_emitted", out var readyEmittedValue) ? Math.Max(0, readyEmittedValue) : 0;
        var clientReadyAgeMs = TryGetInt64(root, "client_ready_age_ms", out var clientReadyAgeValue) ? clientReadyAgeValue : -1;
        var disconnectCountSinceLast = TryGetInt64(root, "disconnect_count_since_last", out var disconnectCountValue) ? Math.Max(0, disconnectCountValue) : 0;
        var connectFailedCountSinceLast = TryGetInt64(root, "connect_failed_count_since_last", out var connectFailedCountValue) ? Math.Max(0, connectFailedCountValue) : 0;
        var wsErrorCountSinceLast = TryGetInt64(root, "ws_error_count_since_last", out var wsErrorCountValue) ? Math.Max(0, wsErrorCountValue) : 0;
        var rpcFallbackAttemptCountSinceLast = TryGetInt64(root, "rpc_fallback_attempt_count_since_last", out var rpcFallbackCountValue) ? Math.Max(0, rpcFallbackCountValue) : 0;
        var controlReady = TryGetInt64(root, "control_ready", out var controlReadyValue) ? Math.Max(0, controlReadyValue) : 0;
        var mediaReady = TryGetInt64(root, "media_ready", out var mediaReadyValue) ? Math.Max(0, mediaReadyValue) : 0;
        var bulkReady = TryGetInt64(root, "bulk_ready", out var bulkReadyValue) ? Math.Max(0, bulkReadyValue) : 0;
        var framesSentSinceLast = TryGetInt64(root, "frames_sent_since_last", out var framesSentValue) ? Math.Max(0, framesSentValue) : 0;
        var latestDisconnectReason = TryGetString(root, "latest_disconnect_reason", out var latestDisconnectReasonValue) ? latestDisconnectReasonValue : "(none)";
        var sampleWindowMs = TryGetInt64(root, "sample_window_ms", out var sampleWindowValue) ? Math.Max(0, sampleWindowValue) : 0;
        var controlSubClients = TryGetInt64(root, "control_subclients", out var controlSubClientsValue) ? Math.Max(0, controlSubClientsValue) : 0;
        var mediaSubClients = TryGetInt64(root, "media_subclients", out var mediaSubClientsValue) ? Math.Max(0, mediaSubClientsValue) : 0;
        var bulkSubClients = TryGetInt64(root, "bulk_subclients", out var bulkSubClientsValue) ? Math.Max(0, bulkSubClientsValue) : 0;
        var bulkSendConcurrency = TryGetInt64(root, "bulk_send_concurrency", out var bulkSendConcurrencyValue) ? Math.Max(0, bulkSendConcurrencyValue) : 0;
        var controlMessagesReceivedSinceLast = TryGetInt64(root, "control_messages_received_since_last", out var controlMessagesReceivedValue) ? Math.Max(0, controlMessagesReceivedValue) : 0;
        var mediaMessagesReceivedSinceLast = TryGetInt64(root, "media_messages_received_since_last", out var mediaMessagesReceivedValue) ? Math.Max(0, mediaMessagesReceivedValue) : 0;
        var bulkMessagesReceivedSinceLast = TryGetInt64(root, "bulk_messages_received_since_last", out var bulkMessagesReceivedValue) ? Math.Max(0, bulkMessagesReceivedValue) : 0;
        var totalMessagesReceivedSinceLast = TryGetInt64(root, "total_messages_received_since_last", out var totalMessagesReceivedValue) ? Math.Max(0, totalMessagesReceivedValue) : controlMessagesReceivedSinceLast + mediaMessagesReceivedSinceLast + bulkMessagesReceivedSinceLast;
        var controlBytesReceivedSinceLast = TryGetInt64(root, "control_bytes_received_since_last", out var controlBytesReceivedValue) ? Math.Max(0, controlBytesReceivedValue) : 0;
        var mediaBytesReceivedSinceLast = TryGetInt64(root, "media_bytes_received_since_last", out var mediaBytesReceivedValue) ? Math.Max(0, mediaBytesReceivedValue) : 0;
        var bulkBytesReceivedSinceLast = TryGetInt64(root, "bulk_bytes_received_since_last", out var bulkBytesReceivedValue) ? Math.Max(0, bulkBytesReceivedValue) : 0;
        var totalBytesReceivedSinceLast = TryGetInt64(root, "total_bytes_received_since_last", out var totalBytesReceivedValue) ? Math.Max(0, totalBytesReceivedValue) : controlBytesReceivedSinceLast + mediaBytesReceivedSinceLast + bulkBytesReceivedSinceLast;
        var controlLastReceivedAgeMs = TryGetInt64(root, "control_last_received_age_ms", out var controlLastReceivedAgeValue) ? controlLastReceivedAgeValue : -1;
        var mediaLastReceivedAgeMs = TryGetInt64(root, "media_last_received_age_ms", out var mediaLastReceivedAgeValue) ? mediaLastReceivedAgeValue : -1;
        var bulkLastReceivedAgeMs = TryGetInt64(root, "bulk_last_received_age_ms", out var bulkLastReceivedAgeValue) ? bulkLastReceivedAgeValue : -1;

        MaybeLogReceiveStallRecoveryReceiveResumed(
            connectKey,
            totalMessagesReceivedSinceLast,
            totalBytesReceivedSinceLast,
            controlMessagesReceivedSinceLast,
            mediaMessagesReceivedSinceLast,
            bulkMessagesReceivedSinceLast,
            controlLastReceivedAgeMs,
            bulkLastReceivedAgeMs);

        Log(
            "event=screenshare_bridge_transport_health_summary; " +
            $"selected_rpc={selectedRpc}; selected_rpc_key={selectedRpcKey}; selected_rpc_stage={selectedRpcStage}; connect_id={connectId}; connect_key={connectKey}; " +
            $"ready_emitted={readyEmitted}; client_ready_age_ms={clientReadyAgeMs}; disconnect_count_since_last={disconnectCountSinceLast}; " +
            $"connect_failed_count_since_last={connectFailedCountSinceLast}; ws_error_count_since_last={wsErrorCountSinceLast}; rpc_fallback_attempt_count_since_last={rpcFallbackAttemptCountSinceLast}; " +
            $"control_ready={controlReady}; media_ready={mediaReady}; bulk_ready={bulkReady}; frames_sent_since_last={framesSentSinceLast}; latest_disconnect_reason={latestDisconnectReason}; sample_window_ms={sampleWindowMs}; " +
            $"control_subclients={controlSubClients}; media_subclients={mediaSubClients}; bulk_subclients={bulkSubClients}; " +
            $"bulk_send_concurrency={bulkSendConcurrency}; control_messages_received_since_last={controlMessagesReceivedSinceLast}; media_messages_received_since_last={mediaMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; " +
            $"total_messages_received_since_last={totalMessagesReceivedSinceLast}; control_bytes_received_since_last={controlBytesReceivedSinceLast}; media_bytes_received_since_last={mediaBytesReceivedSinceLast}; bulk_bytes_received_since_last={bulkBytesReceivedSinceLast}; " +
            $"total_bytes_received_since_last={totalBytesReceivedSinceLast}; control_last_received_age_ms={controlLastReceivedAgeMs}; media_last_received_age_ms={mediaLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}; " +
            $"srk={selectedRpcKey}; srs={selectedRpcStage}; cky={connectKey}; rdy={readyEmitted}; cra={clientReadyAgeMs}; dcc={disconnectCountSinceLast}; cfc={connectFailedCountSinceLast}; wec={wsErrorCountSinceLast}; rfc={rpcFallbackAttemptCountSinceLast}; cr={controlReady}; mr={mediaReady}; br={bulkReady}; fss={framesSentSinceLast}; " +
            $"cmr={controlMessagesReceivedSinceLast}; mmr={mediaMessagesReceivedSinceLast}; bmr={bulkMessagesReceivedSinceLast}; tmr={totalMessagesReceivedSinceLast}; cbrx={controlBytesReceivedSinceLast}; mbrx={mediaBytesReceivedSinceLast}; bbrx={bulkBytesReceivedSinceLast}; tbrx={totalBytesReceivedSinceLast}; clar={controlLastReceivedAgeMs}; mlar={mediaLastReceivedAgeMs}; blar={bulkLastReceivedAgeMs}; " +
            $"ldr={latestDisconnectReason}; csc={controlSubClients}; msc={mediaSubClients}; bsc={bulkSubClients}; bcc={bulkSendConcurrency}");

        EvaluateReceiveStallRecovery(
            connectKey,
            readyEmitted,
            controlReady,
            mediaReady,
            bulkReady,
            framesSentSinceLast,
            controlMessagesReceivedSinceLast,
            bulkMessagesReceivedSinceLast,
            totalMessagesReceivedSinceLast,
            controlLastReceivedAgeMs,
            mediaLastReceivedAgeMs,
            bulkLastReceivedAgeMs,
            sampleWindowMs);
    }

    private void EvaluateReceiveStallRecovery(
        string connectKey,
        long readyEmitted,
        long controlReady,
        long mediaReady,
        long bulkReady,
        long framesSentSinceLast,
        long controlMessagesReceivedSinceLast,
        long bulkMessagesReceivedSinceLast,
        long totalMessagesReceivedSinceLast,
        long controlLastReceivedAgeMs,
        long mediaLastReceivedAgeMs,
        long bulkLastReceivedAgeMs,
        long sampleWindowMs)
    {
        var allChannelsReady = readyEmitted > 0 && controlReady > 0 && mediaReady > 0 && bulkReady > 0;
        var activeOutboundTraffic = framesSentSinceLast > 0;
        var fileTransferActiveSessionCount = Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions));
        var fileTransferActiveRuntimeCount = activeFileTransferRuntimeTransfers.Count;
        var fileTransferActive = fileTransferActiveSessionCount > 0;
        var fileTransferRuntimeActive = fileTransferActiveRuntimeCount > 0;
        var receiveStalled = allChannelsReady && activeOutboundTraffic && totalMessagesReceivedSinceLast == 0;
        var consecutiveWindows = receiveStalled
            ? Interlocked.Increment(ref receiveStallConsecutiveWindows)
            : Interlocked.Exchange(ref receiveStallConsecutiveWindows, 0);
        var controlLivenessFreshForActiveFileTransfer =
            controlMessagesReceivedSinceLast > 0 ||
            (controlLastReceivedAgeMs >= 0 && controlLastReceivedAgeMs < ReceiveStallControlAgeThresholdMs);
        var bulkReceiveStalledCandidate =
            fileTransferActive &&
            readyEmitted > 0 &&
            bulkReady > 0 &&
            activeOutboundTraffic &&
            bulkMessagesReceivedSinceLast == 0 &&
            bulkLastReceivedAgeMs >= ReceiveStallBulkAgeThresholdMs &&
            !controlLivenessFreshForActiveFileTransfer;
        var bulkConsecutiveWindows = bulkReceiveStalledCandidate
            ? Interlocked.Increment(ref receiveStallBulkConsecutiveWindows)
            : Interlocked.Exchange(ref receiveStallBulkConsecutiveWindows, 0);
        var bulkReceiveStalled = options.ReceiveStallFileTransferFastRecoveryEnabled && bulkReceiveStalledCandidate;
        var controlReceiveStalledCandidate =
            fileTransferActive &&
            readyEmitted > 0 &&
            controlReady > 0 &&
            activeOutboundTraffic &&
            controlMessagesReceivedSinceLast == 0 &&
            controlLastReceivedAgeMs >= ReceiveStallControlAgeThresholdMs;
        var controlConsecutiveWindows = controlReceiveStalledCandidate
            ? Interlocked.Increment(ref receiveStallControlConsecutiveWindows)
            : Interlocked.Exchange(ref receiveStallControlConsecutiveWindows, 0);
        var controlReceiveStalled = options.ReceiveStallFileTransferFastRecoveryEnabled && controlReceiveStalledCandidate;
        var bulkReceiveActiveThisWindow = bulkMessagesReceivedSinceLast > 0;

        Log(
            "event=filetransfer_v4_receive_liveness_summary; " +
            $"reason=sample; active_file_transfer_sessions={fileTransferActiveSessionCount}; active_file_transfer_runtime_sessions={fileTransferActiveRuntimeCount}; ready_emitted={readyEmitted}; control_ready={controlReady}; media_ready={mediaReady}; bulk_ready={bulkReady}; frames_sent_since_last={framesSentSinceLast}; " +
            $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; total_messages_received_since_last={totalMessagesReceivedSinceLast}; " +
            $"control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}; all_zero_receive_consecutive_windows={consecutiveWindows}; bulk_zero_receive_consecutive_windows={bulkConsecutiveWindows}; control_zero_receive_consecutive_windows={controlConsecutiveWindows}; " +
            $"filetransfer_fast_recovery_enabled={(options.ReceiveStallFileTransferFastRecoveryEnabled ? 1 : 0)}; sample_window_ms={sampleWindowMs}");

        string? stallReason = null;
        var qualifiedConsecutiveWindows = 0;
        var requiresControlProof = false;
        var requiresBulkProof = false;
        if (receiveStalled &&
            consecutiveWindows >= ReceiveStallRequiredConsecutiveWindows &&
            controlLastReceivedAgeMs >= ReceiveStallControlAgeThresholdMs)
        {
            if (fileTransferActive &&
                options.ReceiveStallFileTransferFastRecoveryEnabled &&
                bulkConsecutiveWindows < ReceiveStallFastRequiredConsecutiveWindows)
            {
                Log(
                    "event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_bulk_probe_window; " +
                    $"connect_key={connectKey}; consecutive_zero_receive_windows={consecutiveWindows}; bulk_zero_receive_consecutive_windows={bulkConsecutiveWindows}; active_file_transfer_sessions={fileTransferActiveSessionCount}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}; " +
                    $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}");
                return;
            }

            stallReason = "all_channels_zero_receive";
            qualifiedConsecutiveWindows = consecutiveWindows;
            requiresControlProof = true;
            requiresBulkProof = bulkLastReceivedAgeMs >= ReceiveStallBulkAgeThresholdMs;
        }
        else if (bulkReceiveStalled &&
                 bulkConsecutiveWindows >= ReceiveStallFastRequiredConsecutiveWindows)
        {
            stallReason = "bulk_receive_stalled";
            qualifiedConsecutiveWindows = bulkConsecutiveWindows;
            requiresBulkProof = true;
        }
        else if (controlReceiveStalled &&
                 controlConsecutiveWindows >= ReceiveStallFastRequiredConsecutiveWindows)
        {
            var forceControlOnlyRecoveryForStaleFileTransfer =
                fileTransferActive &&
                controlConsecutiveWindows >= ReceiveStallControlOnlyActiveFileTransferGraceWindows &&
                controlLastReceivedAgeMs >= ReceiveStallControlOnlyActiveFileTransferGraceMs;

            if (bulkReceiveActiveThisWindow &&
                !options.ReceiveStallControlOnlyRecoveryEnabled &&
                !forceControlOnlyRecoveryForStaleFileTransfer)
            {
                Log(
                    "event=nkn_bridge_control_receive_degraded; " +
                    $"connect_key={connectKey}; consecutive_control_zero_receive_windows={controlConsecutiveWindows}; active_file_transfer_sessions={fileTransferActiveSessionCount}; frames_sent_since_last={framesSentSinceLast}; " +
                    $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; total_messages_received_since_last={totalMessagesReceivedSinceLast}; " +
                    $"control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}; sample_window_ms={sampleWindowMs}");
                Log(
                    "event=nkn_bridge_control_receive_recovery_suppressed; reason=filetransfer_bulk_receive_active; " +
                    $"connect_key={connectKey}; consecutive_control_zero_receive_windows={controlConsecutiveWindows}; active_file_transfer_sessions={fileTransferActiveSessionCount}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}; " +
                    $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}");
                return;
            }

            if (!options.ReceiveStallControlOnlyRecoveryEnabled &&
                bulkConsecutiveWindows < ReceiveStallFastRequiredConsecutiveWindows &&
                !forceControlOnlyRecoveryForStaleFileTransfer)
            {
                Log(
                    "event=nkn_bridge_control_receive_degraded; " +
                    $"connect_key={connectKey}; consecutive_control_zero_receive_windows={controlConsecutiveWindows}; active_file_transfer_sessions={fileTransferActiveSessionCount}; frames_sent_since_last={framesSentSinceLast}; " +
                    $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; total_messages_received_since_last={totalMessagesReceivedSinceLast}; " +
                    $"control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}; sample_window_ms={sampleWindowMs}");
                Log(
                    "event=nkn_bridge_control_receive_recovery_suppressed; reason=filetransfer_bulk_not_idle; " +
                    $"connect_key={connectKey}; consecutive_control_zero_receive_windows={controlConsecutiveWindows}; bulk_zero_receive_consecutive_windows={bulkConsecutiveWindows}; active_file_transfer_sessions={fileTransferActiveSessionCount}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}; " +
                    $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}");
                return;
            }

            if (forceControlOnlyRecoveryForStaleFileTransfer &&
                !options.ReceiveStallControlOnlyRecoveryEnabled)
            {
                Log(
                    "event=nkn_bridge_control_receive_recovery_forced; reason=filetransfer_control_stale_beyond_grace; " +
                    $"connect_key={connectKey}; consecutive_control_zero_receive_windows={controlConsecutiveWindows}; active_file_transfer_sessions={fileTransferActiveSessionCount}; active_file_transfer_runtime_sessions={fileTransferActiveRuntimeCount}; " +
                    $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}; grace_ms={ReceiveStallControlOnlyActiveFileTransferGraceMs}");
            }

            stallReason = "control_receive_stalled";
            qualifiedConsecutiveWindows = controlConsecutiveWindows;
            requiresControlProof = true;
        }

        if (stallReason is null)
        {
            if (!options.ReceiveStallFileTransferFastRecoveryEnabled &&
                ((bulkReceiveStalledCandidate && bulkConsecutiveWindows >= ReceiveStallFastRequiredConsecutiveWindows) ||
                 (controlReceiveStalledCandidate && controlConsecutiveWindows >= ReceiveStallFastRequiredConsecutiveWindows)))
            {
                Log(
                    "event=nkn_bridge_receive_stall_recovery_failed; reason=filetransfer_fast_recovery_disabled; " +
                    $"connect_key={connectKey}; bulk_zero_receive_consecutive_windows={bulkConsecutiveWindows}; control_zero_receive_consecutive_windows={controlConsecutiveWindows}; active_file_transfer_sessions={fileTransferActiveSessionCount}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}");
            }

            return;
        }

        Log(
            "event=nkn_bridge_receive_stall_detected; " +
            $"connect_key={connectKey}; reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; all_zero_receive_consecutive_windows={consecutiveWindows}; bulk_zero_receive_consecutive_windows={bulkConsecutiveWindows}; control_zero_receive_consecutive_windows={controlConsecutiveWindows}; active_file_transfer_sessions={fileTransferActiveSessionCount}; active_file_transfer_runtime_sessions={fileTransferActiveRuntimeCount}; frames_sent_since_last={framesSentSinceLast}; " +
            $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; total_messages_received_since_last={totalMessagesReceivedSinceLast}; control_last_received_age_ms={controlLastReceivedAgeMs}; " +
            $"media_last_received_age_ms={mediaLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}; sample_window_ms={sampleWindowMs}");

        if (!options.ReceiveStallRecoveryEnabled)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_failed; reason=disabled; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}");
            return;
        }

        if (disposed || shuttingDown)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_failed; reason=adapter_not_active; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}");
            return;
        }

        var nowTick = Stopwatch.GetTimestamp();
        var lastRecoveryTick = Volatile.Read(ref receiveStallLastRecoveryStartedTick);
        var recoveryCount = Volatile.Read(ref receiveStallRecoveryCount);
        var awaitingReceiveProof = Volatile.Read(ref receiveStallRecoveryAwaitingReceiveProof) != 0;
        var requiresControlProofSnapshot = Volatile.Read(ref receiveStallRecoveryRequiresControlProof) != 0;
        var requiresBulkProofSnapshot = Volatile.Read(ref receiveStallRecoveryRequiresBulkProof) != 0;
        var activeFileTransferRecoveryUnproven =
            fileTransferActive &&
            awaitingReceiveProof &&
            (requiresControlProofSnapshot || requiresBulkProofSnapshot);
        if (recoveryCount >= ReceiveStallMaxRecoveriesPerSession)
        {
            if (!fileTransferActive ||
                recoveryCount >= ReceiveStallMaxActiveFileTransferRecoveriesPerSession)
            {
                Log(
                    "event=nkn_bridge_receive_stall_recovery_failed; reason=max_restarts_reached; " +
                    $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={recoveryCount}; max_restarts={ReceiveStallMaxRecoveriesPerSession}; active_file_transfer_max_restarts={ReceiveStallMaxActiveFileTransferRecoveriesPerSession}; active_file_transfer_sessions={fileTransferActiveSessionCount}");
                EmitBridgeLifecycle(new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: bridgeSupervisor.CurrentPid,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: ElapsedSinceTicksMilliseconds(bridgeSupervisor.CurrentSpawnTicks),
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: $"{stallReason}_max_restarts"));
                return;
            }

            var elapsedSinceLastRecovery = lastRecoveryTick > 0
                ? Stopwatch.GetElapsedTime(lastRecoveryTick, nowTick)
                : ReceiveStallActiveFileTransferExtendedRecoveryCooldown;
            var activeFileTransferBudgetCooldown = activeFileTransferRecoveryUnproven
                ? ReceiveStallActiveFileTransferUnprovenRecoveryCooldown
                : ReceiveStallActiveFileTransferExtendedRecoveryCooldown;
            if (elapsedSinceLastRecovery < activeFileTransferBudgetCooldown)
            {
                var cooldownRemainingMs = Math.Max(
                    0,
                    (long)(activeFileTransferBudgetCooldown - elapsedSinceLastRecovery).TotalMilliseconds);
                var reason = activeFileTransferRecoveryUnproven
                    ? "active_filetransfer_unproven_cooldown"
                    : "active_filetransfer_extended_cooldown";
                Log(
                    $"event=nkn_bridge_receive_stall_recovery_failed; reason={reason}; " +
                    $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={recoveryCount}; max_restarts={ReceiveStallMaxRecoveriesPerSession}; active_file_transfer_max_restarts={ReceiveStallMaxActiveFileTransferRecoveriesPerSession}; active_file_transfer_sessions={fileTransferActiveSessionCount}; cooldown_remaining_ms={cooldownRemainingMs}");
                return;
            }

            Log(
                "event=nkn_bridge_receive_stall_recovery_budget_extended; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={recoveryCount}; base_max_restarts={ReceiveStallMaxRecoveriesPerSession}; active_file_transfer_max_restarts={ReceiveStallMaxActiveFileTransferRecoveriesPerSession}; active_file_transfer_sessions={fileTransferActiveSessionCount}");
        }

        var activeFileTransferShortCooldown = fileTransferActive;
        var effectiveRecoveryCooldown = activeFileTransferRecoveryUnproven
            ? ReceiveStallActiveFileTransferUnprovenRecoveryCooldown
            : activeFileTransferShortCooldown
            ? ReceiveStallActiveFileTransferExtendedRecoveryCooldown
            : ReceiveStallRecoveryCooldown;
        var elapsedSinceLastRecoveryForCooldown = lastRecoveryTick > 0
            ? Stopwatch.GetElapsedTime(lastRecoveryTick, nowTick)
            : ReceiveStallRecoveryCooldown;
        if (activeFileTransferShortCooldown &&
            lastRecoveryTick > 0 &&
            elapsedSinceLastRecoveryForCooldown >= ReceiveStallActiveFileTransferExtendedRecoveryCooldown &&
            elapsedSinceLastRecoveryForCooldown < ReceiveStallRecoveryCooldown)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_cooldown_shortened; reason=active_filetransfer; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={recoveryCount}; " +
                $"base_cooldown_ms={(long)ReceiveStallRecoveryCooldown.TotalMilliseconds}; active_filetransfer_cooldown_ms={(long)ReceiveStallActiveFileTransferExtendedRecoveryCooldown.TotalMilliseconds}; active_file_transfer_sessions={fileTransferActiveSessionCount}");
        }

        var withinCooldown = lastRecoveryTick > 0 && elapsedSinceLastRecoveryForCooldown < effectiveRecoveryCooldown;
        if (withinCooldown && (!awaitingReceiveProof || activeFileTransferRecoveryUnproven))
        {
            var cooldownRemainingMs = Math.Max(0, (long)(effectiveRecoveryCooldown - elapsedSinceLastRecoveryForCooldown).TotalMilliseconds);
            var reason = awaitingReceiveProof
                ? "previous_recovery_unproven_cooldown"
                : "cooldown_active";
            Log(
                $"event=nkn_bridge_receive_stall_recovery_failed; reason={reason}; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={recoveryCount}; cooldown_remaining_ms={cooldownRemainingMs}; awaiting_receive_proof={(awaitingReceiveProof ? 1 : 0)}");
            return;
        }

        if (withinCooldown && awaitingReceiveProof)
        {
            var elapsedSinceLastRecoveryMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(lastRecoveryTick, nowTick).TotalMilliseconds);
            Log(
                "event=nkn_bridge_receive_stall_recovery_cooldown_bypassed; reason=previous_recovery_unproven; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={recoveryCount}; elapsed_since_last_recovery_ms={elapsedSinceLastRecoveryMs}");
        }

        if (Interlocked.CompareExchange(ref receiveStallRecoveryInProgress, 1, 0) != 0)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_failed; reason=recovery_already_in_progress; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; consecutive_zero_receive_windows={qualifiedConsecutiveWindows}; recovery_count={recoveryCount}");
            return;
        }

        Volatile.Write(ref receiveStallLastRecoveryStartedTick, nowTick);
        var attempt = Interlocked.Increment(ref receiveStallRecoveryCount);
        Volatile.Write(ref receiveStallRecoveryRequiresControlProof, requiresControlProof ? 1 : 0);
        Volatile.Write(ref receiveStallRecoveryRequiresBulkProof, requiresBulkProof ? 1 : 0);
        _ = Task.Run(
            () => RecoverFromReceiveStallAsync(
                connectKey,
                stallReason,
                attempt,
                qualifiedConsecutiveWindows,
                framesSentSinceLast,
                controlLastReceivedAgeMs,
                mediaLastReceivedAgeMs,
                bulkLastReceivedAgeMs),
            CancellationToken.None);
    }

    private async Task RecoverFromReceiveStallAsync(
        string connectKey,
        string stallReason,
        int attempt,
        int consecutiveWindows,
        long framesSentSinceLast,
        long controlLastReceivedAgeMs,
        long mediaLastReceivedAgeMs,
        long bulkLastReceivedAgeMs)
    {
        Log(
            "event=nkn_bridge_receive_stall_recovery_started; " +
            $"connect_key={connectKey}; stall_reason={stallReason}; attempt={attempt}; max_restarts={ReceiveStallMaxRecoveriesPerSession}; consecutive_zero_receive_windows={consecutiveWindows}; " +
            $"frames_sent_since_last={framesSentSinceLast}; control_last_received_age_ms={controlLastReceivedAgeMs}; media_last_received_age_ms={mediaLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}");

        try
        {
            EmitBridgeLifecycle(new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                StartMode: null,
                Pid: bridgeSupervisor.CurrentPid,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: ElapsedSinceTicksMilliseconds(bridgeSupervisor.CurrentSpawnTicks),
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: stallReason));

            lock (gate)
            {
                suppressBridgeDisconnectDuringReceiveStallRecovery = true;
            }

            var activeFileTransfer =
                Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions)) > 0 ||
                !activeFileTransferRuntimeTransfers.IsEmpty;
            var coreRequestedFileTransferRecovery = string.Equals(
                connectKey,
                "core_filetransfer_request",
                StringComparison.Ordinal);
            var useHardRestart =
                activeFileTransfer &&
                (coreRequestedFileTransferRecovery ||
                 attempt >= ReceiveStallActiveFileTransferHardRestartMinAttempt);

            if (useHardRestart)
            {
                Log(
                    "event=nkn_bridge_receive_stall_recovery_hard_restart; " +
                    $"connect_key={connectKey}; stall_reason={stallReason}; attempt={attempt}; core_requested={(coreRequestedFileTransferRecovery ? 1 : 0)}; " +
                    $"active_file_transfer_sessions={Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions))}; active_file_transfer_runtime_sessions={activeFileTransferRuntimeTransfers.Count}");
                if (!await RestartBridgeProcessAsync(reconnectAfterRestart: true).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Bridge hard restart did not reconnect.");
                }
            }
            else
            {
                connectAttempts.ResetPendingReadyForNewProcessStart();
                Interlocked.Exchange(ref receiveStallRecoveryConnectActive, 1);
                try
                {
                    await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref receiveStallRecoveryConnectActive, 0);
                }
            }

            Interlocked.Exchange(ref receiveStallConsecutiveWindows, 0);
            Interlocked.Exchange(ref receiveStallBulkConsecutiveWindows, 0);
            Interlocked.Exchange(ref receiveStallControlConsecutiveWindows, 0);
            Interlocked.Exchange(ref receiveStallRecoveryAwaitingReceiveProof, 1);
            Interlocked.Exchange(ref receiveStallRecoveryLastUnprovenLogTick, 0);
            Volatile.Write(ref receiveStallLastRecoveryCompletedTick, Stopwatch.GetTimestamp());
            Log(
                "event=nkn_bridge_receive_stall_recovery_completed; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; attempt={attempt}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}; " +
                $"fallback_delay_ms={options.ReceiveStallRecoveryFallbackDelayMs}; requires_control_proof={Volatile.Read(ref receiveStallRecoveryRequiresControlProof)}; requires_bulk_proof={Volatile.Read(ref receiveStallRecoveryRequiresBulkProof)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NknRuntimeDiagnostics.SetLastError("nkn_bridge_receive_stall_recovery_failed");
            Log(
                "event=nkn_bridge_receive_stall_recovery_failed; " +
                $"connect_key={connectKey}; stall_reason={stallReason}; attempt={attempt}; reason={ex.GetType().Name}; message={SanitizeLogToken(ex.Message)}");
            SignalDisconnected("receive_stall_recovery_failed");
        }
        finally
        {
            lock (gate)
            {
                suppressBridgeDisconnectDuringReceiveStallRecovery = false;
            }

            Interlocked.Exchange(ref receiveStallRecoveryInProgress, 0);
        }
    }

    private void MaybeLogReceiveStallRecoveryReceiveResumed(
        string connectKey,
        long totalMessagesReceivedSinceLast,
        long totalBytesReceivedSinceLast,
        long controlMessagesReceivedSinceLast,
        long mediaMessagesReceivedSinceLast,
        long bulkMessagesReceivedSinceLast,
        long controlLastReceivedAgeMs,
        long bulkLastReceivedAgeMs)
    {
        if (totalMessagesReceivedSinceLast <= 0)
        {
            return;
        }

        if (Volatile.Read(ref receiveStallRecoveryAwaitingReceiveProof) == 0)
        {
            return;
        }

        var requiresControlProof = Volatile.Read(ref receiveStallRecoveryRequiresControlProof) != 0;
        var requiresBulkProof = Volatile.Read(ref receiveStallRecoveryRequiresBulkProof) != 0;
        var fileTransferRuntimeActive =
            Volatile.Read(ref activeFileTransferDataSessions) > 0 ||
            !activeFileTransferRuntimeTransfers.IsEmpty;
        var fileTransferBulkProofAcceptedForControl =
            fileTransferRuntimeActive &&
            requiresControlProof &&
            controlMessagesReceivedSinceLast <= 0 &&
            bulkMessagesReceivedSinceLast > 0;
        var hasRequiredControl =
            !requiresControlProof ||
            controlMessagesReceivedSinceLast > 0 ||
            fileTransferBulkProofAcceptedForControl;
        var hasRequiredBulk = !requiresBulkProof || bulkMessagesReceivedSinceLast > 0;
        if (!hasRequiredControl || !hasRequiredBulk)
        {
            MaybeLogReceiveStallRecoveryUnproven(
                connectKey,
                totalMessagesReceivedSinceLast,
                totalBytesReceivedSinceLast,
                controlMessagesReceivedSinceLast,
                mediaMessagesReceivedSinceLast,
                bulkMessagesReceivedSinceLast,
                controlLastReceivedAgeMs,
                bulkLastReceivedAgeMs,
                requiresControlProof,
                requiresBulkProof);
            return;
        }

        if (fileTransferBulkProofAcceptedForControl)
        {
            Log(
                "event=nkn_bridge_receive_stall_recovery_filetransfer_bulk_proof_accepted; " +
                $"connect_key={connectKey}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}; " +
                $"requires_control_proof={(requiresControlProof ? 1 : 0)}; requires_bulk_proof={(requiresBulkProof ? 1 : 0)}; " +
                $"total_messages_received_since_last={totalMessagesReceivedSinceLast}; total_bytes_received_since_last={totalBytesReceivedSinceLast}; " +
                $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; media_messages_received_since_last={mediaMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; " +
                $"control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}; active_file_transfer_sessions={Math.Max(0, Volatile.Read(ref activeFileTransferDataSessions))}; active_file_transfer_runtime_sessions={activeFileTransferRuntimeTransfers.Count}");
        }

        if (Interlocked.Exchange(ref receiveStallRecoveryAwaitingReceiveProof, 0) == 0)
        {
            return;
        }

        var completedTick = Volatile.Read(ref receiveStallLastRecoveryCompletedTick);
        var resumeAfterRecoveryMs = completedTick > 0
            ? Math.Max(0, (long)Stopwatch.GetElapsedTime(completedTick, Stopwatch.GetTimestamp()).TotalMilliseconds)
            : -1;
        Log(
            "event=nkn_bridge_receive_stall_recovery_receive_resumed; " +
            $"connect_key={connectKey}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}; resume_after_recovery_ms={resumeAfterRecoveryMs}; " +
            $"total_messages_received_since_last={totalMessagesReceivedSinceLast}; total_bytes_received_since_last={totalBytesReceivedSinceLast}; " +
            $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; media_messages_received_since_last={mediaMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}");
        EmitBridgeLifecycle(new BridgeLifecycleEvent(
            BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
            StartMode: null,
            Pid: bridgeSupervisor.CurrentPid,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: ElapsedSinceTicksMilliseconds(bridgeSupervisor.CurrentSpawnTicks),
            ExitCode: null,
            ExitReasonKind: null,
            ExitReasonText: "receive_stall_recovery_receive_resumed"));
    }

    private void MaybeLogReceiveStallRecoveryUnproven(
        string connectKey,
        long totalMessagesReceivedSinceLast,
        long totalBytesReceivedSinceLast,
        long controlMessagesReceivedSinceLast,
        long mediaMessagesReceivedSinceLast,
        long bulkMessagesReceivedSinceLast,
        long controlLastReceivedAgeMs,
        long bulkLastReceivedAgeMs,
        bool requiresControlProof,
        bool requiresBulkProof)
    {
        var nowTick = Stopwatch.GetTimestamp();
        var lastLogTick = Volatile.Read(ref receiveStallRecoveryLastUnprovenLogTick);
        if (lastLogTick > 0 && Stopwatch.GetElapsedTime(lastLogTick, nowTick) < ScreenShareBridgeLogInterval)
        {
            return;
        }

        Volatile.Write(ref receiveStallRecoveryLastUnprovenLogTick, nowTick);
        Log(
            "event=nkn_bridge_receive_stall_recovery_unproven; " +
            $"connect_key={connectKey}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}; " +
            $"requires_control_proof={(requiresControlProof ? 1 : 0)}; requires_bulk_proof={(requiresBulkProof ? 1 : 0)}; " +
            $"total_messages_received_since_last={totalMessagesReceivedSinceLast}; total_bytes_received_since_last={totalBytesReceivedSinceLast}; " +
            $"control_messages_received_since_last={controlMessagesReceivedSinceLast}; media_messages_received_since_last={mediaMessagesReceivedSinceLast}; bulk_messages_received_since_last={bulkMessagesReceivedSinceLast}; " +
            $"control_last_received_age_ms={controlLastReceivedAgeMs}; bulk_last_received_age_ms={bulkLastReceivedAgeMs}");
    }

    private void SetScreenShareQueueState(BridgeScreenShareQueueState nextState)
    {
        TaskCompletionSource<long>? changed = null;
        bool shouldLogTransition = false;
        bool previousCongested;
        bool previousSevere;
        lock (screenShareQueueStateGate)
        {
            previousCongested = screenShareQueueState.IsCongested;
            previousSevere = screenShareQueueState.IsSevere;
            if (screenShareQueueState.Equals(nextState))
            {
                return;
            }

            screenShareQueueState = nextState;
            screenShareQueueStateVersion++;
            changed = screenShareQueueStateChangedTcs;
            screenShareQueueStateChangedTcs = CreateQueueStateChangedTcs();
            shouldLogTransition = previousCongested != nextState.IsCongested || previousSevere != nextState.IsSevere;
        }

        if (nextState.DroppedSinceLast > 0)
        {
            NknRuntimeDiagnostics.IncrementScreenShareLaneCongestionHit();
        }

        if (shouldLogTransition)
        {
            Log(
                $"event=screenshare_bridge_queue_state; congested={(nextState.IsCongested ? 1 : 0)}; severe={(nextState.IsSevere ? 1 : 0)}; queue_depth={nextState.QueueDepth}; queued_bytes={nextState.QueuedBytes}; oldest_queued_age_ms={nextState.OldestQueuedAgeMs}; dropped_since_last={nextState.DroppedSinceLast}; mode={FormatBridgeScreenShareQueueMode(nextState.Mode)}");
        }

        ScreenShareQueueStateChanged?.Invoke(this, new BridgeScreenShareQueueStateChangedEventArgs(nextState));
        changed?.TrySetResult(screenShareQueueStateVersion);
    }

    private void SetBulkQueueState(BridgeBulkQueueState nextState)
    {
        TaskCompletionSource<long>? changed = null;
        bool shouldLogTransition = false;
        bool previousCongested;
        bool previousSevere;
        lock (bulkQueueStateGate)
        {
            previousCongested = bulkQueueState.IsCongested;
            previousSevere = bulkQueueState.IsSevere;
            if (bulkQueueState.Equals(nextState))
            {
                return;
            }

            bulkQueueState = nextState;
            bulkQueueStateVersion++;
            changed = bulkQueueStateChangedTcs;
            bulkQueueStateChangedTcs = CreateQueueStateChangedTcs();
            shouldLogTransition = previousCongested != nextState.IsCongested ||
                                  previousSevere != nextState.IsSevere ||
                                  nextState.ClearedSinceLast > 0;
        }

        if (shouldLogTransition)
        {
            Log(
                $"event=nkn_bridge_bulk_queue_state; congested={(nextState.IsCongested ? 1 : 0)}; severe={(nextState.IsSevere ? 1 : 0)}; queue_depth={nextState.QueueDepth}; queued_bytes={nextState.QueuedBytes}; oldest_queued_age_ms={nextState.OldestQueuedAgeMs}; in_flight={nextState.InFlightCount}; in_flight_bytes={nextState.InFlightBytes}; configured_concurrency={nextState.ConfiguredConcurrency}; effective_concurrency={nextState.EffectiveConcurrency}; cleared_since_last={nextState.ClearedSinceLast}");
        }

        changed?.TrySetResult(bulkQueueStateVersion);
    }

    private static string FormatBridgeScreenShareQueueMode(BridgeScreenShareQueueMode mode)
        => mode == BridgeScreenShareQueueMode.CatchUpOnly ? "catch_up_only" : "normal";

    private void RecordScreenShareTransportHealthIssueFromBridgeLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (!LooksLikeScreenShareTransportHealthIssue(line))
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        long count;
        bool severe;
        long oldestAgeMs;
        lock (screenShareHealthGate)
        {
            recentScreenShareHealthIssuesUtc.Enqueue(nowUtc);
            PruneScreenShareHealthIssuesUnsafe(nowUtc);
            var state = BuildCurrentScreenShareHealthStateUnsafe(nowUtc);
            count = state.RecentIssueCount;
            severe = state.IsSevere;
            oldestAgeMs = state.OldestIssueAgeMs;
        }

        Log(
            $"event=screenshare_bridge_health_issue; recent_issue_count={count}; severe={(severe ? 1 : 0)}; oldest_issue_age_ms={oldestAgeMs}; detail={SensitiveDataRedactor.Redact(line)}");
    }

    private static bool LooksLikeScreenShareTransportHealthIssue(string line)
    {
        return line.Contains("RpcTimeoutError", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("rpc timeout", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("ConnectToNodeTimeoutError", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("connect timeout", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("WebSocket error", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Wait for reply timeout", StringComparison.OrdinalIgnoreCase);
    }

    private void PruneScreenShareHealthIssuesUnsafe(DateTimeOffset nowUtc)
    {
        while (recentScreenShareHealthIssuesUtc.Count > 0 &&
               nowUtc - recentScreenShareHealthIssuesUtc.Peek() > ScreenShareHealthIssueWindow)
        {
            recentScreenShareHealthIssuesUtc.Dequeue();
        }
    }

    private BridgeScreenShareHealthState BuildCurrentScreenShareHealthStateUnsafe(DateTimeOffset nowUtc)
    {
        var count = recentScreenShareHealthIssuesUtc.Count;
        if (count == 0)
        {
            return new BridgeScreenShareHealthState(0, false, 0);
        }

        var oldestAgeMs = Math.Max(0L, (long)(nowUtc - recentScreenShareHealthIssuesUtc.Peek()).TotalMilliseconds);
        return new BridgeScreenShareHealthState(
            RecentIssueCount: count,
            IsSevere: count >= ScreenShareHealthSevereThreshold,
            OldestIssueAgeMs: oldestAgeMs);
    }

    public void Dispose()
    {
        if (disposed || Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            lock (gate)
            {
                shuttingDown = true;
            }

            try
            {
                StopPingLoopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                CancelPingLoop();
            }

            try
            {
                if (bridgeSupervisor.IsProcessRunning)
                {
                    bridgeSupervisor.RequestShutdownAndCleanupAsync(
                        sendShutdownAsync: shutdownCt => SendCommandAndWaitAckAsync(
                            "shutdown",
                            payload: null,
                            shutdownCt,
                            timeoutOverride: CommandAckTimeout),
                        CancellationToken.None,
                        shutdownReason: "dispose").GetAwaiter().GetResult();
                }
                else
                {
                    bridgeSupervisor.CleanupState();
                }
            }
            catch
            {
                // Fall back to forceful local cleanup if graceful shutdown is no longer possible.
                bridgeSupervisor.CleanupState();
            }
        }
        catch
        {
            // Best-effort shutdown in dispose.
        }
        finally
        {
            NknRuntimeDiagnostics.SetMediaPlaneAttached(false);
            ReleaseIdentityUsageLease();
            disposed = true;
        }
    }

    private async Task EnsureProcessStartedAsync(CancellationToken ct)
    {
        var identity = RefreshAndLogBridgeBundleIdentity();
        BridgeBundleStartupGuard.EnsureTrustedForStartup(identity, Log, RecordBridgeFailure);
        await bridgeSupervisor.EnsureStartedAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            connectAttempts.ResetPendingReadyForNewProcessStart();
            helloCompleted = false;
            Interlocked.Exchange(ref disconnectedRaised, 0);
            shuttingDown = false;
        }
    }

    private async Task AcquireIdentityUsageLeaseAsync(CancellationToken ct)
    {
        SemaphoreSlim? existingLease;
        lock (gate)
        {
            existingLease = heldIdentityUsageLease;
        }

        if (existingLease is not null)
        {
            return;
        }

        var leaseKey = string.IsNullOrWhiteSpace(options.KeyPath)
            ? "(default)"
            : Path.GetFullPath(options.KeyPath);
        var lease = IdentityUsageLeases.GetOrAdd(leaseKey, static _ => new SemaphoreSlim(1, 1));
        await lease.WaitAsync(ct).ConfigureAwait(false);

        lock (gate)
        {
            if (disposed)
            {
                lease.Release();
                throw new ObjectDisposedException(nameof(RealNknClientAdapter));
            }

            if (heldIdentityUsageLease is null)
            {
                heldIdentityUsageLease = lease;
                return;
            }
        }

        lease.Release();
    }

    private void ReleaseIdentityUsageLease()
    {
        SemaphoreSlim? lease = null;
        lock (gate)
        {
            if (heldIdentityUsageLease is not null)
            {
                lease = heldIdentityUsageLease;
                heldIdentityUsageLease = null;
            }
        }

        lease?.Release();
    }

    private async Task EnsureHelloHandshakeAsync(CancellationToken ct)
    {
        long spawnTicksSnapshot;
        int? pidSnapshot;
        bool alreadyHelloCompleted;
        lock (gate)
        {
            spawnTicksSnapshot = bridgeSupervisor.CurrentSpawnTicks;
            pidSnapshot = bridgeSupervisor.CurrentPid;
            alreadyHelloCompleted = helloCompleted;
        }

        if (alreadyHelloCompleted)
        {
            var pingStopwatch = Stopwatch.StartNew();
            await SendBridgePingAndWaitPongAsync(PingTimeout, ct);
            pingStopwatch.Stop();

            EmitBridgeLifecycle(new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.Ready,
                BridgeStartMode.Warm,
                pidSnapshot,
                ReadyTimeMs: 0d,
                PingRttMs: pingStopwatch.Elapsed.TotalMilliseconds,
                UptimeMs: ElapsedSinceTicksMilliseconds(spawnTicksSnapshot),
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: string.Empty));
            return;
        }

        try
        {
            var helloResponse = await SendCommandAndWaitBridgeEventAsync(
                "hello",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["protocol"] = BridgeProtocolVersion,
                    ["appVersion"] = GetAssemblyVersionString(),
                },
                BridgeWaitKind.HelloOk,
                HelloTimeout,
                ct);
            ValidateHelloProtocolOrThrow(helloResponse);

            var pingStopwatch = Stopwatch.StartNew();
            await SendBridgePingAndWaitPongAsync(PingTimeout, ct);
            pingStopwatch.Stop();

            lock (gate)
            {
                helloCompleted = true;
            }

            EmitBridgeLifecycle(new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.Ready,
                BridgeStartMode.Cold,
                pidSnapshot,
                ReadyTimeMs: ElapsedSinceTicksMilliseconds(spawnTicksSnapshot),
                PingRttMs: pingStopwatch.Elapsed.TotalMilliseconds,
                UptimeMs: ElapsedSinceTicksMilliseconds(spawnTicksSnapshot),
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: string.Empty));

            Log($"Bridge hello_ok + pong (ping_rtt_ms={pingStopwatch.Elapsed.TotalMilliseconds:0.##})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log("Bridge hello canceled by caller token");
            throw;
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException)
            {
                NknRuntimeDiagnostics.SetLastError("bridge_ping_timeout");
                SetNknStartFailed("bridge_unresponsive", ex.Message);
                RecordBridgeFailure("bridge_unresponsive", "The local helper process did not respond.");
            }
            else
            {
                NknRuntimeDiagnostics.SetLastError("bridge_hello_failed");
                SetNknStartFailed("hello_failed", $"{ex.GetType().Name}: {ex.Message}");
                RecordBridgeFailure("bridge_hello_failed", "Could not start the local helper process.");
            }

            Log($"Bridge hello/ping failed ({ex.GetType().Name})");
            throw new InvalidOperationException($"NKN bridge hello failed: {ex.Message}", ex);
        }
    }

    private async Task<JsonElement> SendBridgePingAndWaitPongAsync(TimeSpan timeout, CancellationToken ct)
    {
        ThrowIfDisposed();

        try
        {
            return await protocolClient.SendPingAndWaitPongAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            NknRuntimeDiagnostics.SetLastError("bridge_ping_timeout");
            throw new TimeoutException("Timed out waiting for bridge pong.", ex);
        }
    }

    private async Task SendCommandAndWaitAckAsync(
        string cmd,
        Dictionary<string, object?>? payload,
        CancellationToken ct,
        TimeSpan? timeoutOverride = null,
        Action<int>? onSerialized = null)
    {
        ThrowIfDisposed();

        var timeout = timeoutOverride ?? CommandAckTimeout;
        try
        {
            await protocolClient.SendCommandAndWaitAckAsync(cmd, payload, timeout, ct, onSerialized).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            NknRuntimeDiagnostics.SetLastError($"bridge_{cmd}_timeout");
            if (string.Equals(cmd, "shutdown", StringComparison.Ordinal))
            {
                RecordBridgeFailure("bridge_shutdown_timeout", "The local helper process took too long to close.");
            }
            throw new TimeoutException($"Timed out waiting for bridge response to '{cmd}'.", ex);
        }
    }

    private async Task<JsonElement> SendCommandAndWaitBridgeEventAsync(
        string cmd,
        Dictionary<string, object?>? payload,
        BridgeWaitKind waitKind,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        return await protocolClient.SendCommandAndWaitBridgeEventAsync(cmd, payload, waitKind, timeout, ct).ConfigureAwait(false);
    }

    private void ValidateHelloProtocolOrThrow(JsonElement root)
    {
        if (TryGetInt32(root, "protocol", out var protocol) && protocol == BridgeProtocolVersion)
        {
            return;
        }

        var actualProtocol = TryGetInt32(root, "protocol", out var actual)
            ? actual.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "(none)";
        var message =
            $"bridge_protocol_outdated: Installed bridge hello protocol {actualProtocol} does not match required protocol {BridgeProtocolVersion}. " +
            "Reinstall/update nLink package.";
        NknRuntimeDiagnostics.SetLastError("bridge_protocol_outdated");
        SetNknStartFailed("bridge_protocol_outdated", message);
        RecordBridgeFailure("bridge_protocol_outdated", "Installed bridge hello protocol version is outdated.");
        Log(message);
        throw new InvalidOperationException(message);
    }

    private void HandleMessage(JsonElement root)
    {
        var source = TryGetString(root, "source", out var s) ? s : string.Empty;
        var payloadBase64 = TryGetString(root, "payloadBase64", out var p) ? p : string.Empty;
        var hasChannel = TryGetString(root, "channel", out var channelText);
        var channel = hasChannel
            ? ParseBridgeChannel(channelText)
            : NknBridgeChannel.Control;
        var isTopic = root.TryGetProperty("isTopic", out var isTopicProp) && isTopicProp.ValueKind == JsonValueKind.True;
        var topic = TryGetString(root, "topic", out var t) ? t : null;

        NknRuntimeDiagnostics.IncrementBridgeRawMessagesReceived();
        NknRuntimeDiagnostics.SetLastBridgeMessage(source, isTopic);

        if (string.IsNullOrWhiteSpace(payloadBase64))
        {
            return;
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(payloadBase64);
        }
        catch (FormatException)
        {
            Log("Bridge message ignored (invalid payloadBase64)");
            return;
        }

        if (payloadBytes.Length > MaxPayloadBytes)
        {
            NknRuntimeDiagnostics.SetLastError("bridge_incoming_payload_too_large");
            Log($"Bridge message ignored (payload too large, payload_len={payloadBytes.Length})");
            return;
        }

        HandleInboundBridgeMessage(source, payloadBytes, channel, isTopic, topic);
    }

    private void HandleBinaryBridgeFrame(BridgeBinaryFrame frame)
    {
        if (frame.Kind != BridgeBinaryFrameKind.Message)
        {
            Log($"Bridge binary frame ignored (kind={(byte)frame.Kind})");
            return;
        }

        NknRuntimeDiagnostics.IncrementBridgeRawMessagesReceived();
        NknRuntimeDiagnostics.SetLastBridgeMessage(frame.PrimaryText, frame.IsTopic);
        if (frame.Payload.Length > MaxPayloadBytes)
        {
            NknRuntimeDiagnostics.SetLastError("bridge_incoming_payload_too_large");
            Log($"Bridge binary frame ignored (payload too large, payload_len={frame.Payload.Length})");
            return;
        }

        var bridgeMessageObservedUtcMs = 0L;
        var socketDataEventEmittedUtcMs = 0L;
        var wsReceiverWriteEnteredUtcMs = 0L;
        var wsMessageEmittedUtcMs = 0L;
        var sdkHandleMsgEnteredUtcMs = 0L;
        var clientMessageDispatchUtcMs = 0L;
        var multiClientMessageDispatchUtcMs = 0L;
        string? topic = frame.SecondaryText;
        if (frame.Channel == NknBridgeChannel.Media &&
            !frame.IsTopic &&
            !string.IsNullOrWhiteSpace(frame.SecondaryText))
        {
            if (TryParseBridgeMediaTimingMetadata(frame.SecondaryText, out var timingMetadata))
            {
                bridgeMessageObservedUtcMs = timingMetadata.BridgeMessageObservedUtcMs;
                socketDataEventEmittedUtcMs = timingMetadata.SocketDataEventEmittedUtcMs;
                wsReceiverWriteEnteredUtcMs = timingMetadata.WsReceiverWriteEnteredUtcMs;
                wsMessageEmittedUtcMs = timingMetadata.WsMessageEmittedUtcMs;
                sdkHandleMsgEnteredUtcMs = timingMetadata.SdkHandleMsgEnteredUtcMs;
                clientMessageDispatchUtcMs = timingMetadata.ClientMessageDispatchUtcMs;
                multiClientMessageDispatchUtcMs = timingMetadata.MultiClientMessageDispatchUtcMs;
                topic = null;
            }
        }

        HandleInboundBridgeMessage(
            frame.PrimaryText,
            frame.Payload,
            frame.Channel,
            frame.IsTopic,
            topic,
            bridgeMessageObservedUtcMs,
            frame.BinaryFrameDecodedUtcMs,
            socketDataEventEmittedUtcMs,
            wsReceiverWriteEnteredUtcMs,
            wsMessageEmittedUtcMs,
            sdkHandleMsgEnteredUtcMs,
            clientMessageDispatchUtcMs,
            multiClientMessageDispatchUtcMs);
    }

    private void HandleInboundBridgeMessage(
        string source,
        byte[] payloadBytes,
        NknBridgeChannel channel,
        bool isTopic,
        string? topic,
        long bridgeMessageObservedUtcMs = 0,
        long binaryFrameDecodedUtcMs = 0,
        long socketDataEventEmittedUtcMs = 0,
        long wsReceiverWriteEnteredUtcMs = 0,
        long wsMessageEmittedUtcMs = 0,
        long sdkHandleMsgEnteredUtcMs = 0,
        long clientMessageDispatchUtcMs = 0,
        long multiClientMessageDispatchUtcMs = 0)
    {
        var bridgeIngressObservedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (channel == NknBridgeChannel.Media)
        {
            NknRuntimeDiagnostics.IncrementBridgeMediaMessagesReceived();
            NknRuntimeDiagnostics.AddBridgeMediaBytesReceived(payloadBytes.Length);
            MaybeLogScreenShareMessageSummary(payloadBytes.Length, source.Length, isTopic);
        }
        else if (channel == NknBridgeChannel.Bulk)
        {
            MaybeLogBridgeBulkMessageSummary(payloadBytes.Length, source.Length, isTopic);
        }
        else
        {
            NknRuntimeDiagnostics.IncrementBridgeControlMessagesReceived();
            NknRuntimeDiagnostics.AddBridgeControlBytesReceived(payloadBytes.Length);
            MaybeLogBridgeMessageSummary(payloadBytes.Length, source.Length, isTopic);
        }

        var handler = MessageReceived;
        var subscriberPresent = handler is not null;
        var matchesLocalControl = AddressesLikelySamePeer(source, Address);
        var matchesLocalMedia = AddressesLikelySamePeer(source, MediaAddress);
        var matchesLocalBulk = AddressesLikelySamePeer(source, BulkAddress);
        RecordInboundDelivery(
            channel,
            payloadBytes.Length,
            subscriberPresent,
            isTopic,
            matchesLocalControl,
            matchesLocalMedia,
            matchesLocalBulk,
            source.Length,
            HashLogValue(source));

        try
        {
            handler?.Invoke(
                this,
                new NknIncomingMessage(
                    source,
                    payloadBytes,
                    isTopic,
                    topic,
                    channel,
                    bridgeIngressObservedUtcMs,
                    bridgeMessageObservedUtcMs,
                    binaryFrameDecodedUtcMs,
                    socketDataEventEmittedUtcMs,
                    wsReceiverWriteEnteredUtcMs,
                    wsMessageEmittedUtcMs,
                    sdkHandleMsgEnteredUtcMs,
                    clientMessageDispatchUtcMs,
                    multiClientMessageDispatchUtcMs));
        }
        catch (Exception ex)
        {
            RecordInboundDeliveryHandlerFailure(channel);
            Log(
                $"event=nkn_bridge_inbound_delivery_failed; channel={FormatBridgeChannel(channel)}; payload_bytes={payloadBytes.Length}; source_len={source.Length}; is_topic={(isTopic ? 1 : 0)}; ex={ex.GetType().Name}");
            throw;
        }
        finally
        {
            MaybeLogInboundDeliverySummary(channel);
        }
    }

    private static bool TryParseBridgeMediaTimingMetadata(string? secondaryText, out BridgeMediaTimingMetadata metadata)
    {
        metadata = default;
        if (string.IsNullOrWhiteSpace(secondaryText))
        {
            return false;
        }

        var trimmed = secondaryText.Trim();
        if (long.TryParse(trimmed, out var legacyBridgeMessageObservedUtcMs))
        {
            metadata = new BridgeMediaTimingMetadata(
                legacyBridgeMessageObservedUtcMs,
                SocketDataEventEmittedUtcMs: 0,
                WsReceiverWriteEnteredUtcMs: 0,
                WsMessageEmittedUtcMs: 0,
                SdkHandleMsgEnteredUtcMs: 0,
                ClientMessageDispatchUtcMs: 0,
                MultiClientMessageDispatchUtcMs: 0);
            return true;
        }

        if (trimmed.IndexOf('=') < 0)
        {
            return false;
        }

        var bridgeMessageObservedUtcMs = 0L;
        var socketDataEventEmittedUtcMs = 0L;
        var wsReceiverWriteEnteredUtcMs = 0L;
        var wsMessageEmittedUtcMs = 0L;
        var sdkHandleMsgEnteredUtcMs = 0L;
        var clientMessageDispatchUtcMs = 0L;
        var multiClientMessageDispatchUtcMs = 0L;

        foreach (var segment in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == segment.Length - 1)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (!long.TryParse(value, out var parsedValue) || parsedValue <= 0)
            {
                continue;
            }

            switch (key)
            {
                case "b":
                    bridgeMessageObservedUtcMs = parsedValue;
                    break;
                case "s":
                    socketDataEventEmittedUtcMs = parsedValue;
                    break;
                case "r":
                    wsReceiverWriteEnteredUtcMs = parsedValue;
                    break;
                case "w":
                    wsMessageEmittedUtcMs = parsedValue;
                    break;
                case "h":
                    sdkHandleMsgEnteredUtcMs = parsedValue;
                    break;
                case "c":
                    clientMessageDispatchUtcMs = parsedValue;
                    break;
                case "m":
                    multiClientMessageDispatchUtcMs = parsedValue;
                    break;
            }
        }

        if (bridgeMessageObservedUtcMs <= 0 &&
            socketDataEventEmittedUtcMs <= 0 &&
            wsReceiverWriteEnteredUtcMs <= 0 &&
            wsMessageEmittedUtcMs <= 0 &&
            sdkHandleMsgEnteredUtcMs <= 0 &&
            clientMessageDispatchUtcMs <= 0 &&
            multiClientMessageDispatchUtcMs <= 0)
        {
            return false;
        }

        metadata = new BridgeMediaTimingMetadata(
            bridgeMessageObservedUtcMs,
            socketDataEventEmittedUtcMs,
            wsReceiverWriteEnteredUtcMs,
            wsMessageEmittedUtcMs,
            sdkHandleMsgEnteredUtcMs,
            clientMessageDispatchUtcMs,
            multiClientMessageDispatchUtcMs);
        return true;
    }

    event EventHandler<BridgeScreenShareQueueStateChangedEventArgs>? IBridgeScreenShareQueueCapability.ScreenShareQueueStateChanged
    {
        add => ScreenShareQueueStateChanged += value;
        remove => ScreenShareQueueStateChanged -= value;
    }

    private static string BuildFallbackMediaAddress(string identifier, string controlAddress)
    {
        var normalizedIdentifier = string.IsNullOrWhiteSpace(identifier) ? "nlink-media" : identifier.Trim() + "-media";
        if (!string.IsNullOrWhiteSpace(controlAddress))
        {
            var separatorIndex = controlAddress.IndexOf('.');
            if (separatorIndex >= 0 && separatorIndex < controlAddress.Length - 1)
            {
                return normalizedIdentifier + controlAddress[separatorIndex..];
            }
        }

        return normalizedIdentifier;
    }

    private static string BuildFallbackBulkAddress(string identifier, string controlAddress)
    {
        var normalizedIdentifier = string.IsNullOrWhiteSpace(identifier) ? "nlink-bulk" : identifier.Trim() + "-bulk";
        if (!string.IsNullOrWhiteSpace(controlAddress))
        {
            var separatorIndex = controlAddress.IndexOf('.');
            if (separatorIndex >= 0 && separatorIndex < controlAddress.Length - 1)
            {
                return normalizedIdentifier + controlAddress[separatorIndex..];
            }
        }

        return normalizedIdentifier;
    }

    private static NknBridgeChannel ParseBridgeChannel(string? channelText)
    {
        if (string.Equals(channelText, "media", StringComparison.OrdinalIgnoreCase))
        {
            return NknBridgeChannel.Media;
        }

        if (string.Equals(channelText, "bulk", StringComparison.OrdinalIgnoreCase))
        {
            return NknBridgeChannel.Bulk;
        }

        return NknBridgeChannel.Control;
    }

    private static string FormatBridgeChannel(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => "media",
            NknBridgeChannel.Bulk => "bulk",
            _ => "control",
        };

    private static bool AddressesLikelySamePeer(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        var leftTail = GetAddressTail(left);
        var rightTail = GetAddressTail(right);
        return LooksLikeNknPubKeyTail(leftTail) &&
               LooksLikeNknPubKeyTail(rightTail) &&
               string.Equals(leftTail, rightTail, StringComparison.Ordinal);
    }

    private static string GetAddressTail(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var span = address.AsSpan().Trim();
        var lastDot = span.LastIndexOf('.');
        return lastDot < 0 || lastDot == span.Length - 1
            ? span.ToString()
            : span[(lastDot + 1)..].ToString();
    }

    private static bool LooksLikeNknPubKeyTail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var isHex =
                (ch >= '0' && ch <= '9') ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static string HashLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash, 0, Math.Min(hash.Length, 8)).ToLowerInvariant();
    }

    private void ValidateBridgeCapabilitiesOrThrow(BridgeReadyInfo readyInfo)
    {
        if (readyInfo.Protocol != BridgeProtocolVersion)
        {
            var actualProtocol = readyInfo.Protocol?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)";
            var protocolMessage =
                $"bridge_protocol_outdated: Installed bridge protocol {actualProtocol} does not match required protocol {BridgeProtocolVersion}. " +
                "Reinstall/update nLink package.";
            NknRuntimeDiagnostics.SetLastError("bridge_protocol_outdated");
            SetNknStartFailed("bridge_protocol_outdated", protocolMessage);
            RecordBridgeFailure("bridge_protocol_outdated", "Installed bridge protocol version is outdated.");
            Log(protocolMessage);
            throw new InvalidOperationException(protocolMessage);
        }

        var missingChannels = RequiredBridgeChannels
            .Where(required => !readyInfo.SupportsChannel(required))
            .ToArray();
        if (missingChannels.Length == 0)
        {
            return;
        }

        var channelsSummary = readyInfo.ChannelsSummary;
        var message =
            $"{BridgeProtocolOutdatedBulkMissingCode}: Installed bridge does not support required channels " +
            $"[{string.Join(",", missingChannels)}]; supported=[{channelsSummary}]. Reinstall/update nLink package.";
        NknRuntimeDiagnostics.SetLastError(BridgeProtocolOutdatedBulkMissingCode);
        SetNknStartFailed("bridge_protocol_outdated", message);
        RecordBridgeFailure(BridgeProtocolOutdatedBulkMissingCode, "Installed bridge does not support the required transport channels.");
        Log(message);
        throw new InvalidOperationException(message);
    }

    private void EnsureChannelSupported(NknBridgeChannel channel)
    {
        string requiredChannel = channel switch
        {
            NknBridgeChannel.Media => "media",
            NknBridgeChannel.Bulk => "bulk",
            _ => "control",
        };

        string[] channelsSnapshot;
        lock (gate)
        {
            channelsSnapshot = supportedBridgeChannels;
        }

        if (channelsSnapshot.Any(value => string.Equals(value, requiredChannel, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var channelsSummary = channelsSnapshot.Length == 0
            ? "(none)"
            : string.Join(",", channelsSnapshot.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));
        throw new InvalidOperationException(
            $"{BridgeProtocolOutdatedBulkMissingCode}: Installed bridge does not support {requiredChannel} channel; supported=[{channelsSummary}]. Reinstall/update nLink package.");
    }

    private void MaybeLogScreenShareMessageSummary(int payloadLength, int sourceLength, bool isTopic)
    {
        Interlocked.Increment(ref screenShareBridgeMessageCountSinceLastLog);
        Interlocked.Add(ref screenShareBridgePayloadBytesSinceLastLog, payloadLength);

        var nowTick = Stopwatch.GetTimestamp();
        var previousTick = Volatile.Read(ref lastScreenShareBridgeSummaryLogTick);
        if (previousTick == 0)
        {
            if (Interlocked.CompareExchange(ref lastScreenShareBridgeSummaryLogTick, nowTick, 0) == 0)
            {
                var initialMessageCount = Interlocked.Exchange(ref screenShareBridgeMessageCountSinceLastLog, 0);
                var initialPayloadBytes = Interlocked.Exchange(ref screenShareBridgePayloadBytesSinceLastLog, 0);
                Log(
                    $"Bridge screenshare first inbound traffic (messages={initialMessageCount}, payload_bytes={initialPayloadBytes}, source_len={sourceLength}, is_topic={isTopic})");
            }
            return;
        }

        if (Stopwatch.GetElapsedTime(previousTick, nowTick) < ScreenShareBridgeLogInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastScreenShareBridgeSummaryLogTick, nowTick, previousTick) != previousTick)
        {
            return;
        }

        var messageCount = Interlocked.Exchange(ref screenShareBridgeMessageCountSinceLastLog, 0);
        var totalPayloadBytes = Interlocked.Exchange(ref screenShareBridgePayloadBytesSinceLastLog, 0);
        Log(
            $"Bridge screenshare traffic (messages={messageCount}, payload_bytes={totalPayloadBytes}, source_len={sourceLength}, is_topic={isTopic})");
    }

    private void MaybeLogBridgeSendSummary(int payloadLength, int destinationLength)
    {
        Interlocked.Increment(ref bridgeSendCountSinceLastLog);
        Interlocked.Add(ref bridgeSendPayloadBytesSinceLastLog, payloadLength);

        var nowTick = Stopwatch.GetTimestamp();
        var previousTick = Volatile.Read(ref lastBridgeSendSummaryLogTick);
        if (previousTick == 0)
        {
            Interlocked.CompareExchange(ref lastBridgeSendSummaryLogTick, nowTick, 0);
            return;
        }

        if (Stopwatch.GetElapsedTime(previousTick, nowTick) < BridgeTrafficLogInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastBridgeSendSummaryLogTick, nowTick, previousTick) != previousTick)
        {
            return;
        }

        var messageCount = Interlocked.Exchange(ref bridgeSendCountSinceLastLog, 0);
        var totalPayloadBytes = Interlocked.Exchange(ref bridgeSendPayloadBytesSinceLastLog, 0);
        Log($"Bridge outbound traffic (messages={messageCount}, payload_bytes={totalPayloadBytes}, dest_len={destinationLength})");
    }

    private void MaybeLogBridgeMessageSummary(int payloadLength, int sourceLength, bool isTopic)
    {
        Interlocked.Increment(ref bridgeMessageCountSinceLastLog);
        Interlocked.Add(ref bridgeMessagePayloadBytesSinceLastLog, payloadLength);

        var nowTick = Stopwatch.GetTimestamp();
        var previousTick = Volatile.Read(ref lastBridgeMessageSummaryLogTick);
        if (previousTick == 0)
        {
            Interlocked.CompareExchange(ref lastBridgeMessageSummaryLogTick, nowTick, 0);
            return;
        }

        if (Stopwatch.GetElapsedTime(previousTick, nowTick) < BridgeTrafficLogInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastBridgeMessageSummaryLogTick, nowTick, previousTick) != previousTick)
        {
            return;
        }

        var messageCount = Interlocked.Exchange(ref bridgeMessageCountSinceLastLog, 0);
        var totalPayloadBytes = Interlocked.Exchange(ref bridgeMessagePayloadBytesSinceLastLog, 0);
        Log($"Bridge control/session traffic (messages={messageCount}, payload_bytes={totalPayloadBytes}, source_len={sourceLength}, is_topic={isTopic})");
    }

    private void MaybeLogBridgeBulkMessageSummary(int payloadLength, int sourceLength, bool isTopic)
    {
        Interlocked.Increment(ref bulkBridgeMessageCountSinceLastLog);
        Interlocked.Add(ref bulkBridgeMessagePayloadBytesSinceLastLog, payloadLength);

        var nowTick = Stopwatch.GetTimestamp();
        var previousTick = Volatile.Read(ref lastBulkBridgeMessageSummaryLogTick);
        if (previousTick == 0)
        {
            Interlocked.CompareExchange(ref lastBulkBridgeMessageSummaryLogTick, nowTick, 0);
            return;
        }

        if (Stopwatch.GetElapsedTime(previousTick, nowTick) < BridgeTrafficLogInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastBulkBridgeMessageSummaryLogTick, nowTick, previousTick) != previousTick)
        {
            return;
        }

        var messageCount = Interlocked.Exchange(ref bulkBridgeMessageCountSinceLastLog, 0);
        var totalPayloadBytes = Interlocked.Exchange(ref bulkBridgeMessagePayloadBytesSinceLastLog, 0);
        Log($"Bridge filetransfer bulk traffic (messages={messageCount}, payload_bytes={totalPayloadBytes}, source_len={sourceLength}, is_topic={isTopic})");
    }

    private void RecordInboundDelivery(
        NknBridgeChannel channel,
        int payloadLength,
        bool subscriberPresent,
        bool isTopic,
        bool sourceMatchesLocalControl,
        bool sourceMatchesLocalMedia,
        bool sourceMatchesLocalBulk,
        int sourceLength,
        string sourceHash)
    {
        var counters = GetInboundDeliveryCounters(channel);
        Interlocked.Increment(ref counters.MessageCount);
        Interlocked.Add(ref counters.PayloadBytes, payloadLength);
        if (subscriberPresent)
        {
            Interlocked.Increment(ref counters.SubscriberPresentCount);
        }
        else
        {
            Interlocked.Increment(ref counters.SubscriberMissingCount);
        }

        if (isTopic)
        {
            Interlocked.Increment(ref counters.TopicCount);
        }

        if (sourceMatchesLocalControl)
        {
            Interlocked.Increment(ref counters.SourceMatchesLocalControlCount);
        }

        if (sourceMatchesLocalMedia)
        {
            Interlocked.Increment(ref counters.SourceMatchesLocalMediaCount);
        }

        if (sourceMatchesLocalBulk)
        {
            Interlocked.Increment(ref counters.SourceMatchesLocalBulkCount);
        }

        if (sourceMatchesLocalControl || sourceMatchesLocalMedia || sourceMatchesLocalBulk)
        {
            Interlocked.Increment(ref counters.SourceMatchesAnyLocalCount);
        }

        Volatile.Write(ref counters.LastSourceLength, sourceLength);
        counters.LastSourceHash = sourceHash;
    }

    private void RecordInboundDeliveryHandlerFailure(NknBridgeChannel channel)
    {
        var counters = GetInboundDeliveryCounters(channel);
        Interlocked.Increment(ref counters.HandlerFailureCount);
    }

    private void MaybeLogInboundDeliverySummary(NknBridgeChannel channel)
    {
        var counters = GetInboundDeliveryCounters(channel);
        var nowTick = Stopwatch.GetTimestamp();
        var previousTick = Volatile.Read(ref counters.LastSummaryLogTick);
        if (previousTick == 0)
        {
            if (Interlocked.CompareExchange(ref counters.LastSummaryLogTick, nowTick, 0) == 0)
            {
                EmitInboundDeliverySummary(channel, counters, initial: true);
            }

            return;
        }

        if (Stopwatch.GetElapsedTime(previousTick, nowTick) < BridgeTrafficLogInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref counters.LastSummaryLogTick, nowTick, previousTick) != previousTick)
        {
            return;
        }

        EmitInboundDeliverySummary(channel, counters, initial: false);
    }

    private void EmitInboundDeliverySummary(NknBridgeChannel channel, InboundDeliveryCounters counters, bool initial)
    {
        var messageCount = Interlocked.Exchange(ref counters.MessageCount, 0);
        var payloadBytes = Interlocked.Exchange(ref counters.PayloadBytes, 0);
        var subscriberPresent = Interlocked.Exchange(ref counters.SubscriberPresentCount, 0);
        var subscriberMissing = Interlocked.Exchange(ref counters.SubscriberMissingCount, 0);
        var handlerFailures = Interlocked.Exchange(ref counters.HandlerFailureCount, 0);
        var topicCount = Interlocked.Exchange(ref counters.TopicCount, 0);
        var sourceMatchesLocalControl = Interlocked.Exchange(ref counters.SourceMatchesLocalControlCount, 0);
        var sourceMatchesLocalMedia = Interlocked.Exchange(ref counters.SourceMatchesLocalMediaCount, 0);
        var sourceMatchesLocalBulk = Interlocked.Exchange(ref counters.SourceMatchesLocalBulkCount, 0);
        var sourceMatchesAnyLocal = Interlocked.Exchange(ref counters.SourceMatchesAnyLocalCount, 0);
        Log(
            "event=nkn_bridge_inbound_delivery_summary; " +
            $"channel={FormatBridgeChannel(channel)}; " +
            $"messages={messageCount}; " +
            $"payload_bytes={payloadBytes}; " +
            $"subscriber_present_count={subscriberPresent}; " +
            $"subscriber_missing_count={subscriberMissing}; " +
            $"handler_failure_count={handlerFailures}; " +
            $"source_matches_local_control_count={sourceMatchesLocalControl}; " +
            $"source_matches_local_media_count={sourceMatchesLocalMedia}; " +
            $"source_matches_local_bulk_count={sourceMatchesLocalBulk}; " +
            $"source_matches_any_local_count={sourceMatchesAnyLocal}; " +
            $"topic_count={topicCount}; " +
            $"last_source_len={Volatile.Read(ref counters.LastSourceLength)}; " +
            $"last_source_hash={counters.LastSourceHash}; " +
            $"initial={(initial ? 1 : 0)}");
    }

    private InboundDeliveryCounters GetInboundDeliveryCounters(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => mediaInboundDeliveryCounters,
            NknBridgeChannel.Bulk => bulkInboundDeliveryCounters,
            _ => controlInboundDeliveryCounters,
        };

    private void HandleBridgeDisconnected(JsonElement root)
    {
        var reason = TryGetString(root, "reason", out var r) ? r : "bridge_disconnected";
        lock (gate)
        {
            if (suppressBridgeDisconnectDuringReceiveStallRecovery)
            {
                Log($"event=nkn_bridge_receive_stall_recovery_disconnect_suppressed; reason={SanitizeLogToken(reason)}");
                return;
            }
        }

        NknRuntimeDiagnostics.SetBridgeLastExit(exitCode: null, reason: reason);
        SignalDisconnected(reason);
    }

    private void SignalDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref disconnectedRaised, 1) != 0)
        {
            return;
        }

        NknRuntimeDiagnostics.SetAuthoritativeConnectedAddressResolved(false);
        NknRuntimeDiagnostics.SetMediaPlaneAttached(false);
        SetScreenShareQueueState(new BridgeScreenShareQueueState(
            QueueDepth: 0,
            QueuedBytes: 0,
            OldestQueuedAgeMs: 0,
            InFlight: false,
            DroppedSinceLast: 0,
            IsCongested: false,
            IsSevere: false,
            Mode: BridgeScreenShareQueueMode.Normal));
        SetBulkQueueState(new BridgeBulkQueueState(
            QueueDepth: 0,
            QueuedBytes: 0,
            OldestQueuedAgeMs: 0,
            InFlight: false,
            InFlightCount: 0,
            InFlightBytes: 0,
            ConfiguredConcurrency: 1,
            EffectiveConcurrency: 1,
            ClearedSinceLast: 0,
            IsCongested: false,
            IsSevere: false));

        if (!string.Equals(reason, "shutdown", StringComparison.OrdinalIgnoreCase))
        {
            NknRuntimeDiagnostics.SetLastDisconnectReason(reason);
        }
        Log($"Bridge disconnected ({reason})");
        FailPendingOperations(reason);

        if (!shuttingDown && !disposed)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task HandleUnexpectedProcessExitAsync(string reason)
    {
        if (disposed)
        {
            return;
        }

        bool expectedShutdown;
        bool wasConnected;
        lock (gate)
        {
            expectedShutdown = shuttingDown;
            wasConnected = connectAttempts.WasConnected();
        }

        if (expectedShutdown)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref unexpectedRestartLoopActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Log($"Bridge exited unexpectedly ({reason})");
            FailPendingOperations(reason);
            await StopPingLoopAsync();
            await bridgeSupervisor.CleanupStateAsync(waitForReaders: true, "unexpected_exit").ConfigureAwait(false);

            var retryPolicy = new RetryPolicy(UnexpectedExitRestartRetryOptions);
            retryPolicy.EventEmitted += OnUnexpectedExitRetryPolicyEvent;
            try
            {
                var retryResult = await retryPolicy.ExecuteAsync(
                    async (_, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (disposed)
                        {
                            throw new OperationCanceledException("disposed");
                        }

                        await EnsureProcessStartedAsync(ct).ConfigureAwait(false);
                        await EnsureHelloHandshakeAsync(ct).ConfigureAwait(false);

                        if (wasConnected)
                        {
                            await ConnectAsync(ct).ConfigureAwait(false);
                        }

                        NknRuntimeDiagnostics.IncrementBridgeRestartCount();
                        Log("Bridge restart succeeded");
                    },
                    resetBetweenAttemptsAsync: (_, _) =>
                    {
                        NknRuntimeDiagnostics.SetLastError("bridge_restart_retry_failed");
                        return bridgeSupervisor.CleanupStateAsync(waitForReaders: true, "restart_retry_reset");
                    },
                    CancellationToken.None,
                    shouldRetry: _ => !disposed).ConfigureAwait(false);

                if (retryResult.Succeeded)
                {
                    return;
                }
            }
            finally
            {
                retryPolicy.EventEmitted -= OnUnexpectedExitRetryPolicyEvent;
            }

            NknRuntimeDiagnostics.SetLastError("bridge_restart_failed");
            RecordBridgeFailure("bridge_restart_exhausted", "The local helper process kept closing.");
            SignalDisconnected("bridge_restart_failed");
        }
        finally
        {
            Interlocked.Exchange(ref unexpectedRestartLoopActive, 0);
        }
    }

    private void OnUnexpectedExitRetryPolicyEvent(object? sender, RetryEvent e)
    {
        switch (e.Kind)
        {
            case RetryEventKind.AttemptStart:
                Log($"Bridge restart attempt {e.Attempt}/{e.MaxAttempts} starting");
                break;
            case RetryEventKind.AttemptScheduled:
                Log($"Bridge restart retry {Math.Min(e.Attempt + 1, e.MaxAttempts)}/{e.MaxAttempts} in {(e.Delay?.TotalSeconds ?? 0):0}s");
                break;
            case RetryEventKind.AttemptSuccess:
                Log($"Bridge restart attempt {e.Attempt}/{e.MaxAttempts} succeeded");
                break;
            case RetryEventKind.FinalFail:
                Log($"Bridge restart failed after {e.Attempt}/{e.MaxAttempts} attempts ({(string.IsNullOrWhiteSpace(e.ExceptionType) ? "Unknown" : e.ExceptionType)})");
                break;
        }
    }

    private void FailPendingOperations(string reason)
    {
        connectAttempts.FailPendingReady(reason);

        protocolClient.FailPendingOperations(reason);

        CancelPingLoop();
    }

    private void CleanupProcessState()
    {
        bridgeSupervisor.CleanupState();
    }

    private BridgeBundleIdentity? GetBridgeBundleIdentity()
    {
        lock (gate)
        {
            return bridgeBundleIdentity;
        }
    }

    private BridgeBundleIdentity RefreshAndLogBridgeBundleIdentity()
    {
        var bridgePath = ResolveBridgeScriptPath();
        var identity = BridgeBundleIdentity.Load(bridgePath);
        lock (gate)
        {
            bridgeBundleIdentity = identity;
        }

        NknRuntimeDiagnostics.SetBridgeBundleIdentity(identity);
        Log($"event=bridge_bundle_loaded{identity.BuildStructuredLogFields()}");
        if (identity.HasMismatch)
        {
            Log($"event=bridge_bundle_mismatch_detected; classification=installed_payload_drift; reason={identity.ManifestStatus}{identity.BuildStructuredLogFields()}");
        }

        return identity;
    }

    private void StartPingLoopIfNeeded()
    {
        lock (gate)
        {
            if (pingLoopTask is not null && !pingLoopTask.IsCompleted)
            {
                return;
            }

            pingLoopCts?.Cancel();
            pingLoopCts?.Dispose();
            pingLoopCts = new CancellationTokenSource();
            pingLoopTask = Task.Run(() => PingLoopAsync(pingLoopCts.Token), CancellationToken.None);
        }
    }

    private async Task StopPingLoopAsync()
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (gate)
        {
            task = pingLoopTask;
            cts = pingLoopCts;
            pingLoopTask = null;
            pingLoopCts = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        if (task is not null)
        {
            try { await task; } catch { }
        }

        cts?.Dispose();
    }

    private void CancelPingLoop()
    {
        lock (gate)
        {
            try { pingLoopCts?.Cancel(); } catch { }
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var consecutiveMisses = 0;

        try
        {
            while (!ct.IsCancellationRequested && !disposed)
            {
                await Task.Delay(PingInterval, ct);
                ct.ThrowIfCancellationRequested();

                try
                {
                    await SendBridgePingAndWaitPongAsync(PingTimeout, ct);
                    consecutiveMisses = 0;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    consecutiveMisses++;
                    Log($"Ping miss ({consecutiveMisses}/3, ex={ex.GetType().Name})");

                    if (consecutiveMisses < 3)
                    {
                        continue;
                    }

                    NknRuntimeDiagnostics.SetLastError("bridge_ping_timeout");
                    RecordBridgeFailure("bridge_ping_timeout", "The local helper process stopped responding.");
                    if (await TryRecoverBridgePingTimeoutForActiveFileTransferAsync().ConfigureAwait(false))
                    {
                        consecutiveMisses = 0;
                        continue;
                    }

                    await RestartBridgeProcessAsync().ConfigureAwait(false);
                    SignalDisconnected("bridge_ping_timeout");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    internal Task<bool> RecoverBridgePingTimeoutForActiveFileTransferForTestsAsync() =>
        TryRecoverBridgePingTimeoutForActiveFileTransferAsync();

    private async Task<bool> TryRecoverBridgePingTimeoutForActiveFileTransferAsync()
    {
        var activeFileTransferSessionCount = GetActiveFileTransferSessionCountForPingRecovery();
        if (activeFileTransferSessionCount <= 0)
        {
            return false;
        }

        var awaitingReceiveProof = Volatile.Read(ref receiveStallRecoveryAwaitingReceiveProof) != 0;
        var recoveryInProgress = Volatile.Read(ref receiveStallRecoveryInProgress) != 0;
        var recoveryCount = Volatile.Read(ref receiveStallRecoveryCount);
        var activeOnlyRecovery = !awaitingReceiveProof && !recoveryInProgress && recoveryCount <= 0;
        if (activeOnlyRecovery)
        {
            Log(
                "event=nkn_bridge_ping_timeout_filetransfer_recovery_forced; " +
                $"reason=active_filetransfer; active_file_transfer_sessions={activeFileTransferSessionCount}");
        }

        var requiresControlProof = Volatile.Read(ref receiveStallRecoveryRequiresControlProof) != 0;
        var requiresBulkProof = Volatile.Read(ref receiveStallRecoveryRequiresBulkProof) != 0;
        Log(
            "event=nkn_bridge_ping_timeout_filetransfer_recovery_started; " +
            $"active_file_transfer_sessions={activeFileTransferSessionCount}; " +
            $"recovery_count={recoveryCount}; awaiting_receive_proof={(awaitingReceiveProof ? 1 : 0)}; recovery_in_progress={(recoveryInProgress ? 1 : 0)}; " +
            $"requires_control_proof={(requiresControlProof ? 1 : 0)}; requires_bulk_proof={(requiresBulkProof ? 1 : 0)}");
        EmitBridgeLifecycle(new BridgeLifecycleEvent(
            BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
            StartMode: null,
            Pid: bridgeSupervisor.CurrentPid,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: ElapsedSinceTicksMilliseconds(bridgeSupervisor.CurrentSpawnTicks),
            ExitCode: null,
            ExitReasonKind: null,
            ExitReasonText: "bridge_ping_timeout"));

        if (!await RestartBridgeProcessAsync(reconnectAfterRestart: true).ConfigureAwait(false))
        {
            Log(
                "event=nkn_bridge_ping_timeout_filetransfer_recovery_failed; " +
                $"reason=reconnect_failed; active_file_transfer_sessions={activeFileTransferSessionCount}; recovery_count={recoveryCount}");
            return false;
        }

        Interlocked.Exchange(ref receiveStallConsecutiveWindows, 0);
        Interlocked.Exchange(ref receiveStallBulkConsecutiveWindows, 0);
        Interlocked.Exchange(ref receiveStallControlConsecutiveWindows, 0);
        Interlocked.Exchange(ref receiveStallRecoveryAwaitingReceiveProof, 1);
        Interlocked.Exchange(ref receiveStallRecoveryLastUnprovenLogTick, 0);
        Volatile.Write(ref receiveStallRecoveryRequiresControlProof, 1);
        Volatile.Write(ref receiveStallRecoveryRequiresBulkProof, 1);
        Volatile.Write(ref receiveStallLastRecoveryCompletedTick, Stopwatch.GetTimestamp());
        Log(
            "event=nkn_bridge_ping_timeout_disconnect_suppressed; " +
            $"reason=active_filetransfer_recovery; active_file_transfer_sessions={activeFileTransferSessionCount}; recovery_count={Volatile.Read(ref receiveStallRecoveryCount)}");
        EmitBridgeLifecycle(new BridgeLifecycleEvent(
            BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
            StartMode: null,
            Pid: bridgeSupervisor.CurrentPid,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: ElapsedSinceTicksMilliseconds(bridgeSupervisor.CurrentSpawnTicks),
            ExitCode: null,
            ExitReasonKind: null,
            ExitReasonText: "bridge_ping_timeout_reconnected"));
        return true;
    }

    private async Task<bool> RestartBridgeProcessAsync(bool reconnectAfterRestart = false)
    {
        lock (gate)
        {
            shuttingDown = true;
        }
        bridgeSupervisor.MarkForcedKillRequested();
        await bridgeSupervisor.CleanupStateAsync(waitForReaders: true, "restart").ConfigureAwait(false);

        try
        {
            if (!disposed)
            {
                await EnsureProcessStartedAsync(CancellationToken.None);
                await EnsureHelloHandshakeAsync(CancellationToken.None);
                if (reconnectAfterRestart)
                {
                    connectAttempts.ResetPendingReadyForNewProcessStart();
                    Interlocked.Exchange(ref receiveStallRecoveryConnectActive, 1);
                    try
                    {
                        await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref receiveStallRecoveryConnectActive, 0);
                    }
                }

                NknRuntimeDiagnostics.IncrementBridgeRestartCount();
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log($"Bridge restart failed ({ex.GetType().Name})");
            return false;
        }
        finally
        {
            lock (gate)
            {
                if (!disposed)
                {
                    shuttingDown = false;
                }
            }
        }
    }

    private double? GetCurrentBridgeUptimeMs()
    {
        return ElapsedSinceTicksMilliseconds(bridgeSupervisor.CurrentSpawnTicks);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.Null)
        {
            value = string.Empty;
            return true;
        }

        value = prop.ToString();
        return true;
    }

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = default;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value);
    }

    private static bool TryGetInt64(JsonElement root, string propertyName, out long value)
    {
        value = default;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value);
    }

    private static bool TryGetInt64OrBool(JsonElement root, string propertyName, out long value)
    {
        value = default;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
        {
            return true;
        }

        if (prop.ValueKind == JsonValueKind.True)
        {
            value = 1;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.False)
        {
            value = 0;
            return true;
        }

        return false;
    }

    private static bool TryGetBool(JsonElement root, string propertyName, out bool value)
    {
        value = default;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        return false;
    }

    private static bool TryGetId(JsonElement root, out string id)
    {
        id = string.Empty;
        if (!root.TryGetProperty("id", out var prop))
        {
            return false;
        }

        switch (prop.ValueKind)
        {
            case JsonValueKind.String:
                id = prop.GetString() ?? string.Empty;
                return id.Length > 0;
            case JsonValueKind.Number:
                id = prop.GetRawText();
                return true;
            default:
                return false;
        }
    }

    private string ResolveBridgeScriptPath()
    {
        var overridePath = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", category: "bridge_runtime_path");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolved = Path.GetFullPath(overridePath);
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        var rid = GetBridgeRid();

#if DEBUG
        foreach (var candidate in EnumerateBridgeCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
#else
        var bundledBridge = Path.Combine(AppContext.BaseDirectory, "bridge", rid, "index.js");
        if (File.Exists(bundledBridge))
        {
            return bundledBridge;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var macResourcesBridge = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, "index.js"));
            if (File.Exists(macResourcesBridge))
            {
                return macResourcesBridge;
            }
        }
#endif

        throw new FileNotFoundException(
            $"NKN bridge script not found. Expected bridge/{rid}/index.js (or macOS Resources/bridge/{rid}/index.js) in app output.");
    }

    private string ResolveNodeExecutablePath()
    {
        var overridePath = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_NKN_NODE_PATH", category: "bridge_runtime_path");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolved = Path.GetFullPath(overridePath);
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        var rid = GetBridgeRid();
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";

        var bundled = Path.Combine(AppContext.BaseDirectory, "bridge", rid, exeName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var macResourcesNode = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, exeName));
            if (File.Exists(macResourcesNode))
            {
                return macResourcesNode;
            }
        }

#if DEBUG
        foreach (var candidate in EnumerateNodeCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "node";
#else
        throw new FileNotFoundException(
            $"Bundled Node runtime not found. Expected bridge/{rid}/{exeName} (or macOS Resources/bridge/{rid}/{exeName}) in app output.");
#endif
    }

    private static IEnumerable<string> EnumerateBridgeCandidates()
    {
        var rid = GetBridgeRid();

        yield return Path.Combine(AppContext.BaseDirectory, "bridge", rid, "index.js");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, "index.js"));
        }

        yield return Path.Combine(AppContext.BaseDirectory, "bridge", "index.js");
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "nkn-bridge", "index.js");

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            yield return Path.Combine(current.FullName, "tools", "nkn-bridge", "index.js");
            yield return Path.Combine(current.FullName, "artifacts", "bridge", rid, "index.js");
        }
    }

    private static IEnumerable<string> EnumerateNodeCandidates()
    {
        var rid = GetBridgeRid();
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";

        yield return Path.Combine(AppContext.BaseDirectory, "bridge", rid, exeName);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, exeName));
        }

        yield return Path.Combine(AppContext.BaseDirectory, "bridge", exeName);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            yield return Path.Combine(current.FullName, "artifacts", "bridge", rid, exeName);
            yield return Path.Combine(current.FullName, "tools", "node", rid, exeName);
        }
    }

    private static string GetAssemblyVersionString()
    {
        var assembly = typeof(RealNknClientAdapter).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            return plusIndex > 0 ? informational[..plusIndex] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.2.0";
    }

    private static string GetBridgeRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new NotSupportedException(
                    $"Unsupported bridge platform/architecture: macOS {RuntimeInformation.OSArchitecture}.")
            };
        }

        throw new NotSupportedException(
            $"Unsupported bridge platform/architecture: {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}.");
    }

    private static void EnsurePayloadWithinLimit(byte[] payload, string cmd)
    {
        if (payload.Length <= MaxPayloadBytes)
        {
            return;
        }

        NknRuntimeDiagnostics.SetLastError($"bridge_{cmd}_payload_too_large");
        throw new InvalidOperationException($"Bridge payload too large for '{cmd}' (max {MaxPayloadBytes} bytes).");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static void SetNknStartFailed(string shortReason, string? detail)
    {
        var safeDetail = SensitiveDataRedactor.Redact(detail ?? string.Empty).Trim();
        if (safeDetail.Length > 120)
        {
            safeDetail = safeDetail[..120];
        }

        if (string.IsNullOrWhiteSpace(safeDetail))
        {
            NknRuntimeDiagnostics.SetLastError($"NKN_START_FAILED: {shortReason}");
            return;
        }

        NknRuntimeDiagnostics.SetLastError($"NKN_START_FAILED: {shortReason} ({safeDetail})");
    }

    private static string BuildBridgeDiagnosticLogMessage(string prefix, string? detail)
    {
        var safePrefix = string.IsNullOrWhiteSpace(prefix) ? "bridge" : prefix.Trim();
        var safeDetail = SensitiveDataRedactor.Redact(detail ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(safeDetail) ? safePrefix : $"{safePrefix}: {safeDetail}";
    }

    private static string SanitizeLogToken(string? value)
    {
        var safe = SensitiveDataRedactor.Redact(value ?? string.Empty).Trim();
        if (safe.Length == 0)
        {
            return "(none)";
        }

        if (safe.Length > 160)
        {
            safe = safe[..160];
        }

        return safe
            .Replace(";", ",", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string BuildLastProgressSummaryForDiagnostics()
    {
        var snapshot = NknRuntimeDiagnostics.Snapshot();
        if (string.Equals(snapshot.LastProgressEventType, "(none)", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(" progress=").Append(snapshot.LastProgressEventType);
        if (!string.Equals(snapshot.LastSelectedRpc, "(none)", StringComparison.Ordinal))
        {
            builder.Append(", rpc=").Append(snapshot.LastSelectedRpc);
        }

        return builder.ToString();
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[nLink][NKN][Bridge] {message}");
        LocalOperationalLog.Info("NKN.Bridge", message);
    }

    private bool ShouldSuppressBridgeStderrDuringShutdown(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (!line.Contains("WebSocket was closed before the connection was established", StringComparison.Ordinal))
        {
            return false;
        }

        lock (gate)
        {
            return shuttingDown || disposed;
        }
    }

    private void RecordBridgeFailure(string errorCode, string? errorHint)
    {
        string mode;
        lock (gate)
        {
            mode = reliabilityModeHint;
        }

        SessionReliabilityLog.RecordStandalone(
            mode,
            transport: "NKN",
            stage: SessionReliabilityStage.Disconnected,
            errorCode: errorCode,
            errorHint: errorHint);
    }

    private void EmitBridgeLifecycle(BridgeLifecycleEvent evt)
    {
        try
        {
            BridgeLifecycle?.Invoke(this, evt);
        }
        catch
        {
            // Telemetry observers must not affect runtime behavior.
        }
    }

    private static double? ElapsedSinceTicksMilliseconds(long startTicks)
    {
        if (startTicks <= 0)
        {
            return null;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
        if (elapsedTicks < 0)
        {
            elapsedTicks = 0;
        }

        return elapsedTicks * 1000d / Stopwatch.Frequency;
    }

}

