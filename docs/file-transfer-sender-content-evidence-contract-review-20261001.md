# Sender content evidence contract review

2026-10-01. Reviewed checkpoint `7ddbe15c3b894cf2497207cc39a08189b7578cc0`
with the local [rebatching fixture](file-transfer-rebatching-fixture-implementation-20261001.md).

## Decision

A capture-only producer is a feasible next implementation under the contract
below. It would provide sender-local chunk commitments that survive rebatching,
while leaving the wire, Coordinator authority, route selection, retry, progress,
strict matcher and acceptance unchanged. This review does not implement the
producer or adopt a new acceptance rule.

The intended bridge has three independent bindings. First, the existing
encrypted-payload identity connects the old Regular send to its exact rejected
receive. Second, a new sender-local commitment compares that old chunk with its
segment in the containing Tuna send. Third, the existing exact encrypted Tuna
send/receive pair connects that send to authenticated receiver dispatch. The
receiver needs neither a shared diagnostic key nor a new content hash.

These bindings would establish the bounded content claim proposed in the
[acceptance contract review](file-transfer-rebatching-contract-review-20261001.md).
They would not establish disk persistence, execution-time key lifetime, general
stale-callback safety or a whole-scenario pass. Missing bindings remain unproven.

## Current hooks and constraints

The [raw send path](../src/nLink.Infra.Nkn/NknSignalingTransport.FileTransferTransportChannel.cs)
receives a private serialized plaintext array. `CreateSecureFileTransferPayloadWithEvidence`
captures the security installation used for the key copy, encrypts those bytes,
and captures the encrypted-payload fingerprint. The operation also captures
`FileTransferPublicationUseEvidence.GetSend(dataPathSendAuthority)` before the
transport await. The existing send-result record is emitted after successful
delivery. Budget estimates use a separate method and must remain excluded.

The [V4 codec](../src/nLink.Core/FileTransfer/FileTransferDataFrameCodec.cs)
can decode that exact serialized array. It requires matching chunk and segment
counts, a valid contiguous range, complete consumption and at most three
segments. The [chunk budget](../src/nLink.Core/FileTransfer/FileTransferChunkBudget.cs)
limits one raw segment to 48 KiB and a raw or serialized batch to 64 KiB.
The current decoder allocates segment arrays in `TryReadBytes`, then copies
them again in normalization. Its intermediate arrays are not returned for
cleanup, including on malformed-input exits. Reusing that allocating decoder
would not satisfy a promise to clear every additional payload copy.

The recommended first slice therefore adds an internal read-only codec
projection of canonical IDs and at most three segment offsets/lengths, sharing
the codec's reader, frame constants and validation rules. It must create no
segment arrays, reject incomplete or trailing data, and leave existing
serialization and decode behavior unchanged. Infra can hash the projected
slices of the exact serialized array synchronously. It must not reread mutable
caller arrays after an await or duplicate the wire parser in Infra.

The [security context](../src/nLink.Infra.Nkn/NknSignalingTransport.SecurityEvidence.cs)
has explicit installation and retirement hooks under `controlSecureStateGate`.
Same-key reinstall still creates a new installation. The transport also has
accepted terminal-state and exact data-session removal paths. None currently
owns a content secret. Existing send authority has no data-session object
reference, so a producer must carry the originating session privately from the
adapter call site; it must not add that reference to an authority DTO.

## Proposed producer contract

Capture only an actual serialized `FileTransferChunkBatchFrameV4`, excluding
V6 subclasses, control frames, estimates and test observer callbacks. Require
an explicit outbound protocol-4 send authority and its existing
`v4_physical_submission` association. Eligible routes are
`regular_nkn_v4_fast` with Regular NKN and `file_tuna_v4` with Tuna. A route
cache or a similarly shaped DTO without the captured association supplies no
replacement binding.

Validate that the decoded canonical session and transfer IDs match the secure
metadata, captured security installation and publication association. Use the
already validated ID spelling with ordinal equality; do not introduce case
folding or another normalization rule. Any mismatch omits the optional content
evidence and preserves the existing send outcome.

Use a fresh 32-byte random diagnostic secret for one sender transfer,
originating data-session instance and security installation. Never derive it
from an encryption key, export it, persist it or send it to the receiver. Give
the scope a random opaque ID, proposed as `ftcs1-` plus 32 lowercase hex digits.
Scope IDs and commitments are diagnostic references, not Coordinator tokens.

For each segment, compute HMAC-SHA256 over this unambiguous encoding:

1. The fixed ASCII domain `nLink/filetransfer/content/v4-chunk/v1` and a zero byte.
2. The UTF-8 canonical session and transfer IDs, each preceded by its unsigned
   32-bit little-endian byte length.
3. The nonnegative logical chunk index and positive segment byte length, each
   encoded as unsigned 32-bit little-endian values.
4. The exact segment bytes projected from the serialized plaintext used for
   encryption.

Batch boundaries, route publication and encrypted-envelope identity are
excluded from the HMAC input so equal content can compare across rebatching.
Those fields remain separate required bindings on each send record. Proposed
commitment encoding is `ftcc1-` plus the full 64 lowercase hex digits; do not
truncate it or substitute an unkeyed plaintext hash.

## Secret lifetime and capture races

Keep one active outgoing diagnostic scope slot per security context, with no
per-chunk history or new dictionary of retired secrets. Bind its owner by the
originating data-session object identity as well as canonical transfer scope.
An unexpected competing owner makes its evidence unavailable; it cannot replace
the occupied slot or affect admission. The existing one-outgoing-transfer
admission rule is not new authority for this diagnostic slot.

