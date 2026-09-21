namespace NLink.Infra.Nkn;

internal sealed partial class NknTunaAccelerationLane
{
    private sealed record LaneStartOperation(long Generation, bool Preparation = false, string? ExecutionIdentity = null)
    {
        public CancellationTokenRegistration Revocation;
    }
    private long startGeneration;
    private LaneStartOperation? pendingLaneStart;
    private LaneStartOperation? currentClientStart;

    private bool IsPreparationOwnedUnsafe()
        => pendingLaneStart?.Preparation == true || client is not null && currentClientStart?.Preparation == true;

    private async Task ConnectStartedClientAsync(NknTunaSidecarClient nextClient, string endpoint,
        LaneStartOperation operation, CancellationToken ct)
    {
        if (!operation.Preparation)
        {
            await nextClient.ConnectAsync(endpoint, TimeSpan.FromMilliseconds(options.ConnectTimeoutMs), ct).ConfigureAwait(false);
            return;
        }
        // Preparation cancellation owns startup, not a future adopted client's
        // read/write lifetime. After connect, cancellation goes through the exact
        // lease callback, which ownership retention can safely retire.
        using var startup = new CancellationTokenSource();
        using var cancelStartup = ct.Register(startup.Cancel);
        await nextClient.ConnectAsync(endpoint, TimeSpan.FromMilliseconds(options.ConnectTimeoutMs), startup.Token).ConfigureAwait(false);
    }

    public async Task<NknTunaLaneLease?> StartPreparationLaneAsync(NknTunaLanePreparationRequest request, CancellationToken ct)
    {
        if (!options.Enabled || (options.Lanes & NknAccelerationLaneKind.File) == 0 ||
            string.IsNullOrWhiteSpace(request.ExecutionIdentity) || string.IsNullOrWhiteSpace(request.ExpectedRemotePeer) ||
            request.LocalIsPayer && (listenerSupervisor is null || !CanOfferListener) ||
            !request.LocalIsPayer && string.IsNullOrWhiteSpace(request.RemoteTunaAddress)) return null;
        LaneStartOperation operation;
        lock (gate)
        {
            // Never coalesce with or take over generic, retry, or successor work.
            if (disposed || ct.IsCancellationRequested || client is not null || dialerProcessOwner is not null ||
                pendingLaneStart is not null || dialerStartTask is not null) return null;
            operation = pendingLaneStart = new(++startGeneration, Preparation: true, request.ExecutionIdentity);
        }

        bool connected;
        try
        {
            connected = request.LocalIsPayer
                ? await ConnectListenerAsync(new(request.ExpectedRemotePeer, NknAccelerationLaneKind.File,
                    NknTunaListenerLifetimePolicy.SessionPreparation, request.ExecutionIdentity), operation, ct).ConfigureAwait(false)
                : await StartDialerSidecarCoreAsync(request.RemoteTunaAddress!, request.ExpectedRemotePeer, operation, ct).ConfigureAwait(false);
        }
        finally { FinishLaneStart(operation); }
        NknTunaLaneLease? lease;
        lock (gate)
        {
            lease = connected ? CaptureCurrentLaneLease() : null;
            if (lease?.StartGeneration != operation.Generation) lease = null;
        }
        if (lease is null)
        {
            return null;
        }
        operation.Revocation = ct.Register(() => TryStopCurrentLaneLease(lease, "preparation_revoked"));
        lock (gate)
        {
            if (!ct.IsCancellationRequested && CaptureCurrentLaneLease() == lease) return lease;
        }
        operation.Revocation.Unregister();
        TryStopCurrentLaneLease(lease, "preparation_retired_before_return");
        return null;
    }

    private LaneStartOperation BeginLaneStartUnsafe()
        => pendingLaneStart = new(++startGeneration);

    private void RetireLaneStartUnsafe()
    {
        pendingLaneStart?.Revocation.Unregister();
        currentClientStart?.Revocation.Unregister();
        ++startGeneration;
        pendingLaneStart = null;
    }

    private bool IsLaneStartCurrentUnsafe(LaneStartOperation operation, CancellationToken ct)
        => !disposed && !ct.IsCancellationRequested && ReferenceEquals(pendingLaneStart, operation) &&
            operation.Generation == startGeneration;

    private void FinishLaneStart(LaneStartOperation operation)
    {
        lock (gate)
        {
            if (ReferenceEquals(pendingLaneStart, operation)) pendingLaneStart = null;
        }
    }

