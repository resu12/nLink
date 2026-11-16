# Native source-read validation: implementation and canonical timeout RCA

Date: 2026-09-28. Status: approved cadence implemented; 51 focused checks pass.
The single rebuilt offline canonical advanced through native source/origins,
but failed at the unchanged outer 300-second harness deadline. It is not an
end-to-end pass. No live GUI/NKN run, portable build, full matrix, commit, or push.

## Implemented scope

Approval applies to the per-line contract in
`docs/file-transfer-primary-read-validation-review-20260928.md`, including its
transient-mutation observation tradeoff. That review remains a historical record.

Only `tools/FileTransferOps/LocalFrontierPrimarySource.cs` changes production
behavior. The native reader now uses abort-only checks at its existing fragment
callback positions: closed state, the original work budget, base liveness,
scope, file cancellation, and facts cancellation. Original entry checks remain.
Each decoded line stays private until full resource validation and a final
abort check succeed. Genuine EOF is gated the same way after count/byte
reconciliation. No earlier successful line authorizes a later line.

Open/begin/completion validation, locks, reader identity, exact line references,
strict decoding/framing, all source/origin bounds, cleanup, and the original
20,000 ms active-work clock remain intact. Post-EOF origin batching is unchanged.
The shared line reader is unchanged; no extra callback or public API was needed.
Profile-only entry/fragment/commit/verified fault hooks remain excluded from the
default loader and execute inside the original clock.

A facts/pin mutation fully restored during one record's construction can be
observed less often than with full fragment-level scans. Both host controls
explicitly exercise that approved limit. Persistent mismatches must still reject
the current line before publication. This is not continuous facts/pin observation.

The existing framing fixture had stale reflection arguments for an already
four-parameter shared-reader constructor. It now supplies null for the existing
optional fourth argument; shared-reader production code was not changed.

## Verification

Artifacts: `artifacts/primary-read-validation-20260928/`.

| Check | Result |
| --- | --- |
| Initial contract regression against old implementation | Failed as expected |
| Focused native cadence/framing lane | 32 passed, 0 failed, 0 skipped |
| Directly affected diagnostics/default-loader/channel-cancellation lane | 19 passed, 0 failed, 0 skipped |
| Four affected PowerShell support scripts | Parse successfully |
| Scoped whitespace check | Passed |
| Nonincremental smoke rebuild | 0 errors, 357 warnings; 18.05 s |
| One rebuilt offline canonical | Failed: outer harness timeout after 300 s |

The 51 checks cover persistent faults at entry/fragment/commit, final budget and
liveness rejection, EOF faults, separate cancellation channels, exact references
and origins, original copied-reader/origin guards, default-loader isolation,
and framing on Windows PowerShell and PowerShell 7. No aggregate lane was rerun.
The build warning in the affected diagnostics file is the unchanged xUnit2013
collection-size assertion; the build is not warning-free.

## Latest canonical evidence

Directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-3ccd1e52b79c4d4ba079ac8b052f13b6`.
Machine-readable observations are in
`artifacts/primary-read-validation-20260928/CANONICAL-OBSERVATIONS.json`.

- `analysis-observed-source.json` records native input with **73,716 lines and
  73,716 strictly increasing positive origins**, from physical line 5 to 101,855.
  The unchanged parent writes this artifact only after native parsing and all
  bounded origin lookups succeed. This supports advancement through the previous
  source/origin blocker, not a measured final native-work duration.
- Source/retained/writer/semantic counts are 101,865 / 101,863 / 101,865 / 73,716.
  No original records or origin assertions were removed.
- Coordinator full/compact parity reports pass: both are SnapshotV1Authoritative,
  with 1,496 boundaries, 1,496 transitions, 68 effects, and three fallback-tail
  peer-proof bindings. Parity is not live route acceptance or final completion.
- The local fake GUI child completed normally in approximately 105.45 seconds,
  exit 0, with output capture complete and no timeout/deadline extension.
  Both native roles report the same 256 MiB payload, session, transfer, fallback
  route, and SHA-256. Exclusive read/write access to both payload files succeeds.
- Known primary/GUI/native child PIDs 56816, 54924, 50884, and 51644 are gone.
  This proves process cleanup was observed, not that final analyzer receipts or
  normal-exit diagnostic capture completed.

The stage snapshot reports parse_ms=66,905 and index_ms=10,301. These are analyzer
stage timings, not the native source's 20-second active-work clock. The last
recorded stage is `efficiency_summary`, started at 05:20:58.3652664 UTC, without
a completion stamp. Its elapsed_ms=0 field is an unfinished placeholder.

## First blocker and RCA

`FileTransferOpsScriptsTestBase.RunPowerShellScriptTextAsync` defaults to five
minutes for the entire harness. The canonical caller supplies no override.
After that wall-clock interval the helper kills the complete process tree.
The retained `result.txt` explicitly reports that 300-second timeout.

That outer interval includes GUI setup/transfer/finalization and then primary
analysis/reporting. The inner RouteAcceptance analyzer timeout is 1,800 seconds;
it never gets that opportunity when the outer harness kills it first. The native
20,000 ms active-work guard is a third, independent limit. None was changed.

RouteAcceptance captures primary stdout/stderr and writes the normal attempt
receipt after waiting for the analyzer to exit. The outer kill prevents that
normal finalization. The retained stdout/stderr files and attempt receipt are
absent, and temporary primary output files are empty. Exact native work ticks,
the final three-evaluations/one-read assertion, final exit code, and captured
binding/cleanup diagnostics therefore remain unverified.

Source ordering places the injected Bind probe before coordinator report
generation and the Closed probe in compact-artifact cleanup. Efficiency reporting
is called only after that compact function returns. Reaching the efficiency
stage is consistent with those guarded stages having advanced, but is not a
substitute for their missing captured records. Missing stdout is likewise not
proof that binding failed.

The efficiency stage was active near the deadline. That does not establish an
efficiency algorithm defect or a hang; the outer budget was already nearly spent.
The current retained input hash is recorded as current-only, not a proven
before/after match. Payload SHA, fallback completion, EOF, and coordinator parity
do not prove the live route cycle or a second published `file_tuna_v4` route.

## Recommended next bounded step

Review the canonical fixture's outer wall-clock budget and timeout capture
contract before another run. Keep runtime authority, source/origin assertions,
all original proof expectations, and the native 20,000 ms limit unchanged.

1. Account for GUI and analyzer/report phases under the single outer deadline;
   distinguish measured completed stages from interrupted placeholders.
2. Design test-only diagnostic retention on interruption so original output,
   work profile, and cleanup observations survive without replay, re-evaluation,
   substituted evidence, or changing the original failure outcome.
3. Explicitly review a bounded canonical-only deadline if needed. Do not silently
   raise the shared timeout, remove reports, or call the partial run a pass.
4. After any separately approved contract change, run focused lifecycle tests,
   rebuild, and execute one offline canonical. No live matrix is justified yet.

This turn stops at evidence review: no timeout extension, canonical retry,
acceptance relaxation, or further runtime change was made after the new blocker.
The starting 440-entry dirty workspace and checkpoint pins are audited in the
final verification manifest; unrelated work is preserved.
