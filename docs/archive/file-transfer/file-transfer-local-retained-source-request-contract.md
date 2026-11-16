# C1f: retained parent/worker source ownership

Date: 2026-09-14. Base HEAD: `ef78e9d7`, with the existing uncommitted local
publication evidence work. This slice implements source ownership only. It is
not the complete [C1b producer request](file-transfer-local-producer-request-contract.md),
a trusted writer-ledger receipt, an evaluator owner, or a publication-completion
issuer. The live retained call sites remain opted out.

## First missing edge and implementation

The retained parents already compute the primary source hash and decoded line
count before starting their worker. C1e could retain the hash pass's read-only
file handle, but no request associated that frozen object with the actual child.
Passing an arbitrary path/hash pair or a diagnostic JSON file would not supply
that missing ownership.

`Invoke-RouteAcceptanceRetainedAnalysis` and `Invoke-TunaGuiRetainedAnalysis` now
have an internal, default-off `CaptureLocalSourceOwnership` switch. They retain
the existing hash pass through C1e, keep the original separate line-count read,
and create a source-only request before starting the worker. The request uses
the same GUID as the existing attempt ID, not another attempt. Neither script's
CLI or live call sites enable this switch.

`LocalRetainedSourceRequest.TryCreate` requires an actual verified
`LocalInputReadLease`, not a caller-declared digest or duck-typed object. It takes
its own independent read-only handle to the same volume/file identity and byte
count. Disposing the original lease does not release the request's freeze. The
request stores owner, acceptance/scenario IDs, canonical source path, original
file-byte SHA-256, file identity, byte count and the parent's decoded line count.
The two C# types load together so this prerequisite is strongly typed.

The parent creates an opaque, non-file-backed local memory mapping and closed
event, then binds the actual launched process once. It retains a duplicate of
that exact process handle. Worker attachment checks PID **and process creation
time**, the request GUID and exact source/run/scenario fields. The worker also
holds a handle to the parent process after checking its creation time. A stale
PID alone cannot satisfy either process association.

Attachment opens an existing mapping read-only. It never creates a missing
namespace, accepts payload-selected paths, waits for a producer, or retries.
The mutex is always acquired with `WaitOne(0)`. An unbound, stale, contended,
closed, malformed or mismatched request omits ownership. Each retained worker
process has at most one attachment attempt; each attached owner has at most one
source open. Public constructors for request, worker and source reader are absent.

The child opens the source read-only and checks its actual file identity and
length against the parent-held object. `Invoke-FileTransferRetainedAnalysis`
passes that owned reader to the existing parser only for one resolved source
and `TailMinutes=0`. Otherwise ordinary `File.ReadLines` parsing remains in use.
The reader retains the same UTF-8/default BOM detection and line framing as the
original parser; blank/non-event physical lines are still counted.

## Lifetime, bounds and primary compatibility

`InputReadComplete` is true only after EOF, exact decoded line count and a current
parent lifetime. Consuming the declared number of records without advancing to
EOF is insufficient. Partial reads, count mismatch, request cancellation, parent
exit or disposal cannot produce completion. This property is provisional source
read state, **not** an immutable producer-completion token.

Losing optional ownership after a source reader has opened does not abort the
ordinary primary read. It continues through the same frozen file object, while
`InputReadComplete` remains false. Primary errors are not hidden by rereading or
replacing the source. Both parent and primary `finally` paths release their owned
handles; request disposal does not dispose the original primary `Process` object.

Fixed descriptor admissions: 64 MiB source bytes, 262,144 decoded source lines,
4,096 path code units and 1,024 code units per run/scenario field. The binary
request body is capped at 16 KiB, with a 64-byte header. Serialization has bounded
field sizes; over-cap descriptors omit, rather than truncate. These are optional
ownership admissions, not new limits or decoding rules imposed on primary input.

