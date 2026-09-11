using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal sealed record FileTransferBootstrapPhysicalCopySnapshot(
    string Lane, string FrameType, long TransportEpoch, string FrameFingerprint,
    FileTransferBootstrapCopyOutcome Outcome);

internal sealed record FileTransferBootstrapCorrelationOperationSnapshot(
    int Version, bool AgreementEnabled, string AgreementFingerprint,
    string SenderIssuerFingerprint, string ReceiverIssuerFingerprint,
    string FirstPhase, string Phase, string LocalIdFingerprint, string PeerIdFingerprint,
    string CompletedPeerIdFingerprint, string RetainedPeerFrameIdentityFingerprint, string PredecessorIdentity,
    string EpochFrameFingerprint, string ProofFrameFingerprint, string ProofFrameIdentityFingerprint, long TargetTransportEpoch,
    bool Held, bool SubmissionSealed, bool Completed, bool Invalidated,
    ImmutableArray<FileTransferBootstrapPhysicalCopySnapshot> DeliveryCopies,
    ImmutableArray<FileTransferBootstrapPhysicalCopySnapshot> SettlementCopies,
    ImmutableArray<FileTransferBootstrapPhysicalCopySnapshot> FailureCopies);

internal static partial class FileTransferCoordinator
{
    private static FileTransferBootstrapAgreementDecision FinalizeBootstrapAgreementSnapshot(
        FileTransferCoordinatorState before, FileTransferBootstrapAgreementDecision decision, string kind, object attempt)
    {
        if (string.IsNullOrWhiteSpace(before.PersistentState.CoordinatorInstanceId)) { return decision; }
        var snapshot = CreateBootstrapCorrelationSnapshot(before, decision.State, decision.Accepted, kind, decision.Reason, attempt);
        return decision with { State = snapshot.State, SnapshotEnvelope = snapshot.Envelope };
    }

    private static FileTransferBootstrapExchangeDecision FinalizeBootstrapExchangeSnapshot(
        FileTransferCoordinatorState before, FileTransferBootstrapExchangeDecision decision, string kind, object attempt)
    {
        if (string.IsNullOrWhiteSpace(before.PersistentState.CoordinatorInstanceId)) { return decision; }
        var snapshot = CreateBootstrapCorrelationSnapshot(before, decision.State, decision.Accepted, kind, decision.Reason, attempt);
        return decision with { State = snapshot.State, SnapshotEnvelope = snapshot.Envelope };
    }

    private static FileTransferCoordinatorSnapshotResult CreateBootstrapCorrelationSnapshot(
        FileTransferCoordinatorState before, FileTransferCoordinatorState after, bool accepted, string kind, string reason, object attempt)
    {
        var attempted = ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot>.Empty;
        if (!accepted)
        {
            var fingerprint = CalculateSnapshotFingerprint(attempt);
            var descriptor = CreateBootstrapCorrelationDescriptor(before, "fallback_bootstrap_attempt", kind,
                "attempt:" + fingerprint, 1, "Rejected", fingerprint, CreateStateBoundarySnapshot(before), null, []);
            attempted = [CreateOperationTransitionSnapshot(descriptor, "None", "Rejected", false)];
        }
        return CreateTransitionSnapshot(before, after, before.PersistentState.CoordinatorInstanceId!,
            before.PersistentState.SnapshotStateGeneration, kind,
            !accepted ? "Rejected" : ReferenceEquals(before, after) ? "Idempotent" : "Accepted", reason, attempted);
    }

    private static void AddBootstrapCorrelationDescriptors(FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        var substate = state.PersistentState.FileTransferRouteTransition;
        if (substate.BootstrapAgreement is not { } agreement) { return; }
        var agreementId = "fbcagreement1:" + CalculateSnapshotFingerprint(new { agreement.CoordinatorInstanceId, agreement.Offer });
        var agreementEvidence = CreateBootstrapCorrelationEvidence(agreement, null, null);
        builder.Add(CreateBootstrapCorrelationDescriptor(state, "fallback_bootstrap_agreement", "FallbackBootstrapAgreement",
            agreementId, 1, agreement.IsEnabled ? "Enabled" : agreement.Accept is null ? "Offered" : "Selected",
            CalculateSnapshotFingerprint(agreement), agreement.SourceBoundary, agreementEvidence, []));
        if (substate.BootstrapExchange is not { } exchange) { return; }
        var command = exchange.Bootstrap;
        var boundary = new FileTransferCoordinatorStateBoundarySnapshot(command.SourceRuntimeOwner.RouteToken, 4,
            command.SourceLiveRouteEpoch, command.SourceRoutePublication.PublicationGeneration, command.SourceRouteCommit.CommitGeneration,
            command.SourceRuntimeOwner.RuntimeGeneration, command.SourceTerminalGeneration, command.SourceLegGeneration,
            command.SourceTransportEpoch, command.SourceBridgeRecoveryGeneration, exchange.LegCheckpointGeneration,
            exchange.CheckpointRequestId ?? "(none)", exchange.DataSessionIdentity, command.SourceDataSessionGeneration);
        builder.Add(CreateBootstrapCorrelationDescriptor(state, "fallback_bootstrap_exchange", "FallbackBootstrapExchange",
            command.AuthorityId.Value, command.BootstrapGeneration,
            exchange.Invalidated ? "Invalidated" : exchange.Completed ? "Completed" : "Pending",
            CalculateSnapshotFingerprint(exchange), boundary, CreateBootstrapCorrelationEvidence(agreement, exchange, null), []));
        AddPhase(exchange.First);
        if (exchange.Final is { } final) { AddPhase(final); }

        void AddPhase(FileTransferBootstrapPhaseState phase)
        {
            var receipts = new[] { phase.Delivery, phase.Settlement, phase.Failure }.OfType<FileTransferBootstrapPhaseReceipt>()
                .Select(receipt => CreateRouteTransitionReceiptSnapshot(
                    "bootstrap_phase_" + receipt.Status.ToString().ToLowerInvariant(), phase.Command.Identity,
                    command.BootstrapGeneration, phase.Command, phase.Command.Identity + ":" + receipt.Status,
                    command.BootstrapGeneration, receipt.Status.ToString(), receipt.Reason, receipt, true)).ToImmutableArray();
            builder.Add(CreateBootstrapCorrelationDescriptor(state, "fallback_bootstrap_phase", "FallbackBootstrapPhase",
                phase.Command.Identity, command.BootstrapGeneration,
                phase.Failure is not null ? "Failed" : phase.IsReady ? "Ready" : phase.Held ? "Held" : phase.Delivery is not null ? "Delivered" : phase.Settlement is not null ? "Settled" : "Issued",
                CalculateSnapshotFingerprint(phase.Command), boundary, CreateBootstrapCorrelationEvidence(agreement, exchange, phase), receipts));
        }
    }

