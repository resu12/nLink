# Retry confirmation rejection uses a stale rollback state

## Status and scope

This is the next independently exposed boundary from the bounded joined-peer
test phase. It is reproduced in a snapshot-enabled service test, not a new live
failure or a reinterpretation of historical helper11350. The approved narrow
compensation is now implemented; focused evidence verification is recorded below.
Helper11350 remains unresolved `1/1/0/1`; its classification, quartets and
acceptance policy remain unchanged. Snapshot continuity stays fail-closed.

The successful joined fixture now exchanges actual Coordinator-issued peer
yield frames, queues the service-created paired request, and completes exact
authorization, staging, receipt issuance, commit and confirmation. Only the
sender installs and consumes its paired release. A mismatched install response
is rejected without an adapter installation or release consumption.

## Reproduction and first failing edge

The test-only bridge returns false from confirmation, after accepting the exact
Coordinator-issued receipt proposal. It does not modify Core state.

Retained development evidence:
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/disposition-phase-validation/retry-bridge/negative-02.log`
and `negative-02.trx`, writer `p5772r42a7dafcbfd14e6d`.

| Sequence | Event | Coordinator generation |
|---|---|---|
| 156 | Exact retry authorization | 5 -> 6 |
| 158 | Stage accepted | 6 -> 7 |
| 161 | Install receipt issued | 7 -> 8 |
| 163 | Install commit adopted | 8 -> 9 |
| 164 | Exact paired release consumed | 9 |
| 165 | `install_committed` effect | 9 |

Confirmation then returns false. The outbound service calls
`InvalidateRuntimeUnlockRetryDispatchPendingAttempt(issuance.State, ...)` and
attempts to adopt its envelope. That decision starts at generation8, while the
emitter tail is already generation9. The emitter correctly throws:

`Coordinator snapshot envelope is not contiguous with the emitted stream tail.`

The exception occurs before normal rejection notification and before a rollback
is adopted. Adapter install count remains zero, but Core retains the accepted
installed attempt and consumed-release history. No rollback snapshot is emitted.
The inbound branch had the same source pattern. The replacement regression now
covers both directions through the joined service fixture.

Relevant production sites at this revision:

- `SessionFileTransferService.cs`: outbound commit/adoption6623 and stale rollback6643;
  inbound commit/adoption6745 and stale rollback6765.
- `SessionFileTransferService.CoordinatorSnapshots.cs`: validation precedes
  state adoption; `ValidateEnvelopeTailLocked` rejects the old generation/hash.
- `FileTransferCoordinator.cs`: commit removes `PendingAttempt`, installs the
  receipt, and consumes the paired release. The pending invalidation API cannot
  invalidate that now-installed attempt.

The original regression was explicitly named
`JoinedFallbackYield_RejectedConfirmationExposesStaleRollbackEnvelope`. It
asserts the exception, no adapter activation, the exact retained Core install,
the unchanged stored emitter tail, and no route/progress grant. It
characterized the defect, not successful confirmation-failure recovery. That
development evidence remains retained. The earlier failed full-context hash
assertion in `joined-final-03.trx` compared different hash domains: a raw graph
fingerprint and the persisted transition-chain fingerprint. Those are not
expected to be equal after a mutation; this assertion did not establish another
state-adoption defect. The new tests compare raw reconstructed/returned graphs
separately from the stored/emitter chain markers.

## Narrow follow-up implementation plan

1. Add a Coordinator-owned, exact installed-but-unconfirmed compensation path
   authored from the current adopted state. Bind it to the installed authority,
   request, receipt, generations and complete source/data-session scope. It must
   reject a changed receipt, an alias-bound or dispatched attempt, a replacement,
   conflicting installed authority, or a stale callback. Duplicate compensation
   must be state-neutral. Do not rewind to `issuance.State` or disable continuity.
2. Preserve the committed install as historical evidence while terminally
   invalidating only the exact unconfirmed current installation. Preserve the
   consumed paired-release receipt/reservation history; do not silently turn it
   back into reusable paired authority. Any proposal to reauthorize that release
   needs its own explicit Coordinator contract. Preserve unrelated operations
   and any predecessor rather than treating this as whole-scope replacement.
3. Use the same current-state compensation in both service directions and clear
   only the matching transport proposal through the existing exact rejection
   notification where supported. Verify the real transport, not just this test
   bridge. Do not call confirmation before Coordinator installation: the current
   confirmation contract validates that accepted install.
4. Replace the characterization with desired successful-failure handling tests:
   no exception, one current-generation compensation, no active failed install,
   no adapter activation, no second release consumption/reuse, unchanged route,
   ownership and progress, and exact predecessor preservation. Add inbound,
   duplicate, mismatched receipt, scope replacement, terminal and ABA controls.
5. Run focused Coordinator/service/Infra retry tests and one joined contract lane.
   Check complete generation/hash continuity and exact effect ownership in the
   generated evidence. Stop on another independent edge; do not rebuild or run
   live as part of this compensation slice.

Simply changing the argument to current state is insufficient because the
pending attempt no longer exists. The full-scope invalidation API is not a
drop-in replacement either: Infra restricts its reasons and expects matching
operational scope, not necessarily an unconfirmed proposal. Do not relabel the
failure as data-session replacement or broadly relax that matcher.

## Implemented correction and focused validation

The accepted install commit now supplies an internal, nonserialized compensation
context. It binds the exact receipt, original predecessor, Coordinator identity,
committed generation, chained and raw graph fingerprints, complete boundary,
and expected compensated graph. Both service directions reconstruct the live
Coordinator state under the service gate after confirmation returns false.
Neither uses the earlier issuance state.

The Coordinator rejects changed scope, generation, receipt, pending authority,
alias binding, dispatch or predecessor lineage. Accepted compensation advances
once, terminally invalidates only the failed install and restores only its exact
original predecessor where present. Duplicate compensation is state-neutral.
Consumed release/reservation history and unrelated state remain unchanged. The
existing real-transport rejection path clears only the matching proposal; no
transport production change was needed. This API remains confined to the
synchronous false-confirmation branch, not a late post-confirmation callback.

The shared evaluator admits the exact new decision through a separate predicate,
without changing the frozen catalog hash. It requires the immediate validated
install commit and exact inverse operation transitions. A pre-existing
MissingDispatch terminal is resolved through its exact retained operation
history. Compensation emits no authority effect and cannot replace any original
stage/install/settlement effect requirement.

Focused validation is green for **29 distinct test cases**, using the latest
result for each case across `runtime-02.trx`, `evidence-04.trx` and
`evidence-05.trx` in
`artifacts/validation/consolidation-bounded-20260907/rca-remaining-stall/confirmation-compensation`:

- Both joined service directions, current/active/settled predecessor controls,
  duplicate/stale/mutation checks, and real Infra proposal cleanup pass.
- First-install and MissingDispatch-predecessor Family-2 evidence pass strict
  validation on Windows PowerShell 5.1 and PowerShell 7, with independent
  full/compact evaluation and shared-GUI boundary parity. The original
  MissingDispatch incident remains unresolved; compensation does not recover it.
- Missing original install effects, forged compensation effects, competing or
  mutated history, and removal/restoration controls remain fail-closed.
- Release build passes with zero errors and 221 existing warnings. Modified
  PowerShell parsing and `git diff --check` pass. Development failures remain
  retained; the last seven-case affected rerun passed without failures.

This is family-specific evidence coverage, not an all-family joined route-cycle
or live acceptance claim. The subsequent narrowly approved Family-2 catalog fix
now admits the exact active-predecessor `Superseded` commit and compensation
inverse. Runtime restoration and the subsequent scoped cancellation/role-reducer
fix pass; the narrowly witnessed old-B-terminal eviction correction now also
passes its24-case dual-host continuation matrix. Complete successful-confirmation
active-successor evidence is not yet established. No whole report, package
rebuild, preflight, live run or commit was performed in these slices.

## Remaining joined proof

Safe confirmation failure and exact active-predecessor catalog admission are now
covered. The scoped cancelled-settlement/role-reducer fix now preserves open A
at576 with cross-host/full/compact/GUI parity. The old-B-terminal installation-effect
requirement is now corrected and tested
([implementation and validation](file-transfer-retry-invalidated-terminal-eviction-effect-rca.md)).
Next capture the successful-confirmation retained-to-terminal Superseded
emission/catalog path separately; then retain actual offer/answer
provenance and join the real probe/ACK/confirmation path. Only then advance the
sender commit, old-source request rejection and receiver handoff fixture.
No seeded route proof or fabricated receiver observation may supply that edge.

The separately requested validation is now complete and stopped on two gaps:
[successful-confirmation emission/catalog](file-transfer-confirmed-supersession-emission-validation.md)
and [probe/ACK prerequisite and joined-provenance limits](file-transfer-probe-ack-provenance-validation.md).
Successful cleanup passes its state checks, but captured terminal retirement56
has no settlement effect and remains catalog-invalid. The standalone ACK test
fails before answer emission on a non-runtime-offer applicability guard. Neither
gap is fixed or waived; no joined end-to-end acceptance is claimed.
