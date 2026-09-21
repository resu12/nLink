using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal enum FileTransferBootstrapPhase { Solicit, Announce, Response, Confirm }
internal enum FileTransferBootstrapCopyOutcome { Pending, Succeeded, Failed, Canceled }
internal enum FileTransferBootstrapPhaseReceiptStatus { Held, Delivered, Settled, Failed }

internal sealed record FileTransferBootstrapPhaseCommand(
    FileTransferFallbackV6ProofBootstrapCommand Bootstrap,
    FileTransferBootstrapPhase Phase,
    string Identity,
    string DataSessionIdentity,
    string? CheckpointRequestId,
    int LegCheckpointGeneration,
    FileTransferFallbackBootstrapFrameV6 Frame,
    PostTunaFallbackPeerProofFrameIdentity FrameIdentity,
    FileTransferTransportEpochFrameV6? EpochFrame,
    PostTunaFallbackPeerProofFrameIdentity? EpochFrameIdentity,
    string? PredecessorIdentity);

internal sealed record FileTransferBootstrapPhysicalCopy(
    string Lane, PostTunaFallbackPeerProofFrameIdentity FrameIdentity, FileTransferBootstrapCopyOutcome Outcome);

internal sealed record FileTransferBootstrapPhaseReceipt(
    FileTransferBootstrapPhaseCommand Command,
    FileTransferBootstrapPhaseReceiptStatus Status,
    bool SubmissionSealed,
    ImmutableArray<FileTransferBootstrapPhysicalCopy> Copies,
    string Reason);

internal sealed record FileTransferBootstrapPhaseExecution(
    Task<FileTransferBootstrapPhaseReceipt> Delivery, Task<FileTransferBootstrapPhaseReceipt> Settlement)
{
    internal static FileTransferBootstrapPhaseExecution Rejected(FileTransferBootstrapPhaseCommand command, string reason)
    {
        var receipt = Task.FromResult(new FileTransferBootstrapPhaseReceipt(command,
            FileTransferBootstrapPhaseReceiptStatus.Failed, true, [], reason));
        return new(receipt, receipt);
    }
}

internal sealed record FileTransferBootstrapPhaseState(
    FileTransferBootstrapPhaseCommand Command,
    FileTransferBootstrapPhaseReceipt? Delivery = null,
    FileTransferBootstrapPhaseReceipt? Settlement = null,
    bool Held = false,
    FileTransferBootstrapPhaseReceipt? Failure = null)
{
    internal bool IsReady => Delivery is not null && Settlement is not null;
}

internal sealed record FileTransferBootstrapExchange(
    FileTransferBootstrapAgreement Agreement,
    FileTransferFallbackV6ProofBootstrapCommand Bootstrap,
    string LocalId,
    string DataSessionIdentity,
    string? CheckpointRequestId,
    int LegCheckpointGeneration,
    FileTransferBootstrapPhaseState First,
    FileTransferBootstrapPhaseState? Final = null,
    string? PeerCandidateId = null,
    FileTransferFallbackBootstrapFrameV6? RetainedPeerFrame = null,
    string? LastCompletedPeerId = null,
    bool Completed = false,
    bool Invalidated = false,
    FileTransferBootstrapSourceHoldCommand? SourceHoldCommand = null);

internal sealed record FileTransferBootstrapExchangeDecision(
    FileTransferCoordinatorState State,
    bool Accepted,
    string Reason,
    FileTransferBootstrapPhaseCommand? Command = null,
    FileTransferCoordinatorTransitionEnvelope? SnapshotEnvelope = null);

internal static partial class FileTransferCoordinator
{
    internal static FileTransferBootstrapExchangeDecision StartFallbackBootstrapExchange(
        FileTransferCoordinatorState state,
        FileTransferFallbackV6ProofBootstrapObservation observation,
        FileTransferFallbackBootstrapStateFrameV6? localState,
        FileTransferFallbackBootstrapRequestFrameV6? solicit = null)
        => FinalizeBootstrapExchangeSnapshot(state, StartFallbackBootstrapExchangeCore(state, observation, localState, solicit),
            nameof(StartFallbackBootstrapExchange), new { observation, localState, solicit });

