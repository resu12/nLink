# Initial Tuna to fallback: boundary review and fix contract

2026-09-21. Base HEAD: `faf838d7e39e2ffbb422e8ff0b261c58e85330f1`.
Status: Core source/exchange, transition-proof and service/adapter handoff implemented and
verified offline. Pre-live source review is complete; local checkpoint and portable rebuild remain.
See `file-transfer-initial-tuna-fallback-precommit-20260921.md`. The checkpoints below supersede the
review-only status, while preserving the original diagnostic history.

## Evidence and limits

The latest live run, `artifacts/initial-tuna-checkpoint-20260921-faf838d7/live/20260921T144324Z`,
passes `active-tuna-v4-64mb` with complete primary current-runtime proof. It has
no switch-off or fallback step and cannot prove this contract. Older matrix
failures and the live-256 exact-delivery finding are not reevaluated here.

The original review added only tests and this document, with no production
change, commit, portable rebuild, live NKN/GUI test, second authority evaluation
or push. The joined diagnostics use fake physical/network delivery with the real
preparation, transport, service and Coordinator code. They do not invoke a live
stop or claim successful fallback. Evidence and commands are recorded in
`artifacts/initial-tuna-fallback-boundary-20260921/RESULTS.md`.

## Reproduced first boundaries (before this implementation)

1. **Adapter observation capture omits the legitimate initial source.** Four
   joined cases cover both payer roles and both send directions. After public
   preparation, offer/admission and manifest processing, initial feedback is
   held before submission to keep the source nonterminal. On each peer, Core
   accepts the current initial-Tuna source binding, and the adapter has the
   exact forwarded publication, Tuna-v4 hint, current data session and enabled
   correlation. Its publication has no commit. Both exact local stop reasons,
   `runtime_disabled` and `header_switch_off`, return zero captured sources.
   Remote/unknown reasons also remain rejected.
2. **A valid observation cannot authorize the Core hold/exchange.** Four Core
   cases cover inbound/outbound and independent runtime/leg generations. The
   source and checkpoint are current and the local-stop observation is valid.
   `CanStartFallbackBootstrapExchange` is false. Hold preparation returns
   `bootstrap_source_hold_initiation_rejected`, without a command or applied
   operation. Calling exchange issuance directly returns
   `bootstrap_source_hold_receipt_required`; no route or transition state changes.

These are intentionally green characterization tests of an unsupported path,
not positive fallback acceptance. Convert them to positive lifecycle tests when
implementing the contract; retain their diagnostic-only rejection controls.

## Why changing one predicate is insufficient

| Boundary | Current assumption | Required preservation |
| --- | --- | --- |
| `NknSignalingTransport.BootstrapInitiation.cs` capture | Publication commit generation must be positive. | Captured adapter state remains evidence; Core revalidates exact source and session. |
| `FileTransferBootstrapExchange.cs` first exchange | Source must be committed Regular-v4 to Tuna-v4 activation. | No reuse of a prior/incomplete exchange; successor exchanges retain exact committed reactivation lineage. |
| `FileTransferBootstrapSourceHold.cs` preparation | Both exchange admission and a current commit are required. | Core-issued hold, exact captured writes/feedback/callbacks, physical settlement and durable checkpoint remain mandatory. |
| `FileTransferCoordinator.cs` bootstrap command/issuance | Non-null commit and current live epoch; `IsDefault` rejects no commit. | Explicit source identity and currentness through delivery, settlement, peer proof and invalidation. |
| Service peer-frame binding and wake construction | Commit token is non-null and supplies route/epoch fields. | Exact session/provider, checkpoint, bootstrap ID and authenticated peer-frame correlation. |
| Fallback transition proof and route-kind validation | Tuna-to-fallback requires a source commit. A no-commit proof instead requires a predecessor transition. | Initial published Tuna is neither committed activation nor uncommitted Regular activation. Do not relabel it as either. |
| Bootstrap ID and snapshot projection | Hash and boundary code dereference the commit. | Unambiguous source encoding, unchanged committed-source identity, authoritative snapshot continuity. |

