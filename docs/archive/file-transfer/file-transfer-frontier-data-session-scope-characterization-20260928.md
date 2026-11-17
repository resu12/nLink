# Native data-session scope: original-process characterization

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

Status: **the one rebuilt small offline characterization passed**. The original
optional resolver returned 41 exact source-line witnesses and committed the
diagnostic output. The preceding `native_frontier_data_session` omission is
cleared in this original-process path. No new diagnostic blocker was observed.
This is not route acceptance or a live-256 result.

## Original evidence

Handoff evidence:
`artifacts/frontier-data-session-scope-characterization-20260928/`.
Original run:
`artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-33ffeb5cb1794f5580227e07ac43a574/`.

- Exact test: `OriginalApps_CarryLateFrontierReceiptIntoPrimary`; one passed,
  zero failed/skipped; duration 65.7254372 seconds. No retry occurred.
- Transfer: 172,032 bytes over the small named-pipe offline carrier. Both peers
  and the independent destination check match SHA-256
  `67331c29dda482392a6413e0b35c3a7fa7692b5db1bfd1496da5575752f49a0b`.
- The unchanged `late-publication-fixed-4s-v1` profile closed within its bound
  with three physical copies and the original root unchanged. Original outcome:
  `PublishedWithinObservationBound`.
- The original primary performed three evaluations and one native read, with
  1,244 semantic lines and physical origins. The optional resolver ran once,
  with an empty omission reason and 41 witnesses, inside the unchanged 1-64 cap.
- The native receipt is 6,537 bytes, observation
  `62f2a0eb4d69401f8bbe547b7b12f068`, writer `p42548r9d314cb3d0524320`.
  Completion and marker physical lines are 1427 and 1648.
- The original diagnostic output committed successfully. Its saved packet is
  118,971 bytes, SHA-256
  `b0e416ea8aaddc2976d1081341ba7969845521b44626be5dbcfb951d4ec7ff43`.
  File decoding for this integrity check did not recreate native custody or
  invoke an evaluator/resolver.
- Primary PID 55512 exited 0 without timeout. Sender 32116 and receiver 42548
  exited. Independent escalated cleanup found no matching test processes; all
  51 run files opened exclusively for reading. Zero native pins remain open;
  retained payload/witness text was cleared. Nothing was killed or deleted.

## What the pass does and does not mean

The successful original probe checks receipt identity, clock/lifecycle ordering,
the original full/compact views and selected failure, witness ownership, exact
line hashes and physical origins, diagnostic output adoption, and cleanup.
All ten original report hashes remain identical before/after optional resolution
and match the saved files. No second analysis or post-run witness replay ran.

The original selected failure remains
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`, semantic
line 976 / physical line 1281. Primary verdict remains
`FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`. This deliberately late profile requires
that unresolved failure to exercise the diagnostic branch; passing the harness
does not recover it, change the acceptance rule, or establish a production fix.
All three diagnostic authority flags remain false.

The fixture parent creates a canonical 256 MiB source and retains GUI/live-cycle
scenario labels. That file is not the source of this 168 KiB transfer, and those
labels do not establish same-pair 256 MiB or live NKN/GUI proof. Earlier failed
characterizations remain failed and unchanged.

## Preflight and preservation

Entry and both strict escalated preflights matched all 3,537 prior pins and all
531 dirty entries. Process-only environment isolation, Debug override policy,
high-resolution clock, disk space and overlapping-process checks passed.
A create-new marker guarded the single attempt. The saved 63 passing focused
cases were checked, not rerun.

The normal-sandbox Debug rebuild succeeded with zero warnings/errors, using
command-local `-p:UsedAvaloniaProducts=` for optional build telemetry. Five
pinned binary before-images were retained; their hashes remained unchanged.
This was not a portable GUI build.

No production/test-source or acceptance change, live NKN, live-256, full GUI
matrix, commit or push occurred. All prior pins and dirty contents/statuses are
unchanged. Only this handoff adds a non-ignored file (532 dirty entries).
The final seal records preservation and diagnostic success, not route acceptance.

## Next bounded step

Close the diagnostic data-session milestone. No additional retry of this small
profile or speculative runtime change is warranted by this passing result.
Review readiness for the next targeted live-256 gate against the existing
same-pair custody and coordinator-published route-cycle requirements; identify
any remaining prerequisite explicitly. Do not carry the small late-profile
receipt forward as live proof or extend its test-only schedule to the full-size
profile. A live run still needs its own portable rebuild, escalated preflight
and scoped authorization; the full GUI matrix is not implied by this result.
