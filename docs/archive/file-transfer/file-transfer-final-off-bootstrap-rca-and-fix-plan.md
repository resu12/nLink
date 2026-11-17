# Final switch-off: outbound bootstrap admission RCA and bounded fix plan

Status: historical RCA/12-case characterization followed by bounded correlation
implementation and focused verification, completed on 2026-09-11. The contract is in
[the bounded correlation contract](file-transfer-bootstrap-correlation-contract.md).
Production capability remains disabled; packaging, report, preflight and live execution
remain separate gates. Historical statements below describe their original stages.

## Outcome

The first missing edge is the sender's peer-first fallback observation reaching
coordinator bootstrap issuance. A bootstrap receiver-state frame was observed by
the sender; absence of every such peer frame is not the failing edge. This is not
the previously fixed GUI second-Tuna recognition.

The outbound service gate still requires legacy V6 recovery bookkeeping before
calling `FileTransferCoordinator.IssueFileTransferFallbackV6ProofBootstrap`.
The current coordinator-owned V4 reactivation path supplies a committed route,
publication and disposition, but does not manufacture those legacy V6 markers.
Consequently the service returns before a coordinator decision is logged. The
adapter then coalesces the identical observation; the received state remains
staged, and neither direction can complete the final fallback proof exchange.

This is a source-backed admission defect. The exact live short-circuit predicate
was not directly logged: reconstructing default legacy fields from the inspected
write sites and absence of legacy epoch events is an inference, not a deserialized
live object. A real coordinator/service regression is the first implementation
gate. Other latent failures beyond that gate have not been ruled out.

## Current evidence and provenance

Source HEAD: `d654e55d308fbe1208e84c80ead035b2664a6c86`.
Latest artifact: `artifacts/filetransfer-route-acceptance/20260910T174419Z`.
Scenario: `regular-v4-live-activation-off-on-off-256mb`.
The run was escalated, rebuilt after final isolation, strict preflights passed,
and the 87-file freeze passed before and after the run. No affected production
source has changed since. The three unrelated dirty files remain untouched.

Full-capture path, relative to the artifact:
`regular-v4-live-activation-off-on-off-256mb/filetransfer-gui-log-ledger-slice.log`.
SHA256: `93d365aba19632c51d8695dbf932c480a39d45dcd98df5f27e44f690ef427a85`.
All line numbers below refer to that immutable 120121-line capture, not GUI cursor
indices. Sender writer: `p31836r1f5225c9a62c43b8`; receiver writer:
`p56416r52354cd214e54125`. Cursor/writer integrity passed with no gaps or missing
drains. Exact extracted records are retained in
`artifacts/validation/consolidation-bounded-20260907/rca-final-off-bootstrap/evidence-anchors.log`.

### Proof chain

1. First fallback has a real sender bootstrap command and delivered receipt:
   L72658/L72722, generation1, fallback epoch1, target transport epoch1.
2. Both peers publish second Tuna: sender L112298, receiver L112815; live epoch3,
   protocol4, local runtime/publication4, commit2. Sender recovered projection is
   L112353. GUI milestones recognize both directions and invoke final off at
   `17:47:52.3965608Z`, before terminal; this is not a missed GUI action.
3. Final stop is observed locally and remotely: L114567/L114673. Both adapters
   start fallback epoch2 at L114569/L114675. These are observations, not route
   publication authority.
4. Receiver observation L114707 reaches Coordinator. Command L114710 is Issued,
   generation2, source Tuna epoch3, target transport epoch1. Execution is Delivered
   at L114733 and the exact receipt is Applied at L114746. Queue acceptance is not
   used as delivery proof.
5. Sender receives the bootstrap receiver-state frame and publishes its local
   evaluation at L114830/L114831, source runtime/publication4, commit2, transport0,
   data-session generation1, trigger transport1, observation3/fallback2. The state
   is staged at L114836. Duplicate L114874 is Coalesced, and L114879 remains a
   staged duplicate. There are **zero outbound bootstrap decisions after L114830**.
