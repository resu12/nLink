# Canonical efficiency lookup: native gate and next witness-state edge

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: **the efficiency timeout is cleared; the single native fixture failed
at optional witness resolution. Same-pair acceptance remains incomplete.**

## Run and results

The prior lookup-fix gate was executed exactly once after rebuilding
`nLink.SmokeTests.Core` (0 warnings, 0 errors). Preflight-only and the actual-run
preflight both passed outside the restricted sandbox. Both checked frozen inputs,
the existing focused-test results, process conflicts, free space and debug
overrides. No source or test implementation was edited during this step.

Test: `FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`.
Profile: `writer-source-read-cross-process-carrier-full-canonical-tail-late-publication`.
This is the original offline native-process fixture, not a live NKN or desktop
GUI acceptance run. It does not use the small diagnostic-custody profile.

The 600-second outer, 180-second carrier, 1,800-second analyzer and 20,000-ms
cumulative native-work bounds were unchanged. The run-once marker prevents an
accidental repeat. No retry was performed.

| Observation | Result |
| --- | --- |
| .NET test | Failed after 327.0858649 seconds; not a timeout |
| Original retained analyzer | Exit 1; input unchanged, 73,885 lines |
| Parse / index | 63,173 / 9,473 ms |
| Efficiency report | **Completed in 38,778 ms** |
| Full and compact coordinator snapshots | Authoritative, complete, evidence pass; 1,507 boundaries each |
| Full/compact parity | Pass |
| Selected original failure | `filetransfer_fallback_frontier_recent_progress_grace_recheck_missing` |
| Optional resolver | One call; `native_frontier_unavailable`; zero witnesses |
| Same-pair join / diagnostic output commit | Both false |

The intentionally failed route verdict remains `FAIL_PROTOCOL_OR_INTEGRITY`,
gate `fail`. All ten original report hashes are identical before and after the
optional resolver. Neither matching file hashes nor snapshot parity changes
that verdict or proves a second coordinator-published Tuna route.

Both peers completed 268,435,456 bytes on `post_tuna_fallback_v6`. Independent
exclusive-read verification found the same SHA-256 on source and destination:
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
The receiver recorded the bounded late-publication schedule with three physical
copies and unchanged root. Initial tail hold to readiness was 63 ms, leaving
3,937 ms on that hold. These observations do not establish the missing witness.

## First missing edge and bounded RCA

The original payload capture completed with eleven native pins, exact completion
and marker origins, and the diagnostic outcome `PublishedWithinObservationBound`.
The missing edge is the original native payload/evaluation pair becoming the
original optional witness batch. Later same-pair checks and output publication
cannot run without that batch.

Code inspection found a deterministic capacity mismatch:

- `AnalyzerOrchestration.ps1` supplies every indexed event to the original full
  array (73,885 here).
- `LocalFrontierDiagnosticState.Capture` snapshots `Index.AllEvents` before the
  semantic witness callback. Its collection and property snapshots share a
  65,536-cell ceiling, despite the input-record ceiling being 262,144.
- The event-reference array alone needs 73,885 cells. Each event then also needs
  `RawLine` and `FilePath` property cells; failure, proof and view metadata consume
  additional cells. This capture therefore cannot fit the existing state guard.
- `TryResolveWitnesses` catches capture exceptions and returns null. Before the
  callback changes its state, the PowerShell omission remains the generic
  `native_frontier_unavailable`.

An offline synthetic shape probe executed the unchanged diagnostic-state class,
not an authority evaluator or native resolver. The existing seven mutation and
capacity controls passed. Five additional synthetic cases showed:

| Events, with minimal valid metadata | State guard |
| ---: | --- |
| 1 | Captured and current |
| 21,825 | Captured and current (exact 65,536-cell total) |
| 21,826 | `frontier_diagnostic_state_capacity` |
| 65,536 | `frontier_diagnostic_state_capacity` |
| 73,885 | `frontier_diagnostic_state_capacity` |

All five sizes are within the public input-record bound. The 21,825 boundary is
specific to this minimal synthetic metadata, not a universal native limit.

This proves an unavoidable capacity blocker if the native call reaches this
capture. The native log does **not** expose the internal first rejection, so it
does not establish that capacity was the first guard hit on this run; ownership,
currentness and cumulative-budget checks precede it. The printed 13.865-second
work profile is an earlier source-input observation, not a final witness budget
measurement. Its `facts_ack=False` is likewise not evidence of a new failure.
Do not infer a route/runtime bug from either value.

## Next bounded fix plan

1. Add focused, test-only first-rejection observations around native witness
   admission and state capture, preserving the original result, one-shot guards,
   cleanup, exception handling and timing. Record only stage/reason, counts and
   elapsed work; do not issue another evaluation or retry the original call.
2. Review the state representation for input-sized event identity separately
   from bounded failure/proof/view metadata. Preserve every original event's
   identity, order and exact `RawLine`/`FilePath` note-property checks. Reuse of
   existing custody is acceptable only if all these mutation checks remain
   covered. Do not solve this by filtering the original full index, taking only
   selected witness rows, substituting hashes, or blindly raising limits.
3. Verify the chosen representation with focused boundary, mutation, hostile
   property, capacity and cumulative-work controls. Keep 1–64 selected witnesses,
   20,000-ms active work, 2,097,152 visits, ownership and authority flags unchanged.
   If satisfying the gate needs a larger contract rather than a smaller equivalent
   representation, stop for explicit contract review.
4. Only after those controls pass, rebuild and run this one native fixture again
   with refreshed pins and strict preflights. No full GUI matrix or live-256 test
   is warranted yet. Stop at any newly exposed first edge.

This step documents the problem; it does not implement or approve a larger
acceptance/custody budget.

## Cleanup and preservation

Independent post-run inspection found none of the owned sender, receiver or
analyzer PIDs (57116, 59104, 36948), no conflicting native/test process, and only
four unrelated build servers. Both payloads were exclusively readable; no `.part`
files remained. The primary reports zero open pins/handles, retired evaluation
custody and cleared text. No deletion or process termination was necessary.

Entry verification matched all 4,668 previous pins and 576 dirty files. Frozen
run inputs contain 4,673 pins; their exact SHA-256 is
`16f8a4c2bb0d37783153a16232736fc607ecc4eb1552c19e495296b59a7dad7f`.
The final audit matched all 4,673 frozen pins and every pre-existing dirty file;
the dirty count is 577 because this report is the only added repository-visible
file. New evidence is under
the two artifact directories below. No commit or push was performed.

Metadata, preflights, TRX, synthetic probe, cleanup and final seal:
`artifacts/canonical-efficiency-lookup-native-20260929/`.

Original fresh fixture:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-0d9063992c4e4e80a47a234424109209/`.
