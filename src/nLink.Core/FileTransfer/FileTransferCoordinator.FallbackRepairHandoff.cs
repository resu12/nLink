using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal readonly record struct FileTransferRepairHandoffRange(int StartChunkIndex, int ChunkCount);

internal sealed record FileTransferFallbackRepairHandoffCommand(
    FileTransferAuthorityId AuthorityId, int Generation, string CoordinatorInstanceId,
    FileTransferFallbackTailYieldScope Source, FileTransferFallbackTailPeerProofReceipt PeerProof);

internal sealed record FileTransferFallbackRepairHandoffCapture(
    FileTransferFallbackRepairHandoffCommand Command, long PauseInstance,
    ImmutableArray<long> InboundWrites, ImmutableArray<long> InboundFeedback,
    FileTransferRouteTransitionDurableCheckpoint Checkpoint,
    ImmutableArray<FileTransferRepairHandoffRange> MissingRanges);

internal sealed record FileTransferFallbackRepairHandoffDrainReceipt(
    FileTransferFallbackRepairHandoffCapture Capture,
    ImmutableArray<long> SettledInboundWrites, ImmutableArray<long> SettledInboundFeedback,
    FileTransferRouteTransitionDurableCheckpoint Checkpoint,
    ImmutableArray<FileTransferRepairHandoffRange> MissingRanges);

internal sealed record FileTransferFallbackRepairHandoffRestoreCommand(
    FileTransferFallbackRepairHandoffCommand Hold, FileTransferRouteTransitionDurableCheckpoint Checkpoint,
    ImmutableArray<FileTransferRepairHandoffRange> MissingRanges);

internal enum FileTransferFallbackRepairHandoffState { Prepared, Captured, Drained, Restored, Adopted }

internal sealed record FileTransferFallbackRepairHandoffOperation(
    FileTransferFallbackRepairHandoffCommand Command,
    FileTransferFallbackRepairHandoffState State = FileTransferFallbackRepairHandoffState.Prepared,
    FileTransferFallbackRepairHandoffCapture? Capture = null,
    FileTransferFallbackRepairHandoffDrainReceipt? DrainReceipt = null,
    FileTransferFallbackRepairHandoffRestoreCommand? RestoreCommand = null,
    FileTransferRouteTransitionDispositionCommand? TunaDisposition = null,
    FileTransferRouteTransitionDispositionCommand? RestoreDisposition = null);

internal sealed record FileTransferFallbackRepairHandoffDecision(
    FileTransferCoordinatorState State, bool Accepted, string Reason,
    FileTransferFallbackRepairHandoffCommand? HoldCommand = null,
    FileTransferFallbackRepairHandoffRestoreCommand? RestoreCommand = null,
    FileTransferCoordinatorTransitionEnvelope? SnapshotEnvelope = null);

internal static partial class FileTransferCoordinator
{
    // Source identity is execution evidence, not permission to hold or release repair ownership.
    internal static FileTransferFallbackTailYieldScope? CaptureInboundFallbackRepairWriteScope(
        FileTransferCoordinatorState state)
        => state.Direction == FileTransferDirection.Inbound &&
            state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
            state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
            IsCurrentPostTunaFallbackLeg(state.CurrentLeg) &&
            TryCreateCurrentFallbackTailYieldScope(state, out var scope) ? scope : null;

    internal static bool IsInboundFallbackRepairWriteScopeCurrent(FileTransferCoordinatorState state,
        FileTransferFallbackTailYieldScope scope)
        => scope.Direction == FileTransferDirection.Inbound &&
            scope.Route == FileTransferRoute.PostTunaFallbackV6 &&
            scope.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
            IsCurrentPostTunaFallbackLeg(state.CurrentLeg) && IsFallbackTailYieldScopeCurrent(state, scope, out _);

