using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private bool PublishFallbackBootstrapLocalStopsBeforeReset(FileTransferBootstrapLocalStopSource[]? sources, string reason)
    {
        if (sources is not { Length: > 0 } ||
            !TryCaptureAccelerationNegotiation(out var sessionId, out var lanes) ||
            (lanes & NknAccelerationLaneKind.File) == 0 ||
            sources.Any(source => source.Scope.Publication.SessionId != sessionId)) { return false; }
        StartTunaFallbackProofIfNeeded(reason, sessionId, lanes & NknAccelerationLaneKind.File);
        if (ShouldSuppressDuplicateRecoveredFileTransferFallback(sessionId, reason)) { return false; }
        // Reserve observation identity without disabling the source lane. Core synchronously
        // validates the copied source and installs its hold before reset changes send eligibility.
        MarkFileTransferFallbackNknProofPending(reason, sessionId, lanes & NknAccelerationLaneKind.File);
        PublishFallbackBootstrapLocalStops(sources, reason);
        return true;
    }

    private FileTransferBootstrapLocalStopSource[] CaptureFallbackBootstrapLocalStopSources(string reason)
    {
        return reason is "runtime_disabled" or "header_switch_off" ? CaptureFallbackBootstrapLocalSources(null) : [];
    }

    private FileTransferBootstrapLocalStopSource[] CaptureFallbackBootstrapFailureSources(AccelerationStateChangedEventArgs args)
    {
        if (args.IsAvailable || NormalizeAccelerationSidecarResetReason(args.Reason) is not ("sidecar_tuna_write_failed" or "sidecar_tuna_stream_eof") ||
            args.FailureOrigin is not { IsDefault: false } origin || accelerationLane?.IsFailureOriginCurrent(origin) != true) { return []; }
        return CaptureFallbackBootstrapLocalSources(new(origin.LaneInstanceId, origin.ClientGeneration, origin.ProcessRunId, origin.FailureGeneration));
    }

    bool IFileTransferRouteTransitionBridge.IsFallbackBootstrapFailureCurrent(FileTransferBootstrapLocalStopSource source)
        => source.FailureOrigin is { IsDefault: false } origin && accelerationLane?.IsFailureOriginCurrent(
            new(origin.LaneInstanceId, origin.ClientGeneration, origin.ProcessRunId, origin.FailureGeneration)) == true;

    private FileTransferBootstrapLocalStopSource[] CaptureFallbackBootstrapLocalSources(FileTransferBootstrapFailureOrigin? origin)
    {
        if (currentSessionSecurityState.SessionId?.Value is not { } sessionId) { return []; }
        KeyValuePair<string, FileTransferTransportState>[] selected;
        lock (controlSecureStateGate)
        {
            selected = fileTransferStates.Where(pair => !pair.Value.IsTerminal && pair.Value.CorrelatedBootstrapSelected &&
                !string.IsNullOrEmpty(pair.Value.BootstrapLocalIssuer)).ToArray();
        }
        var sources = new List<FileTransferBootstrapLocalStopSource>();
        lock (gate)
        {
            foreach (var (transferId, state) in selected)
            {
                var direction = state.InitiatedLocally ? FileTransferDirection.Outbound : FileTransferDirection.Inbound;
                var key = new FileTransferRouteHintKey(sessionId, transferId, direction);
                if (!fileTransferDataSessions.TryGetValue(transferId, out var session) || session.IsDisposed || session.SessionId != sessionId ||
                    !fileTransferRoutePublicationAuthorities.TryGetValue(key, out var publication) ||
                    !fileTransferRouteHints.TryGetValue(key, out var hint) || hint.Route != FileTransferRoute.FileTunaV4 || hint.ProtocolVersion != 4 ||
                    publication.RouteToken != FileTransferRouteResolver.FileTunaV4Token ||
                    (publication.RouteCommitGeneration is not > 0 &&
                     (publication.RouteCommitGeneration is not null || publication.RoutePublicationGeneration != 1 ||
                      publication.LiveRouteEpoch != 0 || publication.TerminalGeneration != 0)))
                {
                    continue;
                }
                sources.Add(new(new(state.BootstrapLocalIssuer!, publication,
                    new(hint.Token, hint.ProtocolVersion, hint.LiveRouteEpoch, hint.RuntimeGeneration, hint.RoutePublicationGeneration,
                        hint.RouteCommitGeneration ?? 0, hint.TerminalGeneration, hint.PublicationLegGeneration, hint.FallbackTransportEpoch,
                        hint.FallbackBridgeRecoveryGeneration, hint.FallbackCheckpointRequestId ?? "(none)", hint.FallbackCheckpointGeneration)),
                    session, session.Generation) { FailureOrigin = origin });
            }
        }
        return sources.ToArray();
    }

    private void PublishFallbackBootstrapLocalStops(FileTransferBootstrapLocalStopSource[] sources, string reason)
    {
        foreach (var source in sources)
        {
            var failure = source.FailureOrigin is not null;
            if (failure && !((IFileTransferRouteTransitionBridge)this).IsFallbackBootstrapFailureCurrent(source)) { continue; }
            var publication = source.Scope.Publication;
            long observationGeneration;
            lock (fileTransferFallbackProofGate)
            {
                if (!fileTransferFallbackProofPending || fileTransferFallbackProofSessionId != publication.SessionId) { continue; }
                observationGeneration = fileTransferFallbackProofGeneration;
            }
            long fallbackEpoch;
            lock (accelerationGate)
            {
                if (!TryGetCurrentTunaFallbackProofStateUnsafe(publication.SessionId, out var pending)) { continue; }
                fallbackEpoch = pending.Epoch;
            }
            var evaluation = new FileTransferFallbackV6ProofBootstrapEvaluation(publication.SessionId, publication.Direction,
                observationGeneration, fallbackEpoch, failure ? reason : "runtime_disabled",
                failure ? "local_transport_failure" : "local_runtime_disabled", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                InitiationKind = failure ? FileTransferBootstrapInitiationKind.LocalTransportFailure : FileTransferBootstrapInitiationKind.LocalRuntimeDisable,
                LocalStop = source,
            };
            // No legacy session-only coalescing: the Coordinator validates the captured source before idempotence.
            fallbackV6ProofBootstrapEvaluationRequested?.Invoke(this, new(evaluation));
        }
    }
}
