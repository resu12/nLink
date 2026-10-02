# Bounded content-consumer contract review

2026-10-01. Base commit `7ddbe15c3b894cf2497207cc39a08189b7578cc0` on
`v0.7.0`, with the uncommitted sender-evidence implementation preserved.
This follows the [capture implementation](file-transfer-content-evidence-capture-implementation-20261001.md)
and [rebatching contract review](file-transfer-rebatching-contract-review-20261001.md).

## Recommendation and scope

Proceed next with a default-off, test-only content-consumer shadow. Do not change
production analysis, runtime behavior, `Recovered`, failure counts, existing
shadow decisions or acceptance. This review does not adopt a new acceptance rule.
No new live test is required for that bounded implementation.

The proposed diagnostic claim remains `pre_rejection_tuna_content_coverage_v1`:
the exact bytes of one rejected old Regular-V4 chunk were observed in one
authenticated Tuna-V4 batch dispatched during the selected second Tuna
publication, before the old packet's rejection. It does not prove durable storage,
runtime key retirement, general stale-callback safety or a scenario pass.

Version 1 admits exactly one old chunk and one containing batch of one to three
segments. No range unions, multiple replacement batches, partial segments,
later receives, V6/fallback substitutes or other failure classes are admitted.
Commitment equality assumes the trusted producer and cryptographic construction;
it is not authentication against a malicious log writer.

## Findings that constrain implementation

The existing [Tuna-pair matcher](../tests/nLink.SmokeTests.Core/Support/PublicationTunaPairMatcher.ps1)
returns the first independently bound send/receive pair for an activation. That
is sufficient for its current claim, but not candidate-specific content coverage:
a disjoint pair may pass. Keep that behavior unchanged. The new query must
enumerate eligible containing pairs within existing bounds and require one
unambiguous pair; it must not borrow the first pair, choose the nearest row or
resolve ambiguity by testing commitments until one matches.

The [security-span matcher](../tests/nLink.SmokeTests.Core/Support/PublicationSecurityLifecycleMatcher.ps1)
currently binds eight selected packet/yield/rejection observations. Its passed
result does not cover additional Tuna send/receive observations. Bind those new
uses to their own recorded installation intervals; neither `captured` nor a
matching `content_scope_id` establishes that binding alone.

The two rebatching fixtures explicitly exclude reciprocal-yield mode. Their
existing byte/HMAC checks therefore validate the producer, not the complete
alternative-acceptance pipeline. Add a separate candidate-scoped test probe at
the existing [primary observer seam](../tests/nLink.SmokeTests.Core/Support/PublicationLivePrimaryObserver.ps1).
Reuse the existing custody, row-binding, rejection, publication-history and
security-identity validators. Do not make the new claim depend on an unrelated
yield shadow, weaken its prerequisites or synthesize missing yield records.

## Required proof chain

All checks are conjunctive. A missing, malformed, contradictory, ambiguous,
retired, out-of-window or over-budget input leaves this diagnostic unproven.

1. Start with the primary's already selected unrecovered
   `file_tuna_data_path_mismatch` candidate and its exact failure witness. Verify
   the same invocation's completed source/writer/native custody and passing,
   complete full/compact authority views. No other failure selection, source
   reopen or additional coordinator/recovery evaluation is allowed. Witness
   `ContentSha256` is a source-line custody hash, not a payload commitment.
2. Bind the rejected Regular receive to exactly one successful original Regular
   send using encrypted envelope/payload identity, security sequence and
   establishment, session/transfer tokens and exact batch metadata. Require the
   one-chunk shape, outbound physical submission, Regular-V4 publication and
   independently bound receiver-local rejection companions. Preserve the
   existing `rejected_before_dispatch` and `authority_effect=none` checks.
3. Select exactly one containing Tuna send/receive pair for the second Tuna
   activation in each completed view. Use each endpoint's own publication,
   forwarding receipt, observed state and local writer order. The successful
   physical send and `dispatch_entered` receive must match their own encrypted
   envelope/payload, security sequence/establishment, scope and batch shape.
   Neither encrypted identity is required to equal the differently encrypted
   old Regular packet. Reject superseded or terminal publication uses.
4. Require sender-local original-send sequence before Tuna-send sequence, and
   receiver-local second-publication sequence before Tuna-receive sequence
   before old-rejection sequence. Bind all four observations to the respective
   recorded security installation intervals, with verified capture endpoints
   and no intervening replacement. Reuse complete captured history for negative
   interval checks. Do not compare peer clocks, installation generations or
   coordinator generations. These are recorded observation orders, not a new
   claim about physical execution or secret lifetime.
