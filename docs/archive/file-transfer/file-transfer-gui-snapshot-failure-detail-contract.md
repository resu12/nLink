# C1x: bounded GUI Snapshot failure detail

2026-09-15. Follow-up to the failed C1w live attempt at clean checkpoint
`ed8ca1a6`. The original GUI classifier reported an invalid Snapshot boundary,
but its persisted result discarded the boundary's decision reason and input
index. This reporting gap prevents precise diagnosis from the saved result.

## Scope

Retain one diagnostic projection from the already-selected
`FirstUnresolvedBoundary` in the original invalid-Snapshot classification branch.
Do not evaluate again, select another boundary, read more source, replay saved
views, change verdicts, or start retained analysis after a failed GUI exit.
This projection is not a local-publication companion, receipt or authority token.

The optional `CoordinatorSnapshotEvidenceDiagnostic` on the classification is
copied to `nonAuthoritySnapshotEvidenceDiagnostic` in the GUI error summary.
It contains only schema version 1, `authorityEffect=none`, boundary ID/hash,
decision reason, event name, and the original zero-based evidence input index
with an explicit index domain. No raw log line, arbitrary error text, path,
private evaluator object, operation list or source payload is exported.

Admission is finite: boundary ID/hash at most 128 characters each; reason at
most 256; event name at most 128. Text is restricted to identifier characters
(event name also allows the exact `(artifact)` sentinel). The index is an
integer in the original input range or -1 for an unlocated artifact failure.
An absent/malformed/oversized projection is omitted, never truncated or used to
replace the primary failure. Projection exceptions are diagnostic-only. The
fixed schema remains below 2 KiB UTF-8 JSON; no collection or extra scan is added.

For immutable inputs the index addresses the original sorted/unique path
concatenation, not a per-file line number or compact-array position. For live
inputs it addresses the original app-log plus process-output concatenation.
Index -1 means no source location was supplied by the original evaluator.

## Verification and exclusions

Reproduce the current omission with the original classifier/request/summary
projection in offline tests. Verify the detailed reason, artifact sentinel,
located index, serialization and absence on ineligible results on PowerShell
5.1 and 7. Check unchanged primary identity/counts/classification and the two
existing GUI evaluator invocations per classification. Boundaries with missing,
oversized, malformed or throwing diagnostic fields must preserve the original
failure and omit detail.

The sender's missing writer drain is a separate open evidence-finalization
finding. This change does not alter shutdown, drain requirements, timeouts,
preflights, runtime/bootstrap/route authority, or acceptance selection. C1w
remains FAIL/incomplete proof. A new live attempt is not part of this step.

## Verification result

The omission was reproduced on both hosts before implementation. After the
bounded projection was added, 16 admission/omission controls per host passed,
as did separate full/live-input selection and fresh real-evaluator malformed-
input controls. Each original GUI classification retained its two evaluations;
the test did not replay the C1w live artifact. The real fixture's
`snapshot_instance_missing` reason is a fixture result, not the unknown C1w cause.

The focused classification/companion/enrichment/writer lane passed 8/8, followed
by 2/2 directly relevant Snapshot-branch and durable-finalizer regressions.
No aggregate suite or live test ran. Exact commands, results and remaining
sender-drain investigation are in
`artifacts/publication-evidence-finalization-20260915/RESULTS.md`.
