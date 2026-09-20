# Publication boundary clock and stall-successor contract

2026-09-15. Reviewed HEAD `2691fc70f98d668bae943ebd41c6f8540156ec57`
plus A1-A6. This finalizes the next diagnostic design, not live enablement,
runtime timing changes, or a new acceptance disposition.

## Decision

Keep the original UTC deadline. **Do not convert D to another clock.** Require
both its original UTC predicates and conservative elapsed-counter predicates
in a separately versioned diagnostic profile. Clock disagreement or an interval
that straddles a boundary omits positive evidence; it never extends a deadline.

Record actual same-deadline stall successors at their existing assignment site,
keeping the original stall root and deadline immutable. Do not infer successors
from timer callbacks, matching generation numbers, later logs or transport state.

The first bounded implementation is an internal immutable boundary stamp and pure
timing predicate with deterministic tests. It adds no observer activation or
service hooks. Its verification and the subsequent opt-in service integration
are recorded below.

## Source findings that constrain implementation

These are the pre-integration findings; A7b below records the implemented capture hooks.

- `FileTransferCoordinator.Snapshots.cs:2385` computes decision time as the
  maximum of UTC milliseconds and the previous value plus one. This deliberately
  ordered Snapshot field can run ahead of wall time; it is not C's boundary clock.
- `SessionFileTransferService.FallbackFrontierAuthority.cs:894` acquires the UTC
  value used by the strict fallback path to create its stall descriptor. The
  generic path has a separate acquisition at `PullTransferSessionV6.cs:6448`.
  `ShouldSendInboundV6FrontierRequestLocked` receives that value; sampling again
  inside `StallArm` cannot recover the earlier acquisition.
- `PullTransferSessionV6.cs:5333-5342` commits contiguous chunks, calls the old
  injected observer, then acquires the runtime progress UTC. Preserve that runtime
  acquisition and assignments; do not substitute the old observer's clock.
- `PullTransferSessionV6.cs:5893` captures the selected frame under the gate;
  delivery binding occurs in a later gate. `SessionFileTransferService.cs:20313`
  adopts the accepted completion before the observer sees it. Neither log time
  nor send/queue acceptance replaces these boundaries.
- `PullTransferSessionV6.cs:7675-7696` creates a current same-deadline stall
  successor with only generation/highest changed, but has no observer hook.
  The recent-recheck path releases the gate before sending and reacquires it at
  lines 8122-8133: a successor can exist before that recent/stall link is recorded.
- **Write-path coverage is incomplete.** `PullTransferSessionV4.cs:23797` commits
  through the V4-compatible sparse path without the publication observer. Its
  fallback-v6 stall refresh at line 23907 is real. The paused-progress flush at
  line 24118 also commits through the shared helper. A V6-only clock hook cannot
  establish complete ordinary-write coverage for these paths. This is a capture
  gap, not a new runtime RCA or proof of the historical run's execution path.

Paths above are under `src/nLink.Core/FileTransfer/`. A6's passing results remain
unchanged; they exercise the explicitly injected V6 fixture, not live timing.

## Clock profile and ownership

Proposed profile ID: `frontier_publication_boundary_utc_qpc_v1`. Existing
`offline_injected_v1`, receipt/lifecycle versions and hash domains are unchanged.
The A6 mapper must continue to reject this new profile until its own consumer
contract is implemented. A profile label alone never grants live proof eligibility.

On Windows, Microsoft's guidance identifies Stopwatch's high-resolution basis
as QPC, independent of UTC, with a fixed frequency. It documents one-tick
cross-thread ordering uncertainty and one-tick interval quantization uncertainty;
QPC includes sleep time. These are counter measurements, not a guarantee of
perfect physical-clock accuracy. [Microsoft timing guidance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps).

Use a high-resolution Stopwatch/QPC source with positive cached frequency. Other
platforms or a non-high-resolution source are unsupported in this first profile.
Do not add a clock service, change thread affinity, inspect processor counters,
calibrate the system clock, or change runtime timer APIs.