Source anchors include `CaptureFallbackBootstrapLocalStopSources`,
`CanStartFallbackBootstrapExchange`, `PrepareFallbackBootstrapSourceHoldCore`,
`IssueFileTransferFallbackV6ProofBootstrapCore`,
`IsFileTransferFallbackV6ProofBootstrapSourceScopeCurrent`,
`CreateBootstrapCorrelationWakeEvaluation`, `CreateFallbackBootstrapCommandId`,
`TryValidateFileTransferFallbackProofBoundary` and the route-kind switch.
Only the two boundaries above were newly executed diagnostically; downstream
constraints are source-inspection findings, not additional observed live failures.

## Recommended bounded contract

Represent an **initial-publication source** as an explicit alternative to the
existing **transition-committed source** in the Core-owned bootstrap command and
fallback proof. Prefer extending the existing Tuna-v4 to fallback-v6 transition
with that explicit source alternative over inventing a second transition engine.
Do not make a bare null commit sufficient or synthesize a commit/predecessor.

For the initial alternative, Core must require all of:

- Exact current session, transfer, direction, runtime owner, leg and publication;
  Tuna route/protocol 4; ordinary first `RoutePublication`, generation 1, epoch 0.
- No current or historical route commit/live epoch, no terminal generation,
  no current/terminal route-transition predecessor masquerading as initial state.
- Current data-session identity/generation and authenticated enabled bootstrap
  agreement. Runtime and leg generations need not equal one or each other.
- First exchange only: no previous exchange, pending bootstrap, terminal receipt
  or bootstrap-generation history that could be discarded to manufacture a new
  initial admission. Reentry is idempotent only for the exact already-issued
  operation; rejected, copied or stale observations remain diagnostic-only.

The existing data-source projection may supply the tuple for comparison. It is
not fallback permission. Core alone issues the source hold and exchange after
validating initiation and history; the adapter only forwards the observation.

Preserve the existing ordered proof chain:

`local observation / exact peer wake -> Core hold -> captured work drains ->
durable receipt -> correlated exchange -> delivered AND settled phases -> exact
peer proof -> Core transition prepare/drain/commit -> applied publication`.

Until the last boundary, no fallback route publication or runtime activation is
authorized. V6 bootstrap control frames are not a V6 Tuna data route. The final
route is `post_tuna_fallback_v6` on regular NKN, with its epoch allocated by Core.
Do not hard-code the committed-activation cycle's epoch numbers for an initial
epoch-zero source. Subsequent reactivation must retain real predecessor lineage.

The initial-source variant must be carried unchanged through service wake and
pending-write bindings, command IDs, receipts, peer proof, transition authority,
callback successor retirement, and snapshots. Preserve committed-source ID/hash
serialization. Encode absence and source kind explicitly for initial sources;
never encode a default commit as a real commit. If snapshot shape/catalog changes
are necessary, version them deliberately; do not edit frozen catalog hashes.

## Implementation and verification sequence

1. **Core source alternative.** Add the exact initial-publication shape and
   history validation, then carry it through hold, bootstrap command and proof
   issuance/currentness. Convert the four Core diagnostics into lifecycle
   positives with real receipts. Keep committed and reactivation paths unchanged.
2. **Service/adapter handoff.** Capture the copied initial publication for exact
   local stop reasons and let Core decide. Extend only the affected peer-frame,
   pending-write and transition consumers. Convert joined diagnostics to actual
   stop -> correlated exchange -> both-peer fallback publication -> payload/SHA
   tests for both payer roles/directions, then sender-only/receiver-only/dual stop.
3. **Authority/evidence controls.** Use real emitted snapshots and independently
   verify full/compact parity and route consistency. Cover missing hold/capture/
   settlement, changed source/session/provider, checkpoint mutation, republication,
   erased commit history, terminal/reset/route races, stale or wrong-issuer peer
   IDs, duplicate/conflicting delivery and late source callbacks. None may grant
   route, repair, retry or recovery authority. Include committed fallback and
   second-cycle reactivation controls; do not weaken their exact lineage.
4. **Focused gate, then targeted live.** Run only new/affected Core, service,
   adapter and analyzer cases, plus at most one relevant contract lane after
   stabilization. Review/checkpoint and rebuild the portable before one affected
   switch-off scenario under strict escalated preflights. Stop at its first new
   proof edge. No full-matrix or live-256 rerun follows automatically.

