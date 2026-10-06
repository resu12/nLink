# Proposal: proven stale rejection is not delivery recovery

Status: design review reopened on 2026-09-13; classifier remains disabled.
See the [current review](file-transfer-security-provenance-contract-review.md).
The 2026-09-10 classifier no-go below remains historical and is not overturned.
Historical baseline: `d408e58c`.
This document proposes a contract; it does not authorize implementation,
an acceptance-rule cutover, historical reclassification, or another live run.

Feasibility follow-up: the user approved the first bounded slice. It stopped
before implementation at the missing validated cross-writer publication link;
see the [no-go report](<file-transfer-stale-rejection-feasibility.md>).
The complete classifier and acceptance changes remain unapproved.
The separately approved paired-proof test slice subsequently demonstrated a
candidate identity/receipt link through real Coordinator histories; the report
records its passing controls and unproved production-writer/validation boundaries.
The final read-only binding review confirmed missing security-incarnation,
production writer-ownership and cross-writer projection/handoff proof. The user
approved scoped local closeout, not further implementation. The proposal below
is retained as design history; its former approval gates are not a current plan.

## 2026-09-10 closeout decision

Park the stronger classifier and preserve current acceptance. Retain the
test-only candidate and the final no-go report; do not expand this contract as
an automatic next step. Any work on the original missing-Tuna-receive issue
requires a separate scope. Reopening this proposal first requires a separately
approved security-provenance evidence contract, not classifier implementation.

## Original proposed decision (not adopted)

Recognize `proven_stale_rejected` as a narrowly evidenced rejection-safety
outcome, separate from the existing delivery-recovery result. The proposed first
implementation stage is only a 30-active-minute provenance/handoff feasibility
slice. It must not enable the new verdict. A complete diagnostic classifier
requires a separate go/no-go and implementation approval after that slice.

Do not map this outcome to `Recovered=true`, remove the exact Tuna-receive
requirement from the existing matcher, subtract it from existing mismatch
counts, or make it an automatic operator/GUI pass.

A later, explicit acceptance decision may consider a proven safe rejection
non-blocking for the ingress-safety subgate. That would be a policy change, not
a parser correction. It must specify the independent delivery obligations and
all affected operator/GUI consumers before adoption. This proposal does not
approve that change or assume that completion/SHA alone proves Tuna delivery.

## Two independent questions

| Question | Required proof | What it does not establish |
|---|---|---|
| Was this an old, authorized Regular-NKN frame rejected safely under a later Tuna publication? | Exact authenticated source identity, validated publication history, and an explicit bounded rejection chain | Tuna receipt, application delivery, progress, or transfer success |
| Did the existing second-Tuna recovery condition pass? | Every existing predicate, including the matching Tuna resend and exact Tuna receive | That every other ingress, route, authority, or finalization obligation passed |

Both results may coexist. In particular, a receive may be proven stale and
rejected while its existing recovery result remains false with
`missing_exact_tuna_receive`. Neither result grants runtime authority.

## Critical prerequisite: MessageId is not authenticated payload identity

The current `data_path_envelope_fingerprint` is `ftenv1-` plus SHA-256 of a
domain separator and the outer `Envelope.MessageId`. It is useful correlation
evidence, but it is not a payload hash or proof that a particular authenticated
payload was previously sent under an old publication.

The secure-envelope codec authenticates version, family, message type, session,
sender identity, secure sequence and request/transfer identity. Its authenticated
data does not include the outer MessageId. Receive decryption is given
`env.Payload` and the expected session/sender/type, not that MessageId.

Therefore, a fresh authenticated payload carrying a reused old outer MessageId
must not borrow the old send's provenance, even when range and byte counts match.
The existing informational companion is not an implementation of this stronger
contract and must not be relabeled as one.

Before any positive `proven_stale_rejected` result, require an additional private
identity binding derived from the actual authenticated metadata:

- On send, capture the metadata used for that exact encryption operation,
  including its allocated secure sequence. Never re-read a mutable next-sequence
  counter after an asynchronous send.
- On receive, capture the metadata returned by successful authenticated
  decryption and the existing secure/session/transfer validation. Do not extract
  an unverified identity from an undecoded payload.
