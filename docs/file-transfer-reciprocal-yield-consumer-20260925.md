# Reciprocal Service-yield receipt consumer

2026-09-25. Branch `v0.7.0`, base HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`. Implements the test-only next step in
`file-transfer-peer-lineage-stale-safety-review-20260925.md`. No application code,
runtime authority, acceptance rule or live scenario was changed.

## Implemented boundary

The existing default-off consumer has a separate reciprocal opt-in. It preserves
the forward result and starts the reverse association from that forward chain's
exact receiver yield operation. It follows the receiver's original local receipt
and successful encrypted copy to the source's authenticated ingress, its own
accepted peer receipt and the source's paired release. Both local proof histories
must retain the exact receipts and operation ownership without invalidation.
The two ends must reference each other's canonical proof and explicit remote
authority/generation; independent peer counters are not equated.

The reverse query stops at the source paired release. It does not invent a sender
responder handoff. The existing receiver handoff and target-publication checks
remain intact. Per-copy transport matching is shared with the forward query;
retry-request/reservation companion operations are not mistaken for a second
yield proof lifecycle. They remain subject to the original authority evaluation.

The query runs within the original primary custody over the original arrays,
origins, writer evidence and completed full/compact views. It adds no evaluator
or input reader. Both directions share the existing 64-line witness cap and all
other original input/work bounds. Only a separate diagnostic adds
`ReciprocalStatus=matched_original_reciprocal_receipts`; the primary carrier stays
`peerLineageStatus=not_evaluated`.

## Genuine copy and receipt ordering

The two-process fake transport now supports four fixed test cases. Single-copy
cases fail the unavailable copy at the actual fake-send boundary, preserving its
real unsuccessful outcome and all original diagnostic records. Two-copy cases
hold the second original packet until the first reaches the receiving data
session. Repeated queue/direct control deliveries are preserved for normal
transport deduplication, not removed from the source logs.

The local-first case calls the receiver's existing local-yield Service path
before applying any peer proof. It does not install a manufactured receipt or
change production scheduling. Original boundary generations verify the actual
receipt order. The old default fixture still uses its original delivery path.

Final fresh integrations, under `artifacts/publication-reciprocal-yield-20260925/`:

| Case / original run | Selected copy, both directions | Raw / full / compact records | Witness lines |
| --- | --- | --- | --- |
| Control-only: `01aca8ca50e640e6b981ebc2cce6ad33` | Control | 372 / 326 / 162 | 17 |
| Bulk-only: `57806faaba6a43f7b1c0e1dd52f10187` | Bulk | 371 / 322 / 162 | 17 |
| Both, control-first and receiver-peer-first: `735aca88e133487da0bb5fbf1938239b` | Control | 384 / 339 / 162 | 17 |
| Both, bulk-first and receiver-local-first: `0a30891b8da94036a2d372cc18dc2d9b` | Bulk | 383 / 338 / 162 | 16 |

All four pass on Windows PowerShell and PowerShell 7. Each retains 71 passing
boundaries per view, full/compact agreement and exactly three original authority
evaluations. Single-copy cases contain 10 yield diagnostics; two-copy cases
contain 14. Counts are observed results, not relaxed admission limits.

Each case also passes 20 detached reverse-edge negative controls per authority
view on both hosts. These cover missing/substituted associations, wrong owner or
security episode, borrowed idempotent acceptance, failed selected copy, broken
receipt/history continuity, invalidation and absent/invalid paired release.
Every negative preserves the forward query, so a forward failure cannot satisfy
a reverse control. Detached controls are not new authenticated histories or
authority validation. Eight existing custody/origin/limit omission checks remain
inside each original consumer invocation.

## Development findings

An initial Debug run exhausted the test stack after large value-type proof
assertions were added to the existing route-commit async fixture. The local-first
assertions were moved into their own helper, and the fixture unwinds the receive
continuation before its existing deep Snapshot call. No production stack or
scheduling workaround was introduced. The first packet-order gate also assumed
only one physical control delivery; it now retains the original queue/direct
duplicates for the real transport's deduplication path.

The next query attempt rejected the source pairing because it counted the
companion `retry_request_pending` operation as a second proof operation. The
consumer now selects the explicit current/terminal/pending-peer yield lifecycle
roles while preserving complete state-generation continuity. The original
authority evaluation is unchanged. Failed development receipts are preserved;
`results/reciprocal-final.trx` is the accepted four-case run.

The first build was blocked by Avalonia build telemetry writing outside the
sandbox. The local build was rerun with `-p:UsedAvaloniaProducts=` to suppress
that telemetry target; no application code or test predicate was altered for it.

## Final verification

Seven focused xUnit cases pass: four genuine integrations in
`results/reciprocal-final.trx` and three compatibility cases in
`results/affected-regression.trx`. The existing forward-only consumer still
passes its original controls and emits no reciprocal field. The original
Service-history and small-input observer cases remain consumer-default-off on
both hosts. No aggregate suite was run. PowerShell parsing and `git diff --check`
also pass.

Of the previous implementation seal's 113 pins, 106 are unchanged and seven
test/support files are extended by this slice. All previous artifacts, review
documents and seals are preserved. Of the 45 pre-existing dirty files, 38 are
byte-identical; the other seven are the intended test-only extensions. All
pre-existing application-source changes are preserved byte-for-byte.

## Remaining boundary

Every genuine case still reports `missing_exact_tuna_receive`, unrecovered, with
the original primary explanation and acceptance. Reciprocal receipt association
is not Tuna payload delivery, general stale-callback safety, durable-write proof,
or a whole-peer-lineage certificate. The ingress-versus-dispatch guard distinction
and existing fixture limits in the reviewed contract remain applicable.

Next is the bounded rejection-safety contract decision: identify the exact
candidate/companion and contradiction checks a diagnostic would need, without
promoting these receipt matches into a recovery or acceptance exemption. No live
256 MiB test is required for this completed diagnostic boundary. Final regression
receipts, preservation checks and hashes are recorded in
`artifacts/publication-reciprocal-yield-20260925/FINAL-VERIFICATION.json`.
No portable rebuild, live GUI/NKN test, commit or push was performed.
