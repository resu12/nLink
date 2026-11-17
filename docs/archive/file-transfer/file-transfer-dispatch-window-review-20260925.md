# Captured dispatch-observation window: review and next contract

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, with the existing uncommitted
rejection-chain implementation. This step is review/documentation only.

## Decision

The next bounded implementation can use existing evidence: a default-off,
test-only dispatch-observation query over the original captured input. No new
runtime event, semantic-retention change, reader reopening or authority evaluation
is indicated. It must bind its interval to the original publication boundary and
the verified writer-capture endpoint, explicitly **not runtime retirement**.

This is an observation diagnostic, not the historical `proven_stale_rejected`
classifier or an acceptance exemption. The original `missing_exact_tuna_receive`
failure remains independent and unrecovered. A count of zero cannot prove absence
of all effects or establish that a queued callback did no earlier preparation.

## Evidence reviewed

Latest accepted positive case:
`artifacts/publication-rejection-chain-20260925/7946356620fc43f4bdace7c93817dcc3/`.
Its saved results have 383 raw, 339 full and 162 compact records, 71 passing
boundaries per view, 20 shared witnesses and three original evaluations per host.
These results were inspected, not reevaluated or rerun here.

Receiver writer: `p22424r4cd4f679dff6428f`; Coordinator:
`fte1-5718d1454332628f0b3eb4e4`; selected publication boundary:
`fte1-3abd3556e07b16076c8b0d9a`.

| Receiver writer sequence | Retained-source physical line | Meaning |
| --- | --- | --- |
| 188 | 271 | Commit boundary authors publication generation 4 at state 33 |
| 201 | 284 | Exact publication-forwarding effect |
| 202 | 285 | Disposition receipt advances state 33 to 34; does not retire this publication |
| 220–222 | 326–328 | Matched receive/mismatch/ignore chain |
| 225 | 338 | Snapshot stream completion at final state 34 |
| 226 | 339 | Last retained receiver record: data-session removal |
| 228 | absent | Security-context retirement in the raw writer |
| 230 | absent | Raw writer drain / final captured receiver sequence |

The full view has no later retirement of the selected route-publication operation.
The boundary at 202 changes transition-disposition operations, not that
publication. A transition operation named `terminal` is not itself a transfer
terminal or publication-retirement receipt.

Saved raw and retained records both contain zero `dispatch_entered` receive
results and zero generic dispatch records in receiver sequence interval
188–230. The raw interval has 43 records; the retained interval has 33. The
difference is filtering, not a writer gap. Saved writer integrity, chronology,
recovery and cursor verdicts pass, with zero writer gaps, duplicate sequences,
write failures and missing drains. These are artifact observations, not a newly
issued private consumer result.

## Which event supports which claim

`TryPrepareFileTransferDataFrameDispatch` captures the dispatch-time publication,
rechecks the guard, then emits `filetransfer_data_path_receive_result` with
`publication_use_kind=receive_dispatch` and
`receive_disposition=dispatch_entered` before calling `DeliverFileTransferDataFrame`.
This record carries the authenticated envelope/security and publication fields.
The existing local publication resolver already supports this use kind. The
record establishes entry into that dispatch path, not successful queueing,
Service delivery, a durable write or an effect attributable to the rejected
attempt.

For Tuna payloads, `filetransfer_tuna_data_path_peer_visible` can accompany the
receive result with the same identity and disposition. It must not be counted as
a second dispatch entry. An orphan candidate-like companion must not be ignored
when deciding that no matching entry was observed.

`filetransfer_data_frame_dispatched` is different: it is emitted later under
`FileTransferDiagnosticLogPolicy.TraceEnabled` and lacks authenticated payload
and publication identity. Its `transport=nkn` names the adapter, not proof of the
actual Regular/Tuna path. It cannot replace a bound dispatch-entry record, and
its absence cannot prove that no dispatch happened. Do not invent an association
using timestamps, neighboring records, chunk range alone or queue counters.

All three event names already belong to the semantic catalog.

## Capture endpoint is available without a new producer

The current private `SourceOwner.WriterContext` has an original read-only writer
list and output descriptors. `WriterReader.TryVerifyMembership` has already
consumed the exact declared writer output to EOF, checked its hash/line count,
validated contiguous per-writer sequences through each declared `LastSequence`,
and matched original full-input rows to those writer records. Its active
`IsVerified` state remains dependent on the same source owner. For this case the
selected writer endpoint is 230, even though its last semantic record is 226.

The original native ledger pass filters every captured line using the supplied
semantic event set, including the dispatch events above, and the finalizer binds
the analyzer copy to that semantic output's hash, byte count and line count.
That explains the retained/raw tail difference. Membership by itself only proves
that retained rows belong to the writer; do not describe it as independently
proving complete retention of every raw event.

The next diagnostic should therefore explicitly check that its full original
array covers the entire primary-owned semantic source: matching source-reader
completion/count, full origins exactly 1 through `SourceLines`, and unchanged
original source/output association. If this narrow complete-source condition
does not hold, omit the diagnostic; do not broaden it to an arbitrary subset.
The claim remains limited to these recorded event surfaces in this captured
input. It does not certify that every runtime action was logged.