6. No final epoch4 route publication exists. Later Tuna send deferral and failure
   cleanup are downstream of this admission stop, not targets for this fix.

Cross-peer wire identity is not asserted from redacted frame fingerprints or
temporal adjacency. The local command/receipt and sender-observation chains are
reported separately; sender-side staging is direct positive evidence of receipt.

The received transport-epoch announcements at L114823/L114865 are ignored as
`protocol_not_v4`. Do not make them independent route authority to bypass the
missing coordinator bootstrap command.

## Code cause and counter domains

`SessionFileTransferService.FallbackV6ProofBootstrap.cs:303-345` validates the
outbound context and physical trigger, then requires all of:

- fallback epoch greater than1;
- `IsPostRecoveryFileTunaV4LiveActivationEpoch`, requiring a positive legacy
  `LastRecoveredV6TransportLiveRouteEpochId` older than the current live route;
- `IsStalePostReactivationFallbackV6Proof`, requiring positive legacy recovered
  transport epoch, incoming epoch strictly smaller than it, legacy handoff kind
  NormalToTunaActivation and legacy target Tuna;
- exact trigger source scope.

Only after those checks does line351 reach Coordinator issuance. The early returns
do not log a reason, so adapter `Published` means the event was dispatched, not that
Coordinator accepted it. `NknSignalingTransport.Acceleration.cs:14009-14129` records
the observation key before subscriber invocation and coalesces later repeats.
Do not remove this coalescing or add periodic retries to conceal the admission gap.

The legacy helper is in `SessionFileTransferService.PullTransferSessionV4.cs:180-251`.
Legacy recovered epoch fields are written on the V6 epoch completion path in
`SessionFileTransferService.TransportEpochV6.cs:7017-7020` and `7166-7169`.
There are zero `filetransfer_v6_epoch_started` and zero
`filetransfer_v6_epoch_state_changed` events in this complete capture. The current
route transition instead applies the coordinator disposition and emits its
recovered projection through `SessionFileTransferService.RouteTransitionFallback.cs:3884`
and `:4316`; it does not set legacy recovery markers to authorize another route.

Important correction to a tempting interpretation: the
`last_recovered_transport_epoch=1` field at L91425 is emitted from
`LastRecoveredV6TransportHandoffEpoch`, as shown at
`SessionFileTransferService.PullTransferSessionV4.cs:14535`. It is **not**
`LastRecoveredV6TransportEpoch`. Yield cleanup can update the handoff counter at
`SessionFileTransferService.cs:17951` without changing the legacy epoch fields.
Do not copy one counter into the other or infer a missing NormalToTuna marker.

## Existing coverage and bounded diagnostic

`SessionFileTransferRouteTransitionGapTests.cs:82` covers outbound peer-first
bootstrap by calling `SeedRecoveredFileTunaV4SecondCycleScopeForTest` and seeding
legacy counters. It explicitly rejects a zero predecessor and a trigger epoch
equal to the legacy recovered epoch (around lines244-316). That remains a valid
negative when exact coordinator successor authority is absent; it is not coverage
of the real committed coordinator reactivation path.

The inbound runtime-unlock successor test at line790 already uses a distinct
coordinator-backed branch (`IsCurrentInboundPeerFirstFallbackV6ProofBootstrapCycle`,
service line1623), including the exact prior delivered bootstrap and committed
PostFallbackV6ToTunaV4 terminal/disposition. Its incoming FrontierRequest has an
explicit bootstrap correlation generation. The outbound ReceiverState does not.

One saved read/hash/predicate inspection passed in 1.1389591s:
`artifacts/validation/consolidation-bounded-20260907/rca-final-off-bootstrap/inspection.json`.
It calls unchanged compiled production predicates with five constructed scalar
controls. Default legacy fields and handoff-only counter1 both reject; an equal
epoch rejects even with a legacy Tuna marker; the old legacy epoch2/trigger1
control passes. This is not a full state replay, a new xUnit result, a proposed
predicate implementation, or live acceptance. Core assembly SHA256:
`401b3eb91043eb952ac250f2b05b7634b117e6cf589a4374a8479ca0f1135890`.

