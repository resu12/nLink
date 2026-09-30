# Primary source currentness RCA and bounded work-cost plan

Date: 2026-09-27. Status: first rejection identified. Twelve distinct focused
checks pass. One rebuilt offline canonical attempt still fails, now with an
exact original predicate: the primary reader's cumulative 20,000 ms work limit.

## Proven boundary

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-79ed2e4df2d544829268f5e932761177`.

The native handoff remains repaired: wrapper forwarding succeeds and releases,
the first parent take is accepted with matched association, and repeated take
is rejected. Primary analysis launches and reads original native source lines.

```text
canonical-source-failure; reason=frontier_source_not_current; active_ms=20003; lines_read=41947
primary-source-currentness; rejected=read:budget; base=none; close=ReadSource; exception=System.IO.IOException:frontier_source_not_current; work_ms=20001; lines=41947; units=40103250; started=True; ended=False
primary-source-first-close; work_ms=20001; lines=41947; units=40103250; started=True; ended=False
```

The first failing original condition is the direct `ReadSource` work-budget
guard, not its closed guard or the per-fragment scope/files/pins predicates.
The exact elapsed value used in that original comparison was 20,001 ms.
ReadSource's existing exception cleanup is the first close; no earlier
cancellation callback closed this input. No prior base-currentness rejection
was recorded. Because the budget check short-circuits, this does not assert
that later predicates were evaluated or current at the failing instant.

The reader had returned 41,947 of 73,754 input lines (40,103,250 UTF-16 code
units). EOF had not been reached. The later 20,003 ms diagnostic is a post-cleanup
observation, not the original comparison. There were zero analyzer evaluations
and one native parser invocation. The outer incomplete-proof assertion remains
a downstream failure, not the initiating cause.

The attempt receipt records primary PID 39056, exit 1, no timeout, no completed
proof artifacts and missing coordinator parity. Input bytes and line count are
unchanged before/after, SHA-256
`27cc52577d99cb283a020b256bcba8681100ca0a95131a8d177f55e2822bb6e2`.
Input integrity does not waive ownership or work-limit enforcement.

## Changes and verification

Only explicit test-profile diagnostics and their fixtures/tests were added.
The original short-circuit predicates receive labels without a second authority
evaluation. The budget helper receives the already-observed elapsed value.
First rejection and first close are retained separately, including progress
before CloseSource clears its stored lines. Base liveness labels also preserve
original short-circuit order and return values. No native diagnostic I/O,
callback suppression, clock reset, timing exclusion or replacement custody is
introduced. The original throw/return and cleanup outcomes remain.

Default-source projections remain unchanged:

- `LocalFrontierPrimarySource.cs`:
  `6d3f7a534b58edee63aa9085514fef421351466d71d1c1e6cb78af97d3c8f495`.
- `LocalFrontierPrimaryWorker.cs`:
  `e4814b34ff3726dd205e23d15ac353be642d7b6029c388ea479e8aa87b4b9798`.

Results: `artifacts/primary-source-diagnostics-20260927/results`.

| Check | Result |
| --- | --- |
| Default projections and predicate/first-close source contract | 3 passed |
| Default-loader isolation, both PowerShell hosts | 2 passed |
| Native valid sources, both hosts | 2 passed |
| Native read budget, changed scope/facts, closed pin, parent cancellation | 5 passed |
| Smoke rebuild | Succeeded, 337 warnings, zero errors |
| One offline canonical attempt | Failed at original read budget |

The first native-control run exposed a fixture path issue: mixed separators
were refused by original request validation. The fixture now builds its path
from individual segments. That initial failure did not exercise native source
reading. The next run passed six controls; cancellation exposed an incorrect
diagnostic assertion expecting lines after cleanup. A separate first-close
snapshot repaired the observation without changing cancellation or restoring
cleared lines. The final ten-check run passes all seven native controls and
three source contracts; the two unaffected default-loader checks had already
passed. No failed run is counted as a passing gate.

Cancellation now records `watch:request-closed`, one line at first close and
zero stored lines at the subsequent `read:closed` refusal. Budget injection
records the actual 21,000 ms value after one genuine line. Scope/facts/pin
mutations retain their original rejection and cleanup. Later disposal does not
replace first-failure evidence.

One build attempt hit Avalonia's external telemetry-log write permissions.
The retry used the inspected target's `UsedAvaloniaProducts=` condition to
disable that build-only task; it did not change runtime configuration. Changed
PowerShell files parse and the focused diff check passes. No broader unrelated
test lane or canonical retry was run.

## Cause versus remaining cost attribution

The immediate cause is proven: native input work exhausts its unchanged
20-second cumulative allowance before EOF. That clock includes attachment and
subsequent input operations; the current trace does not partition those costs.
The exact dominant operation is not yet measured, and test-profile overhead
must not be assumed to be zero.

Read-only inspection identifies plausible repeated costs, not a proven hotspot:

- Each native line/fragment and completed line calls the original custody check.
- ResourceCurrent checks scope, file/facts channel and held native pins.
- Scope validation copies/compares the original request snapshot; facts-channel
  validation compares the entire live facts body on each call.
- Mapped source reads create bounded view accessors as input buffers advance.

These observations do not authorize skipping checks, caching a successful
currentness verdict, sampling bytes or treating a retained hash as custody.

## Next bounded step

1. Measure attachment/open versus native-read work, and the cost of the existing
   scope, file/facts, pins and mapped-read operations. Instrument original
   evaluations only, under the explicit test profile; do not add predicate
   calls, stop/reset the authoritative clock or exclude work from it. Preserve
   first-failure diagnostics and account for measurement overhead.
2. Establish a deterministic representative-input control before selecting an
   optimization. If one exact comparison or allocation path dominates, optimize
   only that implementation while preserving full-byte checks, original
   frequency/order, native handle lifetime protection, cancellation, EOF and
   one-shot ownership. Do not increase the 20-second allowance.
3. Run the directly affected positive/negative controls, then rebuild and use
   one targeted offline canonical attempt. Require genuine EOF and original
   analysis progression; keep incomplete source and timeout outcomes failing.

No new live-256 test or full GUI matrix is appropriate until this offline
source-read boundary is resolved. Route correctness still requires the second
coordinator-published `file_tuna_v4`; payload SHA or fallback completion is not
that proof.

## Cleanup and checkpoint

GUI PID 39988 exited zero, without timeout or deadline extension, with complete
output capture. Its active work was 6,661.6523 ms of the unchanged 10,000 ms
allowance. Native PIDs 28908 and 54640 each report 268,435,456 bytes on
`post_tuna_fallback_v6`. Independent checks confirm both payloads have that
length, SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`,
and released exclusive read/write handles. All four recorded processes,
including primary PID 39056, are no longer running.

Source/retained/writer/semantic counts are
101,902 / 101,900 / 101,902 / 73,754. These are offline evidence, not live acceptance.

Preflight verified all 6,612 prior pins with zero mismatches:
`artifacts/native-pull-order-fix-20260927/FINAL-VERIFICATION.json`,
SHA-256 `9882e878c6e210742d348ae89f55c696b1e0470f80f2ee15d8a63b62ef9f3631`.
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`;
419 pre-existing dirty entries were preserved.

Current observations and preservation manifest:
`artifacts/primary-source-diagnostics-20260927/observations.json` and
`artifacts/primary-source-diagnostics-20260927/FINAL-VERIFICATION.json`.
No default runtime behavior, acceptance rule or timer limit changed.
No live GUI/NKN run, portable rebuild, full matrix, commit or push occurred.
