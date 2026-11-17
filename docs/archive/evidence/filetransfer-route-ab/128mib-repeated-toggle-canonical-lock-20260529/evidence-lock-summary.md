# 128 MiB Repeated Toggle Canonical Lock

Date: 2026-05-29

Branch: `v0.7.0`

Source head before commit: `d956f078`

## Decision

Use `128MiB` as the canonical payload for the repeated live toggle scenario:

`regular_nkn_v4_fast -> file_tuna_v4 -> post_tuna_fallback_v6 -> file_tuna_v4 -> post_tuna_fallback_v6`

Canonical scenario name:

`regular-v4-live-activation-off-on-off-128mb`

The 128 MiB payload is long enough to prove ordered live route epochs and expose fallback/runtime-unlock recovery failures that shorter 64 MiB runs can finish before surfacing.

## Locked Evidence

### Pre fallback-epoch adoption

Root:

`artifacts/filetransfer-route-ab/stability-proof-20260529T-regular-cycle-128mib-immediate-off-after-epoch/regular-activation-cycle`

Result:

- Operator verdict: `INCONCLUSIVE`
- Hard failures: `0`
- Live route epoch proof: `pass`
- Meaning: the route proof succeeded, but fallback V6 did not terminalize. The useful signal was stale fallback transport epoch proof and terminal starvation, not route selection failure.

### Post fallback-epoch adoption

Root:

`artifacts/filetransfer-route-ab/stability-proof-20260529T-regular-cycle-128mib-adopt-fallback-epoch/regular-activation-cycle`

Result:

- Operator verdict: `FAIL_PROTOCOL_OR_INTEGRITY`
- Failure phase: `activation_offer_send`
- Failure reason: `activation_offer_not_observed`
- Meaning: this run failed earlier, while still on regular V4, before Tuna activation and before exercising the fallback epoch adoption fix.

## Current Conclusion

The 128 MiB run is now the correct acceptance pressure test. The next blocker is not active `file_tuna_v6` and not route selection. It is runtime-unlock/recovery coordination: an unobserved Tuna activation offer can leave the transfer in regular V4 until liveness terminalizes the session before a fresh observed retry succeeds.

## Constraints

- Regular NKN remains `regular_nkn_v4_fast`, protocol 4.
- Active Tuna remains `file_tuna_v4`, protocol 4.
- Fallback remains `post_tuna_fallback_v6`, protocol 6.
- Diagnostic regular-NKN V6 remains unsafe opt-in only.
- Active `file_tuna_v6` must not return.
- No throughput tuning or bridge queue/concurrency tuning is included in this lock.
