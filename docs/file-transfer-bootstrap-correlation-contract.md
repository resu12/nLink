# Bounded fallback-bootstrap correlation contract

Status: bounded implementation and focused verification completed on 2026-09-11.
Production capability remains disabled; deployment and live validation are separate gates.
Design/source baseline: `d654e55d308fbe1208e84c80ead035b2664a6c86`.
Preceding evidence: [final-off RCA](file-transfer-final-off-bootstrap-rca-and-fix-plan.md).
The original12/12 characterization is historical, not implementation acceptance.
Verification history is recorded in
`artifacts/validation/consolidation-bounded-20260907/bootstrap-correlation-implementation/WORK-IN-PROGRESS.md`.

## Decision and scope

Use a negotiated, two-sided correlation exchange. The sender issues request ID S;
the receiver issues challenge R. The sender must receive an exact echo of S, and
the receiver must receive an exact echo of R. Both IDs are bound to their issuing
Coordinator's current bootstrap command. Neither peer's generation is compared
with the other peer's local generation.

A sender-only echo fixes the demonstrated ReceiverState replay, but is insufficient
as the complete contract: removing inbound peer/local generation equality must not
allow an old request to authorize the receiver's new cycle. R closes that direction.
One additional sender confirmation is necessary; this is a correctness exchange,
not a retry cadence or throughput change. No generic challenge framework is needed.

Scope is bootstrap admission/proof for TunaV4 -> FallbackV6 and the exact preceding
reactivation lineage. Regular transfer, repair, checkpoint scheduling, fallback
worker policy, Tuna activation proofs, GUI actions, queues, performance, wallet and
installer behavior remain outside scope. Route versions remain4/4/6; TunaV6 is invalid.

## Source constraints at the design baseline

| Current source | Constraint |
|---|---|
| `FileTransferProtocol.cs:112,159,185` | Legacy request encodes local generation; state has no request correlation. |
| `FileTransferDataFrameCodec.cs:568,785,1847` | State code41 rejects trailing bytes; request code45 has a legacy optional tail; codes40-54 are allocated. |
| `FileTransferCoordinator.cs:6235,6378,6519,6602` | Bootstrap is one-shot; local Delivered alone currently enables a generation-less state as peer proof. |
| `FileTransferCoordinator.cs:23806` | Existing authority ID omits CoordinatorInstanceId and truncates the hash; do not reuse it alone as a restart-qualified challenge. |
| `IFileTransferSignalingTransport.cs:941` | Exact peer-frame identity hashes serialized bytes; every correlation field must participate. |
| `NknSignalingTransport.Acceleration.cs:13860` | Adapter extracts observations, not peer/local generation authority. |
| `FileTransferOfferV2.cs`, `FileTransferAcceptV1.cs`, `FileTransferSessionOpenV2.cs` | No peer-negotiated bootstrap correlation feature exists. Local V6 capability is not peer agreement. |

## 1. Negotiation and compatibility

Add only two optional signaling properties: `fallbackBootstrapCorrelationVersion`
and `fallbackBootstrapIssuerId`. Preserve the existing one-way sequence: sender
Offer -> receiver Accept -> sender SessionOpen. Offer advertises version1 and the
sender's issuer; Accept selects version1 and supplies the receiver's issuer only
when supported and offered. Sender SessionOpen echoes version1 and the sender's
issuer, not the receiver's. No receiver SessionOpen or new acknowledgment is added.
A malformed selection, unsolicited upgrade, changed issuer or disagreement rejects
that negotiation; it does not become legacy.

The following are proposed Coordinator agreement events, not existing API names:

- `SenderAcceptApplied`: validate the exact Accept against the current transfer's
  pinned Offer and install the immutable selection and both issuers in the outbound
  Coordinator. Before dispatch, validate the sender's outgoing SessionOpen against
  that agreement. The sender never waits for a receiver SessionOpen; local emission
  is not represented as receipt of peer confirmation.
- `ReceiverAcceptSelectionRecorded`: pin the exact offered sender issuer and the
  receiver's Accept selection in the inbound Coordinator, initially not enabled for
  correlated bootstrap. `ReceiverSessionOpenApplied` validates the incoming sender
  SessionOpen against that record and enables its use. Both events require the
  exact current transfer/context; neither installs a new route or runtime owner.

