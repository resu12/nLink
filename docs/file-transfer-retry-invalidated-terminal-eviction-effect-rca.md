# RCA and Fix Plan: Compensated Retry Terminal Eviction Requires the Wrong Install-Effect Status

Status: narrow analyzer/test correction implemented in the existing worktree.
No commit, package, preflight, whole retained report or live run is included.
The original RCA below records the pre-fix result; validation is recorded at the end.

## Evidence and scope

The preceding scoped provisional-supersession correction is implemented in the
worktree at base HEAD `b955354f`. Its actual-service control clears the cancelled
intent at574 and correctly leaves restored A unresolved at576,1/1/0/1.

The next boundary comes from the **genuine Coordinator continuation fixture**,
not a new live transfer. Its stronger shared-evaluator regression remains red:

`FileTransferRetryConfirmationCompensationEvidenceTests.RejectedRetryConfirmation_RestoredAuthorityClosesOnlyWithItsOwnLaterDispatch`

Saved result:
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/provisional-effect/closure-05.trx`.
Saved producer-projected input:
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/confirmation-compensation/failed-fixture-inputs/predecessor-True-active-True-settled-True-20260909T1711152485966Z/envelopes.json`.
Input SHA-256:
`01F6762B74BE1417C87B50BB151686F2CB198954BABE9C60F3065D8479C8F89D`.

The support script serializes these real envelopes and supplies receipt-backed
test observations. Those effects are **synthetic fixture evidence**, not proof
of physical service/NKN callbacks. This is outbound Regular-V4 active-retry
coverage, with Family2 selected in a scoped `SnapshotV1Shadow` fixture; it is not
an all-family authoritative or paired-fallback acceptance result.

Canonical current false result:

- Writer/sequence: `fixture-writer/120` (fixture numbering, not live chronology).
- Coordinator: `fte1-e3d1cc4b2f692f7d54c9eadb`.
- Transition: `fte1-31e7c9fa18f01bb0a6a87592`, generation11→12.
- Decision: `ObserveRuntimeUnlockRetryDispatchSettlement/Accepted`.
- Event: `filetransfer_coordinator_transition_snapshot`.
- Class: `runtime_unlock_retry_dispatch_install_grant_evidence_incomplete`.
- Domain/phase: `route_runtime/runtime_unlock_retry`.
- Current quartet: **1/1/0/1**; correct sibling result: **0/0/0/0**.
- Proposed diagnostic variant:
  `compensated_retry_terminal_eviction_requires_invalidated_status_install_effect`.
- Current selected boundary hash:
  `4083aecfd1c869f6868012ac5452204c82525c172e389bcdc20918e1db8270f4`.

A scoped read-only reevaluation reproduced this as the sole failed semantic
boundary. The previous validation recorded artifact-evidence PASS; this is not
a malformed producer envelope. The reevaluation stage took0.989s, with3.861s
total command elapsed; no test suite or full report was invoked.

## RCA

Let A be `fte1-f537e4cc3f44a5d8910b6603`, authority generation1, and B be
`fte1-c38a7100816caee4d3f5601d`, authority generation2.

| Fixture sequence | Exact boundary/effect |
|---|---|
|40/41|A enters Installed and has its original exact installation effect.|
|50/51|A enters AliasBound with its exact execution observation.|
|90/91|B commits Installed; A is provisionally retained. B has one original installation effect.|
|100|Accepted Applied compensation restores A and moves B into the terminal slot as Invalidated. No authority effect is due.|
|110/111|Restored A enters SendInFlight with its own execution observation.|
|120/121|A's own DispatchObserved settlement replaces the terminal slot; its exact settlement observation is present.|

Sequence120 atomically contains:

- A installed: `SendInFlight→None`.
- A terminal: `None→Settled`, with its own `DispatchObserved` settlement receipt.
- B terminal: `Invalidated→None`, carrying its unchanged compensation-era receipts.

The Coordinator stores only one `LastTerminalAttempt` snapshot role. It replaces
that slot with A while retaining invalidated B in `AttemptHistory`; B is neither
installed again nor given a dispatch/settlement. Compensation preserves B's
original install receipt, changing only attempt state. The projector uses that
state as the install-stage receipt's display status while keeping the original
identity, generation, request fingerprint and install fingerprint.

Consequently B's install evidence has:

- Original physical effect at91: status `Installed`, install generation42.
- Retained/evicted terminal receipt: status `Invalidated`, same generation42,
  identity `fte1-5dfc249b2178ebe1bdb73f2d` and fingerprint
  `fte1-c754899ac2403eea5d13522c`.

The evaluator's `install` switch sends every `attempt_terminal` whose before
state is non-None directly to the generic `install_committed` requirement. That
bypasses its existing original-install proof lookup. The subsequent effect
matching requires exact receipt status, so B's real `Installed` effect cannot
satisfy its retained `Invalidated` receipt.

More precisely, this branch searches authority-wide: it does not explicitly
require a new transition-local effect. It nevertheless demands the wrong
lifecycle-status proof. Inventing an Invalidated installation effect, weakening
status matching globally, or removing all terminal effect checks would be wrong.