Reuse the existing verified endpoint metadata. Do not reread raw logs, rerun
membership, export an owner capability, synthesize an EOF record, add the raw
drain to the semantic source, or equate `StreamComplete` with runtime closure.
An endpoint reported as original metadata has no retained-source line witness;
label it accordingly rather than assigning it the last retained line's origin.

## Bounded next implementation

Add an explicit opt-in after the successful original reciprocal and rejection
chain checks. Keep their existing results intact if the optional query fails.

1. Reuse the private custody, arrays/origins, completed views and verified writer
   reader. Bind one original writer descriptor to the selected receiver; require
   unchanged descriptor/context ownership, complete primary-source coverage and
   valid bounds. Require `boundary sequence < forwarding sequence < receive
   sequence < mismatch sequence < ignore sequence <= writer capture end`.
2. Define the inclusive window from the selected publication's original
   Coordinator **boundary record** through that writer's verified capture end.
   Record the forwarding sequence separately. This is a record-order interval,
   not an assertion about the instant publication became active: Snapshot
   emission can be asynchronous. Include all records after ignore through capture
   end; never stop at ignore, last relevant row or Snapshot completion. Search
   the remaining original selected-writer rows for candidate-publication uses
   outside the declared interval and reject inconsistent ordering.
3. Inspect original receive results, peer-visible companions and generic dispatch
   observations in that writer/scope. Use the authenticated payload fingerprint,
   security establishment/sequence and local installation, outer-envelope
   fingerprint, canonical session/transfer and v4 frame/range to distinguish
   candidates. A candidate-like record with missing/conflicting fields or reused
   payload identity under a substituted envelope is unproven, not silently
   excluded. Do not equate identities or numeric generations across writers.
4. Resolve each candidate dispatch entry's own `receive_dispatch` publication use
   against each completed view. Match the selected publication's stable owner,
   boundary, operation, token and authored generation. Its observed Coordinator
   state may legitimately advance; validate that state's own exact scope with
   the existing resolver instead of demanding equality with the rejected
   receive's observed state. Keep strict route/protocol invariants.
5. Count each original receive-result dispatch entry once. A separately validated
   different publication or authenticated payload is outside the exact candidate,
   not a contradiction. Check peer-visible companions without double counting.
   A generic dispatch record that cannot be ruled out for this candidate using
   its available fields is an unbound observation: it prevents a clean
   no-matching-observation result. Do not borrow another record's identity to
   upgrade it. Clearly unrelated frame types/scopes may be excluded.
6. Require normalized full/compact agreement, exact original origins for selected
   evidence, and before/after custody and unchanged-input checks. Keep the
   existing 4,096-record, 256-boundary-per-view, 64-operation-per-boundary,
   cumulative work limits and **64 shared original witness lines total**. Capture
   endpoint metadata stays separately labeled and within the existing bounded
   descriptor list. Overflow or incomplete coverage is unproven, not truncation.

The separate diagnostic may report `observed_matching_bound_dispatch`,
`no_matching_bound_dispatch_observed_in_capture`, or explicit unproven with its
first missing edge. Include the interval endpoints/kinds, last retained writer
sequence, match count and any unbound-observation reason. Even a positive match
is not an unsafe-callback verdict: the same authenticated envelope may be used
in another attempt. None of these statuses changes primary failure, candidate
accounting, recovery, completion, carrier lineage or acceptance.

No generic publication-retirement framework, security-retirement reconstruction,
attempt-to-effect correlator, route scheduling change or new live run belongs to
this step. If implementation cannot establish the endpoint from the current
original metadata, report that missing edge instead of manufacturing a closure.

## Verification scope

Use one fresh existing genuine both-copy rejection history on both PowerShell
hosts. Expect the original chain to match, the captured observation window to
contain no matching bound dispatch, and the unchanged primary to retain exactly
three original evaluations and `missing_exact_tuna_receive`, unrecovered.

Add detached query controls for matches before/after ignore and after Snapshot
completion; orphan peer-visible and unbound generic observations; duplicate
origins; different payload/publication/writer exclusions; reused payload with a
different outer identity; malformed fields and unsupported observed state;
missing/foreign/early EOF metadata; incomplete full-source coverage; ordering,
custody, parity and shared-witness overflow. Include a valid later observed state
under the same publication. Label constructed dispatch cases as structural
controls, not genuine authenticated unsafe histories or authority evaluations.
Do not alter runtime or synthesize original records to manufacture such a case.

Then run only directly affected default-off/omission regressions once. Do not
repeat unrelated copy/order cases, aggregate lanes, portable builds or GUI/NKN
tests. This review establishes the diagnostic's scope, not live-256 readiness.

## Review verification

All 80 pins in the previous implementation seal and all 54 pre-existing dirty
file contents remain unchanged. That seal's SHA-256 is
`2fbb992bd6df77811d7c9b4644b882367b7576fc0fb41bdf8cd11ff00a31e3f8`.
This review adds only this document and the ignored seal at
`artifacts/dispatch-window-review-20260925/FINAL-VERIFICATION.json`.
No tests, authority/recovery evaluations, source edits, package rebuild, live run,
commit or push were performed.
