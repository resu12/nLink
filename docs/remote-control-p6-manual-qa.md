# Remote Control P6 Manual QA Script

## Prerequisites
1. Build the app in `Release`.
2. Start two app instances on two machines (or VM + host):
 - Helpee (shares screen)
 - Helper (views and requests control)
3. Confirm both peers are on the same app version and session can connect.

## Scenario 1: Consent Handshake
1. Helper connects to helpee.
2. Helper clicks `Request control`.
3. Verify helpee sees consent modal.
4. Click `Deny`.
5. Verify helper returns to `Off` state and can request again.
6. Request again and click `Allow`.
7. Verify both headers show `Remote control active`.

Expected:
 - Deny path recovers cleanly (no stuck `Requesting`).
 - Allow path transitions both peers to `Active`.

## Scenario 2: Keyboard Control Mode UX
1. While `Active`, keep `Control mode` OFF.
2. Type in chat on helper.
3. Enable `Control mode` ON.
4. Verify helper shows keyboard-to-remote indicator.
5. Press `Esc` while viewer is focused.

Expected:
 - Chat typing works when control mode is OFF.
 - `Esc` exits control mode when viewer has focus.

## Scenario 3: Stop Priority Under Input Spam
1. While `Active`, move mouse continuously on helper for 10+ seconds.
2. Click `Stop control` on helper during motion.
3. Repeat and click `Stop control` on helpee during motion.

Expected:
 - Stop takes effect immediately.
 - Status returns to `Off` on both peers.
 - No lingering active indicator after stop.

## Scenario 4: Disconnect Mid-Start
1. Helper requests control.
2. Helpee clicks `Allow`.
3. Before handshake fully settles, disconnect helper network (or close helper app).

Expected:
 - Helpee quickly reverts to `Off`.
 - No stale consent prompt or active state remains.

## Scenario 5: DisplayInfo Change While Active
1. Enter `Active` remote control state.
2. On helpee, change capture target or resolution (monitor switch, display settings, DPI scale).

Expected:
 - Control auto-stops for safety.
 - Helper shows screen-changed status.
 - New request is required to resume control.

## Scenario 6: Mapping Validation
1. Enter `Active`.
2. Move helper pointer to each corner and center of viewer.
3. On helpee, verify pointer behavior/overlay corresponds to expected positions.
4. Resize helper window to create letterboxing/pillarboxing and repeat.

Expected:
 - Edge/corner mapping remains correct after resize.
 - No pointer inversion or offset drift.

## Scenario 7: Unsupported Peer Behavior
1. Connect helper to an older peer without remote-control capability (or force capability off).
2. Open session header actions.

Expected:
 - Request control action is disabled or clearly unavailable.
 - No crash, no invalid control state transition.

## Scenario 8: Diagnostics and Logs
1. Run through Allow, Deny, Start, Stop flows.
2. Trigger a mapping failure (stale display info / mismatch setup).
3. Review operational logs.

Expected:
 - Logs include request/allow/deny/start/stop with requestId + peerId + reason.
 - Repeated mapping failures are rate-limited (no uncontrolled spam).
