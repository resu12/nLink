# Bounded fallback-bootstrap correlation contract

Current status: section23.22 records authorization to proceed with routine reversible work
on2026-09-12, including the verified five-file commit and local-only portable packaging.
Section23.21 resolved the stale capability assertion; its focused pass and the prior15
selected enablement passes are retained. Backend ownership and runtime authority are unchanged.
Deployment, live execution, exhausted campaign/report allowances and unrelated frontier
files remain separate boundaries. Commit/package results are recorded in the ignored WIP checkpoint.
The authority boundary is in
[the bounded callback contract](file-transfer-bootstrap-source-callback-ownership-contract.md).
Earlier green/unfinished/awaiting-approval statements below are historical checkpoints.
Capability is enabled in the local candidate; deployment and live validation remain separate gates.
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

## 7. Joined-path local-stop initiation addendum

Reviewed 2026-09-11 against HEAD `adfa9c049c10e233d6b449b980cad09b33166e94` and
`joined-bootstrap-exchange-source-released.trx`. Bounded implementation subsequently approved;
the initiation slice is implemented but uncommitted. The joined positive remains blocked
at the next phase-lifetime edge described below; evidence-extension work is still gated.
In the pre-implementation baseline, the fixture reaches committed/disposition-applied Tuna on both peers, then records
pending fallback after runtime toggle-off without issuing an exchange. Source traffic
was released; required-Tuna sends fail and defer for handoff. This identifies the
then-first missing initiation edge, not a live-network failure or a proven historical regression.

### Decision and representation

Add an explicit **local runtime-disable observation**, restricted to an accepted
`StopAccelerationAsync("runtime_disabled", ...)` on a selected version1 transfer.
This requests Coordinator evaluation; it is not received NKN proof, route authority,
transport readiness or delivery. Do not initiate from the shared fallback-reason
recorder, arbitrary reason strings, remote Down, cap failures, stale callbacks or
adapter counters alone. Other stop reasons retain existing behavior in this slice.
The opposite peer joins through the existing authenticated correlated Solicit or
Announce path. Single-endpoint stop must therefore be tested in both directions.

Extend the existing non-wire evaluation DTO with a typed origin and a local-stop
source payload, mutually exclusive with its physical peer-trigger payload. Keep
legacy/peer evaluation interpretation unchanged. The local payload carries the
exact session/transfer/direction, actual data-session reference and provider
generation, negotiated local issuer identity, copied Coordinator-issued publication authority, and existing source
route/commit/terminal/leg/transport/bridge/checkpoint-request scope. Capture it at
the local stop boundary, before reset can discard source observations; discard it
if the stop/reset is rejected. Do not reconstruct its transfer or source from a
new current context when a queued callback finally runs. Adapter-held copies are
evidence to compare with Coordinator state, never authority to trust on their own.
Retain the local issuer only from the existing Coordinator-pinned agreement notification;
it distinguishes a replaced Coordinator context even when scalar generations repeat.

Reuse the existing bridge event after accepted reset, outside transport locks;
do not add a message, timer, queue, polling loop or authority family. The service
checks the exact attached bridge, current context, data-session reference/provider
and service generations, enabled immutable agreement and copied source scope.
It then supplies immutable value evidence to Coordinator; task handles and object
references stay outside the pure Coordinator. The issued command freezes the
current authoritative owner/publication/commit, source identity and durable checkpoint.
Preserve the existing S/R scope encoding and wire bytes.

### Admission, duplicate and wake rules

Validate typed origin and captured source **before** current-exchange idempotence
or any phase lookup. `StartFallbackBootstrapExchangeCore` currently returns early
for a current exchange; that must not turn an invalid stop callback into an accepted
wake for its current phase. Initial committed/disposition-applied activation and
exact completed-fallback/reactivation successor lineage remain mandatory.

An exact duplicate may coalesce with a current exchange but cannot issue another
cycle, dispatch/re-dispatch a phase, replace an observation, or act as an availability
wake. Only a newly Coordinator-issued phase may start transport execution; Held
resumption uses its existing owned wake. A stop captured under a prior publication,
transfer, agreement, data session or checkpoint scope is rejected even if its
adapter observation counter is larger. Local bootstrap generations remain Coordinator-owned.
Do not consume the legacy session/direction coalescing key for this new typed case;
coalescing must include exact local source/transfer/data-session identity and preserve
the existing peer-first wake path. No unbounded per-stop history is introduced.

Both roles reject user-paused/terminal/replaced/disposed scopes before issuance.
For inbound pending writes, retain only the exact accepted local-stop evaluation
in the existing pending-write wake, with an origin-specific currentness check.
The present wake accepts peer-first physical triggers only; a local stop must not
be disguised as one or dropped after coalescing. Recheck the same structural
owner/publication/commit/data-session/leg/checkpoint-request scope after drain.
Normal pending-write completion supplies the final durable frontier at issuance;
it cannot rewrite a previously issued command. Pause or scope supersession retires
the pending wake. No automatic resume, checkpoint reissue or new retry is authorized.

An accepted initiation grants only the existing bounded first phase (Solicit or
Announce/Response according to actual retained evidence). It does not mark Tuna
inactive as Coordinator state, publish fallback, release credit, change progress,
retire workers or satisfy peer proof. Exact phase Delivered AND Settled, two-sided
S/R echoes, subsequent pause/drain/commit/publication/disposition remain unchanged.

### Evidence and bounded implementation gate

Persist the typed initiation kind and exact source-observation fingerprint in the
existing route_transition evidence, linked to its Coordinator decision and base
command. Rejections remain stateApplied=false; initiation alone has no physical
delivery, settlement or route-publication effect. Add an explicit
`fallback-bootstrap-initiation-v1` catalog extension for this new interpretation;
compute its actual hash during implementation and preserve every existing profile/hash.
Full/compact/shared-GUI readers must distinguish local intent from received proof
and reject relabeled origin, altered source and missing linkage without waiving
independent physical/route findings. No full retained report is required.

Bounded implementation order after approval:

1. Add the typed observation/source validation and narrow local-stop emission.
   Focused negatives: wrong bridge/session/transfer/direction, missing source,
   stale publication/commit/leg/checkpoint, replaced data session, paused/terminal
   context, unsupported agreement, non-local or non-runtime-disable origin.
2. Extend the existing pending-write wake and duplicate checks only for this origin.
   Verify one current issuance, zero duplicate dispatch, pending-write drain,
   pause/supersession while held, delayed prior-cycle callback, and unchanged legacy mode.
3. Rerun the joined positive with only the sender stopped, then only the receiver,
   using real Service negotiation/NKN execution and real receipts. Retain the
   simultaneous-stop control. Do not increase the fixture deadline or manufacture
   a trigger/receipt. Stop at the next missing proof edge; later phase fixes need review.
4. Once the targeted runtime edge passes, verify the changed evidence projection/
   catalog with focused full/compact/shared-GUI tests and directly affected lanes.
   Subsequent broad correlation scenarios remain gated on the joined positive.

Production capability stays false. No GUI setup, portable package, live run, full
matrix, queue/chunk/retry/performance tuning, commit or push is authorized by this
review. The approved implementation does not authorize deployment or later phase fixes.

### Implementation checkpoint and next review (2026-09-11)

Typed local-stop source capture, Service admission, Coordinator validation before
idempotence, and origin-specific pending-write/duplicate handling are implemented.
Final focused initiation tests pass20/20; directly affected peer/legacy regressions
pass14/14. These are deterministic component tests, not live acceptance.

The sender-only joined positive remains0/1 FAIL in both retained runs. It now issues
Solicit from the real local stop and Response from actual received peer evidence.
The latest diagnostic run records Delivered AND Settled for both phases, including
all four control/bulk epoch/proof copies per phase. Immediately after accepted
phase receipts, the outbound Coordinator accepts InvalidateFallbackBootstrapExchange
with reason bootstrap_transport_paused (log sequence565). The ordinary required-Tuna
payload failure enters the existing transport-pause path and invalidates the active
exchange. Neither peer publishes fallback; both remain at Tuna4/publication2.

The earlier run also rejects receiver callbacks as bootstrap_phase_callback_scope_stale.
In the latest timeout snapshot, the receiver's issued/current durable checkpoints
differ only in StateEpoch (2 -> 62), with unchanged durable frontier and source
ownership. This is a timeout observation, not proof of the exact earlier callback
mismatch or an independent root cause. Do not weaken currentness from this evidence.

Next bounded step is review of Coordinator-owned phase lifetime and source quiescence
relative to the ordinary transport pause/checkpoint updates. Define the ordering and
rejection rules before changing code. Do not exempt pauses from invalidation, rewrite
issued checkpoints, add automatic reissuance, or relax callback guards as a shortcut.
Receiver-only and dual-stop positives are defined but not run after this first new
edge. The fallback-bootstrap-initiation-v1 catalog/hash and reader parity tests remain
unimplemented until the targeted runtime gate passes. Production capability stays false.

Retained evidence: bootstrap-initiation-focused-final.trx,
bootstrap-initiation-peer-regression.trx, bootstrap-initiation-joined-sender.trx,
and bootstrap-initiation-joined-sender-scope.trx under
artifacts/validation/consolidation-bounded-20260907/bootstrap-correlation-implementation/.

## 8. Pre-bootstrap source hold: reviewed fix contract

Reviewed 2026-09-11 against unchanged HEAD adfa9c049c10e233d6b449b980cad09b33166e94
plus the uncommitted section7 implementation. This extends the local bootstrap
lifecycle; subsequent implementation approval and its stop condition are recorded below. The review's latest relevant
runtime evidence is bootstrap-initiation-joined-sender-scope.trx and its unchanged
log. No runtime code changed or test was rerun during this review.

### RCA and limits of the evidence

The proven break is not failure to deliver Solicit/Response. Log sequences563/564
accept phase receipts; sequence565 accepts outbound exchange invalidation with
reason bootstrap_transport_paused. The ordinary required-Tuna failure invokes
TryDeferOutboundV4SendForFileTunaFallbackHandoffLocked (PullTransferSessionV4.cs:10485),
then TryPauseOutboundTransportLocked (PullTransferSession.cs:890). That accepted
pause invalidates the active exchange. A received Response cannot authorize Confirm
once this exchange is invalidated; restarting the unfinished exchange is forbidden.

There is also an exposed checkpoint-lifetime risk. CreateInboundFileTransferRouteTransitionCheckpoint
(RouteTransitionFallback.cs:4853) includes V4StateEpoch. CreateInboundV4StateLocked
(PullTransferSessionV4.cs:25470) increments it when constructing ordinary feedback,
even before physical delivery. SendInboundV4StateCoreAsync suppresses construction
for the Tuna-activation pause, not a generic runtime_disabled pause. The latest
timeout snapshot has StateEpoch2 ->62 with identical remaining checkpoint fields.
This explains how ordinary work can invalidate the frozen tuple, but does not
prove which field failed at the earlier run's callback instant. A controlled
callback-time characterization is required before treating that mismatch as fixed.

The design gap is local ordering: section7 issues a frozen bootstrap command while
source payload/feedback work can still run; the existing route-transition pause is
bound only after completed bootstrap proof. That later pause command requires a
prepared transition (RouteTransitionFallback.cs:968-1008). Moving it earlier would
manufacture route-preparation authority. Merely setting PullTransportPaused is also
insufficient: inbound chunk admission checks UserPaused/PeerPaused, and ordinary
feedback has its own construction/submission path. These are separate boundaries.

### Bounded representation and ordering

Introduce one Coordinator-owned bootstrap source-hold preparation in the existing
route_transition substate, plus its exact local command and apply/drain receipt.
Do not create a new route, runtime owner, recovery framework or wire message. This
is the minimum proposed representation change beyond section7: a hold can exist
before the base bootstrap command and its immutable checkpoint exist.

The preparation pins Coordinator instance, enabled agreement/local issuer, exact
session/transfer/direction, owner/publication/commit/terminal/leg/transport/bridge/
checkpoint-request scope and data-session identity/generation. The Service shadow
also pins the actual data-session reference/provider generation. Preserve the
validated initiation origin/source or exact authenticated peer-wake frame; a reason
string, adapter counter, pause flag or empty queue is never sufficient admission.
Only one preparation may own a current source; exact duplicates coalesce without
new commands, dispatch or resumption. Conflicting/stale candidates fail closed.

The required order is:

1. Coordinator accepts the exact initiation and issues source-hold preparation.
   No S/R phase is authorized yet. Recheck current source when applying its command.
2. Service atomically closes admission of new ordinary source payload, receive-write
   and feedback/state work under its existing gate, then captures already-admitted
   physical tasks/writes. Bind the operational transport pause before phase issuance.
   Do not change UserPaused or use transport observations as hold authority.
3. Already-admitted work settles under its existing ownership. Reuse the existing
   physical-send lease/task and pending-write tracking; report the exact captured
   work and settlement against the hold command, not just count-zero/readiness.
   No lock is held while waiting. A failed task is settlement, not delivery proof.
4. Coordinator accepts the current hold receipt and the resulting durable checkpoint,
   then issues the base bootstrap command and first phase exactly once. Retained
   peer-wake evidence keeps its exact identity across the existing owned write-drain
   wake. No already-issued command/checkpoint or S/R hash is rewritten.
5. The hold remains applied through the exchange. After completed two-sided proof,
   existing route preparation/pause binding takes over without reopening source
   admission. Existing physical drain, commit, retained-frame application, publication
   and disposition remain required; the pre-bootstrap receipt substitutes for none.

Keep ordinary source state construction and pre-submission gates consistent: a
feedback send suppressed after construction must not already increment StateEpoch,
grant credit or create repair work. Close inbound write admission before draining
accepted writes, so late payload cannot move the frozen frontier; unaccepted bytes
are not acknowledged or counted committed. Outbound ordinary state application must
not mutate the frozen checkpoint while held. These are selected-mode correctness
gates only, not changes to chunking, pacing, repair policy or legacy behavior.

Keep the existing data-session receive loops and authenticated correlated control
dispatch active while source work is held. Otherwise Response/Confirm cannot arrive.
Do not replace/rebind the data session, suppress all frames, or bypass runtime-install
currentness. Held physical-phase execution still uses its existing owned availability
wake; it cannot replace the preparation or refresh the frozen checkpoint.

### Ownership, invalidation and cleanup

The expected stop-induced operational pause must be bound before the exchange is
issued. A later failure of exactly captured, still-owned source work may settle only
that work; it cannot create a fresh pause/recovery decision from its reason string.
Do not add a blanket runtime_disabled exemption to invalidation. A genuinely new
user/transport pause, terminal/disposal event, agreement/data-session replacement or
source/checkpoint supersession invalidates the applicable hold/exchange. The current
exact callback guards remain strict, including StateEpoch after checkpoint freeze.

Rejected or stale hold/receipt/phase callbacks cannot advance progress, publish,
resume, release another owner's hold, defer liveness or start retries/recovery.
Release/handoff requires a current Coordinator decision. Invalidating preparation
does not automatically resume stopped Tuna or restart an unfinished exchange.
Retain existing terminal/recovery deadlines; no indefinite-wait lease or deadline
extension is introduced. Reuse completion signals; no timer, polling loop or queue.

### Verification and approval boundary

After separate bounded implementation approval:

1. Add controlled regressions for a source send failing before/during/after hold
   application, blocked source task settlement, pending writes, feedback construction
   between issuance and callbacks, and a new pause after phase issuance. Capture
   issued/current checkpoint at the callback boundary; distinguish the two known
   symptoms instead of assuming one fixes the other.
2. Implement only this source-hold ordering. Pure Coordinator and Service tests must
   prove no phase before the exact receipt, no new source work after hold application,
   final frontier frozen once after drain, control ingress still usable, duplicate
   no-op behavior and stale/paused/replaced/terminal rejection without new effects.
   If existing tracking cannot prove admitted-work settlement, stop for review rather
   than expanding into a generic drain subsystem.
3. Rerun the joined sender-only positive with real Service/NKN execution and receipts;
   then receiver-only and dual-stop controls only if it passes. Require both fallback
   publications/dispositions, not just phase delivery. Preserve the current deadlines,
   source-release behavior and failed artifacts; stop at the first new proof edge.
4. Once that runtime gate passes, finish the still-unpublished
   fallback-bootstrap-initiation-v1 evidence extension, including hold-command,
   receipt, frozen-checkpoint and base-command linkage. Preserve existing profile
   hashes and S/R wire encoding. Verify focused full/compact/shared-GUI interpretation
   and directly affected lanes; do not invoke a full report or aggregate matrix.

This contract changes the pre-issuance ordering in section7, not section4's rule that
new pauses and superseded scope retire active authority. Production capability remains
false. The original review authorized no implementation, enablement, package,
GUI/preflight/live run, commit or push. Subsequent bounded approval does not waive
the admitted-work tracking stop condition.

### Approved implementation: settlement-tracking gate blocked (2026-09-11)

The user approved the bounded slice. Work started with its controlled regressions
and stopped before runtime edits because existing tracking does not prove ordinary
inbound feedback settlement. This is the explicit step2 stop condition, not a claim
that the source-hold implementation is complete or that the approval was absent.

Three focused tests now pin the boundary:

- BootstrapSourceHold_WaitsForAlreadyAdmittedReceiverFeedbackBeforeIssuingPhase is
  FAIL: a real Service feedback SendAsync is blocked, both inbound pending-write
  collections are empty, but the Coordinator issues one bootstrap phase. The fixture
  uses committed initial Tuna history and the existing modeled phase bridge; it is
  not joined/live acceptance. No phase receipt is supplied before the failing check.
- BootstrapSourceHold_Characterization_FeedbackConstructionChangesCheckpointBeforePhaseCallback
  is PASS as a pre-hold characterization only. One ordinary feedback construction
  changes StateEpoch by exactly1, with every other checkpoint field equal, and both
  supplied modeled phase callbacks are rejected as bootstrap_phase_callback_scope_stale.
  This attributes the controlled mismatch, not the precise callback in the older joined log.
- BootstrapSourceHold_PhysicalFeedbackCompletionWaitsForSecondaryCopy is FAIL through
  the real NknSignalingTransport physical-submission API over FakeNkn. Delivery and
  PhysicalCompletionTask both complete while the redundant bulk feedback copy is
  still blocked. Releasing it in cleanup delivers the actual serialized copy. No
  phase receipts, Coordinator route authority or completion flags are manufactured.

Source confirmation: SendInboundV4StateCoreAsync retains its direct feedback task
only in a local variable (PullTransferSessionV4.cs:24595,24642); outbound payload
leases do not track it. The ordinary Tuna path does not use the fallback delivery
contract. Simply switching to SubmitPhysicalSend is insufficient: SendAsyncCore
passes its tracker to the redundant-feedback path only for correlated bootstrap
frames (FileTransferTransportChannel.cs:8201), and RegisterBootstrapCopy ignores
ordinary frames without a bootstrap identity (:8689). The redundant path preserves
first-success delivery, leaving its other copy to a background observer. Consequently
neither queue/write emptiness nor the present feedback completion result proves drain.

Required next bounded extension, pending review/approval: retain exact ordinary
inbound Tuna feedback submissions in the Service from admission through actual
physical settlement; use the existing physical-submission API and tracker to include
both feedback copies without labeling them bootstrap proof. Keep first-success
DeliveryTask semantics, lane selection, concurrency and retry policy unchanged.
Registration must precede submission/capture under the Service gate, settlement
must be source/data-session bound, and late cleanup may retire only its own task.
Do not reuse fallback-control-plane ownership for Tuna or claim a new runtime owner.
This is narrow task-lifetime accounting, not a generic drain framework or a substitute
for the Coordinator source hold. Prove blocked secondary, cancellation request versus
actual settlement, failure and stale cleanup before resuming section8 implementation.

Retained results in the existing implementation artifact directory:
bootstrap-source-hold-feedback-boundary.trx (1 characterization PASS,1 regression FAIL),
bootstrap-source-hold-physical-feedback-red.trx (0/1), and
bootstrap-source-hold-fixture-compatibility.trx (4/4 existing initial peer-first tests).
bootstrap-source-hold-feedback-red.trx retains an earlier2/2 fixture failure: it
incorrectly expected a control-plane request on the direct Tuna feedback path; that
test-only assumption was corrected before the boundary run. No failed result is
reclassified or quarantined. No joined rerun, source-hold DTO/runtime implementation,
catalog/hash change, aggregate lane, report, package, live run or commit occurred.

## 9. Ordinary inbound feedback settlement: reviewed extension

Reviewed 2026-09-11 against the unchanged section8 regression artifacts and runtime
source at HEAD adfa9c049c10e233d6b449b980cad09b33166e94 plus section7's uncommitted
changes. This section defines the previously blocked tracking addition; it does not
approve its implementation or broaden section8 into a generic drain subsystem.

### Review findings and scope

Two changes are necessary, and neither alone completes the source hold:

- NKN's explicit physical-submission API must track all started copies of ordinary
  redundant feedback. Passing the tracker onward is not enough: RegisterBootstrapCopy
  returns immediately without a bootstrap frame identity. Use existing physical-task
  registration for ordinary feedback; preserve bootstrap-copy evidence separately.
- The Service must retain the exact admitted feedback operation, including the gap
  between frame construction and the later locked physical submission. Registering
  only after SendAsync/SubmitPhysicalSend returns misses an operation a hold can race
  with; a null physical-task binding must mean pending, never already drained.

Service integration is restricted to selected-correlation, current inbound Tuna4
ordinary receiver feedback through SendInboundV4StateCoreAsync. Non-selected/legacy,
regular-V4 and post-fallback control-plane ownership paths retain their behavior.
The NKN API fix applies whenever its explicit physical-submission API is used for
the affected redundant feedback; ordinary SendAsync delivery semantics stay unchanged.
No new wire field, route/runtime authority, scheduler, retry, lane or concurrency policy.

### Transport completion contract

Keep DeliveryTask as first successful copy, or the existing all-failed/canceled
delivery result. PhysicalCompletionTask completes only after submission is sealed
and every started physical copy has actually finished, regardless of success/failure.
Register both control and bulk tasks exactly once, including partial-start failure
paths, using the existing tracker. Do not delay delivery to wait for the secondary.
Ordinary feedback must keep BootstrapCopies absent; its settlement is not Solicit,
Response, Confirm or epoch proof and cannot become a Coordinator phase receipt.

Retain the existing linked cancellation lifetime until all registered tasks settle.
A cancellation request, canceled observer, context disposal or DeliveryTask completion
does not itself settle a still-running copy. Preserve the tracker's existing distinction
between observable delivery failure and successful completion of a settlement wait.
Do not add retries, turn the secondary into another primary, or change copy ordering.

### Service registration and retirement contract

Use a bounded active-operation record on the owning inbound context, not a new
Coordinator authority or fallback-control-plane token. It contains a context-local
operation identity, Coordinator instance/agreement identity, exact source owner/
publication/commit/terminal/leg/transport/bridge/checkpoint-request scope, actual
data-session reference plus provider/service generation, and immutable frame identity.
The local operation counter identifies evidence only; it grants no send or hold rights.

Register after the current state-send admission checks and final frame construction,
before releasing the initial Service gate. Keep the existing pre-submission currentness
check and physical invocation atomic under that gate, then bind the returned submission
to the same record. Section8's admission gate must run before state construction can
increment StateEpoch or alter credit. Do not call transport or wait under a new lock.

An admitted record stays pending while not yet physically bound. Bind the existing
DeliveryTask and PhysicalCompletionTask once. Await first-success delivery where the
current sender does; observe actual physical completion independently outside the gate.
Every early rejection/exception path must retire only its own never-submitted operation
or wait for its started work. A fault or cancellation alone is not proof of no submission.
Do not infer physical completion from SendAsync when the explicit API is unavailable:
the selected path must fail closed before dispatch. Test sessions may explicitly model
a single physical task through that API; production transport semantics are not inferred.

The capture-visible operation is settled only when both physical settlement and the
Service send routine's post-delivery/early-exit finalization have finished. Physical
completion can race ahead of the local continuation; retiring then would allow a
checkpoint freeze before that continuation's state changes. Preserve normal first-
success return behavior: do not make the send routine await a blocked secondary.
Join its finalization signal with physical completion for hold accounting only.
Cancellation of either observer cannot manufacture either part of that settlement.

Retirement compares the captured context/record identity and generations, never looks
up a replacement by transfer ID. A late callback may release its own execution resources
only; no progress/checkpoint/credit/repair mutation, new pause, bootstrap issuance,
hold release or resume. Keep records only for unsettled admitted work; no completed-task
history, permanent tombstone list or new queue. A held capture retains its exact operation
identities/completion evidence so normal removal cannot masquerade as missing work.

The registry is evidence, not drain authority. Only section8's current Coordinator hold
command can capture its source-bound set and accept the corresponding settlement receipt.
Do not synthesize an empty receipt from collection counts or let task completion bypass
the hold's source/currentness, checkpoint-freeze or two-sided phase-proof requirements.

### Bounded implementation sequence after approval

1. Fix NKN ordinary-feedback physical tracking and pass the existing blocked-secondary
   regression. Add focused control-first/bulk-first, secondary failure, cancellation
   requested while a copy stays blocked, all-failed and no-start cases. Verify ordinary
   BootstrapCopies remains absent and correlated all-copy receipts are unchanged.
2. Add Service admission/binding/retirement tracking with controlled construction-to-
   submission, synchronous completion, blocked secondary and stale context/data-session
   replacement tests, including physical completion before local finalization. Prove
   that an unbound or locally unfinished record is pending and late cleanup cannot
   remove a successor or authorize effects. Run only the directly affected lanes.
3. Once those prerequisites pass, resume the already-approved section8 hold implementation
   without substituting registry readiness for Coordinator authority. The existing
   Service source-hold regression must then pass. Replace the pre-hold characterization's
   active-hold expectation with no checkpoint movement/no stale current callback; retain
   the historical artifact rather than treating its old PASS as hold acceptance.
4. Follow section8's sender-only joined gate, then receiver-only/dual-stop and the pending
   initiation evidence extension only if prior gates pass. Stop at the first new proof
   edge; no aggregate matrix, full report, package, GUI/preflight/live run or commit.

Approval of this extension would cover steps1-2 and resumption of the existing bounded
section8 plan, not production enablement. Production capability stays false. No runtime
code, tests, retained test artifacts, catalog/hash or physical behavior changed in this review.

### Approved implementation outcome (2026-09-11)

The subsequent user approval covered section9 implementation and resumption of
section8. The ordinary-feedback tracking prerequisite is now implemented, not just
reviewed. NKN passes the explicit physical tracker through redundant ordinary
feedback and registers non-bootstrap copies as ordinary physical tasks. Delivery
remains first-success and ordinary BootstrapCopies remains absent.

The Service registers a selected inbound Tuna4 operation before releasing its
construction gate, binds the actual submission under that gate, and retires only
after local finalization plus physical completion. The operation pins its owning
context, Coordinator/agreement/source, data-session reference/provider/service
generations, pause instance and immutable frame fingerprint. Currentness is
rechecked before submission and post-delivery effects. A failure's terminal
mutation also rechecks the exact operation under the terminal mutation gate;
non-selected terminal callers retain their original entrypoint and behavior.

Focused retained results:

- bootstrap-feedback-tracking-infra.trx:6/6, control-first/bulk-first, secondary
  failure, cancellation while a copy remains blocked, all-failed and no-start.
- bootstrap-feedback-tracking-service-final.trx:9/9, registration/binding gaps,
  local finalization, blocked physical work, unsupported API and stale replacement.
- bootstrap-feedback-tracking-compatibility.trx:12/12, correlated physical/executor
  semantics and existing initial peer-first fixtures.
- bootstrap-feedback-tracking-restored-final.trx:11/11, the same9 Service cases
  after prototype removal plus2 existing pure initiation controls.
- bootstrap-feedback-tracking-terminal-final.trx:2/2, exact current failure may
  terminalize; a replacement-bound failure cannot. Pending physical work remains
  pending in both cases. These results cover31 distinct passing cases, not40.

The first Service development run, bootstrap-feedback-tracking-service.trx,
retains1/9 PASS:8 fixture failures incorrectly read an int counter as long. Only
the fixture cast was corrected. No failed artifact is hidden or reclassified.

Section8 was resumed with a temporary Coordinator/Service hold prototype. Its
initial four controlled cases passed in bootstrap-source-hold-first.trx, but a
new admitted-write source-binding failure hit section8's explicit tracking stop
condition. The incomplete hold prototype and its prototype-only tests were
removed; these four historical passes are NOT acceptance of current source hold.
The original pre-hold feedback characterization and desired hold regression remain
in their prior forms. No hold DTO, hold gate, hold receipt, catalog/hash change or
production enablement remains from that prototype. See section10 for the new edge.

## 10. Admitted inbound write source binding: RCA and reviewed extension

This is a new review boundary, not implementation approval. Latest relevant
evidence is bootstrap-source-hold-stale-write-current.trx and its unchanged log,
built after removing the incomplete hold prototype, at HEAD
adfa9c049c10e233d6b449b980cad09b33166e94 plus sections7/9's retained dirty work.
It reproduces the same failure first seen in
bootstrap-source-hold-stale-write-prototype.trx. Both remain0/1 FAIL.

The controlled test uses a real Service inbound V4 chunk handler and a blocked
sparse-write Stream in a committed initial Tuna fixture. After physical writing
starts, it requests the validated local bootstrap stop, verifies that pending
writes still prevent phase issuance, then replaces the data-session reference and
increments its Service generation. Releasing the old write changes frontier
100->101, committed bytes0->21504 and StateEpoch1->2. No phase receipts are
manufactured, and this is deterministic fixture evidence, not live acceptance.

The first missing edge is between the admitted write and its post-write commit.
HandleInboundV4ChunkBatchAsync records only chunk indexes in
ReceiverSparseChunksPendingWrite (PullTransferSessionV4.cs:23386), waits for the
write gate and actual Stream.WriteAsync (:23489-23495), then checks only current
context identity, terminal state and the written-bit collection (:23539). It does
not revalidate the admitted source/data-session/provider/service generation or
stream identity before removing indexes and committing the frontier (:23549-23563).
The bootstrap pending-write wake's later source check prevents bootstrap issuance,
but cannot undo already-applied progress or subsequent ordinary feedback.

This proves stale write completion mutates the source. It does not yet prove an
old write removes a newly admitted same-index successor; the index-only ownership
makes that a specific required controlled test, not a claimed observed outcome.
Queue emptiness and a canceled observer are not an exact write-settlement receipt.

Proposed bounded next step, requiring review/approval:

1. Bind selected-correlation inbound Tuna4 write admission to its exact source,
   actual data-session reference/provider/service generations, actual stream and
   per-operation identity before releasing the existing Service gate. Preserve
   chunk indexes for duplicate detection; indexes alone must not own retirement.
2. Track actual accepted write work and its local commit/finalization lifetime.
   Recheck the captured source at the commit/effect gate. A stale completion may
   release only its own resources; it cannot advance frontier/bytes/StateEpoch,
   create feedback/repair, remove a successor or issue/bootstrap/resume anything.
   A started write's physical side effect cannot be undone; do not mislabel it as
   canceled-before-start or completed durable proof for a replacement source.
3. Prove blocked write, source/data-session/stream replacement, same-index successor,
   cancellation request versus actual settlement, failure and terminal cleanup.
   Keep legacy/regular-V4/post-fallback behavior unchanged. No generic drain queue,
   timer, retry/deadline, chunking, pacing or transport policy change.
4. Once this prerequisite passes, resume section8 with both admitted feedback and
   write identities available to the Coordinator-owned capture/receipt. Then follow
   the original sender-only joined gate and its stop order; do not jump to receiver/
   dual-stop/evidence publication or the GUI matrix.

Production capability remains false. Section8, joined positive acceptance and the
initiation evidence extension remain unfinished. No staging/commit/push, package,
GUI/preflight/live NKN, full report, aggregate lane or subagent was used this turn.

### Review findings (2026-09-11)

The user approved this contract review, not implementation. The retained current
failure and affected source were inspected without rebuilding or rerunning tests.
HEAD and sections7/9's dirty runtime are unchanged. The failure remains0/1 FAIL.
The proposed direction is sound, but a commit-time generation check alone is not
sufficient. The following requirements close its admission and lifetime gaps.

- Ingress identity must survive dispatch. The V4 receive loop awaits its captured
  dataSession (:22430-22450) but calls the chunk handler with only context, frame
  and transport kind (:22720). Reading context.DataSession inside that handler
  could bind an old receive result to a replacement. Carry the actual receive
  session reference and its captured provider/Service generation into selected
  write admission; session/transfer strings or current context fields are not
  provenance. This is a source-inspection risk, not another reproduced failure.
- Admission precedes waiting for ReceiverSparseWriteGate. Recheck after acquiring
  that gate and before each new Seek/WriteAsync pair; otherwise a queued stale
  batch can still write, or a multi-chunk batch can start more writes after scope
  replacement. Pin write layout rather than rereading mutable ChunkSizeBytes for
  offsets after the await (:23493). Already-started I/O cannot be rolled back.
- Chunk-index membership is not retirement ownership. Manifest reset clears the
  shared pending set and counters and replaces the written-bit map
  (:23174-23184). An old completion must not remove or debit a later same-index
  operation. Same-index successor interference still needs a controlled test.
- Physical write completion is earlier than local finalization. The handler
  releases its write gate before the commit lock, then may claim a bootstrap wake
  (:23659), send feedback/repair and finalize the transfer (:23790-23840).
  A settled write receipt must not outrun these continuations. The stale error
  path can also reach FailInboundV4Async (:23501); guard the actual error/terminal
  mutation, not merely an earlier check before another asynchronous handoff.

The retained log independently records a Stale/source_not_current bootstrap wake
at writer sequence39, then committed batch21504 bytes at40 and receiver StateEpoch2
sent using data_session_generation=2 at43. Thus rejecting the wake is not enough;
the old write has already been permitted to affect current progress and feedback.
These observations do not establish a live transport failure or prove physical
stream corruption, same-index interference, queued-write or multi-chunk races.

### Bounded operation representation

Restrict the addition to selected-correlation inbound file_tuna_v4 ordinary V4
batch admission and its write/continuation path. Keep V6/legacy/regular-V4 behavior,
wire DTOs, NKN transport, receiver scheduling and general disposal policy unchanged.
A born-selected operation retains its checks even if the current agreement/route
later changes; it cannot downgrade itself into an untracked legacy completion.

Retain one active record per admitted batch on the captured inbound context. It
pins context-local operation identity, Coordinator instance/agreement, exact
owner/publication/commit/terminal/leg/transport/bridge/checkpoint-request scope,
actual ingress data-session reference/provider/Service generations, actual stream,
written-map/layout identity and the accepted chunk indexes/lengths. Capture source
authority at admission only after proving the ingress session is current. Do not
infer a route owner from a received frame or create a new runtime/route token.

Freeze the layout required for validation and offsets. The moving durable frontier,
byte counters and ordinary StateEpoch are not pre-drain operation identity: other
legitimately admitted work can change them before section8 freezes its checkpoint.
No full-payload hash, duplicate payload copy, permanent history or generic task
queue is required. Per-chunk membership needs exact operation ownership in addition
to existing duplicate detection; a shared index alone cannot authorize cleanup.

Register atomically with accepted-chunk admission before releasing the Service
gate, including work that has not yet acquired the existing sparse-write gate.
Validate selected ingress before malformed-frame terminal effects or duplicate/
empty-batch feedback can affect a replacement. Tests invoking private handlers
must supply explicit modeled ingress; missing provenance is not replaced by the
current session as a convenience. Keep the production receive loop/control ingress
running; this is not approval for a new receive supervisor or transport redesign.

### I/O, commit, continuation and retirement rules

Keep ReceiverSparseWriteGate as the existing physical serializer. Do not await it
or a Stream operation while holding the Service gate. After acquiring it, make
the source/currentness check and start of each physical write atomic with respect
to Service source replacement using the existing gate, then await outside that
gate. Preserve established lock ordering; do not add another lock/semaphore.
Observe each actual WriteAsync completion once, including synchronous completion,
synchronous start failure and non-repeatable ValueTask implementations.