    private static FileTransferBootstrapExchangeDecision StartFallbackBootstrapExchangeCore(
        FileTransferCoordinatorState state,
        FileTransferFallbackV6ProofBootstrapObservation observation,
        FileTransferFallbackBootstrapStateFrameV6? localState,
        FileTransferFallbackBootstrapRequestFrameV6? solicit)
    {
        var transition = state.PersistentState.FileTransferRouteTransition;
        var agreement = transition.BootstrapAgreement;
        var prior = transition.BootstrapExchange;
        if (!IsFallbackBootstrapInitiationCurrent(state, observation))
        {
            return new(state, false, "bootstrap_initiation_source_not_current");
        }
        if (agreement is not { IsEnabled: true } || !IsBootstrapAgreementCurrent(state, agreement) ||
            string.IsNullOrWhiteSpace(state.CurrentDataSessionIdentity))
        {
            return new(state, false, "bootstrap_agreement_not_enabled");
        }
        if (prior is not null && IsFallbackBootstrapExchangeCurrent(state, prior))
        {
            return new(state, true, "bootstrap_exchange_already_issued");
        }
        if (transition.BootstrapSourceHold is not { Receipt: { } holdReceipt } hold ||
            !IsFallbackBootstrapSourceHoldCurrent(state, hold.Command) || holdReceipt.DurableCheckpoint != observation.DurableCheckpoint ||
            observation with { DurableCheckpoint = hold.Command.Initiation.DurableCheckpoint } != hold.Command.Initiation)
        {
            return new(state, false, "bootstrap_source_hold_receipt_required");
        }
        if (prior is null
            ? !IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(state, out _) && !CanStartInitialTunaFallbackExchange(state)
            : !prior.Completed || prior.Invalidated ||
              transition.LastFallbackV6ProofBootstrapReceipt?.Command != prior.Bootstrap ||
              !IsCommittedPostFallbackV6ToTunaV4ReactivationCurrent(state, prior.Bootstrap, out _))
        {
            return new(state, false, "bootstrap_exchange_lineage_invalid");
        }
        if (solicit is not null && (state.Direction != FileTransferDirection.Inbound ||
            !FileTransferBootstrapCorrelation.IsValidFrame(solicit) || solicit.ReceiverChallengeId is not null ||
            solicit.SessionId != state.SessionId || solicit.TransferId != state.TransferId ||
            !IsFreshBootstrapPeerId(agreement, solicit.SenderRequestId, prior?.LastCompletedPeerId)))
        {
            return new(state, false, "bootstrap_solicit_invalid");
        }
        var issued = IssueFileTransferFallbackV6ProofBootstrapCore(state, observation);
        if (issued.Status != FileTransferFallbackV6ProofBootstrapDecisionStatus.Issued || issued.Command is not { } bootstrap)
        {
            return new(state, false, issued.Reason);
        }
        var localId = CreateFallbackBootstrapCommandId(CreateLocalFallbackBootstrapIssuer(state), bootstrap,
            state.CurrentDataSessionIdentity!, state.CurrentLeg?.CheckpointRequestId, state.CurrentLeg!.CheckpointGeneration);
        FileTransferFallbackBootstrapFrameV6 frame;
        FileTransferBootstrapPhase phase;
        if (state.Direction == FileTransferDirection.Outbound)
        {
            phase = FileTransferBootstrapPhase.Solicit;
            frame = new FileTransferFallbackBootstrapRequestFrameV6
            {
                SessionId = state.SessionId, TransferId = state.TransferId,
                TransportEpoch = bootstrap.TargetTransportEpoch, SenderRequestId = localId,
            };
        }
        else
        {
            if (localState is null || localState.SessionId != state.SessionId || localState.TransferId != state.TransferId ||
                localState.ContiguousCommittedChunkIndex != observation.DurableCheckpoint.ContiguousCommittedChunkIndex ||
                localState.DurableReceivedHighestChunkIndex != observation.DurableCheckpoint.DurableHighestChunkIndex ||
                solicit is not null && solicit.TransportEpoch != bootstrap.TargetTransportEpoch)
            {
                return new(state, false, "bootstrap_local_state_invalid");
            }
            phase = solicit is null ? FileTransferBootstrapPhase.Announce : FileTransferBootstrapPhase.Response;
            frame = localState with
            {
                TransportEpoch = bootstrap.TargetTransportEpoch, SenderRequestId = solicit?.SenderRequestId,
                ReceiverChallengeId = localId,
            };
        }
        if (!FileTransferBootstrapCorrelation.IsValidFrame(frame))
        {
            return new(state, false, "bootstrap_local_frame_invalid");
        }
        var command = CreateBootstrapPhaseCommand(bootstrap, localId, phase, frame,
            state.CurrentDataSessionIdentity!, state.CurrentLeg?.CheckpointRequestId, state.CurrentLeg!.CheckpointGeneration, null);
        var exchange = new FileTransferBootstrapExchange(agreement, bootstrap, localId, state.CurrentDataSessionIdentity!,
            state.CurrentLeg?.CheckpointRequestId, state.CurrentLeg!.CheckpointGeneration, new(command), PeerCandidateId: solicit?.SenderRequestId,
            LastCompletedPeerId: prior?.LastCompletedPeerId, SourceHoldCommand: hold.Command);
        return SetBootstrapExchange(issued.State, exchange, "bootstrap_exchange_issued", command);
    }

