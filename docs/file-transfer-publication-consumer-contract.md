# Publication-use consumer: reviewed bounds and implementation plan

Date: 2026-09-13. Source: `ef78e9d7d54c05066e74ee006f5d67b488a68ccd`.
Status: slice A is implemented in the working tree as a private, read-only helper
with no production caller. Causal classification, export/handoff and acceptance
changes remain outside this implementation.

## Implementation follow-up

`tools/FileTransferOps/PublicationUseEvidence.ps1` binds the original capture and
Snapshot input hashes/locations, writer content/run/scenario and completed authoritative Snapshot view before
resolving a capture. It returns only detached local-reference facts or unproven.
It does not reevaluate Snapshot authority or modify the existing resolver.

The generated fake-network fixture uses actual service/transport publications,
secure payload capture, redaction and a separately started/drained operational
logger. Existing validators independently pass its full and regenerated compact
streams. The reduced Snapshot stream uses the primary evaluator's exact
Snapshot-authority/effect event selection, separately from the compact use-event
input. Both PowerShell 5.1 and 7 agree on its pre-pump, physical-send and dispatch
references, with 182 fail-closed controls. The saved live capture supplies the
separate rejection-guard inspection; it is not a new generated offline Tuna cycle.
Verification and limitations are recorded in
`artifacts/publication-consumer-implementation-20260913/RESULTS.md`.

Generated evidence exposed an important observation rule: several idempotent
boundaries can share one Coordinator state generation and core state hash while
the data-session overlay changes. The helper requires distinct boundary IDs,
one core state hash, and agreement from every observation on the carried local
publication tuple/ownership. If optional data-session fields are present, every
observation must agree with them too; matching only one overlay is unproven.
Absent fields neither select an incarnation nor weaken a later causal gate.

The saved live full view binds the selected four use records. Its compact view
uses offsets into an in-memory reduced Snapshot/effect stream whose exact input
is not persisted/bound in this artifact. Passing the retained log in its place
correctly fails `snapshot_input_binding_invalid`; missing that separate input
identity fails `input_evidence_invalid`. The consumer does not rewrite offsets,
regenerate an unbound historical compact input, or reevaluate authority. Thus
generated full/compact parity is proved, but saved-live consumer parity is not.

A separate [B1 forwarding consumer](file-transfer-publication-forwarding-contract.md)
now matches exact local forwarding effects on generated Regular V4 evidence,
with no production caller or export. It leaves A unchanged. The next causal edge
is local publication-to-fallback-proof history and paired local/peer lineage.
The finalized saved summaries still omit the private forwarding/peer rows; B1
does not fill those historical bindings, recover live ingress failures, or
authorize reconstructing omitted projections from diagnostics. The remaining
B/C work stays separately gated below.

## Decision

Implement a **separate private, read-only local publication-reference consumer**
first, with production-generated offline evidence and no production caller.
Its result may say only that the captured reference matches an exact accepted
local receipt and its original observed state. It must not say that a frame is
proven stale, that a cross-peer link exists, that current runtime authority is
granted, or that a rejected frame was delivered/recovered.

Do not modify `Resolve-FileTransferPublicationEvidence`: it serves existing
frontier admission/recovery consumers whose semantics are outside this slice.
Do not normalize redacted placeholders or feed modified event copies to it.
Keep the existing second-Tuna resend/receive predicates and all primary results.

The later causal validator and detached export/handoff remain **no-go for
combined implementation** until their exact joins and primary-preservation
gates below are demonstrated. The previous parked classifier remains disabled;
this review does not revive its historical time-box or authorize a policy change.

## Current artifact and executable findings

Use run `c8ba8a92-bc5a-4877-9eda-d8b4b3924abc`, scenario
`regular-v4-live-activation-off-on-off-256mb`, attempt 1, directory
`artifacts/filetransfer-route-acceptance/publication-use-live-20260913/20260913T180334Z`.
The finalized live verdict remains FAIL: 49 distinct ingress cases, 39 recovered,
10 unresolved; a later stall-grace lane has one separate unresolved case.
The first missing ingress predicate is `missing_tuna_resend`, not missing capture.

