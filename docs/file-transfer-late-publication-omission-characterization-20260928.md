# Late-publication omission characterization and first-edge RCA

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result

One rebuilt, isolated small offline original-process characterization was run.
It **failed** after 66.074916 seconds, but captured the original resolver's
previously missing reason: **`native_frontier_receipt_ambiguity`**.
No retry, production/test source change, acceptance relaxation, live NKN,
live-256, full GUI matrix, commit or push occurred in this step.

The original resolver reached its exact receipt-match guard. Saved evidence
confirms that the selected failure's session and transfer fields are both
`[redacted]`; hashing that literal cannot match the receipt's original
privacy-safe scope tokens. This is now the demonstrated first missing diagnostic
association, not a guess based only on the preceding run. Later resolver checks
remain unvalidated.

## Run and verification

Evidence directory:
`artifacts/late-publication-omission-characterization-20260928/`.

Original run:
`artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-76715a5ae4d64c8c8becfb6639158c34/`.

- Entry audit and both strict preflights matched all 2,368 saved pins and 515
  existing dirty entries. Branch and HEAD were unchanged.
- Debug rebuild passed with zero warnings/errors and no pinned binary changes.
  The existing 42 focused omission/guard tests were verified from their pinned
  all-passing TRX; no unrelated lane was rerun.
- Process-only environment isolation, Debug overrides, high-resolution clock,
  disk space and absence of overlapping processes passed outside the restricted
  sandbox. A create-new attempt marker prevented reuse.
- Exactly one selected test ran: `OriginalApps_CarryLateFrontierReceiptIntoPrimary`.
  TRX: one failed, zero passed/skipped.
- This was a **168 KiB / 172,032-byte named-pipe offline carrier**, not a network
  or GUI acceptance run. The original parent retains its canonical 256 MiB
  source and GUI/live route-cycle labels; those labels do not establish live
  execution or route acceptance.

## Observed sequence

The sender (PID 36644) and receiver (PID 50152) completed with the same
172,032-byte destination SHA-256:
`67331c29dda482392a6413e0b35c3a7fa7692b5db1bfd1496da5575752f49a0b`.

The receiver recorded the unchanged `late-publication-fixed-4s-v1` policy and
`closed-within-bound; physical-copies=3; root=unchanged`.
The wrapper (PID 36856) exited 0 without timeout. The original analyzer
(PID 19988) performed three evaluations and one native read, then exited 1
without timeout.

The original observation records:

- Outcome `PublishedWithinObservationBound`, observation
  `49a48c87e1bd450390a45e3727213a91`, writer `p50152re7562f207043447a`.
- Receipt: 6,533 bytes, SHA-256
  `02774cb1f80303ab6bf34541dc8ff767730560b2b425f06b0b133d56c60db562`.
- Completion physical line 1506, marker physical line 1711; 11 original pins.
- Selected failure `filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.
- One resolver call; reason `native_frontier_receipt_ambiguity`; zero witnesses;
  diagnostic output not committed.
- Primary verdict `FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`.
  All three diagnostic authority flags remain false.
- All ten original report hashes unchanged across the optional resolver.
  Zero pins open after cleanup; retained payload/witness text cleared.

The first stderr line contains the exact JSON-escaped omission reason. The
following AfterResolve, AfterOptional and Closed milestone errors are downstream
consequences of the missing witness, not separate first-edge failures.

## Exact first-edge RCA

The original selected semantic line 1056 maps to physical line 1375, writer
sequence 607. Its recorded native text equals the retained selected text.
Both scope fields are `[redacted]`, whose token is
`fte1-017ad73325fcf108a972edac`.

The one original captured receipt instead contains session
`fte1-1b21fbe50c2acd3f0dfd2abe` and transfer
`fte1-5fe4c0c34d7fff309ca00f9c`. Hashing the sender's original completion IDs
matches these receipt tokens; the receipt writer also matches the selected row.
Neither selected-row identity matches. Thus this run has no exact receipt
candidate for that row; the guard name does not imply duplicate receipts.

`tools/FileTransferOps/LocalNativeFrontierWitness.ps1:110-115` requires exact
writer, session, transfer and repair-progress association, then exactly one
match. Its original reported reason proves it reached this guard; an earlier
input-shape or custody rejection is no longer the explanation for this run.
This is a diagnostic identity-association failure, not evidence that a transport
or timing change is needed. No later proof obligation is inferred to pass.

The supporting comparison only reads saved fields and calculates hashes. It
does not rerun the resolver, authority evaluator or receipt producer.

## Next bounded step

Prepare and review the finite privacy-safe event-scope contract before any
scope-binding implementation. Inventory all required, competing and invalidating
events consumed by the resolver, including the raw-ID episode partition at
lines 117-118. The contract must preserve exact session AND transfer association,
original writer/physical-source ownership, strict legacy behavior, and rejection
of missing, duplicate, conflicting, foreign or ambiguous scope evidence.

Do not patch only the first comparison: otherwise later invalidating or
competing events could silently disappear from the owned episode. Raw IDs must
stay redacted; neither one available receipt nor writer/timestamp proximity can
substitute for exact identity. New scope evidence is diagnostic-only and must
not grant route/runtime/publication authority. Hold caps and production deadlines
remain unchanged. Implementation and its focused controls follow that contract;
no new characterization or live-256/matrix is warranted during contract review.

## Cleanup and preservation

Read-only cleanup confirmed all four known processes exited and no overlapping
test/native process remained. All 50 original-run files opened exclusively for
reading; destination length/hash and all ten report hashes matched. No process
was killed or artifact deleted.

All 2,368 prior pins and all 515 existing dirty file statuses/contents are
preserved. This handoff is the sole new non-ignored file (516 dirty entries).
The final seal and independent check under the evidence directory distinguish
a successful preservation audit from the **failed characterization**.
