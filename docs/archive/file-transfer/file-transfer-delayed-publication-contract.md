# Delayed receiver publication: evidence-first contract proposal

2026-09-15. Base HEAD `2691fc70f98d668bae943ebd41c6f8540156ec57`, with
the verified same-deadline rearm correction still uncommitted.

## Decision and scope

Proceed first with an **evidence-only committed-progress/publication receipt**,
not a wider `Recovered` predicate. No existing receipt covers the entire edge
from an ordinary contiguous commit to its later receiver-state publication.
Existing source and control-plane authority can be reused, but their meanings
must not be expanded by an analyzer join.

This document began as the reviewed design proposal. The receipt-capture
implementation checkpoint is recorded below; it is not acceptance approval.
The proposed observation window below is an explicit new reporting contract;
it is not an existing runtime guarantee. The existing recent-progress/stall
deadlines, raw/distinct ownership, `Recovered`/`Superseded` accounting and live
correctness verdict remain unchanged. No new live run is useful at this stage.

The smallest first implementation should capture and verify the missing receipt
with fresh production-generated offline evidence. A separate review must decide
whether a new, distinctly named correctness disposition is appropriate afterward.
Do not fold a late observation into existing `Recovered` counts.

## Current executable evidence

| Existing item | What it proves | What it does not prove |
| --- | --- | --- |
| `RoutePublicationToken` and its accepted Snapshot receipt | Exact Coordinator route/runtime publication | A particular committed frontier or receiver-state frame |
| `CheckpointContinuitySourceStamp` | Coordinator identity and a fingerprint of the captured source scope | An accepted write, a deadline, or a publication completion |
| `InboundFallbackFrontierProgressReceipt` | One exact current repair contract/alias progressed on its bound data session | An ordinary later write or an unrelated/coalesced publication |
| `FallbackControlPlaneToken` and accepted completion | Current issued send ownership and Coordinator-accepted peer-visible control-plane evidence | Which earlier natural writes the captured frame covers |
| `filetransfer_v6_receiver_state_sent` | Captured frame fields after the send path returns | By itself, exact grace ownership, a data-session incarnation, or a validated completion receipt |

Source anchors at the reviewed tree:

- `FileTransferCoordinator.cs:343,380`: route publication and control-plane token
  shapes. Neither includes the original grace obligation or a committed-write chain;
  the control-plane token also does not identify the data-session object/generation.
- `FileTransferCoordinator.CheckpointRepairContinuity.cs:65`: the source stamp is
  an immutable diagnostic fingerprint, not an additional runtime authorization.
- `FileTransferCoordinator.Snapshots.cs:16`: the current state-boundary payload
  has source/data generations but no pause history. An unpaused final frame and
  a passing route-family stream do not by themselves exclude pause/resume ABA.
- `SessionFileTransferService.FallbackFrontierAuthority.cs:1317,1396`: progress
  receipt creation/validation requires the exact repair contract and alias,
  settlement, transport and data-session identity. Reusing it for a later ordinary
  write would violate those checks.
- `SessionFileTransferService.PullTransferSessionV6.cs:5333-5349`: contiguous commit
  and committed-progress time are captured under the service gate. The ordinary
  sparse-write log at line 5444 is emitted later, outside that gate.
- The same file, lines 5750-6029: a receiver-state frame is constructed under the
  gate; `V6LastReceiverStateSentUtc` and the last-state frontier are set at lines
  5886-5888 **before** the asynchronous send. They are selection/coalescing state,
  not publication-completion evidence. A second gate later creates the delivery
  request, so capturing whichever source is current there cannot relabel the
  earlier frame's source.
- `SessionFileTransferService.cs:19800`: the delivery contract's extra captured
  data-session check is specifically for `FallbackCheckpointProof`; do not assume
  it proves ordinary `ReceiverState` data-session continuity.
- `SessionFileTransferService.cs:20193` and `FileTransferCoordinator.cs:26847`:
  completion consumes the issued token and rejects missing peer-visible proof.
  `AcceptedAny` alone is insufficient. Data-session fallback with no such receipt
  cannot be silently promoted to this stronger publication proof.

The first selected live obligation still uses the original September 15 capture.
Its receiver state 97 at GUI ledger line 42774 / writer sequence 16082 contains
frontier 5001/highest 5252 but no repair continuity, publication-evidence or
control-plane authority reference. This is an evidence gap, not proof that the
runtime send lacked authority. Do not backfill these fields from nearby records.

## Proposed bound: fixed at the first committed advance

Let `D` be the original exact stall wake's deadline, and `W1` the **first** accepted
contiguous advance from its frontier, committed strictly before `D`. Require the
existing recent-arm/current-recheck/stall ownership chain first.

Propose a versioned, evidence-only observation window of **1000 ms**:

`P = W1.CommitUtc + 1000 ms`.

The duration is motivated by the existing one-second committed-progress grace,
but is a new reporting choice, not a consequence or extension of that grace.
Neither the 500 ms force interval nor the 750 ms state retry interval bounds an
asynchronous completion. They must not be advertised as delivery guarantees.

Freeze `W1`, `D`, `P` and the contract version once. Never recompute them from a
later write, highest-only rearm, new recent wake, selected state, retry, logging
time, wall-clock time at analysis, or a later convenient successful send. A write
at `D` does not qualify; a capture/completion at `P` is outside the proposed window.
With `W1 < D`, `P < D + 1000 ms`; continuing traffic cannot keep this obligation open.

Use commit/capture/completion times recorded at their actual service/Coordinator
boundaries, not delayed log-writer attempt times. Require ordered observation
generations and reject backward/malformed/overflowed clocks. Do not add a timer,
sleep, send, retry or liveness deferral to meet or observe `P`. A passive observer
may close on subsequent activity or normal stream finalization. Missing coverage
through `P` is incomplete evidence, not proof that no publication occurred.

This window must remain diagnostic-only in the first implementation. It does not
justify reclassifying the saved live case, even though its raw timestamps fit it.

## Exact receipt chain

Proposed names describe payload roles, not public APIs or wire fields. Reuse
existing token/receipt identities; add only the missing evidence bindings.

1. **Original obligation/source.** Identify the existing admitted repair-progress
   receipt, recent root/current recheck and resulting stall wake. Carry their
   distinct generation families, exact frontier and `D`, Coordinator/writer/
   session/transfer, direction, route/protocol, full runtime/publication/commit
   tokens and live/terminal/leg/transport/bridge/checkpoint/data-session scope.
   Preserve the original wake descriptor before the commit clears or replaces it.
   A raw source fingerprint or numeric generation tuple alone is not a receipt.
   Register passive observation at that original wake, not retrospectively at
   completion. Retain a closed source/lifecycle history through completion:
   source replacement, data-session replacement and pause/resume invalidate the
   observation monotonically, even if values later return. Capture those facts
   at their existing mutation boundaries; absent invalidation coverage is
   incomplete evidence. The current Snapshot boundary alone cannot supply it.
2. **Committed-progress receipt.** At the existing successful contiguous-commit
   boundary, have the Coordinator author an evidence-only receipt using the
   captured source, current canonical state and exact accepted write observation.
   It must bind `before`, `after`, count, highest, commit time and an ordered
   observation identity. Require `after - before = count > 0` for `W1`, source
   continuity, and `W1 < D`. Failed/pending disk writes, accepted transport queues,
   stale callbacks and out-of-order tail-only writes cannot open this receipt.
   This receipt grants **no** route, retry, repair, runtime or send authority.
3. **Coalesced coverage.** Retain exact contiguous coverage from the original
   frontier through the first selected frame's frontier, including intervening
   writes after `D` if the frame coalesces them. Mark those later writes separately;
   they do not retroactively satisfy the old deadline or move `P`. Preserve
   monotone highest and clock/order, zero-advance observations where relevant,
   and exact final frontier/highest/bytes. A missing intermediate link is unproven.
4. **Frame capture.** Select the first actual receiver-state frame captured after
   `W1` that covers `W1`'s committed frontier, before checking send success. Capture
   its source/data-session identity and immutable value snapshot under the same
   gate as frame creation, before releasing it or awaiting. Bind frame epoch,
   frontier, highest, bytes, transport, pause/terminal flags and canonical frame
   fingerprint; freeze collection values too. An older in-flight frame that does
   not cover `W1` cannot close it. Do not skip a failed/conflicting first covering
   frame to borrow a later success. Retries require a separate future contract.