An unstarted or partially executed batch stays pending until no physical work can
still run and its local routine is finalized. Cancellation requests, disposal,
empty pending sets, write-gate release or a canceled observer cannot manufacture
physical settlement. If a started write ignores cancellation, retain its actual
completion evidence. Do not submit the remaining chunks after genuine scope/pause
invalidation. Preserve existing batch commit semantics; partial physical success
does not introduce partial durable commit, compensating writes or automatic replay.

At the commit gate, verify the exact admitted operation, current source, session,
stream/layout and chunk ownership before changing bits, pending membership,
frontier, bytes, credit, liveness or progress. Carry the same binding into any
write-originated feedback, repair, wake, failure, terminal-ready or finalization
handoff, and recheck at that effect's actual mutation/admission gate. Checking
only before calling another asynchronous method leaves a replacement race.
Diagnostic reporting of a completed old action is not new progress authority.

Retirement compares the captured context and exact operation/chunk ownership.
It must not look up a replacement by transfer ID, remove its pending chunk,
subtract its buffered bytes, dispose its stream/session, signal its wake or mark
its receipt settled. Source/manifest reset may invalidate old evidence but must
not erase evidence of still-running I/O. Once settled, retain no completed record
or tombstone. The record does not take ownership of context-wide resource disposal.

Treat a still-running stale write on a reused stream as unresolved physical work,
not an empty current-source drain. Do not adopt it as successor authority or claim
that source invalidation stopped its bytes. Preserve its exact settlement evidence;
section8 must fail closed where a new source's frozen checkpoint would otherwise
ignore such work. Do not add automatic hold/phase renewal, reconciliation or retries.

### Source-hold integration and verification order

The registry is local execution evidence only. Once section8 is resumed, its exact
Coordinator hold command captures the admitted records and the corresponding
completion signals, not just pending chunk counts. Its own operational pause may
allow exactly captured, still-current admitted writes to finish under that hold;
the pause flag/reason alone grants no exception. A genuinely new pause, terminal,
agreement/source/data-session/stream/layout supersession cannot inherit that grant.

Keep the operation unsettled through local commit and all state-changing tails,
including any feedback admission gap. Reuse section9 tracking for feedback that
was actually admitted. Suppress new held-source feedback before construction.
The existing owned pending-write wake must not issue a phase before this combined
boundary, or be awaited by the same operation whose settlement it requires.
Release/coalesce the exact owned wake after finalization, without polling, a new
queue or a fresh peer identity. Only the Coordinator accepts the hold receipt and
freezes the final durable checkpoint; no phase or route proof is synthesized here.

After separate bounded implementation approval:

1. Make the retained stale-write regression pass using the actual V4 handler and
   source-bound continuation gates. Add controlled ingress-result replacement,
   queued-before-I/O and between-chunk replacement cases; stale work must not
   start further I/O or create authoritative effects.
2. Cover stream/layout reset, same-index successor and same-transfer-ID replacement
   contexts. Check both the old record's settlement and the successor's pending
   ownership, counters, stream contents and wake identity independently. Keep
   still-running old I/O visible even when its source is invalidated.
3. Cover synchronous completion, physical completion before local finalization,
   current/stale write failure, cancellation while a write remains blocked, and
   terminal/disposal cleanup. Current failure retains existing failure behavior;
   stale failure cannot terminalize a successor. Preserve semaphore release exactly
   once and avoid unfinished-tail/hold-wake dependency cycles.
4. Run directly affected Service/currentness and compatibility cases only. Once
   these prerequisites pass, resume the already-approved section8 hold work,
   prove exact capture/drain/frozen-frontier/control-ingress ordering, then run
   sender-only joined acceptance before receiver-only/dual-stop or the pending
   evidence extension. The initial hold prototype is not reusable acceptance.

Stop at the first new missing proof edge. If this requires changing stream I/O
semantics, general receiver lifecycle, route authority, disposal deadlines or a
generic drain subsystem, return for review instead of expanding this contract.
Approval would cover the narrow write prerequisite and resumption of section8,
not enablement, package/GUI/preflight/live NKN, full reports, aggregate matrix,
staging/commit/push or new campaign/report allowance. Production remains false.

### Approved implementation outcome (2026-09-11)

The subsequent user proceed authorized this write prerequisite and resumption of
section8. Both are now implemented in the dirty workspace at HEAD
adfa9c049c10e233d6b449b980cad09b33166e94. They are not yet end-to-end accepted.

The selected inbound V4 path carries actual receive-session/provider/Service
provenance into batch admission. Its active record pins source authority, stream,
layout and exact per-chunk retirement ownership. It rechecks after the sparse-write
gate and before each physical write, observes the actual ValueTask once, and guards
commit, feedback, repair, failure and finalization effects with the same binding.
Retirement follows both physical settlement and local tails; reset/invalidation
cannot erase still-running I/O or let old cleanup remove a same-index successor.
Already-started physical write/finalization side effects are not rolled back.

Section8 now has Coordinator-issued preparation, exact capture and final receipt,
with a Service execution shadow for actual sessions and admitted physical work.
The command closes source admission and binds its pause before freezing a checkpoint
or issuing a phase. Exactly captured current writes/feedback may finish under that
specific pause, not a generic pause exemption. The drain awaits captured outbound
physical tasks and write/feedback settlement signals outside the Service gate.
The existing owned pending-write wake is reused after receipt acceptance. Ordinary
held-source state/feedback cannot move the frozen checkpoint, and availability
resumes only the exact held phase rather than reopening source payload work.
Exchange currentness retains exact issuing hold-command/receipt linkage plus the
existing strict source checks. A genuinely new pause invalidates the exchange
before invalidating its hold; the hold itself cannot authorize automatic resumption.

Controlled write coverage has24 distinct passes, plus the original stale-write
regression. Compatibility has19 passes and one pre-existing skipped sparse-frontier
case. Controlled hold checks cover physical success/failure settlement, exact
captured-write pause grants and stale scope, feedback admission/finalization gaps,
ordinary-state suppression, new pause, availability, successor handoff and Core
capture/receipt ordering. Across this turn's13 retained TRX artifacts, the latest
result per exact test name is122 PASS,1 FAIL (joined sender),1 SKIP. This is a
deduplicated focused-case inventory, not a single clean aggregate invocation.

Intermediate failures remain retained: an immutable fixture-property assignment,
the expected pre-hold regression, invalidation ordering, held-phase availability,
and a pure helper that incorrectly prepared a conflicting hold before an idempotent
exchange call. Each has a later targeted pass. The service-lane artifact also
contains a testhost stack overflow during successor Core history; removal of a
redundant full hold-currentness call from exchange currentness, retaining exact
issued-command linkage and existing source guards, is followed by a passing
successor test. A general recursion root cause is not claimed from that stack.

The required sender-only joined rerun failed at a new edge described below.
Receiver-only/dual-stop runs and the formal initiation/hold evidence extension
remain blocked. The intermediate snapshot projector includes hold operation state,
but no new catalog/profile has been published or consumer acceptance established.
No production enablement, staging/commit/push, package, GUI/preflight/live NKN,
aggregate matrix, full report or subagent was used.

## 11. Joined sender control-ingress stop: RCA boundary and next plan

Latest evidence is bootstrap-source-hold-joined-sender.trx and its matching log in
artifacts/validation/consolidation-bounded-20260907/bootstrap-correlation-implementation.
This is the rebuilt dirty implementation from the preceding subsection, using real
Service/NKN paths over FakeNkn and fake Tuna, not live-network or GUI evidence.
Result is0/1 FAIL at the unchanged correlated fallback-publication timeout.

### Proven chronology and evidence limits

- Coordinator snapshots442/443/444 accept the sender hold preparation/capture/drain;
  445 issues its exchange. Receiver514/515/517 accept the corresponding hold steps;
  518 issues its exchange. No phase precedes that side's accepted hold receipt.
- Sender phase receipts516/519 and receiver558/559 are accepted. Timeout diagnostics
  show sender Solicit and receiver Response delivered and physically settled, with
  all epoch and payload copies successful. Both exchanges remain noninvalidated
  and incomplete, with no fallback transition/publication/disposition.
- Issued/current checkpoints compare equal on both sides. Sender StateEpoch is1,
  receiver2; both frontier0, durable-highest-1, chunk-count4, data-session-generation1.
  Both still own the initial file_tuna_v4 publication generation2. The previous
  ordinary-state drift and fresh-pause invalidation are not this run's first edge.
- Response reaches the sender NKN channel: control dispatch-entered543 and queued545
  (queue2), bulk dispatch-entered550 and queued552 (queue3), secondary success557.
  There is no pre-timeout sender Service Response receive/peer-observation or Confirm.
  Queue admission and phase delivery are not Service consumption or route authority.
- The fixture releases its source-read barrier before waiting for fallback
  (NknBootstrapCorrelationJoinedTests.cs:110). Intentional continued source blocking
  is therefore not established. Disposal/channel-close/stale-descriptor records
  beginning560 are cleanup after timeout, not proof of the preceding cause.

The first missing proof-chain edge is queued correlated Response to the sender's
existing Service receive/validated correlated dispatch, before Confirm issuance.
The precise blocked await or lost dispatch identity is NOT established yet.
CurrentBindingPresent in timeout diagnostics is a context read, not an ingress
receipt. Transport logs with direction/route absent cannot manufacture or disprove
the Service's current receive authority either.

Source inspection identifies the narrow area to test, not a proven patch location:
PullTransferSessionV4.cs:2772 starts session-generation receives; :2842 supervises
receive/pump-install/pump completion; :2675 adopts completed installs. Received
frames pass install/session/source revalidation before the receive log at:3483
and again before correlated dispatch at:3575. The selected payload-source hold
guard at:16979 must not suppress or strand that existing control ingress. Do not
infer that it does solely because it was recently added, or bypass the current
descriptor gates to force a Confirm.

### Recommended bounded sequence, pending next approval

1. Add a controlled regression around the existing outbound receive supervisor and
   real source-handoff path. Capture exact pending-receive session/provider/Service
   generation, supervisor generation, captured/current install and owner, and the
   handoff/dispatch stage before timeout. Use existing barriers where available;
   any additional seam is test-only observation, not authority or a new supervisor.
   Reproduce queued Response without Service dispatch and identify the first
   blocked transition. Keep the source release and existing timeout unchanged.
2. Fix only the demonstrated receive/handoff continuation gap. Current correlated
   controls must reach existing authenticated dispatch while ordinary held-source
   payload stays stopped. Preserve exact Coordinator hold/exchange/phase ownership,
   session/install validation, stale-callback rejection and physical settlement.
   Do not add direct transport-to-Coordinator dispatch, a second receive consumer,
   automatic reinstall/phase renewal, queue polling, timeout extension or retries.
   If the gap requires a new lifetime/authority representation, return for contract
   review before implementing that expansion.
3. Prove current Response-to-Confirm plus stale session/provider/install/owner,
   new pause, terminal and duplicate controls using only directly affected tests.
   Then rerun the same sender-only joined gate once. Require both completed
   exchanges and fallback publications/dispositions, not merely consumed Response
   or emitted Confirm. Stop at the next missing edge; receiver/dual/evidence work
   follows only after sender acceptance in section8's original order.

This is a bounded diagnostic/fix proposal, not a completed RCA for a specific await
and not approval to commit or enable the current changes. Production capability
remains explicitly false. No further joined or broader run was made after this
failure. Retained artifact SHA256: TRX
B6E56805B3C9BA28475DBCCE0A3E647357409606E7E5CA2FA8A7A849C23B0102;
log105437A67336FF89270C54F994FCF37893C089A16A0C959E0E71B3B2510438F3.

### Diagnostic RCA: synchronous held-source pump spin (2026-09-11)

The user next requested RCA and a fix plan, not runtime implementation. The
following focused diagnostic resolves the pump-start mechanism left uncertain
above. Runtime sources, the retained joined artifacts and HEAD are unchanged.
Only a two-case diagnostic regression and this plan/checkpoint were added.

The root defect is disagreement between selection and submission admission:

1. The accepted hold sets PullTransportPaused with reason bootstrap_source_hold
   (BootstrapSourceHold.cs:130-132), retaining unused ordinary sender credit.
2. ShouldPauseOutboundV4SenderPumpForTransportPauseLocked (:10206) recognizes four
   older route/recovery pause cases, but not the Coordinator-owned source hold.
   The pump's selection at:10787 therefore treats this source as runnable.
3. With RemoteGrantedUntilExclusive greater than ChunksAcceptedForTransport, it
   selects the same ordinary chunk range (:10933-10953). SendChunkIndicesV4Async
   rejects the exact held source at entry (:16979/:17072) before I/O and returns
   a completed false task. Accepted chunks and credit remain unchanged, correctly.
4. The caller awaits that already-completed task and unconditionally continues
   (:11758-11767), without reaching the existing signal/delay wait. The next
   iteration selects the identical range. This is a synchronous no-progress loop,
   not an asynchronous wait for delivery, stream bytes or checkpoint acceptance.
5. TryInstallOutboundSparsePumpAsync starts that async pump inline at:1516. An
   async call does not return its Task until its first incomplete await. The loop
   can therefore strand the installer before its return, and the existing
   InstallCurrentPumpAsync/StartCurrentPumpInstall chain before assignment of
   pendingPumpInstallTask (:2664). The receive supervisor cannot proceed to its
   next receive/Task.WhenAny even though the new descriptor was already committed
   and logged as installed. Logging installed is not installer-return evidence.

The retained joined run matches these prerequisites: credit4 is received at
writer117/135, the initial source has accepted0 chunks, the hold drains at444,
and the new Tuna install is logged at483 before Response queues at545/552.
The failure is thus local control-ingress starvation caused by held payload
selection, not evidence that Response needs looser authentication or a fresh owner.
This source-chain explanation is supported by the controlled reproduction below;
there is no new joined capture or claim that all later handshake edges will pass.

### Controlled proof and its limits

SessionFileTransferBootstrapSourceHoldPumpRcaTests.cs uses the actual Coordinator
history, accepted Service hold/receipt and TryInstallOutboundSparsePumpAsync. It
does not inject a pause flag, fake an install return or bypass source authority.
The two cases differ only in unused credit: accepted4/granted4 versus
accepted4/granted8. An existing test-only normal-completion hook observes the first
seven iterations without changing them and stops at iteration8 to bound a spin.
The hook's forced completion is diagnostic cleanup, never fallback completion proof.

bootstrap-source-hold-pump-rca-final.trx is1 PASS/1 FAIL:

- No unused credit: the installer returns without the safety stop; control PASS.
- Unused credit: hold=true, ordinary pause policy=false, eight inline iterations,
  accepted chunks still4 and zero payload frames. Regression FAIL. The second
  iteration's captured synchronous stack contains RunOutboundV4SenderPumpAsync and
  TryInstallOutboundSparsePumpAsync:1516 before reflection invocation returns.

This deterministically proves the non-yielding installer mechanism; it does not
measure production CPU usage or validate live networking. The test uses a
Coordinator-authored successor fixture, while the retained joined run is initial
Tuna activation. The shared selected-hold/credit/install path is the proven overlap;
joined initial-route acceptance remains a separate required gate after the fix.
The earlier diagnostic0/2 file retains test-cleanup exact-type assertion failures
for Task subclasses. Cleanup was corrected to assignable Task/Stream checks; no
runtime source or substantive regression assertion changed.

Final diagnostic SHA256: TRX
3CD3E88F76C0A46D7112A0DEA441DC56F61AED0FE6E40C2A5D329C64FC23D5DF;
log C25892172AA9A9E351EF2478A62E1794487DF45222185591308946570A48EA91.
The failing regression remains intentionally visible, not skipped or relabeled.

### Bounded fix contract for review

Keep the fix in the existing selected outbound sparse pump. No new command, owner,
receive supervisor, stream policy, physical settlement tracker or transport change
is needed for this defect.

1. After the existing context/terminal/install-currentness checks, recognize the
   exact source hold through HasBootstrapSourceHoldLocked, before terminal-ready
   inference, repair dequeue, normal chunk selection or tail reconciliation. Use
   the existing context signal/wait path outside the Service gate. Preserve the
   low-level source-submission gate as protection against a hold arriving after
   selection; a failed send is not permission to consume credit or resume work.
2. The existing idle-delay continuation must also recognize the hold before any
   repair settlement/queueing, tail reconciliation or liveness-affecting work.
   Reuse the current signal and existing poll/cancellation mechanics; do not add
   another timer or refresh the transport recovery deadline. An unrelated/spurious
   signal merely causes currentness/hold revalidation, not release of the hold.
3. Preserve fail-closed same-source invalidation. A new pause or source/session
   replacement cannot turn a stale hold into resumed source work. Current
   Coordinator publication/handoff controls when the old pump exits and the new
   route may run. Do not weaken receive/install checks or put correlated dispatch
   ahead of authentication/currentness to compensate for starvation.

Do not solve this by Task.Run/Task.Yield around the pump, by returning a completed
pump on hold, by incrementing accepted chunks or clearing credit/repair queues.
Scheduling the spin elsewhere leaves the admission defect; returning can trigger
the existing HasOutboundSparsePumpWorkLocked-driven reinstall loop (:1368/:2945).
A generic pause-reason exemption would also lose the exact Coordinator hold binding.

Verification after separate bounded implementation approval:

1. Make both retained installer cases pass without their diagnostic safety stop.
   Add an actual current supervisor/handoff case proving it can consume and validate
   Response while payload remains held; keep exact session/provider/install/owner
   replacement, new-pause, terminal and duplicate controls fail-closed.
2. Cover hold application between selection and submission, held repair/terminal/
   idle-delay paths, and spurious signals. No send, progress, repair dispatch,
   deadline refresh or normal-pump completion may be synthesized while held.
   Existing canceled/retired physical work retains its original ownership.
3. Run only affected pump/hold/currentness cases, then one relevant contract lane
   after stabilization. Rerun sender-only joined acceptance once with its existing
   source release and deadlines; require both fallback publications/dispositions.
   Stop at the next proof edge. Receiver/dual-stop and evidence publication remain
   ordered behind that gate, as in section8.

RCA and bounded plan are ready for review; the runtime fix is not implemented.
Production remains false, index empty, no commit/push, package, GUI/preflight/live
NKN, full report, aggregate matrix or new campaign/report allowance. Previous122
focused passes/1SKIP are historical results, not reruns during this RCA. Joined
sender remains0/1 FAIL; this turn adds a distinct1PASS/1FAIL diagnostic pair.

### Approved pump fix implemented; joined gate advances (2026-09-11)

The user's subsequent implement request authorized the bounded fix above. The
runtime delta is confined to PullTransferSessionV4.cs:10795 and:11798: the exact
source hold selects the existing signal wait before ordinary/repair/terminal work,
and a still-current held idle continuation returns to that wait before repair or
liveness effects. Existing context/install validation, low-level physical admission,
poll/cancellation mechanics, deadlines and Coordinator ownership are unchanged.
No new receive loop, timer, scheduling workaround or authority representation.

The original two installer cases pass without their safety stop and now explicitly
require the held pump to remain incomplete. Added controls cover spurious signal,
queued repair, terminal-ready state, a new pause, Service generation replacement,
provider replacement, terminal cancellation, and hold arrival while the selected
source batch is reading. The last case proves no physical submission or later
selection; already-selected batch reads retain existing semantics. Its first
assertion incorrectly required a single read even though the batch aggregates
multiple chunks. That0/1 development artifact is retained; the corrected bound
uses the selected range and requires the read count to remain stable through idle.
No runtime I/O behavior was changed to satisfy the fixture.

Final focused inventory is32 distinct PASS and1 FAIL(joined sender). The stabilized
hold/currentness/Core contract lane passes24/24; the directly affected existing
receive/worker lane passes8/8. Seven retained TRX artifacts this turn are prefixed
bootstrap-source-hold-pump-{fix,controls,selection,selection-final,currentness,
contract,joined-sender}. Their individual counts are2/0,9/0,0/1,1/0,8/0,24/0,0/1
(passed/failed). No aggregate suite or previous122-case inventory was rerun.

The sender-only joined gate still fails0/1, but it proves the intended runtime
advance: Service consumes Response at551/566, Coordinator accepts it at564,
Confirm completes, and both exchanges are Completed=true/Invalidated=false.
Sender fallback commit is accepted609, retained-frame receipt673, disposition680;
Service logs publication618 and applied disposition682. Sender owns current
post_tuna_fallback_v6/protocol6/publication3. Its missing old-source binding at
timeout is expected after source publication changes, not checkpoint corruption.

The receiver remains at file_tuna_v4/protocol4/publication2 with its unchanged
epoch2/frontier0 frozen checkpoint. Its next failure is the pressure-lineage gate
below, not the prior receive starvation. The strengthened final joined assertions
for exact hold linkage and Confirm are not reached because the two-sided
publication wait fails first; the partial-success claims above come from the
retained timeout state and actual Coordinator/Service records, not a passing test.

No further runtime change or joined rerun followed this new first edge. Receiver/
dual-stop and the formal initiation/hold evidence extension remain blocked. HEAD
adfa9c049c10e233d6b449b980cad09b33166e94, index empty, production capability false;
no commit/push, package, GUI/preflight/live NKN, full report, aggregate or subagent.

## 12. Receiver pressure-lineage stop after completed exchange

Latest relevant artifact is bootstrap-source-hold-pump-joined-sender.trx/log in the
same implementation artifact directory. It uses the rebuilt current dirty sources
and the existing sender-only initial-activation/stop fixture over fake transports.
TRX SHA256 E8808A5E2AC197E02A08756692BB59E982B95532E67C1CFBFCCFB1E1E3284F4C;
log SHA256 4FCEEB69CC223D442805EDF37CCA7717C9781054AB992A2589AAB309C49B8067.

The receiver's exchange completes and its correlated peer proof is observed589.
Transition preparation592, pause binding594/595, and physical-drain admission600
all succeed. The drain captures zero physical sends. At603 the Service rejects
the drain/commit continuation with
filetransfer_route_transition_pressure_lineage_pending. Receiver state stays
Draining; no inbound commit/disposition follows before timeout. Later legacy
checkpoint-frame rejections and cleanup are downstream, not the first failure.

This is an explicit lineage rejection, not an unexplained wait for physical sends.
The existing Service pressure-local-adoption/settlement gates
(RouteTransitionFallback.cs:4449/:4739) and Core commit gate
(FileTransferCoordinator.cs:7512) reject a nonnull PendingBinding or
RetainedPredecessorBinding. An empty physical drain, completed bootstrap exchange,
or adapter readiness cannot substitute for the missing Coordinator pressure
lineage settlement. The precise pending/predecessor identity and its missing
producer/adoption/settlement edge have not yet been traced; do not clear it or
relax that gate based on the reason string alone.

Recommended next bounded step is RCA and fix-plan review: trace that exact receiver
lineage from its Coordinator issuance through current Service/adapter receipts,
identify the first absent/stale operation, and reproduce it with the existing
fixture. Propose only the exact ownership/settlement repair, retaining malformed,
duplicate, superseded and terminal controls. If a new representation is needed,
review it before implementation. Do not tune pressure/queue performance, invent a
settlement receipt, adopt adapter dictionaries as authority, bypass the gate,
renew the bootstrap exchange or extend deadlines. The current pump fix is complete
for its scoped defect, but sender-only two-sided acceptance is still NOT passed.

### 12.1 Completed RCA: unsupported joined-fixture pressure installation

Read-only RCA on 2026-09-11 traces the existing artifact to the first missing edge:
receiver RepairReplay is accepted481, then StageRegularV4FrontierRepairPressureBinding
is accepted508. The actual pressure_binding_pending operation at508 (not the
attempted_decision projection) has install generation1, receipt status Pending,
receiptValidated=false and no predecessor. Its evidence authority ID is
fte1-6146621955da60a685e2ddc8; the matching operational authority is
fta2-repair_replay-c6cbecf87756be5bec2ee618, repair key0:4:0:4, decision generation1,
frontier0, durable highest-1, requested count4. Source is inbound file_tuna_v4,
protocol4, live epoch1, runtime2, terminal0, leg2, transport epoch0, data-session
generation1, publication2 and commit1. Pressure reason is
file_tuna_v4_receiver_frontier_repair_due. This predates receiver hold preparation514.

Service stages the initial install510/511 at inbound_frontier_tail_attempt. NKN
rejects512 and Service records Rejected513, both install generation1; no accepted
installation/settlement closes this binding before continuation rejection603.
The operational decision reason is redacted. Its exact cause is established by
current source, not guessed from that redaction: NknBootstrapCorrelationJoinedTests
constructs FakeNknClient for each peer; the proxy includes the pressure-install
bridge interface and forwards its calls to NknSignalingTransport. Acceleration.cs
rejects every non-RealNknClientAdapter with
regular_v4_frontier_repair_pressure_install_unsupported. Settlement application
also rejects that client type with
regular_v4_frontier_repair_pressure_settlement_apply_unsupported.

PullTransferSessionV4.cs returns after the rejected initial install, before the
Coordinator installation-commit call. The pending binding therefore remains
unvalidated. Existing Service and Coordinator lineage gates correctly reject603;
this is not evidence that those gates should clear or waive pending state. The
first absent proof is an authentic accepted pressure-install receipt, not a lost
bootstrap response, physical-send completion or route-transition callback. The
joined fixture has an unsupported pressure backend; this artifact does not prove
a production RealNknClientAdapter pressure defect or subsequent successful settlement.

### 12.2 Bounded harness fix contract (subsequently approved; status below)

Use the existing RealNknClientAdapter install/settlement validators offline, as
already exercised without Connect by RegularV4PressureInfraShadowTests and
Bridge_RegularV4FrontierRepairPressure_InitialInstallIsIdempotentAndOwnsSettlement.
Do not implement another acceptance state machine or return synthetic success.

1. Add only an internal constructor-bound pressure-adapter test seam in
   NknSignalingTransport. Normal construction continues to use its actual real
   client, or remains unsupported for a fake client without this explicit seam.
   The joined fixture supplies one unconnected real pressure adapter per peer;
   production construction/capability advertisement must not change. No public
   option, mutable/global override, new Core authority DTO or adapter refactor.
2. Route pressure installation, settlement application and their existing feedback/
   settlement-observation checks through that same adapter. Feed it from the
   transport's actual data-session registration/removal and runtime state commit/
   terminal/reset lifecycle calls. Preserve existing call order and exactly-once
   bookkeeping; production must not double-register its real client. Test ownership
   disposes the offline backend after transport teardown. Do not seed active maps
   by transfer ID at fixture setup, infer readiness from pressure, wrap/replace data
   sessions, intercept receipts in the proxy, or broaden unrelated real-client paths.
3. Add focused controls through the actual NKN bridge: no override stays unsupported;
   inactive lifecycle stays rejected; valid issued command yields the real receipt;
   exact duplicate stays idempotent; mutated command/generation/predecessor and stale
   settlement cannot clear current/successor ownership. Cover actual session close,
   replacement, terminal/reset and late callback paths. Lifecycle bookkeeping is
   evidence only; existing Service/Core currentness remains the acceptance authority.
4. After focused seam/pressure tests and directly affected Infra/Service/Core lanes
   pass, run at most one broader relevant contract lane, then rerun only the retained
   sender-only joined test once. Require receiver pressure installation adoption and
   authorized settlement plus both exchange completions, fallback publications and
   exact transition/disposition linkage. Neither adapter receipts alone nor SHA/
   transfer completion substitutes for Coordinator route proof. Stop at the next
   missing edge; receiver-only/dual-stop and formal evidence extension remain blocked.

At RCA closeout this was a proposed harness dependency, not an implemented fix. If exact lifecycle
forwarding requires a broader representation change, stop for review. A resulting
pass would be Service/NKN integration over fake byte transport with a real offline
pressure backend, not live bridge/network acceptance. No live rerun is needed for
this deterministic non-real-client branch. No new tests, builds or executions were
run for this RCA; the retained failing artifact remains unchanged. Runtime/tests
unchanged this turn; no staging/commit/push, package, GUI/preflight/live, full report,
aggregate, subagent or production enablement.

### 12.3 Approved harness dependency implemented; verification stopped

The user approved section12.2 on 2026-09-11. NknSignalingTransport now has a readonly
pressure-adapter dependency selected by an internal constructor overload. Existing
constructors preserve their signatures and select the actual RealNknClientAdapter,
or null for an unsupported fake client. An actual real client takes precedence over
the test argument. The four existing pressure install/apply/observation methods and
five existing data-session/runtime registration/removal/reset methods use that same
adapter, once at their original call sites. No RealNknClientAdapter validator, Core
authority, Service gate, session identity/interface or runtime policy was changed.

Each joined peer now owns an unconnected real pressure backend, disposed after its
NKN transport and Service. The proxy still forwards original calls and only changes
capability advertisement/event-sender identity. Production capability remains false.
The joined test additionally requires exact committed transition/disposition route
publication linkage and inbound Coordinator pressure settlement, but these new final
assertions have NOT run: its prerequisite regression lane aborted first.

New NknRegularV4PressureBackendTests controls exercise real Core-issued commands and
actual NKN bridge receipts through actual fake-network session setup. Latest result
per case:9 PASS, no SKIP. Coverage includes unsupported/inactive rejection with Core
pending state preserved, immutable production backend choice, duplicate session open,
close/replacement and late disposal, runtime acceptance/terminal/reset bookkeeping,
exact install/duplicate/malformed command, retained predecessor settlement, malformed
generation/scope/current authority, terminal/progress settlement and late Core receipt
rejection. Adapter activity maps are inspected only, never seeded. No accepted install
or settlement receipt is fabricated. Pre-apply settlement observation cannot apply it.

Retained test history (all under the existing implementation artifact directory):

- bootstrap-pressure-backend-focused.trx:7 PASS/2 FAIL. Both failures were the final
  test assertion demanding that the adapter remember every retired authority forever.
  This is not its contract. Replaced that assertion with a late genuine receipt into
  Core, requiring rejection and unchanged settled state; no runtime validator change.
- bootstrap-pressure-backend-ownership-final.trx:2/2 PASS after that test correction.
- bootstrap-pressure-backend-affected.trx:9 cases PASS, then testhost stack overflow;
  overall run ABORTED/failed, not a passing lane. Eight existing bridge pressure cases
  and one Service data-session replacement case completed; remaining cases did not.
- bootstrap-pressure-backend-route-regression.trx:the exact crashing test alone also
  aborts with stack overflow, no completed case. No joined rerun followed either crash.

Thus18 distinct cases have latest passing results, but the required affected lanes
and sender-only joined acceptance are incomplete. Build history also contains one
Avalonia telemetry sandbox write failure and two compiler-failed attempts (a lambda
parameter named underscore captured out-discards). No tests ran in those attempts.
The lambda name was corrected. Command-only UsedAvaloniaProducts= disables the
build telemetry task; no repository build setting or sandbox permission was changed.

No broader contract lane, receiver-only/dual-stop, formal evidence extension, package,
GUI/preflight/live, full report, staging/commit/push or subagent. Prior joined failure
artifacts and unrelated three file pins are unchanged. HEAD remains
adfa9c049c10e233d6b449b980cad09b33166e94; implementation is uncommitted and not yet
accepted end-to-end. Four retained TRX windows total11.0229206s, recorded separately
from the overlapping root work bound; complete outer build timings are unavailable.

## 13. Regression prerequisite stop: Coordinator snapshot stack overflow

The latest exact reproducer is bootstrap-pressure-backend-route-regression.trx/log.
TRX SHA256209FC50DC7969E03957103746302FD307F32730A581D1DDEFFA5C27E00B8DEA5;
log SHA25666BBA815F31E7A3C8442F2AB0A70CC5C608C99A8DA8FE4140AD9341E94D3BFEE.
RegularV4Pressure_RouteCommitRetiresExactBindingBeforeTunaOwnerInstall constructs
LoopbackFileTransferTransport, not NknSignalingTransport or the new backend seam.
It crashes during PrepareInboundRegularV4ToTunaRouteTransitionForPressureTest ->
Service TryPromoteInboundRegularNknV4ToFileTunaV4Locked -> Core
PrepareFileTransferRouteTransition -> snapshot creation. Top reported frame is
AddFallbackTailYieldOperationDescriptors, followed by CreateOperationDescriptors,
CreateOperationTransitionSnapshots and CreateTransitionSnapshot. The same boundary
appears in the affected-lane run and the isolated rerun of current rebuilt sources.

This is a confirmed prerequisite crash outside the modified backend path, not proof
of a newly introduced NKN defect or a complete stack-overflow RCA. Do not infer
recursive logic versus excessive finite stack usage from the top frame alone. The
next bounded step is source/stack RCA and a focused reproducer for this exact
snapshot path, then a separately reviewed fix plan if needed. Do not increase stack
size, bypass snapshot authority checks, remove pressure coverage, quarantine the test
or proceed to joined validation merely to get past this prerequisite. No unrelated
runtime change was made after discovering this stop.

### 13.1 Completed RCA: overlapping fixed snapshot frames

One additional no-build diagnostic reran the exact failing Debug test with only
method-specific DOTNET_JitDisasm/DOTNET_JitStdOutFile enabled in that child command.
No stack, tiering, optimization, project configuration or source change was made.
It aborts at the same boundary. The retained bootstrap-snapshot-stack-jit.asm shows
Windows x64 MinOpts (debuggable) machine code, not the historical Release/Tier0
artifact from rca-fallback-control-stack. The old RCA is context, not current proof.

The reported chain is acyclic. Fixed prologue allocations, excluding saved registers,
return addresses and unlisted reflection/xUnit/runtime frames, are:

| Active method | Reserved bytes | KiB |
| --- | ---: | ---: |
| Exact Service test body | 1,168 | 1.14 |
| PrepareInboundRegularV4ToTunaRouteTransitionForPressureTest | 81,856 | 79.94 |
| TryPromoteInboundRegularNknV4ToFileTunaV4Locked | 240 | 0.23 |
| TryPrepareInboundRegularV4ToTunaRouteTransitionLocked | 25,440 | 24.84 |
| TryPrepareInboundFileTransferRouteTransitionLocked | 125,456 | 122.52 |
| PrepareFileTransferRouteTransition | 44,288 | 43.25 |
| FinalizeRouteTransitionDecisionSnapshot | 558,240 | 545.16 |
| CreateTransitionSnapshot (before/after overload) | 313,648 | 306.30 |
| CreateOperationTransitionSnapshots | 560 | 0.55 |
| CreateOperationDescriptors | 128 | 0.13 |
| AddFallbackTailYieldOperationDescriptors | 389,344 | 380.22 |
| Total named frames | 1,540,368 | 1,504.27 |

The failing collector reserves0x5F0E0 bytes through CORINFO_HELP_STACK_PROBE before
reading fallback state. Its name at the top of the crash does not establish a
fallback-state/authority defect. Source and generated code show large fixed local/
temporary copies of nested value records. For example, the persistent-state getter
is followed by a0x10F0-qword copy (34,688 bytes); nullable operation/receipt copies
also remain in the fixed frame even when their branch has no work. Both
FileTransferCoordinatorState and FileTransferFallbackTailYieldSubstate are already
reference records; FileTransferCoordinatorPersistentState and several nested
operations/receipts are value records. A global class/struct rewrite is not justified.

The finalizer constructs the attempted-operation snapshot using large value
temporaries, then calls the common snapshot builder while its entire545.16KiB
frame remains reserved. The common builder calls all family collectors and adds
another306.30+380.22KiB at this edge. Earlier RepairReplay, pressure stage/install
and precommit-pending snapshots complete in this same process (generations0->4);
the later route preparation enters the heavier finalizer chain and exhausts the
available stack. This establishes additive fixed-frame exhaustion, not recursive
route/repair execution or a failure of the pressure backend. Exact OS thread stack
reservation/remaining bytes were not measured; no nominal stack-size claim is made.

Production snapshot methods dominate this reproduced test stack, but no live GUI/
network crash or general Release/default-stack guarantee has been established.
A test-wrapper split could remove some of its79.94KiB overhead; its sufficiency is
not measured. The proposed fix below instead removes the directly observed large
production-frame overlap without changing authority data or test execution policy.

Current diagnostic artifacts in the implementation directory:

- bootstrap-snapshot-stack-jit.trx:ABORTED, no completed case;
  SHA2563D3FFF55F041EF474544ADA3C673FCF7E73016225128B5640DBC01AA37271E72.
