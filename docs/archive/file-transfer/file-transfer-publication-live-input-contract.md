# C1u: measured live-scale diagnostic input/work contract

2026-09-14, HEAD `ef78e9d7`, accumulated work uncommitted.
The user approved revising the input/work contract after the
[C1t capacity finding](file-transfer-publication-live-capacity-rca.md).
This document specifies the bounded implementation. Production limits and
ownership code are **not changed by this contract/prototype step**.

## Scope and preserved invariants

Replace the small-input assumptions in C1c/C1e/C1f/C1j/C1k with the finite limits
below. The existing default-off C1s switch and normal Phase 5 activation-cycle
scope remain unchanged. No runtime, route, transport, throughput, preflight,
watchdog, retry, installer or acceptance-policy change is authorized here.

The original source hash/count passes, GUI finalizer ownership, exact launched
worker binding, two original primary evaluations, private evaluation custody,
original terminal receipt, one-shot adoption and non-overwriting persistence
remain mandatory. No second authority evaluation, replay of saved private views,
path/digest-only capability, manufactured source event or late failure selection.
Selection stays primary-owned, at most 64 exact failure witnesses; the existing
selector still chooses only the original first reported failure. This revision
does not add a multi-failure selection feature. The companion stays 256 KiB.

## Fixed admission and work limits

All limits must hold together. Admitting the maximum of one dimension does not
promise admission at every other maximum. Over-cap work omits only the optional
diagnostic; the primary reads/evaluations/reports continue unchanged.

| Dimension | Revised limit | Known live shape |
| --- | ---: | ---: |
| Retained source bytes | 268,435,456 (256 MiB) | 108,501,606 |
| Writer evidence bytes | 268,435,456 (256 MiB) | 140,661,859 |
| Source and writer records, each | 262,144, unchanged | 81,941 / 117,572 |
| Distinct native-array hashed record visits | 262,144, unchanged | 85,315 with sharing |
| Distinct native-array hashed UTF-16 units | 268,435,456 | 126,097,231 |
| Writer decoded UTF-16 units | 268,435,456 | below the writer byte count |
| Conservative producer record-work charge | 2,097,152 | 907,542 before boundary parsing |
| Producer code-unit work charge | 1,073,741,824 | includes native hashes, writer and source/boundary traversal |
| Original failure's event-name bucket | 16,384 | 9,180 |
| Combined full/compact boundary/effect rows | 8,192, unchanged | 6,740 |
| Combined full/compact operation rows | 8,192 | 4,114 |
| Operations per boundary / alternatives per selected key | 64 each; C2b scopes retained lookup keys without skipping global validation | at most 18 operations |
| Individual line / structured segments | 262,144 UTF-16 units / 256, unchanged | longest line 72,513 units |
| Writer identities / selected witnesses | 64 each, unchanged | 2 writers; 1 selected failure |
| Final companion, including receipt metadata | 262,144 bytes, unchanged | small-fixture result only so far |

Preserve conservative `7*F + 4*C` preparation charging even when hashing aliases
save physical work. Reserve an additional `F + C + 2*W` for membership work;
boundary visits consume the remaining record pool. All arithmetic is checked
64-bit arithmetic before allocating per-record arrays. Native hash counters
report actual distinct-array work; do not claim that four logical roles were
hashed four times when only two arrays were traversed. Producer code-unit
charging starts with actual native hashing and includes bounded writer collection,
writer metadata parsing, native membership and boundary parsing. Existing bounded
selected A/B1 lookups remain separately constrained by admitted line/row limits;
these counters are not a count of every interpreter instruction or allocation.

## Time budgets and primary-report impact

The shared optional producer clock changes from 2,000 to **20,000 ms**. It resumes
across preparation, post-evaluation input validation, witness/membership work and
completion preparation, and remains stopped across the original evaluations
and primary analysis. This explicitly permits more optional work before primary
report publication; it is not a claim that diagnostics add no latency.

Sub-budgets within that clock are 2,000 ms native hashing, 5,000 ms writer
verification/collection, and 5,000 ms native membership. They do not extend the
shared deadline. All stages recheck current owner/cancellation and remaining
budget before allocating or admitting output, with bounded per-record/per-block
checks. A sub-budget or shared-budget failure cannot be retried or revived.

The failure selector retains its separate 250-ms budget; its larger bucket fit
that limit in the offline timing controls. Completion serialization retains its
existing maximum of 2,000 ms: pass `min(2000, remaining producer time)`, not the
whole enlarged pool, to the existing C# issuer. GUI/relay metadata, parent adoption
and persistence retain their existing 250-ms limits and fixed payload bounds.
No post-report scan or new input read is added.