`Inspect-ConsumerBoundary.ps1` rechecks the completed receipt (including its 11
output bindings) and the pinned full/compact summaries without reevaluating
Snapshot authority. It runs the unchanged resolver and inventories named rows:

| Retained record | Captured use | Accepted authored / observed state | Existing resolver |
| --- | --- | --- | --- |
| 8547, source writer sequence 14255 | Physical V4 submission, Regular epoch 0 | 3 / 12; one named boundary and one observed boundary | No match |
| 28378, receiver writer sequence 14254 | Rejection guard, Tuna epoch 3 | 370 / 370; one named boundary and one observed boundary | No match |
| 28380 and 28382 | Same guard's mismatch and ignore | Same receiver reference | No match |

All four records have agreeing publication/security/data-path projected scopes.
Their literal raw session is `sess_[redacted]`, which the existing resolver does
not recognize as exact `[redacted]` and cannot project to the recorded token.
All 17 publication notifications also return no match. Full/compact resolver
results agree. These negative results are not evidence of unauthorized runtime
publication; fixing only a raw-string comparison is not a complete consumer.

The source receipt is publication generation 1, token fingerprint
`fte1-25e300ecc734fd7d207e3de1`, boundary
`fte1-c0eabfb90a26fdcaa6a2959a`. The receiver receipt is publication generation 4,
fingerprint `fte1-9b13d48d639081b45d9df81d`, boundary
`fte1-433ca98100aea5ab29972114`. Their Coordinator/writer histories are distinct.
Equal local generations are neither required nor sufficient for a peer link.

The saved full and compact summaries both omit `__FallbackTailPeerProofBindings`
and `__RoutePublicationEffects`. Existing parity includes a semantic hash of six
peer bindings, but does not export their rows or prove the proposed end-to-end
publication-use links. Do not reconstruct a positive detached projection from
these summaries or rerun this live artifact's authority evaluation to obtain it.

## A. Local-reference consumer contract

This is the only implementation-ready slice from this review. Use a new private
helper under `tools/FileTransferOps` with focused tests; exact API names may follow
the existing script conventions. Do not introduce a public DTO, wire format,
mandatory event family, classifier field or GUI/retained call site.

Inputs are the caller's already completed, validated Snapshot view, the exact
immutable capture input and Snapshot input identities/locations and validated
per-writer ownership, plus the original structured event. These inputs can be
the same file for a full view; a reduced Snapshot stream has separate offsets
and excludes use records. Both inputs must bind to the same frozen writer log.
No caller-supplied `eligible=true` or bare global PASS
flag replaces these bindings. The helper consumes existing validation; it must
not instantiate an authority evaluator or repair missing evidence.

Required checks, in a stable fail-closed order:

1. **Input and writer binding.** Require complete authoritative route-family
   coverage and passing artifact evidence; bind the event, named boundary and
   observed boundary to the same validated writer/run/scenario and frozen input.
   Consume ledger writer-start ownership and continuity, not process-ID parsing.
   Check original writer sequence/content membership, not merely that its number
   lies between a writer's first and last sequence. Preserve original physical
   path/line separately from logical identity. Conflicting copies are unproven.
2. **Strict parse and domains.** Reject duplicate keys (even identical values),
   missing required fields, unsupported versions/kinds, malformed tokens,
   conflicting reference copies and nonpositive/overflowed generations. Use the
   existing duplicate-aware structured parser before dictionary lookup; do not
   silently accept a last-value-wins parse.
   The existing publication version is `1`; security uses versioned token
   prefixes, not a new `security_evidence_version` field. Require
   `security_context_status=captured`, well-shaped existing installation,
   transport, establishment, payload and outer references, and positive captured
   installation generation/secure sequence. These are capture-shape checks, not
   validation of installation authority or peer causation.
