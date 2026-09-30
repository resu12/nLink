param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Compile the exact resolver method twice against inert control-flow doubles.
# These doubles cannot open native handles, evaluate authority, or publish.
$source=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierPrimaryWitnesses.cs'))
$start=$source.IndexOf('                private bool witnessesAttempted;',[StringComparison]::Ordinal)
$end=$source.IndexOf('                // A bounded native-origin selection,',[StringComparison]::Ordinal)
if($start -lt 0 -or $end -le $start) {throw 'Resolver method boundaries changed'}
$method=$source.Substring($start,$end-$start)
$observer=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWitnessResolutionDiagnostics.cs')).Replace('using System;','')
$template=@'
using System;
using System.Diagnostics;
#pragma warning disable 0169, 0649
namespace NLink.Tools {
    internal sealed class LocalFrontierDiagnosticState {
        internal static string Fault;
        private int checks;
        internal static LocalFrontierDiagnosticState Capture(object f, object c, object p, object i, object fv, object cv, int n, Action visit) {
            visit();
            if (Fault == "capacity") throw new InvalidOperationException("frontier_diagnostic_state_capacity");
            return new LocalFrontierDiagnosticState();
        }
        internal bool IsCurrent() { checks++; return !(Fault == "state-change" && checks == 1) && !(Fault == "final-change" && checks == 2); }
    }
    public sealed partial class LocalRetainedSourceRequest {
        public sealed partial class FrontierWorkerInputs {
            internal sealed class Owner { internal readonly object frontierWorkerSync = new object(); }
            internal readonly Owner owner = new Owner(); internal readonly object sync = new object();
            internal readonly Stopwatch work = new Stopwatch(); internal long evaluationVisits;
            internal int Closes; internal EvaluationCustody Custody;
            internal void VisitEvaluation() { evaluationVisits++; }
            internal void Close() { Closes++; Custody.Retire(); }
            public sealed class EvaluationPair { }
            public sealed class NativePayloadSet { internal bool Contains(object value) { return value != null; } }
            public sealed class NativeWitnessSelection { internal object Payload = new object(); internal string Snapshot = "snapshot"; internal int[] Indices = new[] { 1 }; }
            public sealed class NativeWitnessBatch { internal void BindSnapshot(string snapshot, LocalFrontierDiagnosticState state) { } }
            public sealed partial class EvaluationCustody {
                private readonly FrontierWorkerInputs inputs = new FrontierWorkerInputs();
                private readonly EvaluationPair issued = new EvaluationPair();
                private string[] full = new[] { "full" }, compact = new[] { "compact" };
                private object fullView = new object(), compactView = new object();
                private int state = 3, currentCalls, callbackCalls; private string fault;
                private bool Original { get { return fault != "ownership"; } }
                private bool Matches(object a, string[] b, string[] c, object d, object e) { return true; }
                private bool OriginalPayloads(NativePayloadSet p) { return true; }
                private bool Current() {
                    currentCalls++; inputs.VisitEvaluation();
                    if (fault == "budget") throw new InvalidOperationException("frontier_evaluation_budget");
                    return fault != "currentness";
                }
                internal void Retire() { state = 4; full = compact = null; }
                private NativeWitnessBatch TryCaptureWitnesses(EvaluationPair p, object c, string[] f, string[] k, object fv, object cv, NativePayloadSet s, object n, int[] i) {
                    inputs.work.Stop(); return fault == "witness" ? null : new NativeWitnessBatch();
                }
                METHOD
#if NLINK_TEST_ATTEMPT_DISPATCH
                public static void CheckProfileControls() {
                    var value = new EvaluationCustody();
                    value.RecordWitnessCallbackForTest(0, 0, 0, 0, false, false);
                    if (value.WitnessResolutionCallbackForTest[0] != null) throw new Exception("Out-of-callback sample accepted");
                    value.inputs.work.Start();
                    var stages = new[] { "ownership", "availability", "state-capture", "selection-callback", "selection-validation", "witness-capture", "final-currentness", "accepted" };
                    for (int i = 0; i < stages.Length; i++) {
                        value.inputs.evaluationVisits = i;
                        value.ObserveWitnessResolutionForTest(stages[i], null, false);
                        if (value.WitnessResolutionStagesForTest[i].Length != 2 || value.WitnessResolutionStagesForTest[i][1] != i)
                            throw new Exception("Stage sample mismatch");
                    }
                    value.WitnessResolutionStageForTest = "selection-callback";
                    for (int i = 0; i < 9; i++) {
                        value.RecordWitnessCallbackForTest(i, 10, 5, 2, false, false);
                        var sample = value.WitnessResolutionCallbackForTest[i];
                        value.RecordWitnessCallbackForTest(i, 20, 10, 4, false, false);
                        if (sample.Length != 8 || sample[0] != i || sample[2] != 7 || sample[3] != 10 || sample[4] != 5 || sample[5] != 2 ||
                            !Object.ReferenceEquals(sample, value.WitnessResolutionCallbackForTest[i])) throw new Exception("Callback sample changed");
                    }
                    value.RecordWitnessCallbackForTest(-1, 0, 0, 0, true, true);
                    value.RecordWitnessCallbackForTest(9, 0, 0, 0, true, true);
                    value.RecordWitnessCallbackForTest(1, -1, 0, 0, true, true);
                    value.RecordWitnessCallbackForTest(1, 0, 0, 65, true, true);
                    if (value.WitnessResolutionCallbackForTest[9] != null) throw new Exception("Invalid sample accepted");
                    value.RecordWitnessCallbackForTest(1, 12, 5, 2, true, true);
                    var terminal = value.WitnessResolutionCallbackForTest[9];
                    value.RecordWitnessCallbackForTest(8, 99, 99, 64, true, false);
                    value.WitnessResolutionCallbackForTest[0] = null;
                    value.RecordWitnessCallbackForTest(0, 99, 99, 64, false, false);
                    if (terminal[0] != 1 || terminal[3] != 12 || terminal[6] != 1 || terminal[7] != 1 ||
                        !Object.ReferenceEquals(terminal, value.WitnessResolutionCallbackForTest[9]) || value.WitnessResolutionCallbackForTest[0] != null)
                        throw new Exception("Terminal sample not frozen");
                    value.ObserveWitnessResolutionForTest(null, null, true);
                    value.WitnessResolutionStagesForTest[0] = null;
                    value.ObserveWitnessResolutionForTest("ownership", null, false);
                    if (value.WitnessResolutionStagesForTest[0] != null) throw new Exception("Rejected stage changed");
                    var broken = new EvaluationCustody();
                    broken.WitnessResolutionStagesForTest = null; broken.WitnessResolutionCallbackForTest = null;
                    broken.ObserveWitnessResolutionForTest("selection-callback", null, false);
                    broken.RecordWitnessCallbackForTest(0, 0, 0, 0, false, false);
                    if (value.inputs.evaluationVisits != 7 || !value.inputs.work.IsRunning || value.currentCalls != 0 || value.inputs.Closes != 0 ||
                        broken.inputs.evaluationVisits != 0) throw new Exception("Profile changed work or authority");
                }
#endif
                public static object[] Run(string fault) {
                    var value = new EvaluationCustody(); value.fault = fault; value.inputs.Custody = value;
                    LocalFrontierDiagnosticState.Fault = fault;
                    if (fault == "already-attempted") value.resolutionAttempted = true;
                    Func<Action, NativeWitnessSelection> callback = visit => {
                        value.callbackCalls++; visit();
                        if (fault == "callback-error") throw new InvalidOperationException("private callback text must not escape");
                        if (fault == "selection") return null;
                        return new NativeWitnessSelection();
                    };
                    if (fault == "no-callback") callback = null;
                    var result = value.TryResolveWitnesses(value.issued, null, value.full, value.compact, value.fullView, value.compactView,
                        new NativePayloadSet(), null, null, null, null, callback);
                    string stage = "", reason = ""; int calls = 0, sampledState = 0, sampledFull = 0; long visits = 0;
#if NLINK_TEST_ATTEMPT_DISPATCH
                    stage = value.WitnessResolutionStageForTest; reason = value.WitnessResolutionReasonForTest;
                    calls = value.WitnessResolutionCallsForTest; sampledState = value.WitnessResolutionStateForTest;
                    sampledFull = value.WitnessResolutionFullCountForTest; visits = value.WitnessResolutionVisitsForTest;
                    // Later rejection/cleanup must not replace the original observation.
                    if (reason != "none") {
                        value.ObserveWitnessResolutionForTest("later", new InvalidOperationException("frontier_evaluation_budget"), true);
                        if (stage != value.WitnessResolutionStageForTest || reason != value.WitnessResolutionReasonForTest || visits != value.WitnessResolutionVisitsForTest)
                            throw new Exception("First rejection was replaced");
                    }
#endif
                    return new object[] { result != null, value.inputs.Closes, value.currentCalls, value.callbackCalls,
                        value.resolutionAttempted, value.inputs.evaluationVisits, value.state, stage, reason, calls, sampledState, sampledFull, visits };
                }
            }
        }
    }
}
'@
foreach($profile in @('Plain','Observed')) {
    $text=$template.Replace('                METHOD',$method)+"`n"+$observer
    $text=$text.Replace('namespace NLink.Tools','namespace Witness'+$profile)
    if($profile -ceq 'Observed') {$text="#define NLINK_TEST_ATTEMPT_DISPATCH`n"+$text}
    Add-Type -TypeDefinition $text -WarningAction SilentlyContinue
}
$expected=@{
    valid=@('accepted','none'); ownership=@('ownership','rejected'); currentness=@('availability','rejected')
    'already-attempted'=@('availability','rejected'); 'no-callback'=@('availability','rejected')
    budget=@('availability','frontier_evaluation_budget'); capacity=@('state-capture','frontier_diagnostic_state_capacity')
    'callback-error'=@('selection-callback','InvalidOperationException'); selection=@('selection-validation','rejected')
    'state-change'=@('selection-validation','rejected'); witness=@('witness-capture','rejected'); 'final-change'=@('final-currentness','rejected')
}
foreach($fault in $expected.Keys) {
    $plain=[WitnessPlain.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]::Run($fault)
    $observed=[WitnessObserved.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]::Run($fault)
    for($i=0;$i -lt 7;$i++) {if($plain[$i] -cne $observed[$i]) {throw ('Original control flow changed: '+$fault+' field '+$i)}}
    if($observed[7] -cne $expected[$fault][0] -or $observed[8] -cne $expected[$fault][1] -or
        $observed[9] -ne 1 -or $observed[10] -ne 3 -or $observed[11] -ne 1 -or $observed[12] -ne $observed[5]) {throw ('Rejection observation mismatch: '+$fault)}
    if($fault -ceq 'capacity' -and ($observed[3] -ne 0 -or $observed[1] -ne 1 -or $observed[6] -ne 4)) {throw 'State rejection entered callback or lost cleanup'}
}
$plainType=[WitnessPlain.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]
if($null -ne $plainType.GetMethod('ObserveWitnessResolutionForTest',[Reflection.BindingFlags]'Instance,NonPublic')) {throw 'Test observer survived normal compilation'}
if($null -ne $plainType.GetMethod('RecordWitnessCallbackForTest')) {throw 'Callback profile survived normal compilation'}
[WitnessObserved.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]::CheckProfileControls()
'witness-resolution-diagnostics-controls=12'