Source anchors in the current worktree:

- `FileTransferCoordinator.cs:4463`: preserve B's receipt while invalidating it.
- `FileTransferCoordinator.cs:17365`: A's own settlement replaces the terminal slot.
- `FileTransferCoordinator.Snapshots.cs:3080`: removal retains the prior descriptor.
- `FileTransferCoordinator.Snapshots.cs:4032`: project the last terminal slot.
- `FileTransferCoordinator.Snapshots.cs:5109`: lifecycle-derived install status.
- `SessionFileTransferService.CoordinatorSnapshots.cs:1073`: installation effects
  belong to an accepted install commit, not terminal eviction.
- `CoordinatorSnapshotEvidence.ps1:12724`: existing exact committed-install proofs;
  `:12837`: overbroad terminal branch; `:12985`: exact effect-status comparison.
- `FileTransferCoordinatorTests.RetryConfirmationCompensation.cs:332`: real
  continuation asserts B's unchanged receipts and retained invalidated history.

No runtime, Coordinator, producer or service defect is established by this finding.

## Recommended implementation order

### 1. Preserve the red continuation and strengthen its exact controls

Do not skip or soften the positive assertion. Assert the complete A/B sequence,
one original B install effect, no compensation/eviction install effect, and one
A-owned later settlement effect. Keep generation domains distinct: A/B authority
generations1/2, install generations41/42, A offer/settlement generation17, and
aggregate generation12 are deliberately unequal.

Add an explicit assertion that missing A's later settlement effect is selected
as A's own unsettled obligation after the B false finding disappears. Today the
B install finding can outrank it at the same aggregate boundary. Retain the
existing deterministic finding/arbitration implementation.

### 2. Extend the existing private role reduction with an exact eviction witness

Reuse `Get-FileTransferCoordinatorProvisionalRetryReduction`; do not add a second
whole-stream reducer or generic terminal-history abstraction. Retain the exact
origin and compensation identities alongside the existing role slots. Publish
a private witness only after the participating scope/history passes validation:

1. B's unique original accepted commit installs its exact authority/generation,
   request and receipt while provisionally retaining A.
2. The exact accepted **Applied** compensation inverses that commit, restores
   the same A and creates B's `None→Invalidated` terminal.
3. B's terminal remains uninterrupted and immutable through any exact carries.
   Its structural key, source, command/receipt tuple and Invalidated status must
   match the terminal born in that compensation.
4. A's own accepted DispatchObserved closure produces its exact installed
   retirement and terminal entrant; the same atomic boundary evicts B with
   `Invalidated→None`. B must be that compensation's failed successor, not just
   another participant or terminal-looking descriptor.
5. Existing generation/hash, aggregate-scope, receipt, role-slot, replacement,
   terminal-poison and ABA guards remain satisfied.

Key the witness by exact coordinator/writer/transfer scope, eviction transition,
operation key and authority generation. Retain the original commit/operation,
install receipt and compensation identities. Do not publish a witness for
`DiagnosticOnly`, poisoned, missing or ambiguous history. Descriptor order and
writer-sequence adjacency cannot establish provenance.

### 3. Discharge only this historical install obligation through its origin

At the generic terminal-install branch, consult the exact witness only for
`attempt_terminal/RetryAttempt: Invalidated→None`. Require one exact original
committed-install proof from the existing proof collection, including origin
transition, installed operation key, aggregate generation, authority, command,
receipt identity/generation/fingerprint and complete authority-authored scope.

Validate the actual original effect against the original Installed operation
and receipt, **including status Installed**. Separately validate the evicted
descriptor against its witnessed Invalidated terminal. The only permitted status
transformation is the producer-defined one already proved by exact compensation;
do not alias or rewrite receipt statuses in the generic matcher.

Original A/B stage/install proofs remain mandatory. A's own execution and
settlement proofs remain mandatory. Eviction closes no new operation and must
not count B as recovered. Reject an installation effect newly attached to the
compensation or witnessed eviction; it cannot substitute for either origin.

Leave all unrelated terminal states, successful Superseded retirement, unbound
Invalidated removals, and existing first-install/MissingDispatch compensation
handling unchanged. Do not change runtime policy, service emission, DTOs, wire,
catalog/profile hashes, Snapshot version, schemas, report limits or deadlines.

## Focused tests and validation

- Genuine later-A continuation: sibling0/0/0/0, no failed Family2 obligation.
- Same input with reordered descriptors and byte-identical duplicates: same
  semantic result; independently serialized consumer inputs retain exact parity.
- Missing original A or B installation evidence: exact install failure remains.
- Missing A execution or settlement effect: exact A failure remains, not hidden
  by a spurious B finding and never recovered by B eviction.
- Wrong original commit, compensation, authority, generation, command, receipt,
  source, release or data session: no witness; remain fail-closed.
- Rejected/idempotent-only compensation, changed B terminal receipt/status,
  missing/competing terminal origins, replacement, gaps, terminal poison and
  overwrite/restore ABA: remain fail-closed.
