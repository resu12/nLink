# Fallback-yield receipt binding: review and capture-only fix plan

2026-09-24. Reviewed HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, branch
`v0.7.0`, with the preceding test-only peer-lineage prototype still uncommitted.
Scope: inspect existing production evidence and specify the next bounded change.
This document is a proposed implementation contract, not implementation approval.

## Decision and RCA

Existing captured evidence cannot close the selected authenticated-yield-to-
Coordinator-receipt edge. New **diagnostic capture and an internal handoff** are
needed; a new acceptance rule, resend rule or authority token is not.

The production sender differs materially from the prototype. The prototype sends
the yield through `IFileTransferDataSession.SendAsync` after its test driver has
already recorded a local proof. Production Service delivery uses
`IFileTransferControlPlaneDeliveryTransport`. That sender creates separate control
and bulk ciphertexts with security capture explicitly disabled. Therefore the
prototype's matched yield ciphertext pair is not evidence that production already
captures the corresponding sender operation.

On receive, authentication evidence is captured and logged, but only the decoded
frame proceeds into the data-session queue and fallback-yield event. The Service
then creates a Coordinator receipt from that frame without the transport evidence.
Existing exact decoded-proof identities bridge Coordinator histories, but cannot
identify which authenticated ingress or send completion supplied that receipt.

This is an observability gap and a fixture-coverage limitation, not evidence of a
runtime routing defect, insecure authentication or lost file data. No historical
capture was reevaluated. The current strict recovery result remains unchanged.

## Verified production paths

Line references below are for the reviewed HEAD; all paths are repository-relative.

| Boundary | Existing behavior | Evidence limit |
| --- | --- | --- |
| Service send selection, `SessionFileTransferService.cs:19676` | Uses control-plane delivery when supported; data-session fallback returns null | Data-session fixture is not the production control-plane completion path |
| Control-plane encryption, `NknSignalingTransport.FileTransferTransportChannel.cs:337` | Serializes one frame; independently encrypts control and optional bulk envelopes | Calls the wrapper at `:5694`, which sets `captureEvidence:false` |
| Lifecycle result, `NknSignalingTransport.cs:2456` | Aggregates queue/ACK/control-copy/bulk-copy outcomes | `MessageId` is the control envelope's ID, not the successful copy's identity; a queue acceptance is not peer receipt |
| Local Service receipt, `SessionFileTransferService.cs:16171` and `:16337` | Rechecks current context/owner/authority, then applies local proof only when `PeerVisibleAny` is true | The receipt uses the decoded frame's identity/fingerprint, not a per-copy security reference |
| Authenticated ingress, `NknSignalingTransport.FileTransferTransportChannel.cs:4391` | Authenticates, decodes, validates metadata/session, retains security evidence in the dispatch closure and logs at `:4557` | `DeliverFileTransferDataFrame` at `:7386` receives no security evidence |
| Queue/event handoff, same file `:9348` and `:9471` | Queues `FileTransferReceivedDataFrame` and raises the yield event | Both carriers omit the authentication reference; the Service event callback at `SessionFileTransferService.cs:2686` forwards only `Message` |
| Coordinator receipt, `SessionFileTransferService.cs:16394` and `:16752` | Creates/applies local or peer receipts and emits existing Snapshot/effect evidence | Receipt/effect identities certify the existing Coordinator decision, not a physical envelope association |

The relevant source files are under `src/nLink.Core/FileTransfer/` and
`src/nLink.Infra.Nkn/`. `IFileTransferSignalingTransport.cs:2900`, `:2923` and
`:3224` define the result, queued-frame and yield-event carriers. Existing receipt
logs (`SessionFileTransferService.cs:9897`, `:14757`) do not restore the missing
association. The existing control-plane correlation formatter handles frontier
requests, not fallback-yield identities (`NknSignalingTransport.FileTransferTransportChannel.cs:435`).

No source of an existing complete binding was found in the inspected production
send, dispatch, receipt, security-capture or lifecycle-result paths. Matching
timestamps, local generation numbers, `PeerVisibleAny`, a raw outer message ID,
or a newly calculated digest of incomplete log fields cannot replace it.

## Proposed bounded implementation contract

