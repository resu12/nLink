# Primary reader work profile and file/facts follow-up

Date: 2026-09-27. Status: 13 focused checks pass. One rebuilt offline canonical
attempt identifies file/facts currentness as the dominant measured reader cost.
The unchanged 20-second work guard still rejects before EOF. No runtime
optimization or acceptance change is included in this step.

## Measured result

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-1985cac0d6ce475da9b335cb6b308c95`.

The original native handoff again succeeds and rejects repeated take. Primary
analysis then returns 42,906 of 73,721 lines (41,005,856 code units) before the
original `read:budget` condition rejects at 20,001 ms. First close is
`ReadSource`, with EOF false. There are zero analyzer evaluations and one
native parser invocation. This remains a failed integration gate.

Authoritative-clock totals at the later diagnostic:

| Phase | Work |
| --- | ---: |
| Attachment, including initial scope creation | 47.5745 ms |
| Source opening | 1.1924 ms |
| Native ReadSource calls | 19,954.7140 ms |
| Total | 20,003.4809 ms |

These phase totals sum exactly to the observed cumulative work. There are
42,907 ReadSource calls: 42,906 returned lines and the final budget refusal.

The four measured checks are separate, non-overlapping original predicate
evaluations. Mapped reads are measured separately within the reader:

| Operation during active reading | Calls | Time | Share of native read work |
| --- | ---: | ---: | ---: |
| Base identity/liveness | 129,971 | 861.5515 ms | 4.318% |
| Scope snapshot | 87,065 | 278.4094 ms | 1.395% |
| File/facts channel currentness | 87,065 | 15,191.6573 ms | 76.131% |
| Native pin currentness | 87,065 | 3,380.3559 ms | 16.940% |
| Mapped source read | 1,255 | 63.0017 ms | 0.316% |
| Remaining native read work | — | 179.7382 ms | 0.901% |

The extra base checks are the original per-ReadSource entry checks. The remaining
87,065 base checks accompany the same number of scope/files/pins evaluations;
the failed budget entry does not evaluate base or later predicates.

The profile separately records 192 checks of each kind during other active
work, and one check of each kind while the authoritative clock is stopped.
Those inactive observations are not misreported as consumed work.

The immediate target is therefore the original
`LocalFrontierParentFileChannel.IsCurrent` path, including its nested
`Facts.IsCurrent`. This is attribution to that whole path, not proof that one
specific inner comparison dominates. Attachment, opening and mapped I/O are not
the leading measured costs in this run.

## Instrumentation contract

All instrumentation is behind the existing explicit test-profile symbol.
Default production source projections are unchanged:

- `LocalFrontierPrimarySource.cs`:
  `6d3f7a534b58edee63aa9085514fef421351466d71d1c1e6cb78af97d3c8f495`.
- `LocalFrontierPrimaryWorker.cs`:
  `e4814b34ff3726dd205e23d15ac353be642d7b6029c388ea479e8aa87b4b9798`.

Each measured predicate executes its original expression once and returns the
original value. Finally blocks record failed/throwing calls without swallowing
exceptions. Original short-circuit ordering and first-failure/first-close
diagnostics remain intact. Stage measurements read the original cumulative
Stopwatch at its existing boundaries; no new start, stop, reset, timing exclusion
or budget adjustment is introduced. Mapped-read measurement wraps the unchanged
read body and return value.

High-resolution counters include elapsed scheduling time, not CPU-only time.
The authoritative work clock continues to charge profiler overhead.
The unassigned 179.7382 ms includes original reader/bookkeeping work and some
instrumentation overhead; it is not an isolated measurement or upper bound of
all profiler perturbation. Time already inside measured getter bodies can also
be affected by instrumentation, compilation or scheduling. No overhead is
subtracted to claim a pass.

The earlier unprofiled diagnostic returned 41,947 lines versus 42,906 here.
Inputs and scheduling differ, so that difference is not a profiler-overhead
estimate or a throughput improvement claim.

## Verification

Results: `artifacts/primary-work-profile-20260927/results`.

- Two unchanged default-source projections.
- Original short-circuit/first-close source contract.
- Profiling source contract: original expression once, finally accounting,
  unchanged clocks, and no authority evaluation while formatting the profile.
- Seven original native controls: valid sources on both hosts, read budget,
  changed scope, changed facts, pin closure and parent cancellation.
- Two default-loader isolation checks.

All 13 checks passed on the first run. Native controls verify that read-call
counts equal returned lines plus the EOF/refusal attempt, predicate counts obey
short-circuit ordering, active/inactive observations remain distinct, clocks are
stopped on cleanup, and formatting does not mutate counters or clocks.
They retain original success/failure and cleanup outcomes. Cancellation can be
won by any original cancellation watch; this run observed `watch:facts-cancelled`.

The small native controls validate instrumentation, not canonical capacity.
The subsequent smoke rebuild completed with zero warnings/errors. Changed
PowerShell scripts parse; the focused diff check passes. Exactly one offline
canonical attempt followed, failing at the original work guard after about
2 minutes 51 seconds. No canonical retry or unrelated aggregate lane was run.

## Bounded next step

1. Build a representative deterministic native-input control for the measured
   file/facts path, retaining the original facts size and near-canonical record
   count. Keep the three-line controls for semantic refusals, not performance.
2. Attribute that path's cost between its original fixed offer comparison and
   nested facts validation (header/body comparison and acknowledgment where
   present). Measure original calls only; do not re-evaluate currentness for
   logging. Do not expand profiling to unrelated transfer or GUI paths.
3. Select only the dominant inner implementation for optimization. Preserve
   every byte comparison, original check frequency/order, mutable-state rules,
   native handle lifetime protection, cancellation and failure behavior. A
   faster exact comparison/allocation strategy may be appropriate; a cached
   successful verdict, sampling, path reopen, or skipped per-line check is not.
4. Verify mismatch boundaries, state transitions, disposal/cancellation and the
   representative case. Rebuild before one targeted offline canonical attempt.
   Require genuine EOF and original analyzer progression before advancing.

Do not tune the pin path merely because it is second largest, enlarge the
20-second allowance, change transfer queues/chunks/retries, or grant authority
from a hash. No live-256 test or full GUI matrix is needed for this next local
step. The live route-cycle contract still requires the second
coordinator-published `file_tuna_v4`.

## Evidence and preservation

Primary PID 40400 exited 1 without timeout. Its 73,721-line input remains
unchanged before/after, SHA-256
`c680abc04039044bcc318ce210fba8276a61e459b0dff19d9ba8fcc318431567`.
No completed proof artifacts or coordinator parity result were produced.

GUI PID 7248 exited zero with complete output capture, no timeout or deadline
extension. Its active work was 6,894.9951 ms of the unchanged 10,000 ms limit.
Native PIDs 57408 and 25536 each report 268,435,456 bytes on
`post_tuna_fallback_v6`. Independent checks confirm both payload lengths,
SHA-256 `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`,
exclusive read/write access after cleanup, and all four processes no longer
running. Source/retained/writer/semantic counts are
101,867 / 101,865 / 101,867 / 73,721. None of this is live route acceptance.

Preflight verified all 6,818 prior pins, with zero mismatches:
`artifacts/primary-source-diagnostics-20260927/FINAL-VERIFICATION.json`,
SHA-256 `fb076d83229f057d94e44d6984f9ab7fdb5e8c56b358b65531e7478b80e162d4`.
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.
The 423 pre-existing dirty entries are preserved.

Observations and checkpoint:
`artifacts/primary-work-profile-20260927/observations.json` and
`artifacts/primary-work-profile-20260927/FINAL-VERIFICATION.json`.
No default runtime behavior, acceptance rule, timer limit, live GUI/NKN run,
portable rebuild, full matrix, commit or push changed/occurred.