- bootstrap-snapshot-stack-jit.log:
  SHA256EF27CABF885F1FA1962AC80DADA6D4A293F5B55B957CA0BC5B054E1161E876C9.
- bootstrap-snapshot-stack-jit.asm:
  SHA256267949032D54CD0C22FC466339DCD66FA5B156A6CCD1B38081A9E7C1C7482A1A.

The no-build TRX window is1.546835s. A fresh shell shows no persistent JIT/tiering/
stack override. Snapshot source and the failing test source are unchanged from
current HEAD; no runtime or test source was edited in this RCA.

### 13.2 Bounded fix contract (subsequently approved; status below)

1. In FileTransferCoordinator.Snapshots.cs, extract only the attempted-operation
   construction inside FinalizeRouteTransitionDecisionSnapshot into a private
   MethodImplOptions.NoInlining helper returning the completed immutable operation
   array. Move the existing construction, including responder-handoff augmentation,
   without changing fields, decision inputs, normalization or branch conditions.
   The lightweight outer finalizer retains its existing identity/no-snapshot gate,
   invokes the helper to completion, calls CreateTransitionSnapshot once after that
   helper returns, and stamps the same returned state/envelope into the decision.
   Do not make a tail/helper call to CreateTransitionSnapshot from inside the large
   construction frame: that would preserve the offending overlap. No new authority
   representation, scheduling, parallelism, observer side effect or cached state.
2. Preserve exact operation roles/keys, command and receipt identities/fingerprints,
   rejected/idempotent mutation checks, responder augmentation, all family collectors,
   barrier/continuity reconciliation, boundary comparisons, timestamp/transition ID
   generation, generation advancement and the single outer snapshot publication.
   Do not alter CreateTransitionSnapshot, the fallback collector, Service route logic
   or fixture stack policy as part of this first patch. If another frame still
   overflows, retain it and stop for a newly bounded review rather than widening.
3. Run the exact failing Service test first under normal Debug JIT/stack settings.
   Require both survival and all existing pressure retirement, route publication,
   ownership and emission assertions. Capture method-specific disassembly afterward
   if needed to verify that the large construction frame returns before the common
   snapshot builder is entered; a diagnostic-only pass is not an acceptance gate.
4. Verify narrowly affected snapshot semantics: missing Coordinator identity,
   accepted/rejected/idempotent decisions, installed-pressure exact receipt,
   responder handoff augmentation, superseded drain and rollback/terminal paths.
   Reuse current route-transition fixtures and family-local descriptor assertions;
   do not substitute fabricated successful receipts or skip real route history.
   Canonical comparisons must preserve all authority/operation fields; only existing
   nondeterministic time/transition-ID handling may differ across separate executions.
5. Complete the previously aborted affected pressure lanes, avoiding reruns of cases
   whose covered path did not change. At most one broader relevant contract lane.
   Only after the prerequisite passes, run the sender-only joined test once. Its
   two-sided exchange, exact pressure settlement and route/disposition publication
   assertions remain mandatory. Stop at the next proof edge before receiver-only/
   dual-stop or formal evidence extension.

At RCA closeout this was a reviewed proposal, not an implemented runtime fix. No larger thread stack,
Task.Run/thread workaround, permanent JIT optimization, missing-family fast return,
snapshot guard weakening, fixture quarantine, queue tuning or deadline extension.
No build, broader suite, joined rerun, package, GUI/preflight/live, full report,
staging/commit/push or subagent in this RCA. Production capability remains false;
the harness change is still uncommitted and end-to-end acceptance remains blocked.

### 13.3 Approved phase split implemented; next commit-stack edge exposed

The user approved section13.2. Only FileTransferCoordinator.Snapshots.cs runtime code
changed: the outer finalizer retains normalization and the missing-identity return,
calls a private NoInlining CreateRouteTransitionAttemptedOperationSnapshots helper,
then invokes the common snapshot builder once and returns the same stamped decision.
The attempted-operation construction block, including responder augmentation, is
textually identical to the original after line-ending normalization. No authority,
fingerprint, receipt, reconciliation, generation or publication rule was changed.
No test fixture or assertion was edited, and no stack/JIT/scheduling policy changed.

The rebuilt exact test ran first with normal Debug settings. It advances past the
old collector overflow and records PrepareFileTransferRouteTransition Prepared4->5
and BeginFileTransferRouteTransitionPhysicalDrain Draining5->6, both containing
responder_probe_handoff and route_transition snapshot families. Runtime-unlock
scope invalidation is then Idempotent6->6. The test subsequently aborts during route
commit; it does not reach all final assertions and is NOT a passing prerequisite.

A single no-build method-specific disassembly rerun confirms the same later abort
and the intended phase separation under unchanged MinOpts code generation:

- Outer finalizer:558,240 ->125,952 bytes (545.16 ->123.00KiB), a432,288-byte reduction.
- New attempt helper:474,944 bytes (463.81KiB), returns before the common snapshot
  builder call. Its generated code does not call that common builder.
- The common snapshot313,648-byte and fallback collector389,344-byte frames are
  unchanged and no longer overlap the old545.16KiB construction frame.

This verifies the original preparation/snapshot edge advanced; it does not establish
a passing full Service regression or a global stack bound. Per the explicit stop
contract, no further runtime edits, semantic/affected/broader lanes, or joined rerun
followed the newly exposed commit-stack failure. Their verification remains pending.

Artifacts in the implementation directory:

- bootstrap-snapshot-phase-exact.trx/log:normal Debug run, ABORTED/no completed case.
  TRX SHA2566FFB3DC098FB5A8C69E8105D507B6D4AA5A49FD9733614998936D273698495EA;
  log SHA256B7A892393DE2655E86D40B1B7AC8A44B28B1264346602F155E4FB9C33F33411E.
- bootstrap-snapshot-phase-jit.trx/log/asm:diagnostic rerun, same ABORT, no completed
  case; asm SHA2566F8138F11403B7FA5B67B5FC4F52D1B0912B846FA91BDCF0B67076179297B8B5.

Two TRX windows4.8920759+1.5762385=6.4683144s. First command rebuilt successfully;
complete outer build elapsed time was not separately captured. Command-only
UsedAvaloniaProducts= still avoids the sandboxed telemetry write; no project setting
changed. Fresh shell has no JIT/tiering/stack override. Prior18 passing cases remain
historical, not newly rerun. HEAD adfa9c049c10e233d6b449b980cad09b33166e94; index empty,
production capability false. No package/GUI/preflight/live, report, subagent,
staging/commit/push or new campaign/report allowance. Implementation remains uncommitted.

## 14. Next prerequisite stop: route-commit stack overflow

The latest ordinary-current-source reproducer is bootstrap-snapshot-phase-exact.trx,
confirmed by bootstrap-snapshot-phase-jit.trx. This is later than section13's fixed
snapshot preparation edge. Same exact Service test and loopback fixture now reach:
CommitInboundFileTransferRouteTransitionForPressureTest -> Service
TryCommitInboundFileTransferRouteTransitionLocked -> Core
CommitFileTransferRouteTransition/Core -> TransitionToRoute ->
InvalidateFallbackOwnership -> InvalidateFallbackTailYieldSubstate ->
ApplyFallbackTailYieldSubstate (top reported crash frame).

The diagnostic retains the new active methods' prologues; CommitCore reserves985,328
bytes and ApplyFallbackTailYieldSubstate104,112 bytes. These measurements are initial
evidence, not a complete new-chain RCA or permission to skip fallback invalidation.
The attempted-operation helper and snapshot finalizer are not on the new crash stack.
No claim is made that route commit completed, pressure ownership retired, or the
sender-only joined gate passed. No live/Release failure or general stack size is assumed.

Recommended next step is bounded RCA/fix-plan review of this exact commit chain,
using the newly retained disassembly and current source. Determine the active
fixture/Service/Core overlap and a minimal phase boundary before proposing a change.
Keep source-state reconstruction, pressure/retry/fallback invalidation, route commit,
publication and all rejected/stale callback rules intact. Do not enlarge stacks,
switch JIT policy, skip the failing test, suppress snapshots, clear authority state or
continue to joined/receiver/dual/evidence validation past this prerequisite.

### 14.1 Completed RCA: overlapping fixed commit frames, not recursive invalidation

RCA-only review on 2026-09-11 reused the current-source normal Debug reproducer and
method-specific disassembly from13.3; no new build or test run was needed. Both
artifacts still report ABORTED with zero completed test cases. The normal run reports
x64 .NET8.0.31; the diagnostic emits Debug MinOpts code. HEAD remains
adfa9c049c10e233d6b449b980cad09b33166e94 with the accumulated uncommitted changes.

These fixed stack reservations are simultaneously on the reported call path:

| Method, caller to callee | Reservation bytes | KiB |
| --- | ---: | ---: |
| Test CommitInboundFileTransferRouteTransitionForPressureTest | 288 | 0.28 |
| Service TryCommitInboundFileTransferRouteTransitionLocked | 140,560 | 137.27 |
| Core CommitFileTransferRouteTransition | 48,432 | 47.30 |
| Core CommitFileTransferRouteTransitionCore | 985,328 | 962.23 |
| TransitionToRoute | 160,128 | 156.38 |
| InvalidateFallbackOwnership | 1,168 | 1.14 |
| InvalidateFallbackTailYieldSubstate | 155,520 | 151.88 |
| ApplyFallbackTailYieldSubstate | 104,112 | 101.67 |
| Selected fixed reservations total | 1,595,536 | 1,558.14 |

This approximately1.52MiB total excludes the test body, reflection/xUnit/runtime
frames, saved registers, return addresses and any unmeasured stack usage. It is not
an OS stack-reservation measurement or a universal stack limit. The top method probes
its fixed0x196B0 frame before reading state; no instruction-pointer capture establishes
the exact faulting instruction. The observed chain and its source are acyclic.
ApplyFallbackTailYieldSubstate only copies/stamps the persistent substate and returns;
it does not recursively invoke route commit or fallback invalidation.

The dominant0xF08F0 CommitCore frame remains allocated across TransitionToRoute and
the entire invalidation chain. Its one method includes early idempotence/validation,
pressure settlement/projection, route mutation, publication/disposition construction,
terminal reconstruction and fallback checkpoint registration. Debug code reserves
temporaries for these sequential source phases in one prologue. PersistentState is
a large readonly value record: retained assembly copies0x10F0 qwords (34,688 bytes)
for that value. CoordinatorState and FallbackTailYieldSubstate are already reference
records; changing those types to classes is not a relevant remedy. Nested value
copies amplify the fixed-frame cost; this is not evidence of heap exhaustion or a
network timeout.

The first uncompleted edge is the return of the coordinator route-mutation chain
during commit. Prepared4->5/Draining5->6 and retry invalidation Idempotent6->6 are
already recorded. Source order shows pressure-receipt validation/projection precedes
the failing call, but the crash does NOT prove a completed/adopted pressure retirement,
new owner, route publication or passing test. The snapshot attempt helper/finalizer
are absent from this crash stack. The loopback fixture does not use the new NKN
pressure backend. The older Release fixture RCA is context only, not current Release
or live failure evidence.

Source anchors: FileTransferCoordinator.cs:7421/7445 (wrapper/core),10664 (fallback
ownership),13640/13652 (apply/invalidate),24463 (route mutation);
SessionFileTransferService.RouteTransitionFallback.cs:2960 (Service caller);
SessionFileTransferRegularV4PressureScopeTransitionTests.cs:534/1300 (test/helper).
Current source SHA256 values, unchanged during this RCA:

- Coordinator:1E4E95095771C2BCB6130358BC945638B7E640519AA6CBB010D0F29BFE175A6F.
- Service caller:8E3B96E17A9423AACC57ACB6576D576D0E6D7D8F9AAE74725161EB66E3278CD1.
- Snapshot file and test file retain the13.3/work-in-progress hashes. The exact TRX
  and JIT asm retain the13.3 hashes; no historical artifact was rewritten.

### 14.2 Bounded fix plan for approval: sequential Core commit phases

Proposed production scope is CommitFileTransferRouteTransitionCore and two private
no-inline helpers in FileTransferCoordinator.cs. Keep the existing outer snapshot
wrapper, Service, transition/invalidation helpers, DTO representations and test
fixture behavior unchanged. This is a proposal, not an implemented or passing fix.

1. Extract the existing early-decision/validation/pressure-projection block through
   CloneCoordinatorStateGraph and the temporary transition projection into a private
   no-inline preparation helper. Return either the existing idempotent/rejected
   decision or the projected CoordinatorState reference for continuation. A private
   Try-style result with out parameters can express this without a new authority or
   public state-machine type. Do not manufacture an accepted receipt or intermediate
   published decision. Preserve check order and every existing rejection reason.
2. Keep CommitCore as the thin synchronous orchestrator. Only after preparation has
   returned, construct the same coordinator event and call TransitionToRoute once.
   No heavy preparation or completion helper may enclose this call. Do not keep a
   copied PersistentState/transition substate or completion-only structs in this
   orchestrator, and do not bundle those values into a new large by-value carrier.
3. After TransitionToRoute returns, call the second private no-inline completion
   helper for the existing route-result checks, commit/publication/disposition and
   retained-frame evidence, terminal reconstruction, checkpoint registration and
   final decision. Read the original transition substate from the original input
   state in that helper; do not reconstruct it from the cleared projected state.
   The outer wrapper still finalizes exactly once after Core returns.

Required semantic equivalence checks before accepting the extraction:

- Exact committed duplicate handling remains first and returns the original state
  and same terminal commands, including canonical drain/pressure duplicate rules.
- Retry-scope, drain/final proof, pending/predecessor pressure and exact installed
  pressure apply checks retain their order. No missing receipt becomes success.
- The graph clone remains before route mutation. Existing temporary clearing of
  CurrentOperation/PendingSupersession occurs only on that private projection; it
  is not a new bypass, source-state mutation or externally adopted intermediate state.
- All early and late rejection branches return the original input state. Verify
  original leg/history/epoch and authority evidence remain unchanged, not merely
  that the top-level rejected State reference matches.
- Route/owner/fallback/retry invalidation stays in the existing TransitionToRoute
  path. Preserve protocol4 regular/Tuna and protocol6 post-Tuna fallback invariants.
- Commit/publication generations still derive from original input counters;
  terminal operations still derive from the original current operation and retain
  exact drain, final proof, pressure receipt, disposition and retained-frame linkage.
- Inbound correlated/legacy bootstrap boundary checks, outbound/inbound checkpoint
  registration and failure reasons remain in their current order. No checkpoint,
  route publication or Service effect may escape a rejected completion.
- Snapshot normalization, identity gate, families, receipts, fingerprints and
  reconciliation remain unchanged: one final envelope, no per-phase finalization.

Validation after separate implementation approval:

1. Rebuild and run the unchanged exact Service regression in normal default Debug
   settings first. Require completion with all existing pressure/owner/single-effect
   assertions, not merely advancement or no crash. Preserve unique TRX/log artifacts.
2. Inspect one bounded method-specific disassembly capture to verify preparation
   returns before route mutation and completion starts afterward. Measure the new
   orchestrator/helper frames and actual active overlaps; do not promise a byte
   reduction or global stack bound before measuring. No optimization/tiering/stack
   policy change. Stop on a new failing edge rather than expanding the extraction.
3. Run focused existing Core controls: InstalledPressureRequiresExactApplyReceipt,
   TunaFallbackCommitsAndAppliesRetainedFrameOnce (both pressure variants),
   RetainedFrameAttachesAtCommitWithoutChangingAuthority,
   FallbackToTunaRequiresExactEffectivePhase2Receipt and
   InboundResponderAckedProbeUsesPairedReceiptWithoutPhase2Release (all prefixed
   FileTransferRouteTransition_). Include the correlated real-history successor
   control for inbound bootstrap/checkpoint and original-generation reconstruction.
   Add only missing assertions/cases for rejected source-graph immutability or
   phase-boundary structure; do not weaken or reseed existing authority fixtures.
4. Finish pending narrow snapshot semantics and directly affected prerequisite
   checks from13.2/13.3 without rerunning already unaffected lanes. At most one
   relevant broader contract lane after stabilization, no generic aggregate suite.
   Only then rerun sender-only joined once; stop at its first missing proof edge.
   Receiver-only, dual-stop and evidence extension remain separate blocked gates.

A test-wrapper-only edit addresses a288-byte helper, not the dominant962.23KiB Core
frame; the first fix therefore targets production phase overlap. No global struct
rewrite, fallback fast return, skipped invalidation, stack enlargement, Task.Run/new
thread, permanent JIT change, quarantine, snapshot suppression, queue/deadline tuning
or live behavior change. Production capability stays false. This RCA changed only
documentation/checkpoints: no source/tests/build/rerun, joined/full report, package,
GUI/preflight/live, staging/commit/push or new campaign/report allowance.

### 14.3 Approved commit split implemented; exact Service regression passes

The user approved14.2. Only the commit method and its two new private no-inline
helpers in FileTransferCoordinator.cs changed this turn. TryPrepareFileTransferRouteTransitionCommit
returns either the original early decision or the private projected state before the
thin CommitCore calls TransitionToRoute. CompleteFileTransferRouteTransitionCommit
then reconstructs terminal evidence from the original state and performs the existing
completion/checkpoint work. No new DTO, authority, receipt, intermediate publication,
Service change, invalidation shortcut, snapshot change or test/fixture edit.

Normalized source comparison against the pre-edit method verified three equal blocks:
preparation after accounting for Try-style returns and the projected-state assignment;
the route event/call unchanged; completion after accounting for its moved local
declarations. Check ordering/reasons, original-state rejections, graph clone and
original-counter/transition reconstruction remain intact by inspection. Supplemental
source-graph/structural test work remains pending because the semantic gate below
encountered a new stop before that work began.

The rebuilt normal Debug exact Service test now completes and passes all unchanged
assertions: Tuna V4 route, replaced runtime owner, no pending/installed/predecessor
pressure binding, exact local contract StaleRejected settlement, one transport
settlement and one of each expected coordinator route-projection effect. One no-build
method-specific diagnostic rerun also passes. These are two runs of one distinct
regression, not two distinct passing cases.

Retained MinOpts disassembly verifies sequential calls and fixed frames:

| Frame | Before bytes | After bytes |
| --- | ---: | ---: |
| CommitCore orchestrator | 985,328 | 53,552 |
| New preparation helper | n/a | 533,792 |
| New completion helper | n/a | 609,680 |

The route-mutation/invalidation call path no longer includes either helper frame.
Its selected fixed-reservation total from14.1 falls1,595,536 ->663,760 bytes, removing
931,776 bytes (909.94KiB) from that overlap. Existing Service/wrapper/transition/
invalidation frame sizes are unchanged. Neither helper calls TransitionToRoute or
the outer snapshot finalizer. Helpers still have substantial independent frames;
this is not a global stack bound or proof of all fallback paths.

New artifacts under bootstrap-correlation-implementation:

- bootstrap-commit-phase-exact.trx/log:1/1PASS, normal rebuilt Debug.
  TRX SHA256240E151D1D9ECB93B26DBE682DF80D929F920C7D5488BC88DC230DA46BCA19AD.
- bootstrap-commit-phase-jit.trx/log/asm:1/1PASS, same test, no-build diagnostic.
  ASM SHA256E9C81E78966DB91A695C802B552905C28B01EC3D2016AA9CD7CEA14A0E7510DA.
- bootstrap-commit-phase-semantics.trx:ABORTED, zero completed cases; see15.
  TRX SHA256A1233CA58831FC01009AB68023AE0B96EB3AD4D183DF29362449755B8FB1EA2B.
  This Core-only run produced no standalone operational log at the requested path.

Coordinator source SHA2567B0752454B8D451F8405DDE1BF63B83D6ECBA36B1771A03816829B3567BA6D34.
Snapshot/failing Service test source and three unrelated pins remain unchanged.
Normal/diagnostic/semantic TRX windows3.2365101+1.5900656+1.7942485=6.6208242s;
outer command elapsed31.3199444+2.3005166+2.516792=36.137253s (first includes build).
These overlapping measures are not additive active-work charges. JIT disassembly
settings were child-command-only; fresh shell has no JIT/tiering/stack override.

## 15. New focused-contract stop: correlated fallback preparation stack overflow

The14.2 focused semantic selection aborted in
CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames, with no
completed test cases. Current-source default Debug stack, caller to callee:
test -> CommitCorrelatedFallback -> CommitEmptyFileTransferRouteTransitionForTest ->
PrepareFileTransferRouteTransition -> PrepareFileTransferRouteTransitionCore ->
TryValidateFileTransferRouteTransitionPrepareRequest ->
TryValidateFileTransferFallbackProofBoundary (top reported crash frame).

This is a different path from the now-passing Service route-commit regression. The
newly split commit methods are not on the reported failing stack. No current frame
sizes or complete root cause for this path have yet been established; do not infer
them from the previous commit/snapshot failures, assume a specific cycle/peer, or
call the semantic selection passed. The saved TRX is the authoritative crash evidence.

Per the approved stop rule, no further runtime/test edits or test reruns followed.
Other selected semantic controls are not credited as passed; pending snapshot/
affected/broader checks, supplemental graph/phase tests and sender joined remain
unfinished. Receiver/dual/evidence stay blocked. Previous18PASS are historical and
were not rerun. Production capability remains false; HEAD/index unchanged and the
implementation is uncommitted. No package/GUI/preflight/live, full report, staging/
commit/push, subagent, new campaign/report allowance or test quarantine.

Recommended next step: bounded RCA/fix-plan review of this exact correlated fallback
preparation stack. Isolate the named current-source fixture and measure fixture,
prepare and proof-validator overlap before choosing a minimal phase boundary. Keep
all fallback proof/source/route/pressure currentness and snapshot authority checks
intact. Do not broaden14.2 into an unreviewed validator/fixture redesign, enlarge
stacks, change JIT/scheduling policy, clear state, skip the fixture or continue to
joined acceptance past this prerequisite.

### 15.1 RCA and authorized bounded preparation fix

The user requested RCA, a fix plan and implementation together. The exact isolated
current-source Debug fixture reproduces the15 stack with child-only method-specific
disassembly (bootstrap-fallback-prepare-rca.trx/asm), ABORTED/0completed. No optimization,
tiering, stack, scheduling, source or fixture change preceded this diagnostic.

Measured simultaneous fixed reservations, excluding runtime/reflection overhead and
saved registers/return addresses:

| Method | Bytes | KiB |
| --- | ---: | ---: |
| CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames | 816 | 0.80 |
| CommitCorrelatedFallback | 318,288 | 310.83 |
| CommitEmptyFileTransferRouteTransitionForTest | 122,960 | 120.08 |
| PrepareFileTransferRouteTransition | 44,288 | 43.25 |
| PrepareFileTransferRouteTransitionCore | 707,904 | 691.31 |
| TryValidateFileTransferRouteTransitionPrepareRequest | 216,544 | 211.47 |
| TryValidateFileTransferFallbackProofBoundary | 184,320 | 180.00 |
| Total | 1,595,120 | 1,557.73 |

The reported chain is acyclic. The top validator probes its fixed frame before
evaluating the correlated proof guard; its source does not recurse into preparation.
There is no measured OS stack reservation or captured fault instruction. The dominant
PrepareCore frame combines current/terminal idempotence, validation and new-operation
construction with large persistent/operation value copies, so construction temporaries
remain reserved while validation runs. The fixture also contributes material stack
usage; this is not evidence of a network failure, invalid peer proof or live/Release
crash, and does not establish which peer/cycle reaches the fault.

Smallest selected first fix: extract only the new-operation construction block after
successful request validation into a private no-inline helper in Coordinator. The
helper reads transition state from the same post-handoff-invalidation input state,
builds the same authority/generation/operation/pause command and returns the existing
Prepared decision. PrepareCore returns through this helper only after validators have
returned. Its existing handoff invalidation, current-operation validation with
allowCurrentOperation=true, committed/invalidated duplicate handling, pending
disposition rejection, all rejection reasons and outer single snapshot finalization
remain unchanged. No new DTO, intermediate authority, fixture edit or validator bypass.

This targets overlap from the largest production frame without redesigning duplicate
classification or changing the180KiB fallback validator. It is not a promised stack
bound: measure the extracted frames and require the exact test to complete. A fixture
split could also reduce overhead, but would not address the production construction
overlap selected here. No global representation rewrite, state clearing, larger stack,
JIT/thread workaround, quarantine, deadline/queue tuning or capability enablement.

Verification: compare moved source; rebuild/run the unchanged isolated correlated
history test normally first; use one bounded disassembly check; then finish the
interrupted narrow commit/prepare/negative/snapshot controls, with at most one relevant
broader lane after stabilization. Preserve current Service regression success and do
not rerun unrelated lanes. Sender joined remains gated; stop on a different first
failure edge before receiver/dual/evidence or further runtime changes.

The first extraction alone was insufficient: the exact rebuilt test and one diagnostic
still abort within the same uncompleted fallback-prepare request, now at
IsFallbackBootstrapProofCurrent. PrepareCore707,904->481,296 bytes; construction helper
374,672. Current-proof195,888 and exchange-currentness257,376-byte frames are retained
in the diagnostic (the latter is not on this crash stack and must not be added to its
measured total). No successful prepare or next route/authority decision is claimed.

Refined same-edge implementation plan: also move existing-operation/terminal decision
classification into a private no-inline Try helper, preserving every predicate,
return value and the duplicate path's allowCurrentOperation=true validation in place.
On the fresh-request path it returns before validation; the thin PrepareCore keeps
handoff invalidation first, then existing-decision handling, fresh validation, and
construction in the original order. Reuse the same post-invalidation state reference;
no new classification enum/authority DTO and no validator/fixture change. This
completes the measured preparation-frame separation rather than weakening the newly
reached proof guard. Require the exact test to pass; retain both failed artifacts.

### 15.2 Preparation phase separation implemented; next commit-proof stop reached

Implemented the refined15.1 plan only in FileTransferCoordinator.cs. PrepareCore
retains handoff invalidation, then calls no-inline existing-decision classification,
fresh validation, and no-inline operation construction sequentially. A normalized
comparison verifies original existing-decision predicates/results (apart from Try
return plumbing), fresh validation and construction blocks are unchanged. All
authority/receipt/route/snapshot rules and test fixtures remain unchanged. Existing
duplicate validation intentionally stays in the existing-decision helper; no global
stack guarantee for that path is claimed.

The first construction-only extraction lowered PrepareCore707,904->481,296 bytes but
still exhausted the same prepare-validation chain at correlated-proof currentness.
It was not credited as a fix or a new passed gate. After existing-decision separation,
the rebuilt normal Debug exact fixture passes its Prepared and Draining assertions
and reaches commit; one no-build diagnostic confirms that advancement. The full
fixture still ABORTS with no completed case, now on16's commit-proof chain. No further
runtime/test changes followed that different contract step.

Final measured PrepareCore49,552 bytes (48.39KiB), down658,352 bytes from the691.31KiB
baseline. Existing-decision helper446,448 bytes and construction helper374,672 bytes
execute sequentially around fresh validation. The old selected preparation-chain
total falls1,595,120->936,768 bytes before deeper proof callees/runtime overhead.
Disassembly shows classification -> fresh validation -> construction call order.
This proves removal of the measured fresh-prepare overlap, not complete acceptance.

Artifacts in bootstrap-correlation-implementation, all ABORTED/0completed:

- bootstrap-fallback-prepare-rca.trx/asm:isolated original failure;
  asm SHA256A0D73FAF1253D5AA4BB833F7D3D6D344A6F85218BFB256517B16B9F64876F363.
- bootstrap-fallback-prepare-exact.trx:first rebuilt construction-only attempt;
  bootstrap-fallback-prepare-jit.trx/asm:its diagnostic, same uncompleted preparation.
- bootstrap-fallback-prepare-separated.trx:rebuilt final split, reaches commit-proof
  overflow; SHA25640AA807BF0BC8AEE09F8301374DA486953E3073D26CB4A465A7840C81F4E35D7.
- bootstrap-fallback-prepare-separated-jit.trx/asm:final diagnostic, same later stop;
  asm SHA25615D3C5C754C6B80F097506E567A4186C44A9C856F6D676ED5CE5C4355ACAB908.

No standalone operational log was produced by these Core-only runs. Five TRX windows
total12.6470372s; five outer command windows61.6037961s, including two builds. These
overlap and are not additive active-work charges. No global JIT/tiering/stack setting
changed; disassembly variables were child-command-only and absent in a fresh shell.
Coordinator source SHA256CA5F78489B4BAC93BF9B4FD663E25CBE3F78AF04A244185AFE3B9AD1BB74FFA4.
Test sources remain D7C307746725693E7E857AE88AA764B950FD9D51E61E3CF49592153EAAB22DFD
(FileTransferCoordinatorTests.cs) and330C3318C408461295CCBDCA69B977F867114EDD74EA7CDAD1FF226AF0173A15
(FileTransferBootstrapExchangeTests.cs). Prior commit/snapshot changes and unrelated
pins preserved; git diff --check passes. No new complete passing test this turn.

## 16. Next prerequisite: correlated fallback commit-proof stack overflow

The latest exact default Debug fixture now reaches CommitEmptyFileTransferRouteTransitionForTest's
commit call, so its preceding Prepare/Drain status assertions have succeeded. It then
overflows before the correlated commit-proof check can return. Reported active chain:
test -> CommitCorrelatedFallback -> CommitEmptyFileTransferRouteTransitionForTest ->
CommitFileTransferRouteTransition/Core -> TryPrepareFileTransferRouteTransitionCommit ->
TryValidateFileTransferRouteTransitionCommitProof -> IsFallbackBootstrapProofCurrent ->
IsFallbackBootstrapExchangeCurrent (top frame).

The final retained disassembly includes this chain: test816, correlated helper318,288,
empty-transition helper122,960, commit wrapper48,432, CommitCore53,552, commit-preparation
helper533,792, commit-proof validator249,152, correlated-proof195,888, exchange-currentness
257,376 bytes. Selected fixed total1,780,256 bytes, excluding runtime/reflection/prologue
overhead. These are current measurements, not a complete new source-phase RCA or an
OS stack-size claim. The just-split PrepareCore/helpers are no longer on this stack.

Next bounded work should review this exact commit-proof overlap and choose the
smallest separation preserving exact currentness, pressure and original-state/receipt
semantics. Reuse the retained disassembly before considering another diagnostic.
Do not skip correlated exchange-currentness, manufacture proof, clear authority state,
increase stacks, change JIT/scheduling, or broaden into representation/fixture redesign.
No conclusion that fallback commit/retained apply/checkpoint/publication completed.

Per stop rule, remaining semantic/negative/snapshot/affected and broader lanes and
sender joined were not run. Receiver/dual/evidence remain blocked. Previous Service
pressure regression PASS and prior18PASS are historical, not newly rerun. Production
capabilityfalse; HEAD adfa9c049c10e233d6b449b980cad09b33166e94/index unchanged; no commit,
package/GUI/preflight/live, report, subagent or new campaign/report allowance.

### 16.1 Completed RCA: commit preparation overlaps the currentness validators

This RCA reused16's current-source default Debug TRX and final disassembly. Source
and artifact hashes match15.2; no rerun/build was needed. All nine measured frames
are on the reported stack, totaling1,780,256 bytes (about1.70MiB) before runtime,
reflection, saved-register and return-address overhead. Exchange-currentness probes
its fixed0x3ED60 (257,376-byte) frame before state reads. The call chain and inspected
validation code are acyclic. No fault instruction or OS stack reservation was measured.

The largest frame is TryPrepareFileTransferRouteTransitionCommit533,792 bytes. Its
terminal-duplicate materialization and pressure settlement/projection value copies
share a fixed MinOpts frame with the intervening drain/commit-proof checks. Thus
those copies remain reserved while commit-proof249,152, correlated-proof195,888 and
exchange-currentness257,376-byte validators execute. The two fixture helpers add
441,248 bytes, but are not the sole cause or a basis for changing their assertions.
Previous14's split removed this helper from the route-mutation stack; it did not
separate its own proof-validation stack. The15 preparation helpers are not active here.

The first missing edge is a successful commit-proof return. Source order establishes
that the caller has taken the non-idempotent path, passed its retry-scope guard and
drain validation, but has not reached pressure projection, route mutation, owner
issuance or completion. This does not establish a specific peer/cycle, completed
fallback commit, invalid exchange, or completed pressure retirement.

The failing exchange-currentness check enforces the actual coordinator-pinned
exchange, non-invalidated source hold and exact capture/receipt/checkpoint, agreement,
source scope, data session and leg checkpoint. The parent proof check also requires a
completed exchange/retained frame, exact Delivered bootstrap receipt, durable
checkpoint, source commit, target epoch and derived peer proof. These are mandatory
authority checks, not disposable diagnostics. No missed receipt, retry, race or NKN
connectivity cause is shown by the overflow.

Source anchors: Coordinator.cs:7534 (commit preparation),30243 (commit-proof validator);
FileTransferBootstrapExchange.cs:364/299 (proof/exchange currentness). The latter's
unchanged SHA256 is8B543FD18672371D0D6551EF9243812543A1815A6016FF390564E0ABCC120A7C.
The current artifacts already contain sufficient frame evidence for this RCA.

### 16.2 Bounded implementation plan for approval

Scope: only TryPrepareFileTransferRouteTransitionCommit and two private no-inline
helpers in Coordinator.cs. Keep existing outer CommitCore/completion and snapshot
wrappers, all validators, DTOs, Service/Infra and fixture behavior unchanged.

1. Extract the exact already-committed duplicate block into a no-inline Try helper.
   It returns a handled decision only when all existing authority, canonical drain,
   final-proof, pressure-duplicate and terminal-evidence checks match. Keep this
   before retry-scope/drain/final-proof validation. A mismatch continues through the
   original checks; it is not an accepted or rejected result fabricated by the helper.
2. Retain the retry-scope guard and drain/commit-proof validation in the now-thin
   TryPrepareFileTransferRouteTransitionCommit wrapper, with identical order,
   short-circuit behavior and reasons. Do not retain a copied PersistentState or
   transition substate in this wrapper. The duplicate helper must have returned
   before the currentness validators execute.
3. Extract the entire post-validation pressure/projected-state block into a second
   no-inline Try helper. Its inputs need only the original state reference and
   existing pressure apply receipt; preserve pending/predecessor rejection, expected
   settlement preparation, exact canonical receipt checks, accepted pressure commit,
   unexpected-receipt rejection and clone order. Read transition substate from the
   original input, not pressureCommit.State, when making the existing private
   projection. Return the same projected state/early decision through out parameters.
   This helper starts only after proof validation returns; it must not enclose or
   repeat commit-proof validation or call route mutation/snapshot finalization.

This separates duplicate handling -> validation -> pressure projection inside the
already bounded commit workflow. It is not a new state machine, authority token,
intermediate adopted state or public DTO. Default out values are ignored on paths
where they are not results; they must never be interpreted as successful receipts.
Every rejection and exact duplicate still returns the original input state. Existing
private CurrentOperation/PendingSupersession projection remains after validation and
graph clone, not a source-state clearing shortcut. Completion still reconstructs
original terminal/counter evidence; the outer wrapper finalizes exactly once.

Why this boundary: wrapping a validator in another helper leaves the533,792-byte
parent frame active and cannot remove this overlap. Local caching in the globally
used exchange validator, fixture-only splitting, or splitting only the pressure
suffix may reduce some overhead, but do not isolate both unrelated value-heavy
phases around this measured proof chain. No broad representation rewrite or claim
of a specific post-fix frame size is warranted before measurement.

Verification after approval:

1. Compare moved blocks; rebuild and run unchanged
   CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames with
   normal Debug/JIT/stack settings. Require all two-cycle/both-peer assertions and
   complete PASS, not merely a deeper crash. Preserve unique artifacts.
2. Capture at most one bounded method-specific diagnostic for the stabilized split,
   checking fresh-call sequencing and measured active frames. Retain any failed
   attempt, and distinguish an insufficient same-proof fix from a later contract
   step. Stop before expanding to a different first missing proof edge.
3. Run interrupted exact pressure receipt/canonical duplicate, fallback retained-frame,
   initial/fallback initiator/responder and preparation duplicate/rejection controls;
   add only missing targeted checks for unchanged source graph and no projection on
   failed commit proof. Independently cover incomplete/stale correlated proof at
   commit (existing prepare-only/legacy checks are not substitutes). Preserve missing
   or mismatched source-hold/receipt/checkpoint/DS/leg rejection and authority rules.
