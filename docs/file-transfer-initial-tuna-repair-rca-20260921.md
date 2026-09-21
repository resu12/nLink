# Initial Tuna frontier repair: bounded RCA and fix plan

## Evidence boundary

Base HEAD: `eca5a862133a6dcfd4269754eb30ad2f23eed32a`.
The fresh, clean-source live run is
`artifacts/initial-tuna-checkpoint-20260921-eca5a862/live/20260921T130727Z`,
acceptance run `9c376452-f3ee-4346-a431-881d088c0b92`, scenario
`active-tuna-v4-64mb`, attempt 1. Strict preflights passed. The initial Core
publications used Tuna v4, epoch 0, publication generation 1 and no commit.
The receiver reached 67,044,352 of 67,108,864 bytes, with missing range `1071:3`.
Repair scheduling authority was rejected. The raw rejection reason was redacted.

The primary failure classifier timed out; parent current-runtime proof is
incomplete. This document does not replace that classifier or establish a
completed live diagnosis. No historical artifact was reevaluated. The separate
evidence-enrichment timeout is not explained or changed here.

## Deterministically reproduced defect

`CreateRegularV4FrontierRepairContract` calls the coordinator's
`StageRegularV4FrontierRepairPressureBinding`. Current repair-decision validation
succeeds, but `TryIssueRegularV4FrontierRepairAuthority` rejects every Tuna
publication without a positive route-commit generation. Initial Tuna starts
have a legitimate first publication without a transition commit. Therefore
their current owner cannot stage the repair binding required to schedule a
missing-range repair. This is the first failed edge in the focused reproduction;
no repair installation or payload dispatch is reached.

After correcting issuance, extending the same four cases through receipt-based
settlement exposed the matching commit-only condition in
`RegularV4RepairSettlementMatchesCurrentRepair`. Staging and installation passed,
but settlement failed with
`regular_v4_frontier_repair_pressure_current_repair_authority_mismatch` in
`initial-tuna-repair-lifecycle.trx`. Issuance and settlement therefore share one
strict initial-publication predicate in the completed fix. This is a second
deterministically verified edge of the same repair lifecycle, not a claim about
an additional live failure.

The new initial-Tuna snapshot cases then exposed the same commit-only assumption
in the repair snapshot scope/candidate predicates. They omitted the current
repair descriptor before staging, leaving the accepted attempted-decision
snapshot without its repair subject. Those predicates now use the same exact
initial-publication check. The fixture also verifies contiguous state-generation
and fingerprint boundaries and keeps its idempotent-restaging check outside the
recorded main lifecycle.

The analyzer's Family 5 repair scope and transition validation also required a
positive commit. They now recognize only the initial Tuna-v4 boundary shape
(publication 1, commit 0, epoch 0, terminal generation 0, positive independent
owner/leg generations). This shape remains evidence, not publication authority:
the route-consistency consumer must still prove the first coordinator
publication. The initial fixture now advertises the existing bootstrap catalog
that matches its records, without changing any frozen catalog hash. Its negative
controls reject altered boundaries and missing publication history.

Before production changes, four cases covering both directions and independent
runtime/leg generations all failed at staging with
`regular_v4_repair_route_invariant_mismatch`. Results:
`artifacts/initial-tuna-repair-20260921/initial-tuna-repair-red.trx`.
The preceding build attempt failed before tests due to sandbox-blocked Avalonia
build-statistics logging; the red run disabled only that optional target with
`-p:UsedAvaloniaProducts=`. It did not disable compilation or tests.

This proves the code defect and is consistent with the live observations. It
does not prove that it is the only cause of the live stall or that a fix completes
the transfer.

## Bounded fix contract

1. Require coordinator state and current repair-decision validation at the
   authority-issuance boundary, including direct internal callers.
2. Admit a commit-less Tuna repair only for the exact current initial publication
   and owner: ordinary route-publication action, publication generation 1 with
   matching last generation, epoch 0, no current or prior commit/live epoch,
   terminal generation 0, and the current Tuna-v4 leg. Runtime and leg generation
   counters remain independent; neither must equal 1.
3. Keep the existing positive-commit path for transitioned Tuna routes and the
   regular-v4 path. Reject republication, removed-commit history, stale owners,
   terminal/route changes and altered decision copies without installing a
   binding or changing route authority.
