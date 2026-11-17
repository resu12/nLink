# Initial-Tuna fallback: pre-live source review

2026-09-21. Reviewed accumulated changes against
`faf838d7e39e2ffbb422e8ff0b261c58e85330f1` on `v0.7.0`.

## Result

No new actionable correctness blocker found. The reviewed scope is ready for a
local checkpoint and an isolated Release portable rebuild. This is not a live
acceptance or release approval. No production or test changes were needed during
this review; only this record and the contract status were added/updated.

## Boundaries checked

- Initial Tuna is an explicit first-publication source, not a default commit or
  an uncommitted Regular activation. Core revalidates publication, owner, leg,
  authenticated agreement, data-session identity/generation and absent history.
- Source hold capture, physical drain and durable receipt still precede exchange
  issuance. Delivery and settlement plus exact peer proof precede transition
  prepare/drain/commit. Reentry retains the same command; stale callbacks cannot
  publish fallback or revive a replaced source.
- Initial-source currentness survives only its exact first Prepared/Draining
  transition. Proof matching is separated from currentness to avoid recursive
  validation. Missing/invalidated exchange, foreign authority and terminal or
  historical source state reject the transition.
- Service carries the Core descriptor in peer and pending-write bindings and
  copies it into transition proof only from the completed Core peer boundary.
  Legacy peer frames remain commit-only. The adapter's two exact local-stop
  reasons capture evidence only; remote/unknown reasons remain rejected.
- Committed-source command-ID encoding and successor lineage are unchanged.
  The analyzer's zero-retired-commit exception is limited to the first successor
  of a validated ordinary initial Tuna publication. Exact owner/publication
  retirement, transition receipt and effect joins remain mandatory.
- Test-only read gating and fixture splitting preserve authority assertions.
  Four-byte joined payload tests establish offline completion/SHA, not live
  transfer performance. Injected-write cases establish bounded drain/currentness,
  not end-to-end payload completion.

## Evidence audited (not rerun)

The exact-name union of the retained final TRX files matches all **103 selected
cases**, with zero missing. Counts overlap and must not be summed:

- `affected-final-gate.trx`: 85 passed results, run outcome **Failed** after an
  abort; not a completed aggregate pass.
- `joined-bounded.trx`: 7/7, completed after the committed joined fixture fix.
- `final-remainder.trx`: 13/13, completed.

Source: `artifacts/initial-tuna-fallback-service-20260921/RESULTS.md` and
`artifacts/initial-tuna-service-final-selected.log`. The actual joined evidence
reports artifact completeness, independent full/compact parity and route
consistency passing; it is fake-network evidence, not a new live evaluation.
Earlier focused Core/golden-vector and transition verification remain in their
separate checkpoint reports. `git diff --check` passes.

## Next boundary

Checkpoint the explicit reviewed file set locally, without pushing. Rebuild the
portable and bridge/sidecar into a new artifact directory using `-LocalOnly`;
verify package contents and retain hashes/provenance. Then validate only one
initial-Tuna switch-off scenario with strict escalated live preflights. Do not
bypass a failed preflight or automatically run live-256/full matrix. The separate
live-256 rejected-ingress, Regular inbound terminal proof and parent parity-mirror
items remain unresolved by this review.
