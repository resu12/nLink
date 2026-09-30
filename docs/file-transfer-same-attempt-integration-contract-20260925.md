# Same-attempt source and terminal integration contract

2026-09-25. Review only, on `v0.7.0` at
`a653d4063070235c71fe211480aeb37863d9b2a4`, with existing uncommitted work.
Follows `file-transfer-terminal-integrity-evidence-20260925.md`.
No runtime, acceptance, classifier or receipt semantics change in this review.

## Decision and first missing edge

The terminal producer and the two-writer route capture cannot be combined.
They belong to different attempts. The terminal test binds its observations to
a private `TransferRun` object, but that owner does not own an analyzer log
source, writer completion or original primary invocation. Identical run labels,
paths or payload hashes in exported JSON would not supply that missing link.

The next bounded implementation is a **test-only, same-attempt binding between
the real transfer's terminal/file evidence and its frozen operational log**.
Establish this producer edge before extending a cross-process receipt. Keep the
existing alternative shadow unchanged and unproven; this is not a canonical
scenario positive or acceptance cutover.

## Three different completion meanings

| Existing result | What its owner establishes | What it does not establish |
| --- | --- | --- |
| Terminal test `Capture` | Exact private transfer owner, both real snapshots and measured whole-file equality | Ownership of the primary's source or evaluations |
| GUI writer `Completion` | Original writer finalization and bound log outputs | Completed transfer or actual destination-file hash |
| Analysis `TerminalReceiptCustody` | Original analysis attempt and its eleven output artifacts | Transfer completion; its `completed` status concerns analysis |

`LocalGuiWriterRequest.cs:227` finalizes three persisted output descriptors;
the accepted completion has four outputs, and `MatchesSource` binds output 3
to the retained source (`:320`). None is a saved transfer destination.
`LocalFrontierTerminalReceipt.cs:53` binds the original source request, analysis
attempt and artifact catalog, not the file-transfer producer.
`LocalPrimaryPublicationCompletion.cs:76` remains explicitly
`nlink-local-publication-forwarding-v2`, scope `local_publication_forwarding`.
Do not reinterpret any of these as a whole-scenario receipt.

## Next bounded implementation and verification

Limit edits to the terminal test fixture and, only if needed, one adjacent
test-only helper. Reuse its genuine 256 KiB Regular-v4 transfer and existing
source/destination checks. No production DTO, writer codec or parent/primary
acceptance change is needed for this producer milestone.

1. Run the binding fixture in a fresh isolated test host. Set its owned log
   path and run/scenario scope before the static logger initializes, as the
   existing publication consumer driver does. Do not reset the global logger
   or drain the logger shared by unrelated tests. The parent checks the actual
   child exit; exported diagnostics are assertions about the child, not a
   cross-process capability accepted by the primary.
2. One private fixture owner controls the fresh attempt, Service pair, source,
   destination and log lifetime. Keep both genuine terminal snapshots and the
   existing post-closure file measurements. Capture the actual terminal log
   records from that transfer, without appending synthetic completion records
   or copying the earlier two-writer trace.
3. Wait for the real terminal records as well as the terminal snapshots before
   draining. The Service emits `transfer_terminal` after resource cleanup
   (`SessionFileTransferService.OutboundTransferController.cs:344,678`); a
   snapshot becoming terminal is not by itself proof that the log write has
   finished. Use bounded observation, not an assumed sleep. Then complete and
   flush the isolated writer and hold a read lease over its frozen source.
4. Retain the exact source-owner reference, physical source identity, size,
   hash, line count, actual writer identity and sequence bounds. Bind the two
   terminal records to their original physical lines, writer sequences,
   session, transfer and opposite directions, and to the same snapshots/file
   measurement owner. No supplied JSON or caller-selected path can nominate
   these results. Do not rewrite redacted fields to make records match; if a
   necessary identity is unavailable, report that exact missing edge.
5. Require a real positive and fail-closed controls for a foreign capture,
   relabeled foreign capture with identical payload bytes, missing/duplicate
   terminal record, wrong direction/session/transfer, changed or replaced log,
   incomplete writer drain and changed destination. Retain existing canceled
   transfer and file-integrity negatives. Detached record mutations must be
   labeled as predicate controls, not new genuine histories.
6. Report only offline source/terminal binding. Both Services share one actual
   writer in this fixture: that is not independent-peer or authenticated NKN
   evidence. Do not set scenario-scope, Tuna-payload, full-cycle or remaining-
   failures gates, or emit a canonical `would_pass`.

Run only the new binding tests and existing terminal tests whose shared helper
changes. No analyzer aggregate, portable build or live GUI run is needed.
Success means a genuine same-attempt source/terminal positive plus the stated
ownership and mutation rejections, not merely a generated JSON file.

## Subsequent primary join: preserve every independent result

Only after the producer binding is demonstrated should the existing original
primary custody consume a separately designed owner handoff. It must bind the
same source/writer descriptors and original full/compact invocation identities,
alongside actual parent scope. The test observer already has a seam after the
original reports and before custody cleanup in `PublicationLivePrimaryObserver.ps1`.
Use already computed original objects there; do not reopen logs, rerun an
authority evaluator or treat the terminal diagnostic JSON as an input receipt.

The future handoff must carry all of these independently:

- Actual parent request and scope, including offline versus canonical live
  scope; the existing offline parent requests `2B`, not 268,435,456 bytes.
- Complete original route-cycle result, including final fallback after second
  Tuna. Route consistency alone is insufficient.
- Terminal/file result from that same producer and source lifetime.
- Each distinct original candidate and a closed inventory of independent
  verdict factors, including their applicability and warning policy. Preserve
  GUI/terminal checks, hard failures, active Tuna-v6, route consistency,
  live-route proof, preterminal liveness, warning caps/exemptions, unresolved
  proof-specific checks, artifact evidence and full/compact parity.

`AnalyzerOrchestration.ps1:6175` starts independent verdict checks; subsequent
proof-family, first-failure and artifact/parity assignments overwrite the
aggregate result. Therefore neither the aggregate verdict nor the selected
mismatch is a complete factor inventory. The implementation must cover the
actual predicates and original candidate collections, not enumerate nonzero
JSON fields, assume absent counts are zero, or subtract one admitted mismatch.
Unknown/missing coverage remains unproven; a known independent failure remains
a failure even if another gate is unproven or the selected mismatch is admitted.

Required regression: the existing two-pair capture at
`artifacts/publication-tuna-pairs-20260925/37b7d3a3ae0a42b389cecdfc11f5a499/`
has `route_consistency_verdict=pass` but `live_route_epoch_proof_verdict=fail`
in both host reports. Its sequence stops at second Tuna. A future owned join
must report that failed route gate, not erase it when the recorded rejection
is eligible. Historical reports and the current shadow are not relabeled by
this review.

The producer milestone performs no authority evaluation. Any later primary
integration must preserve the existing three evaluations and bounds: 4,096
records, 256 boundaries per view, 64 operations per boundary, 64 witnesses and
cumulative 20-second diagnostic work. Neither changing acceptance nor running
live-256 follows from this review; a full same-attempt positive remains pending.

## Review verification

Source/artifact inspection only. No code changed, so no tests were rerun.
All 91 baseline dirty-file contents and all 45 pins from the terminal milestone
remain unchanged. This document is the only new non-ignored file;
`git diff --check` passes. The ignored seal is
`artifacts/same-attempt-integration-review-20260925/FINAL-VERIFICATION.json`.
No build, live run, commit or push was performed.
