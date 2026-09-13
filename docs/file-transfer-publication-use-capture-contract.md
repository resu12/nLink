# Publication-use evidence: bounded contract and implementation

Date: 2026-09-13. Reviewed runtime source:
`356f5dbce4302e64e5539a109ab24f2339180198`.
The original review below was offline, after the finalized security-capture
live attempt. The user subsequently approved the private capture implementation.

## Implementation follow-up

Private publication-use capture is now implemented in the working tree at the
four specified producer boundaries. The source association uses weak-key object
identity outside authority equality/serialization. The receiver companion is
copied under the existing gate and supplies diagnostics only; replacement,
missing evidence and cleanup cannot borrow another association. Actual send
completion retains its captured reference, payload and sequence across awaits.

Focused offline producer, privacy and publication tests are recorded in
`artifacts/publication-use-implementation-20260913/RESULTS.md`. Real service
loopback transfers exercise both authority-aware and legacy sessions. Generated
transport records with publication fields preserve the existing full/fresh
compact proof result under both PowerShell hosts; removing only those diagnostic
fields in a test-only control does not change the result.

No public DTO, wire, Coordinator authority, crypto, replay, dispatch predicate,
redactor implementation, shared resolver, classifier or acceptance rule changed.
The forwarded observed generation still names the original service lookup,
not a fresh Coordinator observation at the later guard. Historical live FAIL
and its 48 unresolved cases remain unchanged. Stop before another live 256 MiB
run; the stronger causal validator and detached handoff remain separate no-go
items. The sections below retain the original review and governing contract.

## Decision

The next bounded implementation candidate is **private publication-use capture**,
not a cross-writer classifier or an acceptance correction. Reuse the existing
authored receipt reference and carry it with the exact operation that used the
Coordinator-issued authority. Do not reconstruct use from route-tuple equality.

Keep the stronger classifier disabled. Security byte correspondence now has a
live producer check, but exact publication use, validated two-writer causation
and safe detached handoff are still distinct, incomplete prerequisites. This
review specifies only the first missing producer edge; it does not approve
relaxing the existing Tuna resend/receive recovery predicates.

## Current-capture findings

Authoritative attempt: `4315f2af-630d-43aa-83ee-d51f62bb5390`, scenario
`regular-v4-live-activation-off-on-off-256mb`, attempt 1. Its retained input is
66,886 lines, SHA-256
`1f0c47e109cd4f282fa6cd209e1d5416f121f800d2c88a41436e0edbf6943249`.
The finalized verdict remains FAIL: 67 distinct rejection cases, 19 recovered,
48 unresolved. First: retained line 22848, range 4373-4375. There is a later
Tuna send at 23713, outside the existing pre-rejection recovery window. Do not
reuse the preceding attempt's claim that no exact-range resend exists anywhere.

Read-only inspection used the receipt-pinned, already completed full and compact
Snapshot summaries; it did not evaluate Snapshot authority again. Results:

| Evidence location | What is present | What is absent or rejected |
| --- | --- | --- |
| Original send 6068; rejected receive 22848; later Tuna send 23713 | Captured security context, payload identity and route tuple | No `publication_evidence_*` reference at any of these operation-use records |
| Fourteen accepted publication notifications | Each names one existing authored boundary | Existing `Resolve-FileTransferPublicationEvidence` returns no match for all 14 |
| Notification 131, outbound initial Regular | Coordinator `fte1-ab4060a8079f9629671be7a6`, boundary `fte1-2e8f6edfb1f3cb485030cdc9`, authored/observed generation 3 | Not a capture of send 6068's admission |
| Notification 22833, inbound second Tuna | Coordinator `fte1-c073945ea0dca92329a91f86`, boundary `fte1-943b208afd07bb055fad35d9`, authored/observed generation 231 | Not a capture of receive 22848's guard use |
| Persisted full and compact Snapshot summaries | Validated boundaries and matching existing resolver outcomes | Neither persists `__FallbackTailPeerProofBindings` nor `__RoutePublicationEffects` |

The resolver's first failing check for all notifications is explicit: raw
`session_id` is `sess_[redacted]`, whereas the separately emitted projected
session is `fte1-9f3ddb14d0ef354b508fb964`. The existing function accepts the
exact placeholder `[redacted]` or requires projection of the raw value to match;
hashing `sess_[redacted]` cannot satisfy that equality. The 14 named boundaries
being present does not override this rejection. No raw placeholder was replaced,
no event was rewritten, and no resolver outcome was promoted into a positive
publication-use result.

The existing resolver tests use constructed events/Snapshot objects; their pass
does not establish this live redactor shape is supported. This is a diagnostic
binding limitation, not evidence that the runtime publication was unauthorized.

