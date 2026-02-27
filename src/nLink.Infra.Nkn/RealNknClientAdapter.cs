using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NLink.Core;
using NLink.Core.Logging;
using NLink.Core.Retry;

namespace NLink.Infra.Nkn;

internal sealed class RealNknClientAdapter : INknClient, IBridgeProcessRunner
{
    private const int MaxPayloadBytes = 64 * 1024;
    private static readonly TimeSpan CommandAckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    private static readonly RetryPolicyOptions UnexpectedExitRestartRetryOptions = new(
        MaxAttempts: 5,
        InitialDelay: TimeSpan.FromSeconds(1),
        MaxDelay: TimeSpan.FromSeconds(16),
        JitterRatio: 0d);

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
    private int disconnectedRaised;
    private int unexpectedRestartLoopActive;
    private bool helloCompleted;
    private bool shuttingDown;
    private bool disposed;
    private string reliabilityModeHint = "Helper";

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
                Log($"bridge stderr: {line}");
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

            var seedBase64 = ReadPersistedSeedBase64(options.KeyPath);

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

        Log($"Bridge send (dest_len={destination.Length}, payload_len={payload.Length})");
        return SendCommandAndWaitAckAsync(
            "send",
            new Dictionary<string, object?>
            {
                ["destination"] = destination,
                ["payloadBase64"] = Convert.ToBase64String(payload),
            },
            ct,
            timeoutOverride: CommandAckTimeout);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            lock (gate)
            {
                shuttingDown = true;
            }

            CancelPingLoop();
            bridgeSupervisor.CleanupState();
        }
        catch
        {
            // Best-effort shutdown in dispose.
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
        TimeSpan? timeoutOverride = null)
    {
        ThrowIfDisposed();

        var timeout = timeoutOverride ?? CommandAckTimeout;
        try
        {
            await protocolClient.SendCommandAndWaitAckAsync(cmd, payload, timeout, ct).ConfigureAwait(false);
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

        byte[] payloadBytes;
        try
        {
            payloadBytes = string.IsNullOrWhiteSpace(payloadBase64)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(payloadBase64);
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

        Log($"Bridge message (source_len={source.Length}, payload_len={payloadBytes.Length}, is_topic={isTopic})");
        MessageReceived?.Invoke(this, new NknIncomingMessage(source, payloadBytes, isTopic, topic));
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

    private static string? ReadPersistedSeedBase64(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
        {
            return null;
        }

        using var stream = File.OpenRead(keyPath);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("SeedBase64", out var seedProp))
        {
            return null;
        }

        return seedProp.ValueKind == JsonValueKind.String ? seedProp.GetString() : null;
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

        return assembly.GetName().Version?.ToString() ?? "0.1.0";
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
        var safeDetail = (detail ?? string.Empty).Trim();
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