- Use a separate, versioned diagnostic domain for the authenticated identity;
  do not change the meaning of `ftenv1-`. Reuse the codec's canonical
  authenticated-data encoding rather than duplicating normalization rules.
  Pin the domain and exact bytes in focused tests before implementation lands.
- Preserve the existing outer fingerprint as an additional consistency check.
  Require both identity bindings to agree across the source send and rejection
  records. A changed outer ID remains unproven in this initial narrow contract.
- Keep raw peer identity, plaintext, keys and payload bytes out of diagnostics.
  The new binding is a privacy-safe projection, not a new wire field.

The authenticated tuple must be unique within its validated session/sender/run
scope. Conflicting reuse, a sequence reset without a new validated scope, or
ambiguous restart/ABA lineage prevents a positive result. The contract assumes
trusted local evidence producers; it is not an attestation against a compromised
endpoint. No adjacent liveness record or timestamp may supply the missing tuple.

This is a prerequisite for the proposed stronger classification, not a request
to change encryption, replay policy, public DTOs, or the wire protocol. If a
narrow private projection cannot establish it, stop for review instead of
weakening the identity requirement or broadening the security design.

## Positive proof: every condition is required

Initial scope is a V4 bulk payload received over Regular NKN while the receiver
has the second Tuna publication following PostFallback V6. Existing first-cycle
recovery behavior and unrelated message families remain outside the change.

1. **Complete, trustworthy inputs.** Use immutable, identity-checked evidence
   with valid writer continuity, run ownership and the applicable existing
   authority/parity checks. Missing streams, malformed fields, duplicate keys,
   conflicting identities or bootstrap boundaries prevent a positive result.
2. **One exact old source identity.** Bind the authenticated identity and outer
   fingerprint to an authorized outbound Regular-NKN V4 send. Require a unique
   source authority binding. Same ranges, byte counts, nearby timestamps and
   redacted placeholders are not source identity. Conflicting reuse cannot be
   resolved by choosing the most convenient candidate.
3. **Validated older publication.** The source send's captured authority must
   bind uniquely to an old Regular publication, and the receiver's captured guard
   authority to its later `file_tuna_v4/4` publication, through the immutable
   validation projection specified below. Diagnostic route labels and global
   eligibility flags are insufficient. Require the projection's exact validated
   lineage links; local writer/publication numbers never establish cross-writer
   order. A missing, ambiguous or unsupported binding is unproven.
4. **One explicit receiver chain.** Bind one receive, its mismatch companion and
   its ignored companion by writer identity/sequence, both identity bindings,
   projected session/transfer scope and captured receiver publication. Preserve
   the existing bounded companion window and conflict/terminal/route-change
   vetoes; no broad forward search across another receive or publication.
   The receive, mismatch and ignore must explicitly record
   `rejected_before_dispatch` and `authority_effect=none`, with the exact
   `file_tuna_v4_payload_requires_tuna` ignore reason.
5. **Consistent frame evidence.** Frame type, lane and chunk range must agree
   wherever present in the existing records. Payload-bearing send/receive/mismatch
   records must also agree on raw byte count and chunk count. The ignore record
   uses its existing range and identity fields; do not invent absent byte fields.
   Receiver authority is Tuna V4, actual ingress is Regular NKN, and direction is
   inbound. Sender and receiver directions are deliberately different.
6. **No exactly bound contradictory dispatch or effect.** Apply only the
   attempt key, window and explicit contradiction rules below. A positive
   contradiction forbids a safe result. An incomplete required binding is
   unproven. Absence of effect records is never evidence of no effects; rejection
   safety also depends on the explicit guard disposition and producer boundary
   tests. Later legitimate delivery under a different publication is separate
   and does not close the earlier Tuna-recovery proof.

Interleaved telemetry is allowed only within the existing exact bounded chain.
Duplicate mismatch/ignore companions are ambiguous. A duplicate callback creates
no new safe result unless it has its own valid candidate chain; never manufacture
a receive record from a deduplication log.

The legacy matcher's derived `ReceiveDisposition` is insufficient: it currently
sets that value after recognizing the old companion chain, including evidence
without explicit disposition fields. The stronger predicate must inspect the
recorded fields and authenticated binding, not that derived summary alone.

