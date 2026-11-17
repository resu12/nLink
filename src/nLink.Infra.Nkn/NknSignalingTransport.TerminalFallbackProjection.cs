using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    bool IFileTransferRouteTransitionBridge.ApplyTerminalFallbackProjectionRetirement(
        FileTransferTerminalFallbackProjectionRetirementCommand command)
    {
        var publication = command.Publication;
        if (publication.IsDefault || publication.Route != FileTransferRoute.PostTunaFallbackV6 ||
            publication.ProtocolVersion != 6 || command.TerminalGeneration <= 0 ||
            command.TerminalGeneration != publication.TerminalGeneration + 1 ||
            command.TransportEpoch <= 0 || command.DataSessionGeneration <= 0)
            return false;

        long fallbackEpoch;
        // Only local state is inspected/mutated here. In particular, no service
        // callback or availability notification may run under these locks.
        lock (gate)
        {
            if (!fileTransferDataSessions.TryGetValue(publication.TransferId, out var session) ||
                session.IsDisposed || session.SessionId != publication.SessionId ||
                session.Generation != command.DataSessionGeneration)
                return false;

            lock (accelerationGate)
            {
                var shadow = fileTransferRouteTransitionExecutionShadow;
                if (tunaFallbackProofState is not { } projection || projection.IsFileProjectionRetired ||
                    projection.SessionId != publication.SessionId ||
                    (projection.Lanes & NknAccelerationLaneKind.File) == 0 ||
                    projection.FileProjectionPublication != publication ||
                    projection.FileProjectionDataSessionGeneration != command.DataSessionGeneration ||
                    projection.FileV6TransportEpoch != command.TransportEpoch ||
                    projection.FileState != TunaFallbackLaneState.Recovered ||
                    shadow.Current is not null || shadow.RetainedPredecessor is not null ||
                    shadow.PendingAvailabilityDisposition is not null)
                    return false;

                lock (fileTransferFallbackProofGate)
                {
                    if (fileTransferFallbackProofPending &&
                        fileTransferFallbackProofSessionId == publication.SessionId &&
                        (fileTransferFallbackProofLanes & NknAccelerationLaneKind.File) != 0)
                        return false;

                    projection.FileTerminalProjectionRetirement = command;
                    fallbackEpoch = projection.Epoch;
                }
            }
        }

        LocalOperationalLog.Info("NKN.Tuna",
            $"event=filetransfer_fallback_projection_terminal_retired; session_id={SanitizeLogToken(publication.SessionId)}; transfer_id={SanitizeLogToken(publication.TransferId)}; direction={FormatFileTransferDirection(publication.Direction)}; fallback_epoch={fallbackEpoch}; source_route_publication_generation={publication.PublicationGeneration}; source_leg_generation={publication.LegGeneration}; source_transport_epoch={command.TransportEpoch}; terminal_generation={command.TerminalGeneration}; data_session_generation={command.DataSessionGeneration}; reason=coordinator_terminalized");
        return true;
    }
}
