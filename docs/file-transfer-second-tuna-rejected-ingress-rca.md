# RCA and bounded fix plan: rejected NKN ingress after second Tuna activation

## Implementation update — 2026-09-10

The bounded diagnostic and provenance changes are implemented. The original
RCA below records the pre-change evidence; it is not a new historical evaluation.

- The exact rejection matcher now returns a structured diagnostic while its
  Boolean wrapper and recovery conditions remain unchanged. The missing exact
  second-Tuna receive has its own variant on the existing selected candidate.
- Private transport logging carries the captured send/receive publication and
  the same exact envelope fingerprint on send, receive, mismatch, and rejection.
  The fingerprint is `ftenv1-` plus lowercase SHA-256 of UTF-8
  `nLink/filetransfer/data-path-envelope/v1\0` followed by the exact MessageId.
  The separator ends with one NUL byte; no trimming or payload hashing occurs.
  Scoped session/transfer tokens use the existing `fte1-24hex` projection shape.
  Missing identities remain `(none)`.
- Rejected ingress is explicitly `rejected_before_dispatch` with
  `authority_effect=none`; the normal callback is `dispatch_entered`, not a
  claim that payload/progress was committed. Exact projected fields are protected
  only on the five named diagnostic records during operational-log redaction.
- Deterministic transport fixtures use real Coordinator-issued first/second
  Tuna publications, hold an actual authorized Regular envelope, then inject
  its original and a fresh same-range envelope. File-transfer lifecycle state
  stays unchanged and neither envelope reaches the delivery queue. The fake
  network does not exercise the real-adapter receive-recovery proof branch;
  exclusion of that branch is also checked by source ordering, not claimed as
  live-network coverage.

No routing, Coordinator policy, wire/DTO, Snapshot profile/catalog, timeout, or
recovery waiver changed. In particular, stronger rejected-envelope provenance
does not satisfy the existing exact Tuna-receive requirement. Historical helper
9564 remains unresolved and the immutable run remains failed. No new standalone
historical report, package, preflight, live attempt, or commit belongs to this
implementation slice. Focused validation details are recorded separately in
`artifacts/validation/consolidation-bounded-20260907/implementation-rejected-ingress/RESULTS.md`.

## Conclusion

Artifact `20260910T060311Z`, produced clean at `5e790d11`, completed both
256 MiB transfers with matching SHA-256 and both exact five-route histories:

`regular → Tuna → fallback → Tuna → fallback`

GUI and retained analyzer exited `0`. Capture, writer drains, finalization,
authoritative Snapshot V1 validation, full/regenerated-compact snapshot parity,
and selection of one completed retained receipt passed. Phase 5 nevertheless
failed its operator data-path correctness gate.

The first finding is **rejected ingress with unproven exact recovery**, not a
demonstrated acceptance of NKN payload under Tuna authority. The receive guard
worked. The retained rule deliberately requires an exact Tuna receive to close
this second-cycle mismatch, and that evidence is absent.

An old NKN delivery is a plausible explanation, but equal chunk ranges and byte
counts do not identify the same physical envelope. The historical logs cannot
prove that attribution. Do not call this a proven runtime misroute, a proven
analyzer false positive, or a recovered incident.

Recommended scope: first make the failed condition explicit and capture exact
envelope/disposition provenance in deterministic fixtures. Do not change routing
or waive the existing recovery requirement in this slice.

## Canonical finding and evidence

- Class/reason: `file_tuna_data_path_mismatch`.
- Event: `filetransfer_data_path_receive_result`.
- Writer: helper `p2548rb3c18267645046c1`, sequence **9564**.
- Retained line: **21716**.
- Current variant: `(none)`.
- Proposed diagnostic variant:
  `second_tuna_rejected_regular_ingress_exact_tuna_receive_missing`.
- Current quartet: **25/25/18/7**, unchanged by this plan's diagnostic step.
- Separate scalar count `14` counts the seven unresolved receive records and
  their seven explicit mismatch companions; it is not the quartet's raw count.

The first batch is `filetransfer.chunk_batch.v4`, bulk, chunks `4602–4604`,
64,512 raw bytes, three chunks. Actual/delivery transport is `regular_nkn`;
current inbound authority is `file_tuna_v4/4`, target Tuna, epoch 3, runtime/leg
4, publication 4, commit 2, terminal 0.

| Retained line | Writer/sequence | Evidence |
|---|---|---|
| 6601 | sender 9278 | Same-range Regular-NKN V4 send, publication 1, `sent` |
| 19981 | sender 24514 | Same-range Tuna V4 send, publication 4, `sent` |
| 20186 | helper 9370 | Published route diagnostic: PostFallback V6 → Tuna V4, exact candidate scope |
| 21716 | helper 9564 | NKN ingress logged against current Tuna authority |
| 21717 | helper 9565 | Receive-side transport mismatch diagnostic |
| 21718 | helper 9566 | Explicit `file_tuna_v4_payload_requires_tuna` rejection |
| 21986 | sender 29173 | Later same-range V6/NKN send after final fallback |
| 29761 | helper 15710 | Later same-range V6/NKN receive |

