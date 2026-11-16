# Canonical deferred-write observation timing

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: **test-only diagnostics implemented; 89 distinct tests passed, zero
failed/skipped. No native 256 MiB rerun or live GUI/NKN run in this step.**

## Scope and unchanged contract

The preceding fresh native attempt failed at
`fresh-sparse-write-deferred / offline_schedule_deferred_outside_recent_grace`
before retained analysis. Its matching deferred-write log attempt preceded the
recent-grace deadline, but the follow-up fixture check ran after expiry. Retained
log flush/read cost remains a hypothesis; this step does not claim to identify
the original unmeasured delay or validate the witness-capacity fix natively.

Changes are limited to the Core test harness: a bounded timing collector, probes
in `AttemptOfflineLatePublicationProgress`, and deterministic diagnostic tests.
No production coordinator, service, transport, logger, authority evaluator,
acceptance rule or deadline was changed. The original retained-log read, newline
split, geometry/reason predicate and first-match order remain in use. No extra
log reads, state evaluations, retry, clock reset or schedule release were added.

The existing strict comparison is still `now >= due` -> reject. The same current
recent deadline is read under the original service gate; recorded timing values
are never consulted for acceptance. Clock evaluation order is preserved: current
UTC time is sampled before the deadline property is read. Missing or malformed
diagnostic metadata does not affect the original matching predicate.

## Recorded evidence

The collector is created only for the canonical tail profile. It retains six
first-sample boundaries (cursor start/end, release start/end, recheck requested,
service gate entered), first/latest read points, bounded counters and total/max
read duration, last poll end, first matching event's numeric writer sequence and
writer-attempt UTC ticks, and pre-release/recheck current-time/deadline scalars.

The read interval covers the existing retained-tail call as a whole, including
its writer flush and retained-file scan. It does not separately measure flush
versus scan, or claim that returned tail character count equals disk bytes read.
The read-end to match/poll-end points expose matching overhead; requested-to-gate
points expose final service-lock wait. Writer-attempt time remains diagnostic
evidence, not the actual write/commit boundary or native publication authority.

No event text, file contents, app object, receipt or capability is retained.
Missing samples are null, not invented timestamps. A failed start sample cannot
reuse a prior poll's start. Formatting and output occur after the existing
carrier cleanup, outside service/scheduler gates, as one bounded JSON line:
`carrier-canonical-deferred-write-timing=...`.
Output is consumed before I/O and never retried; diagnostic clock/output failures
cannot replace the original match result or exception. The collector contains
fixed storage regardless of poll count.

## Verification

24 distinct diagnostic tests cover:

- Timely writer/read/check, timely writer with late read completion, timely read
  with late gate check, and writer attempt at the deadline. Late checks still
  reject; synthetic writer timestamps never grant acceptance.
- Exact before/at/after deadline with and without diagnostics.
- Both canonical and small-profile matching geometry/reason; first-match order;
  absent, negative, overflowing, duplicate and nonnumeric timing metadata.
- Read failure and cancellation preserve the exact original exception and one
  read call; clock failure cannot change matching or grace rejection.
- First-sample preservation, aggregate accounting, unavailable read-start
  samples, 10,000 polls in one record below 3,000 characters, and one-shot output
  failure outside the test gate.

The directly related `AttemptCanonicalTailCarrierTests` contract lane ran once
after stabilization: **65 passed**. It covers original geometry, repair/write
shape, deferred predicate, prefix cancellation/currentness, publication-window
shape and dispatch admission. No native transfer is launched by this lane.

Results: 23 initial focused tests, followed by 67 cases (65 carrier cases plus
the strengthened output-failure test and a new missing-sample regression).
Final review also separated successful-read accounting from diagnostic-clock
availability; that focused failure control was rerun once. **89 distinct cases,
91 executions; all passed.** Unchanged focused cases were not rerun after the
small follow-ups. Build warnings are outside changed files;
changed-file warning and whitespace checks passed. No aggregate analyzer/Smoke
suite or full GUI matrix was run.

## Next gate

Rebuild, freeze the updated source and test-assembly hashes, run strict
out-of-sandbox preflights, then exactly one fresh canonical native 256 MiB
attempt. Read the new timing line from that original process lifetime. Stop at
the first new failure; do not replay the preceding capture or widen deadlines.

If the next attempt again misses recent grace, distinguish retained-read time,
matching cost, final gate wait, and an already-late writer attempt using the
recorded scalars before proposing a narrowly scoped harness fix. If the test
advances, use its first new proof-chain edge instead. Native-scale witness capture
and the live GUI matrix remain unverified/gated until their own contracts pass.

## Preservation

Entry verification matched 5,221 pins and 588 dirty entries. Expected existing
changes are only `AttemptOfflineLatePublicationProgress.cs` and the rebuilt Core
test DLL; new files are the collector, its tests, this report and step artifacts.
Independent process inspection found no remaining project test process. No commit,
push, deletion, live connection or acceptance change was made.

Artifacts: `artifacts/canonical-deferred-observation-timing-20260929/`.
