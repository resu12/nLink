# Agent Instructions

## Live GUI And NKN Network Runs

- Do not treat restricted-sandbox failures as authoritative evidence for live NKN, GUI bridge bootstrap, or Phase 5 route-acceptance behavior.
- Keep deterministic code inspection and focused `dotnet test` runs in the normal sandbox when possible.
- Run `tools\Run-FileTransferRouteAcceptance.ps1`, GUI smoke scenarios, bridge readiness preflight, and live NKN connectivity checks outside the restricted sandbox with explicit escalation, because the sandbox can block outbound TCP and make bridge bootstrap fail falsely.
- If a sandboxed live-network check fails, confirm it outside the sandbox before writing an RCA or changing runtime code.
- Never bypass a failed Phase 5 preflight by running scenarios anyway; first decide whether the failure is a real app/setup failure or a sandbox/network isolation artifact.

## Phase 5 File-Transfer RCA Guardrails

- Use the latest relevant artifact for RCA. If an artifact predates current HEAD and the code has changed in the affected path, rebuild and rerun the targeted scenario before changing runtime code.
- Identify the first missing proof-chain edge and fix only that edge. Do not pile on heuristic fallbacks or tune unrelated behavior to make a scenario pass.
- Treat `FileTransferCoordinator` or coordinator-issued DTOs/tokens as the only authority for route ownership, runtime unlock, retry dispatch, route commit/publication, Tuna-active state, fallback control-plane ownership, repair/replay, liveness deferral, bridge recovery, and late callback effects.
- Treat listener readiness, answer ACKs, route caches, adapter runtime dictionaries, bridge observations, pressure, queue/send acceptance, and transport observations as evidence only. They must not grant route or runtime authority.
- Keep route invariants strict: `regular_nkn_v4_fast` uses protocol `4`, `file_tuna_v4` uses protocol `4`, `post_tuna_fallback_v6` uses protocol `6`, and active `file_tuna_v6` is invalid.
- Keep goodput/performance separate from correctness. Do not tune bridge queue/concurrency, chunk size, retry cadence, throughput policy, wallet behavior, installer, GUI invite setup, or screen sharing while fixing Phase 5 authority issues.
- Rejected or stale callbacks are diagnostic-only: they must not publish route state, mark Tuna active, schedule repair, mutate progress, reissue checkpoints, retire fallback workers, defer liveness, start bridge recovery, send retry offers, commit routes, or resume fallback as authority.

## Phase 5 Verification Order

- Run focused unit/analyzer tests first.
- Rebuild the portable package before every GUI rerun that is meant to validate runtime behavior.
- Rerun only the targeted failing GUI scenarios after a fix. Do not run the full Phase 5 matrix until the targeted correctness paths pass or advance to a new first proof edge.
- For `regular-v4-live-activation-off-on-off-256mb`, do not accept SHA OK or fallback completion as proof of the route-cycle contract unless a second coordinator-published `file_tuna_v4` route is observed.
