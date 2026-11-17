# All-copy publication delay: reviewed offline contract

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Source/evidence review only; no code edits, builds, tests, analyzer reruns or
transfer runs. This proposal does not approve a new characterization or live run.

## Decision

The late-publication timing window remains plausible, but **a pipe-client-only
hold is not the correct implementation**. Use a bounded, default-off,
transport-instance test seam that delays the selected publication's queued copy
before enqueueing and its direct copies before transport locks, with the existing
direct-copy timeouts already running. Verify success at actual pipe writes.

This is a pre-dispatch scheduling fault, not a receive-side delay or a claim of
network latency. It explicitly revises the earlier candidate seam. Recommend
implementing and testing this primitive first, without starting an original-app
transfer. A distinct failure-bearing integration profile follows only after
those controls pass; the existing positive/no-selected-failure profile stays
unchanged.

The marker-retention and single-root watchdog fixes are now verified
prerequisites. They do not prove the new schedule, original primary selection,
or end-to-end diagnostic output. The latest original-pair capture still reports
`PublishedBeforeOriginalDeadline`, selected failure `(none)`, resolver calls 0,
and primary verdict `FAIL_PROTOCOL_OR_INTEGRITY`. Its custody has been retired;
this review does not reopen it or reinterpret it as a new witness.

## Findings that constrain the design

1. **Three attempts, two envelope identities.**
   `NknSignalingTransport.FileTransferTransportChannel.cs:337` creates control
   and bulk encrypted envelopes from the same original state frame. The
   lifecycle path (`NknSignalingTransport.cs:2387`) starts the queued control,
   direct control and direct bulk tasks. The two control paths serialize the
   same envelope, so equal control hashes are expected twice, not an accidental
   duplicate. The bulk envelope has a separate identity. The existing ordinary
   chunk observer cannot identify these state copies.
2. **A hidden outer lock makes the pipe hold unsuitable.**
   `NknSignalingTransport.LifecycleChannel.cs:2343` acquires `outboundSendGate`
   before awaiting `client.SendAsync` and releases it afterward. Delaying inside
   `AttemptNknPipeClient.SendFrameAsync`, even before its own writer semaphore,
   therefore holds that outer gate. `ControlOutboundQueue.cs` is actually the
   nested implementation in `NknSignalingTransport.ControlOutboundQueue.cs`;
   its single drainer awaits the same send at line 403. A pipe-level
   three-arrival barrier can deadlock: the other control copy cannot reach it.
   Moving the hold only before the outer semaphore would still stall the queue
   drainer if its selected item had already been dequeued.
3. **Do not restart the timeout after waiting.**
   `NknSignalingTransport.cs:2651` starts each direct copy's cancellation timer
   before sending. Ordinary ReceiverState uses one attempt, peer visibility and
   the default 750 ms timeout; the 2500 ms durable-checkpoint override does not
   apply (`SessionFileTransferService.cs:21297,21462`). Waiting before lifecycle
   fan-out would start these clocks late and is not equivalent to the requested
   bounded fault. The queued copy has different cancellation/bounded-wait
   semantics; do not pretend it has the direct-copy timer.
4. **Queue success is not merely enqueue success here.**
   The queue completion is set after the awaited send finishes
   (`NknSignalingTransport.ControlOutboundQueue.cs:403-409`). Peer-visible
   acceptance nevertheless excludes queue-only success. Keep both semantics.
   The lifecycle's default `DrainChildrenOnCompletion=false` also means a
   bounded wait result alone cannot prove every pending child has retired.
5. **The current probe intentionally rejects the desired outcome.**
   `FrontierDiagnosticPrimaryProbe.ps1:23,97` requires predeadline publication
   and rejects the supported unresolved failure. Broadening those assertions
   would weaken an existing control. Add a separately named profile instead.

These are source-backed design findings, not newly observed transfer failures.

## Exact proposed scope

