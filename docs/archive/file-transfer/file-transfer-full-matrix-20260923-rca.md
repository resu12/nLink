# Full GUI matrix 2026-09-23: RCA and bounded fix plan

Source checkpoint: `e35fbbf1f44fc6e2fd59a445e408687953d16694`.
Evidence: `artifacts/full-gui-matrix-20260923-e35fbbf1/live/20260923T080838Z`.
The original six-scenario verdict remains FAIL. No live rerun or historical
analyzer/authority reevaluation is part of this RCA.

Status: narrow GUI terminal-prefilter correction implemented; all 59 focused
offline tests pass. Second-transfer authority fix remains planned, not
implemented. No throughput, route-policy, payload, timing-threshold or retry
change.

## 1. Helper switch-off: late harness fault, not file corruption

The helper scenario's milestone log records:

- Route activation 08:19:58 UTC, observed 08:19:59.
- Minimum elapsed threshold 5,000 ms; gate returned at elapsed 10,008 ms.
- Peer-visible Tuna payload 58,576,896 bytes; receiver committed bytes 25,804,800.
- Attempted click 08:20:12.5325119 UTC; recorded route-to-click 13,533 ms.
- Toggle wait (prearm plus admission wait) 2,707 ms; admission visited 16,712 lines.
- Terminal won, no switch-off evidence, no fallback epoch.

The 64 MiB transfer and its hash succeeded, but the requested fault contract was
not exercised. The available timing fields do not separate every synchronization,
UI lookup, parsing and projection cost. They do not prove that one function
accounts for the entire late-click interval.

### Confirmed avoidable work

`Get-TunaGuiCompletedTerminalFromLogLine` in `tools/GuiSmoke-Windows.ps1`
used an `_terminal` substring prefilter before the full semicolon parser.
Nonterminal physical-send lease events contain `source_terminal_generation`
and/or `target_terminal_generation`. They pass that prefilter even though
`Get-GuiSmokeFileTransferTerminalDirection` accepts only three event names.

Read-only counting in the actual 18,972-line helper ledger found 4,253 old
prefilter candidates. Four unrelated send-lease classes each contributed
1,041 records. The corrected candidate check selects 13 lines. This is parser
work avoided, not an authority verdict or measured end-to-end speedup.

A deterministic regression reproduces the four classes (4,164 lines), followed
by one valid peer-visible payload. Before the fix, all four host/gate
combinations made 4,166 full-parser calls; the required count is two. Both
progress-gate variants still visit all 4,165 records.

### Implemented boundary

The preliminary check now looks for the three supported terminal event names:
`file_transfer_inbound_terminal`, `file_transfer_outbound_terminal`, and
`transfer_terminal`. It is only a negative fast path, not a replacement parser.
Matching text in a value or a suffixed event still reaches the existing parser,
which decides exact event identity, final duplicate-field value, direction,
state, and error code.

Unchanged: full synchronized snapshot/horizon, source-error handling, every
record visit, terminal-pair precedence, progress thresholds, minimum wait,
prearmed-control checks, click arbitration, and physical runtime evidence.
No early return on progress is introduced. Actual live fault-window recovery
remains unverified.

## 2. Second transfer: stale session fallback state masks reactivated Tuna

This is a real selected-route mismatch, not merely absent telemetry.

Original retained evidence:

- First-transfer reactivation publishes `file_tuna_v4` at live epoch 2 on both
  directions; for example retained line 27246 is the inbound authorized
  publication.
- Second-transfer retained slice lines 1285 and 1311 select
  `post_tuna_fallback_v6` / protocol 6 on outbound and inbound. Both report
  `selection_reason=post_tuna_file_fallback_active`,
  `file_tuna_active=0`, `post_tuna_fallback_active=1`.
- The second 16 MiB file completes on that fallback route (slice lines 7004 and
  7041) and independently matches its source hash. This does not satisfy the
  required Tuna-v4 route.
- Full GUI ledger lines 57481 and 57560 retain fallback epoch 1, created by the
  original local/remote header switch-off, until disposal. It was not retired
  when the first transfer reactivated.
- Original GUI failure:
  `file_tuna_v4_publication_missing_after_transfer`. Retained finalization
  failure: `snapshot_authoritative_route_consistency_invalid`.

### Code-level cause and authority boundary

