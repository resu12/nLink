# Canonical tail: bounded pending-prefix readiness fix

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: **implemented; 62 focused checks pass**. No new native transfer ran.

## Change

The preceding native capture recorded the exact rejected state: `next=12023`,
`highest=12025`, three pending writes including `next`, and its written bit false.
See `file-transfer-canonical-pending-prefix-rca-20260929.md` for the unchanged
capture, source ordering, evidence limits and fix plan.

The test-only `ReadSparseShape` now returns **false**, never success, when all of
the following hold in the existing locked context read:

- Canonical-tail profile and `0 <= next < 12476`.
- `next <= highest < 12484`.
- Written-bit array exists with the canonical length 12484, and `written[next]`
  is false.
- The actual pending-write set contains that exact `next` index.

This uses existing context state, not diagnostic text. It does not require a
particular pending count or hard-code the observed frontier. It does not infer
that other missing chunks will arrive. Removing the current chunk from pending,
without committing it, restores the original rejection even if another chunk is
still pending. Missing/invalid evidence falls through to the unchanged predicate;
read failures cannot manufacture readiness or replace its original rejection.

`MatchesSparseShape` is unchanged: settled-prefix waiting and exact-tail success
at `12476 / 12483` retain their contracts. Small profiles, advanced/invalid
frontiers and inconsistent state cannot use the new case. Existing geometry,
currentness and unbound-observation checks still run before this reader.

The helper change is a 14-line insertion. All surrounding code, diagnostic
capture/output, wait loops, cancellation and cleanup are unchanged. No observation
is reset, no frame is released, no repair is dispatched and no authority evaluator
is called. All runtime/coordinator authority and acceptance rules are unchanged.

## Verification

Artifacts: `artifacts/canonical-pending-prefix-fix-20260929/`.

- Red: seven new pending-prefix cases reproduced the prior exception; 36 other
  diagnostic/boundary controls passed.
- Green: all 43 focused checks pass. The captured state is covered, along with
  prefix start/end boundaries, highest at the tail, differing pending counts,
  no output or state mutation on waiting, and restored rejection after removing
  the pending frontier. Negative cases cover unrelated pending chunks, a written
  frontier, missing/empty/wrong-length bit arrays, invalid indices, an advanced
  frontier, the small profile and unavailable pending evidence.
- Direct regression, once: all 19 existing exact-tail, settled-prefix, rejection
  and observation-preservation/guard checks pass.
- The original diagnostic snapshot/output test now uses the still-invalid advanced
  frontier `12477 / 12479`, preserving every assertion instead of weakening it.
  Single emission, bounded output, unknown written state and original exception
  preservation remain covered.

Final unique result: **62 passed, zero failed**. The intentional red failures are
retained. Unchanged timing, composition, analyzer and aggregate lanes were not
rerun. These are deterministic tests, not proof of a successful native transfer.

## Preservation and next gate

Entry matched 4,374 prior hashes and 558 dirty entries. Before-images preserve
both edited test files and the five pinned build binaries. Production sources and
the four pinned Core/Infra DLL copies remain unchanged; only the two test files
and rebuilt test DLL changed among prior pins. Prior captures, consumed runners
and accumulated work are preserved by the final audit. Only this report is a new
non-ignored file in this step.

Next: rebuild and freeze fresh inputs, run strict escalated preflights, and gate
one new offline canonical-tail attempt with a fresh create-new marker. Do not reuse
the earlier consumed runners. Keep the 600-second outer, 180-second carrier,
eight-second edge, four-second held-frame and 20,000 ms native-primary work limits.
Waiting on pending work does not guarantee that the prefix finishes within them.
Stop at the first remaining failure; do not retry automatically or increase limits.

No full-size/native transfer, live-256 run, GUI matrix, commit or push occurred.
Live validation and full-file/native same-pair acceptance remain pending.