## Bounded publication-proof handoff

The existing companion receives eligibility flags and input descriptors; its
nine-event adapter does not receive validated Snapshot publication or peer-proof
objects. Those flags must never stand in for the bindings required here.

The proposed producer is a read-only projection of the **already completed**
full and independently regenerated compact validation results in the existing
primary enrichment process. Relevant current inputs include `Boundaries`,
`__RoutePublicationEffects`, applicable already-validated peer-proof bindings,
and the completed route-consistency result. The existence of these collections
does not establish that every required join is available; the first stage must
demonstrate the exact joins with a real Coordinator fixture.

The private projection has this fixed logical shape; it is diagnostic evidence,
not a new Coordinator token or an authorization to perform effects:

| Projection part | Required contents and identity |
|---|---|
| Header | Schema version 1; enrichment request correlation; contract/catalog versions and hash; full/compact boundary hash; route-consistency hash; completeness/parity results |
| Input binding | The exact ordered input descriptors already used by enrichment: absolute path, decoded line count, `utf8-lines-lf-v1` content hash; no substituted or additional input |
| Publication rows | Coordinator instance, writer, projected session/transfer, direction, boundary ID/hash, original writer sequence, operation key/authority ID/generation, validated receipt identity/generation/status/fingerprint, and the complete existing route-state boundary tuple |
| Publication-forwarding references | Exact validated `route_publication_forwarded` effect identities and their boundary/operation/receipt references; no bare route diagnostic qualifies |
| Lineage links | Explicit from/to publication references, link kind, and the existing validated boundary/operation/receipt references that establish it; cross-writer links additionally name both writers and the existing validated peer-proof identity |
| Optional contradiction bindings | Exact attempt-to-operation/effect links, only when supported by existing validated causal evidence as defined below; no invented links |

A publication reference is the tuple `(coordinator instance, writer, boundary ID,
operation key, operation generation, receipt identity, receipt generation,
receipt fingerprint)`. Its row must match all source/receiver authority fields
that the payload record actually captures; any omitted fields must come from
that uniquely validated row, never defaults or the latest cached publication.
The route-state tuple is the existing validator's complete tuple, not a new
partial route/epoch approximation. Session/transfer token domains must be equal
or have an explicit producer-supported mapping; similar redacted strings do not
join. Source authorization must be established for the send's captured authority;
a later send-completion timestamp is not an admission timestamp.

Required lineage demonstrates the old Regular source publication and the
receiver's Regular -> Tuna -> PostFallback V6 -> second Tuna history with exact
existing causation links. If the validator exposes no supported cross-role or
publication-use binding for the fixture, the feasibility result is **no-go**.
Do not synthesize links from equal generation numbers, wall-clock order or
per-transfer proximity. Security-sequence reset/ABA exclusion must likewise bind
to an existing validated session incarnation, or remain unproven.

Copy only rows relevant to the observed candidate subjects. Fixed upper bounds
for this proposal are 64 publication rows, 128 lineage links, 128 contradiction
links and 256 KiB of serialized UTF-8 projection data per request. The source
input list is inherited, not a new allowlist. Overflow omits the entire optional
projection with an explicit reason; never truncate and treat the remainder as
complete. These caps do not change runtime deadlines or transfer limits.

Derive the semantic projection independently from the existing full and compact
validated results and require equality of its logical rows before exporting it.
Do not rely on an aggregate pass flag or compare only row counts. Bind the
canonical serialized projection to its own content hash; retain physical source
references outside its semantic parity hash. Pin serialization/order and byte
test vectors in the first-stage design before a later classifier consumes it.

Pass the bounded, detached scalar/array projection through an optional private
enrichment carrier. Copy no mutable evaluator objects or raw Snapshot graph into
the child. Missing/invalid projection data must not reject primary adoption.
The projection must not extend worker lifetime, restart the budget, require a
second validation pass, or make diagnostic serialization a prerequisite for
saving the completed primary response. The feasibility slice must demonstrate
this preservation using the actual enrichment handoff; if safe export requires
an orchestration redesign, stop rather than silently expand the slice.