4. Finish pending narrow snapshot/affected checks; at most one relevant broader
   contract lane after stabilization. Only then sender joined once. Receiver/dual/
   evidence, production enablement, packaging and live runs remain separate gates.

This turn is RCA/plan only: no source/test changes, builds, test runs, commit, package,
GUI/preflight/live, report or new allowance. No stack increase, JIT/thread workaround,
guard suppression, fake proof, state clearing, quarantine or queue/deadline tuning.

### 16.3 Approved commit-proof phase split implemented; checkpoint stop exposed

Implemented16.2 only in Coordinator.cs: TryResolveDuplicateFileTransferRouteTransitionCommit
handles exact committed duplicates, the existing TryPrepare wrapper retains retry/
drain/proof checks, then TryProjectFileTransferRouteTransitionCommit performs the
unchanged pressure/clone/projection block. New helpers are private/no-inline and
return existing decisions/state references. No new DTO/authority, validator change,
Service/Infra/snapshot change or fixture/assertion edit.

Normalized source comparison verifies the duplicate, validation and projection blocks
are equal to the pre-edit source after accounting for duplicate-helper boolean return
plumbing and moving the original-state transition-substate read into projection.
Check ordering/reasons, exact receipts, original-state early decisions and private
clone semantics remain unchanged. No intermediate state is externally adopted;
existing completion and one outer snapshot finalization are untouched.

Rebuilt default Debug CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames
gets past commit proof and reaches fallback checkpoint registration. It then ABORTS
at17's recovery-invalidation stack. The one planned no-build method-specific diagnostic
reproduces that later failure. Both runs have zero completed cases, so no complete
regression PASS, two-cycle acceptance or new passing test is claimed.

MinOpts measurements confirm the intended separation:

| Method | Before bytes | After bytes |
| --- | ---: | ---: |
| TryPrepareFileTransferRouteTransitionCommit | 533,792 | 51,168 |
| Duplicate helper | n/a | 223,488 |
| Pressure/projection helper | n/a | 413,568 |

The wrapper's calls are duplicate -> proof validation -> projection; neither helper
encloses proof validation. Unchanged proof/currentness frames remain249,152/195,888/
257,376 bytes. Original named proof-chain overlap1,780,256->1,297,632 bytes, removing
482,624 bytes (471.31KiB). No global stack bound or all-branch guarantee is inferred.

Artifacts in bootstrap-correlation-implementation:

- bootstrap-commit-proof-phases-exact.trx:normal rebuilt Debug, ABORTED/0completed;
  SHA256ECC035C9CAC85D2084585530BB0C819C5BF56B6B5E1132A7D5C88326E818943E.
- bootstrap-commit-proof-phases-jit.trx/asm:diagnostic, same later abort;
  asm SHA256B768D1792441FCDDA7D8FBAD234328CDAE6F5A035B6C8AF1F05171FD846965BF.

Neither Core-only run produced the requested standalone operational log. TRX windows
3.6453216+1.8330782=5.4783998s; outer command windows32.7500648+2.5662238=35.3162886s,
first including build, overlapping/not double-added. No global JIT/tiering/stack policy
change; diagnostic variables absent from a fresh shell. Coordinator source SHA256
45509784A02ACEDC34D9A61C3F504C0EE4ECB71C673673FA667F1C99B5ED4297; exchange/test hashes
and unrelated pins unchanged. HEAD/index unchanged, productionfalse, uncommitted.

## 17. Next prerequisite: fallback checkpoint recovery-invalidation stack overflow

Latest default Debug exact stack: correlated history test -> CommitCorrelatedFallback
-> CommitEmptyFileTransferRouteTransitionForTest -> CommitFileTransferRouteTransition/Core
-> CompleteFileTransferRouteTransitionCommit -> MarkFallbackCheckpointRequested
-> InvalidateRecoveryAuthority (top). This is after proof validation and route mutation
have returned, during completion's checkpoint registration; no final committed
decision/checkpoint lease or external publication/adoption has been proven.

The retained diagnostic contains the current active frames: test816, correlated
helper318,288, empty-transition helper122,960, commit wrapper48,432, CommitCore53,552,
completion609,680, checkpoint registration200,480 and recovery invalidation278,080
bytes. Named fixed total1,632,288 bytes before runtime/reflection/prologue overhead.
These are initial evidence for the new chain, not a complete source-phase RCA or
an OS stack-size measurement. The16.3 helper frames are absent from this failure.

Next step: bounded RCA/fix-plan review of this checkpoint-completion overlap, reusing
the retained disassembly. Preserve checkpoint request/lease identity and generation,
recovery invalidation, retry-scope rules, original-state rejection, graph isolation
and final route/terminal/publication linkage. No skipping recovery invalidation,
fake checkpoint success, early external state adoption, stack/JIT/thread workaround,
fixture quarantine, representation rewrite or unrelated queue/deadline tuning.

Per16.2's stop rule, no further code/test edits or reruns followed except the planned
diagnostic capture. Focused semantic/negative/source-graph/snapshot/affected/broader
checks and sender joined remain pending; receiver/dual/evidence remain blocked.
Historical Service pressure PASS and18 earlierPASS are not newly rerun. No package,
GUI/preflight/live, report, staging/commit/push, subagent or new campaign/report allowance.

### 17.1 Checkpoint-completion RCA from retained current-source evidence

Rechecked the17 normal Debug TRX and MinOpts disassembly against the current source.
Their hashes and Coordinator SHA25645509784A02ACEDC34D9A61C3F504C0EE4ECB71C673673FA667F1C99B5ED4297
still match16.3. No rebuild or diagnostic rerun was needed for this review. The exact
regression remains ABORTED with zero completed cases, not a checkpoint/commit PASS.

The named acyclic stack reserves1,632,288 bytes: test816, correlated helper318,288,
empty-transition helper122,960, commit wrapper48,432, CommitCore53,552,
CompleteFileTransferRouteTransitionCommit609,680, event-overload checkpoint200,480,
and InvalidateRecoveryAuthority278,080. The last method probes0x43E40 bytes in its
prologue before reading recovery state. These fixed-frame measurements support
excessive overlapping Debug stack frames; they do not identify the exact faulting
instruction, OS reservation, complete runtime overhead or Release/live behavior.
There is no recursive cycle in this retained failure chain.

Completion currently combines three sequential responsibilities in one609,680-byte
frame: route commit/publication and terminal evidence materialization; checkpoint
boundary validation/registration; final decision construction. Its large value
locals/copies remain reserved while the checkpoint helper enters recovery cleanup.
The already-returned duplicate/proof/pressure helpers are not on this failure stack.
The first missing edge is checkpoint registration returning to completion, not proof
validation, final snapshot publication or Service adoption.

Source audit (pre-implementation Coordinator.cs anchors7719,7948,10759,10814,24703,
24900): completion passes no recovery child-binding request. The checkpoint helper
validates current fallback leg/request/epoch/pressure/retry scope, mutates the private
cloned target leg, increments its checkpoint generation, and issues a Pending lease
with exact owner/route/epoch/leg/checkpoint identity and the next lease generation.
Changed epoch/bridge/request/generation on this non-child path requires
InvalidateRecoveryAuthority before tail-yield checkpoint rebinding and decision return.
The stack proves entry into invalidation, not successful return of that lease.

Invalidation must remain: it retires the current recovery operation and matching
reserved retry attempt/initial-activation hold, invalidates the matching checkpoint
binding, clears pending frontier hold/recheck, repair settlement release and current
repair/transport decisions. Its unconditional cleanup matters even without a current
recovery operation. Skipping the call or adding a supposed no-op fast path is not an
acceptable fix. A checkpoint is also not permission to adopt a partially committed
state or publish a route externally.

### 17.2 Bounded completion-phase split for approval

Scope: only CompleteFileTransferRouteTransitionCommit and two private no-inline
helpers in Coordinator.cs, plus narrowly missing regression assertions/tests. Leave
MarkFallbackCheckpointRequested, InvalidateRecoveryAuthority, lease issuance,
tail-yield rebinding, inbound/currentness validators, commit preparation/Core/outer
snapshot wrapper, DTOs, Service/Infra and existing fixture behavior unchanged.

1. Extract the exact completion prefix, through terminal-operation installation,
   into TryMaterializeFileTransferRouteTransitionCommit. Preserve route rejection
   and missing-target checks, then both Tuna/fallback token-publication branches,
   disposition/retained-frame validation and terminal reconstruction in that order.
   Use the ORIGINAL input state for counters and transition substate, and the
   already-cloned routeDecision.State as the private working graph. Preserve all
   proof/drain/pressure receipts and reasons. Return only the materialized state
   reference and existing early decision via out parameters; rejection returns the
   original state, and success must not manufacture a public Committed decision.
2. Keep existing Complete as a thin sequential wrapper. After the materialization
   helper returns, retain that state reference separately from the checkpoint-updated
   committedState. Keep the exact inbound-bootstrap predicate/currentness check and
   outbound-or-inbound-bootstrap checkpoint block here, in their original order.
   Preserve request-id format, target epoch/bridge/frontier, state_refresh priority,
   Pending lease and exact request-id checks, and original-input-state rejection.
   Do not copy PersistentState, transition substate, terminal operation, publication
   request or disposition into this wrapper; those values would stay on its frame
   during registration. The existing checkpoint method must still perform all
   recovery invalidation/rebinding before returning.
3. Only after checkpoint registration returns (or the existing route branch does
   not require it), call BuildCompletedFileTransferRouteTransitionCommitDecision.
   This no-inline helper reads the exact terminal operation already installed in
   the materialized state from step1, not the original still-Draining operation and
   not a newly reconstructed authority. Reuse its route-commit token, drain command,
   disposition and retained-frame command for the original final decision suffix;
   use checkpoint-updated committedState for the returned state and the exact
   registration lease for RouteDecision.FallbackCheckpointLease. Preserve original
   authority, accepted/rejection flags, status and reason. Issue no tokens, perform
   no validation/registration twice, and finalize no snapshot in these helpers.

FileTransferCoordinatorState and FileTransferCoordinatorDecision are reference types;
carrying their existing references between these phases needs no new result DTO,
authority type or adopted intermediate state. Terminal values are read only in the
post-checkpoint helper, after the materialization frame has returned. Graph clone
order remains in the earlier projection helper; mutable target-leg updates must
never affect the original input graph. The outer commit wrapper still performs
exactly one final snapshot after the entire commit returns.

This two-helper boundary removes unrelated value-heavy work from the measured
checkpoint overlap. Merely extracting the checkpoint call leaves the609,680-byte
parent live; fixture-only splitting or changing globally used recovery machinery
does not address this local overlap as directly. No predicted post-fix frame size or
complete regression PASS is assumed. No stack increase, JIT/tiering/thread policy,
state clearing, fake receipt, guard bypass, representation rewrite or tuning.

Verification after approval:

1. Compare moved source blocks and state provenance. Rebuild and run the UNCHANGED
   CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames in
   normal Debug/JIT/stack settings, with unique retained artifacts. Require complete
   two-peer/two-cycle PASS, including disposition application, checkpoint leases and
   stale-frame rejection; entering a later method is only progress, not acceptance.
2. At most one bounded method-specific diagnostic for the stabilized split: verify
   materialization returns before checkpoint/invalidation, final decision work starts
   afterward, and measure the active chain. Retain failures. If the same registration
   edge still fails, classify the proposed split as insufficient, not fixed. Stop
   before expanding to a genuinely different first missing contract edge.
3. Run the exact TunaFallbackCommitsAndAppliesRetainedFrameOnce cases (pressure off/on)
   for lease identity/currentness, terminal/disposition/frame linkage and duplicate
   no-reissue; retain initial/fallback initiator/responder and exact pressure controls
   pending from16.2. Cover missing/mismatched inbound bootstrap boundary and the
   independently stale/incomplete commit-proof controls without weakening authority.
   Add only missing route-commit source-graph checks: successful and rejected commits
   leave the original leg/epoch/checkpoint fields, counters and persistent evidence
   unchanged; success produces the intended single state-changing transition
   envelope, while rejection preserves existing attempted-snapshot semantics.
   Existing FileTransferCoordinatorSnapshot_CheckpointAndTerminalTransitionsDoNotMutatePriorGraphs
   covers generic checkpoint mutation, not by itself this split's commit isolation.
4. Complete remaining narrow snapshot/affected checks and at most one relevant broader
   contract lane after stabilization, then sender joined once. Receiver/dual/evidence,
   production enablement, packaging and live acceptance remain separate gates.

This RCA/plan turn changes documentation/checkpoints only. No runtime/test edit, build,
test run, report, new allowance, staging/commit/push, package, GUI/preflight/live or
subagent. HEAD/index, productionfalse, source/test hashes and unrelated pins remain
unchanged. Implementation of17.2 awaits separate approval.

### 17.3 Approved two-helper attempt failed and was restored, not accepted

User approved17.2. Implemented its completion-only materialization/checkpoint/final
decision split with two private no-inline helpers. Normalized comparison confirmed
the materialization and checkpoint blocks were unchanged except helper return
plumbing; the final suffix reused the same terminal-record values. No validator,
checkpoint/recovery routine, DTO, Service/Infra, fixture or assertion was changed.

The rebuilt normal Debug exact regression ABORTED earlier than the baseline:
CommitCorrelatedFallback -> empty-transition helper -> commit/Core -> Complete ->
TryMaterializeFileTransferRouteTransitionCommit -> ApplyIssuedRoutePublicationAuthority
-> ApplyFallbackTailYieldSubstate. This is an introduced overlap regression within
the proposed completion split, NOT progress to a later prerequisite or a fixed17.
The one bounded diagnostic reproduced it. Candidate fixed frames: Complete94,624,
materialization521,584, publication271,152, tail-yield104,112 bytes, plus unchanged
named outer frames544,048; selected total1,535,520. Complete+materialization616,208
exceeds the original completion609,680 by6,528 bytes at publication. A smaller
checkpoint parent alone is insufficient when the earlier publication work still
overlaps the terminal-construction temporaries in the extracted helper.

A same-design refinement passed the three large readonly inputs to materialization
by in reference, without changing their values or checks. Its rebuilt normal Debug
run still ABORTED at the same publication stack. No second disassembly was captured;
the refined candidate's exact frame sizes are unmeasured. Both candidates compiled,
but neither reached the checkpoint return or completed any test case.

Restored only this turn's runtime edits, including the original mixed line endings.
Coordinator SHA256 is again EXACTLY45509784A02ACEDC34D9A61C3F504C0EE4ECB71C673673FA667F1C99B5ED4297.
A fresh rebuild of that restored source reproduces17's original checkpoint ->
InvalidateRecoveryAuthority abort. No test/source changes from this attempt remain;
all earlier uncommitted implementation is preserved. This confirms restoration,
not a passing regression or proof that the checkpoint overflow is fixed.

Retained artifacts in bootstrap-correlation-implementation (all four TRX files
ABORTED/0completed; no requested standalone operational log was produced):

- bootstrap-commit-completion-phases-exact.trx:
  F23B48A592CD7A02950FC4B06EBD110368BB91A558B2B09F0085D0869AC31CDE.
- bootstrap-commit-completion-phases-jit.trx/asm: one candidate diagnostic;
  asm922F89EEE05271364437D18EA438DA62890FA38D1BC5C2CCC6FD5C73F85B2CB6.
- bootstrap-commit-completion-in-exact.trx:
  C80DA05BE1BFD8417F551CA3A953F8EBF460AC1D3F7B9426BADC568DEAF47BC8.
- bootstrap-commit-completion-restored-baseline.trx: latest current-source result;
  B39BC5D04348143448B7A20AE464C2A9FBE231B37DEC29A066A734F1A0C575BA.
- bootstrap-commit-completion-rejected-candidate.patch: retained refined attempt,
  NOT an approved implementation;818D08601AD65A60D7D93A92F609A7394534A8C33137F10A362E8D8B3F907121.

Candidate source hashes: by-valueB359666159298FE264ED5904175C27185B9A50A0B671606F96B31A9ADFC77EBF;
readonly-referenceA6280897FB5258220075F5945BF3A34A80A6BD9F3C5F16BC48AE08BEC3FEF04A.
The retained patch describes the latter; removing its three in parameter/call modifiers
recovers the former's code. All failure evidence remains, without relabeling as PASS.

TRX windows3.5322755+1.88648+2.2676551+2.8207551=10.5071657s. Outer command windows
31.2238412+2.6283582+8.0114078+17.3148123=59.1784195s, including three builds; these
overlap the TRX/active work and are not added twice. Fresh shell has no diagnostic
JIT/tiering variables. No stack/thread/global JIT changes, quarantine or guard waiver.

### 17.4 Revised local split for approval; supersedes failed17.2

Keep17's authority, state-isolation and verification rules. Revise the local boundary
to THREE private no-inline helpers plus the existing thin completion wrapper:

1. Token/publication phase: move only the original route-rejection/target-scope checks
   and Tuna/fallback token-publication branches into the first Try helper. Use original
   input counters and the existing cloned routeDecision.State; return its private
   issued state and an existing early decision. This helper must return BEFORE any
   disposition/terminal-operation/PersistentState construction in the next phase.
   Leave ApplyIssuedRoutePublicationAuthority and all of its cleanup unchanged.
2. Terminal-evidence phase: a separate Try helper constructs and validates the exact
   disposition/retained-frame commands, then reconstructs and installs the terminal
   operation from the ORIGINAL input transition substate. Reuse the exact commit and
   publication token values stored on the private issued state by phase1, and the
   exact validated runtime owner from routeDecision.State; verify this provenance
   against the moved original locals. Do not reissue tokens or infer missing ones.
   Preserve final proof/drain/pressure receipts and original-state rejection. Return
   the materialized state reference, not an externally adoptable Committed decision.
3. Thin existing completion wrapper: phase1 returns, phase2 returns, then the UNCHANGED
   inbound-boundary/checkpoint registration block executes. Keep only existing state
   references/early decision and required checkpoint locals here, not persistent,
   publication-request, disposition or terminal-operation value copies. Preserve
   checkpoint scope, generation, lease checks, recovery invalidation and rebinding.
4. Final-decision helper: after registration returns, read the exact terminal values
   from phase2's materialized state and combine them with checkpoint-updated state
   and returned lease for the original suffix. One outer snapshot finalization only.

This adds one local phase boundary beyond17.2, not a new authority/DTO/state machine
or a change to globally used publication/checkpoint/recovery methods. It addresses
the demonstrated publication-plus-terminal overlap as well as the checkpoint parent.
Readonly input references may avoid new private-call copies, but are not themselves
the fix, as the failed refinement proves. No post-fix frame bound or PASS is assumed.

After approval, rebuild the unchanged exact two-peer/two-cycle regression first.
Require both publication materialization and checkpoint return and the FULL test PASS;
retain any failed attempt. One bounded method-specific diagnostic should check all
three private phases and active publication/checkpoint chains. Then17.2's exact
lease/duplicate/pressure, missing-negative/source-graph/snapshot and affected checks;
at most one broader lane only after stabilization, then sender joined once. Stop at
a genuinely different first contract edge. Receiver/dual/evidence, productionfalse,
packaging/live and report/campaign gates remain unchanged. Implementation of this
revised three-helper boundary needs approval; no further runtime edits were made.

### 17.5 Approved revised completion split implemented; reactivation stop exposed

Implemented17.4 only in Coordinator.cs. TryPublishFileTransferRouteTransitionCommit
performs the original route checks and token/publication branches, then returns.
TryMaterializeFileTransferRouteTransitionCommit constructs/validates disposition and
retained-frame evidence and installs the terminal operation, then returns. Existing
Complete retains the original inbound-boundary/checkpoint block. Only after that
returns does BuildCompletedFileTransferRouteTransitionCommitDecision build the final
result. All three new helpers are private/no-inline; new large readonly inputs use
in references. No new DTO, public authority, adopted intermediate state or extra
snapshot finalization. Publication/checkpoint/recovery validators and methods remain
unchanged, as do Service/Infra, fixtures and assertions.

Normalized source comparisons confirm publication, terminal, checkpoint and final
blocks are unchanged after accounting for helper return plumbing, relocation of the
fallback-proof local and reads of the SAME stored tokens/terminal values. Publication
application stores the issued token and its cleanup changes recovery/tail-yield
substates, not runtime owner or route-commit token. Terminal construction reads the
validated owner from routeDecision.State, issued tokens from private publishedState,
and original input counters/transition history remain authoritative. The final helper
reads the exact pre-checkpoint materialized terminal record and returns checkpoint-
updated state/lease. Reconstructing the preturn file in memory with the saved original
completion block yields the exact original SHA, proving all other source is unchanged.

Rebuilt normal Debug unchanged CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames
now completes the sender's first fallback commit, retained-frame/disposition application
and lease assertions, then accepts that checkpoint inside ReactivateCorrelatedFallback.
It subsequently ABORTS during outbound reactivation source authorization (18). The
one no-build method-specific diagnostic reproduces the later stop. Both have zero
completed cases: no full two-peer/two-cycle PASS, receiver fallback, completed Tuna
reactivation or later stale-frame/second-cycle acceptance is claimed. These are
Coordinator fixture observations, not Service adoption, live NKN or GUI acceptance.

Measured MinOpts frames (bytes):

| Method | Restored baseline | Revised split |
| --- | ---: | ---: |
| CompleteFileTransferRouteTransitionCommit | 609,680 | 91,424 |
| Token/publication helper | n/a | 97,408 |
| Terminal-evidence helper | n/a | 432,336 |
| Final-decision helper | n/a | 208,496 |

The new helpers execute sequentially; neither publication nor terminal construction
encloses checkpoint registration. Named checkpoint-chain total1,632,288->1,114,032,
removing518,256 bytes. Named publication chain is1,108,144 bytes. These are selected
fixed-frame sums, not OS stack reservations, complete runtime bounds or all-branch
safety guarantees. The17.3 failed candidates remain retained, not relabeled as passed.

Artifacts in bootstrap-correlation-implementation:

- bootstrap-commit-completion-three-phases-exact.trx: rebuilt default Debug, ABORTED/0;
  C801986717021D5F757F93694BD99F7D0C7BC885B6342203871CF7CC8DCCBDF1.
- bootstrap-commit-completion-three-phases-jit.trx/asm: same later failure, ABORTED/0;
  TRX833025990201140A80CB79D319E6F5046C60ED236DAB540775D0D6482DBEA499;
  asmB7B4161BF60E6975EFB94BB51D283F4026623A19DEC9ED16A422DB4298E42A6A.

Neither requested standalone operational log was produced. TRX windows
3.6642588+2.0173879=5.6816467s; outer commands31.0931194+2.7709883=33.8641077s including
the first build, overlapping/not double-added. No JIT/tiering/stack/thread policy
change; diagnostic variables absent in a fresh shell. Current Coordinator SHA256
EC6336FDD78EA807EC92B30587796C8F41B826F871F175FA51E98D4F30FBFBBA; all relevant
test/exchange/snapshot/Service hashes and unrelated three pins unchanged. HEAD/index
unchanged, productionfalse, uncommitted. No additional runtime/test edits after18
appeared. Remaining semantic/source-graph/negative/snapshot/affected/broader checks
and sender joined are pending; prior Service/18PASS are historical, not new results.

## 18. Next prerequisite: reactivation-source action-publication stack overflow

Latest current-source default Debug exact stack: correlated history test ->
ReactivateCorrelatedFallback -> CreateFallbackV6ToTunaRouteTransitionSetup ->
AuthorizeFallbackRuntimeUnlockRetryDispatchSource -> AuthorizeAction ->
AuthorizeActionCore -> ApplyIssuedRoutePublicationAuthority (top). This is after the
sender fallback commit/disposition/checkpoint acceptance returned, before outbound
reactivation setup completes. The changed commit-completion helpers are absent from
this active stack; unlike17.3, this is a later first missing contract step.

Retained diagnostic fixed frames: test816, reactivation helper21,008, setup71,488,
retry-dispatch-source helper236,784, AuthorizeAction182,208, AuthorizeActionCore761,840,
publication application271,152; named total1,545,296 bytes before runtime/reflection/
saved-register overhead. This is initial evidence, not a completed source-phase RCA
or exact fault-PC/OS stack-size/Release/live determination. Reuse this retained
disassembly rather than rerunning merely to collect these already-measured frames.

Next bounded step: review the action-authorization/publication overlap and prepare
its RCA/fix plan. Preserve the exact action request, admission and route-publication
authority, retry/source scope and recovery/tail-yield invalidation; evidence alone
cannot authorize reactivation. Do not patch shared AuthorizeAction/Core or publication
cleanup before identifying the smallest source boundary. No fixture quarantine,
fabricated source proof, guard bypass, state clearing, stack/JIT/thread workaround,
representation rewrite or unrelated timing/pressure/queue tuning.

Per17.4 stop rule, remaining focused controls, negative/source-graph/snapshot/affected/
broader lanes and sender joined did not run after this new edge. Receiver/dual/evidence,
production capability, packaging/GUI/preflight/live, reports and campaign gates remain
unchanged. No staging/commit/push, subagent or new campaign/report allowance.

### 18.1 Reactivation action-publication RCA

Reused18's current-source default Debug TRX and retained MinOpts disassembly. Their
hashes still match17.5, as does Coordinator SHA256EC6336FDD78EA807EC92B30587796C8F41B826F871F175FA51E98D4F30FBFBBA.
No build/test/diagnostic rerun was required. The exact test remains ABORTED/0completed;
the previously crossed sender checkpoint boundary is not a full two-cycle PASS.

The fixture's first reactivation-source action is RoutePublication, reason
fallback_retry_publication, on current post_tuna_fallback_v6/protocol6 with its exact
owner, live epoch, leg/runtime/terminal generations and RegularNkn target. The crash
precedes that call's Assert.True(publication.Accepted), tail-yield pairing, retry
grant/install and Tuna transition preparation. It is NOT a rejected retry request,
missing NKN receipt or evidence that the existing fallback publication can simply
be reused as a new reactivation grant. Source anchors: CoordinatorTests.cs:26605,
23731; BootstrapExchangeTests.cs:244; Coordinator.cs:8835/8858/9324/11476.

Retained acyclic fixed frames total1,545,296 bytes: test816, reactivation21,008,
setup71,488, retry-source fixture236,784, public action wrapper182,208, action
Core761,840 and publication application271,152. The top method reserves0x42330 bytes
with a stack probe before reading state. The dominant Core combines validation,
exact early/idempotent decision handling, all action kinds' token/decision issuance,
three authority applications and final decision construction in one frame. Its
validation/value temporaries remain reserved during publication cleanup even for
this RoutePublication request. This supports excessive overlapping Debug frames,
not recursion or an exact fault-PC/OS reservation/Release/live conclusion.

The current ordering has important semantics:

- Core first invalidates replaced data-session tail-yield scope, then replaced
  responder-probe handoff scope. All later checks/results use that returned state.
  An early rejection is not necessarily reference-identical to the original public
  input when these existing scope invalidations apply. Do not undo or postpone them.
- Validation preserves effective-owner precedence (different when a route-commit
  token is required), scope generations/route/protocol/checkpoint checks, required
  and optional admission/commit/publication tokens, callback/pending-probe/publication
  metadata, yield duplicate/conflict rules, fallback/recovery ownership and pressure
  retirement/replay checks. Exact current yield and pressure replay return accepted
  early decisions WITHOUT reissuing authority. They are not a fresh-issuance path.
- Every issued token/decision and its generation is computed from the same
  post-invalidation state BEFORE any application. ApplyIssuedFallbackAuthority,
  ApplyIssuedRepairOrTransportAuthority and ApplyIssuedRoutePublicationAuthority
  then run in that order, followed by the original ActionDecision construction.
- Publication application preserves exact reauthorization scope checks, recovery
  invalidation, publication generation, yield invalidation and retained-proof cleanup.
  The overflow is no reason to skip these or publish/adopt partial authority.
- The public wrapper snapshots only after Core returns, using its ORIGINAL input
  state/request/CoordinatorInstanceId; the empty-instance fast path remains distinct.

### 18.2 Bounded action-Core phase split for approval

Scope: AuthorizeActionCore plus two private no-inline helpers in Coordinator.cs, and
only missing directly affected regression controls. Keep public AuthorizeAction,
ActionDecision, all validators/issuers/application methods, DTOs, snapshots, commit
completion, Service/Infra and original correlated fixture/assertions unchanged.

1. Make existing Core a no-inline thin orchestrator. Retain the two initial scope
   invalidations in their exact order. Pass the resulting state and same readonly
   request to the helpers, with no copied PersistentState or validation/issuance
   locals retained here. Do not move snapshot construction into this path.
2. Extract the entire block from terminal rejection through the exact idempotent
   pressure-replay return into TryResolveFileTransferCoordinatorActionEarlyDecision.
   The helper returns true only when it supplies one of the EXISTING rejected or
   accepted-idempotent ActionDecision results via an out parameter. Preserve every
   check, precedence, short circuit, reason and result field in its original order.
   Return false only after validation falls through to fresh issuance; its default
   out value is then ignored, never interpreted as an accepted result. Core returns
   handled decisions unchanged. This helper must return before issuance begins.
3. Extract the complete issuedControlPlaneToken-through-final-ActionDecision suffix
   into IssueAndApplyFileTransferCoordinatorAction. It receives the same post-
   invalidation state and readonly request. Preserve ALL issuance expressions and
   generations before application, all supplied-token passthroughs, null decisions
   for other kinds, then the three existing application calls in their original
   order, and the exact final result. Do not issue/apply each kind one at a time,
   derive later counters from an already-applied state, suppress nominally empty
   application calls, reconstruct tokens or create a provisional accepted decision.

Both helpers use in FileTransferCoordinatorActionRequest to avoid new private-call
copies of this large readonly struct. Keep public/Core signatures unchanged. No new
authority/result DTO, action-specific fast path, global representation migration,
thread/stack/JIT policy or concurrency change. This is a phase boundary, not a bypass
of the shared authorization contract. The validation and issuance helpers are
siblings called sequentially; a value-heavy helper must not enclose the other.

Why this scope: extracting only the publication call leaves Core's761,840-byte frame
active. Changing the fixture or globally shared publication cleanup targets a broader
or different behavior. Splitting only validation out while retaining all issuance
temporaries in its caller risks the same summed-parent regression seen in17.3.
The thin Core plus two sibling helpers isolates both phases without changing the
existing public snapshot wrapper. Actual post-fix sizes and success require testing.

Verification after approval:

1. Compare moved blocks and state provenance, rebuild and run the UNCHANGED exact
   CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames under
   normal Debug/JIT/stack settings with unique artifacts. Require full both-peer/
   two-cycle PASS, not just publication return or a deeper crash. One bounded method-
   specific diagnostic should measure the new Core/helper/validation/application
   chains and confirm sequential phases. Preserve failed attempts; if a candidate
   introduces an earlier regression, reject/refine or restore only that candidate,
   not label it a newly completed prerequisite. Stop at a genuinely different edge.
2. After the exact path passes, run directly affected AuthorizeAction publication/
   route-commit/admission/callback/pending-probe and owner/scope rejection controls.
   Include current regular/fallback publication, strict Tuna4/protocol4 commit,
   forbidden Tuna6, missing/stale owner/token and terminal/route/epoch/leg mismatch.
   Check preserved decisions/reasons and generation linkage, not just Accepted.
3. Cover the shared suffix's control-plane/checkpoint/pressure/yield/repair/transport
   paths using existing focused fixtures; add only missing checks for exact yield
   and pressure-replay idempotence with no reissuance. Verify pre-validation scope
   invalidation on early results, unchanged original state graphs, supplied-token
   passthrough, and one outer snapshot versus the existing empty-instance fast path.
   No new authority on a diagnostic-only rejected callback. Preserve existing
   attempted-snapshot semantics and permitted scope-invalidation evidence.
4. Finish the already-pending narrow commit lease/duplicate/pressure/negative/source-
   graph/snapshot/affected controls. At most one relevant broader contract lane
   after stabilization, then sender joined once. Receiver/dual/evidence, production
   capability, packaging/GUI/preflight/live and reports/campaign remain separate gates.

This turn changes RCA/plan/checkpoint documentation only. No runtime/test edits,
builds, test runs, new diagnostics, report, staging/commit/push, package/live work or
subagent. HEAD/index/source/test hashes and unrelated pins unchanged, productionfalse.
Implementation of18.2 requires approval; no post-fix frame size or new PASS is claimed.

### 18.3 Approved action-Core phase split implemented; yield-snapshot stop exposed

Implemented18.2 only in Coordinator.cs. Existing AuthorizeActionCore is now no-inline
and retains the two scope invalidations before calling private/no-inline
TryResolveFileTransferCoordinatorActionEarlyDecision, then (only on fallthrough)
IssueAndApplyFileTransferCoordinatorAction. Both new helpers take the same readonly
request by in reference. The early helper returns the exact existing rejected or
accepted-idempotent decision; all fresh issuance/application remains in the second
helper. No new authority/DTO, provisional accepted result, validator/application
change, public wrapper/snapshot change, fixture/assertion edit or guard waiver.

Normalized source comparisons confirm both initial invalidations, all31 early
ActionDecision branches, and the complete issuance/application/final-result suffix
are equal to the original after helper return plumbing. The same post-invalidation
state, token/counter sources, supplied-token passthrough, application order and
result fields are preserved. Reconstructing the preturn file in memory using the
saved original Core block yields EXACTLYEC6336FDD78EA807EC92B30587796C8F41B826F871F175FA51E98D4F30FBFBBA,
proving no other source was changed this turn. Earlier completion fixes are intact.

Rebuilt normal Debug unchanged CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames
now returns from the fallback_retry_publication action and passes its publication
Accepted assertion. The following FallbackTailQuiescent event reaches snapshot
construction, then ABORTS at19. The one no-build method-specific diagnostic reproduces
this later stop. Both runs have zero completed cases; no full two-peer/two-cycle PASS,
returned quiescence decision, completed pairing/retry grant/install/reactivation,
Service adoption or live acceptance is claimed. The fixture's snapshot-preserving
path is used; the alternate data-session/reset fixture branch is not a workaround.

Measured MinOpts frames (bytes): Core761,840->34,048, early-decision helper700,864,
issuance/application helper67,040. The early helper returns before issuance begins;
its large frame is absent during publication application. Original named publication
chain1,545,296->884,544, removing660,752 bytes. Existing outer action wrapper182,208
and publication application271,152 are unchanged. This proves the local overlap
reduction and the observed action return, not global/all-branch stack safety.

Artifacts in bootstrap-correlation-implementation:

- bootstrap-action-core-phases-exact.trx: rebuilt default Debug, ABORTED/0completed;
  673A71D4398BA92CCA0AE6882A0C154F92ABD2D3BF0CF9575F5F47B90F52F73E.
- bootstrap-action-core-phases-jit.trx/asm: same later stop, ABORTED/0completed;
  TRX19FC4B5BAF97F0090C7AD9798471AB45E157E96BB8C640C3283B2976EB509E87;
  asm593B6D36BF97E80C395A8397EF241271F60F1FB4A3A194A079780C5AED5CD479.

Neither requested standalone operational log was produced. TRX windows
3.8471613+2.0492322=5.8963935s; outer commands34.6795632+2.7724499=37.4520131s including
the first build, overlapping/not double-added. No global JIT/tiering/stack/thread
change; diagnostic variables absent from a fresh shell. Current Coordinator SHA256
C2AFB6A45041B2EB1BD5C3432AA38C0C27363DA646898148FE8F9345F93AE51A; relevant exchange/
snapshot/test/Service hashes and unrelated three pins unchanged. HEAD/index unchanged,
productionfalse, uncommitted. No further runtime/test edits after19 appeared; remaining
action/shared-suffix/negative/source-graph/snapshot/affected/broader checks and sender
joined remain pending. Prior Service/18PASS are historical, not newly rerun results.

## 19. Next prerequisite: fallback-tail-quiescence snapshot stack overflow