    // Holding a source is not a release, runtime-unlock admission, or route commit.
    internal static FileTransferFallbackRepairHandoffDecision PrepareInboundFallbackRepairHandoff(
        FileTransferCoordinatorState state, FileTransferFallbackTailPeerProofReceipt peerProof)
    {
        var survival = state.RuntimeUnlockFallbackSurvivalState;
        var fallback = state.PersistentState.FallbackTailYield;
        var acceptedPeer = fallback.PendingPeerProofBeforeLocalYield ?? fallback.CurrentOperation?.PeerProof;
        var valid = state.Direction == FileTransferDirection.Inbound &&
            state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
            state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
            IsCurrentPostTunaFallbackLeg(state.CurrentLeg) &&
            state.CurrentLeg is { CanSendData: true, CheckpointRequestId: null } &&
            state.CurrentFallbackCheckpointLease is null && !survival.FallbackTerminalized &&
            state.CurrentRuntimeUnlockAdmission is null && state.CurrentRuntimeUnlockPendingPreCommitProbe is null &&
            !survival.CheckpointDeliveryRecoveryPending && !survival.ProofReplayPending &&
            state.CurrentBridgeRecoveryExecutionCommand is null && state.CurrentRecoveryLivenessLease is null &&
            !peerProof.IsDefault && acceptedPeer == peerProof &&
            peerProof.ObservationScope.HasCompleteExecutionScope &&
            IsFallbackTailYieldScopeCurrent(state, peerProof.ObservationScope, out _) &&
            !string.IsNullOrWhiteSpace(state.PersistentState.CoordinatorInstanceId);
        FileTransferFallbackRepairHandoffDecision result;
        if (!valid)
        {
            result = new(state, false, "fallback_repair_handoff_source_rejected");
        }
        else if (fallback.RepairHandoff is { State: FileTransferFallbackRepairHandoffState.Prepared or FileTransferFallbackRepairHandoffState.Captured or FileTransferFallbackRepairHandoffState.Drained } prior)
        {
            var same = IsInboundFallbackRepairHandoffCurrent(state, prior.Command) && prior.Command.PeerProof == peerProof;
            result = new(state, same, same ? "fallback_repair_handoff_already_prepared" : "fallback_repair_handoff_conflict");
        }
        else
        {
            var generation = checked((fallback.RepairHandoff?.Command.Generation ?? 0) + 1);
            var command = new FileTransferFallbackRepairHandoffCommand(
                IssueAuthorityId("fallback_repair_handoff", state, generation), generation,
                state.PersistentState.CoordinatorInstanceId!, peerProof.ObservationScope, peerProof);
            result = new(WithFallbackRepairHandoff(state, new(command)), true,
                "fallback_repair_handoff_prepared", HoldCommand: command);
        }
        return FinalizeFallbackRepairHandoff(state, result, nameof(PrepareInboundFallbackRepairHandoff), peerProof);
    }

    internal static bool IsInboundFallbackRepairHandoffCurrent(
        FileTransferCoordinatorState state, FileTransferFallbackRepairHandoffCommand command)
        => state.PersistentState.FallbackTailYield.RepairHandoff is { State: FileTransferFallbackRepairHandoffState.Prepared or FileTransferFallbackRepairHandoffState.Captured or FileTransferFallbackRepairHandoffState.Drained } hold &&
            hold.Command == command && state.PersistentState.CoordinatorInstanceId == command.CoordinatorInstanceId &&
            state.Direction == FileTransferDirection.Inbound &&
            state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
            state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
            IsCurrentPostTunaFallbackLeg(state.CurrentLeg) &&
            state.CurrentLeg is { CanSendData: true, CheckpointRequestId: null } && state.CurrentFallbackCheckpointLease is null &&
            state.CurrentBridgeRecoveryExecutionCommand is null && state.CurrentRecoveryLivenessLease is null &&
            IsFallbackTailYieldScopeCurrent(state, command.Source, out _);

    internal static FileTransferFallbackRepairHandoffDecision CaptureInboundFallbackRepairHandoff(
        FileTransferCoordinatorState state, FileTransferFallbackRepairHandoffCapture capture)
    {
        var hold = state.PersistentState.FallbackTailYield.RepairHandoff;
        var valid = IsInboundFallbackRepairHandoffCurrent(state, capture.Command) && capture.PauseInstance > 0 &&
            AreHoldIdsValid(capture.InboundWrites) && AreHoldIdsValid(capture.InboundFeedback) &&
            IsRepairHandoffCheckpointCurrent(state, capture.Checkpoint) &&
            IsRepairHandoffGapMapValid(capture.Checkpoint, capture.MissingRanges) && !capture.MissingRanges.IsEmpty &&
            (hold!.State == FileTransferFallbackRepairHandoffState.Prepared ||
                hold.Capture is { } prior && SameRepairHandoffEvidence(prior, capture));
        var result = new FileTransferFallbackRepairHandoffDecision(
            valid && hold!.Capture is null ? WithFallbackRepairHandoff(state,
                hold with { State = FileTransferFallbackRepairHandoffState.Captured, Capture = capture }) : state,
            valid, valid ? "fallback_repair_handoff_captured" : "fallback_repair_handoff_capture_rejected");
        return FinalizeFallbackRepairHandoff(state, result, nameof(CaptureInboundFallbackRepairHandoff), capture);
    }