After primary adoption and atomic persistence, the isolated companion checks
request/input/hash binding, version, bounds and unique row references, then uses
the projection as read-only validated evidence. It never evaluates Snapshot
authority or grants new authority. Missing projection means no positive result.
Retained diagnostics consume the equivalent projection from their already-run
validator, with the same logical schema and predicate, not a second authority
evaluator. The nine-event diagnostic selector remains distinct from this
projection; extending the Snapshot mandatory event catalog is not implied.

## Exact contradiction scope

The candidate attempt key is `(receiver writer, receive writer sequence,
authenticated identity binding, outer fingerprint, projected session/transfer,
captured receiver publication reference)`. Physical file position is not in this
key. The enclosing evaluation interval is that receiver publication's validated
start through its validated retirement/terminal boundary, or the verified end of
the complete writer stream when still current. The narrower receive -> mismatch
-> ignore companion window remains governed by the existing chain rules.

The initial contradiction set is deliberately limited:

1. **Explicit dispatch.** A `filetransfer_data_path_receive_result` with recorded
   `dispatch_entered`, matching both identity bindings, receiver writer and exact
   publication reference, contradicts safe rejection in that interval. Scan its
   bounded identity/publication bucket, including records after the ignore; do
   not stop at the next receive and miss a later matching dispatch. This
   conservative rule also rejects any ambiguity between attempts for the same
   envelope in one publication. Different authenticated identities or genuinely
   different publications are not this contradiction.
2. **Explicitly bound applied effect.** A validated Coordinator authority effect
   contradicts this attempt only if the projection supplies an exact causal link
   from the attempt key to its `(coordinator instance, transition ID, operation
   key, operation authority/generation, receipt identity/generation/fingerprint,
   effect identity/generation/kind)`. Follow only existing validated links;
   `authority_effect=applied` plus a matching transfer/route is insufficient.
   Any effect kind with such an exact causal link is disqualifying, so a new
   effect kind cannot evade the veto. Conflicting declared links are unproven.

Current effect records do not themselves contain the authenticated payload or
receive-attempt identity. Do not claim that all effects are already joinable.
Unlinked concurrent effects do not contradict this candidate, and an empty
effect-link list does not prove that no effects occurred. If an input explicitly
declares a link but its referenced proof is missing/invalid, return unproven;
do not drop it as unrelated. Existing global authority failures continue to block
eligibility independently, whether or not an effect binds to this candidate.

The no-file-transfer-effects guarantee comes from the authenticated rejection
guard returning before tracking/recovery/dispatch and directly observing that
boundary in deterministic producer tests. It is not derived from missing logs.
This initial contract does not introduce a generic attempt-to-effect correlator,
new effect authority, or a public/wire attempt ID. If broader causation evidence
is required to support the claim, stop for a separate scope decision.

## Proposed diagnostic semantics and preserved consumers

Preserve the existing canonical identical-receive-record domain: coalesce only
identical original receive-record content, ignoring the existing appended
`gui_evidence_sequence` location field. Identical copies in full, compact or split
files are one candidate. Do not deduplicate merely by envelope/range, and do not
discard conflicting content sharing a writer/sequence or authenticated identity;
retain that conflict and prohibit a positive result.

The logical reference uses the original writer identity/sequence together with
original-record content identity/conflict checks. Physical absolute source path
and one-based local line are provenance only; preserve the current first-encounter
source selection for identical copies. They are neither candidate-count keys nor
semantic parity fields. A genuine duplicate callback at a new writer sequence is
a separate candidate, not another physical copy of the same record.

Normalized full/compact/GUI parity compares logical candidate identity, validated
publication/lineage references, authenticated/outer bindings, status/reason and
unchanged recovery fields. Exclude physical paths/lines and input-container
hashes from that comparison while validating them independently against each
input. Preserve an explicit original global evidence sequence when available;
never substitute a compact ordinal. Missing original writer identity is unproven.

The rejection-safety dimension has four conceptual outcomes:

- `not_evaluated`: ineligible input, no remaining budget, timeout or diagnostic
  execution failure. Counts remain unavailable, not zero.
