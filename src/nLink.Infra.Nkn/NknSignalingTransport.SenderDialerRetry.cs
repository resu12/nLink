using System.Security.Cryptography;
using System.Text;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private static bool ResponderAcceptanceMatchesPhysicalRole(
        RuntimeUnlockResponderAnswerAcceptanceSource source, string runId)
        => source == RuntimeUnlockResponderAnswerAcceptanceSource.AuthenticatedAnswer
            ? runId.StartsWith("listener-start-", StringComparison.Ordinal)
            : source is (RuntimeUnlockResponderAnswerAcceptanceSource.SemanticAnswerAck or
                  RuntimeUnlockResponderAnswerAcceptanceSource.PeerPreCommitProbe) &&
              runId.StartsWith("peer-offer-dialer-", StringComparison.OrdinalIgnoreCase);

    private bool IsRetainedListenerAnswerPhysicalSourceCurrent(RuntimeUnlockResponderAnswerProbeConvergenceHold evidence)
        => evidence.AcceptanceSource != RuntimeUnlockResponderAnswerAcceptanceSource.AuthenticatedAnswer ||
           evidence.PhysicalListener is { } listener &&
           (accelerationLane as INknTunaAccelerationSession)?.CaptureCurrentListenerAdoptionSnapshot() == listener;

    private bool TryContinueInstalledRetryOnAuthenticatedDialer(
        INknTunaAccelerationSession session, RuntimeUnlockRetryDispatchInstallReceipt receipt)
    {
        if (receipt.Authority.Direction != FileTransferDirection.Outbound ||
            receipt.Authority.Route != FileTransferRoute.PostTunaFallbackV6 ||
            receipt.Authority.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            receipt.Authority.ReleaseKind != RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield ||
            session.CaptureCurrentLaneLease() is not { DialerOwner: not null, Listener: null } physicalLease)
            return false;

        RuntimeUnlockRouteCommitSnapshot? launch = null;
        var reason = "authenticated_peer_answer_ack_not_current";
        if (!TryCaptureCurrentRuntimeUnlockFileTransferOwnerAuthority(receipt.Authority.SessionId,
                out var publication, out _) ||
            !FileTransferCoordinator.MatchesRoutePublicationAuthority(publication, receipt.Authority.SourcePublicationAuthority))
            return true;
        lock (accelerationGate)
        {
            var current = runtimeUnlockTransactionState;
            if (runtimeUnlockRecoveryRetryState is { } state &&
                TryValidateRuntimeUnlockRetryDispatchInstallReceiptCurrentUnsafe(state, receipt, out reason) &&
                session.CaptureCurrentLaneLease() == physicalLease &&
                string.Equals(current.SessionId, receipt.Authority.SessionId, StringComparison.Ordinal) &&
                TryCaptureLiveExactRuntimeUnlockResponderWaitingConvergenceUnsafe(
                    current, receipt.Authority.TransferId, out var handshake) &&
                handshake is
                {
                    AcceptanceSource: RuntimeUnlockResponderAnswerAcceptanceSource.SemanticAnswerAck,
                    AckMessageId.Length: > 0, AckSourceFingerprint.Length: 64, AckFrameFingerprint.Length: 64,
                    AckObservedUtcMs: > 0,
                } &&
                handshake.PhysicalDialerLease == physicalLease &&
                handshake.AckObservedUtcMs <= handshake.AnswerDeadlineUtcMs &&
                current.HasPeerVisibleProof && !current.RouteCommitted &&
                ResponderAcceptanceMatchesPhysicalRole(handshake.AcceptanceSource, current.TunaPathLeaseListenerRunId!))
            {
                var sameLaunch = runtimeUnlockPreCommitProbeLaunchSnapshot is { } prior &&
                    prior.SessionId == current.SessionId && prior.TransferId == receipt.Authority.TransferId &&
                    prior.TransactionGeneration == current.TransactionGeneration && prior.OfferGeneration == current.OfferGeneration &&
                    prior.TunaPathLeaseGeneration == current.TunaPathLeaseGeneration &&
                    prior.TunaPathLeaseListenerRunId == current.TunaPathLeaseListenerRunId;
                var generation = sameLaunch ? runtimeUnlockPreCommitProbeLaunchGeneration : ++runtimeUnlockPreCommitProbeLaunchGeneration;
                launch = CreateRuntimeUnlockPreCommitProbeLaunchSnapshot(current, receipt.Authority.TransferId,
                    "installed_retry_authenticated_sender_dialer", generation);
                runtimeUnlockPreCommitProbeLaunchSessionId = current.SessionId;
                runtimeUnlockPreCommitProbeLaunchTransferId = receipt.Authority.TransferId;
                runtimeUnlockPreCommitProbeLaunchTransactionGeneration = current.TransactionGeneration;
                runtimeUnlockPreCommitProbeLaunchOfferGeneration = current.OfferGeneration;
                runtimeUnlockPreCommitProbeLaunchSnapshot = launch;
                ClearRuntimeUnlockPreCommitProbeLaunchHoldUnsafe();
            }
        }

        // The retained handshake is evidence, not a route grant. In particular,
        // do not invent a new offer or report DispatchObserved for this path.
        // The Service must admit the probe using the exact installed Core grant.
        if (launch is not null && session.CaptureCurrentLaneLease() == physicalLease &&
            ValidateRuntimeUnlockRetryDispatchInstallReceiptBoundary(receipt, "sender_dialer_probe_handoff"))
        {
            LocalOperationalLog.Info("NKN.Tuna",
                $"event=runtime_unlock_sender_dialer_continuation; decision_status=Requested; session_id={SanitizeLogToken(launch.SessionId)}; transfer_id={SanitizeLogToken(launch.TransferId)}; transaction_generation={launch.TransactionGeneration}; offer_generation={launch.OfferGeneration}; install_generation={receipt.InstallGeneration}; authority_effect=none; decision_reason=authenticated_handshake_requires_coordinator_probe_admission");
            RequestFileTransferDataSessionsHandoff("installed_retry_authenticated_sender_dialer",
                FileTransferTransportHandoffKind.NormalToTunaActivation, FileTransferTransportKind.Tuna,
                launch.SessionId, launch);
        }
        else
        {
            if (reason == "accepted") reason = "authenticated_peer_answer_ack_not_current";
            LocalOperationalLog.Info("NKN.Tuna",
                $"event=runtime_unlock_sender_dialer_continuation; decision_status=Held; install_generation={receipt.InstallGeneration}; authority_effect=none; decision_reason={SanitizeLogToken(reason)}");
        }
        return true;
    }

    private void RetainListenerAnswerForPeerProbe(
        RuntimeUnlockOfferAuthorityBoundary? boundary, long barrierGeneration,
        string answerMessageId, string source, byte[] authenticatedAnswer, NknAccelerationLaneKind lanes)
    {
        if (boundary is not { Mode: RuntimeUnlockOfferAuthorityMode.InitialActivation,
                Direction: FileTransferDirection.Inbound } ||
            boundary.InitialActivationExpectedScope.SourcePublicationAuthority is not
                ({ RouteToken: FileTransferRouteResolver.RegularNknV4FastToken, ProtocolVersion: 4 } or
                 { RouteToken: FileTransferRouteResolver.PostTunaFallbackV6Token, ProtocolVersion: 6 }) ||
            string.IsNullOrWhiteSpace(boundary.TunaPathLeaseListenerRunId) ||
            !ResponderAcceptanceMatchesPhysicalRole(RuntimeUnlockResponderAnswerAcceptanceSource.AuthenticatedAnswer,
                boundary.TunaPathLeaseListenerRunId) ||
            !ValidateRuntimeUnlockAnswerHandoffBarrier(barrierGeneration, boundary))
            return;

        RuntimeUnlockResponderAnswerProbeConvergenceHold evidence;
        lock (accelerationGate)
        {
            if (runtimeUnlockRetiredAnswerBarrier is not
                    { Phase: RuntimeUnlockRetiredAnswerBarrierPhase.AwaitingHandoffDisposition } barrier ||
                barrier.BarrierGeneration != barrierGeneration || barrier.Boundary != boundary ||
                !TryValidateRuntimeUnlockRetiredAnswerBarrierUnsafe(barrier, out _) ||
                runtimeUnlockTransactionState is not { IsTerminal: false, AnswerReceived: true } current ||
                current.TransactionGeneration != boundary.TransactionGeneration ||
                current.OfferGeneration != boundary.OfferGeneration ||
                current.TunaPathLeaseGeneration != boundary.TunaPathLeaseGeneration ||
                !current.TunaPathLeaseCurrent ||
                current.TunaPathLeaseListenerRunId != boundary.TunaPathLeaseListenerRunId ||
                current.PathProbeState != RuntimeUnlockPathProbeState.None ||
                (runtimeUnlockResponderAnswerProbeConvergence is { } retained &&
                 (retained.State != RuntimeUnlockResponderAnswerProbeConvergenceState.Invalidated ||
                  retained.SessionId == boundary.SessionId &&
                  retained.TransactionGeneration == boundary.TransactionGeneration &&
                  retained.OfferGeneration == boundary.OfferGeneration)))
                return;

            // This side received the authenticated Answer; it did not receive
            // a semantic AnswerAck. Preserve that distinction through Core's
            // observation while reusing the exact message/probe custody tuple.
            evidence = new(
                RuntimeUnlockResponderAnswerProbeConvergenceState.AwaitingPeerProbe,
                boundary.SessionId, boundary.TransferId!, boundary.TransactionGeneration, boundary.OfferGeneration,
                boundary.OfferGeneration, boundary.Nonce, boundary.Mode, boundary.PayerDecisionId,
                lanes & NknAccelerationLaneKind.File, answerMessageId, boundary.EnvelopeExpiresAtUnixMs,
                boundary.TunaPathLeaseGeneration, boundary.TunaPathLeaseListenerRunId,
                answerMessageId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Trim()))).ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(authenticatedAnswer)).ToLowerInvariant(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                AcceptanceSource = RuntimeUnlockResponderAnswerAcceptanceSource.AuthenticatedAnswer,
                PhysicalListener = (accelerationLane as INknTunaAccelerationSession)?.CaptureCurrentListenerAdoptionSnapshot(),
            };
            runtimeUnlockResponderAnswerProbeConvergence = evidence;
        }
        LocalOperationalLog.Info("NKN.Tuna",
            $"event=runtime_unlock_listener_answer_peer_probe_evidence_retained; session_id={SanitizeLogToken(evidence.SessionId)}; transfer_id={SanitizeLogToken(evidence.TransferId)}; transaction_generation={evidence.TransactionGeneration}; offer_generation={evidence.OfferGeneration}; payer_decision_id={evidence.PayerDecisionId}; tuna_path_lease_generation={evidence.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={SanitizeLogToken(evidence.TunaPathLeaseListenerRunId)}; answer_acceptance_source={evidence.AcceptanceSource}; answer_message_id={SanitizeLogToken(evidence.AnswerMessageId)}; answer_source_fingerprint={evidence.AckSourceFingerprint}; answer_envelope_fingerprint={evidence.AckFrameFingerprint}; answer_deadline_utc_ms={evidence.AnswerDeadlineUtcMs}; authority_effect=none");
    }
}
