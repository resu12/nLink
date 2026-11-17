# Provisional retry supersession is omitted from the Family-2 receipt catalog

## Status and scope

The approved narrow catalog fix is implemented and uncommitted, on top of the
preserved confirmation-compensation changes. The initial RCA below was read-only;
implementation and focused verification are recorded at the end. No runtime,
service, Coordinator policy, frozen catalog, effect requirement or semantic
reducer changed in this catalog slice. Historical helper11350 stays unresolved
`1/1/0/1`. No whole report, portable package, preflight or live run was performed.

This is reproduced from actual service-emitted development evidence, not a new
live failure:

`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/confirmation-compensation/runtime-02.log`

- Test source: `SuccessorConfirmationRejection_PreservesInstalledPredecessor`
  and the snapshot-enabled active-predecessor compensation controls.
- Base HEAD: `b955354f`, with the preserved uncommitted compensation changes.
- Input SHA-256: `4AE11E0B96627CEA8C72020A3B12D087A593BD3C26E93FD62BF35115A7F40F84`.
- Writer: `p15196r43f47ddf087f42f6`.

## First rejected catalog boundary

- Writer sequence/log line: **574**.
- Transition: `fte1-12d0afaba73c9ce7b3397040`.
- Decision: `CommitRuntimeUnlockRetryDispatchInstall/Accepted`.
- Reason: `retry_dispatch_install_committed`.
- Aggregate generation: **14 -> 15**.
- Class: `filetransfer_coordinator_snapshot_evidence_invalid`.
- Reason: `snapshot_operation_catalog_mismatch`.
- Proposed precise variant:
  `provisional_active_predecessor_superseded_receipt_omitted_from_family2_catalog`.

The valid atomic mutation is:

| Authority | Role | Transition |
|---|---|---|
| A, generation1 | attempt_installed | AliasBound -> None |
| B, generation2, predecessor=A | attempt_pending | Staged -> None |
| B | attempt_installed | None -> Installed |
| A, successor=B | attempt_retained_predecessor | None -> Settled |

A is `fte1-3bb1fdb2414012da8e3616d6`; B is
`fte1-e288ced9dcea34af2affc30a`. Only A's retained descriptor fails the catalog.
Its newly authored settlement receipt is validated, outcome `Superseded`,
reason `coordinator_successor_installed`, and exact offer generation18. The
operation generation remains A's authority generation1. These generation
domains must not be equated.

B's exact `install_committed` effect occurs at575. Confirmation returns false.
At576, transition `fte1-c0dd340b2d36c645da0e590b`, the new compensation advances
15 -> 16 and restores original A, removes retained A, and invalidates failed B.
The removed retained descriptor carries its immutable prior Superseded receipt;
this is not a new settlement. That removal is the second catalog rejection.

## Cause and diagnostic proof

`CoordinatorSnapshotEvidence.ps1:297-308` enumerates settlement receipt statuses
but omits `Superseded`. The array contributes to the frozen catalog hash at591.
`Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog` checks that array
at3094-3098 without an exact provisional-supersession sibling.

Direct candidate evaluation found two rejected operations across the two
boundaries. A diagnostic-only in-memory addition of the one status changed
those two catalog results to pass; the array was then restored. No log, payload
or source was modified. The catalog hash before/after remains
`17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2`.
This isolates the catalog omission; it does not establish full stream acceptance
or justify adding the status globally.

The producer deliberately retains A provisionally at Coordinator.cs4154-4179.
It does not yet update LastSettlement or attempt history. The service's false
confirmation branch returns through compensation before sending the predecessor
settlement decision to the bridge. The rollback behavior is therefore correct.

## Recommended narrow fix

1. Reproduce the two exact boundaries using the existing active-predecessor
   fixture, enabling snapshot identity before authorization. Capture finalized
   outer decision states; never stage from a nested pre-finalization state.
2. Add one non-catalog-hashed validator for provisional active-predecessor
   `settlement/Superseded` evidence. Do not append to the frozen status array.
