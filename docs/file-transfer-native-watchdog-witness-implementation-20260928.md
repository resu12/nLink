# Native watchdog witness: bounded implementation

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result and boundary

Implemented the single-root watchdog diagnostic contract reviewed in
`file-transfer-native-watchdog-witness-contract-20260928.md`.
The genuine watchdog fixture now binds its original native witness on both
Windows PowerShell and PowerShell 7. It still has exactly one unresolved primary
failure and zero recovered failures. This is diagnostic compatibility, not a
runtime repair or acceptance waiver.

Only `tools/FileTransferOps/LocalNativeFrontierWitness.ps1` and six test/support
files changed. No runtime, coordinator, recovery classifier, acceptance rule,
semantic catalog, transport scheduling or live-test setting changed. No new
runtime scheduling hook was needed. No commit or push was made.

## Implementation

The native resolver recognizes `frontier_stall_repair_due` only alongside the
existing native custody and original-primary gates. Its watchdog-specific branch
requires one unchanged root, no `StallSuccessor`, exact generations, frontier and
highest, inbound direction, zero initial elapsed time, the 750 ms stall interval,
and authority-effect `none`. It checks the final recent arm and its one-second
deadline, root/recheck physical order and writer timestamps, and the original
write/completion/marker bindings. Missing, malformed or overflowing required
watchdog writer clocks omit the diagnostic.

The same-transfer episode scan rejects competing initial roots even when their
reason or source fields would exclude them from a favorable root search. It
also rejects pause/terminal/data-session invalidation and contradictory source
fields. Existing budgets, 1-64 witness cap, source uniqueness, one-shot custody,
native completion ranges and false authority flags remain in force.

Recent-origin successor handling is unchanged. Watchdog recent-arm rearms are
supported; watchdog stall successors deliberately remain unsupported and omit
this optional diagnostic. Late accepted publication is not passed to a recovery
closure and cannot recover the original failure.

## Genuine regression and negative controls

The seeded Core fixture holds the existing service gate until the real recent
deadline, then invokes the existing watchdog request path before releasing the
gate to the real recent callback. It asserts which path won; there is no retry
until a favorable race. Real writes, native lifecycle objects, writer records,
receipt and accepted completion are retained. The rearm and successor profiles
also use actual service writes. No positive fixture rewrites reason strings or
forges a receipt or primary failure.

The initial two-host regression failed at `native_frontier_stall` before the
resolver edit. After it, both hosts produced `PublishedWithinObservationBound`,
28 witnesses and exactly two original coordinator evaluations. The optional
diagnostic's three authority flags remained false. The original full/compact
views and correlation proof serialize identically before and after the resolver.
This seeded join test does not claim a fresh full original-app report integration.

Negative-only tests inject faults into decoded row fields after the original
primary proof/views are computed. They do not rewrite native files, original
source arrays/index events or receipt, and do not perform another evaluation.
Each checks the exact omission reason and the expected number of injected rows.
Coverage includes missing/unknown/reordered/duplicate/conflicting roots, a
competing differently scoped root, missing/stale/wrong-generation rechecks,
highest/direction/authority/source mismatches, malformed clocks, deadline
boundaries, invalidation and duplicate evidence fields.

## Verification

95 focused tests passed, with zero unexpected failures:

| Coverage | Passed |
| --- | ---: |
| Genuine watchdog late-publication diagnostic, both hosts | 2 |
| Watchdog rearm/successor and negative controls, 30 per host | 60 |
| Existing recent-origin/native-join custody controls, 14 per host | 28 |
| Existing missing-native-marker control, both hosts | 2 |
| Existing watchdog-ordering contract, both hosts | 2 |
| Existing rejected-completion producer control | 1 |

The watchdog-ordering lane ran once after stabilization; each host exercises
50 classifier variants. Its strict timely-publication recovery predicate is
unchanged. Existing join controls cover original failure/context ownership,
source mutation, completion/hash/projection, native lifecycle/frame/root,
witness overflow, replay and cancellation. Only newly added or directly affected
cases were run after follow-up test edits.

The Debug test assembly was rebuilt without project-reference rebuilds: zero
errors, 338 existing warnings, none in the changed C# files. The invocation-only
`UsedAvaloniaProducts=` property avoids the unrelated telemetry write; repository
build settings were not edited. The ordering and rejected-completion fixtures
used all four scoped operational-log environment values and restored them.
Scoped `git diff --check` passed.

The two expected pre-fix failures are retained separately from the 95 passes.
TRX files, build logs, evidence summary and before-edit copies are under
`artifacts/native-watchdog-witness-implementation-20260928/`.

## Preservation and next step

At entry, all 1,525 prior pins and 485 dirty entries matched the preceding seal.
The final verification permits only the six existing source/test edits, the new
negative helper, this report and the rebuilt test assembly. Other pinned code,
binaries, prior reports and saved captures remain unchanged. The final manifest
chains the preceding seal and pins this turn's results and selected native
fixture evidence; its independent check verifies the resulting dirty state.

No old capture was reclassified as fresh native custody. No portable build,
original-app transfer characterization, live NKN run, live-256 test or GUI matrix
was performed. This does not establish a canonical live witness.

Next bounded step: review the separate all-copy physical-send delay profile for
the small offline characterization. Its scheduling/completion contract must be
settled before implementation or another original-pair run. Keep watchdog stall
successors, canonical 256 MiB witness and live enablement as separate later work.