- `unproven`: evaluation completed, but a required identity, source, publication
  or rejection proof is missing/ambiguous.
- `proven_stale_rejected`: every positive condition above passed.
- `contradicted`: positive conflicting evidence, such as same-attempt dispatch,
  incompatible authenticated identity, or an unauthorized source send. This must
  never hide an existing hard failure; absence alone is not a contradiction.

Keep these diagnostics additive and separate from `sharedMatcherRecovered`,
its reason/variant, the existing raw/distinct/recovered/unresolved quartet, the
legacy scalar and primary candidate arbitration. A receive and its mismatch
companion are not two safety candidates. Recovered and proven-stale counts are
different dimensions; do not add them into one "success" total.

Use one small shared predicate/index implementation for retained diagnostics and
the isolated GUI companion. Preserve immutable-input binding, primary adoption
and atomic persistence before optional diagnostics, original remaining budget,
process cleanup, and original exception behavior. The bounded projection above
is the only proposed new validated-evidence handoff. No broad analyzer import,
Snapshot re-evaluation, primary GUI cutover, or new mandatory event catalog is
implied. An input missing a necessary record yields unproven/not-evaluated.

## Focused validation required before adoption

| Fixture/control | Required safety result |
|---|---|
| Held, actually authorized old Regular envelope; real Coordinator second-Tuna publication; exact authenticated identity; explicit rejection; no matching Tuna receive | Proven stale rejection; existing recovery remains false |
| Same-range fresh authenticated payload with a fresh MessageId | Unproven source identity; no borrowing |
| Fresh authenticated payload with the old outer MessageId/fingerprint | Not proven; authenticated identity must prevent borrowing |
| Old authenticated payload under a changed outer MessageId | Unproven in this narrow contract |
| Missing authenticated binding, legacy-only inferred disposition, or helper 9564's historical shape | Unproven; current failure/counts unchanged |
| Wrong session, transfer, sender, secure sequence, range, byte count, source publication or receiver publication | Not proven; retain existing hard failures |
| Competing source bindings, duplicate companions, replacement, terminal boundary, restart or ABA ambiguity | Not proven; no heuristic selection |
| Same-attempt dispatch-entered evidence or related file-transfer state/progress/recovery effect | Contradicted; no safe classification |
| Matching dispatch after the ignore but inside the same validated publication interval | Contradicted; the narrow companion window must not hide it |
| Concurrent dispatch/effect for a different authenticated identity under the same transfer/publication | Does not contradict this candidate; primary authority checks still apply |
| Applied effect with an exact validated attempt-to-effect link | Contradicted, including an otherwise unknown effect kind |
| Declared attempt-to-effect link with missing or invalid referenced proof | Unproven; do not silently drop the link |
| Absent, tampered, wrong-input, stale-request, incomplete, over-cap or full/compact-divergent publication projection | No positive result; primary adoption and persisted summary unchanged |
| Valid aggregate Snapshot pass but no exact candidate publication/lineage binding | Unproven; pass flags do not stand in for bindings |
| Identical receive copied into two different input files or relocated by compaction | One candidate; identical logical result; independently correct physical provenance |
| Same writer/sequence with conflicting record content, or distinct callback sequences for one envelope | Preserve conflicts/separate attempts; no accidental coalescing |
| Authorized Tuna delivery and ordinary valid Regular delivery | Existing behavior unchanged; no manufactured rejection candidate |
| Independent full/regenerated compact inputs and isolated GUI companion on PS5.1/PS7 | Identical normalized safety records; primary decisions unchanged |

Extend the existing production-shaped transport/redactor fixtures. Capture the
old envelope rather than synthesizing its provenance; use real Coordinator-issued
publications. Observe unchanged file-transfer lifecycle state and no delivery
queue entry. Generic secure-session liveness may precede the guard and is not
claimed absent. Fake-network coverage is not proof of live adapter execution;
retain that limitation unless a directly relevant adapter test actually runs.

Run only directly affected regressions during development. After stabilization,
run the relevant producer/redactor/shared-consumer checks and at most one joined
generated-evidence contract lane. Do not run an aggregate suite, historical whole
report, portable rebuild, preflight or GUI/NKN campaign as an implicit dependency.