The agreement record contains the selected mode/version, sender and receiver
issuers, exact Offer/Accept identities and local transfer/Coordinator identity.
Selection fields are immutable; the receiver's validated-open readiness is a
one-way Coordinator transition. Matching duplicate Accept/SessionOpen evidence is
idempotent; mismatches are rejected, and stale-transfer evidence cannot install or
enable an agreement. Absent support follows the legacy row below, not this enablement.

Issuer is the lowercase SHA256 of a domain-separated, length-prefixed canonical
tuple ("fallback-bootstrap-issuer-v1", CoordinatorInstanceId, session, transfer,
local direction). The current service creates a fresh GUID CoordinatorInstanceId
per context. Coordinator records the exact agreement in per-transfer state before
any correlated bootstrap command can issue. Signaling fields are evidence of peer
support, never route/runtime authorization. The service must not infer agreement
from an app version, listener, local capability or incoming data-frame shape.

The agreement is immutable for the transfer. There is no in-flight upgrade,
issuer rollover, downgrade-on-timeout or reinterpretation of historical receipts.
A replaced transfer context requires a fresh transfer negotiation. Replacing a
data-session within that transfer invalidates pending commands through normal
scope currentness; it does not reset the negotiated issuer or replay history.

| Peers / state | Behavior |
|---|---|
| Both select version1 on a fresh transfer | New exchange from the first bootstrap cycle onward. |
| Offer or Accept omits support | Legacy wire/behavior remains separate; the new successor-admission branch is unavailable. No correlated-cycle safety claim for that legacy path. |
| Existing active transfer without agreement | No migration; retain its existing behavior. |
| Version1 selected, then legacy bootstrap frame arrives | Diagnostic-only; cannot satisfy a version1 peer-proof boundary or downgrade the mode. |
| Unknown version or mismatched issuer | Reject negotiation/frame as applicable, with a bounded reason. |

Preserving legacy behavior is a compatibility boundary, not a claim that the
legacy replay defect is solved. Requiring all transfers to use the new mode or
disabling old-peer Tuna activation would be a separate product-policy decision.

## 2. Wire representation

Reserve two new V6 frame types/codes at this HEAD (recheck allocation before coding):

- `filetransfer.fallback_bootstrap_request.v6`, code55, proposed DTO
  `FileTransferFallbackBootstrapRequestFrameV6`.
- `filetransfer.fallback_bootstrap_state.v6`, code56, proposed DTO
  `FileTransferFallbackBootstrapStateFrameV6`.

Keep NFT2 binary version1, all old codes and old serialized bytes unchanged. Old
decoders reject unknown codes; they must never receive these without negotiation.
Use explicit new DTOs, not inheritance that could fall through a legacy state or
repair handler. No field is hidden in BatchId, RepairRequestId, pause reason,
RecoveryMode or the existing request-generation string.

Both frames use the existing session/transfer envelope and V6 transport metadata.
Require positive target transport epoch, priority `proof`, mode
`post_tuna_fallback_v6_proof_bootstrap_correlated_v1`, empty batch/repair IDs, and no
repair ranges. The state payload retains the existing durable frontier/bytes,
state-epoch and paused/no-credit/no-pressure/nonterminal bootstrap constraints.
It is never ordinary flow-control state before its exact proof is admitted.

Append fields within the *new* code's payload in this order: optional
`SenderRequestId`, optional `ReceiverChallengeId`. Request requires S; state
requires R. Request with R absent is Solicit; request with both is Confirm. State
with S absent is Announce; state with both is Response. Neither ID may be absent
from a Confirm/Response, and no trailing bytes are accepted. State serializes its
normal durable-state body before the two explicit fields. Request has no repair
body or legacy RequestCorrelationId. Domain/frame kind fixes which ID the sender
is allowed to originate and which it may only echo.

Each ID has exact ASCII form `fbc1:<issuer64>:<generation>:<scope64>` (maximum154
characters). Generation is canonical positive Int64 decimal without sign or leading
zero; hashes are64 lowercase hexadecimal characters. Reject whitespace, trimming,
case folding, overflow, truncation and malformed/oversized IDs. Do not pass IDs
through `NormalizeTransportMetadataToken`, which truncates and trims.