5. Parse only the two canonical sender send-result suffixes. Require profile
   `sender_chunk_hmac_v1`, state `captured`, exact token shapes and no duplicate,
   unknown or extra content fields. Require all declared slots in canonical
   order, not just the matching slot; suffix size is at most 768 ASCII bytes.
   Counts equal frame/batch counts, indices are contiguous from the batch start,
   each length is 1–49,152 bytes, and their checked sum equals raw batch bytes
   and is at most 65,536. Chunk indices remain below 1,500,000. Canonical decimal
   integers must not overflow. No redaction placeholder or receiver-carried
   commitment may substitute for sender evidence.
6. Both sends must have the same sender writer, transport, installation ID and
   generation, establishment, session/transfer scope and content scope ID.
   Compare the old chunk's index, length and full commitment with the uniquely
   corresponding Tuna segment. Equality only supports this diagnostic when all
   preceding bindings passed; a range overlap or final SHA is not a substitute.

The HMAC profile intentionally excludes route and batch boundaries. It includes
session, transfer, logical index, length and serialized segment bytes. Matching
scope IDs do not grant publication or runtime authority. Recorded security
retirement is likewise diagnostic, not proof that runtime secrets were destroyed.

## Ownership, bounds and output

The test probe runs while the original primary still owns its private inputs and
completed views. It reuses those arrays, their origin mappings and verified
writer endpoints. The pure matcher receives bounded observations and existing
resolved evidence; it has no I/O, evaluator, runtime or acceptance capability.
Detached mutation controls cannot borrow original custody.

Keep the current test-probe ceilings: 4,096 source rows, 256 boundaries per view,
64 operations per boundary and 1–64 distinct original source-line witnesses in
total, including prior witnesses and newly used sends, receive, installation,
retirement, publication and forwarding records. Share the original cumulative
20-second optional-work budget and existing native/producer input accounting.
No fresh stopwatch, extra allowance, truncated search or raised limit. The
[production input limits](../tools/FileTransferOps/LocalNativeInputCapture.cs)
remain unchanged. Bounded complete-history inspection is not an export of every
inspected line; selected supporting witnesses must retain exact origin custody.

Proposed detached result: versioned policy, `Status=pass|unproven`, first missing
edge, selected candidate reference and, only on pass, a coverage record with
chunk index/length plus exact source references and local publication bindings
for old send and Tuna send/receive. Full and compact normalized results and
source origins must agree. Do not export payloads, secrets, keys or private
arrays; commitments already reside in the referenced private logs. Explicitly
record zero added authority evaluations, unchanged prior inputs/results and
`AcceptanceChanged=false`. Do not publish an overall alternative decision from
this content result.

## Bounded implementation and verification plan

1. Add one pure test-only matcher and one optional primary-seam probe. Reuse
   existing validators without changing their current callers or production
   tools. Keep the original two rebatching fixtures and private byte oracle.
2. Verify same bytes yield a separate content pass and a byte change remains
   unproven on PowerShell 5.1 and 7. Both must still report the original
   `missing_tuna_resend`, `Recovered=false`, one original candidate and three
   original evaluations. Exercise a matching chunk at another containing-batch
   slot so the query cannot silently hard-code slot zero.
3. Add detached controls for absent/malformed/retired/duplicate suffixes,
   wrong scope/index/length/commitment, invalid count/contiguity/sum, multiple
   eligible pairs, a disjoint first pair, missing/late/non-dispatched receives,
   replaced installation, superseded/terminal publication, missing forwarding,
   custody replacement, full/compact disagreement, witness and work exhaustion.
   Reuse the relevant existing pair controls rather than adding a second
   authority correlator. Partial results must not retain a successful coverage
   record after a later failed check.
4. Run only this consumer's focused cases and directly affected pair/rejection/
   lifecycle controls after it stabilizes. Verify unchanged inputs, prior
   results, production tools and historical artifact pins. No aggregate suite,
   portable rebuild or GUI matrix belongs to this slice.

Production integration and any policy cutover require separate review after
these tests pass. A later rebuilt targeted live run is needed to collect and
validate new evidence; old logs cannot acquire commitments retrospectively.
The other unresolved failures and independent route, fallback, ownership and
terminal-integrity gates are not waived by a content pass.

## Review verification and current state

The [review receipt](../artifacts/content-consumer-contract-review-20261001/verification.json)
rechecks all 15 accumulated changed/new code files, the prior capture source and
assembly pins, seven preserved test receipts, 16 rebatching artifact pins and
99 original live artifact pins. It pins the reviewed consumer seams and this
document. This is source inspection and hash verification, not an implemented
consumer test or a new live verdict. The prior 118 passing distinct test outcomes
remain historical verification of the producer.

This step changes documentation and ignored review evidence only. Runtime,
tests, tools, acceptance and historical verdicts are unchanged. All accumulated
work remains uncommitted; no live run, commit or push was performed.
