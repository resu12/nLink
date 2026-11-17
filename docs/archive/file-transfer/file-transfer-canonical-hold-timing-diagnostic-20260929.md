# Canonical hold timing: bounded diagnostic implementation

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.

Implemented the diagnostic-only next step from
`file-transfer-canonical-tail-hold-expiry-rca-20260929.md`. This does not resolve or
reclassify the prior native failure. No new native, live NKN or GUI attempt ran.

## Change and evidence contract

`AttemptOfflineFrameSchedule` retains five fixed scalar slots, canonical profile
only: the original held frames at relative indices 1, 2 and 4; the first observed
exact-tail-ready transition; and the first enforced hold expiry. It retains no
payload, callback, context, authority token, or new unbounded collection.

Each output line starts `carrier-canonical-hold-timing=` and contains JSON:

- `Boundary` is `held`, `exact-tail-ready-observed`, or `expired`.
- `ChunkIndex` identifies the original absolute chunk for `held`; otherwise -1.
- `TickMilliseconds` uses the scheduler's existing `Environment.TickCount64`
  clock. Expiry records the actual `now` argument used by the existing expiry path.
- `StartedTick` is the original selected frame's `Held.Started` for `held`, or the
  oldest currently held start for ready/expiry. `ExpiresAtTick` is that original
  start plus the unchanged 4,000 ms limit. Both are null if no frame remains.
- `UtcTicks` is sampled while recording, for approximate correlation with runtime
  UTC logs. It is not an atomic clock translation or a reconstructed UTC hold start.
- `HeldCount`, `HeldBytes`, `Failed`, and `Disposed` describe the scheduler snapshot.
  Expiry captures before the original `Fail` clears the frames, so `Failed` can
  still be false in that record; the `expired` boundary is the expiry evidence.

The ready capture occurs immediately after the existing successful service-gated
read returns, acquiring only the scheduler gate. The two locks are never nested.
It is a later scheduler snapshot, not an atomic snapshot with the service read:
expiry can race between them. Empty/failed/disposed evidence remains explicit and
must not be treated as a renewed budget or authority.

The original frame-start assignment, timer, `Check`, expiry predicate, failure
exception, release order and cleanup are unchanged. Capture performs no `Check`,
`Expire`, release, wait, evaluator call, timer creation or deadline reset. The
existing `RunAsync` finally writes after owner cleanup, outside both locks. Slots
are consumed before formatting/output; at most five lines are attempted, in fixed
slot order (not necessarily capture-time order). Output is never retried. Read or
output failures are swallowed only within the diagnostic paths and cannot replace
the original scheduling failure. Missing diagnostics do not imply success.

No route ownership, retry/repair, production progress, acceptance, publication,
queue, batching, throughput or grace-period logic changed.

## Verification

Artifacts: `artifacts/canonical-hold-timing-diagnostic-20260929/`.

- New focused checks: **12 passed, 0 failed**, `focused.trx`.
- Strengthened the ready-once assertion to attempt a second capture after expiry;
  reran only that changed test: **1 passed**, `ready-once.trx`.
- Directly affected existing scheduler, canonical scheduling/hint/cleanup, and
  original hold-budget regression lane: **57 passed, 0 failed**, `regression.trx`.
- Total: **69 distinct tests**, 70 passing executions. No skipped tests.

Coverage includes all five records and bounded output, original starts/deadlines,
repeated capture/output, exact expiry at -1/0/+1 ms, a passive overdue-frame read,
unchanged drop/pass/release order, cancellation/disposal, noncanonical silence,
broken diagnostic storage, and a throwing writer outside the scheduler lock.
The existing three-/four-second real-timer tests also passed.

The test project was rebuilt by the focused runs; no broader aggregate lane was
needed for this test-only change. Changed-file whitespace checks passed. The
entry audit matched all 4,427 prior pins and all 560 dirty entries; before-images
of both edited helpers and the five pinned binaries are retained. Final hashes,
dirty-state comparison, production-binary preservation and seal are recorded in
the verification artifacts. No commit or push was made.

## Next step

Gate one fresh offline native 256 MiB capture against this rebuilt test assembly,
with strict preflights and no automatic retry. Verify the actual held starts,
ready-time remaining budget (`ExpiresAtTick - TickMilliseconds`, not clamped),
and expiry before revising the RCA. A negative or unavailable ready-time budget
must remain negative or unavailable. Correlate UTC cautiously; the monotonic
records alone do not timestamp the production grace or repair callback.

Any change to fixture scheduling or acceptance still requires a reviewed contract.
Do not increase the four-second cap, shorten original runtime grace periods,
inject repair, or promote this timing evidence into authority. Full-file/native
same-pair acceptance and subsequent live/GUI gates remain pending.
