# Canonical witness work-profile implementation

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: bounded test-only profiling implemented and focused controls passing.
No native rerun, GUI/NKN run, saved-capture evaluation, production change,
acceptance change, commit or push was performed.

## Basis and scope

The latest fresh native attempt is documented in
`docs/file-transfer-canonical-deferred-observation-native-20260929.md`.
It completed state capture, then the original PowerShell selection callback
returned null after `frontier_evaluation_budget` at 20,003.9223 ms cumulative
work and 1,842,885 visits. Its exact expensive subphase remains unmeasured.
The apparent C# stage `selection-validation` does not imply its subsequent
currentness walk executed: null selection short-circuited it.

Entry verification matched all 5,308 saved pins and 592 dirty entries.
Implementation changes four existing test files and adds two test support
scripts. All production files, original checks and limits remain unchanged.

## Diagnostic contract

The existing test-only C# observer now samples cumulative stopwatch ticks and
visit counts at its eight existing stage calls. Slots, in order, are:
ownership, availability, state-capture, selection-callback,
selection-validation, witness-capture, final-currentness, accepted.
Each populated row is `[workTicks, visits]`; null means not observed.
Subtract consecutive samples to measure intervening work, not absolute time
since callback entry. Work already consumed before resolution remains included.

A canonical-profile-only BeforeResolve hook inserts fixed checkpoints into the
original in-memory PowerShell function. It does not edit its source file.
Unique literal seams are required; missing, duplicated or repeated
instrumentation is rejected. Removing the marked additions restores the
original function text exactly.

| Callback phase | Work beginning at checkpoint |
| --- | --- |
| 0 | Initial failure/view snapshot |
| 1 | Full input-row materialization and identity parsing |
| 2 | Failure-row validation and receipt matching |
| 3 | Owned-row scan, after chosen receipt/source validation |
| 4 | Lifecycle links and witness selection |
| 5 | Completion marker and snapshot-boundary matching |
| 6 | Boundary serialization and watchdog coverage |
| 7 | Stream coverage and final owned-row selection |
| 8 | Final snapshot/currentness comparisons and selection construction |

Callback slots 0–8 retain only the first sample per phase. Slot 9 is the
one-shot terminal success/failure sample, carrying the active phase.
Each callback row is
`[phase, workTicks, visits, materializedRows, ownedRows, selectedCount, terminal, failed]`.
The report retains the existing stopwatch `Frequency` for conversion.
Materialized/owned/selected counts are collection sizes, **not** a count of
every record examined in later scans. A terminal sample in an unfinished
phase distinguishes partial row materialization from completion of that phase.

Storage is fixed at eight two-counter stage rows and ten eight-counter
callback rows. It holds no event text, receipts, object identities or native
handles. There are eleven static PowerShell checkpoint sites (nine entries,
success, failure), no per-record logging and no additional budget visits.
The observer does not start/stop work, validate currentness, create a
capability or invoke authority. Samples freeze after first rejection or
callback termination. Invalid/late calls and diagnostic storage failures do
not change resolution.

The original catch records its omission before the diagnostic call; its null
return is unchanged. New diagnostic calls catch their own failures. The
observer API and storage compile only with `NLINK_TEST_ATTEMPT_DISPATCH`.
The original 20,000-ms work limit, 2,097,152-visit limit, capacity bounds,
1–64 witness cap and every identity/scope/duplicate/order check are unchanged.

## Verification

Final state: **13 distinct tests passing**, on Windows PowerShell and PowerShell
7 where parameterized. There were 17 executions: 15 passed, with two initial
failures in the new control script corrected and superseded by focused reruns.

- Initial focused run: six passed, two failed. The control's negative-input
  array needed parentheses around string concatenation; without them
  PowerShell appended a fourth array element and also tested the valid original.
- Callback-only follow-up: two passed. Eight controls per host compare the
  actual original/profiled callbacks using inert custody doubles, including
  budget failures at snapshot and partial-row positions, an original scope
  rejection and deliberately throwing diagnostic recorders.
- Existing resolver controls preserve twelve rejection/success paths, visit
  counts, cleanup and first rejection, plus fixed storage, terminal freezing,
  invalid calls, broken storage and absence from normal compilation.
- Actual attempt-profile compilation passed on both hosts.
- The relevant canonical composition contract lane ran once after stabilization:
  seven passed, preserving original calls, receipt/pair identity, failed route
  verdict, output gates, geometry/profile checks and cleanup.
- Source parsing and whitespace checks passed for all six implementation/test
  files. Test assembly compilation succeeded; existing repository
  compiler/analyzer warnings were emitted.

TRX files, source hashes and final preservation checks are under
`artifacts/canonical-witness-work-profile-20260929/`.
No unrelated aggregate test lane ran.

## Next step and remaining boundary

Rebuild and freeze the current test harness, then perform strict out-of-sandbox
preflights before **one fresh targeted offline native 256 MiB attempt** using
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`.
Do not replay the saved capture or automatically retry. Inspect the new stage
and callback samples to identify the dominant operation before proposing an
optimization. Profiling itself adds some bounded work; it does not establish
that the path fits the original budget.

The full live GUI matrix remains gated. The previous native witness count was
zero, same-pair join and output commit were false, and the original route
verdict was FAIL_PROTOCOL_OR_INTEGRITY. Nothing in this diagnostic implementation
promotes that result or treats SHA-correct fallback as route-cycle acceptance.

## Preservation

The post-change audit checked all 5,308 preceding pins: only the four specified
test sources and rebuilt Core test DLL changed. All other pinned files and
pre-existing dirty entries matched. The dirty set is now 595 entries (the prior
592 plus two support scripts and this report); branch and HEAD are unchanged.
The final verification seal carries these updates and the new test artifacts.