    internal static FileTransferFallbackRepairHandoffDecision DrainInboundFallbackRepairHandoff(
        FileTransferCoordinatorState state, FileTransferFallbackRepairHandoffDrainReceipt receipt)
    {
        var hold = state.PersistentState.FallbackTailYield.RepairHandoff;
        var captured = receipt.Capture;
        var valid = IsInboundFallbackRepairHandoffCurrent(state, captured.Command) &&
            hold!.Capture is { } retained && SameRepairHandoffEvidence(retained, captured) &&
            AreHoldIdsValid(receipt.SettledInboundWrites) && AreHoldIdsValid(receipt.SettledInboundFeedback) &&
            captured.InboundWrites.SequenceEqual(receipt.SettledInboundWrites) &&
            captured.InboundFeedback.SequenceEqual(receipt.SettledInboundFeedback) &&
            IsRepairHandoffCheckpointCurrent(state, receipt.Checkpoint) &&
            receipt.Checkpoint.ChunkCount == captured.Checkpoint.ChunkCount &&
            receipt.Checkpoint.StateEpoch >= captured.Checkpoint.StateEpoch &&
            receipt.Checkpoint.ContiguousCommittedChunkIndex >= captured.Checkpoint.ContiguousCommittedChunkIndex &&
            receipt.Checkpoint.DurableHighestChunkIndex >= captured.Checkpoint.DurableHighestChunkIndex &&
            IsRepairHandoffGapMapValid(receipt.Checkpoint, receipt.MissingRanges) &&
            AreRepairHandoffGapsRetained(captured.MissingRanges, receipt.MissingRanges) &&
            (!captured.InboundWrites.IsEmpty ||
                receipt.Checkpoint.ContiguousCommittedChunkIndex == captured.Checkpoint.ContiguousCommittedChunkIndex &&
                receipt.Checkpoint.DurableHighestChunkIndex == captured.Checkpoint.DurableHighestChunkIndex &&
                captured.MissingRanges.SequenceEqual(receipt.MissingRanges)) &&
            (hold.DrainReceipt is null || SameRepairHandoffEvidence(hold.DrainReceipt, receipt));
        var result = new FileTransferFallbackRepairHandoffDecision(
            valid && hold!.DrainReceipt is null ? WithFallbackRepairHandoff(state,
                hold with { State = FileTransferFallbackRepairHandoffState.Drained, DrainReceipt = receipt }) : state,
            valid, valid ? "fallback_repair_handoff_drained" : "fallback_repair_handoff_drain_rejected");
        return FinalizeFallbackRepairHandoff(state, result, nameof(DrainInboundFallbackRepairHandoff), receipt);
    }

    internal static FileTransferFallbackRepairHandoffDecision RestoreInboundFallbackRepairHandoff(
        FileTransferCoordinatorState state, FileTransferFallbackRepairHandoffCommand command,
        FileTransferRouteTransitionDispositionCommand? rollback = null)
    {
        var hold = state.PersistentState.FallbackTailYield.RepairHandoff;
        FileTransferFallbackRepairHandoffDecision result;
        if (!CanRestoreInboundFallbackRepairHandoffAfterRetryInvalidation(state, command, rollback) ||
            RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            result = new(state, false, "fallback_repair_handoff_restore_rejected");
        }
        else
        {
            var restore = new FileTransferFallbackRepairHandoffRestoreCommand(command,
                hold!.DrainReceipt!.Checkpoint, hold.DrainReceipt.MissingRanges);
            var restored = WithFallbackRepairHandoff(state, hold with
            { State = FileTransferFallbackRepairHandoffState.Restored, RestoreCommand = restore, RestoreDisposition = rollback });
            if (rollback is not null) { restored = restored with { CurrentRuntimeUnlockAdmission = null, CurrentRuntimeUnlockPendingPreCommitProbe = null }; }
            restored = InvalidateFallbackTailYieldSubstate(restored, "fallback_repair_handoff_restored");
            result = new(restored, true, "fallback_repair_handoff_restored", RestoreCommand: restore);
        }
        return FinalizeFallbackRepairHandoff(state, result, nameof(RestoreInboundFallbackRepairHandoff), command);
    }

