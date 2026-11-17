# Recovery-State Refactor Phase 0 Evidence Note

Created UTC: 2026-05-29T16:35:23Z

HEAD: `67c157dc`

Canonical scenario: `regular-v4-live-activation-off-on-off-128mb`

Canonical payload: `128MiB`

## Locked Evidence Roots

- `artifacts/filetransfer-route-ab/stability-proof-20260529T-regular-cycle-128mib-immediate-off-after-epoch/regular-activation-cycle`
- `artifacts/filetransfer-route-ab/stability-proof-20260529T-regular-cycle-128mib-adopt-fallback-epoch/regular-activation-cycle`

## Failure Contract

The current canonical failure class is `runtime_unlock_recovery_coordination`.

The post-adoption 128 MiB run failed before Tuna activation: the runtime-unlock activation offer was not observed, retry was scheduled behind active negotiation, and session liveness terminalized the transfer as peer disconnected before a fresh observed retry could complete.

## Phase 0 Boundary

This phase changes evidence classification and test scaffolding only. Runtime behavior was not changed.
