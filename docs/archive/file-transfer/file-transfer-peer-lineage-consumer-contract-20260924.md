# Bounded Service-yield peer-lineage consumer contract

2026-09-24. Reviewed branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, including the uncommitted capture and
Service-history integration. This step is review/documentation only. The contract
below describes the next bounded implementation; it does not enable recovery,
change acceptance or certify a live run.

## Finding and decision

The next missing edge is custody of the new diagnostic records. The sealed
Service-history fixture contains 383 original records, including 14
`filetransfer_fallback_tail_yield_evidence` records. Its primary retained input
has 325 full records and 162 compact authority records, with zero new yield
diagnostics. The post-run capture assertions read the complete original merged
source and existing reports. They are a fixture gate, not a custody-owned consumer.

The minimal change is to retain this exact event through the existing semantic
diagnostic allowlist, before source completion and original primary evaluation,
then query it in a separate default-off, test-only consumer. Do not introduce a
sidecar reader/capability, reread raw logs in the consumer, append records to sealed
arrays, or add this event to the authority catalog. Old captures without these
records remain insufficient; do not repair or reclassify them.

This is an evidence-retention/consumer gap, not evidence of a runtime routing,
authentication or data-integrity defect. The sealed primary still reports
`missing_exact_tuna_receive`, unrecovered. Both hosts have 71 passing boundaries
per authority view, full/compact parity `pass`, and three original evaluations.

## Verified insertion and custody boundaries

| Existing boundary | Consequence for implementation |
| --- | --- |
| `tools/GuiSmokeEvidenceCatalog.ps1:2` and `:57` | Add only the exact event to `GuiSmokeLiveDiagnosticEvidenceEventNames`; the existing semantic union already includes diagnostics separately from authority families. |
| `tools/GuiSmoke-Windows.ps1:3533` and `:3799` | Native frontier and ordinary streaming finalization both obtain their semantic event names from that union. Do not alter their completion/adoption gates. |
| `tools/GuiSmokeLogLedgerStreamingProcessor.cs:945` | Existing selection writes the original line bytes and records the original ledger origin. No event-specific copier or processor change is indicated. |
| `tools/FileTransferOps/LocalGuiWriterSemanticCopy.cs:19` | Native semantic copying remains a one-shot, original-cursor subgate, not primary custody or publication completion by itself. |
| `tools/GuiSmoke-Windows.ps1:4300` | Ordinary analyzer input is copied from the finalized semantic slice and checked against its hash, bytes and lines. Retention must precede this step. |
| `tools/FileTransferSoak/LogParsing.ps1:141` and `:215` | The existing parser/index retains structured events and original raw strings; it does not require adding the new event to an authority family. |
| `tools/FileTransferOps/LocalPrimaryEvaluationInputs.ps1:6` | The source owner, exact original arrays, physical origins, native hashes and writer reader already form the input-binding path. Use this unchanged. |
| `tools/FileTransferOps/LocalRetainedSourceRequest.cs:285` and `:429` | Native input identity is not evaluation authority. The consumer also needs the original completed evaluations and live primary custody. Native-frontier omissions must not be bypassed through legacy readers. |

The native role mapping is significant: `fullCapture`, `fullSnapshot` and
`compactCapture` use the same original full array; only `compactSnapshot` uses
the authority-filtered array. The new consumer must query the shared full
diagnostic capture against each independently completed authority view. It must
not claim the diagnostic event occurs in the compact Snapshot input.

Before and after querying, require the original custody to own the original
context, completed evaluations, full/compact arrays and views; require the
verified original writer reader and current native input ownership. Bind the
selected failure to the original `Index.AllEvents` object and its physical
source origin. Byte equality of replacement arrays, a serialized report, a path,
an environment flag or a copied completion object is not ownership.

Retain the existing whole-writer EOF/gap/identity checks. The consumer must not
establish a second source/writer reader or extend a disposed capability's lifetime.
Unsupported or omitted native-frontier custody remains omitted. This contract
does not complete any independent native-frontier gate.

## Exact read-only candidate chain

Query one original primary-selected failure in the initial fixture. Retain the
existing old-send/second-Tuna-rejected-ingress envelope match and local
publication/forwarding checks. The old source publication must remain
`regular_nkn_v4_fast` protocol 4; the receiver publication must be the exact
coordinator-published `file_tuna_v4` protocol 4 after `post_tuna_fallback_v6`
protocol 6. Never accept active `file_tuna_v6` or treat fallback delivery as
exact Tuna receive proof.

