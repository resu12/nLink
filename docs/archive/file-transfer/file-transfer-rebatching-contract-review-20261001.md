# Rebatching acceptance contract review

2026-10-01. Reviewed checkpoint `7ddbe15c3b894cf2497207cc39a08189b7578cc0`.
This follows the [batch-shape RCA](file-transfer-second-tuna-batch-shape-rca-fix-plan-20261001.md).

## Decision

**Do not relax the matcher using the current logs.** They can identify an exact
encrypted envelope on both peers, but cannot establish equal chunk content
between different envelopes. A containing range is insufficient.

Recommend a **test-only one-chunk to three-chunk reproduction** as the next
implementation. It should demonstrate the proposed content-coverage claim with
actual adapter traffic and private fixture byte comparisons while preserving
the current strict failure. No production evidence producer or acceptance
cutover is approved by this review. No new live test is needed yet.

## What the existing evidence proves

`NknSignalingTransport.SecurityEvidence.cs` computes `security_payload_fingerprint`
over the **encrypted secure-envelope bytes**. Send capture receives the output
of `SessionSecureEnvelopeCodec.Encrypt`; receive capture uses the received
encrypted payload after successful decryption and replay validation. Matching
fingerprints support exact-envelope correspondence, not a reusable plaintext
chunk identity. Encryption includes a fresh random nonce, so even unchanged
plaintext gets a different fingerprint on another encryption.

The V4 frame codec exposes each segment's bytes and length while encoding or
decoding. It enforces a valid contiguous range, matching segment/count fields
and at most **three segments**. Current operational evidence emits the batch
range, total bytes and count, not per-segment content commitments.

The sender repair cache can also reread a seekable source after a cache miss.
This is not evidence that the latest run's source changed; it explains why a
chunk index or cache history is not itself an exported content-equality proof.
The test-only offline frame observer exposes metadata and an envelope hash,
explicitly not payload bytes or authority. It does not fill this gap.

| Existing observation | Supported claim | Unsupported extension |
| --- | --- | --- |
| Matching Tuna encrypted-envelope fingerprint on send and receive | The observed pair refers to the same encrypted payload | The pair contains the same bytes as a differently encrypted old packet |
| Same session, transfer, chunk index and length | Same declared logical position and size | Equal content |
| Receiver-local publication and writer order | Receipt belongs to the selected local route interval and precedes rejection | Equality between independently numbered peer generations |
| Final file SHA and size | Completed file matches the expected file | Which packet supplied a chunk before rejection |
| Local-publication forwarding receipt | The bounded local publication reference was forwarded | Peer delivery, plaintext equality or durable storage |

In the current live witness, the three-chunk Tuna pair and the rejected
one-chunk Regular envelope have separate encrypted-payload fingerprints.
Neither fingerprint can be decomposed to compare their shared chunk. The
historical run therefore remains unproven under the proposed content rule too;
this is a statement about missing evidence, not a new analyzer verdict.

## Proposed candidate claim

Proposed name: `pre_rejection_tuna_content_coverage_v1`, reported separately from
legacy `Recovered`, mismatch counts and acceptance. This is a proposal, not an
implemented public schema.

The claim would be: **the exact bytes of one rejected old Regular-v4 chunk were
observed in one authenticated Tuna-v4 batch dispatched to the receiver during
the selected second Tuna publication, before the old packet's rejection**.
It does not claim a disk write, durable commit, absence of all stale-callback
effects or a whole-scenario pass.

For the first version, keep the scope to one rejected chunk and one containing
Tuna batch of one to three chunks. Do not add unions of several batches,
partial overlaps, later receipts, V6 payloads, fallback substitutes or other
failure classes. Wider coverage would need a separate demonstrated use case.

All of the following would be required:

1. Preserve original input custody, primary-owned candidate selection and
   passing full/compact authority views. Use the existing evaluation; never
   reconstruct authority from copied log fields or run another evaluator.
2. Bind the old Regular send and exact rejected receive through their encrypted
   envelope, security establishment/sequence, canonical session/transfer and
   payload shape, including the current rejection companions and recorded
   security/publication-history checks. Missing or ambiguous bindings remain
   unproven.
3. Bind one successful Tuna send and dispatched receive as an exact pair using
   their own encrypted identity, range, bytes and count. Validate each use
   against its own Coordinator-issued publication and local installation
   history; require the selected second activation, no intervening supersession
   or terminal boundary, and no conflicting observations. A queued send is not
   a successful pair. Do not infer execution-time key-lifetime proof from logs.
4. On the sender, the old send precedes the selected Tuna send. On the receiver,
   the second publication precedes the Tuna receipt, which precedes the stale
   rejection. Use writer-local sequences; do not compare peer generations or
   wall clocks as a causal clock. Do not alter the current matcher's window.
5. Prove equality of the old chunk and its exact segment in that Tuna batch,
   with matching canonical scope, chunk index and segment length. A direct
   private byte comparison can establish this in a controlled fixture. Current
   production logs cannot supply it. Any malformed, missing, conflicting or
   out-of-bound evidence leaves the proposed result unproven.

