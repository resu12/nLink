# Receiver destination finalization: review and bounded fix plan

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-attempt-payload-read-20260926.md`.
Review only: no runtime, test-source, or acceptance changes in this step.

## Decision

Do not connect a later final-path reopen to the native attempt proof as if it
were the original received file. Preserve the destination's native identity
through rename and retain a read-only handle before releasing its writer.

The next implementation should first address the Windows guard-owned
destination's open/finalize lifetime with focused tests. Actual helper receive
integration and parent adoption follow that verified storage boundary. A
test-only alternate destination must not be presented as validation of the
unchanged production guard.

This is a confirmed **custody-proof gap**, not evidence that a live transfer
was corrupted, that an attacker exercised the gap, or that the route-cycle
failure originated here. No new live RCA is inferred from these offline checks.

## Current path and first missing edge

1. `SessionRuntime.cs:1461` builds the offered session/file descriptor, checks
   authorization via the guard, and wraps the returned exact write handle in
   `FileTransferReceiveDestination` (`:1496`). The ordinary storage policy is
   unchanged: default receive root, numbered collisions, overwrite disabled.
2. `SessionFileTransferGuard.cs:351` creates the temporary stream as read/write,
   create-new, exclusive, asynchronous/random-access. It is a real readable,
   seekable destination suitable for sparse V4 writes and hashing.
3. `SessionFileTransferService.PullTransferSessionV4.cs:23353` validates manifest
   metadata and installs the exact destination/stream on the current inbound
   context. `SessionFileTransferService.cs:5248` checks byte count and computes
   the sparse hash from that same stream before calling destination finalization.
4. **The first missing native-identity edge is inside the guard's finalizer.**
   `SessionFileTransferGuard.cs:100` flushes, then closes the stream, then
   `:110` moves `Plan.TempPath` to `Plan.FinalPath`. No original native handle is
   retained across this close-to-path-move interval.
5. The destination's Boolean `IsFinalized`, saved-path strings, integrity log,
   and completion messages are not a native destination capability. A later
   final-path open or matching SHA/length cannot restore uninterrupted custody.
   Core eventually disposes `WriteDestination` in `DisposeResources` (`:26735`),
   so retaining a handle only inside that wrapper is also insufficient for
   later app/GUI/parent adoption unless ownership is handed off before cleanup.

The write plan and guard handle do not themselves bind a transfer ID or original
app lease. Any future evidence owner must bind the actual Service factory
invocation/current transfer and exact helper lease in addition to owning the
file. Reconstructing ownership from a path, session/transfer strings, public
constructor, hash, or copied DTO is not acceptable.

## What must stay unchanged

Core remains responsible for byte-count/hash verification and transfer
completion. Storage finalization is not coordinator route authority. Preserve
all current coordinator/source-generation checks before publication, saved-path
updates, completion feedback, and terminal transition.

In particular, `BootstrapWriteTracking_DestinationFinalizationTailCannotCompleteReplacement`
already demonstrates that started physical finalization can finish after its
source is superseded, but cannot complete the replacement, set its saved path,
or send its frames. Do not attempt to roll back an already successful rename
or turn such a late physical result into an authority-bearing receipt.

Keep collision numbering, no-overwrite behavior, explicit overwrite policy,
directory-target rejection, failed-finalize artifact preservation, and cleanup
semantics covered. Do not change non-Windows behavior or make unsupported
filesystems silently qualify for native custody. This review does not broaden
the root/path/reparse-point security contract or claim power-loss durability.

## Options reviewed

- Reopen the final path and hash it: insufficient continuity; rejected.
- Duplicate today's exclusive stream and retain it around `File.Move`: the
  path-based rename still conflicts with the retained non-delete-sharing open;
  rejected as the implementation strategy.
- Add delete sharing and retain a path-based move: weakens protection against
  replacement/rename and still selects the source by pathname; rejected.
- Open with rename rights, rename the original still-open handle, retain a
  read-only duplicate before writer closure: recommended, subject to the
  focused compatibility and failure tests below.

Windows rename-by-handle requires DELETE access on the source open. Request it
at creation, not by closing/reopening or attempting to upgrade an exclusive
existing stream. Microsoft documents the access requirement in
[FileRenameInformation](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/87f86c9b-6c2a-4803-84b7-131a74a434fa)
and the access/sharing distinction in
[CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).

`SetFileInformationByHandle` with `FileRenameInfo` targets the open source file;
the rename record supplies the destination and replace policy. Keep replacement
disabled for the normal app policy; never turn a collision into a success.
See [SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)
and [FILE_RENAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info).

There is already a local precedent in
`tools/FileTransferOps/LocalFrontierEvidenceFile.cs`: create with DELETE rights,
rename the open file, downgrade a duplicate to read-only before writer closure,
and retain native identity. Reuse the proven technique, not that diagnostic
owner, its API, its byte-array materialization, or its 64 KiB/6 MiB limits for
a 256 MiB transfer. Do not create a general persistence framework for this fix.

Duplicates share the underlying file position. Diagnostic reading must wait
until actual writes/hash work settle and use an offset-safe read or another
verified by-handle read strategy compatible with the chosen sharing policy;
it must not seek a duplicate concurrently with Core. See
[DuplicateHandle](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle).
Preserve asynchronous-handle mode and random-access behavior when wrapping
native handles in `FileStream`; the existing synchronous diagnostic primitive
is not a drop-in transfer stream.

## Bounded implementation and verification sequence

1. **Windows destination lifetime.** In the guard-owned creation/finalization
   path, create the original temporary file with the required rights and current
   restrictive sharing, rename it by handle after Core verification, and retain
   a read-only native reference before closing the writer. Keep authorization,
   filename/root selection, caller API compatibility, and non-Windows behavior.
   Do not add a pathname fallback that reports native-custody success. Handle
   no-rename-rights/unsupported opens explicitly; externally supplied legacy
   streams must not acquire proof eligibility from their constructor alone.
   Verify real guard-created handles, unchanged volume/file identity, full
   bytes, denied native writes/rename/delete while retained, collision policies,
   directory target, cancellation, repeated finalize/dispose, and failure cleanup.
   A failure after physical rename must not delete the final file via the old
   temporary name or report a successful custody handoff.
2. **Exact transfer/app binding and retirement.** Bind the retained file to the
   actual current Service destination and original helper lease, with original
   object identity and one-way retirement. The transfer retains its own cleanup
   responsibility; any proof reader needs a bounded explicit ownership handoff
   before terminal cleanup. No writable/delete-capable duplicate leaves the
   storage owner. Verify copied/foreign/stale owners, app exit, cancellation,
   destination/source replacement, duplicate take, active-reader disposal, and
   late finalization. Failure to obtain evidence must not grant route authority
   or silently manufacture a transfer-integrity/acceptance result.
   Retention is observable: an exclusive retained open can delay user access
   to the saved file. Release the ordinary hold promptly with terminal cleanup;
   longer evidence retention must be bounded to the explicitly admitted native
   attempt, not added to every download or held until arbitrary app shutdown.
3. **Actual helper receive integration.** Connect the original helper's real
   receiver Service and authorized destination, then verify full 256 MiB
   receipt/hash/finalization with retained source and destination identities.
   This is separate from the already-passing sender-to-same-child-loopback
   single-chunk fixture. Only after that should native GUI/parent adoption and
   selected-witness consumption use the retained destination.

Run only the focused new regressions and directly affected guard/Core/app
binding tests, with one relevant compatibility lane after stabilization.
Do not rerun the full matrix or change transport tuning. Live validation still
requires a fresh portable build, strict preflights, and an escalated run.

## Review verification

All 408 previous payload-read seal pins matched before review. All 248
pre-existing dirty files are preserved. This review adds only this document;
no production or test source changed. Branch and HEAD remain unchanged.

One focused run passed all seven selected cases, with zero failures/skips:

- Actual guard creation/finalization with duplicate-name numbering.
- Actual guard preservation when a final-path collision appears before move.
- Service partial-file-to-final-path ordering.
- Service waiting for finalization before completion.
- Bootstrap stale-finalization-tail rejection.
- Existing native evidence write/rename primitive on Windows PowerShell 5.1
  and PowerShell 7 (two cases).

The Service ordering fixtures use their existing test destination helper, and
the native primitive cases concern diagnostic files. Neither is presented as
an actual 256 MiB receiver-custody test or a reproduction of a malicious
close/move race. Current-code inspection establishes the missing edge; the
checks confirm existing behavior to preserve and a native implementation
precedent. No new fault was injected into the production finalizer.

Results: `artifacts/frontier-destination-review-20260926/results/review-focused.trx`.
Pins/baseline: `artifacts/frontier-destination-review-20260926/FINAL-VERIFICATION.json`.
No aggregate lane, portable build, GUI/NKN run, full matrix, commit, or push.
Acceptance remains unchanged, including the required second coordinator-published
`file_tuna_v4` route. This review does not establish live-256 readiness.
