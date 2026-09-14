# C1n: parent-bound private publication channel

Date: 2026-09-14. Base HEAD `ef78e9d7`, with accumulated uncommitted work.
This implements an opt-in transport receipt, not live adoption or a replacement
for the parent's primary diagnostics and immutable terminal receipt.

## Request and issuer binding

`TryCreateWithPublication` requires the actual accepted GUI completion and the
parent's original verified source lease. It retains the original source handle,
attempt GUID, owner and run/scenario identities. It adds an NRS3 request variant;
the source-only NRS1 and writer-bound NRS2 paths remain unchanged and default-off.

The parent creates separate fixed-size control and payload mappings under its
random retained-request namespace. A worker derives this namespace only from its
successfully attached NRS3 request, not from payload fields or an optional helper.
Attachment still requires the exact bound PID and process creation time, and the
actual held parent-process handle and cancellation lifetime. No new process is
nominated by the candidate, and no extra worker identity is generated.

Before launch, the parent independently computes the bounded canonical request
digest from its own source descriptor, accepted writer descriptor and output
namespace. The full request uses `nlink-local-publication-producer-request-v1`,
the fixed primary-owned failure policy and maximum 64 witnesses. The process
identity and cancellation handles remain separately pinned request context.
Parent request canonicalization has a separate cooperative 2,000-ms metadata-only
cap before launch; it performs no new file/input hash pass and extends no watchdog.
Non-channel C1m preparation retains its distinctly named prepared-request format
and its original bytes. Native descriptors still have no invented file paths.

The worker's endpoint is private. It is opened during attachment, before the two
original authority evaluations. Only C1m's actual custody issuer can publish its
genuine immutable `PreparedPublication`; there is no public endpoint, `TryOffer`,
deserializer, caller-supplied payload or trusted-digest setter. The optional
formatter receives neither channel access nor the primary capability.

The issuer first completes the existing native/source/writer/view ownership and
canonical payload checks. It writes the fixed completion fields and owned payload
copy under a zero-wait mutex, then checks ownership and the existing optional-work
deadline before marking the record ready. A partial write never becomes ready.
Any later failure in the issuer adapter revokes the channel. Revocation cannot
grant authority or change a primary outcome; it is safe after worker disposal.

This is trusted local-worker isolation, not authentication against malicious code
running under the same Windows account and directly opening named kernel objects.
Two arbitrary self-checksummed objects are not admitted through the production API.
The corruption tests deliberately use raw mappings outside that API.

## Lifetime, receipt and bounds

The control mapping is 384 bytes; the separate payload mapping is 262,144 bytes.
The control record binds the request ID, actual worker PID/creation time, both
original invocation IDs, request/input/selection digests and payload length/hash.
The parent has no API for changing these expected values. It independently pins
the request hash and worker identity, and obtains dynamic expectations only from
the private issuer's control channel, never from candidate payload parsing.

Issuance is one-shot, with no retry or blocking mutex acquisition. Channel writes
occur inside the pre-report C1m builder and its remaining share of the existing
2,000-ms optional-work budget. Added transport work is one payload copy and one
mapped write, each at most 262,144 bytes, plus fixed control writes. These visits
are separate from C1m's codec counters. Mapping setup happens before evaluations.
No serializer, input scan/hash, file/network I/O or IPC offer is added after primary
report publication. There is no new watchdog, grace period or background worker.

Successful publication transfers a sealed diagnostic record to the parent-owned
channel. Normal primary cleanup still invalidates and drops the local completion,
native/view references and reader handles, but does not erase the transferred
record. Explicit revocation, early parent take or parent disposal freezes it.
Cleanup does not revive or confer authority on an expired local capability.

`TakePublicationAfterExit()` is a zero-argument, one-shot transport operation.
It freezes the channel first and requires the request's original duplicated
worker handle to report an exact successful exit. Running, unknown or nonzero
exit, cancellation, abandonment, partial readiness, mismatched request/process/
namespace digest or invalid bounds omit the whole result. The parent copies at
most 256 KiB and verifies that bounded payload against the independent control
digest. Returned bytes are copy-only and metadata has no public constructors or
setters. A repeat take cannot recover an earlier omission or replace a receipt.

This transport primitive does **not** authenticate the parent's primary artifact
checks or terminal receipt. It must not be called for live adoption until the
actual parents preserve: exact exit, original diagnostics/proof checks, immutable
terminal receipt, then optional receipt/adoption. `proof_incomplete` remains a
primary failure; a transport receipt adds no accepted/recovered/eligible verdict,
peer lineage or Coordinator/runtime authority.

## Remaining integration boundary

No live callsite enables publication. Follow-up
[C1o](file-transfer-publication-adoption-contract.md) adds the opt-in request and
private post-terminal-receipt adoption at both actual retained-parent functions.
Its independently bound catalog digest extends the control record within the
original 384-byte cap and advances its magic; payload bytes remain unchanged.
The Wrapper-to-RouteAcceptance GUI-owner relay is still absent. C1a's
pre-known-digest slot remains unchanged and disconnected.
No live 256 MiB run, GUI/NKN connection, portable rebuild, commit or push is part
of this slice. Generated failure labels remain synthetic selection controls,
not claims about a real transfer failure.

Verification: `artifacts/publication-primary-channel-20260914/RESULTS.md`.

The completed gate passed 17 unique xUnit tests, including 46 new channel workers
and the prior 54-worker completion regression, plus two focused Unicode-path
channel cases. New default-channel issuance used 460–682 ms and 5,539–5,548 payload
bytes. These generated measurements are not live 256 MiB deadline guarantees.

The C1o integration belongs at the original terminal-receipt return in
`Invoke-TunaGuiRetainedAnalysis` and `Invoke-RouteAcceptanceRetainedAnalysis`.
Those parents previously discarded that direct return and later threw on nonzero
exit or incomplete proof artifacts. Adoption preserves these outcomes, captures
trusted original terminal facts in private custody, rejects missing primary
artifacts, and distinguishes diagnostic availability from a passing proof. Do not
use a reread mutable receipt, candidate fields, or a second authority evaluation
as an adoption issuer. The seam remains opt-in while the upstream owner relay is
still absent.

The existing `Test-FileTransferRetainedAnalysisAttemptReceipt` is not an unchanged
drop-in adoption gate: it requires status `completed` and performs another current
input hash/line-count pass. The original receipt writer already records the final
source comparison and artifact hashes. C1o uses trusted facts from that original
work; it must not add a new post-report whole-input traversal or
weaken the existing passing-proof validator to accept failed proofs globally.
