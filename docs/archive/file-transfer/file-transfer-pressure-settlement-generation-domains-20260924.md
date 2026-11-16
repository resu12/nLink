# Pressure-settlement attempt binding: RCA and bounded fix plan

## Status and evidence

RCA only, at `f107b5a1f6519e1c65be3eb3ff6af02b6e0fe120` on `v0.7.0`.
No production/test implementation, authority re-evaluation, build, GUI run,
commit, or push was performed. All 140 pins from the latest live verification
were checked before inspection.

The relevant campaign is
`artifacts/second-transfer-retoggle-live-20260924-f107b5a1/live/20260924T053114Z`.
Its parent receipt remains `proof_incomplete`; acceptance remains FAIL. The
parent-selected first boundary is
`snapshot_regular_v4_pressure_settlement_transition_invalid` /
`pressure_settlement_attempt_binding_invalid`, boundary hash
`5684dbe72c037c543876214c804bdf86a918d272fdd00a0107d1ccfa294dc958`.

The exact failed snapshot is retained-input line **31557**, writer
`p10212r454f1cb475f34472`, sequence **35668**, transition
`fte1-97e300c64339a4d51bb3b127`. Line **31548** is the immediately preceding
successful pressure-binding commit for its named successor, used only as
source context. Both original snapshot payload hashes are checked before
decoding; no analyzer is invoked to select or replace the parent boundary.

## Root cause

The snapshot producer and validator conflate **repair decision generation**
with **pressure install generation**. They are independent counters, not aliases.