No timer, retry, queue, chunk, throughput, wallet, installer or screenshare
changes are part of this contract. The parent parity-summary mirror, regular
inbound terminal proof and live-256 rejected-ingress disposition stay separate.

## Original review result

The initial-source gap is reproduced and the required contract expansion is
identified. It is not safe to approve an adapter-only or nullable-token-only
implementation. The next implementation should follow the explicit source
alternative and proof chain above; this review itself leaves runtime unchanged.

Review-only verification: eight new diagnostics pass across focused runs; nine
existing nearby controls pass. The first diagnostic run contained four Core
passes and four joined-test assertion failures: the new test expected the token
reason `new_transfer` instead of the forwarded decision reason `accepted`.
The test now checks `accepted` explicitly and compares the complete projection
using that reason. No production behavior or authority comparison was relaxed.

## Core source/exchange implementation checkpoint

The first bounded implementation adds `FileTransferInitialTunaFallbackSource`,
an explicit publication/session alternative on the Core bootstrap command.
`SourceRouteCommit` is nullable only alongside that valid initial source. A
missing source, mixed initial/committed source, altered initial publication,
changed generation or nonzero initial epoch is invalid. Core recaptures the
exact source for currentness; the copied descriptor alone grants no authority.

Eligibility requires the exact initial data-source binding plus no transition
operation, supersession, terminal transition or transition-generation history.
First exchange admission also requires no previous exchange, pending bootstrap,
bootstrap receipt or bootstrap-generation history. Hold capture and the matching
durable drain receipt remain necessary before issuance. Once issued, the source
is pinned through phase commands, delivery/settlement receipts and peer proof.

The existing committed-source command-ID fields are unchanged; the independent
UTF-8 golden vectors pass. Initial IDs encode an explicit initial-publication
marker and commit absence instead of a synthetic commit. Snapshot projection
records epoch/commit zero and the existing authoritative initial owner; no
analyzer/catalog schema or frozen hash was changed. Two existing log projections
now safely render the nullable commit generation; they grant no authority.

The initial Core positive cases pass in both directions with independent
runtime/leg generations. Source/history mutation and late-receipt controls pass.
A paired initial-Coordinator exchange now reaches completed exact peer proof
only after both delivery and settlement, while both routes remain Tuna v4 and
no commit is invented. Real emitted initial exchange snapshots pass independent
full/compact consumers and route consistency on both PowerShell hosts.

Final coverage audit: all 70 selected affected cases have passing results;
including the focused initial and golden cases, 82 distinct cases passed across
the retained runs. Both intermediate aborted runs remain explicitly documented
as aborted; their unfinished cases were completed in isolated runs. Committed-
source full/compact analyzer parity passes in all four selected cases.

### Boundary at the source/exchange checkpoint

This milestone does **not** enable initial-Tuna switch-off in the adapter or
publish a fallback route. The adapter still rejects commit-less local-stop
capture, service peer/wake bindings remain commit-only, and the transition proof
still rejects a bare missing commit. The joined diagnostic retains those checks.
The next bounded implementation must carry this same explicit source into the
fallback proof and transition validation/currentness, then service/adapter
handoff; preserve source validity through its exact in-flight transition, not an
arbitrary transition. A new source field is not permission to bypass that gate.

No commit, portable build, GUI/live run or push occurred in this milestone.
Detailed verification, intermediate failures and the next integration boundary:
`artifacts/initial-tuna-fallback-core-20260921/RESULTS.md`.

## Core transition-proof implementation checkpoint

The explicit initial source now travels in `FileTransferFallbackTransitionProofBoundary`.
It is mutually exclusive with a source commit or predecessor transition. A bare
null commit remains invalid. Initial Tuna uses the existing
`TunaV4ToPostTunaFallbackV6` kind, not the uncommitted Regular activation path.
Preparation and commit require the completed current correlated exchange, exact
checkpoint, delivery receipt, peer frame and initial source. Correlation cannot
be disabled to bypass that requirement.

The source remains current only through its exact first Prepared/Draining
transition, with matching authority, proof and generation. A terminal or foreign
transition, source replacement, missing hold/delivery, or invalidated/incomplete
exchange rejects late drain/commit without publishing a route. Inbound and
outbound Core lifecycle tests reach the real fallback publication at epoch 1,
then apply the exact retained-frame and disposition receipts. No source commit
or predecessor is invented; duplicate preparation is idempotent.

