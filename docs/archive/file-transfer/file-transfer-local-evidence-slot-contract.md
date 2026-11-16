# C1a: parent-owned prepared-payload slot

Date: 2026-09-13. Base HEAD: `ef78e9d7` plus uncommitted A/B1/C0/C1 work.
This is the bounded transport/lifetime part of the producer-export gate, not a
completed producer projection or permission to wire a production caller.

Follow-up: the [C1b producer/request review](file-transfer-local-producer-request-contract.md)
now pins native input framing, source-offset/augmentation handling and the
separate primary-completion requirement. Its focused format/input tests pass;
the real preparation and completion issuer remain unimplemented. This slot API
and its production-wiring gate are unchanged.

The subsequent [C1c native-input capture](file-transfer-local-native-input-capture-contract.md)
also exercises this slot through test-only retained-parent integration. Native
input tokens are not completion authority; the slot's production gate stays shut.

## Decision and verification plan

1. Create a unique parent-owned, preallocated named memory slot with at most
   256 KiB of payload. Bind it once to the actual primary process object and a
   parent-created request GUID. Do not create an additional worker.
2. Offer already prepared bytes using a zero-wait mutex. No scanning, hashing,
   serialization, file/network I/O, task dispatch, retry or wait loop belongs
   in the offer. Contention, missing owner, wrong request/producer, oversize or
   incomplete publication yields no payload. A second offer cannot replace one.
3. Seal once and read only after the bound process exits zero. Require the exact
   payload digest supplied independently to the parent, not a digest inside the
   slot. Copy into detached parent bytes. Test ordinary and malformed transport,
   contention, partial/abandoned publication, nonzero/live producer, cancellation,
   size limits and one-shot cleanup on both PowerShell hosts.

The slot transports opaque bytes. A readable slot does not establish C0 schema,
input provenance, primary receipt completion, peer lineage or authority. The
caller still needs the unchanged primary terminal receipt and C0's independently
trusted request/input/payload bindings. No `eligible`/recovered flag is introduced.

## Ownership and freeze

The parent creates unpredictable per-request `Local\\` kernel object names and
retains the map, mutex and cancellation event. Publication opens those existing
objects only. It cannot recreate an owner that has disposed them. Bind duplicates
the actual process handle, not a later lookup of a recycled PID, and survives
the parent's normal disposal of its original Process object. The producer
must match the parent-pinned PID and request header.

The mutex is acquired with `WaitOne(0)`. A missing, busy or abandoned lock omits
the operation; there is no retry. The producer marks an in-progress copy and
only then a ready candidate. The parent seals before attempting to read, and
will never retry or adopt a candidate that was busy, incomplete, malformed or
unavailable at that point. Cancellation after a completed read is harmless.

In-flight writes may finish in a retired private memory mapping; they cannot
produce a later parent result or any artifact. This is not a guarantee that the
OS stops an already executing memory copy. No file is created by the slot, and
there is no detached export worker to outlive cleanup. This is same-user trusted
worker IPC, not a security boundary against malicious processes in that account.

Bounded memory operations and a zero-wait lock remove explicit optional waits;
they are not a hard real-time guarantee against OS scheduling or paging delay.

## Remaining gate

No production caller is added. C0's current whole-input projection cannot be
placed before this offer and called deadline-safe. The bounded projection from
the completed primary views, exact original reduced-input/hash domain, parent-
issued request and independently trusted expected hashes remain to be connected
and verified. Do not use a slot-provided checksum as that external expectation.
The C1 retained-parent counterexample remains applicable to any slow preparation
before worker exit. No new live 256 MiB run is required for this transport slice.

## Implemented and verified boundary

`LocalEvidenceExportSlot.cs` and its PowerShell loader implement the opaque slot;
neither is imported by a production caller. The final focused regression passed
6/6 tests across PowerShell 5.1/7: 24 transport cases, 24 actual retained-parent
lifecycle cases, and the existing 36 pre-exit counterexamples per host. A later
constructor-only fix rejects a digest with a trailing newline using absolute
regex anchors; the affected transport tests passed 2/2 again, with five invalid
owner controls per host. See
`artifacts/publication-slot-implementation-20260913/RESULTS.md`.

The lifecycle harness inserts only test-owned binding/contending/cancellation
hooks into AST-extracted parents. Successful prepared publication, omission,
contention and cancellation preserve all nine primary report files. The slot
is read after the unchanged terminal receipt and original Process disposal;
that read does not change any primary report or receipt byte. Finalized failing
proof stays `proof_incomplete`, even when the opaque slot can be read.

The test payload is a constant prepared byte array, not a C0 carrier. Test-only
module loading and payload preparation precede primary fixture publication.
The tests do not prove bounded preparation from real completed views, safe
compiler startup near the primary deadline, cancellation during a memory copy,
or production adoption. `PreExitSafe` in each harness row describes only the
controlled case; the overall `PreExitHandoffGate` remains `not_satisfied`.

## Preparation review: do not substitute existing diagnostic indexes

The existing stall-grace paths do build/cache publication boundary and observed-
state indexes (`FileTransferFrontierGraceHandoff.ps1:54,1652`). Thus an assertion
that no publication index exists would be incorrect. Those indexes are neither
an exact original-writer membership index nor the complete A/B1 export binding.
They do not supply unique physical use locations, per-stream forwarding-effect
locations, or five independently bound input descriptors. The data-path index
uses diagnostic range/scope keys, including case-folding, and cannot replace
those exact ordinal bindings. No existing recovery/cache behavior is changed.

Both retained writer paths produce a separate in-memory reduced Snapshot stream
before evaluating it. The evaluator returns boundary/effect arrays, not the
complete request-selected export index. Saved summaries exclude private effects.
Constructing A/B1/C0 indexes after evaluation still scans whole inputs; the
64-row/256-KiB output limits do not bound that work. Persisting a new reduced file
before exit would add I/O and still needs lifecycle verification. Relabeling an
ordered-lines digest as `sha256-file-bytes-v1` is invalid.

There is also an explicit expectation timing gap: this slot requires the parent
to know the payload digest at construction. The controlled tests know their
payload in advance; a real completed-view projection is not known then. A future
producer/request contract must identify an independent trusted source and timing
for that expectation. Reading a checksum from the candidate slot cannot solve it.

The next bounded offline step is that producer/request contract: exact native
input identities and offsets, bounded selected-row preparation, and a separately
bound producer-completion expectation. Review it against C0 before changing its
file-input premise or the slot API. Only then add generated-evidence/lifecycle
tests and a production caller. A live 256 MiB run cannot close these design gaps.
