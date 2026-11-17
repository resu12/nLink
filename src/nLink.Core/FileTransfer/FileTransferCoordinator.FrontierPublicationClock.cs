namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    // Values for pure timing checks, not proof of acquisition or producer custody.
    // No clock reads, observer activation, runtime tokens or mutable clock state.
    internal readonly record struct FrontierPublicationBoundaryStamp(
        int ProcessId, long ProcessCreationFileTime, Guid ClockInstanceId, Guid ObservationId,
        long CaptureSequence, long Frequency, long UtcTicks, long CounterBefore, long CounterAfter);

    internal sealed record FrontierPublicationTimingResult(string Outcome, string Reason)
    {
        public bool LiveProofEligible => false;
    }

    internal static class FrontierPublicationBoundaryTiming
    {
        internal const string Profile = "frontier_publication_boundary_utc_qpc_v1";
        internal const int MaximumStamps = 64;

        // The caller must separately prove actual boundary roles, source/lifecycle,
        // first commit/frame and accepted completion. This checks only their times.
        internal static FrontierPublicationTimingResult Evaluate(
            FrontierPublicationBoundaryStamp recentProgress, long recentDeadlineUtcTicks,
            FrontierPublicationBoundaryStamp originalStallStart, long originalStallDeadlineUtcTicks,
            ReadOnlySpan<FrontierPublicationBoundaryStamp> commits, long publicationDeadlineUtcTicks,
            FrontierPublicationBoundaryStamp frameCapture, FrontierPublicationBoundaryStamp deliveryBinding,
            FrontierPublicationBoundaryStamp completion)
        {
            // The other five stamps also consume this input bound. Full lifecycle
            // capacity and custody remain the caller's separate responsibility.
            if (commits.Length is < 1 or > MaximumStamps - 5)
            {
                return new("EvidenceIncomplete", "timing_capacity");
            }

            var previous = recentProgress;
            if (!IsValid(previous) || !Advance(originalStallStart))
            {
                return new("EvidenceIncomplete", "clock_stamp_invalid");
            }
            foreach (var commit in commits)
            {
                if (!Advance(commit))
                {
                    return new("EvidenceIncomplete", "clock_stamp_invalid");
                }
            }
            if (!Advance(frameCapture) || !Advance(deliveryBinding) || !Advance(completion))
            {
                return new("EvidenceIncomplete", "clock_stamp_invalid");
            }

            var firstCommit = commits[0];
            const long recentTicks = 1000 * TimeSpan.TicksPerMillisecond;
            const long stallTicks = 750 * TimeSpan.TicksPerMillisecond;
            if (recentProgress.UtcTicks > DateTimeOffset.MaxValue.Ticks - recentTicks ||
                originalStallStart.UtcTicks > DateTimeOffset.MaxValue.Ticks - stallTicks ||
                firstCommit.UtcTicks > DateTimeOffset.MaxValue.Ticks - recentTicks)
            {
                return new("EvidenceIncomplete", "utc_deadline_overflow");
            }
            if (recentDeadlineUtcTicks != checked(recentProgress.UtcTicks + recentTicks) ||
                originalStallDeadlineUtcTicks != checked(originalStallStart.UtcTicks + stallTicks) ||
                publicationDeadlineUtcTicks != checked(firstCommit.UtcTicks + recentTicks))
            {
                return new("EvidenceIncomplete", "deadline_mismatch");
            }
            if (originalStallStart.UtcTicks < recentDeadlineUtcTicks ||
                !ElapsedAtLeast(recentProgress, originalStallStart, 1000))
            {
                return new("EvidenceIncomplete", "recent_grace_unproven");
            }
            if (firstCommit.UtcTicks >= originalStallDeadlineUtcTicks)
            {
                return new("PreDeadlineCommitUnproven", "first_commit_not_before_original_deadline");
            }
            if (!ElapsedBefore(originalStallStart, firstCommit, 750))
            {
                return new("EvidenceIncomplete", "pre_deadline_elapsed_unproven");
            }

            // Ordered validation makes completion the largest UTC/counter endpoint.
            // These strict checks therefore bound every intervening sample as well.
            if (completion.UtcTicks >= publicationDeadlineUtcTicks)
            {
                return new("PublicationOutsideObservationBound", "publication_utc_bound_reached");
            }
            if (!ElapsedBefore(firstCommit, completion, 1000))
            {
                return new("EvidenceIncomplete", "publication_elapsed_unproven");
            }
            if (completion.UtcTicks < originalStallDeadlineUtcTicks &&
                ElapsedBefore(originalStallStart, completion, 750))
            {
                return new("PublishedBeforeOriginalDeadline", "timing_before_original_deadline");
            }
            if (completion.UtcTicks >= originalStallDeadlineUtcTicks &&
                ElapsedAtLeast(originalStallStart, completion, 750))
            {
                return new("PublishedWithinObservationBound", "timing_within_observation_bound");
            }
            return new("EvidenceIncomplete", "original_deadline_elapsed_unproven");

            bool Advance(FrontierPublicationBoundaryStamp next)
            {
                if (!IsValid(next) || next.ProcessId != previous.ProcessId ||
                    next.ProcessCreationFileTime != previous.ProcessCreationFileTime ||
                    next.ClockInstanceId != previous.ClockInstanceId || next.ObservationId != previous.ObservationId ||
                    next.Frequency != previous.Frequency || next.CaptureSequence <= previous.CaptureSequence ||
                    next.UtcTicks < previous.UtcTicks || next.CounterBefore < previous.CounterAfter)
                {
                    return false;
                }
                previous = next;
                return true;
            }
        }

        private static bool IsValid(FrontierPublicationBoundaryStamp stamp)
            => stamp.ProcessId > 0 && stamp.ProcessCreationFileTime > 0 &&
               stamp.ProcessCreationFileTime <= DateTimeOffset.MaxValue.ToFileTime() &&
               stamp.ClockInstanceId != Guid.Empty && stamp.ObservationId != Guid.Empty &&
               stamp.CaptureSequence > 0 && stamp.Frequency > 0 &&
               stamp.UtcTicks >= 0 && stamp.UtcTicks <= DateTimeOffset.MaxValue.Ticks &&
               stamp.CounterBefore >= 0 && stamp.CounterAfter >= stamp.CounterBefore;

        private static bool ElapsedBefore(
            FrontierPublicationBoundaryStamp start, FrontierPublicationBoundaryStamp end, int milliseconds)
            => checked(((Int128)end.CounterAfter - start.CounterBefore + 2) * 1000) <
               checked((Int128)milliseconds * start.Frequency);

        private static bool ElapsedAtLeast(
            FrontierPublicationBoundaryStamp start, FrontierPublicationBoundaryStamp end, int milliseconds)
            => checked(Int128.Max(0, (Int128)end.CounterBefore - start.CounterAfter - 2) * 1000) >=
               checked((Int128)milliseconds * start.Frequency);
    }
}
