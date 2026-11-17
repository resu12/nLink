# Recent-progress grace deadline race

## Current evidence and root cause

Baseline: `a52f46331bb815306ebe2b1d522cd6179519b45e` on `v0.7.0`.
Latest live campaign: `artifacts/bootstrap-peer-admission-live-20260924-a52f4633/live/20260924T121030Z/second-transfer-after-reactivation`.
Its primary-selected failure is
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`, reason
`post_tuna_fallback_frontier_recent_progress_grace_expired_without_exact_stall_recheck`.
The primary reports 107 raw observations, 9 distinct episodes, 8 recovered, and
1 unresolved. Snapshot evidence, stream completeness, parity, and parent route
consistency passed; this does not discharge the missing runtime proof.

The exact selected witness is retained source line 24392, receiver writer
`p21860re2cd7f6c506447c7`, sequence 13200. Bounded context in
`artifacts/recent-grace-deadline-20260924/RCA-EVIDENCE.json` records:

| Receiver sequence | Observation (UTC on 2026-09-24) |
| --- | --- |
| 13178 | Wake 202: frontier 3358, highest 5403; committed progress 12:12:39.2343421, due 12:12:40.2343421. |
| 13213 / 13224 | Highest advances to 5404 / 5405; replacement wakes 203 / 204 retain the same deadline. |
| 13298 | Sparse write at 12:12:40.2362717 commits 3 chunks but advances contiguous progress by 0; frontier/highest remain 3358/5405. |
| 13300 | Wake 204 fires at 12:12:40.2778857 and is rejected as `wake_generation_replaced`. |
| 13312 | Ordinary `chunk_batch_committed` path arms stall wake 221 at 12:12:40.3439693, due 12:12:41.0939693. This is not the required recent-progress recheck. |
| 13420 / 13522 | Later Tuna publication precedes rejection of stall wake 221 as `source_not_current`. |

The timer fired. Code inspection and a deterministic regression identify the
race: `ArmOrClearInboundV6FrontierRecentProgressGraceWakeLocked` cleared an
expired wake **before** checking whether its complete source/progress scope was
unchanged. A concurrent sparse-write/state-send refresh after the deadline could
erase the still-current scheduled callback's identity before it acquired the
gate. Later successful transfer completion or route publication is not proof of
that missing recheck.

## Bounded implementation

Move the existing expiry reset after the existing complete unchanged-wake
comparison. Retain the same wake, generation, deadline, and scheduled generation
only when every compared scope field still matches. A missing or changed wake
cannot be newly armed after expiry. The callback still checks current transfer,
pause/terminal state, route/owner/publication, leg, checkpoint, and data session
before entering the existing coordinator-owned repair path.

No grace duration, throughput policy, retry cadence, queue behavior, route
authority, GUI behavior, or second-transfer startup behavior is changed.

Fresh affected consumer tests also exposed an independent catalog compatibility
gap: supersession selection accepted only older catalog versions and rejected
the current `initial-data-session-v1` profile as `authoritative_profile_required`.
That catalog inherits the prior repair/retry/handoff contracts and adds only a
non-route-authoritative initial data-session binding. Explicitly add this exact
known version to both supersession profile lists and the exact repair-owner
diagnostic branch. Keep catalog-hash, authoritative mode/families, stream,
deadline, peer, commit, disposition, publication, and repair checks unchanged.
Unknown versions remain rejected. This corrects compatibility; it does not turn
an arbitrary valid snapshot into supersession proof.

## Verification and review

Artifacts: `artifacts/recent-grace-deadline-20260924`.

- Runtime red: 4 new cases fail on the unchanged implementation; the 2 existing
  cases pass. Exact-deadline and one-tick-after-deadline refreshes erase the wake.
- Runtime green: all 6 cases pass, including value-equivalent coordinator state
  adoption. The boundary is exercised through the helper's clock input while the
  real scheduled callback is pending; the real callback, blocked receiver-state
  send, exact successor request, and repair-progress assertions then continue.
- Rejection controls cover changed frontier/highest/progress, owner/publication,
  terminal/data-session generation, absent data session, and absent wake after
  expiry. Existing callback scope/ABA/pause/terminal checks are retained.
- Directly affected runtime lane: 8 passed. Fresh joined recent-progress and
  stalled-repair supersession fixtures: 1 passed each. Final unique xUnit total:
  **16 passed, 0 failed, 0 skipped**.
- Actual checked-in consumer scripts ran against those fresh joined logs on
  Windows PowerShell 5.1 and PowerShell 7. Recent-progress: 58 controls per host;
  stalled repair: 29 named controls per host. Both pass, including full/compact
  consumption and strict invalid-profile/peer/authority rejection controls.
  These were direct consumer runs, not reruns of wrappers that would load the
  workspace's older default binaries. Intermediate failed attempts are retained.
- Review found no blocking issue. `git diff --check` passes. All 142 previous
  live artifact pins are unchanged. No historical primary evaluation, backfill,
  or reclassification was performed; fresh fake-peer evaluations are separate.

The previous live result remains **FAIL**. These offline checks do not prove the
second transfer selects `file_tuna_v4`, nor positively validate its success report.
No portable rebuild, live run, full matrix, commit, or push is part of this fix's
offline verification.

## Next validation gate

Checkpoint the reviewed changes, rebuild the portable package, then run only
`second-transfer-after-reactivation` with strict elevated preflights and no
implicit retries. Inspect the fresh primary result and stop at its first missing
proof edge if it fails. Keep the full GUI matrix gated on targeted correctness;
do not use hashes or fallback completion to substitute for required Tuna-v4
publications.
