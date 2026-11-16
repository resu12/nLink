# Fallback pending peer receipt: exact local-scope correlation

2026-09-20. Base HEAD `7c28d20701d3dd253d6c1c16f8f673eb3aef082a`.
Scope: one raw-event analyzer predicate and focused deterministic regression.
No runtime, route authority, retry, transport, or acceptance-contract change.

## Original evidence

The completed `20260920T170220Z` live run reports five pairing commits, four valid
pairings and one unresolved fallback-coordinator aggregate incident. Its full
and compact coordinator snapshot validations pass. Those original verdicts are
preserved; the retained input has not been reevaluated.

Source: `artifacts/terminal-checkpoint-live-20260920/live/20260920T170220Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-retained-log-slice.log`.
Input SHA256: `b6fc6d07a3c93404d0208a927eaae54c4cb5dfe4e817cd5e10b7f4d5696e7c61`.
All rows below belong to receiver writer `p14428r50cd0dfb83874cd8`.

| Line / writer sequence | Record | Local publication/runtime/leg | Bridge generation |
| --- | --- | --- | --- |
| 21220 / 9381 | Earlier accepted pending peer receipt | 5 / 5 / 5 | 1 |
| 42063 / 22654 | Current accepted pending peer receipt | 7 / 7 / 7 | 3 |
| 42066 / 22657 | Current receipt bound to local yield 4 | 7 / 7 / 7 | 3 |
| 42067 / 22658 | Local yield 4 issued | 7 / 7 / 7 | 3 |
| 42077 / 22672 | Local proof receipt accepted | 7 / 7 / 7 | 3 |
| 42079 / 22674 | Pairing generation 3 committed | 7 / 7 / 7 | 3 |

Both pending records retain the same displayed peer identity, fingerprint,
generation 2, canonical pair key, session, transfer, direction and data session.
Their local owner keys differ with the local generations above. The current
binding/yield/pairing uses authority
`fta2-fallback_tail_yield-ce2e3ba59b0d245eeb13ff2c`.

These are read-only raw-field observations, not newly computed live verdicts.
Redacted identifiers are not promoted into new authority or cross-peer proof.
The older generic receipt row at 42060 displays the peer direction, but the
canonical pending row at 42063 has the correct local direction. That diagnostic
discrepancy is not the cause of this lineage failure and is not changed here.

## Root cause and bounded fix

`Test-FallbackTailAggregatePendingPeerLineage` requires exactly one earlier
accepted pending receipt matching the bound peer evidence. Its predicate omitted
the pending receipt's complete local execution scope. Thus a historical receipt
from a different publication could both create false ambiguity beside the exact
receipt and substitute for that exact receipt if it were absent.

The coordinator already requires
`initialPeer.ObservationScope == candidateOperation.Scope` before binding the
pending receipt (`FileTransferCoordinator.cs`, local-yield issuance). The raw
analyzer must preserve that ownership distinction too.

The correction adds the existing `Test-FallbackTailAggregateLocalScope` predicate
against the bound receipt. It checks the writer, session/transfer/direction,
local owner and pair keys, route/protocol, publication/commit, live epoch,
runtime/terminal/leg/bridge/transport generations, checkpoint and data session.
The pending receipt intentionally has no local yield authority yet, so the
authority-bearing local-tuple helper is not used.

Existing ordering, accepted-status, peer identity/fingerprint and peer-lineage
checks remain in place. Exactly one matching pending record is still required;
duplicate exact receipts remain ambiguous. No arbitrary latest-record selection,
deduplication, scope fallback or historical receipt reuse is introduced.

## Verification

The regression reuses the existing peer-first aggregate fixture, converts its
peer receipt to pending/bound evidence, and parses full and compact inputs
independently. It runs on Windows PowerShell 5 and PowerShell 7.

The pre-fix regression passes the single exact receipt and fails when an
unrelated local writer's otherwise identical receipt precedes it, reproducing
the omitted-local-scope defect. After the one-line fix, the focused test passes.
The final matrix covers 67 cases per host: exact receipt, same peer under an
older local publication, twenty individual foreign-scope fields (with and
without an exact receipt), twenty missing scope fields, absent/duplicate/rejected
pending receipts and mismatched peer identity/fingerprint.

The affected `FallbackTailCoordinatorAggregate` lane passes all 36 tests, with
zero failures/skips, including the final 67-case matrix on both hosts. The test
assembly was rebuilt successfully after an offline local-cache restore; this is
not a stale-assembly-only verification.

Detailed test/build results are recorded in
`artifacts/fallback-pending-scope-20260920/RESULTS.md`.

## Remaining boundaries

The original run remains correctness FAIL. This offline correction is not a new
live acceptance result. The thirteen unresolved data-path incidents and one
stall-grace obligation remain independent findings.

The original compact summary retains stall-grace accounting 36 raw / 36 distinct /
35 recovered / 0 superseded / 1 unresolved. The primary exports its detailed
stall diagnostic only when stall grace is the overall first failure; here the
earlier data-path failure masks that selection. No frontier/publication companion
artifact is present. The exact unresolved stall witness has not been established
by this inspection. Do not label a guessed wake, successful SHA, dispatched
request, or passed coordinator snapshot as closure of that obligation. Continue
with its original evidence/diagnostic path before a runtime fix or another live
test.

Follow-up: the subsequent read-only investigation isolates wake 137 across
fallback publication 6-to-7 replacement and adds a passing local retirement
regression. See [the fallback-republication RCA](file-transfer-stall-grace-fallback-republication-rca.md).
This is not a reclassification of the original primary result.