    internal static bool CanRestoreInboundFallbackRepairHandoffAfterRetryInvalidation(
        FileTransferCoordinatorState state, FileTransferFallbackRepairHandoffCommand command,
        FileTransferRouteTransitionDispositionCommand? rollback = null)
    {
        var hold = state.PersistentState.FallbackTailYield.RepairHandoff;
        var transition = state.PersistentState.FileTransferRouteTransition.CurrentOperation;
        var closedByRollback = rollback is { Kind: FileTransferRouteTransitionDispositionKind.RolledBack } disposition &&
            state.PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                { State: FileTransferRouteTransitionState.Invalidated, DispositionReceipt: { } applied } terminal &&
            terminal.DispositionCommand == disposition && applied.Command == disposition &&
            applied.Status is FileTransferRouteTransitionDispositionApplyStatus.Applied or FileTransferRouteTransitionDispositionApplyStatus.Idempotent &&
            DoesFallbackRepairHandoffMatchTransition(hold, disposition.Authority) &&
            disposition.Authority.ProofBoundary is FileTransferTunaActivationTransitionProofBoundary activation &&
            (state.CurrentRuntimeUnlockAdmission is null || state.CurrentRuntimeUnlockAdmission == activation.RuntimeUnlockAdmission) &&
            (state.CurrentRuntimeUnlockPendingPreCommitProbe is null || state.CurrentRuntimeUnlockPendingPreCommitProbe == activation.PendingPreCommitProbe);
        return IsInboundFallbackRepairHandoffCurrent(state, command) && hold!.DrainReceipt is not null &&
            (rollback is not null ? closedByRollback : state.CurrentRuntimeUnlockAdmission is null && state.CurrentRuntimeUnlockPendingPreCommitProbe is null) &&
            transition?.State is not (FileTransferRouteTransitionState.Prepared or FileTransferRouteTransitionState.Draining);
    }

    internal static bool HasDrainedInboundFallbackRepairHandoff(FileTransferCoordinatorState state)
        => state.PersistentState.FallbackTailYield.RepairHandoff is
            { State: FileTransferFallbackRepairHandoffState.Drained, DrainReceipt: { } receipt } hold &&
            IsInboundFallbackRepairHandoffCurrent(state, hold.Command) &&
            IsRepairHandoffCheckpointCurrent(state, receipt.Checkpoint) &&
            !receipt.MissingRanges.IsEmpty;

    private static bool IsInboundFallbackRepairHandoffLocalYieldAllowed(FileTransferCoordinatorState state)
        => state.PersistentState.FallbackTailYield.RepairHandoff is not
            { State: FileTransferFallbackRepairHandoffState.Prepared or FileTransferFallbackRepairHandoffState.Captured or FileTransferFallbackRepairHandoffState.Drained } ||
            HasDrainedInboundFallbackRepairHandoff(state);

    private static bool DoesFallbackRepairHandoffMatchTransition(FileTransferFallbackRepairHandoffOperation? hold,
        FileTransferRouteTransitionAuthority authority)
        => hold is { State: FileTransferFallbackRepairHandoffState.Drained, DrainReceipt: { } receipt } &&
            authority.Direction == FileTransferDirection.Inbound &&
            authority.Kind == FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4 &&
            authority.TargetRoute == FileTransferRoute.FileTunaV4 && authority.TargetProtocolVersion == 4 &&
            authority.SourceRoute == FileTransferRoute.PostTunaFallbackV6 && authority.SourceProtocolVersion == 6 &&
            authority.DurableCheckpoint == receipt.Checkpoint;