5. **Accepted completion.** Attach the frozen frame/coverage reference to the
   existing delivery contract, and author the completion evidence at the already
   accepted Coordinator completion boundary. Bind its exact token/authority ID,
   original operation/receipt, frame fingerprint and completion time. Consume the
   existing decision; do not call `AuthorizeAction` again to mint evidence. A
   transport result, `.Sent`, mutable service dictionary or nearest Snapshot
   boundary cannot replace the accepted receipt. No-result data-session fallback
   remains unsupported for this stronger proof.

Here publication means the exact frame's existing Coordinator-accepted
peer-visible control-plane completion. It does not claim that the remote
application applied that state, advanced its own frontier, or verified the file.

The frame's source, delivery source and completion source must match. Check
data-session object/incarnation as well as generation, not just the control-plane
token. Source A -> B -> A is invalid even if the final display tuple matches.
Terminal, pause, source replacement or a rejected completion permanently prevents
this observation chain from closing positively. Stale callbacks remain diagnostic
and cannot create/reopen/refresh a receipt or settle any runtime work.

Retain at most one pending observation per transfer, bound to one admitted stall
episode, without a retry queue. Completion, invalidation, observed cutoff or
normal transfer disposal retires only that diagnostic state. An overlapping
episode or exhausted evidence capacity is incomplete evidence, not permission to
extend or replace the old observation. Never evict a contradiction to salvage a
positive result. New evidence must stay outside runtime-token equality and
existing action policy; an evidence rejection must not suppress or grant a send.

## Diagnostic outcomes and fail-closed order

The first slice produces a private diagnostic result only. All rows below leave
the existing primary correctness verdict and recovery accounting untouched.

| Evidence | Proposed diagnostic outcome |
| --- | --- |
| Exact chain, frame and accepted completion before `D` | PublishedBeforeOriginalDeadline |
| `W1 < D`, exact chain and accepted completion in `[D, P)` | PublishedWithinObservationBound |
| No exact accepted pre-deadline advance | PreDeadlineCommitUnproven |
| Missing/ambiguous receipt, coverage, writer binding or end watermark | EvidenceIncomplete |
| First covering send rejects/fails/cancels | PublicationRejected |
| Source/data ABA, pause or terminal before completion | ObservationInvalidated |
| First covering frame/completion at or after `P` | PublicationOutsideObservationBound |
| Complete owned coverage through `P`, no covering frame/completion | PublicationAbsentWithinObservationBound |

Validate input custody/unique records first, then the original obligation/source,
commit chain, first frame, source continuity, accepted completion and temporal
classification. Invalid identity must not degrade to a mere timeout. Never select
the latest passing source observation from conflicting alternatives. A stream that
ends before `P` without complete coverage is not the absence outcome. Terminal
before completion is invalidation, not successful supersession.

## One primary evaluation and retained witnesses

- Consume only the primary's already validated, complete Snapshot view and its
  exact frozen writer input. No second authority pass, file-based historical
  rebuild, nearest-boundary search or diagnostic-derived receipt fabrication.
- The new receipt needs an explicit Snapshot evidence representation and validator
  for the commit/frame/completion links. Existing `route_runtime` PASS does not
  automatically validate a new payload. Unsupported/missing payload stays unproven.
  Do not insert an unrecognized mandatory kind into the existing Snapshot profile
  or require the new payload for old primary results. Initial capture/validation
  is confined to the fresh offline fixture until compatibility is demonstrated.
- Preserve primary-selected failure ownership and exact raw source-line membership.
  The current optional publication-use witness supports other use kinds; do not
  repurpose its completion capability for recent-progress receipts without a
  separately verified mapping. No added mandatory live output in the first slice.
- Generate full and compact evidence independently from the same fresh frozen
  writer; preserve all required receipt links, source invalidations and conflicting
  writer copies. Compare canonical semantic rows, not native array positions.
- Stay within the existing maximum of 64 selected source-line witnesses. If exact
  coverage needs more, return budget/incomplete evidence; do not silently truncate,
  summarize an unvalidated chain, or broaden the limit. The primary's normal
  failure result survives optional diagnostic preparation failure.
- Historical captures without the new receipts retain their original verdicts.
  There is no backfill path, fallback inference mode or report rewrite.

## Implementation and verification gates

**A — receipt and fresh offline fixture only (recommended next step).** Add the
smallest internal Coordinator-authored evidence payload and passive observation
hooks at the original wake, required source/lifecycle invalidations, and existing
commit/frame/accepted-completion boundaries. Do not alter decisions,
send cadence, coalescing, grace, queue/concurrency, wire frames or runtime actions.
No production recovery consumer or live optional-witness export yet.

Extend the existing real service checkpoint-continuity fixture with an **ordinary**
post-repair sparse commit, a controllably delayed ordinary state send, and drained
production logs/Snapshot evidence. Use the real commit path and the real accepted
control-plane completion. Do not manufacture the new positive receipt or advance
`NextChunkIndex` by reflection to stand in for a successful write. Keep timing
deterministic with injected test observations/delays, not a wall-clock race.

Required controls before calling A complete:

- Positive: single write; several coalesced writes; post-`D` writes covered without
  rolling `P`; current issued completion even when a newer unrelated send token
  exists; exact duplicate evidence remains idempotent.
- Temporal: commit at/after `D`; frame or completion at/after `P`; publication
  absent; stream ends early; writer logging delayed; backward clocks; newer wakes
  and continuing writes cannot extend the bound.
- Identity/coverage: wrong original repair, wake family/generation, source token,
  Coordinator/writer/session/transfer, data-session ABA, checkpoint/bridge/route
  replacement, missing write, count/frontier/highest mismatch, changed frame,
  duplicate/conflicting copy, capture from a different source after the first gate.
- Authority/lifecycle: queue-only acceptance, rejected completion after successful
  physical send, no-result data-session fallback, first covering frame fails but a
  later one succeeds, pause/terminal, and stale callbacks with every effect absent.
- Invariance: identical runtime decisions, transport calls and original primary
  recovery counts with evidence capture enabled/disabled; bounded state/cleanup.

**B — private diagnostic consumer.** Only after A has real-receipt positives,
validate original/full/compact source custody and all outcomes on PowerShell 5.1
and 7; require flat/indexed and partition parity without changing primary verdicts.
Failure-selected witness integration is a separate bounded substep, not implied
by a private consumer passing.

**C — correctness decision, then live gate.** Review whether the new disposition
should satisfy a newly versioned requirement at all. Explicitly distinguish it
from old `Recovered`; decide the proposed 1000 ms window before any acceptance
change. Only after the affected offline paths pass should a clean checkpoint,
portable rebuild, strict escalated preflight and one targeted live-256 run be
considered. This document authorizes none of those actions.

## Verification performed for this design

Inspected current Coordinator, ordinary V6 commit/state/control-plane paths and
existing evidence contracts; read only the bounded original state-97 ledger
records. No primary replay or historical evidence modification.

Focused baseline: three Coordinator control-plane receipt tests and the fake-NKN
late-authority-rejection test passed. The selected terminal-callback test initially
failed before exercising its assertion: reflection requested a static method that
is now an instance method. Corrected only that invocation, asserted setup success,
rebuilt the test project and reran that one test successfully. No runtime source
changed in this turn. Results: `artifacts/delayed-publication-contract-20260915/`.

These five design-baseline tests establish existing authority boundaries, **not**
a positive proof of the proposed commit/frame receipt. At that checkpoint the
receipt and diagnostic outcomes were unimplemented. Adjacent stale reflection callers were noticed but not
changed; they are outside this selected verification lane.

## Implementation checkpoint: A1, passive capture foundation

The internal `FrontierPublicationObservation` now captures a real admitted repair
origin, recent-arm chain/current recheck, fixed original stall deadline, ordinary
sparse commits, immutable first covering frame and the existing accepted
Coordinator completion. It retains source runtime/publication/commit tokens and
the original completion envelope. It performs no action authorization or Apply
call and has no live/default activation, runtime consumer, wire field, timer or
retry queue. The private service context is opt-in only through the offline test
fixture. Pending frame/session references are cleared on closure.

Source checks run at Coordinator decision/action adoption and observation
boundaries; inbound transport-epoch adoption, data-session replacement/generation,
user/peer/transport pause and transfer-state changes invalidate monotonically.
Reset/disposal closes pending evidence before releasing the context. The earlier
shared fixture wait now selects its own transfer's stale-wake record; unrelated
late records from a preceding fixture cannot release its wait.

