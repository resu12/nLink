# Canonical native capture: measured hold-budget exhaustion

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.

Result: **one offline native 256 MiB attempt failed at the same first repair edge**.
The new bounded diagnostics captured all five records and now measure why the
original hold window expires. No retry, production change, acceptance relaxation,
commit, push, live NKN run or desktop GUI matrix followed.

## Measured timing

Original receiver `process-output/helper-39924.stdout.log`, lines 6549-6553, records
the three held starts, first exact-tail-ready observation and enforced expiry.
For the oldest held frame, chunk 12477:

| Boundary | Monotonic tick (ms) | Original budget remaining (ms) |
| --- | ---: | ---: |
| Held | 455017671 | 4000 |
| Exact tail ready observed | 455020390 | 1281 |
| Existing expiry enforced | 455021671 | 0 |

Thus **2,719 ms elapsed between the original hold start and the ready snapshot**;
only **1,281 ms remained**. Expiry occurred at the recorded original deadline,
not a renewed or shortened deadline. The other original held starts/deadlines are
455017734/455021734 for chunk 12478 and 455017750/455021750 for chunk 12480.
Ready and expiry both report all three frames still held, totaling 66,861 retained
bytes including descriptors. No release had advanced this schedule.

These differences use the scheduler's monotonic millisecond clock. They measure
held-before-readiness time, not a separately instrumented pure prefix-drain duration.
The ready snapshot follows the service-gated read and is not atomic with that read.

## Original runtime sequence and first missing edge

The receiver reaches `exact-tail-repair-and-recent-grace` at stdout line 6548.
There are zero sparse-shape rejection lines. Operational-log line 101744, receiver
writer `p39924r4b37915e6571452c`, sequence 42422, corroborates the exact
`next=12476, highest=12483` sparse shape.

The same writer records last prefix progress at 04:56:58.4722284 UTC and the
current recent-progress deadline at 04:56:59.4722284. Line 101749 (sequence 42425)
arms the original 750 ms stall grace at 04:56:59.4731332, due at
**04:57:00.2231332 UTC**. Line 101752 (sequence 42428) reports the current recent
wake rechecked with `stall_grace_armed`. Three replaced recent wakes remain stale,
diagnostic-only observations.

The expiry capture's UTC sample is **04:56:59.7715728**, about **451.5604 ms before
that recorded stall deadline**. Receiver disposal's log attempt at line 101754
(sequence 42429) is 04:56:59.7823510, 440.7822 ms before it. These are UTC sample/log
comparisons, not an atomic translation of production deadlines into the monotonic
hold clock. They independently agree with the exhausted fixed hold budget.

Receiver stderr preserves the original chain:

```text
offline_schedule_unavailable; stage=exact-tail-repair-and-recent-grace
  offline_schedule_failed
    TimeoutException: offline_schedule_hold_expired
```

There is no stall-grace recheck record. Sender efficiency line 101800 (writer
`p53876rdbbbbe5baa3d4d03`, sequence 59357) reports zero repair raw bytes, zero repair
batch frames and no pending repair. The first missing edge remains the original
current stall-wake recheck and authorized repair before fixture teardown, not a
later publication or acceptance decision.

Current source `RunInboundV6FrontierStallGraceWakeAsync` waits until its original
`DueUtc`, respects lifetime cancellation, validates current scope, and only then
sends the frontier request. The fresh evidence is consistent with that behavior:
the fixture expires before the due time. It does not establish a production
authority/repair defect or justify shortening those runtime grace periods.

## Preserved identity, execution and cleanup

The sender arms once at offset 268,219,392 (stdout line 6391). All eight original
epoch-zero, single-chunk descriptors 12476-12483 match receiver envelope lengths,
hashes and the required drop/hold/pass actions. The bounded diagnostic output is
complete and one-shot; it is scheduling evidence only, never route authority.

Entry audit: all 4,443 prior pins and 562 dirty entries matched. Rebuild passed
with zero warnings/errors, with all five pinned binary hashes unchanged. Both
escalated preflights passed against 4,445 frozen input hashes: no conflicting
processes, Debug overrides allowed, developer mode off, high-resolution clock,
and over 245 GB free. Saved 69 distinct timing/regression checks (70 executions)
and earlier controls were verified, not redundantly rerun.

One create-new attempt marker; **0 passed, 1 failed**, duration
**206.5836319 seconds**. The sender's peer-readiness failure, receiver/sender writer
drains (lines 101883 and 101895), post-drain diagnostics, incomplete writer-shutdown
wrapper failure, and later canceled wrapper connection at 199,631 ms are retained
as downstream evidence. Retained primary analysis never started; full-file/native
same-pair acceptance was not established.

Escalated cleanup at 04:59:27 UTC found no remaining app/test/native peers, only
four build servers. The 268,435,456-byte source was available for exclusive read;
no destination `.bin` or `.part` remained. No cleanup deleted user files.

Run evidence: `artifacts/canonical-hold-timing-native-20260929/`.
Original fixture directory:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-b437e60ad5314f3f9e12e2c077f019c1/`.
`first-edge-observations.json` retains exact source line references, matched frames,
timing records and selected runtime fields. Final verification pins the full
capture and confirms preservation of prior work, source, and binaries.

## Recommended next step: scheduling contract review only

Review whether a bounded, test-only pre-tail prefix-drain barrier can make the
fixture exercise the intended original grace/repair sequence within the existing
four-second per-frame holds. The review must identify an exact original receiver
committed-prefix witness and a safe handoff point after the needed prefix bytes
have been delivered but before the first tail frame enters the fault schedule.
It must address cancellation, process/context identity, bounded retention, and
deadlock avoidance without nesting scheduler/service locks or creating authority.

This is a candidate for review, not an approved scheduling implementation. Do not
infer a required timeout increase from this run. Keep the original hold starts,
limits, release order, runtime grace periods, payload provenance, single authority
evaluation path and acceptance contract unchanged. Do not add repair injection,
queue/concurrency tuning, automatic retry or diagnostic-driven pacing. If no safe
barrier fits that contract, report the fixture limitation rather than weakening
the proof. A further native run follows only a separately reviewed bounded change.