Use only the separate small offline Debug profile: eight original 21 KiB chunks
(172,032 bytes), existing one-segment override, original native peers, real
Regular-v4 -> Tuna-v4 -> fallback-v6 transition, and existing drop/hold schedule.
Keep the canonical 256 MiB source, normal GUI path and existing small profile
unchanged. Never enable developer mode to obtain the override.

Before releasing chunk 1, bind one test owner to the original receiver process,
session, transfer, observation and unchanged stall root. Require the real recent
recheck, frontier 1/highest 7, and no StallSuccessor. Read these only under the
existing service gate; do not retain or expose mutable contexts/authority tokens
to the transport hook, invoke an evaluator, or alter runtime state.

The first eligible subsequent publication must be the actual nonterminal,
unpaused ReceiverState for the real 1 -> 4 write, highest 7, with the same source
and transport epoch. Freeze immutable scheduling facts from that actual frame:
session/transfer, frame and transport epochs, committed/highest frontiers, bytes,
credit, flags, and a bounded copy of its exact missing ranges. Bind these to the
control/bulk envelope IDs, lengths and hashes before any copy dispatch. Native
frame/receipt/coordinator checks remain the proof; these facts grant no authority.

Require the current profile's bulk destination, one peer attempt, no ACK retry,
normal cancellation and ordinary 750 ms timeout. A different copy shape, second
publication, changed source, competing state/repair, or unsupported frame is an
unavailable profile, not permission to select a later episode, suppress traffic,
force a state, or expand the fault.

## Delay placement and release

| Copy | Proposed wait point | What remains pending |
| --- | --- | --- |
| Queued control | Inside the lifecycle queue-copy method, before `QueueControlEnvelopeAsync` | Original queue-copy task; no queue item/drainer/gate held |
| Direct control | Inside the lifecycle peer-copy method, after `copyCts.CancelAfter`, before `SendEnvelopeAsync` | Original direct-copy task and unchanged timeout |
| Direct bulk | Same point, before `SendBulkEnvelopeAsync` | Original bulk-copy task and unchanged timeout |

Registration before fan-out must be synchronous and nonblocking. Carry the same
optional immutable test binding into those three paths; unselected calls and
all default/null-hook paths keep their original behavior. The awaitable seam
can delay or fault a selected attempt but cannot return a delivery result,
replace bytes, choose transport, change policy, or manufacture success. Retain
all existing pre/post-send validation; use existing validators for currentness,
not a new authority evaluation.

All selected waits use one absolute release time and caller/profile/peer
cancellation. **Never wait for three physical arrivals to release.** The task
owner must not hold the service gate, queue lock, outbound-send semaphore,
pipe writer semaphore or delivery lock while awaiting release. Release is
one-shot, with no renewal or release-on-expiry fallback. A timer firing is not
evidence that a byte was written.

Add nonblocking test-only write-attempt/completion observations to the existing
pipe client. Match exact peer, Regular kind, lane, length and envelope hash;
record completion only after the original header/payload writes, flush and peer
check succeed. Require exactly two control and one bulk physical completions,
one invocation of each lifecycle role, and the unchanged original successful
copy results. No selected write may start before release. Equal control bytes
are expected; an extra occurrence, early escape, missing role or late leftover
is failure. Do not decrypt, split, re-encrypt or edit the original envelopes.

Unrelated queued/direct control and bulk traffic must demonstrably progress
while the selected copies wait. Preserve normal contention after release;
do not change queue priorities, concurrency, retry policy or gate ownership.

## Timing and resource budget

Let S be the actual original stall start, D=S+750 ms, W the actual first
contiguous write, C the accepted completion, and Q_i each direct copy's original
timeout start. The necessary native conditions stay:

`W < D <= C < W+1000 ms`, with the producer's original UTC/QPC checks.

For the first fixed test profile, target chunk-1 release near S+250 ms and
copy release G=D+50 ms. If dispatch starts near that write, the intended wait
is about 550 ms, leaving roughly 200 ms of the normal 750 ms direct-copy budget
and 450 ms of the publication bound. These are design arithmetic, not measured
performance or guaranteed timing. Actual W and original copy deadlines govern;
do not extend any bound to accommodate scheduling lateness.