Latest current-source default Debug exact stack: correlated history test ->
ReactivateCorrelatedFallback -> CreateFallbackV6ToTunaRouteTransitionSetup ->
AuthorizeFallbackRuntimeUnlockRetryDispatchSource -> Apply(FallbackTailQuiescent) ->
CreateTransitionSnapshot -> CreateOperationTransitionSnapshots -> CreateOperationDescriptors
-> AddFallbackTailYieldOperationDescriptors -> CreateFallbackTailYieldOperationDescriptor
-> CreateFallbackTailYieldSnapshot (top). This is AFTER the action publication returned,
but before the subsequent public Apply returns or its quiescence assertions execute.
The modified action-Core/helpers and prior completion helpers are absent from this
active chain. No pairing or retry grant/install is proven by entering this snapshot.

Retained diagnostic fixed frames: test816, reactivation21,008, setup71,488,
retry-source236,784, Apply288,992, state-to-state CreateTransitionSnapshot313,728,
operation transitions560, descriptor dispatcher128, fallback descriptor adder389,344,
fallback descriptor14,656, fallback snapshot263,696. Selected total1,601,200 bytes,
excluding runtime/reflection/prologue overhead. This is initial frame evidence, not
a complete source-phase RCA, exact fault-PC/OS reservation or Release/live finding.
The retained asm already contains these frames; do not rerun merely to obtain them.

Next bounded step: source RCA and fix-plan review of this snapshot descriptor path.
Preserve Coordinator state/receipt authority, complete operation descriptors,
generation/hash semantics, family/catalog contracts and full/compact evidence parity.
Do not skip snapshot construction, drop a current/terminal/retained descriptor, clear
state, fabricate receipts, alter the catalog, quarantine the fixture, enlarge stacks,
change JIT/thread policy or tune unrelated runtime behavior to pass the test.

Per18.2's stop rule, no downstream action/shared-suffix/commit/control/negative/source-
graph/snapshot/affected/broader or sender-joined run followed this new edge. Receiver/
dual/evidence, production enablement, packaging/GUI/preflight/live, report/campaign
and staging/commit/push gates remain unchanged. No subagent or new allowance.

### 19.1 RCA: fallback-family collector overlaps unrelated descriptor phases

Reused18.3's retained exact default Debug TRX and single method-specific diagnostic;
all three artifact hashes still match. Current CoordinatorC2AFB6A4... and snapshot
source1A250C1E... also match that run. No build, test or diagnostic rerun was needed.
The unchanged correlated test passed fallback_retry_publication's Accepted assertion,
then public Apply(FallbackTailQuiescent) entered its normal authoritative snapshot
path. The retained failure is before that Apply returns, not a returned rejection or
proof that quiescence, pairing, retry installation or reactivation completed.

Source inspection explains the measured overlap. AddFallbackTailYieldOperationDescriptors
has one389,344-byte MinOpts frame containing current/terminal/retained operation,
pending proof/reauthorization, and retry/hold/recheck/reservation temporaries. A
present yield operation calls the14,656-byte descriptor factory, which calls the
263,696-byte CreateFallbackTailYieldSnapshot. The collector's unrelated later-phase
storage remains allocated during that nested call. Including the already identified
outer frames gives the same selected1,601,200-byte chain in19. This is acyclic fixed-
frame pressure, not evidence of recursive descriptor traversal or a runtime authority
defect. The top snapshot method probes its fixed frame before reading state; retained
evidence does not identify the exact fault instruction, OS stack reservation, total
runtime overhead, Release behavior or live-network behavior.

The collector has three consecutive, read-only projection groups. Root operations
are current, terminal unless its authority matches current, then an exact retained
predecessor. Next are three pending peer proofs and pending/invalidated reauthorization.
Last are current-operation retry request/hold/recheck and owned current/consumed
reservations. FileTransferFallbackTailYieldSubstate is a sealed record REFERENCE,
whereas each FileTransferFallbackTailYieldOperation is a large readonly record struct.
Passing the already-read substate reference between phases need not introduce another
large value/DTO or change ownership. The same collector serves aggregate authoritative
snapshots and family-local attempted-decision projections, so both uses must survive.

The individual snapshot builder is not disposable overhead: it preserves source and
effective scope, proofs/release/reauthorization, reservation/install ownership, pending-
before-terminal recheck precedence, invalidation and receipt history. Its retained
recheck ownership deliberately uses immutable release scope, not live hold currentness.
Its current-reservation preference and consumed-install match are also intentional.
Leave that builder, all descriptor factories and ownership helpers unchanged. The
generic transition pipeline still needs complete before/after descriptors, exact keys,
ordinal ordering/equivalence, state generations and fingerprints; supplying an empty
or purportedly complete operation list is not a valid fix.

### 19.2 Bounded collector phase split proposed for approval

Change only the fallback-family collector orchestration in Coordinator.Snapshots.cs.
Keep AddFallbackTailYieldOperationDescriptors' name/signature and both callers. Make
it a thin no-inline wrapper that reads fallback once and calls three private/no-inline
sibling helpers sequentially, passing the same state, fallback substate reference and
builder. The helper boundaries are:

1. AddFallbackTailYieldRootOperationDescriptors: move the current, terminal and retained
   predecessor blocks unchanged. Preserve authority-ID inequality for terminal exclusion
   versus full decision equality for retained predecessor inclusion. A terminal and
   retained-predecessor descriptor may coexist under distinct roles; do not deduplicate.
2. AddFallbackTailYieldPendingProofAndReauthorizationDescriptors: move all three pending
   peer-proof calls, both reauthorization blocks and their existing AddPeerProof local
   function unchanged, including the optional peer proof and invalidation reason.
3. AddFallbackTailYieldRetryAndReservationDescriptors: move the complete CurrentOperation
   retry block unchanged, including request, hold, pending and nondefault terminal
   recheck, then current and consumed reservations. Preserve exact ownership predicates,
   generations, statuses, receipt validation (including rejected terminal rechecks),
   LastConsumingInstallReceipt provenance and all insertion order.

The wrapper must not retain large operation or child locals, precompute descriptors,
or nest one value-heavy phase around another. Reference-type substate parameters do
not need in/ref copying machinery. Existing factory signatures and operation arguments
stay unchanged. Preserve every role/key/field and both full/compact evidence semantics;
no new authority or result DTO, lazy enumeration, parallelism, snapshot suppression,
catalog/representation rewrite, state cleanup, fixture change or public Apply change.
No edits to action/completion fixes, Service, Infra, runtime validators or route policy.

Why this scope: wrapping only the top snapshot call leaves the389,344-byte collector
active. Splitting only the root block while leaving all other value-heavy work in its
caller also retains unrelated storage during the failing call. Three sibling projection
phases isolate the existing groups without changing snapshot content or widening into
the shared Apply/transition pipeline. Actual new frame sizes and successful execution
must be measured; this plan does not promise stack safety or a test PASS in advance.

Verification after approval:

1. Compare the moved blocks against this pinned baseline, allowing only helper plumbing.
   Rebuild and run the UNCHANGED exact CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames
   with normal Debug/JIT/stack settings and unique artifacts. Full both-peer/two-cycle
   PASS is required; returning from this snapshot alone is only a crossed prerequisite.
   One bounded method-specific diagnostic measures the wrapper/three helpers and the
   active descriptor/snapshot/outer chain, confirming phase frames do not overlap.
   Preserve failed candidates; an earlier failure introduced by the split is an
   insufficient fix, not a new completed edge. Stop at a genuinely different proof edge.
2. Once that path passes, run directly affected existing snapshot regressions:
   FallbackTailYield_CoordinatorAuthorsTypedProofAndPairingSnapshots (both orders),
   FallbackTailYield_FailedToQuiescentCarriedPeerProofAuthorsAtomicSnapshot,
   FallbackTailYield_FreshFailedYieldDoesNotInheritConsumedReservation,
   FallbackTailYield_RetainedRecheckSnapshotOwnershipUsesImmutableReleaseScope,
   FallbackTailYield_ReboundEpochRequestRestartsWithExactPendingDescriptor,
   FallbackTailYield_ReauthorizationInvalidationRequiresExactDescriptor, and
   SnapshotAttemptBuilders_UseOnlyFamilyLocalDescriptors. Preserve their assertions.
3. Review role coverage and add only missing focused checks for current/terminal
   exclusion, retained-predecessor coexistence, pending proof/reauthorization roles,
   retry/recheck/reservation ownership and ordering. Verify rejected/idempotent attempted
   projections, receipt validation, unchanged source graphs, generations/fingerprints
   and canonical full/compact envelope parity using in-memory fixtures, not a new
   retained-report invocation. Keep the existing aggregate/family-local source contract.
4. Complete previously pending narrow action/shared-suffix/commit/negative/source-graph/
   snapshot/affected controls; at most one relevant broader lane after stabilization,
   then sender joined once. Receiver/dual/evidence extension, production enablement,
   packaging/GUI/preflight/live and report/campaign remain separate unchanged gates.

This turn changes RCA/plan/checkpoint documentation only. Source/test/artifact hashes,
HEAD/index and unrelated pins are unchanged; production capability remainsfalse.
No build/test/diagnostic/report/package/live work, subagent, staging/commit/push or new
allowance. No new passing tests or completed correlated regression. Implementation
of19.2 requires approval; earlier accumulated code remains uncommitted.

### 19.3 Approved collector split implemented; retry-install snapshot stop exposed

Implemented19.2 only in the fallback-family collector in Coordinator.Snapshots.cs.
The existing collector is now a no-inline wrapper: read fallback once, then call the
three private/no-inline root, pending-proof/reauthorization and retry/reservation
helpers sequentially with the same state, substate reference and builder. No new DTO,
authority, in/ref value plumbing, caller/factory/ownership/snapshot-builder change,
state cleanup, fixture/assertion edit or public Apply/transition/catalog modification.

Normalized comparisons confirm all three moved blocks (including the local AddPeerProof
function) are unchanged apart from helper plumbing. Reconstructing the preturn file
in memory with the saved original collector gives EXACTLY
1A250C1E88CFADB10A6B0E9602489C81FC0AFD0C014C9BBCE5817CD9CE22602A,
proving no other snapshot source changed. Current snapshot source SHA256 is
C9AA5B4A26432B0C58FB04FF6AE816258D3BCC84D7DD7C285A0C4C6945C98418.
Existing role coexistence/exclusion, all predicates/order/fields/receipts and source
provenance are preserved; earlier action and completion fixes remain unchanged.

Rebuilt default Debug unchanged CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames
now returns from FallbackTailQuiescent Apply and passes its no-rejection/typed-decision
assertions. It also passes local/peer proof acceptance, paired-release/request ownership,
retry Granted, retained stage Accepted and install-receipt issuance Accepted/typed-
receipt assertions. The following CommitRuntimeUnlockRetryDispatchInstall ABORTS while
finalizing its snapshot at20. One no-build method-specific diagnostic reproduces this
later stop. Both runs have zero completed cases; no returned install commit, Installed
adoption, Tuna reactivation, receiver fallback, second-cycle/full both-peer PASS, Service
adoption or live/GUI acceptance is claimed. Snapshot-preserving fixture path remains.

Measured MinOpts fixed frames: collector389,344->69,424; root helper102,208,
pending-proof/reauthorization helper19,312; retry/reservation helper204,352. Disassembly
confirms three sequential calls from the wrapper. The root call overlaps only wrapper
plus root (171,632), not the other two phases, removing217,712 bytes versus the original
collector. The formerly failing named quiescence chain1,601,200->1,383,488; descriptor
14,656, snapshot263,696 and outer frames are unchanged. This establishes the local
overlap reduction and observed return, not global/all-branch stack safety.

Artifacts in bootstrap-correlation-implementation:

- bootstrap-fallback-descriptor-phases-exact.trx: rebuilt default Debug, ABORTED/0completed;
  8E7B8FC5E07EB04C8078C8C097ECACD9714A340B6A387C30BD08289335FD975B.
- bootstrap-fallback-descriptor-phases-jit.trx/asm: same later stop, ABORTED/0completed;
  TRX75D528797D3FA175BBD3546FE43DF5176EE51F06AB29F40954FCCF4061E31109;
  asm6E8CB90453E6542CEDCA2CFF244313AF6E01F440C4EC09F4C83401A4CEB2967F.

Neither requested standalone operational log was produced. TRX windows
4.0771333+2.358898=6.4360313s; outer commands32.216807+3.1316162=35.3484232s including
the first build, overlapping/not double-added. No global JIT/tiering/stack/thread
change; fresh shell diagnostic variables absent. Coordinator remains
C2AFB6A45041B2EB1BD5C3432AA38C0C27363DA646898148FE8F9345F93AE51A;
relevant exchange/test/Service hashes and three unrelated pins rechecked unchanged.
HEAD/index unchanged, productionfalse, uncommitted. No further runtime/test edits or
supplemental lanes after20 appeared;19.2's focused snapshot/ownership/role/parity and
earlier action/shared-suffix/commit/negative/source-graph/affected/broader checks remain
pending. No new complete test PASS; prior Service/18PASS are historical, not rerun results.

## 20. Next prerequisite: retry-install commit route-transition receipt snapshot overflow

Latest current-source default Debug exact stack: correlated history test ->
ReactivateCorrelatedFallback -> CreateFallbackV6ToTunaRouteTransitionSetup ->
StageAndInstallPersistentRetryLifecycle -> CommitRuntimeUnlockRetryDispatchInstall ->
its local Decision -> FinalizeRuntimeUnlockRetrySnapshot (lifecycle-transition overload)
-> CreateRuntimeUnlockRetryTransitionSnapshot -> CreateTransitionSnapshot (state-to-state)
-> CreateOperationTransitionSnapshots -> CreateOperationDescriptors ->
AddRouteTransitionOperationDescriptors -> CreateRouteTransitionOperationDescriptor ->
CreateRouteTransitionReceiptSnapshots (top). The modified fallback collector and its
helpers are absent from this active stack. The earlier quiescence/pairing/grant/stage/
issuance assertions returned before this call; the install-commit Accepted assertion
has NOT executed, so retained issuance is not proof of a committed installation.

Retained diagnostic fixed frames, in that order:816/21,008/71,488/15,136/456,656/
123,536/1,456/104,352/313,728/560/128/311,968/87,168/68,096; selected total1,576,096
bytes, excluding runtime/reflection/prologue overhead. The top method probes its fixed
frame before receipt reads. This is initial current-source evidence, not complete
source-phase RCA, exact fault-PC/OS reservation, Release/live finding or approval to
change runtime retry installation. The retained asm already has these frames; do not
rerun merely to obtain them.

Next bounded step: source RCA and fix-plan review of the retry-install finalization/
route-transition receipt snapshot path. Preserve Coordinator authority, exact install
receipt ownership, all descriptors/receipt history, state generations/fingerprints,
family/catalog contracts and full/compact parity. Do not bypass commit finalization,
drop historical descriptors, clear state, fabricate receipts, change the catalog,
quarantine the fixture, enlarge stacks, alter JIT/thread policy or tune runtime behavior.

Per19.2's stop rule, no downstream focused/supplemental/affected/broader or sender-joined
run followed this different edge. Receiver/dual/evidence extension, production enablement,
packaging/GUI/preflight/live, report/campaign and staging/commit/push gates unchanged.
No subagent or new allowance. Further implementation requires its own reviewed scope.

### 20.1 RCA: install calculation remains active during snapshot finalization

Reused19.3's current-source exact default Debug TRX and one retained method-specific
diagnostic. All three artifact hashes and CoordinatorC2AFB6A4.../SnapshotsC9AA5B4A...
still match; no rerun was needed. Quiescence, proof pairing, retry authorization,
staging and receipt issuance returned, but install commit has not returned or passed
its Accepted assertion. The stack alone does not identify which local Decision
branch was finalizing; do not infer a completed installation from this abort.

CommitRuntimeUnlockRetryDispatchInstall owns a456,656-byte MinOpts frame across
receipt/currentness validation, duplicate/predecessor decisions, repair-release
consumption, lifecycle construction, receive-recovery/fallback consumption and final
result construction. Every exit calls its local Decision while that frame remains
active. Decision has a123,536-byte frame spanning existing decision construction,
snapshot finalization and subsequent compensation eligibility/context preparation.
Retained disassembly confirms FinalizeRuntimeUnlockRetrySnapshot is called BEFORE
the finalized InstalledAttempt check and compensation-context call. Thus these two
frames contribute580,192 bytes during the failing snapshot traversal, even though
the install calculation has finished and compensation work has not started.

The remainder of the selected1,576,096-byte chain includes the shared retry/aggregate
snapshot wrappers,311,968-byte route-transition collector,87,168-byte descriptor and
68,096-byte receipt builder. The top receipt builder probes before receipt reads.
This is acyclic overlapping fixed-frame pressure; no recursive snapshot cycle, exact
fault-PC/OS reservation, full runtime-overhead measurement, Release or live-network
failure is established. The previously split fallback-family helpers are absent.

The snapshot path is doing required projection work. It builds family-local retry
attempt evidence and complete before/after aggregate descriptors, including retained
route-transition history. The receipt builder preserves ordered pause-bind, drain,
commit, pressure-settlement, retained-frame and disposition receipts with their
existing validation rules. Its presence does not make historical evidence optional.
Dropping that family or changing a supplied-operations completeness flag would bypass
the contract, not repair the install-to-snapshot phase boundary.

Source inspection also identifies the finalization contract that a split must retain:
all exits build the existing RuntimeUnlockRetryLifecycleTransitionDecision with
State = nextState ?? original state and the original optional predecessor settlement.
Snapshot finalization uses the ORIGINAL input state, public commit method name, exact
installResult command and receipt authority. Only status EXACTLY Accepted plus an
InstalledAttempt in the FINALIZED state can create CompensationContext. Its predecessor
comes from the ORIGINAL input state's InstalledAttempt, while fingerprints/generation/
boundary come from the FINALIZED state. The decision's Accepted property also includes
Idempotent, so substituting that property would incorrectly broaden this condition.

### 20.2 Bounded install-calculation/finalization split proposed for approval

Change only CommitRuntimeUnlockRetryDispatchInstall's orchestration in Coordinator.cs,
using the existing Core/result-finalization pattern already used by stage expiry.
Keep its current internal signature and callers; make it a thin no-inline wrapper
calling two private/no-inline sibling helpers sequentially:

1. CommitRuntimeUnlockRetryDispatchInstallCore receives the original state, readonly
   install result and observed time. Move the entire existing terminal-check-through-
   final-Decision body unchanged. Its local Decision now only constructs the SAME
   existing decision/status/reason/state/predecessor-settlement fields; it must not
   finalize snapshots or create compensation. Preserve every guard, short circuit,
   reason, branch order, exact receipt/generation/scope check, duplicate result,
   expiry, predecessor lineage, release consumption and state-construction expression.
   The helper returns before snapshot traversal begins.
2. FinalizeRuntimeUnlockRetryDispatchInstallDecision receives the original state,
   readonly install result and the Core's existing decision. Preserve the old local
   Decision's FinalizeRuntimeUnlockRetrySnapshot call, using that decision and the
   ORIGINAL input state, nameof(CommitRuntimeUnlockRetryDispatchInstall), exact boxed
   installResult and installResult.Receipt.Authority.AuthorityId. Then preserve the
   strict decision.Status != Accepted / finalized InstalledAttempt-null return guard,
   followed by the original CompensationContext assignment and context factory call.
   Context construction remains AFTER snapshot finalization, using finalized.State,
   exact input receipt and original state's InstalledAttempt. Return finalized or
   finalized-with-context unchanged. The existing compensation factory stays untouched.

Use in RuntimeUnlockRetryDispatchInstallResult for the two new private helper inputs,
and in RuntimeUnlockRetryLifecycleTransitionDecision for the finalization helper's
decision input, avoiding unnecessary new large-value call copies. Public signature
stays unchanged. Core's local result factory captures only its original state, not
the readonly install-result parameter. Keep the wrapper free of lifecycle/receipt/
predecessor projections and compensation checks; otherwise their storage would again
overlap Core execution. No helper callback may run finalization under the Core frame.

No new DTO, provisional external authority, service callback, worker dispatch or
intermediate state adoption. The internally returned decision is the same value the
old local function already constructed immediately before finalizing; callers still
receive only the finalized result. Finalize ALL statuses exactly once through the
existing snapshot entry point, retaining its empty-coordinator-instance behavior.
Do not special-case only success. Preserve explicit invalidated/replaced states on
rejection, and preserve original-state fallback on consumption failure rather than
leaking the unaccepted committedState. Do not retarget an embedded predecessor
settlement's State to the finalized outer state or change its status/report.

Why this scope: extracting just the top receipt builder leaves the456,656-byte install
frame active throughout all snapshot families. Splitting the route collector would
address a downstream projection while leaving this completed calculation active.
Moving finalization to a single large outer wrapper could instead add compensation
storage during Core's validation/consumption calls. A thin wrapper and two siblings
separate calculation from the existing finalization/compensation sequence without
changing either contract. No third compensation-phase split is proposed: keep that
existing sequence together unless new measured evidence requires a separately reviewed
change. Actual post-fix frame sizes and success require testing, not assumptions.

Do not change snapshot collectors/factories/receipt builders, compensation policy or
context representation, release/receipt validators, authority issuance, Service/Infra,
fixtures, route policy, global stack/JIT/thread settings or earlier approved fixes.

Verification after approval:

1. Compare the moved calculation, result fields, finalization inputs and compensation
   block against this pinned baseline. Rebuild and run the UNCHANGED exact
   CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames under
   normal Debug/JIT/stack settings with unique artifacts. Require full both-peer/
   two-cycle PASS; an install return alone is only a crossed prerequisite. One bounded
   method-specific diagnostic measures wrapper/Core/finalization and relevant nested
   calculation/snapshot/context frames, confirming sibling execution. Retain failed
   candidates; introduced earlier regressions are insufficient fixes, not progress.
   Stop at a genuinely different proof edge rather than widening implementation.
2. After that path passes, run directly affected existing focused install controls:
   RuntimeUnlockRetryLifecycle_IssuedReceiptRemainsStagedUntilExactInstall (both routes),
   RuntimeUnlockRetryLifecycle_AuthorizeStageInstallAndDuplicatesAreIdempotent,
   RuntimeUnlockRetryDispatchInstallReceipt_RequiresExactPredecessorLineage,
   RuntimeUnlockRetryLifecycle_ActiveSuccessorCommitAuthorsPredecessorSettlement,
   RuntimeUnlockRetryLifecycle_ClosureBackedPendingAttemptPreservesBothClosuresUntilInstall,
   RuntimeUnlockRetryDispatchInstall_ConsumesRepairSettlementReleaseExactlyOnce,
   FallbackTailYield_PairedReleaseIsReservedAndConsumedOnceAcrossRetrySuccessors and
   FallbackTailYield_StageRejectionExpiryAndStaleReceiptReleaseOnlyExactReservation.
3. Run focused compensation controls for current install/consumed release (both
   directions and predecessor variants), exact active predecessor, snapshot-disabled
   duplicate neutrality, and stale/advanced-install rejection from the existing
   RetryConfirmationCompensation tests. Add only missing assertions for context absent
   on Idempotent/Rejected/Expired, finalized generation/fingerprints/boundary, original
   predecessor provenance, unchanged input graph and exact predecessor-settlement
   fields. Verify original decision kind, one finalization, rejected/idempotent attempted
   evidence, full/compact canonical parity and unchanged generations using in-memory
   fixtures, without invoking another retained report or weakening existing assertions.
4. Complete pending19.2 snapshot/ownership/role/parity and earlier narrow action/shared-
   suffix/commit/negative/source-graph/affected controls. Use affected service install/
   confirmation gate tests where needed, at most one relevant broader lane after
   stabilization, then sender joined once. Receiver/dual/evidence extension, production
   enablement, packaging/GUI/preflight/live and report/campaign remain separate gates.

This turn changes RCA/plan/checkpoint documentation only. No runtime/test edits,
build/test/diagnostic rerun, report, package/live work, agent, staging/commit/push or
new allowance. Source/test/artifact hashes, HEAD/index and unrelated pins unchanged;
productionfalse. No new PASS or completed install/correlated regression is claimed.
Implementation of20.2 requires approval; accumulated code remains uncommitted.

### 20.3 Approved install phase split implemented; admission fingerprint stop exposed

Implemented20.2 only in Coordinator.cs. CommitRuntimeUnlockRetryDispatchInstall is now
a thin no-inline wrapper calling private/no-inline Core, then private/no-inline
FinalizeRuntimeUnlockRetryDispatchInstallDecision. New helper inputs use the approved
readonly references. Core's local Decision constructs only the existing result fields;
finalization receives that result after Core returns, then preserves the original
snapshot-before-compensation order. Strict status Accepted check, original before-state/
predecessor, finalized context state, exact command/authority and public method name
remain intact. No DTO, validator, consumption, compensation-factory, snapshot, Service/
Infra, fixture/assertion or earlier approved-fix change.

Normalized comparisons confirm the complete terminal-check-through-final-result
calculation, local result fields, and finalization/compensation block are unchanged
apart from helper plumbing (including status becoming decision.Status). Reconstructing
the preturn file in memory with the original method yields EXACTLY
C2AFB6A45041B2EB1BD5C3432AA38C0C27363DA646898148FE8F9345F93AE51A,
proving no other Coordinator source changed. Current Coordinator SHA256 is
9654539000688D4A0C69BF91AE80945681B74AB7A90B0E7B11B313934164D8AE.

Rebuilt default Debug unchanged CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames
now returns from retry-install commit and passes its status Accepted assertion. The
fixture returns the committed state/receipt and passes the typed LastConsumedReservation
release assertion. The subsequent RequestRuntimeUnlockAdmission -> Apply(RuntimeUnlockAdmission)
ABORTS during snapshot fingerprint construction at21. One no-build method-specific
diagnostic reproduces that later stop. Both runs have zero completed cases; no returned
admission/token, precommit probe, Tuna reactivation, receiver fallback, second-cycle/full
both-peer PASS, Service adoption or live acceptance is claimed. Source preservation
and a returned install do not replace pending dedicated compensation/negative/parity
checks. Original snapshot-preserving fixture path remains unchanged.

Measured MinOpts frames: entry456,656->2,400, Core455,648, Core's pure local Decision1,216,
finalization helper122,944 (old local finalizer123,536). Disassembly confirms Core returns
before finalization. During the formerly failing route-receipt snapshot, wrapper plus
finalizer125,344 replace the old580,192-byte install/local-finalizer overlap, removing
454,848 bytes. The named snapshot chain1,576,096->1,121,248. Existing compensation-context
factory208,800 is reached after finalization; no third phase split was added. These
are local fixed-frame comparisons and observed progress, not global/all-branch stack
safety or complete compensation validation.

Artifacts in bootstrap-correlation-implementation:

- bootstrap-retry-install-phases-exact.trx: rebuilt default Debug, ABORTED/0completed;
  AA1C786F57903AC52DA89E860F8DA98BF0DBD53BDE33C5396AA24364BD335A4F.
- bootstrap-retry-install-phases-jit.trx/asm: same later stop, ABORTED/0completed;
  TRXABB953E8DFB609BED0CD33CE1596A1713EFAD27DC66C32466E5F74C33F7E2877;
  asm212223AD4D0A7A6C974590A02B8687AEEB6C16A93CC27EBC69DA35383DCA6048.

Neither requested standalone operational log was produced. TRX windows
4.3386647+2.4611363=6.799801s; outer commands33.620901+3.1778566=36.7987576s including
the first build, overlapping/not double-added. No global JIT/tiering/stack/thread
change; fresh shell diagnostic variables absent. Snapshot source remains
C9AA5B4A26432B0C58FB04FF6AE816258D3BCC84D7DD7C285A0C4C6945C98418;
relevant exchange/test/compensation-test/Service hashes and three unrelated pins
rechecked unchanged. HEAD/index unchanged, productionfalse, uncommitted. No further
runtime/test edits or supplemental lanes after21 appeared.20.2's focused install/
compensation/negative/provenance/parity checks and previously pending snapshot/action/
shared-suffix/commit/affected/broader/sender-joined controls remain pending. No new
complete test PASS; prior Service/18PASS are historical, not newly rerun results.

## 21. Next prerequisite: admission fingerprint fallback-reservation snapshot overflow

Latest current-source default Debug exact stack: correlated history test ->
ReactivateCorrelatedFallback -> CreateFallbackV6ToTunaRouteTransitionSetup ->
RequestRuntimeUnlockAdmission -> Apply(RuntimeUnlockAdmission) -> CreateTransitionSnapshot
(state-to-state) -> CalculateStateFingerprint -> CreateOperationDescriptors ->
AddFallbackTailYieldOperationDescriptors -> AddFallbackTailYieldRetryAndReservationDescriptors
-> CreateFallbackTailYieldReservationDescriptor -> CreateFallbackTailYieldChildDescriptor
-> CreateFallbackTailYieldSnapshot (top). The new install wrapper/Core/finalizer are
absent from this active chain; installation and the consumed-release assertion already
returned. The admission call has not returned, so no admission-token authority is proven.

Retained diagnostic fixed frames in that order:816/21,008/71,488/41,296/288,992/313,728/
188,544/128/69,424/204,352/50,992/35,408/263,696; selected total1,549,872 bytes, excluding
runtime/reflection/prologue overhead. The top snapshot method probes before state reads.
This is initial current-source evidence, not complete source-phase RCA, exact fault-PC/
OS reservation, which fingerprint input was being projected, Release/live finding or
approval to change admission authority. Unlike19's root operation projection under
quiescence, this is reservation/child snapshot construction under admission fingerprinting.
Retained asm already includes these frames; do not rerun merely to obtain them.

Next bounded step: source RCA and fix-plan review of admission snapshot fingerprinting
and fallback reservation/child projection. Preserve exact Coordinator state authority,
admission checks, reservation/install receipt ownership, all descriptors/history,
canonical fingerprint inputs/generations, family/catalog contracts and full/compact
parity. Do not skip a fingerprint or snapshot, substitute stale/cached hashes, omit
retained reservation evidence, clear state, fabricate receipts, alter the catalog,
quarantine fixtures, enlarge stacks, change JIT/thread policy or tune unrelated runtime.

Per20.2's stop rule, no downstream focused/supplemental/affected/broader or sender-joined
run followed this different edge. Receiver/dual/evidence extension, production enablement,
packaging/GUI/preflight/live, report/campaign and staging/commit/push gates unchanged.
No subagent or new allowance. Further implementation requires its own reviewed scope.

### 21.1 RCA: fingerprint payload preparation overlaps descriptor collection

Reused20.3's retained exact default Debug TRX and single method-specific diagnostic;
all three artifact hashes and current Coordinator96545390.../SnapshotsC9AA5B4A...
match. No build/test/diagnostic rerun was needed. Install commit and the typed consumed-
release assertion returned; the following admission Apply has not returned. The stack
does not establish an admission-token result or which before/after fingerprint input
was being projected.

CalculateStateFingerprint has a188,544-byte MinOpts frame containing both descriptor
collection and subsequent responder-handoff/retry-lifecycle normalization and payload
construction. Source and retained disassembly agree that its FIRST substantive call
is CreateOperationDescriptors(state, includeResponderProbeHandoff: false); only after
that returns does it read handoff/lifecycle state and serialize the payload. The later
phase's value storage is therefore active throughout the failing descriptor traversal.
Under this call, the existing fallback collector/retry helper/reservation descriptor/
child descriptor/top snapshot frames are69,424/204,352/50,992/35,408/263,696. Including
the already identified outer frames gives the same selected1,549,872-byte chain in21.
This is acyclic fixed-frame overlap, not a recursive fingerprint call or evidence
that a reservation or admission guard is wrong. Top snapshot probes before state reads;
exact fault-PC/OS reservation, full runtime overhead and Release/live behavior remain
unproven. The recently modified install helpers are absent from this active stack.

The descriptor content is required. Reservation descriptors retain current versus
consumed roles, reservation/consumption identities, install generations and exact
reservation/install ownership validation. Child descriptors retain their typed receipt
and generation overrides. The top snapshot carries source/effective scopes and
immutable proof/release/history ownership. This RCA does not justify dropping or
changing any of those projections or revising19.2's fallback collector split.

Fingerprint semantics are also required: operation collection excludes full responder-
probe-handoff descriptors but the payload explicitly includes its existing eight-field
summary. Retry AttemptHistory is normalized from default to Empty in a copied lifecycle;
existing nondefault history is preserved. The anonymous payload contains35 top-level
properties, including the entire ordered operation descriptor array and authority state
outside route boundaries. SerializeCanonical uses camelCase, never ignores defaults,
sorts object properties ordinally and preserves array order. UTF-8 payload bytes are
SHA256-hashed and rendered lowercase hexadecimal. None of this is a cache or a route-
boundary-only hash. CreateTransitionSnapshot's existing stored-before-fingerprint
selection, stateApplied short circuits, fresh-state comparisons, chained after hash,
rejection guards and generation stamping remain outside the proposed change.

### 21.2 Bounded fingerprint collection/payload split proposed for approval

Change only CalculateStateFingerprint's orchestration in Coordinator.Snapshots.cs.
Keep its signature/callers, including CalculateSnapshotStateFingerprint. Add no-inline
to the existing method and make it a thin wrapper containing its unchanged
CreateOperationDescriptors(state, includeResponderProbeHandoff: false) call, followed
by a call to one new private/no-inline CalculateStateFingerprintFromDescriptors helper.
Pass the SAME state reference and returned ImmutableArray<CoordinatorOperationDescriptor>.
The immutable array is a small wrapper; no in/ref machinery or new DTO is needed.

Move the COMPLETE existing suffix, starting with var responderHandoff and ending with
the lowercase SHA256 return, into that helper unchanged. This includes lifecycle
normalization, all35 payload properties and their original inferred types, all eight
handoff-summary fields/defaults, Boundary creation, Operations = operations, exact
serializer options, UTF-8 conversion, hash and formatting. The existing descriptor
collector must return before the payload helper enters. Keep the wrapper free of
PersistentState reads, normalization and payload temporaries, so their fixed storage
cannot remain active during collection. Do not put descriptor collection back inside
the new helper, precompute state fields before it, or introduce lazy/parallel enumeration.

No descriptor reuse across states/calls, memoization, cached/stored hash substitution,
empty-operation shortcut, omission/deduplication/reordering of roles, new normalization,
null/default suppression, serialization/algorithm/version change or hash recomputation
policy change. Do not change CreateTransitionSnapshot, CalculateSnapshotFingerprint,
SerializeCanonical/options, any collector/factory/ownership predicate, admission
runtime logic, compensation context, Service/Infra, fixtures or prior approved fixes.
The helper is private implementation plumbing, not a new way to supply partial or
foreign-state descriptors as authoritative fingerprint inputs.

Why this scope: wrapping only the top snapshot leaves fingerprint preparation's
188,544-byte frame active. Splitting reservation/child builders changes more projection
boundaries while retaining unrelated payload work on their stack. The single-helper
split separates two already consecutive phases without changing fingerprint inputs
or broadening into shared admission/transition logic. Actual wrapper/helper sizes and
success must be measured; no numerical reduction or PASS is promised in advance.

Verification after approval:

1. Compare the moved suffix byte-for-byte/normalized only for extraction plumbing;
   check the unchanged collection call and same-state/array provenance. Rebuild and
   run the UNCHANGED exact CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames
   with normal Debug/JIT/stack settings and unique artifacts. Full both-peer/two-cycle
   PASS is required; returning admission alone is only a crossed prerequisite. One
   bounded method-specific diagnostic measures wrapper/helper and active collection/
   payload/snapshot chains and confirms nonoverlapping phases. Retain failed candidates;
   introduced earlier regressions are insufficient fixes. Stop at a different proof edge.
2. After that path passes, run the directly affected snapshot tests:
   AcceptedMutation_AdvancesGenerationOnceAndProducesStablePayload,
   IdempotentBoundary_RetainsGeneration,
   AggregateFingerprint_AdvancesForAuthorityStateOutsideRouteBoundary,
   RejectedDecision_CannotMutateSnapshotState,
   CoordinatorInstanceIdentity_CannotChangeAcrossSnapshotBoundary,
   CanonicalFingerprint_IsIndependentOfObjectPropertyInsertionOrder and
   RoutePublicationSnapshot_ContainsExactCommandAndReceiptFingerprint.
   These existing tests are necessary controls, not by themselves proof of all state-
   fingerprint field parity.