There is **no matching V4/Tuna receive anywhere in the retained input** for the
first range. The later V6 delivery and terminal SHA do not supply one.

All seven unresolved ingress candidates have the same missing-Tuna-receive
condition and explicit rejection evidence:

| Chunk range | Helper receive / mismatch / ignore sequences | Retained receive line |
|---|---|---:|
| 4602–4604 | 9564 / 9565 / 9566 | 21716 |
| 4605–4607 | 9591 / 9592 / 9596 | 21739 |
| 4608–4610 | 9603 / 9604 / 9605 | 21750 |
| 4872–4874 | 9615 / 9616 / 9617 | 21762 |
| 4926–4928 | 9622 / 9623 / 9624 | 21769 |
| 4917–4919 | 9629 / 9631 / 9632 | 21776 |
| 4923–4925 | 9636 / 9637 / 9638 | 21783 |

Matching publication-4 Tuna sends exist for all seven. Matching Tuna receives
do not. Six earlier Tuna batches, `4584–4601`, were received; this does not prove
receipt of the subsequent or sparse ranges.

## Causal source trace

1. `TryPrepareFileTransferDataFrameDispatch` validates secure metadata/session,
   resolves the actual inbound transport, and invokes
   `ShouldRejectFileTunaV4PayloadReceive` before state tracking.
2. That guard calls `LogFileTransferDataPathReceiveResult`, which writes
   `delivery_result=received` and the mismatch diagnostic. It then writes the
   ignored record and returns `true`.
3. The caller returns `false` before `TryValidateAndTrackFileTransferDataFrame`,
   receive-recovery proof observation, dispatch work creation, or frame delivery.
   Here `received` means physical ingress, not accepted application delivery.
   Generic session-security liveness observations at helper 9562–9563 precede
   this guard; the claim is not that absolutely no earlier state changed.
4. `Test-FileTransferFileTunaV4DataPathMismatchRejectedFailClosed` recognizes the
   rejection chain and finds the earlier Regular-NKN send and exact second-Tuna
   receiver transition. Its PostFallback→Tuna branch also requires a matching
   Tuna resend and receive before the candidate.
5. At `ArtifactWriters.ps1:10066`, the resend collection is nonempty but the
   receive collection is empty. The function returns false, leaving the candidate
   unresolved. This is not a missing-ignore, byte-count, or route-tuple mismatch.

The diagnostic ambiguity is real: one logging method serves both rejected
ingress and entry to the dispatch callback, and neither these data-path records
nor their rejection companions include the physical envelope identity. The
existing second-cycle positive fixture supplies a matching Tuna receive and
therefore does not represent this production shape.

`Envelope.MessageId` is already available locally on both send and receive paths;
the private logging/guard interfaces omit it. A wire change is not necessary to
record stronger provenance. No queue, transport-label, or packet-loss defect has
been established by this audit.

## Recommended implementation order

### 1. Explain the unresolved result without changing acceptance

- Add a small structured diagnostic result to the existing exact rejection
  matcher, retaining its current Boolean success semantics for existing callers.
- Distinguish missing rejection companion, missing earlier source send, missing
  route transition, missing Tuna resend, and missing exact Tuna receive.
- Report explicit `rejected_before_dispatch` evidence separately from transport
  mismatch and from recovery status. Use the proposed variant only when the
  exact rejection chain and all other required predicates validate.
- Preserve counts, candidate location, arbitration, and fail-closed behavior.
  Helper 9564 must remain unresolved; all 18 already-recovered candidates stay
  unchanged. Do not exclude every record accompanied by an ignore diagnostic.
- Reuse the existing bounded indexes. This is not a new chronological correlator
  or reporting-performance project.

### 2. Capture exact local envelope and rejection provenance

- Pass a private, immutable evidence context from the existing outgoing envelope
  and authenticated incoming envelope into the data-path logging helpers.
- Include a deterministic privacy-safe envelope fingerprint: SHA-256 of a fixed
  domain separator and the existing exact `Envelope.MessageId`, with one defined
  UTF-8 encoding. Distinct fresh IDs for same-range sends must remain distinct.
  Bind it to session/transfer evidence identities and the existing frame/range/
  byte-count fields; do not hash or log plaintext, credentials, or peer addresses.
- Preserve source send authority from the existing captured Coordinator-issued
  send authority. Preserve the exact receiver publication used by the rejection
  guard; do not re-read a newer route merely to format the rejection record.
- Include the same envelope fingerprint on send, ingress, mismatch, and rejection
  records. Add explicit `rejected_before_dispatch` versus `dispatch_entered`
  disposition and `authority_effect=none` for the rejected path. The latter
  disposition means the delivery callback was entered, not that the service
  committed payload or progress. Do not assert application delivery from ingress.
- Keep the guard, return path, secure validation, deduplication, route policy,
  deadlines, and Coordinator authority unchanged. Use private plumbing only;
  do not add fields to public DTOs or wire frames.
