# Canonical prefix readiness: bounded test-only fix

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: **implemented; 25 focused checks pass**. No new native transfer ran.

## Change

The preceding offline run reached all eight physical tail frames while the
receiver was still committing the contiguous prefix. Its last saved commit was
`next=12002, highest=12001`; the old readiness predicate rejected every frontier
unequal to 12,476. See `file-transfer-canonical-tail-native-rca-20260929.md` for
the original failed run, evidence limits and cleanup results.

`AttemptOfflineLatePublicationProgress.MatchesSparseShape` now returns **false**
(not ready, never success) only when all of these hold:

- This is the canonical-tail profile.
- `0 <= next < 12476`.
- `highest == next - 1`, so the observed prefix is contiguous.

The existing exact-tail success remains `next=12476, highest=12483`. Prefix gaps,
inconsistent indices, advanced frontiers and invalid small-profile states still
reject. A wrong final highest still cannot start the episode.

The implementation is one three-line insertion, including its comment. The
entire rest of the helper compares equal to its before-image after line-ending
normalization: original read/observation guards, eight-second edge wait,
cancellation, no observation replacement/reset, and subsequent repair/publication
checks are unchanged. The 180-second carrier, four-second held-frame expiries,
600-second outer and native-primary work budgets are not extended. No frame is
released or authority granted by the new not-ready result.

## Verification

Artifacts: `artifacts/canonical-prefix-readiness-fix-20260929/`.

- Red: four new contiguous-prefix cases (0, 1, 12,002 and 12,475) reproduced the
  old exception. Ten exact-tail/rejection cases passed.
- Green after the fix: all 14 readiness cases passed. Negatives cover a prefix
  gap, inconsistent/negative indices, advanced frontier and small-profile scope;
  the existing exact-tail test now also checks a highest index beyond the end.
- Direct lifetime regression, once: 11 passed. These cover unbound observation
  preservation, closed/bound/repaired observation rejection, original cancellation
  and expired-token behavior, currentness failure, small-profile exclusion, and
  the original four-second hold deadline not being renewed by later frames.
- Final unique result: **25 passed, zero failed**. The intentional red failures
  remain in their original TRX. Existing unrelated compiler warnings were left
  unchanged. All four pinned Core/Infra DLL copies are unchanged; only the test
  DLL was rebuilt.

All 4,266 prior pins and 553 dirty entries matched at entry. Before-images retain
the two edited test-source files and five build binaries. Final seals preserve
the earlier failed native run and the other accumulated work. Production code,
acceptance, timing and authority rules are unchanged. No native/full-size run,
live-256 test, GUI matrix, commit or push occurred in this step.

## Next gate

Review/freeze fresh source and binary hashes, perform strict escalated preflights,
and separately gate one rebuilt offline canonical-tail run with a new create-new
marker. Do not reuse the consumed runner/marker from the failed characterization.
Keep all current limits; stop at the first remaining geometry, timing, ownership,
budget or cleanup failure. Passing these deterministic checks does not establish
that the full prefix drains within the existing holds, or that native same-pair
analysis succeeds. Live-256 and the full GUI matrix remain later gates.