Only `FileTransferFallbackTailYieldFrameV6` evidence is in scope. The original
frame serialization, encryption inputs, number/order of sends, timeouts,
cancellation, retry counts, callback scheduling and Coordinator decisions must
remain unchanged. No public wire or authority DTO changes; optional internal
diagnostic members on existing delivery/receive carriers are permitted by this
proposal. They must not participate in serialization or authority comparisons.

### 1. Capture the actual send operation and its copies

In `SendFileTransferControlPlaneFrameAsync`, retain the existing security evidence
from each actual encryption of a yield frame, instead of using the capture-off
wrapper. Use the same keys, codec metadata, ciphertext and envelope objects as
the unchanged send. Never encrypt again for diagnostics.

Create one opaque, transport-owned diagnostic reference per delivery invocation.
Associate at most two immutable envelope slots: control and optional bulk. The
control queue/ACK/copy may reuse its one envelope; retrying the existing copy does
not create another ciphertext slot. Each slot retains its own outer fingerprint,
payload fingerprint, secure sequence, installation/transport identity,
establishment and projected scope. Do not assign one slot's identity to the other.

Carry that optional reference on the original delivery result. Preserve the
original result Booleans/errors exactly, including pre/post-validation failure,
timeout and cancellation. Record per-slot outcomes from the corresponding
existing result fields; never describe `ControlQueue=true` as physical send or
peer receipt. Do not change the generic lifecycle sender or add waits to obtain
more favorable evidence. A missing bulk endpoint yields one slot, not a fabricated
bulk success. A legacy result or data-session fallback has no bound reference.

### 2. Carry the authenticated ingress through both existing paths

Create an optional transport-owned ingress reference only from the actual decoded
yield frame and its existing successful authentication capture, after metadata
and session validation. Carry it unchanged through the existing dispatch closure,
queued-frame wrapper and yield-event arguments. Preserve both routes: event-first
and queue-first processing must associate the same original ingress without
changing their scheduling or deduplication behavior.

Keep the reference separate from the wire frame and runtime authority. No ambient
current-evidence slot, static lookup registry, matching-by-frame scan or lookup of
the latest security installation may substitute for the original handoff. Legacy
frame-only calls remain valid runtime inputs but unbound diagnostic inputs.

### 3. Associate only the actual Coordinator decision

At the existing local/peer receipt application sites, use the original delivery
or ingress reference and the actual returned Coordinator Snapshot envelope. Emit
a separate diagnostic association naming the exact writer/Coordinator, boundary,
operation and receipt identity/generation/fingerprint/status. Use existing emitter
projection helpers; do not synthesize a receipt or reconstruct it from a route
cache. Retain the canonical decoded-proof identity/fingerprint used by the
existing receipt factory. Share that calculation if needed without changing its
input normalization, encoding or hash; do not duplicate it in the analyzer.

An accepted, state-changing decision may be labelled associated only after its
existing adoption. Idempotent, stale, rejected, missing-envelope and unbound cases
remain explicitly distinguishable and cannot borrow an earlier accepted decision.
The event/queue duplicate must not manufacture another accepted association.
Producer diagnostics must never authorize a send, adopt state, publish a route,
resume fallback, release a worker or create retry/liveness effects.

Security lifetime must remain attached to the captured installation: retain an
issuer-owned diagnostic handle capable of checking that captured context's
retirement flag at association. Never sample current global key/context state.
Reset/reinstall, including a same-key reinstall, produces retired/unbound evidence
when appropriate; it must not alter the runtime receipt's existing outcome.

### Diagnostic shape and privacy

Use the proposed diagnostic event `filetransfer_fallback_tail_yield_evidence`,
with `yield_evidence_version=1` and `yield_evidence_role` distinguishing `send_copy`,
`receive_dispatch` and `receipt_association`. This is not an authority event.
`yield_evidence_id` is `ftye1-` plus 32 lowercase GUID hex characters;
`yield_evidence_copy` is `control`, `bulk` or `none` (receipt association).
`yield_evidence_status` is `bound`, `unbound`, `retired` or `diagnostic_only`;
`bound` means only this local capture association, never proven peer lineage.
The existing Coordinator `receipt_status` remains a separate field.