Scope hash is full SHA256 over a new domain tag and length-prefixed canonical
fields: issuer, session, transfer, direction, local bootstrap generation, command
authority ID, exact runtime-owner/publication/route-commit identities, terminal and
live-route epochs, leg, source/target transport epochs, bridge generation,
data-session identity/generation and durable-checkpoint identity. Freeze the ID
in the Coordinator-issued command; adapters cannot generate or rewrite it. Fields
use explicit invariant encodings, not record ToString, JSON property order or
delimiter concatenation. The implementation must supply canonical golden vectors.

These IDs prove exact exchange correlation, not peer authorization or cryptographic
identity. Existing authenticated transport and current transfer/scope validation
remain required. The receiver treats S as opaque apart from issuer/generation
validation; the sender treats R likewise. Neither reconstructs the peer's local
authority tokens or trusts a peer-provided scope hash as local authority.

## 3. Exchange and completion predicates

S and R below include the full issuer/generation/scope ID, not just their counters.

| Step | Sender-first | Receiver-first |
|---|---|---|
| 1 | Sender sends Solicit(S, no R). | Receiver sends Announce(no S, R). |
| 2 | Receiver sends Response(S, R). | Sender sends Solicit(S, no R); announcement is only a wake hint. |
| 3 | Sender validates echoS, sends Confirm(S, R). | Receiver sends Response(S, R) under the existing R command. |
| 4 | Exchange can close on exact receipts. | Sender validates echoS, sends Confirm(S, R); exchange can close. |

An Announce never causes the sender to bind its R directly: a delayed announcement
must not choose the new peer-proof binding. Only a Response echoing current S may
bind R. Simultaneous start uses the same rules; redundant Announce is diagnostic
after the corresponding Response. Existing transport-epoch announcements remain
evidence only and do not count as any of these proof edges.

### Phase ordering

Coordinator elects the receiver's first phase exactly once, atomically with its
issuance. A valid Solicit retained before Announce issuance elects Response first;
no Announce may subsequently issue in that exchange. If Announce was already issued,
retain the exact admissible Solicit (duplicates are idempotent) but issue no Response
until that Announce's exact Delivered AND Settled receipts have been applied.
The same predecessor gate applies to sender Solicit -> Confirm, even if Response
arrives before Solicit's callbacks. A retained frame is evidence, not a dispatch.

An issued Announce is no longer optional: Held waits on its owned existing wake;
failure/cancellation cannot skip it to dispatch Response. The first eligible
Coordinator re-evaluation after both predecessor receipts issues one next phase;
duplicate callbacks cannot issue another. No earlier phase may remain active when
the next phase starts or the exchange completes. Preserve the single-active bridge
executor; do not add a queue, timer or per-phase concurrent execution.

### Delivery and physical settlement

Map both new DTOs to the existing V6 feedback lane policy: state Announce/Response
like legacy ReceiverState, request Solicit/Confirm like legacy FrontierRequest.
With the existing redundancy policy enabled, each logical frame uses the existing
parallel control and bulk copies with first-success delivery. When that policy is
disabled, preserve the existing single control lane. Epoch announcements use their
existing identical policy. Do not change overrides, scheduling or copy concurrency.

Send the existing epoch announcement only as a tracked sub-operation of the elected
first local phase (Solicit, Announce or Response). Freeze its identity with that
phase; require epoch delivery before the proof-frame submission, rechecking command
currentness between them. Later Response/Confirm phases do not repeat the epoch.

Each exact phase has a closed physical submission record: required logical frame
identities (epoch when first, plus proof), submitted copy identities/lanes, their
outcomes and the same command/source/data-session/cancellation scope. Infra retains
the physical completion handles; Coordinator receives immutable identity/outcome
evidence, not task handles. Register every started copy, including partial-submission
failures, and seal registration only once no further copy can start. Delivery/
settlement observations are evidence applied by Coordinator, never adapter-granted
phase or route authority.

- `Delivered`: at least one exact physical copy succeeded for **each** required
  logical frame. Epoch success alone is not proof-frame delivery. Queue acceptance
  is insufficient. First-success delivery does not imply all copies have finished.
- `Settled`: submission is sealed and every submitted physical copy has actually
  completed, failed or canceled. A cancellation request or first-success observer
  alone is insufficient. Settlement with no successful proof delivery cannot advance
  the phase. A secondary failure after primary success settles without rewriting
  Delivered, provided the command remains current.