    private static bool IsFallbackRepairHandoffTransitionCommitAllowed(FileTransferCoordinatorState state,
        FileTransferRouteTransitionAuthority authority)
        => state.PersistentState.FallbackTailYield.RepairHandoff is not
            { State: FileTransferFallbackRepairHandoffState.Prepared or FileTransferFallbackRepairHandoffState.Captured or FileTransferFallbackRepairHandoffState.Drained } hold ||
            HasDrainedInboundFallbackRepairHandoff(state) && DoesFallbackRepairHandoffMatchTransition(hold, authority);

    private static FileTransferCoordinatorState RecordInboundFallbackRepairHandoffSuccessor(FileTransferCoordinatorState before,
        FileTransferCoordinatorState committed, FileTransferRouteTransitionDispositionCommand disposition)
        => DoesFallbackRepairHandoffMatchTransition(before.PersistentState.FallbackTailYield.RepairHandoff, disposition.Authority)
            ? WithFallbackRepairHandoff(committed, before.PersistentState.FallbackTailYield.RepairHandoff! with
                { State = FileTransferFallbackRepairHandoffState.Adopted, TunaDisposition = disposition }) : committed;

    private static bool IsRepairHandoffCheckpointCurrent(
        FileTransferCoordinatorState state, FileTransferRouteTransitionDurableCheckpoint checkpoint)
        => !checkpoint.IsDefault && IsBootstrapHoldCheckpointSourceCurrent(state, checkpoint) &&
            checkpoint.CheckpointGeneration == state.CurrentLeg?.CheckpointGeneration;

    private static bool IsRepairHandoffGapMapValid(FileTransferRouteTransitionDurableCheckpoint checkpoint,
        ImmutableArray<FileTransferRepairHandoffRange> ranges)
    {
        if (ranges.IsDefault) { return false; }
        if (ranges.IsEmpty) { return checkpoint.ContiguousCommittedChunkIndex == checkpoint.ChunkCount; }
        if (ranges[0].StartChunkIndex != checkpoint.ContiguousCommittedChunkIndex) { return false; }
        long previousEnd = -1;
        foreach (var range in ranges)
        {
            var end = (long)range.StartChunkIndex + range.ChunkCount;
            if (range.StartChunkIndex <= previousEnd || range.ChunkCount <= 0 || end > checkpoint.ChunkCount) { return false; }
            previousEnd = end;
        }
        // The exact complement of the missing ranges is the retained written map.
        var last = ranges[^1];
        var highestWritten = (long)last.StartChunkIndex + last.ChunkCount == checkpoint.ChunkCount
            ? last.StartChunkIndex - 1 : checkpoint.ChunkCount - 1;
        return checkpoint.DurableHighestChunkIndex == highestWritten;
    }

    private static bool AreRepairHandoffGapsRetained(ImmutableArray<FileTransferRepairHandoffRange> captured,
        ImmutableArray<FileTransferRepairHandoffRange> remaining)
    {
        var index = 0;
        foreach (var range in remaining)
        {
            while (index < captured.Length && (long)captured[index].StartChunkIndex + captured[index].ChunkCount <= range.StartChunkIndex) { index++; }
            if (index == captured.Length || range.StartChunkIndex < captured[index].StartChunkIndex ||
                (long)range.StartChunkIndex + range.ChunkCount > (long)captured[index].StartChunkIndex + captured[index].ChunkCount) { return false; }
        }
        return true;
    }

    private static bool SameRepairHandoffEvidence(object first, object second)
        => CalculateSnapshotFingerprint(first) == CalculateSnapshotFingerprint(second);

    private static FileTransferCoordinatorState WithFallbackRepairHandoff(FileTransferCoordinatorState state,
        FileTransferFallbackRepairHandoffOperation operation)
        => ApplyFallbackTailYieldSubstate(state, state.PersistentState.FallbackTailYield with { RepairHandoff = operation });