## Original first implementation bound and approval gates (historical)

The first approval requested after review is **at most 30 cumulative active
engineering minutes**, including implementation, focused tests and write-up.
Run one agent unless separately authorized. Unattended build/test elapsed time
is recorded separately and does not reset the active-time cap. No positive
`proven_stale_rejected` output is enabled in production during this slice.

Deliverables are limited to an exact authenticated-identity encoding/capture
design and one production-shaped feasibility fixture demonstrating the bounded
publication projection, immutable handoff and primary-result preservation. Add
only the narrow private provenance/projection plumbing needed for that fixture;
do not implement a broad effect correlator, a new primary predicate, policy
cutover, or live-validation workflow. Use directly affected focused checks;
the later complete generated-evidence lane is not an automatic part of this
feasibility slice.

At the cap, stop and report which joins and boundary checks were actually proved.
A go result requires matching send/decrypted-receive authenticated identities,
the fresh-authenticated-payload/reused-outer-ID negative, exact full/compact
projection parity and successful missing/tampered-projection primary-preservation
controls, not merely fields in a mock response. If even the positive
publication-use/lineage join is unavailable,
report no-go and its first missing edge without adding heuristics or extending
the clock. A complete classifier remains separately scoped and approved.

1. Review this proposed meaning and the authenticated-identity prerequisite.
   Draft approval is not implementation approval.
2. Obtain explicit approval for the 30-minute feasibility slice above. It does
   not approve the full diagnostic classifier or alter legacy recovery/acceptance.
3. Review the feasibility result, remaining engineering allowance and exact
   non-authority/primary-preservation checks. If viable, separately approve a
   concrete bounded classifier implementation and its focused contract lane.
4. Separately decide whether and how proven rejection safety affects a future
   operator/GUI acceptance contract. List unchanged delivery, route-cycle,
   integrity and finalization obligations explicitly; a diagnostic must not
   silently waive them or alter a completed historical verdict.
5. Only then request any fresh bounded package/preflight/live validation allowance.

Stop if the authenticated/source-publication proof cannot be bound with narrow
private evidence, if consumers require authority/schema redesign, or if the
agreed engineering allowance is exhausted. Do not relax predicates, add heuristic
fallbacks, tune retry/queue/performance behavior, or infer a campaign restart.

## Historical disposition and source anchors

`20260910T060311Z` remains failed at helper 9564. Its full capture hash matches
the saved ledger; all seven affected ranges have an exact-range Tuna send and
an explicit NKN rejection but no Tuna receive record. The second switch-off was
invoked about 728 ms before helper 9564. This is consistent with delayed ingress,
not proof of its physical origin or the reason the Tuna receipt is missing.
The required new identity binding cannot be reconstructed from that capture.

Saved quartet `25/25/18/7`, scalar `14`, and retained input SHA-256
`478b7f7aaf8802458f8cb57d1d8a0019696db9810c3334a57d4656d5e2c27683`
remain unchanged. Standalone reports: three consumed. Live campaign: 6/6 exhausted.
Preparing this proposal runs no tests/reports and changes no runtime or acceptance
code. Unrelated frontier work and ArtifactWriters changes remain untouched.

- [Current outer-ID fingerprint](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L2802>)
- [Secure metadata allocated for send](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L5602>)
- [Authenticated-data fields](<../src/nLink.Core/SessionSecurity/SessionSecureEnvelopeCodec.cs#L248>)
- [Receive decryption and replay boundary](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L5850>)
- [Guard before file-transfer state tracking](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L4363>)
- [Legacy-derived rejection disposition](<../tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1#L726>)
- [Unchanged missing-Tuna-receive result](<../tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1#L951>)
- [Existing validated publication collections](<../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1#L14499>)
- [Current eligibility-only handoff](<../tools/GuiSmoke-Windows.ps1#L7244>)
- [Existing effect identity and binding fields](<../src/nLink.Core/FileTransfer/SessionFileTransferService.CoordinatorSnapshots.cs#L3192>)
- [Historical full-capture receive](<../artifacts/filetransfer-route-acceptance/20260910T060311Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-gui-log-ledger-slice.log#L38278>)
