# Late-publication budget diagnostics

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Outcome

Implemented the bounded next step from
`file-transfer-late-publication-characterization-20260928.md`.
**29 focused window checks pass**, including nine new exact-boundary cases.
No new transfer, native-process characterization, analyzer invocation, live NKN,
live-256 or full GUI matrix was run. The prior single characterization remains
failed before the publication owner armed; its exact predicate is still unknown.

Only two existing test files changed:

- `Support/AttemptLatePublicationOwner.cs`: the existing unavailable-budget
  branch appends one compact `budget_json` object to its original IOException
  prefix. It records the original immutable stall and hold snapshots, supplied
  current counter, UTC deadline, derived QPC deadlines, signed QPC margins and
  separate `LateArm`, `HoldReserveUnavailable`, `ExpiryAllowanceUnavailable`
  flags. A zero margin fails, just as before. Frequency is in the stall snapshot.
- `AttemptLatePublicationTests.cs`: verifies the exact record and unchanged
  decisions at one tick before, equal to, and one tick after both arm and hold
  boundaries, including simultaneous failures. It also checks unchanged shape
  rejection, successful returned window, culture-independent JSON, one-line
  bounded output and preservation inside the original nested exception text.

The diagnostics are constructed **only after** the original guard rejects the
window. The guard, shape checks, arithmetic, successful return and owner lifecycle
are unchanged. There is no new clock read, logging callback, evaluator, token,
timer, retry, side effect on the scheduler or serialized mutable context. These
values are scheduling evidence, not a native timing receipt or route authority.
The successful path does not serialize or retain diagnostic data.

S+250/S+800 and the original 750/1000/3000 ms bounds and completion reserves
remain unchanged. No production Core/Infra, transport policy, primary analyzer,
acceptance rule, existing positive schedule or wrapper was edited.

## Verification

Evidence: `artifacts/late-publication-budget-diagnostics-20260928/`.

1. Before implementation, `diagnostic-red.trx`: one positive boundary passed;
   eight failing-window cases failed their new diagnostic-prefix assertion,
   reproducing the missing diagnostic rather than a changed scheduling decision.
2. After implementation, `window-focused.trx`: all 29 `LateWindow_` cases passed
   (12 original input/budget guards, nine new boundary cases, eight original write
   checks). The Debug test build succeeded. The build log retains 338 warnings;
   none refers to either edited file. No unrelated aggregate suite was rerun.
3. Reviewed the two-file diff against hash-checked before-images. Entry matched
   all 2,003 previous pins and 509 dirty entries. The two edited test sources and
   rebuilt test DLL are the only intentional pinned replacements; their exact
   prior bytes are retained under `before/`. The final preservation check covers
   all other pinned sources, binaries, previous reports and failed-run artifacts.

No commit or push. The new handoff is the sole additional non-ignored file.

## Next gate

The test profile is ready for **one separately gated, rebuilt small offline
original-pair characterization** to capture the actual scheduling values. It is
not expected or required to pass merely because diagnostics were added. If it
fails, retain that first result and distinguish late arm from missing original
hold reserve using the captured counters; no automatic retry or deadline tuning.

Use a fresh artifact directory and one-run marker with current source/binary
pins, isolated process environment and explicit escalation for the original
native peers. Do not reuse or modify the previous run's marker/runner/evidence.
This is the Debug named-pipe harness, not portable GUI/live NKN. A production GUI
validation would require its separate portable rebuild and network preflights.

The prior UTC inference of insufficient remaining hold time remains only an
inference; these synthetic tests do not prove a predicate for that retired run.
If the next measured episode cannot fit the unchanged bounds, review the test
contract separately instead of changing production timers, extending holds or
selecting another episode. Live-256 and the full GUI matrix remain later gates.