3. Admit creation only in the exact Accepted install commit above, requiring:
   - one generation advance, changed hash and unchanged complete aggregate scope;
   - the four unique role transitions, exact structural keys and no unrelated
     operation supplying the required subject;
   - distinct A/B identities, increasing authority generation, B.predecessor=A,
     retained A.successor=B and B's explicit active-supersession request;
   - exact authority-authored source, release and request lineage, without
     equating historical authority scope to aggregate transport/bridge scope;
   - A's unchanged stage/install/execution identities and generation domains,
     immutable stage/install fingerprints, and the documented execution status
     and state-derived fingerprint change when AliasBound becomes Settled;
     the new opaque fingerprint is not recomputed from projected identities,
     while compensation must preserve it exactly and restore A's original one;
   - one exact new validated Superseded receipt for A's offer, reason
     `coordinator_successor_installed`, DispatchObserved=false and successor=B;
   - exact B pending/install receipt linkage under the existing install validator.
4. Admit its removal only in the already-validated exact compensation inverse.
   Reuse the immediate commit's identity/generation/hash/operation-history
   binding; require the removed retained A to equal that prior descriptor and
   restored A to equal the original installed descriptor. A restore must contain
   no Superseded settlement. Do not treat any `Settled -> None` as compensation.
5. Keep all other roles/decisions/statuses fail-closed. Do not generally admit
   Superseded terminal receipts, arbitrary carries, unrelated settlement reports
   or a callback merely naming A/B. Missing or conflicting subjects stay invalid.
6. Add the precise diagnostic variant without changing schema, contract version,
   catalog hash, runtime behavior or existing effect requirements in this slice.

## Focused validation

- Generated active A -> B commit -> rejected confirmation -> exact compensation:
  both catalog failures disappear, original A is restored, failed B remains
  invalidated, and consumed release history is unchanged.
- Missing/duplicate/foreign siblings; wrong predecessor/successor, source/data
  session, release, offer, receipt identity/fingerprint, authority generation,
  role or status; dispatched A; intervening mutation, replacement or ABA:
  remain fail-closed.
- Descriptor reordering and exact duplicates must not alter selection.
- Preserve first-install and MissingDispatch-predecessor compensation, unchanged
  duplicate/rejected decisions, and original A/B install-effect requirements.
- Run the directly affected Coordinator/service snapshot controls and exact
  evaluator fixtures on PS5.1 and PS7. Require independently evaluated full,
  regenerated compact and shared GUI to return identical normalized metadata.
- The catalog sibling becomes clean; do not require the whole fixture to pass by
  suppressing the next independent effect/state-machine findings below.
- One stabilized targeted contract lane is sufficient. No aggregate report,
  portable rebuild, preflight or live rerun belongs to this correction.

## Stop boundary: distinguish retained intent from observed settlement

The current effect reducer at `CoordinatorSnapshotEvidence.ps1:12510` requires
`settlement_observed` for every validated settlement receipt. A retained
provisional Superseded receipt at574 has no physical settlement observation:
confirmation failed before the bridge settlement callback. Compensation also
must emit none. Catalog admission alone will expose this separate requirement.
Do not fabricate an effect, waive original A/B installation effects, or mark A
settled/recovered to make the evidence pass. Resolve this effect contract in a
separate bounded follow-up after catalog admission.

Two related follow-up observations are static findings, not completed RCA/fixes:

- On successful confirmation, Core can move retained A to terminal A only after
  the exact bridge acknowledgment. The inline service path currently adopts that
  decision without calling the settlement-effect helper; the generic helper
  would also see two settlement-bearing role deltas and refuse selection. Any
  required confirmed-retirement effect needs an exact producer binding, not a
  catalog exemption or guessed receipt.
- The existing Family-2 semantic reducer groups roles under one authority and
  uses the last row. At compensation, restored A's installed row and removed
  retained row share the boundary sort keys. A later correction must reduce
  roles explicitly so a `None` removal cannot hide restored A's live obligation.
  This risk was found by source inspection, not by a claimed full evaluation.

The original rollback fix remains valid and uncommitted. Actual joined
offer/answer/probe provenance and complete route-overlap proof remain future
work. No live or historical acceptance is inferred from this development RCA.

## Implementation and focused verification