    private static FileTransferFallbackRepairHandoffDecision FinalizeFallbackRepairHandoff(
        FileTransferCoordinatorState before, FileTransferFallbackRepairHandoffDecision decision, string kind, object attempt)
    {
        if (string.IsNullOrWhiteSpace(before.PersistentState.CoordinatorInstanceId)) { return decision; }
        var attemptIdentity = "attempt:" + CalculateSnapshotFingerprint(attempt);
        var attempted = decision.Accepted ? ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot>.Empty :
            ImmutableArray.Create(CreateOperationTransitionSnapshot(new CoordinatorOperationDescriptor(
                "fallback_tail_yield", "RepairHandoffAttempt", attemptIdentity, "(none)",
                1, "Rejected", "(none)", 0, CalculateSnapshotFingerprint(attempt), "(none)", 0, "Rejected", "(none)", false,
                OperationRole: "repair_handoff_attempt", OperationKey: CreateCoordinatorOperationKey("fallback_tail_yield",
                    "repair_handoff_attempt", attemptIdentity, 1, "RepairHandoffAttempt")), "None", "Rejected", false));
        var snapshot = CreateTransitionSnapshot(before, decision.State, before.PersistentState.CoordinatorInstanceId!,
            before.PersistentState.SnapshotStateGeneration, kind,
            !decision.Accepted ? "Rejected" : ReferenceEquals(before, decision.State) ? "Idempotent" : "Accepted", decision.Reason, attempted);
        return decision with { State = snapshot.State, SnapshotEnvelope = snapshot.Envelope };
    }

    private static void AddFallbackRepairHandoffDescriptor(FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        if (state.PersistentState.FallbackTailYield.RepairHandoff is not { } hold) { return; }
        var command = hold.Command;
        var identity = command.AuthorityId.Value;
        var receipts = ImmutableArray.CreateBuilder<FileTransferCoordinatorReceiptTransitionSnapshot>();
        AddReceipt("hold", "Prepared", command);
        if (hold.Capture is { } capture) { AddReceipt("capture", "Captured", capture); }
        if (hold.DrainReceipt is { } drain) { AddReceipt("drain", "Drained", drain); }
        if (hold.RestoreCommand is { } restore) { AddReceipt("restore", "Restored", restore); }
        if (hold.TunaDisposition is { } tuna) { AddReceipt("adopt", "Adopted", tuna); }
        var last = receipts[^1];
        var disposition = hold.TunaDisposition ?? hold.RestoreDisposition;
        builder.Add(new("fallback_tail_yield", "SparseRepairHandoff", identity, command.PeerProof.Proof.AuthorityId.Value,
            command.Generation, hold.State.ToString(), identity, command.Generation, CalculateSnapshotFingerprint(command),
            last.ReceiptIdentity, last.ReceiptGeneration, last.ReceiptStatus, last.ReceiptFingerprint, true,
            OperationRole: "repair_handoff", OperationKey: CreateCoordinatorOperationKey("fallback_tail_yield",
                "repair_handoff", identity, command.Generation, "SparseRepairHandoff"),
            FallbackRepairHandoff: new(CreateFallbackTailYieldScopeBoundary(command.Source),
                command.PeerProof.ObservationIdentity, CalculateSnapshotFingerprint(command.PeerProof), hold.Capture?.PauseInstance ?? 0,
                hold.Capture?.InboundWrites ?? [], hold.Capture?.InboundFeedback ?? [],
                hold.DrainReceipt?.SettledInboundWrites ?? [], hold.DrainReceipt?.SettledInboundFeedback ?? [],
                Checkpoint(hold.Capture?.Checkpoint), hold.Capture?.MissingRanges ?? [],
                Checkpoint(hold.DrainReceipt?.Checkpoint), hold.DrainReceipt?.MissingRanges ?? [],
                Checkpoint(hold.RestoreCommand?.Checkpoint), hold.RestoreCommand?.MissingRanges ?? [],
                disposition?.Kind.ToString() ?? "(none)", disposition?.Authority.AuthorityId.Value ?? "(none)",
                disposition?.Authority.TransitionGeneration ?? 0, CalculateSnapshotFingerprint(disposition), receipts.ToImmutable())));

        void AddReceipt(string stage, string status, object evidence)
            => receipts.Add(new(stage, identity, command.Generation, CalculateSnapshotFingerprint(command),
                identity + ":" + status, command.Generation, status, "fallback_repair_handoff_" + stage,
                CalculateSnapshotFingerprint(evidence), true));
        static FileTransferCoordinatorRepairHandoffCheckpointSnapshot? Checkpoint(FileTransferRouteTransitionDurableCheckpoint? checkpoint)
            => checkpoint is { } value ? new(value.StateEpoch, value.ContiguousCommittedChunkIndex, value.DurableHighestChunkIndex,
                value.ChunkCount, value.CheckpointGeneration, CalculateSnapshotFingerprint(value)) : null;
    }
}