- Test the fingerprint through operational redaction and regenerated compact
  selection. Missing or conflicting identities remain unbound; never reconstruct
  historical identities from chunk ranges, timestamps, or redacted tokens.
- Treat this as non-authority diagnostic evidence. Snapshot V1 profile/hash,
  seven-family coverage, effect catalog, and legacy snapshot routing stay frozen.

### 3. Validate provenance before proposing any acceptance-rule change

Use a production-shaped deterministic transport fixture to queue an authorized
Regular-NKN envelope, complete the real two-cycle route setup, then deliver the
old envelope while Tuna is current. Deliberately omit its same-range Tuna receive.
Require an exact old-send→ingress fingerprint match and rejection before all
file-transfer state/progress/repair/dispatch effects. Separately prove that a same-range fresh
envelope does not borrow the old send's provenance.

Stop after this evidence contract passes. Any new `proven_stale_rejected`
classification or removal of the same-range Tuna-receipt requirement needs a
separate explicit semantic decision. It must distinguish rejection safety from
successful data delivery and retain genuine unauthorized send/acceptance failures.
Do not change the historical quartet to zero simply because the transfer finished.

## Focused tests and gates

1. Extend `NknTransport_FileTunaV4PayloadReceivedOverRegularNkn_IsRejectedBeforeDispatch`
   to verify exact disposition/fingerprint and absence of file-transfer state
   tracking, receive-recovery proof, dispatch, progress, or authority effects.
2. Add first- and second-Tuna cases, old/fresh same-range envelopes, duplicate
   callbacks, wrong session/transfer/publication, missing/competing identities,
   route replacement, terminalization, and ABA controls. Preserve accepted Tuna
   delivery and unrelated inbound Regular operation.
3. Extend `BuildFileTunaV4RejectedReceiveFixture` with the actual missing-Tuna-
   receive shape. It must remain `1/1/0/1` with the precise diagnostic. Preserve
   current positive recovery and missing-companion negatives. Add a fixture with
   physically valid route-publication-before-Tuna-receive ordering; do not rely
   only on the older synthetic fixture's pre-transition receive placement.
4. Parse affected PowerShell; Release-build the directly affected Core/Infra and
   smoke-test projects. Run only focused transport, redactor, data-path index,
   `RetainedAuthorityProofSuite=FileTunaV4DataPathProof`, and affected summary tests.
5. Run one generated transport→redactor→full/regenerated-compact data-path contract
   lane after stabilization, including both PowerShell hosts and unchanged
   normalized candidate location/counts. Test the shared diagnostic consumer
   separately from the GUI's authoritative early-return behavior.

The actual GUI passed, but that is **not** independent GUI reproduction of this
operator data-path finding: authoritative GUI classification returns before its
legacy data-path branch. Snapshot boundary parity is also not data-path-rule
parity. Keep that integration discrepancy as a separate follow-up; do not make a
false full/compact/GUI parity claim here.

## Stop conditions and preserved evidence

- At RCA preparation, no implementation or execution was performed: only source and
  finalized artifact inspection plus this plan.
- Retained input remains 60,067 lines, SHA-256
  `478b7f7aaf8802458f8cb57d1d8a0019696db9810c3334a57d4656d5e2c27683`.
- Preserve the immutable run, its failed Phase-5 result, completed receipt, and
  all current unrelated worktree changes. No evidence reconstruction or retroactive
  acceptance.
- The standalone-report allowance is exhausted at three reports; the live campaign
  is exhausted at six attempts. This plan authorizes neither another report nor a
  package/preflight/live launch. Request a fresh bounded validation allowance only
  after the deterministic evidence gates and semantic decision are complete.
- Reporting cost (734.258 seconds in artifact writing), GUI data-path integration,
  any remaining candidate with a different cause, and actual Tuna delivery loss
  each require separate evidence and scope. Do not increase a timeout, alter
  throughput, or tune retries as part of this correction.

## Source references

- [Receive guard and early return](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L2960>)
- [Incoming secure envelope dispatch](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L4319>)
- [Captured outgoing authority logging](<../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs#L2841>)
- [Exact rejection/recovery matcher](<../tools/FileTransferSoak/ArtifactWriters.ps1#L9850>)
- [Decisive resend/receive requirement](<../tools/FileTransferSoak/ArtifactWriters.ps1#L10066>)
- [Candidate and scalar count construction](<../tools/FileTransferSoak/ArtifactWriters.ps1#L15449>)
- [Existing rejection regression](<../tests/nLink.SmokeTests.Core/NknFileTransferTransportTests.cs#L2544>)
- [Existing second-cycle retained fixture](<../tests/nLink.SmokeTests.Core/FileTransferRetainedAuthorityProofAnalyzerTests.cs#L1340>)
- [Final attempt record](<../artifacts/validation/consolidation-bounded-20260907/live-retry-confirmation-20260910/RUN.md>)
- [Immutable selected evidence](<../artifacts/filetransfer-route-acceptance/20260910T060311Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-retained-log-slice.log#L21716>)
