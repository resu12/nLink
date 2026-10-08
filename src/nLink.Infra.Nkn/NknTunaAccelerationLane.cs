using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed partial class NknTunaAccelerationLane : INknTunaAccelerationSession
{
    private enum ClientRole
    {
        None = 0,
        Listener,
        Dialer,
    }

    private readonly object gate = new();
    private readonly Guid laneInstanceId = Guid.NewGuid();
    private readonly NknTunaAccelerationOptions options;
    private readonly INknTunaListenerSidecarSupervisor? listenerSupervisor;
    private NknTunaSidecarClient? client;
    private ClientRole clientRole;
    private NknTunaListenerStartRequest? listenerRequest;
    private NknTunaListenerAdoptionSnapshot? clientListenerLease;
    private NknTunaSidecarProcessOwner? dialerProcessOwner;
    private NknTunaSidecarProcessOwner? clientDialerProcessOwner;
    private Task<bool>? dialerStartTask;
    private long clientGeneration;
    private long failureGeneration;
    private NknAccelerationFailureOrigin? currentFailureOrigin;
    private NknAccelerationLaneDiagnostics lastDiagnostics = NknAccelerationLaneDiagnostics.Empty;
    private bool disposed;

    public NknTunaAccelerationLane(
        NknTunaAccelerationOptions options,
        INknTunaListenerSidecarSupervisor? listenerSupervisor = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.listenerSupervisor = listenerSupervisor;
        if (this.listenerSupervisor is not null)
        {
            this.listenerSupervisor.StateChanged += OnListenerSupervisorStateChanged;
        }
    }

    public bool IsAvailable
    {
        get
        {
            lock (gate)
            {
                return client?.IsAvailable == true;
            }
        }
    }

    public bool CanOfferListener => options.CanOfferListener && (listenerSupervisor?.CanOfferListener ?? true);

    public NknAccelerationLaneKind ConfiguredLanes => options.Lanes;

    public NknAccelerationLaneKind SupportedLanes
    {
        get
        {
            lock (gate)
            {
                return client is null ? options.Lanes : client.SupportedLanes;
            }
        }
    }

    public string? LocalTunaAddress
    {
        get
        {
            lock (gate)
            {
                return client?.LocalTunaAddress;
            }
        }
    }

    public bool IsLocalPaidListenerActive
    {
        get
        {
            lock (gate)
            {
                return clientRole == ClientRole.Listener && client?.IsAvailable == true;
            }
        }
    }

    public event EventHandler<NknIncomingMessage>? MessageReceived;

    public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;

    public bool IsFailureOriginCurrent(NknAccelerationFailureOrigin origin)
    {
        lock (gate)
        {
            return IsFailureOriginCurrentUnsafe(origin);
        }
    }

    public bool TryApplyFailureOrigin(NknAccelerationFailureOrigin origin, Func<bool> apply)
    {
        lock (gate)
        {
            // The callback may only perform the data-session's atomic availability
            // close. It must not enter transport/service gates or invoke route code.
            return IsFailureOriginCurrentUnsafe(origin) && apply();
        }
    }

    private bool IsFailureOriginCurrentUnsafe(NknAccelerationFailureOrigin origin)
        => !disposed && !origin.IsDefault && currentFailureOrigin == origin &&
            (client is null && dialerProcessOwner is null ||
             clientRole == ClientRole.Listener && clientGeneration == origin.ClientGeneration &&
             clientListenerLease?.ProcessRunId == origin.ProcessRunId);

    public NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot()
    {
        lock (gate)
        {
            if (client is null)
            {
                return lastDiagnostics;
            }

            lastDiagnostics = client.GetDiagnosticsSnapshot();
            return lastDiagnostics;
        }
    }

    public async Task<bool> EnsureListenerSidecarConnectedAsync(string expectedRemotePeer, CancellationToken ct)
        => await EnsureListenerSidecarConnectedAsync(
                new NknTunaListenerStartRequest(
                    string.IsNullOrWhiteSpace(expectedRemotePeer)
                        ? string.Empty
                        : expectedRemotePeer.Trim(),
                    options.Lanes),
                ct)
            .ConfigureAwait(false);

    public async Task<bool> EnsureListenerSidecarConnectedAsync(
        NknTunaListenerStartRequest request,
        CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return false;
        }

        LaneStartOperation operation;
        lock (gate)
        {
            if (disposed || ct.IsCancellationRequested || IsPreparationOwnedUnsafe()) return false;
            if (clientRole == ClientRole.Dialer && client?.IsAvailable == true) return false;
            if (client?.IsAvailable == true &&
                clientRole == ClientRole.Listener &&
                listenerRequest == request)
            {
                return true;
            }
            // Listener retries must not supersede an in-flight dialer or listener start.
            if (pendingLaneStart is not null || dialerStartTask is not null) return false;
            operation = BeginLaneStartUnsafe();
        }

        try { return await ConnectListenerAsync(request, operation, ct).ConfigureAwait(false); }
        finally { FinishLaneStart(operation); }
    }

    private async Task<bool> ConnectListenerAsync(NknTunaListenerStartRequest request,
        LaneStartOperation operation, CancellationToken ct)
    {
        NknTunaListenerAdoptionSnapshot? startedListener = null;
        var endpoint = operation.Preparation ? null : options.ListenerEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint) && listenerSupervisor is not null)
        {
            if (operation.Preparation)
            {
                startedListener = await listenerSupervisor.EnsureStartedIfIdleAsync(request, ct).ConfigureAwait(false);
                endpoint = startedListener?.Endpoint.LocalIpc;
            }
            else
            {
                var started = await listenerSupervisor.EnsureStartedAsync(request, ct).ConfigureAwait(false);
                endpoint = started?.LocalIpc;
                startedListener = listenerSupervisor.CaptureCurrentListenerAdoptionSnapshot();
            }
            if (startedListener?.CurrentRequest != request) startedListener = null;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        bool current;
        lock (gate) { current = IsLaneStartCurrentUnsafe(operation, ct); }
        if (!current)
        {
            StopExactListener(startedListener, operation, "listener_start_retired");
            return false;
        }

        var connectEndpoint = endpoint.Trim();
        var nextClient = new NknTunaSidecarClient(options.Lanes, options.QueueCapacity);
        nextClient.MessageReceived += OnClientMessageReceived;
        try
        {
            await ConnectStartedClientAsync(nextClient, connectEndpoint, operation, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_listener_sidecar_connect_failed; error={ex.GetType().Name}");
            nextClient.Dispose();
            StopExactListener(startedListener, operation, "listener_connect_failed");
            if (ex is OperationCanceledException) throw;
            return false;
        }

        nextClient.StateChanged += OnClientStateChanged;
        if (!ReplaceStartedClient(nextClient, ClientRole.Listener, null, operation, ct, request, startedListener))
        {
            StopExactListener(startedListener, operation, "listener_start_retired");
            return false;
        }

        ForwardCurrentClientReadyIfAvailable(nextClient, "ready");
        return true;
    }

    public NknTunaListenerAdoptionSnapshot? CaptureCurrentListenerAdoptionSnapshot()
    {
        lock (gate)
        {
            if (disposed ||
                client?.IsAvailable != true ||
                clientRole != ClientRole.Listener ||
                listenerRequest is null ||
                listenerSupervisor is null)
            {
                return null;
            }

            var snapshot = listenerSupervisor.CaptureCurrentListenerAdoptionSnapshot();
            return snapshot is not null &&
                   (clientListenerLease is null || snapshot == clientListenerLease) &&
                   snapshot.CurrentRequest == listenerRequest &&
                   string.Equals(
                       snapshot.Endpoint.TunaAddress,
                       client.LocalTunaAddress,
                       StringComparison.Ordinal)
                ? snapshot
                : null;
        }
    }

    public bool TryAdoptCurrentListener(
        NknTunaListenerAdoptionSnapshot snapshot,
        NknTunaListenerStartRequest successorRequest,
        out NknTunaListenerAdoptionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(successorRequest);
        lock (gate)
        {
            if (disposed ||
                client?.IsAvailable != true ||
                clientRole != ClientRole.Listener ||
                listenerRequest != snapshot.CurrentRequest ||
                (clientListenerLease is not null && snapshot != clientListenerLease) ||
                listenerSupervisor is null ||
                !string.Equals(
                    snapshot.Endpoint.TunaAddress,
                    client.LocalTunaAddress,
                    StringComparison.Ordinal) ||
                !listenerSupervisor.TryAdoptCurrentListener(
                    snapshot,
                    successorRequest))
            {
                receipt = default!;
                return false;
            }

            listenerRequest = successorRequest;
            clientListenerLease = snapshot with
            {
                CurrentRequest = successorRequest,
                OwnershipGeneration = snapshot.OwnershipGeneration + 1,
            };
            clientGeneration++; // Rebinding ownership invalidates predecessor cleanup.
            if (currentClientStart?.Preparation == true &&
                snapshot.CurrentRequest.LifetimePolicy == NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation &&
                !successorRequest.PreparationOnly)
            {
                currentClientStart.Revocation.Unregister();
                currentClientStart = null;
            }
            receipt = new NknTunaListenerAdoptionReceipt(
                snapshot,
                successorRequest,
                client.LocalTunaAddress ?? snapshot.Endpoint.TunaAddress);
            return true;
        }
    }

    public async Task<bool> StartDialerSidecarAsync(string tunaAddress, string expectedRemotePeer, CancellationToken ct)
    {
        Task<bool> startTask;
        lock (gate)
        {
            if (disposed || ct.IsCancellationRequested || IsPreparationOwnedUnsafe()) return false;
            if (client?.IsAvailable == true && clientRole == ClientRole.Dialer)
            {
                return true;
            }

            if (dialerStartTask is null)
            {
                var operation = BeginLaneStartUnsafe();
                dialerStartTask = StartOwnedDialerAsync(tunaAddress, expectedRemotePeer, operation, ct);
            }

            startTask = dialerStartTask;
        }

        try
        {
            return await startTask.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(dialerStartTask, startTask))
                {
                    dialerStartTask = null;
                }
            }
        }
    }

    private async Task<bool> StartOwnedDialerAsync(string tunaAddress, string expectedRemotePeer,
        LaneStartOperation operation, CancellationToken ct)
    {
        try { return await StartDialerSidecarCoreAsync(tunaAddress, expectedRemotePeer, operation, ct).ConfigureAwait(false); }
        finally { FinishLaneStart(operation); }
    }

    private async Task<bool> StartDialerSidecarCoreAsync(string tunaAddress, string expectedRemotePeer,
        LaneStartOperation operation, CancellationToken ct)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.SidecarExePath))
        {
            return false;
        }

        lock (gate)
        {
            if (client?.IsAvailable == true && clientRole == ClientRole.Dialer)
            {
                return true;
            }
        }

        if (!File.Exists(options.SidecarExePath))
        {
            LocalOperationalLog.Warn("NKN.Tuna", "event=tuna_dialer_sidecar_missing");
            return false;
        }

        lock (gate)
        {
            if (!IsLaneStartCurrentUnsafe(operation, ct)) return false;
            if (operation.Preparation && (client is not null || dialerProcessOwner is not null)) return false;
        }
        if (!operation.Preparation) StopCurrentListenerBeforeDialer();

        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastSidecarEvent = null;
        string? lastSidecarReason = null;
        NknTunaSidecarProcessOwner? owner = null;
        NknTunaSidecarClient? nextClient = null;
        var installed = false;
        var hasDialerSeed = !string.IsNullOrWhiteSpace(options.DialerSeedBase64);
        var dialerIdentifier = string.IsNullOrWhiteSpace(options.DialerIdentifier)
            ? null
            : options.DialerIdentifier.Trim();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.SidecarExePath,
                UseShellExecute = false,
                RedirectStandardInput = hasDialerSeed,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        process.StartInfo.ArgumentList.Add("dial");
        process.StartInfo.ArgumentList.Add("--to");
        process.StartInfo.ArgumentList.Add(tunaAddress);
        process.StartInfo.ArgumentList.Add("--local-ipc");
        process.StartInfo.ArgumentList.Add("127.0.0.1:0");
        if (options.TunaDialTimeoutMs > 0)
        {
            process.StartInfo.ArgumentList.Add("--tuna-dial-timeout-ms");
            process.StartInfo.ArgumentList.Add(options.TunaDialTimeoutMs.ToString(CultureInfo.InvariantCulture));
        }

        if (hasDialerSeed)
        {
            process.StartInfo.ArgumentList.Add("--seed-stdin");
        }

        if (!string.IsNullOrWhiteSpace(dialerIdentifier))
        {
            process.StartInfo.ArgumentList.Add("--identifier");
            process.StartInfo.ArgumentList.Add(dialerIdentifier);
        }

        process.StartInfo.ArgumentList.Add("--jsonl");

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }
            if (TryPreserveOwnedDialerTrace(owner, e.Data)) return;
            if (TryHandleReadyLine(e.Data, ready))
            {
                Interlocked.Exchange(ref lastSidecarEvent, "ready");
                return;
            }
            var eventName = TryExtractSidecarEvent(e.Data, out var reason);
            if (!string.IsNullOrWhiteSpace(eventName))
            {
                var safeEventName = SanitizeSidecarEventName(eventName);
                var safeReason = SanitizeSidecarReason(reason);
                Interlocked.Exchange(ref lastSidecarEvent, safeEventName);
                if (!string.IsNullOrWhiteSpace(safeReason))
                {
                    Interlocked.Exchange(ref lastSidecarReason, safeReason);
                }
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_dialer_sidecar_event; sidecar_event={safeEventName}; sidecar_reason={safeReason ?? ""}; line_len={e.Data.Length}");
                if (string.Equals(safeEventName, "tuna_bridge_terminal", StringComparison.Ordinal))
                {
                    LogBridgeTerminal(e.Data);
                }
                if (IsTerminalSidecarEvent(safeEventName))
                {
                    MarkDialerClientUnavailable(owner, $"sidecar_{safeReason ?? safeEventName}");
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                HandleOwnedDialerStderr(owner, e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            if (!ready.Task.IsCompleted)
            {
                ready.TrySetException(new InvalidOperationException("Tuna dialer sidecar exited before ready."));
            }

            MarkDialerClientUnavailable(owner, "dialer_exited");
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return false;
            }

            owner = NknTunaSidecarProcessOwner.Attach("dialer", process);
            lock (gate)
            {
                if (!IsLaneStartCurrentUnsafe(operation, ct)) return false;
                dialerProcessOwner = owner;
                currentFailureOrigin = null;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (hasDialerSeed)
            {
                await WriteDialerSeedAsync(process, options.DialerSeedBase64!, ct).ConfigureAwait(false);
            }

            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyCts.CancelAfter(options.DialerReadyTimeoutMs);
            using var readyRegistration = readyCts.Token.Register(() => ready.TrySetCanceled(readyCts.Token));
            var endpoint = await ready.Task.ConfigureAwait(false);
            nextClient = new NknTunaSidecarClient(options.Lanes, options.QueueCapacity);
            nextClient.MessageReceived += OnClientMessageReceived;
            nextClient.StateChanged += OnClientStateChanged;
            await ConnectStartedClientAsync(nextClient, endpoint, operation, ct).ConfigureAwait(false);

            if (!ReplaceStartedClient(nextClient, ClientRole.Dialer, owner, operation, ct))
            {
                StopDialerOwnerIfCurrent(owner, "dialer_replaced_by_current_client");
                return false;
            }
            installed = true;

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_dialer_sidecar_started; expected_remote_len={expectedRemotePeer?.Length ?? 0}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_dialer_sidecar_start_failed; error={ex.GetType().Name}; last_event={lastSidecarEvent ?? ""}; last_reason={lastSidecarReason ?? ""}");
            StopDialerOwnerIfCurrent(owner, "dialer_start_failed");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_dialer_sidecar_start_failed; error=ready_timeout; last_event={lastSidecarEvent ?? ""}; last_reason={lastSidecarReason ?? ""}");
            StopDialerOwnerIfCurrent(owner, "dialer_ready_timeout");
            return false;
        }
        catch (OperationCanceledException)
        {
            StopDialerOwnerIfCurrent(owner, "dialer_canceled");
            throw;
        }
        finally
        {
            if (!installed)
            {
                nextClient?.Dispose();
                StopDialerOwnerIfCurrent(owner, "dialer_start_not_installed");
                if (owner is null) process.Dispose();
            }
        }
    }

    public Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] envelopeBytes, CancellationToken ct)
    {
        NknTunaSidecarClient? current;
        NknAccelerationLaneDiagnostics diagnostics;
        ClientRole role;
        lock (gate)
        {
            current = client;
            diagnostics = current?.GetDiagnosticsSnapshot() ?? lastDiagnostics;
            role = clientRole;
        }

        if (current is null)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                "event=tuna_acceleration_lane_try_send_returned_false" +
                "; reason=client_missing" +
                $"; role={FormatClientRole(role)}" +
                $"; channel={MapBridgeChannel(lane)}" +
                $"; payload_bytes={Math.Max(0, envelopeBytes?.Length ?? 0)}" +
                $"; last_available={FormatBool(diagnostics.IsAvailable)}" +
                $"; last_unavailable_reason={SanitizeSidecarReason(diagnostics.LastUnavailableReason) ?? "none"}" +
                $"; terminal_sidecar_reason={SanitizeSidecarReason(diagnostics.TerminalSidecarReason) ?? "none"}" +
                $"; send_rejected={diagnostics.SendRejected}" +
                $"; queue_overflow={diagnostics.QueueOverflow}" +
                $"; bulk_accepted={diagnostics.BulkFramesAccepted}" +
                $"; bulk_written={diagnostics.BulkFramesWritten}" +
                $"; media_accepted={diagnostics.MediaFramesAccepted}" +
                $"; media_written={diagnostics.MediaFramesWritten}" +
                $"; control_accepted={diagnostics.ControlFramesAccepted}" +
                $"; control_written={diagnostics.ControlFramesWritten}");
            return Task.FromResult(false);
        }

        return current.TrySendAsync(lane, envelopeBytes, ct);
    }

    public Task StopAsync(string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NknTunaSidecarClient? clientToDispose;
        NknTunaSidecarProcessOwner? processToStop;
        lock (gate)
        {
            RetireLaneStartUnsafe();
            clientToDispose = client;
            processToStop = dialerProcessOwner;
            CaptureClientDiagnostics_NoLock(clientToDispose);
            if (clientToDispose is not null)
            {
                clientToDispose.MessageReceived -= OnClientMessageReceived;
                clientToDispose.StateChanged -= OnClientStateChanged;
            }

            client = null;
            clientRole = ClientRole.None;
            clientDialerProcessOwner = null;
            listenerRequest = null;
            dialerProcessOwner = null;
            dialerStartTask = null;
            currentFailureOrigin = null;
        }

        clientToDispose?.Dispose();
        listenerSupervisor?.Stop(string.IsNullOrWhiteSpace(reason) ? "stopped" : reason.Trim());
        if (processToStop is not null)
        {
            processToStop.Stop(reason);
        }

        LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_acceleration_lane_stopped; reason={SanitizeSidecarReason(reason) ?? "unknown"}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        NknTunaSidecarClient? clientToDispose;
        NknTunaSidecarProcessOwner? processToStop;
        lock (gate)
        {
            RetireLaneStartUnsafe();
            clientToDispose = client;
            processToStop = dialerProcessOwner;
            CaptureClientDiagnostics_NoLock(clientToDispose);
            if (clientToDispose is not null)
            {
                clientToDispose.MessageReceived -= OnClientMessageReceived;
                clientToDispose.StateChanged -= OnClientStateChanged;
            }

            client = null;
            clientRole = ClientRole.None;
            clientDialerProcessOwner = null;
            listenerRequest = null;
            dialerProcessOwner = null;
            dialerStartTask = null;
            currentFailureOrigin = null;
        }

        clientToDispose?.Dispose();
        if (listenerSupervisor is not null)
        {
            listenerSupervisor.StateChanged -= OnListenerSupervisorStateChanged;
            listenerSupervisor.Dispose();
        }
        if (processToStop is not null)
        {
            processToStop.Stop("disposed");
        }
    }

    private void OnClientMessageReceived(object? sender, NknIncomingMessage e)
        => MessageReceived?.Invoke(this, e);

    private void OnClientStateChanged(object? sender, AccelerationStateChangedEventArgs e)
    {
        NknTunaSidecarProcessOwner? processToStop = null;
        Action<string>? stopPreparedListener = null;
        NknTunaSidecarClient? failedPreparedClient = null;
        var forwardedEvent = e;
        var shouldForward = true;
        if (sender is NknTunaSidecarClient sidecarClient)
        {
            lock (gate)
            {
                if (ReferenceEquals(client, sidecarClient) &&
                    (clientRole != ClientRole.Dialer ||
                     ReferenceEquals(clientDialerProcessOwner, dialerProcessOwner)))
                {
                    lastDiagnostics = sidecarClient.GetDiagnosticsSnapshot();
                    var preparationIdentity = currentClientStart?.ExecutionIdentity;
                    if (preparationIdentity is not null)
                        forwardedEvent = new(e.IsAvailable, e.Reason, e.FailureOrigin, preparationIdentity);
                    if (!e.IsAvailable)
                    {
                        var alreadyReported = currentFailureOrigin is { } reported && reported.ClientGeneration == clientGeneration;
                        if (currentClientStart?.Preparation == true)
                        {
                            currentClientStart.Revocation.Unregister();
                            failedPreparedClient = sidecarClient;
                            sidecarClient.MessageReceived -= OnClientMessageReceived;
                            sidecarClient.StateChanged -= OnClientStateChanged;
                            if (clientListenerLease is { } preparedListener && listenerSupervisor is not null)
                                listenerSupervisor.TryDetachCurrentListener(preparedListener, out stopPreparedListener);
                        }
                        var runId = clientRole == ClientRole.Dialer ? clientDialerProcessOwner?.RunId : clientListenerLease?.ProcessRunId;
                        if (!string.IsNullOrEmpty(runId) && clientGeneration > 0)
                        {
                            var origin = alreadyReported ? currentFailureOrigin!.Value : new NknAccelerationFailureOrigin(
                                laneInstanceId, clientGeneration, runId, ++failureGeneration);
                            currentFailureOrigin = origin;
                            forwardedEvent = new AccelerationStateChangedEventArgs(false, e.Reason, origin, preparationIdentity);
                            if (alreadyReported) shouldForward = false;
                        }
                        client = null;
                        clientRole = ClientRole.None;
                        clientDialerProcessOwner = null;
                        listenerRequest = null;
                        dialerStartTask = null;
                        processToStop = dialerProcessOwner;
                        dialerProcessOwner = null;
                    }
                }
                else
                {
                    shouldForward = false;
                }
            }
        }

        if (!shouldForward)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_stale_sidecar_state_ignored; available={FormatBool(e.IsAvailable)}; reason={SanitizeSidecarReason(e.Reason) ?? "unknown"}");
            return;
        }

        failedPreparedClient?.Dispose();
        processToStop?.Stop(string.IsNullOrWhiteSpace(e.Reason) ? "client_unavailable" : e.Reason);
        stopPreparedListener?.Invoke("preparation_lane_unavailable");
        StateChanged?.Invoke(this, forwardedEvent);
    }

    private void OnListenerSupervisorStateChanged(object? sender, AccelerationStateChangedEventArgs e)
    {
        if (e.IsAvailable || e.SessionPreparationExecutionIdentity is not null)
        {
            StateChanged?.Invoke(this, e);
            return;
        }
        if (e.Reason is not ("listener_terminal_tuna_write_failed" or "listener_terminal_tuna_stream_eof"))
        {
            StateChanged?.Invoke(this, e);
            return;
        }
        NknAccelerationFailureOrigin origin;
        lock (gate)
        {
            if (disposed || client is null || clientRole != ClientRole.Listener || clientGeneration <= 0 ||
                e.ListenerFailureSource is not { } source || source != clientListenerLease || source.CurrentRequest != listenerRequest ||
                currentFailureOrigin is { } prior && prior.ClientGeneration == clientGeneration) { return; }
            origin = new(laneInstanceId, clientGeneration, source.ProcessRunId, ++failureGeneration);
            currentFailureOrigin = origin;
        }
        StateChanged?.Invoke(this, new(false, e.Reason, origin));
    }

    private void ForwardCurrentClientReadyIfAvailable(NknTunaSidecarClient expectedClient, string reason)
    {
        var shouldForward = false;
        string? preparationIdentity = null;
        lock (gate)
        {
            if (ReferenceEquals(client, expectedClient) && expectedClient.IsAvailable)
            {
                lastDiagnostics = expectedClient.GetDiagnosticsSnapshot();
                preparationIdentity = currentClientStart?.ExecutionIdentity;
                shouldForward = true;
            }
        }

        if (shouldForward)
        {
            StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(true, reason,
                sessionPreparationExecutionIdentity: preparationIdentity));
        }
    }

    private void CaptureClientDiagnostics_NoLock(NknTunaSidecarClient? sidecarClient)
    {
        if (sidecarClient is not null)
        {
            lastDiagnostics = sidecarClient.GetDiagnosticsSnapshot();
        }
    }

    private void StopCurrentListenerBeforeDialer()
    {
        NknTunaSidecarClient? clientToDispose = null;
        lock (gate)
        {
            if (clientRole != ClientRole.Listener)
            {
                return;
            }

            clientToDispose = client;
            CaptureClientDiagnostics_NoLock(clientToDispose);
            if (clientToDispose is not null)
            {
                clientToDispose.MessageReceived -= OnClientMessageReceived;
                clientToDispose.StateChanged -= OnClientStateChanged;
            }

            client = null;
            clientRole = ClientRole.None;
            clientDialerProcessOwner = null;
            listenerRequest = null;
        }

        clientToDispose?.Dispose();
        listenerSupervisor?.Stop("payer_switch_to_dialer");
        LocalOperationalLog.Info("NKN.Tuna", "event=tuna_listener_suppressed_for_remote_payer");
    }

    private bool ReplaceClient(
        NknTunaSidecarClient nextClient,
        ClientRole nextRole,
        NknTunaSidecarProcessOwner? nextDialerOwner)
        => ReplaceClientCore(nextClient, nextRole, nextDialerOwner, null, CancellationToken.None, null, null);

    private bool ReplaceStartedClient(NknTunaSidecarClient nextClient, ClientRole nextRole,
        NknTunaSidecarProcessOwner? nextDialerOwner, LaneStartOperation operation, CancellationToken ct,
        NknTunaListenerStartRequest? request = null, NknTunaListenerAdoptionSnapshot? listenerLease = null)
        => ReplaceClientCore(nextClient, nextRole, nextDialerOwner, operation, ct, request, listenerLease);

    private bool ReplaceClientCore(NknTunaSidecarClient nextClient, ClientRole nextRole,
        NknTunaSidecarProcessOwner? nextDialerOwner, LaneStartOperation? operation, CancellationToken ct,
        NknTunaListenerStartRequest? request, NknTunaListenerAdoptionSnapshot? listenerLease)
    {
        NknTunaSidecarClient? previousClient = null;
        NknTunaSidecarProcessOwner? previousDialerOwner = null;
        var rejectNextClient = false;
        lock (gate)
        {
            if (disposed || operation is not null && !IsLaneStartCurrentUnsafe(operation, ct))
                rejectNextClient = true;
            if (listenerLease is not null && listenerSupervisor?.CaptureCurrentListenerAdoptionSnapshot() != listenerLease)
                rejectNextClient = true;
            if (nextRole == ClientRole.Listener &&
                clientRole == ClientRole.Dialer &&
                client?.IsAvailable == true)
            {
                rejectNextClient = true;
            }

            if (rejectNextClient)
            {
                nextClient.MessageReceived -= OnClientMessageReceived;
                nextClient.StateChanged -= OnClientStateChanged;
            }
            else
            {
                if (operation is null) RetireLaneStartUnsafe();
                previousClient = client;
                CaptureClientDiagnostics_NoLock(previousClient);
                if (previousClient is not null)
                {
                    previousClient.MessageReceived -= OnClientMessageReceived;
                    previousClient.StateChanged -= OnClientStateChanged;
                }

                if (clientRole == ClientRole.Dialer && !ReferenceEquals(clientDialerProcessOwner, nextDialerOwner))
                {
                    previousDialerOwner = clientDialerProcessOwner;
                }

                client = nextClient;
                currentClientStart = operation;
                clientRole = nextRole;
                clientDialerProcessOwner = nextRole == ClientRole.Dialer ? nextDialerOwner : null;
                clientGeneration++;
                currentFailureOrigin = null;
                listenerRequest = nextRole == ClientRole.Listener ? request : null;
                clientListenerLease = nextRole == ClientRole.Listener ? listenerLease : null;
                dialerProcessOwner = nextRole == ClientRole.Dialer ? nextDialerOwner : null;
            }
        }

        if (rejectNextClient)
        {
            nextClient.Dispose();
            if (operation is null) listenerSupervisor?.Stop("listener_rejected_current_dialer");
            LocalOperationalLog.Info(
                "NKN.Tuna",
                "event=tuna_listener_client_rejected; reason=current_dialer_active");
            return false;
        }

        previousClient?.Dispose();
        previousDialerOwner?.Stop("client_replaced");
        if (nextRole == ClientRole.Dialer && operation is null)
        {
            listenerSupervisor?.Stop("payer_switch_to_dialer");
        }

        return true;
    }

    private void MarkDialerClientUnavailable(NknTunaSidecarProcessOwner? owner, string reason)
    {
        NknTunaSidecarClient? current;
        lock (gate)
        {
            if (owner is null || !ReferenceEquals(dialerProcessOwner, owner) ||
                !ReferenceEquals(clientDialerProcessOwner, owner) ||
                clientRole != ClientRole.Dialer)
            {
                return;
            }
            current = client;
        }

        current?.MarkUnavailableFromSidecarEvent(reason);
    }

    private static bool TryHandleReadyLine(string line, TaskCompletionSource<string> ready)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("event", out var eventProperty) ||
                !string.Equals(eventProperty.GetString(), "ready", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("localIpc", out var endpointProperty) ||
                endpointProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var endpoint = endpointProperty.GetString();
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                ready.TrySetResult(endpoint.Trim());
                return true;
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON diagnostic output.
        }

        return false;
    }

    private static string? TryExtractSidecarEvent(string line, out string? reason)
    {
        reason = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("terminalReason", out var terminalReasonProperty) &&
                terminalReasonProperty.ValueKind == JsonValueKind.String)
            {
                reason = terminalReasonProperty.GetString();
            }
            else if (root.TryGetProperty("reason", out var reasonProperty) &&
                reasonProperty.ValueKind == JsonValueKind.String)
            {
                reason = reasonProperty.GetString();
            }

            return root.TryGetProperty("event", out var eventProperty) &&
                   eventProperty.ValueKind == JsonValueKind.String
                ? eventProperty.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryPreserveOwnedDialerTrace(NknTunaSidecarProcessOwner? owner, string line)
        => NknTunaDeliveryTrace.PreserveChildLine(line, owner?.RunId, "dialer");

    private static void HandleOwnedDialerStderr(NknTunaSidecarProcessOwner? owner, string line)
    {
        owner?.SetupDiagnostics.DialErrorLine(line);
        LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_dialer_sidecar_stderr; line_len={line.Length}");
    }

    private static void LogBridgeTerminal(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                "event=tuna_dialer_bridge_terminal" +
                $"; terminal_reason={SanitizeSidecarReason(TryGetString(root, "terminalReason") ?? TryGetString(root, "reason")) ?? "unknown"}" +
                $"; role={SanitizeSidecarReason(TryGetString(root, "role")) ?? "unknown"}" +
                $"; direction={SanitizeSidecarReason(TryGetString(root, "direction")) ?? "unknown"}" +
                $"; stage={SanitizeSidecarReason(TryGetString(root, "stage")) ?? "unknown"}" +
                $"; frames_forwarded={TryGetLong(root, "framesForwarded") ?? -1}" +
                $"; bytes_moved={TryGetLong(root, "bytesMoved") ?? -1}" +
                $"; payload_bytes={TryGetLong(root, "payloadBytes") ?? -1}" +
                $"; traffic_flowed={FormatBool(TryGetBool(root, "trafficFlowed") ?? false)}" +
                $"; last_lane={SanitizeSidecarReason(TryGetString(root, "lastFrameLane")) ?? "unknown"}" +
                $"; last_sequence={TryGetLong(root, "lastFrameSeq") ?? -1}" +
                $"; max_read_ms={TryGetLong(root, "maxReadMs") ?? -1}" +
                $"; max_write_ms={TryGetLong(root, "maxWriteMs") ?? -1}" +
                $"; provider_usable_count={TryGetLong(root, "providerUsableCount") ?? -1}" +
                $"; payment_status={SanitizeSidecarReason(TryGetString(root, "paymentStatus")) ?? "unknown"}" +
                $"; payment_event_count={TryGetLong(root, "paymentEventCount") ?? -1}");
        }
        catch (JsonException)
        {
            // Best-effort diagnostics only.
        }
    }

    private static long? TryGetLong(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value)
            ? value
            : null;

    private static string? TryGetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool? TryGetBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;

    private static string FormatBool(bool value)
        => value ? "true" : "false";

    private static string FormatClientRole(ClientRole role)
        => role switch
        {
            ClientRole.Listener => "listener",
            ClientRole.Dialer => "dialer",
            _ => "none",
        };

    private static string MapBridgeChannel(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => "media",
            NknBridgeChannel.Bulk => "bulk",
            _ => "control",
        };

    private static bool IsTerminalSidecarEvent(string eventName)
        => string.Equals(eventName, "tuna_bridge_terminal", StringComparison.Ordinal) ||
           string.Equals(eventName, "bridge_direction_stopped", StringComparison.Ordinal) ||
           string.Equals(eventName, "error", StringComparison.Ordinal);

    private static string SanitizeSidecarEventName(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 64)];
        var written = 0;
        foreach (var ch in value)
        {
            if (written >= buffer.Length)
            {
                break;
            }

            buffer[written++] = char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_';
        }

        return written == 0 ? "unknown" : new string(buffer[..written]);
    }

    private static string? SanitizeSidecarReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var safe = value.Trim();
        if (safe.Length > 160)
        {
            safe = safe[..160];
        }

        return safe
            .Replace(";", ",", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal);
    }

    private static async Task WriteDialerSeedAsync(Process process, string seedBase64, CancellationToken ct)
    {
        byte[] seedBytes;
        try
        {
            seedBytes = Convert.FromBase64String(seedBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Tuna dialer seed is not valid base64.", ex);
        }

        try
        {
            if (seedBytes.Length != 32)
            {
                throw new InvalidOperationException("Tuna dialer seed must decode to exactly 32 bytes.");
            }

            var seedHex = Convert.ToHexString(seedBytes).ToLowerInvariant();
            await process.StandardInput.WriteLineAsync(seedHex.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seedBytes);
        }
    }

    private void StopDialerOwnerIfCurrent(NknTunaSidecarProcessOwner? owner, string reason)
    {
        if (owner is null)
        {
            return;
        }

        var shouldStop = false;
        lock (gate)
        {
            if (ReferenceEquals(dialerProcessOwner, owner))
            {
                dialerProcessOwner = null;
                shouldStop = true;
            }
        }

        if (shouldStop || owner.IsRunning)
        {
            owner.Stop(reason);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