3. **Projected scope.** Require well-formed `fte1-` session/transfer tokens in
   all three existing `publication_evidence_*`, `security_*`, and `data_path_*`
   domains, equal to one another and the named/observed validated boundary scope.
   Raw placeholders are never identity. For this slice the unavailable raw forms
   are exact `[redacted]` and, for session only, exact `sess_[redacted]` (the live
   transfer field is exact `[redacted]`). Do not accept arbitrary strings
   containing a redaction marker; an additional form needs a pinned producer
   fixture and explicit extension. Unredacted raw scope must independently agree.
   Missing or conflicting projected scope cannot be repaired from raw text.
4. **Exact authored receipt.** Require one `(Coordinator, boundary ID)` row and
   one exact operation key with the captured authored state generation. Check
   accepted/validated publication receipt, operation/receipt identity and
   generation, fingerprint, `route_runtime/route_publication` role, and existing
   publication/commit kind. Require agreement among the captured token reference,
   the operation's receipt and typed route-publication boundary. No nearest
   generation, route-cache lookup or matching route-selected log is a substitute.
5. **Original observation.** Resolve the captured observed generation in that
   same local Coordinator history. Its writer, direction, scope and captured
   publication tuple must agree. Authored and observed generations remain
   separate: source 3/12 is valid input shape, not a reason to force equality.
   Async send completion must retain the earlier use reference even if later
   state exists. Do not test current authority at the send-result timestamp.
   Receiver generation 370 describes the original service lookup forwarded to
   the adapter, not a fresh Coordinator observation at guard time.
6. **Use kind and carried fields.** Keep `v4_physical_submission`,
   `v4_pre_pump_request`, `receive_guard` and `receive_dispatch` distinct. A
   pre-pump request does not prove a physical lease/submission. Guard records
   require their recorded rejection disposition and `authority_effect=none`;
   dispatch records require `dispatch_entered`. Validate every carried route/
   protocol/epoch/runtime/terminal/leg/publication/commit field against its exact
   typed boundary. Regular and Tuna require protocol 4; fallback requires 6;
   active Tuna6 is invalid. Validate optional carried data-session fields when
   present, but never manufacture them when absent (they are absent in the four
   selected events). Missing stronger incarnation proof stays outside this
   local-reference result.

Return only detached reference/use facts and a diagnostic match/unproven reason.
Preserve the exact authored and observed references and original location. Do
not expose `Recovered`, safe-stale, acceptance or runtime-permission semantics.
The marker and security fingerprints scope the observed use; matching them does
not itself validate security installation, forwarding or cross-peer causation.

Repeated use of one publication is expected. Do not use its fingerprint as a
unique payload attempt ID. Different original writer sequences remain different
use records; identical physical copies may share one logical record only under
the existing original-content identity rule. Ambiguity never selects a convenient
receipt, writer, notification or source.

## B. Later exact causation: required proof, not inferred edges

Both local-reference matches are necessary but insufficient. The later validator
must also validate the security installation/retirement and unique actual payload
binding from the existing capture, then bind the following explicit edges:

| Edge | Required existing evidence | Insufficient substitute |
| --- | --- | --- |
| Publication to local forwarding/use | Exact validated `route_publication_forwarded` effect and its boundary/operation/receipt, plus original notification/use reference | Matching cached tuple or notification text |
| Old Regular to the source's fallback proof | Same validated local Coordinator history, exact publication/transition/proof references | Transfer proximity or wall-clock order |
| Source local proof to receiver peer proof | Exact proof authority and generation, identity/fingerprint, receipt/effect and both validated writer histories | Fixture writer labels, security-byte match, epoch equality |
| Receiver peer proof to second Tuna publication | Exact paired release carried into activation and its accepted transition/commit/publication receipts | Ordered Regular/Tuna/fallback/Tuna route names |

Use the validator's existing `paired_yield` binding fields and local/peer proof
receipts/effects; validate both endpoint directions and ownership. The current
paired-proof fixture is useful evidence of a possible serialized frame link,
not a production two-writer validator. It checks fixture labels and candidate
identity matching and must not be promoted into that role without generated
writer histories and independent full/regenerated-compact validation.

