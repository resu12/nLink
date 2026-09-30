# Canonical native rerun after witness-state capacity fix

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: **one fresh offline 256 MiB attempt failed before retained analysis.
The witness-capacity fix was not exercised. No retry or live GUI matrix.**

## Gate and result

Entry verification matched all 5,184 prior pins and 587 dirty entries. The Core
test harness rebuilt successfully with zero warnings/errors; its DLL hash stayed
`36f431d4982247fd5a2f0953f7230b258ed990d83ff22dde43e8a2ce21db0802`.
Fresh frozen inputs contain 5,190 pins. Both strict preflights passed outside the
restricted sandbox, including process isolation and the saved 44 capacity and
contract tests plus eight diagnostic controls. These controls were verified from
their pinned TRXs, not rerun as unrelated suites.

Exactly one test ran:
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`.
It failed in **209.0424795 seconds**, before its 600-second outer deadline.
The 180-second carrier deadline, 1,800-second analyzer deadline, 20,000-ms native
active-work limit, 2,097,152 visits, 262,144 event rows, 65,536 metadata cells and
1–64 witness selection were unchanged. This headless local process/pipe fixture
does not contact live NKN and is not the portable GUI test.

Fixture:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-d881d17e3bbb4bad8ad6c5a6ddf81914/`.

## First failing edge

The receiver failed in `AttemptOfflineLatePublicationProgress.RunAsync` at
`fresh-sparse-write-deferred`, with
`offline_schedule_deferred_outside_recent_grace` (line 96). The exact committed
prefix was exported and consumed, and the intended eight tail frames were
described. Tail-ready observation was 63 ms after the first held frame, leaving
3,937 ms of that frame's original four-second hold budget. No hold-expiry failure
was recorded at that boundary.

The original receiver log records:

| Evidence | UTC / recorded value |
| --- | --- |
| Committed progress | 09:04:46.9225657 |
| Original recent-grace deadline | 09:04:47.9225657 |
| Cursor writer attempt, sequence 42453 | 09:04:46.9825148 |
| Matching deferred-write writer attempt, sequence 42470 | 09:04:47.6360897 |
| Original recheck writer attempt, sequence 42475 | 09:04:47.9338970 |

The matching deferred-write log attempt precedes the deadline by **286.476 ms**.
The subsequent fixture check nevertheless found the original recent-grace
deadline expired. These log-writer timestamps are diagnostic evidence, not a
replacement for coordinator timing or native custody. They do not measure the
poll's start, flush, scan completion, or service-gate acquisition times.

The sender's later `PeerReady(verified)` assertion, failed normal writer-shutdown
gate, and wrapper cancellation at 202,031 ms are downstream symptoms. The wrapper
reported `forward=not-called`; retained analysis never started. There is no
primary witness observation, authority evaluation, same-pair join, completed
destination payload or route-acceptance result to claim from this attempt.

## Bounded RCA and next step

Confirmed cause of this fixture failure: the scheduling observer did not finish
its required deferred-write observation/currentness check before the existing
recent-grace deadline. The underlying source of that delay is not yet isolated.

The relevant path is:
`ReadOperationalLogTail` -> `LocalOperationalLog.TryReadRetainedTail` ->
`RollingFileLogger.TryReadRetainedTail`. It flushes the writer, then reads/searches
retained file text under the logger gate. The final operational log is 93,276,303
bytes. Repeated retained-log polling is therefore a concrete observer-cost
hypothesis, not proof of a production coordinator or transport defect. Final file
size alone does not establish the byte count scanned by each original poll.

Next bounded step:

1. Add test-only, deferred scalar timing observations around this single edge:
   cursor capture, release, retained-tail read, match and service-gate recheck.
   Capture the original recent deadline; do not extend or replace it.
2. Add deterministic controls distinguishing a genuinely late deferred write
   from a timely write whose observation finishes too late. Preserve fail-closed
   behavior, original event identity and no runtime/publication authority.
3. Only after that evidence isolates the cost, make the smallest test-harness
   observation-path fix if justified, run its directly affected controls, rebuild,
   and schedule one fresh canonical attempt. Do not replay this capture, widen
   deadlines, increase work limits, or change runtime/route acceptance.

The capacity implementation's 44 passing tests remain valid, but native-scale
witness capture remains unverified. The full live GUI matrix is still gated.

## Cleanup and preservation

Independent out-of-sandbox inspection found no remaining project/native test
processes; receiver PID 5240 and sender PID 34788 were absent. The source payload
was exclusively readable, 268,435,456 bytes, with the expected SHA256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
No completed destination or partial payload remained in the fixture tree.

After the run, all 5,184 preceding pins and all 587 pre-existing dirty entries
still matched. No production/test source was changed in this step. Only this
report and new run/evidence artifacts were added; no commit, push, deletion,
automatic retry, aggregate test suite, saved-capture evaluation or live NKN/GUI
run occurred. All route and authority acceptance rules remain unchanged.

Run artifacts: `artifacts/canonical-witness-state-capacity-native-20260929/`.