    internal static FileTransferBootstrapExchangeDecision ObserveFallbackBootstrapPeerFrame(
        FileTransferCoordinatorState state, FileTransferFallbackBootstrapFrameV6 frame,
        string dataSessionIdentity, long dataSessionGeneration)
        => FinalizeBootstrapExchangeSnapshot(state, ObserveFallbackBootstrapPeerFrameCore(state, frame, dataSessionIdentity, dataSessionGeneration),
            nameof(ObserveFallbackBootstrapPeerFrame), new { frame, dataSessionIdentity, dataSessionGeneration });

    private static FileTransferBootstrapExchangeDecision ObserveFallbackBootstrapPeerFrameCore(
        FileTransferCoordinatorState state, FileTransferFallbackBootstrapFrameV6 frame,
        string dataSessionIdentity, long dataSessionGeneration)
    {
        var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange;
        if (exchange is null || !IsFallbackBootstrapExchangeCurrent(state, exchange) || exchange.Completed ||
            dataSessionIdentity != exchange.DataSessionIdentity || dataSessionGeneration != exchange.Bootstrap.SourceDataSessionGeneration ||
            !FileTransferBootstrapCorrelation.IsValidFrame(frame) || frame.SessionId != state.SessionId || frame.TransferId != state.TransferId ||
            frame.TransportEpoch != exchange.Bootstrap.TargetTransportEpoch)
        {
            return new(state, false, "bootstrap_peer_scope_invalid");
        }
        if (state.Direction == FileTransferDirection.Outbound)
        {
            if (frame is not FileTransferFallbackBootstrapStateFrameV6 || frame.SenderRequestId != exchange.LocalId ||
                !IsFreshBootstrapPeerId(exchange.Agreement, frame.ReceiverChallengeId, exchange.LastCompletedPeerId) ||
                exchange.PeerCandidateId is not null && exchange.PeerCandidateId != frame.ReceiverChallengeId)
            {
                return new(state, false, "bootstrap_response_invalid");
            }
            if (exchange.RetainedPeerFrame is not null && exchange.RetainedPeerFrame != frame)
            {
                return new(state, false, "bootstrap_response_conflict");
            }
            exchange = exchange with { PeerCandidateId = frame.ReceiverChallengeId, RetainedPeerFrame = frame };
        }
        else
        {
            if (frame is not FileTransferFallbackBootstrapRequestFrameV6 ||
                !IsFreshBootstrapPeerId(exchange.Agreement, frame.SenderRequestId, exchange.LastCompletedPeerId) ||
                exchange.PeerCandidateId is not null && exchange.PeerCandidateId != frame.SenderRequestId)
            {
                return new(state, false, "bootstrap_request_invalid");
            }
            if (frame.ReceiverChallengeId is null)
            {
                exchange = exchange with { PeerCandidateId = frame.SenderRequestId };
            }
            else
            {
                var response = exchange.Final?.Command ?? exchange.First.Command;
                if (frame.ReceiverChallengeId != exchange.LocalId || response.Phase != FileTransferBootstrapPhase.Response ||
                    response.Frame.SenderRequestId != frame.SenderRequestId ||
                    exchange.RetainedPeerFrame is not null && exchange.RetainedPeerFrame != frame)
                {
                    return new(state, false, "bootstrap_confirm_invalid");
                }
                exchange = exchange with { RetainedPeerFrame = frame };
            }
        }
        return AdvanceBootstrapExchange(state, exchange, "bootstrap_peer_retained");
    }