| Subject | Repair/operation generation | Pressure install generation |
| --- | ---: | ---: |
| Retained predecessor `fte1-3433392049abfc319049e143` | 17 | 7 |
| Installed successor `fte1-ab5db39299e4ac1b32ebf1d1` | 18 | 8 |
| Entering settlement terminal for predecessor | 17 | 8 (receipt's current install) |

`FileTransferCoordinator.cs:9491` issues repair generations from
`LastRepairReplayGeneration + 1`; `:15733` independently issues install
generations from `RegularV4FrontierRepairPressure.LastInstallGeneration + 1`.
The descriptor at `FileTransferCoordinator.Snapshots.cs:3915` correctly uses
the authority's decision generation for `OperationGeneration` and the binding's
install generation for the install command/receipt and payload field.

### Producer defect: missing attempted-decision witness

For retained-predecessor settlement, `CreateRegularV4PressureAttemptedDecisionSnapshots`
selects the current installed successor using the settlement command. Its filter
at `FileTransferCoordinator.Snapshots.cs:891` additionally requires
`descriptor.OperationGeneration == requiredInstallGeneration`.
In this run that is **18 == 8**, false. The zero-match path at `:909` returns
an empty attempted-decision array. The selected settlement snapshot consequently
has five pressure-family state-change operations and **zero** attempted-decision
operations, although the coordinator committed the settlement.

The coordinator's commit path independently checks the exact prepared settlement
command/receipt, predecessor identity, Superseded outcome, installed successor,
and clear scope (`FileTransferCoordinator.cs:15901`, `:15993`). The observed
missing witness is a snapshot projection defect; no runtime authority relaxation
is indicated by this evidence.

### Validator defect: cross-domain equality and ordering

`Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure`,
`CoordinatorSnapshotEvidence.ps1:6413`, requires successor attempt operation,
command, receipt, and decision generations all to equal install generation 8.
The emitted attempted-decision receipt, when present, uses the selected operation
generation (18), as constructed at `FileTransferCoordinator.Snapshots.cs:921`.
The validator also rejects when predecessor operation generation is greater
than or equal to install generation (`CoordinatorSnapshotEvidence.ps1:6443`):
**17 >= 8** is true, but compares unrelated counters.

Thus fixing the producer alone is insufficient. Keeping the missing-attempt
rejection is correct; the invalid cross-domain tests must be replaced with
same-domain binding checks, not removed wholesale.

### Why existing coverage missed it

`RegularV4FrontierRepairPressure_SuccessorRetiresOnlyExactPredecessor` starts both
counters together and explicitly equates successor install generation with
attempt operation generation (`FileTransferCoordinatorTests.cs:4471`). The
synthetic retained-settlement fixture in `FileTransferOpsScriptSurfaceTests.cs:3540`
uses 15/16 for both domains. Both encode the accidental equality, so neither
exercises the live 17/18 versus 7/8 shape.

## Fix contract

1. Change only retained-predecessor settlement attempted-snapshot selection in
   `FileTransferCoordinator.Snapshots.cs`. Keep the exact successor authority,
   predecessor identity, installed role, settlement successor link, source and
   effective boundaries, and unique-descriptor requirement. Match install
   generation against the installed binding's install payload/receipt domain;
   match operation generation against the authority decision generation. Do not
   renumber operation generations, synthesize an attempt from logs, or change
   coordinator decisions or effect authority.
2. In the matching validator branch, require the successor attempt's operation,
   payload decision, and attempted command/receipt generations to agree in the
   **repair domain**. Require a positive install generation shared by the
   settlement receipt, settlement payload, and attempted payload in the
   **install domain**. Require predecessor repair generation < successor repair
   generation, not predecessor repair generation < install generation. Preserve
   the exact authority/predecessor, command-fingerprint-to-settlement-receipt,
   source/effective boundary, and one-attempt checks.
3. Preserve terminal/mirror partitioning and rollover checks, exact predecessor
   retirement, absence of successor mutation, current-clear behavior, and all
   stale/rejected/idempotent handling. No catalog/schema extension is needed:
   existing fields already represent both domains. Do not touch GUI readiness,
   route publication, runtime counters, Infra, retries, timeouts, or throughput.

## Focused verification plan

1. Add a coordinator regression with divergent repair/install counters, including
   the live-shaped predecessor 17/install 7 and successor 18/install 8. Obtain
   the authorities through coordinator issuance, then install and settle via the
   existing APIs. Before the fix, require the regression to fail because the
   accepted settlement lacks its single attempted-decision witness. Afterward
   assert operation/decision/attempt-receipt generation 18, install generation 8,
   terminal operation 17/receipt 8, and unchanged exact effect binding.
2. Add a focused analyzer regression from coordinator-produced envelopes, plus
   a small direct validator fixture with divergent domains. Do not rely only on
   handwritten fixtures. Check regular-v4 and Tuna-v4 supported directions,
   existing equal-domain behavior, and a non-adjacent but ordered repair pair.
3. Negative controls must reject a missing or duplicate attempt, wrong successor
   or predecessor, mismatched install generation, mismatched repair/attempt
   generation, non-increasing repair order, changed command fingerprint, changed
   source/effective boundary, or mutated successor. Preserve existing mirror,
   current-clear, idempotent, and rejected-callback behavior.
4. Run the new focused regressions first, then the directly affected pressure
   coordinator/analyzer tests once after stabilization. Use both PowerShell
   hosts for the focused validator controls. Avoid the large aggregate evaluator
   test solely to reach its embedded fixture; factor or add a bounded test.
   No unrelated Infra or broad aggregate lane is justified by the proposed diff.
5. Review the bounded diff and checkpoint, rebuild portable, and rerun only
   `second-transfer-after-reactivation` outside the sandbox with strict preflights
   and no implicit retries. The second Tuna-v4 publication, terminal result, and
   payload integrity are still required. Retoggle-branch coverage remains open;
   do not claim it from a run where that branch is unexercised.

The fix addresses the first missing proof edge, not an asserted cause of the
later second-transfer publication timeout. A corrected fresh run may expose a
new boundary. Do not retroactively accept the old campaign or start the full
matrix merely because this RCA explains its evidence rejection.

## Reproducible inspection

`artifacts/pressure-settlement-generation-domains-20260924/Verify-Rca.ps1`
checks the sealed parent artifacts, decodes only the two exact source snapshots,
records the counter-domain facts and failed cross-domain predicates, and seals
the RCA files/source references. It performs no authority evaluation and changes
no historical artifact. A successful RCA check is not a passing runtime test.
