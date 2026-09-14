# C1h: parent-bound GUI writer finalization

Date: 2026-09-14. Base HEAD: `ef78e9d7`, with accumulated uncommitted evidence
work. This closes the GUI parent/finalizer ownership seam following
[C1g native provenance](file-transfer-native-writer-capture-contract.md).
It does not enable a live exporter or close retained-worker/evaluator completion.

## Root cause and bounded fix

C1g's immutable native capture can also be obtained by calling the native
processor directly. Its existence cannot attest the actual PowerShell finalizer's
marker, recovery, drain, cursor, copy and persistence checks. Reading the ledger
JSON or supplying a `validated=true` flag would not close that ownership gap.

`Invoke-TunaGuiSmokeWithHardDeadline` now accepts an optional empty
`LocalWriterCompletion` reference. No CLI or existing live caller passes it.
The parent creates a fresh `LocalGuiWriterRequest` before its original process
start and binds a duplicated handle to that exact child immediately afterward.
The request carries acceptance/scenario IDs, the canonical artifact root and a
new GUI-finalization request GUID, separate from the later retained attempt GUID.
Unrelated inherited request environment variables are removed from child startup
even when the option is off; the parent's environment is unchanged.

At entry, the actual `Write-FileTransferGuiLogLedgerEvidence` attaches once using
the request and its actual run/artifact scope. Attachment checks the bound child
PID and creation time, the request ID and the live parent's PID/creation time.
It retains a real parent handle. Missing, stale, already attached, cancelled or
contended requests omit the capability without changing primary finalization.
There are no attachment waits or retries.

The returned issuer lives in a PowerShell **private local variable**, covered by
an outer `finally`. Called helpers cannot inherit it. It is not put in the mutable
native result, the immutable native capture, an output reference, a formatter or
a diagnostic DTO. Only this private owner can call the one-shot capability method.
The protocol does not accept JSON or a caller-declared validation boolean and
exposes no public completion constructor/deserializer.

This is trusted local workflow ownership, not authentication against malicious
same-account code, reflection, injected scripts or direct memory-map tampering.
The public attach entry point is process-bound and one-shot; it is not a proof
that an arbitrary caller is the finalizer. The actual production call site's
private custody and ordering are part of the contract. A future formatter must
not be given the issuer, nor run before the finalizer claims its attachment.

## Issuance and adoption order

The existing finalizer still does all validation. Only after its original ledger
JSON atomic persistence and finalization-stage update succeed, with both existing
integrity reason lists empty, can it issue the bounded record. It requires the
exact result-owned native capture and binds the original semantic hash/byte/line
counts to the existing verified analyzer copy. It does not reevaluate authority,
revisit source/writer lines, reread/hash files or copy additional input.

The record contains the actual ledger ID, original source path and frozen
boundary/bookmark, three native output hashes/identities/counts and at most 64
immutable writer summaries. Contiguous sequences start at one; each summary's
last sequence also represents the C1g-verified observed count. Output paths are
the finalizer's fixed names under the parent artifact root. The analyzer copy has
the semantic digest/counts, **not** the native semantic file identity: it is a
different file, whose identity is deliberately absent from that descriptor.

The parent adopts only on its original natural-exit path, after output capture,
an available zero exit code and persistence of its original `gui_child_exited`
state. Adoption independently checks the held child process handle for exit zero,
the original PID/creation stamp and request, and a committed bounded record.
It seals the channel on the first attempt, even if the child is still alive.
Timeout, unavailable/nonzero exit, failed output capture, parent persistence
failure, partial record or cancellation cannot return a completion.

All original scenario/finalization deadlines, terminal-phase rebasing, waits,
cleanup and primary state/return fields remain unchanged. No new production wait
is added. Finally releases the optional channel independently of the original
Process object. Accepted data retains no process, file, map, issuer or native
result reference and has no public mutators; collections are read-only.

## Bounds and limits of the receipt

Request body: 16 KiB. Completion body: 64 KiB. Variable metadata (including shared
request scope): 16,384 code units, 1,024 per run/scenario/ledger/writer ID, 4,096
per supplied canonical path, 1–64 writers. Adoption also constructs four fixed
output paths from the bounded root and constant filenames; those derived copies
are not counted as transmitted variable metadata.
Completion serialization and adoption parsing each have a 250 ms cooperative
elapsed budget. Admission is checked before serializing; publication is one
bounded write under a zero-wait mutex. No whole-input work or persistent disk
receipt is introduced. No arbitrary payload formatter participates in issuance.

Original file identities are historical, not frozen current contents. The
accepted completion survives disposal as immutable original facts, but is not a
claim that paths have not been overwritten. Later C1e writer verification must
still compare actual bytes during its admitted traversal. The source spool path
is not a verified spool digest, and frozen visits are not a new EOF proof.

This receipt has no coordinator, route or runtime authority. It does not certify
a retained primary evaluation, witness membership, a V2 payload digest or worker
completion. A post-finalization GUI failure is withheld by the nonzero-exit gate.
Milestone-merged or synthetic retained inputs remain unsupported for initial V2:
the later source owner must match the original analyzer descriptor or omit the
optional export without changing primary analysis.

## Next boundary

[C1i](file-transfer-local-retained-writer-binding-contract.md) now provides the
typed retained-parent/source association and admitted writer-reader API. C1h's
accepted context retains its bounded original binary body privately for that
internal codec; it is not exposed as a public serialization/factory API. The
upstream Wrapper-to-RouteAcceptance process relay remains to be implemented.

Carry this separately accepted writer context through the original Wrapper and
RouteAcceptance lifecycles into the retained source request. Bind candidate
writer bytes during the existing admitted traversal, then bind the primary's
actual full/reduced evaluation contexts and selected failure witness. Final V2
completion and adoption remain separate gates. Do not enable live export or run
a new 256 MiB scenario based on this receipt alone.

Verification is recorded in `artifacts/publication-gui-writer-20260914/RESULTS.md`.

Follow-up: [C1p](file-transfer-local-gui-writer-relay-contract.md) now reuses this
bounded codec and process-binding mechanism for a distinct one-hop relay. It
forwards only the actual GUI parent's privately adopted completion, preserving
the original GUI identity separately from the Wrapper's relay identity. The
normal RouteAcceptance function has an opt-in consumer; top-level live callers
and direct Wrapper-owned publication remain disabled.