Each immutable stamp carries process incarnation/clock-instance identity,
observation identity, capture sequence, frequency, raw UTC ticks, and counter
values acquired immediately before and after that one UTC read. For a runtime
UTC acquisition, bracket the existing read and pass its exact value onward.
There is no second UTC read or inferred UTC/counter offset.

The producer privately binds those stamps to the actual source/descriptor/frame/
accepted-envelope objects. A caller-supplied stamp, copied DTO, matching PID, or
JSON digest cannot recreate that binding. The later payload channel must bind
the process incarnation to its native writer completion. Until then all output
remains opt-in local diagnostics with `LiveProofEligible=false`.

Require valid UTC/counter ranges, one clock identity/frequency, increasing capture
sequence, nondecreasing UTC and nondecreasing raw counter reads. Reject a reversed
bracket or backward sample, even where rejection is conservative. Do not repair
samples with maximum-so-far logic, average the bracket, or select a favorable end.

## Exact timing predicates

Let A be the stamp bracketing the original runtime stall-start UTC acquisition.
Its exact descriptor must satisfy `Dutc = A.Utc + 750 ms`. Let W1 be the first
positive contiguous commit after the owned recent-recheck/stall chain. Freeze
its stamp and `Putc = W1.Utc + 1000 ms` once. No later write or successor moves them.

For ordered stamps X and Y, define conservative counter-interval bounds:

```text
Lower(X,Y) = max(0, Y.CounterBefore - X.CounterAfter - 2)
Upper(X,Y) =        Y.CounterAfter  - X.CounterBefore + 2

ElapsedBefore(X,Y,milliseconds): Upper(X,Y) * 1000 <  milliseconds * Frequency
ElapsedAtLeast(X,Y,milliseconds): Lower(X,Y) * 1000 >= milliseconds * Frequency
```

The fixed two-counter-tick allowance conservatively combines the two documented
one-tick uncertainties. It enlarges uncertainty, never the allowed deadline.
It is not a tunable millisecond tolerance. Keep raw integer units and checked
wide arithmetic (Int128 is sufficient for signed-64-bit counter inputs and these
fixed durations); do not divide/truncate elapsed milliseconds or use floating
point. UTC additions must separately fit DateTimeOffset's range.

After identity, lifecycle, accepted-completion and basic clock validation:

1. If `W1.Utc >= Dutc`, pre-deadline progress is unproven. Otherwise require
   `ElapsedBefore(A,W1,750)`; failure/ambiguity omits with incomplete timing evidence.
2. Every covered subsequent commit, frame capture, delivery binding and completion
   must have UTC strictly below Putc. A sample at/after Putc is outside that bound.
   Also require `ElapsedBefore(W1,sample,1000)`; if UTC fits but this does not,
   report incomplete timing evidence, not recovered progress or a runtime timeout.
3. For accepted completion C, classify before-original-deadline only when
   `C.Utc < Dutc` AND `ElapsedBefore(A,C,750)`.
4. Classify within-observation-bound only when `C.Utc >= Dutc` AND
   `ElapsedAtLeast(A,C,750)`, after step 2 passed. Otherwise the D boundary is
   ambiguous or the two clocks disagree: omit the positive disposition.

An exact D/P equality never becomes a pre-deadline/in-bound success. Sequence and
original object custody establish causal order; close counter values do not.
The recent root similarly retains its original progress UTC and exact one-second
deadline; stall creation needs the existing UTC relation and a conservatively
elapsed recent-grace interval from that bound progress stamp. No retrospective
recent-progress clock anchor is permitted.

These predicates define agreement of captured UTC and elapsed measurements. They
do not prove that no system-clock adjustment happened between observations, or
that a remote application applied the frame. Long suspend gaps cannot restart
P. No timer, sleep, retry or fabricated end sample may establish timed absence;
normal finalization without coverage through P remains incomplete evidence.

