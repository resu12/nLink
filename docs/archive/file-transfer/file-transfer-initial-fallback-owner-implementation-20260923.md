# Initial fallback-v6 owner association — implementation

2026-09-23. Base HEAD `9e74e8181894c67f6aa8cf5db8cc2402702dbc93`.
Companion contract: `file-transfer-initial-fallback-owner-rca-20260923.md`.

## Change

`Get-FileTransferCoordinatorSnapshotInitialPublications` now admits an initial
`post_tuna_fallback_v6` / protocol 6 publication at live epoch zero. The executable
analyzer change is confined to the route/protocol eligibility predicate; its
complete-history, exact owner, receipt, token, generation and forwarded-effect
checks are unchanged. No Core, transport, service, GUI or parent acceptance
logic changed.

Regular-v4 still permits its existing same-owner republication. Tuna-v4 and
fallback-v6 use this initial-owner association only before the first publication.
Later publications still need their independent transition/owner proof. Active
Tuna-v6, fallback/protocol-4 and regular/protocol-6 remain invalid.

## Verification

The four RCA characterization cases previously reproduced the missing-owner
rejection. They are now positive contract cases, with two additional genuine
Core-issued second-publication rejection cases. All fixtures are generated
fresh, not derived from the retained live run.

- Six focused cases pass under both Windows PowerShell and PowerShell 7.
- Four initial-publication cases cover inbound/outbound and independently
  numbered runtime/leg generations (1/1 and 7/13). Each retains exactly one
  proof and passes full, compact and GUI classification checks.
- Each initial-publication case checks 34 negative resolver controls, including
  missing/duplicate/rejected/retired/mismatched owners, incomplete state history,
  scope mutation, illegal route/protocol pairs, epoch/commit/terminal changes,
  missing/duplicate/mismatched effects and invalid receipts. Protocol controls
  also include consistently mutated route tuples, so rejection does not depend
  only on a mismatched single tuple. That is 272 control executions across the
  four fixtures and two hosts.
- Two second same-owner fallback publication cases retain only the first proof
  and fail the later-publication requirement. Full/compact and GUI consumers
  agree on the selected failure boundary.
- All 19 directly affected existing Regular, Tuna and snapshot route-consistency
  cases pass in `initial-route-regressions.trx`: 25 selected cases total, with
  zero failures or skips. No unrelated aggregate suite was rerun.

Positive authority snapshots and publication tokens come from Core. Forwarding
effects use the production emitter but are modeled test effects, not proof of
service callback execution, peer delivery or live acceptance. Negative resolver
mutations are not represented as full GUI acceptance tests.

Artifacts: `artifacts/initial-fallback-owner-implementation-20260923` contains
fresh per-case captures/host results, both TRX files and final integrity checks.
The prior RCA artifacts and original live input/results remain unchanged;
only the two RCA test source files were intentionally advanced into contract
tests. No historical analyzer or authority evaluation was rerun.

## Remaining gate

This fixes the initial-owner association only. The later fallback-owner
successor contract and missing second-transfer Tuna-v4 publication remain
independent requirements. No verdict is changed on the existing failed live run,
and this is not live validation of the recent-progress supersession fix.

Next: review/checkpoint the bounded diff, rebuild the portable package, and run
only `second-transfer-after-reactivation` with strict external preflights. Use
its single primary analysis to identify the next unresolved proof edge. Do not
start the full GUI matrix, weaken the parent runtime-proof gate, or tune runtime
timers, throughput, queues or retry policy.