## Fix plan and approval gates

### A. Reproduce through real coordinator history; do not patch predicates first

Add one focused outbound regression in the existing route-transition test lane:
initial RegularV4 -> TunaV4 -> first coordinator bootstrap delivered -> committed
FallbackV6 -> committed/disposition-applied TunaV4 -> final stop observation.
Leave legacy V6 epoch fields at the values produced by that path. Do not seed a
legacy Tuna epoch or alter source/checkpoint counters to satisfy the old test.

Deliver the actual generation-less bootstrap ReceiverState in both arrival orders
(evaluation before staging and staging before evaluation). Reproduce the current
zero-command/staged-frame result. Confirm the exact current source, prior delivered
local receipt, data-session reference and coordinator committed successor in the
fixture. If the real path fails an earlier guard, stop and revise this RCA before
changing production code.

### B. Review the successor contract, including receiver-state replay safety

Recommended admission design: a distinct coordinator-validated outbound successor
branch using the exact prior **local** delivered bootstrap receipt and current
committed PostFallbackV6ToTunaV4 transition. Reuse the read-only Coordinator
predicate at `FileTransferCoordinator.cs:6023` where its contract fits. Preserve
the existing legacy branch and all physical-trigger/source/data-session checks.
Require current source ownership, matching direction/session/transfer, completed
prior local delivery, the exact next local fallback cycle, no conflicting pending
command, and the committed/disposition-applied reactivation lineage. Treat the
adapter observation as a request to Coordinator, never as an unlock or route token.

There is a mandatory design gate before runtime approval:

- ReceiverState parsing deliberately returns `TriggerBootstrapGeneration=0`
  (`NknSignalingTransport.Acceleration.cs:13860`); the outbound staged binding also
  records0. This is current representation, not a malformed live frame.
- Both first and second bootstrap target transport epoch1 because the committed
  Tuna source transport epoch resets to0. The current outbound peer-boundary check
  at `FileTransferCoordinator.cs:6519` validates profile and target epoch but has
  no bootstrap-generation check, unlike the inbound FrontierRequest branch.
- Therefore merely copying the inbound helper, treating0 as the next generation,
  or deleting the legacy conjunct would leave the replay contract unproved.
  Matching session/transfer/current local route hints alone must not re-authorize
  a delayed frame from a previous cycle.

The test-only contract review must include an old-cycle ReceiverState delivered
after reactivation, including an unchanged-frontier/identical-payload control.
Prove whether existing exact coordinator/retained-frame evidence distinguishes
the cycles. If it cannot, stop for an explicit bounded correlation representation
decision. A possible design is a coordinator-issued request correlation echoed in
the peer response, qualified by source/data-session identity; an uncorrelated
precommand frame may wake evaluation but must not complete new-cycle route proof.
Do not equate independent peer generation counters, repurpose a blank repair field,
silently change a wire/catalog profile, or fabricate missing historical identity.

Recommended next authorization is **test-only reproduction plus this contract
review**, not an unconditional runtime patch. The runtime fix scope is decided
after the replay case closes; a receiver correlation extension would need explicit
approval beyond a service-only admission change.

### C. Bounded implementation after A/B pass

Implement only the admitted sender successor path and the minimum approved proof
binding necessary to keep old/stale callbacks diagnostic-only. Keep command issue,
send execution, delivered receipt, peer-proof binding, route commit/publication
and disposition as separate coordinator-owned edges. Add a bounded rejection
diagnostic at the changed admission boundary so a future failure does not depend
on the 30-second enrichment worker to identify the first rejected predicate.

Do not change GUI fault timing, fallback retry cadence, source chunk size, bridge
queues/concurrency, wallet behavior, installer, performance policy or the parked
stopped-delivery classifier. Do not rewrite historical verdicts or fill receipts.

### D. Verification and stop rules

- New positive fixture: one exact next local command and delivery, with both
  arrival orders and duplicate observation handling; no fabricated legacy fields.
