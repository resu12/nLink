# Collector capture failure: bounded reproduction and timing investigation

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-collector-membership-20260925.md`.
Offline/test-only. Historical root cause remains unconfirmed; no runtime fix,
acceptance change or timing-contract relaxation is claimed.

## Observed results

The preceding milestone recorded two unexpected pre-primary failures:
`f65edaaf-4f4e-48bc-ad0d-7405cac49831` and
`5233f11f-9662-4c1c-8308-9fbecb873f0b`, both reporting
`filetransfer_gui_evidence_capture_not_persisted`. Their original inner exceptions
were not retained. Neither primary analysis started.

This investigation first verified all 7,801 preceding file pins. Then it ran
three bounded diagnostic rounds containing only the previously failing
PowerShell 7 membership-positive and wrong-output-identity cases. All six tests
passed; the spontaneous failure did not recur. The reproduction limit was not
extended to obtain a desired result. Evidence is in
`artifacts/collector-capture-20260925/results/capture-reproduction-{1,2,3}.trx`.

## Timing hypothesis, not an established historical RCA

The test-only `ProcessCollectorForTest` starts its capture stopwatch before
`ProcessFromStream[WithRetainedOutputs]`. That includes the original stream read,
hashing, output creation and three durable flushes in the native capture's
250-ms limit. This stricter boundary was explicitly recorded in the earlier
collector-finalizer milestone.

In the production path, `GuiSmokeLogLedgerStreamingProcessor.ProcessCore` starts
its native-capture stopwatch after those flushes, just before collecting the
native output metadata. The stream reader also has its separate 5-second
lifetime bound. These clocks measure different scopes; a slow disk flush can
therefore make the test-only capture unavailable without invalid input content.
This is a plausible explanation for the intermittent failure, not proof that it
caused either historical attempt.

Diagnostic replay used the unchanged native factory against both archived
producer inputs on both PowerShell hosts. For each input, one fresh-clock case
created a native capture and one explicitly expired-clock case did not. All
eight controls passed. Each input contained 135 source lines. Fresh-clock
factory calls finished in 0-12 ms; expired calls began at 305-315 ms with the
original 250-ms bound.

The script is
`artifacts/collector-capture-20260925/verify-capture-budget.ps1`.
It creates new replay files and never overwrites historical evidence. It does
not attach an original GUI or primary owner, does not feed replay results into
acceptance, and explicitly checks that no stream authority/completion flag is
enabled. Fresh replay clocks are deliberate controls, not replacements for an
actual finalizer's clock. This establishes a rejection mechanism and rules out
static factory rejection of these replayed contents with a fresh clock; it does
not reproduce their original native-handle lifetime or original elapsed time.

An initial diagnostic-script invocation failed at reflection argument binding:
PowerShell wrapped a string in `PSObject`. It did not invoke the factory. The
script now unwraps the argument objects, uses unique run directories and passes
on both hosts. The incomplete first replay remains a development artifact, not
a successful control or application failure.

## Implemented diagnostic change

Only the guarded test helper `Support/TerminalCollectorFinalizer.cs` changed.
On `collector_finalizer_capture_missing`, its exception now includes:

- elapsed milliseconds immediately before native capture;
- elapsed milliseconds after the failed ownership/capture check;
- the unchanged native capture budget;
- whether any native capture was returned.

The previous bounded exception/stack preservation records these details if the
failure recurs. No source text, IDs or unbounded collection is added to the
message. Ownership checks, original stopwatch start, factory implementation,
one-shot processing, deadlines, successful-exit requirements and cleanup are
unchanged. A non-null capture would distinguish an ownership rejection from a
factory returning null; an already-expired pre-capture clock would establish
that the factory's first timing rejection applies on that future attempt.

Both original host integrations passed after this change in
`capture-diagnostic-hosts.trx`. They dynamically compile and load the current
guarded helper; the normal test DLL did not require rebuilding for a guarded
source-only diagnostic change. Together with the six reproduction executions,
this turn has eight passing integration executions, with no unexpected failure.
Those passes do not reclassify either preceding failure or close the RCA.

## Fix plan / next bounded step

1. Add a single controlled expiry case inside the original owned finalizer
   lifecycle, not only the standalone factory replay. Verify that the new
   diagnostic reports the exceeded bound, no completion/output custody is
   accepted, no primary authority evaluation runs, and handles/channels close.
2. If a spontaneous failure is observed, use its original exception and timing
   fields to identify the first actual failing edge. Do not repeat aggregate
   lanes until green or change runtime code based on replay alone.
3. If capture-budget expiry is confirmed, review the timing contract explicitly:
   retaining the current whole-processing 250-ms constraint and aligning its
   scope with production metadata capture are different choices. Any alignment
   must preserve the separate bounded stream lifecycle, original ownership,
   one-shot read and failure cleanup; this turn does not adopt that change.
4. Only after this edge is understood should the owned writer-membership/origin
   and terminal/file-integrity join advance. No live-256/full-matrix rerun is
   justified by the current diagnostic results.

The primary outcome remains `file_writer_provenance_unproven`, integrity
`unproven`, `GuiIntegrityOk=false`, and `FAIL_PROTOCOL_OR_INTEGRITY` / `fail`.
No failure witness, full provenance, payload-integrity join or canonical route
proof is asserted.

## Preservation

One previously dirty test helper changed; all 143 other baseline dirty files
remain byte-for-byte unchanged. This document is the only new workspace file;
diagnostic scripts/results are under the ignored artifact directory. Production,
native factory/processor, previous artifacts, test DLL and previous verification
seal are unchanged. `git diff --check` passes. The new verification seal is
`artifacts/collector-capture-20260925/FINAL-VERIFICATION.json`.

No build, portable rebuild, aggregate lane, real GUI/network run, full matrix,
commit, push or acceptance change was performed.
