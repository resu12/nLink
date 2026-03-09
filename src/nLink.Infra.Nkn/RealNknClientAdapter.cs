using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NLink.Core;
using NLink.Core.Logging;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;

namespace NLink.Infra.Nkn;

internal sealed class RealNknClientAdapter : INknClient, IBridgeProcessRunner, IAuthoritativeConnectedAddressSource
{
    private const int MaxPayloadBytes = 64 * 1024;
    private static readonly TimeSpan CommandAckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScreenShareBridgeLogInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BridgeTrafficLogInterval = TimeSpan.FromSeconds(2);
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
    private readonly ScreenShareFrameReassembler screenShareFrameReassembler = new();

    private readonly ConnectAttemptCoordinator connectAttempts = new();
    private TimeSpan? connectReadyTimeoutOverrideForTests;
    private CancellationTokenSource? pingLoopCts;
    private Task? pingLoopTask;
    private string address;
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
    private bool inboundScreenShareEnabled;
    private string? inboundScreenShareSessionId;
    private string? inboundScreenShareSourceAddress;
    private long inboundScreenShareExpiresAtUnixMs;
    private int disposeStarted;
    private SemaphoreSlim? heldIdentityUsageLease;

    public RealNknClientAdapter(NknIdentity identity, NknTransportOptions options)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        address = identity.Address;
        bridgeSupervisor = new BridgeSupervisor(
            callbacks: new BridgeSupervisorCallbacks
            {
                Log = Log,
                SignalDisconnected = SignalDisconnected,
                OnUnexpectedExitDetected = reason => _ = Task.Run(() => HandleUnexpectedProcessExitAsync(reason), CancellationToken.None),
                RecordBridgeFailure = RecordBridgeFailure,
                EmitBridgeLifecycle = EmitBridgeLifecycle,
            },
            resolveNodePath: ResolveNodeExecutablePath,
            resolveBridgePath: ResolveBridgeScriptPath,
            onStdoutLineAsync: (line, _, _, _) =>
            {
                protocolClient!.HandleStdoutJsonLine(line);
                return Task.CompletedTask;
            },
            onStderrLineAsync: (line, _, _, _) =>
            {
                Log(BuildBridgeDiagnosticLogMessage("bridge stderr", line));
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
            connectAttempts,
            getCurrentPid: () => bridgeSupervisor.CurrentPid,
            setConnectedAddress: addr =>
            {
                lock (gate)
                {
                    address = addr;
                }
            },
            log: Log);

        protocolClient = new BridgeProtocolClient(
            getWriter: () => bridgeSupervisor.GetActiveIoOrThrow().JsonlWriter,
            log: Log,
            onReady: root => protocolEventRouter.HandleReady(root),
            onRpcProgress: (eventName, root) => protocolEventRouter.HandleRpcProgress(eventName, root),
            onMessage: HandleMessage,
            onDisconnected: HandleBridgeDisconnected,
            onHelloOk: root => protocolEventRouter.HandleHelloOk(root),
            onPong: root => protocolEventRouter.HandlePong(root),
            onUnmatchedBridgeError: reason => SignalDisconnected("bridge_error:" + reason));

        screenShareFrameReassembler.FrameReady += OnScreenShareFrameReady;
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

    public event EventHandler<NknIncomingMessage>? MessageReceived;

    internal event EventHandler<ScreenShareFrameChunkV1>? ScreenShareFrameChunkReceived
    {
        add => screenShareFrameReassembler.ChunkAccepted += value;
        remove => screenShareFrameReassembler.ChunkAccepted -= value;
    }

    private event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompletedCore;
    private event EventHandler<string>? ScreenShareStoppedCore;

    internal event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted
    {
        add => ScreenShareFrameCompletedCore += value;
        remove => ScreenShareFrameCompletedCore -= value;
    }

    internal event EventHandler<string>? ScreenShareStopped
    {
        add => ScreenShareStoppedCore += value;
        remove => ScreenShareStoppedCore -= value;
    }

    public event EventHandler? Disconnected;
    internal event EventHandler<BridgeLifecycleEvent>? BridgeLifecycle;

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

    internal void SetConnectReadyTimeoutForTests(TimeSpan timeout)
    {
        connectReadyTimeoutOverrideForTests = timeout <= TimeSpan.Zero ? null : timeout;
    }

    internal void HandleStdoutJsonLineForTests(string line)
    {
        protocolClient.HandleStdoutJsonLine(line);
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
        TaskCompletionSource<string> readyWait;
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

            if (options.PreflightRpcEnabled)
            {
                payload["preflightRpcEnabled"] = true;
                payload["preflightTimeoutMs"] = options.PreflightTimeoutMs;
                payload["preflightConcurrency"] = options.PreflightConcurrency;
                payload["preflightCacheTtlMs"] = options.PreflightCacheTtlMs;
            }

            await SendCommandAndWaitAckAsync("connect", payload, ct, timeoutOverride: CommandAckTimeout);

            string readyAddress;
            try
            {
                readyAddress = await readyWait.Task.WaitAsync(connectReadyTimeoutOverrideForTests ?? ConnectReadyTimeout, ct);
            }
            catch (TimeoutException ex)
            {
                NknRuntimeDiagnostics.SetLastError("bridge_connect_ready_timeout");
                var progressSuffix = BuildLastProgressSummaryForDiagnostics();
                SetNknStartFailed("ready_timeout", $"Timed out waiting for bridge ready.{progressSuffix}");
                RecordBridgeFailure("bridge_connect_ready_timeout", $"The local helper process did not become ready.{progressSuffix}");
                throw new TimeoutException("Timed out waiting for NKN bridge ready(address) after connect.", ex);
            }

            lock (gate)
            {
                address = string.IsNullOrWhiteSpace(readyAddress) ? identity.Address : readyAddress;
            }

            StartPingLoopIfNeeded();
            Log($"Connected bridge (address_len={Address.Length})");
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
                    CancellationToken.None).ConfigureAwait(false);
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
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(payload);
        EnsurePayloadWithinLimit(payload, "send");

        MaybeLogBridgeSendSummary(payload.Length, destination.Length);
        var isScreenSharePayload = LooksLikeScreenSharePayload(payload);
        return SendCommandAndWaitAckAsync(
            "send",
            new Dictionary<string, object?>
            {
                ["destination"] = destination,
                ["payloadBase64"] = Convert.ToBase64String(payload),
            },
            ct,
            timeoutOverride: CommandAckTimeout,
            onSerialized: isScreenSharePayload
                ? bytes => NknRuntimeDiagnostics.AddScreenShareBridgeBytesSent(bytes)
                : null);
    }