- Negatives: absent/failed/foreign prior receipt; wrong or missing committed
  reactivation/disposition; stale runtime/publication/commit/leg/checkpoint;
  replaced data-session reference or generation; wrong frame epoch/profile;
  terminal/pause/supersession before evaluation, execution or callback; conflicting
  pending command; duplicate/old-cycle proof; unchanged-frontier replay.
- All rejected/stale cases must leave authority generation, route/publication,
  progress/checkpoint/worker state and physical-effect counts unchanged. Exact
  duplicate current callbacks must be idempotent, not create a new cycle.
- Run only affected sender/Core/Infra tests, preserving the existing legacy,
  inbound peer-first, pending-write and command-first controls. Add analyzer tests
  only if evidence semantics change; at most one broader contract lane after the
  implementation stabilizes. No full aggregate/matrix gate here.
- Review/commit separately. Only with new live approval: isolate unrelated work,
  rebuild canonical portable, freeze, and run this single scenario with both
  rerun limits0. Require both final epoch4 fallback publications, full cycle proof,
  delivery/SHA and attempt-owned evidence finalization independently. Stop at the
  first new proof edge; do not tune throughput to mask correctness.

## Classification timeout is secondary and remains separate

The original GUI exception is the final fallback projection timeout. Classification
was requested at17:49:51.1529141Z with a30000ms budget against the132492598-byte
semantic slice. Enrichment ended at30100ms, parsed/visit/lookup counters0, and no
completed classification response was retained. Those zeros do not establish
whether startup, input loading or evaluation consumed the budget. This RCA does
not diagnose that worker's internals or authorize a rerun/timeout increase.

The placeholder `evidence_enrichment_pending` and false route/listener/offer fields
are not runtime facts and contradict raw positive route evidence if interpreted
literally. Preserve the original exception. A separate bounded classifier-startup
RCA may be considered later; it must not replace the first runtime proof edge.

## Boundaries and retained result

Attempt9 remains FAIL; no delivery/SHA or final route-cycle acceptance is claimed.
Nine cumulative production attempts and three standalone reports remain consumed.
No new allowance is used or reopened. Scope of this turn is source inspection,
immutable evidence extraction, pure predicate diagnostics and this plan only.

## Test-only implementation result (2026-09-10)

Gates A/B now have deterministic characterization tests in
`tests/nLink.SmokeTests.Core/SessionFileTransferOutboundBootstrapSuccessorTests.cs`.
The stabilized focused lane passes **12/12** cases, including four new cases.
This is not a runtime fix or live acceptance result.

Gate A reproduces the sender failure in both arrival orders through actual
Coordinator start, initial Tuna commit, first bootstrap issue/delivered receipt,
fallback commit and reactivation commit. The fixture uses the existing Coordinator
prepare/drain/retained-frame/disposition helpers and applies Coordinator-produced
route decisions through the production service projection. Physical drain and
delivery receipts are deterministic test inputs, not live transport observations.
It does not install a synthetic terminal operation or seed legacy recovery markers.
The source data-session identity/generation is preserved through retry setup.

On the final Tuna epoch3, the prior local receipt and committed-reactivation
predicate are valid; exact physical-trigger/source/data-session checks pass.
Legacy live/transport epoch markers remain0, kindNone and targetUnknown.
Both legacy admission predicates reject. Two evaluations produce no new command,
send or transition execution; the peer frame remains staged and route, checkpoint,
publication, generation and progress remain unchanged. This confirms the RCA's
first missing edge without changing the production predicate.

Gate B confirms a representation gap. An isolated Coordinator-issued second
command has a new authority and generation2 but reuses target transport epoch1.
Before local delivery, peer proof is rejected. After local delivery, the old
cycle1 ReceiverState is accepted as generation2 proof, as is a fresh response.
The unchanged-frontier control uses identical canonical frame identity; the other
control advances state epoch/frontier. Neither cycle can be inferred safely from
the current ReceiverState representation. Wrong epoch/profile, replaced session
generation, terminal state and the old local delivered receipt remain rejected or
non-mutating. The replay test does not commit a final fallback route.