## Boundary placement and supported paths

| Boundary | Required capture point | Preserve |
| --- | --- | --- |
| Repair's committed progress | Existing progress-UTC read after real contiguous commit, bound to the subsequently admitted repair progress | Exact progress receipt/source and existing runtime timestamp |
| Original stall start | Bracket the caller's original UTC read; carry that stamp into the actual descriptor assignment | Original generation, start and D; do not resample in StallArm |
| W1 and later writes | Post-commit gate boundary using the caller's progress UTC and actual before/after/count/highest/bytes | First advance and frozen P; no reflection-created progress |
| First covering frame | Original frame-selection gate before asynchronous send | Exact frame/source; never substitute a later successful frame |
| Delivery binding | Existing issued-token binding gate | Earlier frame source and original token |
| Completion C | After original accepted Coordinator adoption, before leaving that completion gate | Original accepted envelope/token; zero extra Authorize/Apply calls |
| Stall successor | Immediately after the existing current-wake assignment, under the same gate | Actual predecessor/successor objects and unchanged runtime preconditions |

The V6 and V4-compatible sparse commit callers need explicit path coverage before
live use; do not change V4's runtime `PullLastCommittedProgressUtc` behavior to
make it resemble V6. A pause/flush during an open observation invalidates that
observation; it cannot supply a positive resumed write. Unsupported commit paths
must close diagnostic coverage as incomplete instead of silently skipping writes.

Diagnostic sampling failure must not change runtime settlement or suppress/grant
a send. Contain new diagnostic errors without wrapping or changing existing
Coordinator decision/error policy. Keep capture bounded and avoid payload file
I/O or new serialization work under the gate. Export remains a separate review.

## Stall-successor chain

Keep the original root descriptor G0 and A/D immutable. Before W1, maintain an
observed tip and an ordered successor history. Only the actual existing refresh
assignment may advance this diagnostic tip. Require:

- The predecessor is the exact observed tip and the successor is the actual
  newly assigned current wake; both remain bound to the current Coordinator
  source, data-session incarnation and original frontier.
- Generation advances exactly one and highest strictly increases. All other
  descriptor members, original start and deadline stay identical; identity and
  reference-sensitive source checks remain strict.
- Each successor consumes the shared bounded lifecycle capacity. No independent
  unbounded list, eviction, capacity increase, repair queue or observer-owned
  runtime token is introduced.

The original recent recheck may bind to G0 or its verified current descendant,
because a post-write refresh can occur while the existing asynchronous recheck
is between gates. Preserve that exact recheck-time tip, then any further verified
successors to the tip at W1. The root/current/at-W1 generations have distinct roles;
never overwrite the root generation with the latest one.
If W1 arrives before that actual recheck-time binding, the episode stays
unsupported; never backdate the recheck or reorder writes to salvage it.

W1 freezes the tip/history as it consumes the original frontier. A normal new
recent wake at the advanced frontier is not a successor of this obligation.
Highest-only writes and any later wake cannot roll P or replace W1. A first
covering frame rejection/source change still permanently prevents a positive
receipt, irrespective of subsequent valid wakes or successful sends.

An exact repeated observation of the same already-bound successor is idempotent
and consumes no slot. Stale or foreign notifications that did not assign the
current wake cannot advance or reopen the diagnostic chain and have no runtime
effects. An actual current-wake mutation with missing/conflicting predecessor,
changed deadline/source or exhausted coverage closes it as incomplete/invalid.
Do not invoke authority again to repair an evidence gap.

## Bounded implementation and verification order

1. Add only immutable clock stamps and the pure predicate. Test D/P equalities,
   integer/fractional-frequency comparisons, bracket uncertainty, UTC/counter
   regression and disagreement, identity/frequency/sequence substitution,
   overflow and suspend-sized gaps. No service activation or payload changes.
