# Native content selection budget RCA and next fix plan

2026-10-02. The new fixture-only result diagnostic identifies the scale
failure: the original cumulative active-work clock exceeds its 20-second
producer allowance during the initial input validation, before the primary
selection callback runs. The scale gate remains failed. Thirteen focused
controls pass, and no limit, authority check, or acceptance rule changes.

## Diagnostic implementation and verification

The worker emits one result record after its existing selection call and
before the unchanged assertions. It reads only the returned result DTO and
the already counted callbacks. Null results are explicit. Status and reason
text are capped at 256 characters with a truncation flag; the fixed schema
fits within 4,096 characters. No raw lines, paths, payloads, hashes, private
arrays, or custody capabilities are exported.

The helper does not perform selection, inspect currentness, mutate a clock,
or reread the source. Its witness count comes from the returned detached
array, not a native query. Removing the two diagnostic insertions reproduces
the original worker body exactly. The acquisition primitive, producer limit,
parent waits, and production files are unchanged. The original call stops
the cumulative clock before the formatter runs, so formatting does not
contribute to the measured rejection.

Both formatter controls fail before implementation and pass afterward on
PowerShell 5.1 and 7. They cover null and returned results, bounded text,
selective field retention, and a guard that throws if custody is inspected.
The eleven existing small acquisition controls also pass, preserving valid
selection, copied inputs, omitted and excessive witnesses, record and time
exhaustion, stale ownership, and mutation rejection. No unrelated aggregate
lane is rerun.

## Fresh scale evidence

One normal scale fixture runs without the source timing profile. Its
returned result is `unproven`, with `FirstMissingEdge=content_input_budget`,
zero witnesses, zero callbacks, and no truncated diagnostic text.

| Cumulative bound | Observed value | Unchanged ceiling |
| --- | ---: | ---: |
| Record visits | 617,239 | 2,097,152 |
| Code-unit visits | 208,749,911 | 1,073,741,824 |
| Active milliseconds | 20,001 | 20,000 |

The source read and event conversion return after 57,690 ms of wall time;
selection returns after a further call interval of 1,886 ms. The worker
records its result at the selection boundary and completes cleanup at
65,289 ms from the stage clock start. These wall intervals are not active
work measurements. The parent reports `Deadline=false`,
`Phase=validate_output`, and a 90,000-ms attachment barrier. Both streams
complete without truncation, retaining 23 ordered stage records and the
single selection-result record.

Earlier original processing and semantic copying succeed. Local processing
takes 5,357 ms, shared cumulative work reaches 7,624 ms, and the semantic copy
returns at 10,255 ms cumulative work. Semantic and analyzer outputs each
contain 102,218 lines and 208,326,025 bytes, with matching actual hashes.
Source completeness, original evaluation recording, pair issuance, and the
default-off check pass before the failing enabled selection.

## Cause and bounded fix plan

The adapter deliberately shares prior cumulative native work and checks the
producer allowance during its full original-event membership scan. It calls
that scan before invoking the primary's selector. The returned reason,
elapsed work above the ceiling, and record and code-unit counts below their
ceilings identify active-work exhaustion. This is a correct fail-closed
budget rejection, not evidence of a transport, content-integrity, or route
authority failure. It also explains why a longer parent wait cannot make
this acquisition pass.

The remaining gate requires an in-budget prepared selection, not merely a
fast rejection of every large input. The artifact does not measure the exact
inherited work at selection entry, the completed scan prefix, or the cost
of each native validation family. It therefore does not establish a safe
replacement limit or prove that a small increase would finish acquisition.
The existing reviewed contract explicitly keeps the 20-second producer cap
and prohibits borrowing the evaluator's separate 60-second allowance.

Recommended next step: profile the initial membership scan under the same
cap, retaining passive entry and rejection snapshots of inherited work and
validation costs. Then review a surgical algorithmic reduction against the
original input, before-and-after mutation, cancellation, identity, and
charging contract. Do not remove native checks, cache currentness, reset
elapsed work, or raise the global producer constant to make the fixture pass.
If the required work cannot fit, prepare an explicit scoped contract revision
with measured cost and affected-consumer inventory before implementation.
Only the reviewed fix should trigger focused regressions and one targeted
scale rerun; production integration and live validation remain later gates.

The [verification receipt](../artifacts/native-selection-result-20261002/verification.json)
pins the fresh result and source checkpoint, preserves 81 prior scale
artifact pins and 99 historical live artifact pins, and keeps old verdicts
unchanged. No live GUI/NKN run, portable rebuild, full matrix, commit, or
push occurs.