## Exact source boundary

`FileTransferDataPathSendAuthority` copies publication fields but omits the
Coordinator instance, boundary and operation/receipt fingerprint reference.
Equal DTO values cannot choose between independent Coordinator histories with
the same route tuple. A matching later send-result or route-selected record is
not an admission event.

`CoordinatorSnapshotEmitter.GetRoutePublicationEvidence` already resolves the
exact token fingerprint to an authored accepted receipt and returns a private
`FileTransferRoutePublicationEvidence`. Its `StateGeneration` is the receipt's
authored boundary; `ObservedStateGeneration` is the emitter generation at lookup.
The lookup is evidence only, not an authority check or an offline validation.

For a future implementation:

1. Capture from that existing lookup using the **same token** that produced the
   exact send-authority object. For physical V4 submission, do so under the
   existing service gate after the existing current-scope/install/hold checks
   and successful physical-send lease registration, immediately before the
   existing submission call. Do not let diagnostic success become another send
   precondition, and do not treat authority construction alone as submission.
2. The pre-pump path has a separate existing install check and calls `SendAsync`
   after leaving the gate. Preserve that ordering and label its capture as a
   request, not a physical-send lease grant. Do not add a new revalidation or
   claim a stronger admission point than this producer actually observes.
3. Bind immutable diagnostic metadata to the exact authority object by reference
   identity, outside the public record's fields. An internal weak-key association
   is the recommended mechanism; a value-keyed map or added record backing field
   would not preserve the required identity/equality separation. Core already
   uses a weak-key metadata pattern for `FileTransferV6TransportEpochSnapshot`.
4. A reconstructed DTO or `with` clone must not inherit the association, even if
   value-equal. Missing associations remain unproven without changing dispatch.
   Runtime authorization, equality, hashing and public serialization must remain
   byte/behavior compatible. Do not introduce a public observation interface.
5. Carry this captured reference through the existing asynchronous operation.
   Emit it only with the actual payload's captured security evidence at the
   existing send-result sites. No latest-route lookup after `await`, association
   by range, payload metadata, log time or publication-generation number.
   Legacy/no-explicit-authority sends must not borrow route-cache evidence.

## Exact receiver boundary

The route-hint notification already contains private publication evidence.
`TrackAuthorizedFileTransferRouteHint` currently logs it but stores only the
authority and route tuple. The rejection guard correctly captures one inbound
tuple and reuses it for receive/mismatch/ignore; preserve that behavior.

The implementation candidate is a private diagnostic companion entry beside the
existing per-session/transfer/direction maps. Store the exact accepted authority
object, its copied route tuple and the notification's immutable evidence under
the same existing gate, only after the existing stale-authority check accepts.
This entry is never consulted by route selection, admission or any effect.

Read the entry together with the exact inbound tuple at the existing guard
lookup. Require reference identity with the authority object already stored for
that key, and exact scope agreement. An absent, replaced, inconsistent or
rejected-notification association supplies no diagnostic reference. Clear it
with the same existing removal path; a replacement with missing evidence must
not inherit the prior reference. Do not add fields to the route-hint record
whose equality might participate in runtime behavior.

Return a detached local capture for all members of that one rejection chain.
A publication change between logging calls must not relabel the original guard.
For `dispatch_entered`, capture the publication at the existing dispatch-time
lookup separately; do not borrow the earlier guard's observation or alter queued
dispatch behavior. Different capture points must remain distinguishable.

The forwarded reference's `ObservedStateGeneration` remains the generation at
the original service lookup. It is **not** a fresh Coordinator observation at
receiver guard time. A consumer must validate the original publication and
forwarding reference plus the actual guard-use capture; it must not relabel
that number or infer global current authority from the adapter cache.

## Diagnostic fields and scope rules

Reuse the existing `publication_evidence_*` field meanings without changing
`FileTransferRoutePublicationEvidence` or the Snapshot catalog. The proposed
additional marker is `publication_use_kind` with these exact values:

- `v4_physical_submission`: capture at the existing physical submission point.
- `v4_pre_pump_request`: capture after the existing pre-pump install check.
- `receive_guard`: copied authority used by the rejection guard.
- `receive_dispatch`: copied authority observed at the existing dispatch point.

The marker describes where evidence was captured, never permission or a
validated stale/delivery result. Absence is unproven. Repeated use of the same
authority is not a unique payload-attempt ID; bind each use to its own security
payload/sequence and original writer sequence.