2. Add opt-in boundary and actual stall-successor capture, preserving the old
   injected profile. Fresh service tests must include both sparse-write paths,
   pre-W1 zero-advance highest-only successors, a successor before recent recheck,
   multiple successors, stale/foreign/duplicate/gapped notifications, source/pause
   ABA, flush invalidation and unchanged enabled/disabled runtime decisions/sends.
3. Extend fresh private validation/witness mapping for this distinct profile and
   successor links within the same 64-witness budget. Keep native primary custody,
   original evaluations and verdict/count invariance. Missing V4-compatible or
   successor evidence cannot fall back to the old profile.
4. Only then address the separately owned payload/parent channel and correctness
   decision. No live-256 test, rebuild/checkpoint or historical reclassification
   follows merely from this clock contract being settled.

## Design verification, not implementation verification

Source call sites and Microsoft's clock guidance were reviewed. An independent
integer arithmetic model exercised 20 timing cases at frequencies 1000, 3001 and
10000000: **60/60 passed**. This checks the core D/P predicates, not the service,
recent/stall lifecycle, clock acquisition or primary authority. The cases include
before/late positives, exact D/P, ambiguity/disagreement, wide brackets,
regressions, identity/frequency/sequence errors, zero frequency, UTC overflow,
near-Int64-limit counters and a suspend-sized gap.
The case/result record is
`artifacts/frontier-publication-clock-contract-20260915/RESULTS.md`.

No runtime code or tests were changed in this review; the prior A6 7/7 test gate
was not rerun. Implementation must supply the focused production-helper and real
service tests above. Existing live `FAIL/incomplete_proof` remains unchanged.

## Implementation checkpoint: A7a, isolated timing helper

`FileTransferCoordinator.FrontierPublicationClock.cs` now provides the immutable
boundary stamp and pure timing result. It validates every stamp's range, identity,
frequency and ordering before checking original recent/D/P deadline derivation,
recent-grace elapsed agreement, W1 and accepted-completion timing. Checked Int128
comparisons preserve fractional-frequency and two-counter-tick uncertainty without
rounding UTC or moving a deadline. The input scan is capped at 64 stamps including
the five non-commit boundaries; this is not a replacement for the later shared
lifecycle/witness budget. Covered commits precede their selected frame; validated
ordering makes completion the latest endpoint and bounds intervening samples too.

The helper performs no clock acquisition or service activation. Its values do not
authenticate a process, source, boundary role, first frame or accepted completion;
those remain separately owned capture/validation obligations. Every result keeps
`LiveProofEligible=false`. Existing injected profiles, writer/hash domains and
the primary mapper are unchanged; no runtime caller uses the new helper.

Focused production-helper verification covers **115 distinct cases**. The first
104-case run passed. An expanded run passed 114/115; its one added positive fixture
incorrectly placed the counter inside P's uncertainty window. Only that fixture
sample changed, and its targeted rerun passed. No production change followed the
104-case passing run. Detailed commands and retained red/green records are in
`artifacts/frontier-publication-clock-20260915/RESULTS.md`.

Next is step 2: opt-in real-boundary and actual stall-successor capture, including
V4-compatible sparse writes and flush invalidation, with fresh noninterference
tests. Payload/parent custody and acceptance gates remain later work. No portable
package rebuild, live-256 run, historical reclassification, commit or push occurred.

## Implementation checkpoint: A7b, opt-in actual boundary capture

`SessionFileTransferService.FrontierPublicationBoundary.cs` adds a separate private
in-memory observation. Only focused fixtures attach it; there is no default/live
activation, writer export, parent channel or primary consumer. It never produces
the old injected receipt. `LiveProofEligible` remains false for every result.

The existing V6 and V4-compatible sparse progress UTC reads and both stall caller
UTC reads are bracketed by native high-resolution counters. They retain their
exact original runtime UTC value. In particular, V4 still does not assign
`PullLastCommittedProgressUtc`. The initial real repair write's stamp is bound to
the original admitted repair receipt, and then to the exact recent/stall objects.
Frame capture, delivery binding and completion sample inside their original gates;
completion follows accepted Coordinator adoption without another authority call.