These are cooperative work budgets, not preemptive wall-clock guarantees. OS
calls, scheduling, type compilation and existing parent operations retain their
previous semantics. The original 1,800-second retained watchdog is unchanged.
No machine-dependent auto-tuning, retry with a larger budget or timeout bypass.

## Implementation boundaries

### Source ownership is a bounded descriptor operation

Both request creation and worker attachment use the same 256-MiB size limit.
Retain the original hash-pass read lease, exact file identity/length and private
parent/worker lifetime. The larger descriptor allowance adds no source hash or
scan and does not certify writer membership or completed primary views.
Above the cap, discard optional ownership without truncating primary input.

### Native input hashing may share only actual array identity

Add one bounded batch entrypoint for the exact original four-role array tuple.
The three current full roles reference the same `string[]`; compact Snapshot
uses the original independently assembled reduced array. Hash each distinct
array once and retain four separate role descriptors. Share only within this
invocation using `ReferenceEquals`, never matching hashes, equal strings, paths,
saved descriptors or caller-authored success flags. Equal-content array clones
must incur independent hashing and accounting.

Keep `nlink-ordered-utf16le-v1`, record/line length framing, original order, empty
lines, duplicates and exact UTF-16 code units, including isolated surrogates.
The hash domain currently excludes the role label; sharing must not change any
role's digest. Retain shallow original-string snapshots and exact original
array/reference and physical-origin checks before/after both evaluator calls.
Do not replace the independently generated compact evaluation with the full one.
Native batching must not turn a hash result into completion or authority.

### Writer verification and membership use bounded native traversal

Move the hot collection/membership loops across the PowerShell/native boundary
in batches. Keep the original candidate's held file identity, strict decoding,
single SHA traversal, exact count and verified EOF requirement. Provisional
strings cannot validate a witness before EOF; retain the live read lease through
the original completion/adoption path. No pre-launch verification scan.

Validate every writer record and every full native record. Preserve writer start,
run/scenario, canonical contiguous sequence, duplicate writer-field rejection,
exact raw-string equality and duplicate native membership rejection. Ordinary
writer messages may omit `event`; only the first writer record must have the
start event. Preserve existing first-field/duplicate/whitespace semantics.
Selected publication rows and boundaries still use the original complete field
parser and unchanged A/B1 checks, independently for both completed views.

Use bounded writer-ordinal/sequence position arrays and a seen bitmap instead of
allocating a composite writer/sequence string key for every record. Limit the
sum of writer sequence counts before allocating; no sparse array indexed by an
untrusted huge sequence. At most 64 writer identity keys, 262,144 writer strings,
262,144 positions and 262,144 seen flags. Raw writer character storage is at most
512 MiB; full/compact original strings are referenced, not copied. Origin/shallow
snapshot arrays and transient parsing remain separately count/line bounded.
These managed-payload bounds are not a claim of a process RSS/GC heap ceiling.
Release all optional references and leases on success, omission and exceptions.

### Selection, rows and issuance

Raise only the original event bucket and combined operation-row admissions as
listed above. Preserve unique exact event-object/source-line matching, original
failure labels, strict field checks, independent view/location parity and all
per-boundary ambiguity limits. Missing or over-cap evidence still omits; do not
select a later failure or reinterpret a rejected receive as dispatch.
The existing private issuer and immutable receipt-bound adopted object remain
the only publication/persistence path. No prototype object is admissible there.

## Measured evidence and required verification

Artifact-only native prototypes used the hash-pinned prior live files. They
loaded no production ownership/issuer types and ran no authority evaluation.
The latest complete measurements summed to 4.286 s on PS5.1 and 10.011 s on PS7
for native selection, shared hashing, verified writer reading, exact membership
and both boundary-parser passes. Those sums exclude original source assembly,
real process-lifetime checks, private-view index construction, selected A/B1
resolution and completion. Thus the 20-second pool is a finite implementation
ceiling with measured headroom, **not a passed end-to-end live-scale gate**.

Both hosts passed 10 framing/identity controls (including equal-content clones)
and 518 parser-parity cases. The revised-bucket selector used 43/38 ms for 9,180
records with explicitly synthetic timing labels, not an actual primary invocation.
Source/writer hashes were unchanged before/after the final bulk measurements.
Evidence and reproducible scripts: `artifacts/publication-live-capacity-20260914/RESULTS.md`.