    public NknTunaLaneLease? CaptureCurrentLaneLease()
    {
        lock (gate)
        {
            if (disposed || pendingLaneStart is not null || client?.IsAvailable != true) return null;
            var listener = clientRole == ClientRole.Listener ? CaptureCurrentListenerAdoptionSnapshot() : null;
            if (clientRole == ClientRole.Listener && listenerSupervisor is not null && listener is null) return null;
            if (clientRole == ClientRole.Dialer &&
                (clientDialerProcessOwner is null || !ReferenceEquals(clientDialerProcessOwner, dialerProcessOwner))) return null;
            return new(laneInstanceId, startGeneration, clientGeneration, client, clientDialerProcessOwner, listener);
        }
    }

    public bool TryStopCurrentLaneLease(NknTunaLaneLease lease, string reason)
    {
        Action<string>? stopListener = null;
        NknTunaSidecarClient retiredClient;
        NknTunaSidecarProcessOwner? retiredDialer;
        lock (gate)
        {
            if (disposed || pendingLaneStart is not null || lease.LaneInstanceId != laneInstanceId ||
                lease.StartGeneration != startGeneration || lease.ClientGeneration != clientGeneration ||
                !ReferenceEquals(lease.Client, client) || !ReferenceEquals(lease.DialerOwner, clientDialerProcessOwner)) return false;
            if (clientRole == ClientRole.Listener && listenerSupervisor is not null &&
                (lease.Listener is null || lease.Listener.CurrentRequest != listenerRequest ||
                 (clientListenerLease is not null && lease.Listener != clientListenerLease) ||
                 !listenerSupervisor.TryDetachCurrentListener(lease.Listener, out stopListener))) return false;
            if (clientRole == ClientRole.Dialer && !ReferenceEquals(lease.DialerOwner, dialerProcessOwner)) return false;

            retiredClient = client!;
            retiredDialer = clientDialerProcessOwner;
            CaptureClientDiagnostics_NoLock(retiredClient);
            retiredClient.MessageReceived -= OnClientMessageReceived;
            retiredClient.StateChanged -= OnClientStateChanged;
            client = null;
            clientRole = ClientRole.None;
            clientDialerProcessOwner = null;
            dialerProcessOwner = null;
            listenerRequest = null;
            currentFailureOrigin = null;
            RetireLaneStartUnsafe();
        }

        // Only detached instances are touched here; successor installation is
        // allowed while disposal/process shutdown runs. No StateChanged or
        // generic supervisor stop is emitted by stale-cleanup handling.
        retiredClient.Dispose();
        retiredDialer?.Stop(reason);
        stopListener?.Invoke(reason);
        return true;
    }

    // Rotate physical cleanup ownership only. The adapter must first obtain its
    // Core admission/ownership decision; this receipt is not that authority.
    public bool TryRetainCurrentLaneLease(NknTunaLaneLease lease, out NknTunaLaneLease? retained)
    {
        lock (gate)
        {
            retained = null;
            var current = CaptureCurrentLaneLease();
            if (current is null || current != lease) return false;
            clientGeneration++;
            currentClientStart?.Revocation.Unregister();
            currentClientStart = null;
            retained = lease with { ClientGeneration = clientGeneration };
            return true;
        }
    }

    private void StopExactListener(NknTunaListenerAdoptionSnapshot? snapshot, LaneStartOperation operation, string reason)
    {
        Action<string>? stop = null;
        lock (gate)
        {
            // A successor may have reused this physical listener. An old
            // completion cannot infer ownership from a fresh supervisor read.
            if (!operation.Preparation || operation.Generation != startGeneration || clientRole == ClientRole.Listener) return;
            if (snapshot is not null && listenerSupervisor is not null)
                listenerSupervisor.TryDetachCurrentListener(snapshot, out stop);
        }
        stop?.Invoke(reason);
    }

    public bool TryTransferPreparationLaneLease(NknTunaLaneLease lease, string identity, bool activate,
        out NknTunaLaneLease? successor)
    {
        lock (gate)
        {
            successor = null;
            if (string.IsNullOrWhiteSpace(identity) || currentClientStart?.Preparation != true ||
                CaptureCurrentLaneLease() != lease) return false;
            if (lease.Listener is { } listener)
            {
                var request = listener.CurrentRequest with
                {
                    ExecutionIdentity = identity,
                    LifetimePolicy = activate ? NknTunaListenerLifetimePolicy.CoordinatorAdmittedTransfer
                        : NknTunaListenerLifetimePolicy.SessionPreparation,
                };
                if (!TryAdoptCurrentListener(listener, request, out _)) return false;
            }
            else clientGeneration++;
            currentClientStart.Revocation.Unregister();
            currentClientStart = activate ? null : new(startGeneration, Preparation: true, identity);
            successor = CaptureCurrentLaneLease();
            return successor is not null;
        }
    }

}