3. Add only missing focused state-fingerprint checks for default versus empty retry
   history equivalence, preservation of nonempty history, admission/authority changes
   outside route boundaries, retained reservation/receipt sensitivity and existing
   responder summary coverage. Verify deterministic same-state results and untouched
   input graphs. Preserve canonical byte/hash parity on deterministic pre-change
   controls; do not derive both expectations through the new helper or rewrite expected
   hashes to accommodate drift. Keep all existing assertions and full/compact envelope
   semantics; no new retained-report invocation or catalog/representation rewrite.
4. Complete pending20.2 install/compensation/provenance and19.2 snapshot/ownership/role/
   parity controls, then earlier narrow action/shared-suffix/commit/affected checks.
   At most one relevant broader contract lane after stabilization, then sender joined
   once. Receiver/dual/evidence extension, production enablement, packaging/GUI/preflight/
   live and report/campaign remain separate unchanged gates. Do not run unrelated lanes
   merely because fingerprinting is shared.

This turn changes RCA/plan/checkpoint documentation only. Runtime/test/source/artifact
hashes, HEAD/index and unrelated pins unchanged; productionfalse. No build/test/diagnostic
rerun, report, package/live work, agent, staging/commit/push or new allowance. No new
PASS, returned admission or completed correlated regression. Implementation of21.2
requires approval; accumulated code remains uncommitted.

### 21.3 Fingerprint split implemented; correlated history now passes

Implemented21.2 only in CalculateStateFingerprint: no-inline thin wrapper retains
the exact descriptor collection call with includeResponderProbeHandoff:false, then
calls private/no-inline CalculateStateFingerprintFromDescriptors with the same state
and immutable array. The complete normalization/payload/serialization/hash suffix is
byte-for-byte unchanged after extraction. Same-state/array provenance comparison
passes. Reconstructing the original method in memory yields the exact preturn snapshot
source SHA256C9AA5B4A26432B0C58FB04FF6AE816258D3BCC84D7DD7C285A0C4C6945C98418,
proving no other snapshot source changed. Current snapshot source SHA256 is
3C5456401DCF888DB28CAB7BD575ACAFB4A8E6D98FDA3B7AA1FDBF29694C0B10.
No field/order/default/serializer/hash-policy, descriptor/authority, admission,
compensation, Service/Infra, fixture/assertion or earlier-fix change.

Rebuilt normal Debug unchanged CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames
PASSES in full. The one no-build method-specific diagnostic also PASSES the same test.
Both peers reactivate to live epoch3; old-cycle frames are rejected with identical
state and no command; the second exchange uses a new sender request ID despite the
same transport epoch; both final fallback commits reach live epoch4. This is the
first complete current-source result for this exact regression, not merely a deeper
stop. It remains a deterministic Coordinator-history result, not Service adoption,
NKN production enablement, GUI/live acceptance or completed overall validation.

Measured MinOpts fingerprint frame188,544->64 bytes. Payload helper188,528 enters
only AFTER CreateOperationDescriptors returns, as confirmed by disassembly. The
formerly failing admission reservation-snapshot chain1,549,872->1,361,392 removes
188,480 bytes of overlap. The payload helper is absent during descriptor construction;
no serialization content is removed. These are selected fixed-frame comparisons,
not a global/all-branch stack guarantee.

The seven existing focused snapshot tests listed in21.2 step2 also PASS under normal
Debug. Thus8 DISTINCT tests pass this turn; the diagnostic repeats the correlated
test and is not a ninth distinct test. While resuming the existing19.2/20.2 focused
controls to assess remaining coverage, their fresh test host ABORTS at22 during
RuntimeUnlockRetryConfirmation_RejectsStaleOrAdvancedInstall fixture startup, with
zero completed cases in that invocation. This is separate from the passing correlated
and snapshot runs. No new code/test edits, added parity fixtures, warming/reordering
workaround, isolated rerun, second diagnostic, broader lane or sender-joined run followed.
Missing state-fingerprint field/normalization/canonical byte-parity controls and all
unfinished prior controls remain pending; source equivalence alone is not their PASS.

Artifacts in bootstrap-correlation-implementation:

- bootstrap-fingerprint-phases-exact.trx: rebuilt Debug,1PASS/0failed/0skipped;
  0118CED6A93C86F7F04CB1B30F817360D4CB59D629E2821775914B9C2904BC73.
- bootstrap-fingerprint-phases-jit.trx/asm:1PASS for the same regression;
  TRXF6D91183B9581B1541A94350CF0D604E19EC3C6767D986A3A7315FEFB1270582;
  asm034406A12461D1A8A70D1F689189D8E71C826372911C24371222F080A02D230B.
- bootstrap-fingerprint-focused-snapshots.trx:7PASS/0failed/0skipped;
  6189269B8C33C6C574975DFF5CF750B75001FD71DE80AC21D07EDC0ADBAEA9B1.
- bootstrap-fingerprint-prerequisite-controls.trx:ABORTED/0completed;
  2DA2003599F95011296CE62DC6B330A641585BD41CC84DE9CAD539060B30D5E5.

Neither requested standalone exact/diagnostic log was produced. TRX windows
5.2961414+3.7778099+1.290435+1.0541218=11.4185081s; outer commands
34.0725856+4.5484701+2.0600444+1.7890296=42.4701297s including first build,
overlapping/not double-added. No global JIT/tiering/stack/thread change; fresh shell
diagnostic variables absent. Coordinator9654539000688D4A0C69BF91AE80945681B74AB7A90B0E7B11B313934164D8AE,
relevant exchange/test/compensation-test/Service hashes and three unrelated pins unchanged.
HEAD/index unchanged, productionfalse, uncommitted. Prior Service/18PASS remain
historical; this turn's8 distinct PASSes are recorded separately.

## 22. Next prerequisite: compensation-control fixture startup stack overflow

Latest current-source normal Debug focused-control invocation aborts with stack:
RuntimeUnlockRetryConfirmation_RejectsStaleOrAdvancedInstall -> CreateRetryConfirmationFixture
-> ActiveFallbackStateWithoutCheckpoint -> StartRegular -> Apply -> ApplyCore ->
StartTransfer -> InvalidateFallbackOwnership -> InvalidateRecoveryAuthority ->
FileTransferCoordinatorPersistentState.get_FileTransferReceiveRecovery ->
FileTransferReceiveRecoverySubstate.get_Empty -> FileTransferReceiveRecoverySubstate..cctor
(top). Source confirms fixture construction precedes that theory's mutation switch
and compensation call. Fingerprinting and the newly split method/helper are absent
from this active stack. No completed compensation assertion is established, and the
specific theory argument is not identified by the retained stack.

This is initial evidence from the latest focused-control run, not a complete RCA,
measured new frame chain, identified fault instruction, proven static-initializer bug,
Release/live finding or regression attribution. The single diagnostic allowed for21.2
was used on the correlated regression and does not measure this new startup chain.
Do not borrow that passing run's frame sizes as a complete explanation of this abort.

Next bounded step: isolate and review this exact compensation-control startup path
under normal Debug in a fresh host, then prepare its RCA and fix plan, using a bounded
method-specific diagnostic if needed. Preserve all mutation variants, rejected/current
state neutrality, exact receipt/predecessor ownership, compensation context checks and
original fixture authority history. Do not prewarm static state, reorder/quarantine
tests, clear history, fabricate authority, enlarge stacks or alter JIT/thread policy
to bypass the failure. Do not change runtime compensation or initialization solely
because the stack ends in a static constructor.

The correlated-history PASS remains valid but does not waive22 or missing21.2 parity/
earlier focused controls. Broader contract, sender joined, receiver/dual/evidence,
production enablement, packaging/GUI/preflight/live, report/campaign and commit/push
gates remain unchanged. No further runtime/test change is approved by this new edge.

### 22.1 Isolated startup RCA: theory body overlaps fixture construction

The exact RuntimeUnlockRetryConfirmation_RejectsStaleOrAdvancedInstall theory was
run alone in a fresh normal Debug host, --no-build/--no-restore, against the same
source/binaries as21.3. It ABORTS with zero completed cases and the same22 stack.
One child-process-only, method-specific JIT diagnostic repeats that abort. Therefore
the surrounding19.2/20.2 test group is not required to reproduce this startup failure.
Neither run identifies the mutation argument reached, completes compensation, or
establishes a failing compensation assertion. No warmup/reordering was used.

The retained MinOpts prologues reserve these bytes along the active startup chain:

| Method | Fixed reservation, bytes |
| --- | ---: |
| RuntimeUnlockRetryConfirmation_RejectsStaleOrAdvancedInstall | 481,376 |
| CreateRetryConfirmationFixture | 125,552 |
| ActiveFallbackStateWithoutCheckpoint | 608 |
| StartRegular | 22,032 |
| Apply | 288,992 |
| ApplyCore | 78,448 |
| StartTransfer | 228,880 |
| InvalidateFallbackOwnership | 1,168 |
| InvalidateRecoveryAuthority | 278,080 |
| PersistentState.get_FileTransferReceiveRecovery | 56 |
| FileTransferReceiveRecoverySubstate.get_Empty | 40 |
| Selected measured subtotal | 1,505,232 |

This subtotal excludes pushes/return addresses, test-host/runtime frames and the
static constructor. The diagnostic file ends at a partial assembly-listing header
after get_Empty; it does NOT contain a measurable .cctor prologue. Do not claim a
complete chain, exact fault instruction, OS stack reservation, recursive initializer,
initializer-specific defect, Release/live failure or regression attribution.

Source and disassembly agree: the theory reserves its481,376-byte frame before its
first substantive call, CreateRetryConfirmationFixture(false). That frame also holds
the later mutation/alias/dispatch/compensation/assertion temporaries, none of which
are needed while the fixture is being constructed. The fixture has not returned at
the abort. Its StartRegular invokes Apply -> ApplyCore -> StartTransfer; fallback
ownership invalidation calls recovery invalidation, whose first recovery-state read
falls through the nullable backing reference to Empty. Empty is a new default record;
the top-of-stack constructor is the exhaustion observation, not proof that its
initialization semantics are wrong. Fingerprint collection/payload helpers are absent.

The bounded, evidenced cause to address first is unnecessary overlap of the large
test-body frame with fixture startup. Removing that overlap is a candidate fix for
this reproducible first edge, not yet proof that all fixture or compensation paths
will complete. The later helper may itself expose a distinct edge, which must be
reported separately rather than fixed speculatively in this scope.

Artifacts in bootstrap-correlation-implementation:

- bootstrap-compensation-startup-isolated.trx: ABORTED/0completed, TRX summary Failed;
  SHA2561D7D1E777A8CACF1AAD619C9A6755F499016908413316E23A4EE55D803BA6472.
  TRX22:28:31.7646007-22:28:32.8192095 Europe/Berlin,1.0546088s;
  outer command1.8370904s, exit1.
- bootstrap-compensation-startup-jit.trx: ABORTED/0completed, TRX summary Failed;
  SHA256294CDE4018A8FA0A2E904A684CD47A417B8EB6C925F0DDB954A9F48D3D32F473.
  TRX22:29:06.2015540-22:29:07.2508818 Europe/Berlin,1.0493278s;
  outer elapsed value was not retained in the available closeout output.
- bootstrap-compensation-startup-jit.asm: partial at the next method header;
  SHA2568B2630D0EA3AE8333ADE051388EC84F59E7A0C34F092A2083BB69AFC602A4B6D.

Combined TRX windows2.1039366s, overlapping the root work bound, not double-added.
No standalone operational log was requested. No build or source/test changes; source
Coordinator9654539000688D4A0C69BF91AE80945681B74AB7A90B0E7B11B313934164D8AE,
Snapshots3C5456401DCF888DB28CAB7BD575ACAFB4A8E6D98FDA3B7AA1FDBF29694C0B10,
compensation testD68F51701C17099868C24150B75C3FEF49CA6013DB128FBD67D6FCEE4BFA9DE2.

### 22.2 Bounded test-phase split plan for approval

Scope is only RuntimeUnlockRetryConfirmation_RejectsStaleOrAdvancedInstall in
FileTransferCoordinatorTests.RetryConfirmationCompensation.cs and one private helper.
Keep its public test signature, Theory/InlineData/Trait metadata and all ten cases.
Make the public entry a thin no-inline wrapper retaining the exact existing
CreateRetryConfirmationFixture(false) call. Only AFTER that call returns, pass the
same RetryConfirmationFixture reference and mutation string to a private static
no-inline helper. Move the complete suffix beginning with var state = fixture.Commit.State
through the final persistent-state equality assertion unchanged into that helper.
Do not move any fixture operation or mutation across this boundary.

Preserve every mutation, real alias/send/settlement call, acceptance assertion,
receipt/predecessor/context check, terminal/data/checkpoint/generation ABA condition,
and rejected-result/state-reference/persistent-state neutrality assertion. Keep the
shared fixture, its defaults (including snapshots:true), original authority history,
snapshot capture/continuity checks and nested Install helper unchanged. No runtime,
initialization, representation, compensation, snapshot, Service or Infra edits.
No static prewarming, reordered/quarantined tests, fabricated receipts, omitted
descriptors, state clearing, relaxed guards or stack/JIT/tiering/thread-policy change.

Verification after explicit implementation approval:

1. Compare the extracted suffix byte-for-byte, and reconstruct the original source
   in memory to its22.1 compensation-test SHA. Verify no other fixture/test/runtime
   source changed and all ten InlineData values remain. Only wrapper/helper plumbing
   and the no-inline annotations are new.
2. Rebuild normal Debug and run this exact theory alone in a fresh host. Require all
   ten cases PASS with original assertions; startup-only progress is insufficient.
   Use at most one bounded method-specific no-build diagnostic to measure the wrapper
   and helper and verify the helper is entered after fixture return. The measured
   startup subtotal should replace the old481,376-byte theory reservation with the
   wrapper reservation;1,023,856 plus that new frame is the selected comparison, not
   a complete-stack or all-branch guarantee. Do not invent a before/after .cctor size.
3. On any different first edge, retain the fresh artifact and stop for its bounded
   RCA/review. An earlier regression means this candidate is insufficient. Do not
   modify another test, shared fixture or runtime method under this approval.
4. Once all ten pass and the phase separation is verified, resume only the unfinished
   relevant compensation/fingerprint controls from21.2/19.2/20.2. The existing eight
   distinct21.3 passes remain valid; diagnostic repeats add no distinct coverage.
   Missing parity controls remain pending. At most one broader relevant contract
   lane after stabilization, then sender joined; all other gates remain unchanged.

This turn is isolation/RCA/plan/checkpoint documentation only; implementation22.2
requires approval. No new PASS, completed compensation assertion or overall validation
claim. HEAD/index unchanged, accumulated code uncommitted, productionfalse. No agent,
report/campaign/package/GUI/preflight/live/staging/commit/push or new allowance.

## 23. Approved continuation through subsequent deterministic blockers (in progress)

On2026-09-11 the user approved22.2 and explicitly authorized continuing until subsequent
blockers in this validation path are resolved. This supersedes the per-edge requirement
to await another implementation approval, not the requirement to inspect fresh evidence,
preserve authority/history/assertions, or keep changes bounded. No permission to commit,
enable production correlation, run live/GUI/campaign work, change representation or
global stack/JIT/tiering/thread policy is inferred. Attempts9/reports3 remain consumed.

Work began22:46:40 Europe/Berlin. No subagents. Only Coordinator compensation orchestration
and the two existing Coordinator test files have changed so far, in addition to this
contract/checkpoint documentation. Snapshot payload/collectors, Service and Infra remain
unchanged. All artifacts use new bootstrap-* names in the existing implementation directory.

Intermediate results (not final acceptance):

-22.2 fixture split reduced the rejection-theory entry from481,376 to48 bytes; the
  unchanged suffix initially retained481,360 bytes and overflowed while observing alias/
  dispatch. Separating mutation and neutrality assertions gives1,840-byte orchestration,
  339,664-byte mutation and140,000-byte assertion siblings. All ten cases PASS normally
  and in the diagnostic repeat. Original test source reconstructed exactly to22.1 SHA.
- The four current-install/direction/predecessor cases had a1,068,464-byte theory frame.
  Fixture/execution/assertion phase separation clears startup. Compensation then exposed
  its own306,336-byte calculation frame during snapshot finalization. A thin no-inline
  wrapper now calls a private no-inline Core, then finalizes every result exactly once
  using original state, receipt, predecessor payload, name and authority ID. Wrapper20,448;
  Core304,320 is gone before finalization. All guards, state expressions, branch order and
  result fields are unchanged; normalized original Coordinator source reconstructs to
  E17B064AC308E8C0A41411D2583188EDDBBAAA4D2D52086C5AF3E58720010BC6.
  All four cases PASS normally and in the diagnostic repeat.
- Active-predecessor restoration and successful/rejected active-predecessor snapshot
  fixtures likewise separate original setup from unchanged assertion/history suffixes.
  All21 RuntimeUnlockRetryConfirmation cases now have normal-Debug PASS evidence across
  focused runs; repeats add no distinct coverage. Source/body equivalence audit is pending.
- Closure-backed install startup exposed a459,024-byte receive-recovery fixture and
  later overlapping215,888/335,040-byte setup/completion phases. Thin orchestration and
  sibling setup/completion, plus separation of the111,616-byte closure-source completion
  from its adopted-closure construction, clear the exact test without route-authority edits.
- Paired-release reservation/install/successor verification now PASSes through its final
  route-transition preparation after source, install-history and transition phases were
  separated. No reservation, receipt, duplicate, consumed-release or successor assertion
  was removed. Most named19.2/20.2 controls also PASS; remaining selected controls are
  still being completed. The fresh-yield-after-consumed-reservation case remains under
  targeted validation; failed candidates are retained, not counted as completed fixes.

Missing fingerprint field/normalization/legacy parity controls, extraction audit, affected
fixture checks, one broader relevant contract lane and sender joined remain pending.
This checkpoint is deliberately incomplete; it does not claim all blockers resolved.

### 23.1 Continuation checkpoint, 2026-09-11 23:45 Europe/Berlin

This supersedes the intermediate pending/results list above, not its historical evidence.
All21 compensation cases, the named install/snapshot controls, eight new fingerprint
normalization/field/legacy-payload parity cases,23 action prerequisites and the named
retained-frame/pressure/phase2/commit controls have normal-Debug PASS evidence in focused
TRX files. Diagnostic repeats are not additional coverage. The fingerprint oracle freezes
the original payload shape/options but shares the unchanged descriptor projection and
canonical serializer; it is not an independent serializer implementation.

Further bounded fixes preserve original test statements and order: fresh-yield consumed-
reservation installation/inspection/route-cycle phases; retained-frame activation/pressure
phases; paired-responder binding/rejection/commit phases; initial-responder setup/drain/
commit phases. Verify-PhaseExtraction.ps1 expands helper bodies and confirms13 original
method bodies against HEAD, ignoring whitespace and helper plumbing only. Parameter
provenance was separately reviewed; authority history and assertions remain intact.

Two more snapshot orchestration overlaps were evidenced and separated: responder handoff
finalization now completes descriptor collection before CreateTransitionSnapshot; route-
transition attempted-operation construction now returns before the responder attempt is
constructed. Payloads, ordering, status, role and authority selection remain unchanged.
Normalized reconstruction before the first change matches3C5456401DCF888DB28CAB7BD575ACAFB4A8E6D98FDA3B7AA1FDBF29694C0B10;
reconstruction before the second matches1F7E2CCBC5305B61BFC565138709A29B8FD4630616C2BA7D3881AFC28BCE2364.
Initial responder and checkpoint/responder coexistence exact controls now PASS.

Latest remaining responder run aborts during repair-replay fixture startup, in fallback
retry/reservation descriptor collection; its assertions have not run. Fresh artifact:
bootstrap-responder-attempt-affected-controls.trx. Continue first-edge RCA, then remaining
directly affected controls, missing context/provenance assertions, one broader relevant
contract lane and sender joined. No overall completion claim, report, live/GUI/package,
production enablement, staging/commit/push or new campaign allowance. Productionfalse.

### 23.2 Broader-gate continuation checkpoint, 2026-09-12 00:12 Europe/Berlin

All five directly affected responder controls now PASS. Two new accepted-install context
provenance cases plus three existing install tests with added null-context assertions
PASS5;17 narrow shared-action/route-commit controls PASS, including the complete correlated
both-peer/two-cycle regression. Context checks cover finalized generation/fingerprint/
boundary, exact receipt, original active predecessor and supersession report. Rejected,
idempotent and expired install results expose no compensation context.

The one broader CoordinatorSnapshots lane was started:18 generic snapshot cases passed
before a receive-proof fixture stack overflow. Its unfinished controls are being resumed
in bounded batches excluding already completed cases, not restarting the complete lane.
The existing Release constrained-stack child is deferred until the final source is rebuilt;
it is not waived. One Release build was made before the first broader invocation, but
subsequent runtime changes mean that child binary must be refreshed again before its gate.
No retained-report invocation is authorized or performed by this continuation.

Fresh measurements led to test-phase splits in receive-proof publication, no-current-
recovery and file-Tuna pressure fixtures. Receive-proof generations1/2/7 PASS3; no-current
recovery PASS1; file-Tuna pressure settlement directions/outcomes PASS4. The original
receipt/authority/history/negative assertions remain. Retained-handoff fixture setup and
malformed-input projection were isolated from the real handoff execution; final exact
retained-handoff validation PASS2 in bootstrap-handoff-commit-storage-phase.trx.

Additional runtime overlaps were measured in receive-recovery issuance/execution-start/
receipt/proof reducers. Thin no-inline wrappers now finalize after unchanged private
calculation bodies return. All statuses are finalized once, using original state/name/
command/authority. Receipt/proof/issuance success-side auxiliary decisions are still the
same values; the existing finalizer reads only state/status/reason/authority and does not
consume or retarget those auxiliary decisions. Their attachment before the wrapper's
finalization therefore changes no projection or embedded-state provenance. Exact original
method-body reconstruction against HEAD passes (raw file pins have mixed line endings,
so they are not falsely compared to normalized whole-file hashes).

Handoff commit validation also retained completed-state storage while reissuing recovery
authority for strict validation. Its unchanged guards now precede a separate commit phase,
whose unchanged final persistent-state construction follows successful operation issuance.
All early rejections retain original input state; no projected state is externally adopted.
An intermediate candidate regressed to earlier reconciliation and is retained as insufficient;
only the final exact2PASS candidate clears this path. No global stack/JIT/thread policy change.

Broader unfinished checks, focused recovery reducer/commit rejection controls, independent
full/compact compensation/install evidence parity and sender joined remain pending. No
overall completion claim. Accounting remains open from22:46:40 on09-11; no subagents or
idle-gap charge. Attempts9/reports3 and productionfalse remain unchanged.

### 23.3 Deterministic continuation checkpoint, 2026-09-12 00:52 Europe/Berlin

Held-publication full/compact/GUI-classifier fixture controls PASS2 after supplying the
real coordinator startup/publication prefix to the whole-stream GUI classifier. The
recovery-only suffix remains the original input for all existing negative and parity
checks. The classifier/runtime is unchanged; no actual GUI or report was launched.

InboundBootstrapCheckpoint_CommitRequiresExactLocalBootstrap has11 cases (not12); all
PASS, including rejection mutations and duplicate commit. Its test setup/assertion
phases were split. A481KB Service preparation method also retained pause/drain work
during route preparation; preparation and application now run as sibling phases under
the same caller-held lock. All guards, coordinator calls, state adoption, logs and
queued effects retain their original order. This is the only new Service file changed
by this continuation: SessionFileTransferService.RouteTransitionFallback.cs.

Checkpoint-continuity real retirement PASS1 after source/admission/retirement fixture
phase separation. Completed-pressure-successor real snapshot finalizers PASS4 after
separating its1.24MB fixture into predecessor, binding/handoff, rejection/commit and
execution/proof/terminal phases. No assertion, callback or history was removed; its
terminal-unproven branch still needs its affected evidence caller validation. Normal
receive-recovery route carry PASS2 after bootstrap setup returned before its original
stream/observation construction. Terminal-rollover route carry already PASS2.

Fresh runtime edges in the pressure-successor path required three further phase splits:
the503KB initial-activation-hold reconciler now checks the original no-hold guard before
entering its unchanged active-hold work; issued-recovery preparation returns before
operation persistence; handoff transition validation completes every original guard
and projected-state construction before the exact original issuance comparison. That
projection remains private validation only and never grants externally adopted authority.
All original runtime statements/order and28 test/fixture bodies are reconstructible
against HEAD by Verify-PhaseExtraction.ps1 and Verify-RecoveryPhaseExtraction.ps1.
The source audits ignore whitespace/helper plumbing, with parameter provenance reviewed
separately. Earlier unsuccessful candidates remain retained and are not counted as fixes.

CoordinatorSnapshots discovery has114 cases. Resume-SnapshotValidation.ps1 compares every
discovered parameterized case with this turn's PASS artifacts; a method is excluded only
when all its cases passed. An earlier ad-hoc batch excluded any previously passed method;
that was insufficient accounting and is superseded by this case-complete audit. Latest
remaining count is32 non-child cases. Existing constrained Release child is separate,
not waived. Broader completion, directly affected runtime rejection/hold/Service controls,
independent compensation/install parity and sender joined remain pending. No global stack,
JIT, tiering or thread-policy change; no production enablement, live/package/GUI/report,
new campaign, staging, commit or push. Accounting is still open for this same active turn.

### 23.4 Deterministic gate checkpoint, 2026-09-12 01:35 Europe/Berlin

All112 non-child CoordinatorSnapshots cases now have case-complete PASS evidence;
Resume-SnapshotValidation.ps1 reports zero unfinished cases. The separate Release
parent gate PASSes after a fresh Release build and launches the actual existing1MB
child. Its normal no-environment no-op is not counted as child coverage. No runtime
or child-fixture changes followed that gate. Twelve directly affected receive-recovery,
handoff-rejection and hold controls also PASS in bounded post-change runs. Independent
compensation/install full/compact and Windows PowerShell5.1/PowerShell7 parity PASS9
(441.3579883s outer elapsed). This is fixture evaluation, not a retained-report invocation.

Fresh Service checks PASS4 late-proof cases, PASS4 initial arrival-order/pending-write
cases and PASS2 command-first direction cases. The full correlated two-cycle/stale-frame
regression also PASSes again on current runtime. Large late-proof and initial-arrival
test frames were split into original setup/commit/assertion sibling phases; no callbacks,
mutation, authority history, duplicate or neutrality assertion was removed. Original
statement/order reconstruction now covers43 test/fixture bodies; all runtime extraction
audits remain PASS. Only source-equivalence plumbing and whitespace are normalized.

One Debug rebuild while the long evidence test still held its assembly failed with
MSB3027/MSB3021. No code defect or test failure is inferred from that lock. After the
test exited, the rebuilt late-proof gate PASSed; no process was killed or test omitted.

The affected pressure-successor evidence caller reached its assertions in all four
cases, but its script still expected a missing-live-route-epoch boundary. The unchanged
evaluator already accepts epoch-zero initial Regular-v4 only with complete startup,
same-owner publication lineage and exact receipt/effect proof. This real fixture has
that history. Its script now requires that exact initial publication proof in both
independent inputs, no unresolved boundary for completed/proven history, and the exact
remaining recovery obligation for unproven history. All existing malformed-effect,
descriptor, duplicate and GUI-consumer controls remain. Targeted validation is running;
sender-only joined follows only after it passes. No overall completion claim yet.

Protected pins, HEAD adfa9c049c10e233d6b449b980cad09b33166e94, empty index and productionfalse
are unchanged. Diff whitespace check passes. Accounting remains open from22:46:40 on
09-11; no subagents, live/package/GUI/report/campaign, production enablement or commit.

### 23.5 Approved deterministic path complete, 2026-09-12 02:27 Europe/Berlin

Sender-only joined acceptance now PASSes in
bootstrap-sender-joined-retained-capture-phases.trx (4.0360783s outer). The original
real-Service/fake-connectivity test reaches both coordinator-published fallback routes,
completed exchange/physical-receipt checks, exact source-hold linkage, committed
transition/disposition/publication linkage, and inbound pressure settlement. Production
capability remains false on both transports. No live NKN, GUI, package or report was run.

The remaining measured stack overlaps were resolved by bounded phase extraction:

- Joined test agreement/final assertions now return separately; its async orchestration
  frame fell from0x7BF40 to0x5E0. Original statement/order reconstruction matches the
  pre-edit method pin A79B4DF12EC92D199C97BEE0F492942FD8906FA11049607DFC947DD15DE2A8FA.
- Initial-activation hold evaluation calculates before snapshot finalization. Ordinary
  results finalize exactly once; the original direct reconciliation return remains
  unfinalized. Six no-hold/rebind/proven-successor/invalid-scope/command-capture controls
  PASS. Two test setup frames were split without changing their assertions.
- Source-hold preparation returns before its unchanged finalizer. Both exact capture/
  receipt directions PASS2. Its original method pin is
  18CD9F43350CBC094F9957A50C39ED5F5E14D191EA8B7324B93D42B9E668292B.
- Exchange-current source-hold and scope checks now run as sibling phases under a thin
  wrapper, preserving the original short-circuit order. A partial split retained the
  suffix and was insufficient; its failed artifacts remain. Final Core exchange lane
  PASS25 includes the complete two-cycle/stale-frame history. An overbroad intermediate
  name filter also completed15 transport controls before aborting; those are additional
  evidence, not a passing overall run or a reason to rerun unrelated lanes.
- Outbound fallback preparation/application are now separate, as already done inbound.
  Each direction's supersession drain branch is isolated from normal draining. Original
  guards, logs, adoption, pause/drain/disposition effects and caller-held lock order are
  preserved. Focused Service controls PASS6, inbound arrival/command controls PASS6,
  and final outbound command/structural controls PASS3; overlaps are not new coverage.
- Outbound retained replay separates initial locked capture, replay, and final locked
  receipt/disposition application. Exact checkpoint-before-replay and stale/replaced-
  binding tests PASS2 in bootstrap-retained-replay-capture-phase.trx. Their large common
  setup, branch, observation and assertion frames were separated, with all original
  callbacks, waits, mutations and assertions retained. The runtime method reconstructs
  to pre-edit pin1F91C9A16158EE588E91872141AEF1EF7F55A712506048FED4C1D5E547C05220.
- Service receive-recovery issuance now returns before action projection. A separate
  1MB proof callback was split into exact directional matching/processing phases under
  the same lock, preserving outbound precedence and all post-lock bridge/retry effects.
  The directly affected completed-health-evaluation successor checks PASS4.

The pressure-successor evidence correction from23.4 PASSes all four cases in
bootstrap-pressure-successor-current-boundary.trx (116.8537032s outer), including
completed, unproven, terminal-unproven, identical/conflicting duplicates, missing/wrong
effects, malformed successor descriptors, independent full/compact inputs and GUI
classifier parity. The evaluator and frozen catalog were not changed. The helper uses
Windows PowerShell5.1; the separate compensation/install parity gate covers both5.1/7.

Final verification: the one broader CoordinatorSnapshots lane has112 case-complete
non-child PASSes, with focused post-change checks rather than a full-lane restart.
The final Release rebuild succeeds (222 warnings,0 errors;15.6040713s outer).
bootstrap-final-constrained-stack-release-parent.trx PASSes the actual existing1MB
child gate (3.7746251s outer); its no-environment no-op is not counted as child coverage.
Only whitespace cleanup followed that build. The original12 focused recovery controls,
9 compensation/install parity cases and earlier named gates remain recorded in23.4.
Verify-PhaseExtraction.ps1 now reconstructs47 original test/fixture bodies; the joined
audit adds its separate pre-edit pin. Runtime audits PASS for all extracted bodies and
expressions. The audit parser was corrected to skip expression-bodied forwarding calls
when finding declarations; no expected pin was changed to make an audit pass.

This turn's production edits are limited to FileTransferCoordinator.cs, its Snapshots
partial, FileTransferBootstrapSourceHold.cs, FileTransferBootstrapExchange.cs, and the
Service main/RouteTransitionFallback/Liveness partials. Other accumulated dirty work
remains preserved. A test helper's initially incorrect int parameter was corrected to
the original long type after one compile failure; no test result is claimed for that
failed build. Failed candidates/diagnostics are retained and never counted as PASSes.

All blockers in the approved deterministic implementation-through-sender-joined path
are resolved. Receiver-only/dual-stop joined expansion, broader evidence expansion,
production enablement, live/package/GUI/campaign/report and commit/push remain separate
gates; this completion is not authorization or acceptance for them. HEAD remains
adfa9c049c10e233d6b449b980cad09b33166e94, index empty, protected pins unchanged,
productionfalse, and final diff whitespace check PASSes.

Accounting closes this active continuation from2026-09-11 22:46:40 Europe/Berlin with
a conservative225m root-only bound: root3903m / cross-agent7069.9m. No subagents or
preceding idle-gap charge.238 retained TRX files from this turn total1380.5819074s of
test-run windows, including diagnostics, aborted runs and repeats—not distinct coverage.
Build/outer windows overlap and are not double-added; named long parity/build windows
are recorded above and in23.4. Historical09-10 21:33→09-11 00:28 remains unreconciled.
Attempts9/reports3 consumed and attempt9FAIL are unchanged; no new allowance is inferred.

### 23.6 Receiver-only and dual-stop joined gate complete, 2026-09-12 06:27 Europe/Berlin

The user authorized the next deterministic joined gate. Final current-source Debug
validation PASSes all three stop variants together in
bootstrap-all-stop-joined-pressure-contract-20260912.trx: sender-only, receiver-only,
and dual-stop (3/3;14.2090829s outer). Both real Services reach coordinator-published
post_tuna_fallback_v6/protocol6 with the original completed exchange, exact source-hold,
physical phase readiness, retained frame, commit/disposition/publication and current
publication-token checks. Connectivity is fake; production capability stays false.

Two blockers were measured and addressed:

1. The first combined run completed dual-stop but aborted during receiver-only source-
   hold capture snapshot construction. A fresh receiver-only diagnostic reproduced
   the stack overflow. ApplyFallbackBootstrapSourceHoldCapture retained0x1F3F0 bytes
   while calling its finalizer; TryEnsureBootstrapSourceHoldLocked retained0x2F8B0,
   the finalizer0x10FC0, and CreateBootstrapCorrelationSnapshot0x19870. These are fixed
   overlapping frames, not evidence of recursive authority or a network failure.
   Capture now calculates in a private no-inline Core helper before the unchanged
   finalizer runs exactly once with the original state, result, kind and capture.
   The wrapper's current Debug frame is64 bytes; Core retains0x1F3E0 but returns before
   snapshot construction. Reconstruction matches pre-edit body pin
   F9101B49950B95A2BA581B6E83D417A3A55289DEE1F9FF058A99DB58076D4E01.
2. Receiver-only then reached both fallback publications but failed the blanket inbound
   LastInstallGeneration>0 assertion. The fresh retained receiver diagnostic log has
   zero pressure-install events. Service TryApplyFileTransferRouteTransitionPressureSettlement
   explicitly permits no installed binding; Core TryProjectFileTransferRouteTransitionCommit
   accepts that path only without a supplied pressure receipt, and still rejects pending/
   retained lineage. A first receiver-only-specific assertion correction was too narrow:
   the next joined run exposed the same legal no-pressure state in sender-only. That
   failed candidate remains retained. The final assertion follows the optional-pressure
   contract: zero installation must equal the entire empty pressure substate; any nonzero
   installation retains every original exact applied-settlement/cleared-authority/linkage
   assertion. All routes still require no pending/installed/retained binding. No pressure
   is seeded, no runtime gate is relaxed, and no timing or deadline is changed.

The final joined log contains an actual inbound generation1 pressure installation and
settlement, in addition to the no-pressure paths. Deterministic Core pressure-off/on
TunaFallbackCommitsAndAppliesRetainedFrameOnce cases PASS2, and exact source-hold
capture/receipt controls PASS2 in bootstrap-source-hold-pressure-controls-20260912.trx
(4/4;9.631702s outer). The directly affected Core CorrelatedBootstrap lane PASS25 in
bootstrap-exchange-capture-regression-20260912.trx (6.9980184s outer), including its
two-cycle/stale-frame controls. An earlier focused source-hold run PASS2 is a repeat,
not additional distinct coverage. No aggregate snapshot/analyzer/GUI lane was restarted.

