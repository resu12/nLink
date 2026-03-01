# Session UX Contract

## Scope
This contract defines UI-facing session semantics only. It is intentionally additive and does not require any transport, bridge, NKN, message contract, or reliability/resource gate changes.

## SessionUiPhase
- `Idle`: no active session flow.
- `Waiting`: waiting on another party or a local allow/decline decision.
- `Connecting`: actively establishing the session.
- `Connected`: session is established.
- `Recovering`: session is attempting to recover (for example retry/reconnect).
- `Ended`: a session flow ended without an active recovery loop.
- `Failed`: terminal failure that requires user action (retry/new code/diagnostics).

## Runtime State Mapping
Base phase comes from `SessionRuntimeState` via `SessionUxPhaseMapper.FromRuntimeState(state, isHelper)`.

| SessionRuntimeState | SessionUiPhase | Notes |
| --- | --- | --- |
| `Idle` | `Helper -> Waiting`, `Helpee -> Idle` | Helper screen is ready-to-connect; helpee may still be pre-host startup. |
| `Waiting` | `Waiting` | Primarily helpee hosting and waiting for helper. |
| `IncomingJoinRequest` | `Waiting` | Helpee is waiting for local approval/decline. |
| `Connecting` | `Connecting` | Active connect path. |
| `Connected` | `Connected` | Active session. |
| `Rejected` | `Failed` | Conservative UX mapping for explicit rejected outcome. |
| `Disconnected` | `Failed` | Conservative UX mapping; disconnected is treated as failure/retry-needed. |
| `Failed` | `Failed` | Explicit failure state. |

## Banner Status Mapping
Overlay phase comes from `UserFacingStatus` via `SessionUxPhaseMapper.FromBannerStatus(...)`.

Only map when the banner encodes an unambiguous phase:
- `UserStatusKind.Failed` -> `SessionUiPhase.Failed`
- `UserStatusKind.Reconnecting` -> `SessionUiPhase.Recovering`

All other banner kinds return `null` (no override), because runtime state remains the canonical source for non-failure/non-recovery phases.

## Composition Rule
When used by a page VM:
1. Compute base phase from runtime state.
2. Compute optional overlay from banner status.
3. Final phase is `overlay ?? base`.

## Role Notes
`FromRuntimeState` supports a role-aware flag (`isHelper`) and a compatibility overload using `Role`.
