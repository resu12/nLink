using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed class NknTunaListenerSidecarOptions
{
    public required string SidecarExePath { get; init; }

    public required string WalletPath { get; init; }

    public required Func<char[]?> TakeWalletPassword { get; init; }

    public required string MaxPriceNknPerMb { get; init; }

    public required int MaxTotalMiB { get; init; }

    public required int MaxDurationSec { get; init; }

    public int AcceptTimeoutSec { get; init; } = 120;

    public int ReadyTimeoutMs { get; init; } = 75_000;

    public int ListenStartTimeoutSec { get; init; } = 45;

    public int StartupAttemptCount { get; init; } = 2;

    public bool RequireProviderReady { get; init; }

    public int ProviderReadyAttempts { get; init; } = 1;

    public INknTunaUsageTelemetrySink? UsageSink { get; init; }

    public Action<string>? StatusChanged { get; init; }

    public Action<string>? CapHandoffRequested { get; init; }

    public Func<bool>? CanTakeWalletPassword { get; init; }
}

internal sealed record NknTunaProviderPathDiagnostics(
    int DegradedAcceptedCount,
    int RecoveredCount,
    int StillDegradedCount);

internal sealed class NknTunaListenerSidecarSupervisor : INknTunaListenerSidecarSupervisor
{
    private readonly object gate = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly NknTunaListenerSidecarOptions options;
    private NknTunaSidecarProcessOwner? processOwner;
    private NknTunaListenerSidecarEndpoint? endpoint;
    private string? expectedRemotePeer;
    private bool summaryObserved;
    private bool disposed;
    private string? lastStartFailureReason;
    private int providerPathsDegradedAcceptedCount;
    private int providerPathsRecoveredCount;
    private int providerPathsStillDegradedCount;

    public NknTunaListenerSidecarSupervisor(NknTunaListenerSidecarOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool CanOfferListener
    {
        get
        {
            lock (gate)
            {
                if (processOwner?.IsRunning == true)
                {
                    return true;
                }
            }

            try
            {
                return options.CanTakeWalletPassword?.Invoke() ?? true;
            }
            catch
            {
                return false;
            }
        }
    }

    public NknTunaProviderPathDiagnostics ProviderPathDiagnostics
    {
        get
        {
            lock (gate)
            {
                return new NknTunaProviderPathDiagnostics(
                    providerPathsDegradedAcceptedCount,
                    providerPathsRecoveredCount,
                    providerPathsStillDegradedCount);
            }
        }
    }

    public async Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(NknTunaListenerStartRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedRemotePeer))
        {
            SetStatus("missing_expected_peer");
            return null;
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (endpoint is not null &&
                processOwner?.IsRunning == true &&
                string.Equals(expectedRemotePeer, request.ExpectedRemotePeer, StringComparison.Ordinal))
            {
                return endpoint;
            }
        }

