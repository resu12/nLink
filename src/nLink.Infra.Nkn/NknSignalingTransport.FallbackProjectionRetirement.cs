using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    // Capture local projection identity as evidence under accelerationGate. Only
    // the subsequently applied coordinator disposition may retire this object.
    private TunaFallbackProofState? CaptureFileFallbackProjectionUnsafe(FileTransferRouteTransitionAuthority authority)
    {
        if (tunaFallbackProofState is not { } state ||
            state.SessionId != authority.SessionId || (state.Lanes & NknAccelerationLaneKind.File) == 0)
        {
            return null;
        }

        if (authority.TargetRoute == FileTransferRoute.PostTunaFallbackV6)
        {
            return state;
        }

        return state.FileProjectionRetirement is null &&
            authority.Kind == FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4 &&
            state.FileProjectionPublication == authority.SourceRoutePublication &&
            state.FileState == TunaFallbackLaneState.Recovered &&
            state.FileV6TransportEpoch == authority.SourceTransportEpoch &&
            accelerationUserStoppedSessionId != authority.SessionId &&
            accelerationPeerUserStoppedSessionId != authority.SessionId
                ? state : null;
    }

    private bool ApplyFileFallbackProjectionDispositionUnsafe(
        FileTransferRouteTransitionDispositionCommand command, TunaFallbackProofState? captured)
    {
        if (captured is null || !ReferenceEquals(captured, tunaFallbackProofState) ||
            command.Kind != FileTransferRouteTransitionDispositionKind.Committed ||
            command.RoutePublicationToken is not { } publication ||
            publication.SessionId != command.Authority.SessionId || publication.TransferId != command.Authority.TransferId ||
            publication.Direction != command.Authority.Direction)
        {
            return false;
        }

        if (command.Authority.TargetRoute == FileTransferRoute.PostTunaFallbackV6)
        {
            if (captured.FileProjectionRetirement is { } retired)
            {
                if (command.Authority.SourceRoute != FileTransferRoute.FileTunaV4 ||
                    command.Authority.SourceProtocolVersion != 4 ||
                    command.Authority.SourceTransport != FileTransferTransportKind.Tuna ||
                    command.Authority.ProofBoundary is not FileTransferFallbackTransitionProofBoundary fallback ||
                    (command.Authority.TransferId == retired.Authority.TransferId &&
                     command.Authority.SourceRoutePublication != retired.RoutePublicationToken))
                {
                    return false;
                }
                // A new coordinator-owned fallback, not an epoch observation,
                // can reuse this session projection. Screen state is untouched.
                captured.FileProjectionRetirement = null;
                captured.FileV6TransportEpoch = fallback.TargetTransportEpoch;
                captured.FileV6EpochState = V6TransportEpochState.Recovered;
            }
            else if (captured.FileProjectionPublication is null && captured.FileV6TransportEpoch == 0 &&
                command.Authority.TargetProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
                command.Authority.TargetTransport == FileTransferTransportKind.RegularNkn &&
                command.Authority.ProofBoundary is FileTransferFallbackTransitionProofBoundary { TargetTransportEpoch: > 0 } initialFallback)
            {
                // Bind the first fallback's epoch from its committed command;
                // retirement must not depend on a later epoch observation.
                captured.FileV6TransportEpoch = initialFallback.TargetTransportEpoch;
            }
            captured.FileProjectionPublication = publication;
            return false;
        }

        if (captured.FileProjectionRetirement is not null ||
            captured.FileProjectionPublication is not { } source ||
            !FileTransferCoordinator.AuthorizesFileFallbackProjectionRetirement(command, source) ||
            captured.FileState != TunaFallbackLaneState.Recovered ||
            captured.FileV6TransportEpoch != command.Authority.SourceTransportEpoch ||
            accelerationUserStoppedSessionId == command.Authority.SessionId ||
            accelerationPeerUserStoppedSessionId == command.Authority.SessionId)
        {
            return false;
        }

        // Keep the screen lane, proof counters and pending control-plane owners.
        // The retained tombstone also prevents old epoch observations reactivating this projection.
        captured.FileProjectionRetirement = command;
        LocalOperationalLog.Info("NKN.Tuna",
            $"event=filetransfer_fallback_projection_retired; session_id={SanitizeLogToken(source.SessionId)}; transfer_id={SanitizeLogToken(source.TransferId)}; direction={FormatFileTransferDirection(source.Direction)}; fallback_epoch={captured.Epoch}; authority_id={command.Authority.AuthorityId}; transition_generation={command.Authority.TransitionGeneration}; source_route_publication_generation={source.PublicationGeneration}; source_leg_generation={source.LegGeneration}; source_transport_epoch={command.Authority.SourceTransportEpoch}; terminal_generation={publication.TerminalGeneration}; route_publication_generation={publication.PublicationGeneration}; route_commit_generation={publication.RouteCommitGeneration}; reason=coordinator_committed_reactivation");
        return true;
    }
}