Fresh service positives use actual sparse writes after repair, with no reflected
frontier advance. A controlled fake delivery holds ordinary receiver-state
completion; it does not connect to NKN. The fixture controls the existing
coalescing precondition and injects observation-boundary times, while leaving
runtime timers/decisions unchanged. Therefore the exported fixtures are explicitly
`offline_frontier_publication_capture_v1`, **not live timing evidence**: their
injected observation clock is distinct from the existing Snapshot/log clocks.

Each exported positive includes the actual typed receipt and frozen persisted log
input. Its accepted completion is checked against exactly one persisted Snapshot
payload, including its canonical hash, bytes, writer ID and source line/sequence.
This is a fixture custody check, not a replay of the primary authority evaluator.
See `artifacts/frontier-publication-receipt-20260915/RESULTS.md` for verification.

**A is not yet complete; do not enable B/live consumption from this checkpoint.**
The remaining gate is the evidence representation and rejection coverage:

- Define/validate the new payload's entire source/write/frame/completion chain,
  clock-domain contract and independently frozen full/compact forms. The existing
  completion Snapshot alone does not validate the newly added receipt fields.
- Establish exact raw witness membership and the real 64-source-line budget;
  the in-memory record cap is only a memory bound, not a witness-budget proof.
- Complete closed lifecycle-history/writer coverage, conflicting copies,
  incomplete/end-watermark and delayed-writer controls. An early end currently
  stays `EvidenceIncomplete`; no absence disposition is emitted.
- Add service-path no-result/late-rejection and pre-delivery source-change
  controls beyond the already exercised queued result, terminal rejection,
  changed frame and source/data/pause ABA cases. Audit invalidation coverage
  before treating a serialized source fingerprint as a closed history.
- Preserve unchanged primary recovery counts during later optional-consumer
  integration. Current verification compares actual Coordinator decision traces
  and transport calls with capture enabled/disabled; it does not re-run or
  reclassify the historical live primary result.

The original live-256 correctness result remains `FAIL/incomplete_proof`. No
live run, portable rebuild, commit, push, historical backfill or recovery-accounting
change is part of A1. The next bounded step is the remaining offline evidence
representation/validation gate, not a new live test.

## Implementation checkpoint: A2, producer-bound offline validation

`FrontierPublicationOfflineCapture` now freezes the completed producer's actual
receipt and a copied writer input. It checks receipt/source fingerprints, route
invariants, wake/write coverage, immutable frame fields, fixed deadlines and the
exact completion Snapshot. Required repair, recent/stall/recheck, write and frame
records must be present in that writer. Missing/reordered writer sequences,
conflicting copies, duplicate stamp/event fields and missing completion/end
records fail closed. Exact repeated writer copies are idempotent.

Full and compact payloads are built separately from the same frozen input.
Compact records retain exact one-based source lines, writer sequence and raw
decoded line strings; more than 64 selected records returns incomplete
evidence. The full-input hash uses the explicit
`sha256-utf8-canonical-json-records-v1` domain, not a file-byte hash. Both payloads
are bound to a private capture identity and the explicit
`injected_observation_separate_snapshot_clock_v1` domain. Delayed writer-attempt
times do not change the diagnostic deadline/outcome.

Candidate validation compares against that producer-owned immutable expectation.
A supplied JSON file, digest, selected lines or clock label cannot establish its
own expected capture. The only successful result is `ValidatedOfflineCapture`,
with `LiveProofEligible=false`; it does not mean `Recovered` or a passing primary
Snapshot authority evaluation. This API is called only by the offline fixture.
Its trusted freeze boundary is the completed in-process producer plus the
fixture's drained writer input, not arbitrary caller-supplied historical files.

The older-issued-token control exposed an important distinction: a successful
completion that preserves a newer current token has an `Idempotent` Snapshot.
The validator accepts that state-unchanged envelope only alongside the original
producer-accepted completion; a bare idempotent Snapshot remains insufficient.
No Coordinator completion policy was changed.

Two additional real service controls now cover no-result data-session fallback
(`EvidenceIncomplete`) and source ABA inside the original frame gate before
delivery-request creation (`ObservationInvalidated`). Neither suppresses the
underlying authorized send or grants publication proof.

Results and fresh full/compact artifacts:
`artifacts/frontier-publication-validation-20260915/RESULTS.md`.

**Still before B/live:** A2 is not a portable/new Snapshot evidence profile or a
replacement for primary custody. The remaining gate is the closed, serialized
source/lifecycle history and receipt representation, including its binding to
the primary's already evaluated authority view, a non-injected live clock
contract, and primary-selected witness membership. The current 64-record check
is an exact bound for this private projection, not certification of that future
primary witness mapping. No historical backfill, second authority evaluation,
acceptance-count change or live-256 run is permitted by this checkpoint.

## Implementation checkpoint: A3, serialized private lifecycle history

The passive producer now freezes an ordered lifecycle alongside the receipt:
repair origin, each recent arm, original stall/current recheck, each committed
write, first frame, issued delivery token and the existing accepted completion.
Each entry carries a contiguous sequence, original source fingerprint and an
exact binding fingerprint. The close watermark and outcome are immutable.
Source invalidation, capacity exhaustion and early closure retain their closing
record and cannot be reopened by a later callback. The 64-entry memory bound
reserves a closing slot; it is separate from the 64-source-line witness limit.

The source audit found that checks only after grouped Coordinator adoption could
miss an intervening assignment-and-restore. Inbound source setters now check the
captured scope at every assignment: route selection, epoch/leg, terminal/runtime
owner, commit/publication, checkpoint lease, transport decision and persistent
Coordinator state (including bridge and identity scope). Existing data-session,
incarnation, transfer-state and user/peer/transport-pause setters close immediately.
Initial pause also prevents a positive episode. Session/transfer identity is
immutable; the direction is fixed inbound. Lifetime/reset/disposal closes pending
observation as before.

The mutable leg's transport epoch, checkpoint generation/request, bridge generation
and state, and the live epoch's state/publication, now have passive internal
mutation notifications. These notifications are unsubscribed when the observer
closes or the adopted source object changes. Coordinator graph adoption makes
fresh objects even when source values are unchanged; exact-value clones transfer
the subscriptions, not invalidate the episode. An immutable source-graph value
captures epoch selection/handoff/target/state/publication and leg identity/state
in addition to the existing continuity scope. The new events and private context
fields are not serialized runtime authority and do not enter token equality.

Positive history validation requires every exact receipt binding, source-graph
fingerprint, sequence, ordered boundary clock and closing completion. Mutation
callbacks and the existing recheck have no new clock read: their entries retain
order without inventing a timestamp. They cannot establish timed absence.
Structure alone remains insufficient: even a structurally valid renamed history
does not match the producer-owned frozen capture. Missing, reordered, null,
altered or foreign histories fail closed.

The private receipt/capture formats advance to version 2; lifecycle format is
version 1. Full/compact JSON carries the same closed history, with the unchanged
injected/separate-clock label and `LiveProofEligible=false`. Existing version-1
artifacts are preserved, not migrated or backfilled. No new operational-log event,
mandatory Snapshot kind, runtime send/action, timer or acceptance rule was added.

Verification: `artifacts/frontier-publication-lifecycle-20260915/RESULTS.md`.

**A still does not authorize B or live consumption.** This closes the private
service-source history representation, not primary custody of a portable payload.
The next bounded step is binding the versioned receipt/history to the primary's
already evaluated immutable authority view and exact failure-owned witnesses,
without a second authority evaluation. Persisted payload/writer closure and safe
projection/redaction, non-injected live clock semantics, and the actual primary
64-line mapping remain gates. No new live-256 test is required yet; the saved live
correctness result remains `FAIL/incomplete_proof`.

## Implementation checkpoint: A4, exact writer binding prerequisite

Review of the primary handoff found a required ordering constraint: its existing
failure-witness resolver admits only `publication_use_kind` records for physical
submission, pre-pump, receive guard and receive dispatch. A recent-grace receipt
is none of those kinds. Do not extend that resolver by relabeling this receipt or
assuming its existing completion capability validates these new fields.

