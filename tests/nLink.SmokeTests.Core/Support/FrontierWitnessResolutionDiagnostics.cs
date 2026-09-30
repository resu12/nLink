#if NLINK_TEST_ATTEMPT_DISPATCH
using System;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            public sealed partial class EvaluationCustody
            {
                internal string WitnessResolutionStageForTest = "not-entered", WitnessResolutionReasonForTest = "none";
                internal int WitnessResolutionCallsForTest, WitnessResolutionStateForTest;
                internal int WitnessResolutionFullCountForTest, WitnessResolutionCompactCountForTest;
                internal long WitnessResolutionWorkTicksForTest, WitnessResolutionVisitsForTest;
                // Fixed slots contain only cumulative counters, never evidence or authority.
                internal long[][] WitnessResolutionStagesForTest = new long[8][];
                internal long[][] WitnessResolutionCallbackForTest = new long[10][];
                private bool witnessCallbackFinishedForTest;
                public void RecordWitnessCallbackForTest(int phase, int rows, int owned, int selected, bool terminal, bool failed)
                {
                    try
                    {
                        if (WitnessResolutionStageForTest != "selection-callback" || WitnessResolutionReasonForTest != "none" ||
                            witnessCallbackFinishedForTest || phase < 0 || phase > 8 || rows < 0 || owned < 0 || selected < 0 || selected > 64) return;
                        int slot = terminal ? 9 : phase;
                        if (WitnessResolutionCallbackForTest[slot] != null) return;
                        WitnessResolutionCallbackForTest[slot] = new long[] { phase, inputs.work.ElapsedTicks, inputs.evaluationVisits,
                            rows, owned, selected, terminal ? 1 : 0, failed ? 1 : 0 };
                        if (terminal) witnessCallbackFinishedForTest = true;
                    }
                    catch { } // Test diagnostics cannot alter callback failure or cleanup.
                }
                private void RecordWitnessStageForTest(string stage)
                {
                    try
                    {
                        int slot;
                        switch (stage)
                        {
                            case "ownership": slot = 0; break;
                            case "availability": slot = 1; break;
                            case "state-capture": slot = 2; break;
                            case "selection-callback": slot = 3; break;
                            case "selection-validation": slot = 4; break;
                            case "witness-capture": slot = 5; break;
                            case "final-currentness": slot = 6; break;
                            case "accepted": slot = 7; break;
                            default: return;
                        }
                        if (WitnessResolutionStagesForTest[slot] == null)
                            WitnessResolutionStagesForTest[slot] = new long[] { inputs.work.ElapsedTicks, inputs.evaluationVisits };
                    }
                    catch { }
                }
                partial void ObserveWitnessResolutionForTest(string stage, Exception error, bool rejected)
                {
                    if (stage == "ownership") WitnessResolutionCallsForTest++;
                    if (WitnessResolutionReasonForTest != "none") return;
                    RecordWitnessStageForTest(stage);
                    if (stage != null) WitnessResolutionStageForTest = stage;
                    if (!rejected && stage != "accepted") return;
                    if (rejected)
                    {
                        WitnessResolutionReasonForTest = error == null ? "rejected" : error.GetType().Name;
                        // Only fixed internal rejection codes, never arbitrary callback text.
                        if (error != null && error.GetType() == typeof(InvalidOperationException))
                            switch (error.Message)
                            {
                                case "frontier_diagnostic_state_capacity":
                                case "frontier_diagnostic_text_capacity":
                                case "frontier_diagnostic_collection":
                                case "frontier_diagnostic_index":
                                case "frontier_diagnostic_scalar":
                                case "frontier_diagnostic_owner":
                                case "frontier_evaluation_budget":
                                    WitnessResolutionReasonForTest = error.Message; break;
                            }
                    }
                    WitnessResolutionWorkTicksForTest = inputs.work.ElapsedTicks;
                    WitnessResolutionVisitsForTest = inputs.evaluationVisits;
                    WitnessResolutionStateForTest = state;
                    WitnessResolutionFullCountForTest = full == null ? 0 : full.Length;
                    WitnessResolutionCompactCountForTest = compact == null ? 0 : compact.Length;
                }
            }
        }
    }
}
#endif
