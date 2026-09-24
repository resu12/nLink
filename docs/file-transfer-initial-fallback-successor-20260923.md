# Initial fallback startup successor implementation

## Scope

This completes prerequisite 2 of `file-transfer-initial-fallback-startup-20260923.md`,
building on the first-session binding in `file-transfer-initial-fallback-binding-20260923.md`.
The baseline remains `0a2cd9d37dbd81149e367f6ce8729d0e0f7e82a0` on `v0.7.0`.
The accumulated changes are local and uncommitted.

The new analyzer path accepts only the exact initial fallback startup successor:

- A validated initial owner and first publication, including its bound forwarding effect.
- A complete aggregate generation/hash/scope chain containing exactly one validated
  first-session binding. Its owner and publication fingerprints must match that
  initial proof. Identity mismatches, scope changes, missing history, and duplicate
  bindings fail closed, including hostile non-mutating observations.
- Accepted `StartTransfer` with reason `post_tuna_fallback_v6_route`, fallback protocol 6,
  epoch zero, publication 1 to 2, and runtime and leg each advancing by one in their
  own generation domains. The bound session and unrelated scope remain unchanged.
- Exactly one old owner and publication retirement, one new owner and publication,
  and one attempted command. Retired tokens remain immutable, publication receipts
  stay exact, stale authority reuse is rejected, and one exact forwarding effect is required.

The existing `state_refresh_received` path still requires its prior committed
Tuna-to-fallback transition and delivered bootstrap. Initial startup does not
fabricate either. No runtime behavior, transport tuning, catalog payload format,
or authority source changed in this step.

## Additional checks found during implementation

Removing the new publication from normalized evidence could previously make that
startup disappear from route analysis. It now produces an explicit missing-successor
finding rather than a route pass.

An independent-counter Core fixture also exposed the generic epoch-zero check for
a startup publication that remains current. That check now consumes the same exact
startup proof as the route reducer. It does not accept an epoch-zero publication
based only on its reason, route, generation, or presence of a binding marker.

## Verification

Artifacts are retained under `artifacts/initial-fallback-successor-20260923`.
The original desired-pass test failed on both PowerShell hosts at the expected
same-route successor finding; `red/successor-red.trx` retains that reproduction.
Intermediate failures are retained separately, including harness-scoping and
numeric-conversion mistakes corrected while adding the negative controls.

The positive fixtures are:

- A fresh production-service loopback transfer of 128,000 bytes, with exact received
  bytes, ordinary send/offer/manifest handling, actual publication forwarding, and
  production-emitted evidence in both directions.
- Fresh Core decisions and the production emitter, with explicitly modeled forwarding,
  exercising runtime 7 to 8, leg 13 to 14, and publication 1 to 2 in both directions.
  These are not live-network or peer-delivery claims.

Both fixtures run under Windows PowerShell and PowerShell 7. Full and compact
consumers must agree, with complete snapshot evidence, route pass, and no findings.
Each fixture/host checks 48 malformed binding markers and 308 successor rejection
cases (77 cases across two directions and two consumers). The latter also verify
that malformed evidence cannot retain the current-publication epoch-zero proof.
Successor controls mutate copies of normalized evidence from the independently
parsed full and compact inputs; the positive producer evidence remains unmodified.

The final affected-contract lane covers these fixtures, initial regular and fallback
publications, initial Tuna/fallback exchange, checkpoint republication, and the existing
two-direction route-cycle reducer. Final TRX totals and source/artifact SHA-256 pins
are recorded in `FINAL-VERIFICATION.json` by `Verify-Implementation.ps1`.
All 29 final cases passed, with zero failures or skips. All 139 prior live pins,
12 immutable RCA-artifact pins, and 25 immutable binding-artifact pins still match.

No aggregate suite, GUI matrix, portable build, live network run, historical authority
re-analysis, commit, or push was performed. Previous live evidence remains historical:
the targeted live scenario is still `FAIL/correctness/incomplete_proof` until rerun.

## Next gate

Review the accumulated binding/successor diff and make the appropriate local checkpoint.
Then rebuild the portable package, run strict preflights outside the restricted sandbox,
and rerun only `second-transfer-after-reactivation`. Its parent-owned analysis must
establish the required second coordinator-published `file_tuna_v4` route; payload SHA
success or fallback completion alone is not acceptance. Do not start the full matrix
or tune throughput to compensate for a correctness failure.
