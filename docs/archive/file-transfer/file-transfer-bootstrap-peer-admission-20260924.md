# Bootstrap peer-initiation admission fix

## Evidence and scope

Baseline: `8554b98fb3e14d8840c0cbc9434eda286599e547` on `v0.7.0`.
Latest live campaign: `artifacts/second-transfer-report-live-20260924-8554b98f/live/20260924T112701Z`.
Its primary-selected failure is `snapshot_bootstrap_initiation_operation_invalid`
at receiver writer sequence 4640, exact primary source line 15415. An Accepted
`PrepareFallbackBootstrapSourceHold` used PeerEvidence with source
`tuna_unavailable_before_send` and proof kind `file_transfer_v4_state_frame_sent`.
The exact source witness is sealed alongside that campaign. No historical
authority evaluation or backfill was performed.

The coordinator previously allowed any PeerEvidence observation without a local
stop or local-disable proof kind, combined with a separately current wake. The
Service could use an unbound local observation or mismatched frame fingerprint
alongside its staged peer frame; the inbound binding check occurred after hold
creation. This explains the rejected snapshot. Other downstream timeout causes
remain subject to fresh live validation.

## Implementation

- Coordinator peer initiation requires non-default evidence, the correlated-peer
  source, direction-specific received-v6 proof kind, exact session/transfer/
  direction, and current checkpoint owner/publication/data-session/source scope.
  The check precedes hold preparation and exchange idempotence.
- The Service checks the active bridge and exact staged received-frame identity,
  provider/session lifetime, checkpoint/publication scope, and evaluation trigger
  before entering hold creation or drain dispatch. Existing binding checks are
  reused, with explicit frame-identity comparison (the generic scope comparator
  deliberately does not compare frame identity).
- A rejected local observation is not relabeled. A later actual correlated frame
  can still initiate the correct coordinator-owned exchange.
- Explicit local-runtime-disable and non-correlated legacy paths are preserved.
  Coordinator admission remains authoritative; Service checks do not grant a route.
- No analyzer catalog/rule relaxation, reporting change, throughput tuning,
  bridge retry/concurrency change, wallet change, or GUI/installer change.

## Verification and review

Artifacts: `artifacts/bootstrap-peer-admission-implementation-20260924`.

- Red regression: 8 failed / 8 passed on the unchanged runtime. Failures reproduce
  both coordinator origin gaps plus Service local-send, wrong-fingerprint, and
  replaced-frame admission in both directions.
- Initial fixture attempts hit a missing test-only logging configuration and a
  large-value async test stack overflow. Logs were redirected within the workspace
  using existing test settings; fixture state-copy assertions were isolated in
  no-inline helpers. Neither issue resulted in a production workaround.
- Initial green regression: 16 passed.
- Final focused lane: 85 passed, 0 failed. Includes 24 peer-admission cases,
  existing local-disable/write-drain/held-phase/legacy/initial-source and Infra
  controls, and fresh production snapshot full/compact/GUI parity on PowerShell
  5.1 and 7. Rejected local-send snapshots are included in the fresh peer fixtures.
- Remaining directly affected coordinator exchange contract: 25 passed, 0 failed.
  Total final unique checks: **110 passed**. No aggregate suite was run.
- Pre-commit review: no blocking finding; `git diff --check` passes. Invalid input
  returns before hold mutation and drain dispatch. Both current peer directions
  and later real-frame recovery remain covered. All 145 previous live pins remain
  unchanged, and that campaign remains FAIL.

Next validation gate: local checkpoint, fresh portable package, then one targeted
`second-transfer-after-reactivation` run with strict elevated preflights and no
implicit retries. Live success is not claimed by these offline tests.
