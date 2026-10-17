using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport : IFileTransferFallbackRepairRetryStopObserver
{
    FileTransferFallbackRepairRetryStopObservation?
        IFileTransferFallbackRepairRetryStopObserver.ObserveStoppedFallbackRepairRetry(
            FileTransferFallbackRepairHandoffDrainReceipt drain,
            RuntimeUnlockAdmissionToken admission, long dataSessionProviderGeneration)
    {
        // Match route publication/launch lock order. An idle listener or busy slot
        // is not proof of closure; require an explicit stop and no active attempt.
        lock (gate)
        lock (accelerationGate)
        {
            var source = drain.Capture.Command.Source;
            if (disposed || admission.IsDefault || source.Direction != FileTransferDirection.Inbound ||
                source.RuntimeOwner != admission.RuntimeOwner || source.RoutePublication is not { } publication ||
                source.SessionId != currentSessionSecurityState.SessionId?.Value ||
                !fileTransferDataSessions.TryGetValue(source.TransferId, out var session) || session.IsDisposed ||
                session.SessionId != source.SessionId || dataSessionProviderGeneration <= 0 ||
                session.Generation != dataSessionProviderGeneration ||
                !fileTransferRouteHints.TryGetValue(new(source.SessionId, source.TransferId, source.Direction), out var hint) ||
                hint.Route != FileTransferRoute.PostTunaFallbackV6 || hint.ProtocolVersion != 6 ||
                hint.LiveRouteEpoch != publication.LiveRouteEpoch || hint.RuntimeGeneration != publication.RuntimeGeneration ||
                hint.TerminalGeneration != publication.TerminalGeneration || hint.PublicationLegGeneration != publication.LegGeneration ||
                hint.RoutePublicationGeneration != publication.PublicationGeneration || hint.RouteCommitGeneration != publication.RouteCommitGeneration ||
                hint.FallbackTransportEpoch != source.TransportEpoch || hint.FallbackBridgeRecoveryGeneration != source.BridgeRecoveryGeneration ||
                accelerationNegotiationSlotGeneration != 0 || accelerationNegotiationScheduled != 0 ||
                !string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce) || runtimeUnlockRetiredAnswerBarrier is not null ||
                runtimeUnlockTransactionState.State is not (RuntimeUnlockTransactionState.Idle or
                    RuntimeUnlockTransactionState.Failed or RuntimeUnlockTransactionState.Retired))
            {
                return null;
            }

            if (IsAccelerationUserStoppedForCurrentSession())
            {
                return new(drain, admission, dataSessionProviderGeneration, FileTransferFallbackRepairRetryStopReason.LocalSwitchOff);
            }
            return IsAccelerationPeerUserStoppedForFallbackSession(source.SessionId)
                ? new(drain, admission, dataSessionProviderGeneration, FileTransferFallbackRepairRetryStopReason.PeerSwitchOff)
                : null;
        }
    }
}