Implementation order: shared limits and source admission; native batch ownership
and hashing; verified writer/native membership; selector/row admissions and the
remaining-time wiring. Test each changed seam before proceeding. Include exact
limit and cap+1, Unicode/framing, cloned/mutated arrays, duplicate fields, sparse
sequences, source identity, EOF, cancellation, replay and expired-owner controls.
Then run the existing real-primary failure integration with a genuinely produced
live-shaped offline input. It must preserve the two original full/compact custody
evaluations and the unchanged whole-primary caller sequence, original
failure/receipt, default-off behavior and a genuine persisted companion on both
hosts. No saved summary may stand in for that positive gate.

Only after that gate and the accumulated review may the checkpoint/build sequence
resume. Run directly affected lanes and at most one broader contract lane, not
the full matrix. Stop before a new live 256 MiB test. This contract/prototype step
does not claim production implementation, live readiness or a changed verdict.

Implementation follow-up: C1v is now in progress in the accumulated worktree.
The revised input/work limits and native traversal are implemented; focused
ownership/parser/completion regressions have passed, but the genuine large-input
positive gate remains open. See
`artifacts/publication-live-inputs-20260914/RESULTS.md` for exact results and
remaining work. This follow-up does not mark the readiness blocker resolved.

C1v assertion correction: scalar observation found three evaluator calls in the
complete primary, both with capture enabled and in a separate default-off
baseline. The first two are the original full/compact calls; the third is the
existing `Get-FileTransferRecoveryFailureClassification` probe-summary evaluation.
The earlier phrase "exactly two evaluator calls" incorrectly counted custody
calls as the entire workflow. Preserve all three existing calls and introduce
none; do not remove the classifier call as part of this diagnostic change.

C1v first-edge follow-up: a genuine live-shaped offline primary completed but
omitted optional preparation. A fresh owned probe with zero evaluator calls
isolated the unchanged 2,000-ms native hash cap, not source ownership or the
20,000-ms shared clock. The bounded fix removes duplicate lifetime polls while
retaining checks at every record, additional 4-KiB block and final issuance.
The cap, exact framed hash and ownership requirements remain unchanged. This
follow-up is not a passed large-input gate; see C1v results for measured status.

C1v writer follow-up: the next original primary passed native preparation but
omitted at writer verification. A fresh zero-evaluation owned probe reached the
5,000-ms reader cap before verified EOF, with source ownership still current.
Writer collection now avoids repeating the iterator's exact-owner poll, while
retaining per-record lifetime checks, verified EOF/hash/count, per-membership
checks and both fixed 5,000-ms sub-budgets. Focused binding/witness/completion
regressions passed on both hosts; the large-input publication gate is still open.

C1v shared-clock follow-up: the full gate then passed on PS5 but omitted the
witness on PS7 at 20 seconds. A fresh zero-evaluation input-work probe completed
writer membership, then exhausted the shared clock in the original full
physical-origin recheck. Only that recheck is moved to bounded native traversal,
retaining the original comparisons and per-record source/native lifetime checks.
No shared/sub-budget, work charge, compact-origin check or authority rule changes.
Focused regressions and the final full positive gate remain required.

C1v verification follow-up: those focused origin/witness/completion regressions
and the fresh full two-host offline positive gate have now passed. The genuine
512 MiB FakeNkn transfer produced 145,389 source / 186,307 writer records; both
hosts preserved the original three evaluator calls, failure and receipt and
persisted a genuine companion. Optional work was 12,406 ms (PS5) / 18,633 ms
(PS7), with unchanged limits. This closes the observed input-work blocker for
the measured shape, not every simultaneous maximum or the live route cycle.
The accumulated review found no further actionable issue. The narrow shared
checkout/finalizer regression passed 3/3, and an isolated LocalOnly portable
0.7.0 package built and passed manifest verification. Full measurements, package
paths and hashes are in C1v results. The next required validation is a new live
256 MiB route-cycle test; no such test or live preflight was run at that handoff.

C1w launch prerequisite: after the user's approval to proceed, pre-launch review
confirmed that `Test-RouteAcceptanceEvidenceManifest` rejects any dirty source
checkout (`source_dirty`). The reviewed accumulated changes therefore require a
local checkpoint and a new portable build tied to that commit before meaningful
live acceptance. This does not relax provenance or introduce another authority
evaluation. The targeted live run keeps strict preflights and scoped capture;
there is no push or full-matrix authorization in this step.
