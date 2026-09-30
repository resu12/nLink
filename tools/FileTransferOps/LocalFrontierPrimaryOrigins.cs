using System;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class SourceReader
        {
            // Evidence only. A failed batch exposes no successful prefix.
            public int[] GetFrontierPhysicalOrigins(int[] ordinals, string[] originalLines)
            { return frontier == null ? null : frontier.SourceOrigins(this, ordinals, originalLines); }
        }

        public sealed partial class FrontierWorkerInputs
        {
#if NLINK_TEST_ATTEMPT_DISPATCH
            internal Action<string, int> OriginBatchProbeForTest = null;
            internal long PrimaryOriginBatchCallsForTest, PrimaryOriginBatchTuplesForTest, PrimaryOriginBatchWorkTicksForTest;
            private void ProbeOriginBatchForTest(string stage, int index)
            { var probe = OriginBatchProbeForTest; if (probe != null) probe(stage, index); }
#endif
            internal int[] SourceOrigins(SourceReader candidate, int[] ordinals, string[] lines)
            {
                lock (owner.frontierWorkerSync)
                lock (sync)
                {
                    if (!OriginalSource(candidate)) return null;
#if NLINK_TEST_ATTEMPT_DISPATCH
                    long startedForTest = work.ElapsedTicks; PrimaryOriginBatchCallsForTest++;
#endif
                    work.Start();
                    try
                    {
#if NLINK_TEST_ATTEMPT_DISPATCH
                        ProbeOriginBatchForTest("entry", -1);
#endif
                        if (!ResourceCurrent) { Close(); return null; }
                        if (!sourceEnded || ordinals == null || lines == null || ordinals.Length < 1 ||
                            ordinals.Length > 64 || lines.Length != ordinals.Length) return null;
                        var result = new int[ordinals.Length];
                        for (int i = 0; i < result.Length; i++)
                        {
#if NLINK_TEST_ATTEMPT_DISPATCH
                            ProbeOriginBatchForTest("tuple", i);
#endif
                            if (!CanContinueOriginBatch()) { Close(); return null; }
                            int ordinal = ordinals[i]; string line = lines[i];
#if NLINK_TEST_ATTEMPT_DISPATCH
                            ProbeOriginBatchForTest("tuple-read", i);
#endif
                            if (ordinal < 1 || ordinal > sourceLines.Count ||
                                !Object.ReferenceEquals(sourceLines[ordinal - 1], line)) return null;
                            result[i] = facts.SemanticOrigin(ordinal - 1);
                        }
#if NLINK_TEST_ATTEMPT_DISPATCH
                        ProbeOriginBatchForTest("commit", -1);
#endif
                        if (!ResourceCurrent) { Close(); return null; }
#if NLINK_TEST_ATTEMPT_DISPATCH
                        ProbeOriginBatchForTest("verified", -1);
#endif
                        if (!CanContinueOriginBatch()) { Close(); return null; }
#if NLINK_TEST_ATTEMPT_DISPATCH
                        PrimaryOriginBatchTuplesForTest += result.Length;
#endif
                        return result;
                    }
                    catch { Close(); return null; }
#if NLINK_TEST_ATTEMPT_DISPATCH
                    finally { work.Stop(); PrimaryOriginBatchWorkTicksForTest += work.ElapsedTicks - startedForTest; }
#else
                    finally { work.Stop(); }
#endif
                }
            }
            // Abort-only checks. Only the full entry/exit checks can admit a batch.
            private bool CanContinueOriginBatch()
            {
                return !closed && work.ElapsedMilliseconds <= LocalPublicationInputLimits.ProducerMilliseconds &&
                    owner.FrontierBaseCurrent && scope.IsCurrent && !files.Cancelled.WaitOne(0) && !files.Facts.Cancelled.WaitOne(0);
            }
        }
    }
}
