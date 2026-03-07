# Remote Control 0.4.1 Quick Sanity Checklist

## Scope
- Consent handshake works (request/allow/deny/start/stop).
- Control input only runs when control is active.
- Mapping and guardrails are observable in DEBUG overlays/logs.

## Fast manual pass
1. Start two instances and connect helper <-> helpee.
2. Helper clicks `Request control`.
3. Helpee clicks `Allow`.
4. Verify both sides show `Remote control active`.
5. Move helper mouse in the viewer and press a few keys in control mode.
6. Helpee clicks `Stop control`.
7. Verify control state returns to `Off` quickly on both sides.

## Negative checks
1. Repeat request, click `Deny` on helpee.
2. Verify helper returns to `Off` (or transient `Denied` -> `Off`) with no stuck state.
3. While active, disconnect helper.
4. Verify helpee exits active control and no further injection occurs.

## DEBUG overlay checks
- Helper/helpee overlays should show:
  - control state
  - request id
  - controller peer id
  - display id/revision
  - queue info
  - guardrail counters (`clamps`, `drops`, `suppressed`, `flushes`)

## Very important log line
During active control mapping, confirm this rate-limited log event appears:
- `event=input_mapping_applied`

It should include:
- display id + revision
- incoming `nx/ny`
- clamped `nx/ny`
- capture region bounds
- mapped pixel coordinates

This is the primary sanity line for coordinate debugging without changing rendering.
