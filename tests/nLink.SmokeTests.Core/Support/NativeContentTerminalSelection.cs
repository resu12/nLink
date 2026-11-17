#if NLINK_TEST_NATIVE_CONTENT_TERMINAL_SELECTION
using System;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            public sealed partial class EvaluationCustody
            {
                private const int TerminalContentMillisecondsForTest = 150000;
                private bool terminalContentAcquisitionForTest;

                // Dedicated offline fixture only. finalConsumer is a test-harness
                // declaration, not a native terminal capability or route authority.
                // Its fixed caller must return directly to normal final disposal.
                public ContentInputSelectionForTest TryAcquireTerminalContentInputsForTest(bool enabled, bool finalConsumer,
                    EvaluationPair pair, object candidateContext, string[] candidateFull, string[] candidateCompact,
                    object candidateFullView, object candidateCompactView, object[] events, object selectedEvent,
                    Func<ContentInputCursorForTest, int[]> select)
                {
                    if (!enabled || !finalConsumer) return null;
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (terminalContentAcquisitionForTest || contentAcquisitionAttemptedForTest ||
                            witnessBatch != null || witnessesAttempted || resolutionAttempted || payloadSet != null || payloadAttempted) return null;
                        terminalContentAcquisitionForTest = true;
                        try { return TryAcquireContentInputsForTest(true, pair, candidateContext, candidateFull, candidateCompact,
                            candidateFullView, candidateCompactView, events, selectedEvent, select); }
                        finally { terminalContentAcquisitionForTest = false; }
                    }
                }

                private void VisitTerminalContentInputForTest()
                {
                    if (++inputs.evaluationVisits > LocalPublicationInputLimits.EvaluationVisits ||
                        inputs.work.ElapsedMilliseconds > TerminalContentMillisecondsForTest)
                        throw new InvalidOperationException("content_input_budget");
                }
            }
        }
    }
}
#endif
