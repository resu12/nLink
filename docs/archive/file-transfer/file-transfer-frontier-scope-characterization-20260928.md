# Frontier scope characterization and data-session first-edge RCA

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result

The single rebuilt small offline characterization **failed**, but advanced past
the previous `native_frontier_receipt_ambiguity` rejection. The original resolver
now reports **`native_frontier_data_session`**. No retry or runtime/test-source
change was made; acceptance remains unchanged.

The 172,032-byte destination completed with matching SHA-256. That demonstrates
transfer integrity in this offline fixture, not the GUI/live route-cycle contract.
No live NKN, live-256, full matrix, commit or push occurred.

Evidence:
`artifacts/frontier-scope-characterization-20260928/`.
Original run:
`artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-e315d617c7884323b805239e5b8987e4/`.

## Verification

- Entry and strict escalated preflights matched all 3,063 prior pins and all 527
  dirty entries. Debug build succeeded with zero warnings/errors; pinned binary
  hashes did not change. The existing 58 passing focused cases were checked from
  their saved results, not rerun.
- Process-only environment isolation, Debug overrides, high-resolution clock,
  disk space and no overlapping processes passed. A create-new attempt marker
  guarded the one selected test, `OriginalApps_CarryLateFrontierReceiptIntoPrimary`.
- This was a 168 KiB named-pipe offline carrier. Its original parent still creates
  the canonical 256 MiB source and retains GUI/live scenario labels; those labels
  do not establish a live run or route acceptance.
- Sender PID 50016 and receiver PID 44644 completed with SHA-256
  `67331c29dda482392a6413e0b35c3a7fa7692b5db1bfd1496da5575752f49a0b`.
- The unchanged `late-publication-fixed-4s-v1` profile closed within its observation
  bound with three physical copies and the original root unchanged. Wrapper PID
  41656 exited 0; analyzer PID 49344 exited 1. Neither timed out.
- The original analyzer performed three evaluations and one native read. Its
  optional resolver ran once, returned zero witnesses, and committed no optional
  diagnostic output. Primary verdict remains `FAIL_PROTOCOL_OR_INTEGRITY`, gate
  `fail`; all three diagnostic authority flags remain false.
- All ten report hashes are identical before/after the resolver and match the
  saved files. All four processes exited, all 50 run files opened exclusively for
  reading, zero native pins remained open, and retained text was cleared.

## First-edge RCA

The selected failure is semantic line 985 / physical line 1292, writer
`p44644r5df2431281a9412c`. Its raw session/transfer fields remain `[redacted]`.
Its new diagnostic session and transfer tokens exactly match the original
captured receipt, as does its writer. The native, retained and physical source
texts match. This confirms the previous identity association is repaired in
the original process path, without exposing raw IDs or relaxing matching.

The new reason originates at
`tools/FileTransferOps/LocalNativeFrontierWitness.ps1:88`: the resolver hashes
the logged raw `data_session_identity` and compares it with the receipt's
`Source.DataSessionIdentity`.

The first relevant recent-grace arm is semantic line 947 / physical line 1244,
frontier 1, wake 1, data-session generation 1. Its original log contains
`data_session_identity=[redacted]:1`. The later frontier-4 arm has the same raw
redaction form. The scope check still hashes that redacted text.

The original producer instead hashes the typed `wake.DataSessionIdentity`
(`SessionFileTransferService.FrontierPublicationPayload.cs:88`); its paired log
emitter formats that same typed field before normal redaction
(`SessionFileTransferService.PullTransferSessionV6.cs:8485`). The new session/
transfer tokens did not address this separate data-session representation.

The original reason proves the data-session comparison failed. Source ordering
and the saved unique frontier-1 arm localize the first `Scope -Wake` call at
resolver line 200. This localization is read-only analysis, not another resolver
execution or native witness. The receipt's data-session token was not exported by
the observation summary; it is not reconstructed from transfer ID, generation,
timestamps or nearby coordinator rows. Later checks remain unvalidated.

## Next bounded step

Review a diagnostic data-session identity contract before implementation:

1. Inventory the exact `Scope -Wake` and invalidation consumers and their typed
   data-session sources; retain the existing generation and ownership checks.
2. Specify a redaction-safe projection of the original typed identity, strict
   duplicate/conflict/missing-field behavior and explicit legacy handling. Do
   not borrow identity from session/transfer tokens or change the native payload,
   producer validation, redactor, timing policy or runtime authority.
3. After that contract is approved, implement only this identity edge with focused
   controls. Gate any subsequent original-process characterization separately;
   no automatic retry or live-256/full matrix is warranted now.

## Preservation

All 3,063 prior pins and all 527 prior dirty contents/statuses are unchanged.
Only this handoff is a new non-ignored file (528 dirty entries). New run artifacts,
binary before-images, one-shot driver and final checks are under the evidence
directory. A passing preservation seal must not be read as a passing
characterization or acceptance verdict.