4. Preserve the existing pressure-install receipt boundary and settlement
   lifecycle, using the same initial-publication predicate at both boundaries.
   Do not enable initial-Tuna fallback admission, fabricate a commit,
   or let receiver ranges/transport observations grant authority.

## Verification plan

- Red/green regression for initial staging, exact tuple propagation, idempotent
  restaging and receipt-based installation in both directions.
- Negative cases for owner/publication/decision mutations and transition history.
- Directly affected frontier-repair coordinator/Core/Infra lanes, selected once
  after implementation stabilizes. No unrelated aggregate lanes.
- Static diff review and `git diff --check`.
- No live test in this step. A future targeted live validation requires a new
  clean checkpoint, portable rebuild and strict preflights; full matrix and
  live-256 remain deferred.

## Explicit non-goals

No classifier timeout adjustment, secondary evaluator, route/fallback redesign,
queue/concurrency/chunk/retry/goodput tuning, wallet/setup/installer/screen-share
change, push, or broad test campaign.

## Verification outcome and remaining gate

The new repair path has 10 distinct passing cases: eight Core cases and two
initial-Tuna snapshot cases. The latter cover full/compact parity, GUI
classification, missing-effect closure, altered-boundary rejection and missing
initial-publication rejection. Eleven previously uncompleted, directly selected
Core pressure cases also completed with 11/11 passing. The existing regular-v4
direct-issuance control passed in the nine-case focused run.

This is **not** a completed passing affected lane:

- The first affected run aborted with a stack overflow after 18 passes.
  `FileTransferReceiveRecoveryOperation_CompletedSuccessor_PressureHandoffCommitsAtomically`
  reproduces the crash in isolation, during receive-recovery fixture setup. No
  claim that the crash predates this patch is made without a baseline comparison.
- The remaining-case run recorded 27 passes and six snapshot failures, then
  aborted. Its stderr included sandbox-denied operational-log writes; these
  messages alone do not identify the crash cause.
- A bounded, explicitly escalated run of uncompleted Infra/projection cases also
  crashed with a stack overflow after three passes. Blame identifies
  `RegularV4FrontierRepairPressure_MissingApplyBridgeRetainsBindingAndIssuesNoAuthority`
  as the test in progress. Its stack passes through pressure recovery handoff and
  receive-recovery state creation. Thus the execution problem cannot be dismissed
  as only a sandbox logging artifact.
- After the snapshot follow-up, both new initial-Tuna snapshot cases pass. The
  last run of the four existing committed/cross-route comparison cases failed:
  three reported GUI route-consistency rejection of their recorded history; the
  outbound cross-route case reported activation-retry and pressure-settlement
  transition failures. These were not rerun after the initial-only analyzer
  change. They remain unresolved, not silently waived or declared pre-existing.

Detailed runs, partial results and retained synthetic evidence are recorded in
`artifacts/initial-tuna-repair-20260921/RESULTS.md`. Counts overlap across runs and
must not be added into a successful campaign total. `git diff --check` passes.
Changes remain uncommitted. No live scenario, portable rebuild or push occurred.

## Next bounded work

1. Isolate the two stack-overflow paths against the checkpoint and patched builds
   before attributing them to production code or adjusting stack/JIT behavior.
   Preserve failing results; do not treat partial passes as lane completion.
2. Repair the comparison fixtures' complete route-history/operation proof, using
   their retained primary results and exact failing boundaries. Do not relax
   publication, activation-retry or settlement validation to make fixtures pass.
3. Run only the repaired cases and uncompleted directly affected cases. Finish
   the pre-commit review before a clean checkpoint and targeted portable/live
   validation. The live classification timeout remains a separate open item.

## Checkpoint isolation and Debug stack fix contract

Both isolated stack-overflow tests also abort on an unchanged export of HEAD
`eca5a862133a6dcfd4269754eb30ad2f23eed32a`. The missing-apply-bridge
reproduction was confirmed outside the sandbox. Baseline logs, TRX results and
method-specific JIT diagnostics are retained under
`artifacts/initial-tuna-verification-20260921`. Thus these two crashes predate
the initial-Tuna patch; no corresponding Release/live failure is inferred.

The original completed-successor theory reserves 550,464 bytes in its Debug
frame before calling fixture construction. A thin wrapper lets fixture setup
return first, but its remaining assertion helper still reserves 538,256 bytes
and the run aborts. Separate preparation from receipt/snapshot assertions next,
preserving every existing assertion and their order.

