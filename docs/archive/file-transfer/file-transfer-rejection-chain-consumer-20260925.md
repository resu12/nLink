# Original rejection-chain consumer implementation

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, including the existing uncommitted
work. Implements the bounded diagnostic in
`file-transfer-rejection-chain-consumer-contract-20260925.md`; no runtime,
authority-catalog, retention, classifier or acceptance changes.

## Result

The explicitly enabled test consumer now reports
`RejectionChainStatus=matched_original_rejection_chain`. It starts with the
primary's exact original failure and the existing reciprocal receipt match,
checks the source/receive range and byte fields, then selects the same receiver
writer's consecutive mismatch and ignore records. All three require the recorded
rejection disposition, no authority effect, exact authenticated identity and
the same publication. Each record is independently resolved against both
completed authority views using the existing local publication resolver.

The ignore record's absent numeric range/count/byte fields are not manufactured;
its actual shared fields, including `chunk_index`, are checked. Transport bytes
and batch profiles are not incorrectly equated across peers. Duplicate or missing
companions, inconsistent fields and ambiguous bindings remain unproven.

The query uses the original private inputs and custody, adds exact companion and
publication/effect origins to the existing shared witness set, verifies normalized
full/compact equality and rechecks unchanged inputs. Existing record, boundary,
operation, 64-witness and cumulative work limits remain in place. No source
reader is reopened and no authority/recovery evaluation is added.

The opt-in is `NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER=1`, confined to test
support. A failed optional check leaves the previously verified yield result
intact and reports its own missing edge. It does not relabel the primary result,
claim recovery, or issue a general stale-safety certificate.

## Fresh verification

Artifacts are under `artifacts/publication-rejection-chain-20260925/`.

| Accepted case directory | Raw / full / compact records | Boundaries per view | Shared witnesses | Result on both hosts |
| --- | --- | --- | --- | --- |
| `7946356620fc43f4bdace7c93817dcc3` | 383 / 339 / 162 | 71 | 20 | Original reciprocal and rejection chains matched |
| `bb86b088b6904199ba965df9610bc3ff` | 384 / 339 / 162 | 71 | 12 | Rejection opt-in omitted without reciprocal prerequisite; forward result preserved |

Both cases execute genuine two-process writer histories. Each host's original
full/compact authority checks and parity pass with exactly three original
evaluations. The first edge remains `missing_exact_tuna_receive`, unrecovered;
the carrier remains `peerLineageStatus=not_evaluated`. Classifier, acceptance and
live flags remain false.

In the accepted positive case the rejection writer sequences are 220–222.
Their retained-source physical lines are 326–328; the receiver publication effect
is retained-source line 284 (writer sequence 201). These coordinate systems are
distinct: `WitnessOrigins` refers to the primary-owned retained source, not line
numbers in the larger merged raw writer capture. Every retained line is still
an exact original writer record. The combined 20 witnesses remain below 64.

The positive test includes 33 detached negative companion-query cases, a
full/compact disagreement control and shared-witness-limit checks, including
deduplication at the limit. The sparse ignore record is an explicit positive
control. These are structural query controls, not authenticated histories or
additional authority evaluations. Eight existing custody/origin/limit omission
checks remain inside each real consumer invocation. The separate genuine
optional-failure case verifies the preservation behavior rather than relying
only on inspection of the catch block.

Four focused xUnit cases pass, each using both PowerShell hosts:

- `results/rejection-chain-final.trx`: positive chain and optional omission,
  2/2 passed.
- `results/affected-regression.trx`: existing forward-only consumer and small-input
  observer, 2/2 passed. The former emits no rejection-chain field; the latter
  emits no Service-yield consumer output.

The regression histories are respectively
`artifacts/publication-service-yield-consumer-20260924/a608bc7046404afe9e35447ffa88613d/`
and `artifacts/publication-live-inputs-20260914/fe6e0a8182de4b0783c0bf21c00920e3/`.
Unchanged copy/order variants and aggregate lanes were not rerun.

The first development run already matched the original chain on both hosts but
failed the normalized-output comparison: PowerShell 5 escaped an apostrophe in
a generic missing-field exception differently from PowerShell 7. The matcher
now reports a stable explicit missing-field code. The failed receipt and original
artifacts (`937847a422254f5bba7436454a9857f0`,
`results/rejection-chain-development.trx`) remain preserved. No runtime workaround
was introduced. Local test builds used `-p:UsedAvaloniaProducts=` to suppress the
previously identified Avalonia build-telemetry target; existing compiler warnings
were not changed.

## Preservation and remaining boundary

Of 49 pre-existing dirty files, 45 remain byte-identical and four test/support
files have the intended extensions. All 11 pre-existing application-source files
remain byte-identical. The prior implementation's 142 pins have 139 unchanged
and three intended test-file extensions; the review's 26 pins have 24 unchanged
and two of those extensions. Previous reports, artifacts and seals are unchanged.
PowerShell parsing and `git diff --check` pass. Hashes and accepted results are
recorded in `artifacts/publication-rejection-chain-20260925/FINAL-VERIFICATION.json`.

Next is the bounded dispatch-observation interval/closure check described in the
review, with exact ownership and clearly limited claims. Matching this rejection
chain does not establish absence of all effects, payload delivery, durable writes,
recovery or acceptance. The ingress-versus-queued-dispatch distinction remains.
No portable package rebuild, live GUI/NKN test, commit or push was performed.
A new live-256 test is not required for this diagnostic milestone.
