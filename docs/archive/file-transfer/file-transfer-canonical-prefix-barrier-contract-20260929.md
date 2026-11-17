# Canonical prefix barrier: scheduling contract review

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: **recommended for bounded test-only implementation, subject to the checks
below; not implemented or native-validated in this review**.

## Decision and scope

The latest single native capture measured 2,719 ms from first held receipt to
tail readiness, leaving 1,281 ms of the original four-second hold. It expired
before the recorded original stall-grace deadline. Review a one-shot sender-side
barrier **before the first actual tail read**, released only by a scoped original
receiver committed-prefix observation. Do not delay an already-received frame's
admission to `AttemptOfflineFrameSchedule` or reset its `Held.Started`.

This changes fixture ordering, not runtime authority or acceptance. It applies
only to the Debug/Windows canonical 256 MiB offline profile. No Core/Infra code,
production wire format, runtime timer, queue/concurrency, chunk size, wallet,
installer, GUI setup, screen sharing or other fixture profile is in scope.

## Reviewed insertion point and rejected shortcuts

Use `RetainedRuntimeReadGate.ReadAsync` in the fourth retained native reader,
immediately before its first nonempty underlying read at **offset 268,283,904**
(`12476 * 21504`). Keep the existing pre-arm at offset 268,219,392 unchanged: that
earlier hook selects single-chunk packetization but must not wait for prefix commit.

Source inspection supports this point:

- `AttemptCanonicalTailProfile` arms three chunks early. The existing
  `OldThreeSegmentBatchReachingPrearmEndsBeforeTail` controls cover the old captured
  three-segment batch; it ends before the tail, never spanning the barrier.
- `TryPrepareChunkBatchV4Async` reads chunks outside the service gate. Its caller
  schedules a prepared batch before preparing the next one. Earlier submitted
  prefix writes can therefore continue while the first tail source read awaits.
- `AttemptNknPipeClient` has an independent reader. A wait in the source wrapper
  does not hold its `writes` semaphore or `deliveries` lock.
- Native source ownership checks before/after the underlying read remain intact;
  the barrier is not a replacement source, file reopen, or runtime-owner grant.

Rejected alternatives:

| Alternative | Why it is not this contract |
| --- | --- |
| Wait at pre-arm offset 268,219,392 | Withholds the last three prefix chunks needed to satisfy the receiver predicate. |
| Wait after complete tail receipt but before schedule admission | Moves the effective hold start past physical receipt and weakens the existing hold contract. |
| Await while holding the pipe write semaphore, scheduler gate, or service gate | Can block the prefix writes/feedback or commit needed to release the wait. |
| Poll only sender `RemoteNextExpectedChunkIndex` | The counter can also advance from a frontier request; it is not an exact original receiver commit witness. |
| Named event, log line, elapsed-time guess, or timing diagnostic alone | Does not bind the observation to the current original receiver/context and peer endpoint. |

## Exact receiver witness

Before waiting for any suffix frame, the original receiver helper polls a
read-only predicate under its existing service gate. It must retain and recheck
the original context, data-session reference/generation and coordinator-published
fallback route/runtime identity established by the existing fixture. Checks use
existing currentness predicates, not a new authority evaluation or mutable shadow
owner. The local lease, peer process and original carrier must still be current.

Readiness requires all of the following in one service-gated snapshot:

- Original session and transfer IDs; fixed geometry 268,435,456 bytes, 21,504-byte
  chunks and 12,484 chunks; manifest received; nonterminal and not user/peer paused.
- `NextChunkIndex == 12476`, `ChunksTransferred == 12476`, and
  `PullHighestReceivedChunkIndex == 12475`.
- `BytesTransferred == ReceiverSparseBytesWritten == 268283904`.
- `ReceiverSparseChunksPendingWrite.Count == 0`, `BufferedBytes == 0`, and the
  expected sparse-written bitmap shape (12,484 entries).
- Existing `RequireUnboundObservation` still passes: no closed/bound prior
  frontier episode or captured repair. Do not construct or bind an observation.

Do **not** require earlier prefix bits in `ReceiverSparseChunksWritten` to remain
true: `CommitInboundV4ContiguousWrittenLocked` clears each bit as it advances the
contiguous frontier and committed bytes. The snapshot above uses those committed
fields, not mere physical receipt, queued writes or a UI progress projection.

An unfinished valid prefix returns not-ready. Wrong identity/geometry, retirement,
terminal/pause state, an already-admitted tail or prior bound repair episode fails
the fixture. Do not relax the exact boundary to `>=`, fabricate missing prefix
progress, or repair it from the helper. Any scheduler checks occur outside the
service lock. No service/scheduler lock nesting is permitted.

## One-shot peer-bound handoff and sender gate

Prefer one dedicated test-only named pipe over extending the byte carrier's frame
types or adding a generic messaging layer. Establish it before releasing payload
flow, with `CurrentUserOnly`, one instance, a name scoped to the existing attempt
request, and endpoint verification through the retained `AttemptPeerProcessChannel`.
Never reopen a peer by PID or trust the pipe name as identity.

