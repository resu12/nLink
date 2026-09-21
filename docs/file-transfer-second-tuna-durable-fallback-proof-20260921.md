# Second-Tuna rejection followed by Service-owned durable range delivery

## Result and scope

The bounded offline continuation now crosses the actual receiver Service's
second-Tuna and final-fallback transitions. The generated primary reaches
`missing_tuna_resend`, rather than the adapter-only fixture's earlier
`missing_receiver_route_transition`. Later disk delivery still does not satisfy
the unchanged pre-rejection exact-Tuna contract.

Base HEAD is `d96eeac0b9c1553c786b633adae91b4d1dd85be7`. All changes are tests
and documentation. No production code, acceptance predicate, live GUI/NKN run,
portable build, full matrix, commit or push is included.

## What is exercised

1. The existing receiver Service fixture starts from an explicit committed-prefix baseline
   of 100 zero-filled chunks. Each chunk is 21 KiB. The new held range is chunks
   100–101, not the previously used 12–13, which would already be in that prefix.
2. Two real offline NKN adapters and separate process writers carry the original
   authenticated Regular-V4 envelope and the paired-yield frames. The receiver
   Service commits second Tuna through its existing production transition path.
3. Under second Tuna, replaying the held envelope produces rejection before
   dispatch; its duplicate deduplicates. Service Core state fingerprint, emitted
   transitions, forwarded hints, control-frame count, progress and destination
   hash remain unchanged. There is no Service write from that rejected packet.
4. A separate fixture action supplies the existing peer-first fallback trigger
   to the production Service bootstrap/transition path. It commits and applies
   the final-fallback disposition: live epoch 4, publication 5, protocol 6, new
   runtime owner. Both actual Service notifications (checkpoint pending and then
   recovery active) are forwarded to the adapter in order.
5. A fresh authenticated V6 envelope carries the same 43,008 bytes. The Service's
   already-bound recording data-session wrapper delegates receive to the real
   adapter, preserving transport metadata. Its identity and generation remain
   unchanged. The production V6 receive handler validates and writes the batch.
6. Service frontier advances 100 → 102 and bytes 2,150,400 → 2,193,408. Pending
   writes are empty. The test explicitly flushes the destination to disk, opens
   a separate read handle, and compares the exact range bytes. Replaying the old
   second-Tuna drain execution then changes neither Core state nor file/progress.

The two-peer security establishment/session/transfer scope stays unchanged;
fallback uses a new envelope and a greater secure sequence. Original writer
records and their retained projection are checked without rewriting identities.
The final Service transition tuple is matched to the actual fallback receipt.

## Important limits

- This proves the new range through the Service receive handler, not a complete
  production receive-pump lifecycle, whole-file completion, or live NKN behavior.
- The sender still uses the existing Core history driver; this is not a run of
  two complete production Services. The receiver uses the real Service path.
- The durable prefix, control delivery responses and independent bootstrap
  trigger are explicit fixture setup. If a current repair alias is needed, the
  fixture carries that Service-issued alias through its offline coordination
  pipe; this is not proof of a complete live repair-request/response exchange.
- The test performs the explicit disk flush/reopen check. It does not assert
  that every production progress update itself executes a disk flush.
- The source data-session wrapper is installed before the first Core snapshot.
  It is never swapped to admit the final batch. This is scoped local continuity,
  not a general cross-incarnation or whole-session lineage adjudication.
- No historical live candidate is reanalyzed, reclassified or waived.

## Fixture corrections, not runtime fixes

The first attempts exposed test setup assumptions:

- A read-only file verification handle must share the existing write handle.
- Fallback cycle 2 is distinct from transport epoch 2. Here second Tuna resets
  the current leg transport epoch to zero; the next accepted wire epoch is 1.
  The fixture now derives that epoch from the source leg while retaining
  fallback/bootstrap generation 2. The rejected mismatched setup granted no
  final-fallback authority; production guards were unchanged.
- Final publication produces both a checkpoint-pending notification and an
  accepted-checkpoint notification. Both are preserved, rather than asserting
  there is only one or constructing a replacement hint.

## Verification

Evidence: `artifacts/live256-service-durable-fallback-20260921/RESULTS.md`.
Final verification: 28 selected cases passed (one new integration and 27 affected
regressions), zero failed/skipped/aborted. No aggregate lane was run.
The final focused test passes on PowerShell 5.1 and pwsh. Each primary keeps its
existing three evaluations, reports the exact missing-Tuna-resend variant, and
completes its original analysis receipt. Both generated Core Snapshot streams
are complete with 96 passing boundaries and passing original Snapshot parity.

## Next decision

Follow-up: the [bounded contract review](file-transfer-rejected-ingress-delivery-contract-review-20260921.md)
is complete. It keeps the hard recovery result and stronger classifier no-go,
and specifies only a small reporting clarification as the next implementation.
The durable fixture does not authorize a live delivery/safety exemption.

Review the contract distinction between (a) safe stale-envelope rejection plus
independently authorized later durable range delivery and (b) recovery by an
exact Tuna send/receive before rejection. The new evidence shows these are not
interchangeable. It does not establish a missing runtime resend obligation or
authorize a new acceptance exemption.

Recommended next step: a bounded contract review for a separate diagnostic-only
stale-rejection/delivery finding, with explicit missing-lineage exclusions and
negative cases. Keep the current hard acceptance result unchanged until any
acceptance change is separately reviewed. Do not rerun live-256 merely to seek
a different result under the unchanged predicate.