All independent route-cycle, ownership, fallback, terminal and file-integrity
gates remain. Every candidate must be accounted for before any future policy
adoption; a pass for the first witness cannot waive the other 67 unresolved
candidates in the latest run.

## Smallest possible production evidence bridge

This is a design direction for later review, **not part of the next test-only
implementation**. A sender-local content commitment could connect the old
encrypted send to the containing Tuna send. The existing exact Tuna encrypted
send/receive pair would then connect that send to its receiver observation.
This avoids inventing a shared cross-peer generation or changing the wire.

If later pursued, capture the commitment from the exact immutable serialized
plaintext used for encryption, not the original mutable `DataSegments` arrays
after an asynchronous send. Bind it to that send's encrypted fingerprint,
captured publication evidence and writer record. A diagnostic must never grant
send authority or change transport selection, retry, progress or callback effects.

Do not log raw payload, encryption keys or plain globally reusable chunk hashes.
A candidate design is HMAC-SHA256 under a fresh, sender-local diagnostic secret
scoped to one transfer/security installation, never exported or sent to the
peer. A versioned, unambiguous encoding would bind canonical session/transfer,
logical chunk index, length and bytes, but exclude batch boundaries. Emit an
opaque scope ID and at most the existing three segment commitments per send.
Both sends must use the same retained diagnostic scope; a reset, retirement,
missing commitment or capture failure makes the comparison unavailable.

This proposal needs privacy, key lifetime, capture atomicity, bounded logging
and redaction tests before production approval. Equal commitments would be
trusted diagnostic observations under the existing artifact threat model, not
new Coordinator tokens or a defense against a malicious producer. No new key
sharing, replay cache, unbounded history or mandatory production logging is
justified by this review. Do not raise existing work or witness limits.

The simpler no-new-metadata alternative remains the separately reviewed
[independent route-use policy](file-transfer-alternative-acceptance-contract-review-20260925.md).
That policy intentionally drops the rejected-chunk replacement requirement;
it must not be presented as proof of this stronger content-coverage claim or
silently adopted here.

## Next implementation boundary and checks

Reuse `NknFileTransferTransportTests.TwoWriterIngress.cs` and the existing
`PublicationTunaPayloadIntegration` lane. Its real adapter fixture already
holds a Regular envelope, publishes both peers' second Tuna routes, sends a
Tuna batch, receives it through the data-session API, and releases the old
packet afterward. Add an isolated opt-in rebatching mode; preserve default
fixtures and production files.

The positive should hold one chunk, send it inside a three-chunk Tuna batch,
compare the actual decoded/received segment bytes privately, then verify the
original packet is rejected without new dispatch or state change. Keep those
byte assertions fixture-owned; do not export them as a production authority
receipt or attach them to historical evidence. Explicitly assert that the
unchanged primary still reports `missing_tuna_resend`.

Focused negative controls should cover:

- Changed bytes at the same index, with identical lengths, batch range and count.
- Absent Tuna receive, disjoint range, wrong segment index/length and malformed
  segment/count shape.
- Different writer, transfer, security installation or publication; duplicate
  ambiguous pair; supersession or terminal boundary before use.
- A Tuna receipt only after rejection, a fallback-only replacement, and final
  SHA without the required pair.
- Optional fixture output omitted or failed: original primary, counts, source
  locations, full/compact parity and authority-evaluation count stay unchanged.

First implement the real one-to-three positive and same-shape changed-content
negative. Reuse existing pair/publication negative controls instead of building
a second correlator. Then run the directly affected strict exact/missing/wrong-
range cases on PowerShell 5.1 and 7. No aggregate suite, production evidence
capture, portable rebuild, live run or acceptance cutover belongs to that slice.

## Verification of this review

Six codec checks passed using a fresh isolated Release build of unchanged Core:
same bytes across different batch shapes; changed bytes in the shared subrange;
identical range/count/length despite that change; different encrypted
fingerprints for identical plaintext; successful decryption of both copies;
and fingerprint identity for the same encrypted envelope.

Receipt: `artifacts/rebatching-contract-review-20261001/payload-identity-probe.json`.
These are synthetic codec counterexamples, not the proposed two-writer test,
live route proof or a Coordinator evaluation. The probe used PowerShell 7.6.6;
it is not a new two-host acceptance-test result.

The initial build attempt encountered a DLL lock held by the inspection process.
An isolated output build succeeded. Informational version `0.7.0` intentionally
omits the commit ID; custody uses a fresh build, unchanged tracked production
sources and recorded source/assembly hashes, not that version string. The probe
compiler uses the PowerShell .NET 10 references with the Core .NET 8 assembly;
only reference-unification warning CS1701 was suppressed for this scratch probe.

All 99 original live artifact pins were reverified. Runtime, tracked tests,
analyzers, acceptance and original verdicts are unchanged. This review adds only
documentation and ignored scratch evidence; nothing was committed or pushed.