Retain the same scope across Regular and Tuna route publications for the same
owner. Retire and zero its owned secret on security reset/reinstall, accepted
transfer termination, exact owner removal/disposal or transport disposal.
Retirement must detach the active slot. An old removal or completion must never
retire or recreate a replacement owner's scope. A new transfer or installation
gets a fresh secret and scope ID; equality cannot bridge that boundary.

Synchronize secret use and retirement with the existing security gate. Hash
only the bounded projected batch while that scope is valid; never hold the gate
through network awaits, logger calls or a Coordinator lookup, and introduce no
nested route gate. If the captured installation retires between key copy and
optional capture, omit the commitment rather than borrowing the new context.

Freeze the commitments and wire-derived index/length tuple before transport
awaits. Retain no plaintext, live secret or adapter owner reference in the
operation's emitted evidence; a small nonsecret retirement marker may accompany
the immutable record. A delayed completion must preserve its original scope
and publication, and report retired/unavailable evidence when appropriate.
It must not relabel itself from current adapter state.

Capture status cannot prove that a context remained current throughout an
asynchronous send. Any later consumer must also check original writer-local
installation, retirement, publication and terminal ordering. A retirement that
precedes the send record cannot be waived by a `captured` field. Secret zeroing
is lifecycle hygiene, not a claim of resistance to process-memory inspection.

## Logging and privacy bounds

Use the existing [trace opt-in](../src/nLink.Core/FileTransfer/FileTransferDiagnosticLogPolicy.cs).
Production trace is off by default; tests have an existing automatic override.
When disabled, do not allocate a secret, project content metadata or append
content fields. Enabling diagnostics after an earlier send cannot invent that
send's missing commitment.

Append only to the existing canonical `filetransfer_data_path_send_result`
record. Do not duplicate the suffix on peer-visible or delivery-trace records,
add a second log queue, wait for persistence or emit per-chunk events. Require
the captured wire tuple to agree with that record's scope, range, byte total and
count; a mismatch omits the suffix without rewriting existing fields. Capture
creation is not send success: a failed or canceled transport attempt cannot
publish a successful content record.

Proposed fields are `content_evidence_profile=sender_chunk_hmac_v1`,
`content_scope_id`, `content_evidence_state`, `content_segment_count`, and slots
0 through 2 containing `index`, `bytes` and `commitment`. Retired evidence may
identify its old scope but must not offer usable segment commitments. All fields
must be ASCII and invariantly formatted, with a hard 768-byte suffix limit.
The conservative three-slot proposal is 666 bytes using maximum-width indices
and 48-KiB lengths. This is a format bound check, not a runtime benchmark.

Retain the existing log queue, retention, witness and work limits. Optional
capture or redaction failure must not change transport results; missing or
truncated metadata makes a later comparison unproven. Stream projected spans
into the hash provider without assembling a second payload/preimage array.
Dispose the provider and clear owned secret material on every retirement. No
new payload cache, background task or retained per-send history is needed.

Extend [redaction](../src/nLink.Core/Logging/SensitiveDataRedactor.cs) only for
these exact field names, token shapes, slot numbers, profile and canonical
single-event line. Do not allow a general `content_*` field or token-prefix
exception. Wrong profiles, duplicate/ambiguous fields, malformed tokens,
unrelated events and cross-line copies must not receive protection. Raw
plaintext, secrets, keys, addresses and payload-bearing fields stay redacted.
The intended residual disclosure is equality at the same logical index within
one private diagnostic scope. Commitments are trusted producer observations,
not authenticated evidence against a malicious log producer.

## Bounded implementation and verification plan

First add the internal read-only Core codec projection and a small Infra helper
for this exact HMAC encoding and scope retirement. Test that each projected
offset and length identifies the exact bytes consumed by the unchanged V4
codec, with no retained span or payload copy. Test equality across batch shapes
and inequality for changed bytes, index, length, transfer and secret. Reject
malformed/trailing data, wrong scope, V6 and excessive shape. Verify secret
zeroing and the suffix bound. No new public API, authority DTO or wire field is
needed. The Core assembly already grants internal access to Infra.

Then integrate capture at actual V4 physical sends and append its frozen
metadata to the existing send-result record. Add focused tests for opt-out,
estimates, missing/copied publication association, serialization followed by
caller mutation, failed/canceled delivery, reset after key copy, reset or
same-key reinstall while delivery awaits, terminal/removal races and competing
owners. Verify no borrowed scope, key retention, route/progress effect or new
dispatch. Add the narrow redaction controls before enabling exported fields.

Reuse the two rebatching fixtures to compare exported commitments with their
private actual-byte assertions. Process their original analysis on PowerShell
5.1 and 7, retaining `missing_tuna_resend`, three original evaluations and
full/compact custody/parity. Run only the directly affected security,
publication and redaction lanes after the implementation stabilizes.

Do not implement a correlator, classifier or acceptance change in this producer
slice. A later content consumer must reuse primary-owned witnesses and existing
authority results, keep the 64-witness bound, require the complete three-part
binding, and reject changed/missing/ambiguous/late evidence. Review that consumer
separately before any policy adoption. A targeted portable/live rerun becomes
useful after the producer and its offline bindings are verified; a full matrix
is not the next gate.

## Review verification and current state

The [review receipt](../artifacts/content-evidence-contract-review-20261001/verification.json)
pins 12 relevant source files, verifies the proposed suffix bound, confirms the
prior fixture source and test-receipt hashes, and rechecks all 99 original live
artifact pins. This was code inspection and a proposal format check. No new
test or live run was performed, and the proposed lifecycle implementation has
not been tested yet.

This step adds documentation and ignored verification evidence only. The four
earlier test-file edits remain intact. Runtime, tools, acceptance and historical
verdicts are unchanged; nothing was committed or pushed.