The missing-bridge crash has a finite call chain through recovery handoff,
`IssueReceiveRecoveryOperationCore`, state preparation and
`ApplyRuntimeUnlockRetryLifecycle`. The issuance method retains its large
validation/authority-construction frame while applying the new state. The
bounded production fix is to return the validated decision and predecessor
from a non-inlined preparation helper before applying supersession, issuing
the operation and reconciling the activation hold. Guard order, decision
statuses/reasons, authority fields, state-application order and the single outer
snapshot finalization must remain unchanged.

Verify the two isolated reproductions first, then directly affected recovery
controls. Do not increase test stacks, alter JIT optimization settings, remove
assertions, quarantine tests or loosen any authority checks. Comparison-fixture
failures remain a separate unresolved gate.

The two original reproductions pass after separating both preparation and
application into non-inlined phases. A directly selected recovery-control run
then records 32 passes before aborting in
`FileTransferReceiveRecoveryOperation_HeldRetryGrantsWhenReceiptAndProofAdoptClosure`.
That test also aborts on clean HEAD outside the sandbox. Its measured Debug
chain retains a 475,440-byte execution-receipt frame while entering the
531,184-byte retry-authorization core, before counting the intervening frames.
Next isolate held-retry authorization from receipt construction: invoke it only
after the accepted core result returns, and before the existing outer snapshot
finalizer. Rejected/idempotent observations must not authorize retries. Preserve
the observation state separately from the nested retry decision's state, as the
existing contract does, and verify both proof/receipt arrival orders.

## Verification follow-up completed

This section supersedes the unresolved deterministic gates listed above; the
earlier failures remain recorded as execution history.

- Issuance preparation and application now run in separate non-inlined phases.
  Receipt/proof construction returns before held-retry authorization. The proof
  path carries a private branch-result flag so accepted terminal proofs, rejected
  proofs and duplicates do not gain a retry side effect. Snapshot finalization
  remains at the original outer boundary, with the original observation state
  and nested retry decision kept distinct.
- Both original stack reproductions pass. The held-retry test passes in both
  arrival orders; 42 directly selected Core recovery controls pass. Added
  assertions retain diagnostic-only behavior for terminal/duplicate proofs and
  conflicting receipts.
- The four comparison fixtures now capture StartTransfer, initial publication
  and route transitions with continuous fingerprints. The outbound cross-route
  fixture advertises the existing frozen activation-retry catalog and reuses
  the current coordinator publication rather than creating an extra ordinary
  fallback publication. No production route-consistency validator or catalog
  hash was relaxed. All four cases pass, including their mutation controls;
  both new initial-Tuna cases also pass with the final script.
- A selected service test failed only in the combined run because a discarded
  receipt from prior work was logged at writer sequence 289, immediately after
  its cursor at 288; its own controller invalidation was at 323. Its old long
  test identifier was redacted, preventing transfer-level attribution. The test
  now uses a short synthetic identifier and scopes all existing log assertions
  to that transfer. All 11 selected recovery service cases pass together.
- A shared-fixture control exposed another clean-HEAD stack overflow in
  `FallbackTailUnblockRecheck_ExactRepairSuccessorCarriesHeldRequestOnce`.
  Setup separation alone was insufficient: the remaining assertion method
  reserved 850,512 Debug bytes. Separating successful successor construction
  from mutation assertions preserves all checks and makes the isolated test
  pass. No further production changes were needed for this test.
- The 50 previously selected cases without retained passing results complete
  with 50/50 passing. Auditing the original 120-case selection against latest
  per-case results finds 120 passes and no unresolved case. These are focused
  runs, not a single aggregate run; overlapping run counts must not be summed.

Detailed results and retained evidence:
`artifacts/initial-tuna-verification-20260921/RESULTS.md`.
Static review checked unchanged authority guards/order, no retry on rejected or
duplicate callbacks, one outer snapshot finalizer, strict catalog selection and
continuous fixture history. `git diff --check` passes. No additional actionable
issue was found in that bounded review.

The patch is still local and uncommitted. No portable rebuild, live scenario,
full matrix, push or historical live-artifact reevaluation occurred. The prior
live classifier timeout is not proven fixed by synthetic tests. Next is a local
checkpoint and portable rebuild, followed by strict preflights and the targeted
initial-Tuna validation; do not claim live-256 readiness or full-matrix success
from these deterministic results.