Reject missing, duplicate, conflicting, wrong-writer, restarted/ABA, retired or
unsupported lineage. No equality between different writers' local generation
numbers is a causal edge. No generic attempt-to-effect correlator, new runtime
authority or inferred no-effects result belongs to this scope.

Even a future complete causal link cannot set delivery recovery true. The exact
Tuna resend/receive requirement remains independent; the live 10-case unresolved
result is not reclassified by this contract.

## C. Later export and handoff gate

Export only from the full and independently generated compact evaluations that
the normal primary workflow already completed. Extract independently, compare
the exact logical publication/forwarding/lineage rows, and hash canonical bytes;
counts or the current aggregate hashes are not sufficient. Pin ordering,
serialization and byte vectors before implementing a consumer of that export.
Keep physical provenance outside semantic parity while validating its exact
input/request/receipt binding. A saved summary lacking private rows is unsupported,
not permission to reevaluate authority or reconstruct links from diagnostics.

Retain the parked bounds: 64 publication rows, 128 lineage links, 128 contradiction
links and 256 KiB serialized UTF-8 per request. Export only needed rows. On any
overflow omit the whole optional projection with a reason; never present a
truncated graph as complete. Missing input, wrong hash/request, malformed or
conflicting references, timeout and serialization failure yield no positive
diagnostic result. They must not change primary adoption, original failure,
persisted primary bytes, cleanup or the existing worker deadline/budget.

The current GUI worker serializes `diagnosticEligibility` inside its response
before parent adoption; the parent persists primary error JSON before its
optional companion. That proves neither safe expensive pre-persistence export
nor a GUI-success/retained-success handoff. The live GUI succeeded while retained
acceptance failed, so an error-only companion would miss this run. Resolve that
orchestration boundary in a separate tested slice. Do not add success-path work,
move primary persistence, or redesign worker ownership during slice A.

## Verification sequence and stop conditions

1. Implement only A with production-generated publication/send/guard records and
   real redactor output. Validate full and independently regenerated compact
   fixtures through existing validators; do not call synthetic DTO fixture PASS
   a generated-proof result. Run the helper in both PowerShell 5.1 and 7.
2. Require controls for all field domains, duplicate keys/rows, missing writer
   ownership, wrong run/input, foreign equal-valued Coordinator, wrong direction,
   stale observed publication, malformed/overflowed numbers, replacement/cleanup,
   absent association/clone, pre-pump vs physical submission and capture across
   async completion. Matching raw placeholders with bad projected scope must
   fail; valid projected scope must not require placeholder rewriting.
3. Pin logical full/compact results and original provenance. Prove existing
   shared-resolver outcomes and recovery quartets/first failure remain unchanged
   on directly affected fixtures, including a field-ablation control. No runtime
   producer edit or live recapture is required to implement this local consumer.
4. Run focused tests during edits; after stabilization run the directly affected
   consumer/producer/parser checks and at most one relevant broader contract lane.
   No aggregate or full GUI matrix, portable rebuild or NKN run is needed here.
5. Stop before wiring any caller, enabling causal/stale classification, adding
   an export/handoff, changing primary acceptance or attempting another live test.
   Report the first unsupported binding explicitly; do not broaden A to fill it.

Review verification: six existing prerequisite cases passed (two resolver-host
cases and four paired-proof controls). These do not implement or validate A/B/C.
The current-artifact inspection passed receipt/hash/full-compact checks without
changing its negative resolver outcomes or live verdict. Details and reproducible
script: `artifacts/publication-consumer-review-20260913/RESULTS.md`.

Source anchors: `FileTransferPublicationEvidence.ps1:24` (existing resolver),
`GuiSmokeLogLedger.ps1:329` and `:1663` (writer validation/projection),
`CoordinatorSnapshotEvidence.ps1:13023` (paired binding), `:14620` (private rows),
`:15820` (cross-direction route comparison), `:16414` (peer-binding semantic hash),
`AnalyzerOrchestration.ps1:8979` (saved-summary exclusions),
`GuiSmoke-Windows.ps1:31710`, `:31886`, `:38024` (carrier/adoption/persistence).