`Test-FileTransferCoordinatorSnapshotProvisionalRetrySupersession` now admits
only the exact four-role Accepted commit and its exact compensation inverse.
The history validator also authenticates the prior commit before accepting
compensation. Other Superseded roles, decisions and receipts remain fail-closed.
Actual Boolean types are required; string `false` cannot become authority.
The retained execution fingerprint must change with its lifecycle, remain opaque,
and match the prior commit exactly when removed. Stage/install fingerprints and
the original restored execution fingerprint remain immutable.

The generated active fixture uses the existing valid outbound Regular-V4 active
supersession path, with authority, offer and install generations independently
asserted. It does not manufacture an inbound or paired-fallback active request:
the paired fixture canonically uses `ListenerRearmRequirement=None`, whereas
active supersession requires a rearm. Existing first-install/MissingDispatch
and service compensation controls still cover both directions.

Verification under `rca-remaining-stall/superseded-catalog`:

- Release build: pass, zero errors and 221 existing warnings, 14.65 seconds.
- Latest results for **14 distinct focused xUnit cases pass**: runtime9/9,
  the two existing staged-install evidence controls, and the final3/3 shared
  compensation contract run. Final contract: 51.230 seconds.
- The new active matrix includes42 malformed shape/history/context controls per
  host and six serialized evidence cases. Windows PowerShell5.1 and PowerShell7
  independently evaluate full, regenerated compact and both shared-GUI inputs.
  Catalog verdicts and the original selected boundary metadata agree, except
  the explicitly deferred competing-semantic-failure case described below.
- Missing original installation effects remain required by the unchanged
  first-install/MissingDispatch controls. Fabricated compensation effects fail.
- Parser and diff checks pass. Development failures are retained, including
  strict-mode boundary-property assertions and PS5 empty-result serialization;
  the test now uses the actual `referencedTransitionId` and explicit null fields.

The immutable actual service control was evaluated again from28 exact scoped
records, preserving original writer/line locations, independently through full
and catalog-regenerated compact inputs. Both report complete authoritative
streams, zero snapshot-validation failures/legacy selections and matching
boundary-set hash
`25b90cd57c4efa984f4fd34052a98c18ee88c5c2f46fee85b33fb6b95f5a1eb9`.
The input SHA above and frozen catalog hash are unchanged. Evaluation took
1.268 seconds; it was not a whole retained report.

Both catalog failures at574/576 are removed. Sequence574 remains
`runtime_unlock_retry_dispatch_contract_unsettled`, **1/1/0/1**, because no
physical settlement observation exists for its provisional receipt. This is
the intended stop, not a completed or recovered retry.

Two semantic limits are now reproduced, rather than merely suspected:

- Cross-role row ordering can expose or hide an additional unresolved restored-A
  obligation at compensation. Total semantic boundary counts therefore are not
  claimed equal across hosts. Reordered payloads also intentionally have different
  payload-derived hashes; their selected identity/quartet is unchanged, and each
  input retains exact full/compact/GUI hash parity.
- Removing B's install effect leaves A's missing provisional settlement and B's
  missing install on the same atomic boundary. The existing per-authority reducer
  overwrites that shared boundary's taxonomy. Either documented class may win
  across hosts; the exact commit remains unresolved1/1/0/1. This case is explicitly
  characterized, not presented as cross-host taxonomy parity or installation proof.

These findings remain separate from catalog admission. No effect was fabricated,
no original installation requirement was waived, and no semantic reducer was
changed. Resolve the provisional-versus-observed settlement contract and exact
role reduction in a separately approved follow-up before broader evidence claims.

The separate [provisional settlement-effect RCA and bounded fix plan](file-transfer-provisional-supersession-effect-rca.md)
now confirms why effect exclusion must follow role reduction: excluding only574's
false effect requirement makes the saved fixture look clean while A is still
AliasBound at576. The subsequent bounded implementation now achieves576 unresolved
with exact cross-host/full/compact/GUI parity and removes574's false demand.
See that plan's implementation results: its genuine later-A continuation exposes
a separate old-B-terminal installation-effect requirement, so the complete gate
remains unpassed and no acceptance is claimed.
