using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed class NknTunaAccelerationLane : INknTunaAccelerationSession
{
    private enum ClientRole
    {
        None = 0,
        Listener,
        Dialer,
    }

    private readonly object gate = new();
    private readonly NknTunaAccelerationOptions options;
    private readonly INknTunaListenerSidecarSupervisor? listenerSupervisor;
    private NknTunaSidecarClient? client;
    private ClientRole clientRole;
    private NknTunaSidecarProcessOwner? dialerProcessOwner;
    private Task<bool>? dialerStartTask;
    private NknAccelerationLaneDiagnostics lastDiagnostics = NknAccelerationLaneDiagnostics.Empty;
    private bool disposed;

    public NknTunaAccelerationLane(
        NknTunaAccelerationOptions options,
        INknTunaListenerSidecarSupervisor? listenerSupervisor = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.listenerSupervisor = listenerSupervisor;
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
    {
        if (!options.Enabled)
        {
            return false;
        }

        lock (gate)
        {
            if (client?.IsAvailable == true && clientRole == ClientRole.Listener)
            {
                return true;
            }
        }

        var endpoint = options.ListenerEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint) && listenerSupervisor is not null)
        {
            var started = await listenerSupervisor.EnsureStartedAsync(
                    new NknTunaListenerStartRequest(
                        string.IsNullOrWhiteSpace(expectedRemotePeer) ? string.Empty : expectedRemotePeer.Trim(),
                        options.Lanes),
                    ct)
                .ConfigureAwait(false);
            endpoint = started?.LocalIpc;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var connectEndpoint = endpoint.Trim();
        var nextClient = new NknTunaSidecarClient(options.Lanes, options.QueueCapacity);
        nextClient.MessageReceived += OnClientMessageReceived;
        nextClient.StateChanged += OnClientStateChanged;
        try
        {
            await nextClient.ConnectAsync(
                    connectEndpoint,
                    TimeSpan.FromMilliseconds(options.ConnectTimeoutMs),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_listener_sidecar_connect_failed; error={ex.GetType().Name}");
            nextClient.Dispose();
            return false;
        }

        return ReplaceClient(nextClient, ClientRole.Listener, null);
    }

    public async Task<bool> StartDialerSidecarAsync(string tunaAddress, string expectedRemotePeer, CancellationToken ct)
    {
        Task<bool> startTask;
        lock (gate)
        {
            if (client?.IsAvailable == true && clientRole == ClientRole.Dialer)
            {
                return true;
            }

            if (dialerStartTask is null)
            {
                dialerStartTask = StartDialerSidecarCoreAsync(tunaAddress, expectedRemotePeer, ct);
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

    private async Task<bool> StartDialerSidecarCoreAsync(string tunaAddress, string expectedRemotePeer, CancellationToken ct)
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

        StopCurrentListenerBeforeDialer();

        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastSidecarEvent = null;
        string? lastSidecarReason = null;
        NknTunaSidecarProcessOwner? owner = null;
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
                    MarkCurrentClientUnavailable($"sidecar_{safeReason ?? safeEventName}");
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_dialer_sidecar_stderr; line_len={e.Data.Length}");
            }
        };
        process.Exited += (_, _) =>
        {
            if (!ready.Task.IsCompleted)
            {
                ready.TrySetException(new InvalidOperationException("Tuna dialer sidecar exited before ready."));
            }

            if (IsCurrentDialerOwner(owner))
            {
                MarkCurrentClientUnavailable("dialer_exited");
            }
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
                ThrowIfDisposed();
                dialerProcessOwner = owner;
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
            var nextClient = new NknTunaSidecarClient(options.Lanes, options.QueueCapacity);
            nextClient.MessageReceived += OnClientMessageReceived;
            nextClient.StateChanged += OnClientStateChanged;
            await nextClient.ConnectAsync(
                    endpoint,
                    TimeSpan.FromMilliseconds(options.ConnectTimeoutMs),
                    ct)
                .ConfigureAwait(false);

            if (!ReplaceClient(nextClient, ClientRole.Dialer, owner))
            {
                StopDialerOwnerIfCurrent(owner, "dialer_replaced_by_current_client");
                return false;
            }

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
    }

    public Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] envelopeBytes, CancellationToken ct)
    {
        NknTunaSidecarClient? current;
        lock (gate)
        {
            current = client;
        }

        return current is null ? Task.FromResult(false) : current.TrySendAsync(lane, envelopeBytes, ct);
    }

    public Task StopAsync(string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NknTunaSidecarClient? clientToDispose;
        NknTunaSidecarProcessOwner? processToStop;
        lock (gate)
        {
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
            dialerProcessOwner = null;
            dialerStartTask = null;
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
            dialerProcessOwner = null;
            dialerStartTask = null;
        }

        clientToDispose?.Dispose();
        listenerSupervisor?.Dispose();
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
        if (sender is NknTunaSidecarClient sidecarClient)
        {
            lock (gate)
            {
                if (ReferenceEquals(client, sidecarClient))
                {
                    lastDiagnostics = sidecarClient.GetDiagnosticsSnapshot();
                    if (!e.IsAvailable)
                    {
                        client = null;
                        clientRole = ClientRole.None;
                        dialerStartTask = null;
                        processToStop = dialerProcessOwner;
                        dialerProcessOwner = null;
                    }
                }
            }
        }

        processToStop?.Stop(string.IsNullOrWhiteSpace(e.Reason) ? "client_unavailable" : e.Reason);
        StateChanged?.Invoke(this, e);
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
        }

        clientToDispose?.Dispose();
        listenerSupervisor?.Stop("payer_switch_to_dialer");
        LocalOperationalLog.Info("NKN.Tuna", "event=tuna_listener_suppressed_for_remote_payer");
    }

    private bool ReplaceClient(
        NknTunaSidecarClient nextClient,
        ClientRole nextRole,
        NknTunaSidecarProcessOwner? nextDialerOwner)
    {
        NknTunaSidecarClient? previousClient = null;
        NknTunaSidecarProcessOwner? previousDialerOwner = null;
        var rejectNextClient = false;
        lock (gate)
        {
            ThrowIfDisposed();
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
                previousClient = client;
                CaptureClientDiagnostics_NoLock(previousClient);
                if (previousClient is not null)
                {
                    previousClient.MessageReceived -= OnClientMessageReceived;
                    previousClient.StateChanged -= OnClientStateChanged;
                }

                if (clientRole == ClientRole.Dialer && !ReferenceEquals(dialerProcessOwner, nextDialerOwner))
                {
                    previousDialerOwner = dialerProcessOwner;
                }

                client = nextClient;
                clientRole = nextRole;
                dialerProcessOwner = nextRole == ClientRole.Dialer ? nextDialerOwner : null;
            }
        }

        if (rejectNextClient)
        {
            nextClient.Dispose();
            listenerSupervisor?.Stop("listener_rejected_current_dialer");
            LocalOperationalLog.Info(
                "NKN.Tuna",
                "event=tuna_listener_client_rejected; reason=current_dialer_active");
            return false;
        }

        previousClient?.Dispose();
        previousDialerOwner?.Stop("client_replaced");
        if (nextRole == ClientRole.Dialer)
        {
            listenerSupervisor?.Stop("payer_switch_to_dialer");
        }

        return true;
    }

    private void MarkCurrentClientUnavailable(string reason)
    {
        NknTunaSidecarClient? current;
        lock (gate)
        {
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

    private bool IsCurrentDialerOwner(NknTunaSidecarProcessOwner? owner)
    {
        if (owner is null)
        {
            return false;
        }

        lock (gate)
        {
            return ReferenceEquals(dialerProcessOwner, owner);
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
