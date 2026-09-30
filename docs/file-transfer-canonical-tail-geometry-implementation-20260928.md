# Canonical tail geometry and localized arming: implementation checkpoint

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.

## Result and scope

Implemented the deterministic geometry/read-arming portion of the approved
`file-transfer-canonical-same-pair-witness-contract-20260928.md` contract.
**92 focused tests passed**: 52 new cases and 40 existing scheduler/hold-budget
regressions. This is a partial implementation checkpoint, not completion of the
canonical same-pair integration or evidence of a full-size transfer.

The new `AttemptCanonicalTailProfile` is Debug/Windows-only and requires the
fixed 268,435,456-byte, 21,504-byte-chunk, 12,484-chunk geometry. It retains the
initial process batching setting (absent or exactly `3`), rejects another owner,
and arms the existing single-segment override once, before an actual source
read at or after byte 268,219,392. It restores the exact prior setting on disposal;
changed settings and retired owners cannot resume the profile.

The existing retained-read probe accepts this optional hook only for its fourth
reader. Metadata/earlier reader behavior, native admission, four-open limit,
read results and seek forwarding remain unchanged. The validated reader's
`Position` getter still performs its original currentness check before the hook,
and its actual read still checks currentness before and after I/O.

The descriptor/scheduler extension keeps the small geometry as the default.
Canonical prefix frames consume no descriptor slots. The fixed tail requires
eight unique single-chunk original descriptors at 12,476..12,483 and an armed
profile; crossing batches, cache-bypass without arming, changed identity/epoch,
duplicates and hash replays are rejected. Original frame indices/bytes remain
unchanged. The relative release order `[2,1,4]`, eight descriptor slots, three
held frames, one-MiB held-data/descriptor budget and four-second original hold
expiry are unchanged. `AnyOrdinarySeen` exposes a read-only first-frame condition
for the upcoming overall-deadline wait; no new wait or deadline is installed yet.

## Verification

All tests ran in the normal sandbox, Debug, with `--no-restore` and
`-p:UsedAvaloniaProducts=`. Test log environment changes were process-local and
pointed into this checkpoint's ignored artifact directory.

- New geometry/read/descriptor/scheduler cases: 48 passed in the initial run.
- Additional EOF/retired-reader, one-transition/finally cleanup and retired
  descriptor-owner cases: 4 passed. An initial compile-only failure from xUnit's
  ambiguous `Assert.Throws` overload was fixed with an explicit `Action`; the
  failed build and successful rerun logs are both retained.
- Existing `AttemptOfflineFrameScheduleTests` plus the directly affected
  `AttemptLatePublicationTests.HoldBudget_ReadAndReleaseDoNotRenewOriginalDeadline`:
  40 passed once, using the final build.
- No compiler warnings were reported for the new or modified source files in
  the saved final build log; unrelated existing project warnings remain.

Tests use tiny deterministic byte stores with full-size positions and synthetic
metadata. They do not claim native file custody, actual encrypted tail-frame
admission, coordinator repair progress, native timing, witness ownership or
full-input work-budget compliance. The old three-segment boundary tests model
the reviewed batch builder's limit-before-read ordering; they do not execute a
canonical transfer.

## Remaining approved implementation work

1. Connect the exact named composite profile through the offline carrier and
   dispatch only. Validate actual context geometry, keep the fourth reader and
   profile alive through cleanup, and wait for the first suffix frame under the
   already-running 180-second deadline. Translate the existing controller's
   original frontier/repair/write checks to the fixed suffix without replacing
   its original observation or changing publication timing/owner.
2. Compose the actual-pair handoff and original late-witness worker; implement
   the same-custody/pair/receiver-lineage assertion, negative controls, original
   call-count/report/cleanup checks and the new exact stop-before-acceptance
   sentinel. Keep all existing small/full-size exclusions and verdicts unchanged.
3. Review those focused results before the separately gated, one-shot full-size
   offline run. No full-size run, live-256 run, GUI matrix, commit or push occurred
   at this checkpoint. No production source, loader, acceptance rule or limit was
   changed. The new profile is not yet reachable from scenario dispatch.

Before-images, test results, logs and preservation verification are under
`artifacts/canonical-tail-geometry-implementation-20260928/`. Continue from this
checkpoint; do not rerun the successful small native capture or merge its
artifacts with the earlier canonical run.