Replace neither the old fixture nor its deliberate unproven result. The new
Service-owned fixture needs its own opt-in query because the old prototype uses
a data-session send after a driver-authored local receipt. The real Service
control-plane path records its local receipt after the original send result.

1. Select the original source writer's accepted local receipt association for
   the relevant yield episode after its old packet use. Require version 1,
   `authority_effect=none`, `yield_evidence_status=bound`, captured envelope,
   `yield_receipt_stage=local_proof`, decision `Accepted`, state-changed `1`, and
   exactly one validated `Observed` local receipt in the named returned boundary.
   Match writer, Coordinator, transition, after-state generation, operation
   key/authority/generation and receipt identity/generation/status/fingerprint/
   stage. Resolve scope/direction from that original boundary; do not invent
   session fields on association records that do not contain them.
2. Follow its original writer-local `yield_evidence_id` to the original
   `send_copy` records. There are at most two named slots per invocation:
   control and bulk. Each retains its own security/envelope identity and outcome.
   The selected slot must have its own successful `control_copy` or `control_ack`,
   or its own successful `bulk_copy`, and bound captured security evidence.
   Queue acceptance alone is insufficient. A success on the other slot or a
   generic aggregate `PeerVisibleAny` cannot supply this slot's outcome.
3. Match the selected slot to the other original writer's authenticated
   `receive_dispatch` by exact copy name, envelope fingerprint, payload
   fingerprint, secure sequence, establishment fingerprint, projected data-path
   and security session/transfer, canonical proof identity and frame fingerprint.
   Version/event identity identifies this yield diagnostic; it has no separate
   `frame_type` field. Do not add or reconstruct one as a matching prerequisite.
   All required fields must be present, canonical and unambiguous. Reject
   duplicate structured fields rather than relying on the general index's
   last-value parser; use the existing strict structured-field parser.
4. Follow that original ingress's receiver-local `yield_evidence_id` to its own
   accepted, state-changing `peer_proof` association, and match its exact
   Coordinator boundary/operation/receipt as in step 1. Sender and receiver
   evidence IDs are distinct local handles, not an on-wire identity to equate.
   Do not substitute another ingress's accepted association. An idempotent or
   rejected association names only its own attempted decision and is not a
   positive receipt witness.
5. Follow the receiver's accepted peer proof through the original complete
   Snapshot history to its validated paired release and exact responder handoff
   into the selected second-Tuna publication. Require the same canonical source
   proof identity/fingerprint and the source authority/yield-generation reference
   in the peer proof, using existing projection helpers. The handoff's paired
   release authority/generation and complete target route tuple must match the
   original publication. Keep writer, Coordinator, session, transfer and direction
  ownership strict throughout; never join a restart or another episode.

This initial candidate binds the source-to-receiver yield used by the selected
failure. It does not claim to have independently authenticated the reciprocal
yield merely because the receiver's complete authority history is paired.

Step 5 must handle the observed Service ordering. In the sealed receiver history,
writer sequence 133 accepts `peer_proof_pending_initial`; sequence 148 is the
idempotent duplicate; sequence 158 applies the local proof and pairs the current
yield. The accepted peer receipt is not required to originate in `yield_current`
or in the pairing boundary. Follow its exact validated proof through the existing
pending-to-current transition, including intervening invalidation checks. The
pending operation key/authority need not equal the later current-yield operation
key/authority. A matching proof string without the validated transition chain is
insufficient. Local-first acceptance directly into `yield_current` must also work.

One fully matched successful slot is sufficient for this diagnostic chain; both
network copies are not required. Preserve the second distinct copy and its
idempotent attempted receipt as diagnostics, not ambiguity or extra authority.
In contrast, conflicting duplicate slots, reused ingress handles, or multiple
accepted transitions for the same selected association are unproven. Repeated
receipt projections in later valid snapshots are history, not new acceptances.
Select the original accepting transition, not an arbitrary later matching row.

Compare ordering only within a writer's verified sequence. Local send-result
diagnostics precede that Service's local receipt; authenticated dispatch precedes
its own peer receipt; pairing/handoff precedes the receiver's publication/use.
The send-result diagnostic is emitted after delivery completes: a remote ingress
log can precede that log write. Cross-writer timestamps or merged line order must
not be treated as causality. Peer-local transport/installation IDs and generation
counters are independent; validate each local lifetime without equating them.
Captured-context retirement, unbound/stale status, missing evidence or intervening
Coordinator invalidation disqualifies a positive witness. Do not sample current
global security state or change runtime rejection/replay behavior.

## Bounds and output isolation