The shared route-consistency reducer now permits zero retired commits only for
the first successor of a validated initial Tuna publication (publication 1,
epoch/commit/terminal zero, no earlier route transition). Exact publication and
owner retirement, transition and effect joins remain required. Existing
committed-source cycles still require their retired commit. No frozen catalog,
schema hash, timeout or route invariant was relaxed.

Verification found Debug stack pressure from large persistent-state value
copies. Narrow extraction helpers unwind those copies before nested proof/wake
validation; two service test drivers also separate fixture setup and snapshot
assertions. Scheduling, receipt order and authority checks remain unchanged.
The analyzer fixture supplies explicitly modeled Service effects from actual
Core receipts; it is not a physical adapter or live test.

Final exact-name coverage audit: **86/86 selected cases have passing results**
across focused runs, including initial lifecycle/rejection cases, full/compact
evidence on both PowerShell hosts, existing two-direction five-route-cycle
controls, committed service successor behavior and the closed adapter gate.
Intermediate aborted runs are not counted as completed lanes; see
`artifacts/initial-tuna-fallback-transition-20260921/RESULTS.md`.

Next: carry the same source through Service peer/wake/pending-write bindings and
transition construction, then enable exact adapter local-stop observation and
prove the joined lifecycle. Initial fallback is still not enabled end to end.
No commit, push, portable build, GUI/live test or historical live reevaluation
occurred in this milestone.

## Service/adapter handoff checkpoint

The Service now preserves the explicit Core-validated initial source in peer-frame
and pending-write bindings. Wake construction uses its exact publication and
zero absent-commit generation. Binding comparisons, write-drain refresh and
command matching include that source; null/default/mixed sources do not substitute
for it. Legacy peer-frame admission still requires a committed source. Transition
construction copies the initial descriptor only from the current completed Core
peer-proof boundary, matching both peer evidence and retained-frame identity.

The adapter now captures an initial publication for the two exact local reasons
`runtime_disabled` and `header_switch_off`. It forwards copied evidence only;
Service and Core retain all source, history, hold, drain and exchange decisions.
Remote, unknown and differently cased reasons remain rejected. No runtime timer,
retry, queue, throughput or route-invariant changes were made.

The public preparation/admission fixture now proves both-peer fallback
publication, disposition and matching SHA-256 payload completion in all 12
payer-role/send-direction/stop-mode combinations. Four capture cases additionally
check exact initial binding, nine malformed/stale stop variants per direction,
and seven binding mutations. Replaying the original stop after fallback cannot
change Core state. Three owned-write cases verify an exact nullable-commit wake,
no exchange before physical write drain, successful fallback after current drain,
and no progress/publication from a replaced service generation or paused source.
Those three cases inject a receive batch without the sender ledger and are
bounded drain/currentness evidence, not end-to-end payload tests.

Actual joined Service/Core/adapter logs pass artifact completeness, independent
full/compact parity and route consistency on both `powershell` and `pwsh`.
These are offline fake-network logs, not live NKN validation. Existing modeled
Core transition evidence and its mutation controls also pass.

Final exact-name coverage is **103/103 selected cases** across focused runs:
19 initial-source cases, 30 existing Service correlation/initiation cases,
7 committed-source joined controls, 37 legacy bootstrap controls, 6 initial
Core transition cases and 4 analyzer cases. The combined final gate stopped
after 85 passes in a committed receiver-only fixture; it is not a completed
aggregate pass. Its mock sender loop repeatedly produced synchronous failed
sends before the peer wake. The fixture now keeps its existing source read held
until both fallback publications (local-stop correlation uses separate control
pumps). All seven affected joined controls then pass. Two legacy fixtures also
needed setup/execution separation to unwind large Debug persistent-state copies;
their assertions and production scheduling are unchanged. Full intermediate
results and the final coverage union are in
`artifacts/initial-tuna-fallback-service-20260921/RESULTS.md`.

Next: pre-live review and local checkpoint, rebuild the portable, then one
targeted initial-Tuna switch-off scenario with escalated strict preflights.
No commit, push, portable build, GUI/live test, live-256 rerun or full matrix
occurred in this checkpoint. This does not resolve or reevaluate the separate
historical live-256 rejected-ingress or regular inbound terminal proof edges.
