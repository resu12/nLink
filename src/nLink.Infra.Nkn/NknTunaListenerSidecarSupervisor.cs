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

    public INknTunaUsageTelemetrySink? UsageSink { get; init; }

    public Action<string>? StatusChanged { get; init; }
}

internal sealed class NknTunaListenerSidecarSupervisor : INknTunaListenerSidecarSupervisor
{
    private static readonly TimeSpan ProcessExitWait = TimeSpan.FromSeconds(3);
    private readonly object gate = new();
    private readonly NknTunaListenerSidecarOptions options;
    private Process? process;
    private NknTunaListenerSidecarEndpoint? endpoint;
    private string? expectedRemotePeer;
    private bool disposed;

    public NknTunaListenerSidecarSupervisor(NknTunaListenerSidecarOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
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
                process?.HasExited == false &&
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

        var password = options.TakeWalletPassword();
        if (password is null || password.Length == 0)
        {
            SetStatus("wallet_not_unlocked");
            return null;
        }

        try
        {
            return await StartProcessAsync(request, password, ct).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    private async Task<NknTunaListenerSidecarEndpoint?> StartProcessAsync(
        NknTunaListenerStartRequest request,
        char[] password,
        CancellationToken ct)
    {
        SetStatus("listener_starting");
        var startupStopwatch = Stopwatch.StartNew();
        var ready = new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        nextProcess.StartInfo.ArgumentList.Add("--local-ipc");
        nextProcess.StartInfo.ArgumentList.Add("127.0.0.1:0");
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
            SetStatus("listener_exited");
            if (!ready.Task.IsCompleted)
            {
                ready.TrySetException(new InvalidOperationException("Tuna listener sidecar exited before ready."));
            }
        };

        try
        {
            if (!nextProcess.Start())
            {
                nextProcess.Dispose();
                SetStatus("listener_start_failed");
                return null;
            }

            nextProcess.BeginOutputReadLine();
            nextProcess.BeginErrorReadLine();
            await nextProcess.StandardInput.WriteLineAsync(password.AsMemory(), ct).ConfigureAwait(false);
            nextProcess.StandardInput.Close();

            lock (gate)
            {
                ThrowIfDisposed();
                StopProcess_NoLock();
                process = nextProcess;
                endpoint = null;
                expectedRemotePeer = request.ExpectedRemotePeer.Trim();
            }

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
            SetStatus("listener_failed");
            TryStopProcess(nextProcess);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LocalOperationalLog.Warn("NKN.Tuna", "event=tuna_listener_sidecar_start_failed; error=ready_timeout");
            SetStatus("listener_ready_timeout");
            TryStopProcess(nextProcess);
            return null;
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
            var stageStatus = RuntimeStatusForSidecarEvent(eventName);
            if (!string.IsNullOrWhiteSpace(stageStatus))
            {
                SetStatus(stageStatus);
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_listener_startup_stage; stage={stageStatus}; elapsed_ms={startupStopwatch.ElapsedMilliseconds}; sidecar_duration_ms={TryGetInt64(root, "durationMs") ?? -1}");
            }

            switch (eventName)
            {
                case "ready":
                    var localIpc = TryGetString(root, "localIpc");
                    var address = TryGetString(root, "address");
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
                    SetStatus("session_summary");
                    break;
                case "error":
                    SetStatus("sidecar_error");
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON diagnostic lines.
        }
    }

    private static string? RuntimeStatusForSidecarEvent(string eventName)
        => eventName switch
        {
            "tuna_listen_start" => "listener_starting",
            "tuna_provider_paths_ready" => "provider_paths_ready",
            "tuna_provider_paths_ready_timeout" => "provider_paths_wait_timeout",
            "tuna_listen_started" => "provider_paths_ready",
            "ready" => "listener_ready",
            "local_ipc_connected" => "waiting_for_peer_dial",
            "tuna_accept_connected" => "peer_connected",
            "bridge_started" => "peer_connected",
            "error" => "sidecar_error",
            _ => null,
        };

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
        var reason = TryGetString(root, "reason") ?? "unknown";
        var paymentObserved = TryGetBool(root, "paymentObserved") ?? false;
        var cumulativeSpend = TryGetDecimal(root, "cumulativeSpendNkn", out var parsedCumulativeSpend)
            ? parsedCumulativeSpend
            : (decimal?)null;
        options.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(bytesMoved, reason, paymentObserved, cumulativeSpend));
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
            StopProcess_NoLock();
            endpoint = null;
        }
    }

    public void Stop(string reason)
    {
        lock (gate)
        {
            StopProcess_NoLock();
            endpoint = null;
            expectedRemotePeer = null;
        }

        SetStatus(string.IsNullOrWhiteSpace(reason) ? "listener_stopped" : $"listener_stopped_{reason.Trim()}");
    }

    private void StopProcess_NoLock()
    {
        var current = process;
        process = null;
        if (current is not null)
        {
            TryStopProcess(current);
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

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(ProcessExitWait);
            }
        }
        catch
        {
            // Best-effort sidecar cleanup.
        }
        finally
        {
            process.Dispose();
        }
    }

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
