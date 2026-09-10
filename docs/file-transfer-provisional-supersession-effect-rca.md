# Provisional retry supersession is mistaken for physical settlement

## Status and scope

The bounded analyzer fix is implemented on base HEAD `b955354f`, preserving all
previous uncommitted rollback, catalog and test work. This is development-fixture
evidence, not a new live failure. The original RCA below records the pre-fix behavior.

The saved service control now clears574 and keeps restored A at576 unresolved
`1/1/0/1`, with exact PS5.1/PS7 full/compact/shared-GUI parity. Historical
helper11350 remains unresolved `1/1/0/1` and was not reanalyzed.

The complete later-A continuation gate is **not green**: it exposes a separate
old-B-terminal installation-effect requirement. Its failing regression is retained,
not skipped or weakened. No commit, whole retained report, portable rebuild,
preflight or live run is authorized or performed in this slice.

## Evidence and first false finding

Input:
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/confirmation-compensation/runtime-02.log`

- SHA-256: `4AE11E0B96627CEA8C72020A3B12D087A593BD3C26E93FD62BF35115A7F40F84`.
- Coordinator: `fte1-9c2f1b377d1004f144f8115a`.
- Writer: `p15196r43f47ddf087f42f6`.
- Scope inspected:28 original records, including17 transitions and9 effects.
- Current stream: complete `SnapshotV1Authoritative`, zero validation errors and
  legacy selections; prior independent full/compact parity passed.

Canonical false finding:

- Writer sequence/log line **574**.
- Transition `fte1-12d0afaba73c9ce7b3397040`, generation **14 -> 15**.
- `CommitRuntimeUnlockRetryDispatchInstall/Accepted`.
- Reason `retry_dispatch_install_committed`.
- Selected class `runtime_unlock_retry_dispatch_contract_unsettled`.
- Domain/phase `route_runtime/runtime_unlock_retry`.
- Current quartet **1/1/0/1**.
- RCA variant:
  `provisional_superseded_receipt_required_as_physical_settlement_after_exact_compensation`.

The authority lineage is exact:

| Sequence | Coordinator boundary |
|---|---|
| 553 / 555 / 558 | A authorized, staged, install receipt issued |
| 560 | A installed, generation9 -> 10 |
| 564 | A becomes AliasBound |
| 567 / 569 / 572 | Active successor B prepared, staged, receipt issued |
| 574 | A installed role retires; A retained role enters Settled/Superseded; B installs |
| 575 | B's exact install effect |
| 576 | Failed confirmation restores A AliasBound, removes retained A, invalidates B |

A is `fte1-3bb1fdb2414012da8e3616d6`, authority generation1, offer generation18.
B is `fte1-e288ced9dcea34af2affc30a`, authority generation2, predecessor A.
The final capture contains no later A dispatch or settlement closing its restored
operation. Stream completion is not proof that every operation completed.

## RCA

### 1. Intent and observation are different ownership stages

`FileTransferCoordinator.cs:4153` deliberately authors the provisional report
when installing B. A is retained, not entered into final settlement history.
Its snapshot receipt validates this Coordinator state mutation; it does not
prove that the bridge physically retired A.

Both service directions call operational confirmation before predecessor cleanup
(`SessionFileTransferService.cs:6638` and6763). A false result takes exact
compensation and returns before any predecessor-settlement callback. Therefore
no `settlement_observed` effect belongs to the provisional receipt or to its
compensating removal.

On true confirmation, Infra retains A and blocks B's follow-up until the exact
Coordinator-authored predecessor settlement is accepted. Only afterward does
Core `ObserveRuntimeUnlockRetryDispatchSettlement` move A from retained to
terminal/history (`FileTransferCoordinator.cs:17276`). Neither B's installation
nor confirmation alone proves this settlement or subsequent execution.

The evaluator's blanket `settlement -> settlement_observed` mapping at
`CoordinatorSnapshotEvidence.ps1:12674` does not distinguish those stages. It
reports the absent physical observation at574 even though exact compensation
cancelled the provisional handoff at576.

### 2. Removing only that demand would hide a live authority

The Family-2 reducer combines operation roles under an authority and chooses
`$history[-1]` at12852. At576, both restored `attempt_installed: None -> AliasBound`
and removed `attempt_retained_predecessor: Settled -> None` have the same boundary
sort keys. The latter row can hide the former. Descriptor order is not authority.

A memory-only diagnostic excluded the settlement-effect demand for only the
exact574 transition/retained operation/Superseded receipt. Current evaluation
reports574 unresolved; that isolated exclusion reports **no failed boundaries**,
despite the unchanged576 restored AliasBound operation. This is proof that a
receipt exemption alone is unsafe, not a passing counterfactual to implement.

The two scoped evaluations took1.660 seconds. Evaluator and input hashes were
identical before/after; no evidence, source, effect or receipt was rewritten.
The evaluator source hash was
`2AF22DFB9CAEF2A1240ECF4B4F16FCEF018CF66592A731B3D10C0F89CF9EF894`.

### 3. Competing findings overwrite one aggregate boundary

Each authority group mutates its shared Boundary object at12714-12727. Removing
B's install effect produces two findings on574: A's false missing settlement and
B's genuine missing installation. Their taxonomy is whichever authority runs
last, causing the previously reproduced PS5/PS7 disagreement. Likewise, cross-role
tie order changes whether576 is independently reported.

The previous catalog tests intentionally characterize these differences; they
do not claim complete cross-host semantic parity. Sorting roles or authorities
lexically would conceal rather than fix ownership and arbitration.

## Recommended implementation order

### 1. Reproduce the complete expected outcome first

Extend the existing genuine active compensation fixture and shared support script.
Before changing effect requirements, assert that installed A survives576 as an
open role and that B remains Invalidated. Include descriptor reordering and the
missing-B-install control. No new runtime harness or seeded successful route is
needed.

Use the valid outbound Regular-V4 active fixture. Keep existing two-direction
service and first/MissingDispatch controls. Do not mutate a canonical paired
request with `ListenerRearmRequirement=None` into an active-supersession request
or claim an ungenerated inbound active positive.

### 2. Retain exact participating subjects and deterministic findings

Reuse the existing validated four-role commit and compensation-history checks.
Build one private index for these participants, keyed by coordinator, writer,
session/transfer/direction, transition, authority/generation, operation role/key,
receipt and source lineage. Store accepted immutable subjects, not aliases or
authority-wide Boolean exemptions.

Compensation must have an **Applied** shape and exact prior commit history.
Membership in `$retryConfirmationCompensations` is insufficient: that Boolean
cache also admits unchanged diagnostic decisions. Rejected, idempotent,
ambiguous, mismatched or poisoned attempts cannot establish cancellation.

For affected participants, collect findings before updating their shared boundary.
Choose deterministically at each atomic boundary: missing stage/install proof
before an unsettled-contract finding; use exact operation/receipt/rule identities
as stable ties. Keep all private findings available to validate selection, but
do not sum role rows into a changed aggregate quartet. Preserve existing1/1/0/1
atomic-boundary counting and cross-boundary arbitration. Do not refactor unrelated
Family-2 or other-family selection.

### 3. Reduce role state by validated atomic generations

For those same subjects, fold complete boundaries by Coordinator generation,
not physical line order or timestamp adjacency. Reduce each structural operation
key/role independently, applying all deltas in one boundary before deciding which
roles remain live.

- The exact commit moves A installed -> retained and installs B.
- The exact Applied compensation removes retained A and B installed, restores
  A installed with its original receipts, and creates B terminal Invalidated.
- Removed retained A cannot erase restored installed A.
- Reopening the installed key is permitted only by that exact compensation
  backlink; arbitrary A -> B -> A restoration remains invalid.
- Validate generation/hash continuity, scope, immutable receipts and unique
  role transitions. Mutation, replacement, terminal poison or conflicting
  duplicates must not be neutralized by a later matching-looking row.

Use the reduced current role for the existing open-contract classification. Do
not count the restoration as recovery or infer later dispatch/settlement. No
new public snapshot field, contract version or catalog hash is needed.

### 4. Only then cancel the false provisional effect obligation

For the exact provisional retained Superseded receipt whose handoff is cancelled
by that validated Applied inverse, do not require a physical settlement effect.
Exempt only that receipt, not the whole boundary or its stage/install/execution
receipts. Keep compensation's existing prohibition on authority effects.

Pending provisional intent without exact cancellation remains unresolved; lack
of an effect must not itself prove cancellation. An arbitrary Superseded receipt,
unchanged diagnostic compensation or terminal receipt must not qualify. Original
A/B installation effects remain mandatory, as do real observed settlements.

This first fix does not add support for successful retained-to-terminal Superseded
retirement. Its catalog/effect path remains a separate step. Do not fabricate an
effect or broaden the generic receipt rule to make that path pass.

### 5. Remove only the now-resolved test deferrals

Require PS5.1/PS7 to agree on exact selected metadata and total semantic findings
for this bounded matrix. Each independently evaluated full, regenerated compact
and shared-GUI input must also agree. Reordered payload hashes may differ from
the original payload because array order is serialized; compare exact hashes
across consumers of the same input, and semantic identity/counts across reordered
inputs. Byte-identical duplicates remain fully idempotent.

## Focused validation

- Saved exact cancelled intent:574 sibling becomes0/0/0/0; restored A at576
  remains `runtime_unlock_retry_dispatch_contract_unsettled`,1/1/0/1, recovered0.
- No compensation, rejected/idempotent-only compensation, forged history, changed
  receipt, missing role, wrong authority/generation/source/release/data session,
  duplicate conflict, replacement, terminal poison and ABA remain fail-closed.
- Missing original A/B installation effects retain their exact evidence failure;
  competing findings have deterministic selection independent of host/row order.
- A later genuine Coordinator dispatch/settlement may close only A's current
  role, with its exact original install and physical effect proofs. No fabricated
  Superseded observation may substitute for it.
- Existing first-install/MissingDispatch compensation, staged-receipt, forbidden
  compensation-effect and real service/Infra controls remain unchanged.

Run only affected producer assertions, shared evidence tests and one stabilized
contract invocation, with Release build only if C# fixtures change. Then evaluate
the28-record immutable service control independently in full, regenerated compact
and shared GUI; preserve its SHA and all original locations. No full report,
portable rebuild, preflight or live attempt is part of this gate.

## Expected stop and separate follow-up

The expected next boundary is **576**, transition
`fte1-c0dd340b2d36c645da0e590b`, generation15 -> 16: restored A remains AliasBound.
Its unresolved1/1/0/1 is a real open obligation in this deliberately truncated
fixture, not evidence of a runtime-policy bug. Do not tune runtime or declare
the historical/live transfer recovered on that basis.

Successful confirmation retirement has separate source-proven gaps: the inline
service path adopts retirement without invoking the settlement-effect helper;
that generic helper would see both retained removal and terminal creation and
refuse its exactly-one selection (`SessionFileTransferService.CoordinatorSnapshots.cs:978`).
The current narrow catalog also intentionally excludes that terminal Superseded
shape. A later step needs a genuine positive capture and exact entering-terminal
binding. Existing reusable controls include
`RuntimeUnlockRetryLifecycle_ActiveSuccessorCommitAuthorsPredecessorSettlement`,
`SuccessorInstall_UsesNextGenerationAndCannotBeClearedByOldReceipt`, and
`RuntimeUnlockRetryDispatchInstall_SuccessorCannotActivateBeforePredecessorCleanup`.
They were inspected, not rerun here; no new successful-path failure is claimed.

Keep runtime authority, protocol, DTOs, timeouts, wire, service effects, route
policy, frozen catalogs/schemas, report limits and historical finalization unchanged.

## Implementation and focused validation

Implemented in the recommended order: regression first, private deterministic
same-boundary findings, exact atomic role reduction, then the exact cancelled
provisional-receipt exemption. The new aggregate scope guard starts at the
validated provisional active commit and ends when its participating live roles
close. Original A/B role, authority-source and immutable receipt history still
fold from their issuance. A's earlier valid installation can adopt its bridge
scope; the new compensation guard does not redefine that installation policy.

Malformed artifacts can retain deterministic private negative findings, but cannot
populate the cancellation index. A malformed active subtree keeps its artifact
failure without falling back to descriptor-order authority inference. Original
A/B stage and install effects remain required. Exact source/terminal ABA, skipped
or reversed execution, and terminal-looking unproved closure remain fail-closed.

Validation artifacts are under
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/provisional-effect`:

