# Selected-failure membership borrower

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the bounded offline step in
`file-transfer-gui-integrity-handoff-review-20260926.md`.

## Implemented boundary

The existing selected-failure witness remains the only code that opens and
verifies its writer. A test-only hook retains the result of that original
successful membership pass and its private source-to-writer position map.
At the original witness's successful return, the opt-in observer binds that
result to the exact context, index, selected failure and original work totals.

After original reports and witness/completion preparation, a one-use diagnostic
borrower can consume that retained evidence. It requires the original worker,
reader, custody, completed evaluation pair, context, index, selected failure,
arrays/views and cumulative clock. It checks the original event/raw-string
reference, source coordinate, selected index, writer and sequence. The private
map comes from the original EOF membership pass; no file is reopened or hashed,
and no evaluator is called by the borrower.

Disposing a borrower invalidates only that borrower. It cannot close the
original reader or custody. Original context/worker cleanup invalidates any
still-active borrower. Copies and substituted references cannot act as the
original. Consumption is separately one-shot; reuse checks do not clear the
writer's one-shot slot or replace index zero with a made-up failure witness.

The implementation uses the new `NLINK_TEST_MEMBERSHIP_BORROWER` compilation
symbol in isolated test hosts and an explicit test-worker opt-in. The ordinary
production loader exposes none of the borrower API. The existing terminal
collector compilation profile keeps its original constructor/map behavior.
The production preprocessor branch still returns the same original membership
type and values.

No production analyzer source, acceptance rule, coordinator/runtime path,
wire format or live entry point was changed. The pre-verdict selected-failure
reservation guard remains unchanged. A missing optional binding reports
`unavailable`, preserving the original witness, completion and failed verdict.

## Focused verification

The three new xUnit cases pass on both Windows PowerShell 5.1 and PowerShell 7:

- Active borrower survives until original cleanup, then becomes invalid.
- Disposed borrower leaves the original writer and publication completion valid.
- Deliberately omitted optional binding leaves the original result intact.

Each successful borrowed result exercises 12 pre-issue reference controls and
22 consumption controls, including copied result/owner/reader/custody/pair,
context/index/failure/array/view substitution, wrong selected index/writer/
sequence, foreign or stopped clock, changed original raw reference, copied
selected event, expiry, and record/code-unit exhaustion. Clock and budget
faults are labeled test-only predicate mutations, not genuine elapsed-time or
live-network failures. Repeated issue/consumption is rejected.

The final successful observations have 11 full and 5 compact source records,
one 21-record writer EOF traversal, zero additional writer reads, the original
three evaluations, 161 cumulative record visits and 133826 cumulative code units.
Observed cumulative work in that final run is 308-527 ms, below the original
20-second limit. The prototype's
4096-visit cap remains separate from the larger production limits.

The fixture is the existing seeded offline publication failure with a `2B`
parent request, not a real completed 256 MiB transfer. The original witness and
publication completion remain valid while the original primary verdict stays
`FAIL_PROTOCOL_OR_INTEGRITY` / `fail`. Existing parent checks continue to reject
its current-runtime claim. No evidence is joined with another attempt's terminal
fixture, and no terminal-integrity or whole-scenario capability is issued.

Builds succeed with 257 warnings and zero errors. The guarded native helper also
compiles in the isolated PowerShell hosts. New/modified PowerShell scripts parse.

## Regression finding: stale source-only assertion

The relevant regression lane exposed an older expectation in
`Support/RetainedAnalysisIntegrationGenerated.ps1`: it required
`INCONCLUSIVE` and described any other verdict as promotion into acceptance.

Current evidence at
`artifacts/publication-offline-integration-20260914/bbe992293ed54cfdb6e45fdf49ec3c2c/integration-5/valid/`
instead shows `gui_completed=1`, `gui_integrity_ok=0`, no selected proof failure,
and `FAIL_PROTOCOL_OR_INTEGRITY` / `fail`.
The unchanged analyzer infers completion only from the two terminals
(`AnalyzerOrchestration.ps1:3115`), preserves absent integrity as false
(`:3058-3059`), and rejects it at the existing independent gate (`:6184`).

Only this stale test assertion was corrected: it now requires those exact
completion/integrity values, the protocol/integrity failure class and failed
gate. It does not allow PASS or accept arbitrary alternative verdicts.
The corrected test passes on both hosts; no analyzer change was necessary.

## Test ledger

Artifacts: `artifacts/selected-membership-borrower-20260926/results/`.

| TRX | Passed | Failed | Meaning |
| --- | ---: | ---: | --- |
| borrower-initial | 0 | 2 | Observer attempted to resolve the witness function before the original lazy import. |
| borrower-loader-corrected | 0 | 2 | Nested in-memory source had a path-quoting error. |
| borrower-seam-corrected | 2 | 0 | Active/disposal cases on both hosts. |
| borrower-final | 3 | 0 | All new cases, including optional omission, on both hosts. |
| borrower-regression | 7 | 1 | Eight directly affected tests; only the stale source-only assertion failed. |
| borrower-stale-assertion-corrected | 1 | 0 | Exact current source-only failure assertion, both hosts. |

The first two failures were test-observer wiring errors; their artifacts remain.
The observer now substitutes the original module at its existing import seam,
without moving the original witness call or verdict block.

All **11 distinct targeted tests pass** in their latest relevant runs. The
regression lane ran once; only its failed test was rerun after correction.
Across the six TRX runs there are 18 executions, 13 passes and 5 failures.
No aggregate analyzer/contract suite or GUI matrix was run. The compatibility
lane covered the directly affected witness/completion, seeded-positive/
no-selected-failure, terminal-membership and Frontier-primary tests.

## Remaining boundary

This proves post-witness resource coexistence only. It does not remove the
pre-verdict selected-failure guard, prove a terminal/file join for that selected
failure, implement real GUI payload adoption, or make the canonical live-256
scenario ready. The next integration must put selected-failure membership and
real terminal/payload evidence under one actual attempt owner; separate fixtures
cannot be combined to claim success. Existing real-GUI custody/scope and full
route-cycle obligations remain as specified by the reviewed contract.

## Preservation

All inherited pins matched before work. Four previously dirty source files were
edited surgically for the opt-in implementation. The formerly clean stale test
assertion is the fifth edited existing file. Six test/support files and this
report were added. The other 167 baseline dirty files are preserved.

The 12737 unique inherited pins have only five expected changes: the four
implementation source files and the rebuilt test DLL. The other 12732 match;
the stale assertion file was not in that inherited pin set and its original
hash is separately recorded. HEAD/branch are unchanged and the final dirty
count is 179. PowerShell parsing, touched-file whitespace checks and
`git diff --check` pass.

The seal is
`artifacts/selected-membership-borrower-20260926/FINAL-VERIFICATION.json`.
It retains the prior seal references and pins current source/test evidence.
No portable build, live test, full GUI matrix, commit or push was performed.