The receiver exports only the fixed prefix observation and identity scalars after
leaving the service gate. Maximum encoded body: **512 bytes**. Bind schema/version,
both original app request IDs, expected session/transfer, fixed geometry and exact
committed boundary. Do not serialize Core authority tokens, context objects or
payload. Read a bounded length-prefixed body plus EOF (at most one extra byte to
detect trailing input); malformed, truncated, oversized, foreign or extra input
fails closed. The producer closes this dedicated pipe after its one write, without
waiting for tail receipt or an acknowledgment. There is no reconnect or retry.

Sender pipe reading must already be asynchronous before the first tail read waits.
Recheck the retained peer process, local lease, original carrier and original
sender source/context scope when consuming the message. Only this attempt's
one-shot gate opens; it is not route publication, runtime unlock, repair dispatch,
liveness deferral or a success receipt. The receiver continues checking its
original context in the existing suffix/readiness sequence. A context retirement
after the snapshot is not made current by the message; original Core guards and
fixture currentness checks must still reject the run.

The first tail read must enter at exactly offset 268,283,904. Seeking or an empty
read does not consume the gate. Partial reads after that legitimate entry use the
same consumed gate; they do not reopen it. An initial offset past the boundary,
overlapping first-read claims or missing gate binding fails. Recheck cancellation
and native source currentness after the await and before the original read.

**Cache-bypass guard is mandatory.** `LoadChunkBytesForSendAsync` can return cached
bytes without invoking the wrapper. The existing canonical descriptor observer
must reject an ordinary tail unless this original reader has entered the exact
barrier and consumed the valid prefix handoff. Merely having armed packetization
is insufficient. Keep that guard before physical tail output; metadata failures
stay latched and are surfaced by the existing descriptor checks. Do not flush or
disable caches or alter batching to manufacture a read.

Bounds: one pipe, one pending first-read waiter, one scalar record, zero new
payload copies, and no additional read factory or frame queue. All waits use the
already-running carrier/context cancellation and original lifetime. Do not start
a fresh overall timeout after prefix draining. Dispose/failure cancels the wait
and closes only owned resources; it must not signal success to unblock cleanup.

## Progress and timing limits

The receiver must run its prefix predicate **before** the existing
`WaitForCanonicalSuffixAsync`; otherwise sender and receiver would wait on each
other. After the one export, keep the current exact-tail readiness, original
single-chunk repair, recent recheck, publication window and releases unchanged.

The dependency is one-way: earlier prefix sends/writes complete independently;
receiver observes their committed boundary; sender performs its first tail read;
the original receive-side fault schedule then begins on physical receipt. Neither
pipe setup/EOF nor prefix commit may depend on that first tail read. Missing
prefix work, retirement or a prior repair episode must terminate within the
existing lifetime, not trigger synthetic progress or additional repair.

This is feasible by source inspection, **not a promise of a passing native run**.
For scale, the unchanged 1,000 ms recent grace, 750 ms first stall grace, 1,000 ms
post-repair recent grace and 800 ms copy schedule already total about 3,550 ms;
the existing 200 ms copy/hold reserve raises that nominal budget to 3,750 ms.
These are relative scheduling bounds, not a measured or guaranteed end-to-end
duration. OS scheduling, repair and writer overhead still matter. Original
`AttemptLatePublicationWindow.Create` and the per-frame four-second expiry remain
the decisive checks. No minimum sleep, hold renewal or diagnostic-driven pacing.

## Bounded implementation and verification gates

Implementation may touch only canonical fixture wiring, the retained-reader
wrapper, canonical profile/descriptor guard, receiver prefix predicate and a small
test-only handoff owner. Reuse retained process/source checks; do not introduce a
second session/route model. No production source change is justified by this RCA.

Focused tests must cover:

1. Prefix and pre-arm reads continue; the first tail read waits; exact handoff
   releases once; empty/partial/seek/past-boundary and cached-tail bypass cases.
2. Exact receiver committed shape versus pending writes, inconsistent counts/bytes,
   wrong geometry/identity, pause/terminal, replacement and prior frontier episode;
   cleared committed-prefix bitmap bits must not cause a false rejection.
3. Peer endpoint/request mismatch; bounded framing, EOF, duplicate/trailing input,
   early close, missing peer, cancellation, source/context retirement and disposal.
4. Real asynchronous prefix writes and receiver feedback can complete while the
   source gate waits; neither a held lock nor an unsent partial prefix batch is a
   dependency. Cover all three pre-arm batch alignments.
5. Original descriptor/hash provenance, native reader count, four-second starts,
   exact expiry, one-shot diagnostics, release order and cleanup remain unchanged.

After focused checks, run only directly affected existing canonical/source/pipe
and hold-budget tests once. Rebuild and review the diff/pinned binaries before a
separate single gated native capture. That capture must demonstrate the prefix
witness precedes the first physical tail, no tail bypass, unchanged original
deadlines and advancement of the first missing repair edge. A new later edge is
an RCA result, not permission to weaken the full-file/native same-pair contract.

## Review verification

No source or binary was edited, no tests were rerun, and no native/live/GUI attempt
was made. Existing test evidence was preserved; the newly proposed behaviors do
not yet have implementation results. Entry matched all 4,476 prior hashes and
563 dirty entries. Exact source anchors, arithmetic and final preservation hashes
are in `artifacts/canonical-prefix-barrier-contract-20260929/`.

Recommended next step: bounded test-only implementation of this reviewed contract
and its focused regressions, without starting another native capture in that step.