The completed offline producer now creates one digest-only
`filetransfer_frontier_publication_receipt_closed` record, emitted through the
actual service writer after its already accepted completion. The record binds
the entire canonical receipt (including lifecycle, source graph and injected
clock domain), exact persisted completion hash, projected Coordinator/completion
identities, observation identity, outcome and lifecycle closing sequence. It
contains no raw receipt JSON, frame payload, file contents or secret values.
Its receipt hash domain is
`sha256-utf8-canonical-offline-frontier-receipt-v2`; it is not a file-byte hash.

The service consumes the emission once. Logging failure cannot grant proof:
freezing requires exactly one matching record in the copied writer input, after
the exact completion and before the owned stream end. Missing records, altered
fields, duplicate fields, conflicting/new-sequence copies, wrong writer and
misordering fail closed. Identical retained copies with the same writer sequence
remain idempotent. Both private projections include the exact one-based source
location/raw line and count it against their existing 64-record witness cap.
Private capture validation advances to version 3; receipt version 2 and lifecycle
version 1 remain unchanged. Older artifacts are not migrated.

Redaction protects only three cataloged, fixed-shape digest fields on the exact
single event. Unknown events/fields, malformed values, encoded payloads and
secrets retain normal redaction. The existing Snapshot projection was exposed
through an internal non-test-named helper, without changing its payload or
reevaluating authority. No new mandatory Snapshot kind was introduced.

Fresh service-generated tests on PowerShell 5.1 and 7 perform their original
full/compact Snapshot evaluations once per view, then disable further evaluation.
Both existing views resolve the same exact accepted completion and writer order
without mutation. This is **compatibility/prerequisite evidence**, not the live
primary's invocation custody and not a failure-owned witness capability.
Results: `artifacts/frontier-publication-writer-20260915/RESULTS.md`.

### Next bounded binding contract

The remaining integration must be separate from the old publication-use issuer:

- The primary's private `PrimaryEvaluationCustody` and exact
  `CompletedEvaluations` must own both original input arrays, context and already
  completed views before and after optional preparation. A copied view, saved
  JSON, candidate digest or bare passing Snapshot must not recreate this custody.
- Selection starts with the primary's actual reported failure object and exact
  event membership. Bind its recent/stall obligation to the receipt's original
  repair/source/generation chain; never select a nearby or later passing marker.
- Validate the versioned receipt/history against the exact owned writer marker,
  then bind its completion ID/hash to the already evaluated full/compact boundary
  and exact original physical line. Preserve all required source witnesses within
  64; exhaustion omits the optional result and preserves the primary failure.
- Retain `LiveProofEligible=false` for this injected-clock profile. Optional
  failure, cancellation, corruption or missing coverage must not change primary
  recovery counts, correctness, reporting or emit the old publication capability.

No live consumer, parent channel, primary failure selection or acceptance rule
was changed in A4. The primary-owned mapping and non-injected live timing remain
unfinished; a new live-256 run is still not useful.

## Implementation checkpoint: A5, primary-owned offline failure witnesses

The optional primary mapping is now separate from the old publication-use
resolver and issuer. `Write-FileTransferCompactRetainedAnalysisArtifacts` accepts
an explicit offline canonical receipt and an empty diagnostic output reference;
no CLI, live parent channel or acceptance consumer enables this option.

The primary supplies its actual first reported failure, original candidate list
and unresolved recent-grace proof. The mapper requires exact candidate/event
membership and source-line references, not a new failure assembled from a receipt.
The primary's private custody checks the original completed full/compact pair
before and after preparation. Native source, input origins and writer membership
are verified independently; copied contexts/views, substituted events and revoked
ownership cannot recreate the result.

The receipt's exact canonical UTF-8 bytes are hashed before parsing and must match
the one owned writer marker. The existing producer exposes the same canonical
payload routine used by its digest, avoiding PowerShell date/escape
reserialization. The versioned lifecycle/source history remains authenticated as
part of that payload. The mapper then binds the selected original frontier to its
repair continuity, recent arms, current recheck, stall generation, ordinary writes,
published state and exact completion ID/hash in both already completed views.
Physical full/compact completion origins must agree. It does not evaluate
authority or invoke a correlator again.

End-to-end testing exposed a retention gap: the A4 marker was present in the
writer but absent from the GUI retained slice. It is now retained in the existing
diagnostic-only event list. It is not a mandatory Snapshot kind, a generation
effect or a legacy correlator dependency; pinned authority/catalog bytes are
unchanged. Existing opaque primary diagnostics can contain repeated fields;
they are retained byte-for-byte, while every record interpreted for the new
receipt binding must have unique fields.

The detached result identifies its native request, source path/file hash, writer,
actual reported failure, exact completion/marker lines and at most 64 source-line
hashes. It is not a portable capability. Exhaustion or optional failure returns
no result; the original primary failure and counts remain unchanged. Both
`LiveProofEligible` and `PublicationCompletionIssued` are always false.

Fresh tests now exercise a real non-contiguous sparse write during the settled
repair's recent grace, followed by an ordinary contiguous write and a held actual
delivery completion beyond the original deadline. Failure selection and writer
lines/timestamps are not synthesized. The injected observation clock is separate from
writer timing and is still ineligible for live proof. The real primary selects
one unresolved recent-grace incident, and the positive mapping selects 52 exact
source lines. A larger real Coordinator stream exceeds the cap and omits the
result; cancellation after successful preparation is also rejected at the
primary's custody boundary.

The full original primary has two full/compact evaluations plus one pre-existing
recovery-classifier evaluation. All three remain in their original sequence;
the optional mapper adds zero. Tests forbid evaluation once preparation starts,
and verify unchanged views, failure, counts and failed gate on PowerShell 5.1/7.
Verification: `artifacts/frontier-publication-primary-20260915/RESULTS.md`.

**A5 completes this offline mapping, not live acceptance.** Non-injected boundary
clock semantics and a bounded live payload/parent-consumer contract remain to be
reviewed before live enablement. Any new correctness disposition still needs its
separate review; existing `Recovered`/`Superseded` accounting is unchanged. No
new live-256 run, historical backfill, portable rebuild, commit or push was made.

## Post-A5 review: live boundaries remain gated

See [the live-boundary contract review](file-transfer-live-publication-contract-review.md)
for the source-grounded findings and bounded sequence. The next implementation
is episode-local witness selection under unchanged native primary custody:
the current mapper includes unrelated same-transfer/Coordinator history, so
eight ordinary post-receipt sends exhaust the 64-line cap. Preserve full input
validation, relevant contradictions and the cap; narrow only witness selection.

Live clock/deadline provenance, same-deadline stall-successor coverage, and a
bounded writer-owned payload/separate parent channel remain later gates. This
review changes no runtime or acceptance contract and requires no new live-256
run. The A5 test results above are unchanged; no authority evaluation was replayed.

## Implementation checkpoint: A6, episode-local primary witnesses

Witness selection now keeps the exact earlier repair-authority/generation links,
the selected repair-progress-to-state diagnostic episode, every owned Coordinator
record from that progress through the accepted completion, and both stream
anchors. The exact completion and receipt marker remain mandatory; the matching
state-send diagnostic is retained even though it follows the marker. Full native
source/writer/origin validation, complete evaluated views and global unique-link
searches are unchanged. The detached selection is not a standalone proof or a
publication capability.

A separate full-input identity check rejects multiple state-send records for the
selected frame epoch/transport, including a conflicting record after closure.
It cannot choose only the copy whose other fields happen to match the receipt.

Fresh real-writer fixtures cover 16 ordinary sends before the repair progress and
16 after receipt closure, a 56-contiguous-write receipt with more than 64 required
links, and a deliberately conflicting diagnostic in the genuine writer. The
large receipt fixture controls existing coalescing metadata only; all committed
chunks and accepted publication evidence still come from the actual service
path. Neither runtime coalescing nor the 64-witness limit changed.

Verification and final artifact locations are recorded in
`artifacts/frontier-publication-episode-20260915/RESULTS.md`: 7/7 selected tests
passed. Both PowerShell hosts select the same 25 witnesses from the normal
66-line and history-heavy 292-line sources. Source-file and individual line
hashes were independently verified; 96 negative controls preserve the primary.
This step adds no clock profile, stall-successor hook, payload transport, parent channel or live
acceptance rule. Those review gates remain open; no new live-256 run is useful.

## Post-A6 review: boundary-clock and stall-successor contract settled

The [boundary-clock contract](file-transfer-publication-boundary-clock-contract.md)
keeps the original UTC D/P and adds conservative, integer-only elapsed guards in
a separate diagnostic profile. It does not convert or replace runtime deadlines,
use the ordered Snapshot decision timestamp as a boundary clock, or promote the
old injected profile. Actual same-deadline stall successors preserve the original
root while separately recording the recheck-time and W1-time tips.