- Release smoke-project builds passed with zero errors and221 existing warnings.
- `core-01.trx`:2/2 genuine active-compensation/own-settlement fixture tests pass.
- `contract-06.trx`:5/5 bounded evidence and existing staged-install controls pass.
  The compensation matrix independently checks23 cases per host through full,
  regenerated compact and both shared-GUI inputs, with exact selected metadata
  and total finding counts; prior parity deferrals are removed.
- After narrowing the new guard to its exact provisional-commit interval,
  `open-final-07.trx` reran only the affected open-authority fact:1/1 pass.
- Five additional poisoned-history controls pass through both hosts and all
  consumers using the same support assertions: data-session ABA, terminal ABA,
  arbitrary Closed, skipped alias, and execution reversal.
- Expected initial red and subsequent PS5 array/count, invalid-input diagnostic
  parity and overbroad guard failures are retained. Diagnostic wrapper empty-line
  handling was corrected without changing the immutable source or product GUI.
- Final PowerShell parsing, diff checks and independent safety review pass.

The exact28-record actual service stream passes artifact validation, is complete
and authoritative, and has zero legacy selections. Both hosts and all consumers
select576, `fte1-c0dd340b2d36c645da0e590b`,
`runtime_unlock_retry_dispatch_contract_unsettled`, **1/1/0/1**, recovered0.
There is no failure at574. The source SHA remains
`4AE11E0B96627CEA8C72020A3B12D087A593BD3C26E93FD62BF35115A7F40F84`.
Normalized boundary-set hash:
`84763fb1ee461d32ad00478eb688bf070ea048993036011383a80ff94970447d`.
Selected boundary hash:
`4e004d10e494dc5fb21426e4c96fd0b9433ce0e9aa9746230dbe258902e92046`.
These are scoped diagnostic evaluations, not a whole retained report or finalization.