`IsFileTransferUsingRegularNknFallbackForCurrentSession` in
`NknSignalingTransport.Acceleration.cs` considers a session-matching
`tunaFallbackProofState` with any non-None file state active. That includes
Recovered. `GetPublishedAccelerationLanesUnsafe` also suppresses the file lane
while that marker is present. New-transfer input in
`SessionFileTransferService.CreateInitialRouteResolverInputForNewTransfer`
preserves the fallback flag, and `ResolveInitialOutboundRouteSelection`
does not choose Tuna while that base fallback flag is true.

The coordinator-authorized `TrackAuthorizedFileTransferRouteHint` path updates
per-transfer route publication state, but does not retire that session fallback
marker. `SupersedePostTunaFileFallbackRouteForFileTunaActivation` currently has
no callers. Its session-wide `CompleteTunaFallbackProof` operation is not safe
to reconnect indiscriminately: it can clear other lanes or newer fallback
state, and it does not itself establish an exact coordinator-owned retirement
scope.

The missing effect is retirement/supersession of the old **file fallback
projection** at a current authorized reactivation boundary. Route hints,
listener readiness, adapter dictionaries, and a successful earlier transfer
must not independently authorize this effect or the next transfer's route.

### Bounded implementation plan (not yet implemented)

1. Trace the existing coordinator transition/publication DTO path and use its
   exact current transition owner for file-fallback retirement. Reuse an
   existing scoped command if one already carries the required predecessor
   boundary; otherwise add a narrowly scoped coordinator-issued result.
2. Bind session, transfer, direction, predecessor fallback leg/epoch and
   generation, successor Tuna-v4 publication/commit generation, and current
   terminal generation. Reject stale, mismatched, missing-authority and
   superseded callbacks with no effects. Never clear a newer user switch-off.
3. Retire only the matching file projection. Preserve unrelated screen fallback
   and pending fallback/control-plane owners. Retiring this marker must not
   automatically mark Tuna active or grant the next transfer's route.
4. Verify fresh new-transfer admission through the existing coordinator and
   readiness path after retirement. If this exposes a separate missing
   readiness/lease edge, report that next edge instead of adding a heuristic.
5. Reproduce with focused Core/Infra tests: successful reactivation followed by
   a new transfer, stale/rejected/mismatched publications, a newer fallback
   epoch, terminal races, opposite direction/session, duplicate callbacks, and
   mixed-lane preservation.

Do not simply prefer Tuna over fallback in the resolver, clear all session
fallback state after a terminal, call the unused session-wide cleanup from an
observation, refresh readiness from a route cache, or relax the second-transfer
analyzer expectation.

## 3. Manifest failures are downstream evidence consequences

The campaign has six attempts but four selected-finalized entries. The helper
and second-transfer attempts are unfinalized/unselected.
`FileTransferRouteAcceptanceEvidence.ps1` requires exactly one selected
attempt per declared scenario and matching selected identities; therefore
`scenario_selected_attempt_count_invalid` and
`selected_scenario_attempt_identity_count_invalid` follow directly.
Its unfinalized branch supplies `scenario_artifact_not_finalized` writer/cursor
reasons. The zero recorded writer-error counters do not override these gates.

No evidence currently justifies weakening manifest selection, synthesizing
missing finalization, rerunning the old analyzer, or treating this as a separate
log-corruption fix.

## Verification and next gate

Offline artifacts: `artifacts/full-matrix-20260923-rca/tests` and
`terminal-prefilter-evidence.json`. Test fixtures cover both PowerShell hosts,
both progress-gate variants, valid terminal forms, whitespace/case, duplicate
event keys, names in values, suffixes, failed/error terminals, and irrelevant
generation fields. Directly affected existing progress/terminal/fault-click
tests are included in the focused run.

Final run: `terminal-prefilter-green.trx`, 59 passed, 0 failed, 0 skipped,
6 minutes 14 seconds. The production PowerShell file is loaded from the current
working tree by the already rebuilt test assembly. `git diff --check` passes.
All 233 original full-matrix artifact pins were verified unchanged. No live
rerun, new commit, or push was performed.

An initial new-test fixture missed initialization of its visit counter; that
fixture issue was corrected before the semantic regression was reproduced.
A sandboxed rebuild then encountered Avalonia's external telemetry log
permission; passing an empty `UsedAvaloniaProducts` build property skips only
the inspected telemetry target. Neither issue is runtime failure evidence.

Next live gate requires a fresh portable build and strict preflights. Validate
only helper switch-off after this harness change, and second-transfer
reactivation after its separately scoped runtime correction. The full matrix
and all original verdicts remain unchanged; performance floors are unchanged.