The review also identifies an uninstrumented V4-compatible contiguous-commit
path and paused-progress flush. Both must be accounted for before live capture;
V6-only passing fixtures cannot establish that coverage. The next bounded
implementation is only immutable clock stamps and a pure timing predicate, then
fresh service integration/coverage tests. The design arithmetic model passed
60/60 cases, not production tests. No runtime changes or live rerun occurred.

## Implementation checkpoint: A7a, isolated boundary timing

The immutable stamp and pure timing helper from the boundary-clock contract are
implemented without clock acquisition, service hooks or observer activation.
All stamp identities/ranges/order are checked before timing; original recent/D/P
deadlines must exactly match their root UTC values. Checked wide-integer elapsed
guards retain uncertainty and cannot extend D or P. Results are timing diagnostics
only, always `LiveProofEligible=false`, with at most 64 supplied boundary stamps.
Producer custody, lifecycle capacity and accepted-boundary roles are not inferred.

115 distinct focused cases are verified across the final expanded run and its
single fixture-correction rerun. Production code was unchanged after the initial
104-case pass. Details: `artifacts/frontier-publication-clock-20260915/RESULTS.md`.
The existing injected profile, primary mapper, authority evaluations, historical
live result and acceptance rules are unchanged. No commit or live run occurred.

Next is opt-in actual boundary/stall-successor capture and fresh service coverage
for both sparse-write paths, races and invalidation. Only after that should the
new profile's private validation/witness mapping and payload/parent gates advance.

## Implementation checkpoint: A7b, actual boundary and successor capture

A separate private opt-in observation now brackets the existing progress/stall
UTC reads and captures the original frame, delivery binding and accepted
completion gates. It covers V6 and V4-compatible sparse writes without changing
V4's runtime committed-progress timestamp behavior. Source/pause/data ABA and
paused-progress flush close diagnostic coverage; a rejected first frame cannot
be replaced by later success. No extra Coordinator authority call is added.

Actual post-assignment stall successors retain root/D, recheck-time tip and W1-time
tip independently within one 64-entry lifecycle. Duplicates are idempotent only
for the exact recorded pair; missing/conflicting/gapped/current-tip evidence is
incomplete. A successor before actual recent recheck is supported; W1 before that
binding is not. No timer or later write restarts the publication bound.

20 distinct focused cases are verified: 19 new real-service/negative-control cases
and the prior injected-profile noninterference regression. Both write paths have
enabled/disabled runtime-decision and send comparisons. Details and retained runs:
`artifacts/frontier-publication-boundary-20260915/RESULTS.md`.

Capture remains test-attached local diagnostics with `LiveProofEligible=false`.
The old injected receipt, writer hashes, primary mapper and saved live verdict are
unchanged. Next is private validation/witness mapping for this distinct captured
profile, then the separately owned payload/parent and acceptance contracts.
No live-256 run, portable rebuild, commit or push occurred.

## Implementation checkpoint: A7c, private boundary validation and witnesses

Closed boundary captures now revalidate the original repair/source, recent/stall
chain, actual writes, first frame, issued token, accepted completion and native
clock predicates without another authority evaluation. The admitted repair stamp
is retained independently of later contract retirement. Frozen full/compact
diagnostic projections require exact candidate bytes from the same capture.

The new mapper binds exact input lines to both sparse-write paths and their
successors. It retains episode-local coverage within 64 witnesses, rejects missing
or ambiguous interpreted links, and preserves opaque diagnostic bytes. Input
copying is bounded and happens outside the service gate. Writer sequence fields
and input-line hashes are structural evidence, not native source/parent custody.

Review corrected the earlier race fixture: its reentrant callback ran before the
original-root log inside the creation gate. That is not a demonstrated production
logging gap. The existing callback now exercises the actual gap between the request
attempt and recent-recheck gates; both paths map the preserved original root and
its successor. Missing-root rejection is separately tested by removing that input
event. No runtime logging fix or substituted witness was introduced.

Final retained verification: 18/18 stabilized gate, 2/2 input-copy follow-up and
9/9 inter-gate follow-up, covering 23 distinct focused cases. Six positive
full/compact projections also pass independent line/hash/producer parity checks.
See `artifacts/frontier-boundary-validation-20260915/RESULTS.md` and the
[boundary-clock contract](file-transfer-publication-boundary-clock-contract.md).

All new results remain diagnostic-only, with live eligibility, native writer
custody and publication-completion issuance explicitly false. The injected
profile, primary evaluations/verdict/counts and historical live result are
unchanged. The next gate is the writer-owned payload/parent and consumer contract;
no new live-256 run is needed yet. No portable rebuild, commit or push occurred.

## Implementation checkpoint: A8a, closed-producer payload preparation

The new [payload/channel contract](file-transfer-frontier-payload-channel-contract.md)
separates producer bytes from A7c's completed input-log JSON, avoiding a marker/input
self-reference. It also records that the native GUI worker and nLink app process
have distinct ownership roles which must be connected by the later native channel.

Preparation now creates one cached immutable, explicitly projected producer
payload outside the service gate and before source-stream closure. It preserves
the original native-clock samples and accepted completion reference, excludes raw
runtime tokens/logs/source identifiers, and enforces strict UTF-8 with a 262144-byte
maximum. Copied bytes or an equal-byte object do not establish private producer
ownership. No writer marker, file output or primary/parent channel is added.

Final focused verification passed 21/21, including both write paths, successors
before/after recent recheck, pre-stream-close preparation, immutable copies,
foreign ownership, privacy projections, invalid producers, UTF-8 boundary cases
and both runtime noninterference comparisons. See
`artifacts/frontier-payload-preparation-20260915/RESULTS.md`.

Native app/writer/source binding, bounded catalog/admission/work rules and a
separate parent output remain the next gates. The current payload is diagnostic-
only; primary evaluations/verdict/counts and the historical live result remain
unchanged. No portable rebuild, live-256 run, commit or push occurred.

## Implementation checkpoint: A8b1, native process attachment only

The next isolated ownership primitive is implemented in the separate
`LocalFrontierAppRequest` codec. It binds the actual launched app-role process
and parent incarnations, exact bounded run/scenario bytes, cancellation and
one-shot attachment completion. It reads no files, transports no producer bytes
and issues no writer/source/publication capability.

Final focused verification passed four host-specific test rows: 23 fresh native
child-process scenarios on each of PowerShell 5.1 and 7, plus actual parent-death
checks. Registration limits and exited-before-bind controls also pass. Evidence:
`artifacts/frontier-native-app-20260915/RESULTS.md`. The initial cancellation
failure was a test barrier placed before the baseline assertion, not a runtime
authority defect; the corrected barrier preserves cancellation semantics.

The codec is not yet called by nLink or the GUI finalizer. Native attachment alone
does not bind the original GUI request, exact prepared producer, operational
writer, persisted marker or source read lease. Those links and the separately
bounded payload catalog/admission/work rules remain next, as specified in the
[payload/channel contract](file-transfer-frontier-payload-channel-contract.md).
All live/writer/publication eligibility flags remain false. No existing native
channel, primary evaluation/result, live verdict or acceptance rule changed in
this step. No portable rebuild, live-256 test, commit or push occurred.

## Implementation checkpoint: A8b2, private bounded producer catalog

The catalog now owns admission before capture and the exact preparation owner.
Limits are 16 lifetime observations, 262144 UTF-8 bytes per payload, 4194304 retained
payload bytes, one attempt per observation, and one preparation in flight. A
two-second cumulative preparation cutoff discards over-budget output and prevents
further work/admission; it is cooperative, not a hard latency/allocation guarantee.

Every admission must have its own positive preparation before the single complete
take. Partial output, capacity failure, invalid preparation, overlapping work and
early take invalidate the catalog. No failed slot is recycled or replaced by later
success. Cancellation revokes even an issued private snapshot, releases catalog
references and leaves runtime observations/decisions untouched. Admitted producers
cannot bypass catalog preparation through the older standalone method; ordinary
standalone A8a producers keep their previous behavior.

The stabilized focused lane passed 51/51. After the preparation-owner check,
35/35 directly affected cases passed, including both sparse-write paths and
successor orderings, multi-payload order, partial rejection, in-flight invalidation,
all A8a preparation regressions and both runtime noninterference comparisons.
Details: `artifacts/frontier-payload-catalog-20260915/RESULTS.md`.