The existing SubmitPhysicalSend wrapper does not currently track both feedback
copies: pass bounded physical tracking through the redundant-feedback helper and
register all copies before first-success return. Include the epoch copies in the
phase evidence; do not infer coverage from an empty inbound drain or an unregistered
outbound lease. Require both exact receipts for every issued local phase before
route preparation. Existing subsequent data-path/pending-write drains stay intact;
this bootstrap-specific gate does not require a generic inbound drain redesign.

### Exchange completion

Sender completion requires current local commandS; local Solicit and Confirm(S,R)
each Delivered AND Settled; and exact retained Response(S,R) on the current source/
data-session. Receiver completion requires current local commandR; Response(S,R)
Delivered AND Settled; exact retained Confirm(S,R) echoing R; and, if issued, Announce
Delivered AND Settled. Announce alone is insufficient. Peer frames and local
delivery/settlement callbacks may arrive in either order; retain exact evidence and
recheck only through the corresponding current Coordinator decision.

Only after those predicates may Coordinator issue the existing route-transition
proof boundary. The sender retains the Response state; the receiver retains the
Confirm request. Prepare, pause binding, physical drain, commit, retained-frame
application, publication and disposition remain distinct existing authority edges.
Receiving a bootstrap state cannot update progress, release credit or run ordinary
V6 repair logic before the coordinator-authorized retained-frame application.

After the predecessor gate, issue Response as a separate exact execution command
under the same bootstrap authority. Do not rerun or mutate Announce's receipt or
increment bootstrap cycle generation. Likewise Confirm is a new execution phase,
not a second bootstrap cycle.

For version1, the base command's Delivered record references its first exact
Delivered phase receipt: Solicit for the sender, Announce or Response for the
receiver depending on arrival order. It is not a second transport event. A later
phase never overwrites that record. Exchange completion is a separate Coordinator
result requiring the predicates above; the prior-local-receipt successor check
must require both this base record and the completed exchange. Legacy receipt
layout and meaning remain unchanged in the legacy profile.

## 4. Coordinator state, effects and admission

Extend the existing route-transition bootstrap substate, not adapter dictionaries,
with a correlated exchange record containing: negotiated agreement reference;
local base command/ID; at most one peer candidate and one validated peer binding;
first-phase election; immutable phase commands, sealed submission evidence and
separate delivery/settlement receipts; exact retained peer-frame identity;
completion state; and the last completed peer issuer/generation/ID. Preserve the
prior completed exchange through fallback commit and subsequent reactivation.

Required commands are local phases Solicit/Confirm (outbound) and Response with an
optional elected Announce predecessor (inbound). Each phase identity is tied to the base command,
frozen frame hash, exact data-session/source/checkpoint tuple and cancellation
scope. A phase has at most one execution of its prescribed frame/copy set; an
in-flight duplicate joins that execution, and a terminal duplicate returns its
existing receipts without another send. Once submission starts, a phase remains
active until its physical submission is sealed and settled, even after Delivered
has been reported.

`Held` is allowed only before any physical submission, including an epoch copy.
It may resume the same current command through an explicitly owned existing wake,
not a new timer. Once any copy starts, unavailability, interruption or partial
failure cannot return Held, forget that work or resend the phase identity. Retain
and observe all started copies through settlement; if any required logical frame
has no successful delivery when execution ends, close this exchange fail-closed.
Source invalidation immediately retires authority even while physical work settles.
No retry/replacement or successful-phase retransmission policy is added.

The Infra execution cache currently keys its terminal receipt/active task by the
base command (`NknSignalingTransport.Acceleration.cs:2492`). For version1 it must
key by the exact Coordinator-issued phase command; otherwise Response would merely
return Announce's old receipt. Keep the one-active execution slot until all copies
settle, and release it before publishing the settlement callback that can unlock the
next phase. Coordinator still enforces the predecessor gate; cache contents remain
execution evidence only and cannot authorize dispatch.
Likewise the existing once-per-cycle evaluation coalescer at line14048 is not a
phase dispatcher. Preserve wake coalescing, but route each decoded correlated phase
to the service/Coordinator with its exact frame/source identity and phase-specific
deduplication. Do not remove coalescing, add polling, or let a changed fingerprint
grant a second local bootstrap cycle.