The original source hash and line-count traversals are reused. The child performs
its original parse, without another source hash or whole-input copy. Added work
is the bounded descriptor, handle/identity checks, zero-wait attachment and scalar
line/EOF accounting. Optional setup occurs before primary report publication;
after publication this slice performs only handle cleanup, not new source scans,
payload serialization, output writes, evaluation, worker startup or joins.

Both original 1,800-second watchdogs, process exit checks, proof checks, primary
reports and immutable terminal receipts remain unchanged. No pre-exit grace or
post-receipt adoption is added. This is trusted local workflow ownership, not
protection against malicious same-account code, writable mappings or injected
code modifying the parent itself.

## Verification

The focused suite covers both PowerShell 5.1 and 7 for source-request primitives
and the extracted real retained-parent bodies. Parent fixtures change only test
paths, the controlled child, no-window launch and the test watchdog/polling scale.
Each host exercises 30 parent cases across GUI/proof outcomes, optional omission,
wrong request, nonzero exit and timeout. Successful non-timeout report bytes and
the original terminal receipt semantics are preserved; timeout remains a failure.

Four complete primary compact analyses run on Windows PowerShell, the host used
by both retained parents: baseline, owned source, closed request and a later
primary-report failure. Four reports compare byte-for-byte; the compact summary
compares after excluding only its two pre-existing elapsed fields. The synthetic
LegacyV0 fixture retains its original three authority evaluations. Ownership adds
none, preserves the primary-selected physical failure line, and is disposed even
when a later primary report throws. This is not live publication-authority proof.

The source controls separately cover exact scope/process binding, creation-stamp
mismatch, zero-wait contention, one-shot attachment/open/read, partial and non-EOF
reads, count mismatch, cancellation before/after open, independent freeze lifetime
and handle release. The parent-process-exit control substitutes a real test
process's identity in the test header, then terminates that process to exercise
the held-handle death check. It is deliberately not described as an end-to-end
crashing-production-parent test.

Actual primary reporting is not claimed on PS7: its pre-existing stage writer's
read-only `$IsWindows` collision remains unchanged. Results and exact test runs:
`artifacts/publication-source-request-20260914/RESULTS.md`.

## Remaining offline boundary

Follow-up: [C1g native writer capture](file-transfer-native-writer-capture-contract.md)
preserves immutable facts from the original native write/hash pass and supplies
an opt-in output at the actual finalizer's success seam. This is not itself a
full finalizer-validation or process-completion receipt.

[C1h GUI finalizer ownership](file-transfer-local-gui-writer-request-contract.md)
now binds the actual finalizer's private completion capability to the exact GUI
child and its parent's original exit/persistence path. No live caller opts in;
the source-only format deliberately carries no accepted writer context.

Follow-up [C1i](file-transfer-local-retained-writer-binding-contract.md) adds a
distinct optional writer-bearing request format. It requires the real accepted
GUI context to match the existing source owner, otherwise retaining this original
source-only format. A worker-owned writer reader separately verifies actual
bytes. Upstream process relay and live/primary producer integration remain off.

The GUI ledger finalizer owns writer hash/count and validation facts, but the
retained-parent call does not carry that trusted owner. Existing ledger JSON,
direct-evidence manifests and matching self-checksums are diagnostic artifacts,
not a substitute for that owner. Carry its exact validation/input association
through the original GUI/Wrapper/RouteAcceptance lifecycle without another
pre-launch writer scan. Then bind the existing full/reduced evaluator invocations
and primary-selected witness before issuing independent completion metadata.

Some live retained inputs are milestone-merged, and primary orchestration may
append synthetic harness records. This source request neither removes those
records nor certifies writer/native membership for them. The initial C1b export
must still omit unsupported augmentation; a merged pathname is not an original
writer membership proof. Do not reconstruct omitted private views from old JSON,
perform another authority evaluation, or turn C1a's caller-known digest into an
issuer. V2 export and post-receipt adoption remain disabled. No new live 256 MiB
run is needed to validate this source-only edge.
