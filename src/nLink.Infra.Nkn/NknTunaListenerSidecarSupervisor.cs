using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed class NknTunaListenerSidecarOptions
{
    public required string SidecarExePath { get; init; }

    public required string WalletPath { get; init; }

    public string? ExpectedPayerAddress { get; init; }

    internal bool TryAddWalletArguments(ProcessStartInfo start)
    {
        if (string.IsNullOrWhiteSpace(ExpectedPayerAddress)) return false;
        start.ArgumentList.Add("--wallet");
        start.ArgumentList.Add(WalletPath);
        start.ArgumentList.Add("--expected-payer-sha256");
        start.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ExpectedPayerAddress))));
        start.ArgumentList.Add("--password-stdin");
        return true;
    }

    public required Func<char[]?> TakeWalletPassword { get; init; }

    public Func<Func<bool>, char[]?>? TakeWalletPasswordIfCurrent { get; init; }

    public Func<string, Func<bool>, bool>? BeginUsageAttemptIfCurrent { get; init; }

    public long RuntimeGeneration { get; init; }

    public required string MaxPriceNknPerMb { get; init; }

    public required int MaxTotalMiB { get; init; }

    public required int MaxDurationSec { get; init; }

    public int AcceptTimeoutSec { get; init; } = 120;

    public int ReadyTimeoutMs { get; init; } = 75_000;

    public int ListenStartTimeoutSec { get; init; } = 45;

    public int StartupAttemptCount { get; init; } = 2;

    public bool RequireProviderReady { get; init; }

    public int ProviderReadyAttempts { get; init; } = 1;

    public int DegradedProviderGraceSeconds { get; init; }

    public INknTunaUsageTelemetrySink? UsageSink { get; init; }

    public Action<string>? StatusChanged { get; init; }

    public Action<string>? CapHandoffRequested { get; init; }

    public Action<string, NknTunaListenerAdoptionSnapshot>? ListenerCapHandoffRequested { get; init; }

    public Func<bool>? CanTakeWalletPassword { get; init; }
}

internal sealed record NknTunaProviderPathDiagnostics(
    int DegradedAcceptedCount,
    int RecoveredCount,
    int StillDegradedCount,
    NknTunaProviderPathQualitySummary? LatestQualitySummary = null);

internal sealed record NknTunaProviderPathQualitySummary(
    string QualityClass,
    int UsableCount,
    int[] MissingIndices,
    long RecoveryLatencyMs,
    long Stable3OnlyMs,
    string[] FinalPathReasons);

internal sealed class NknTunaListenerSidecarSupervisor : INknTunaListenerSidecarSupervisor
{
    private readonly object gate = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly NknTunaListenerSidecarOptions options;
    private NknTunaSidecarProcessOwner? processOwner;
    private NknTunaListenerSidecarEndpoint? endpoint;
    private string? expectedRemotePeer;
    private NknTunaListenerStartRequest? currentRequest;
    private TaskCompletionSource<NknTunaListenerSidecarEndpoint>? pendingReady;
    private bool terminalFailureObserved;
    private long stopGeneration;
    private long ownershipGeneration;
    private bool summaryObserved;
    private bool disposed;
    private string? lastStartFailureReason;
    private int providerPathsDegradedAcceptedCount;
    private int providerPathsRecoveredCount;
    private int providerPathsStillDegradedCount;
    private NknTunaProviderPathQualitySummary? latestProviderPathQualitySummary;

