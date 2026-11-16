# Canonical prefix fix: native rerun and remaining evidence gap

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Result: **one fresh offline 256 MiB attempt failed before primary analysis**.
The settled-prefix fix is not sufficient to establish native tail readiness.
No automatic retry or code change followed the failure.

## Verified execution

Entry matched all 4,284 pins and 554 dirty entries. The Debug build succeeded
with zero warnings/errors and no changes to the five pinned build binaries.
Both escalated preflights verified 4,286 frozen hashes, the unchanged dirty set,
no conflicting app/test processes, Debug overrides allowed, developer mode off,
high-resolution clock and over 247 GB free. Process-only environment isolation
did not read/log secret values or change user/machine settings.

The runner verified the pinned 59 composition, 10 admission and 25 prefix/lifetime
checks; unchanged lanes were not rerun. A fresh create-new attempt marker was
used. The earlier failed run and consumed runner/marker remain unchanged.

Selected test:
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`.
TRX: **0 passed, 1 failed**, duration **210.7637538 seconds**.
This was the named-pipe offline carrier, not live NKN or a desktop GUI run.

Run evidence: `artifacts/canonical-prefix-native-characterization-20260929/`.
The existing test emitted its new unique fixture directory under its unchanged
artifact root:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-1d348dad980942039a5c29c05c9123ee/`.

## What the new run establishes

- Sender output line 6372 records one arming at offset 268,219,392. Its eight
  single-chunk, epoch-zero tail descriptors at 12,476–12,483 match all receiver
  envelope hashes and lengths. Drop/hold/pass actions retain the intended shape.
- The receiver still rejects at `ordinary-frontier-tail-highest-final`, with
  `offline_schedule_frontier_advanced_before_sparse_shape`, now at the patched
  helper's line 261. The name alone does not prove that the frontier advanced.
- Last saved sparse commit: operational-log physical line 100395, writer sequence
  41754, `next=12020, highest=12019`. Terminal line 100543 retains 258,478,080
  bytes / 12,020 chunks. These are settled log observations, not the exact values
  read by the failing predicate.
- The primary analyzer never started. There is no retained-analysis receipt,
  successful late-publication result, completed file pair or native same-pair
  witness. No later ownership/resolver/work-budget gate can be assessed here.

The wrapper's pending payload connection later canceled after approximately
202.5 seconds, and the GUI fixture reported incomplete writer shutdown. These
remain downstream failures. Original writer drains are preserved at physical
lines 100556 and 100568; post-drain write-failure diagnostics are also retained.

## Why a second readiness change is not yet justified

Inspection of `HandleInboundV6SparseChunkBatchAsync` in
`SessionFileTransferService.PullTransferSessionV6.cs` shows distinct phases:
under the original lock it adds pending chunks and advances highest-received
(lines 5352–5354); it then awaits the write gate and writes outside that lock;
only afterward does it remove pending chunks and advance the committed frontier
under the lock. Therefore `highest > next - 1` can represent an in-flight write,
not necessarily a missing prefix chunk. This ordering is existing runtime
behavior, not a proposed production change.

Read-only synthetic calls to the actual rebuilt helper confirm:

| Input | Result |
| --- | --- |
| canonical `12020 / 12019` | false: settled prefix correctly waits |
| canonical `12020 / 12022` | throws: could be pending work or a real gap |
| canonical `12476 / 12483` | true: exact tail shape |

These calls do not capture the native failing snapshot. Pending-write state is
a plausible explanation, supported by the source ordering, but is **not proven
as this run's cause**. The scalar pair alone cannot distinguish pending contiguous
work from a genuine gap. Do not simply permit every `next < B` state or change
the existing negative tests to accept it.

## Recommended next step

Add one bounded, test-only rejection diagnostic at the existing locked read:
record the exact `next`, `highest`, pending-write count and membership of `next`,
the current written bit for `next`, and existing observation status/binding
booleans. Check bounds before indexing, retain only scalar facts, and preserve
the original exception and decision. Do not enumerate an unbounded pending set,
wait, reset an observation, alter logging in production, invoke an evaluator,
or grant authority from these diagnostics.

Cover single emission, unchanged exception/rejection, invalid-index safety and
bounded output with focused tests. Only then separately gate one fresh native
capture to identify the exact state. Any later readiness change must distinguish
genuine gaps using observed existing state and retain all original limits:
600-second outer, 180-second carrier, eight-second edge, four-second frame holds
and 20,000 ms native-primary work. No automatic retry or throughput tuning.

## Cleanup and preservation

Escalated read-only inspection found no remaining app/test/native peer processes;
only idle build servers remained. The retained 268,435,456-byte source permits
exclusive read; no destination `.bin`/`.part` remains. This is cleanup of a failed
attempt, not successful full-file verification.

Only this report and ignored run/evidence files were added. Source, pinned binaries,
timing, authority and acceptance rules are unchanged. Previous evidence and
accumulated work are preserved by the final hash/status audit. No commit, push,
live-256 test or full GUI matrix occurred. Those live gates remain pending.