    internal static FileTransferBootstrapExchangeDecision ApplyFallbackBootstrapPhaseReceipt(
        FileTransferCoordinatorState state, FileTransferBootstrapPhaseReceipt receipt)
        => FinalizeBootstrapExchangeSnapshot(state, ApplyFallbackBootstrapPhaseReceiptCore(state, receipt),
            nameof(ApplyFallbackBootstrapPhaseReceipt), receipt);

    private static FileTransferBootstrapExchangeDecision ApplyFallbackBootstrapPhaseReceiptCore(
        FileTransferCoordinatorState state, FileTransferBootstrapPhaseReceipt receipt)
    {
        var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange;
        if (exchange is null || !IsFallbackBootstrapExchangeCurrent(state, exchange) || exchange.Completed)
        {
            return new(state, false, "bootstrap_receipt_stale");
        }
        var first = receipt.Command == exchange.First.Command;
        var phase = first ? exchange.First : exchange.Final;
        if (phase is null || phase.Command != receipt.Command || string.IsNullOrWhiteSpace(receipt.Reason) ||
            !IsBootstrapPhaseReceiptValid(receipt, phase))
        {
            return new(state, false, "bootstrap_receipt_invalid");
        }
        switch (receipt.Status)
        {
            case FileTransferBootstrapPhaseReceiptStatus.Held:
                phase = phase with { Held = true };
                break;
            case FileTransferBootstrapPhaseReceiptStatus.Delivered:
                if (phase.Delivery is not null)
                {
                    return new(state, true, "bootstrap_delivery_duplicate");
                }
                phase = phase with { Delivery = receipt, Held = false };
                break;
            case FileTransferBootstrapPhaseReceiptStatus.Settled:
                if (phase.Settlement is not null)
                {
                    return new(state, true, "bootstrap_settlement_duplicate");
                }
                phase = phase with { Settlement = receipt, Held = false };
                break;
            default:
                phase = phase with { Failure = receipt, Held = false };
                exchange = first ? exchange with { First = phase } : exchange with { Final = phase };
                return SetBootstrapExchange(state, exchange with { Invalidated = true }, "bootstrap_phase_failed");
        }
        exchange = first ? exchange with { First = phase } : exchange with { Final = phase };
        if (first && phase.Delivery is not null &&
            state.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt?.Command != exchange.Bootstrap)
        {
            state = state with
            {
                PersistentState = state.PersistentState with
                {
                    FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with
                    {
                        PendingFallbackV6ProofBootstrap = null,
                        LastFallbackV6ProofBootstrapReceipt = new(exchange.Bootstrap,
                            FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered, phase.Command.FrameIdentity,
                            "bootstrap_first_phase_delivered"),
                    },
                },
            };
        }
        return AdvanceBootstrapExchange(state, exchange, "bootstrap_receipt_applied");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static bool IsFallbackBootstrapExchangeCurrent(FileTransferCoordinatorState state, FileTransferBootstrapExchange exchange)
        => HasCurrentFallbackBootstrapExchangeSourceHold(state, exchange) &&
           HasCurrentFallbackBootstrapExchangeScope(state, exchange);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool HasCurrentFallbackBootstrapExchangeScope(FileTransferCoordinatorState state, FileTransferBootstrapExchange exchange)
        => !exchange.Invalidated && IsBootstrapAgreementCurrent(state, exchange.Agreement) &&
           state.PersistentState.FileTransferRouteTransition.BootstrapAgreement == exchange.Agreement &&
           IsFileTransferFallbackV6ProofBootstrapSourceScopeCurrent(state, exchange.Bootstrap) &&
           state.CurrentDataSessionIdentity == exchange.DataSessionIdentity &&
           state.CurrentLeg?.CheckpointGeneration == exchange.LegCheckpointGeneration &&
           state.CurrentLeg?.CheckpointRequestId == exchange.CheckpointRequestId;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool HasCurrentFallbackBootstrapExchangeSourceHold(FileTransferCoordinatorState state, FileTransferBootstrapExchange exchange)
        => state.PersistentState.FileTransferRouteTransition.BootstrapExchange == exchange &&
           state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold is { Invalidated: false, Receipt: { } holdReceipt } hold &&
           hold.Command == exchange.SourceHoldCommand && holdReceipt.Capture.Command == hold.Command &&
           holdReceipt.DurableCheckpoint == exchange.Bootstrap.Observation.DurableCheckpoint;

    internal static bool IsFallbackBootstrapPhaseCommandCurrent(FileTransferCoordinatorState state, FileTransferBootstrapPhaseCommand command)
    {
        var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange;
        if (exchange is null || exchange.Completed || !IsFallbackBootstrapExchangeCurrent(state, exchange))
        {
            return false;
        }
        var phase = exchange.Final ?? exchange.First;
        return phase.Command == command && !phase.IsReady;
    }

    internal static FileTransferBootstrapPhaseCommand? GetCurrentFallbackBootstrapPhaseCommand(FileTransferCoordinatorState state,
        bool heldOnly = false)
    {
        var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange;
        var phase = exchange?.Final ?? exchange?.First;
        return phase is not null && (!heldOnly || phase.Held) && IsFallbackBootstrapPhaseCommandCurrent(state, phase.Command)
            ? phase.Command : null;
    }

    internal static bool CanStartFallbackBootstrapExchange(FileTransferCoordinatorState state)
    {
        var (agreement, prior, receipt) = CaptureFallbackBootstrapExchangeContext(state);
        if (agreement is not { IsEnabled: true } || !IsBootstrapAgreementCurrent(state, agreement) ||
            string.IsNullOrWhiteSpace(state.CurrentDataSessionIdentity)) { return false; }
        if (prior is null)
        {
            return IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(state, out _) || CanStartInitialTunaFallbackExchange(state);
        }
        return IsFallbackBootstrapExchangeCurrent(state, prior) || prior.Completed && !prior.Invalidated &&
            receipt?.Command == prior.Bootstrap &&
            IsCommittedPostFallbackV6ToTunaV4ReactivationCurrent(state, prior.Bootstrap, out _);
    }

    internal static bool IsFallbackBootstrapWakeFrameCurrent(FileTransferCoordinatorState state, FileTransferFallbackBootstrapFrameV6 frame)
    {
        if (!CanStartFallbackBootstrapExchange(state) || !FileTransferBootstrapCorrelation.IsValidFrame(frame) ||
            frame.SessionId != state.SessionId || frame.TransferId != state.TransferId ||
            frame.TransportEpoch != Math.Max(1, state.CurrentLeg!.TransportEpochId + 1)) { return false; }
        return IsFallbackBootstrapWakePeerIdCurrent(state, frame);
    }

    private static bool IsFallbackBootstrapWakePeerIdCurrent(FileTransferCoordinatorState state, FileTransferFallbackBootstrapFrameV6 frame)
    {
        var substate = state.PersistentState.FileTransferRouteTransition;
        var peerId = state.Direction == FileTransferDirection.Outbound ? frame.ReceiverChallengeId : frame.SenderRequestId;
        return IsFreshBootstrapPeerId(substate.BootstrapAgreement!, peerId, substate.BootstrapExchange?.LastCompletedPeerId) &&
            (state.Direction == FileTransferDirection.Outbound
                ? frame is FileTransferFallbackBootstrapStateFrameV6 && frame.SenderRequestId is null
                : frame is FileTransferFallbackBootstrapRequestFrameV6 && frame.ReceiverChallengeId is null &&
                  (substate.BootstrapExchange is not { } exchange || !IsFallbackBootstrapExchangeCurrent(state, exchange) ||
                   exchange.PeerCandidateId is not { } candidate || candidate == peerId));
    }

    internal static bool CanRetainFallbackBootstrapPeerFrame(FileTransferCoordinatorState state, FileTransferFallbackBootstrapFrameV6 frame)
        => ObserveFallbackBootstrapPeerFrameCore(state, frame, state.CurrentDataSessionIdentity ?? string.Empty,
            state.CurrentDataSessionGeneration).Accepted || IsFallbackBootstrapWakeFrameCurrent(state, frame);

    private static bool IsFallbackBootstrapProofCurrent(FileTransferCoordinatorState state,
        FileTransferFallbackTransitionProofBoundary proof, FileTransferRouteTransitionDurableCheckpoint checkpoint)
    {
        var (_, exchange, receipt) = CaptureFallbackBootstrapExchangeContext(state);
        return exchange is not null && IsFallbackBootstrapExchangeCurrent(state, exchange) &&
            IsFallbackBootstrapProofBoundToExchange(exchange, receipt, proof, checkpoint);
    }

    // Copy persistent value state outside the nested source-validation call chain.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferBootstrapAgreement? Agreement, FileTransferBootstrapExchange? Exchange, FileTransferFallbackV6ProofBootstrapReceipt? Receipt)
        CaptureFallbackBootstrapExchangeContext(FileTransferCoordinatorState state)
    {
        var transition = state.PersistentState.FileTransferRouteTransition;
        return (transition.BootstrapAgreement, transition.BootstrapExchange, transition.LastFallbackV6ProofBootstrapReceipt);
    }

    private static bool IsFallbackBootstrapProofBoundToExchange(FileTransferBootstrapExchange? exchange,
        FileTransferFallbackV6ProofBootstrapReceipt? receipt,
        FileTransferFallbackTransitionProofBoundary proof, in FileTransferRouteTransitionDurableCheckpoint checkpoint)
    {
        if (exchange is not { Completed: true, Invalidated: false, RetainedPeerFrame: { } frame } ||
            receipt is not
                { Status: FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered } delivered ||
            delivered.Command != exchange.Bootstrap || delivered.FrameIdentity != exchange.First.Command.FrameIdentity ||
            checkpoint != exchange.Bootstrap.Observation.DurableCheckpoint ||
            proof.SourceRouteCommit != exchange.Bootstrap.SourceRouteCommit ||
            proof.InitialSource != exchange.Bootstrap.InitialSource ||
            proof.TargetTransportEpoch != exchange.Bootstrap.TargetTransportEpoch)
        {
            return false;
        }
        var identity = PostTunaFallbackPeerProofFrameIdentity.Create(frame);
        var observation = exchange.Bootstrap.Observation;
        return proof.RetainedFrameIdentity == identity && proof.PeerProofEvidence == new PostTunaFallbackPeerProofEvidence(
            observation.SessionId, observation.TransferId, observation.Direction, exchange.Bootstrap.BootstrapGeneration,
            exchange.Bootstrap.TargetTransportEpoch, "fallback_v6_proof_bootstrap_peer_frame",
            "coordinator_fallback_v6_proof_bootstrap_peer_frame", identity);
    }

    internal static FileTransferBootstrapExchangeDecision InvalidateFallbackBootstrapExchange(FileTransferCoordinatorState state, string reason)
        => FinalizeBootstrapExchangeSnapshot(state, InvalidateFallbackBootstrapExchangeCore(state, reason),
            nameof(InvalidateFallbackBootstrapExchange), reason);

    private static FileTransferBootstrapExchangeDecision InvalidateFallbackBootstrapExchangeCore(FileTransferCoordinatorState state, string reason)
    {
        var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange;
        return exchange is not null && IsFallbackBootstrapExchangeCurrent(state, exchange)
            ? SetBootstrapExchange(state, exchange with { Invalidated = true }, reason)
            : new(state, false, "bootstrap_invalidation_stale");
    }

    private static bool IsFreshBootstrapPeerId(FileTransferBootstrapAgreement agreement, string? id, string? completedId)
    {
        if (!FileTransferBootstrapCorrelation.TryParseId(id, out var parsed) || parsed.IssuerId !=
            (agreement.Direction == FileTransferDirection.Outbound ? agreement.Accept?.FallbackBootstrapIssuerId : agreement.Offer.FallbackBootstrapIssuerId))
        {
            return false;
        }
        return !FileTransferBootstrapCorrelation.TryParseId(completedId, out var completed) || parsed.Generation > completed.Generation;
    }

    private static FileTransferBootstrapPhaseCommand CreateBootstrapPhaseCommand(
        FileTransferFallbackV6ProofBootstrapCommand bootstrap, string localId, FileTransferBootstrapPhase phase,
        FileTransferFallbackBootstrapFrameV6 frame, string dataSessionIdentity, string? checkpointRequestId,
        int legCheckpointGeneration, string? predecessor)
    {
        var frameIdentity = PostTunaFallbackPeerProofFrameIdentity.Create(frame);
        var epoch = predecessor is null ? new FileTransferTransportEpochFrameV6
        {
            SessionId = frame.SessionId, TransferId = frame.TransferId, TransportEpoch = bootstrap.TargetTransportEpoch,
            RecoveryMode = FileTransferBootstrapCorrelation.RecoveryMode,
        } : null;
        var epochIdentity = epoch is null ? null : new PostTunaFallbackPeerProofFrameIdentity(epoch.Type, epoch.TransportEpoch,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(FileTransferDataFrameCodec.Serialize(epoch))).ToLowerInvariant());
        var identity = "fbcphase1:" + FileTransferBootstrapCorrelation.HashFields(localId, Number((int)phase),
            frameIdentity.Sha256, epochIdentity?.Sha256 ?? "absent", predecessor ?? "absent");
        return new(bootstrap, phase, identity, dataSessionIdentity, checkpointRequestId, legCheckpointGeneration,
            frame, frameIdentity, epoch, epochIdentity, predecessor);
    }

