# Second-transfer retoggle implementation

Implements the bounded fix in `file-transfer-second-transfer-retoggle-20260924.md`.
Baseline is `8c2c6fbf874e36960cc0cd4251febf0491f471bc` on `v0.7.0`.
Changes are local and uncommitted.

## Change

The second-transfer fresh-readiness branch no longer calls `Unlock-TunaPayers`.
The live on step has already issued that action. Its prearmed/non-prearmed paths,
wallet-prompt handling, and initial-unlock failure propagation are unchanged.
The unused follow-up unlock error variable, summary field, and catch/message are
removed; two source-contract assertions now reject those obsolete remnants.

Fresh readiness remains diagnostic-only. The new-transfer bookmark, send/accept
sequence, exact coordinator-publication wait, zero-route-commit-generation check,
terminal/integrity gates, and failure reporting are unchanged. No app, Core,
transport, authority evaluator, wallet, timeout, or throughput behavior changed.

## Verification

New regressions execute AST-extracted production on/preparation statements and
the existing unlock helpers against synthetic controls. Only the helper's UI
parameter type is adapted to `object`; every UI/wallet/log-wait dependency is a
stub. These tests do not control a real app or evaluate retained live authority.

The pre-fix desired-pass regression failed on both PowerShell hosts with
`Second-transfer preparation repeated the on action ... clicks=2`.
The first test attempt exposed a fixture array-shape issue; a later post-fix
attempt exposed a missing fixture step number. Both were corrected in test setup,
not by changing production behavior, and their failed TRXs are retained separately.

Final verification passed **8 unique xUnit cases**, zero failures/skips:

- Two new host cases, each executing 36 synthetic controls: three payer modes,
  prearmed/non-prearmed activation, fresh/not-fresh readiness, observed/missing
  readiness, missing second publication, and failed initial unlock. Each host has
  24 successful preparation cases, six publication-rejection cases, and six
  unlock-failure cases. These are 72 internal controls, not 72 additional xUnit tests.
- Four existing source-contract tests for readiness, exact publication requirements,
  separate second-transfer proof, and live-reactivation wallet-prompt servicing.
- Two existing failure-reporting host cases using freshly generated Core/emitter
  evidence. Original errors, failed-phase persistence, and fail-closed classification
  remain intact. No historical authority analysis was rerun.

The publication stub in the new regression verifies that production always calls
the unchanged required wait with the new bookmark and exact scope; it deliberately
rejects when the new publication is missing despite old completion/readiness flags.
This is harness orchestration coverage, not a substitute for live publication proof.

Artifacts: `artifacts/second-transfer-retoggle-implementation-20260924`.
`Verify-Implementation.ps1` checks final TRXs, case inventories, minimal harness
delta, unchanged original evidence, and SHA-256 pins. The RCA remains a historical
pre-fix record; its two changed source pins are explicitly superseded, not rewritten.

No aggregate suite, portable rebuild, GUI/live run, full matrix, commit, or push
was performed in this step. Normal test compilation emitted existing repository
warnings but completed successfully.

## Next gate

Review/checkpoint the local changes, rebuild the portable package, and rerun only
`second-transfer-after-reactivation` outside the sandbox with strict preflights
and no implicit retry. Require the second coordinator-published `file_tuna_v4`
route plus successful completion/integrity. The prior campaign remains failed;
the retoggle correction has not yet been live-validated. The separate initial-
fallback startup path also remains unexercised in the latest live campaign.
