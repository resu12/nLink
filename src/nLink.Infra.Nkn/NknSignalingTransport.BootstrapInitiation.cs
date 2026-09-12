using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private FileTransferBootstrapLocalStopSource[] CaptureFallbackBootstrapLocalStopSources(string reason)
    {
        if (reason != "runtime_disabled" || currentSessionSecurityState.SessionId?.Value is not { } sessionId) { return []; }
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
                    publication.RouteToken != FileTransferRouteResolver.FileTunaV4Token || publication.RouteCommitGeneration is not > 0)
                {
                    continue;
                }
                sources.Add(new(new(state.BootstrapLocalIssuer!, publication,
                    new(hint.Token, hint.ProtocolVersion, hint.LiveRouteEpoch, hint.RuntimeGeneration, hint.RoutePublicationGeneration,
                        hint.RouteCommitGeneration ?? 0, hint.TerminalGeneration, hint.PublicationLegGeneration, hint.FallbackTransportEpoch,
                        hint.FallbackBridgeRecoveryGeneration, hint.FallbackCheckpointRequestId ?? "(none)", hint.FallbackCheckpointGeneration)),
                    session, session.Generation));
            }
        }
        return sources.ToArray();
    }

    private void PublishFallbackBootstrapLocalStops(FileTransferBootstrapLocalStopSource[] sources)
    {
        foreach (var source in sources)
        {
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
                observationGeneration, fallbackEpoch, "runtime_disabled", "local_runtime_disabled", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                InitiationKind = FileTransferBootstrapInitiationKind.LocalRuntimeDisable,
                LocalStop = source,
            };
            // No legacy session-only coalescing: the Coordinator validates the captured source before idempotence.
            fallbackV6ProofBootstrapEvaluationRequested?.Invoke(this, new(evaluation));
        }
    }
}