- Forged eviction install effect, conflicting duplicate effects and mutated
  original effect: fail. Only byte-equivalent duplicates coalesce.
- An unrelated Invalidated terminal cannot use this exception. Existing
  successful Superseded and other terminal paths are not newly admitted.
- The actual-service truncated control still selects576,
  `fte1-c0dd340b2d36c645da0e590b`, `runtime_unlock_retry_dispatch_contract_unsettled`,
  **1/1/0/1**, recovered0. Helper11350 is not reinterpreted or marked recovered.

Run serialized, in order:

1. Parse changed PowerShell; Release-build only if C# fixture changes require it.
2. Run the directly affected Coordinator continuation and shared-evidence fact.
   Complete the existing11-case later-A matrix rather than stopping after its
   first positive case; add the narrowly missing controls above.
3. Independently evaluate full, catalog-regenerated compact and shared GUI on
   PS5.1 and PS7. Require identical normalized boundary JSON/hash, taxonomy,
   quartet, location, operation and receipt metadata for each identical input.
4. Run the adjacent active/open and compensation contract controls once after
   the implementation stabilizes. Preserve original A/B effect requirements.
5. Recheck the saved actual-service control separately, unchanged SHA and576
   unresolved. Record synthetic versus actual-service provenance explicitly.

This is a bounded analyzer/test gate, not proof of live transport completion.
Do not rerun unrelated aggregate suites or consume another retained-report or
campaign attempt. Stop at any independently promoted boundary. If these controls
pass, the remaining successful-confirmation retained-to-terminal Superseded
emission/catalog path and joined real probe/ACK provenance still need their
separate positive-capture step before any commit/package/live gate is proposed.

## Implementation and focused validation

The existing provisional-retry role reduction now retains an exact private B
terminal-eviction witness. It is published only after validated original commit,
Applied compensation, uninterrupted immutable Invalidated terminal occupancy,
and restored A's own accepted DispatchObserved settlement. All same-scope
terminal-slot mutations participate in that guard, including unrelated C, so an
overwrite-and-restore cannot hide behind an A/B-only filter. Poisoned or
diagnostic-only history cannot supply positive witnesses.

Only that witnessed B `Invalidated→None` descriptor may discharge its historical
install obligation using B's exact original installed operation and receipt.
The real effect still must match status `Installed`; no receipt status is aliased
or rewritten. Newly attached eviction-install effects fail artifact validation.
Original A/B stage/install evidence and A's own execution/settlement effects
remain required. B's eviction is not counted as a recovered incident.

The strengthened continuation fact passes all24 cases on Windows PowerShell5.1
and PowerShell7, including exact full/regenerated-compact/shared-GUI metadata
parity, receipt/effect mutation, missing proofs, singleton-terminal occupancy,
and ABA controls. The genuine Coordinator own-settlement test also passes.
The initial expected red and intermediate test-support failures are preserved
under `artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/terminal-eviction`.

Reaching a clean Family2-only Shadow result exposed missing real GUI helper
dependencies in the test support. The support now loads the production helper
closure and initializes its real ledger under an isolated fixture directory.
No mocks or GUI production edits were added. A clean Shadow result legitimately
uses coarse immutable-evidence fallback and has no Coordinator boundary;
the test checks the actual returned shape and cross-host semantic equality.
This is not an all-family authoritative GUI-pass claim.

The missing-A-execution negative remains unresolved at A's own settlement,
where its DispatchObserved proof obligation matures, rather than at the earlier
SendInFlight snapshot. Only the overspecified fixture location was corrected;
no execution-effect policy changed.

The separate immutable28-record actual-service control passes on both hosts:
complete authoritative stream, zero legacy selections, cancelled574 clear,
restored A still unresolved at576, `fte1-c0dd340b2d36c645da0e590b`,
`runtime_unlock_retry_dispatch_contract_unsettled`, **1/1/0/1**, recovered0.
Input SHA remains
`4AE11E0B96627CEA8C72020A3B12D087A593BD3C26E93FD62BF35115A7F40F84`.
Both normalized outputs are byte-identical, boundary-set hash
`84763fb1ee461d32ad00478eb688bf070ea048993036011383a80ff94970447d`,
selected hash
`4e004d10e494dc5fb21426e4c96fd0b9433ce0e9aa9746230dbe258902e92046`.

No runtime/producer/service code, public or wire contract, frozen catalog/profile,
schema, deadline, historical receipt or finalization evidence changed in this
slice. Earlier dirty work is preserved. The successful-confirmation Superseded
emission/catalog path and joined real probe/ACK provenance remain separate work.

Final focused gate: **7 distinct tests pass** (`core-01.trx`,
`continuation-06.trx`, `contract-07.trx`). The adjacent contract lane ran once
after stabilization and passed5/5. The four shared-evidence tests cover47 cases
per host in total, including the24-case later-A matrix. Both scripts parse on
PS5.1 and PS7; diff checks and independent safety review pass. No C# changes
were needed in this slice, so the existing Release test build was reused.
Detailed provenance, preserved red runs and timings are recorded in
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/terminal-eviction/RESULTS.md`.