A later private diagnostic resolver must match the exact projected publication
session/transfer to both the captured security and data-path scope tokens, then
to the validated boundary's scope and writer. Require complete, well-formed
equal token domains; raw redacted strings are not identity. Missing, ambiguous
or conflicting projected scopes remain unproven, even if raw placeholders match.
Unredacted raw scope, when available, must agree too. Never rewrite
`sess_[redacted]` to `[redacted]`, hash placeholders as identity, or loosen the
existing shared resolver to make this capture pass.

Pin the actual producer/redactor output on the selected data-path events,
including invalid field shapes and unrelated neighboring values. Keep raw peer
identity, approval grants, keys, plaintext and encrypted bytes out of logs.
No diagnostic failure may change the existing crypto, replay, rejection,
dispatch, progress, repair, liveness, route publication or recovery outcome.

## Gates for the bounded implementation

Only directly affected tests are required during the capture slice:

1. Real service publication and send producers: exact authored reference,
   independent Coordinator with equal route values, unrelated token, missing
   emitter, completed emitter, failed admission, non-authority session and
   unchanged authority equality/hash/public serialization; a value-equal clone
   must have no inherited metadata.
2. Async send replacement/reset/late completion: preserve the original capture,
   actual encrypted payload and secure sequence. Do not emit a newer publication
   or use evidence to cancel/allow the existing send.
3. Actual receiver guard: direction isolation, accepted replacement, rejected
   stale notification, missing-evidence replacement, cleanup, change between
   receive/mismatch/ignore logs, and separate later dispatch. State/progress and
   all existing no-effect assertions must remain unchanged.
4. Real producer/redactor fixtures: literal partially redacted session IDs,
   correct projected scope, wrong/missing projected scope, malformed evidence,
   duplicate/ambiguous references, no unsafe placeholder normalization.
5. Once stable, run directly affected Core/Infra/publication/analyzer lanes and
   at most one relevant broader contract lane. Keep existing recovery results,
   counts and primary verdicts unchanged. Do not run aggregate/full live suites.

No new classifier or handoff implementation belongs to this slice. The existing
eight tests run for this review are prerequisite checks, not evidence that these
new capture gates already pass.

## Later causation and handoff remain no-go

The production route-consistency cross-direction check compares ordered
epoch/route/protocol sequences. It does not name a causal pair of exact endpoint
publications. The passing paired-proof fixture exchanges real serialized
fallback proof frames and checks local receipt identities, but its writer
labels and experimental join are not production writer/receipt validation.

A later consumer must validate both writer/run histories and each exact local
publication/forwarding receipt, then prove the actual local-to-peer proof link
and its local lineage to second Tuna. No equality of local generations,
timestamps, aggregate hashes or security payload references supplies causation.

The in-memory validator owns the peer bindings/publication effects; the saved
full/compact summaries inspected here omit them. Do not reconstruct a positive
detached projection from those summaries or run a second authority evaluation
to populate an optional diagnostic. A future export must come from the already
completed full and independently generated compact evaluations, compare exact
logical links, respect the parked bounds, and preserve original locations.

The current worker serializes diagnostic eligibility with the primary response;
the GUI saves the completed primary before running its optional error companion.
Neither proves that a new expensive projection can safely precede primary
persistence, nor provides an explicit GUI-success/retained-success handoff.
Resolve those boundaries in their own tested slice. Missing/tampered/over-cap
projection, timeout or serialization failure must never alter primary adoption
or extend the worker budget.

Stop before another live 256 MiB run. It is justified only after an implemented
producer and its focused offline gates; this review does not start that run.

## Verification and source anchors

Review checks: **8/8 PASS** (2 exact receipt lookup, 4 paired-proof controls,
2 resolver hosts: Windows PowerShell and PowerShell 7). No production or test
source changed; the only tracked changes in this review are documentation.
Details and receipt: `artifacts/publication-use-contract-review-20260913/RESULTS.md`.

- Authority DTO: `IFileTransferSignalingTransport.cs:663`; existing notification
  evidence: `:709`.
- Receipt lookup: `SessionFileTransferService.CoordinatorSnapshots.cs:3097`.
- Authority copying: `SessionFileTransferService.cs:23284`.
- Pre-pump capture opportunity: `SessionFileTransferService.PullTransferSessionV4.cs:10678`;
  guarded physical submission: `:18514` and following lease/submission block.
- Accepted publication storage: `NknSignalingTransport.FileTransferTransportChannel.cs:2641`;
  cleanup: `:2618`; inbound lookup: `:2763`; rejection guard: `:3014`;
  dispatch-time observation: `:4495`.
- Existing resolver: `tools/FileTransferPublicationEvidence.ps1:24`.
- Cross-direction comparison: `tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1:15820`;
  private peer/publication collections: `:14620`.
- Worker response: `tools/GuiSmoke-Windows.ps1:31692`; primary persistence and
  optional companion: `:38023`.
