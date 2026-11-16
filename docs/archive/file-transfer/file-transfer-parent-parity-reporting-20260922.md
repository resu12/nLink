# Parent snapshot-parity reporting correction

## RCA and boundary

The parent `Assert-Phase4RouteSummary` read generic snapshot parity from text
keys absent from the route-consistency summary. That text exposes the separate
route-consistency parity result; the original generic result is persisted in
`filetransfer-coordinator-snapshot-parity-summary.json`.

This explains the earlier regular-NKN 64 MiB attempt's `missing` parent parity
projection despite generic parity PASS in its original JSON. It does not explain
away that attempt's missing capture finalization or change its overall failure.
Producer finalization was addressed separately in
`file-transfer-regular-gui-finalization-20260922.md`.

## Implemented reporting contract

`Read-RouteAcceptanceSnapshotParityReport` reads the original parity, full and
compact JSON files from the result's artifact directory. It checks:

- JSON object roots, required field types and nonnegative integer counts;
- each parity side's mode, boundary hash and boundary/transition/effect counts
  against its companion summary;
- consistency of those paired values and contract/catalog identity for a
  recorded PASS;
- that the route summary's mode, contract, catalog, hash and counts match one
  complete primary summary, including when a genuine FAIL has different sides;
- agreement with any generic parity text keys if present.

The four parent report fields now project the original verdict, failure reason
and full/compact boundary hashes. Missing companion artifacts produce `missing`;
malformed or conflicting evidence produces `invalid` with empty projected
hashes. No fallback to route-consistency parity is allowed. Genuine primary
FAIL, its reason and its distinct hashes are preserved. Legacy mode is not
upgraded by a parity PASS.

This reader does not evaluate snapshots, boundaries or coordinator authority.
It does not select attempts, issue receipts, write source artifacts or add a
new acceptance gate. Attempt identity remains the responsibility of the existing
finalization/selection contract. The checks bind reporting to companion content;
they do not claim cryptographic attempt provenance on their own.

## Verification

Latest receipts in `artifacts/snapshot-parity-reporting-20260922/` cover **24
distinct passing tests**, with no failed, skipped or aborted case:

- `parent-parity-verified.trx`: 22 focused controls executing the actual parent
  assertion. Positive, genuine-failure, legacy and distinct-family cases;
  missing, malformed, wrong-type, wrong-binding and conflicting-input cases;
  exact input-byte preservation and throwing guards against reevaluation.
- `parent-parity-regression.trx`: 2 existing parent checks: the bounded Phase 4
  fake pass-artifact gate and completed-proof warning handling. This is fake
  coverage, not a live GUI matrix. It ran once; later JSON-shape checks were
  covered by the focused controls and do not change these fixture outcomes.

The single-element-array controls first reproduced PowerShell's automatic
unwrapping behavior. Explicit object-root checking now rejects arrays in all
three input files. Both PowerShell files parse and `git diff --check` passes.
Full receipt history and commands are retained in the artifact `RESULTS.md`.

A read-only invocation on the original
`artifacts/regular-terminal-live-20260922-144e89ab/live/20260922T042925Z/regular-nkn-v4-64mb`
artifacts returns generic parity PASS with matching hash
`37b9927448fe0fdfae0bce52ded41487e3e7dd8ab8183e80dd09b08a7310d995`.
No parent report was rewritten and no analyzer was rerun. The original retained
input SHA remains
`9e8e0b4b68f45475beb58e6e2cbafcb3ac4dc0a43a478f6085527474bc2a5a82`.
The old overall acceptance remains FAIL/incomplete proof.

## Resume point

The three bounded harness fixes are implemented and offline verified: compact
NKN reporting, regular producer/wrapper finalization, and parent generic-parity
projection. Source HEAD remains `144e89ab7ff32c482f6d86b2308b82daa5142776`;
all accumulated changes remain uncommitted.

Next: pre-commit review/checkpoint of this combined scope, fresh portable build,
then only the targeted regular-NKN 64 MiB live test with strict preflights outside
the restricted sandbox. No full GUI matrix or live-256 is justified yet. No
build, live run, commit or push was performed in this reporting step.

## Combined pre-commit review

The accumulated reporting, finalization and parent-projection changes were
reviewed together on 2026-09-22. No blocking finding remains. The review checked
the reporting projection's complete consumer field set, original gate ownership,
producer cleanup/drain/capture order, immutable capture guards, failed-scenario
and timeout behavior, exact attempt identity, and the distinction between
generic and route-consistency parity. No coordinator/runtime change was added.

The latest-case union of the three steps' retained receipts is **54 distinct
PASS, zero failed/skipped/aborted**. Repeated compact-report cases are counted
once, and intermediate failures are superseded only by their passing reruns.
Unchanged suites were not repeated. `git diff --check` passes.

The checkpoint/build/single-run execution record will be maintained separately
in `artifacts/regular-harness-live-20260922/RESULTS.md`. That record, rather than
this pre-commit note, determines whether build, preflights and live acceptance
actually complete. The planned run remains one regular-NKN 64 MiB attempt,
zero setup/goodput reruns, one maximum fallback attempt and unchanged 900/180
second scenario/progress limits.