    private static FileTransferBootstrapExchangeDecision AdvanceBootstrapExchange(
        FileTransferCoordinatorState state, FileTransferBootstrapExchange exchange, string reason)
    {
        FileTransferBootstrapPhaseCommand? command = null;
        if (exchange.First.IsReady && exchange.Final is null && exchange.PeerCandidateId is not null)
        {
            FileTransferFallbackBootstrapFrameV6? frame = null;
            var phase = FileTransferBootstrapPhase.Response;
            if (exchange.First.Command.Phase == FileTransferBootstrapPhase.Announce)
            {
                frame = exchange.First.Command.Frame with { SenderRequestId = exchange.PeerCandidateId };
            }
            else if (exchange.First.Command.Phase == FileTransferBootstrapPhase.Solicit && exchange.RetainedPeerFrame is not null)
            {
                phase = FileTransferBootstrapPhase.Confirm;
                frame = exchange.First.Command.Frame with { ReceiverChallengeId = exchange.PeerCandidateId };
            }
            if (frame is not null)
            {
                command = CreateBootstrapPhaseCommand(exchange.Bootstrap, exchange.LocalId, phase, frame,
                    exchange.DataSessionIdentity, exchange.CheckpointRequestId, exchange.LegCheckpointGeneration, exchange.First.Command.Identity);
                exchange = exchange with { Final = new(command) };
            }
        }
        if (exchange.First.IsReady && exchange.RetainedPeerFrame is not null &&
            (exchange.First.Command.Phase == FileTransferBootstrapPhase.Response || exchange.Final is { IsReady: true }))
        {
            exchange = exchange with { Completed = true, LastCompletedPeerId = exchange.PeerCandidateId };
        }
        return SetBootstrapExchange(state, exchange, reason, command);
    }