Runtime extraction audits PASS including the new capture pin. Joined reconstruction
preserves the original pre-edit pin after excluding only the exact reviewed no-pressure
assertion branch; it does not claim that this assertion change is a pure extraction.
The production edit is confined to FileTransferBootstrapSourceHold.cs; test changes are
confined to the joined assertion. Existing accumulated dirty work is preserved. This
gate validates Debug; the earlier Release/1MB-child results in23.5 are historical and
were not rerun after this capture change. HEAD remains adfa9c049c10e233d6b449b980cad09b33166e94,
index empty, protected pins unchanged. No commit/push, production enablement, package,
live NKN, GUI, campaign or report invocation occurred.

Next bounded gate: review and implement the already-described formal initiation/source-
hold evidence extension, with independent full/compact/shared-GUI reader parity and
malformed origin/source/linkage controls. That is not production/live acceptance.

Accounting adds a conservative12m root-only bound for this continuation (first retained
clock04:17:16 UTC; includes opening inspection; prior idle gap excluded): root3915m /
cross-agent7081.9m. No subagents. Nine new TRX files total44.211579s of test-run windows,
including aborted, failed, diagnostic and repeated runs; outer/build windows overlap
and are not double-added. Failed candidates are not PASSes. Attempts9/reports3 consumed,
attempt9FAIL and the historical unreconciled interval are unchanged; no new allowance
or campaign restart is inferred.

### 23.7 Initiation evidence implemented; runtime/compatibility blockers retained, 2026-09-12 07:28 Europe/Berlin

The next bounded evidence gate was authorized. The new explicit profile is
snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1,
with catalog hash40a8177eae51664f06bc2bc7aaa4dc2fb62983fc2262c3c2f99d030401efd7db.
Its parent correlation hash remains
ebbaf19d291129caa47b34e69395de2b38985661560791c7c762c437ed6e679b. The base,
receiver-barrier and checkpoint-continuity catalog hashes also remain unchanged.
An earlier unpublished candidate0062705b38d13708c98ba5ec455f4200c20ddf99d9e13a44daca16f76d186a21
passed six controls before the exact retirement rule was added to the catalog;
that result is historical, not acceptance of the final catalog. Retained inputs
were not rewritten to the new hash.

Implementation:

- Coordinator snapshots now carry a typed bootstrapInitiation payload: exact local-
  stop/peer-wake origin, pinned source/agreement, immutable hold command, capture/pause/
  captured-work linkage, drain/settled-work receipt, frozen checkpoint and base command.
  Source-hold descriptors no longer borrow retained-frame data from an older exchange.
  Exchange/phase linkage comes from its own immutable SourceHoldCommand, not a newer
  current hold. Production projection redacts identities/fingerprints consistently.
- BootstrapInitiationEvidence.ps1 is a versioned extension, not a mutation of frozen
  catalogs. It requires enabled agreement and Prepared/Applied/Drained history, exact
  immutable links, carried receipt/checkpoint, exact next-generation source replacement,
  and the matching base fallback command before exchange progression. Invalidated or
  retired holds cannot authorize later progress. Source-hold effect records are forbidden.
- Full/compact readers and the shared GUI classifier recognize the explicit new profile.
  Unknown suffixes and old-profile relabeling remain rejected. GUI-reader tests execute
  the classifier against fixture logs; no application GUI or live NKN run occurred.
  Modeled receipt history is explicitly not physical route-publication acceptance.

Current focused evidence results in bootstrap-correlation-implementation/:

- bootstrap-initiation-remaining-evidence-controls-20260912.trx PASS18: four local/peer
  origin/direction parity cases, exact redaction controls, six agreement/legacy cases,
  two invalidation cases and two current successor catalog cases. Each parity fixture
  checks independent serialized full/compact/GUI inputs on PowerShell5.1 and7, including
  missing hold/capture/drain, altered source/origin/proof, extra payload field, wrong
  receipt/work/checkpoint/base linkage, hidden drain, rejected-applied and late-phase-
  applied controls. Exact inbound fixtures preserve the four independent missing physical
  responder effects; outbound modeled history still fails physical route consistency.
- bootstrap-initiation-retirement-generation-phase-20260912.trx PASS2, both directions'
  actual two-cycle retirement histories on both PowerShell hosts (174.8035991s outer).
- Four completed-exchange parity cases PASSed in
  bootstrap-initiation-correlation-retirement-receipt-phases-20260912.trx before its
  subsequent retirement fixture stack abort. These are case-complete evidence only;
  the overall aborted invocation is not a passing lane. They use the final catalog.
- Core exchange regression PASS25 (8.3247497s outer), exact source-hold capture/receipt
  PASS2 (18.4786104s outer), and original origin/scope/terminal/legacy controls PASS2
  (9.6474061s outer). No aggregate snapshot/analyzer/contract lane was restarted.

Measured stack corrections made within this gate preserve original behavior:

- ApplyFallbackBootstrapSourceHoldReceipt retained0x1F870 while snapshotting. Its
  calculation now returns before the unchanged finalizer, exactly once. Body audit
  matches pre-edit pinC5E31316B139EB09A0203306A88DDD9A9D67446743E99C51F7CA7522F355D52A.
- The origin/scope fixture retained0x9BAD0; setup, rejected origins and terminal/legacy
  assertions are now sibling no-inline phases. Reconstruction matches pre-edit pin
  9376F5FFCE78619C0344F3BD9340B68EA9E6B7B2FEE4BACCA570FC4B4701BD39.
- The two-cycle fixture exposed CommitCorrelatedFallback0x4DB50 and a non-preserved
  checkpoint branch in AuthorizeFallbackRuntimeUnlockRetryDispatchSource0x39CF0.
  Final assertions/non-preserved setup are separate phases; the preserved-stream branch
  is unchanged. The final second-generation assertion was also isolated. All three
  reconstruct to their HEAD bodies. The audit handles the existing eight-space helper
  indentation locally; no expected baseline/pin was changed to make an audit pass.

The first overbroad BootstrapInitiation_ filter completed17 PASS/5 FAIL before aborting
in the original origin fixture. The five failures were four duplicate records wrongly
appended after stream-close and one redactor input missing its event context. The final
tests use an adjacent in-stream duplicate and the actual snapshot event context. Those
failed artifacts and all intermediate stack diagnostics remain retained, not accepted.

Two current blockers prevent completion, commit or runtime acceptance:

1. Receiver-only source failure can outlive the source-hold admission check.
   bootstrap-initiation-final-joined-20260912.trx PASS2/FAIL1 (16.0986802s outer).
   Its matching log at05:13:28 UTC records outbound source-hold Prepared/Captured/Drained
   at coordinator generations11/12/13, exchange issuance14, then source-send failure and
   accepted Terminalized15. Inbound subsequently terminalizes after the peer error.
   Writer sequences2185/2187/2188/2189 precede failure2209 and terminal2226. The immutable
   coordinator generation chain establishes the held/exchange state before terminal;
   asynchronous log ordering alone does not prove the exact earlier lock interleaving.
   The fresh isolated receiver-only rerun PASS1 (4.5525144s outer), with unchanged source-
   release behavior and deadlines, is evidence of timing sensitivity, not resolution.

   Code inspection identifies a concrete unclosed interval: SendChunkIndicesV4Async
   captures errorCompletionCurrent under gate using its hold-aware local predicate,
   releases gate, and later throws. FailOutboundV4Async authorizes the still-current
   runtime owner and TransitionOutboundToTerminalAsync checks the sparse install, but
   that install check does not include source-hold ownership. A source hold intentionally
   preserves the source owner/publication. This is the leading RCA hypothesis; the trace
   does not yet bind the failed physical lease to the hold capture at the final effect.
   No runtime failure-suppression fix has been implemented on this hypothesis alone.

   Bounded next fix plan: deterministically pause the real failure path after eligibility
   and before terminal effects, apply the real correlated peer-wake/source-hold command,
   and retain the exact physical lease/source tuple. Establish whether the callback is
   captured-held work or a genuinely current unheld failure. Gate final failure effects
   under the same lock using Coordinator-issued hold/source authority and exact work
   identity; reject stale callbacks diagnostically without terminal/progress/peer-error/
   retry/route effects. Preserve current unheld failure and user cancellation behavior.
   Cover before/during/after capture plus replaced owner/session/provider/pause/terminal
   negatives, then rerun only receiver-only and the directly affected joined controls.
   Review the final authority contract before changing terminal behavior; do not infer
   authority from bridge status, presence of an adapter hold, or successful later reruns.

2. The directly affected checkpoint-continuity compatibility fixture aborts before reader
   evaluation. bootstrap-initiation-continuity-compatibility-20260912.trx and its exact
   diagnostic bootstrap-initiation-continuity-stack-20260912.trx both ABORT0 (2.1934126s
   and2.1079222s outer). The current stack is ApplyCheckpoint -> Apply/ApplyCore ->
   MarkFallbackCheckpointRequested -> InvalidateRecoveryAuthority ->
   GetRuntimeUnlockRetryLifecycle. Measured frames: ApplyCheckpoint0x5A00,
   MarkFallbackCheckpointRequested0x30F20, InvalidateRecoveryAuthority0x43E40,
   GetRuntimeUnlockRetryLifecycle0x10F30, plus the async fixture/callers. This is a
   reproducible stack-capacity blocker, not a reader verdict or live-network failure.
   Next: measure the async caller and split only the first excessive overlapping phase,
   retaining original statements, state provenance, lock scope and body reconstruction;
   rerun the same two-host compatibility cases. No larger stack, warmup, reordering,
   skipped assertion, global JIT option or alternate fixture may stand in for this gate.

Runtime extraction audits, including the new receipt/origin/retirement phases, PASS.
Current-source Debug was rebuilt during focused implementation; no Release or1MB child
rerun occurred after these changes. HEAD remains adfa9c049c10e233d6b449b980cad09b33166e94,
index empty, protected contract/StallGrace/ArtifactWriters pins unchanged and diff
whitespace check PASS. Accumulated unrelated work remains preserved. Productionfalse;
no package/live GUI/NKN/campaign/report invocation, commit or push. This checkpoint
records partial completion and two explicit blockers, not an all-green replacement of23.6.

Accounting adds a conservative55m root-only bound from04:35:50 UTC for this continuation,
excluding the preceding idle gap: root3970m/cross-agent7136.9m. No subagents. The19 new
bootstrap-initiation-*20260912.trx files total958.6092738s of test-run windows, including
failed/aborted/diagnostic/repeated runs; these are not distinct coverage or additional
active-time charges. The initial Debug build took30.6869662s outer (226 warnings,0 errors);
outer/build/test windows overlap and are not double-added. Attempts9/reports3 consumed,
attempt9FAIL and the historical09-10 21:33->09-11 00:28 unreconciled interval are unchanged.
No new numeric allowance, campaign restart or report budget is inferred.

### 23.8 Stack gate resolved; callback-ownership RCA confirmed, 2026-09-12 08:10 Europe/Berlin

The user authorized proceeding with the two23.7 blockers. The physical source-send
callback race is now reproduced deterministically, not merely inferred from log timing.
BootstrapSourceFailure_HoldAfterEligibilityCannotTerminalize FAILs both cases in
bootstrap-failure-race-owned-red-20260912.trx (13.0987283s outer), and remains identically
red on final rebuilt source in bootstrap-failure-final-snapshot-and-race-20260912.trx.
Its dormant test hook pauses only after the production hold-aware failure eligibility
check and before throwing. A valid Service/Coordinator local-stop hold is then applied;
the real failure-to-terminal method still returns true and terminalizes the transfer.

The pending-physical case proves that the exact nonzero lease is captured; the physically
settled case proves that the physical lease list is already empty while its callback is
still pending. Thus an exact-captured-physical-lease-only guard would be incomplete.
The first fixture candidate failed earlier because its recording session did not implement
the required Tuna authority-session interface; that artifact is not defect proof. The
corrected dedicated subclass adds that interface without changing other fixtures' behavior.
The test invokes the production terminal method directly after obtaining the real send
failure; it is not a new complete supervisor/peer-wake joined acceptance result.

The proposed next change is documented separately in
[Bootstrap source-send callback ownership](file-transfer-bootstrap-source-callback-ownership-contract.md).
It separates physical completion from pending callback ownership, extends the exact Core
hold capture, carries callback identity to the final effect boundary, and requires held/
stale outcomes to avoid the generic stale-failure branch's source reopen/pump installation.
This changes the physical-only representation reviewed in8; implementation pauses for
contract review. No blanket pause exemption, timer, retry, callback-authority shortcut,
production enablement or speculative terminal suppression was implemented.

The independent checkpoint-continuity stack gate now passes:

- Main production-generated full/compact/shared-GUI reader cases PASS2 on PowerShell5.1/7
  in bootstrap-failure-continuity-tail-proof-reference-20260912.trx (49.6145105s outer).
- Committed-retirement cross-host parity PASS1 in bootstrap-failure-committed-retirement-
  20260912.trx (132.8058262s outer).
- Final rebuilt delayed-disposition cross-host parity PASS1 in bootstrap-failure-delayed-
  serialized-20260912.trx (72.480454s outer).
- Direct action scope/token/fallback/repair controls PASS27 in bootstrap-failure-action-
  controls-20260912.trx (2.2471487s outer). Final snapshot invariant/projection checks
  PASS18 alongside the two expected unresolved race failures in bootstrap-failure-final-
  snapshot-and-race-20260912.trx (5.4089886s outer). The mixed invocation is FAIL overall.
  An earlier related invocation also completed those18 cases before aborting; it is not
  another passing lane. No full aggregate snapshot/contract/analyzer or GUI matrix ran.

Corrections follow the measured first stack edges, preserving statements and ownership:

1. The async continuity fixture retained0x97B30 bytes. Its delayed-disposition, stale-wake
   assertions and checkpoint cycle now run in separate no-inline phases, with the same
   lock and await order. The measured remaining parent frame is0x1DA00. Reconstruction
   matches the original entire fixture body from HEAD, including every assertion.
2. Coordinator TryResolveFileTransferCoordinatorActionEarlyDecision retained0xAB1C0.
   Scope, token and recovery checks now execute as ordered sibling no-inline phases,
   preserving the first rejection and exact undecided/default-out behavior. The combined
   body matches pre-edit pin1F5BDE35886D3F920E2D4FC692821B83D034A1609CA06799A2599342BC03D2CA.
3. Generic snapshot creation retained0x4C980 while invoking reconciliation/descriptor
   children. Reconciliation, identity validation, prior fingerprint lookup and final state
   stamping are separated from construction. The full expanded original body matches
   pinC8872AF84A96E2E829B57152DA1133DC240130BCA1030AFBE8DB07CD47620A45. Null checks, identity
   errors, reconciliation-before-validation, operation selection, rejected-mutation guards,
   generation/fingerprint rules and final timestamp/serialization ordering are unchanged.
4. Fallback-tail snapshot construction retained0x40610. Its operation/root-substate and
   exact reservation/recheck/consumption matcher parameters now use readonly references
   instead of copying large readonly value types. There are no asynchronous borrows or
   mutable authority changes. Four affected method bodies compare exactly with HEAD;
   the root helper and expression-bodied consumption check retain their original bodies.

The first final-action-snapshot helper candidate did not fix its measured overlap and
was removed; AuthorizeAction itself remains unchanged by this turn. The original runtime
authority/token/callback decisions are not redesigned by these stack corrections. Production
edits are limited to the Coordinator main/snapshot partials and the dormant source-failure
test hook in PullTransferSessionV4. The readonly-reference changes do not alter catalogs.

All original-body/reference audits PASS after correcting a one-off audit invocation that
called Get-MethodBody outside the script's scope; its misleading printed PASS lines were
not accepted. The retained script now performs those checks with terminating failures.
One initial fixture helper used long instead of the existing int log bookmark and caused
a compile failure; it was corrected. A diagnostic without the workspace log redirect failed
its persisted-log requirement; that is not a reader/authority verdict. One rebuild collided
with the still-running retirement test and failed MSB3027/MSB3021; after it completed, the
serialized rebuild and targeted delayed test passed. All failed candidates remain retained.

Source/profile/catalog/protected artifact scope is unchanged: productionfalse, HEAD
adfa9c049c10e233d6b449b980cad09b33166e94, index empty, protected frontier contract/StallGrace/
ArtifactWriters hashes unchanged, diff whitespace check PASS. No unrelated dirty work was
removed. No commit/push, Release/1MB-child rerun, package, actual GUI/live NKN, campaign or
report invocation occurred. Only callback ownership remains blocked, pending representation
review; stack acceptance here does not waive either red race or constitute live acceptance.

Accounting adds a conservative40m root-only bound from05:31:45 UTC for this continuation,
excluding the prior idle gap: root4010m/cross-agent7176.9m. No subagents. The21 new
bootstrap-failure-*20260912.trx files total327.9260592s of test-run windows, including
aborted, failed, diagnostic and repeated results, not distinct coverage. The initial
build/test outer window was36.4353386s; failed int-helper build6.1581502s and DLL-lock
build25.9749136s are recorded separately. Outer/build/test windows overlap and are not
double-added. Attempts9/reports3 consumed, attempt9FAIL and the historical unreconciled
interval remain unchanged. No numeric allowance, campaign restart or new report budget.

### 23.9 Approved callback ownership implemented; bounded gates pass, 2026-09-12 09:49 Europe/Berlin

The user approved the separate source-callback ownership contract. Implementation now
separates callback lifetime from physical drainage: Coordinator-issued immutable tokens
bind the exact source, agreement, session/provider generations and original pause tuple;
the Service registers before submission and reports exact pending callbacks in the Core
hold capture. Physical completion/lease retirement and the existing drain receipt remain
independent. A callback already consumed for progress cannot be recaptured as pending.

The real sparse sender/supervisor carries the exact token through throw, supervisor and
final terminal authorization. Every existing callback effect lock checks Core currentness.
Held/stale callbacks are consumed as diagnostic-only and cannot invoke generic stale-pump
recovery. Control receive stays active. A different publication alone was proven insufficient
by the new terminal-publication regression: it reopened source and installed another pump.
That candidate was replaced by an exact Coordinator committed successor check, tied to the
completed source-hold exchange and current commit/publication/owner. Exchange completion
alone and a forged publication fail; a genuine committed fallback and its Core-reactivated
Tuna successor pass. Current unheld failure/success, source-read failure and cancellation
retain their positive behavior. The original two races now use the actual supervisor and
authenticated peer wake, not direct substitution of a terminal method.

Evidence uses a new explicit source-callback-v1 profile, hash
0ad879a93ec4d4639ecffd0fecf1c64d2aa32dc594bd39c680b8ca30c3c492dd, extending frozen initiation
hash40a8177eae51664f06bc2bc7aaa4dc2fb62983fc2262c3c2f99d030401efd7db without changing it.
Typed admission/capture/consumption records link the exact callback, source, agreement,
hold and capture. Independent readers reject missing/altered/conflicting attribution,
captured callbacks with effects, and duplicate consumption. These markers are explicitly
excluded from physical route-effect acceptance. Full/compact/shared-GUI readers independently
reparse serialized evidence on PowerShell5.1/7. The initiation altered-source negative now
accepts the new profile's earlier callback-binding rejection only for that exact case;
positive checks also reject callback findings. No old negative assertion was omitted.

Final retained gates under bootstrap-correlation-implementation/:

- bootstrap-callback-runtime-final-20260912.trx: PASS50 (27.594926s TRX window), including
  21 actual-supervisor callback boundaries, nine Core callback/successor cases and directly
  affected physical hold/pump/legacy failure controls.
- bootstrap-callback-terminal-snapshot-20260912.trx: PASS24, including18 original snapshot
  invariant/projection cases and six directly affected terminal/recovery invalidation cases.
- bootstrap-callback-readers-20260912.trx: new callback reader PASS1 and redactor PASS2,
  but initial initiation controls FAIL4 overall on the earlier rejection-name mismatch.
  bootstrap-callback-reader-compat-20260912.trx reruns those four and PASSes all four on
  both hosts. The new reader's twelve modeled controls also pass both hosts; modeled
  callback evidence is explicitly not physical route proof.
- bootstrap-callback-joined-20260912.trx: PASS3, sender-only/receiver-only/dual-stop through
  real Services, real Infra adapters and deterministic FakeNkn. Original40s cancellation,
  8s edge deadlines, source gates and route/receipt assertions are unchanged. This is not
  live NKN, actual GUI, package or production activation acceptance.

Two additional stack edges were measured and resolved without changing authority:

1. Normal failure terminalization aborted in recovery invalidation, then snapshot creation.
   InvalidateRecoveryAuthority's0x43E40 frame is now64 bytes with ordered active-operation
   and clearance siblings (0x31E40/0x1A7C0). Terminal async guards/retirements execute in
   one no-inline sibling under the same gate; the parent falls from0x42460 to0x78C0.
   Both unheld controls pass, followed by the full affected lane.
2. That lane then passed44 cases before aborting in the existing physical-hold fixture's
   route-history construction. Its0x669D0 async frame now holds0x9B0 after moving only
   original history adoption and state-reading assertions into no-inline phases. All
   three physical-settlement cases pass, then the complete50-case lane passes.

Verify-CallbackPhaseExtraction.ps1 and all inherited phase audits PASS. Original
invalidation, terminal guards, sender non-callback body and physical-hold fixture statements
reconstruct exactly after removing only the documented new callback checks/effects. Same
locks, awaits, assertions, rejection order and physical receipt semantics are preserved.
No stack-size increase, global JIT/tiering change, warm-up, reordering or quarantine used.

Earlier supervisor fixture candidates (connection, manifest-vs-payload, SHA, historical
hold observation), a wrong reflection property, the publication-restart defect, one
zero-selected-test filter, a compile failure and all stack-aborted invocations remain
retained. Partial PASS output before abort is not an accepted lane. No hidden rerun or
deadline increase was used to obtain the final results.

Protected frontier-contract/StallGrace/ArtifactWriters hashes are unchanged; production
SupportsFallbackBootstrapCorrelation remainsfalse. HEADadfa9c049c10e233d6b449b980cad09b33166e94
and empty index are unchanged; diff whitespace check PASS. Unrelated dirty work remains.
No commit/push, package, live/GUI, campaign/report or full aggregate matrix was run.
This completes the approved bounded callback contract. Next: scoped pre-commit review
of accumulated changes and evidence before any separate commit/deployment/live approval.

Accounting conservatively adds71m root-only from08:39 through09:50 Europe/Berlin,
excluding08:10-to08:39 idle: root4081m/cross-agent7247.9m. No subagents. The27 new
bootstrap-callback-*20260912.trx windows total458.0642722s, including failed/aborted,
diagnostic, zero-test and repeated windows, not distinct coverage. Build/diagnostic time
is within the root bound and overlaps those windows; no double addition. Attempts9/
reports3 consumed, attempt9FAIL and the historical unreconciled interval remain unchanged.
No new numeric allowance, campaign restart or report budget is inferred.

### 23.10 Pre-commit review: two blocking callback gaps, 2026-09-12

Review verdict: request changes. The50 runtime/24 terminal-snapshot/three joined and
cross-host reader results in23.9 remain genuine results for their selected cases, but
they do not cover delayed callback delivery after pump handoff or a receive-gap install
while the callback successor gate remains closed. Production and test source were not
edited during this review; only this checkpoint/recovery notes and an ignored diagnostic
script were written. No staging/commit or external/live operation occurred.

R1 [P1] Retired pumps lose callback-consumption ownership. RetireNextAsync dequeues the
pending send, sets transferredToSupervisor=true for the typed callback exception, and
therefore skips its local retirement. A Coordinator-committed successor can clear
callbackAwaitingSuccessor and hand off the still-running pump before that exception is
delivered. InstallCurrentPumpAsync clears the active task slot and sends the old task to
DisposeRetiredOutboundSparsePumpAsync, whose catch discards the exception without calling
CompleteBootstrapSourceCallbackFailureAsync or retiring the exact callback. The supervisor
can no longer consume that old task. The callback remains in ActiveBootstrapSourceCallbacks;
a later Tuna source-hold capture sees the obsolete source and rejects the entire capture
as bootstrap_source_hold_outbound_callback_unbound. Consumption evidence is also missing.
Relevant source: PullTransferSessionV4.cs17272-17286,1553-1560,2589-2619; capture at
BootstrapSourceHold.cs100-107. Diagnostic reproduction creates a genuine Coordinator-issued
callback using the existing route-history fixture, settles its physical lease, then compares
the production retired-pump and active-supervisor cleanup paths: pending callback count1
after retired cleanup,0 after active-supervisor cleanup.

R2 [P1] The exact-successor restart check is not applied to every install entry. The new
callbackAwaitingSuccessor checks protect the top-of-loop handoff/retry paths, but the
frontier_receive_authority_gap branch calls StartCurrentPumpInstall unconditionally.
That common entry and TryInstallOutboundSparsePumpAsync do not check callback ownership
or HasCommittedBootstrapSourceCallbackSuccessor. After a captured callback's data-session
generation changes, the stale receive descriptor plus an authenticated frontier frame can
take this path before any successor is committed; it stages a descriptor, reopens source
and installs a runtime. The same unguarded entry is used by outer exception recovery.
Relevant source: PullTransferSessionV4.cs2653-2670,3461-3467,1374-1420. A lower-boundary
diagnostic applies a real hold/capture, confirms Held, changes service session generation,
then invokes the production receive-gap installer: ExactCoordinatorSuccessor=false,
ReplacementRuntimeCreated=true, InstalledGeneration=1. This is not permission from Core.

The retained script is
artifacts/validation/consolidation-bounded-20260907/bootstrap-correlation-implementation/Review-CallbackRetirement.ps1,
run once normally and once with -CheckReceiveGap. Both use current built assemblies and
existing genuine Core history/Service helpers, no live connection. These are isolated
reflection diagnostics under PowerShell7/.NET10.0.12, NOT additional default-.NET8 test
lane passes or end-to-end supervisor interleaving tests. The production call-chain review
establishes how the affected consumers are reached; exact supervisor regressions are the
next required proof. Combined command wall time5.6410322s; no new TRX or aggregate rerun.

Recommended bounded fix sequence (not implemented/approved by this review):

1. Add an actual-supervisor regression holding a callback through a genuine committed
   fallback handoff, then deliver it from the retired pump; require exact one-time
   diagnostic consumption and a subsequent source-hold capture that is not poisoned.
2. Add the captured-callback/session-generation/frontier-arrival regression; require no
   source reopen/install until an exact Core successor is committed. Keep a real committed
   successor positive control, physical settlement independence and control receive alive.
3. Give the retired observer explicit exact callback cleanup ownership, preserving its
   capture/evidence binding, and put the Core successor authorization at the common
   installation boundary. No generic stale-result fallback, service-only authority flag,
   new retry/deadline or early physical-drain release. Rerun affected tests/readers and only
   the three joined variants after these focused regressions pass; no full matrix.

HEADadfa9c049c10e233d6b449b980cad09b33166e94/index empty, productionfalse, protected frontier/
StallGrace/ArtifactWriters pins unchanged, whitespace check PASS. Review cannot approve
commit while R1/R2 remain. No subagents, package, actual GUI/live NKN, campaign or report.
Accounting adds conservative12m root-only09:51-to10:03, prior idle excluded:
root4093m/cross-agent7259.9m. Diagnostic windows overlap root time and are not double-added.
Attempts9/reports3 consumed, attempt9FAIL, historical unreconciled interval and allowance
remain unchanged. No campaign restart or new numeric budget is inferred.

### 23.11 Approved fixes for retired callback cleanup and common install authorization, 2026-09-12

The user's request to fix the issues authorizes the bounded R1/R2 implementation from23.10,
not commit, production enablement, packaging, GUI/live NKN, campaign/report or a full matrix.
Both findings now have default-.NET8 production-path reproductions and passing regressions.

R1: the actual receive supervisor publishes a genuine Coordinator fallback successor while
the old pump's typed callback is paused. Before the fix, releasing that callback leaves its
registration indefinitely. Retired-pump observation now consumes the exact callback once,
diagnostically only, including a task that completes between handoff selection and cleanup.
Stream-reuse cleanup also observes an unconsumed task. No retired failure invokes terminal,
progress, recovery or route effects. The accepted capture is retained on that callback for
diagnostic consumption linkage; it supplies no runtime authority. The regression verifies
registry retirement, duplicate cleanup neutrality, real Coordinator reactivation through
the existing fixture history, a third source open, and a subsequent unpoisoned capture.
Reactivation fixture envelopes are enqueued contiguously; no route authority is invented.

R2: the exact Coordinator-successor requirement is now common to install request, stage,
source-open invocation and commit. The retained pending-source field is the original
Coordinator-issued source scope, not a Service permission bit. Current capture and active
callback identities are checked under the same gate; only Core's exact committed successor
can discharge the source restriction. Receive-gap/outer-recovery entry cannot cancel or
replace the active pump while this check is closed. Staging before a hold does not permit
a later reopen or commit after capture. Empty-callback hold behavior is preserved.

Actual-supervisor receive-gap regression: capture, consume the now-stale callback after
session-generation replacement, then deliver an authenticated frontier frame. Before the
fix, source opens increased1->2 without a successor. After the fix, opens remain1 and the
owner/publication/terminal/progress/pause/recovery/install tuple remains unchanged. A lower
install-boundary regression stages a real descriptor before capture and rejects its commit;
another pauses a staged reopen before capture and proves it cannot invoke the source later.
The latter reproduced1->2 opens before the source-open boundary check was added.

During the affected lane, user cancellation exposed a new supervisor spin: disposed-token
access occurred before the existing terminal/context exit and the newly closed install path
no longer escaped that loop. The existing gate-protected terminal/context check now precedes
token access. Independent cancellation and genuine current-unheld failure remain intact.

Retained verification (all files in the existing bootstrap-correlation-implementation directory):

- bootstrap-handoff-red-20260912.trx: receive-gap defect reproduced; the first retired fixture
  failed earlier because it reread peer history after exchange replacement, not defect proof.
- bootstrap-handoff-red2-20260912.trx: corrected real-handoff regression fails awaiting callback
  retirement. bootstrap-handoff-reopen-red-20260912.trx reproduces the staged-reopen bypass.
- bootstrap-handoff-green-20260912.trx: receive-gap PASS; retired cleanup advances to next-cycle
  setup, whose manually adopted Core history initially omitted snapshot envelopes. Corrected
  fixture bootstrap-handoff-green2-20260912.trx PASS1. These earlier failures remain retained.
- bootstrap-handoff-boundaries-20260912.trx PASS4: retired/duplicate cleanup, stage/commit checks,
  and two projection-only retained-capture exact/missing-membership controls. Projection tests
  explicitly replace the current hold; they do not claim route acceptance or runtime authority.
- bootstrap-handoff-reopen-green-20260912.trx PASS1.
- bootstrap-handoff-runtime-final-20260912.trx:64 PASS/1 FAIL (user cancellation),65 selected
  callback/Core/physical-hold and directly affected legacy install/failure cases. After the
  terminal-check ordering fix, bootstrap-handoff-cancel-green-20260912.trx PASS4. This is a
  corrected focused rerun, not a claim that the earlier65-case invocation passed wholesale.
- bootstrap-handoff-receive-readers-20260912.trx PASS6: five affected receive/reopen/outer-recovery
  controls and callback full/compact/shared-GUI consumer parity on PowerShell5.1 and7. The GUI
  consumer is invoked against deterministic evidence; no actual GUI scenario or live run.
- bootstrap-handoff-joined-20260912.trx PASS3: original sender-only, receiver-only and dual-stop
  joined transport-receipt scenarios. Original source release and40s/8s deadlines unchanged.
- bootstrap-handoff-final-20260912.trx PASS4 on the final runtime: retired handoff, receive-gap,
  captured stage/commit and delayed reopen. No aggregate suite was repeated after the small fix.

The source/callback phase-reconstruction audit and whitespace check pass. HEAD remains
adfa9c049c10e233d6b449b980cad09b33166e94, index empty, production capability=false, protected
frontier/StallGrace/ArtifactWriters pins unchanged. Existing dirty work is preserved.
No schema/profile/hash change, deadline/retry/queue/performance tuning or physical-drain
relaxation. Next gate is scoped pre-commit review; no outstanding failure in these selected
regressions is being deferred to live acceptance.

Accounting: conservative27m root-only bound10:07-to10:34 Europe/Berlin, prior idle excluded;
root4120m/cross-agent7286.9m. No subagents. The12 new bootstrap-handoff TRX windows total
150.7533919s, including failed/fixture/repeated windows; this is execution time, not distinct
coverage or an extra active charge. Build and audit work overlap the root bound. Attempts9/
reports3 consumed, attempt9FAIL, historical unreconciled interval and allowance unchanged.

### 23.12 Pre-commit review: next-cycle capture can precede retired callback delivery, 2026-09-12

Review verdict: request changes; stop at the first missing proof edge. No production/test
source was edited, no build or test lane was rerun, and nothing was staged or committed.
The23.11 cleanup/common-install fixes and selected passing results remain genuine for their
covered orderings. They do not establish safety when the next source stop occurs before
an old callback has been delivered to the retired observer.

R1 [P1] Do not include already-superseded callbacks in the next source's capture admission.
SessionFileTransferService.BootstrapSourceHold.cs100-107 collects every registration in
ActiveBootstrapSourceCallbacks and rejects the new capture if any callback is not current
for the new source. A callback from the preceding Tuna source legitimately remains pending
after its physical send settled and Core committed fallback, then reactivated Tuna. Its
accepted old capture already owns effect suppression; it cannot match the new source.
Nevertheless the new stop's prepared hold is invalidated as
bootstrap_source_hold_outbound_callback_unbound. Retiring that callback afterward does not
repair the cycle: PrepareFallbackBootstrapSourceHold sees the same-source invalidated hold
and rejects re-preparation. This can permanently block the next requested fallback.

The23.11 test AssertCallbackRetiredAfterSupervisorHandoffAsync explicitly releases and waits
for an empty callback registry at lines26-29, then reactivates/stops at lines41-43. That is a
valid positive ordering but does not cover stop-before-delivery. The retained capture field
was added for diagnostic linkage, yet the new capture admission does not classify it as
belonging to a Coordinator-superseded source.

New ignored diagnostic: Review-CallbackNextCycle.ps1, run once normally and once with
-RetireBeforeNextStop. It registers a genuine Core-issued callback through the production
Service, settles its physical lease, captures the source, drives actual Service correlation
phase receipts to fallback publication, finishes the existing no-pump fixture's real install/
retained-frame disposition, and uses the existing genuine Core reactivation fixture. The
source is at live epoch5 with HasCommittedBootstrapSourceCallbackSuccessor=true before
both cases; no source/runtime authority is synthesized.

- Next stop before retirement: pending callbacks1; new hold generation3, Invalidated=true,
  Capture=null. The production retired observer then consumes the callback (count0), but
  a repeated local stop leaves Invalidated=true/Capture=null.
- Retirement before next stop: pending callbacks0; same next hold generation3,
  Invalidated=false/Capture present. This confirms the setup can capture that next source.

These are isolated Service/Coordinator boundary diagnostics under PowerShell7/.NET10.0.12,
not new default-.NET8 unit results or a complete actual-supervisor timing test. The bounded
actual-supervisor regression for the reversed ordering remains required. Logs are retained
as bootstrap-callback-next-cycle-after-20260912.log and ...-before-20260912.log. Both commands
exit0 after asserting their expected diagnostic outcomes; that is not a passing app verdict.
Combined command window9.0793788s. No live NKN, actual GUI, package, campaign or report.

Recommended bounded fix: classify pending callback ownership using exact Core-authorized
source/capture/successor lineage. Keep old captured callbacks registered for one-time late
diagnostic consumption without letting them poison a successor source's capture; retain
fail-closed rejection for genuinely unbound current work. Do not use registry emptiness,
timers, waiting for callback delivery as physical drain, or a Service permission bit as
authority. Add the stop-before-delivery regression and late consumption after the new
capture, including preserved original capture fingerprints and no new-source effects.
Then run only affected Core/Service/evidence checks and the three joined controls. Further
implementation requires approval; this review does not authorize committing the candidate.

