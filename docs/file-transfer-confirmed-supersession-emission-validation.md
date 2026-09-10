# Successful-confirmation Superseded evidence: separate validation

Implementation follow-up (2026-09-09): the user subsequently authorized fix 1,
then fix 2, then joined validation. The successful-retirement correction and
focused gates are recorded in
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/successful-confirmation-fix/RESULTS.md`.
The historical capture and validation-only conclusions below remain unchanged.

2026-09-09, base HEAD `b955354f`, existing uncommitted work preserved.
Validation only: no production fix, build, commit, full report or live attempt.

## Captured result

The existing service test
`SuccessorInstall_UsesNextGenerationAndCannotBeClearedByOldReceipt` passes its
runtime state assertions: successful B confirmation is followed by bridge
acceptance of A's Superseded settlement, A is retired and B remains installed.
The exact production service/emitter/redactor/writer stream was captured using
the supported private-log environment, with diagnostic-only run IDs:

`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/successful-confirmation/capture-02/service.log`

SHA256: `5797DC2F78CF1C43641D885C5200756395F2F962E703BC5FC4C3394C988E64E5`.
Writer: `p42916rf069cf5e9b074d3f`.
Coordinator: `fte1-d013d0246791e47d79cf10e1`.
The raw audit verifies18 payload hashes and consecutive generation/hash links;
the producer emits start and completion, final generation16. This is a service
unit fixture using a test bridge, not live NKN or complete route-cycle proof.

| Writer sequence / log line | Exact evidence |
|---|---|
|38|A installs with its exact receipt, authority generation1.|
|53|B installs at14→15 while A becomes a retained provisional Superseded predecessor.|
|54|B's exact install effect is emitted.|
|56|A's accepted retirement at15→16 moves retained A `Settled→None` and terminal A `None→Settled` with the same Superseded receipt.|
|57|Stream completion; no terminal-bound settlement effect was emitted.|

Retirement transition: `fte1-78cc3bbbc4f38c77f9c16b67`.
Decision: `ObserveRuntimeUnlockRetryDispatchSettlement/Accepted`.
Reason: `retry_dispatch_settlement_superseded_predecessor`.
A: `fte1-6f354875df10bfd6fc836d7a`, authority generation1.
B: `fte1-20185986091f86a2291d1075`, authority generation2.
A settlement receipt: `fte1-17d658bac0c4528e4deee74f`, offer generation18,
fingerprint `fte1-0952b2d9b14476681a2c7983`, status `Superseded`.
These generation domains must not be equated.

Independent full, catalog-regenerated compact and both shared-GUI inputs agree
on Windows PowerShell5.1 and PowerShell7. The evaluator reports a complete stream
with18 transitions,9 effects and legacy0, but artifact evidence FAIL at56:
`snapshot_operation_catalog_mismatch`, `filetransfer_coordinator_snapshot_evidence_invalid`,
**1/1/0/1**, variant `(none)`. Both retirement descriptors fail local catalog
validation. There are zero actual settlement effects, and two potential generic
helper receipt matches; the latter is an inspection result, not a claimed helper
invocation. No effect or receipt was reconstructed.

Selected boundary ID/hash:
`dc268c849701c6596786bb9fe901e4f97b755225a6227b7e24bfec4a23be064a`.
Boundary-set hash:
`6018753a0274085f31e3e121dbdfc2e730e6bd372e08e53de0d7fd341463cae9`.
Cross-host normalized diagnostic JSON is identical and all pinned input/source
hashes are unchanged. The missing-effect condition remains independently proven
by the original capture; catalog failure is the currently selected boundary.

## Two coupled conformance gaps

1. **Missing service effect.** The successful inline branches at
   `SessionFileTransferService.cs:6665` and6790 adopt the Coordinator retirement
   but do not call the settlement-effect path. The ordinary callback instead
   uses `ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked`, at
   `SessionFileTransferService.RuntimeUnlockRetryAuthority.cs:1318`.
2. **Successful retirement not admitted by the narrow analyzer path.** Existing
   Superseded admission is intentionally limited to provisional installation
   and its compensation inverse. Successful retained removal/terminal entry is
   neither. The role fold and receipt-effect consumer also need exact successful
   retirement provenance; adding a status globally is not a correction.

Calling the current generic effect helper alone would still emit nothing: the
retirement has two validated settlement-bearing descriptors. Its exactly-one
receipt selection at `SessionFileTransferService.CoordinatorSnapshots.cs:978`
correctly refuses to guess. The effect must belong only to the entering terminal.
Core's actual cleanup at `FileTransferCoordinator.cs:17276` is valid and must
remain unchanged. A missing telemetry effect is not failed operational cleanup.

## Recommended bounded fix order (not implemented here)

1. Preserve this real service capture and add a regression requiring exactly one
   terminal-A `settlement_observed` effect after successful exact bridge cleanup.
   Provisional commit, retained removal and failed confirmation require none.
   Keep B independently installed/open until its own execution and settlement.
2. Centralize the existing successful retirement adoption/effect path for both
   directions. Resolve the unique entering terminal and exact retained removal,
   authority/successor, command/receipt and source tuple. Bind the effect to the
   terminal operation key; missing/ambiguous subjects emit no guessed effect.
   Do not alter Coordinator cleanup, bridge confirmation or retry policy.
3. Add exact non-catalog-hashed successful-retirement admission linked to the
   validated original active A→B commit. Extend the existing role reduction for
   that atomic terminal entry only; retain immutable terminal-slot history,
   scope/generation/hash, replacement, terminal poison and ABA checks.
4. Use a separate successful-retirement proof mapping, not `CancelledReceipts`.
   Require the real terminal-bound observation to satisfy the linked provisional
   obligation. Forbid effects on the provisional commit or retained removal.
   Preserve A/B install proofs, rejected-confirmation compensation, exact B
   eviction and the saved service576 unresolved control.
5. Validate missing/wrong/duplicate effects and subjects, receipt/generation/scope
   mutations, foreign terminal occupancy and ABA. Require independent
   full/regenerated compact/shared-GUI and PS5/PS7 metadata parity. A's successful
   retirement must not silently close B or prove route completion.

Stop before implementing the separate ordinary-offer/probe-ACK finding described
in `file-transfer-probe-ack-provenance-validation.md`. No public API, protocol,
catalog/profile hash, schema, timeout, route policy or live retry is authorized
by this validation result.
