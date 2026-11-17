#if NLINK_TEST_SELECTED_NATIVE_WRITER && NLINK_TEST_MEMBERSHIP_BORROWER && NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class PrimaryEvaluationCustody
        {
            internal bool OwnsSelectedOriginPublication(PreparedPublication completion, WriterReader reader)
            { return OwnsPublication(completion) && Object.ReferenceEquals(publicationWriter, reader); }
        }

        public sealed partial class NativeMembership
        {
            internal CollectorLineOriginForTest SelectedCollectorOrigin(CollectorPrimaryFilesForTest files)
            {
                if (!Object.ReferenceEquals(borrowerCollector, files) ||
                    !OwnsCollectorOriginInputs(borrowerWriter, borrowerFull, borrowerFullOriginal, borrowerWork) ||
                    !Object.ReferenceEquals(collectorWriterPositions, borrowerPositions)) return null;
                var row = files.DescribeLineOriginForTest(borrowerSource, borrowerSourceLine);
                return row != null && row.SemanticLine == borrowerSourceLine && row.ProducerLine > 0 && row.RetainedLine > 0 &&
                    row.WriterLine > 0 && MatchesCollectorWriterPosition(SelectedIndex, row.WriterLine) ? row : null;
            }
        }

        public sealed partial class MembershipBorrowerForTest
        {
            private int selectedOriginAttempted;
            private SelectedCollectorOriginForTest selectedOrigin;

            // No caller-authored row, selection or prepared-witness object is
            // accepted. Use the retained original failure and private publication.
            public SelectedCollectorOriginForTest TryJoinSelectedOriginForTest(CollectorPrimaryFilesForTest files,
                PreparedPublication completion)
            {
                if (!IsCurrent || consumed != 1 || !work.IsRunning ||
                    !Object.ReferenceEquals(owner.CollectorOutputsForTest, files) || files == null ||
                    !files.OwnsPrimary(owner, NativeMembership.Property(context, "SourceReader") as SourceReader,
                        custody, pair, context, full, compact, fullView, compactView) ||
                    !custody.OwnsSelectedOriginPublication(completion, reader) ||
                    CumulativeRecordVisits >= 4096 || CumulativeCodeUnits > LocalPublicationInputLimits.ProducerCodeUnits ||
                    Interlocked.CompareExchange(ref selectedOriginAttempted, 1, 0) != 0) return null;
                var row = membership.SelectedCollectorOrigin(files);
                if (row == null || !IsCurrent) return null;
                selectedOrigin = new SelectedCollectorOriginForTest(this, owner, files, completion, row, membership.SelectedIndex);
                if (!selectedOrigin.IsCurrent) selectedOrigin = null;
                return selectedOrigin;
            }

            internal bool OwnsSelectedOrigin(SelectedCollectorOriginForTest result, CollectorPrimaryFilesForTest files,
                PreparedPublication completion, CollectorLineOriginForTest row)
            {
                if (!Object.ReferenceEquals(selectedOrigin, result) || !IsCurrent || consumed != 1 ||
                    !Object.ReferenceEquals(owner.CollectorOutputsForTest, files) ||
                    !custody.OwnsSelectedOriginPublication(completion, reader) || CumulativeRecordVisits > 4096 ||
                    CumulativeCodeUnits > LocalPublicationInputLimits.ProducerCodeUnits) return false;
                var current = membership.SelectedCollectorOrigin(files);
                return current != null && current.SemanticLine == row.SemanticLine && current.ProducerLine == row.ProducerLine &&
                    current.RetainedLine == row.RetainedLine && current.WriterLine == row.WriterLine;
            }
            internal bool SelectedOriginIssued { get { return selectedOrigin != null; } }
            internal bool SelectedOriginRetired { get { return selectedOrigin == null || !selectedOrigin.IsCurrent; } }
        }

        // Non-owning, invocation-bound diagnostic association only. The result
        // cannot publish a route, complete a transfer or satisfy terminal integrity.
        public sealed class SelectedCollectorOriginForTest : IDisposable
        {
            private readonly SelectedCollectorOriginForTest original;
            private readonly MembershipBorrowerForTest borrower;
            private readonly Worker owner;
            private readonly CollectorPrimaryFilesForTest files;
            private readonly PreparedPublication completion;
            private readonly CollectorLineOriginForTest row;
            private int disposed;
            internal SelectedCollectorOriginForTest(MembershipBorrowerForTest borrower, Worker owner,
                CollectorPrimaryFilesForTest files, PreparedPublication completion, CollectorLineOriginForTest row, int selected)
            {
                original = this; this.borrower = borrower; this.owner = owner; this.files = files;
                this.completion = completion; this.row = row; SelectedIndex = selected;
            }
            public bool IsCurrent
            { get { return Object.ReferenceEquals(original, this) && disposed == 0 && borrower.OwnsSelectedOrigin(this, files, completion, row); } }
            public int SelectedIndex { get; private set; }
            public int SourceLine { get { return row.SemanticLine; } }
            public int ProducerLine { get { return row.ProducerLine; } }
            public int RetainedLine { get { return row.RetainedLine; } }
            public int WriterLine { get { return row.WriterLine; } }
            public bool Owns(Worker candidate, MembershipBorrowerForTest candidateBorrower,
                CollectorPrimaryFilesForTest candidateFiles, PreparedPublication candidateCompletion)
            {
                return IsCurrent && Object.ReferenceEquals(owner, candidate) && Object.ReferenceEquals(borrower, candidateBorrower) &&
                    Object.ReferenceEquals(files, candidateFiles) && Object.ReferenceEquals(completion, candidateCompletion);
            }
            public void Dispose() { if (Object.ReferenceEquals(original, this)) Interlocked.Exchange(ref disposed, 1); }
        }

        public sealed partial class Worker
        {
            public bool SelectedOriginIssuedForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && membershipBorrower != null && membershipBorrower.SelectedOriginIssued; } }
            public bool SelectedOriginRetiredForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && (membershipBorrower == null || membershipBorrower.SelectedOriginRetired); } }
        }
    }
}
#endif