HEADadfa9c049c10e233d6b449b980cad09b33166e94/index empty; production capability=false;
frontier/StallGrace/ArtifactWriters pins unchanged; whitespace PASS. No subagents.
Accounting adds conservative8m root-only11:01-to11:09 bound, idle excluded: root4128m/
cross-agent7294.9m. Diagnostic windows overlap that bound and are not double-added. No new
TRX; attempts9/reports3 consumed, attempt9FAIL and historical unreconciled interval unchanged.

### 23.13 Approved next-cycle callback classification implemented, 2026-09-12

The user approved the bounded23.12 fix. The reversed-order regression now runs through the
actual default-.NET8 sender/receive supervisor: physical settlement, captured callback held
at its throw boundary, genuine fallback publication/install/disposition, genuine Core Tuna
reactivation, then the next source stop before callback delivery. Before the fix it rejects
that next capture. After the fix the next capture succeeds with the old callback excluded;
late retired-pump cleanup leaves the new source's ownership/progress/pause/recovery/install
tuple unchanged and consumes the old callback exactly once.

Core now issues FileTransferBootstrapSourceCallbackRetirement only for an exact admitted
callback in the accepted source-hold capture and its physical-drain receipt, joined to the
completed, non-invalidated exchange and exact committed successor. Callback, capture and
successor publication are pinned by the immutable record's fingerprint. Missing/mismatched
capture, callback, receipt, agreement, owner/publication, terminal state or incomplete/
invalidated exchange cannot issue it. The record is solely a diagnostic classification;
it grants no route, runtime unlock, retry, recovery, liveness or progress authority.

The Service retains this Core-issued record with the still-pending callback. Core revalidates
the exact callback/capture, Coordinator/session/transfer/direction and published successor
scope when classifying it. At its anchor epoch the publication must match exactly; later
current published epochs may retain this permanent old-callback classification. This avoids
depending on an old exchange that the next cycle replaces. Source capture excludes only
these validated retired callbacks; every other pending callback still undergoes the existing
fail-closed current-source checks. The same classification is used by install gating and
the active failure consumer, so an old callback cannot recreate a successor-wait restriction.
The accepted old capture remains untouched for its eventual consumption evidence.

No callback is marked consumed early, no callback delivery is required as physical-drain
proof, and no new registry, timer, retry or Service permission bit was added. Production
callback disposition/schema/catalog/profile/hash meanings are unchanged. The retirement
record is internal, not a wire or snapshot contract extension.

Verification, retained in the existing bootstrap-correlation-implementation directory:

- bootstrap-next-cycle-red-20260912.trx: FAIL1 at the next capture in the actual supervisor,
  confirming23.12 beyond its isolated PowerShell diagnostic. First fixed run
  bootstrap-next-cycle-green-20260912.trx PASS1.
- bootstrap-next-cycle-core-20260912.trx and ...-core-jit-20260912.trx aborted before results:
  the new test's inlined large persistent-state mutation temporaries held a0x56AF0 stack frame
  across history construction. A non-inlined test-only mutation phase reduces the caller
  to0x490 (retained final disassembly); no production stack policy or test-host stack changed.
- bootstrap-next-cycle-core2-20260912.trx:10 PASS/1 FAIL exposed an altered anchor publication
  accepted by classification. Exact equality at the retirement anchor epoch closes that
  check; the subsequent controls run passes all11 Core cases. Invalidated-exchange negative
  setup explicitly mutates that graph because invalidating an already-superseded exchange
  through the normal API is a rejected no-op, not a usable negative fixture.
- bootstrap-next-cycle-evidence-20260912.trx initially fails for the modeled history's absent
  physical scope-invalidation effect, not callback linkage. The fixture-specific check now
  explicitly requires that physical-evidence failure, while independently rejecting callback
  history errors. No production reader was changed or missing physical proof accepted.
- bootstrap-next-cycle-controls-20260912.trx PASS12:11 Core exact/negative controls plus the
  new real-history next-cycle callback reader case, with12 controls on each of PowerShell5.1
  and7. Full/compact/shared-GUI results agree; modeled history is not physical route acceptance.
- bootstrap-next-cycle-runtime-final-20260912.trx:67 PASS/1 FAIL of68 selected callback/Core/
  physical-hold/legacy-failure cases. The failure was the strengthened old-order test taking
  its no-effects baseline before authorized retained checkpoint replay applied (bytes0->4096,
  pause released). The fixture now waits for the existing Coordinator Applied disposition
  receipt before that baseline; the no-effects assertions and3s waits are preserved.
- bootstrap-next-cycle-handoff-final-20260912.trx PASS2 for both before/after-stop callback
  orderings on the final source. The new ordering parses actual emitted snapshot records:
  one Stale/none consumption, matching the original capture/hold fingerprints, with no recapture
  of that callback into the successor hold. Duplicate cleanup remains effect-neutral.
- bootstrap-next-cycle-joined-readers-20260912.trx PASS4: the three original deterministic
  sender-only/receiver-only/dual-stop joined scenarios plus the existing callback reader case
  on PowerShell5.1/7. Original source release and40s/8s joined deadlines are unchanged.

Failed/aborted candidates remain retained and are not relabeled as passing lanes. Only the
affected handoff tests were rerun after their fixture synchronization change; the68-case
invocation remains67/1 in its original TRX. Reconstruction audits and whitespace checks
pass. HEADadfa9c049c10e233d6b449b980cad09b33166e94/index empty; production capability=false;
protected frontier/StallGrace/ArtifactWriters pins unchanged. Existing dirty work is preserved.
No staging/commit, package, actual GUI/live NKN, campaign/report, aggregate matrix, performance
or deadline change. Next is scoped pre-commit review, not production enablement.

Accounting: conservative24m root-only11:10-to11:34 bound, idle excluded: root4152m/
cross-agent7318.9m. No subagents. Ten new bootstrap-next-cycle TRX windows total218.2468999s,
including failed/aborted/repeated windows; execution time overlaps the root bound and is not
an extra active charge. Builds and audit included in the root bound. Attempts9/reports3
consumed, attempt9FAIL, historical unreconciled interval and allowance unchanged.

### 23.14 Pre-commit review: same-epoch recovery revokes callback retirement, 2026-09-12

Scope: review the23.13 callback-retirement implementation, not commit or production
enablement. One P1 remains. No production or unit-test source was changed in this review.

FileTransferBootstrapSourceCallback.cs:46 accepts a retained retirement record only while
the current publication exactly equals its anchor or the live route epoch is higher.
However, a genuine Coordinator-authorized recovery can replace the runtime owner/leg and
publication without changing the live route epoch. The old callback has already been
captured, drained and superseded, but its diagnostic retirement now validates false.
Reissuance cannot repair this: HasCommittedBootstrapSourceCallbackSuccessor compares the
last route-transition publication/commit/owner with the current ones, which recovery has
replaced. The Service caches the original retirement record using ??= as well.

The common install guard has an additional manifestation of this same missing lineage:
SessionFileTransferService.BootstrapSourceCallbacks.cs:24-26 treats the historical hold's
nonempty captured-callback set as requiring that same now-obsolete exact publication.
It runs before individual retirement validation. Consequently, merely relaxing retirement
validation does not fix installation, and consuming every old callback does not unblock it.
Captured history is intentionally immutable; its nonempty set is not pending current work.
The independent BootstrapCallbackAwaitingSuccessor source predicate must also be considered
when binding the same committed-source lineage, without using a Service permission bit.

New ignored diagnostics in bootstrap-correlation-implementation:

- Review-CallbackRetirementPublication.ps1 constructs genuine Core capture/drain/fallback/
  reactivation history, issues retirement, then requests a real publication through the
  production Service authorization helper. Fallback stays at epoch2, publication3->4;
  Tuna stays at epoch3, publication5->6. Both publications are accepted and Core-current,
  but retirement changes true->false, reissuance is null and exact-successor check is false.
  The first invocation failed only to load NLink.Core; explicit assembly loading corrected
  that diagnostic setup. It did not reproduce a runtime defect or produce test results.
- Review-CallbackRetirementServicePublication.ps1 uses the existing no-pump Service fixture:
  production callback admission/physical settlement, actual Service correlation receipts,
  genuine fallback publication and real install/retained-frame disposition. Initial install
  is allowed and the pending callback has a Core-issued retirement record. A same-epoch
  publication refresh (epoch4, publication6->7) makes retirement false and actual stage
  reject with bootstrap_source_callback_successor_required. The production retired observer
  consumes the callback to count0, but install remains forbidden by the historical capture.
- With -RestartTransport, the final script calls StartOutboundTransferLegLocked at its
  recovery boundary and uses the publication issued by that operation itself: runtime5->6,
  live epoch4->4, publication6->7. Core confirms the resulting publication is current.
  Retirement=false; stage rejected for the same callback-specific reason; callback count0
  after diagnostic cleanup still leaves install=false. This is the production leg-start
  boundary used by recovery (including PullTransferSession.cs:5318), not a fabricated
  publication or an adapter-created runtime owner. An earlier diagnostic additionally
  republished after restart (publication6->8); it showed the same result and is retained
  separately, but the final reproduction does not require that extra publication.

Service logs are retained as bootstrap-callback-next-cycle-publication-refresh-20260912.log,
...-publication-recovery-20260912.log and ...-publication-recovery-native-20260912.log.
These are PowerShell7/.NET10.0.12 isolated Core/Service boundary diagnostics using the current
compiled candidate. They are not a new default-.NET8 regression, full actual-supervisor
recovery run, live NKN run or passing application verdict. Exit0 asserts the defective
outcome. No earlier diagnostic or TRX was rewritten. Five command windows including the
assembly-load failure and intermediate reproduction total14.4476865s.

Recommended bounded next step: preserve Core-issued exact source/capture supersession
across genuine same-epoch recovery/publication lineage, and use that lineage consistently
for pending-source, historical-hold and individual-callback install checks. Do not replace
it with a numeric >= test, registry emptiness or an unbound Service flag; keep altered-token,
wrong-source, incomplete-drain and uncommitted-successor negatives fail-closed. Retain old
capture fingerprints and one-time Stale/none cleanup. Add default-.NET8 Core and actual-
supervisor regressions for recovery before and after late callback consumption, including
recovery on the anchor epoch and installation after count0. Verify no callback-authorized
route/progress/pause/retry/recovery effects. Only affected checks are required; no new full
aggregate lane or live campaign. Implementation requires approval; do not commit this state.

Retained23.13 evidence was inspected: controls12/12, final orderings2/2 and joined/old-reader
4/4; the68-case invocation remains67 PASS/1 fixture synchronization failure, followed by
the affected handoff rerun. Reader modeled-successor positives still explicitly fail for
missing physical effects, while requiring valid callback linkage. No reader was relaxed.
No unit/aggregate test rerun or rebuild was needed for this read-only source review.

HEADadfa9c049c10e233d6b449b980cad09b33166e94/index empty; production capability=false;
frontier/StallGrace/ArtifactWriters pins unchanged; whitespace PASS. Existing dirty work
preserved. Only review checkpoints and ignored diagnostic scripts/logs added or updated.
No subagents, stage/commit, package, GUI/live NKN, campaign/report or deadline/performance edit.
Accounting: conservative10m root-only11:39-to11:49 bound, idle excluded: root4162m/
cross-agent7328.9m. Diagnostic windows overlap that bound and are not double-added. No new
TRX; attempts9/reports3 consumed, attempt9FAIL and historical unreconciled interval unchanged.

### 23.15 Approved same-epoch callback lineage fix implemented, 2026-09-12

The user approved the bounded23.14 lineage/install-gate fix. The new actual default-.NET8
supervisor regressions first reproduced both orderings: recovery before old callback
consumption and recovery after consumption reached count0. Both timed out waiting for
the recovery source to open/install. Both now pass through the real sender supervisor.
The Service recovery leg-start boundary supplies genuine Core-issued owner/publication;
the fixture does not invent route or recovery authority or directly install the successor.

RCA: retirement had been tied to equality with an anchor publication unless the live epoch
advanced, while legitimate recovery replaces owner/leg/publication within that epoch. The
historical hold and pending-source gates also consulted the obsolete last-transition scope.
Callback cleanup could not clear the immutable historical capture or restore that scope.

Implementation is confined to the callback Core contract plus two Core lifecycle hooks:

- FileTransferBootstrapSourceCallback.Lineage.cs adds one immutable, fingerprint-pinned
  provenance record in Coordinator persistent state: exact accepted capture, its committed
  successor, and the current Core-issued publication linked to that successor. It is a
  bounded latest-source record, not an accumulating history or Service permission bit.
- The route-transition commit materialization hook records it only after exact capture/
  receipt, completed non-invalidated exchange, agreement/Coordinator/source-publication,
  committed operation, owner/commit/publication and current route validation. The transition
  must actually supersede that captured source; a later Tuna activation cannot manufacture
  an origin for an unrelated old capture.
- ApplyIssuedRoutePublicationAuthority advances its publication field only on Core-issued
  publications within the same Coordinator/agreement/session/transfer/direction/terminal
  lifetime. The current-publication check requires exact equality with this fingerprinted
  record and the existing Core token-current validator. A numerically newer unissued token
  cannot satisfy that check. Legitimate same-epoch recovery and publication refresh can.
- Callback retirement is anchored to the committed source successor and validated against
  this current provenance. HasCommittedBootstrapSourceCallbackSuccessor now consults the
  same proof, so the existing pending-source, historical-capture and per-callback Service
  install predicates agree. No new Service state or install/retry branch was added.
  The proof remains available when the next exchange replaces the previous exchange;
  a new hold with a different source does not inherit that old source's install permission.

The callback stays registered until actual consumption and keeps its original capture.
The source/drain/callback DTOs, callback dispositions, allowed effects, snapshot fields,
catalog/profile/hash and wire contracts remain unchanged. The new Core provenance is
internal; it is not a physical-effect receipt or independent runtime authority. Existing
owner/install checks still authorize successor work, and old callbacks stay diagnostic-only.

Verification artifacts in bootstrap-correlation-implementation:

- bootstrap-callback-recovery-red-20260912.trx FAIL2: both real-supervisor orderings fail
  at the source-open/install wait before the fix. No direct terminal-call substitution.
- ...-green-20260912.trx PASS14: both new orderings,11 existing Core issuance/negative
  controls, and the exact-source successor control.
- ...-controls-20260912.trx PASS4: both strengthened supervisor orderings plus2 Core
  recovery/refresh cases. Those cases also reject missing lineage, altered publication,
  bad lineage fingerprint, foreign Coordinator and wrong source, and confirm retirement
  can first be issued after recovery without an earlier Service cache-population call.
- ...-runtime-final-20260912.trx retains65 passing results before a test-host stack overflow
  in the new commit helper. It is an aborted invocation, not a passing aggregate lane.
  ...-stack-20260912.trx reproduces that overflow in the directly affected held-pump fixture,
  with0 completed results. MinOpts disassembly identifies0x34000 bytes in the new record
  helper plus0x44640 bytes in a duplicate nested exact-successor graph-copy check.
- The helper now receives the already-materialized authority/operation by readonly reference
  and the existing hold/exchange/agreement values. All exact guards remain; the duplicate
  graph-copy helper is removed. Final record-helper frame is0x12980. No production/test-host
  stack size, scheduler, runtime version, deadline or test assertion was relaxed.
- ...-hold-final-20260912.trx PASS33: the affected held-pump cases and Core/Service/Infra
  source-hold controls on final source, including the previously crashing path.
- ...-binding-final-20260912.trx PASS19:13 Core retirement cases, exact-source successor,
  four actual-supervisor next-cycle/recovery orderings, and terminal-session control.
  The recovery fixtures also set only the active-failure execution wait marker to exercise
  that install predicate under the genuine Core recovery proof. Late cleanup and duplicate
  cleanup preserve the effect tuple, emit no peer error, and retain exactly one Stale/none
  consumption with the original hold/capture fingerprints.
- ...-terminal-publication-20260912.trx PASS1 completes the remaining callback case whose
  result was not retained before the aborted lane. No aggregate lane was repeated.
- ...-joined-readers-20260912.trx PASS5: all three original deterministic joined stop
  scenarios and both callback history readers on PowerShell5.1/7. Full/compact/shared-GUI
  parity and12 controls per history/host pass. The modeled successor still explicitly fails
  for absent physical scope-invalidation effects; no physical route acceptance is claimed.

Earlier failed/aborted candidates are retained. Final focused reruns replace no historical
result or deadline. Existing extraction/reconstruction audits pass on final source; they
cover the earlier bounded extractions, not a claim that this new lineage hook is unchanged
old behavior. Protected frontier/StallGrace/ArtifactWriters pins and whitespace checks pass.
HEADadfa9c049c10e233d6b449b980cad09b33166e94/index empty; production capability=false.
No commit/staging, package, actual GUI/live NKN, campaign/report, full aggregate matrix,
transport/performance tuning or source-release change. Existing dirty work is preserved.
Next is scoped pre-commit review of this completed candidate, not production enablement.

Accounting: conservative22m root-only11:57-to12:19 bound, idle excluded: root4184m/
cross-agent7350.9m. No agents. Nine new TRX windows total232.3173951s, including failed,
aborted and repeated windows; execution overlaps the root bound and is not double-added.
Builds and source audits are included in that bound. Attempts9/reports3 consumed, attempt9FAIL,
historical unreconciled interval and allowance unchanged; no new campaign budget inferred.

### 23.16 Scoped pre-commit review: no actionable blocker, 2026-09-12

The user's proceed instruction selected the23.15 recommended pre-commit review. Scope was
the new Core source/capture lineage, its commit/publication lifecycle hooks, retirement
issuance/validation, the existing three Service install predicates and affected regressions.
No actionable finding was identified in that scope. This is not a fresh whole-worktree
review of all accumulated changes, commit authorization or production/live acceptance.

The lineage has one commit writer and one Core-issued-publication advancement path. Exact
accepted capture/receipt, completed exchange, Coordinator/agreement/source, committed
successor and current owner/publication checks remain. Consumers require fingerprint-bound
lineage and the exact current Core publication; increasing a token generation alone does
not grant permission. Storage outside the route-transition substate preserves the proof
when that substate is replaced. Rejected commit finalization does not adopt candidate proof.

Pending-source, historical-capture and individual-callback gates share that Core proof.
They do not replace the existing owner/session/predecessor/install-current checks. New
captures exclude only Core-validated retired callbacks; physical lease checks are unchanged.
Retirement retains the original capture and does not consume callbacks early. Actual stale
consumption still records Stale/none once and cannot authorize runtime/route effects.

Reviewed retained TRX counters: final hold33/33, binding19/19, terminal-publication1/1 and
joined/readers5/5. The earlier runtime invocation remains failed/aborted after65 retained
passes, and the stack reproduction remains failed with0 results; neither is a green lane.
The new actual-supervisor fixtures cover recovery before/after callback cleanup, including
count0 and the pending-source marker. Recovery enters at the controlled Service leg-start
boundary and uses Core-issued authority, not a natural live-network recovery trigger.
Reader modeled histories still reject absent physical effects. No tests, build, aggregate
lane, live scenario or obsolete defect-asserting diagnostic script were rerun for review.

HEADadfa9c049c10e233d6b449b980cad09b33166e94/index empty; production capability=false;
frontier/StallGrace/ArtifactWriters SHA256 pins unchanged; whitespace PASS. Existing dirty
work preserved. Only review checkpoint documents changed; no runtime/test edits, agents,
stage/commit, package, GUI/live NKN, campaign/report or deadline/performance changes.
Next: explicitly approve a commit scoped to the reviewed correlation candidate, with its
exact staging manifest checked against the accumulated worktree before committing.

Accounting: conservative6m root-only12:20-to12:26 review bound, idle excluded: root4190m/
cross-agent7356.9m. No new TRX or unattended build/test time. Read-only command windows overlap
the root bound and are not double-added. Attempts9/reports3 consumed, attempt9FAIL, historical
unreconciled interval and allowance unchanged; no campaign budget or enablement inferred.

### 23.17 Approved scoped commit boundary, 2026-09-12

The user approved the23.16 recommended scoped commit. The candidate is81 explicit paths:
two contract documents,31 Core/Infra implementation files,42 test/evidence-fixture files and
six Ops/GUI evidence files. It includes the accumulated initiation/source-hold/callback
dependencies needed by the reviewed same-epoch lineage fix; committing only that new helper
would omit its uncommitted contracts and consumers. Existing phase-extraction prerequisites,
joined-test pressure-backend seam and frozen-profile compatibility updates are included as
documented and verified by the preceding checkpoints, not new implementation in this turn.

Exact path/SHA256 manifest: local ignored bootstrap-correlation-implementation/
bootstrap-correlation-commit-manifest-20260912.json. Stage only those paths, verify the
staged set and content against that manifest and parentadfa9c049c10e233d6b449b980cad09b33166e94,
then commit on the existing v0.7.0 branch. Record resulting commit identity and post-commit
checks in WORK-IN-PROGRESS.md. The manifest and retained diagnostics/TRX are not source files
in this commit. No wholesale staging of the worktree is approved.

Excluded and preserved unchanged: docs/file-transfer-frontier-supersession-contract.md,
tests/nLink.SmokeTests.Core/TestData/StallGraceUnresolvedDiagnostic.ps1 and
tools/FileTransferSoak/ArtifactWriters.ps1, with the same three protected SHA256 pins.

The inherited Verify-CallbackPhaseExtraction.ps1 audit chain passes on this candidate.
Retained callback runtime50/50 and terminal/snapshot24/24, final hold33/33, binding19/19,
terminal-publication1/1 and joined/readers5/5 counters were checked, not rerun. Historical
failed/aborted artifacts remain failed/aborted. No new build/test lane, physical acceptance
claim, runtime/test behavior change or production enablement is part of this commit gate.
Only current-status/commit-boundary documentation is updated during commit preparation.

After this scoped commit, the next gate is production-enablement readiness review, not
automatic enablement or live execution. Existing attempts9/reports3, attempt9FAIL,
historical unreconciled interval and allowance remain unchanged; no renewed budget.
Commit-turn accounting and final scope/hash verification are in the local WIP/phase records.

### 23.18 Production-enablement readiness review, 2026-09-12

The user requested the next readiness review, not enablement. HEAD is
e9350a4dec4b5685c7c153136a93d691853ed6c3; all81 committed manifest SHA256 values matched before
review documentation. No new runtime defect was identified in this scoped review. The
candidate is suitable for a bounded enablement implementation gate, not production release.

Reviewed boundaries:

- NknSignalingTransport.BootstrapCorrelation.cs:16 still advertises false. Service Offer
  and Accept construction select version1 only when supported/offered. Core pins exact
  agreement/issuers, rejects malformed or unsolicited upgrades and conflicting duplicates,
  and requires the receiver's matching sender SessionOpen before enabling its agreement.
  Negotiation does not issue route/runtime authority. Existing transfers do not migrate.
- Retained Service handshake cases cover all four support pairings in
  bootstrap-initiation-peer-regression.trx. Core one-way negotiation, stale/mismatch and
  no-upgrade cases pass in resumed-focused-core-service-infra.trx. These are historical
  retained results, not new passes on this turn. A future capability edit must rerun the
  directly affected negotiation checks; codec shape and frozen legacy vectors stay intact.
- Service snapshot profile/hash, independent callback catalog and GUI ledger profile
  agree on source-callback-v1/hash0ad879a93ec4d4639ecffd0fecf1c64d2aa32dc594bd39c680b8ca30c3c492dd.
  Frozen initiation hash40a8177eae51664f06bc2bc7aaa4dc2fb62983fc2262c3c2f99d030401efd7db remains
  separate. Callback/initiation markers cannot satisfy physical route-effect acceptance.
- Mixed-version/unsupported peers intentionally retain legacy behavior. This preserves
  compatibility, not a claim that the known legacy replay defect is fixed for those peers.
  Requiring version1 for all transfers or disabling old-peer Tuna is separate product scope.

First missing shipping-path proof: NknBootstrapCorrelationJoinedTests.cs:67-70 asserts
the real transports are disabled, then attaches JoinedBootstrapTransportProxy, whose
capability getter returns true at:305. The four joined cases therefore verify selected
behavior through forwarded real Services/Infra, not enabled direct transport attachment.
Simply flipping the four Assert.False calls to Assert.True while keeping the proxy would
leave this evidence gap. This is an enablement test prerequisite, not a newly reproduced
runtime defect. The exchange cases end at the first correlated fallback, then cancel the
contexts; they do not prove the full live off-on-off cycle or the second Tuna publication.

Recommended bounded implementation, requiring approval:

1. Enable only the production SupportsFallbackBootstrapCorrelation capability. Update its
   stale gate comment and the joined fixture to attach the actual NknSignalingTransport
   instances with no capability-forcing proxy. Rename the disabled-production test to its
   new claim; remove only proxy code/usings made unused. Do not add configuration, in-flight
   negotiation changes, authority fallbacks, or transport/performance policy changes.
2. Verify direct negotiation plus sender-only, receiver-only and dual-stop joined cases
   with existing deadlines/source release/route receipts. Preserve and rerun the four
   supported/unsupported Service pairings and exact Core negotiation negatives. Run only
   directly affected checks; reuse unchanged focused callback/hold/evidence results unless
   implementation changes those paths. No automatic full aggregate or GUI matrix.
3. Review the enabled candidate and exact diff before its separate commit/package/live
   gates. Rollback is disabling advertisement in a subsequent build and using fresh
   transfers; never downgrade an already negotiated transfer or reinterpret its receipts.

Live acceptance remains a later gate: a fresh canonical win-x64 package from the approved
enabled candidate on both peers, frozen source/binary/config/evidence hashes, explicit new
attempt and necessary report-finalization allowance, then one serialized
regular-v4-live-activation-off-on-off-256mb run outside the restricted sandbox. Use
StrictCampaignPreflight, GoodputOnlyRerunLimit0 and SetupOnlyRerunLimit0; stop on failed
preflight, first missing proof edge or hash drift. Do not run scenarios past failed preflight.
Require the second Coordinator-published file_tuna_v4 route in addition to fallback, SHA
and finalization; retain correctness and performance as separate outcomes. No existing
package/artifact establishes this new live claim. Three unrelated frontier files remain
outside implementation scope and must not be silently incorporated into the frozen run.

This turn changed review/checkpoint documents only. No source/test edit, build/test rerun,
staging/commit/push, package, GUI/preflight/live NKN, report/campaign or agents. Production
remains false. Conservative5m root-only12:33-to12:38 bound, idle excluded: root4202m/
cross-agent7368.9m. No new TRX or unattended build/test time. Attempts9/reports3 consumed,
attempt9FAIL, historical unreconciled interval and allowance unchanged; no renewed budget.

### 23.19 Approved capability enablement and direct-transport verification, 2026-09-12

The user approved the23.18 bounded patch/offline checks. The only production change is
NknSignalingTransport.BootstrapCorrelation.cs:16 returning true for capability support,
with its comment clarifying that Core still requires mutual selection on fresh transfers.
No negotiation, route/runtime authority, callback, source-hold, wire/catalog, liveness,
transport/performance or compatibility policy was changed.

NknBootstrapCorrelationJoinedTests now attaches the actual host/helper NknSignalingTransport
instances and invokes their activation/stop methods directly. The capability-forcing proxy,
its interface and its three now-unused imports were removed. The negotiation test is named
JoinedBootstrapCorrelation_RealServicesNegotiateWithProductionCapability and asserts actual
production capability support. Existing agreement, readiness, exchange, exact physical
receipt, route-publication and pressure-settlement assertions remain. An explicit source
comparison against HEAD, allowing only this capability/direct-attachment conversion,
verifies the original body/order, source-release points and3s/8s/40s deadlines are preserved.

Retained default-.NET8 Debug verification in bootstrap-correlation-implementation:

- bootstrap-production-capability-red-20260912.trx: expected FAIL1 before production
  enablement. The direct negotiation fixture fails its real-host capability assertion at
  line64 (expected true, actual false), establishing that no proxy masks the shipping gate.
- bootstrap-production-capability-negotiation-20260912.trx: PASS12 after enablement.
  One direct joined negotiation case, all four Service support pairings and seven Core
  agreement/issuer/mismatch/stale/no-upgrade cases pass. No receiver SessionOpen is invented,
  and legacy/unsupported behavior remains mode-separated.
- bootstrap-production-capability-joined-20260912.trx: PASS3 for direct sender-only,
  receiver-only and dual-stop. Real Services/Infra execute over deterministic FakeNkn and
  test Tuna lanes; the existing real pressure backends are never network-connected.
  Initial Tuna and correlated fallback publication/receipt checks pass on both peers.

Two incremental build/test invocations plus the no-build joined invocation completed.
Existing compiler/analyzer warnings were emitted; no build errors or new stack workaround.
No aggregate contract/analyzer/Smoke lane, full report, GUI or live NKN run was added.
Unchanged callback/hold/readers retain their preceding focused evidence, not new passes.
The joined cases still stop after first fallback; the second Tuna publication and complete
off-on-off cycle require the separately authorized future live gate from23.18.

Final source SHA256: capabilityFC1BA17178B72A8354F44F6635CD841765E8EABE1BC9D39A601B03972E7A4D2B;
joined fixtureA9AA9C179EF810701D1746220AA85A88775E29362198BF4AA89CD6C256C5E326.
All other77 files from the81-file commit manifest are unchanged (excluding these two and
the two current-status docs). Protected frontier/StallGrace/ArtifactWriters pins unchanged;
whitespace PASS. HEAD e9350a4dec4b5685c7c153136a93d691853ed6c3/index empty. The capability is
enabled only in this local source/Debug candidate; no package, deployment, staging/commit,
push, GUI/preflight/live run, report/campaign or agents. Next is scoped pre-commit review.

Accounting: conservative6m root-only12:38-to12:44 bound, idle excluded: root4208m/
cross-agent7374.9m. Three TRX windows total11.9096703s, including the expected red run;
build and audit time is included in the root bound and not double-added. Attempts9/reports3
consumed, attempt9FAIL, historical unreconciled interval and allowance remain unchanged.

### 23.20 Pre-commit review: stale pressure-backend assertion blocks commit, 2026-09-12

One P2 finding: tests/nLink.SmokeTests.Core/NknRegularV4PressureBackendTests.cs:210,
NknPressureBackend_ProductionClientRemainsTheOnlyBackend, still requires the real transport's
SupportsFallbackBootstrapCorrelation to be false. The approved23.19 capability change makes
that expectation obsolete. This existing assertion was outside the15 selected enablement
checks and was missed in that implementation's affected-test inventory.

Confirmed with the current23.19 Debug binaries and no build:
bootstrap-production-capability-review-pressure-red-20260912.trx retains FAIL1 at line210,
Assert.False expectedfalse/actualtrue. The preceding Assert.Same verifies that the real
production client remains the pressure backend despite the supplied alternate test adapter;
execution passes that ownership assertion before failing on the old capability expectation.
This is a stale test contract, not a reproduced pressure-backend or route-authority defect.

Recommended correction, requiring approval: change only that capability expectation to
Assert.True while retaining Assert.Same and all receipt/ownership/lifecycle controls. Rebuild
and rerun this one directly affected test. No pressure implementation, capability rollback,
deadline adjustment, generic suite or live run is needed for this first missing test edge.
The old red artifact remains historical evidence; do not relabel it as passing.

The rest of this scoped review found no actionable issue in the two-file enablement diff:
only Nkn advertises true (the default interface remainsfalse); Core mutual selection and
no-upgrade rules remain; direct joined transports have no proxy or capability override;
existing source-release/deadline/agreement/receipt/route assertions are unchanged. Verified
the retained12/12 negotiation and3/3 direct-stop results. Those results remain valid for
their exact selected cases, not a claim that the omitted backend test passed. The second
Tuna publication/full live cycle and mixed-version legacy limitation remain unchanged gates.

Review changed checkpoint documents only. Capability/fixture SHA256 match23.19; the failing
test file is unchanged. HEAD e9350a4dec4b5685c7c153136a93d691853ed6c3/index empty, unrelated
frontier/StallGrace/ArtifactWriters pins unchanged, whitespace PASS. No source/test edits,
build, aggregate lane, stage/commit/push, package, GUI/preflight/live, report/campaign or agents.

Accounting: conservative4m root-only12:44-to12:48 bound, idle excluded: root4212m/
cross-agent7378.9m. One new failed TRX window1.0961369s overlaps that bound, not double-added;
no build time. Attempts9/reports3 consumed, attempt9FAIL, historical unreconciled interval
and allowance remain unchanged. Next: approve the one-line test correction and focused rerun.

### 23.21 Approved stale-expectation correction verified, 2026-09-12

The user approved the23.20 narrow correction. Changed only
NknRegularV4PressureBackendTests.cs:210 from Assert.False to Assert.True for the production
correlation capability. The preceding Assert.Same still verifies that the production client
is the selected pressure backend despite an alternate adapter argument. Exact comparison
against HEAD confirms the rest of this file, including receipt/ownership/lifecycle checks,
is unchanged. This aligns the test with approved capability enablement; no runtime fix.

Rebuilt with the ordinary .NET8 Debug configuration and reran only
NknPressureBackend_ProductionClientRemainsTheOnlyBackend. Retained
bootstrap-production-capability-pressure-fix-20260912.trx: PASS1,0 failures. The23.20 FAIL1
artifact is preserved. The prior12 negotiation and3 direct-joined passes are reused, not
new passes; no aggregate lane or unrelated test rerun. Existing build warnings remain;
no errors, deadline/assertion removal, stack/JIT policy or authority change.

Final test SHA256D5CC417A475A18352F66146FC20F41D128C115D23D4DB890397DCC04132BF8A5.
Capability and joined-fixture hashes remain the23.19 pins; the three unrelated frontier
pins remain unchanged. Capability-use inventory found no remaining production-disabled
assertion; the default interface'sfalse and explicit unsupported-peer fixtures remain.
Whitespace PASS, HEADe9350a4dec4b5685c7c153136a93d691853ed6c3/index empty. The only known
scoped pre-commit finding is resolved; no further implementation is required for that finding.

Next: explicit scoped commit approval for five paths: this contract, the callback ownership
contract, NknSignalingTransport.BootstrapCorrelation.cs, NknBootstrapCorrelationJoinedTests.cs
and NknRegularV4PressureBackendTests.cs. Three unrelated frontier files and ignored evidence
remain excluded. No stage/commit/push, package/deployment, GUI/preflight/live, report/campaign
or agents ran. Capability remains enabled only in the uncommitted local source/Debug
candidate; full off-on-off live acceptance and its fresh allowance remain separate gates.

Accounting: conservative3m root-only12:53-to12:56 bound, intervening user idle excluded:
root4215m/cross-agent7381.9m. One new PASS1 TRX window2.1042101s; build and verification
time are inside the root bound, not double-added. Attempts9/reports3 consumed, attempt9FAIL,
historical unreconciled interval and allowance unchanged; no new campaign budget inferred.

### 23.22 Routine continuation: scoped commit and local packaging, 2026-09-12

The user requested proceeding without approval for every action, reserving approval for
potentially harmful actions. This supersedes23.21's extra routine commit-approval request.
Proceed with exactly the two contract docs, production capability file, direct joined
fixture and pressure-backend test. Preserve and exclude the three unrelated frontier files.
Retained verification is12 negotiation,3 direct joined and1 backend-ownership passes;
the two earlier expected/failing red artifacts remain failures. Source pins are unchanged.

After the scoped local commit, rebuild the canonical Release win-x64 portable with
Build-Portable.ps1 -LocalOnly, retaining the preceding package before generated-output
replacement. Validate resolved output paths and package manifests; freeze commit/package
hashes in ignored evidence. No source/test changes or aggregate reruns are needed here.
Local bridge hello/ping/shutdown and sidecar version probes are packaging checks, not live
NKN readiness or off-on-off acceptance. No push, installer, deployment or GUI launch.

The attempts9/reports3 consumption, attempt9FAIL and historical accounting interval remain
unchanged. Routine continuation does not reset an explicit cap: a fresh targeted live
attempt/report allowance remains required, with strict preflight and both rerun limits0.
The second Core-published file_tuna_v4 route is still an unproven live acceptance condition.
Actual commit, packaging outcome and this turn's accounting follow in WORK-IN-PROGRESS.md.
