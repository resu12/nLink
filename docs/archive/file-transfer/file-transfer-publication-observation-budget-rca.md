# C2b: selected publication lookup and unrelated observation buckets

2026-09-15, based on `4a9a1520bfac21b7d11f4f9bfd24458a2cedeacd`.
This is an offline diagnostic fix, not a runtime or acceptance-policy change.

## Evidence and root cause

C2a's original full Snapshot summary contains 2,159 boundaries. Three sender
state buckets have 440, 170 and 150 observations. The first reported receive
witness references receiver coordinator `fte1-f711903bf6c567f644c05a3a`, observed
generation 186, which has one observation. Its published boundary and forwarding
effect are present in the original retained evidence. These facts were read from
the original reports/records, without replaying their views or reevaluating them.

`Resolve-FileTransferPrimaryPublicationWitness` previously populated lookup maps
for every coordinator/boundary and coordinator/observed-generation key. Any map
bucket above 64 caused `primary_witness_observation_budget`, including buckets
that the selected witness could never use. Thus C2a's original inputs contain a
deterministic obstruction if that resolver reaches index construction. C2a did
not persist its omission stage, so this does not establish the earliest stage
that actually omitted its companion.

A fresh fixture reproduces the obstruction through the existing offline whole
wrapper, GUI writer handoff and original retained primary. It uses the real
Coordinator, snapshot emitter, security codecs and logger. Sixty-five genuine
rejected actions preserve an earlier state generation; a later publication owns
the seeded wrong-transport send observation. Original Snapshot authority passes,
the primary selects that send failure, relay/adoption are reached, and the old
witness builder omits specifically on the unrelated bucket. The primary still
performs its original three evaluator calls and preserves its FAIL.

This fixture isolates diagnostic capacity. It is not a new live transfer, a
receive-guard reproduction, or proof that C2a's 13 unresolved records are false
positives.

## Bounded fix and controls

Retain only the selected use's exact publication-boundary and observed-generation
lookup keys, derived from its already verified source record. Every original
boundary still receives the same origin, verdict, field and operation checks and
consumes the existing global work budgets. Preserve every alternative for the
selected keys; the 64-alternative bound remains unchanged. No deduplication,
first/latest selection, new evaluator, input reread, runtime authority or budget
increase is introduced.

The paired negative fixture places the same 65 observations at the selected
generation. It must still omit with `primary_witness_observation_budget`, produce
no companion, and retain the original primary failure. The positive fixture must
reach actual one-shot adoption and non-overwriting persistence on both PowerShell
hosts, not merely prepare an in-memory row.

Verification artifacts and the next bounded step are recorded in
`artifacts/publication-observation-budget-20260915/RESULTS.md`. C2a's sealed
artifacts and FAIL remain unchanged. No additional live run is authorized by this
diagnostic result alone.

Final focused verification passed 5/5 tests, including both PowerShell hosts and
all 35 witness controls per host. One earlier post-fix attempt prepared/issued
the witness but omitted at the separate parent-adoption ownership/time guard;
that omission did not recur in the instrumented focused rerun. Its cause remains
unestablished, and no parent guard or budget was changed.
