# C2g: consumed post-fallback responder observation

2026-09-15. Continues C2f using fresh deterministic service evidence only.

## First missing proof edge

The fresh C2f fixture still produces a complete second-Tuna commit and applied
disposition, followed by two state-neutral rejections of the original responder
observation. Both PowerShell hosts report one unresolved handoff incident. The
clean fixture passes. Baseline: `artifacts/publication-second-tuna-handoff-20260915/test-results/handoff-baseline.trx`.

This is not a missing outer service callback. The generic inbound responder
handler only dispatches by transition mode. The initial handler records an
accepted `ObserveRuntimeUnlockResponderPeerProbeHandoff`; the post-fallback
handler instead adopts the actual Coordinator
`BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff` decision. That
decision contains a non-mutating `peer_acceptance_attempt` and an installed
`handoff_current` operation. The fixture follows this production path.

The consumed-observation reducer only selects the initial handler's decision
kind. It also requires matching command/receipt identities across acceptance
and rejection. The producer explicitly includes decision kind in those IDs, so
a Bind receipt cannot equal an Observe receipt. The observation fingerprint,
handoff authority/generation, source scope and paired proofs do match. In the
fresh receiver, Bind is generation 32, preparation 33, drain 34, commit 35,
applied disposition 36, and both state-neutral rejections remain at 36.

## Bounded fix contract

Keep the initial-route reducer unchanged. For `post_fallback_v6_paired` only,
recognize exactly one accepted Coordinator binding immediately preceding the
preparation by generation and state hash. Require its accepted attempt and
installed current authority, exact lifecycle identity (writer, Coordinator,
session, transfer, direction, handoff authority, observation and paired proof
scope), matching observation fingerprint/generations, and validated receipts.
Command and receipt IDs remain stage-specific; do not rewrite emitted records.

Keep the existing exact preparation, drain, commit, publication, applied
disposition and post-disposition interval checks. Missing, duplicate or altered
binding/receipt/scope/commit evidence must not consume the rejection. This is
diagnostic convergence, not a new runtime authorization or payload recovery.

Verification: first reproduce the failing all-pass assertion on a newly
generated replay stream; then run positive and mutation controls against the
already evaluated fresh boundaries, with full/compact/shared-GUI parity on both
PowerShell hosts. Run only directly related existing responder regressions.
The runtime rejection guard, exact-range Tuna resend/receive predicate,
historical live verdict, catalog and primary evaluation count stay unchanged.

## Implementation and verification

Implemented only in the consumed-observation reducer. The new branch also
compares the immutable observation payload, including the pending precommit and
probe timestamp fields not present in the lifecycle grouping key. Attempt,
installed-authority and rejected-observation receipt summaries must match their
own exact typed receipts. Stage-specific IDs are not treated as interchangeable.

The clean service stream passes all 40 boundaries; the replay stream passes all
42 while retaining both original rejected observations. Full, independently
selected compact and shared-GUI inputs agree on both PowerShell 5 and 7. There
are 59 direct reducer controls over copies of the already evaluated boundaries,
including original positive consumption and negative writer/scope/receipt,
publication, disposition, immutable-observation and state-mutation controls.
These controls add zero authority evaluations and do not mutate input records.

The final selected run is **4 passed, 3 failed**, not an all-pass regression
gate. The four passing cases are the clean/replay service evidence, existing
exact post-commit duplicate reducer regression, and existing second-Tuna
rejected-ingress recovery-contract regression. The other three failures are:

- `FileTransferOps_PreparationFirstResponderUsesSharedSnapshotEvaluation`:
  `GUI reopened completed responder`.
- `FileTransferOps_RetainedHandoffReceiptUsesSharedSnapshotEvaluation(Outbound)`:
  `GUI rejected the exact settlement`.
- The inbound retained-handoff case:
  `snapshot_checkpoint_repair_continuity_operation_invalid:87`.

For isolation, only this reducer was temporarily restored to its original HEAD
definition, leaving the rest of the accumulated workspace unchanged. All three
tests reproduced the same failures. The verified C2g reducer was then restored
exactly. This establishes that C2g did not introduce these failures; it does not
establish their cause or certify the full accumulated worktree.

Next gate: retain fresh outputs from those three tests, inspect the first
checkpoint-continuity failure and the complete GUI classification objects, and
distinguish stale test-input assumptions from a production evidence defect.
Fix only the first proven edge; do not relax checkpoint validation or replace
their GUI assertions with unconditional acceptance. Two-writer service/payload
integration remains downstream. No live run, commit or historical reevaluation.

Detailed run ledger: `artifacts/publication-second-tuna-handoff-20260915/RESULTS.md`.

## C2h continuation

The three regression failures above are now resolved; their historical results
are preserved. Fresh retained evidence showed incomplete fixture publication
prefixes and a checkpoint-continuity catalog mismatch. Only fixture generation,
test catalog selection and diagnostic retention changed. The final affected gate
passes 9/9 tests across PowerShell 5/7 and Core. See
`file-transfer-shared-gate-fixture-rca.md`. The next open work is the controlled
two-writer service/payload integration, not a live run.
