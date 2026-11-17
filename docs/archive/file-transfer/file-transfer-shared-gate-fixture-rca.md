# C2h: retained shared-analyzer gate evidence

2026-09-15. Follow-up to C2g's three original-reducer baseline failures.

Fresh rebuilt tests retain their original Coordinator envelopes, generated input
lines, primary evaluation and GUI classification under
`artifacts/publication-shared-gates-20260915/0996d79900bd4fb5874c42c977427a7b`.
All three failures reproduce before fixture changes.

## First missing edges

The preparation-first and outbound retained-handoff GUI results do not report a
responder failure. They report `snapshot_authoritative_route_consistency_invalid`.
Deriving route consistency from the retained primary objects identifies missing
publication generation 1: the first recorded publication is generation 2, a
Tuna commit with a predecessor, not an initial publication. Both Core fixtures
start recording after their initial Regular runtime/publication setup. The
preparation-first stream even omits admission and pending-probe preparation.
The shared route validator correctly refuses a complete-lineage claim.

The inbound retained-handoff input also contains a real
`FallbackCheckpointRepairScopeContinuity` acceptance at generation 29 but stamps
every line with the original `snapshot-authority-v1` catalog. The same exact
operation fails the original catalog and passes the pinned checkpoint-continuity
catalog. This is an input capability mismatch, not a malformed runtime receipt.

## Bounded fix plan

Capture real Coordinator StartTransfer/publication/setup envelopes, with the
Coordinator ID, data-session identity and intentional test generation seeds set
before capture starts. Do not manufacture earlier snapshots or rewrite route
publication generations. Keep the original handoff and missing-receipt controls.
Select the existing pinned checkpoint-continuity catalog only for generated
inputs that contain that marker; do not weaken the base catalog.

Run these three focused tests first and inspect any newly exposed first edge.
Production Coordinator, shared validators, runtime guards and live verdicts are
unchanged. Artifact retention uses the existing test artifact-root setting.

## Implemented correction

The preparation-first fixture now captures StartTransfer, initial publication,
admission and pending-probe decisions before its original six handoff boundaries.
The combined stream explicitly asserts adjacent state generations and hashes.
The retained-handoff fixture now seeds its intentional generation-domain controls
before StartTransfer and captures that start plus its actual initial publication.
All later recovery handoffs, independent settlement, route commits and checkpoint
receipts still come from the original Coordinator workflow.

The inbound checkpoint marker selects the existing pinned continuity catalog;
the base catalog remains pinned and is explicitly tested to reject the marker.
Both scripts also derive a negative route check from a copy of the already
evaluated primary object with its initial publication omitted. It must fail.
Neither this negative control nor artifact retention invokes an additional
Snapshot authority evaluation. The existing per-case full/compact/GUI evaluation
flow, receipt-mutation controls and fail-closed GUI assertions are preserved.

The first corrected run, `test-results/shared-gates-prefix.trx`, passes all three
previously failing cases. Complete positive histories have 10 boundaries for the
preparation-first case, 34 for inbound retained handoff and 35 for outbound.
The retained-handoff failures that initially appeared to concern a fallback
predecessor also clear with the missing initial prefix restored; no additional
route or receipt validator change was needed.

The final gate passes **9/9 tests**: each shared-analyzer case on both PowerShell
5 and 7 plus the three directly affected Core cases. The positive, identical
copy, missing acceptance/closure, shuffled-operation, malformed retained receipt
and conflicting-copy checks all pass. The explicit missing-initial-publication
and unsupported-base-catalog controls also remain fail-closed.

This closes the three C2g regression blockers. It does not certify the whole
accumulated worktree or the live route cycle. No runtime/analyzer production
change was made in C2h, and no live run, full matrix or commit was performed.
Detailed results: `artifacts/publication-shared-gates-20260915/RESULTS.md`.

## Next bounded step

Integrate the verified service-owned second-Tuna publication/disposition with
the existing genuine two-writer delayed-envelope capture. Keep the single-writer
and missing-service-transition negative fixtures intact. Use the original primary
diagnostic to reach and test the exact-range Tuna resend/receive predicate;
rejection safety alone is not payload delivery or cross-peer lineage proof.
Do not reclassify C2a or start another live-256 run automatically.

The C2i continuation is recorded in
`docs/file-transfer-service-ingress-integration-rca.md`. The original primary now
reaches the missing-Tuna-resend predicate in the separate controlled service case.
