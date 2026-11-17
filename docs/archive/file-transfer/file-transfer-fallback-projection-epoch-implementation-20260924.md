# First fallback projection epoch: implementation and verification

Base HEAD: `75b2acf30e3b1c687c5ff5ceddd9cca4f6304313`, branch `v0.7.0`.
Implemented locally; not committed or pushed. The current live scenario remains
FAIL / incomplete proof until a fresh targeted run validates this runtime change.

## Change

The nine-line production addition is confined to
`NknSignalingTransport.FallbackProjectionRetirement.cs`. At the existing exact,
validated, current committed fallback disposition, an unpublished projection with
epoch zero now takes its epoch from the coordinator command's positive fallback
target epoch. The command must name fallback protocol 6 and regular NKN transport.

The change does not retire the projection by itself or supply Tuna readiness.
It preserves existing observation-populated epochs, including conflicting/newer
ones, and does not initialize an already publication-bound projection. The existing
committed-reopen path, exact retirement checks, current-binding/predecessor checks,
stale/idempotent handling, screen state, counters, and control-plane owners remain
unchanged. No route-resolver preference, analyzer classification, retry, timeout,
performance, wallet, or GUI behavior changed.

## Regression coverage

`FileTransferFallbackProjectionEpochTests.cs` adds 23 cases:

- Four inbound/outbound cases prove initial epoch binding and later authorized
  retirement both with and without a subsequent legacy epoch observation.
- Three cases preserve matching, older-conflicting, and newer observed epochs and
  retain the exact-epoch requirement for retirement.
- Fifteen controls cover missing/wrong proof, invalid epoch, protocol/transport and
  publication-scope mismatches, missing/current-invalidated binding, rollback,
  retained predecessor, and newer local/peer stop replacement.
- One duplicate-commit case preserves a subsequently observed newer epoch.

The existing retirement fixture gains an optional pre-commit return so these cases
can exercise the first disposition without pre-populating the epoch. Its default
setup and all existing assertions remain intact. The no-observation cases model
the recovered diagnostic lane state produced by committed cleanup; they do not
install an execution shadow or manufacture new route authority.

## Verified results

Artifacts: `artifacts/fallback-projection-epoch-implementation-20260924`.

| Run | Result |
| --- | --- |
| `red/results.trx` | Four expected failures before the production change: expected epoch 7, actual 0 |
| `focused-green/results.trx` | 22 pass; one malformed-null-proof test encounters the pre-existing diagnostic limitation below |
| `missing-proof-check/results.trx` | Corrected null-proof control passes in isolation |
| `affected-contract/results.trx` | **77/77 pass**, zero failures or skips |

The final 77 unique cases comprise 23 new epoch tests, 40 existing retirement and
transition pause/disposition tests, eight initial-Tuna/Core admission tests, and
six service second-Tuna publication controls. This is one directly affected lane,
not a generic aggregate or GUI matrix. Ordinary Debug was used, with isolated build
outputs under the artifact directory to preserve earlier sealed binaries.

`git diff --check` passes. Verification preserves all 142 current-live pins, all
49 previous implementation pins, and 23 immutable RCA pins. Two RCA source pins
are intentionally superseded: the production retirement file and its fixture file.
The original RCA seal and results are unchanged; no historical authority evaluator
was rerun.

## Known diagnostic limitation

A deliberately null `ProofBoundary` is rejected by Core, but routing that malformed
DTO through the existing bridge rejection logger throws in
`FormatFileTransferRouteTransitionAuthorityTuple`, which dereferences the proof to
format its type. This predates and is outside the epoch-binding change. It was not
fixed or counted as passing in the initial focused result.

The final null-proof control verifies Core rejection and directly exercises the
epoch reducer's guard without invoking that logger. It does not claim that the
malformed-DTO logging path is fixed. All other negative controls use the real bridge.
This limitation is distinct from the latest live run, which carried valid proofs.

## Next step

Review and locally checkpoint the five accumulated source/test/documentation files,
rebuild the portable package, then run only `second-transfer-after-reactivation`
outside the sandbox with strict readiness/isolation preflights and no implicit retry.
Require retirement of the exact old fallback projection, fresh second-transfer
coordinator-published Tuna-v4/protocol 4, and paired completion/integrity/evidence
checks. Initial readiness may expose a later proof edge; this offline result is not
a promise that the targeted live run will pass. Do not run the full matrix yet.

No portable rebuild, new live run, full matrix, commit, or push occurred in this step.