        await startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (endpoint is not null &&
                    processOwner?.IsRunning == true &&
                    string.Equals(expectedRemotePeer, request.ExpectedRemotePeer, StringComparison.Ordinal))
                {
                    return endpoint;
                }
            }

            if (!File.Exists(options.SidecarExePath))
            {
                SetStatus("sidecar_missing");
                return null;
            }

            if (!File.Exists(options.WalletPath))
            {
                SetStatus("wallet_missing");
                return null;
            }

            char[]? password = null;
            try
            {
                password = options.TakeWalletPassword();
                if (password is null || password.Length == 0)
                {
                    SetStatus("wallet_not_unlocked");
                    return null;
                }

                var attemptCount = Math.Clamp(options.StartupAttemptCount, 1, 5);
                for (var attempt = 1; attempt <= attemptCount; attempt++)
                {
                    ClearLastStartFailureReason();
                    var endpoint = await StartProcessAsync(request, password, attempt, attemptCount, ct).ConfigureAwait(false);
                    if (endpoint is not null)
                    {
                        return endpoint;
                    }

                    var reason = GetLastStartFailureReason();
                    if (attempt >= attemptCount || !IsRetryableStartFailure(reason))
                    {
                        return null;
                    }

                    SetStatus("listener_retrying");
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_listener_sidecar_start_retrying; attempt={attempt + 1}; max_attempts={attemptCount}; reason={SanitizeLogToken(reason ?? "unknown")}");
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000 * attempt, 2500)), ct).ConfigureAwait(false);
                }

                return null;
            }
            finally
            {
                if (password is not null)
                {
                    Array.Clear(password);
                }
            }
        }
        finally
        {
            startGate.Release();
        }
    }

    private async Task<NknTunaListenerSidecarEndpoint?> StartProcessAsync(
        NknTunaListenerStartRequest request,
        char[] password,
        int attempt,
        int maxAttempts,
        CancellationToken ct)
    {
        SetStatus("listener_starting");
        var startupStopwatch = Stopwatch.StartNew();
        var ready = new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        NknTunaSidecarProcessOwner? owner = null;
        var nextProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.SidecarExePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        nextProcess.StartInfo.ArgumentList.Add("listen");
        nextProcess.StartInfo.ArgumentList.Add("--wallet");
        nextProcess.StartInfo.ArgumentList.Add(options.WalletPath);
        nextProcess.StartInfo.ArgumentList.Add("--password-stdin");
        nextProcess.StartInfo.ArgumentList.Add("--allow-remote");
        nextProcess.StartInfo.ArgumentList.Add(request.ExpectedRemotePeer.Trim());
        nextProcess.StartInfo.ArgumentList.Add("--max-price-nkn-per-mb");
        nextProcess.StartInfo.ArgumentList.Add(options.MaxPriceNknPerMb);
        nextProcess.StartInfo.ArgumentList.Add("--max-total-mib");
        nextProcess.StartInfo.ArgumentList.Add(options.MaxTotalMiB.ToString(CultureInfo.InvariantCulture));
        nextProcess.StartInfo.ArgumentList.Add("--max-duration-sec");
        nextProcess.StartInfo.ArgumentList.Add(options.MaxDurationSec.ToString(CultureInfo.InvariantCulture));
        nextProcess.StartInfo.ArgumentList.Add("--accept-timeout-sec");
        nextProcess.StartInfo.ArgumentList.Add(options.AcceptTimeoutSec.ToString(CultureInfo.InvariantCulture));
        nextProcess.StartInfo.ArgumentList.Add("--listen-start-timeout-sec");
        nextProcess.StartInfo.ArgumentList.Add(Math.Clamp(options.ListenStartTimeoutSec, 5, 300).ToString(CultureInfo.InvariantCulture));
        nextProcess.StartInfo.ArgumentList.Add("--local-ipc");
        nextProcess.StartInfo.ArgumentList.Add("127.0.0.1:0");
        if (options.RequireProviderReady)
        {
            nextProcess.StartInfo.ArgumentList.Add("--require-provider-ready");
        }

        if (options.ProviderReadyAttempts > 1)
        {
            nextProcess.StartInfo.ArgumentList.Add("--provider-ready-attempts");
            nextProcess.StartInfo.ArgumentList.Add(options.ProviderReadyAttempts.ToString(CultureInfo.InvariantCulture));
        }

        nextProcess.StartInfo.ArgumentList.Add("--jsonl");

        nextProcess.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            HandleStdoutLine(e.Data, ready, startupStopwatch);
        };
        nextProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_listener_sidecar_stderr; line_len={e.Data.Length}");
            }
        };
        nextProcess.Exited += (_, _) =>
        {
            if (IsCurrentProcessOwner(owner, nextProcess))
            {
                SetStatus("listener_exited");
                CompleteIncompleteSession("sidecar_exited_before_summary");
                if (!ready.Task.IsCompleted)
                {
                    SetLastStartFailureReason(GetLastStartFailureReason() ?? "listener_exited_before_ready");
                    ready.TrySetException(new InvalidOperationException("Tuna listener sidecar exited before ready."));
                }
            }
        };

        try
        {
            lock (gate)
            {
                ThrowIfDisposed();
                StopProcess_NoLock("listener_replaced");
                endpoint = null;
                expectedRemotePeer = request.ExpectedRemotePeer.Trim();
                summaryObserved = false;
                providerPathsDegradedAcceptedCount = 0;
                providerPathsRecoveredCount = 0;
                providerPathsStillDegradedCount = 0;
            }

            if (!nextProcess.Start())
            {
                nextProcess.Dispose();
                SetLastStartFailureReason("listener_start_failed");
                SetStatus("listener_start_failed");
                CompleteIncompleteSession("listener_start_failed");
                return null;
            }

            owner = NknTunaSidecarProcessOwner.Attach("listener", nextProcess);
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_listener_sidecar_start_attempt; attempt={attempt}; max_attempts={maxAttempts}; listen_start_timeout_sec={Math.Clamp(options.ListenStartTimeoutSec, 5, 300)}; ready_timeout_ms={options.ReadyTimeoutMs}");
            lock (gate)
            {
                ThrowIfDisposed();
                processOwner = owner;
            }

            nextProcess.BeginOutputReadLine();
            nextProcess.BeginErrorReadLine();
            await nextProcess.StandardInput.WriteLineAsync(password.AsMemory(), ct).ConfigureAwait(false);
            nextProcess.StandardInput.Close();

            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyCts.CancelAfter(options.ReadyTimeoutMs);
            using var registration = readyCts.Token.Register(() => ready.TrySetCanceled(readyCts.Token));
            var startedEndpoint = await ready.Task.ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfDisposed();
                endpoint = startedEndpoint;
            }

            SetStatus("listener_ready");
            return startedEndpoint;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_listener_sidecar_start_failed; error={ex.GetType().Name}");
            SetLastStartFailureReason(GetLastStartFailureReason() ?? "listener_failed");
            SetStatus("listener_failed");
            CompleteIncompleteSession("listener_failed");
            StopOwnerIfCurrent(owner, "listener_failed");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LocalOperationalLog.Warn("NKN.Tuna", "event=tuna_listener_sidecar_start_failed; error=ready_timeout");
            SetLastStartFailureReason("listener_ready_timeout");
            SetStatus("listener_ready_timeout");
            CompleteIncompleteSession("listener_ready_timeout");
            StopOwnerIfCurrent(owner, "listener_ready_timeout");
            return null;
        }
        catch (OperationCanceledException)
        {
            SetLastStartFailureReason("listener_canceled");
            SetStatus("listener_canceled");
            CompleteIncompleteSession("listener_canceled");
            StopOwnerIfCurrent(owner, "listener_canceled");
            throw;
        }
    }

    private void HandleStdoutLine(
        string line,
        TaskCompletionSource<NknTunaListenerSidecarEndpoint> ready,
        Stopwatch startupStopwatch)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventName = TryGetString(root, "event");
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            var safeEvent = SanitizeLogToken(eventName);
            LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_listener_sidecar_event; sidecar_event={safeEvent}; line_len={line.Length}");
            RecordProviderPathDiagnosticEvent(eventName);
            var stageStatus = RuntimeStatusForSidecarEvent(eventName, root);
            if (!string.IsNullOrWhiteSpace(stageStatus))
            {
                SetStatus(stageStatus);
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_listener_startup_stage; stage={stageStatus}; elapsed_ms={startupStopwatch.ElapsedMilliseconds}; sidecar_duration_ms={TryGetInt64(root, "durationMs") ?? -1}; attempt={TryGetInt64(root, "attempt") ?? -1}; max_attempts={TryGetInt64(root, "maxAttempts") ?? -1}; will_retry={FormatBool(TryGetBool(root, "willRetry") ?? false)}; usable_provider_count={TryGetInt64(root, "usableCount") ?? -1}; min_provider_count={TryGetInt64(root, "minProviderCnt") ?? -1}");
            }

            switch (eventName)
            {
                case "ready":
                    var localIpc = TryGetString(root, "localIpc");
                    var address = TryGetString(root, "address");
                    var compatibility = NknTunaSidecarCompatibility.Validate(
                        TryGetInt32(root, "appProtocolVersion"),
                        TryGetInt32(root, "frameProtocolVersion"),
                        TryGetString(root, "sidecarVersion"));
                    LogCompatibilityCheck(compatibility);
                    if (!compatibility.IsCompatible)
                    {
                        SetStatus(compatibility.Reason);
                        ready.TrySetException(new InvalidOperationException(compatibility.Reason));
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(localIpc) && !string.IsNullOrWhiteSpace(address))
                    {
                        ready.TrySetResult(new NknTunaListenerSidecarEndpoint(localIpc.Trim(), address.Trim()));
                    }

                    break;
                case "tuna_payment":
                    HandlePayment(root);
                    break;
                case "summary":
                    HandleSummary(root);
                    SetStatus((TryGetBool(root, "capReached") ?? false)
                        ? (TryGetString(root, "capReason") ?? "cap_reached")
                        : "session_summary");
                    break;
                case "tuna_cap_handoff_requested":
                    var capReason = TryGetString(root, "capReason") ?? "cap_reached";
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_listener_cap_handoff_requested; cap_reason={SanitizeLogToken(capReason)}; bytes_moved={TryGetInt64(root, "bytesMoved") ?? -1}; projected_bytes={TryGetInt64(root, "projectedBytes") ?? -1}; limit_bytes={TryGetInt64(root, "limitBytes") ?? -1}; remaining_bytes={TryGetInt64(root, "remainingBytes") ?? -1}");
                    SetStatus("cap_handoff_pending");
                    RequestCapHandoff(capReason);
                    break;
                case "tuna_bridge_terminal":
                    var terminalReason = TryGetString(root, "terminalReason") ?? TryGetString(root, "reason") ?? "unknown";
                    LogBridgeTerminal(root);
                    SetStatus("terminal_" + SanitizeLogToken(terminalReason));
                    break;
                case "error":
                    SetLastStartFailureReason(TryGetString(root, "reason") ?? "sidecar_error");
                    SetStatus("sidecar_error");
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON diagnostic lines.
        }
    }

    private static string? RuntimeStatusForSidecarEvent(string eventName, JsonElement root)
        => eventName switch
        {
            "tuna_listen_start" => "listener_starting",
            "tuna_listen_call_completed" => "listener_paths_starting",
            "tuna_listen_start_timeout" => "listener_start_timeout",
            "tuna_provider_paths_ready" => "provider_paths_ready",
            "provider_paths_degraded_accepted" => "provider_paths_degraded",
            "provider_paths_recovered" => "provider_paths_ready",
            "provider_paths_still_degraded" => "provider_paths_degraded",
            "provider_paths_degraded" => (TryGetBool(root, "willRetry") ?? false)
                ? "provider_paths_retrying"
                : "provider_paths_degraded",
            "tuna_provider_paths_ready_timeout" => (TryGetBool(root, "willRetry") ?? false)
                ? "provider_paths_retrying"
                : "provider_paths_wait_timeout",
            "tuna_cap_handoff_requested" => "cap_handoff_pending",
            "tuna_listen_started" => (TryGetBool(root, "providerReady") ?? false)
                ? "provider_paths_ready"
                : "provider_paths_degraded",
            "ready" => "listener_ready",
            "local_ipc_connected" => "waiting_for_peer_dial",
            "tuna_accept_connected" => "peer_connected",
            "bridge_started" => "peer_connected",
            "error" => "sidecar_error",
            _ => null,
        };

    private void RecordProviderPathDiagnosticEvent(string eventName)
    {
        lock (gate)
        {
            switch (eventName)
            {
                case "provider_paths_degraded_accepted":
                    providerPathsDegradedAcceptedCount++;
                    break;
                case "provider_paths_recovered":
                    providerPathsRecoveredCount++;
                    break;
                case "provider_paths_still_degraded":
                    providerPathsStillDegradedCount++;
                    break;
            }
        }
    }

    private void HandlePayment(JsonElement root)
    {
        if (options.UsageSink is null ||
            !TryGetDecimal(root, "amountNkn", out var amount) ||
            !TryGetDecimal(root, "cumulativeSpendNkn", out var cumulative))
        {
            return;
        }

        var bytesMoved = TryGetInt64(root, "bytesMoved") ?? 0L;
        var nknPerMb = TryGetDecimal(root, "nknPerMb", out var parsedNknPerMb)
            ? parsedNknPerMb
            : (decimal?)null;
        options.UsageSink.RecordPayment(new NknTunaPaymentTelemetry(amount, cumulative, bytesMoved, nknPerMb));
    }

    private void HandleSummary(JsonElement root)
    {
        if (options.UsageSink is null)
        {
            return;
        }

        var bytesMoved = TryGetInt64(root, "bytesMoved") ?? 0L;
        var reason = TryGetString(root, "terminalReason") ?? TryGetString(root, "reason") ?? "unknown";
        var paymentObserved = TryGetBool(root, "paymentObserved") ?? false;
        var paymentEventCount = (int)Math.Clamp(TryGetInt64(root, "paymentEventCount") ?? 0L, 0L, int.MaxValue);
        var paymentStatus = TryGetString(root, "paymentStatus") ?? string.Empty;
        var capReached = TryGetBool(root, "capReached") ?? false;
        var capReason = TryGetString(root, "capReason") ?? string.Empty;
        var fallbackReason = TryGetString(root, "fallbackReason") ?? string.Empty;
        var cumulativeSpend = TryGetDecimal(root, "cumulativeSpendNkn", out var parsedCumulativeSpend)
            ? parsedCumulativeSpend
            : (decimal?)null;
        var nknPerMb = TryGetDecimal(root, "nknPerMb", out var parsedNknPerMb)
            ? parsedNknPerMb
            : (decimal?)null;
        lock (gate)
        {
            if (summaryObserved)
            {
                return;
            }

            summaryObserved = true;
        }

        options.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(
            bytesMoved,
            reason,
            paymentObserved,
            cumulativeSpend,
            paymentEventCount,
            paymentStatus,
            capReached,
            capReason,
            fallbackReason,
            nknPerMb));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lock (gate)
        {
            StopProcess_NoLock("disposed");
            endpoint = null;
        }
    }

    public void Stop(string reason)
    {
        lock (gate)
        {
            StopProcess_NoLock(string.IsNullOrWhiteSpace(reason) ? "listener_stopped" : reason.Trim());
            endpoint = null;
            expectedRemotePeer = null;
        }

        SetStatus(string.IsNullOrWhiteSpace(reason) ? "listener_stopped" : $"listener_stopped_{reason.Trim()}");
    }

    private void StopProcess_NoLock(string reason)
    {
        var current = processOwner;
        processOwner = null;
        if (current is not null)
        {
            CompleteIncompleteSession_NoLock(reason);
            current.Stop(reason);
        }
    }

    private void CompleteIncompleteSession(string reason)
    {
        lock (gate)
        {
            CompleteIncompleteSession_NoLock(reason);
        }
    }

    private void CompleteIncompleteSession_NoLock(string reason)
    {
        if (summaryObserved)
        {
            return;
        }

        summaryObserved = true;
        try
        {
            options.UsageSink?.RecordIncomplete(string.IsNullOrWhiteSpace(reason) ? "accounting_incomplete" : reason.Trim());
        }
        catch
        {
            // Accounting telemetry is diagnostic only.
        }
    }

    private void SetStatus(string status)
    {
        try
        {
            options.StatusChanged?.Invoke(SanitizeLogToken(status));
        }
        catch
        {
            // Diagnostics-only status updates must never affect transport flow.
        }
    }

    private void RequestCapHandoff(string reason)
    {
        try
        {
            options.CapHandoffRequested?.Invoke(SanitizeLogToken(reason));
        }
        catch
        {
            // Cap handoff is best-effort; hard caps remain enforced by the sidecar.
        }
    }

    private void SetLastStartFailureReason(string reason)
    {
        lock (gate)
        {
            lastStartFailureReason = SanitizeLogToken(reason);
        }
    }

    private void ClearLastStartFailureReason()
    {
        lock (gate)
        {
            lastStartFailureReason = null;
        }
    }

    private string? GetLastStartFailureReason()
    {
        lock (gate)
        {
            return lastStartFailureReason;
        }
    }

    private static bool IsRetryableStartFailure(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("exited_before_ready", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentProcessOwner(NknTunaSidecarProcessOwner? owner, Process process)
    {
        if (owner is null)
        {
            return false;
        }

        lock (gate)
        {
            return ReferenceEquals(processOwner, owner) && owner.Owns(process);
        }
    }

    private void StopOwnerIfCurrent(NknTunaSidecarProcessOwner? owner, string reason)
    {
        if (owner is null)
        {
            return;
        }

        var shouldStop = false;
        lock (gate)
        {
            if (ReferenceEquals(processOwner, owner))
            {
                processOwner = null;
                endpoint = null;
                shouldStop = true;
            }
        }

        if (shouldStop || owner.IsRunning)
        {
            owner.Stop(reason);
        }
    }

    private static void LogCompatibilityCheck(NknTunaSidecarCompatibilityResult compatibility)
    {
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_sidecar_version_checked; compatible={FormatBool(compatibility.IsCompatible)}; reason={SanitizeLogToken(compatibility.Reason)}; expected_app_protocol={NknTunaSidecarCompatibility.AppProtocolVersion}; expected_frame_protocol={NknTunaSidecarFrameProtocol.ProtocolVersion}; expected_version={SanitizeLogToken(NknTunaSidecarCompatibility.ExpectedSidecarVersion)}; sidecar_version={SanitizeLogToken(compatibility.SidecarVersion)}");
        if (!compatibility.IsCompatible)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_sidecar_version_mismatch; reason={SanitizeLogToken(compatibility.Reason)}; expected_app_protocol={NknTunaSidecarCompatibility.AppProtocolVersion}; expected_frame_protocol={NknTunaSidecarFrameProtocol.ProtocolVersion}; expected_version={SanitizeLogToken(NknTunaSidecarCompatibility.ExpectedSidecarVersion)}");
        }
    }

    private static string FormatBool(bool value)
        => value ? "true" : "false";

    private static void LogBridgeTerminal(JsonElement root)
    {
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            "event=tuna_listener_bridge_terminal" +
            $"; terminal_reason={SanitizeLogToken(TryGetString(root, "terminalReason") ?? TryGetString(root, "reason"))}" +
            $"; role={SanitizeLogToken(TryGetString(root, "role"))}" +
            $"; direction={SanitizeLogToken(TryGetString(root, "direction"))}" +
            $"; stage={SanitizeLogToken(TryGetString(root, "stage"))}" +
            $"; frames_forwarded={TryGetInt64(root, "framesForwarded") ?? -1}" +
            $"; bytes_moved={TryGetInt64(root, "bytesMoved") ?? -1}" +
            $"; payload_bytes={TryGetInt64(root, "payloadBytes") ?? -1}" +
            $"; traffic_flowed={FormatBool(TryGetBool(root, "trafficFlowed") ?? false)}" +
            $"; last_lane={SanitizeLogToken(TryGetString(root, "lastFrameLane"))}" +
            $"; last_sequence={TryGetInt64(root, "lastFrameSeq") ?? -1}" +
            $"; max_read_ms={TryGetInt64(root, "maxReadMs") ?? -1}" +
            $"; max_write_ms={TryGetInt64(root, "maxWriteMs") ?? -1}" +
            $"; provider_usable_count={TryGetInt64(root, "providerUsableCount") ?? -1}" +
            $"; payment_status={SanitizeLogToken(TryGetString(root, "paymentStatus"))}" +
            $"; payment_event_count={TryGetInt64(root, "paymentEventCount") ?? -1}");
    }

    private static int? TryGetInt32(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)
            ? value
            : null;

    private static string? TryGetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static long? TryGetInt64(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value)
            ? value
            : null;

    private static bool? TryGetBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;

    private static bool TryGetDecimal(JsonElement root, string propertyName, out decimal value)
    {
        value = 0m;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out value))
        {
            return true;
        }

        return prop.ValueKind == JsonValueKind.String &&
               decimal.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string SanitizeLogToken(string? value)
    {
        var safe = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        if (safe.Length > 80)
        {
            safe = safe[..80];
        }

        return safe
            .Replace(";", "_", StringComparison.Ordinal)
            .Replace("\r", "_", StringComparison.Ordinal)
            .Replace("\n", "_", StringComparison.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
