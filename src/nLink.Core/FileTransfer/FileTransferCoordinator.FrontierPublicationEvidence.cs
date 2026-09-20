using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    // Passive, opt-in evidence for offline fixtures. None of these objects is an
    // action token, participates in token equality, or changes a runtime decision.
    internal sealed record FrontierPublicationCommit(int Before, int After, int Count,
        int Highest, long Bytes, DateTimeOffset ObservedUtc);

    internal sealed record FrontierPublicationRecentArm(long Generation, int Frontier, int Highest,
        DateTimeOffset ProgressUtc, DateTimeOffset Deadline, DateTimeOffset ObservedUtc);

    internal sealed record FrontierPublicationLifecycleEntry(int Sequence, string Kind,
        string SourceFingerprint, string BindingFingerprint, DateTimeOffset? ObservedUtc, string? Boundary = null);

    internal sealed record FrontierPublicationSourceGraph(int? EpochId, FileTransferRouteSelection? EpochSelection,
        FileTransferTransportHandoffKind? HandoffKind, FileTransferTransportKind? TargetTransport,
        string? EpochState, RoutePublicationToken? EpochPublication, string? LegId, FileTransferLegState? LegState);

    internal sealed record FrontierPublicationLifecycle(string ObservationId, string Coverage, string SourceGraphFingerprint,
        string Outcome, int LastSequence, ImmutableArray<FrontierPublicationLifecycleEntry> Entries)
    {
        public int ContractVersion => 1;
    }

    internal sealed record FrontierPublicationReceipt(
        CheckpointRepairContinuityStamp Repair, FallbackFrontierRepairAuthority RepairAuthority,
        CheckpointRepairContinuityScope Source, long RecentGeneration, long StallGeneration,
        DateTimeOffset RecentDeadline, DateTimeOffset StallDeadline, DateTimeOffset PublicationDeadline,
        ImmutableArray<FrontierPublicationCommit> Commits, FileTransferReceiverStateFrameV6 Frame,
        string FrameFingerprint, DateTimeOffset CapturedUtc, DateTimeOffset CompletedUtc,
        FallbackControlPlaneToken DeliveryToken, FileTransferCoordinatorTransitionEnvelope Completion,
        string Outcome)
    {
        public int ContractVersion => 2;
        public required RuntimeOwnerToken SourceRuntimeOwner { get; init; }
        public required RoutePublicationToken SourcePublication { get; init; }
        public RouteCommitToken? SourceCommit { get; init; }
        public required ImmutableArray<FrontierPublicationRecentArm> RecentArms { get; init; }
        public required DateTimeOffset StallStartedUtc { get; init; }
        public required FrontierPublicationLifecycle Lifecycle { get; init; }
        public required FrontierPublicationSourceGraph SourceGraph { get; init; }
    }

    internal sealed class FrontierPublicationObservation(Func<DateTimeOffset> clock)
    {
        private CheckpointRepairContinuityStamp? repair;
        private FallbackFrontierRepairAuthority repairAuthority;
        private CheckpointRepairContinuityScope? source;
        private RuntimeOwnerToken sourceRuntimeOwner;
        private RoutePublicationToken sourcePublication;
        private RouteCommitToken? sourceCommit;
        private object? dataSession;
        private FileTransferLeg? sourceLeg;
        private LiveRouteEpoch? sourceEpoch;
        private FrontierPublicationSourceGraph? sourceGraph;
        private readonly string observationId = Guid.NewGuid().ToString("N");
        private readonly List<FrontierPublicationLifecycleEntry> lifecycle = [];
        private int frontier;
        private int highest;
        private long bytes;
        private long recentGeneration;
        private DateTimeOffset progressUtc;
        private DateTimeOffset recentDeadline;
        private long stallGeneration;
        private DateTimeOffset stallStarted;
        private DateTimeOffset lastUtc;
        private readonly List<FrontierPublicationCommit> commits = [];
        private readonly List<FrontierPublicationRecentArm> recentArms = [];
        private FileTransferReceiverStateFrameV6? originalFrame;
        private FileTransferReceiverStateFrameV6? frame;
        private string? frameFingerprint;
        private DateTimeOffset capturedUtc;
        private FallbackControlPlaneToken? deliveryToken;
        private int recentArmCount;
        private bool closed;

        internal volatile string Status = "Unobserved";
        internal DateTimeOffset StallDeadline { get; private set; }
        internal DateTimeOffset PublicationDeadline { get; private set; }
        internal FrontierPublicationReceipt? Receipt { get; private set; }
        internal FrontierPublicationLifecycle? Lifecycle { get; private set; }
        internal FrontierPublicationWriterBinding? WriterBinding { get; private set; }
        private bool writerBindingTaken;
        internal bool HasOpenSource => !closed && source is not null;

        internal string? TakeWriterBinding()
        {
            if (WriterBinding is null || writerBindingTaken) { return null; }
            writerBindingTaken = true;
            return WriterBinding.ToLogMessage();
        }

        internal void RepairProgress(FileTransferCoordinatorState state, CheckpointRepairContinuityStamp? stamp,
            FallbackFrontierRepairAuthority authority, object session, int current, int receivedHighest, long committedBytes,
            bool userPaused, bool peerPaused, bool transportPaused)
        {
            if (closed) { return; }
            if (repair is not null)
            {
                if (userPaused || peerPaused || transportPaused)
                { Invalidate("ObservationInvalidated", "observed_pause"); return; }
                if (repair != stamp || repairAuthority != authority || !ReferenceEquals(dataSession, session) ||
                    frontier != current || highest != receivedHighest || bytes != committedBytes)
                { Invalidate("EvidenceIncomplete"); }
                return;
            }
            var scope = CreateCheckpointRepairContinuityScope(state);
            if (stamp is null || state.IsTerminal || state.Direction != FileTransferDirection.Inbound ||
                state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
                state.CurrentRoutePublicationToken is not { } publication ||
                !IsRoutePublicationTokenCurrent(state, publication, out _) ||
                stamp.CoordinatorInstanceId != state.PersistentState.CoordinatorInstanceId ||
                stamp.CurrentScopeFingerprint != scope.ScopeFingerprint || authority.IsDefault ||
                stamp.RepairAuthorityId != authority.AuthorityId.ToString() ||
                stamp.RepairGeneration != authority.DecisionGeneration ||
                stamp.ProtocolRepairRequestId != authority.ProtocolRepairRequestId ||
                stamp.RequestCorrelationId != authority.RequestCorrelationId ||
                authority.SessionId != state.SessionId || authority.TransferId != state.TransferId ||
                authority.Direction != state.Direction || authority.RuntimeOwner != state.CurrentRuntimeOwner ||
                current <= authority.FrontierChunkIndex || receivedHighest < current - 1 || committedBytes <= 0 ||
                state.CurrentDataSessionGeneration <= 0) { return; }
            repair = stamp;
            repairAuthority = authority;
            source = scope;
            sourceRuntimeOwner = authority.RuntimeOwner;
            sourcePublication = publication;
            sourceCommit = state.CurrentRouteCommit;
            dataSession = session;
            sourceGraph = CaptureSourceGraph(state);
            WatchSourceMembers(state);
            frontier = current;
            highest = receivedHighest;
            bytes = committedBytes;
            lastUtc = clock();
            if (closed) { return; }
            Status = "RepairObserved";
            Record("RepairObserved", repair, lastUtc);
            if (userPaused || peerPaused || transportPaused) { Invalidate("ObservationInvalidated", "initial_pause"); }
        }

        internal void ObserveSource(FileTransferCoordinatorState state, object? session, string boundary = "coordinator_source")
        {
            if (closed || source is null) { return; }
            if (state.IsTerminal || state.PersistentState.CoordinatorInstanceId != repair!.CoordinatorInstanceId ||
                state.SessionId != repairAuthority.SessionId || state.TransferId != repairAuthority.TransferId ||
                state.Direction != repairAuthority.Direction ||
                !ReferenceEquals(dataSession, session) ||
                sourceGraph != CaptureSourceGraph(state) ||
                CreateCheckpointRepairContinuityScope(state).ScopeFingerprint != source.ScopeFingerprint)
            {
                Invalidate("ObservationInvalidated", boundary);
            }
            else { WatchSourceMembers(state); }
        }

        private static FrontierPublicationSourceGraph CaptureSourceGraph(FileTransferCoordinatorState state)
            => new(state.CurrentLiveRouteEpoch?.EpochId, state.CurrentLiveRouteEpoch?.RouteSelection,
                state.CurrentLiveRouteEpoch?.HandoffKind, state.CurrentLiveRouteEpoch?.TargetTransport,
                state.CurrentLiveRouteEpoch?.State, state.CurrentLiveRouteEpoch?.RoutePublicationToken,
                state.CurrentLeg?.LegId, state.CurrentLeg?.State);

        private void WatchSourceMembers(FileTransferCoordinatorState state)
        {
            // Coordinator adoption clones this graph. Transfer the passive
            // subscription only after exact source values have been checked.
            if (!ReferenceEquals(sourceLeg, state.CurrentLeg))
            {
                if (sourceLeg is not null) { sourceLeg.FrontierPublicationSourceChanged -= SourceMemberChanged; }
                sourceLeg = state.CurrentLeg;
                if (sourceLeg is not null) { sourceLeg.FrontierPublicationSourceChanged += SourceMemberChanged; }
            }
            if (!ReferenceEquals(sourceEpoch, state.CurrentLiveRouteEpoch))
            {
                if (sourceEpoch is not null) { sourceEpoch.FrontierPublicationSourceChanged -= SourceMemberChanged; }
                sourceEpoch = state.CurrentLiveRouteEpoch;
                if (sourceEpoch is not null) { sourceEpoch.FrontierPublicationSourceChanged += SourceMemberChanged; }
            }
        }

        private void SourceMemberChanged(string member) => Invalidate("ObservationInvalidated", member);

        internal void Invalidate(string reason = "ObservationInvalidated", string boundary = "observation")
        {
            if (closed || repair is null) { return; }
            // Reserve the last slot for closure. Do not evict an earlier record
            // or assign a clock time to a mutation that has no clock observation.
            lifecycle.Add(new(lifecycle.Count + 1, reason, source!.ScopeFingerprint,
                CalculateSnapshotFingerprint(boundary), null, boundary));
            FreezeLifecycle(reason);
            closed = true;
            ReleasePendingReferences();
            Status = reason;
        }

        private void ReleasePendingReferences()
        {
            dataSession = null;
            if (sourceLeg is not null) { sourceLeg.FrontierPublicationSourceChanged -= SourceMemberChanged; }
            if (sourceEpoch is not null) { sourceEpoch.FrontierPublicationSourceChanged -= SourceMemberChanged; }
            sourceLeg = null;
            sourceEpoch = null;
            originalFrame = null;
            frame = null;
            deliveryToken = null;
            commits.Clear();
            recentArms.Clear();
            lifecycle.Clear();
        }

        private bool Record(string kind, object binding, DateTimeOffset? now)
        {
            if (closed) { return false; }
            if (lifecycle.Count >= 63) { Invalidate("EvidenceIncomplete", "lifecycle_capacity"); return false; }
            lifecycle.Add(new(lifecycle.Count + 1, kind, source!.ScopeFingerprint,
                CalculateSnapshotFingerprint(binding), now));
            return true;
        }

        private void FreezeLifecycle(string outcome)
            => Lifecycle = new(observationId, "offline_service_source_mutations_v1", CalculateSnapshotFingerprint(sourceGraph), outcome,
                lifecycle.Count, lifecycle.ToImmutableArray());

        private bool Check(FileTransferCoordinatorState state, object? session, out DateTimeOffset now)
        {
            now = clock();
            ObserveSource(state, session);
            if (closed || source is null) { return false; }
            if (now < lastUtc) { Invalidate("EvidenceIncomplete"); return false; }
            lastUtc = now;
            return true;
        }

        internal void RecentArm(FileTransferCoordinatorState state, object session, long generation,
            int current, int receivedHighest, DateTimeOffset committedUtc, DateTimeOffset deadline)
        {
            if (closed || source is null || stallGeneration > 0) { return; }
            if (!Check(state, session, out var now)) { return; }
            if (generation == recentGeneration && current == frontier && receivedHighest == highest &&
                committedUtc == progressUtc && deadline == recentDeadline) { return; }
            if (current != frontier || generation <= 0 || deadline - committedUtc != TimeSpan.FromSeconds(1) ||
                now >= deadline || ++recentArmCount > 64 || receivedHighest < highest ||
                (recentGeneration > 0 && (generation - recentGeneration != 1 || receivedHighest <= highest ||
                    progressUtc != committedUtc || recentDeadline != deadline)))
            { Invalidate("EvidenceIncomplete"); return; }
            recentGeneration = generation;
            progressUtc = committedUtc;
            recentDeadline = deadline;
            highest = receivedHighest;
            recentArms.Add(new(generation, current, receivedHighest, committedUtc, deadline, now));
            Status = "RecentArmed";
            Record("RecentArmed", recentArms[^1], now);
        }

        internal void StallArm(FileTransferCoordinatorState state, object session, long generation,
            int current, int receivedHighest, DateTimeOffset started, DateTimeOffset deadline)
        {
            if (!Check(state, session, out var now)) { return; }
            if (stallGeneration > 0 || recentGeneration <= 0 || generation <= 0 || current != frontier ||
                receivedHighest < highest || started < recentDeadline || now < started ||
                deadline - started != TimeSpan.FromMilliseconds(750))
            { Invalidate("EvidenceIncomplete"); return; }
            stallGeneration = generation;
            stallStarted = started;
            StallDeadline = deadline;
            highest = receivedHighest;
            Status = "StallCaptured";
            Record("StallCaptured", new { RecentGeneration = recentGeneration, StallGeneration = generation,
                StallStartedUtc = started, StallDeadline = deadline }, now);
        }

        internal void RecentRechecked(long recent, long stall)
        {
            if (closed || source is null) { return; }
            if (recent != recentGeneration || stall != stallGeneration || Status != "StallCaptured")
            { Invalidate("EvidenceIncomplete"); return; }
            Status = "StallArmed";
            Record("StallArmed", new { RecentGeneration = recent, StallGeneration = stall }, null);
        }

        internal void Commit(FileTransferCoordinatorState state, object? session, int before, int after,
            int count, int receivedHighest, long committedBytes)
        {
            if (closed || stallGeneration == 0 || originalFrame is not null) { return; }
            if (!Check(state, session, out var now)) { return; }
            if (Status is not ("StallArmed" or "ProgressObserved") || before != frontier || count < 0 ||
                (long)after - before != count || receivedHighest < highest || committedBytes < bytes ||
                commits.Count + recentArmCount + 5 >= 64 || now < stallStarted)
            { Invalidate("EvidenceIncomplete"); return; }
            if (commits.Count == 0)
            {
                if (count == 0) { highest = receivedHighest; return; }
                if (now >= StallDeadline) { Invalidate("PreDeadlineCommitUnproven"); return; }
                if (now > DateTimeOffset.MaxValue.AddSeconds(-1)) { Invalidate("EvidenceIncomplete"); return; }
                PublicationDeadline = now.AddSeconds(1);
            }
            if (now >= PublicationDeadline) { Invalidate("PublicationOutsideObservationBound"); return; }
            commits.Add(new(before, after, count, receivedHighest, committedBytes, now));
            frontier = after;
            highest = receivedHighest;
            bytes = committedBytes;
            Status = "ProgressObserved";
            Record("ProgressObserved", commits[^1], now);
        }

        internal void Capture(FileTransferCoordinatorState state, object? session, FileTransferReceiverStateFrameV6 candidate)
        {
            if (closed || commits.Count == 0 || originalFrame is not null ||
                candidate.ContiguousCommittedChunkIndex < commits[0].After) { return; }
            // Select the first covering frame before checking its outcome/scope.
            originalFrame = candidate;
            if (!Check(state, session, out var now)) { return; }
            if (candidate.SessionId != state.SessionId || candidate.TransferId != state.TransferId ||
                candidate.Epoch <= 0 || candidate.TransferPaused || candidate.TerminalReady ||
                candidate.TransportEpoch != source!.Boundary.TransportEpoch ||
                candidate.ContiguousCommittedChunkIndex != frontier ||
                candidate.DurableReceivedHighestChunkIndex != highest || candidate.BytesCommitted != bytes)
            { Invalidate("EvidenceIncomplete"); return; }
            if (now >= PublicationDeadline) { Invalidate("PublicationOutsideObservationBound"); return; }
            frame = candidate with { MissingRanges = candidate.MissingRanges.ToImmutableArray() };
            frameFingerprint = CalculateSnapshotFingerprint(frame);
            capturedUtc = now;
            Status = "FrameCaptured";
            Record("FrameCaptured", frame, now);
        }

        internal void BindDelivery(FileTransferCoordinatorState state, object? session,
            FileTransferDataFrame candidate, FallbackControlPlaneToken token)
        {
            if (!ReferenceEquals(originalFrame, candidate) || closed) { return; }
            if (!Check(state, session, out var now) || frame is null) { return; }
            if (deliveryToken is not null || token.IsDefault || token.ControlPlaneKind != FileTransferControlPlaneKind.ReceiverState ||
                token.RuntimeOwner != state.CurrentRuntimeOwner || state.CurrentFallbackControlPlaneToken != token ||
                CalculateSnapshotFingerprint(candidate) != frameFingerprint)
            { Invalidate("EvidenceIncomplete"); return; }
            deliveryToken = token;
            Status = "DeliveryBound";
            Record("DeliveryBound", token, now);
        }

        internal void Complete(FileTransferCoordinatorState state, object? session, FileTransferDataFrame candidate,
            FallbackControlPlaneToken token, FileTransferCoordinatorDecision accepted)
        {
            if (!ReferenceEquals(originalFrame, candidate) || closed) { return; }
            if (!Check(state, session, out var now)) { return; }
            if (frame is null || deliveryToken != token || accepted.FallbackControlPlaneToken != token ||
                !accepted.ControlPlanePeerVisibleProofAccepted || accepted.MutationRejectedReason is not null ||
                accepted.SnapshotEnvelope is null || CalculateSnapshotFingerprint(candidate) != frameFingerprint)
            { Invalidate("EvidenceIncomplete"); return; }
            if (now >= PublicationDeadline) { Invalidate("PublicationOutsideObservationBound"); return; }
            var outcome = now < StallDeadline ? "PublishedBeforeOriginalDeadline" : "PublishedWithinObservationBound";
            if (!Record(outcome, accepted.SnapshotEnvelope, now)) { return; }
            FreezeLifecycle(outcome);
            Receipt = new(repair!, repairAuthority, source!, recentGeneration, stallGeneration, recentDeadline,
                StallDeadline, PublicationDeadline, commits.ToImmutableArray(), frame, frameFingerprint!, capturedUtc,
                now, token, accepted.SnapshotEnvelope, outcome)
            {
                SourceRuntimeOwner = sourceRuntimeOwner,
                SourcePublication = sourcePublication,
                SourceCommit = sourceCommit,
                RecentArms = recentArms.ToImmutableArray(),
                StallStartedUtc = stallStarted,
                Lifecycle = Lifecycle!,
                SourceGraph = sourceGraph!,
            };
            WriterBinding = FrontierPublicationWriterBinding.Create(Receipt);
            closed = true;
            ReleasePendingReferences();
            Status = outcome;
        }

        internal void Reject(FileTransferDataFrame candidate)
        {
            if (ReferenceEquals(originalFrame, candidate)) { Invalidate("PublicationRejected"); }
        }

        internal void Unsupported(FileTransferDataFrame candidate)
        {
            if (ReferenceEquals(originalFrame, candidate)) { Invalidate("EvidenceIncomplete"); }
        }

        internal void EndObservation()
        {
            if (!closed && repair is not null) { Invalidate("EvidenceIncomplete"); }
        }
    }
}