    internal Task UpdateInboundScreenSharePolicyAsync(
        bool enabled,
        string? sessionId,
        string? sourceAddress,
        DateTimeOffset? expiresAtUtc,
        CancellationToken ct)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var normalizedSourceAddress = string.IsNullOrWhiteSpace(sourceAddress) ? null : sourceAddress.Trim();
        DateTimeOffset? normalizedExpiresAtUtc = expiresAtUtc is DateTimeOffset value ? value.ToUniversalTime() : null;
        var effectiveEnabled =
            enabled &&
            !string.IsNullOrWhiteSpace(normalizedSessionId) &&
            !string.IsNullOrWhiteSpace(normalizedSourceAddress) &&
            normalizedExpiresAtUtc is DateTimeOffset;

        lock (gate)
        {
            inboundScreenShareEnabled = effectiveEnabled;
            inboundScreenShareSessionId = effectiveEnabled ? normalizedSessionId : null;
            inboundScreenShareSourceAddress = effectiveEnabled ? normalizedSourceAddress : null;
            inboundScreenShareExpiresAtUnixMs = effectiveEnabled
                ? normalizedExpiresAtUtc!.Value.ToUnixTimeMilliseconds()
                : 0L;
        }

        if (!bridgeSupervisor.IsProcessRunning)
        {
            return Task.CompletedTask;
        }

