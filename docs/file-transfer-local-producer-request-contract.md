# C1b: local producer/request contract review

Date: 2026-09-14. Base HEAD: `ef78e9d7`, with the uncommitted A/B1/C0/C1/C1a work.
This review pins the next producer boundary and executable input-identity vectors.
It does not implement a production exporter or close the preparation/lifetime gate.

Implementation follow-up: [C1c bounded native-input capture](file-transfer-local-native-input-capture-contract.md)
now supplies the incremental fingerprint/owner-token mechanism with fixed work
limits. It does not yet bind file/writer membership, selected rows or completed
views, and deliberately cannot issue a publication-completion receipt.

[C1d's diagnostic binding consumer](file-transfer-local-native-input-binding-contract.md)
adds exact source/writer/native membership and selected-row parity over supplied
objects. Frozen-file and evaluator-invocation ownership still require the real
primary producer; the consumer does not issue completion or close this gate.

[C1e read-only input leases](file-transfer-local-input-read-lease-contract.md)
add optional file-object identity without replacing ordinary primary readers.
[Primary failure-witness selection](file-transfer-primary-failure-witness-contract.md)
implements the approved selection policy at the compact primary's existing
first-failure seam. Neither primitive is a parent-bound completion issuer.

[C1f retained source ownership](file-transfer-local-retained-source-request-contract.md)
now associates the parent's frozen source with its exact worker through an
internal default-off seam. It is source-only: the trusted writer ledger,
completed evaluator contexts and independent completion issuer remain separate
requirements. Live call sites and V2 export remain disabled.

[C1g native writer capture](file-transfer-native-writer-capture-contract.md)
preserves bounded immutable provenance from the original output write/hash pass.
The native capture alone cannot attest full ledger validation or completion.
[C1h](file-transfer-local-gui-writer-request-contract.md) now adds private
finalizer custody and exact GUI-parent process adoption through an opt-in channel.
Carrying that accepted owner into retained analysis and binding its actual
writer/input/evaluator contexts remain separate gates; live export stays disabled.

[C1i](file-transfer-local-retained-writer-binding-contract.md) adds the typed
accepted-GUI/source association at both retained-parent function boundaries and
a lifetime-bound, independently verified writer-reader API. The upstream process
relay and actual primary preparation/evaluator integration are still disconnected.

[C1j](file-transfer-primary-evaluation-input-contract.md) now keeps a bounded,
private association with the actual compact primary input arrays and returned
views. Selected writer membership, A/B1 preparation and completion issuance remain
open; this is not a completed producer gate or permission to enable live export.

[C1k](file-transfer-primary-publication-witness-contract.md) now integrates the
primary's exact first-failure witness with verified writer membership and selected
full/reduced A/B1 parity. Private completion issuance, dynamic expectation transport
and the upstream process relay remain open. No live export is enabled.

[C1l](file-transfer-primary-evaluation-custody-contract.md) adds private one-shot
custody at the two original evaluator return sites and immutable invocation-pair
identities. It gates C1k preparation without promoting its mutable context or JSON
to publication completion. Descriptor/row completion binding and transport remain
open; C1a and live callers are unchanged.

[C1m](file-transfer-primary-publication-completion-contract.md) adds typed native
ownership and primary-local immutable payload/completion preparation. It does not
consume diagnostic prepared JSON or caller-provided expected digests. The request
projection is distinctly named; complete parent output-namespace/channel binding
and adoption remain open. Live export is still disabled.

## Decision

[C1n](file-transfer-primary-publication-channel-contract.md) now adds an opt-in
parent-bound request/namespace, private issuer control channel and exact-worker
transport receipt. Live relay and post-terminal-receipt adoption remain separate
gates; the receipt primitive is not permission to skip those checks.

[C1o](file-transfer-publication-adoption-contract.md) now supplies opt-in private
adoption after the original terminal receipt at both retained parents. It binds
the exact written return and original source/artifact/catalog facts, preserving
failed-proof status and throws. The actual upstream GUI-owner relay remains
disconnected; these completed local seams do not enable live export.

[C1p](file-transfer-local-gui-writer-relay-contract.md) adds the opt-in one-hop
accepted GUI-owner relay into the normal RouteAcceptance function. Wrapper and
GUI identities remain distinct; the actual retained-source request accepts the
relayed context. The combined offline real-report/adoption integration and
top-level live consumption/persistence remain separate gates.

Keep C0's `nlink-local-publication-forwarding-v1` and its five file-byte
descriptors unchanged. A future native-input producer needs a distinct V2 format,
not an alternate meaning of the V1 `absolutePath` or `sha256` fields. Keep C1a's
current pre-known-digest API disconnected while that producer is implemented.

The selected direction is a parent-issued request, primary-owned completed-view
context and separate producer-completion record. The parent adopts only after
its existing terminal receipt. Payload bytes cannot provide their own expected
request, input digest, completion provenance or payload digest. A second checksum
field or mailbox, by itself, is not trusted producer provenance.

The minimal first producer scope is one already frozen retained input, TailMinutes
zero, an already validated frozen writer ledger, primary-selected failure
witnesses, and no synthetic augmentation. Missing prerequisites omit the optional export. This does
not filter, change, or reevaluate the primary input to make it eligible. Supporting
augmented or multiple-source views later requires its own explicit origin binding.

## Source facts and the corrected input premise

`Read-FileTransferLogEvents` retains original `FilePath`, `LineNumber`, `Sequence`
and `RawLine`, but skips blank/non-event lines. Thus native all-event position 1
can name physical source line 3. Parsing a rotation BOM for event recognition
does not remove that code unit from `RawLine`.

`Invoke-FileTransferRetainedAnalysis` then calls
`Add-FileTransferLiveHarnessEvidence`. On GUI-progress-timeout evidence this can
append synthetic records with `LineNumber=0`, a stdout origin, current timestamp
and no operational-writer identity. A retained-file hash alone therefore cannot
identify all evaluated full-stream bytes. Do not assign these records fake writer
sequences, source offsets, or membership in the retained file.

Both full and compact writer paths independently evaluate an all-event string
array and a Snapshot-authority/effect-only selection. The reduced selection
preserves order and duplicates but has its own offsets. The full path's existing
invocation-local reuse compares the entire ordered array, not merely its boundary
hash. Saved summaries omit private forwarding effects. None of these facts is
changed by this review.

## Parent request: identity and selection policy

Revision approved by the user on 2026-09-14: the parent requests the fixed
`primary_reported_failure_witnesses_v1` policy, not preselected physical lines.
Neither retained parent currently owns such a pre-launch selection. Selection
therefore belongs to the primary analysis which already chooses the reported
proof failures. This revision does not authorize another authority evaluation,
an automatic latest/nearest publication search, or a new pre-launch scan.

The request owner is the actual RouteAcceptance or Wrapper retained parent. Its
request ID is canonical lowercase GUID D representing the same GUID as that
parent's existing lowercase GUID N attempt ID. Do not generate a second identity.
Keep owner, acceptanceRunId and scenarioRunId as separate exact fields; scenario
IDs are not assumed to be GUIDs. A child cannot substitute any of these values.

The immutable request context contains:

- Format `nlink-local-publication-producer-request-v1`, request/attempt identity,
  owner, acceptance/scenario IDs and the fixed `local_publication_forwarding` scope.
- The parent-pinned retained source path, exact file-byte SHA-256 and decoded line
  count already used by the primary attempt. The writer input descriptor and
  successful run/writer ledger validation must come from its existing trusted
  owner, not from an optional payload. If unavailable, omit without an extra
  pre-launch whole-input scan or new file-copy operation.
- The fixed failure-witness policy and maximum 64 selected physical source lines.
  Exact selections are produced later by the primary, not supplied by the payload
  or predicted by the parent. The completion record separately binds their digest.
- Parent-owned optional output namespace, cancellation/freeze lifetime and exact
  launched process handle. The payload cannot choose paths or nominate a process.

## Primary-owned failure witnesses

The initial integrated scope is the primary's existing first proof-chain failure,
not every unresolved candidate and not a new failure classifier. At most one
reported failure is selected in this scope; the envelope ceiling remains 64 for
distinct exact witnesses should the primary already report more in a later slice.
Do not truncate an over-cap selection or replace an unsupported first failure
with a later convenient one. No reported failure means no optional export.

Capture the exact event used to populate the primary report's class, reason,
event name, source path, physical line and original writer sequence. Bind it to
the same parsed input object and original raw string while primary ownership is
live. A copied summary row, matching tuple, caller-authored event or diagnostic
cache is not an issuer. The selected line must itself contain the supported
publication-use capture; no neighboring use, scope join or offset conversion can
stand in for it. Snapshot-only and synthetic origins omit this initial scope.

Every selected witness carries its report class/reason and exact source location,
writer ID, positive original writer sequence and raw-record content hash. Its
publication/forwarding rows still require all independent full/reduced A/B1 checks.
Selection does not claim that the reported failure was recovered or explained;
it only names the original local evidence to inspect. Primary failure ordering,
classification, summaries and receipts remain unchanged.

Physical source lines, full capture positions and reduced Snapshot positions
are distinct fields. Each selected use must have unique physical membership in
both capture views and an exact writer/sequence/raw-record match. A duplicate
logical request, duplicate membership, changed input or absent mapping omits the
whole export. Never sort/deduplicate primary records to satisfy this requirement.

## Native input identity: framed UTF-16 code units

Reserve domain `nlink-ordered-utf16le-v1` for exact .NET `string[]` identity.
SHA-256 covers, in order:

1. ASCII bytes of `nlink-ordered-utf16le-v1` followed by one zero byte.
2. Unsigned 64-bit little-endian array length.
3. For every array element in original order: unsigned 64-bit little-endian
   UTF-16 code-unit length, then each code unit as unsigned 16-bit little-endian.

Null arrays/elements and non-string-array inputs are invalid. Empty arrays and
empty strings are different identities. Do not coerce an object array or null
element to strings before validation. There is no encoding fallback, newline,
case, BOM, Unicode normalization, trimming, deduplication or sorting. An unpaired
surrogate is fingerprinted as its exact code unit; this does not permit it in a
C0-style JSON semantic field, whose canonical codec still rejects invalid Unicode.

This is not `sha256-file-bytes-v1` and not the GUI companion's `utf8-lines-lf-v1`.
LF joining has a counterexample: `['A','B']` and a single string containing
`A<LF>B` have the same joined bytes. Framing avoids that ambiguity. Native strings
decoded from LF/no-BOM and CRLF/BOM files can agree while original file-byte hashes
differ. Retain both identities; neither substitutes for the other.

Each native descriptor must bind its role, native hash/count, producer request,
source-file identity and exact selection/catalog identity. Derived arrays do not
claim an `absolutePath` of their own. The V2 envelope retains separate descriptors
for writer, full capture, full Snapshot, compact capture and compact Snapshot;
equal inputs may share an identity, but location roles remain distinct. Parent
source offsets remain alongside native offsets. Do not reinterpret V1 provenance.

Pinned native SHA-256 vectors:

| Input | SHA-256 |
| --- | --- |
| Empty array | `e0d5da8ece8aa85f55de0491cfa4a8a0b0024f40b01d5474a499cc244679e670` |
| One empty string | `17e6c97aa6bc619bdfc959ad84158419aee8404a32d0bc7ff0df90189d31b644` |
| Two strings `A`, `B` | `53b9fc58d0134ef0a2f9ec7563a61bd21a11401c87e0432cb5589b268cbc2f54` |

The test reference implementation scans and buffers its whole small input. It is
an executable format definition, not approved production preparation code.

## Primary completion provenance and dynamic expectation timing

The primary orchestration that owns both completed evaluations must own completion
issuance. The optional formatter/transport may receive detached validated rows;
it must not receive a completion-issuer capability or nominate trusted metadata.
The issuer is reachable only for the bound request and same completed full/reduced
contexts, after their original input association and independent row parity pass.
It does not call a second authority evaluator or treat a diagnostic cache as proof.

The completion record binds the request digest, original/native input descriptor
digest, both completed evaluation identities, exact selected semantic-row digest,
payload length and payload digest. It is one-shot, immutable and scoped to the
exact retained worker. It contains no `eligible`, accepted/recovered verdict or
route/runtime authorization. The hashes prove consistency and association under
the trusted primary producer premise, not Coordinator authority independently.

Transport this small completion record through a separately parent-owned bounded
control channel, not by printing new stdout, rewriting a primary summary/receipt,
or reading expected values from the candidate payload. The implementation must
prove that only the primary issuer supplies this record, and that an optional
formatter/late callback cannot substitute it. Separate channels carrying two
untrusted objects fail this requirement. This is trusted local-worker isolation,
not protection against a malicious process running as the same Windows account.

The digest becomes known after preparation, not at request/slot construction.
A later slot API may accept the independently obtained expectation at its one-shot
post-exit read instead of its constructor. That API change is conditional on the
real issuer/control-channel binding; a caller-supplied arbitrary digest setter is
not a fix. C1a remains unchanged in this review.

Parent ordering remains: exact process exit, original primary diagnostics/proof
checks, immutable terminal receipt, then optional completion/payload adoption.
Completed failing analysis is not automatically excluded: it may supply local
diagnostics if all local export prerequisites pass, while `proof_incomplete`
remains the primary result. Timeout, unknown/nonzero exit, input mutation, missing
primary artifacts, wrong attempt or partial completion omits the export. Do not
reclassify a primary failure to permit optional adoption.

## Preparation gate: required bounds, not yet proved

The future producer retains C0's exact A/B1 semantic checks, 64-row selection,
256-KiB serialized ceiling, full/compact independent derivation, original offsets,
authored-versus-observed generation distinction and `peerLineageStatus=not_evaluated`.
The boundary/state caches used by stall-grace do not replace writer membership,
duplicate counting or exact forwarding-effect lookup.

No new whole-input scan/hash, unrestricted private-view copy, serializer startup,
file/network I/O, blocking IPC, retry or background-worker join may be placed after
primary report publication. Hashing a native array still costs work proportional
to all its code units. Length framing, incremental hashing or moving work into an
existing loop does not by itself prove bounded additional cost or deadline safety.

Before production integration, the implementation must name the existing traversal
that owns every required input/lookup, account for added visits/bytes/allocations,
enforce fixed resource ceilings with whole omission, and make the post-completion
path operate only on the bounded prepared context. Missing indexes are a producer
implementation gap, not permission to skip membership/ambiguity checks. The actual
numeric preparation budget and cancellation checkpoints must be established and
tested with representative generated evidence before this gate can pass; output
limits alone are insufficient. No deadline extension or pre-exit grace is allowed.

## Verification and next implementation boundary

New focused tests execute the actual input-assembly statements, file parser and
augmentation function. The evaluator is an argument-capture sentinel, not a fake
authoritative result. Across PS5.1/7, each host verifies 12 writer/input cases,
physical source-line preservation, synthetic-origin mismatch, 13 native hash
vectors, three invalid native inputs, file-byte/native-domain separation and both
parents' actual attempt-ID generation/mapping. Four hash vectors are pinned.
No production exporter, completion issuer, new reader or Snapshot evaluator runs.

The next bounded implementation is the primary-owned prepared-context/completion
issuer seam with generated A/B1 evidence and the bounded-cost accounting above.
Its negative controls must include forged completion, matching self-checksums,
cross-request/worker/attempt/view substitution, stale or duplicate issuance,
source mutation, wrong native offsets, synthetic inputs, parity mismatch, budget
exhaustion and freeze during preparation. Preserve C0 V1 behavior, primary output
bytes, terminal receipts, evaluation counts and all no-effect assertions.

This review supplies input/protocol decisions, not a passing producer gate. Keep
production wiring disabled until the actual seam and C1 lifecycle cases pass.
No new live 256 MiB run is required. Results:
`artifacts/publication-producer-contract-20260914/RESULTS.md`.
