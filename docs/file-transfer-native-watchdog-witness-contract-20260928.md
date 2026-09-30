# Native watchdog witness compatibility: reviewed contract

Date: 2026-09-28. Review and existing focused tests only. No resolver, runtime,
acceptance, test-source or build changes. No saved-capture reanalysis, transfer,
portable build, commit or push.

## Decision

The mismatch is real, but replacing the resolver's reason equality with a
two-name allowlist is insufficient. Add a bounded watchdog-specific diagnostic
binding branch, retaining the existing recent-callback branch. The branch must
prove the exact original stall/recheck interval, not perform recovery analysis.

For the first implementation, recommend the observed **single stall root with
no StallSuccessor** profile. RecentArmed rearms remain allowed under the existing
native lifecycle checks. Watchdog stall-successor episodes should omit this
optional diagnostic until their physical ordering and highest-frontier mapping
have separate coverage. Existing recent-origin successor handling stays intact.
This explicit limitation avoids conflating recent-wake rearms with stall-wake
successors; it does not reject a transfer or change its primary verdict.

This document proposes that contract; it does not implement it or approve a live
run. The marker-retention fix from the preceding step remains unchanged.

## Evidence and authority boundary

Entry baseline: branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`; all 1510 pins and 484 dirty entries
match the marker-retention seal.

The latest saved small diagnostic run is
`artifacts/native-diagnostic-custody-implementation-20260928/pwsh-frontier-schedule-985400656c1f4ae1bad8739793a76e25`.
Literal semantic lines 1052 and 1057 show watchdog stall generation 5, followed
by successful recent recheck generation 1, both frontier 1/highest 7. Root start
is 10:40:38.6461634Z, root deadline 10:40:39.3961634Z, and recent deadline
10:40:38.6378107Z. Root/recheck writer attempts are .6475140Z/.6502483Z.
There is no stall-successor diagnostic between the root and first advancing
write at semantic line 1076. These are literal observations, not a reconstructed
native lifecycle or newly selected witnesses.

Its original observation still records `SelectedFailure=(none)`, resolver calls
0, and `PublishedBeforeOriginalDeadline`. Its original primary verdict remains
`FAIL_PROTOCOL_OR_INTEGRITY`. The native payload text was cleared during cleanup;
this review neither recovers that custody nor invokes the resolver on old files.
The mismatch is therefore a conditional source-backed rejection, not an observed
`native_frontier_stall` exception from that run.

| Layer | Current behavior | Consequence |
| --- | --- | --- |
| Service | Watchdog and recent callback use the same stall path; an existing root keeps its start/deadline. | Origin is not new route authority. |
| Native producer | Captures actual Stall/Rechecked objects and validates source, lifecycle and UTC/QPC bounds. | Producer validity does not require a recent-callback reason string. |
| Ordinary recovery classifier | Supports watchdog-first with exact interval checks and timely forward state. | Late publication must remain unresolved under this closure. |
| Native diagnostic resolver | Requires the recent-callback reason despite binding a native stall receipt. | An otherwise eligible watchdog episode is omitted. |

Relevant source: `tools/FileTransferFrontierGraceHandoff.ps1:1387` (particularly
1505-1528 and the competing-root scan); `tools/FileTransferOps/LocalNativeFrontierWitness.ps1:150`;
`SessionFileTransferService.FrontierPublicationBoundary.cs:241`;
`SessionFileTransferService.FrontierPublicationBoundaryValidation.cs:120`;
`FileTransferCoordinator.FrontierPublicationClock.cs:23`.

## Proposed exact binding requirements

Apply these additional checks only to the new watchdog-origin branch. Keep all
existing failure/custody, receipt, write, frame, completion and witness gates.

1. **Original eligibility:** the primary has already selected the supported
   unresolved failure object. Preserve its object identity, reason, variant,
   counters, full/compact views and final reports. Do not call the classifier,
   reducer or coordinator evaluator again from the resolver.
2. **Source and root:** the same original writer/session/transfer, inbound
   direction, fallback-v6 route/protocol, all existing wake/source generations,
   checkpoint and data-session tokens must match the native receipt. Require
   exactly one root with positive generation, `elapsed_ms=0`,
   `stall_grace_ms=750`, `authority_effect=none` and the exact watchdog reason.
   Root generation, frontier, highest, start and deadline must equal the native
   StallCaptured binding. Require no StallSuccessor entries for this first
   watchdog profile; root/rechecked/first-write generations must be identical.
3. **Recent obligation:** retain every selected RecentArmed binding and native
   generation chain. Require original progress time P and recent deadline P+1s,
   exact final recent generation/frontier/highest at the successful recheck,
   and the existing original deferred-state obligation before that deadline.
4. **Physical order and clocks:** progress precedes the original failure, which
   precedes the root; root strictly precedes the successful recheck, which
   precedes the first advancing write. Require S >= P+1s and root deadline
   D = S+750ms exactly. Require S <= root writer-attempt time <= recheck
   writer-attempt time < D. Require the recheck's committed-progress/recent-
   deadline fields to equal the final native recent arm, with inbound direction,
   `rechecked / stall_grace_armed`, `authority_effect=none`, and matching highest.
   Parse required integers/timestamps strictly; missing, malformed or overflowed
   values must omit the diagnostic, never acquire default-zero meaning.
5. **Do not select around contradictions:** scan the complete same-transfer
   episode, after the failure through the later of the selected state and
   accepted completion. Reject another initial stall root even if its reason,
   frontier or scope would exclude it from the favorable root search. Preserve
   source invalidation, pause/terminal and conflicting writer-copy rejection.
   Keep native uniqueness stricter than the ordinary classifier's duplicate
   deduplication; do not import its deduplication into native custody.
6. **Unchanged diagnostic end:** keep original native completion/marker ranges,
   exact frame, original full/compact completion projections and source-line
   witnesses. Keep 1-64 witnesses, existing work budgets, one-shot ownership,
   cleanup and all false authority flags. No externally supplied reason or
   success flag may select this branch.

The current Scope helper does not by itself check every watchdog-specific
condition above (for example direction and the root's zero elapsed field).
Retaining diagnostics with `Pick -Opaque` is not validation of contradictory
roots. Add the explicit checks; do not describe existing generic guards as if
they already cover this branch.

## Do not confuse diagnostic publication with recovery

Do **not** reuse `Get-FileTransferRecentProgressGraceStallProgressClosure` as a
resolver gate. It includes predeadline publication and returns a recovery
closure. The native diagnostic intentionally also permits a genuine first write
W before D followed by accepted completion C with D <= C < W+1s, subject to the
existing producer UTC/QPC proof. Importing the recovery predicate would make the
intended late-publication diagnostic unreachable.

Do not relax that classifier's strict publication deadline either. A matched
native diagnostic cannot recover the failure, publish a route, mark Tuna active,
or change live acceptance. The orchestration call remains after original reports
and only for the already-selected supported failure.

## Focused implementation and test plan

1. Extend the existing seeded Core/native-join fixture to generate a genuine
   watchdog root through the existing service path and a real recent recheck,
   without rewriting captured reason strings or forging a failure/receipt.
   First demonstrate rejection at `native_frontier_stall`. A deterministic
   fixture must verify which path won; retries until a favorable race are not
   a substitute. If a new runtime scheduling hook is needed, review it separately.
2. Change only the native resolver's root/recheck binding and corresponding
   focused tests. Keep recent-origin behavior and all acceptance code unchanged.
3. Cover valid watchdog/no-successor and recent-origin controls; recent-arm
   rearm; unknown/missing/reordered/competing roots; wrong highest, source field,
   generation, progress/deadline, direction or authority flag; recheck at/after D;
   missing/stale/wrong recent recheck; unsupported watchdog StallSuccessor;
   duplicate/conflicting source records; invalidation, pause and terminal.
4. Keep late accepted publication as a positive **diagnostic** with an unchanged
   unresolved primary failure. Keep missing/mismatched/rejected completion and
   marker, source mutation, copied failure/context, over-cap witnesses and replay
   as negative native controls. Verify no additional evaluator calls and byte-
   equivalent original report outputs before/after the optional diagnostic
   within the same primary invocation (not a second analysis).
5. Run the affected resolver/native-join cases on both hosts, then the existing
   watchdog-ordering lane once. No aggregate suite or GUI matrix is warranted.

The offline all-copy physical-send delay profile, new small characterization,
canonical 256 MiB witness and live enablement remain separate later work.

## Verification performed in this review

The unchanged watchdog-ordering lane passed: two xUnit host cases, each exercising
50 fixture variants through flat/indexed, full/compact, GUI-consumer and partition
paths. This verifies the existing recovery classifier, **not** the proposed
native resolver branch. No native-watchdog positive is claimed.

Two earlier attempts failed before that contract executed because persisted
writer stamps were unavailable at the default LocalAppData log. Setting only
the log path was insufficient: the existing logger also requires an artifact
directory and nonempty run/scenario IDs. With all four scoped environment values
set to this workspace, the same tests passed. Environment values were restored;
no logger code or permissions were changed. All attempts are retained.

Artifacts are under `artifacts/watchdog-witness-contract-review-20260928/`.
Only this review and ignored evidence/test outputs are added. The final pin and
dirty-state check preserves the prior code, binaries, reports and captures.
