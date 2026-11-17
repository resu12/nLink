#if NLINK_TEST_ATTEMPT_DISPATCH
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class Worker
        {
            internal string PrimaryBaseRejectionForTest = "none";
            private bool PrimaryBaseCheckForTest(string reason, bool current)
            {
                if (!current) Interlocked.CompareExchange(ref PrimaryBaseRejectionForTest, reason, "none");
                return current;
            }
        }
        public sealed partial class FrontierWorkerInputs
        {
            // Four original predicates (base, scope, files, pins), partitioned
            // into active-other, active-read and inactive observations.
            internal readonly long[] PrimaryCheckTicksForTest = new long[12], PrimaryCheckCallsForTest = new long[12];
            internal long PrimaryAttachWorkTicksForTest, PrimaryOpenWorkTicksForTest, PrimaryReadWorkTicksForTest, PrimaryReadCallsForTest;
            internal readonly long[] PrimaryOriginCheckTicksForTest = new long[4], PrimaryOriginCheckCallsForTest = new long[4];
            internal long PrimaryOriginWorkTicksForTest, PrimaryOriginEntryWorkTicksForTest, PrimaryOriginCallsForTest, PrimaryOriginAcceptedForTest;
            private bool primaryReadingForTest;
            private bool primaryOriginForTest;
            private bool PrimaryMeasuredCurrentForTest(int predicate)
            {
                int index = (work.IsRunning ? (primaryReadingForTest ? 4 : 0) : 8) + predicate;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    switch (predicate)
                    {
                        case 0: return owner.FrontierBaseCurrent;
                        case 1: return scope.IsCurrent;
                        case 2: return files == null || files.IsCurrent;
                        case 3: return pins == null || pins.IsCurrent;
                        default: throw new ArgumentOutOfRangeException("predicate");
                    }
                }
                finally
                {
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    PrimaryCheckTicksForTest[index] += elapsed; PrimaryCheckCallsForTest[index]++;
                    if (primaryOriginForTest) { PrimaryOriginCheckTicksForTest[predicate] += elapsed; PrimaryOriginCheckCallsForTest[predicate]++; }
                }
            }
            internal string GetPrimaryWorkProfileForTest()
            {
                var result = new StringBuilder("primary-work-profile; frequency=" + Stopwatch.Frequency + "; work_ticks=" + work.ElapsedTicks +
                    "; attach_ticks=" + PrimaryAttachWorkTicksForTest + "; open_ticks=" + PrimaryOpenWorkTicksForTest +
                    "; read_ticks=" + PrimaryReadWorkTicksForTest + "; read_calls=" + PrimaryReadCallsForTest +
                    "; mapped_ticks=" + (nativeStream == null ? 0 : nativeStream.ReadTicksForTest) +
                    "; mapped_calls=" + (nativeStream == null ? 0 : nativeStream.ReadCallsForTest) +
                    "; facts_bytes=" + (files == null || files.Facts == null ? 0 : files.Facts.Body.Length) +
                    "; facts_ack=" + (files != null && files.Facts != null && files.Facts.Acknowledgment != null) +
                    "; origin_entry_work_ticks=" + PrimaryOriginEntryWorkTicksForTest + "; origin_work_ticks=" + PrimaryOriginWorkTicksForTest +
                    "; origin_calls=" + PrimaryOriginCallsForTest + "; origin_accepted=" + PrimaryOriginAcceptedForTest +
                    "; origin_batch_calls=" + PrimaryOriginBatchCallsForTest + "; origin_batch_tuples=" + PrimaryOriginBatchTuplesForTest +
                    "; origin_batch_work_ticks=" + PrimaryOriginBatchWorkTicksForTest);
                for (int i = 0; i < 3; i++)
                {
                    string stage = new[] { "active_other", "active_read", "inactive" }[i];
                    result.Append("; " + stage + "_calls=");
                    for (int j = 0; j < 4; j++) result.Append((j == 0 ? "" : ",") + PrimaryCheckCallsForTest[i * 4 + j]);
                    result.Append("; " + stage + "_ticks=");
                    for (int j = 0; j < 4; j++) result.Append((j == 0 ? "" : ",") + PrimaryCheckTicksForTest[i * 4 + j]);
                }
                result.Append("; origin_check_calls=");
                for (int j = 0; j < 4; j++) result.Append((j == 0 ? "" : ",") + PrimaryOriginCheckCallsForTest[j]);
                result.Append("; origin_check_ticks=");
                for (int j = 0; j < 4; j++) result.Append((j == 0 ? "" : ",") + PrimaryOriginCheckTicksForTest[j]);
                return result.ToString();
            }
            internal string PrimarySourceRejectionForTest = "none", PrimarySourceSnapshotForTest = "none";
            internal string PrimarySourceCloseForTest = "open", PrimarySourceExceptionForTest = "none";
            internal string PrimarySourceCloseSnapshotForTest = "none";
            private string PrimarySourceProgressForTest(long elapsed)
            { return "work_ms=" + elapsed + "; lines=" + sourceLines.Count + "; units=" + sourceUnits + "; started=" + sourceStarted + "; ended=" + sourceEnded; }
            private bool PrimarySourceCheckForTest(string reason, bool current, long elapsed = -1)
            {
                if (!current && PrimarySourceRejectionForTest == "none")
                {
                    PrimarySourceRejectionForTest = reason;
                    PrimarySourceSnapshotForTest = PrimarySourceProgressForTest(elapsed < 0 ? work.ElapsedMilliseconds : elapsed);
                }
                return current;
            }
            private bool PrimarySourceBudgetForTest(string reason, long elapsed, int maximumMilliseconds = LocalPublicationInputLimits.ProducerMilliseconds)
            { return PrimarySourceCheckForTest(reason, elapsed <= maximumMilliseconds, elapsed); }
        }
    }
}
#endif
