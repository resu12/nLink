#if NLINK_TEST_TERMINAL_REQUEST && NLINK_FRONTIER_LEDGER
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class Worker
        {
            public bool CollectorOriginJoinWasIssuedForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && collectorMembership != null && collectorMembership.OriginJoinWasIssued; } }
            public bool CollectorOriginJoinRetiredForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) &&
                (collectorMembership == null || (!collectorMembership.OriginsJoinedForTest && collectorMembership.JoinedOriginsForTest == null)); } }
        }

        public sealed partial class NativeMembership
        {
            private readonly NativeMembership collectorOriginal;
            private readonly WriterReader collectorWriter;
            private readonly string[] collectorFull, collectorFullOriginal;
            private readonly Stopwatch collectorWork;
            private readonly int[] collectorWriterPositions;

            // The existing verified pass supplies this array once. No path,
            // reconstructed sequence positions or caller-authored rows enter it.
            internal NativeMembership(int selected, long records, long units, WriterReader writer,
                string[] full, string[] original, Stopwatch work, int[] positions) : this(selected, records, units)
            {
                collectorOriginal = this; collectorWriter = writer; collectorFull = full;
                collectorFullOriginal = original; collectorWork = work; collectorWriterPositions = positions;
            }
            internal bool OwnsCollectorOriginInputs(WriterReader writer, string[] full, string[] original, Stopwatch work)
            {
                return Object.ReferenceEquals(collectorOriginal, this) && Object.ReferenceEquals(collectorWriter, writer) &&
                    Object.ReferenceEquals(collectorFull, full) && Object.ReferenceEquals(collectorFullOriginal, original) &&
                    Object.ReferenceEquals(collectorWork, work) && collectorWriterPositions != null &&
                    collectorWriterPositions.Length == full.Length;
            }
            internal bool MatchesCollectorWriterPosition(int index, int physicalLine)
            { return collectorWriterPositions[index] == physicalLine; }
        }

        public sealed partial class CollectorMembershipForTest
        {
            private int originJoinAttempted;
            private ReadOnlyCollection<CollectorLineOriginForTest> joinedOrigins;
            private Stopwatch originJoinWork;
            private long originJoinVisits;
            internal bool OriginJoinWasIssued { get { return Object.ReferenceEquals(original, this) && joinedOrigins != null; } }
            public bool OriginsJoinedForTest
            { get { return IsCurrent && joinedOrigins != null && originJoinWork.ElapsedMilliseconds <= 20000; } }
            // Detached diagnostics only, never accepted back as a capability.
            public ReadOnlyCollection<CollectorLineOriginForTest> JoinedOriginsForTest
            { get { return OriginsJoinedForTest ? joinedOrigins : null; } }
            public long OriginJoinRecordVisitsForTest
            { get { return Object.ReferenceEquals(original, this) ? originJoinVisits : 0; } }

            public bool TryJoinOriginsForTest(Worker candidate, CollectorPrimaryFilesForTest candidateFiles,
                SourceReader reader, PrimaryEvaluationCustody evaluationCustody, CompletedEvaluations evaluations,
                object inputs, string[] fullLines, string[] compactLines, object fullResult, object compactResult,
                int[] sourceOrigins, Stopwatch work)
            {
                if (!Owns(candidate, reader, evaluationCustody, evaluations, inputs, fullLines, compactLines, fullResult, compactResult) ||
                    !Object.ReferenceEquals(files, candidateFiles) || work == null || !work.IsRunning || work.ElapsedMilliseconds > 20000)
                    return false;
                var contextObject = PSObject.AsPSObject(context);
                var fullOriginal = contextObject.Properties["FullOriginal"].Value as string[];
                if (!Object.ReferenceEquals(contextObject.Properties["FullOrigins"].Value, sourceOrigins) || sourceOrigins == null ||
                    sourceOrigins.Length != full.Length || full.Length != owner.SourceLines ||
                    !membership.OwnsCollectorOriginInputs(writer, full, fullOriginal, work)) return false;
                if (Interlocked.CompareExchange(ref originJoinAttempted, 1, 0) != 0) return false;
                originJoinWork = work;
                long priorVisits = 7L * full.Length + 4L * compact.Length + membership.RecordVisits;
                if (priorVisits + full.Length > 4096) return false;
                var rows = new List<CollectorLineOriginForTest>(full.Length);
                for (int i = 0; i < full.Length; i++)
                {
                    originJoinVisits++;
                    if (!IsCurrent || work.ElapsedMilliseconds > 20000 || !Object.ReferenceEquals(full[i], fullOriginal[i]) ||
                        sourceOrigins[i] != i + 1) return false;
                    var row = files.DescribeLineOriginForTest(source, sourceOrigins[i]);
                    if (row == null || row.SemanticLine != sourceOrigins[i] || row.ProducerLine < 1 || row.RetainedLine < 1 ||
                        !membership.MatchesCollectorWriterPosition(i, row.WriterLine)) return false;
                    rows.Add(row);
                }
                if (!Owns(candidate, reader, evaluationCustody, evaluations, inputs, fullLines, compactLines, fullResult, compactResult) ||
                    work.ElapsedMilliseconds > 20000) return false;
                joinedOrigins = rows.AsReadOnly();
                return true;
            }
        }
    }
}
#endif