Issue/adopt phase commands and apply receipts atomically through Coordinator-authored
envelopes. The service/bridge checks currentness before each send and on callback.
Only exact Delivered plus Settled can advance the phase; queue/send acceptance does
not. An unsuccessful phase cannot be promoted to Delivered or replaced under the
same identity. A secondary-copy failure is not a phase delivery failure when every
required logical frame already has a successful copy. Other current phase failure
closes this exchange fail-closed; there is no heuristic fallback or retry extension.
Rejected/stale callbacks are diagnostic-only. After invalidation, physical tasks
may finish and release only their own execution resources; their observations
cannot mutate the current exchange, apply a new authoritative receipt or dispatch
the next phase. Historical receipts remain unchanged.

Before any current-cycle bind or phase issuance, validate negotiated mode/issuer,
exact physical frame/profile, source ownership, route/publication/commit/leg,
terminal/checkpoint scope and actual data-session reference plus provider generation.
Inbound still waits for pending writes to drain. Existing pending-write wake
supersession rules remain exact; neither hint nor stored frame can resume work.

Initial activation requires the actual committed/disposition-applied RegularV4 ->
TunaV4 lineage. A successor requires the exact prior local Delivered base receipt,
completed correlated exchange, committed fallback and current committed/disposition-
applied reactivation. Reuse the existing read-only reactivation predicate where
applicable, adding the exchange lineage; do not populate legacy V6 markers. The
local next bootstrap/fallback cycle is coordinator-owned, not assigned from the
incoming peer counter or adapter observation count. This is the bounded new
outbound admission branch; legacy branches remain mode-isolated.

Peer generation is ordered only within its negotiated issuer domain. Retain the
highest *completed bound* peer generation across route changes; hints never advance
it. Old/equal completed-cycle IDs are rejected in a new cycle. A duplicate exact
current ID is idempotent; equal generation with another scope hash is a conflict.
Do not require peer generation == local generation or assume either increments in
lockstep. Reused transport epoch1 and unchanged durable frontier confer no freshness.

An unknown unbound Solicit is evidence requesting a response, not peer proof. It
must pass local cycle admission before Coordinator may issue Response. It cannot
advance the completed-peer watermark or complete receiver fallback. Known retired
IDs cause no new phase/receipt/state mutation. A different candidate competing for
an already-issued current Response is rejected; no last-writer-wins rebinding or
automatic recovery is introduced. If a legitimate peer restarts its unfinished
exchange into such a conflict, this slice fails closed and reports that proof edge;
it does not silently expand into a restart/repair protocol.

Pause, terminal, runtime/publication/commit/leg/checkpoint supersession, replaced
data-session, disposal and route commit retire the applicable pending phase/binding
authority. Late callbacks cannot send Confirm/Response, update progress/checkpoints,
retire workers, defer liveness, recover a bridge, retry an offer, commit/publish a
route or resume fallback. Historical completed receipts remain immutable evidence.

## 5. Evidence and compatibility of evidence

Add the exchange to the existing `route_transition` family, not a new authority
family. Snapshot/projector/redactor evidence must identify local bootstrap/phase
authority, mode, role, agreement readiness, first-phase election, predecessor,
ID fingerprints, peer binding, exact frame/submitted-copy identities, sealed
submission, separate delivery/settlement receipts and completion disposition.
Rejected attempts are stateApplied=false and cannot produce physical authority
effects. Frame identity must cover both IDs.

Readers require a nonempty agreement fingerprint and issuer fingerprints matching
the preceding Coordinator-pinned, enabled agreement; that enabled agreement is
immutable. Phase progress and newly added receipts must belong to the legal
Coordinator decision and match the preceding phase state. Unchanged receipts may
be carried into later decisions, including invalidation, without granting progress.
Validate this history before independent physical-effect findings so neither proof
requirement hides the other.
Agreement evidence follows a closed, direction-specific negotiation table. Both roles
start with accepted PinFallbackBootstrapOffer, None -> Offered, selected version0.
Accepted ApplyFallbackBootstrapAccept moves outbound Offered -> Enabled for version1
or Offered -> Selected for version0; inbound moves Offered -> Selected for either
version. Accepted receiver ApplyFallbackBootstrapSessionOpen moves Selected -> Enabled
for version1, or records one Selected -> Selected mutation for legacy version0.
Version1 requires both issuers; version0 has no receiver issuer. Agreement identity,
generation, source boundary and sender issuer are pinned at Offer; selected version and
receiver issuer are immutable after Accept. Before-state must match observed history.
Unchanged agreement carriers cannot change the payload or command fingerprint, and
repeated legacy SessionOpen cannot introduce another mutation. Legacy-disabled and
advertised-but-declined handshakes remain valid without granting correlated authority.

