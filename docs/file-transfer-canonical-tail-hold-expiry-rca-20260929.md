# Canonical tail: readiness advanced, hold expiry before repair

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Result: **the pending-prefix fix passed the native tail-readiness gate; the single
offline 256 MiB attempt failed at the next repair stage**. No retry followed.

## What advanced

Original receiver output `process-output/helper-30364.stdout.log` reaches
`ordinary-frontier-tail-highest-final` at line 6418 and then
`exact-tail-repair-and-recent-grace` at line 6522. There is no sparse-shape rejection
diagnostic. Operational-log line 101678, receiver writer sequence 42404, records
the exact `next=12476, highest=12483` shape. This corroborates the stage transition;
it is not merely a successful physical send or SHA check.

All eight original epoch-zero, single-chunk tail descriptors still match the
receiver's envelope hashes/lengths and drop/hold/pass actions. The sender armed
once at offset 268,219,392. The readiness fix did not grant repair or route authority.

## First remaining failure

Receiver stderr begins with:

```text
offline_schedule_unavailable; stage=exact-tail-repair-and-recent-grace
  offline_schedule_failed
    TimeoutException: offline_schedule_hold_expired
```

The fixed per-frame scheduler failed while the helper was waiting for the exact
single-chunk repair and its recent-grace observation. The eight-second edge timeout
did not fire; the publication owner and native-primary work gates were not reached.

The same receiver writer records the following sequence (UTC):

| Evidence | Time / state |
| --- | --- |
| Last contiguous prefix progress | 04:27:29.8696622, frontier 12476 |
| Exact tail commit log attempt, line 101678 | 04:27:29.8807072, highest 12483 |
| Current recent-progress deadline | 04:27:30.8696622 |
| Stall grace armed, line 101681 | Start 04:27:30.8776657; due 04:27:31.6276657 |
| Current recent wake rechecked, line 101684 | `stall_grace_armed`, writer sequence 42410 |
| Helper disposal log attempt, line 101688 | 04:27:31.4440279, writer sequence 42411 |

Thus disposal was logged **183.6378 ms before the recorded stall deadline**. This
is a comparison of recorded UTC fields, not a measurement of the monotonic hold's
exact start/expiry. The three stale recent wakes report `wake_generation_replaced`;
the current wake reports rechecked/stall-grace-armed. Stale observations remain
diagnostic-only and must not be promoted into repair authority.

Sender efficiency summary line 101734 records zero repair bytes, zero repair batch
frames and no pending repair. Receiver terminal line 101805 retains frontier 12476
and 268,283,904 bytes. The required repair advance to 12477 was not established.
The first missing chain edge is the original current stall-wake recheck and
authorized repair request before fixture expiry, not a later publication decision.

Source inspection agrees with that ordering: `RunInboundV6FrontierStallGraceWakeAsync`
waits until `DueUtc`, honors lifetime cancellation, checks current scope, and only
then calls `SendInboundV6FrontierRequestAsync`. The test scheduler starts each
four-second hold at physical receipt; `Expire` fails and clears held frames when
the original `Started` limit is reached. Prefix draining and normal grace periods
therefore consume the same finite hold window. Nothing here demonstrates a
production repair/authority defect or justifies shortening grace or extending holds.

## Evidence limits and next bounded step

The failure proves hold expiry and the logs show teardown before the recorded
stall deadline. The exact monotonic hold-start/expiry and remaining budget at the
tail-ready transition were not saved. Do not derive a precise prefix-drain duration
or a required timeout increase from the UTC comparison.

Recommended next step: add bounded, test-only timing evidence for the existing
held-frame starts/deadlines, tail-ready transition and expiry, then review whether
the offline fixture schedule can accommodate the required original grace/repair
sequence within four seconds. Capture scalar values from existing state, retain
only a fixed number of records, and emit outside locks. Do not nest the service
and scheduler locks, call `Check`/`Expire` merely to collect diagnostics, add a new
timer, renew a hold, release a frame, change readiness or invoke an evaluator.
Preserve the original expiry exception and cleanup behavior.

Focused tests should prove bounded output, unchanged expiry/release semantics,
single capture per selected boundary and diagnostic-failure isolation. Any later
fixture scheduling change requires a reviewed contract before implementation;
diagnostics alone must not pace transfers or become runtime authority. Only then
gate another fresh offline capture. No automatic retry, queue/concurrency tuning,
deadline increase, synthetic repair or acceptance relaxation is proposed.

## Execution and preservation

Run evidence: `artifacts/canonical-pending-prefix-native-20260929/`.
Fixture directory:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-52d24a1478f946d0a44857965c578c0c/`.

Entry matched all 4,389 pins and 559 dirty entries. Rebuild passed with zero
warnings/errors and all five pinned binaries unchanged. Both escalated preflights
passed with 4,391 frozen hashes, no conflicting peers, Debug overrides allowed,
developer mode off, a high-resolution clock and over 246 GB free. The runner
verified the saved 62 focused checks and earlier controls without rerunning them.

One create-new marker, one native attempt: **0 passed, 1 failed**, duration
**205.7372574 seconds**. Retained primary analysis never started; no completed file
pair or native same-pair witness exists. Writer drains, post-drain diagnostics,
incomplete-shutdown output and the wrapper's later canceled connection at 198,668 ms
are preserved as downstream evidence.

Escalated cleanup checks found no remaining app/test/native peers and an unlocked
268,435,456-byte source; no destination `.bin` or `.part` remains. All prior captures,
consumed runners and accumulated work are preserved. Only this report and ignored
run/evidence files were added; source, pinned binaries, limits, authority and
acceptance are unchanged. No commit, push, live NKN test or desktop GUI matrix ran.
Full-file/native same-pair acceptance and subsequent live gates remain pending.