This is test-instantiated in-memory catalog custody, not a native writer or output
capability. All live/writer/publication flags stay false. The native app request-
to-catalog and exact operational-writer binding are next. Source review confirms
that the existing writer sequence high-water mark is not sufficient proof of an
individual batch's persistence; the new channel needs exact-batch evidence without
treating enqueue/flush observations as authority. See the
[payload/channel contract](file-transfer-frontier-payload-channel-contract.md).

Existing logger/native GUI/primary channels, route authority, primary results and
historical live verdict remain unchanged. No portable rebuild, live-256 run,
commit or push occurred.

## Implementation checkpoint: A8b3, native request owns the Core catalog

Core now compiles the same native request source used by the PowerShell parent.
Its private catalog factory claims one exact owner slot on the actual app lease;
foreign/already-claimed/expired leases cannot create a second native catalog.
Admission checks the captured app process/incarnation. Preparation, take and
snapshot access recheck native custody, including after preparation finishes.
Cancellation, metadata changes, parent death, lease disposal or attachment
completion revoke output without changing runtime observations or send decisions.

Final focused verification passed 40/40: four new native/Core rows (22 child
scenarios plus two real parent-death checks), four existing native handshake rows,
30 catalog cases and both runtime noninterference comparisons. Both parent hosts
launch a real PowerShell 7 app-role process loading Core and the offline service
fixtures. Payloads are prepared before source-stream closure; cancellation during
preparation cannot retain its result. Details:
`artifacts/frontier-native-catalog-20260915/RESULTS.md`.

A subsequent normal-warning build exposed legacy-source annotation warnings.
The compiler-compatible, file-local correction builds Core with zero warnings
and errors; the eight affected native rows then passed 8/8 on both parent hosts.
The superseded PowerShell 5.1 compiler failure is retained in the results record.

This links the native request to private producer custody, not to an operational
writer/source output. All live/writer/publication flags remain false. The factory
is test-called only, with app/GUI activation still disabled. Exact writer-batch
persistence and the original accepted-completion association are next; message
strings, copied batch values and a sequence high-water flush are not sufficient.
See the [payload/channel contract](file-transfer-frontier-payload-channel-contract.md).
No portable rebuild, live-256 run, commit, push or historical reclassification.

## Implementation checkpoint: A8b4, exact writer-submission outcomes

The operational pump can now issue a bounded diagnostic receipt for one exact
submission. Its private reference registry and the original queued object carry
the outcome; copied batch metadata or a later successful high-water flush cannot
upgrade an earlier failed batch. There are 64 lifetime slots with no recycling.
Exhaustion or revocation omits receipts while preserving ordinary logging.

Immediate/deferred success, terminal failure and shutdown abandonment remain
distinct. Revocation is permanent and affects diagnostics only. A real append
that finishes after abandonment stays ineligible even if its bytes reach disk.
The receipt retains no payload/text and proves only the pump-observed append, not
fsync durability, immutable source custody or accepted-completion authority.

Verification: 27/27 focused rows (17 new exact-receipt cases plus 10 existing
logger cases), and a normal-warning Core rebuild with zero warnings/errors.
The earlier-failure/later-flush gap was reproduced before implementation.
Evidence: `artifacts/frontier-exact-batch-20260915/RESULTS.md`.

The new API is test-called only. Queue/retry/marker policy, `LocalOperationalLog`,
Coordinator route authority and historical live results are unchanged. Next is
binding the original accepted-completion envelope and actual private writer to
the native catalog; marker/drain and native file custody are still later gates.
See the [payload/channel contract](file-transfer-frontier-payload-channel-contract.md).
No portable rebuild, live-256 run, commit, push or historical reclassification.

## Implementation checkpoint: A8b5, native catalog owns its original completion write

The native catalog can explicitly register the real operational writer before
producer admission. Registration checks the native catalog owner, actual process
incarnation and run/scenario; the writer itself assigns its identity and batch
metadata. Each producer's original accepted-completion envelope is staged before
the existing enqueue and retained by reference through the background writer.
Neither copied envelopes/text nor reconstructed batch values establish custody.

The existing string queue and snapshot format are preserved. A bounded side
association keys the exact queued string object and is removed by the normal
drain. At most 16 completion-write attempts are owned by one process-lifetime
registration, with no slot recycling. Native revocation or catalog cancellation
removes diagnostic eligibility without suppressing normal logging or changing
Coordinator authority, send settlement, clocks or retry/queue policy.

The catalog returns a completion receipt only for its exact positively prepared
producer and original envelope after the real batch has persisted. Deferred,
failed, foreign, copied or revoked evidence remains unusable. Payload take is
still not a completed native output descriptor. See
`artifacts/frontier-native-writer-20260915/RESULTS.md` for native-process matrices,
writer/catalog checks and existing proof-consumer compatibility verification.

App/GUI activation remains disabled and all live/full-writer-custody/publication
flags stay false. Next: exact digest-marker/completion/drain ordering, then native
payload/source file custody and the separate primary/parent channel. No portable
rebuild, live-256 run, commit, push or historical reclassification occurred.

The wider compatibility gate surfaced pre-existing bootstrap catalog coverage
failures: 58/66 passed, with all four final native writer rows passing and eight
bootstrap rows failing. The current activation-retry profile bypasses a source-hold check gated
only for two older profiles; separate projected-exchange assertions still demand
an older profile. The source-hold expectations must not be weakened. The immediate
next step is the separate [RCA/fix plan](file-transfer-bootstrap-catalog-coverage-rca.md),
before treating compatibility as green or advancing to marker/drain work.

## Compatibility checkpoint: bootstrap catalog coverage, 2026-09-16

The separate bounded fix now applies inherited source-hold requirements to the
repair-owner and activation-retry catalogs, and the projected-exchange fixture
asserts the current emitter profile with frozen hash/parent checks. All 12 distinct
targeted rows have passing latest results, including the original eight failures,
on PowerShell 5.1 and 7. See
`artifacts/bootstrap-catalog-coverage-20260916/RESULTS.md` for the initial test-only
assertion correction and the final focused rerun. The historical 58/66 artifact
and live verdicts are unchanged; the full lane was not rerun.

This resolves the intervening catalog blocker. The next implementation remains
exact completion / digest-marker / writer-drain ordering, then native payload and
source-file custody. App/GUI activation and all live/full-custody/publication flags
remain disabled. No runtime authority or additional production evaluation changed.
No portable rebuild, live-256 test, commit or push occurred.

## Implementation checkpoint: A8b6, exact completion / marker / drain ordering

The owned taken catalog can now explicitly submit digest-only markers for its
original positive preparations after each exact original completion persists.
The writer retains separate completion/marker receipt roles and an exact receipt
for its actual final drain. A high-water flush boolean, copied metadata or a
generic completion receipt cannot substitute for these links. Marker submission
does not imply persistence, and no waits or I/O were added under the service gate.

All admitted producers must have ordered persisted completion < marker < drain
before the catalog exposes original-reference-owned ordered-writer evidence.
Missing/failed/abandoned writes, duplicate markers, post-drain attempts, native
revocation and copied wrappers prevent eligibility. Bounds remain 16 completions,
16 markers and one drain within the pump's existing 64 receipt slots. Ordinary
logging, queue/retry behavior, Coordinator authority and boundary clocks are unchanged.

Verification: 28/28 writer/redaction rows, 2/2 native receipt-role regressions and
58/58 final affected compatibility rows, including fresh marker and existing native
writer matrices, catalog behavior and runtime noninterference. Core rebuilt with
zero warnings/errors. See `artifacts/frontier-native-marker-20260916/RESULTS.md`.

This closes append ordering only. Native payload/source-file identities, retained
read custody, original GUI/app launch ownership and the separate primary/parent
channel remain unfinished. All live/full-custody/publication flags stay false;
app/GUI activation remains disabled. No portable rebuild, live-256 test, commit,
push or historical acceptance reclassification occurred.

## Implementation checkpoint: A8b7, original app-writer source-file custody

2026-09-16. Optional exact writer receipts now retain the native file identity and
physical byte ranges of actual appends. The original final drain obtains a read-only
retained handle from its real append handle, without reopening a caller-selected
path. An owned taken catalog can attempt one full-source/range verification outside
writer/catalog/service gates, then expose original-reference-owned app-local reads.
Bounds are 33 ranges, 256 MiB source bytes and a five-second cooperative scan budget.

