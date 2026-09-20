using System.Collections.Immutable;
using System.Diagnostics;
using Stamp = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryStamp;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    // Bracket the existing runtime UTC acquisition, without replacing its value.
    private static DateTimeOffset ReadPublicationBoundaryUtc(InboundTransferContext context)
        => context.FrontierBoundaryObservation?.ReadUtc() ?? DateTimeOffset.UtcNow;

    // Private, opt-in diagnostic capture. The original app catalog can own its
    // writer handoff; none of these bindings grants runtime authority.
    private sealed partial class FrontierBoundaryObservation
    {
        private sealed record Entry(string Kind, object? Binding, Stamp? Clock);
        private sealed record WriteBoundary(int Before, int After, int Count, int Highest, long Bytes, string Path, Stamp Clock);
        private readonly List<Entry> entries = [];
        private readonly Guid observationId = Guid.NewGuid();
        private readonly Guid clockId = Guid.NewGuid();
        private readonly int processId;
        private readonly long processCreation;
        private readonly long frequency;
        private long sequence;
        private bool closed;
        private readonly object? serviceGate;
        public bool IsClosed => closed;
        private object? source;
        private IFileTransferDataSession? session;
        private FileTransferLeg? watchedLeg;
        private LiveRouteEpoch? watchedEpoch;
        private WriteBoundary? progress;
        private CheckpointRepairContinuityStamp? capturedRepair;
        private WriteBoundary? firstWrite;
        private InboundV6FrontierRecentProgressGraceWake? recent;
        private InboundV6FrontierStallGraceWake? root;
        private InboundV6FrontierStallGraceWake? tip;
        private InboundV6FrontierStallGraceWake? recheckedTip;
        private InboundV6FrontierStallGraceWake? writeTip;
        private Stamp? stallClock;
        private FileTransferReceiverStateFrameV6? originalFrame;
        private FileTransferReceiverStateFrameV6? frozenFrame;
        private ImmutableArray<FileTransferRangeV4> missingRanges;
        private Stamp? frameClock;
        private Stamp? bindingClock;
        private FallbackControlPlaneToken? token;
        private Stamp? lastAcquired;

        public string Status { get; private set; } = "Unobserved";
        public bool LiveProofEligible => false;
        public int EntryCount => entries.Count;
        public FileTransferCoordinator.FrontierPublicationTimingResult? Timing { get; private set; }
        internal Action? AfterStallForTesting { get; set; }

        public bool MatchesProcess(int id, long creation) => processId == id && processCreation == creation;

        public FrontierBoundaryObservation() : this(null) { }

        public FrontierBoundaryObservation(FrontierPayloadCatalog? catalog) : this(catalog, null) { }

        public FrontierBoundaryObservation(FrontierPayloadCatalog? catalog, object? serviceGate)
        {
            payloadCatalog = catalog;
            this.serviceGate = serviceGate;
            try
            {
                using var process = Process.GetCurrentProcess();
                processId = process.Id;
                processCreation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                frequency = Stopwatch.Frequency;
                if (!OperatingSystem.IsWindows() || !Stopwatch.IsHighResolution || frequency <= 0)
                { Close("UnsupportedClock"); }
            }
            catch (Exception) { Close("UnsupportedClock"); }
        }

        public void CloseForDrain()
        {
            if (serviceGate is null) { return; }
            // Serialize with actual capture callbacks, then prepare outside the
            // service lock. Closed positive observations retain their outcome.
            lock (serviceGate) { Close("EvidenceIncomplete"); }
        }

        public DateTimeOffset ReadUtc()
        {
            long before = 0;
            lastAcquired = null;
            try { if (!closed) { before = Stopwatch.GetTimestamp(); } }
            catch (Exception) { Close("ClockCaptureFailed"); }
            var utc = DateTimeOffset.UtcNow;
            try
            {
                if (!closed)
                {
                    var after = Stopwatch.GetTimestamp();
                    lastAcquired = new(processId, processCreation, clockId, observationId,
                        checked(++sequence), frequency, utc.UtcTicks, before, after);
                }
            }
            catch (Exception) { Close("ClockCaptureFailed"); }
            return utc;
        }

        // Value-only projection: no hashing/serialization or authority evaluation.
        private static object Source(InboundTransferContext c) => new
        {
            c.SessionId, c.TransferId, c.RouteSelection, c.TerminalGeneration, c.CurrentRuntimeOwner,
            c.RuntimeUnlockRouteCommitToken, c.CurrentRoutePublicationToken, c.CurrentFallbackCheckpointLease,
            c.CoordinatorPersistentState.CoordinatorInstanceId, c.CurrentTransportEpochDecision, c.DataSessionGeneration,
            Bridge = c.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration,
            Epoch = (c.CurrentLiveRouteEpoch?.EpochId, c.CurrentLiveRouteEpoch?.State,
                c.CurrentLiveRouteEpoch?.RouteSelection, c.CurrentLiveRouteEpoch?.RoutePublicationToken,
                c.CurrentLiveRouteEpoch?.HandoffKind, c.CurrentLiveRouteEpoch?.TargetTransport),
            Leg = (c.CurrentTransferLeg?.LegId, c.CurrentTransferLeg?.Generation, c.CurrentTransferLeg?.State,
                c.CurrentTransferLeg?.CanSendData, c.CurrentTransferLeg?.RouteSelection, c.CurrentTransferLeg?.ProtocolVersion,
                c.CurrentTransferLeg?.LiveRouteEpochId, c.CurrentTransferLeg?.TransportEpochId,
                c.CurrentTransferLeg?.BridgeRecoveryGeneration, c.CurrentTransferLeg?.CheckpointGeneration,
                c.CurrentTransferLeg?.CheckpointRequestId),
        };

        public void ObserveSource(InboundTransferContext context) => Run(context, () => { });

        private void Run(InboundTransferContext context, Action action)
        {
            if (closed) { return; }
            try
            {
                if (source is not null)
                {
                    if (context.IsTerminal || context.UserPaused || context.PeerPaused || context.PullTransportPaused ||
                        !ReferenceEquals(session, context.DataSession) || !source.Equals(Source(context)))
                    { Close("ObservationInvalidated"); return; }
                    Watch(context);
                }
                action();
            }
            catch (Exception) { Close("EvidenceIncomplete"); }
        }

        private void Watch(InboundTransferContext context)
        {
            if (!ReferenceEquals(watchedLeg, context.CurrentTransferLeg))
            {
                if (watchedLeg is not null) { watchedLeg.FrontierPublicationSourceChanged -= SourceChanged; }
                watchedLeg = context.CurrentTransferLeg;
                if (watchedLeg is not null) { watchedLeg.FrontierPublicationSourceChanged += SourceChanged; }
            }
            if (!ReferenceEquals(watchedEpoch, context.CurrentLiveRouteEpoch))
            {
                if (watchedEpoch is not null) { watchedEpoch.FrontierPublicationSourceChanged -= SourceChanged; }
                watchedEpoch = context.CurrentLiveRouteEpoch;
                if (watchedEpoch is not null) { watchedEpoch.FrontierPublicationSourceChanged += SourceChanged; }
            }
        }

        private void SourceChanged(string _) => Close("ObservationInvalidated");

        public void Close(string reason = "ObservationInvalidated")
        {
            if (closed) { return; }
            closed = true;
            Status = reason;
            if (entries.Count < 64) { entries.Add(new(reason, null, null)); }
            if (watchedLeg is not null) { watchedLeg.FrontierPublicationSourceChanged -= SourceChanged; }
            if (watchedEpoch is not null) { watchedEpoch.FrontierPublicationSourceChanged -= SourceChanged; }
            watchedLeg = null;
            watchedEpoch = null;
            session = null;
            originalFrame = null;
            lastAcquired = null;
            AfterStallForTesting = null;
        }

        private void Add(string kind, object binding, Stamp? stamp = null)
        {
            if (closed) { return; }
            if (entries.Count >= 63) { Close("EvidenceIncomplete"); return; }
            entries.Add(new(kind, binding, stamp));
            Status = kind;
        }

        public void Write(InboundTransferContext c, int before, int count, string path) => Run(c, () =>
        {
            if (lastAcquired is not { } clock || clock.UtcTicks != c.PullLastProgressUtc?.UtcTicks ||
                c.NextChunkIndex - before != count || count < 0)
            { Close("EvidenceIncomplete"); return; }
            var write = new WriteBoundary(before, c.NextChunkIndex, count, c.PullHighestReceivedChunkIndex,
                c.BytesTransferred, path, clock);
            if (source is null) { if (count > 0) { progress = write; } return; }
            if (originalFrame is not null) { return; }
            if (count == 0 && firstWrite is null) { return; }
            if (root is null || recheckedTip is null || recent is null || progress is null)
            { Close("EvidenceIncomplete"); return; }
            if (firstWrite is null)
            {
                if (before != root.FrontierChunkIndex || count <= 0 ||
                    !ReferenceEquals(tip, c.CurrentV6FrontierStallGraceWake))
                { Close("EvidenceIncomplete"); return; }
                firstWrite = write;
                writeTip = tip;
            }
            Add("Write", write, clock);
        });

        public void Repair(InboundTransferContext c, InboundFallbackFrontierProgressReceipt receipt) => Run(c, () =>
        {
            if (source is not null) { Close("EvidenceIncomplete"); return; }
            var continuity = GetCheckpointRepairContinuityStamp(receipt);
            if (progress is null || progress.Before != receipt.PreviousFrontierChunkIndex ||
                progress.After != receipt.CurrentFrontierChunkIndex || progress.After != c.NextChunkIndex ||
                progress.Clock.UtcTicks != c.PullLastCommittedProgressUtc?.UtcTicks ||
                !ReferenceEquals(receipt.DataSession, c.DataSession) || continuity is null)
            { Close("EvidenceIncomplete"); return; }
            // Retain the admitted immutable value; the contract may be retired
            // before closed-capture validation runs outside the service gate.
            capturedRepair = continuity;
            source = Source(c);
            session = c.DataSession;
            Watch(c);
            Add("RepairProgress", receipt, progress.Clock);
        });

        public void Recent(InboundTransferContext c, InboundV6FrontierRecentProgressGraceWake wake) => Run(c, () =>
        {
            if (source is null || root is not null) { return; }
            if (progress is null || wake.FrontierChunkIndex != progress.After ||
                wake.CommittedProgressUtc.UtcTicks != progress.Clock.UtcTicks ||
                wake.DueUtc != wake.CommittedProgressUtc.AddSeconds(1))
            { Close("EvidenceIncomplete"); return; }
            if (ReferenceEquals(recent, wake)) { return; }
            if (recent is not null && (recent.WakeGeneration == long.MaxValue ||
                wake.WakeGeneration != recent.WakeGeneration + 1 || wake.HighestReceivedChunkIndex <= recent.HighestReceivedChunkIndex ||
                wake with { WakeGeneration = recent.WakeGeneration, HighestReceivedChunkIndex = recent.HighestReceivedChunkIndex } != recent))
            { Close("EvidenceIncomplete"); return; }
            recent = wake;
            Add("RecentArmed", wake);
        });

        public void Stall(InboundTransferContext c) => Run(c, () =>
        {
            if (source is null || firstWrite is not null) { return; }
            var wake = c.CurrentV6FrontierStallGraceWake;
            if (root is not null || recent is null || wake is null || lastAcquired is not { } clock ||
                clock.UtcTicks != wake.StallStartedUtc.UtcTicks || wake.FrontierChunkIndex != recent.FrontierChunkIndex ||
                wake.StallStartedUtc < recent.DueUtc || wake.DueUtc != wake.StallStartedUtc.AddMilliseconds(750))
            { Close("EvidenceIncomplete"); return; }
            root = tip = wake;
            stallClock = clock;
            Add("StallCaptured", wake, clock);
        });

        public void Successor(InboundTransferContext c, InboundV6FrontierStallGraceWake predecessor,
            InboundV6FrontierStallGraceWake successor)
        {
            // A stale notification is diagnostic-only, even if its alleged source differs.
            if (closed || !ReferenceEquals(c.CurrentV6FrontierStallGraceWake, successor)) { return; }
            Run(c, () =>
            {
                if (source is null || firstWrite is not null) { return; }
                if (ReferenceEquals(tip, successor))
                {
                    if (!entries.Any(e => e.Binding is ValueTuple<InboundV6FrontierStallGraceWake, InboundV6FrontierStallGraceWake> pair &&
                        ReferenceEquals(pair.Item1, predecessor) && ReferenceEquals(pair.Item2, successor)))
                    { Close("EvidenceIncomplete"); }
                    return;
                }
                if (!ReferenceEquals(tip, predecessor) || predecessor.WakeGeneration == long.MaxValue ||
                    successor.WakeGeneration != predecessor.WakeGeneration + 1 ||
                    successor.HighestReceivedChunkIndex <= predecessor.HighestReceivedChunkIndex ||
                    successor with { WakeGeneration = predecessor.WakeGeneration,
                        HighestReceivedChunkIndex = predecessor.HighestReceivedChunkIndex } != predecessor)
                { Close("EvidenceIncomplete"); return; }
                tip = successor;
                Add("StallSuccessor", (predecessor, successor));
            });
        }

        public void Rechecked(InboundTransferContext c, InboundV6FrontierRecentProgressGraceWake wake) => Run(c, () =>
        {
            if (source is null) { return; }
            if (!ReferenceEquals(recent, wake) || tip is null || !ReferenceEquals(tip, c.CurrentV6FrontierStallGraceWake) ||
                recheckedTip is not null || firstWrite is not null)
            { Close("EvidenceIncomplete"); return; }
            recheckedTip = tip;
            Add("RecentRechecked", (wake, tip));
        });

        public void Frame(InboundTransferContext c, FileTransferReceiverStateFrameV6 frame) => Run(c, () =>
        {
            if (firstWrite is null || originalFrame is not null || frame.ContiguousCommittedChunkIndex < firstWrite.After) { return; }
            originalFrame = frame;
            frozenFrame = frame with { };
            missingRanges = frame.MissingRanges.ToImmutableArray();
            ReadUtc();
            frameClock = lastAcquired;
            Add("FrameCaptured", frame, frameClock);
        });

        public void Bind(InboundTransferContext c, FileTransferDataFrame frame, FallbackControlPlaneToken deliveryToken) => Run(c, () =>
        {
            if (!ReferenceEquals(originalFrame, frame)) { return; }
            if (token is not null || deliveryToken.IsDefault) { Close("EvidenceIncomplete"); return; }
            token = deliveryToken;
            ReadUtc();
            bindingClock = lastAcquired;
            Add("DeliveryBound", deliveryToken, bindingClock);
        });

        public void Reject(FileTransferDataFrame frame)
        { if (ReferenceEquals(originalFrame, frame)) { Close("PublicationRejected"); } }

        public void Complete(InboundTransferContext c, FileTransferDataFrame frame,
            FallbackControlPlaneToken deliveryToken, FileTransferCoordinatorDecision accepted) => Run(c, () =>
        {
            if (!ReferenceEquals(originalFrame, frame)) { return; }
            if (frame is not FileTransferReceiverStateFrameV6 current || current != frozenFrame ||
                !current.MissingRanges.SequenceEqual(missingRanges) || token != deliveryToken ||
                accepted.FallbackControlPlaneToken != deliveryToken || !accepted.ControlPlanePeerVisibleProofAccepted ||
                accepted.MutationRejectedReason is not null || accepted.SnapshotEnvelope is null ||
                progress is null || recent is null || root is null || firstWrite is null || writeTip is null ||
                stallClock is null || frameClock is null || bindingClock is null)
            { Close("EvidenceIncomplete"); return; }
            ReadUtc();
            if (lastAcquired is not { } completion) { Close("EvidenceIncomplete"); return; }
            Add("AcceptedCompletion", accepted.SnapshotEnvelope, completion);
            if (closed) { return; }
            var writes = entries.Where(e => e.Kind == "Write").Select(e => e.Clock!.Value).ToArray();
            Timing = FileTransferCoordinator.FrontierPublicationBoundaryTiming.Evaluate(progress.Clock, recent.DueUtc.UtcTicks,
                stallClock.Value, root.DueUtc.UtcTicks, writes, checked(firstWrite.Clock.UtcTicks + TimeSpan.TicksPerSecond),
                frameClock.Value, bindingClock.Value, completion);
            Close(Timing.Outcome);
        });
    }
}
