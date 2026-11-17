# Agent Instructions

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

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

## Test Selection and Rerun Policy

- During implementation, run only tests directly affected by the changed contract or production path.
- After small follow-up edits, rerun only tests whose covered behavior could have changed.
- Run the relevant contract lane once after the implementation stabilizes.
- Do not repeatedly run aggregate `FileTransferContract`, `FileTransferAnalyzer`, `FileTransferAcceptanceFake`, or generic Smoke lanes unless the change affects their shared infrastructure.
- Before targeted GUI validation, require:
  1. Focused regression tests.
  2. Directly affected coordinator/Core/Infra/analyzer lanes.
  3. At most one broader contract regression lane.
- Reserve full aggregate suites and the full GUI matrix for nightly, pre-release, or an explicitly requested final gate.
- A newly discovered proof edge does not require rerunning unrelated lanes before preparing its RCA.