## Newly exposed independent continuation edge

The genuine Coordinator continuation advances restored A through SendInFlight
and its own accepted DispatchObserved settlement. Its installed role retires and
its terminal role enters Settled; B's old terminal role retires Invalidated→None.
A is no longer open, and the compensation boundary passes.

`closure-05.trx` nevertheless remains red. Its first shared evaluation has valid
artifact evidence but selects fixture sequence120,
`fte1-31e7c9fa18f01bb0a6a87592`, as
`runtime_unlock_retry_dispatch_install_grant_evidence_incomplete`. The existing
generic terminal-receipt branch requires `install_committed` for B's outgoing
Invalidated terminal, whose retained receipt status no longer equals its original
Installed effect. This is separate from cancelled A's provisional settlement.

The receipt-backed test effects are synthetic fixture observations, not a claim
of live transport execution. No effect is fabricated for Superseded or for B's
terminal eviction. The stronger whole-continuation assertion and this failure
remain visible; the later-A matrix is not reported as passing. Stop for a narrow
RCA of the old-terminal effect requirement before any commit/package/live gate.

The separate RCA and regression-first implementation plan is now recorded in
[Compensated terminal eviction](file-transfer-retry-invalidated-terminal-eviction-effect-rca.md).
That narrow correction is now implemented: only an exactly witnessed B terminal
eviction may use its original Installed effect proof. The complete24-case
later-A continuation matrix passes on both hosts; the immutable restored-A open
service control remains unresolved at576. See the linked implementation/results
section for the exact scope and retained development failures. This does not
establish the separate successful-confirmation or joined probe/ACK path.
