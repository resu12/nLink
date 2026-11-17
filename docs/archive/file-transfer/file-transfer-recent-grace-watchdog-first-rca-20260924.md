# Recent-progress recheck: watchdog-first stall ordering

## Status and evidence boundary

The initial review prepared the RCA and bounded fix plan only. The subsequently
approved offline analyzer implementation and verification are recorded below.
No runtime implementation, historical reclassification, live run, commit, or push.
HEAD is `b497f70144b0e3cb047766bd9aa5ae17c5d126e2` on `v0.7.0`.
The previous uncommitted stale-responder analyzer fix is unchanged.

Current live evidence is the sealed full GUI campaign
`artifacts/full-gui-matrix-20260924-b497f701/live/20260924T144758Z`, scenario
`regular-v4-live-activation-off-on-off-256mb`. Its primary report records
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`:
318 raw events, 67 episodes, 66 recovered, zero superseded, one unresolved.
The original campaign remains **FAIL**; successful payload hashing and the
complete five-route cycle do not override this result.

The report saves only the campaign's first failure coordinate, which belongs to
the separate stale-responder finding. It does not save the unresolved
recent-progress episode's own coordinate. The frontier-3133 records below are
therefore **diagnostic selections from literal log inspection**, not claimed
primary-selected witnesses. They establish a concrete code/evidence mismatch
consistent with the remaining finding; they do not regenerate its verdict or
prove that a future run will have no additional blocker.

`artifacts/recent-grace-recheck-rca-20260924/Capture-Evidence.ps1` copied 44 exact
source lines and two already-evaluated coordinator boundaries. It verified all
473 matrix pins, all 89 previous-fix pins, and both raw snapshot payload hashes.
It calls no analyzer, reducer, classifier, or authority evaluator.
`RCA-EVIDENCE.json` SHA256:
`bfd4518871596f25532c472718d584dcca56ebbaa317acede3bd5af896cfb0e5`.
The capture refuses to overwrite existing evidence.

## Observed sequence

All times below are UTC on 2026-09-24. Physical lines refer to the scenario's
`filetransfer-retained-log-slice.log`; receiver writer is
`p7804rac622187559a41bc`.

| Physical line / writer sequence | Time | Observed event |
| --- | --- | --- |
| 33146 / 20103 | 15:04:10.1855691 | Coordinator-issued receiver repair for frontier 3117, generation 68. |
| 33249 / 20152 | 15:04:10.5965401 | Repair progress advances the contiguous frontier from 3117 to 3133. |
| 33250 / 20153 | 15:04:10.5969782 | Recent-progress wake 161 holds frontier 3133, highest 5164; deadline 15:04:11.5959082. |
| 33252 / 20157; 33262 / 20168 | Before the first deferral | Accepted forced receiver state, then published epoch 179 at frontier 3133. |
| 33311 / 20180; 33313 / 20183 | 15:04:11.1135049; .1148654 | Wake 162 rearms for highest 5167; first receiver-state deferral. |
| 33327–33352 / 20193–20223 | 15:04:11.2254196–.3333672 | Same-frontier rearms 163–167 advance highest to 5180 without changing the recent deadline. |
| 33373 / 20241 | 15:04:11.6275939 | Ordinary watchdog has armed stall wake 193, reason `frontier_stall_repair_due`; start .6273274, deadline 15:04:12.3773274. |
| 33379 / 20247 | 15:04:11.6302389 | Recent wake 167 reports `rechecked / stall_grace_armed`, not stale. |
| 33423 / 20268 | 15:04:11.9131462 | Sparse write commits no contiguous chunks; frontier/highest remain 3133/5180. |
| 33449 / 20295 | 15:04:12.2519053 | Sparse write commits five contiguous chunks: frontier 3138, highest 5180. |
| 33460 / 20308 | 15:04:12.3470883 | Receiver publishes epoch 181, frontier 3138/highest 5180, **30.2391 ms before the stall deadline**. |
| 33468 / 20316 | 15:04:12.3826237 | Old stall wake 193 is stale/replaced after the advance; diagnostic-only. |

The repair is `fta2-repair_replay-dc7d767c85814b36d7b9e1aa`, request
`v6-frontier:3117:69`, correlation `frq-b74c4b2c472591ac875691eb`.
Its grant, delivery, dispatch, request send, `ProgressObserved` settlement,
forced state, and initial state publication are retained in the extraction.

The interval uses fallback-v6, live epoch 4, runtime/publication/leg generation
5, transport/bridge/checkpoint generation 1, terminal/commit generation 0,
and data-session generation 1. The existing primary result passes both the
source-publication boundary `fte1-feb1740dcebf0003302a5991` and repair-owner
boundary `fte1-000f0ea937c5d8829e22e9de`; this review copied their results.

## Cause established by code inspection

`Get-FileTransferRecentProgressGraceStallProgressClosure` in
`tools/FileTransferFrontierGraceHandoff.ps1` requires the stall's reason to be
exactly `frontier_recent_progress_grace_elapsed` (line 1507 at this HEAD).
The observed same-frontier stall instead carries `frontier_stall_repair_due`.
That predicate excludes it even though the recent callback actually rechecked
and forward receiver state was published before the stall deadline.

This ordering is supported by the service:

- `MaybeSendInboundSparseCreditFrontierStallStateWithWriteBindingAsync` in
  `SessionFileTransferService.PullTransferSessionV4.cs` calls the same v6
  frontier-request path with `frontier_stall_repair_due`.
- `ShouldSendInboundV6FrontierRequestLocked` in the v6 partial creates a stall
  only when it is absent or belongs to a different frontier. A subsequent
  caller preserves the existing start/deadline instead of relabeling it.
- The recent-progress callback checks for the current same-frontier stall and
  reports `stall_grace_armed`; it does not require that callback to have been
  the stall's original creator.

Thus the analyzer conflates **who first armed the stall** with **whether the
recent-progress recheck found the exact current stall**. This is an analyzer
closure gap, not evidence that the recent callback was lost. No runtime timer,
deadline, retry, queue, or throughput adjustment is justified by this evidence.

The early-natural-progress branch cannot close this sequence: the advance is
after the one-second recent-progress deadline. No new frontier-3133 repair is
needed or observed before the natural advance, so a dispatched-successor-repair
closure is not the relevant proof either. The existing bounded
stall/progress/publication closure is the path to extend.

Other leads were excluded from the proposed scope: frontier 174 has a stale
source callback but no matching receiver-state deferral; frontiers 2737 and
5023 have ordinary stall and subsequent dispatched-repair progress records.
These observations are not newly assigned incident dispositions.

## Bounded implementation plan

1. Add a synthetic watchdog-first regression to the existing recent-grace
   ordering tests. Preserve the separate deadline-late fixtures as failures;
   do not relabel a historical capture or use it as another primary evaluation.
2. Extend only the shared stall/progress closure with an explicit
   `frontier_stall_repair_due` ordering branch. Do not remove the reason check
   or accept arbitrary reasons. Keep the existing recent-origin behavior.
3. Require, for the new branch, one unambiguous same-owner/data-session/frontier
   stall root ordered before the successful recent recheck; the exact recent
   root/rearm chain; stall start at or after the recent deadline; a current
   recheck before the stall deadline; matching highest frontier; and the
   existing 750 ms duration. Reject replacement/ambiguous roots, conflicting
   writer copies, scope changes, pause/terminal events, and stale-only rechecks.
4. Keep the complete, monotonic sparse-write chain and exact unpaused receiver
   publication strictly before the original stall deadline. No grace extension,
   timestamp rounding, missing-publication substitution, or inference from
   final SHA/completion. Timer/transport observations remain evidence only;
   coordinator-issued source and repair authority checks stay unchanged.
5. Verify the new positive ordering and negative mutations in flat/indexed,
   full/compact, and actual GUI-classifier entry paths on PowerShell 5.1 and 7.
   Include missing/wrong/stale recheck, unknown origin, root after recheck,
   multiple roots, wrong source/wake/data session, changed deadline,
   missing/regressed write, missing state, and publication at/after deadline.
   Run directly affected recent-grace tests first, then the relevant contract
   lane once if shared behavior changed. No repeated aggregate suites.

Do not extend the Tuna-handoff closure or other reason families as part of this
fix. If the synthetic regression exposes another first missing edge, document
it before broadening implementation. The shared helper already serves retained
and GUI analysis; change indexing only if a focused parity test proves that
the required interval is missing.

## Implemented bounded fix and verification

The shared `Get-FileTransferRecentProgressGraceStallProgressClosure` now accepts
the explicit watchdog origin only with the original full wake/source checks,
stall-before-recheck sequence and clock ordering, equal highest frontier, and
recheck before the unchanged stall deadline. The complete interval rejects
competing/replacement stall roots even if their reason or scope would otherwise
exclude them from candidate selection. Exact duplicate source records remain
deduplicated by the existing writer-identity check.

The sparse-write chain, receiver-state publication, strict deadline, and
pause/terminal/source-change checks are unchanged. No index change, Tuna-handoff
extension, coordinator authority change, runtime timer change, or tuning was
needed. Implementation is 20 changed lines in the shared analyzer helper, plus
focused test additions. The previous five stale-responder worktree files are
unchanged.

Verification artifacts: `artifacts/recent-grace-watchdog-fix-20260924`.

| Lane | Result | Coverage |
| --- | --- | --- |
| `red.trx` | 2 expected failures | Before the fix, both PowerShell hosts rejected `watchdog_exact` as 1 raw / 1 distinct / 0 recovered / 1 unresolved. |
| `green.trx` | 2 passed | 50 new watchdog-first controls per host, PowerShell 5.1 and 7. |
| `affected.trx` | 10 passed | Existing ordering (69 controls per host), coalesced-write contract, retained full/compact analysis, and GUI indexed fast path. |

The 50 new controls cover exact/rearmed/duplicate observations, a zero-advance
write, publication 30 ms before deadline, and rejection at/after deadline;
unknown origin, missing/reordered/ambiguous/replaced/conflicting root;
missing/stale/wrong-wake recheck; missing/regressed writes; absent/mismatched
publication; scope mutations, pause, terminal, and source replacement.
Each runs through flat/indexed reduction, full/compact serialization, the actual
GUI recent-progress consumer, and single/multiple-frontier partition checks.
Fixtures use fresh service-issued source/repair receipts and synthetic ordering,
not historical-capture reevaluation.

All 12 final xUnit cases passed on the normal testhost stack, with zero skipped
or aborted cases. The earlier stale-responder lane's separate default-Debug
stack-overflow limitation is unchanged; this result does not relabel that gate.
No aggregate suite or live GUI run was repeated. `git diff --check` passed.
`Verify-Results.ps1` seals the results and compares all 473 matrix pins: 472
are unchanged; the one expected difference is the active analyzer helper edited
by this fix, not a captured artifact. All 89 previous-fix pins are unchanged.
The verification script refuses to overwrite an existing seal.

## Next gate

Bounded offline implementation and focused verification are complete. Review
and checkpoint the accumulated analyzer changes, rebuild the portable package,
then use strict escalated preflights before a fresh targeted 256 MiB scenario.
Do not run the full matrix until that targeted correctness gate passes or
identifies a new first edge. Changes remain uncommitted and unpushed.
No claim of live acceptance is made here; the historical matrix remains FAIL.
