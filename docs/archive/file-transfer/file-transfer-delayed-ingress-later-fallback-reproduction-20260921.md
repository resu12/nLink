# Delayed Regular ingress followed by independent fallback delivery

## Scope and result

Test-only continuation of the live-256 prerequisite review at base HEAD
`d96eeac0b9c1553c786b633adae91b4d1dd85be7`. No production transport, Coordinator,
acceptance predicate, timeout, or live-run policy changes.

The new bounded fixture demonstrates that these events can coexist:

1. A genuine encrypted Regular-V4 envelope is held in an offline packet pipe.
2. Both real adapters advance through four Core-issued publications to second
   Tuna-V4, live epoch 3. The original envelope is replayed twice. The receiver
   rejects it before dispatch with `authority_effect=none`; the duplicate is
   deduplicated. Adapter state stays unchanged and its receive queue stays empty.
3. Only after that check, a separate fixture action invokes the real Coordinator
   fallback/checkpoint helpers. Both peers publish final fallback-V6, live epoch
   4, publication 5. Replaying the previous publication does not roll back the
   adapter. The held rejected envelope is never used as fallback authority.
4. The sender derives a new send authority from publication 5. A fresh encrypted
   V6 envelope delivers chunks 12–13, 2,048 bytes, with exact byte comparison at
   the receiving adapter and exactly one V6 dispatch.

Two genuine test-host writers retain their own original logs. Collection merges
original records without changing identities or receipts. The generated source
and its retained projection are verified by the existing provenance assertions.

This is **adapter delivery**, not a complete Service transfer or durable file
write. The route history driver exercises Core and its Snapshot emitter; it does
not pretend to execute a Service route-transition workflow. Its original primary
therefore remains unresolved at `missing_receiver_route_transition`, even after
the V6 delivery. It cannot settle any historical live-256 candidate.

## Acceptance remains strict

Two added, explicitly synthetic matcher controls supply the existing second-Tuna
transition topology and later fifth-publication fallback delivery with identical
range/byte count. They verify that:

- No earlier Tuna send remains `missing_tuna_resend`.
- An earlier Tuna send without an earlier Tuna receive remains
  `missing_exact_tuna_receive`.

Both controls retain the rejection candidate and fresh full/compact proof parity.
They do not relabel the generated transport log as Tuna. Existing Service-backed
two-writer exact-Tuna, missing-receive and wrong-range controls remain separate.
Existing matcher negatives cover source scope, publication, range, byte count,
rejection companions and terminal boundaries.

## Verification and fixture corrections

Evidence index: `artifacts/live256-later-fallback-20260921/RESULTS.md`.
Final verification: eight focused test cases passed, zero failed/skipped; the
diagnostic case includes 24 matcher controls on both PowerShell hosts.

The first developmental run incorrectly required the adapter phase to remain
unchanged after legitimate V6 delivery. Delivery correctly changes it to
`Transferring`; the assertion now requires exactly the expected V6 dispatch.
The second reached delivery but exceeded the offline writer fixture's 1 MiB
small-input log cap. It now selects that fixture's existing large-input mode.
Neither correction changes runtime behavior or a production evidence limit.

The passing generated run has 63 complete, passing Core Snapshot boundaries and
passing original Snapshot parity on both PowerShell 5.1 and pwsh. The original
primary performs its existing three evaluations; no historical capture is
reanalyzed and no parent performs an extra authority evaluation.

## Next gate

The subsequent Service-level bounded range proof is recorded in
`docs/file-transfer-second-tuna-durable-fallback-proof-20260921.md`. It closes
the adapter-only transition/write gap below, without changing acceptance or
claiming whole-transfer completion. The next decision is the contract review
described there.

The original gate was to join the independently issued final-fallback continuation
to the Service-owned second-Tuna commit and a durable receiver path, preserving
owner/publication/security lineage and proving rejection has no Service effects.
The Service proof must not replace a missing receipt with the history driver's
receipt; the subsequent fixture uses the real receiver Service transition.

Only then review whether a separately proven stale-rejection disposition is
warranted. No such acceptance change is approved or implemented here. A runtime
fix must first identify a current missing obligation; the older live artifact
alone does not authorize changing runtime code. Any required live-256 validation
still needs a fresh portable build, escalated strict preflights, and the second
Core-published Tuna-V4 route. No live run, full matrix, commit or push occurred.
