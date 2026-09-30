# Canonical native rerun with deferred-write timing

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: **one fresh offline 256 MiB attempt advanced past the scheduling and
state-capacity edges, then failed on the unchanged cumulative witness work
budget. No retry, live GUI/NKN run, runtime change or acceptance relaxation.**

## Entry and execution

All 5,238 prior pins and 591 dirty entries matched the saved checkpoint. The Core
test harness rebuilt successfully with zero warnings/errors; its DLL hash stayed
`48603c1bb41136889db17f79b141dd454388f2b3ea6e7cedf709d49eea6c8318`.
Fresh frozen inputs contain 5,244 pins. Both strict preflights passed outside the
restricted sandbox, including prior pinned controls and the latest 89 distinct
deferred-timing/carrier cases (91 passing executions). No unrelated suite reran.

Exactly one
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`
ran. It failed after **355.8528400 seconds**. Neither the 600-second outer limit
nor the 1,800-second analyzer timeout expired. The 180-second carrier deadline,
four-second held-frame limit, 20,000-ms cumulative active work, 2,097,152 visits,
262,144 event rows, 65,536 metadata cells and 1–64 witness cap were unchanged.

Fixture:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-8db9d6d90985409f80b29e783d327255/`.

## Scheduling and transfer evidence

The canonical prefix and exact tail progressed. Tail-ready observation was 63 ms
after the first held frame, leaving 3,937 ms of its original hold. The original
late-publication episode closed within its observation bound, with three physical
copies and unchanged root. Both original processes completed the 268,435,456-byte
fallback-v6 transfer with the same expected SHA256.

The new one-shot timing record measured:

| Measurement | Result |
| --- | ---: |
| Cursor capture | 1.4849 ms |
| Selected frame release | 2.2628 ms |
| Completed retained-tail reads | 4 |
| Total retained-tail read time | 711.6447 ms |
| Maximum single read | 256.8866 ms |
| Final read | 151.4812 ms |
| Final read end to poll end | 0.4753 ms |
| Final service-gate wait | 0.0009 ms |
| Matching writer attempt before deadline | 479.0393 ms |
| Final check before deadline | 166.5483 ms |

The pre-release and recheck deadline values are identical. The recorded read
interval includes existing writer flush and retained-file scanning; it does not
separate those costs. The final returned tail is 39,054 characters, not a measure
of bytes scanned. Writer-attempt UTC ticks are diagnostic timestamps, not native
write/commit authority. Original full-width tick integers are preserved in the
raw timing line; reported differences were calculated before JSON conversion.

This confirms substantial retained-read cost in this attempt, with negligible
final matching and lock wait. It does **not** prove the unmeasured cause of the
preceding grace-period failure, or establish that scheduling is now robust. No
scheduling optimization was made; this attempt happened to pass the strict check.

## New first failing edge: witness callback work budget

The original parent analyzer read 73,957 full lines and 1,583 compact lines. Its
input hash/line count stayed unchanged. Full and compact snapshots were
`SnapshotV1Authoritative`, stream-complete, evidence-pass, with 1,507 boundaries
each; parity passed. The original parent recorded three evaluations and one
native read, with no replay or extra authority evaluation.

The one-shot witness diagnostic recorded:

- Stage `selection-validation`, reason `rejected`, custody state 3, one call.
- Cumulative work **20,003.9223 ms**, above the unchanged 20,000-ms cap.
- Visits **1,842,885**, leaving 254,267 below the 2,097,152 cap.
- Resolver omission: `Exception calling "Invoke" with "0" argument(s):
  "frontier_evaluation_budget"`.

Source inspection narrows the apparent stage: the PowerShell selection callback
invokes the original `VisitEvaluation` action and catches its exception, storing
the omission reason and returning null. C# then advances its diagnostic stage to
`selection-validation` and rejects that null selection. Its short-circuit means
this is not evidence that the subsequent C# state revalidation walk failed.
The callback's exact subphase/record at exhaustion was not recorded.

State capture completed for 73,957 original events before the callback started.
Thus the approved event/metadata capacity split cleared its earlier rejection in
this native run. It does not prove that the entire witness lifecycle fits the
shared work/visit limits. The earlier input-work profile of 14,182.6498 ms is not
a per-selection cost measurement or the final cumulative budget.

Witness count is zero, same-pair join is false, and original diagnostic output
commit is false. The selected proof failure remains
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.
The original operator verdict remains `FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`.
The optional probe fails closed, so retained analysis exits 1 despite completed
snapshot artifacts. SHA-correct fallback is not route-cycle acceptance.

## Bounded next step

Instrument the existing test-only witness observer with fixed phase work/visit
snapshots, and add a bounded callback subphase/processed-record diagnostic around
the existing selection passes. Include entry, completed state capture, callback
entry/failure, and validation boundaries. Retain scalar diagnostics only, output
after the original attempt, and preserve the original exception/null result.

Run focused controls for accounting, failure preservation and unchanged guards.
Use a fresh capture only when native timing is needed. Then optimize only the
demonstrated expensive selection operation, if justified, preserving every
original identity, duplicate, source-scope, ordering and witness check. Do not
increase work/visit limits, select fewer input identities, skip validation,
re-evaluate this saved capture or alter runtime/route acceptance.

The full GUI matrix remains gated by the incomplete witness/join contract.

## Cleanup and preservation

Native pins closed to zero, text cleared, evaluation retired. All ten original
report hashes match before/after and still match the files on disk. Independent
out-of-sandbox inspection found no remaining project/native test processes.
Both source and destination were exclusively readable, 268,435,456 bytes each,
and hashed to
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.

After execution, all 5,238 preceding pins and 591 pre-existing dirty entries still
matched. Only this report and new run/evidence artifacts were added. No source
change, commit, push, deletion, retry, full matrix, live network run, acceptance
change or saved-capture evaluation occurred.

Artifacts: `artifacts/canonical-deferred-observation-native-20260929/`.
