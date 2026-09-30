#if NLINK_TEST_MEMBERSHIP_BORROWER && NLINK_FRONTIER_LEDGER
using System;
using System.Collections;
using System.Diagnostics;
using System.Management.Automation;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class Worker
        {
            private NativeMembership borrowerMembership;
            private MembershipBorrowerForTest membershipBorrower;
            private int borrowerAttempted;

            internal NativeMembership RetainMembershipForBorrowerForTest(WriterReader reader, SourceReader source,
                int selected, long records, long units, string[] full, string[] original, Stopwatch work,
                int[] positions, string selectedWriter, long selectedSequence)
            {
                borrowerMembership = new NativeMembership(selected, records, units, this, reader, source,
                    full, original, work, positions, selectedWriter, selectedSequence);
                return borrowerMembership;
            }

            // Called only by the opt-in observer inside the original successful
            // witness preparation, after its origin/selection/parity checks.
            public bool BindMembershipBorrowerForTest(WriterReader reader, NativeMembership membership,
                object context, object index, object failure, long records, long units)
            {
                return Object.ReferenceEquals(frontierOriginal, this) &&
                    Object.ReferenceEquals(borrowerMembership, membership) && OwnsVerifiedWriter(reader) &&
                    membership.BindBorrowerInputs(this, reader, context, index, failure, records, units);
            }

            public MembershipBorrowerForTest TryBorrowMembershipForTest(WriterReader reader,
                PrimaryEvaluationCustody custody, CompletedEvaluations pair, object context, object index, object failure,
                string[] full, string[] compact, object fullView, object compactView, Stopwatch work)
            {
                if (!Object.ReferenceEquals(frontierOriginal, this) || borrowerMembership == null ||
                    !Object.ReferenceEquals(primary, custody) || !OwnsVerifiedWriter(reader) ||
                    !borrowerMembership.MatchesBorrowerInputs(this, reader, context, index, failure, full, work) ||
                    !custody.Owns(pair, context, full, compact, fullView, compactView) ||
                    Interlocked.CompareExchange(ref borrowerAttempted, 1, 0) != 0) return null;
                membershipBorrower = new MembershipBorrowerForTest(this, reader, borrowerMembership, custody, pair,
                    context, index, failure, full, compact, fullView, compactView, work);
                return membershipBorrower;
            }

            internal bool OwnsBorrower(MembershipBorrowerForTest value, WriterReader reader,
                NativeMembership membership, PrimaryEvaluationCustody custody)
            {
                return Object.ReferenceEquals(frontierOriginal, this) && Object.ReferenceEquals(membershipBorrower, value) &&
                    Object.ReferenceEquals(borrowerMembership, membership) && Object.ReferenceEquals(primary, custody) &&
                    OwnsVerifiedWriter(reader);
            }
            public bool MembershipBorrowerIssuedForTest { get { return membershipBorrower != null; } }
            public bool MembershipBorrowerRetiredForTest { get { return membershipBorrower == null || !membershipBorrower.IsCurrent; } }
        }

        public sealed partial class NativeMembership
        {
            private readonly NativeMembership borrowerOriginal;
            private readonly Worker borrowerOwner;
            private readonly WriterReader borrowerWriter;
            private readonly SourceReader borrowerSource;
            private readonly string[] borrowerFull, borrowerFullOriginal;
            private readonly Stopwatch borrowerWork;
            private readonly int[] borrowerPositions;
            private readonly string borrowerWriterId;
            private readonly long borrowerSequence;
#if NLINK_TEST_SELECTED_NATIVE_WRITER
            private readonly CollectorPrimaryFilesForTest borrowerCollector;
#endif
            private object borrowerContext, borrowerIndex, borrowerFailure, borrowerEvent;
            private string borrowerClass, borrowerReason;
            private int borrowerSourceLine;
            private long borrowerPriorRecords, borrowerPriorUnits;
#if NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF
            private IList borrowerEvents;
            private int[] borrowerOrigins;
#endif

            internal NativeMembership(int selected, long records, long units, Worker owner, WriterReader writer,
                SourceReader source, string[] full, string[] original, Stopwatch work, int[] positions,
                string selectedWriter, long selectedSequence)
#if NLINK_TEST_SELECTED_NATIVE_WRITER
                : this(selected, records, units, writer, full, original, work, positions)
#else
                : this(selected, records, units)
#endif
            {
                borrowerOriginal = this; borrowerOwner = owner; borrowerWriter = writer; borrowerSource = source;
                borrowerFull = full; borrowerFullOriginal = original; borrowerWork = work; borrowerPositions = positions;
                borrowerWriterId = selectedWriter; borrowerSequence = selectedSequence;
#if NLINK_TEST_SELECTED_NATIVE_WRITER
                borrowerCollector = owner.CollectorOutputsForTest;
#endif
            }
            internal static object Property(object value, string name)
            {
                if (value == null) return null;
                var property = PSObject.AsPSObject(value).Properties[name];
                return property == null ? null : property.Value;
            }
            internal bool BindBorrowerInputs(Worker owner, WriterReader writer, object context, object index,
                object failure, long records, long units)
            {
                if (!Object.ReferenceEquals(borrowerOriginal, this) || borrowerContext != null ||
                    !Object.ReferenceEquals(owner, borrowerOwner) || !Object.ReferenceEquals(writer, borrowerWriter) ||
                    !Object.ReferenceEquals(Property(context, "Work"), borrowerWork) ||
                    !Object.ReferenceEquals(Property(context, "WriterReader"), writer) ||
                    records < RecordVisits || units < CodeUnitVisits || records >= 4096 ||
                    units > LocalPublicationInputLimits.ProducerCodeUnits) return false;
                var events = Property(index, "AllEvents") as IList;
                var origins = Property(context, "FullOrigins") as int[];
                object selectedEvent = Property(failure, "Event");
                if (SelectedIndex < 0 || SelectedIndex >= borrowerFull.Length || events == null || origins == null ||
                    events.Count != borrowerFull.Length || origins.Length != borrowerFull.Length ||
                    !Object.ReferenceEquals(events[SelectedIndex], selectedEvent) ||
                    !Object.ReferenceEquals(Property(selectedEvent, "RawLine"), borrowerFullOriginal[SelectedIndex]) ||
                    !(Property(selectedEvent, "LineNumber") is int) ||
                    (int)Property(selectedEvent, "LineNumber") != origins[SelectedIndex]) return false;
                borrowerContext = context; borrowerIndex = index; borrowerFailure = failure; borrowerEvent = selectedEvent;
                borrowerClass = Property(failure, "Class") as string; borrowerReason = Property(failure, "Reason") as string;
                borrowerSourceLine = origins[SelectedIndex]; borrowerPriorRecords = records; borrowerPriorUnits = units;
#if NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF
                borrowerEvents = events; borrowerOrigins = origins;
#endif
                return MatchesBorrowerInputs(owner, writer, context, index, failure, borrowerFull, borrowerWork);
            }
            internal bool MatchesBorrowerInputs(Worker owner, WriterReader writer, object context, object index,
                object failure, string[] full, Stopwatch work)
            {
#if NLINK_TEST_SELECTED_NATIVE_WRITER
                if (borrowerCollector == null || !Object.ReferenceEquals(owner.CollectorOutputsForTest, borrowerCollector)) return false;
#endif
                if (!Object.ReferenceEquals(borrowerOriginal, this) || borrowerContext == null ||
                    !Object.ReferenceEquals(owner, borrowerOwner) || !Object.ReferenceEquals(writer, borrowerWriter) ||
                    !Object.ReferenceEquals(context, borrowerContext) || !Object.ReferenceEquals(index, borrowerIndex) ||
                    !Object.ReferenceEquals(failure, borrowerFailure) || !Object.ReferenceEquals(full, borrowerFull) ||
                    !Object.ReferenceEquals(work, borrowerWork) || work.ElapsedMilliseconds > 20000 ||
                    !owner.OwnsVerifiedWriter(writer) || !owner.OwnsCompletedSource(borrowerSource) ||
                    !Object.ReferenceEquals(Property(context, "SourceOwner"), owner) ||
                    !Object.ReferenceEquals(Property(context, "SourceReader"), borrowerSource) ||
                    !Object.ReferenceEquals(Property(context, "WriterReader"), writer) ||
                    !Object.ReferenceEquals(Property(context, "FullLines"), full) ||
                    !Object.ReferenceEquals(Property(context, "FullOriginal"), borrowerFullOriginal) ||
                    !Object.ReferenceEquals(Property(context, "Work"), work) ||
                    !Object.ReferenceEquals(Property(failure, "Event"), borrowerEvent) ||
                    !Object.Equals(Property(failure, "Class"), borrowerClass) || !Object.Equals(Property(failure, "Reason"), borrowerReason)) return false;
                var events = Property(index, "AllEvents") as IList;
                var origins = Property(context, "FullOrigins") as int[];
#if NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF
                if (!Object.ReferenceEquals(events, borrowerEvents) || !Object.ReferenceEquals(origins, borrowerOrigins)) return false;
#endif
                return events != null && events.Count == full.Length && origins != null && origins.Length == full.Length &&
                    Object.ReferenceEquals(events[SelectedIndex], borrowerEvent) && origins[SelectedIndex] == borrowerSourceLine &&
                    Object.Equals(Property(borrowerEvent, "LineNumber"), borrowerSourceLine) &&
                    Object.Equals(Property(borrowerEvent, "FilePath"), owner.SourcePath) &&
                    Object.ReferenceEquals(Property(borrowerEvent, "RawLine"), borrowerFullOriginal[SelectedIndex]) &&
                    Object.ReferenceEquals(full[SelectedIndex], borrowerFullOriginal[SelectedIndex]) &&
                    borrowerPositions.Length == full.Length && borrowerPositions[SelectedIndex] > 0;
            }
            internal bool BorrowerSelectionMatches(int selected, string writer, long sequence)
            { return selected == SelectedIndex && writer == borrowerWriterId && sequence == borrowerSequence; }
            internal long BorrowerRecords { get { return borrowerPriorRecords; } }
            internal long BorrowerUnits { get { return borrowerPriorUnits; } }
        }

        // Non-owning, invocation-bound diagnostic reuse. Disposing this result
        // never closes the original reader, custody, source or work clock.
        public sealed partial class MembershipBorrowerForTest : IDisposable
        {
            private readonly MembershipBorrowerForTest original;
            private readonly Worker owner;
            private readonly WriterReader reader;
            private readonly NativeMembership membership;
            private readonly PrimaryEvaluationCustody custody;
            private readonly CompletedEvaluations pair;
            private readonly object context, index, failure, fullView, compactView;
            private readonly string[] full, compact;
            private readonly Stopwatch work;
            private int consumed, disposed;
            internal MembershipBorrowerForTest(Worker owner, WriterReader reader, NativeMembership membership,
                PrimaryEvaluationCustody custody, CompletedEvaluations pair, object context, object index, object failure,
                string[] full, string[] compact, object fullView, object compactView, Stopwatch work)
            {
                original = this; this.owner = owner; this.reader = reader; this.membership = membership;
                this.custody = custody; this.pair = pair; this.context = context; this.index = index; this.failure = failure;
                this.full = full; this.compact = compact; this.fullView = fullView; this.compactView = compactView; this.work = work;
            }
            public bool IsCurrent
            {
                get { return Object.ReferenceEquals(original, this) && disposed == 0 && owner.OwnsBorrower(this, reader, membership, custody) &&
                    membership.MatchesBorrowerInputs(owner, reader, context, index, failure, full, work) &&
                    custody.Owns(pair, context, full, compact, fullView, compactView); }
            }
            public long CumulativeRecordVisits
            {
                get { return membership.BorrowerRecords + consumed
#if NLINK_TEST_SELECTED_NATIVE_WRITER
                    + selectedOriginAttempted
#endif
#if NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF
                    + payloadOwnerAttempted + selectedTerminalVisits
#endif
                    ; }
            }
            public long CumulativeCodeUnits { get { return membership.BorrowerUnits + (consumed == 0 ? 0 : full[membership.SelectedIndex].Length)
#if NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF
                + payloadOwnerUnits + selectedTerminalUnits
#endif
                ; } }
            public bool TryConsume(Worker candidate, WriterReader candidateReader, PrimaryEvaluationCustody candidateCustody,
                CompletedEvaluations candidatePair, object candidateContext, object candidateIndex, object candidateFailure,
                string[] candidateFull, string[] candidateCompact, object candidateFullView, object candidateCompactView,
                Stopwatch clock, int selected, string writer, long sequence)
            {
                if (!IsCurrent || !Object.ReferenceEquals(owner, candidate) || !Object.ReferenceEquals(reader, candidateReader) ||
                    !Object.ReferenceEquals(custody, candidateCustody) || !Object.ReferenceEquals(pair, candidatePair) ||
                    !Object.ReferenceEquals(context, candidateContext) || !Object.ReferenceEquals(index, candidateIndex) ||
                    !Object.ReferenceEquals(failure, candidateFailure) || !Object.ReferenceEquals(full, candidateFull) ||
                    !Object.ReferenceEquals(compact, candidateCompact) || !Object.ReferenceEquals(fullView, candidateFullView) ||
                    !Object.ReferenceEquals(compactView, candidateCompactView) || !Object.ReferenceEquals(work, clock) || !clock.IsRunning ||
                    !membership.BorrowerSelectionMatches(selected, writer, sequence) || membership.BorrowerRecords >= 4096 ||
                    full[selected].Length > LocalPublicationInputLimits.ProducerCodeUnits - membership.BorrowerUnits) return false;
                return Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
            }
            public void Dispose() { if (Object.ReferenceEquals(original, this)) Interlocked.Exchange(ref disposed, 1); }
        }
    }
}
#endif