The positive assertions for old-frame acceptance deliberately characterize the
current unsafe candidate contract; they are **not** a desired compatibility rule.
Replace them with rejection assertions when the approved correlation fix lands.

### Required decision before runtime implementation

Do not approve a service-only legacy-guard relaxation. The next bounded scope is
an explicit Coordinator-owned request/response correlation contract across Core,
wire serialization/profile, Infra and focused tests:

1. A sender-issued bootstrap request carries a cycle-unique correlation bound to
   its exact local command and source/data-session scope; the ReceiverState echoes
   it in an explicit bootstrap field, not a repurposed repair field.
2. An uncorrelated precommand ReceiverState may request evaluation, but cannot
   complete the new command's peer proof. A local send/delivery receipt alone does
   not prove that an incoming response belongs to that request.
3. If the receiver already delivered its unsolicited bootstrap state, a later
   request still needs a Coordinator-authorized correlated response, with exact
   duplicate handling and no new route/runtime authority from adapter state.
4. Peer request identity and local bootstrap generation remain separate domains.
   Define legacy/new-profile interoperability and fail-closed behavior explicitly;
   do not silently reinterpret current request-generation parsing.
5. Preserve all current authority, source invalidation, stale callback and route
   invariants. Prove both arrival orders, duplicate request/response, prior-cycle
   replay, identical payload and replaced/terminal scope before enabling admission.

This is a proposed design scope, not an approved wire-field name or implementation.
Per Gate B's stop rule, no production admission/protocol change was made. No commit,
portable package, live launch, report, timeout change or historical verdict rewrite
occurred. Attempt9 remains FAIL; nine live attempts and three reports remain used.
Detailed verification: `artifacts/validation/consolidation-bounded-20260907/outbound-bootstrap-successor/RESULTS.md`.

### Correlation design prepared after approval

The [bounded contract](file-transfer-bootstrap-correlation-contract.md) proposes
explicit negotiation and two new V6 frame codes, preserving legacy wire bytes.
A sender request ID plus a receiver challenge provides mutual current-cycle echo
proof; a sender-only echo is not treated as a complete inbound replay contract.
Local generations remain independent, and Announce/Response plus Solicit/Confirm
have distinct Coordinator-owned execution receipts under each base bootstrap.
This is design only; no additional tests, build, runtime change, commit or live
attempt occurred. The next gate is contract review before bounded implementation.

### Correlation contract revision and targeted re-review

The subsequent review requested R1-R3 corrections. The revised contract now elects
and serializes local phases through exact Delivered AND Settled predecessor gates,
tracks every epoch/proof transport copy independently of first-success delivery,
forbids Held after partial submission, and defines the existing sender-only
SessionOpen agreement sequence. Targeted source re-review closes these three design
findings; executable proof remains part of bounded implementation, not this result.
The next gate is bounded implementation approval. Runtime and tests remain unchanged;
no commit, build, test rerun, live attempt or standalone report was performed.

### Bounded implementation and verification completed, 2026-09-11

The approved implementation adds negotiated Coordinator-owned correlation, exact
phase/copy receipts, service/adapter integration and catalog-bound evidence checks.
Follow-up review fixes close successor-retirement evidence, phase-event ownership
and complete agreement-lineage validation. Production capability advertising remains
false; modeled/FakeNkn evidence is not live route acceptance.

Retained verification includes93/93 focused Core/service/Infra tests,38/38 subsequent
Coordinator/evidence checks, and the latest6/6 agreement plus4/4 exchange-evidence
tests on PowerShell5.1 and7. These are separate, partly overlapping invocations, not
a combined distinct-case total. The one broader Coordinator run remains429/431;
its two admission-fixture failures were corrected and passed in a focused4/4 rerun.
No broader rerun or historical verdict rewrite is claimed. Details and immutable
TRX artifacts are under
`artifacts/validation/consolidation-bounded-20260907/bootstrap-correlation-implementation/`.
The scoped commit does not authorize production enablement or another live attempt;
attempt9 remains FAIL and attempts9/reports3 remain consumed.