| Field group | Required meaning |
| --- | --- |
| Version, role, capture reference | Version 1; opaque per-invocation/per-ingress reference, bound to the real writer/transport; never advertised as authenticated identity |
| Frame proof | `yield_proof_identity` and `yield_frame_fingerprint` use the existing `fte1` projection of the receipt factory's canonical identity/fingerprint; frame type and projected session/transfer are retained, never raw proof identity or plaintext |
| Per-copy security | Reuse current `ftenv1`, `ftpay1`, `ftest1`, `ftst1`, `ftsi1`, secure sequence, installation generation and status semantics; keep control/bulk slots separate |
| Send outcome | Corresponding existing queue/ACK/copy outcomes and diagnostic bound/retired/unbound/stale status; no new delivery policy |
| Receipt association | Reuse existing names/projections: `coordinator_instance_id`, `transition_id`, `coordinator_state_generation`, `operation_key`, `operation_authority_id`, `operation_generation`, `receipt_identity`, `receipt_generation`, `receipt_status`, `receipt_fingerprint`; absent on transport-only observations |
| Authority marker | `authority_effect=none` on every new diagnostic record, including an association with an already accepted Coordinator receipt |

No keys, raw peer identifiers, encrypted or plaintext payloads, raw session IDs or
new wire fields may be logged. A failure to construct/project diagnostic evidence
must omit or mark that evidence unbound without changing the existing operation.
Bound the evidence to the original object lifetime, two envelope slots and
constant-size references. No new configuration surface or generalized tracing
framework is needed.

Retain the diagnostic records in the full source capture where necessary, without
adding them to Snapshot authority families or using them as recovery evidence.
Capture line counts may increase; original authority decisions, normalized route
results, failure classification and acceptance must not. The existing local
publication carrier stays `peerLineageStatus=not_evaluated`.

## Implementation and verification order

1. Add focused tests for the unchanged canonical proof projection and real
   control-plane capture: control-only, bulk-only success, both copies, queue-only,
   all-failed, cancellation, stale completion and key retirement. Confirm distinct
   per-copy payload identities and no extra encryption/send attempts. Then add the
   transport-only diagnostic capture and internal result/ingress handoff.
2. Add Service receipt-association tests for local and peer application, event-
   first/queue-first order, idempotent duplicates, rejected/stale callbacks,
   missing legacy metadata and reset between dispatch and receipt. Assert that
   enabling/omitting the diagnostic leaves existing state, effects and outcomes
   identical. Modify only receipt-adjacent diagnostic plumbing, not authority code.
3. Extend the offline fixture with a genuine Service-owned sender as well as the
   existing Service receiver. Exercise production `Send*FallbackTailYieldProofAsync`
   and the real NKN control-plane transport over fake clients. Do not manually
   call `ApplyFallbackTailLocalProofReceipt` or emit a local-proof-sent effect for
   the measured operation. Keep original writer ownership, complete histories,
   full/compact agreement and the primary's original evaluation count. Run only
   directly affected lanes and one default-off preservation check on both hosts.

The current prototype remains a useful baseline, but its pre-authored source
receipt cannot be the positive gate for this implementation. New capture alone
also does not prove the complete peer-lineage contract. Extending the offline
consumer to validate these associations is a later bounded step; enabling the
classifier or changing acceptance requires a separate decision. No live 256 MiB
test is needed before these offline gates.

Stop and revise the contract if implementation requires changing security/replay
rules, wire/authority DTOs, actual send scheduling, receipt acceptance, route
ownership or retry behavior. Do not resolve a test failure by weakening any of
those rules or by converting a rejected callback into authority.

## Review verification and preservation

This step changes only this review document and ignored review evidence. It runs
no authority evaluator, historical reanalysis, test suite, package build or live
network/GUI scenario. The 28 pins in the preceding prototype verification seal
remain unchanged. The prior test-only implementation is preserved, uncommitted;
no commit or push is performed. Source and seal hashes for this review are recorded
in `artifacts/yield-receipt-binding-review-20260924/FINAL-VERIFICATION.json`.

The previous live verdict remains FAIL with its original 62/43/19 accounting.
Those numbers are historical primary results, not a new evaluation under the
current matcher. Recommended next action: bounded capture-only implementation
under this contract, followed by the specified offline verification.