Before arming, require enough measured monotonic budget for the planned G and
100 ms completion allowance, plus a further 100 ms reserve before the oldest
remaining original data hold expires. Add only a read-only test-scheduler budget
view if needed; its existing 3000 ms per-frame expiry must not reset. Recheck
actual W and each Q_i when available. If G+100 ms is not strictly below W+1000 ms
and the applicable direct-copy deadline, abort unavailable. Once running,
expire at the earliest applicable bound, not at a fresh relative timeout.
Clock discontinuity or disagreement with the original native timing evidence
is failure; scheduling observations never replace the producer's evaluator.

Bound the new primitive to one publication, two unique envelope descriptors
(at most 1 KiB each), three role slots and three pending calls, with each selected
envelope at most 16 KiB. Count retained metadata and copy buffers together with
the existing data holds under the existing 1 MiB aggregate profile budget;
do not silently add another MiB. Keep only counters/hashes after completion.
No unbounded history, extra copies, retries or timing sweeps.

On cancellation, expiry, peer/source retirement, callback failure or competing
publication, fault/cancel the test owner and release all pending waiters as
failures. Clear held test buffers and dispose registrations. Await and observe
every task the new primitive owns, including its queue wait; prove no selected
write can occur after cleanup. Do not globally change lifecycle draining or
claim a bounded wait has cancelled its underlying operation.

## Separate integration and honest outcomes

Keep the existing original-pair no-failure probe as a control. A new, explicitly
selected failure-bearing probe must verify the actual late native outcome,
unchanged root, accepted completion, exact frame/marker/native ranges and all
three false authority flags. It must let the original primary select the first
unresolved failure. No copied failure, edited source line, externally selected
witness, second correlator or second coordinator evaluation is allowed.

The production resolver runs after original reports
(`AnalyzerOrchestration.ps1:9182`). The new probe should hash those existing report
files immediately before and after that original optional call, and compare the
original full/compact views, proof counts, failure identity/reason and verdict
within the same invocation. Preserve the existing evaluation/read counts;
do not confuse the seeded two-view test with the parent harness's existing count.
Require one original resolver attempt and 1-64 exact witnesses only when the
supported failure was actually selected. Observe normal diagnostic preparation,
output adoption and cleanup; do not synthesize their success.

A genuine late receipt can still be recovered by another unchanged closure,
lose first-failure selection, exceed the witness cap, or fail native output.
Any such result is an unmet witness milestone and must be preserved as observed.
Transfer SHA, a positive receipt or successful fault scheduling alone is not a
pass. Release chunk 4 only after original accepted closure and the copy checks,
before its unchanged hold expiry. A successful diagnostic must still leave its
original primary failure unresolved; no route-cycle acceptance follows.

## Recommended bounded sequence

1. Implement only the default-off metadata/delay seam and bounded test primitive,
   plus focused tests. Verify byte/copy-policy parity when disabled; exact
   identity and copy multiplicity; unrelated queue/control/bulk progress;
   real write/flush completion; original timeout cancellation; every failure
   and cleanup path; and no effects on non-ReceiverState traffic. Stop before
   wiring or running the original-app characterization.
2. After those controls pass, implement the isolated schedule and failure-bearing
   probe/profile with focused guard tests, preserving the existing no-failure
   profile. Review readiness against actual remaining hold budgets and output
   custody. Do not rerun aggregate suites merely to prepare this next review.
3. Only then propose one rebuilt small original-pair characterization with strict
   preflights and required escalation. Preserve the first result; no automatic
   repeat on an unfavorable race. Live-256, canonical same-pair witness and the
   full GUI matrix remain separate gates.

## Preservation

All 1,694 prior pins and 492 dirty entries matched at entry. This review adds
only this contract and ignored evidence/preservation files. Source, tests,
binaries, saved captures, runtime timers, acceptance and earlier reports remain
unchanged. No test/build was needed for this source-only contract review. No
commit or push. Evidence and the final independent check are under
`artifacts/all-copy-publication-delay-contract-review-20260928/`.
