# C1d: diagnostic native-input and selected-row binding

Date: 2026-09-14. Base HEAD: `ef78e9d7`, with the uncommitted A/B1/C0/C1 work.

`Resolve-FileTransferLocalNativeInputBinding` now checks original-source/writer
membership, four native input identities, and independently derived full/reduced
publication rows **within the objects supplied by its caller**. This is an offline,
bounded reference consumer. It is not a production producer or completion issuer.

## Implemented checks

- Exact request GUID and live C1c owner/token identity; exact run/scenario ownership
  in the supplied writer ledger and writer-start records.
- Contiguous writer sequences from one, matching ledger counts and clean existing
  chronology/identity metrics. Duplicate writer metadata is rejected. Non-event
  writer records remain valid; they are not assumed to have an `event` field.
- Ordinal writer/sequence membership and exact raw strings in the supplied source
  array. Duplicate source membership is rejected without deduplication.
- One to 64 explicit uses, each with an exact positive physical source position,
  writer, writer sequence, and strict UTF-8 raw-content hash, as in C0.
- Four exact `string[]` inputs and separate integral source-offset arrays. Each
  native input must be an ordered, unique subsequence of the source's writer-bound
  records. Zero/synthetic origins, mismatched bytes, wrong offset domains, missing
  selected uses and reordered/duplicate origins omit the whole result.
- C1c framed fingerprints are recomputed over these same checked native strings
  and compared with the still-owned capture token. A matching subset or a changed
  mutable array cannot borrow an earlier token's identity.
- Both supplied completed views must independently be complete, passing
  Snapshot V1 views with matching catalog/contract/boundary identity. Every indexed
  boundary's original native position matches its exact transition/hash/writer
  metadata. All observed-state alternatives remain in the local index.
- Existing A/B1 checks independently resolve each selected use and its one exact
  forwarding effect in each view. The shared C0 scalar row projection then requires
  exact canonical semantic parity and identical original publication/effect source
  positions, while retaining different full/reduced native positions.

The A/B1 resolvers now have an explicit native-role mode. It returns an empty file
path and a named native role, never a pretend filename for an in-memory array.
Their default file-based behavior and C0 V1 format remain unchanged. C0 and C1d use
the same row projection; no second interpretation of forwarding authority is added.

Success returns detached canonical JSON containing request/run/scenario labels,
four framed native descriptors, semantic rows, and distinct source/full/reduced
positions. The scope remains `local_publication_forwarding` and peer lineage
remains `not_evaluated`. No mutable private-view graph is returned.

## Explicitly unproved ownership

All three flags remain false on success and omission:

```
FileOwnershipVerified
EvaluatorInputOwnershipVerified
PublicationCompletionIssued
```

The source and writer arrays are caller-supplied. This consumer does not authenticate
their original file bytes, a frozen file handle, the retained parent, the worker,
or the association between an evaluator invocation and its returned object.
It does not recompute a supplied completed view's authority. Consistently forged
caller inputs or cloned view objects are not a new trust boundary. C1c `Owns=true`,
a matched diagnostic binding, and matching JSON hashes cannot issue completion.

The generated test supplies actual source files, the real streaming writer ledger,
and the actual compact writer's two evaluator calls. That fixture association is
evidence for the algorithm, not a production ownership capability.

## Fixed reference-consumer limits

| Resource | Ceiling |
| --- | ---: |
| Requested uses / writer owners | 64 each |
| Source + writer + four native input record admissions | 262,144 |
| Code units across those input admissions | 67,108,864 |
| Code units in one admitted line | 262,144 |
| Full + reduced boundary/effect rows indexed | 8,192 |
| Operation rows admitted across indexed boundaries | 4,096 |
| Operations per boundary / observations per key | 64 each |
| Canonical output | 262,144 ASCII characters |
| Cooperative elapsed time in the binding function | 2,000 ms |

The separate C1c verification owner also enforces its existing 250 ms active-API
budget. Any failed prerequisite or exceeded limit omits all rows. Cancellation is
checked through the original owner at loop/phase boundaries and before return.

The function retains bounded dictionaries of references to existing strings and
view rows, not whole private-view clones. It still **traverses and rehashes whole
supplied inputs** and reparses selected boundary/use/effect lines. Counters measure
input admissions and indexed rows, not every interpreter operation, hash transform,
allocation or repeated selected-row lookup. These limits do not prove production
allocation cost, hard deadline safety, or live 256 MiB input capacity. The clock is
cooperative: an admitted operation, scheduler pause or GC can overrun a checkpoint.

There is no file/network I/O, authority evaluation, transport, task launch, wait,
retry, report mutation or receipt mutation in the consumer. Module loading remains
outside the function. Neither this whole-input reference traversal nor module
startup may be inserted after primary report publication. Production wiring stays
disabled; the primary's existing deadline is unchanged.

## First remaining ownership edge and fix sequence

The current retained parents (`Invoke-RouteAcceptanceRetainedAnalysis` and
`Invoke-TunaGuiRetainedAnalysis`) pin a source path/hash/count and own an attempt
and launched process. Their request surfaces do **not** carry an already validated
writer ledger, explicit selected-use request, or a primary-owned completed-view
capability. The full writer's existing `fullSnapshotEvaluationHandoff` holds exact
input arrays and completed views locally, but is not a parent-bound export issuer.

The next implementation must connect those facts, not elevate C1d's status:

1. Supply the C1b request and frozen source/writer ownership through the actual
   parent/worker path. C1b's user-approved revision now selects exact reported
   failure witnesses inside the primary; its parent requests that fixed policy.
   Missing trusted ledger context must still omit;
   do not invent it from an optional payload or add an unbounded pre-launch scan.
2. Associate the primary's exact input assembly and returned full/reduced views
   while they are owned and alive. Prepare the bounded selected context during
   existing traversals, using C1d as a correctness oracle, not as a new post-report
   whole-input pass. Account for the actual integrated cost and cancellation.
3. Only then implement the one-shot completion issuer and separate parent-owned
   control channel, followed by the unchanged post-receipt adoption ordering and
   focused retained-parent lifecycle tests.

There is no reason to rerun historical authority evaluation or a new live 256 MiB
scenario for this reference-consumer change. Live validation remains premature
until the actual owned producer and focused correctness/lifecycle gates are ready.

Results: `artifacts/publication-native-binding-20260914/RESULTS.md`.
