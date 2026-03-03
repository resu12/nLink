# RC Guardrails

## Purpose

Prevent scope creep during RC. The RC track is limited to bug fixes, packaging fixes, and docs-only changes.

## What Is Blocked

The RC guardrail workflow blocks pull requests that change:

- `src/nLink.Infra.Nkn/**`
- `tools/**`
- any file whose path or basename contains:
  - `BridgeSupervisor`
  - `NodeBridge`
  - `MultiClient`
  - `Jsonl`
  - `Protocol`
  - `Contract`
  - `Wire`
  - `Bridge`

Ignored during matching:

- `**/bin/**`
- `**/obj/**`

## How To Override

- Apply the PR label `rc-override`
- The override only bypasses the workflow failure
- Reviewer acknowledgement is still required before merge

## How To Update Rules

- Edit `build/rc-guard.rules`
- Keep rules conservative
- Prefer path-prefix rules before broader keyword rules

## Rollback

Revert:

- `.github/workflows/rc-guardrails.yml`
- `build/rc-guard.rules`
- `build/rc-guard.sh`
