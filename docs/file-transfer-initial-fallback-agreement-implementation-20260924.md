# Initial fallback agreement interval implementation

## Scope

Implements the bounded plan in
`file-transfer-initial-fallback-agreement-interval-20260924.md`, based on
`1bed386d61388d383eff2cd33a54cf7e97bc0953` on `v0.7.0`.
Changes are local and uncommitted. No application runtime or wire-format change.

`InitialDataSessionEvidence.ps1` now composes the initial startup interval with
the existing bootstrap agreement contract. It requires the exact pinned offer
from the same coordinator/writer/session/transfer/direction, then:

- Outbound: accepted `Offered -> Enabled`, followed by the empty idempotent
  session-open observation.
- Inbound: accepted `Offered -> Selected`, followed by accepted `Selected -> Enabled`.

This extension is limited to correlated version-1 negotiation. Both directions
must complete negotiation before first data-session binding, preserving the full
publication-time route/runtime boundary. Agreement source/target tuples remain
the immutable offer-time scope. Existing bootstrap operation and history
validators check state progression, identity, generation, issuer continuity,
and fingerprints. The new interval check also requires exact operation counts,
decision statuses/reasons, no fabricated receipts/predecessor, and the existing
complete aggregate generation/hash/scope chain.

Only these validated agreement operations are admitted alongside the existing
binding and unchanged-owner/publication cases. No general exemption for
`route_transition`, reason strings, or transport evidence was added. Missing,
duplicate, reordered, rejected, foreign, or scope-changing negotiation fails
closed. The original binding, owner retirement/successor, publication receipt,
forwarding effect, epoch-zero, and transitioned-fallback requirements remain.

## Regression coverage

The service fixture now has an explicit correlated variant. A test-local
loopback transport enables the production offer/accept/session-open path; the
ordinary 128,000-byte transfer completes with exact received bytes and actual
coordinator/emitter evidence in both directions. The uncorrelated variant remains.

The Core fixture invokes real coordinator acceptance and session-open decisions
between first publication and binding. It preserves independently seeded runtime
7 -> 8, leg 13 -> 14, and publication 1 -> 2 counters. Its forwarding calls are
modeled, not a claim of service delivery or live-network acceptance.

Both fixtures use Windows PowerShell and PowerShell 7, full/compact consumers,
and existing binding/successor negative controls. New agreement controls mutate
copies of normalized evidence, never positive producer snapshots. They cover
missing/duplicate history, empty or extra operations, wrong decisions and operation
roles, coherent authority substitution, issuer/fingerprint/scope changes, receipt
or predecessor fabrication, identity mismatches, hash/generation gaps, hidden
retirement, and negotiation after binding. They must reject both route consistency
and the current-publication epoch-zero startup proof, with full/compact parity.

## Verification

Artifacts: `artifacts/initial-fallback-agreement-implementation-20260924`.

- `red/correlated-red.trx`: all four new tests fail on the intended same-route
  owner-successor rejection before the analyzer change. Core also reports the
  dependent epoch-zero proof failure. Service evidence and received bytes pass.
- `first-green/correlated-first-green.trx`: all four pass after the analyzer fix,
  each retaining 48 binding and 308 successor negative checks.
- `final/affected-contract.trx`: 29 unique tests pass, zero failures or skips.
  This is the single stabilized affected-contract lane: eight startup fixture/
  host cases, six initial-fallback publication cases, six bootstrap negotiation/
  legacy history cases, seven Core agreement cases, and two checkpoint-refresh
  republication cases.
- Each of the eight startup cases retains 48 binding and 308 successor negative
  checks. Each of the four correlated cases additionally passes 192 agreement
  checks (48 cases, two directions, two consumers): 768 new checks in total.
- `Verify-Implementation.ps1` verifies all 142 sealed live pins and 11 unchanged
  RCA pins; three RCA source pins are explicitly superseded by this implementation.
  `FINAL-VERIFICATION.json` records the final test results and artifact/source
  SHA-256 pins. Syntax and `git diff --check` pass.

The bounded diff review found no runtime/producer change needed and no blocking
finding. The sole production-path change is the startup interval analyzer;
coordinator, transport, GUI, catalog hash, and wire format remain unchanged.

No historical live authority evaluation was rerun. No portable build, GUI/NKN
run, full aggregate suite, full GUI matrix, commit, or push was performed.

## Next gate

Review/checkpoint the bounded diff, rebuild the portable package, and run strict
preflights outside the restricted sandbox before rerunning only
`second-transfer-after-reactivation`. Require its new parent-owned proof of the
second-transfer coordinator-published `file_tuna_v4` route and terminal/integrity
results. Full matrix remains gated; matching saved payload hashes alone do not
satisfy the route contract.

The previous live result remains **FAIL / correctness / incomplete proof**.
The earlier pressure-settlement generation fix is not newly live-validated by
this offline analyzer work.