Source projection uses value comparisons and passive mutation subscriptions, not
new hashing, serialization or payload I/O under the gate. Source/pause/data ABA,
flush, missing clock/chain, rejected first frame, and unsupported completion close
the observation. Normal close without accepted completion remains incomplete.
The first frame cannot be replaced by a later successful send.

The actual post-assignment refresh hook records each predecessor/successor pair
in the same 64-entry lifecycle, retaining original root/D, recheck-time tip and
W1-time tip separately. Exact duplicates consume no slot; stale/foreign unassigned
notifications are ignored. A conflicting duplicate, changed deadline, generation
gap or exhausted coverage closes incomplete. W1 must still see the exact current
tip; an unobserved clear/replacement cannot be rescued by matching frontier values.
W1 before the actual recheck binding stays incomplete and cannot be backdated.

Fresh fixture coverage includes both actual sparse-write implementations, multiple
zero-advance highest successors, successors before the real recheck continuation,
pre-recheck W1, mutation/flush/rejection closure, missing current wake, and enabled/
disabled runtime comparisons. The private opt-in observer has a test-only stall
boundary callback to force the inter-gate races; it is unset in normal operation
and released on closure. Malformed descriptors and capacity pressure are explicit
negative controls, not claimed real received-chunk evidence.

Verification: **20 distinct focused cases** (19 new cases plus the unchanged
injected-profile noninterference regression); final artifacts and superseded
fixture/setup failures are recorded in
`artifacts/frontier-publication-boundary-20260915/RESULTS.md`.
This is local capture verification, not live NKN/GUI correctness or native writer
custody. Next is the new profile's private closed-capture validation and bounded
witness mapping; payload/parent and acceptance remain later gates. No portable
rebuild, live-256 run, historical reclassification, commit or push occurred.

## Implementation checkpoint: A7c, private validation and diagnostic line mapping

`SessionFileTransferService.FrontierPublicationBoundaryValidation.cs` validates a
closed positive observation against its retained producer objects and the unchanged
pure timing predicate. The admitted immutable repair continuity stamp is retained
at the repair boundary, so later contract retirement cannot erase that binding.
Validation does not reacquire clocks or invoke Coordinator authority evaluation.

Full and compact canonical projections share the same frozen producer, input hash
and capture identity. Candidate bytes must exactly match that private capture;
caller-provided JSON, hashes, flags or another capture cannot replace it. Copying
validates each retained immutable line once, bounded to 8192 lines, 262144 code
units per line and 8 Mi code units in total. No serialization or file I/O is added
under the service gate.

The mapper requires one structurally consistent writer sequence, exact repair,
recent/root/successor/write links, original accepted completion and matching state
identity, and owned stream anchors. Full-input searches remain unique. Repeated
identical writer records are idempotent; conflicting copies, ambiguous links and
interpreted duplicate fields omit the capture. Opaque coverage diagnostics retain
their exact bytes, including existing repeated non-identity fields.

Compact selection retains earlier exact repair links, the relevant diagnostic
episode and all owned Coordinator records from repair progress through completion,
within the unchanged 64-witness limit. Unrelated before/after history is not charged
as required episode evidence; a genuine required episode over 64 is omitted.
Each witness has a one-based `InputLine`, writer sequence, raw line and content hash.
`InputLine` is an index in the supplied frozen input, **not a native physical-source
origin**. This mapping does not authenticate the input's native writer custody.

### Race-fixture correction

A7b's test callback originally ran immediately after root assignment inside the
creation gate, before the original-root diagnostic. Its reentrant sparse write
could replace the current descriptor before that log was emitted. That forced
fixture ordering was not evidence of a production writer gap: the original root
assignment and diagnostic are inside the same runtime gate.