    public NknTunaListenerSidecarSupervisor(NknTunaListenerSidecarOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;

    internal Action? BeforeWalletPasswordForTests { get; set; }

    internal bool IsIdle
    {
        get
        {
            lock (gate) return !disposed && processOwner is null && currentRequest is null &&
                pendingReady is null && startGate.CurrentCount == 1;
        }
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
                    providerPathsStillDegradedCount,
                    latestProviderPathQualitySummary);
            }
        }
    }

    public NknTunaListenerAdoptionSnapshot? CaptureCurrentListenerAdoptionSnapshot()
    {
        lock (gate)
        {
            if (disposed || terminalFailureObserved ||
                endpoint is null ||
                currentRequest is null ||
                processOwner is not { IsRunning: true } owner)
            {
                return null;
            }

            return new NknTunaListenerAdoptionSnapshot(
                currentRequest,
                endpoint,
                owner.Pid,
                owner.RunId,
                owner.StartedUtc,
                ownershipGeneration);
        }
    }

    public bool TryAdoptCurrentListener(
        NknTunaListenerAdoptionSnapshot snapshot,
        NknTunaListenerStartRequest successorRequest)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(successorRequest);
        lock (gate)
        {
            if (disposed || terminalFailureObserved ||
                endpoint != snapshot.Endpoint ||
                currentRequest != snapshot.CurrentRequest ||
                processOwner is not { IsRunning: true } owner ||
                owner.Pid != snapshot.ProcessId ||
                !string.Equals(owner.RunId, snapshot.ProcessRunId, StringComparison.Ordinal) ||
                owner.StartedUtc != snapshot.ProcessStartedUtc ||
                ownershipGeneration != snapshot.OwnershipGeneration ||
                !((snapshot.CurrentRequest.LifetimePolicy == NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation &&
                   successorRequest.LifetimePolicy == NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation) ||
                  (snapshot.CurrentRequest.LifetimePolicy == NknTunaListenerLifetimePolicy.SessionPreparation &&
                   successorRequest.LifetimePolicy is NknTunaListenerLifetimePolicy.SessionPreparation or NknTunaListenerLifetimePolicy.CoordinatorAdmittedTransfer &&
                   !string.IsNullOrWhiteSpace(snapshot.CurrentRequest.ExecutionIdentity) &&
                   !string.IsNullOrWhiteSpace(successorRequest.ExecutionIdentity))) ||
                !string.Equals(
                    snapshot.CurrentRequest.ExpectedRemotePeer,
                    successorRequest.ExpectedRemotePeer,
                    StringComparison.Ordinal) ||
                snapshot.CurrentRequest.Lanes != successorRequest.Lanes)
            {
                return false;
            }

            currentRequest = successorRequest;
            ownershipGeneration++;
            expectedRemotePeer = successorRequest.ExpectedRemotePeer;
            return true;
        }
    }

    public async Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(NknTunaListenerStartRequest request, CancellationToken ct)
        => (await EnsureStartedCoreAsync(request, ct, requireIdle: false).ConfigureAwait(false))?.Endpoint;

    public Task<NknTunaListenerAdoptionSnapshot?> EnsureStartedIfIdleAsync(NknTunaListenerStartRequest request, CancellationToken ct)
        => EnsureStartedCoreAsync(request, ct, requireIdle: true);

    public bool IsCurrentVacancy(NknListenerVacancy vacant)
    {
        lock (gate) return IsIdle && MatchesVacancyLocked(vacant);
    }

    private bool MatchesVacancyLocked(NknListenerVacancy vacant) => ReferenceEquals(vacant.Supervisor, this) &&
        vacant.Stop == stopGeneration && vacant.Ownership == ownershipGeneration;

    public Task<NknTunaListenerAdoptionSnapshot?> StartInVacancyAsync(NknListenerVacancy vacant,
        NknTunaListenerStartRequest request, CancellationToken ct, Action<NknListenerVacancy> failed)
        => EnsureStartedCoreAsync(request, ct, requireIdle: true, vacant, failed);

    public bool TryUseExactListener(NknTunaListenerAdoptionSnapshot expected, Action use)
    {
        lock (gate)
        {
            if (CaptureCurrentListenerAdoptionSnapshot() != expected) return false;
            use();
            return true;
        }
    }

    private async Task<NknTunaListenerAdoptionSnapshot?> EnsureStartedCoreAsync(
        NknTunaListenerStartRequest request, CancellationToken ct, bool requireIdle, NknListenerVacancy? vacant = null,
        Action<NknListenerVacancy>? failed = null)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedRemotePeer))
        {
            SetStatus("missing_expected_peer");
            return null;
        }

        long expectedStopGeneration;
        lock (gate)
        {
            ThrowIfDisposed();
            expectedStopGeneration = stopGeneration;
            if (vacant is not null && !MatchesVacancyLocked(vacant)) return null;
            if (requireIdle && (processOwner is not null || currentRequest is not null)) return null;
            if (endpoint is not null &&
                processOwner?.IsRunning == true &&
                currentRequest == request)
            {
                return CaptureCurrentListenerAdoptionSnapshot();
            }
        }

        if (requireIdle)
        {
            if (!await startGate.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        }
        else await startGate.WaitAsync(ct).ConfigureAwait(false);
        var admitted = false;
        try
        {
            lock (gate)
            {
                if (disposed || stopGeneration != expectedStopGeneration || ct.IsCancellationRequested) return null;
                if (vacant is not null && !MatchesVacancyLocked(vacant)) return null;
                if (requireIdle && (processOwner is not null || currentRequest is not null)) return null;
                admitted = true;
                if (endpoint is not null &&
                    processOwner?.IsRunning == true &&
                    currentRequest == request)
                {
                    return CaptureCurrentListenerAdoptionSnapshot();
                }
            }

            if (!File.Exists(options.SidecarExePath))
            {
                SetStartupStatus(expectedStopGeneration, ct, "sidecar_missing");
                return null;
            }

            if (!File.Exists(options.WalletPath))
            {
                SetStartupStatus(expectedStopGeneration, ct, "wallet_missing");
                return null;
            }

            char[]? password = null;
            try
            {
                BeforeWalletPasswordForTests?.Invoke();
                lock (gate)
                {
                    bool IsCurrent() => !disposed && stopGeneration == expectedStopGeneration && !ct.IsCancellationRequested;
                    if (!IsCurrent()) return null;
                    // The runtime rechecks its unlock/transport lifetime inside
                    // its own mutation lock, before status or accounting changes.
                    password = options.TakeWalletPasswordIfCurrent is { } take
                        ? take(IsCurrent) : options.TakeWalletPassword();
                    if (!IsCurrent()) return null;
                }
                if (password is null || password.Length == 0)
                {
                    SetStartupStatus(expectedStopGeneration, ct, "wallet_not_unlocked");
                    return null;
                }

                var attemptCount = Math.Clamp(options.StartupAttemptCount, 1, 5);
                for (var attempt = 1; attempt <= attemptCount; attempt++)
                {
                    if (!IsStartupCurrent(expectedStopGeneration) || ct.IsCancellationRequested) return null;
                    ClearLastStartFailureReason();
                    var endpoint = await StartProcessAsync(request, password, attempt, attemptCount, expectedStopGeneration, ct).ConfigureAwait(false);
                    if (endpoint is not null)
                    {
                        return endpoint;
                    }

                    if (!IsStartupCurrent(expectedStopGeneration) || ct.IsCancellationRequested) return null;
                    var reason = GetLastStartFailureReason();
                    if (attempt >= attemptCount || !IsRetryableStartFailure(reason))
                    {
                        return null;
                    }

                    lock (gate)
                    {
                        if (disposed || stopGeneration != expectedStopGeneration || ct.IsCancellationRequested) return null;
                        SetStatus("listener_retrying");
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_listener_sidecar_start_retrying; attempt={attempt + 1}; max_attempts={attemptCount}; reason={SanitizeLogToken(reason ?? "unknown")}");
                    }
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
            lock (gate)
            {
                if (admitted && failed is not null && !disposed && !ct.IsCancellationRequested &&
                    stopGeneration == expectedStopGeneration && processOwner is null && currentRequest is null)
                    failed(new(this, stopGeneration, ownershipGeneration));
                startGate.Release();
            }
        }
    }

    private async Task<NknTunaListenerAdoptionSnapshot?> StartProcessAsync(
        NknTunaListenerStartRequest request,
        char[] password,
        int attempt,
        int maxAttempts,
        long expectedStopGeneration,
        CancellationToken ct)
    {
        if (!IsStartupCurrent(expectedStopGeneration) || ct.IsCancellationRequested) return null;
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
        if (!options.TryAddWalletArguments(nextProcess.StartInfo))
        {
            nextProcess.Dispose();
            SetStartupStatus(expectedStopGeneration, ct, "wallet_payer_missing");
            return null;
        }
        nextProcess.StartInfo.ArgumentList.Add("--allow-remote");
        nextProcess.StartInfo.ArgumentList.Add(request.ExpectedRemotePeer.Trim());
        nextProcess.StartInfo.ArgumentList.Add("--max-price-nkn-per-mb");
        nextProcess.StartInfo.ArgumentList.Add(options.MaxPriceNknPerMb);
        nextProcess.StartInfo.ArgumentList.Add("--max-total-mib");
        nextProcess.StartInfo.ArgumentList.Add(options.MaxTotalMiB.ToString(CultureInfo.InvariantCulture));
        nextProcess.StartInfo.ArgumentList.Add("--max-duration-sec");
        nextProcess.StartInfo.ArgumentList.Add(options.MaxDurationSec.ToString(CultureInfo.InvariantCulture));
        nextProcess.StartInfo.ArgumentList.Add("--accept-timeout-sec");
        var acceptTimeoutSec = request.LifetimePolicy ==
            NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation
                ? 0
                : options.AcceptTimeoutSec;
        nextProcess.StartInfo.ArgumentList.Add(acceptTimeoutSec.ToString(CultureInfo.InvariantCulture));
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

        if (options.DegradedProviderGraceSeconds > 0)
        {
            nextProcess.StartInfo.ArgumentList.Add("--degraded-provider-grace-sec");
            nextProcess.StartInfo.ArgumentList.Add(Math.Clamp(options.DegradedProviderGraceSeconds, 0, 300).ToString(CultureInfo.InvariantCulture));
        }

        nextProcess.StartInfo.ArgumentList.Add("--jsonl");

        nextProcess.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            HandleOwnedStdoutLine(owner, nextProcess, e.Data, ready, startupStopwatch);
        };
        nextProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                HandleOwnedListenerStderr(owner, e.Data);
            }
        };
        nextProcess.Exited += (_, _) => HandleOwnedExit(owner, nextProcess, ready, expectedStopGeneration);

        try
        {
            NknTunaSidecarProcessOwner? replacedOwner;
            bool usageStarted;
            lock (gate)
            {
                if (disposed || stopGeneration != expectedStopGeneration || ct.IsCancellationRequested)
                {
                    nextProcess.Dispose();
                    return null;
                }
                replacedOwner = DetachProcessForStop_NoLock("listener_replaced");
                pendingReady = ready;
                terminalFailureObserved = false;
                endpoint = null;
                expectedRemotePeer = request.ExpectedRemotePeer.Trim();
                currentRequest = request;
                ownershipGeneration++;
                summaryObserved = false;
                providerPathsDegradedAcceptedCount = 0;
                providerPathsRecoveredCount = 0;
                providerPathsStillDegradedCount = 0;
                latestProviderPathQualitySummary = null;
            }

            replacedOwner?.Stop("listener_replaced");

            lock (gate)
            {
                if (disposed || stopGeneration != expectedStopGeneration || ct.IsCancellationRequested)
                {
                    nextProcess.Dispose();
                    return null;
                }
                SetStatus("listener_starting");
                // A status subscriber can synchronously retire this startup.
                if (disposed || stopGeneration != expectedStopGeneration || ct.IsCancellationRequested)
                {
                    nextProcess.Dispose();
                    return null;
                }
                if (!nextProcess.Start())
                {
                    nextProcess.Dispose();
                    TryReportStartFailure(expectedStopGeneration, null, "listener_start_failed", null);
                    return null;
                }
                owner = NknTunaSidecarProcessOwner.Attach("listener", nextProcess);
                processOwner = owner;
                usageStarted = BeginUsageAttempt(owner, expectedStopGeneration, ct);
            }

            if (!usageStarted)
            {
                StopOwnerIfCurrent(owner, "listener_start_retired");
                return null;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_listener_sidecar_start_attempt; attempt={attempt}; max_attempts={maxAttempts}; listen_start_timeout_sec={Math.Clamp(options.ListenStartTimeoutSec, 5, 300)}; ready_timeout_ms={options.ReadyTimeoutMs}");
            if (!IsCurrentProcessOwner(owner, nextProcess))
            {
                owner.Stop("listener_start_retired");
                return null;
            }

            nextProcess.BeginOutputReadLine();
            nextProcess.BeginErrorReadLine();
            await nextProcess.StandardInput.WriteLineAsync(password.AsMemory(), ct).ConfigureAwait(false);
            nextProcess.StandardInput.Close();

            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyCts.CancelAfter(options.ReadyTimeoutMs);
            using var registration = readyCts.Token.Register(() => ready.TrySetCanceled(readyCts.Token));
            var startedEndpoint = await ready.Task.ConfigureAwait(false);
            var startedSnapshot = TryPublishStartedListener(owner, request, startedEndpoint, expectedStopGeneration, ct);
            if (startedSnapshot is null)
            {
                StopOwnerIfCurrent(owner, "listener_ready_retired");
                return null;
            }

            return startedSnapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!TryReportStartFailure(expectedStopGeneration, owner, "listener_failed", ex.GetType().Name)) return null;
            StopOwnerIfCurrent(owner, "listener_failed");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!TryReportStartFailure(expectedStopGeneration, owner, "listener_ready_timeout", "ready_timeout")) return null;
            StopOwnerIfCurrent(owner, "listener_ready_timeout");
            return null;
        }
        catch (OperationCanceledException)
        {
            if (!TryReportStartFailure(expectedStopGeneration, owner, "listener_canceled", null)) return null;
            StopOwnerIfCurrent(owner, "listener_canceled");
            throw;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pendingReady, ready)) pendingReady = null;
                if (processOwner is null && currentRequest == request)
                {
                    currentRequest = null;
                    expectedRemotePeer = null;
                    endpoint = null;
                }
            }
        }
    }

    private NknTunaListenerAdoptionSnapshot? TryPublishStartedListener(NknTunaSidecarProcessOwner owner,
        NknTunaListenerStartRequest request, NknTunaListenerSidecarEndpoint startedEndpoint,
        long expectedStopGeneration, CancellationToken ct)
    {
        lock (gate)
        {
            if (disposed || terminalFailureObserved || stopGeneration != expectedStopGeneration || ct.IsCancellationRequested ||
                !ReferenceEquals(processOwner, owner) || currentRequest != request) return null;
            endpoint = startedEndpoint;
            var receipt = new NknTunaListenerAdoptionSnapshot(request, startedEndpoint, owner.Pid,
                owner.RunId, owner.StartedUtc, ownershipGeneration);
            SetStatus("listener_ready");
            return disposed || terminalFailureObserved || stopGeneration != expectedStopGeneration ||
                !ReferenceEquals(processOwner, owner) || currentRequest != request ? null : receipt;
        }
    }

    private bool BeginUsageAttempt(NknTunaSidecarProcessOwner owner, long expectedStopGeneration, CancellationToken ct)
    {
        lock (gate)
        {
            bool IsCurrent() => !disposed && stopGeneration == expectedStopGeneration &&
                !ct.IsCancellationRequested && ReferenceEquals(processOwner, owner);
            return IsCurrent() && (options.BeginUsageAttemptIfCurrent?.Invoke(owner.RunId, IsCurrent) ?? true) && IsCurrent();
        }
    }

    private void HandleOwnedStdoutLine(
        NknTunaSidecarProcessOwner? owner,
        Process process,
        string line,
        TaskCompletionSource<NknTunaListenerSidecarEndpoint> ready,
        Stopwatch startupStopwatch)
    {
        if (NknTunaDeliveryTrace.PreserveChildLine(line, owner?.RunId, "listener")) return;
        owner?.SetupDiagnostics.ProviderLine(line);

        AccelerationStateChangedEventArgs? notification = null;
        Action? capHandoff = null;
        lock (gate)
        {
            var request = !disposed && IsCurrentProcessOwner(owner, process) ? currentRequest : null;
            if (request is null)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_listener_sidecar_stale_output_ignored; line_len={line.Length}; decision_reason=process_owner_replaced");
                return;
            }
            NknTunaListenerAdoptionSnapshot? source = null;
            if (request is not null && endpoint is not null && owner is not null)
                source = new(request, endpoint, owner.Pid, owner.RunId, owner.StartedUtc, ownershipGeneration);
            var observedStopGeneration = stopGeneration;
            var observedOwnershipGeneration = ownershipGeneration;
            // Serialize dispatch against retirement; callbacks can also retire or
            // adopt this listener reentrantly, so recheck after each callback.
            HandleStdoutLineCore(line, ready, startupStopwatch, request, source,
                () => !disposed && stopGeneration == observedStopGeneration &&
                    ownershipGeneration == observedOwnershipGeneration && currentRequest == request &&
                    IsCurrentProcessOwner(owner, process), e =>
                    {
                        if (!e.Reason.StartsWith("listener_stream_rejected_", StringComparison.Ordinal))
                        {
                            terminalFailureObserved = true;
                            lastStartFailureReason ??= e.Reason;
                            ready.TrySetException(new InvalidOperationException(e.Reason));
                        }
                        notification = e;
                    }, action => capHandoff = action);
        }
        // The lane takes its own lock and revalidates this exact listener lease.
        // Never call it under gate: adoption takes lane -> supervisor locks.
        // A terminal latch also invalidates ready/adoption/installation, even
        // when ready already completed or no lane subscriber exists yet.
        if (notification?.ListenerFailureSource is not null)
            PublishStateUnavailable(notification);
        try { capHandoff?.Invoke(); }
        catch
        {
            // Handoff remains best-effort; the sidecar still enforces hard caps.
        }
    }

    private void HandleOwnedExit(NknTunaSidecarProcessOwner? owner, Process process,
        TaskCompletionSource<NknTunaListenerSidecarEndpoint> ready, long expectedStopGeneration)
    {
        AccelerationStateChangedEventArgs? notification = null;
        lock (gate)
        {
            if (disposed || stopGeneration != expectedStopGeneration || !IsCurrentProcessOwner(owner, process)) return;
            terminalFailureObserved = true;
            lastStartFailureReason ??= "listener_exited_before_ready";
            ready.TrySetException(new InvalidOperationException("Tuna listener sidecar exited."));
            if (endpoint is not null && currentRequest is { } request && owner is not null)
                RaiseStateUnavailable("listener_exited", request,
                    new(request, endpoint, owner.Pid, owner.RunId, owner.StartedUtc, ownershipGeneration), e => notification = e);
            SetStatus("listener_exited");
            if (disposed || stopGeneration != expectedStopGeneration || !IsCurrentProcessOwner(owner, process)) return;
            CompleteIncompleteSession("sidecar_exited_before_summary");
        }
        if (notification is not null) PublishStateUnavailable(notification);
    }

    private void SetStartupStatus(long expectedStopGeneration, CancellationToken ct, string status)
    {
        lock (gate)
        {
            if (!disposed && stopGeneration == expectedStopGeneration && !ct.IsCancellationRequested) SetStatus(status);
        }
    }

    private void HandleStdoutLine(
        string line,
        TaskCompletionSource<NknTunaListenerSidecarEndpoint> ready,
        Stopwatch startupStopwatch)
        => HandleStdoutLineCore(line, ready, startupStopwatch, null);

    private void HandleStdoutLineCore(string line,
        TaskCompletionSource<NknTunaListenerSidecarEndpoint> ready, Stopwatch startupStopwatch,
        NknTunaListenerStartRequest? request, NknTunaListenerAdoptionSnapshot? source = null,
        Func<bool>? isCurrent = null, Action<AccelerationStateChangedEventArgs>? deferState = null,
        Action<Action>? deferCap = null)
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
            var sidecarReason = TryGetString(root, "terminalReason") ??
                                TryGetString(root, "reason") ??
                                TryGetString(root, "error") ??
                                string.Empty;
            var safeReason = SanitizeLogToken(sidecarReason);
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_listener_sidecar_event; sidecar_event={safeEvent}; sidecar_reason={safeReason}; line_len={line.Length}");
            RecordProviderPathDiagnosticEvent(eventName, root);
            var stageStatus = eventName == "tuna_cap_handoff_requested" && options.ListenerCapHandoffRequested is not null
                ? null : RuntimeStatusForSidecarEvent(eventName, root);
            if (!string.IsNullOrWhiteSpace(stageStatus))
            {
                SetStatus(stageStatus);
                if (isCurrent?.Invoke() == false) return;
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
                        if (isCurrent?.Invoke() == false) return;
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
                    if (isCurrent?.Invoke() == false) return;
                    SetStatus((TryGetBool(root, "capReached") ?? false)
                        ? (TryGetString(root, "capReason") ?? "cap_reached")
                        : "session_summary");
                    break;
                case "tuna_cap_handoff_requested":
                    var capReason = TryGetString(root, "capReason") ?? "cap_reached";
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_listener_cap_handoff_requested; cap_reason={SanitizeLogToken(capReason)}; bytes_moved={TryGetInt64(root, "bytesMoved") ?? -1}; projected_bytes={TryGetInt64(root, "projectedBytes") ?? -1}; limit_bytes={TryGetInt64(root, "limitBytes") ?? -1}; remaining_bytes={TryGetInt64(root, "remainingBytes") ?? -1}");
                    // Bound handoff reports status only after exact-source stop
                    // acceptance. A deferred stale cap is diagnostic-only.
                    if (options.ListenerCapHandoffRequested is null) SetStatus("cap_handoff_pending");
                    if (isCurrent?.Invoke() == false) return;
                    if (request?.PreparationOnly == true || request?.LifetimePolicy == NknTunaListenerLifetimePolicy.SessionPreparation)
                        RaiseStateUnavailable("listener_cap_" + capReason, request, source, deferState);
                    else if (options.ListenerCapHandoffRequested is { } capCallback)
                    {
                        if (source is not null && deferCap is not null)
                            deferCap(() => capCallback(SanitizeLogToken(capReason), source));
                        else if (request is not null)
                            RaiseStateUnavailable("listener_cap_" + capReason, request, source, deferState);
                    }
                    else RequestCapHandoff(capReason);
                    break;
                case "tuna_bridge_terminal":
                    var terminalReason = TryGetString(root, "terminalReason") ?? TryGetString(root, "reason") ?? "unknown";
                    LogBridgeTerminal(root);
                    SetStatus("terminal_" + SanitizeLogToken(terminalReason));
                    if (isCurrent?.Invoke() == false) return;
                    RaiseStateUnavailable("listener_terminal_" + terminalReason, request, source, deferState);
                    break;
                case "tuna_stream_rejected":
                    var rejectReason = string.IsNullOrWhiteSpace(sidecarReason)
                        ? "tuna_stream_rejected"
                        : sidecarReason;
                    // Rejection is diagnostic evidence, not a listener lifecycle
                    // transition. Keep the live listener eligible for user stop.
                    if (isCurrent?.Invoke() == false) return;
                    RaiseStateUnavailable("listener_stream_rejected_" + rejectReason, request, source, deferState);
                    break;
                case "error":
                    SetLastStartFailureReason(TryGetString(root, "reason") ?? "sidecar_error");
                    SetStatus("sidecar_error");
                    if (isCurrent?.Invoke() == false) return;
                    RaiseStateUnavailable("listener_sidecar_error", request, source, deferState);
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

    private void RecordProviderPathDiagnosticEvent(string eventName, JsonElement root)
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
                case "provider_path_quality_summary":
                    latestProviderPathQualitySummary = ReadProviderPathQualitySummary(root);
                    break;
            }
        }
    }

    private static NknTunaProviderPathQualitySummary ReadProviderPathQualitySummary(JsonElement root)
        => new(
            TryGetString(root, "qualityClass") ?? "unknown",
            TryGetInt32(root, "usableCount") ?? -1,
            ReadIntArray(root, "missingIndices"),
            TryGetInt64(root, "recoveryLatencyMs") ?? -1,
            TryGetInt64(root, "stable3OnlyMs") ?? -1,
            ReadFinalPathReasons(root));

    private static int[] ReadIntArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<int>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number &&
                item.TryGetInt32(out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static string[] ReadFinalPathReasons(JsonElement root)
    {
        if (!root.TryGetProperty("finalPathReasons", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var index = item.TryGetProperty("index", out var indexElement) &&
                        indexElement.ValueKind == JsonValueKind.Number &&
                        indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : -1;
            var reason = item.TryGetProperty("stateReason", out var reasonElement) &&
                         reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : "unknown";
            values.Add(index.ToString(CultureInfo.InvariantCulture) + ":" + SanitizeLogToken(reason));
        }

        return values.ToArray();
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
        NknTunaSidecarProcessOwner? ownerToStop;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            stopGeneration++;
            ownerToStop = DetachProcessForStop_NoLock("disposed");
            endpoint = null;
            currentRequest = null;
        }

        ownerToStop?.Stop("disposed");
    }

    public void Stop(string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "listener_stopped" : reason.Trim();
        NknTunaSidecarProcessOwner? ownerToStop;
        lock (gate)
        {
            stopGeneration++;
            ownerToStop = DetachProcessForStop_NoLock(normalizedReason);
            endpoint = null;
            expectedRemotePeer = null;
            currentRequest = null;
            SetStatus(string.IsNullOrWhiteSpace(reason) ? "listener_stopped" : $"listener_stopped_{normalizedReason}");
        }

        ownerToStop?.Stop(normalizedReason);
    }

    public bool TryDetachCurrentListener(NknTunaListenerAdoptionSnapshot snapshot, out Action<string>? stop)
        => TryDetachForRearm(snapshot, out stop, out _);

    public bool TryDetachForRearm(NknTunaListenerAdoptionSnapshot snapshot, out Action<string>? stop, out NknListenerVacancy? vacant, bool replacing = false)
    {
        stop = null;
        vacant = null;
        lock (gate)
        {
            if (disposed || (replacing && startGate.CurrentCount == 0) || processOwner is not { } owner || currentRequest != snapshot.CurrentRequest ||
                endpoint != snapshot.Endpoint || ownershipGeneration != snapshot.OwnershipGeneration ||
                owner.Pid != snapshot.ProcessId || owner.RunId != snapshot.ProcessRunId ||
                owner.StartedUtc != snapshot.ProcessStartedUtc) return false;
            processOwner = null;
            endpoint = null;
            expectedRemotePeer = null;
            currentRequest = null;
            stopGeneration++;
            ownershipGeneration++;
            pendingReady?.TrySetCanceled();
            pendingReady = null;
            stop = owner.Stop;
            vacant = new(this, stopGeneration, ownershipGeneration);
            CompleteIncompleteSession_NoLock("listener_detached");
            return true;
        }
    }

    private NknTunaSidecarProcessOwner? DetachProcessForStop_NoLock(string reason)
    {
        pendingReady?.TrySetCanceled();
        pendingReady = null;
        var current = processOwner;
        processOwner = null;
        if (current is not null)
        {
            CompleteIncompleteSession_NoLock(reason);
        }

        return current;
    }

    private bool IsStartupCurrent(long expectedStopGeneration)
    {
        lock (gate) return !disposed && stopGeneration == expectedStopGeneration;
    }

    private bool TryReportStartFailure(long expectedStopGeneration, NknTunaSidecarProcessOwner? owner,
        string reason, string? error)
    {
        lock (gate)
        {
            if (disposed || stopGeneration != expectedStopGeneration || !ReferenceEquals(processOwner, owner))
                return false;
            if (error is not null)
                LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_listener_sidecar_start_failed; error={error}");
            lastStartFailureReason = reason == "listener_failed" ? lastStartFailureReason ?? reason : reason;
            CompleteIncompleteSession_NoLock(reason);
            if (disposed || stopGeneration != expectedStopGeneration || !ReferenceEquals(processOwner, owner))
                return false;
            SetStatus(reason);
            return true;
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

    private void RaiseStateUnavailable(string reason, NknTunaListenerStartRequest? request,
        NknTunaListenerAdoptionSnapshot? source = null, Action<AccelerationStateChangedEventArgs>? deferState = null)
    {
        var notification = new AccelerationStateChangedEventArgs(
            isAvailable: false,
            SanitizeLogToken(reason), sessionPreparationExecutionIdentity:
                request?.PreparationOnly == true || request?.LifetimePolicy == NknTunaListenerLifetimePolicy.SessionPreparation
                    ? request.ExecutionIdentity : null, listenerFailureSource: source);
        if (deferState is not null) deferState(notification);
        else PublishStateUnavailable(notification);
    }

    private void PublishStateUnavailable(AccelerationStateChangedEventArgs notification)
    {
        try
        {
            StateChanged?.Invoke(this, notification);
        }
        catch
        {
            // Diagnostics-only state updates must never affect listener flow.
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
                currentRequest = null;
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

    private static void HandleOwnedListenerStderr(NknTunaSidecarProcessOwner? owner, string line)
    {
        owner?.SetupDiagnostics.StreamErrorLine(line);
        LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_listener_sidecar_stderr; line_len={line.Length}");
    }

    private static void LogBridgeTerminal(JsonElement root)
    {
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            "event=tuna_listener_bridge_terminal" +
            $"; terminal_reason={SanitizeLogToken(TryGetString(root, "terminalReason") ?? TryGetString(root, "reason"))}" +
            $"; stream_error_category={NknTunaSetupDiagnostics.StreamErrorCategory(TryGetString(root, "rawReason"))}" +
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
