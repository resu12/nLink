using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed record FileTransferRouteTransitionExecutionShadow(
        FileTransferRouteTransitionAuthority? Authority,
        IFileTransferDataSession SourceDataSession,
        long SourceDataSessionGeneration,
        FileTransferRouteTransitionPhysicalDrainCommand? DrainCommand,
        IReadOnlyDictionary<long, Task> CapturedPhysicalTasks,
        PostTunaFallbackPeerProofEvidence? PeerProof,
        FileTransferDataFrame? RetainedPeerFrame,
        PostTunaFallbackPeerProofFrameIdentity? RetainedFrameIdentity,
        FileTransferRouteTransitionRetainedFrameApplyReceipt? LastFrameReceipt,
        bool DrainRecheckQueued,
        bool FrameApplyInFlight,
        string Reason)
    {
        public LiveRouteEpoch? StartedLiveRouteEpoch { get; init; }

        public LiveRouteEpoch? RecoveredLiveRouteEpoch { get; init; }

        public bool HasExactPeerBoundary =>
            PeerProof is { IsComplete: true, IsExactFrameBound: true } proof &&
            RetainedPeerFrame is { } frame &&
            RetainedFrameIdentity is { IsComplete: true } identity &&
            proof.FrameIdentity == identity &&
            identity.Matches(frame);

        public static IReadOnlyDictionary<long, Task> SnapshotTasks(
            IEnumerable<KeyValuePair<long, Task>> tasks)
            => new System.Collections.ObjectModel.ReadOnlyDictionary<long, Task>(
                tasks.ToDictionary(static pair => pair.Key, static pair => pair.Value));
    }

    private bool TryRetainOutboundFileTransferRouteTransitionPeerProofLocked(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        PostTunaFallbackPeerProofEvidence evidence,
        string reason,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !ReferenceEquals(context.DataSession, dataSession) ||
            !evidence.IsComplete ||
            !string.Equals(evidence.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.TransferId, context.TransferId, StringComparison.Ordinal) ||
            evidence.Direction != FileTransferDirection.Outbound)
        {
            return false;
        }

        var current = context.FileTransferRouteTransitionExecution;
        if (TryInvalidateOutboundFileTransferRouteTransitionForPeerBoundaryReplacementLocked(
                context,
                current,
                evidence,
                candidateFrameIdentity: null,
                out snapshot))
        {
            return true;
        }

        if (!TryMergeFileTransferRouteTransitionEvidence(
                current,
                dataSession,
                context.DataSessionGeneration,
                evidence,
                frame: null,
                reason,
                out var merged,
                out var rejectionReason))
        {
            LogFileTransferRouteTransitionEvidence(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                evidence,
                current?.RetainedFrameIdentity,
                current?.Authority,
                "Rejected",
                rejectionReason);
            return true;
        }

        context.FileTransferRouteTransitionExecution = merged;
        LogFileTransferRouteTransitionEvidence(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            evidence,
            merged.RetainedFrameIdentity,
            merged.Authority,
            "Observed",
            reason);
        if (merged.HasExactPeerBoundary)
        {
            TryPrepareOutboundFallbackRouteTransitionLocked(
                context,
                merged,
                out snapshot);
        }

        return true;
    }

    private bool TryRetainOutboundFileTransferRouteTransitionPeerFrameLocked(
        OutboundTransferContext context,
        FileTransferDataFrame frame,
        string reason,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            context.DataSession is not { } dataSession ||
            !IsPeerPostTunaFallbackV6RuntimeProofFrame(frame) ||
            !PostTunaFallbackPeerProofFrameIdentity.TryCreate(frame, out var identity) ||
            !string.Equals(frame.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(frame.TransferId, context.TransferId, StringComparison.Ordinal))
        {
            return false;
        }

        var current = context.FileTransferRouteTransitionExecution;
        if (TryInvalidateOutboundFileTransferRouteTransitionForPeerBoundaryReplacementLocked(
                context,
                current,
                candidateEvidence: null,
                identity,
                out snapshot))
        {
            return true;
        }

        if (!TryMergeFileTransferRouteTransitionEvidence(
                current,
                dataSession,
                context.DataSessionGeneration,
                evidence: null,
                frame,
                reason,
                out var merged,
                out var rejectionReason))
        {
            LogFileTransferRouteTransitionEvidence(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                current?.PeerProof,
                identity,
                current?.Authority,
                "Rejected",
                rejectionReason);
            return true;
        }

        context.FileTransferRouteTransitionExecution = merged;
        LogFileTransferRouteTransitionEvidence(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            merged.PeerProof,
            identity,
            merged.Authority,
            "Observed",
            reason);
        if (merged.HasExactPeerBoundary)
        {
            TryPrepareOutboundFallbackRouteTransitionLocked(
                context,
                merged,
                out snapshot);
        }

        return true;
    }

    private bool TryRetainInboundFileTransferRouteTransitionPeerProofLocked(
        InboundTransferContext context,
        IFileTransferDataSession dataSession,
        PostTunaFallbackPeerProofEvidence evidence,
        string reason,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            !ReferenceEquals(context.DataSession, dataSession) ||
            !evidence.IsComplete ||
            !string.Equals(evidence.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.TransferId, context.TransferId, StringComparison.Ordinal) ||
            evidence.Direction != FileTransferDirection.Inbound)
        {
            return false;
        }

        var current = context.FileTransferRouteTransitionExecution;
        if (TryInvalidateInboundFileTransferRouteTransitionForPeerBoundaryReplacementLocked(
                context,
                current,
                evidence,
                candidateFrameIdentity: null,
                out snapshot))
        {
            return true;
        }

        if (!TryMergeFileTransferRouteTransitionEvidence(
                current,
                dataSession,
                context.DataSessionGeneration,
                evidence,
                frame: null,
                reason,
                out var merged,
                out var rejectionReason))
        {
            LogFileTransferRouteTransitionEvidence(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                evidence,
                current?.RetainedFrameIdentity,
                current?.Authority,
                "Rejected",
                rejectionReason);
            return true;
        }

        context.FileTransferRouteTransitionExecution = merged;
        LogFileTransferRouteTransitionEvidence(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            evidence,
            merged.RetainedFrameIdentity,
            merged.Authority,
            "Observed",
            reason);
        if (merged.HasExactPeerBoundary)
        {
            TryPrepareInboundFallbackRouteTransitionLocked(
                context,
                merged,
                out snapshot);
        }

        return true;
    }

    private bool TryRetainInboundFileTransferRouteTransitionPeerFrameLocked(
        InboundTransferContext context,
        FileTransferDataFrame frame,
        string reason,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            context.DataSession is not { } dataSession ||
            !IsPeerPostTunaFallbackV6RuntimeProofFrame(frame) ||
            !PostTunaFallbackPeerProofFrameIdentity.TryCreate(frame, out var identity) ||
            !string.Equals(frame.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(frame.TransferId, context.TransferId, StringComparison.Ordinal))
        {
            return false;
        }

        var current = context.FileTransferRouteTransitionExecution;
        if (TryInvalidateInboundFileTransferRouteTransitionForPeerBoundaryReplacementLocked(
                context,
                current,
                candidateEvidence: null,
                identity,
                out snapshot))
        {
            return true;
        }

        if (!TryMergeFileTransferRouteTransitionEvidence(
                current,
                dataSession,
                context.DataSessionGeneration,
                evidence: null,
                frame,
                reason,
                out var merged,
                out var rejectionReason))
        {
            LogFileTransferRouteTransitionEvidence(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                current?.PeerProof,
                identity,
                current?.Authority,
                "Rejected",
                rejectionReason);
            return true;
        }

        context.FileTransferRouteTransitionExecution = merged;
        LogFileTransferRouteTransitionEvidence(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            merged.PeerProof,
            identity,
            merged.Authority,
            "Observed",
            reason);
        if (merged.HasExactPeerBoundary)
        {
            TryPrepareInboundFallbackRouteTransitionLocked(
                context,
                merged,
                out snapshot);
        }

        return true;
    }

    private static bool TryMergeFileTransferRouteTransitionEvidence(
        FileTransferRouteTransitionExecutionShadow? current,
        IFileTransferDataSession dataSession,
        long dataSessionGeneration,
        PostTunaFallbackPeerProofEvidence? evidence,
        FileTransferDataFrame? frame,
        string reason,
        out FileTransferRouteTransitionExecutionShadow merged,
        out string rejectionReason)
    {
        merged = null!;
        rejectionReason = "accepted";
        PostTunaFallbackPeerProofFrameIdentity? frameIdentity = null;
        if (frame is not null &&
            !PostTunaFallbackPeerProofFrameIdentity.TryCreate(frame, out frameIdentity!))
        {
            rejectionReason = "filetransfer_route_transition_peer_frame_invalid";
            return false;
        }

        if (current is not null &&
            (!ReferenceEquals(current.SourceDataSession, dataSession) ||
             current.SourceDataSessionGeneration != dataSessionGeneration))
        {
            rejectionReason = "filetransfer_route_transition_data_session_replaced";
            return false;
        }

        var retainedEvidence = evidence ?? current?.PeerProof;
        var retainedFrame = frame ?? current?.RetainedPeerFrame;
        var retainedIdentity = frameIdentity ?? current?.RetainedFrameIdentity;
        if (current?.PeerProof is { } existingEvidence &&
            evidence is { } candidateEvidence &&
            existingEvidence != candidateEvidence &&
            !existingEvidence.CanBeReplacedBy(candidateEvidence))
        {
            rejectionReason = "filetransfer_route_transition_peer_proof_replacement_stale";
            return false;
        }

        if (current?.RetainedFrameIdentity is { } existingIdentity &&
            frameIdentity is { } candidateIdentity &&
            existingIdentity != candidateIdentity &&
            candidateIdentity.TransportEpoch <= existingIdentity.TransportEpoch)
        {
            rejectionReason = "filetransfer_route_transition_peer_frame_replacement_stale";
            return false;
        }

        if (retainedEvidence is { } exactEvidence &&
            retainedIdentity is { } exactIdentity &&
            exactEvidence.FrameIdentity != exactIdentity)
        {
            rejectionReason = "filetransfer_route_transition_peer_boundary_mismatch";
            return false;
        }

        merged = current is null
            ? new FileTransferRouteTransitionExecutionShadow(
                Authority: null,
                dataSession,
                dataSessionGeneration,
                DrainCommand: null,
                FileTransferRouteTransitionExecutionShadow.SnapshotTasks([]),
                retainedEvidence,
                retainedFrame,
                retainedIdentity,
                LastFrameReceipt: null,
                DrainRecheckQueued: false,
                FrameApplyInFlight: false,
                NormalizeReason(reason) ?? "filetransfer_route_transition_peer_proof")
            : current with
            {
                PeerProof = retainedEvidence,
                RetainedPeerFrame = retainedFrame,
                RetainedFrameIdentity = retainedIdentity,
                Reason = NormalizeReason(reason) ?? current.Reason,
            };
        return true;
    }

    private bool
        TryInvalidateOutboundFileTransferRouteTransitionForPeerBoundaryReplacementLocked(
            OutboundTransferContext context,
            FileTransferRouteTransitionExecutionShadow? current,
            PostTunaFallbackPeerProofEvidence? candidateEvidence,
            PostTunaFallbackPeerProofFrameIdentity? candidateFrameIdentity,
            out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!TryGetFileTransferRouteTransitionPeerBoundaryReplacement(
                current,
                candidateEvidence,
                candidateFrameIdentity,
                out var authority,
                out var reason))
        {
            return false;
        }

        var invalidated =
            InvalidateOutboundFileTransferRouteTransitionLocked(
                context,
                authority,
                terminalInvalidation: false,
                reason);
        LogFileTransferRouteTransitionEvidence(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            candidateEvidence,
            candidateFrameIdentity,
            authority,
            invalidated ? "Invalidated" : "Rejected",
            invalidated
                ? reason
                : "filetransfer_route_transition_peer_boundary_invalidation_rejected");
        if (invalidated)
        {
            snapshot = CreateSnapshotLocked();
        }

        return true;
    }

    private bool
        TryInvalidateInboundFileTransferRouteTransitionForPeerBoundaryReplacementLocked(
            InboundTransferContext context,
            FileTransferRouteTransitionExecutionShadow? current,
            PostTunaFallbackPeerProofEvidence? candidateEvidence,
            PostTunaFallbackPeerProofFrameIdentity? candidateFrameIdentity,
            out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!TryGetFileTransferRouteTransitionPeerBoundaryReplacement(
                current,
                candidateEvidence,
                candidateFrameIdentity,
                out var authority,
                out var reason))
        {
            return false;
        }

        var invalidated =
            InvalidateInboundFileTransferRouteTransitionLocked(
                context,
                authority,
                terminalInvalidation: false,
                reason);
        LogFileTransferRouteTransitionEvidence(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            candidateEvidence,
            candidateFrameIdentity,
            authority,
            invalidated ? "Invalidated" : "Rejected",
            invalidated
                ? reason
                : "filetransfer_route_transition_peer_boundary_invalidation_rejected");
        if (invalidated)
        {
            snapshot = CreateSnapshotLocked();
        }

        return true;
    }

    private static bool TryGetFileTransferRouteTransitionPeerBoundaryReplacement(
        FileTransferRouteTransitionExecutionShadow? current,
        PostTunaFallbackPeerProofEvidence? candidateEvidence,
        PostTunaFallbackPeerProofFrameIdentity? candidateFrameIdentity,
        out FileTransferRouteTransitionAuthority authority,
        out string reason)
    {
        authority = default;
        reason = "none";
        if (current?.Authority is not { } currentAuthority ||
            currentAuthority.ProofBoundary is not
                FileTransferFallbackTransitionProofBoundary boundary)
        {
            return false;
        }

        if (candidateEvidence is { } evidence &&
            evidence != boundary.PeerProofEvidence &&
            boundary.PeerProofEvidence.CanBeReplacedBy(evidence))
        {
            authority = currentAuthority;
            reason =
                "filetransfer_route_transition_peer_proof_replaced";
            return true;
        }

        if (candidateFrameIdentity is { } frameIdentity &&
            frameIdentity != boundary.RetainedFrameIdentity &&
            frameIdentity.TransportEpoch >=
                boundary.RetainedFrameIdentity.TransportEpoch)
        {
            authority = currentAuthority;
            reason =
                "filetransfer_route_transition_peer_frame_replaced";
            return true;
        }

        return false;
    }

    private bool TryPrepareOutboundFallbackRouteTransitionLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow evidenceShadow,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!evidenceShadow.HasExactPeerBoundary ||
            !ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !ReferenceEquals(context.DataSession, evidenceShadow.SourceDataSession) ||
            context.DataSessionGeneration != evidenceShadow.SourceDataSessionGeneration ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.CurrentTransferLeg is not { } leg)
        {
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        var currentOperation =
            FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(state)
                .CurrentOperation;
        var supersedeUncommittedTuna =
            context.RouteRuntime.UsesRegularNknV4FastRuntime &&
            currentOperation is
            {
                Authority.Kind:
                    FileTransferRouteTransitionKind.RegularV4ToTunaV4,
                State: FileTransferRouteTransitionState.Prepared or
                    FileTransferRouteTransitionState.Draining,
            };
        if (!context.RouteRuntime.UsesFileTunaV4Runtime &&
            !supersedeUncommittedTuna)
        {
            return false;
        }

        var proof = evidenceShadow.PeerProof!;
        var identity = evidenceShadow.RetainedFrameIdentity!;
        var predecessorId = supersedeUncommittedTuna
            ? currentOperation!.Value.Authority.AuthorityId
            : (FileTransferAuthorityId?)null;
        var kind = supersedeUncommittedTuna
            ? FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6
            : FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6;
        var sourceCommit = supersedeUncommittedTuna
            ? null
            : context.RuntimeUnlockRouteCommitToken;
        if (!supersedeUncommittedTuna &&
            sourceCommit is not { IsDefault: false })
        {
            return false;
        }

        var checkpoint = CreateOutboundFileTransferRouteTransitionCheckpoint(
            context,
            owner,
            publication,
            leg);
        var boundary = new FileTransferFallbackTransitionProofBoundary(
            sourceCommit,
            proof,
            identity,
            proof.TransportEpoch,
            Math.Max(1, leg.BridgeRecoveryGeneration + 1),
            predecessorId);
        var request = new FileTransferRouteTransitionPrepareRequest(
            kind,
            owner,
            publication,
            sourceCommit,
            context.RouteSelection.Route,
            context.NegotiatedDataProtocolVersion,
            ResolveFileTransferRouteSourceTransport(context.RouteSelection.Route),
            boundary,
            checkpoint,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            predecessorId,
            NormalizeReason(evidenceShadow.Reason) ??
            "peer_post_tuna_fallback_v6_proof");

        FileTransferRouteTransitionDecision prepared;
        if (supersedeUncommittedTuna)
        {
            prepared = FileTransferCoordinator.StageFileTransferRouteTransitionSupersession(
                state,
                currentOperation!.Value.Authority,
                request);
            LogFileTransferRouteTransitionSupersessionDecision(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                "stage",
                currentOperation.Value.Authority,
                prepared.Authority,
                prepared.Status.ToString(),
                prepared.Reason);
        }
        else
        {
            prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
                state,
                request);
        }

        if (prepared.Status is not (
                FileTransferRouteTransitionDecisionStatus.Prepared or
                FileTransferRouteTransitionDecisionStatus.Idempotent or
                FileTransferRouteTransitionDecisionStatus.SupersessionStaged) ||
            prepared.Authority is not { } authority ||
            prepared.PauseBindCommand is not { } pauseCommand)
        {
            LogFileTransferRouteTransitionDecision(
                "filetransfer_route_transition_prepared",
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                prepared.Authority,
                prepared.Status.ToString(),
                prepared.Reason,
                capturedCount: 0,
                completedCount: 0);
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_prepared",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            prepared.Status.ToString(),
            prepared.Reason,
            capturedCount: 0,
            completedCount: 0);
        context.CoordinatorPersistentState = prepared.State.PersistentState;
        if (!TryBindFileTransferRouteTransitionPause(
                pauseCommand,
                out var pauseReceipt,
                out var pauseRejection))
        {
            InvalidateOutboundFileTransferRouteTransitionLocked(
                context,
                authority,
                terminalInvalidation: false,
                pauseRejection);
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_pause_bound",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            pauseReceipt.Status.ToString(),
            pauseReceipt.Reason,
            capturedCount: 0,
            completedCount: 0);
        FileTransferRouteTransitionDecision draining;
        if (supersedeUncommittedTuna)
        {
            var predecessor = currentOperation!.Value.Authority;
            var committedSupersession =
                FileTransferCoordinator.CommitFileTransferRouteTransitionSupersession(
                    CreateCoordinatorStateLocked(context),
                    pauseReceipt);
            LogFileTransferRouteTransitionSupersessionDecision(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                "commit",
                predecessor,
                committedSupersession.Authority,
                committedSupersession.Status.ToString(),
                committedSupersession.Reason);
            if (committedSupersession.Status is not (
                    FileTransferRouteTransitionDecisionStatus.Prepared or
                    FileTransferRouteTransitionDecisionStatus.Draining or
                    FileTransferRouteTransitionDecisionStatus.Idempotent) ||
                committedSupersession.Authority != authority)
            {
                return false;
            }

            context.CoordinatorPersistentState =
                committedSupersession.State.PersistentState;
            if (!ApplyFileTransferRouteTransitionDispositionWhileLocked(
                    context,
                    committedSupersession.DispositionCommand))
            {
                return false;
            }

            var current =
                FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(
                    CreateCoordinatorStateLocked(context)).CurrentOperation;
            if (current is null ||
                current.Value.Authority != authority)
            {
                return false;
            }

            if (current.Value is
                {
                    State: FileTransferRouteTransitionState.Draining,
                    DrainCommand: { } inheritedDrain,
                })
            {
                draining = new FileTransferRouteTransitionDecision(
                    FileTransferRouteTransitionDecisionStatus.Draining,
                    committedSupersession.Reason,
                    authority,
                    pauseCommand,
                    inheritedDrain,
                    committedSupersession.DispositionCommand)
                {
                    State = CreateCoordinatorStateLocked(context),
                };
            }
            else if (current.Value is
                     {
                         State: FileTransferRouteTransitionState.Prepared,
                         DrainCommand: null,
                     })
            {
                var capturedThrough =
                    context.LastOutboundPhysicalSendLeaseGeneration;
                var capturedIds = context.ActiveOutboundPhysicalSendLeases.Values
                    .Where(lease =>
                        lease.LeaseGeneration <= capturedThrough &&
                        IsOutboundPhysicalSendLeaseForTransitionAuthority(
                            lease,
                            authority))
                    .Select(static lease => lease.LeaseGeneration)
                    .OrderBy(static generation => generation)
                    .ToArray();
                draining =
                    FileTransferCoordinator
                        .BeginFileTransferRouteTransitionPhysicalDrain(
                            CreateCoordinatorStateLocked(context),
                            authority,
                            pauseReceipt,
                            capturedThrough,
                            capturedIds,
                            FileTransferCoordinator
                                .CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                                    capturedIds));
                if (draining.Status is not (
                        FileTransferRouteTransitionDecisionStatus.Draining or
                        FileTransferRouteTransitionDecisionStatus.Idempotent) ||
                    draining.DrainCommand is null)
                {
                    return false;
                }

                context.CoordinatorPersistentState =
                    draining.State.PersistentState;
            }
            else
            {
                return false;
            }
        }
        else
        {
            var capturedThrough = context.LastOutboundPhysicalSendLeaseGeneration;
            var capturedIds = context.ActiveOutboundPhysicalSendLeases.Values
                .Where(lease =>
                    lease.LeaseGeneration <= capturedThrough &&
                    IsOutboundPhysicalSendLeaseForTransitionAuthority(
                        lease,
                        authority))
                .Select(static lease => lease.LeaseGeneration)
                .OrderBy(static generation => generation)
                .ToArray();
            var fingerprint =
                FileTransferCoordinator.CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                    capturedIds);
            draining =
                FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                    CreateCoordinatorStateLocked(context),
                    authority,
                    pauseReceipt,
                    capturedThrough,
                    capturedIds,
                    fingerprint);
            if (draining.Status is not (
                    FileTransferRouteTransitionDecisionStatus.Draining or
                    FileTransferRouteTransitionDecisionStatus.Idempotent) ||
                draining.DrainCommand is null)
            {
                InvalidateOutboundFileTransferRouteTransitionLocked(
                    context,
                    authority,
                    terminalInvalidation: false,
                    draining.Reason);
                return false;
            }

            context.CoordinatorPersistentState = draining.State.PersistentState;
        }

        if (draining.DrainCommand is not { } drainCommand)
        {
            return false;
        }

        var taskSnapshot =
            FileTransferRouteTransitionExecutionShadow.SnapshotTasks(
                drainCommand.CapturedPhysicalSendLeaseIds
                    .Select(id =>
                        context.ActiveOutboundPhysicalSendLeases.TryGetValue(
                            id,
                            out var lease) &&
                        lease.PhysicalCompletionTask is { } task
                            ? new KeyValuePair<long, Task>(id, task)
                            : default)
                    .Where(static pair => pair.Key > 0));
        var execution = evidenceShadow with
        {
            Authority = authority,
            DrainCommand = drainCommand,
            CapturedPhysicalTasks = taskSnapshot,
            DrainRecheckQueued = false,
        };
        context.FileTransferRouteTransitionExecution = execution;
        BindOutboundFileTransferRouteTransitionPauseLocked(context);
        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_draining",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            draining.Status.ToString(),
            draining.Reason,
            drainCommand.CapturedPhysicalSendCount,
            completedCount: 0,
            drainCommand);
        snapshot = CreateSnapshotLocked();
        if (AreOutboundFileTransferRouteTransitionPhysicalTasksCompleteLocked(
                context,
                execution))
        {
            QueueOutboundFileTransferRouteTransitionDrainRecheckLocked(
                context,
                execution,
                "fallback_transition_empty_or_completed_drain");
        }

        return true;
    }

    private bool TryPrepareInboundFallbackRouteTransitionLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow evidenceShadow,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!evidenceShadow.HasExactPeerBoundary ||
            !ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            context.ReceiverSparseChunksPendingWrite.Count != 0 ||
            context.PendingChunks.Count != 0 ||
            !ReferenceEquals(context.DataSession, evidenceShadow.SourceDataSession) ||
            context.DataSessionGeneration != evidenceShadow.SourceDataSessionGeneration ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.CurrentTransferLeg is not { } leg)
        {
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        var currentOperation =
            FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(state)
                .CurrentOperation;
        var supersedeUncommittedTuna =
            context.RouteRuntime.UsesRegularNknV4FastRuntime &&
            currentOperation is
            {
                Authority.Kind:
                    FileTransferRouteTransitionKind.RegularV4ToTunaV4,
                State: FileTransferRouteTransitionState.Prepared or
                    FileTransferRouteTransitionState.Draining,
            };
        if (!context.RouteRuntime.UsesFileTunaV4Runtime &&
            !supersedeUncommittedTuna)
        {
            return false;
        }

        var proof = evidenceShadow.PeerProof!;
        var identity = evidenceShadow.RetainedFrameIdentity!;
        var predecessorId = supersedeUncommittedTuna
            ? currentOperation!.Value.Authority.AuthorityId
            : (FileTransferAuthorityId?)null;
        var kind = supersedeUncommittedTuna
            ? FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6
            : FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6;
        var sourceCommit = supersedeUncommittedTuna
            ? null
            : context.RuntimeUnlockRouteCommitToken;
        if (!supersedeUncommittedTuna &&
            sourceCommit is not { IsDefault: false })
        {
            return false;
        }

        var checkpoint = CreateInboundFileTransferRouteTransitionCheckpoint(
            context,
            owner,
            publication,
            leg);
        var request = new FileTransferRouteTransitionPrepareRequest(
            kind,
            owner,
            publication,
            sourceCommit,
            context.RouteSelection.Route,
            context.NegotiatedDataProtocolVersion,
            ResolveFileTransferRouteSourceTransport(context.RouteSelection.Route),
            new FileTransferFallbackTransitionProofBoundary(
                sourceCommit,
                proof,
                identity,
                proof.TransportEpoch,
                Math.Max(1, leg.BridgeRecoveryGeneration + 1),
                predecessorId),
            checkpoint,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            predecessorId,
            NormalizeReason(evidenceShadow.Reason) ??
            "peer_post_tuna_fallback_v6_proof");
        var prepared = supersedeUncommittedTuna
            ? FileTransferCoordinator.StageFileTransferRouteTransitionSupersession(
                state,
                currentOperation!.Value.Authority,
                request)
            : FileTransferCoordinator.PrepareFileTransferRouteTransition(
                state,
                request);
        if (supersedeUncommittedTuna)
        {
            LogFileTransferRouteTransitionSupersessionDecision(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                "stage",
                currentOperation!.Value.Authority,
                prepared.Authority,
                prepared.Status.ToString(),
                prepared.Reason);
        }

        if (prepared.Status is not (
                FileTransferRouteTransitionDecisionStatus.Prepared or
                FileTransferRouteTransitionDecisionStatus.Idempotent or
                FileTransferRouteTransitionDecisionStatus.SupersessionStaged) ||
            prepared.Authority is not { } authority ||
            prepared.PauseBindCommand is not { } pauseCommand)
        {
            LogFileTransferRouteTransitionDecision(
                "filetransfer_route_transition_prepared",
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                prepared.Authority,
                prepared.Status.ToString(),
                prepared.Reason,
                capturedCount: 0,
                completedCount: 0);
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_prepared",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            prepared.Status.ToString(),
            prepared.Reason,
            capturedCount: 0,
            completedCount: 0);
        context.CoordinatorPersistentState = prepared.State.PersistentState;
        if (!TryBindFileTransferRouteTransitionPause(
                pauseCommand,
                out var pauseReceipt,
                out var pauseRejection))
        {
            InvalidateInboundFileTransferRouteTransitionLocked(
                context,
                authority,
                terminalInvalidation: false,
                pauseRejection);
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_pause_bound",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            pauseReceipt.Status.ToString(),
            pauseReceipt.Reason,
            capturedCount: 0,
            completedCount: 0);
        FileTransferRouteTransitionDecision draining;
        if (supersedeUncommittedTuna)
        {
            var predecessor = currentOperation!.Value.Authority;
            var committedSupersession =
                FileTransferCoordinator.CommitFileTransferRouteTransitionSupersession(
                    CreateCoordinatorStateLocked(context),
                    pauseReceipt);
            LogFileTransferRouteTransitionSupersessionDecision(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                "commit",
                predecessor,
                committedSupersession.Authority,
                committedSupersession.Status.ToString(),
                committedSupersession.Reason);
            if (committedSupersession.Status is not (
                    FileTransferRouteTransitionDecisionStatus.Prepared or
                    FileTransferRouteTransitionDecisionStatus.Draining or
                    FileTransferRouteTransitionDecisionStatus.Idempotent) ||
                committedSupersession.Authority != authority)
            {
                return false;
            }

            context.CoordinatorPersistentState =
                committedSupersession.State.PersistentState;
            if (!ApplyFileTransferRouteTransitionDispositionWhileLocked(
                    context,
                    committedSupersession.DispositionCommand))
            {
                return false;
            }

            var current =
                FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(
                    CreateCoordinatorStateLocked(context)).CurrentOperation;
            if (current is null ||
                current.Value.Authority != authority)
            {
                return false;
            }

            if (current.Value is
                {
                    State: FileTransferRouteTransitionState.Draining,
                    DrainCommand: { } inheritedDrain,
                })
            {
                draining = new FileTransferRouteTransitionDecision(
                    FileTransferRouteTransitionDecisionStatus.Draining,
                    committedSupersession.Reason,
                    authority,
                    pauseCommand,
                    inheritedDrain,
                    committedSupersession.DispositionCommand)
                {
                    State = CreateCoordinatorStateLocked(context),
                };
            }
            else if (current.Value is
                     {
                         State: FileTransferRouteTransitionState.Prepared,
                         DrainCommand: null,
                     })
            {
                var emptyIds = Array.Empty<long>();
                draining =
                    FileTransferCoordinator
                        .BeginFileTransferRouteTransitionPhysicalDrain(
                            CreateCoordinatorStateLocked(context),
                            authority,
                            pauseReceipt,
                            capturedThroughPhysicalSendGeneration: 0,
                            emptyIds,
                            FileTransferCoordinator
                                .CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                                    emptyIds));
                if (draining.Status is not (
                        FileTransferRouteTransitionDecisionStatus.Draining or
                        FileTransferRouteTransitionDecisionStatus.Idempotent) ||
                    draining.DrainCommand is null)
                {
                    return false;
                }

                context.CoordinatorPersistentState =
                    draining.State.PersistentState;
            }
            else
            {
                return false;
            }
        }
        else
        {
            var emptyIds = Array.Empty<long>();
            draining =
                FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                    CreateCoordinatorStateLocked(context),
                    authority,
                    pauseReceipt,
                    capturedThroughPhysicalSendGeneration: 0,
                    emptyIds,
                    FileTransferCoordinator
                        .CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                            emptyIds));
            if (draining.Status is not (
                    FileTransferRouteTransitionDecisionStatus.Draining or
                    FileTransferRouteTransitionDecisionStatus.Idempotent) ||
                draining.DrainCommand is null)
            {
                InvalidateInboundFileTransferRouteTransitionLocked(
                    context,
                    authority,
                    terminalInvalidation: false,
                    draining.Reason);
                return false;
            }

            context.CoordinatorPersistentState = draining.State.PersistentState;
        }

        if (draining.DrainCommand is not { } drainCommand)
        {
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_draining",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            draining.Status.ToString(),
            draining.Reason,
            drainCommand.CapturedPhysicalSendCount,
            completedCount: 0,
            drainCommand);
        var execution = evidenceShadow with
        {
            Authority = authority,
            DrainCommand = drainCommand,
            CapturedPhysicalTasks =
                FileTransferRouteTransitionExecutionShadow.SnapshotTasks([]),
            DrainRecheckQueued = false,
        };
        context.FileTransferRouteTransitionExecution = execution;
        BindInboundFileTransferRouteTransitionPauseLocked(context);
        snapshot = CreateSnapshotLocked();
        QueueInboundFileTransferRouteTransitionDrainRecheckLocked(
            context,
            execution,
            "fallback_transition_empty_inbound_drain");
        return true;
    }

    private bool TryPrepareOutboundRegularV4ToTunaRouteTransitionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string reason,
        out SessionFileTransferSnapshot? transferSnapshot)
    {
        transferSnapshot = null;
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.V4LastStateEpoch < 0 ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is not { } dataSession ||
            !TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
                context,
                snapshot,
                out var admission,
                out var pendingPreCommit))
        {
            return false;
        }

        if (context.FileTransferRouteTransitionExecution is
            {
                Authority.TargetRoute: FileTransferRoute.FileTunaV4,
                DrainCommand: not null,
            } current)
        {
            QueueOutboundFileTransferRouteTransitionDrainRecheckLocked(
                context,
                current,
                "tuna_activation_final_proof_observed");
            return true;
        }

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(
            snapshot,
            admission);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.RegularV4ToTunaV4,
            owner,
            publication,
            SourceRouteCommit: null,
            context.RouteSelection.Route,
            context.NegotiatedDataProtocolVersion,
            FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(
                admission,
                pendingPreCommit,
                proof,
                snapshot.LaunchGeneration),
            CreateOutboundFileTransferRouteTransitionCheckpoint(
                context,
                owner,
                publication,
                leg),
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            PredecessorTransitionAuthorityId: null,
            NormalizeReason(reason) ?? "regular_v4_tuna_activation");
        return TryPrepareOutboundFileTransferRouteTransitionLocked(
            context,
            dataSession,
            request,
            retainedPeerProof: null,
            retainedPeerFrame: null,
            retainedFrameIdentity: null,
            reason,
            out transferSnapshot);
    }

    private bool TryPrepareInboundRegularV4ToTunaRouteTransitionLocked(
        InboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string reason,
        out SessionFileTransferSnapshot? transferSnapshot)
    {
        transferSnapshot = null;
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            !context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.ReceiverSparseChunksPendingWrite.Count != 0 ||
            context.PendingChunks.Count != 0 ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is not { } dataSession ||
            !TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
                context,
                snapshot,
                out var admission,
                out var pendingPreCommit))
        {
            return false;
        }

        if (context.FileTransferRouteTransitionExecution is
            {
                Authority.TargetRoute: FileTransferRoute.FileTunaV4,
                DrainCommand: not null,
            } current)
        {
            QueueInboundFileTransferRouteTransitionDrainRecheckLocked(
                context,
                current,
                "tuna_activation_final_proof_observed");
            return true;
        }

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(
            snapshot,
            admission);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.RegularV4ToTunaV4,
            owner,
            publication,
            SourceRouteCommit: null,
            context.RouteSelection.Route,
            context.NegotiatedDataProtocolVersion,
            FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(
                admission,
                pendingPreCommit,
                proof,
                snapshot.LaunchGeneration),
            CreateInboundFileTransferRouteTransitionCheckpoint(
                context,
                owner,
                publication,
                leg),
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            PredecessorTransitionAuthorityId: null,
            NormalizeReason(reason) ?? "regular_v4_tuna_activation");
        return TryPrepareInboundFileTransferRouteTransitionLocked(
            context,
            dataSession,
            request,
            retainedPeerProof: null,
            retainedPeerFrame: null,
            retainedFrameIdentity: null,
            reason,
            out transferSnapshot);
    }

    private bool TryPrepareOutboundPostTunaFallbackToTunaRouteTransitionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string reason,
        out SessionFileTransferSnapshot? transferSnapshot)
    {
        transferSnapshot = null;
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is not { } dataSession ||
            !FileTransferCoordinator
                .TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(
                    CreateCoordinatorStateLocked(context),
                    out var retryReceipt) ||
            GetCurrentFallbackTailYieldOperation(context) is not
                {
                    PairedRelease: { IsDefault: false } pairedRelease,
                    PairingState:
                        FileTransferFallbackTailYieldPairingState
                            .ReleaseConsumed,
                } ||
            !TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
                context,
                snapshot,
                out var admission,
                out var pendingPreCommit))
        {
            return false;
        }

        if (context.FileTransferRouteTransitionExecution is
            {
                Authority.TargetRoute: FileTransferRoute.FileTunaV4,
                DrainCommand: not null,
            } current)
        {
            QueueOutboundFileTransferRouteTransitionDrainRecheckLocked(
                context,
                current,
                "post_fallback_tuna_final_proof_observed");
            return true;
        }

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(
            snapshot,
            admission);
        var checkpoint = CreateOutboundFileTransferRouteTransitionCheckpoint(
            context,
            owner,
            publication,
            leg);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4,
            owner,
            publication,
            context.RuntimeUnlockRouteCommitToken,
            context.RouteSelection.Route,
            context.NegotiatedDataProtocolVersion,
            FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(
                admission,
                pendingPreCommit,
                proof,
                snapshot.LaunchGeneration,
                retryReceipt,
                pairedRelease),
            checkpoint,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            PredecessorTransitionAuthorityId: null,
            NormalizeReason(reason) ?? "post_tuna_fallback_tuna_activation");
        return TryPrepareOutboundFileTransferRouteTransitionLocked(
            context,
            dataSession,
            request,
            retainedPeerProof: null,
            retainedPeerFrame: null,
            retainedFrameIdentity: null,
            reason,
            out transferSnapshot);
    }

    private bool TryPrepareInboundPostTunaFallbackToTunaRouteTransitionLocked(
        InboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string reason,
        out SessionFileTransferSnapshot? transferSnapshot)
    {
        transferSnapshot = null;
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            context.ReceiverSparseChunksPendingWrite.Count != 0 ||
            context.PendingChunks.Count != 0 ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is not { } dataSession ||
            !FileTransferCoordinator
                .TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(
                    CreateCoordinatorStateLocked(context),
                    out var retryReceipt) ||
            GetCurrentFallbackTailYieldOperation(context) is not
                {
                    PairedRelease: { IsDefault: false } pairedRelease,
                    PairingState:
                        FileTransferFallbackTailYieldPairingState
                            .ReleaseConsumed,
                } ||
            !TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
                context,
                snapshot,
                out var admission,
                out var pendingPreCommit))
        {
            return false;
        }

        if (context.FileTransferRouteTransitionExecution is
            {
                Authority.TargetRoute: FileTransferRoute.FileTunaV4,
                DrainCommand: not null,
            } current)
        {
            QueueInboundFileTransferRouteTransitionDrainRecheckLocked(
                context,
                current,
                "post_fallback_tuna_final_proof_observed");
            return true;
        }

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(
            snapshot,
            admission);
        var checkpoint = CreateInboundFileTransferRouteTransitionCheckpoint(
            context,
            owner,
            publication,
            leg);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4,
            owner,
            publication,
            context.RuntimeUnlockRouteCommitToken,
            context.RouteSelection.Route,
            context.NegotiatedDataProtocolVersion,
            FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(
                admission,
                pendingPreCommit,
                proof,
                snapshot.LaunchGeneration,
                retryReceipt,
                pairedRelease),
            checkpoint,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            PredecessorTransitionAuthorityId: null,
            NormalizeReason(reason) ?? "post_tuna_fallback_tuna_activation");
        return TryPrepareInboundFileTransferRouteTransitionLocked(
            context,
            dataSession,
            request,
            retainedPeerProof: null,
            retainedPeerFrame: null,
            retainedFrameIdentity: null,
            reason,
            out transferSnapshot);
    }

    private bool TryPrepareOutboundFileTransferRouteTransitionLocked(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferRouteTransitionPrepareRequest request,
        PostTunaFallbackPeerProofEvidence? retainedPeerProof,
        FileTransferDataFrame? retainedPeerFrame,
        PostTunaFallbackPeerProofFrameIdentity? retainedFrameIdentity,
        string reason,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
            CreateCoordinatorStateLocked(context),
            request);
        if (prepared.Status is not (
                FileTransferRouteTransitionDecisionStatus.Prepared or
                FileTransferRouteTransitionDecisionStatus.Idempotent) ||
            prepared.Authority is not { } authority ||
            prepared.PauseBindCommand is not { } pauseCommand)
        {
            LogFileTransferRouteTransitionDecision(
                "filetransfer_route_transition_prepared",
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                prepared.Authority,
                prepared.Status.ToString(),
                prepared.Reason,
                capturedCount: 0,
                completedCount: 0);
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_prepared",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            prepared.Status.ToString(),
            prepared.Reason,
            capturedCount: 0,
            completedCount: 0);
        context.CoordinatorPersistentState = prepared.State.PersistentState;
        if (!TryBindFileTransferRouteTransitionPause(
                pauseCommand,
                out var pauseReceipt,
                out var pauseRejection))
        {
            InvalidateOutboundFileTransferRouteTransitionLocked(
                context,
                authority,
                terminalInvalidation: false,
                pauseRejection);
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_pause_bound",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            pauseReceipt.Status.ToString(),
            pauseReceipt.Reason,
            capturedCount: 0,
            completedCount: 0);
        var capturedThrough = context.LastOutboundPhysicalSendLeaseGeneration;
        var capturedIds = context.ActiveOutboundPhysicalSendLeases.Values
            .Where(lease =>
                lease.LeaseGeneration <= capturedThrough &&
                IsOutboundPhysicalSendLeaseForTransitionAuthority(
                    lease,
                    authority))
            .Select(static lease => lease.LeaseGeneration)
            .OrderBy(static generation => generation)
            .ToArray();
        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                CreateCoordinatorStateLocked(context),
                authority,
                pauseReceipt,
                capturedThrough,
                capturedIds,
                FileTransferCoordinator
                    .CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                        capturedIds));
        if (draining.Status is not (
                FileTransferRouteTransitionDecisionStatus.Draining or
                FileTransferRouteTransitionDecisionStatus.Idempotent) ||
            draining.DrainCommand is not { } drainCommand)
        {
            InvalidateOutboundFileTransferRouteTransitionLocked(
                context,
                authority,
                terminalInvalidation: false,
                draining.Reason);
            return false;
        }

        context.CoordinatorPersistentState = draining.State.PersistentState;
        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_draining",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            draining.Status.ToString(),
            draining.Reason,
            drainCommand.CapturedPhysicalSendCount,
            completedCount: 0,
            drainCommand);
        var tasks = FileTransferRouteTransitionExecutionShadow.SnapshotTasks(
            capturedIds
                .Select(id =>
                    context.ActiveOutboundPhysicalSendLeases.TryGetValue(
                        id,
                        out var lease) &&
                    lease.PhysicalCompletionTask is { } task
                        ? new KeyValuePair<long, Task>(id, task)
                        : default)
                .Where(static pair => pair.Key > 0));
        var execution = new FileTransferRouteTransitionExecutionShadow(
            authority,
            dataSession,
            context.DataSessionGeneration,
            drainCommand,
            tasks,
            retainedPeerProof,
            retainedPeerFrame,
            retainedFrameIdentity,
            LastFrameReceipt: null,
            DrainRecheckQueued: false,
            FrameApplyInFlight: false,
            NormalizeReason(reason) ?? authority.Reason);
        context.FileTransferRouteTransitionExecution = execution;
        BindOutboundFileTransferRouteTransitionPauseLocked(context);
        snapshot = CreateSnapshotLocked();
        if (AreOutboundFileTransferRouteTransitionPhysicalTasksCompleteLocked(
                context,
                execution))
        {
            QueueOutboundFileTransferRouteTransitionDrainRecheckLocked(
                context,
                execution,
                "route_transition_empty_or_completed_drain");
        }

        return true;
    }

    private bool TryPrepareInboundFileTransferRouteTransitionLocked(
        InboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferRouteTransitionPrepareRequest request,
        PostTunaFallbackPeerProofEvidence? retainedPeerProof,
        FileTransferDataFrame? retainedPeerFrame,
        PostTunaFallbackPeerProofFrameIdentity? retainedFrameIdentity,
        string reason,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (context.ReceiverSparseChunksPendingWrite.Count != 0 ||
            context.PendingChunks.Count != 0)
        {
            return false;
        }

        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
            CreateCoordinatorStateLocked(context),
            request);
        if (prepared.Status is not (
                FileTransferRouteTransitionDecisionStatus.Prepared or
                FileTransferRouteTransitionDecisionStatus.Idempotent) ||
            prepared.Authority is not { } authority ||
            prepared.PauseBindCommand is not { } pauseCommand)
        {
            LogFileTransferRouteTransitionDecision(
                "filetransfer_route_transition_prepared",
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                prepared.Authority,
                prepared.Status.ToString(),
                prepared.Reason,
                capturedCount: 0,
                completedCount: 0);
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_prepared",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            prepared.Status.ToString(),
            prepared.Reason,
            capturedCount: 0,
            completedCount: 0);
        context.CoordinatorPersistentState = prepared.State.PersistentState;
        if (!TryBindFileTransferRouteTransitionPause(
                pauseCommand,
                out var pauseReceipt,
                out var pauseRejection))
        {
            InvalidateInboundFileTransferRouteTransitionLocked(
                context,
                authority,
                terminalInvalidation: false,
                pauseRejection);
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_pause_bound",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            pauseReceipt.Status.ToString(),
            pauseReceipt.Reason,
            capturedCount: 0,
            completedCount: 0);
        var emptyIds = Array.Empty<long>();
        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                CreateCoordinatorStateLocked(context),
                authority,
                pauseReceipt,
                capturedThroughPhysicalSendGeneration: 0,
                emptyIds,
                FileTransferCoordinator
                    .CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                        emptyIds));
        if (draining.Status is not (
                FileTransferRouteTransitionDecisionStatus.Draining or
                FileTransferRouteTransitionDecisionStatus.Idempotent) ||
            draining.DrainCommand is not { } drainCommand)
        {
            InvalidateInboundFileTransferRouteTransitionLocked(
                context,
                authority,
                terminalInvalidation: false,
                draining.Reason);
            return false;
        }

        context.CoordinatorPersistentState = draining.State.PersistentState;
        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_draining",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            draining.Status.ToString(),
            draining.Reason,
            drainCommand.CapturedPhysicalSendCount,
            completedCount: 0,
            drainCommand);
        var execution = new FileTransferRouteTransitionExecutionShadow(
            authority,
            dataSession,
            context.DataSessionGeneration,
            drainCommand,
            FileTransferRouteTransitionExecutionShadow.SnapshotTasks([]),
            retainedPeerProof,
            retainedPeerFrame,
            retainedFrameIdentity,
            LastFrameReceipt: null,
            DrainRecheckQueued: false,
            FrameApplyInFlight: false,
            NormalizeReason(reason) ?? authority.Reason);
        context.FileTransferRouteTransitionExecution = execution;
        BindInboundFileTransferRouteTransitionPauseLocked(context);
        snapshot = CreateSnapshotLocked();
        QueueInboundFileTransferRouteTransitionDrainRecheckLocked(
            context,
            execution,
            "route_transition_empty_inbound_drain");
        return true;
    }

    private void QueueOutboundFileTransferRouteTransitionDrainRecheckLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow execution,
        string reason)
    {
        if (context.FileTransferRouteTransitionExecution != execution ||
            execution.Authority is null ||
            execution.DrainCommand is null ||
            execution.DrainRecheckQueued)
        {
            return;
        }

        var queued = execution with
        {
            DrainRecheckQueued = true,
            Reason = NormalizeReason(reason) ?? execution.Reason,
        };
        context.FileTransferRouteTransitionExecution = queued;
        _ = RecheckOutboundFileTransferRouteTransitionDrainAsync(
            context,
            queued);
    }

    private void QueueInboundFileTransferRouteTransitionDrainRecheckLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow execution,
        string reason)
    {
        if (context.FileTransferRouteTransitionExecution != execution ||
            execution.Authority is null ||
            execution.DrainCommand is null ||
            execution.DrainRecheckQueued)
        {
            return;
        }

        var queued = execution with
        {
            DrainRecheckQueued = true,
            Reason = NormalizeReason(reason) ?? execution.Reason,
        };
        context.FileTransferRouteTransitionExecution = queued;
        _ = RecheckInboundFileTransferRouteTransitionDrainAsync(
            context,
            queued);
    }

    private void RecheckOutboundFileTransferRouteTransitionAfterPhysicalSendLocked(
        OutboundTransferContext context,
        OutboundPhysicalSendLease retiredLease)
    {
        if (context.FileTransferRouteTransitionExecution is not { } execution ||
            execution.Authority is not { } authority ||
            execution.DrainCommand is not { } command ||
            !command.CapturedPhysicalSendLeaseIds.Contains(
                retiredLease.LeaseGeneration) ||
            !IsOutboundPhysicalSendLeaseForTransitionAuthority(
                retiredLease,
                authority) ||
            !AreOutboundFileTransferRouteTransitionPhysicalTasksCompleteLocked(
                context,
                execution))
        {
            return;
        }

        QueueOutboundFileTransferRouteTransitionDrainRecheckLocked(
            context,
            execution,
            "captured_physical_send_tasks_completed");
    }

    private async Task RecheckOutboundFileTransferRouteTransitionDrainAsync(
        OutboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow queued)
    {
        await Task.Yield();
        SessionFileTransferSnapshot? snapshot = null;
        var committed = false;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.FileTransferRouteTransitionExecution != queued)
            {
                return;
            }

            var execution = queued with { DrainRecheckQueued = false };
            context.FileTransferRouteTransitionExecution = execution;
            if (!IsOutboundFileTransferRouteTransitionExecutionCurrentLocked(
                    context,
                    execution,
                    out var rejectionReason))
            {
                if (execution.Authority is { } staleAuthority)
                {
                    InvalidateOutboundFileTransferRouteTransitionLocked(
                        context,
                        staleAuthority,
                        context.IsTerminal,
                        rejectionReason);
                }

                return;
            }

            if (!AreOutboundFileTransferRouteTransitionPhysicalTasksCompleteLocked(
                    context,
                    execution))
            {
                return;
            }

            committed = TryCommitOutboundFileTransferRouteTransitionLocked(
                context,
                execution,
                out snapshot,
                out rejectionReason);
            if (!committed)
            {
                LogFileTransferRouteTransitionDecision(
                    "filetransfer_route_transition_drain_receipt_decided",
                    context.SessionId,
                    context.TransferId,
                    FileTransferDirection.Outbound,
                    execution.Authority,
                    "Rejected",
                    rejectionReason,
                    execution.DrainCommand?.CapturedPhysicalSendCount ?? 0,
                    completedCount: 0);
            }
        }

        if (committed && snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }
    }

    private async Task RecheckInboundFileTransferRouteTransitionDrainAsync(
        InboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow queued)
    {
        await Task.Yield();
        SessionFileTransferSnapshot? snapshot = null;
        var committed = false;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.FileTransferRouteTransitionExecution != queued)
            {
                return;
            }

            var execution = queued with { DrainRecheckQueued = false };
            context.FileTransferRouteTransitionExecution = execution;
            if (!IsInboundFileTransferRouteTransitionExecutionCurrentLocked(
                    context,
                    execution,
                    out var rejectionReason))
            {
                if (execution.Authority is { } staleAuthority)
                {
                    InvalidateInboundFileTransferRouteTransitionLocked(
                        context,
                        staleAuthority,
                        context.IsTerminal,
                        rejectionReason);
                }

                return;
            }

            if (context.ReceiverSparseChunksPendingWrite.Count != 0 ||
                context.PendingChunks.Count != 0)
            {
                return;
            }

            committed = TryCommitInboundFileTransferRouteTransitionLocked(
                context,
                execution,
                out snapshot,
                out rejectionReason);
            if (!committed)
            {
                LogFileTransferRouteTransitionDecision(
                    "filetransfer_route_transition_drain_receipt_decided",
                    context.SessionId,
                    context.TransferId,
                    FileTransferDirection.Inbound,
                    execution.Authority,
                    "Rejected",
                    rejectionReason,
                    capturedCount: 0,
                    completedCount: 0);
            }
        }

        if (committed && snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            await TryApplyInboundCommittedFileTransferRouteTransitionRetainedFrameAsync(
                    context)
                .ConfigureAwait(false);
        }
    }

    private bool TryCommitOutboundFileTransferRouteTransitionLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow execution,
        out SessionFileTransferSnapshot? snapshot,
        out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = "filetransfer_route_transition_not_current";
        if (execution.Authority is not { } authority ||
            execution.DrainCommand is not { } drainCommand ||
            !IsOutboundFileTransferRouteTransitionExecutionCurrentLocked(
                context,
                execution,
                out rejectionReason) ||
            !TryCreateFileTransferRouteTransitionDrainReceipt(
                drainCommand,
                out var drainReceipt))
        {
            return false;
        }

        RuntimeUnlockRouteCommitProof? finalProof = null;
        if (authority.TargetRoute == FileTransferRoute.FileTunaV4)
        {
            if (authority.ProofBoundary is not
                    FileTransferTunaActivationTransitionProofBoundary activation ||
                transport is not IRuntimeUnlockRouteCommitProofProvider provider ||
                !provider.TryGetRuntimeUnlockRouteCommitProof(
                    context.SessionId,
                    context.TransferId,
                    out var currentSnapshot))
            {
                rejectionReason =
                    "filetransfer_route_transition_final_proof_missing";
                return false;
            }

            finalProof = RuntimeUnlockTransaction.CreateRouteCommitProof(
                currentSnapshot,
                activation.RuntimeUnlockAdmission);
        }

        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator
                .TryValidateFileTransferRouteTransitionCommitProof(
                    state,
                    authority,
                    finalProof,
                    out rejectionReason))
        {
            return false;
        }

        if (!TryApplyFileTransferRouteTransitionPressureSettlement(
                state,
                authority,
                drainReceipt,
                finalProof,
                out var pressureReceipt,
                out rejectionReason))
        {
            return false;
        }

        if (!TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
                context,
                "retry_dispatch_runtime_scope_replaced"))
        {
            rejectionReason =
                "runtime_unlock_retry_scope_invalidation_failed";
            return false;
        }

        state = CreateCoordinatorStateLocked(context);
        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(
            state,
            authority,
            drainReceipt,
            finalProof,
            pressureReceipt);
        if (committed.Status is not (
                FileTransferRouteTransitionDecisionStatus.Committed or
                FileTransferRouteTransitionDecisionStatus.Idempotent) ||
            committed.DispositionCommand is not { } disposition ||
            committed.RouteDecision is not { } routeDecision)
        {
            rejectionReason = committed.Reason;
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_drain_receipt_decided",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            committed.Status == FileTransferRouteTransitionDecisionStatus.Idempotent
                ? "Idempotent"
                : "Accepted",
            committed.Reason,
            drainCommand.CapturedPhysicalSendCount,
            drainReceipt.CompletedPhysicalSendCount,
            drainCommand,
            drainReceipt);
        var previousRoute = context.RouteSelection;
        ApplyCoordinatorDecisionToContextLocked(
            context,
            routeDecision,
            forwardRoutePublication: false);
        ApplyOutboundFileTransferRouteTransitionTargetRuntimeLocked(
            context,
            authority,
            previousRoute,
            disposition,
            execution.Reason);
        context.FileTransferRouteTransitionExecution = execution with
        {
            Authority = authority,
            LastFrameReceipt = null,
            DrainRecheckQueued = false,
            StartedLiveRouteEpoch = routeDecision.StartedLiveRouteEpoch,
            RecoveredLiveRouteEpoch = routeDecision.RecoveredLiveRouteEpoch,
        };
        EmitOutboundFileTransferRouteTransitionCompatibilityProjectionsLocked(
            context,
            authority,
            previousRoute,
            routeDecision,
            disposition,
            execution.Reason);
        ForwardFileTransferRouteTransitionPublication(
            context.RoutePublicationAuthorityForwarder,
            disposition.RoutePublicationToken,
            execution.Reason);
        snapshot = CreateSnapshotLocked();

        if (committed.RetainedFrameApplyCommand is { })
        {
            // The sparse runtime install callback claims and applies this frame.
            return true;
        }

        if (!ApplyFileTransferRouteTransitionDispositionWhileLocked(
                context,
                disposition))
        {
            rejectionReason =
                "filetransfer_route_transition_disposition_apply_failed";
            return false;
        }

        context.FileTransferRouteTransitionExecution = null;
        rejectionReason = "none";
        return true;
    }

    private bool TryCommitInboundFileTransferRouteTransitionLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow execution,
        out SessionFileTransferSnapshot? snapshot,
        out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = "filetransfer_route_transition_not_current";
        if (execution.Authority is not { } authority ||
            execution.DrainCommand is not { } drainCommand ||
            !IsInboundFileTransferRouteTransitionExecutionCurrentLocked(
                context,
                execution,
                out rejectionReason) ||
            !TryCreateFileTransferRouteTransitionDrainReceipt(
                drainCommand,
                out var drainReceipt))
        {
            return false;
        }

        RuntimeUnlockRouteCommitProof? finalProof = null;
        if (authority.TargetRoute == FileTransferRoute.FileTunaV4)
        {
            if (authority.ProofBoundary is not
                    FileTransferTunaActivationTransitionProofBoundary activation ||
                transport is not IRuntimeUnlockRouteCommitProofProvider provider ||
                !provider.TryGetRuntimeUnlockRouteCommitProof(
                    context.SessionId,
                    context.TransferId,
                    out var currentSnapshot))
            {
                rejectionReason =
                    "filetransfer_route_transition_final_proof_missing";
                return false;
            }

            finalProof = RuntimeUnlockTransaction.CreateRouteCommitProof(
                currentSnapshot,
                activation.RuntimeUnlockAdmission);
        }

        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator
                .TryValidateFileTransferRouteTransitionCommitProof(
                    state,
                    authority,
                    finalProof,
                    out rejectionReason))
        {
            return false;
        }

        if (!TryApplyFileTransferRouteTransitionPressureSettlement(
                state,
                authority,
                drainReceipt,
                finalProof,
                out var pressureReceipt,
                out rejectionReason))
        {
            return false;
        }

        if (!TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
                context,
                "retry_dispatch_runtime_scope_replaced"))
        {
            rejectionReason =
                "runtime_unlock_retry_scope_invalidation_failed";
            return false;
        }

        state = CreateCoordinatorStateLocked(context);
        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(
            state,
            authority,
            drainReceipt,
            finalProof,
            pressureReceipt);
        if (committed.Status is not (
                FileTransferRouteTransitionDecisionStatus.Committed or
                FileTransferRouteTransitionDecisionStatus.Idempotent) ||
            committed.DispositionCommand is not { } disposition ||
            committed.RouteDecision is not { } routeDecision)
        {
            rejectionReason = committed.Reason;
            return false;
        }

        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_drain_receipt_decided",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            committed.Status == FileTransferRouteTransitionDecisionStatus.Idempotent
                ? "Idempotent"
                : "Accepted",
            committed.Reason,
            drainCommand.CapturedPhysicalSendCount,
            drainReceipt.CompletedPhysicalSendCount,
            drainCommand,
            drainReceipt);
        var previousRoute = context.RouteSelection;
        ApplyCoordinatorDecisionToContextLocked(
            context,
            routeDecision,
            forwardRoutePublication: false);
        ApplyInboundFileTransferRouteTransitionTargetRuntimeLocked(
            context,
            authority,
            previousRoute,
            disposition,
            execution.Reason);
        context.FileTransferRouteTransitionExecution = execution with
        {
            Authority = authority,
            LastFrameReceipt = null,
            DrainRecheckQueued = false,
            StartedLiveRouteEpoch = routeDecision.StartedLiveRouteEpoch,
            RecoveredLiveRouteEpoch = routeDecision.RecoveredLiveRouteEpoch,
        };
        EmitInboundFileTransferRouteTransitionCompatibilityProjectionsLocked(
            context,
            authority,
            previousRoute,
            routeDecision,
            disposition,
            execution.Reason);
        ForwardFileTransferRouteTransitionPublication(
            context.RoutePublicationAuthorityForwarder,
            disposition.RoutePublicationToken,
            execution.Reason);
        snapshot = CreateSnapshotLocked();

        if (committed.RetainedFrameApplyCommand is { })
        {
            // Inbound retained-frame application is dispatched by the receive runtime hook.
            return true;
        }

        if (!ApplyFileTransferRouteTransitionDispositionWhileLocked(
                context,
                disposition))
        {
            rejectionReason =
                "filetransfer_route_transition_disposition_apply_failed";
            return false;
        }

        context.FileTransferRouteTransitionExecution = null;
        rejectionReason = "none";
        return true;
    }

    private async Task TryApplyOutboundCommittedFileTransferRouteTransitionRetainedFrameAsync(
        OutboundTransferContext context,
        OutboundSparsePumpInstall install,
        IFileTransferDataSession dataSession)
    {
        FileTransferRouteTransitionExecutionShadow execution;
        FileTransferRouteTransitionRetainedFrameApplyCommand command;
        long receiveSupervisorGeneration;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.FileTransferRouteTransitionExecution is not
                    {
                        Authority: { } authority,
                        RetainedPeerFrame: { } frame,
                        RetainedFrameIdentity: { } identity,
                    } current ||
                current.FrameApplyInFlight ||
                current.LastFrameReceipt is not null ||
                !identity.Matches(frame) ||
                !ReferenceEquals(current.SourceDataSession, dataSession) ||
                context.DataSessionGeneration != current.SourceDataSessionGeneration ||
                context.CurrentRuntimeOwner != install.RuntimeOwner ||
                context.CurrentRoutePublicationToken != install.RoutePublication ||
                FileTransferCoordinator
                    .GetFileTransferRouteTransitionSubstate(
                        CreateCoordinatorStateLocked(context))
                    .LastTerminalOperation is not
                    {
                        Authority: var terminalAuthority,
                        RetainedFrameApplyCommand: { } pendingCommand,
                        RetainedFrameApplyReceipt: null,
                    } ||
                terminalAuthority != authority ||
                pendingCommand.FrameIdentity != identity)
            {
                return;
            }

            execution = current with { FrameApplyInFlight = true };
            context.FileTransferRouteTransitionExecution = execution;
            command = pendingCommand;
            receiveSupervisorGeneration =
                context.ActiveOutboundSparseReceiveSupervisorGeneration;
        }

        var applied = false;
        var applyReason = "filetransfer_route_transition_retained_frame_rejected";
        try
        {
            switch (execution.RetainedPeerFrame)
            {
                case FileTransferReceiverStateFrameV6 receiverState:
                    await ApplyOutboundV6ReceiverState(
                            context,
                            receiverState,
                            FileTransferTransportKind.RegularNkn)
                        .ConfigureAwait(false);
                    applied = true;
                    applyReason =
                        "filetransfer_route_transition_retained_frame_applied";
                    break;

                case FileTransferFrontierRequestFrameV6 frontier:
                    var result = ApplyOutboundV6RepairRequestCore(
                        context,
                        frontier,
                        FileTransferTransportKind.RegularNkn,
                        install,
                        install.RuntimeOwner,
                        dataSession,
                        install.DataSessionGeneration,
                        receiveSupervisorGeneration);
                    applied = result.Status is
                        RetainedPeerProofApplicationStatus.Applied or
                        RetainedPeerProofApplicationStatus.Idempotent;
                    applyReason = applied
                        ? "filetransfer_route_transition_retained_frame_applied"
                        : result.Reason;
                    break;

                default:
                    applyReason =
                        "filetransfer_route_transition_retained_frame_type_unsupported";
                    break;
            }
        }
        catch (Exception ex)
        {
            applyReason =
                $"filetransfer_route_transition_retained_frame_apply_failed_{NormalizeReason(ex.GetType().Name)}";
        }

        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.FileTransferRouteTransitionExecution != execution)
            {
                return;
            }

            var receipt = new FileTransferRouteTransitionRetainedFrameApplyReceipt(
                command,
                applied
                    ? FileTransferRouteTransitionRetainedFrameApplyStatus.Applied
                    : FileTransferRouteTransitionRetainedFrameApplyStatus.Rejected,
                applied
                    ? "filetransfer_route_transition_retained_frame_applied"
                    : NormalizeReason(applyReason) ??
                      "filetransfer_route_transition_retained_frame_rejected");
            var appliedDecision =
                FileTransferCoordinator.ApplyFileTransferRouteTransitionRetainedFrameReceipt(
                    CreateCoordinatorStateLocked(context),
                    receipt);
            if (!applied ||
                appliedDecision.Status is not (
                    FileTransferRouteTransitionDecisionStatus.Committed or
                    FileTransferRouteTransitionDecisionStatus.Idempotent))
            {
                context.FileTransferRouteTransitionExecution = execution with
                {
                    FrameApplyInFlight = false,
                    LastFrameReceipt = receipt,
                };
                LogFileTransferRouteTransitionRetainedFrame(
                    context.SessionId,
                    context.TransferId,
                    FileTransferDirection.Outbound,
                    execution.Authority,
                    execution.RetainedFrameIdentity,
                    "Rejected",
                    appliedDecision.Reason);
                return;
            }

            context.CoordinatorPersistentState =
                appliedDecision.State.PersistentState;
            context.FileTransferRouteTransitionExecution = execution with
            {
                FrameApplyInFlight = false,
                LastFrameReceipt = receipt,
            };
            var disposition =
                FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(
                    CreateCoordinatorStateLocked(context)).LastTerminalOperation
                    ?.DispositionCommand;
            LogFileTransferRouteTransitionRetainedFrame(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                execution.Authority,
                execution.RetainedFrameIdentity,
                "Applied",
                appliedDecision.Reason);
            if (!ApplyFileTransferRouteTransitionDispositionWhileLocked(
                    context,
                    disposition))
            {
                return;
            }

            context.FileTransferRouteTransitionExecution = null;
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }
    }

    private bool TryCompleteInboundFileTransferRouteTransitionRetainedFrameLocked(
        InboundTransferContext context,
        FileTransferDataFrame frame,
        bool applied,
        string reason,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.FileTransferRouteTransitionExecution is not
                {
                    Authority: { } authority,
                    RetainedPeerFrame: { } retainedFrame,
                    RetainedFrameIdentity: { } identity,
                } execution ||
            !identity.Matches(frame) ||
            !identity.Matches(retainedFrame) ||
            FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(
                    CreateCoordinatorStateLocked(context))
                .LastTerminalOperation is not
                {
                    Authority: var terminalAuthority,
                    RetainedFrameApplyCommand: { } command,
                    RetainedFrameApplyReceipt: null,
                } ||
            terminalAuthority != authority)
        {
            return false;
        }

        var receipt = new FileTransferRouteTransitionRetainedFrameApplyReceipt(
            command,
            applied
                ? FileTransferRouteTransitionRetainedFrameApplyStatus.Applied
                : FileTransferRouteTransitionRetainedFrameApplyStatus.Rejected,
            applied
                ? "filetransfer_route_transition_retained_frame_applied"
                : NormalizeReason(reason) ??
                  "filetransfer_route_transition_retained_frame_rejected");
        var decision =
            FileTransferCoordinator.ApplyFileTransferRouteTransitionRetainedFrameReceipt(
                CreateCoordinatorStateLocked(context),
                receipt);
        if (!applied ||
            decision.Status is not (
                FileTransferRouteTransitionDecisionStatus.Committed or
                FileTransferRouteTransitionDecisionStatus.Idempotent))
        {
            context.FileTransferRouteTransitionExecution = execution with
            {
                FrameApplyInFlight = false,
                LastFrameReceipt = receipt,
            };
            return false;
        }

        context.CoordinatorPersistentState = decision.State.PersistentState;
        context.FileTransferRouteTransitionExecution = execution with
        {
            FrameApplyInFlight = false,
            LastFrameReceipt = receipt,
        };
        var disposition =
            FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(
                CreateCoordinatorStateLocked(context)).LastTerminalOperation
                ?.DispositionCommand;
        LogFileTransferRouteTransitionRetainedFrame(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            execution.Authority,
            execution.RetainedFrameIdentity,
            "Applied",
            decision.Reason);
        if (!ApplyFileTransferRouteTransitionDispositionWhileLocked(
                context,
                disposition))
        {
            return false;
        }

        context.FileTransferRouteTransitionExecution = null;
        snapshot = CreateSnapshotLocked();
        return true;
    }

    private async Task
        TryApplyInboundCommittedFileTransferRouteTransitionRetainedFrameAsync(
            InboundTransferContext context)
    {
        FileTransferRouteTransitionExecutionShadow execution;
        FileTransferDataFrame frame;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.FileTransferRouteTransitionExecution is not
                    {
                        Authority: { } authority,
                        RetainedPeerFrame: { } retainedFrame,
                        RetainedFrameIdentity: { } identity,
                    } current ||
                current.FrameApplyInFlight ||
                current.LastFrameReceipt is not null ||
                !identity.Matches(retainedFrame) ||
                FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(
                        CreateCoordinatorStateLocked(context))
                    .LastTerminalOperation is not
                    {
                        Authority: var terminalAuthority,
                        RetainedFrameApplyCommand: { } command,
                        RetainedFrameApplyReceipt: null,
                    } ||
                terminalAuthority != authority ||
                command.FrameIdentity != identity)
            {
                return;
            }

            execution = current with { FrameApplyInFlight = true };
            context.FileTransferRouteTransitionExecution = execution;
            frame = retainedFrame;
        }

        var applied = false;
        var reason =
            "filetransfer_route_transition_retained_frame_type_unsupported";
        try
        {
            switch (frame)
            {
                case FileTransferFrontierRequestFrameV6 frontier:
                {
                    MarkInboundV4PeerFrameReceived(context);
                    var shouldRespond = true;
                    lock (gate)
                    {
                        if (!ReferenceEquals(inboundTransfer, context) ||
                            context.IsTerminal ||
                            context.FileTransferRouteTransitionExecution !=
                                execution)
                        {
                            shouldRespond = false;
                        }
                        else if (IsV6RegularNknStateRefreshRequest(frontier))
                        {
                            shouldRespond =
                                MarkInboundFallbackCheckpointRequestedLocked(
                                    context,
                                    frontier,
                                    "route_transition_retained_frame");
                        }
                    }

                    applied = shouldRespond &&
                        await SendInboundSparseCreditStateAsync(
                                context,
                                V6RegularNknStateRefreshRecoveryMode,
                                terminalReady: false,
                                forceSend: true)
                            .ConfigureAwait(false);
                    reason = applied
                        ? "filetransfer_route_transition_retained_frame_applied"
                        : "filetransfer_route_transition_retained_frame_response_failed";
                    break;
                }

                case FileTransferReceiverStateFrameV6 receiverState:
                    MarkInboundV4PeerFrameReceived(context);
                    var peerResumed =
                        ApplyInboundV4PeerState(context, receiverState);
                    if (peerResumed)
                    {
                        await FlushInboundV4PausedProgressAsync(
                                context,
                                "route_transition_retained_frame")
                            .ConfigureAwait(false);
                    }

                    applied = true;
                    reason =
                        "filetransfer_route_transition_retained_frame_applied";
                    break;
            }
        }
        catch (Exception ex)
        {
            reason =
                $"filetransfer_route_transition_retained_frame_apply_failed_{NormalizeReason(ex.GetType().Name)}";
        }

        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.FileTransferRouteTransitionExecution != execution)
            {
                return;
            }

            if (!TryCompleteInboundFileTransferRouteTransitionRetainedFrameLocked(
                    context,
                    frame,
                    applied,
                    reason,
                    out snapshot))
            {
                if (context.FileTransferRouteTransitionExecution == execution)
                {
                    context.FileTransferRouteTransitionExecution = execution with
                    {
                        FrameApplyInFlight = false,
                    };
                }
                LogFileTransferRouteTransitionRetainedFrame(
                    context.SessionId,
                    context.TransferId,
                    FileTransferDirection.Inbound,
                    execution.Authority,
                    execution.RetainedFrameIdentity,
                    "Rejected",
                    reason);
                return;
            }

        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }
    }

    private bool InvalidateOutboundFileTransferRouteTransitionLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        bool terminalInvalidation,
        string reason)
    {
        var decision = FileTransferCoordinator.InvalidateFileTransferRouteTransition(
            CreateCoordinatorStateLocked(context),
            authority,
            terminalInvalidation,
            NormalizeReason(reason) ?? "filetransfer_route_transition_invalidated");
        if (decision.Status is not (
                FileTransferRouteTransitionDecisionStatus.Invalidated or
                FileTransferRouteTransitionDecisionStatus.Idempotent))
        {
            return false;
        }

        context.CoordinatorPersistentState = decision.State.PersistentState;
        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_invalidated",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            decision.Status.ToString(),
            decision.Reason,
            context.FileTransferRouteTransitionExecution?.DrainCommand
                ?.CapturedPhysicalSendCount ?? 0,
            completedCount: 0,
            context.FileTransferRouteTransitionExecution?.DrainCommand);
        if (!ApplyFileTransferRouteTransitionDispositionWhileLocked(
                context,
                decision.DispositionCommand))
        {
            return false;
        }

        context.FileTransferRouteTransitionExecution = null;
        return true;
    }

    private bool InvalidateInboundFileTransferRouteTransitionLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        bool terminalInvalidation,
        string reason)
    {
        var decision = FileTransferCoordinator.InvalidateFileTransferRouteTransition(
            CreateCoordinatorStateLocked(context),
            authority,
            terminalInvalidation,
            NormalizeReason(reason) ?? "filetransfer_route_transition_invalidated");
        if (decision.Status is not (
                FileTransferRouteTransitionDecisionStatus.Invalidated or
                FileTransferRouteTransitionDecisionStatus.Idempotent))
        {
            return false;
        }

        context.CoordinatorPersistentState = decision.State.PersistentState;
        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_invalidated",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            decision.Status.ToString(),
            decision.Reason,
            context.FileTransferRouteTransitionExecution?.DrainCommand
                ?.CapturedPhysicalSendCount ?? 0,
            completedCount: 0,
            context.FileTransferRouteTransitionExecution?.DrainCommand);
        if (!ApplyFileTransferRouteTransitionDispositionWhileLocked(
                context,
                decision.DispositionCommand))
        {
            return false;
        }

        context.FileTransferRouteTransitionExecution = null;
        return true;
    }

    private bool TryBindFileTransferRouteTransitionPause(
        FileTransferRouteTransitionPauseBindCommand command,
        out FileTransferRouteTransitionPauseBindReceipt receipt,
        out string rejectionReason)
    {
        receipt = default;
        rejectionReason = "filetransfer_route_transition_bridge_missing";
        if (transport is not IFileTransferRouteTransitionBridge bridge)
        {
            return false;
        }

        try
        {
            receipt = bridge.BindFileTransferRouteTransitionPause(command);
        }
        catch (Exception ex)
        {
            rejectionReason =
                $"filetransfer_route_transition_pause_bind_exception_{NormalizeReason(ex.GetType().Name)}";
            return false;
        }

        if (receipt.Command != command ||
            receipt.Status is not (
                FileTransferRouteTransitionPauseBindStatus.Installed or
                FileTransferRouteTransitionPauseBindStatus.Idempotent))
        {
            rejectionReason = string.IsNullOrWhiteSpace(receipt.Reason)
                ? "filetransfer_route_transition_pause_bind_rejected"
                : receipt.Reason;
            return false;
        }

        rejectionReason = "none";
        return true;
    }

    private bool ApplyFileTransferRouteTransitionDispositionWhileLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionDispositionCommand? command)
    {
        if (command is null ||
            transport is not IFileTransferRouteTransitionBridge bridge)
        {
            return false;
        }

        FileTransferRouteTransitionDispositionReceipt receipt;
        try
        {
            receipt = bridge.ApplyFileTransferRouteTransitionDisposition(
                command.Value);
        }
        catch
        {
            return false;
        }

        var decision =
            FileTransferCoordinator.ApplyFileTransferRouteTransitionDispositionReceipt(
                CreateCoordinatorStateLocked(context),
                receipt);
        if (decision.Status is not (
                FileTransferRouteTransitionDecisionStatus.Committed or
                FileTransferRouteTransitionDecisionStatus.Invalidated or
                FileTransferRouteTransitionDecisionStatus.Idempotent))
        {
            return false;
        }

        context.CoordinatorPersistentState = decision.State.PersistentState;
        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_disposition_applied",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            command.Value.Authority,
            receipt.Status.ToString(),
            receipt.Reason,
            context.FileTransferRouteTransitionExecution?.DrainCommand
                ?.CapturedPhysicalSendCount ?? 0,
            context.FileTransferRouteTransitionExecution?.DrainCommand
                ?.CapturedPhysicalSendCount ?? 0,
            context.FileTransferRouteTransitionExecution?.DrainCommand,
            resultingRoutePublication: command.Value.RoutePublicationToken,
            resultingRouteCommit: command.Value.RouteCommitToken);
        if (command.Value.Kind ==
            FileTransferRouteTransitionDispositionKind.Committed)
        {
            EmitOutboundFileTransferRouteTransitionRecoveredProjectionLocked(
                context,
                command.Value);
            context.PullTransportPaused = false;
            context.PullTransportPausedSinceUtc = null;
            context.PullTransportGraceDeadlineUtc = null;
            context.PullTransportPauseReason = null;
            context.PullTransportResumeRequestPending = false;
            context.SignalSparseSenderPump();
        }
        else if (command.Value.Kind ==
                 FileTransferRouteTransitionDispositionKind.RolledBack)
        {
            context.PullTransportPaused = false;
            context.PullTransportPausedSinceUtc = null;
            context.PullTransportGraceDeadlineUtc = null;
            context.PullTransportPauseReason = null;
            context.PullTransportResumeRequestPending = false;
            context.SignalSparseSenderPump();
        }

        return true;
    }

    private bool ApplyFileTransferRouteTransitionDispositionWhileLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionDispositionCommand? command)
    {
        if (command is null ||
            transport is not IFileTransferRouteTransitionBridge bridge)
        {
            return false;
        }

        FileTransferRouteTransitionDispositionReceipt receipt;
        try
        {
            receipt = bridge.ApplyFileTransferRouteTransitionDisposition(
                command.Value);
        }
        catch
        {
            return false;
        }

        var decision =
            FileTransferCoordinator.ApplyFileTransferRouteTransitionDispositionReceipt(
                CreateCoordinatorStateLocked(context),
                receipt);
        if (decision.Status is not (
                FileTransferRouteTransitionDecisionStatus.Committed or
                FileTransferRouteTransitionDecisionStatus.Invalidated or
                FileTransferRouteTransitionDecisionStatus.Idempotent))
        {
            return false;
        }

        context.CoordinatorPersistentState = decision.State.PersistentState;
        LogFileTransferRouteTransitionDecision(
            "filetransfer_route_transition_disposition_applied",
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            command.Value.Authority,
            receipt.Status.ToString(),
            receipt.Reason,
            context.FileTransferRouteTransitionExecution?.DrainCommand
                ?.CapturedPhysicalSendCount ?? 0,
            context.FileTransferRouteTransitionExecution?.DrainCommand
                ?.CapturedPhysicalSendCount ?? 0,
            context.FileTransferRouteTransitionExecution?.DrainCommand,
            resultingRoutePublication: command.Value.RoutePublicationToken,
            resultingRouteCommit: command.Value.RouteCommitToken);
        if (command.Value.Kind is
            FileTransferRouteTransitionDispositionKind.Committed or
            FileTransferRouteTransitionDispositionKind.RolledBack)
        {
            if (command.Value.Kind ==
                FileTransferRouteTransitionDispositionKind.Committed)
            {
                EmitInboundFileTransferRouteTransitionRecoveredProjectionLocked(
                    context,
                    command.Value);
            }
            context.PullTransportPaused = false;
            context.PullTransportPausedSinceUtc = null;
            context.PullTransportGraceDeadlineUtc = null;
            context.PullTransportPauseReason = null;
            context.PullTransportResumeRequestPending = false;
        }

        return true;
    }

    private void EmitOutboundFileTransferRouteTransitionCompatibilityProjectionsLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        FileTransferRouteSelection previousRoute,
        FileTransferCoordinatorDecision routeDecision,
        FileTransferRouteTransitionDispositionCommand disposition,
        string reason)
    {
        if (!IsOutboundFileTransferRouteTransitionProjectionCurrentLocked(
                context,
                authority,
                routeDecision,
                disposition) ||
            routeDecision.StartedLiveRouteEpoch is not { } startedEpoch)
        {
            return;
        }

        var routeInput = CreateRouteInputForPublishedSelection(
            context.RouteSelection,
            ResolveTransportProfileKind(transport));
        var token = disposition.RoutePublicationToken!.Value;
        var projectionReason =
            NormalizeReason(reason) ?? routeDecision.Reason;
        if (TryReserveFileTransferRouteTransitionCompatibilityProjection(
                context.FileTransferRouteTransitionCompatibilityProjectionKeys,
                authority,
                "transitioned"))
        {
            LogFileTransferRouteTransitioned(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                previousRoute,
                context.RouteSelection,
                projectionReason,
                token,
                routeDecision.Reason,
                completedSendDrainGeneration: authority.TransitionGeneration,
                transitionAuthorityId: authority.AuthorityId,
                transitionGeneration: authority.TransitionGeneration);
        }

        if (TryReserveFileTransferRouteTransitionCompatibilityProjection(
                context.FileTransferRouteTransitionCompatibilityProjectionKeys,
                authority,
                "selected"))
        {
            LogFileTransferRouteSelected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.RouteSelection,
                routeInput,
                startedEpoch.EpochId,
                token,
                routeDecision.Reason,
                authority.AuthorityId,
                authority.TransitionGeneration);
        }

        if (TryReserveFileTransferRouteTransitionCompatibilityProjection(
                context.FileTransferRouteTransitionCompatibilityProjectionKeys,
                authority,
                "started"))
        {
            LogLiveRouteEpochStarted(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                startedEpoch,
                previousRoute,
                token,
                routeDecision.Reason,
                authority.AuthorityId,
                authority.TransitionGeneration);
        }
    }

    private void EmitInboundFileTransferRouteTransitionCompatibilityProjectionsLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        FileTransferRouteSelection previousRoute,
        FileTransferCoordinatorDecision routeDecision,
        FileTransferRouteTransitionDispositionCommand disposition,
        string reason)
    {
        if (!IsInboundFileTransferRouteTransitionProjectionCurrentLocked(
                context,
                authority,
                routeDecision,
                disposition) ||
            routeDecision.StartedLiveRouteEpoch is not { } startedEpoch)
        {
            return;
        }

        var routeInput = CreateRouteInputForPublishedSelection(
            context.RouteSelection,
            ResolveTransportProfileKind(transport));
        var token = disposition.RoutePublicationToken!.Value;
        var projectionReason =
            NormalizeReason(reason) ?? routeDecision.Reason;
        if (TryReserveFileTransferRouteTransitionCompatibilityProjection(
                context.FileTransferRouteTransitionCompatibilityProjectionKeys,
                authority,
                "transitioned"))
        {
            LogFileTransferRouteTransitioned(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                previousRoute,
                context.RouteSelection,
                projectionReason,
                token,
                routeDecision.Reason,
                completedSendDrainGeneration: authority.TransitionGeneration,
                transitionAuthorityId: authority.AuthorityId,
                transitionGeneration: authority.TransitionGeneration);
        }

        if (TryReserveFileTransferRouteTransitionCompatibilityProjection(
                context.FileTransferRouteTransitionCompatibilityProjectionKeys,
                authority,
                "selected"))
        {
            LogFileTransferRouteSelected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.RouteSelection,
                routeInput,
                startedEpoch.EpochId,
                token,
                routeDecision.Reason,
                authority.AuthorityId,
                authority.TransitionGeneration);
        }

        if (TryReserveFileTransferRouteTransitionCompatibilityProjection(
                context.FileTransferRouteTransitionCompatibilityProjectionKeys,
                authority,
                "started"))
        {
            LogLiveRouteEpochStarted(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                startedEpoch,
                previousRoute,
                token,
                routeDecision.Reason,
                authority.AuthorityId,
                authority.TransitionGeneration);
        }
    }

    private static bool IsOutboundFileTransferRouteTransitionProjectionCurrentLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        FileTransferCoordinatorDecision routeDecision,
        FileTransferRouteTransitionDispositionCommand disposition)
        => authority.Direction == FileTransferDirection.Outbound &&
           string.Equals(authority.SessionId, context.SessionId, StringComparison.Ordinal) &&
           string.Equals(authority.TransferId, context.TransferId, StringComparison.Ordinal) &&
           authority.TargetRoute == context.RouteSelection.Route &&
           authority.TargetProtocolVersion == context.RouteSelection.ProtocolVersion &&
           routeDecision.StartedLiveRouteEpoch is { } epoch &&
           epoch.EpochId == context.CurrentLiveRouteEpoch?.EpochId &&
           epoch.RouteSelection == context.RouteSelection &&
           disposition.Authority == authority &&
           disposition.RoutePublicationToken is { } publication &&
           publication == context.CurrentRoutePublicationToken &&
           publication == routeDecision.State.CurrentRoutePublicationToken &&
           publication.RuntimeOwner == context.CurrentRuntimeOwner &&
           disposition.RouteCommitToken == context.RuntimeUnlockRouteCommitToken;

    private static bool IsInboundFileTransferRouteTransitionProjectionCurrentLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        FileTransferCoordinatorDecision routeDecision,
        FileTransferRouteTransitionDispositionCommand disposition)
        => authority.Direction == FileTransferDirection.Inbound &&
           string.Equals(authority.SessionId, context.SessionId, StringComparison.Ordinal) &&
           string.Equals(authority.TransferId, context.TransferId, StringComparison.Ordinal) &&
           authority.TargetRoute == context.RouteSelection.Route &&
           authority.TargetProtocolVersion == context.RouteSelection.ProtocolVersion &&
           routeDecision.StartedLiveRouteEpoch is { } epoch &&
           epoch.EpochId == context.CurrentLiveRouteEpoch?.EpochId &&
           epoch.RouteSelection == context.RouteSelection &&
           disposition.Authority == authority &&
           disposition.RoutePublicationToken is { } publication &&
           publication == context.CurrentRoutePublicationToken &&
           publication == routeDecision.State.CurrentRoutePublicationToken &&
           publication.RuntimeOwner == context.CurrentRuntimeOwner &&
           disposition.RouteCommitToken == context.RuntimeUnlockRouteCommitToken;

    private static bool TryReserveFileTransferRouteTransitionCompatibilityProjection(
        HashSet<string> projectionKeys,
        FileTransferRouteTransitionAuthority authority,
        string projectionKind)
        => projectionKeys.Add(
            $"{authority.Direction}|{authority.AuthorityId}|{authority.TransitionGeneration}|{projectionKind}");

    private static bool IsFileTransferRouteTransitionRecoveredProjectionCurrent(
        FileTransferCoordinatorPersistentState persistentState,
        FileTransferRouteTransitionExecutionShadow execution,
        FileTransferRouteTransitionDispositionCommand disposition)
    {
        if (execution.Authority is not { } authority ||
            disposition.Authority != authority ||
            disposition.Kind != FileTransferRouteTransitionDispositionKind.Committed ||
            persistentState.FileTransferRouteTransition.LastTerminalOperation is not
            {
                Authority: var terminalAuthority,
                State: FileTransferRouteTransitionState.Committed,
                DispositionCommand: { } terminalDisposition,
                DispositionReceipt: { } dispositionReceipt,
            } terminal ||
            terminalAuthority != authority ||
            terminalDisposition != disposition ||
            dispositionReceipt.Command != disposition)
        {
            return false;
        }

        return authority.TargetRoute != FileTransferRoute.PostTunaFallbackV6 ||
               terminal.RetainedFrameApplyReceipt is
               {
                   Status:
                       FileTransferRouteTransitionRetainedFrameApplyStatus.Applied or
                       FileTransferRouteTransitionRetainedFrameApplyStatus.Idempotent,
               };
    }

    private void EmitOutboundFileTransferRouteTransitionRecoveredProjectionLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionDispositionCommand disposition)
    {
        if (context.FileTransferRouteTransitionExecution is not { } execution ||
            !IsFileTransferRouteTransitionRecoveredProjectionCurrent(
                context.CoordinatorPersistentState,
                execution,
                disposition) ||
            execution.Authority is not { } authority ||
            (execution.RecoveredLiveRouteEpoch ??
             execution.StartedLiveRouteEpoch) is not { } epoch ||
            context.CurrentRoutePublicationToken != disposition.RoutePublicationToken ||
            !TryReserveFileTransferRouteTransitionCompatibilityProjection(
                context.FileTransferRouteTransitionCompatibilityProjectionKeys,
                authority,
                "recovered"))
        {
            return;
        }

        LogLiveRouteEpochRecovered(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            epoch,
            disposition.Reason,
            disposition.RoutePublicationToken,
            disposition.Reason,
            authority.AuthorityId,
            authority.TransitionGeneration,
            evidenceOnly: true);
    }

    private void EmitInboundFileTransferRouteTransitionRecoveredProjectionLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionDispositionCommand disposition)
    {
        if (context.FileTransferRouteTransitionExecution is not { } execution ||
            !IsFileTransferRouteTransitionRecoveredProjectionCurrent(
                context.CoordinatorPersistentState,
                execution,
                disposition) ||
            execution.Authority is not { } authority ||
            (execution.RecoveredLiveRouteEpoch ??
             execution.StartedLiveRouteEpoch) is not { } epoch ||
            context.CurrentRoutePublicationToken != disposition.RoutePublicationToken ||
            !TryReserveFileTransferRouteTransitionCompatibilityProjection(
                context.FileTransferRouteTransitionCompatibilityProjectionKeys,
                authority,
                "recovered"))
        {
            return;
        }

        LogLiveRouteEpochRecovered(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            epoch,
            disposition.Reason,
            disposition.RoutePublicationToken,
            disposition.Reason,
            authority.AuthorityId,
            authority.TransitionGeneration,
            evidenceOnly: true);
    }

    private bool TryApplyFileTransferRouteTransitionPressureSettlement(
        FileTransferCoordinatorState state,
        FileTransferRouteTransitionAuthority authority,
        FileTransferRouteTransitionPhysicalDrainReceipt drainReceipt,
        RuntimeUnlockRouteCommitProof? finalProof,
        out FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
            receipt,
        out string rejectionReason)
    {
        receipt = null;
        rejectionReason = "none";
        var pressure =
            state.PersistentState.RegularV4FrontierRepairPressure;
        if (pressure.PendingBinding is not null ||
            pressure.RetainedPredecessorBinding is not null)
        {
            rejectionReason =
                "filetransfer_route_transition_pressure_lineage_pending";
            return false;
        }

        if (pressure.InstalledBinding is null)
        {
            return true;
        }

        var prepared =
            FileTransferCoordinator.PrepareFileTransferRouteTransitionPressureSettlement(
                state,
                authority,
                drainReceipt,
                finalProof);
        if (prepared.Status !=
                RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted ||
            prepared.SettlementCommand is not { } command)
        {
            rejectionReason = prepared.Reason;
            return false;
        }

        if (transport is not
            IFileTransferRegularV4FrontierRepairPressureInstallBridge bridge)
        {
            rejectionReason =
                "filetransfer_route_transition_pressure_bridge_missing";
            return false;
        }

        try
        {
            receipt =
                bridge.ApplyRegularV4FrontierRepairPressureSettlement(command);
        }
        catch (Exception ex)
        {
            rejectionReason =
                $"filetransfer_route_transition_pressure_apply_exception_{NormalizeReason(ex.GetType().Name)}";
            return false;
        }

        if (receipt.Status is not (
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus
                    .Applied or
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus
                    .Idempotent))
        {
            rejectionReason = receipt.Reason;
            return false;
        }

        return true;
    }

    private static bool TryCreateFileTransferRouteTransitionDrainReceipt(
        FileTransferRouteTransitionPhysicalDrainCommand command,
        out FileTransferRouteTransitionPhysicalDrainReceipt receipt)
    {
        var completed = command.CapturedPhysicalSendLeaseIds
            .OrderBy(static generation => generation)
            .ToArray();
        var fingerprint =
            FileTransferCoordinator.CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                completed);
        if (!string.Equals(
                fingerprint,
                command.CapturedPhysicalSendFingerprint,
                StringComparison.Ordinal))
        {
            receipt = default;
            return false;
        }

        receipt = new FileTransferRouteTransitionPhysicalDrainReceipt(
            command,
            Array.AsReadOnly(completed),
            completed.Length,
            fingerprint,
            FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
            "filetransfer_route_transition_physical_drain_completed");
        return true;
    }

    private static FileTransferRouteTransitionDurableCheckpoint
        CreateOutboundFileTransferRouteTransitionCheckpoint(
            OutboundTransferContext context,
            RuntimeOwnerToken owner,
            RoutePublicationToken publication,
            FileTransferLeg leg)
        => new(
            owner,
            publication,
            context.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            context.DataSessionGeneration,
            Math.Max(0, context.V4LastStateEpoch),
            Math.Clamp(
                context.RemoteNextExpectedChunkIndex,
                0,
                Math.Max(0, context.ChunkCount)),
            Math.Clamp(
                context.V4LastStateDurableHighestChunkIndex,
                -1,
                Math.Max(-1, context.ChunkCount - 1)),
            Math.Max(0, context.ChunkCount),
            checked((int)Math.Min(
                int.MaxValue,
                Math.Max(1, context.DataSessionGeneration))));

    private static FileTransferRouteTransitionDurableCheckpoint
        CreateInboundFileTransferRouteTransitionCheckpoint(
            InboundTransferContext context,
            RuntimeOwnerToken owner,
            RoutePublicationToken publication,
            FileTransferLeg leg)
        => new(
            owner,
            publication,
            context.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            context.DataSessionGeneration,
            Math.Max(0, context.V4StateEpoch),
            Math.Clamp(
                context.NextChunkIndex,
                0,
                Math.Max(0, context.ChunkCount)),
            Math.Clamp(
                Math.Max(
                    context.V4LastStateDurableHighestChunkIndex,
                    context.HighestBufferedChunkIndex),
                -1,
                Math.Max(-1, context.ChunkCount - 1)),
            Math.Max(0, context.ChunkCount),
            checked((int)Math.Min(
                int.MaxValue,
                Math.Max(1, context.DataSessionGeneration))));

    private static FileTransferTransportKind
        ResolveFileTransferRouteSourceTransport(FileTransferRoute route)
        => route == FileTransferRoute.FileTunaV4
            ? FileTransferTransportKind.Tuna
            : FileTransferTransportKind.RegularNkn;

    private static bool IsOutboundPhysicalSendLeaseForTransitionAuthority(
        OutboundPhysicalSendLease lease,
        FileTransferRouteTransitionAuthority authority)
        => lease.RuntimeOwner == authority.SourceRuntimeOwner &&
           lease.RoutePublication == authority.SourceRoutePublication &&
           lease.TerminalGeneration == authority.SourceTerminalGeneration &&
           lease.LegGeneration == authority.SourceLegGeneration &&
           lease.TransportEpoch == authority.SourceTransportEpoch &&
           lease.BridgeRecoveryGeneration ==
               authority.SourceBridgeRecoveryGeneration;

    private static bool AreOutboundFileTransferRouteTransitionPhysicalTasksCompleteLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow execution)
    {
        if (execution.DrainCommand is not { } command)
        {
            return false;
        }

        foreach (var leaseId in command.CapturedPhysicalSendLeaseIds)
        {
            if (context.ActiveOutboundPhysicalSendLeases.ContainsKey(leaseId))
            {
                return false;
            }

            if (execution.CapturedPhysicalTasks.TryGetValue(
                    leaseId,
                    out var task) &&
                !task.IsCompleted)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOutboundFileTransferRouteTransitionExecutionCurrentLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow execution,
        out string rejectionReason)
    {
        rejectionReason = "filetransfer_route_transition_execution_stale";
        if (execution.Authority is not { } authority ||
            execution.DrainCommand is not { } drainCommand ||
            drainCommand.Authority != authority ||
            context.IsTerminal ||
            context.CurrentRuntimeOwner != authority.SourceRuntimeOwner ||
            context.CurrentRoutePublicationToken != authority.SourceRoutePublication ||
            context.TerminalGeneration != authority.SourceTerminalGeneration ||
            context.CurrentTransferLeg is not { } leg ||
            leg.Generation != authority.SourceLegGeneration ||
            leg.TransportEpochId != authority.SourceTransportEpoch ||
            leg.BridgeRecoveryGeneration !=
                authority.SourceBridgeRecoveryGeneration ||
            !ReferenceEquals(context.DataSession, execution.SourceDataSession) ||
            context.DataSessionGeneration != execution.SourceDataSessionGeneration ||
            context.DataSessionGeneration != authority.DataSessionGeneration ||
            !IsOutboundFileTransferRouteTransitionDurableCheckpointCurrentLocked(
                context,
                authority.DurableCheckpoint) ||
            !FileTransferCoordinator.IsFileTransferRouteTransitionCurrent(
                CreateCoordinatorStateLocked(context),
                authority,
                out rejectionReason))
        {
            return false;
        }

        rejectionReason = "none";
        return true;
    }

    private static bool IsInboundFileTransferRouteTransitionExecutionCurrentLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow execution,
        out string rejectionReason)
    {
        rejectionReason = "filetransfer_route_transition_execution_stale";
        if (execution.Authority is not { } authority ||
            execution.DrainCommand is not { } drainCommand ||
            drainCommand.Authority != authority ||
            context.IsTerminal ||
            context.CurrentRuntimeOwner != authority.SourceRuntimeOwner ||
            context.CurrentRoutePublicationToken != authority.SourceRoutePublication ||
            context.TerminalGeneration != authority.SourceTerminalGeneration ||
            context.CurrentTransferLeg is not { } leg ||
            leg.Generation != authority.SourceLegGeneration ||
            leg.TransportEpochId != authority.SourceTransportEpoch ||
            leg.BridgeRecoveryGeneration !=
                authority.SourceBridgeRecoveryGeneration ||
            !ReferenceEquals(context.DataSession, execution.SourceDataSession) ||
            context.DataSessionGeneration != execution.SourceDataSessionGeneration ||
            context.DataSessionGeneration != authority.DataSessionGeneration ||
            !IsInboundFileTransferRouteTransitionDurableCheckpointCurrentLocked(
                context,
                authority.DurableCheckpoint) ||
            !FileTransferCoordinator.IsFileTransferRouteTransitionCurrent(
                CreateCoordinatorStateLocked(context),
                authority,
                out rejectionReason))
        {
            return false;
        }

        rejectionReason = "none";
        return true;
    }

    private static bool
        IsOutboundFileTransferRouteTransitionDurableCheckpointCurrentLocked(
            OutboundTransferContext context,
            FileTransferRouteTransitionDurableCheckpoint checkpoint)
        => context.CurrentRuntimeOwner == checkpoint.SourceRuntimeOwner &&
           context.CurrentRoutePublicationToken ==
               checkpoint.SourceRoutePublication &&
           context.TerminalGeneration ==
               checkpoint.SourceTerminalGeneration &&
           context.CurrentTransferLeg is { } leg &&
           leg.Generation == checkpoint.SourceLegGeneration &&
           leg.TransportEpochId == checkpoint.SourceTransportEpoch &&
           leg.BridgeRecoveryGeneration ==
               checkpoint.SourceBridgeRecoveryGeneration &&
           context.DataSessionGeneration ==
               checkpoint.DataSessionGeneration &&
           Math.Max(0, context.V4LastStateEpoch) >=
               checkpoint.StateEpoch &&
           Math.Clamp(
               context.RemoteNextExpectedChunkIndex,
               0,
               Math.Max(0, context.ChunkCount)) >=
               checkpoint.ContiguousCommittedChunkIndex &&
           Math.Clamp(
               context.V4LastStateDurableHighestChunkIndex,
               -1,
               Math.Max(-1, context.ChunkCount - 1)) >=
               checkpoint.DurableHighestChunkIndex &&
           Math.Max(0, context.ChunkCount) == checkpoint.ChunkCount &&
           checked((int)Math.Min(
               int.MaxValue,
               Math.Max(1, context.DataSessionGeneration))) ==
               checkpoint.CheckpointGeneration;

    private static bool
        IsInboundFileTransferRouteTransitionDurableCheckpointCurrentLocked(
            InboundTransferContext context,
            FileTransferRouteTransitionDurableCheckpoint checkpoint)
        => context.CurrentRuntimeOwner == checkpoint.SourceRuntimeOwner &&
           context.CurrentRoutePublicationToken ==
               checkpoint.SourceRoutePublication &&
           context.TerminalGeneration ==
               checkpoint.SourceTerminalGeneration &&
           context.CurrentTransferLeg is { } leg &&
           leg.Generation == checkpoint.SourceLegGeneration &&
           leg.TransportEpochId == checkpoint.SourceTransportEpoch &&
           leg.BridgeRecoveryGeneration ==
               checkpoint.SourceBridgeRecoveryGeneration &&
           context.DataSessionGeneration ==
               checkpoint.DataSessionGeneration &&
           Math.Max(0, context.V4StateEpoch) >= checkpoint.StateEpoch &&
           Math.Clamp(
               context.NextChunkIndex,
               0,
               Math.Max(0, context.ChunkCount)) >=
               checkpoint.ContiguousCommittedChunkIndex &&
           Math.Clamp(
               Math.Max(
                   context.V4LastStateDurableHighestChunkIndex,
                   context.HighestBufferedChunkIndex),
               -1,
               Math.Max(-1, context.ChunkCount - 1)) >=
               checkpoint.DurableHighestChunkIndex &&
           Math.Max(0, context.ChunkCount) == checkpoint.ChunkCount &&
           checked((int)Math.Min(
               int.MaxValue,
               Math.Max(1, context.DataSessionGeneration))) ==
               checkpoint.CheckpointGeneration;

    private bool TryGetOutboundFileTransferRouteTransitionPauseOwnershipLocked(
        OutboundTransferContext context,
        out FileTransferRouteTransitionPauseOwnership ownership,
        out FileTransferRouteTransitionAuthority? authority,
        out string rejectionReason)
    {
        ownership = default;
        authority = context.FileTransferRouteTransitionExecution?.Authority;
        rejectionReason =
            "filetransfer_route_transition_execution_shadow_missing";
        if (context.FileTransferRouteTransitionExecution is not { } execution ||
            authority is not { } exactAuthority ||
            !ReferenceEquals(context.DataSession, execution.SourceDataSession) ||
            context.DataSessionGeneration != execution.SourceDataSessionGeneration ||
            context.DataSessionGeneration != exactAuthority.DataSessionGeneration)
        {
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator.TryGetFileTransferRouteTransitionPauseOwnership(
                state,
                exactAuthority,
                out ownership,
                out rejectionReason))
        {
            ownership = GetFileTransferRouteTransitionPauseOwnershipForEvidence(
                state,
                exactAuthority);
            return false;
        }

        if (ownership.State == FileTransferRouteTransitionState.Draining &&
            !IsOutboundFileTransferRouteTransitionExecutionCurrentLocked(
                context,
                execution,
                out rejectionReason))
        {
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    private bool TryGetInboundFileTransferRouteTransitionPauseOwnershipLocked(
        InboundTransferContext context,
        out FileTransferRouteTransitionPauseOwnership ownership,
        out FileTransferRouteTransitionAuthority? authority,
        out string rejectionReason)
    {
        ownership = default;
        authority = context.FileTransferRouteTransitionExecution?.Authority;
        rejectionReason =
            "filetransfer_route_transition_execution_shadow_missing";
        if (context.FileTransferRouteTransitionExecution is not { } execution ||
            authority is not { } exactAuthority ||
            !ReferenceEquals(context.DataSession, execution.SourceDataSession) ||
            context.DataSessionGeneration != execution.SourceDataSessionGeneration ||
            context.DataSessionGeneration != exactAuthority.DataSessionGeneration)
        {
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator.TryGetFileTransferRouteTransitionPauseOwnership(
                state,
                exactAuthority,
                out ownership,
                out rejectionReason))
        {
            ownership = GetFileTransferRouteTransitionPauseOwnershipForEvidence(
                state,
                exactAuthority);
            return false;
        }

        if (ownership.State == FileTransferRouteTransitionState.Draining &&
            !IsInboundFileTransferRouteTransitionExecutionCurrentLocked(
                context,
                execution,
                out rejectionReason))
        {
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    private static FileTransferRouteTransitionPauseOwnership
        GetFileTransferRouteTransitionPauseOwnershipForEvidence(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority authority)
    {
        var transition =
            FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(state);
        if (transition.CurrentOperation is { } current &&
            current.Authority == authority)
        {
            return new FileTransferRouteTransitionPauseOwnership(
                authority,
                current.State,
                RetainedFrameHandoffPending: false,
                DispositionHandoffPending: false);
        }

        if (transition.LastTerminalOperation is { } terminal &&
            terminal.Authority == authority)
        {
            return new FileTransferRouteTransitionPauseOwnership(
                authority,
                terminal.State,
                terminal.RetainedFrameApplyCommand is not null &&
                    terminal.RetainedFrameApplyReceipt is null,
                terminal.DispositionCommand is not null &&
                    terminal.DispositionReceipt is null);
        }

        return default;
    }

    private static void LogOutboundFileTransferRouteTransitionTransportPauseDecisionLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionAuthority? authority,
        FileTransferRouteTransitionPauseOwnership ownership,
        string status,
        string reason)
    {
        var state = ownership.Authority.IsDefault
            ? "(none)"
            : ownership.State.ToString();
        var key =
            $"{authority?.AuthorityId.ToString() ?? "(none)"}|{authority?.TransitionGeneration ?? 0}|{state}|{status}|{ownership.RetainedFrameHandoffPending}|{ownership.DispositionHandoffPending}";
        if (string.Equals(
                context.FileTransferRouteTransitionTransportPauseEvidenceKey,
                key,
                StringComparison.Ordinal))
        {
            return;
        }

        context.FileTransferRouteTransitionTransportPauseEvidenceKey = key;
        LogFileTransferRouteTransitionTransportPauseDecision(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            state,
            ownership.RetainedFrameHandoffPending,
            ownership.DispositionHandoffPending,
            status,
            reason);
    }

    private static void LogInboundFileTransferRouteTransitionTransportPauseDecisionLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionAuthority? authority,
        FileTransferRouteTransitionPauseOwnership ownership,
        string status,
        string reason)
    {
        var state = ownership.Authority.IsDefault
            ? "(none)"
            : ownership.State.ToString();
        var key =
            $"{authority?.AuthorityId.ToString() ?? "(none)"}|{authority?.TransitionGeneration ?? 0}|{state}|{status}|{ownership.RetainedFrameHandoffPending}|{ownership.DispositionHandoffPending}";
        if (string.Equals(
                context.FileTransferRouteTransitionTransportPauseEvidenceKey,
                key,
                StringComparison.Ordinal))
        {
            return;
        }

        context.FileTransferRouteTransitionTransportPauseEvidenceKey = key;
        LogFileTransferRouteTransitionTransportPauseDecision(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            state,
            ownership.RetainedFrameHandoffPending,
            ownership.DispositionHandoffPending,
            status,
            reason);
    }

    private static void BindOutboundFileTransferRouteTransitionPauseLocked(
        OutboundTransferContext context)
    {
        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = "filetransfer_route_transition_draining";
        context.PullTransportLastPauseReason =
            "filetransfer_route_transition_draining";
        context.PullTransportResumeRequestPending = false;
        context.SparseSenderPumpLastWakeReason =
            "filetransfer_route_transition_draining";
        context.SignalSparseSenderPump();
    }

    private static void BindInboundFileTransferRouteTransitionPauseLocked(
        InboundTransferContext context)
    {
        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = "filetransfer_route_transition_draining";
        context.PullTransportResumeRequestPending = false;
    }

    private void ApplyOutboundFileTransferRouteTransitionTargetRuntimeLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        FileTransferRouteSelection previousRoute,
        FileTransferRouteTransitionDispositionCommand disposition,
        string reason)
    {
        var runtimeSelection =
            FileTransferRuntimeProfileSelection.FromRouteSelection(
                context.RouteSelection);
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        if (authority.TargetRoute == FileTransferRoute.PostTunaFallbackV6)
        {
            context.StatusMessage = "Switching to regular NKN fallback.";
            context.PullTransportRebindGeneration = Math.Max(
                1,
                context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 1);
            InitializeOutboundPostTunaRecoveryForCurrentCoordinatorLegLocked(
                context,
                reason,
                context.PullTransportRebindGeneration);
        }
        else
        {
            ResetOutboundPostTunaFallbackStateForFileTunaV4Locked(context);
            context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
            context.SparseSenderPumpLastWakeReason =
                "coordinator_route_transition_committed";
        }

        LogFileTransferRouteTransitionRuntimeApplied(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            authority,
            previousRoute,
            context.RouteSelection,
            disposition,
            reason);
    }

    private static void ApplyInboundFileTransferRouteTransitionTargetRuntimeLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        FileTransferRouteSelection previousRoute,
        FileTransferRouteTransitionDispositionCommand disposition,
        string reason)
    {
        var runtimeSelection =
            FileTransferRuntimeProfileSelection.FromRouteSelection(
                context.RouteSelection);
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        if (authority.TargetRoute == FileTransferRoute.PostTunaFallbackV6)
        {
            context.StatusMessage = "Switching to regular NKN fallback.";
            EnsureInboundV6DestinationModeForLivePostTunaFallbackLocked(
                context,
                reason);
            var generation = Math.Max(
                1,
                context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 1);
            var frontier = Math.Clamp(
                context.NextChunkIndex,
                0,
                Math.Max(0, context.ChunkCount - 1));
            ResetInboundPostTunaFallbackLegOwnedStateLocked(
                context,
                reason,
                generation,
                frontier);
            context.PullPostTunaRecoveryActive = true;
            context.PullPostTunaRecoveryGeneration = generation;
            context.PullPostTunaRecoveryFrontierChunkIndex = frontier;
            context.PullPostTunaRecoveryStartedUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            ResetInboundPostTunaFallbackStateForFileTunaV4Locked(context);
            context.StatusMessage = GetInboundResumeStatusMessage(context.State);
        }

        LogFileTransferRouteTransitionRuntimeApplied(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            authority,
            previousRoute,
            context.RouteSelection,
            disposition,
            reason);
    }

    private static void ForwardFileTransferRouteTransitionPublication(
        Action<RoutePublicationToken, string>? forwarder,
        RoutePublicationToken? publication,
        string reason)
    {
        if (publication is not { IsDefault: false } token)
        {
            return;
        }

        try
        {
            forwarder?.Invoke(token, reason);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_route_transition_publication_projection_rejected; route_publication_generation={token.PublicationGeneration}; decision_reason={FormatProtocolLogValue($"publication_forward_failed_{NormalizeReason(ex.GetType().Name)}")}");
        }
    }

    private static void LogFileTransferRouteTransitionEvidence(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        PostTunaFallbackPeerProofEvidence? proof,
        PostTunaFallbackPeerProofFrameIdentity? frameIdentity,
        FileTransferRouteTransitionAuthority? authority,
        string status,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_route_transition_peer_proof_observed; authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(authority?.AuthorityId.ToString() ?? "(none)")}; transition_generation={authority?.TransitionGeneration ?? 0}; transition_kind={FormatProtocolLogValue(authority?.Kind.ToString() ?? "(pending)")}; direction={direction.ToString().ToLowerInvariant()}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; proof_generation={proof?.ProofGeneration ?? 0}; proof_transport_epoch={proof?.TransportEpoch ?? frameIdentity?.TransportEpoch ?? 0}; proof_kind={FormatProtocolLogValue(proof?.ProofKind ?? "(none)")}; frame_type={FormatProtocolLogValue(frameIdentity?.FrameType ?? "(none)")}; frame_transport_epoch={frameIdentity?.TransportEpoch ?? 0}; frame_fingerprint={FormatProtocolLogValue(frameIdentity?.Sha256 ?? "(none)")}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}");

    private static void LogFileTransferRouteTransitionSupersessionDecision(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        string phase,
        FileTransferRouteTransitionAuthority predecessor,
        FileTransferRouteTransitionAuthority? successor,
        string status,
        string reason)
    {
        var predecessorFields =
            FormatPrefixedFileTransferRouteTransitionAuthorityFields(
                "predecessor",
                predecessor);
        var successorFields = successor is { } value
            ? FormatPrefixedFileTransferRouteTransitionAuthorityFields(
                "successor",
                value)
            : "; successor_authority_id=(none); successor_predecessor_authority_id=(none); successor_transition_generation=0; successor_transition_kind=(none)";
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_route_transition_supersession_decided; authority_schema={FileTransferAuthoritySchema.Current}; direction={direction.ToString().ToLowerInvariant()}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; decision_phase={FormatProtocolLogValue(phase)}{predecessorFields}{successorFields}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogFileTransferRouteTransitionDecision(
        string eventName,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferRouteTransitionAuthority? authority,
        string status,
        string reason,
        int capturedCount,
        int completedCount,
        FileTransferRouteTransitionPhysicalDrainCommand? drainCommand = null,
        FileTransferRouteTransitionPhysicalDrainReceipt? drainReceipt = null,
        RoutePublicationToken? resultingRoutePublication = null,
        RouteCommitToken? resultingRouteCommit = null)
    {
        var value = authority;
        var checkpoint = value?.DurableCheckpoint;
        var activation =
            value?.ProofBoundary as
                FileTransferTunaActivationTransitionProofBoundary;
        var fallback =
            value?.ProofBoundary as
                FileTransferFallbackTransitionProofBoundary;
        var proofBoundaryKind = activation is not null
            ? "tuna_activation"
            : fallback is not null
                ? "fallback_peer_proof"
                : "(none)";
        var command = drainCommand ?? drainReceipt?.Command;
        var resultingOwner = resultingRoutePublication?.RuntimeOwner;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(value?.AuthorityId.ToString() ?? "(none)")}; predecessor_authority_id={FormatProtocolLogValue(value?.PredecessorTransitionAuthorityId?.ToString() ?? "(none)")}; transition_generation={value?.TransitionGeneration ?? 0}; transition_kind={FormatProtocolLogValue(value?.Kind.ToString() ?? "(none)")}; direction={direction.ToString().ToLowerInvariant()}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; source_route={FormatProtocolLogValue(value is null ? "(none)" : FileTransferRouteResolver.Resolve(value.Value.SourceRoute).TelemetryToken)}; source_protocol_version={value?.SourceProtocolVersion ?? 0}; source_transport={FormatProtocolLogValue(value is null ? "(none)" : FormatFileTransferTransportKind(value.Value.SourceTransport))}; source_live_route_epoch={value?.SourceLiveRouteEpoch ?? 0}; source_runtime_generation={value?.SourceRuntimeOwner.RuntimeGeneration ?? 0}; source_route_publication_generation={value?.SourceRoutePublication.PublicationGeneration ?? 0}; source_route_commit_generation={value?.SourceRouteCommit?.CommitGeneration ?? 0}; source_terminal_generation={value?.SourceTerminalGeneration ?? 0}; source_leg_generation={value?.SourceLegGeneration ?? 0}; source_transport_epoch={value?.SourceTransportEpoch ?? 0}; source_bridge_recovery_generation={value?.SourceBridgeRecoveryGeneration ?? 0}; source_data_session_generation={value?.DataSessionGeneration ?? 0}; target_route={FormatProtocolLogValue(value is null ? "(none)" : FileTransferRouteResolver.Resolve(value.Value.TargetRoute).TelemetryToken)}; target_protocol_version={value?.TargetProtocolVersion ?? 0}; target_transport={FormatProtocolLogValue(value is null ? "(none)" : FormatFileTransferTransportKind(value.Value.TargetTransport))}; handoff_kind={FormatProtocolLogValue(value is null ? "(none)" : FormatFileTransferTransportHandoffKind(value.Value.HandoffKind))}; proof_boundary_kind={proofBoundaryKind}; admission_generation={activation?.RuntimeUnlockAdmission.AdmissionGeneration ?? 0}; pending_precommit_generation={activation?.PendingPreCommitProbe.PendingGeneration ?? 0}; transaction_generation={activation?.PendingPreCommitProbe.TransactionGeneration ?? 0}; offer_generation={activation?.PendingPreCommitProbe.OfferGeneration ?? 0}; launch_generation={activation?.LaunchGeneration ?? 0}; tuna_path_lease_generation={activation?.PendingPreCommitProbe.TunaPathLeaseGeneration ?? 0}; retry_install_generation={activation?.EffectiveRetryInstallReceipt?.InstallGeneration ?? 0}; fallback_yield_authority_id={FormatProtocolLogValue(activation?.FallbackTailPairedReleaseReceipt?.ReleaseAuthorityId.ToString() ?? "(none)")}; fallback_yield_generation={activation?.FallbackTailPairedReleaseReceipt?.ReleaseGeneration ?? 0}; peer_proof_generation={fallback?.PeerProofEvidence.ProofGeneration ?? 0}; peer_proof_transport_epoch={fallback?.PeerProofEvidence.TransportEpoch ?? 0}; peer_proof_kind={FormatProtocolLogValue(fallback?.PeerProofEvidence.ProofKind ?? "(none)")}; retained_frame_fingerprint={FormatProtocolLogValue(fallback?.RetainedFrameIdentity.Sha256 ?? "(none)")}; target_transport_epoch={fallback?.TargetTransportEpoch ?? 0}; checkpoint_generation={checkpoint?.CheckpointGeneration ?? 0}; checkpoint_state_epoch={checkpoint?.StateEpoch ?? 0}; checkpoint_contiguous_frontier={checkpoint?.ContiguousCommittedChunkIndex ?? 0}; checkpoint_durable_highest={checkpoint?.DurableHighestChunkIndex ?? -1}; checkpoint_chunk_count={checkpoint?.ChunkCount ?? 0}; captured_through_send_lease_generation={command?.CapturedThroughPhysicalSendGeneration ?? 0}; captured_send_count={capturedCount}; captured_send_fingerprint={FormatProtocolLogValue(command?.CapturedPhysicalSendFingerprint ?? "(none)")}; completed_send_count={completedCount}; completed_send_fingerprint={FormatProtocolLogValue(drainReceipt?.CompletedPhysicalSendFingerprint ?? "(none)")}; drain_outcome={FormatProtocolLogValue(drainReceipt?.Outcome.ToString() ?? "(none)")}; resulting_live_route_epoch={resultingRoutePublication?.LiveRouteEpoch ?? 0}; resulting_runtime_generation={resultingOwner?.RuntimeGeneration ?? 0}; resulting_terminal_generation={resultingRoutePublication?.TerminalGeneration ?? 0}; resulting_leg_generation={resultingRoutePublication?.LegGeneration ?? 0}; resulting_route_commit_generation={resultingRouteCommit?.CommitGeneration ?? resultingRoutePublication?.RouteCommitGeneration ?? 0}; resulting_route_publication_generation={resultingRoutePublication?.PublicationGeneration ?? 0}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogFileTransferRouteTransitionTransportPauseDecision(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferRouteTransitionAuthority? authority,
        string coordinatorState,
        bool retainedFrameHandoffPending,
        bool dispositionHandoffPending,
        string status,
        string reason)
    {
        var authorityFields = authority is { } value
            ? FormatFileTransferRouteTransitionAuthorityFields(value)
            : "; authority_schema=0; authority_id=(none); predecessor_authority_id=(none); transition_generation=0; transition_kind=(none)";
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_route_transition_transport_pause_decided{authorityFields}; direction={direction.ToString().ToLowerInvariant()}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; coordinator_state={FormatProtocolLogValue(coordinatorState)}; retained_frame_handoff_pending={(retainedFrameHandoffPending ? 1 : 0)}; disposition_handoff_pending={(dispositionHandoffPending ? 1 : 0)}; decision_status={FormatProtocolLogValue(status)}; grace_policy=coordinator_disposition_only; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogFileTransferRouteTransitionRetainedFrame(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferRouteTransitionAuthority? authority,
        PostTunaFallbackPeerProofFrameIdentity? identity,
        string status,
        string reason)
    {
        var authorityFields = authority is { } value
            ? FormatFileTransferRouteTransitionAuthorityFields(value)
            : "; authority_schema=0; authority_id=(none); transition_generation=0; transition_kind=(none)";
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={(string.Equals(status, "Applied", StringComparison.Ordinal) ? "filetransfer_route_transition_retained_frame_applied" : "filetransfer_route_transition_retained_frame_rejected")}{authorityFields}; direction={direction.ToString().ToLowerInvariant()}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; frame_type={FormatProtocolLogValue(identity?.FrameType ?? "(none)")}; frame_transport_epoch={identity?.TransportEpoch ?? 0}; frame_fingerprint={FormatProtocolLogValue(identity?.Sha256 ?? "(none)")}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogFileTransferRouteTransitionRuntimeApplied(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferRouteTransitionAuthority authority,
        FileTransferRouteSelection previousRoute,
        FileTransferRouteSelection currentRoute,
        FileTransferRouteTransitionDispositionCommand disposition,
        string reason)
    {
        var authorityFields =
            FormatFileTransferRouteTransitionAuthorityFields(authority);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_route_transition_committed{authorityFields}; direction={direction.ToString().ToLowerInvariant()}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; projected_source_route={FormatProtocolLogValue(previousRoute.TelemetryToken)}; projected_target_route={FormatProtocolLogValue(currentRoute.TelemetryToken)}; target_live_route_epoch={disposition.RoutePublicationToken?.LiveRouteEpoch ?? 0}; target_runtime_generation={disposition.RoutePublicationToken?.RuntimeGeneration ?? 0}; target_runtime_family={FormatProtocolLogValue(disposition.RoutePublicationToken?.RuntimeOwner.RuntimeFamily ?? "(none)")}; target_terminal_generation={disposition.RoutePublicationToken?.TerminalGeneration ?? 0}; target_leg_generation={disposition.RoutePublicationToken?.LegGeneration ?? 0}; target_route_commit_generation={disposition.RouteCommitToken?.CommitGeneration ?? 0}; target_route_publication_generation={disposition.RoutePublicationToken?.PublicationGeneration ?? 0}; decision_status=Committed; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static string FormatFileTransferRouteTransitionAuthorityFields(
        FileTransferRouteTransitionAuthority authority)
    {
        var activation =
            authority.ProofBoundary as
                FileTransferTunaActivationTransitionProofBoundary;
        var fallback =
            authority.ProofBoundary as
                FileTransferFallbackTransitionProofBoundary;
        var checkpoint = authority.DurableCheckpoint;
        return
            $"; authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(authority.AuthorityId.ToString())}; predecessor_authority_id={FormatProtocolLogValue(authority.PredecessorTransitionAuthorityId?.ToString() ?? "(none)")}; transition_generation={authority.TransitionGeneration}; transition_kind={FormatProtocolLogValue(authority.Kind.ToString())}; source_route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(authority.SourceRoute).TelemetryToken)}; source_protocol_version={authority.SourceProtocolVersion}; source_transport={FormatProtocolLogValue(FormatFileTransferTransportKind(authority.SourceTransport))}; source_live_route_epoch={authority.SourceLiveRouteEpoch}; source_runtime_generation={authority.SourceRuntimeOwner.RuntimeGeneration}; source_route_publication_generation={authority.SourceRoutePublication.PublicationGeneration}; source_route_commit_generation={authority.SourceRouteCommit?.CommitGeneration ?? 0}; source_terminal_generation={authority.SourceTerminalGeneration}; source_leg_generation={authority.SourceLegGeneration}; source_transport_epoch={authority.SourceTransportEpoch}; source_bridge_recovery_generation={authority.SourceBridgeRecoveryGeneration}; source_data_session_generation={authority.DataSessionGeneration}; target_route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(authority.TargetRoute).TelemetryToken)}; target_protocol_version={authority.TargetProtocolVersion}; target_transport={FormatProtocolLogValue(FormatFileTransferTransportKind(authority.TargetTransport))}; handoff_kind={FormatProtocolLogValue(FormatFileTransferTransportHandoffKind(authority.HandoffKind))}; proof_boundary_kind={FormatProtocolLogValue(activation is not null ? "tuna_activation" : fallback is not null ? "fallback_peer_proof" : "(none)")}; admission_generation={activation?.RuntimeUnlockAdmission.AdmissionGeneration ?? 0}; pending_precommit_generation={activation?.PendingPreCommitProbe.PendingGeneration ?? 0}; transaction_generation={activation?.PendingPreCommitProbe.TransactionGeneration ?? 0}; offer_generation={activation?.PendingPreCommitProbe.OfferGeneration ?? 0}; launch_generation={activation?.LaunchGeneration ?? 0}; tuna_path_lease_generation={activation?.PendingPreCommitProbe.TunaPathLeaseGeneration ?? 0}; retry_install_generation={activation?.EffectiveRetryInstallReceipt?.InstallGeneration ?? 0}; fallback_yield_authority_id={FormatProtocolLogValue(activation?.FallbackTailPairedReleaseReceipt?.ReleaseAuthorityId.ToString() ?? "(none)")}; fallback_yield_generation={activation?.FallbackTailPairedReleaseReceipt?.ReleaseGeneration ?? 0}; peer_proof_generation={fallback?.PeerProofEvidence.ProofGeneration ?? 0}; peer_proof_transport_epoch={fallback?.PeerProofEvidence.TransportEpoch ?? 0}; peer_proof_kind={FormatProtocolLogValue(fallback?.PeerProofEvidence.ProofKind ?? "(none)")}; retained_frame_type={FormatProtocolLogValue(fallback?.RetainedFrameIdentity.FrameType ?? "(none)")}; retained_frame_transport_epoch={fallback?.RetainedFrameIdentity.TransportEpoch ?? 0}; retained_frame_fingerprint={FormatProtocolLogValue(fallback?.RetainedFrameIdentity.Sha256 ?? "(none)")}; target_transport_epoch={fallback?.TargetTransportEpoch ?? 0}; checkpoint_generation={checkpoint.CheckpointGeneration}; checkpoint_state_epoch={checkpoint.StateEpoch}; checkpoint_contiguous_frontier={checkpoint.ContiguousCommittedChunkIndex}; checkpoint_durable_highest={checkpoint.DurableHighestChunkIndex}; checkpoint_chunk_count={checkpoint.ChunkCount}";
    }

    private static string FormatPrefixedFileTransferRouteTransitionAuthorityFields(
        string prefix,
        FileTransferRouteTransitionAuthority authority)
    {
        var activation =
            authority.ProofBoundary as
                FileTransferTunaActivationTransitionProofBoundary;
        var fallback =
            authority.ProofBoundary as
                FileTransferFallbackTransitionProofBoundary;
        var checkpoint = authority.DurableCheckpoint;
        return
            $"; {prefix}_authority_id={FormatProtocolLogValue(authority.AuthorityId.ToString())}; {prefix}_predecessor_authority_id={FormatProtocolLogValue(authority.PredecessorTransitionAuthorityId?.ToString() ?? "(none)")}; {prefix}_transition_generation={authority.TransitionGeneration}; {prefix}_transition_kind={FormatProtocolLogValue(authority.Kind.ToString())}; {prefix}_source_route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(authority.SourceRoute).TelemetryToken)}; {prefix}_source_protocol_version={authority.SourceProtocolVersion}; {prefix}_source_transport={FormatProtocolLogValue(FormatFileTransferTransportKind(authority.SourceTransport))}; {prefix}_source_live_route_epoch={authority.SourceLiveRouteEpoch}; {prefix}_source_runtime_generation={authority.SourceRuntimeOwner.RuntimeGeneration}; {prefix}_source_route_publication_generation={authority.SourceRoutePublication.PublicationGeneration}; {prefix}_source_route_commit_generation={authority.SourceRouteCommit?.CommitGeneration ?? 0}; {prefix}_source_terminal_generation={authority.SourceTerminalGeneration}; {prefix}_source_leg_generation={authority.SourceLegGeneration}; {prefix}_source_transport_epoch={authority.SourceTransportEpoch}; {prefix}_source_bridge_recovery_generation={authority.SourceBridgeRecoveryGeneration}; {prefix}_source_data_session_generation={authority.DataSessionGeneration}; {prefix}_target_route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(authority.TargetRoute).TelemetryToken)}; {prefix}_target_protocol_version={authority.TargetProtocolVersion}; {prefix}_target_transport={FormatProtocolLogValue(FormatFileTransferTransportKind(authority.TargetTransport))}; {prefix}_handoff_kind={FormatProtocolLogValue(FormatFileTransferTransportHandoffKind(authority.HandoffKind))}; {prefix}_admission_generation={activation?.RuntimeUnlockAdmission.AdmissionGeneration ?? 0}; {prefix}_pending_precommit_generation={activation?.PendingPreCommitProbe.PendingGeneration ?? 0}; {prefix}_transaction_generation={activation?.PendingPreCommitProbe.TransactionGeneration ?? 0}; {prefix}_offer_generation={activation?.PendingPreCommitProbe.OfferGeneration ?? 0}; {prefix}_launch_generation={activation?.LaunchGeneration ?? 0}; {prefix}_tuna_path_lease_generation={activation?.PendingPreCommitProbe.TunaPathLeaseGeneration ?? 0}; {prefix}_retry_install_generation={activation?.EffectiveRetryInstallReceipt?.InstallGeneration ?? 0}; {prefix}_fallback_yield_authority_id={FormatProtocolLogValue(activation?.FallbackTailPairedReleaseReceipt?.ReleaseAuthorityId.ToString() ?? "(none)")}; {prefix}_fallback_yield_generation={activation?.FallbackTailPairedReleaseReceipt?.ReleaseGeneration ?? 0}; {prefix}_peer_proof_generation={fallback?.PeerProofEvidence.ProofGeneration ?? 0}; {prefix}_peer_proof_transport_epoch={fallback?.PeerProofEvidence.TransportEpoch ?? 0}; {prefix}_retained_frame_fingerprint={FormatProtocolLogValue(fallback?.RetainedFrameIdentity.Sha256 ?? "(none)")}; {prefix}_target_transport_epoch={fallback?.TargetTransportEpoch ?? 0}; {prefix}_checkpoint_generation={checkpoint.CheckpointGeneration}; {prefix}_checkpoint_state_epoch={checkpoint.StateEpoch}; {prefix}_checkpoint_contiguous_frontier={checkpoint.ContiguousCommittedChunkIndex}; {prefix}_checkpoint_durable_highest={checkpoint.DurableHighestChunkIndex}; {prefix}_checkpoint_chunk_count={checkpoint.ChunkCount}";
    }
}