Keep the initial query fixture-only and default-off. Reuse the existing observer
seam after original reports/evaluations and before private capability cleanup.
Preserve the scriptblock invocation's original primary script scope; replacing it
with a path invocation can change catalog visibility. Do not invoke another
Snapshot or recovery evaluator, modify original views/arrays, or export custody
through artifacts. Recheck ownership/reference identity at completion.

Keep the existing 4,096-full-record and 256-boundary fixture limits, apply the
boundary limit to both views, and cap selected original source-line witnesses at
64 in total (deduplicated by original physical origin). Existing native byte,
record, line-size and cumulative work limits remain in force. Bound candidate
enumeration before expansion; exceeding a limit yields an explicit omission,
not truncation that could hide conflicting evidence. No generalized framework,
new global tracing switch or increased production budget is in scope.

The separate test artifact may report `matched_original_yield_receipts` with
selected exact origins and normalized full/compact agreement, or an explicit
unproven/omitted reason and first missing diagnostic edge. This reports only the
bounded diagnostic association. It does not certify stale safety, successful Tuna
payload delivery, recovery or acceptance. Keep the original publication carrier
`peerLineageStatus=not_evaluated`, primary classification/result/completion and
evaluation callers unchanged. Keep `ClassifierEnabled=false`,
`AcceptanceChanged=false` and `Live=false` explicit. Consumer failure or output
failure cannot replace the primary result.

Retention changes source hashes, source origins, full-array/native record counts
and diagnostic counts. Do not hard-code their old values as outcome invariants.
Compare the normalized original selected witness content, boundary/operation
semantics, route verdicts, original failure, explanation, recovery state,
completion and evaluation count. Compact authority event membership must not
expand. Adding this event to the semantic allowlist is unconditional retention;
only the new consumer is default-off.

## Implementation and verification sequence

1. Add the one event to `tools/GuiSmokeEvidenceCatalog.ps1`. Add a focused
   diagnostic-retention test beside the catalog/ledger harness tests, checking
   exact original records/origins survive semantic copying and indexing while
   Snapshot authority families remain unchanged. Verify both PowerShell hosts.
   The shared ledger/custody implementations should need no changes.
2. Add the separate test-only probe and observer opt-in beside
   `tests/nLink.SmokeTests.Core/Support/PublicationPeerLineageProbe.ps1`, reusing
   the Service-owned fixture and existing original-primary observer. Preserve the
   old probe as the missing-binding baseline. Extend
   `FileTransferPublicationUseConsumerTests.cs` with the focused new integration.
   No production Service/transport, authority catalog or classifier edit is
   indicated by this review.
3. Test exact positive control-only/bulk-only matches, both copies with one
   idempotent association, and pending-first/local-first receipt histories.
   Add detached matcher negatives for missing/substituted diagnostics, duplicate
   fields/slots/accepted receipts, wrong writer/Coordinator/episode, wrong copy or
   security/proof tuple, queue-only/other-slot success, retired/unbound metadata,
   borrowed idempotent receipt, wrong returned boundary/operation/receipt,
   pending-proof invalidation, missing paired release/handoff, and wrong target
   publication. These detached controls test the query, not Snapshot tamper
   acceptance. Separately test actual custody loss/replaced arrays/origins and
   size/witness-limit omission without manufacturing a positive capability.
4. Run a fresh offline Service-owned integration on both hosts. Assert diagnostics
   now belong to the original custody-owned full capture and each query agrees
   against its independent authority view. Keep three original evaluations and
   the strict unrecovered primary failure. Then run only the affected retention,
   original unproven-prototype and default-off observer preservation tests. Use at
   most one relevant broader contract lane if shared infrastructure changes;
   do not rerun unrelated aggregate lanes or the full GUI matrix.

Stop and revise if this requires new source capabilities, reopening completed
inputs, weakening incomplete-history rejection, expanding authority, changing
security/runtime scheduling, or modifying acceptance. A missing next proof edge
must be reported, not filled by heuristics. Classifier enablement and any later
targeted live-256 validation remain separate boundaries after offline proof.

## Review verification

The original integration seal and all 76 pinned files remain unchanged. All 35
pre-existing dirty files are preserved byte-for-byte. This turn adds only this
document and the ignored review seal at
`artifacts/peer-lineage-consumer-contract-20260924/FINAL-VERIFICATION.json`.
No tests/evaluators, historical reanalysis, package rebuild, live GUI/NKN run,
commit or push were performed. The previous 14 passing tests are prior sealed
evidence, not tests rerun during this review.