    private static FileTransferBootstrapCorrelationOperationSnapshot CreateBootstrapCorrelationEvidence(
        FileTransferBootstrapAgreement agreement, FileTransferBootstrapExchange? exchange, FileTransferBootstrapPhaseState? phase)
        => new(agreement.Accept?.FallbackBootstrapCorrelationVersion ?? 0, agreement.IsEnabled, CalculateSnapshotFingerprint(agreement),
            CalculateSnapshotFingerprint(agreement.Offer.FallbackBootstrapIssuerId), CalculateSnapshotFingerprint(agreement.Accept?.FallbackBootstrapIssuerId),
            exchange?.First.Command.Phase.ToString() ?? "(none)", phase?.Command.Phase.ToString() ?? "(none)",
            CalculateSnapshotFingerprint(exchange?.LocalId), CalculateSnapshotFingerprint(exchange?.PeerCandidateId),
            CalculateSnapshotFingerprint(exchange?.LastCompletedPeerId),
            CalculateSnapshotFingerprint(exchange?.RetainedPeerFrame is { } retained ? PostTunaFallbackPeerProofFrameIdentity.Create(retained) : null),
            phase?.Command.PredecessorIdentity ?? "(none)",
            phase?.Command.EpochFrameIdentity?.Sha256 ?? "(none)", phase?.Command.FrameIdentity.Sha256 ?? "(none)",
            CalculateSnapshotFingerprint(phase?.Command.FrameIdentity),
            exchange?.Bootstrap.TargetTransportEpoch ?? 0,
            phase?.Held ?? false, phase?.Delivery?.SubmissionSealed == true || phase?.Settlement?.SubmissionSealed == true || phase?.Failure?.SubmissionSealed == true,
            exchange?.Completed ?? false, exchange?.Invalidated ?? false,
            CreateBootstrapPhysicalCopySnapshots(phase?.Delivery),
            CreateBootstrapPhysicalCopySnapshots(phase?.Settlement),
            CreateBootstrapPhysicalCopySnapshots(phase?.Failure));

    private static ImmutableArray<FileTransferBootstrapPhysicalCopySnapshot> CreateBootstrapPhysicalCopySnapshots(
        FileTransferBootstrapPhaseReceipt? receipt)
        => receipt?.Copies.Select(copy => new FileTransferBootstrapPhysicalCopySnapshot(copy.Lane,
            copy.FrameIdentity.FrameType, copy.FrameIdentity.TransportEpoch, copy.FrameIdentity.Sha256,
            copy.Outcome)).ToImmutableArray() ?? [];

    private static CoordinatorOperationDescriptor CreateBootstrapCorrelationDescriptor(
        FileTransferCoordinatorState state, string role, string kind, string authority, long generation, string lifecycle,
        string fingerprint, FileTransferCoordinatorStateBoundarySnapshot boundary,
        FileTransferBootstrapCorrelationOperationSnapshot? evidence, ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot> receipts)
    {
        var receipt = receipts.LastOrDefault();
        var exchange = role == "fallback_bootstrap_agreement" ? null : state.PersistentState.FileTransferRouteTransition.BootstrapExchange;
        return new("route_transition", kind, authority, evidence?.PredecessorIdentity ?? "(none)", generation, lifecycle,
            authority, generation, fingerprint, receipt?.ReceiptIdentity ?? "(none)", receipt?.ReceiptGeneration ?? 0,
            receipt?.ReceiptStatus ?? "(none)", receipt?.ReceiptFingerprint ?? "(none)", receipt?.ReceiptValidated ?? false,
            role, CreateCoordinatorOperationKey("route_transition", role, authority, generation, kind),
            RouteTransition: new(role, "TunaV4ToPostTunaFallbackV6", boundary, boundary,
                "Tuna", "fallback-bootstrap-correlation-v1", "fallback-bootstrap-correlation-v1", fingerprint,
                CalculateSnapshotFingerprint(exchange?.Bootstrap.Observation.DurableCheckpoint), "post_tuna_fallback_v6", 6,
                "RegularNkn", "TunaToNormalFallback", lifecycle,
                exchange?.RetainedPeerFrame?.Type ?? "(none)",
                exchange?.RetainedPeerFrame is { } retained ? PostTunaFallbackPeerProofFrameIdentity.Create(retained).Sha256 : "(none)",
                receipts, evidence));
    }
}
