# Primary-reported failure witness selection

Date: 2026-09-14. Base HEAD: `ef78e9d7` plus uncommitted local evidence work.
The user approved changing C1b's pre-launch explicit selection to exact witnesses
selected by the primary analysis, without another authority evaluation.

## Implemented scope

`Invoke-FileTransferRetainedAnalysis` now accepts an optional empty
`LocalFailureWitness` reference. Only compact analysis of one source, TailMinutes
zero and no synthetic augmentation forwards it to the compact primary writer.
Both live retained parents already choose compact analysis, but neither supplies
this new reference yet. There is no CLI switch, live export, or full-report-path
selection in this slice. Existing callers retain their original behavior.

The compact writer uses its already chosen `firstCompactProofChainFailure`, the
same event/class/reason which populates its first-proof report fields. It does
not change candidate generation, filtering, ordering or classification. The
selected event itself must contain a supported `publication_use_kind` and V1
capture marker. An unsupported first failure omits; a later convenient use is
never substituted. No failure means no selection. Initial cardinality is zero
or one; C1b's envelope ceiling stays 64, not permission to truncate or infer uses.

`Resolve-FileTransferPrimaryFailureWitness` checks reference identity and unique
exact physical location in the primary's existing event-name bucket. It checks
the original raw event/writer fields, rejects duplicate fields and noncanonical
writer sequences, and hashes only that exact bounded raw line. Physical source
line, parser sequence and reduced Snapshot offset are not interchanged. It
returns detached scalar selection facts with the original report class/reason.

Limits: one event-name bucket of at most 4,096 records, one raw line of at most
262,144 code units, at most 256 structured fields, and a cooperative 250 ms budget.
Path/class/reason/event/writer strings are separately capped. The raw UTF-8 hash
buffer is at most three bytes per allowed UTF-16 code unit; invalid Unicode omits.
Counters describe bucket visits and input length, not every parser allocation or
character visit. This does not establish a hard deadline or live-input capacity.

Module loading occurs before the primary evaluations; bounded selection occurs
before primary report publication. The final writer/outer handoff only assigns
the prepared scalar reference after their original reports finish. Selection,
loading or output-reference conversion failure omits without changing primary
reporting. A later primary failure leaves the outer reference empty and preserves
the original exception. No serializer, input scan, wait or I/O is added after
report publication.

## Selection is not completion or authority

This is a private in-process convenience reference, not an immutable/authenticated
request or completion capability. A direct caller can supply a consistently
fabricated index; the selector does not authenticate a retained parent, frozen
file, original writer ledger or evaluator invocation. The real primary call site
establishes where its provisional selection came from, not export eligibility.

Publication completeness, exact source/writer membership, native fingerprints,
independent A/B1 full/reduced rows and parity still need the prepared producer
context. Selection does not claim that a failure is explained, recovered, stale,
accepted or safe. It cannot authorize route/runtime effects. A saved summary or
this detached object must never become an alternative completion issuer.

## Verification and RCA

Focused controls run on PS5.1 and PS7. Actual primary workflow preservation runs
on Windows PowerShell, matching both live parents, across normal/optional success,
selector failure, assigned/incompatible references and later primary-report
failure. Four report files compare byte-for-byte; the compact summary compares
after excluding only its two existing elapsed-time fields. The synthetic LegacyV0
fixture's original three evaluator calls remain unchanged; no optional evaluation
is added. This is not a valid publication-authority fixture.

A separate real service/transport/logger fixture supplies eight exact captured
uses. Both PowerShell hosts retain their original source/writer/content identities
and independent A/B1 forwarding matches, with exactly two original Snapshot
evaluations and unchanged completed views. Failure labels in this successful
transfer are explicitly synthetic selection controls, not real failure verdicts.

An initial helper import of the GUI ledger module redefined the primary evaluator
and its caches. That import was removed; the selector loads no other modules and
uses a bounded duplicate-aware parser for the selected line only. An initial
whole-workflow PS7 check also exposed the pre-existing read-only `$IsWindows`
collision in `Write-FileTransferRetainedAnalysisStageJson`. That unrelated writer
is unchanged; PS7 tests cover the selector and generated evidence, not the whole
primary reporting workflow. These limitations are not hidden by test shims.

Results: `artifacts/publication-failure-witness-20260914/RESULTS.md`.

## Next ownership edge

The [C1f source-only request](file-transfer-local-retained-source-request-contract.md)
now carries the parent-held frozen source into its exact worker through a
default-off internal seam. The remaining offline step is trusted writer-ledger
ownership and association with the completed full/reduced contexts. Only the
fully bound primary owner may issue independent completion
metadata for a V2 carrier and the parent's unchanged post-receipt adoption.
C1a's pre-known-digest slot and C1d's supplied-object oracle remain insufficient
for that production gate. No new live 256 MiB run is required or justified yet.