The existing test-only callback now runs after the request attempt returns and
before the recent recheck reacquires its gate. Both real sparse-write paths map
the resulting root -> successor -> recheck-time tip successfully. An explicit
input-negative test removes only the original-root event and verifies omission;
the successor cannot stand in for it. No runtime logging repair, deadline change,
extra authority evaluation or new fallback was justified or implemented.

Verification: the stabilized gate passed 18/18, the bounded-copy follow-up passed
2/2, and the actual inter-gate follow-up passed 9/9. Together these cover 23 distinct
focused cases, including the 18 new validation cases and both enabled/disabled
runtime comparisons. Full/compact producer parity, input hashes and every exact
witness line/hash were independently checked for all six positive projections.
Details: `artifacts/frontier-boundary-validation-20260915/RESULTS.md`.

Every projection and validation result keeps `LiveProofEligible=false`,
`NativeWriterCustodyEstablished=false` and `PublicationCompletionIssued=false`.
The existing injected profile, native primary mapper, original evaluations and
saved live verdict/counts remain unchanged. Next is the separately reviewed
writer-owned payload/parent channel and consumer contract, followed by its
acceptance gate. No portable rebuild, live-256 run, historical reclassification,
commit or push occurred.

## Implementation checkpoint: A8a, independent producer payload preparation

The [payload/channel review](file-transfer-frontier-payload-channel-contract.md)
identifies the A7c input-log dependency, the private export-projection boundary,
and the distinction between app-process and GUI-worker custody. A7c's completed
input JSON remains unchanged and is not reused as a writer-bound producer payload.

A separate private preparation now produces cached immutable canonical UTF-8
bytes from the closed, validated producer before the source stream closes. It
retains original clock samples and exact root/successor/recheck/write roles, uses
explicit scalar/evidence-token projections instead of raw runtime token objects,
and references the original accepted Snapshot identity/hash without duplicating
its payload. Its 262144-byte limit is enforced using strict UTF-8; returned arrays
are copies and equal-byte/foreign wrappers do not establish producer ownership.

The focused final gate passed 21/21: 19 new preparation/encoding cases and both
existing runtime noninterference comparisons. Six positive payloads cover both
write paths, multiple successors and the actual pre-recheck inter-gate race;
each is prepared before stream closure and then checked against A7c's completed
mapping. Details: `artifacts/frontier-payload-preparation-20260915/RESULTS.md`.

This implements preparation only, not native writer/source binding or a parent
channel. There is no runtime activation/persistence caller, new operational marker,
primary consumer or acceptance change. All eligibility/custody/issuance flags stay
false. Next is the native app/writer request and bounded transport contract,
including catalog admission, work limits and completed-source ownership. No
portable rebuild, live-256 run, historical reclassification, commit or push occurred.

## Implementation checkpoint: A8b1, isolated native app attachment

A separate tool-side request now binds a launched app-role process using its
actual native handle/incarnation and a still-live native parent. Its fixed header
and exact strict UTF-8 run/scenario registration are bounded to 64 + 4096 bytes.
One attachment/completion attempt/parent take, cancellation, exit and immutable
metadata checks prevent reuse of an incomplete or foreign request. No file I/O,
payload serialization or Coordinator evaluation is part of this handshake.

All four focused test rows passed on Windows PowerShell 5.1 and PowerShell 7:
23 real child-process scenarios per host, plus native parent-death checks and
registration/exited-process controls. Details and the initial fixture-barrier
correction are in `artifacts/frontier-native-app-20260915/RESULTS.md`.

This is a standalone native process-role primitive tested with PowerShell app
surrogates, not nLink GUI integration or producer/clock/writer/source custody.
Its separate attachment receipt keeps all live/writer/publication flags false.
The [payload/channel contract](file-transfer-frontier-payload-channel-contract.md)
records the remaining catalog/admission/work bounds and actual producer/writer/
GUI linkage. Existing channels, primary results and the historical live verdict
remain unchanged. No portable rebuild, live test, commit or push occurred.