Introduce an explicit catalog extension `fallback-bootstrap-correlation-v1` on the
current composite profile and compute its real hash during implementation. Do not
invent a hash in this design. Preserve all existing profile/hash meanings and
historical readers. Update full/compact/shared-GUI consumers only for this proof
extension; old profiles cannot claim correlated exchange completion. Do not repair
old artifacts, populate absent IDs or turn the12/12 characterization result into
runtime acceptance. Add one bounded rejection reason at the admission boundary.

## 6. Bounded implementation and test gate

Implement in this order, with focused checks only:

1. Signaling agreement, exact ID format, new frame DTOs/codec and byte identities.
   Verify both peer-version pairings, absent/unknown/malformed fields, pinned issuer,
   old byte-for-byte goldens and new canonical vectors. Prove the existing Offer ->
   Accept -> sender SessionOpen sequence installs the agreed profile without another
   message; receiver remains disabled until the matching open. Include duplicate
   Accept/open, issuer mismatch and stale-transfer controls. No runtime admission yet.
2. Coordinator phase lifecycle, exact local/peer binding and completed lineage.
   Verify both start orders and simultaneous delivery, response-before-receipt,
   duplicate/in-flight/terminal phase receipts and asymmetric peer/local counters.
   Block Announce, receive Solicit and duplicates, and prove zero Response/conflict/
   commit until exact delivery AND settlement apply, then one Response. Also test
   Solicit-first election with no later Announce, the Solicit -> Confirm gate,
   Held predecessor, failure/cancellation and source invalidation while waiting.
3. Service/Infra execution and the bounded outbound successor branch. Preserve
   legacy isolation, pending-write, command-first, source/physical-trigger checks.
   Prove actual serialization/receive dispatch cannot fall through to ordinary
   state/repair handlers, including duplicate delivery over both transport copies.
   Prove the unchanged enabled/disabled lane mapping; primary success plus blocked
   secondary cannot advance a phase or prepare/commit fallback. Secondary success
   or failure settles it without erasing delivery. Cover epoch-only success,
   partial submission, Held-before-submission only, cancellation request versus
   actual settlement, executor-slot release ordering and duplicate/stale callbacks.
4. Snapshot/catalog consumers and rejection evidence; verify full/compact/shared-GUI
   parity for this exchange and preserve old-profile fixtures. No full report.

Mandatory adversarial cases: prior Response against current S; prior Confirm
against current R; retired Solicit/Announce before fresh frames; identical payload/
frontier and reused target epoch1; wrong issuer/generation/hash/role/transport;
missing echo; fake Delivered/queue-only receipt; replaced or paused/terminal scope
at each phase; conflicting peer candidate; and restart without renegotiation.
Each rejected case asserts unchanged authority/route/publication/progress/checkpoint/
worker/liveness state and zero newly authorized sends. The unbound wake case is
tested separately as a valid local evaluation request, never mislabeled peer proof.

Convert the two current replay-acceptance characterizations to rejection tests for
negotiated version1; retain explicitly labeled legacy characterization separately.
Upgrade the real-history sender test to complete one exact successor exchange and
observe both final fallback route transitions with no seeded legacy markers. Add
a receiver lifecycle fixture through real Coordinator history, not synthesized
committed terminal records. Deterministic modeled receipts are not live delivery.

After focused lanes stabilize, run at most one relevant broader contract lane.
Stop at the first new missing proof edge. Review/commit is separate; a fresh portable
build and one escalated targeted live scenario require new live authorization.
There is no new production-attempt or report allowance here: attempts9/reports3
remain consumed and attempt9 remains FAIL.

## Review exit criteria

This document fixes the proposed wire shape, negotiation boundary and two-sided
proof semantics. It does not approve code execution or promise mixed-version route
cycling. Before bounded implementation approval, review particularly: (a) version1
selection cannot fall through legacy promotion, (b) each side requires its own ID
echo, (c) peer counters stay in separate domains, (d) exact predecessor Delivered
AND Settled gates each later phase and no issued phase remains active at completion,
(e) all epoch/proof copies are tracked without partial-send retry or changed lane
policy, (f) sender-only SessionOpen installs agreement in the stated direction, and
(g) no stale/unbound evidence can become route or runtime authority. No throughput
or automatic restart work is part of that approval.