Original completion/marker/drain order remains mandatory. Replaced files, changed
selected bytes, copied/foreign receipts or wrappers, incomplete catalogs, repeated
acquisition, native revocation and post-drain attempts cannot establish eligibility.
Late abandoned writes release any captured source rather than upgrading evidence.
All live/full-custody/publication-completion flags remain false.

Verification: 90 distinct focused/compatibility rows passed, including real native
parent/child matrices on PowerShell 5.1 and 7 (23 cases plus parent death per host).
The final affected byte-range follow-up passed 18/18. See
`artifacts/frontier-native-source-20260916/RESULTS.md` for exact run accounting.

This closes the app-local source prerequisite only. Native payload output, versioned
cross-process descriptors, original GUI/app launch ownership and the separate
primary/parent channel remain unfinished. No app/GUI activation, portable rebuild,
live-256 test, commit, push or historical acceptance reclassification occurred.

## Implementation checkpoint: A8b8, original catalog-owned native payload files

2026-09-16. The original taken native catalog can attempt one complete temporary
native payload output after acquiring source custody. It accepts no caller bytes,
hashes or paths. Each file is verified against its original retained preparation;
all original source/completion/marker/drain links remain current before publication
of the app-local wrapper. No successful prefix or replacement attempt is exposed.

Limits remain 16 files, 262144 bytes per payload and 4194304 aggregate payload bytes,
with one additional five-second cooperative output budget. Byte copying, writing
and verification occur outside catalog/service gates. Creation is native CREATE_NEW;
delete-on-close applies from creation through failure, disposal and abrupt process
exit. Retained read-only native handles, not paths, supply identity and reads.
Failed/mismatched output or observed revocation retires the whole optional catalog.

Verification: 52 distinct current focused/compatibility rows pass, including native
payload matrices on PowerShell 5.1 and 7, parent death, partial-catalog cleanup,
existing source/catalog compatibility and runtime noninterference. The initial
delete-pending pathname assertion failures and their focused corrections remain
recorded in `artifacts/frontier-native-payload-files-20260916/RESULTS.md`.

Next is the separately versioned native-handle handoff to the original GUI/app
request roles before app custody ends. Temporary payload files cannot be recovered
by pathname after app exit. No descriptor wire extension, GUI/primary/parent
integration or runtime opt-in is added here. All live/full-writer-custody/publication
flags remain false. No portable rebuild, live-256 run, commit, push or historical
acceptance reclassification occurred.

## Integration follow-up: A8b9, original GUI launch ownership

2026-09-16. The A8b8 handoff review identified a shared-source prerequisite: both
actual app roles write the same acceptance log, while A8b7 freezes it at one app's
drain. That can reject the other app's remaining writes. The legacy GUI writer
issuer also attaches only in cleanup. Prior app-local tests remain valid, but
cannot justify shared-log activation. See
`file-transfer-frontier-shared-source-handoff-rca.md` and A8b9 of
`file-transfer-frontier-payload-channel-contract.md` for the revised two-phase
provisional-handoff/GUI-final-seal contract.

The first bounded step adds a separate one-shot early owner of the original GUI
native request. It binds exactly two actual app process handles and requires a
sealed launch set plus both successful native exits before returning exit evidence.
Original parent/request/GUI metadata remain current; copied or rejected bindings,
partial/extra app sets and cancellation fail closed without terminating apps or
consuming the legacy finalizer. This establishes no file custody, writer drain,
output EOF or publication capability. No production caller was added.

The next step is the versioned original app-request join and provisional native
handle handoff, with no single-app shared-source freeze. GUI-owned final sealing
must wait for every registered writer and all original output/finalizer gates.
Primary/parent integration and end-to-end correctness/cost gates remain later work.
All live/full-custody/publication flags remain false. No portable rebuild, live-256
run, commit, push or historical acceptance reclassification occurred. Verification
is retained in `artifacts/frontier-gui-launch-20260916/RESULTS.md`.

## Implementation checkpoint: A8b10, original GUI-owned native app requests

2026-09-16. The original early GUI owner now creates and privately retains its two
native app requests from the original run/scenario. Each request wrapper binds the
actual launched process to both native request and GUI launch ownership. No
caller-authored endpoint, request, PID, scope, descriptor or catalog is imported.
Both exact native attachment completions, successful exits and a sealed complete
request set are required for the new attachment-completion observation.

Copied wrappers, duplicate/extra/missing bindings, mixed plain/request launches,
native metadata changes and cancellation fail closed. Revocation cancels the
owned app leases without terminating processes or changing the legacy GUI writer
request. GUI and app request script loaders now share one codec assembly; managed
apps retain the same codec linked into Core. Existing wire formats are unchanged.

The two-app provisional handoff limits are explicit in A8b10 of
`file-transfer-frontier-payload-channel-contract.md`: at most 32 files, 8 MiB of
payload, 66 exact source ranges, one 256 MiB shared source and 64 KiB of descriptor
metadata. Per-app limits and the independent 64-witness cap are unchanged. These
are policy for the forthcoming file channel; no file handles are transferred here.

The next edge is a writable provisional source anchor, then bounded native-handle
adoption before app exit and a GUI-owned final seal after every writer/output
gate. Writer-only handoff must cover apps with no positive payload. All
live/full-custody/publication flags remain false; no production activation,
portable rebuild, live-256 run, commit, push or historical reclassification.
See `artifacts/frontier-gui-app-join-20260916/RESULTS.md` for focused verification.

## Implementation checkpoint: A8b11, provisional native source retention

2026-09-16. An isolated internal native-file primitive now retains the actual
source handle with read-only access and read/write/delete sharing. It allows
another writer's marker/drain appends and both logger rotation policies while
preserving the original native file identity. It does not reopen paths or delete
source/replacement files. Invalid/non-file inputs and observed size-bound failures
are rejected; copied wrappers cannot close or read the original.

The captured length is provisional, not final integrity proof. Same-length
mutation remains possible and must be rejected by the later exact-range/hash
verification. The primitive issues no hash, seal or publication capability and
has no operational writer/catalog/GUI caller. Existing frozen-source behavior is
unchanged; no caller-authored mode or environment flag bypasses its checks.

The handoff review now requires GUI-side pull duplication and process-local
rollback, avoiding untracked handles from an app dying during a remote push.
The next versioned channel still needs exact wire/readiness/rendezvous integration
before it can authorize provisional writer capture. Final shared-source sealing
remains after every original drain, successful exit and output EOF gate.

Verification is retained in `artifacts/frontier-provisional-source-20260916/RESULTS.md`,
including the reproduced rotation-sharing failure and correction. No activation,
portable rebuild, live-256 test, commit, push or historical reclassification.

## Implementation checkpoint: A8b12, bounded file wire and GUI-side pull

2026-09-16. One internal versioned manifest codec and two-slot native adoption
primitive now run in Core and the PowerShell ownership bundle. Bounds and exact
wire length are checked before duplicate/read work. GUI-owned read-only handles
retain both apps' payload files and the same native shared source, with exact
payload hashes, cross-app conflict checks and one cumulative five-second active
work budget. Failure/replay/revocation discards the whole optional set and closes
only local handles. Shared-source writes and rotation remain permitted.

This is transport, not original writer authority: the native fixtures use
synthetic range metadata, and the primitive accepts no production caller yet.
No final source/range hash is established, no acknowledgment is issued and no
runtime capture mode changes. The request-bound readiness/submission/acknowledgment
rendezvous remains the first integration edge, followed by authorized writer
capture and final all-writer sealing. Existing app/GUI completion schemas and
isolated source freezing remain unchanged. Verification is retained in
`artifacts/frontier-file-handoff-20260916/RESULTS.md`. No live run, commit or push.

## Implementation checkpoint: A8b13, original-request file rendezvous

2026-09-16. The original sealed GUI/app-request registry can now create the
separate versioned readiness/submission/acknowledgment endpoints. The original
Core catalog attaches through its exact live native lease; no endpoint import or
runtime boolean enables capture. One internal submission pins the actual source/
payload handle objects, and the GUI performs one all-or-none pull before issuing
the exact submission-nonce acknowledgment. Core must take it before attachment
completion retires catalog access. Every original ownership gate remains checked.

