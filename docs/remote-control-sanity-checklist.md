# Remote Control Sanity Checklist

## 1) Mapping test matrix
Use **4 high-value combos** (not all permutations required).

| Combo | Helper display | Helpee display | Purpose |
|---|---|---|---|
| A | 1920x1080 @100% | 1920x1080 @100% | Baseline 1:1 |
| B | 2560x1440 @100% | 1920x1080 @100% | Higher helper -> lower helpee |
| C | 3840x2160 @100% | 2560x1440 @100% | 4K helper path |
| D | 2560x1440 @150% (Windows) | 1920x1080 @100% | DPI scaling case |

Notes:
- If possible, also run one Windows DPI case at **125%** (can replace combo D).
- Focus on correctness at corners, center, and edge drags.

## 2) Step-by-step checks
1. `ControlMode` **OFF** (helper): confirm chat typing works normally.
2. `ControlMode` **ON** + viewer focused (helper): confirm key events are sent.
3. Press `Esc`: exits control mode **only when viewer is focused** (no global hotkey behavior).
4. Helpee clicks `Stop control`: injection stops immediately and queued input is not applied after stop.
5. Stress test (10s): move mouse rapidly and verify:
   - MouseMove send rate is capped (diagnostics panel).
   - Dropped move count increases (expected under load).
   - CPU remains acceptable (manual observation).
   - Stop still works instantly during spam.

## 3) What to capture in bug report
- `requestId`
- `controller peer id`
- `displayId` + `revision`
- `captureRegionPx` + `virtualDesktopPx`
- Key log line:
  - `RemoteInput: move (nx=… ny=…) -> (px=… py=…)`
- Screenshot of diagnostics panel (helper and/or helpee side as relevant)
