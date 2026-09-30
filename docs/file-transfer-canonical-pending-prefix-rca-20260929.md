# Canonical tail: pending-prefix rejection RCA and fix plan

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Result: **one offline 256 MiB capture failed before primary analysis; the exact
rejection state was captured successfully**. No retry or readiness change followed.

## Confirmed first failure

Original receiver output `process-output/helper-27348.stdout.log`, line 6442,
contains exactly one new rejection diagnostic:

```text
next=12023; highest=12025; snapshot=available
pending_count=3; next_pending=1; written_length=12484; next_written=0
observation_present=0; observation_closed=-1; observation_status=none
source_bound=0; repair_bound=0
```

These scalar values were captured in the existing service lock at the failing
read, not reconstructed from a later commit log. The current frontier chunk was
in the pending-write set and not yet marked written. It was still in the
write/commit phase; the snapshot does not distinguish waiting for the write gate,
an active disk write, or waiting to reacquire the commit lock.

The unchanged test helper accepts a settled prefix as not ready only when
`highest == next - 1`. Here `12025 != 12022`, so that branch did not apply. It then
threw because `12023 != 12476`, even though the frontier was **below** the tail and
its next chunk was already pending. The exception name
`offline_schedule_frontier_advanced_before_sparse_shape` misdescribes this state.
The outer failure remains `ordinary-frontier-tail-highest-final`.

This confirms the pending-prefix explanation for **this run**. It does not
retroactively supply the missing exact snapshots from earlier runs, establish a
production transport/authority defect, or prove that every remaining prefix chunk
was available or could finish within the current deadlines.

Production ordering agrees with the snapshot: `HandleInboundV6SparseChunkBatchAsync`
adds pending indices and advances highest-received under the lock at lines
5352–5354, then performs asynchronous writing outside the lock; it removes pending
indices, marks written bits and commits the contiguous frontier afterward. No
runtime change is indicated by this first failure.

## Run and supporting evidence

Run evidence: `artifacts/canonical-rejection-native-capture-20260929/`.
Fixture output remains at the unchanged test's unique path:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-569ae24424464095bb27a569651c849d/`.

- Entry matched all 4,336 prior pins and 557 dirty entries. Debug rebuild passed
  with zero warnings/errors and all five pinned binaries unchanged.
- Both escalated preflights passed: 4,338 frozen hashes, unchanged dirty set, no
  conflicting app/test peers, developer mode off, Debug overrides allowed,
  high-resolution clock and over 247 GB free. Environment isolation was process-only.
- The runner verified pinned prior composition/admission/readiness evidence and
  the 39 diagnostic/regression checks without rerunning unchanged test lanes.
- One new create-new marker and one selected native test: **0 passed, 1 failed**,
  duration **208.9920353 seconds**. This was the offline named-pipe carrier, not
  live NKN or a desktop GUI matrix.
- Sender line 6372 records one arming at offset 268,219,392. All eight epoch-zero,
  single-chunk descriptors for 12,476–12,483 match receiver envelope hashes/lengths
  and the intended drop/hold/pass actions.
- Receiver commit log line 100437 records `12023 / 12022`; subsequent commits
  reach `12038 / 12037` at line 100452, writer sequence 41784. Terminal line 100601
  retains 258,865,152 bytes / 12,038 chunks. These are not the rejected snapshot.
- The primary analyzer never started. There is no completed file pair or native
  same-pair witness. Writer drains are preserved at lines 100614 and 100626;
  downstream incomplete-shutdown, post-drain write diagnostics and the wrapper's
  canceled connection at 201,823 ms remain evidence, not substitute root causes.

Cleanup inspection found no remaining app/test/native peers; only idle build
servers remained. The retained 268,435,456-byte source permits exclusive read,
and no destination `.bin` or `.part` remains.

## Bounded fix plan — not implemented in this capture

1. Keep the scalar-only `MatchesSparseShape` contract and exact-tail success
   (`next=12476, highest=12483`) unchanged. In the canonical locked read, add a
   separate **not-ready** case based on the actual current context, not a parsed
   diagnostic string: `0 <= next < 12476`, `next <= highest < 12484`, the canonical
   written-bit array is present with length 12484, its bit at `next` is false,
   and the pending-write set contains that exact `next` index. Retain the existing
   geometry, currentness and unbound-observation guards.
2. Do not require exactly three pending writes or hard-code the observed indices.
   Do not accept a prefix merely because some other chunk is pending. A missing
   current-frontier write, inconsistent indices, invalid/missing written state,
   advanced frontier, small profile or earlier bound observation must not enter
   the new case; retain the existing predicate/guard outcome instead. Genuine missing-frontier states remain distinguishable
   when the current chunk is no longer pending.
3. Add focused tests for the captured case and those negative boundaries. Prove
   that the new result is false, never success; removing the frontier from pending
   must restore rejection. Keep all original exact-tail and diagnostic guarantees.
   The existing diagnostic fixture using a valid pending prefix will intentionally
   become not-ready; move its rejection/output assertions to a still-invalid
   advanced-frontier fixture instead of weakening them. Do not reset observations, release frames, dispatch
   repair, call an evaluator or grant any coordinator/runtime authority.
4. After focused regression passes, separately gate one rebuilt offline capture
   with a fresh frozen manifest and create-new marker. Keep the existing 600-second
   outer, 180-second carrier, eight-second edge, four-second holds and 20,000 ms
   native-primary work limits. No automatic retry or throughput tuning. A later
   timeout or another first proof edge requires its own evidence, not a larger limit.

Only this report and ignored runner/evidence files were added during the capture.
Source, pinned binaries, timing, authority and acceptance rules remain unchanged.
Prior failed captures and accumulated work are preserved by the final audit.
No commit, push, live-256 run or full GUI matrix occurred; those live gates remain
pending.