Premature completion, cancellation, malformed/changed descriptor or nonce, replay,
contention, budget expiry and unsuccessful process closure omit the complete
optional handoff. Existing no-channel app completion and GUI finalizer behavior
remain independent. Control and file work use one cumulative adoption budget;
catalog cancellation does not wait on an acknowledgment holding its local gate.

This closes the request protocol, not the genuine writer issuer. The new native
fixtures retain actual files under an original Core catalog but supply synthetic
receipt metadata. Production submission, explicit writer-only ownership, exact
provisional writer output and final all-writer source sealing remain unimplemented.
All live/full-custody/publication eligibility remains false. Verification is in
`artifacts/frontier-file-rendezvous-20260916/RESULTS.md`. No runtime activation,
portable rebuild, live-256 run, commit, push or historical acceptance upgrade.

## Implementation checkpoint: A8b14, genuine provisional submission

2026-09-16. The private catalog now issues file submissions from its original
prepared payloads and persisted completion/marker/drain receipts. Only an original
attached file handoff selects the new provisional writer drain; its actual source
handle remains writable/rotatable for the other app. Failed attachment does not
select the old per-app source freeze. Explicit writer-only ownership is separate
from positive payload preparation, and requires a real drain with no completion
or marker receipts. Files/metadata are bounded, all-or-none and one-shot; late
writes or cancellation retire even an already-submitted optional channel.

Two real app processes now exercise genuine producer/writer output on the same
shared log, with the second app writing after the first drains. Their native
handoff is still provisional, not final source verification or production opt-in.
The next edge is GUI-owned sealing after every original contribution, successful
attachment/native exit, and original output EOF/drain gate. Primary/parent output
custody and correctness approval remain later. See A8b14 in the payload-channel
contract and `artifacts/frontier-genuine-submission-20260916/RESULTS.md`.

## Implementation checkpoint: A8b15, original output EOF ownership

2026-09-16. Final-seal review identified missing original stdout/stderr ownership
in the early GUI registry. An original app-request start path now owns both raw
streams and their capture sinks from launch. EOF is eligible only after both
original pumps finish successful capture/flush, with current exact-reference GUI
ownership. Process exits, quiet files, caller tasks and old `BindApp` records do
not substitute for this evidence. Failed capture/revocation cannot stop apps or
close their live pipes; failure remains ineligible while the pumps drain.

This is still an unactivated integration primitive. Production GUI output capture
and finalization remain unchanged. Final source sealing must join original
drains/files, attachment/native exit and output EOF under the existing cumulative
GUI work budget. Finalizer output, primary/parent custody and correctness approval
remain later. All live/full-custody/publication flags remain false. Verification:
`artifacts/frontier-output-ownership-20260916/RESULTS.md`. No portable rebuild,
live-256 run, commit, push or historical acceptance upgrade.

## Implementation checkpoint: A8b16, final source sealing

2026-09-16. The original GUI's one-shot no-input seal now joins both acknowledged
writer/file contributions, original attachment/native exit success and original
stdout/stderr EOF. It reopens the actual retained native source with write/delete
exclusion, then verifies the whole-file digest and every original raw receipt
range in one bounded traversal. The existing cumulative GUI work budget and
two-app/66-range/256-MiB bounds remain unchanged. Any failure or repeated attempt
retires the complete optional adopted/sealed set.

The resulting exact-reference wrapper is revocable and diagnostic-only. It does
not export handles/readers, consume the old GUI finalizer, enable production
capture or grant correctness/runtime authority. Separate bounded GUI output and
original primary/parent custody are the next integration edge. See A8b16 of the
payload-channel contract and `artifacts/frontier-final-source-seal-20260916/RESULTS.md`.

## Implementation checkpoint: A8b17, original finalizer binding

2026-09-16. One no-input attempt now binds the original finalizer to the original
sealed files, requiring the same native endpoint as well as original scope and
process incarnations. Copy, relay and foreign-endpoint controls cannot acquire or
retire the original set. Repeat, stale scope, disposal and any original legacy
completion attempt retire the matching optional lane irreversibly; ordinary
legacy completion remains independently usable and its schema is unchanged.

The raw acceptance source is distinct from generated ledger/semantic outputs;
the A8b18 review clarifies that the append-only processor input uses the same
configured pathname as the apps, but its independent reopen lacks original native
source binding. This binding attests only to ownership, not later validation,
copy/persistence or publication. The next edge is a separately reviewed output
handoff with native raw-source origin mapping and original-parent pull/adoption,
acknowledgment and cleanup. Production remains disabled, all eligibility flags
remain false, and there is no portable rebuild, live-256 test, commit or push.
See A8b17 of the payload-channel contract and
`artifacts/frontier-finalizer-binding-20260916/RESULTS.md`.

## Implementation checkpoint: A8b18, original sealed-source read access

2026-09-16. The original finalizer binding can open one non-seekable, read-only
stream over the already sealed native source handle, with no path/descriptor/
reader import or new handle. Each read is capped at 32 KiB, uses the existing
cumulative GUI budget, and checks native source/scope before and after I/O.
Copies, repeats, cancellation, disposal and legacy completion cannot keep or
revive source access. Disposal releases the matching optional file set.

Review corrected the input/output distinction: the append-only finalizer input
and app acceptance log use the same configured pathname; derived output slices
are separate files. The old processor reopens the path and is not yet connected
to this owned reader. Reader EOF is not parser validation/output publication.
Next is the opt-in processor/result/origin binding, then bounded parent custody.
All eligibility flags remain false; production and live-256 remain disabled.
See `artifacts/frontier-finalizer-source-20260916/RESULTS.md` and A8b18 in the
payload-channel contract.

## Implementation checkpoint: A8b19, owned-source processing and origins

2026-09-16. An explicit opt-in helper now derives scope/output root from the
original finalizer binding and passes its one-shot owned reader to the existing
streaming processor. The processor preserves its writer/chronology checks and
output bytes, requires exact full-source line consumption, and records bounded
one-based physical source origins for every retained/writer/semantic output line.
The entire processing interval uses the existing cumulative GUI work budget.

The separate source/output provenance record is reference-owned by the original
result; its original source relationship remains revocable. It does not populate
the legacy completion capability. Current output-path contents, immutable native
output custody, remaining finalizer validation/copy/persistence and parent/primary
custody remain unproved. Failed optional temporary outputs are artifacts, not
published inputs. Production calls and all eligibility flags remain unchanged.

Next is actual output-handle retention/sealing and remaining finalizer gates,
then bounded original-parent adoption and primary custody. See A8b19 of the
payload-channel contract and `artifacts/frontier-ledger-processing-20260916/RESULTS.md`.

## Implementation checkpoint: A8b20, original retained output handles

2026-09-16. The opt-in owned-source helper now retains three read-only native
output handles derived directly from the processor's original write handles.
Write/delete exclusion remains continuous across that transition; no output path
is reopened to reconstruct custody. Original identities/lengths, source ownership
and exact result ownership are checked together. The existing cumulative GUI work
budget covers construction/sealing, and no extra hash pass is introduced.

Original source retirement closes the retained set through a cleanup-only token.
Copied captures cannot release original handles; cancellation, stale handles,
partial failures and explicit disposal cannot leave an eligible result. Generic
streams/tokens do not acquire original GUI custody. Legacy processor output behavior
and the legacy completion format remain unchanged.

Remaining finalizer validation/copy/persistence and original-parent/primary custody
are still unimplemented. Production integration and all eligibility flags remain
disabled. See A8b20 of the payload-channel contract and
`artifacts/frontier-retained-outputs-20260916/RESULTS.md`.

## Implementation checkpoint: A8b21, immutable processor validation facts

2026-09-16. The original stream capture now owns a bounded immutable snapshot of
processor counters, writer scope/sequence facts, drain count/unique sequence and
deferred-batch tuples/hashes. Mutating the returned public result cannot rewrite
these captured facts. A separate writer-closure check preserves the existing
distinction between processor errors and missing/duplicate/non-terminal drains.
Copied, foreign or retired snapshots cannot establish current ownership.

The GUI helper requires its exact current snapshot; bounds failure omits the
optional result and releases its owned set. This is not full finalizer approval:
native writer membership, original recovery-marker custody, cursor validation,
semantic-copy and persistence remain separate gates. All eligibility flags and
production wiring stay disabled. See A8b21 of the payload-channel contract,
`docs/file-transfer-frontier-finalizer-gates-rca.md`, and
`artifacts/frontier-validation-snapshot-20260916/RESULTS.md`.