    private static bool IsBootstrapPhaseReceiptValid(FileTransferBootstrapPhaseReceipt receipt, FileTransferBootstrapPhaseState phase)
    {
        if (receipt.Status == FileTransferBootstrapPhaseReceiptStatus.Held)
        {
            return receipt.Copies.IsDefaultOrEmpty && phase.Delivery is null && phase.Settlement is null;
        }
        if (receipt.Status == FileTransferBootstrapPhaseReceiptStatus.Failed && receipt.SubmissionSealed && receipt.Copies.IsDefaultOrEmpty)
        {
            return phase.Delivery is null && phase.Settlement is null;
        }
        if (!receipt.SubmissionSealed || receipt.Copies.IsDefaultOrEmpty || receipt.Copies.Length > 4 ||
            receipt.Copies.Any(copy => copy.Lane is not ("control" or "bulk") ||
                copy.FrameIdentity != receipt.Command.FrameIdentity && copy.FrameIdentity != receipt.Command.EpochFrameIdentity ||
                !Enum.IsDefined(copy.Outcome)) ||
            receipt.Copies.Select(copy => (copy.Lane, copy.FrameIdentity)).Distinct().Count() != receipt.Copies.Length)
        {
            return false;
        }
        var prior = phase.Delivery ?? phase.Settlement;
        if (prior is not null && (prior.Copies.Length != receipt.Copies.Length || prior.Copies.Any(old =>
                !receipt.Copies.Any(copy => copy.Lane == old.Lane && copy.FrameIdentity == old.FrameIdentity &&
                    (old.Outcome == FileTransferBootstrapCopyOutcome.Pending || old.Outcome == copy.Outcome)))))
        {
            // A late delivery observation may predate a settlement already applied.
            if (receipt.Status != FileTransferBootstrapPhaseReceiptStatus.Delivered || phase.Settlement is null ||
                prior.Copies.Length != receipt.Copies.Length || receipt.Copies.Any(old =>
                    !prior.Copies.Any(copy => copy.Lane == old.Lane && copy.FrameIdentity == old.FrameIdentity &&
                        (old.Outcome == FileTransferBootstrapCopyOutcome.Pending || old.Outcome == copy.Outcome))))
            {
                return false;
            }
        }
        var delivered = receipt.Copies.Any(copy => copy.FrameIdentity == receipt.Command.FrameIdentity && copy.Outcome == FileTransferBootstrapCopyOutcome.Succeeded) &&
            (receipt.Command.EpochFrameIdentity is null || receipt.Copies.Any(copy => copy.FrameIdentity == receipt.Command.EpochFrameIdentity &&
                copy.Outcome == FileTransferBootstrapCopyOutcome.Succeeded));
        return receipt.Status switch
        {
            FileTransferBootstrapPhaseReceiptStatus.Delivered => delivered,
            FileTransferBootstrapPhaseReceiptStatus.Settled => receipt.Copies.All(copy => copy.Outcome != FileTransferBootstrapCopyOutcome.Pending),
            FileTransferBootstrapPhaseReceiptStatus.Failed => !delivered,
            _ => false,
        };
    }

    private static FileTransferBootstrapExchangeDecision SetBootstrapExchange(
        FileTransferCoordinatorState state, FileTransferBootstrapExchange exchange, string reason, FileTransferBootstrapPhaseCommand? command = null)
        => state.PersistentState.FileTransferRouteTransition.BootstrapExchange == exchange
            ? new(state, true, "bootstrap_exchange_duplicate")
            : new(state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with { BootstrapExchange = exchange },
            },
        }, true, reason, command);
}
