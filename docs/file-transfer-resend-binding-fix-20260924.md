# Second-Tuna recovery: bounded writer/publication binding fix

2026-09-24, branch `v0.7.0`, HEAD
`5f7a13dc047fa50da0702713db92b0903cb9e7c0`, plus the previously verified
no-peer supersession changes. Implements the bounded contract in
`file-transfer-resend-contract-review-20260924.md`.

## Implementation

The shared rejected-ingress matcher now applies these additional predicates only
when the selected receiver transition is from `post_tuna_fallback_v6`:

- A Tuna resend must use the matched Regular source writer and follow its
  positive writer sequence.
- A Tuna receipt must use the selected receiver writer and exact receiver-local
  epoch/runtime/terminal/leg/publication/commit tuple, with consistent Tuna target
  route, protocol and transport fields.
- The receipt must follow the selected receiver transition in retained input
  order and satisfy transition < receipt < rejected receive in receiver-local
  sequence. The upper bound is the rejected receive, not its later mismatch log.

The existing pre-rejection input window, range/bytes/chunk-count checks,
first-activation path, rejection-chain requirements, ambiguity handling and
diagnostic reason/variant/explanation mapping remain unchanged. Sender-local
generation numbers are not compared with receiver-local numbers. A valid exact
candidate still qualifies alongside unrelated candidates.

This corrects false recovery accounting; it does not introduce runtime behavior,
retry authority, a new peer-lineage join or a general stale-packet exemption.

## Regression coverage

The diagnostic matrix grows from 24 to 69 cases. It covers the four reproduced
false matches, missing/wrong receipt fields, missing/zero/out-of-order sequences,
retained-input ordering, duplicate evidence, unrelated additional candidates and
different sender/receiver generation numbers. Full/compact reparsing parity,
input immutability and existing diagnostics remain asserted.

The intended positive full/compact fixture now places receipt after publication.
Its former ordering is retained as a negative case. The independent GUI shadow
test preserves its historical baseline file and adjusts only the two expected
recovery outcomes affected by the reviewed receipt bindings. Original GUI-primary
results and all other characterized fields remain pinned.

Evidence is under `artifacts/resend-binding-fix-20260924/results/`:

- `red.trx`: expected failure before the production fix, on the foreign receiver
  case under Windows PowerShell. This red run stopped before reaching PowerShell 7.
- `green-attempt-1.trx`: the matrix passes all 69 cases under PowerShell
  5.1.26100.9549 and 7.6.6, with identical normalized results.
- `affected-regressions.trx`: six passing full/compact, shadow, generated ingress
  and shared-module regression cases, after rebuilding the test assembly.
- `generated-integrations.trx`: five passing generated two-writer cases, each
  checked on both PowerShell hosts: exact Tuna payload, missing receipt, wrong
  range, Service ingress and later durable fallback. Exact evidence still
  recovers; the negative cases retain their existing missing-proof reasons.

Final result: 12 passing xUnit cases, including the 69-case matrix on each host.
The expected red run is separate, not included in that passing count. No
stack-size override, live/NKN run or generic aggregate suite is used. The final
diff review and `git diff --check` found no additional issue in this bounded slice.
`artifacts/resend-binding-fix-20260924/FINAL-VERIFICATION.json` pins the source,
documentation, test receipts, generated captures and unchanged prior seals.

## Preservation and boundary

The previous review and no-peer seals remain byte-identical. Of their active-file
pins, only this fix's expected matcher/test sources changed: 3 of 13 review pins
and 1 of 16 no-peer pins. All no-peer implementation and test receipts remain
unchanged. The live seal remains byte-identical; 134 of its 135 pins are unchanged,
with only the previously implemented `CoordinatorSnapshotEvidence.ps1` change.

No historical authority evaluation or acceptance reclassification was run. The
sealed live verdict remains **FAIL** with its original 62/43/19 distinct/recovered/
unresolved accounting. This fix does not establish whether any of those 43
recoveries used the old binding gap and does not resolve the 19 existing failures.

The strict pre-rejection recovery contract remains in force. A later resend,
fallback completion or final SHA cannot substitute for it. No commit or push is
part of this step; review the accumulated analyzer changes before checkpointing.
