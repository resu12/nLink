# Canonical-tail publication shape: approved bounded implementation

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Status: the publication-shape blocker is resolved; same-pair primary/handoff
integration is still required before a full-size offline run.

## Approval and change

The user's `yes` approved the previous checkpoint's specific request to extend
the test-only publication shape guard. The controller now forwards its explicit
canonical-tail flag through `AttemptLatePublicationOwner` to
`AttemptOfflinePublicationDelay`. Both constructor flags default to false;
canonical admission uses the existing Debug/Windows/unsafe-override guard.

Without the flag, the original exact 4/7 geometry remains unchanged. With it,
only exact frontier 12,480, highest 12,483 and 268,369,920 committed bytes are
admitted. Existing nonterminal/unpaused, capacity, source/identity, route,
native-write, copy-count, original timeout/expiry and cleanup checks remain.
No arbitrary offsets or inference from frame contents were added.

The earlier regression now reaches native-frame checking and then correctly
rejects the three missing physical writes. A zero pending count is still not
completion. The first missing edge was the small-only helper constructor, not
runtime authority, networking or performance.

## Verification

Artifacts: `artifacts/canonical-tail-publication-shape-implementation-20260928/`.

- Initial focused run: 28 passed, 11 failed, including a pass for the preserved
  regression that failed at the preceding checkpoint. The 11 failures exposed
  two incorrect assertions in the newly written tests: a rejected native write
  makes verification throw `late_publication_no_native_write` before copy
  accounting; a retired owner rejects even a `CopiesFinished` read.
- Corrected only those assertions, retaining the initial source/TRX. The narrow
  rerun passed all 11 affected cases. No helper behavior was changed for these
  test corrections.
- Existing directly affected helper regression lane: 63 passed once (publication
  delay tests excluding unchanged pipe-writer-only tests, plus publication-owner
  tests). No aggregate Core, analyzer, acceptance or GUI suites were rerun.
- Unique final outcome across the selected cases: **102 passed, zero failed**.
  No warnings were emitted for the new test file or the four edited files.

Coverage includes explicit/default-off profile separation, wrong tail geometry
and byte count, state/metadata bounds, original timing rejection, foreign and
competing ownership, native-write identity/frame rejection, cancellation and
retirement, and all three original physical-write callbacks. In-memory callback
tests and synthetic timestamp fixtures are helper tests only: they do not prove
native custody, full-size transfer completion or same-pair witness ownership.
The existing preserved regression still checks the original S+250/S+800 window.

Tests ran in the normal sandbox, Debug, without restore, with optional Avalonia
telemetry disabled and process-local logging redirected to this checkpoint.
Before-images preserve all four edited files and five pinned build binaries.
The final verification seal records unchanged prior pins and dirty entries,
the specifically changed hashes, and the new evidence.

## Next step and limits

Continue with actual-pair GUI/wrapper/parent handoffs and the combined original
primary witness worker. Retain original evaluation/custody identity, budgets,
failure ownership, verdicts, output ownership and cleanup. The existing
pre-launch canonical dispatch block remains unchanged until that integration
is complete and its focused tests pass. Then separately gate the one-shot
full-size offline validation.

No production source, acceptance rule, publication timing, queue/concurrency,
chunk size, retry policy or throughput policy was changed. No full-size
transfer, live-256 test, GUI matrix, commit or push occurred.
