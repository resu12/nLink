# Publication forwarding: next bounded local proof edge

Date: 2026-09-13. Base HEAD: `ef78e9d7` plus the uncommitted slice-A consumer.
Scope: B1, an offline read-only local forwarding consumer. No production caller,
Snapshot export/handoff, historical input reconstruction or causal classifier.
Status: implemented and verified on PowerShell 5.1/7 with a newly generated
Regular V4 service transfer, eight use records and 157 negative controls per host.
This is not a generated Tuna-cycle or production two-writer lineage proof.
Results: `artifacts/publication-forwarding-implementation-20260913/RESULTS.md`.

## Review decision and plan

1. Bind the original capture and Snapshot streams through the slice-A consumer;
   verify full and primary-style reduced Snapshot inputs independently.
2. Match only the captured publication's existing validated forwarding effect;
   verify exact original effect content, owner, receipt, tuple and local order.
3. Exercise generated service/transport evidence and strict negative controls on
   both PowerShell hosts; preserve slice-A/shared-resolver results and inputs.

This closes only publication-to-local-forwarding for a captured use. It does not
prove physical payload submission for a pre-pump request, peer arrival, security
installation lineage, safe stale rejection, delivery recovery or current authority.

## Exact inputs and proof

The constructor takes the same completed Snapshot view, capture input identity,
Snapshot input identity and existing writer-ledger result as slice A. Invoke A
unchanged, then detach the existing `__RoutePublicationEffects` collection from
that same supplied view. Missing private rows are unsupported, not permission to
parse diagnostics into new validated effects. Recheck the Snapshot-input hash
when retaining the effect's original source lines. No authority evaluator runs.

Resolve the original use through A, not a caller-provided positive match object.
Require exactly one forwarding row for the local Coordinator and named boundary.
Require its writer, operation key/authority/generation, authored state generation,
accepted receipt identity/generation/fingerprint and complete typed publication
scope to match the exact authored operation. Reuse the existing exact-effect
binding predicate; this is a local consistency check over validated evidence.

Bind every carried effect property to the original line at its own Snapshot
offset with the duplicate-aware parser. Require the actual effect event/kind,
projected session/transfer/direction, `authority_effect=applied`, and canonical
numeric fields. The producer uses publication identity/generation for forwarding
effect identity/generation. Its original same-writer sequence must follow the
authored boundary and precede the captured use; no timestamp or cross-writer
generation ordering substitutes for that edge. Ambiguity stays unproven.

The returned result contains detached use facts and the exact forwarding-effect
identity/location. It never exposes a recovery, acceptance or runtime-permission
flag. Repeated use of one publication/effect remains valid and is not a unique
payload-attempt identity. Later publication state does not replace the captured
authored/observed reference. A rejected ingress gaining a local forwarding match
does not gain any authority or delivery proof.

## Verification gates

Use a newly generated fake-network transfer with the actual services, encrypted
transport, publication emitter and real logger/redactor. Validate full and reduced
Snapshot streams through the normal existing validators; then consume both views
without reevaluation. Compare exact logical results separately from each stream's
physical locations. Preserve raw bytes and existing local/shared-resolver results.

Controls: unavailable/empty/duplicate effects; wrong local owner/boundary/operation;
foreign equal-valued authority; receipt or full-scope mismatch; duplicate raw keys;
raw/view disagreement; missing/malformed/noncanonical/overflowed fields; wrong
effect location; wrong Snapshot hash; effect before publication or after use;
input/result mutation; and a saved-summary-shaped view missing private effects.
Synthetic predicate controls must not be described as generated authority proof.

## Remaining input/handoff and peer-lineage gates

The finalized live full/compact summaries omit private forwarding and peer-proof
rows. Compact Snapshot offsets also reference an in-memory reduced stream whose
exact input is not persisted/bound. B1 must return unproven for these missing
bindings; do not rerun the live authority evaluator or remap its physical offsets.

A later carrier must be produced from the primary workflow's already completed
full/compact evaluations and original inputs, with independent exact projection
parity and pinned canonical bytes. The parked row/byte bounds and primary-first
persistence/deadline/cleanup gates from the consumer contract still apply. Neither
a whole-log synchronous helper nor the existing error-only diagnostic companion
proves this handoff, including GUI-success/retained-failure paths.

The next causal edge after B1 is the exact local publication-to-fallback-proof
history, followed by validated local/peer proof and paired-release-to-second-Tuna
lineage. Matching route names, frame bytes, local generation values or fixture
writer labels cannot supply these edges. Keep the original Tuna resend/receive
acceptance predicates. No new live 256 MiB run is needed for this offline slice.

### Carrier review conclusion

The existing `Resolve-FileTransferCoordinatorSnapshotEvaluationHandoff`
(`tools/FileTransferSoak/ArtifactWriters.ps1:64566`) compares the entire ordered
`string[]` input and returns the same in-process evaluation object. It is useful
for avoiding duplicate primary evaluation, not a serialized producer receipt or
a detached proof bundle. Do not weaken its exact-input comparison to a boundary
hash or substitute a selected stream.

`Write-FileTransferCompactRetainedAnalysisArtifacts` owns both completed views
locally, writes saved summaries without private forwarding/peer-proof rows, and
returns primary verdict/metric values. The full-report path additionally has an
invocation-local `Lines/Evaluation/CompactLines/CompactEvaluation` object. Neither
path currently preserves the proposed detached input/projection carrier.

The GUI classification worker serializes its response before parent adoption;
the parent persists primary error JSON before the optional companion. That path
is guarded by scenario failure and cannot cover this live GUI-success/retained-
failure run. A try/catch around new pre-response projection work would not alone
preserve the existing worker deadline, response adoption or successful-run path.

Therefore the next carrier contract must first pin: the producer lifetime while
both exact ordered inputs and completed views still exist; request/receipt/input
identity for all selected rows; independent full/reduced projection and canonical
byte vectors; and primary persistence/adoption/exit ordering for both success and
failure. Keep all row/byte bounds, whole-projection omission and original deadline
semantics. No insertion point is approved merely because the private rows exist
there. Do not implement carrier and peer classifier as one change.

The current paired-proof feasibility fixture exchanges real serialized proof
frames but supplies fixture writer labels and a fixture-only source join. It
does not run the production two-writer Snapshot/ledger validator over complete
generated histories/effects. Its four passing controls are prerequisites, not
permission to export a positive historical peer-lineage projection.