        return SendCommandAndWaitAckAsync(
            "setScreenSharePolicy",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["enabled"] = effectiveEnabled,
                ["sessionId"] = effectiveEnabled ? normalizedSessionId : null,
                ["sourceAddress"] = effectiveEnabled ? normalizedSourceAddress : null,
                ["expiresAtUnixMs"] = effectiveEnabled ? inboundScreenShareExpiresAtUnixMs : null,
            },
            ct,
            timeoutOverride: CommandAckTimeout);
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
                        CancellationToken.None).GetAwaiter().GetResult();
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
            ReleaseIdentityUsageLease();
            disposed = true;
        }
    }

    private async Task EnsureProcessStartedAsync(CancellationToken ct)
    {
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
            await SendCommandAndWaitBridgeEventAsync(
                "hello",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["protocol"] = 1,
                    ["appVersion"] = GetAssemblyVersionString(),
                },
                BridgeWaitKind.HelloOk,
                HelloTimeout,
                ct);

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

    private void HandleMessage(JsonElement root)
    {
        var source = TryGetString(root, "source", out var s) ? s : string.Empty;
        var payloadBase64 = TryGetString(root, "payloadBase64", out var p) ? p : string.Empty;
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

        if (TryHandleScreenSharePayload(payloadBytes, source))
        {
            MaybeLogScreenShareMessageSummary(payloadBytes.Length, source.Length, isTopic);
            return;
        }

        MaybeLogBridgeMessageSummary(payloadBytes.Length, source.Length, isTopic);
        MessageReceived?.Invoke(this, new NknIncomingMessage(source, payloadBytes, isTopic, topic));
    }

    private bool TryHandleScreenSharePayload(byte[] payloadBytes, string? source)
    {
        if (!ScreenSharePayloadCodec.TryDeserialize(payloadBytes, out var chunk))
        {
            if (!ScreenSharePayloadCodec.TryDeserializeStop(payloadBytes, out var stop))
            {
                return false;
            }

            if (!TryAuthorizeInboundScreenShare(stop.SessionId, source, out var stopFailureReason))
            {
                screenShareFrameReassembler.ClearSession(stop.SessionId);
                LogInboundScreenShareDropped(stopFailureReason, source, stop.SessionId, "stop");
                return true;
            }

            screenShareFrameReassembler.ClearSession(stop.SessionId);
            try
            {
                ScreenShareStoppedCore?.Invoke(this, stop.SessionId);
            }
            catch (Exception ex)
            {
                Log($"Bridge screenshare stop dispatch failed ({ex.GetType().Name})");
            }
            return true;
        }

        if (!TryAuthorizeInboundScreenShare(chunk.SessionId, source, out var frameFailureReason))
        {
            screenShareFrameReassembler.ClearSession(chunk.SessionId);
            LogInboundScreenShareDropped(frameFailureReason, source, chunk.SessionId, "frame");
            return true;
        }

        screenShareFrameReassembler.OnChunk(chunk);
        return true;
    }

    private bool TryAuthorizeInboundScreenShare(string? sessionId, string? source, out string failureReason)
    {
        bool enabled;
        string? expectedSessionId;
        string? expectedSourceAddress;
        long expiresAtUnixMs;
        lock (gate)
        {
            enabled = inboundScreenShareEnabled;
            expectedSessionId = inboundScreenShareSessionId;
            expectedSourceAddress = inboundScreenShareSourceAddress;
            expiresAtUnixMs = inboundScreenShareExpiresAtUnixMs;
        }

        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!enabled)
        {
            failureReason = "policy_disabled";
            return false;
        }

        if (expiresAtUnixMs <= 0 || nowUnixMs >= expiresAtUnixMs)
        {
            failureReason = "approval_expired";
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            failureReason = "missing_session_id";
            return false;
        }

        if (!string.Equals(normalizedSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source";
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedSourceAddress) ||
            !NknSignalingTransport.AddressMatchesForSessionPolicy(normalizedSource, expectedSourceAddress))
        {
            failureReason = "source_mismatch";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private void LogInboundScreenShareDropped(string reason, string? source, string? sessionId, string messageType)
    {
        NknRuntimeDiagnostics.SetLastError($"bridge_screenshare_{reason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"bridge_screenshare_{reason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=bridge_screenshare_dropped; message_type={messageType}; reason={reason}; session_id={sessionId ?? "(none)"}; source={source ?? "(none)"}");
        Log(
            $"Bridge screenshare dropped before dispatch (type={messageType}, reason={reason}, session_id={sessionId ?? "(none)"}, source={source ?? "(none)"})");
    }

    private void MaybeLogScreenShareMessageSummary(int payloadLength, int sourceLength, bool isTopic)
    {
        Interlocked.Increment(ref screenShareBridgeMessageCountSinceLastLog);
        Interlocked.Add(ref screenShareBridgePayloadBytesSinceLastLog, payloadLength);

        var nowTick = Stopwatch.GetTimestamp();
        var previousTick = Volatile.Read(ref lastScreenShareBridgeSummaryLogTick);
        if (previousTick == 0)
        {
            Interlocked.CompareExchange(ref lastScreenShareBridgeSummaryLogTick, nowTick, 0);
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

    private void OnScreenShareFrameReady(object? sender, ScreenShareFrameReadyEventArgs e)
    {
        try
        {
            var metrics = screenShareFrameReassembler.GetMetricsSnapshot();
            ScreenShareFrameCompletedCore?.Invoke(
                this,
                new ScreenShareFrameCompletedEventArgs(
                    e.FrameId,
                    e.Width,
                    e.Height,
                    e.Encoding,
                    e.EncodedFrameBytes,
                    e.TimestampUnixMilliseconds,
                    ChunksDroppedOlderFrame: metrics.FramesDropped,
                    AssembliesExpired: 0,
                    SessionId: e.SessionId));
        }
        catch (Exception ex)
        {
            Log($"Bridge screenshare frame dispatch failed ({ex.GetType().Name})");
        }
    }

    private void HandleBridgeDisconnected(JsonElement root)
    {
        var reason = TryGetString(root, "reason", out var r) ? r : "bridge_disconnected";
        NknRuntimeDiagnostics.SetBridgeLastExit(exitCode: null, reason: reason);
        SignalDisconnected(reason);
    }

    private void SignalDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref disconnectedRaised, 1) != 0)
        {
            return;
        }

        screenShareFrameReassembler.ClearAll();
        NknRuntimeDiagnostics.SetAuthoritativeConnectedAddressResolved(false);

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
                    await RestartBridgeProcessAsync();
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

    private async Task RestartBridgeProcessAsync()
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
                NknRuntimeDiagnostics.IncrementBridgeRestartCount();
            }
        }
        catch (Exception ex)
        {
            Log($"Bridge restart failed ({ex.GetType().Name})");
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

    private static bool LooksLikeScreenSharePayload(ReadOnlySpan<byte> payload)
    {
        return payload.IndexOf("\"screenshare.frame.v1\""u8) >= 0 ||
               payload.IndexOf("\"screenshare.stop.v1\""u8) >= 0;
    }

    private string ResolveBridgeScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
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
            $"NKN bridge script not found. Expected bridge/{rid}/index.js (or macOS Resources/bridge/{rid}/index.js) in app output, or set NLINK_NKN_BRIDGE_PATH.");
    }

    private string ResolveNodeExecutablePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
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
        return "node";
#else
        throw new FileNotFoundException(
            $"Bundled Node runtime not found. Expected bridge/{rid}/{exeName} (or macOS Resources/bridge/{rid}/{exeName}) in app output, or set NLINK_NKN_NODE_PATH.");
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

